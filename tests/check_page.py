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
            ("the map", f"{base}/#/map", "!!document.querySelector('.treemap a rect')",
             "document.querySelectorAll('.treemap a').length > 3"),
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
