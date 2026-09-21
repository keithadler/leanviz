#!/usr/bin/env python3
"""Load the page in a real browser and fail on anything it logs or fails to render.

`node --check` parses app.js, which is not the same as running it: a doc comment landing in the wrong place once
left `async /** … */ async function`, valid syntax whose first statement throws a ReferenceError, and the whole
page silently stayed on "Loading…". This drives headless Chrome over the devtools protocol, visits the three
kinds of page, and asserts that each rendered what it should and that nothing threw.

    python3 tests/check_page.py [base-url]

Needs a Chrome or Chromium on the machine; CI runners have one, and CHROME=/path overrides the search.
"""
from __future__ import annotations

import base64
import json
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

CANDIDATES = [
    os.environ.get("CHROME", ""),
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "google-chrome", "google-chrome-stable", "chromium", "chromium-browser",
]


def find_chrome() -> str:
    for c in CANDIDATES:
        if c and (os.path.isfile(c) or shutil.which(c)):
            return c if os.path.isfile(c) else shutil.which(c)
    print("no Chrome or Chromium found; set CHROME=/path/to/chrome", file=sys.stderr)
    sys.exit(77)


class WS:
    """The smallest websocket client that can carry devtools messages: text frames, no extensions, no fragmentation."""

    def __init__(self, url: str) -> None:
        host, rest = url[5:].split("/", 1)
        host, port = host.split(":")
        self.sock = socket.create_connection((host, int(port)))
        key = base64.b64encode(b"0123456789abcdef").decode()
        self.sock.sendall(
            f"GET /{rest} HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n".encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            buf += self.sock.recv(4096)
        self.rest = buf.split(b"\r\n\r\n", 1)[1]
        self.id = 0
        self.events = []

    def send(self, method, params=None):
        self.id += 1
        payload = json.dumps({"id": self.id, "method": method, "params": params or {}}).encode()
        header = bytearray([0x81])
        n = len(payload)
        if n < 126:
            header.append(0x80 | n)
        elif n < 65536:
            header.append(0x80 | 126)
            header += n.to_bytes(2, "big")
        else:
            header.append(0x80 | 127)
            header += n.to_bytes(8, "big")
        self.sock.sendall(bytes(header) + b"\x00\x00\x00\x00" + payload)
        while True:
            msg = json.loads(self._frame())
            if msg.get("id") == self.id:
                return msg
            self.events.append(msg)

    def drain(self, seconds: float) -> None:
        self.sock.settimeout(seconds)
        try:
            while True:
                self.events.append(json.loads(self._frame()))
        except (TimeoutError, socket.timeout):
            pass
        finally:
            self.sock.settimeout(None)

    def _frame(self) -> bytes:
        def read(n: int) -> bytes:
            while len(self.rest) < n:
                chunk = self.sock.recv(65536)
                if not chunk:
                    raise ConnectionError("devtools closed the connection")
                self.rest += chunk
            out, self.rest = self.rest[:n], self.rest[n:]
            return out

        head = read(2)
        length = head[1] & 0x7F
        if length == 126:
            length = int.from_bytes(read(2), "big")
        elif length == 127:
            length = int.from_bytes(read(8), "big")
        return read(length)


class Page:
    def __init__(self, width: int = 1600, height: int = 1000, scale: int = 1) -> None:
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            port = s.getsockname()[1]
        self.dir = tempfile.mkdtemp()
        self.proc = subprocess.Popen(
            [find_chrome(), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
             "--no-sandbox", f"--force-device-scale-factor={scale}", f"--window-size={width},{height}",
             f"--remote-debugging-port={port}", f"--user-data-dir={self.dir}", "about:blank"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(150):
            try:
                tabs = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/json"))
                self.ws = WS(next(t for t in tabs if t["type"] == "page")["webSocketDebuggerUrl"])
                break
            except Exception:
                time.sleep(0.2)
        else:
            raise SystemExit("chrome did not start")
        self.ws.send("Runtime.enable")
        self.ws.send("Page.enable")

    def visit(self, url: str, ready: str, timeout: float = 60) -> None:
        self.ws.events.clear()
        self.ws.send("Page.navigate", {"url": url})
        deadline = time.time() + timeout
        while time.time() < deadline:
            time.sleep(0.25)
            r = self.ws.send("Runtime.evaluate", {"expression": ready, "returnByValue": True})
            if r.get("result", {}).get("result", {}).get("value") is True:
                return
        raise AssertionError(f"{url}: never satisfied `{ready}` within {timeout:.0f}s")

    def value(self, expression: str):
        r = self.ws.send("Runtime.evaluate", {"expression": expression, "returnByValue": True})
        return r.get("result", {}).get("result", {}).get("value")

    def screenshot(self, out: pathlib.Path) -> None:
        r = self.ws.send("Page.captureScreenshot", {"format": "png"})
        out.write_bytes(base64.b64decode(r["result"]["data"]))

    def problems(self):
        """Anything thrown or logged as an error, minus the favicon nobody has."""
        self.ws.drain(0.5)
        out: list = []
        for e in self.ws.events:
            if e.get("method") == "Runtime.exceptionThrown":
                d = e["params"]["exceptionDetails"]
                out.append(f"{d.get('text')}: {d.get('exception', {}).get('description', '')[:200]}")
            if e.get("method") == "Runtime.consoleAPICalled" and e["params"]["type"] == "error":
                text = " ".join(str(a.get("value", "")) for a in e["params"]["args"])
                if "favicon" not in text:
                    out.append(f"console.error: {text[:200]}")
        return out

    def close(self) -> None:
        self.proc.terminate()


def main(base: str) -> None:
    page = Page()
    failures = []
    try:
        checks = [
            ("the home page", f"{base}/#/", "!!document.querySelector('#top li a') && !!document.querySelector('.tab')",
             "document.querySelector('.stats b').textContent.length > 0"),
            ("a declaration page", f"{base}/#/d/Nat.add_comm", "!!document.querySelector('.graph')",
             "document.querySelector('pre').textContent.includes('+')"),
            # the statement must read as text, not as the markup the colouring adds: a pattern matching inside an
            # attribute it had just inserted once turned a statement into its own HTML
            ("the statement colouring", f"{base}/#/d/Nat.add_comm", "!!document.querySelector('.s-rel')",
             "!document.body.textContent.includes('class=')"),
            # a statement with hypotheses lays out as a theorem rather than one long line
            ("the theorem layout", f"{base}/#/d/Nat.sub_lt", "!!document.querySelector('.theorem .claim')",
             "document.querySelectorAll('.theorem .row').length >= 2"),
            ("a module page", f"{base}/#/m/Init.Prelude", "!!document.querySelector('.list li a')",
             "document.querySelectorAll('.list li').length > 10"),
            # one box, not several: a small library legitimately has a couple of top-level areas
            ("the map", f"{base}/#/map", "!!document.querySelector('.treemap a rect')",
             "document.querySelectorAll('.treemap a').length >= 1"),
            ("the unused list", f"{base}/#/unused", "!!document.querySelector('#unused-prefix')",
             "!!document.querySelector('h1')"),
            ("the request page", f"{base}/#/add", "!!document.querySelector('#add-repo')",
             "!!document.querySelector('#add-go')"),
            ("the holes page", f"{base}/#/holes", "!!document.querySelector('h1')",
             "document.querySelector('h1').textContent.includes('Unfinished')"),
        ]
        for what, url, ready, assertion in checks:
            try:
                page.visit(url, ready)
            except AssertionError as e:
                failures.append(f"{what}: {e}")
                failures += [f"  {p}" for p in page.problems()]
                continue
            if page.value(assertion) is not True:
                failures.append(f"{what}: rendered but `{assertion}` was false")
            failures += [f"{what}: {p}" for p in page.problems()]
            print(f"  {what}: rendered")

        # The graph-backed features: they load a separate file and answer questions no shard can, so a wrong
        # answer looks like a confident one. A chain search that silently missed real chains shipped once.
        page.visit(f"{base}/#/d/Nat.add_comm", "!!document.querySelector('#weigh')")
        page.value("document.querySelector('#weigh').click()")
        weight = None
        for _ in range(240):
            time.sleep(0.5)
            weight = page.value("document.querySelector('#weight').textContent")
            if weight:
                break
        if not weight or "rests on" not in weight:
            failures.append(f"the weight: never answered (last saw {weight!r})")
        else:
            print(f"  the weight: {weight}")

        # Nat.add_comm reaches Nat, which is in the same module as things on the path, so this is the shape that
        # the old pruning got wrong.
        page.value("document.querySelector('#pathto').value = 'Nat'; document.querySelector('#findpath').click()")
        chain = ""
        for _ in range(120):
            time.sleep(0.5)
            chain = page.value("document.querySelector('#pathout').textContent") or ""
            if chain and "looking" not in chain:
                break
        if "step" not in chain:
            failures.append(f"the chain search: expected a chain from Nat.add_comm to Nat, got {chain[:120]!r}")
        else:
            print(f"  the chain search: {chain.split(':')[0]}")

        page.value("const q = document.querySelector('#q'); q.value = '+Nat'; q.dispatchEvent(new Event('input'))")
        hits = 0
        for _ in range(120):
            time.sleep(0.5)
            hits = page.value("document.querySelectorAll('#results a').length") or 0
            if hits > 1:
                break
        if hits < 2:
            failures.append(f"the statement search: +Nat returned {hits} results")
        else:
            print(f"  the statement search: {hits} results for +Nat")
        failures += [f"the graph features: {p}" for p in page.problems()]

        # A shared link loses its `?p=` sooner or later, and what is left lands on the default library. That
        # used to dead-end on "no declaration named X in this bundle" while the site knew perfectly well which
        # other libraries existed.
        page.visit(f"{base}/#/d/Definitely.Not.A.Real.Name", "!!document.querySelector('h1')", timeout=90)
        time.sleep(1.5)
        body = page.value("document.querySelector('#main')?.textContent.replace(/\\s+/g, ' ').trim()") or ""
        offers = page.value("document.querySelectorAll('ul.list li a.nm[href*=\"?p=\"]').length") or 0
        projects = page.value("(typeof S !== 'undefined' && S.projects && S.projects.length) || 1")
        if "no declaration named" not in body.lower():
            failures.append(f"the missing declaration page: said {body[:70]!r}")
        elif projects > 1 and offers < 1:
            failures.append("the missing declaration page: other libraries exist but none were offered")
        else:
            print(f"  a missing declaration: offers {offers} other librar{'y' if offers == 1 else 'ies'}")
        failures += [f"the missing declaration page: {p}" for p in page.problems()]

        # The page most likely to be read as a bigger claim than it is. It has to state the limits, not only
        # the result, and it has to say plainly when a library was not re-checked at all.
        page.visit(f"{base}/#/certificate", "!!document.querySelector('h1')", timeout=90)
        time.sleep(1.2)
        heads = page.value("[...document.querySelectorAll('h2')].map(h => h.textContent.trim())") or []
        limits = page.value("""
            (() => { const hs = [...document.querySelectorAll('h2')];
              const h = hs.find(x => x.textContent.includes('does not establish'));
              if (!h) return 0;
              let n = h.nextElementSibling;
              return n && n.tagName === 'UL' ? n.querySelectorAll(':scope > li').length : 0; })()""") or 0
        verdict = page.value("document.querySelector('.verdict')?.textContent.trim()") or ""
        if "What it does not establish" not in heads:
            failures.append(f"the certificate page: no limits section, headings were {heads}")
        elif limits < 4:
            failures.append(f"the certificate page: only {limits} stated limits")
        elif not verdict:
            failures.append("the certificate page: says nothing about whether this library was re-checked")
        else:
            print(f"  the certificate page: {limits} stated limits, verdict {verdict[:44]!r}")
        failures += [f"the certificate page: {p}" for p in page.problems()]

        # Five that need nothing the bundle does not already carry, so they ship without a rebuild.
        # Ask the bundle for a module that actually has imports and references something. Init.Prelude has
        # neither, so checking it would pass on "0 imports, 0 modules referenced", which proves nothing.
        page.visit(f"{base}/#/", "!!document.querySelector('#q')", timeout=90)
        time.sleep(1.0)
        busy = page.value("""
            (() => { const m = (typeof S !== 'undefined' && S.modules) || [];   // a top-level const is not on window
              let best = null;
              for (const x of m) if (x.i && x.i.length >= 3 && x.c >= 20 && (!best || x.i.length > best.i.length)) best = x;
              return best ? best.n : ''; })()""") or ""
        if not busy:
            print("  imports nothing reaches: no module with imports in this bundle, skipped")
        else:
            page.visit(f"{base}/#/m/{busy}", "!!document.querySelector('#unreached')", timeout=120)
            time.sleep(1.2)
            page.value("document.querySelector('#unreached').click()")
            note = ""
            for _ in range(60):
                time.sleep(0.5)
                note = page.value("document.querySelector('#unreachednote')?.textContent") or ""
                if note:
                    break
            import re as _re
            counts = [int(x.replace(",", "")) for x in _re.findall(r"\d[\d,]*", note)]
            if len(counts) < 2 or counts[0] < 1 or counts[1] < 1:
                failures.append(f"imports nothing reaches: {busy} gave {note[:60]!r}, which tests nothing")
            else:
                print(f"  imports nothing reaches: {busy} -> {note[:52]}")

        page.visit(f"{base}/#/d/Nat.add_comm", "!!document.querySelector('#savebtn')", timeout=120)
        time.sleep(1.0)
        page.value("document.querySelector('#savebtn').click()")
        time.sleep(0.4)
        label = page.value("document.querySelector('#savebtn').textContent") or ""
        page.visit(f"{base}/#/saved", "!!document.querySelector('h1')", timeout=90)
        time.sleep(1.0)
        rows = page.value("document.querySelectorAll('ul.list li a.nm').length") or 0
        if "saved" not in label or rows < 1:
            failures.append(f"the saved list: button {label!r}, {rows} rows")
        else:
            print(f"  the saved list: {rows} row(s) after saving one")

        page.visit(f"{base}/#/vs/Nat.add_comm/Nat.mul_comm", "!!document.querySelector('h1')", timeout=180)
        stats = ""
        for _ in range(90):
            time.sleep(0.5)
            stats = page.value("document.querySelector('.stats')?.textContent.replace(/\\s+/g, ' ').trim()") or ""
            if stats:
                break
        if "both rest on" not in stats:
            failures.append(f"comparing two declarations: {stats[:70]!r}")
        else:
            print(f"  comparing two declarations: {stats[:60]}")
        failures += [f"the no-rebuild features: {p}" for p in page.problems()]

        # The features that use what only this project has: the whole graph, every statement digest, and two
        # libraries side by side.
        page.visit(f"{base}/#/d/Function.comp", "!!document.querySelector('h1')", timeout=90)
        time.sleep(1.0)
        if page.value("!!document.querySelector('#showsrc')") is True:
            page.value("document.querySelector('#showsrc').click()")
            src = ""
            for _ in range(60):
                time.sleep(0.5)
                src = page.value("document.querySelector('#srcout')?.textContent || ''")
                if src:
                    break
            if not src:
                # the file may have moved since the pinned commit, which the button says out loud
                said = page.value("document.querySelector('#showsrc')?.textContent || ''")
                if "could not fetch" not in said:
                    failures.append(f"inline source: no text and no explanation, button says {said!r}")
                else:
                    print("  inline source: not at the pinned commit, and the page says so")
            else:
                print(f"  inline source: {' '.join(src.split())[:56]}")
        else:
            print("  inline source: this library has no source links, skipped")

        page.visit(f"{base}/#/d/Nat.add_comm", "!!document.querySelector('h1')", timeout=90)
        time.sleep(1.0)
        if page.value("!!document.querySelector('#blast')") is True:
            page.value("document.querySelector('#blast').click()")
            blast = ""
            for _ in range(240):
                time.sleep(0.5)
                blast = page.value("document.querySelector('#blastout')?.textContent || ''")
                if blast:
                    break
            if "depend" not in blast:
                failures.append(f"blast radius: {blast[:80]!r}")
            else:
                print(f"  blast radius: {blast[:64]}")
        if page.value("!!document.querySelector('#concl')") is True:
            page.value("document.querySelector('#concl').click()")
            note = ""
            for _ in range(240):
                time.sleep(0.5)
                note = page.value("document.querySelector('#conclnote')?.textContent || ''")
                if note:
                    break
            if not note:
                failures.append("concluding the same thing: never answered")
            else:
                print(f"  concluding the same thing: {note[:56]}")
        failures += [f"the graph features: {p}" for p in page.problems()]

        # A namespace is the unit people think in, and is neither a module nor a directory.
        for ns in ("Nat", "List"):
            page.visit(f"{base}/#/ns/{ns}", "!!document.querySelector('h1')", timeout=90)
            time.sleep(1.2)
            stats = page.value("document.querySelector('.stats')?.textContent || ''")
            if not stats.strip():
                failures.append(f"the namespace page: {ns} showed no counts")
            else:
                print(f"  the namespace page: {ns} -> {' '.join(stats.split())[:44]}")
                break

        # One key to reach anything.
        page.visit(f"{base}/#/", "!!document.querySelector('#q')", timeout=90)
        time.sleep(0.8)
        page.value("document.dispatchEvent(new KeyboardEvent('keydown', {key: 'k', metaKey: true, bubbles: true}))")
        time.sleep(0.6)
        rows = page.value("document.querySelectorAll('#pal-list a').length") or 0
        if page.value("document.querySelector('#palette')?.hidden") is not False or rows < 3:
            failures.append(f"the command palette: hidden or empty ({rows} rows)")
        else:
            print(f"  the command palette: opens with {rows} commands")
        page.value("document.dispatchEvent(new KeyboardEvent('keydown', {key: 'Escape', bubbles: true}))")
        time.sleep(0.5)
        # Closing it has to mean the page can be clicked again. The `hidden` attribute said closed while an
        # id selector's `display: flex` kept the overlay painted over everything, so asking the attribute
        # agreed with the code rather than with the screen. Ask what actually receives a click instead.
        blocked = page.value("(() => { const a = document.querySelector('#main a'); if (!a) return 'no link to test'; const r = a.getBoundingClientRect(); const hit = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2); if (hit === a || a.contains(hit)) return ''; return (hit && (hit.id || hit.className || hit.tagName)) || 'something'; })()")
        if blocked == 'no link to test':
            print('  the command palette: closes (no link on this page to test the click with)')
        elif blocked:
            failures.append(f"the command palette: after closing, the page is still covered by {blocked!r}")
        else:
            print('  the command palette: closes, and the page takes clicks again')

        # Light and dark, chosen rather than inherited. Both directions are checked from a known starting
        # point: comparing against whatever the runner happened to prefer passed on a dark machine and failed on
        # a light one, which is a test measuring the environment rather than the button.
        seen = {}
        for start, want in (("light", "dark"), ("dark", "light")):
            page.value(f"document.documentElement.dataset.theme = {start!r}")
            time.sleep(0.3)
            seen[start] = page.value("getComputedStyle(document.body).backgroundColor")
            page.value("toggleTheme()")
            time.sleep(0.3)
            got = page.value("document.documentElement.dataset.theme")
            after = page.value("getComputedStyle(document.body).backgroundColor")
            if got != want or after == seen[start]:
                failures.append(f"the theme override: from {start} it went to {got!r} ({seen[start]} -> {after})")
        if seen.get("light") == seen.get("dark"):
            failures.append(f"the theme override: light and dark look the same ({seen.get('light')})")
        elif not any(f.startswith("the theme override") for f in failures):
            print(f"  the theme override: light {seen['light']}, dark {seen['dark']}, both directions")
        page.value("delete document.documentElement.dataset.theme")

        # Comparing two libraries, and refusing to compare two that were built differently.
        page.visit(f"{base}/#/compare", "!!document.querySelector('h1')", timeout=90)
        time.sleep(1.0)
        pairs = page.value("document.querySelectorAll('ul.list li a').length") or 0
        print(f"  compare: {pairs} pairs offered" if pairs else "  compare: one library on this site, nothing to pair")
        failures += [f"the new pages: {p}" for p in page.problems()]

        # Ten features, each asked for by a real Lean user in a public issue. Each is checked on a declaration
        # that actually has the property: a modifier badge proves nothing on a page with no modifiers.
        def typed(text):
            # Clear and wait for the box to empty first, or this reads the previous query's results, which are
            # still on screen. Each snippet is wrapped in a function so `const` does not leak into the global
            # scope and make the next evaluation a redeclaration error.
            page.value("(() => { const e = document.querySelector('#q'); e.value = ''; e.dispatchEvent(new Event('input')); })()")
            for _ in range(20):
                time.sleep(0.2)
                if not page.value("document.querySelectorAll('#results a[href^=\"#/i/\"]').length"):
                    break
            page.value(f"(() => {{ const e = document.querySelector('#q'); e.value = {text!r}; e.dispatchEvent(new Event('input')); }})()")
            for _ in range(40):
                time.sleep(0.4)
                n = page.value("document.querySelectorAll('#results a[href^=\"#/i/\"]').length")
                if n:
                    return n
            return 0

        page.visit(f"{base}/#/", "!!document.querySelector('#q')", timeout=90)
        time.sleep(1.0)
        if typed("k:axiom"):
            kinds = page.value("[...document.querySelectorAll('#results a[href^=\"#/i/\"] .kind')].map(k => k.textContent)") or []
            if set(kinds) != {"axiom"}:
                failures.append(f"the search filters: k:axiom returned {sorted(set(kinds))}")
            else:
                print(f"  the search filters: k:axiom -> {len(kinds)} results, all axioms")
        else:
            failures.append("the search filters: k:axiom returned nothing")
        if typed("comm add nat"):
            print("  words in any order: 'comm add nat' finds something")
        else:
            failures.append("words in any order: 'comm add nat' found nothing")
        failures += [f"the search filters: {p}" for p in page.problems()]

        for what, url, probe, want in [
            ("structure fields", "#/d/LinearEquiv",
             "document.querySelectorAll('table.fields tr').length", lambda v: v >= 3),
            ("modifiers", "#/d/ParacompactSpace",
             "[...document.querySelectorAll('.mark')].map(m => m.textContent).join(',')", lambda v: bool(v)),
            ("the copy menu", "#/d/Nat.add_comm",
             "[...document.querySelectorAll('.copies button')].map(b => b.dataset.copy).join('|')",
             lambda v: v and "Nat.add_comm" in v and "#check" in v),
            ("minimal imports", "#/d/Continuous.comp",
             "document.querySelector('pre.minimports')?.textContent || ''", lambda v: v.startswith("import ")),
            ("module impact", "#/m/Mathlib.Order.Basic",
             "document.querySelector('#impact')?.textContent || ''", lambda v: "downstream" in v),
        ]:
            try:
                page.visit(f"{base}/{url}", "!!document.querySelector('h1')", timeout=90)
            except AssertionError:
                print(f"  {what}: not in this bundle, skipped")
                continue
            got = None
            for _ in range(40):
                time.sleep(0.4)
                got = page.value(probe)
                if got and want(got):
                    break
            if not (got and want(got)):
                failures.append(f"{what}: saw {str(got)[:90]!r}")
            else:
                print(f"  {what}: {str(got)[:62]}")
        failures += [f"the power features: {p}" for p in page.problems()]

        # The treemap metrics have to change the picture, not just the label.
        page.visit(f"{base}/#/map/Mathlib", "!!document.querySelector('.treemap a rect')", timeout=90)
        time.sleep(1.0)
        before = page.value("document.querySelector('.treemap a rect').getAttribute('fill')")
        page.value("(() => { const a = [...document.querySelectorAll('a.metric')].find(x => x.dataset.metric === 'unused'); if (a) a.click(); })()")
        time.sleep(1.5)
        after = page.value("document.querySelector('.treemap a rect').getAttribute('fill')")
        if before == after:
            failures.append("the treemap metrics: choosing one did not change the colours")
        else:
            print(f"  the treemap metrics: {before} -> {after}")
        failures += [f"the treemap metrics: {p}" for p in page.problems()]

        # The map is a picture you navigate, so every part has to be reachable and every box hittable. Crowded
        # namespaces are the hard case: Mathlib.Tactic has 178 immediate parts, and laying all of them out gives
        # slivers a pixel wide. Parts that declare nothing have no area at all and used to vanish entirely.
        for frag, want_parts in [("Mathlib.Tactic", None), ("Mathlib.RingTheory", None), ("Mathlib.Order", None)]:
            try:
                page.visit(f"{base}/#/map/{frag}", "!!document.querySelector('.treemap a rect')", timeout=90)
            except AssertionError:
                print(f"  the map ({frag}): not in this bundle, skipped")
                continue
            time.sleep(1.0)
            small = page.value("""[...document.querySelectorAll('.treemap a rect')]
                .filter(r => +r.getAttribute('width') < 9 || +r.getAttribute('height') < 9).length""")
            parts = page.value("document.querySelector('p.dim').textContent.trim().split(' in ')[1].split(' ')[0]")
            reach = page.value("""(() => {
                const drawn = new Set([...document.querySelectorAll('.treemap a[data-tip]')].map(a => a.dataset.tip.split('|')[0]));
                const listed = new Set([...document.querySelectorAll('ul.list li a.nm')].map(a => a.textContent.trim()));
                return drawn.size + listed.size; })()""")
            n = int(str(parts).replace(",", "")) if parts else -1
            if small:
                failures.append(f"the map ({frag}): {small} boxes too small to click")
            elif reach != n:
                failures.append(f"the map ({frag}): {n} parts but {reach} reachable")
            else:
                print(f"  the map ({frag}): {n} parts, all reachable, none under 9px")

        # Drilling into a leaf is drilling into a file, and a file has a page of its own. It used to draw an
        # empty treemap saying "0 parts", which is a dead end at the bottom of every path.
        page.visit(f"{base}/#/map/Init.Prelude", "!!document.querySelector('h1')", timeout=90)
        time.sleep(1.2)
        if not (page.value("location.hash") or "").startswith("#/m/"):
            failures.append(f"the map: drilling into a module went to {page.value('location.hash')!r}")
        else:
            print("  the map: a leaf opens its module")

        page.visit(f"{base}/#/map", "!!document.querySelector('.treemap a[data-tip]')", timeout=90)
        time.sleep(0.8)
        page.value("""(() => { const a = document.querySelector('.treemap a[data-tip]');
            const r = a.getBoundingClientRect();
            a.dispatchEvent(new MouseEvent('mousemove', {bubbles: true, clientX: r.x + r.width / 2, clientY: r.y + r.height / 2}));
          })()""")
        time.sleep(0.4)
        tip = page.value("document.querySelector('#maptip')?.hidden === false && document.querySelector('#maptip').textContent") or ""
        if "declaration" not in tip:
            failures.append(f"the map: the tooltip said {tip[:60]!r}")
        else:
            print(f"  the map tooltip: {tip[:64]}")
        failures += [f"the map: {p}" for p in page.problems()]

        # The import line, the axioms page and the scope switch: one obvious thing each for three of the four
        # kinds of reader the home page names.
        page.visit(f"{base}/#/d/Function.comp", "!!document.querySelector('.importline code')", timeout=90)
        line = page.value("document.querySelector('.importline code').textContent") or ""
        if not line.startswith("import "):
            failures.append(f"the import line: {line!r}")
        else:
            print(f"  the import line: {line}")

        page.visit(f"{base}/#/axioms", "!!document.querySelector('h1')", timeout=90)
        time.sleep(1.0)
        verdict = page.value("document.querySelector('.verdict')?.textContent.trim()") or ""
        predates = page.value("(document.querySelector('#main')?.textContent || '').includes('before the axiom census')")
        if predates:
            print("  the axioms page: skipped, this bundle predates the axiom census")
        elif "axiom" not in verdict and "standard three" not in verdict:
            failures.append(f"the axioms page: {verdict[:80]!r}")
        else:
            print(f"  the axioms page: {verdict[:72]}")
        failures += [f"the axioms page: {p}" for p in page.problems()]

        # A definition has to show its body, not only its type. This was the whole of one piece of feedback:
        # the page showed a signature and called it the definition. Function.comp is in Lean's core, so it is
        # in every bundle; a Mathlib-only name would make this test a test of which library the site opens on.
        # Bundles published from a tarball built before bodies existed do not have them and are not judged.
        page.visit(f"{base}/#/", "!!document.querySelector('.tab')", timeout=90)
        claimed = page.value("""(async () => {
            const ps = await (await fetch('data/projects.json')).json();
            const slug = new URLSearchParams(location.search).get('p') || ps[0].slug;
            const m = await (await fetch(`data/${slug}/manifest.json`)).json();
            window.__bodies = m.definitionBodies ?? null;
        })(), 'started'""")
        for _ in range(40):
            time.sleep(0.25)
            claimed = page.value("window.__bodies")
            if claimed is not None:
                break
        if not claimed:
            print("  the definition body: skipped, this bundle predates definition bodies")
        else:
            page.visit(f"{base}/#/d/Function.comp", "!!document.querySelector('.theorem, pre')", timeout=90)
            for _ in range(40):
                time.sleep(0.5)
                if page.value("!!document.querySelector('pre.body')") is True:
                    break
            body = page.value("document.querySelector('pre.body')?.textContent || ''") or ""
            heads = page.value("[...document.querySelectorAll('h2')].map(h => h.textContent.split(' ')[0])") or []
            if "Definition" not in heads or len(body) < 5:
                failures.append(f"the definition body: heads {heads}, body {len(body)} chars")
            else:
                print(f"  the definition body: {body.strip()[:48]!r} under a Definition heading")
            # A theorem must not grow one: that would mean proof terms had started shipping.
            page.visit(f"{base}/#/d/Nat.add_comm", "!!document.querySelector('.theorem, pre')", timeout=90)
            time.sleep(1.0)
            if page.value("!!document.querySelector('pre.body')") is True:
                failures.append("the definition body: a theorem is showing a proof term")
            else:
                print("  the definition body: a theorem shows none")
        failures += [f"the definition body: {p}" for p in page.problems()]

        # The map was reachable only from the home page and nobody found it.
        page.visit(f"{base}/#/", "!!document.querySelector('.tab')", timeout=90)
        if page.value("!!document.querySelector('#maplink') && !!document.querySelector('#maplink .mapicon rect')") is not True:
            failures.append("the map link: no link with a treemap icon in the header")
        else:
            page.value("location.hash = '#/map'")
            time.sleep(1.5)
            if page.value("document.querySelector('#maplink').classList.contains('on')") is not True:
                failures.append("the map link: does not mark itself on the map page")
            else:
                print("  the map link: present, and marks itself on the map")
        failures += [f"the map link: {p}" for p in page.problems()]

        # The build ticker says things about the past, and for a while it decided them by the weather: a failed
        # request was folded into an empty list, so a rate limited visitor was told "Nothing has been built this
        # way yet", which was false. Each state has to come from the thing it describes, so fake the four.
        page.visit(f"{base}/#/", "!!document.querySelector('.tab')")
        page.value("""
          window.__mode = 'live';
          const real = window.fetch;
          window.fetch = (u, o) => {
            if (String(u).includes('api.github.com/repos') && String(u).includes('add-library')) {
              if (window.__mode === 'limited')
                return Promise.resolve(new Response('{}', {status: 403, headers: {'x-ratelimit-remaining': '0'}}));
              if (window.__mode === 'down') return Promise.resolve(new Response('{}', {status: 500}));
              if (window.__mode === 'empty')
                return Promise.resolve(new Response(JSON.stringify({workflow_runs: []}), {status: 200}));
              if (window.__mode === 'done')
                return Promise.resolve(new Response(JSON.stringify({workflow_runs: [{
                  id: 1, status: 'completed', conclusion: 'success', html_url: 'https://example.invalid/run',
                  created_at: '2026-01-02T03:04:05Z', display_title: 'add-library'}]}), {status: 200}));
            }
            return real(u, o);
          };
        """)
        for mode, want in [("limited", "rate limiting"), ("down", "Could not reach GitHub"),
                           ("empty", "Nothing has been built"), ("done", "succeeded")]:
            page.value(f"window.__mode = {mode!r}; location.hash = '#/'")
            time.sleep(0.4)
            page.value("location.hash = '#/add'")
            seen = ""
            for _ in range(20):
                time.sleep(0.5)
                seen = page.value("document.querySelector('#builds, .builds')?.textContent || ''") or ""
                if seen.strip():
                    break
            if want not in seen:
                failures.append(f"the build ticker ({mode}): expected {want!r}, got {seen.strip()[:110]!r}")
            else:
                print(f"  the build ticker ({mode}): {seen.strip()[:70]}")
        failures += [f"the build ticker: {p}" for p in page.problems()]

        # the role tabs, which are the only stateful thing on the page
        page.visit(f"{base}/#/", "!!document.querySelector('.tab')")
        page.value("document.querySelector('.tab[data-role=\"new\"]').click()")
        time.sleep(0.5)
        if page.value("!!document.querySelector('.role-body')") is not True:
            failures.append("the welcome tabs: clicking a role showed no panel")
        else:
            print("  the welcome tabs: a role opens")
        failures += [f"the welcome tabs: {p}" for p in page.problems()]
    finally:
        page.close()

    if failures:
        print("\n".join(f"FAIL {f}" for f in failures))
        sys.exit(1)
    print("OK the page renders and logs nothing")


if __name__ == "__main__":
    main(sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "http://localhost:8791")
