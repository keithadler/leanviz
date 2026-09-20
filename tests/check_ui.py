#!/usr/bin/env python3
"""Check what a person can actually see and click, rather than what the code meant.

Every UI bug this project has shipped had a passing test beside it, because the test asked the code a question
the code was always going to answer correctly. The command palette is the clearest case: its overlay covered
every page and swallowed every click, and the test asked whether the `hidden` attribute was set. It was. The
attribute agreed with the code and never looked at the screen.

So these are invariants about the rendered page, phrased the way a complaint would be:

  1. nothing is covering the page            (hit-test the middle and the corners)
  2. links can actually be clicked           (hit-test each one where it is drawn)
  3. hidden means invisible                  (the exact bug above, generalised to every element)
  4. nothing runs off the side of the screen (desktop and phone)
  5. controls are big enough to hit
  6. what opens can close                    (and gives the page back)

Run against anything:

    python3 tests/check_ui.py http://localhost:8787
    python3 tests/check_ui.py https://keithadler.github.io/leanviz
"""
from __future__ import annotations

import sys
import time

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from check_page import Page   # noqa: E402  the browser driver, already written

ROUTES = [
    ("the home page", "#/"),
    ("a declaration", "#/d/Nat.add_comm"),
    ("a definition", "#/d/Function.comp"),
    ("a module", "#/m/Init.Prelude"),
    ("the map", "#/map"),
    ("a namespace", "#/ns/Nat"),
    ("the axioms page", "#/axioms"),
    ("the unused list", "#/unused"),
    ("the holes page", "#/holes"),
    ("the request page", "#/add"),
    ("compare", "#/compare"),
    ("saved", "#/saved"),
    ("the certificate page", "#/certificate"),
    ("two declarations", "#/vs/Nat.add_comm/Nat.mul_comm"),
]

# What is on top at nine points across the viewport. A full-page overlay shows up at every one of them.
COVERING = """
(() => {
  const w = innerWidth, h = innerHeight, out = [];
  for (const x of [w * 0.15, w * 0.5, w * 0.85]) {
    for (const y of [h * 0.2, h * 0.5, h * 0.8]) {
      const e = document.elementFromPoint(x, y);
      if (!e) continue;
      // A header or a footer at the top or bottom is the page, not something over it.
      const over = e.closest('#palette, #keys, .results:not([hidden]), [hidden]');
      if (over) out.push((over.id || over.className || over.tagName) + ` at ${Math.round(x)},${Math.round(y)}`);
    }
  }
  return out.join('; ');
})()
"""

# The bug itself, stated as a rule that holds everywhere: an element carrying `hidden` must not be painted.
HIDDEN_BUT_SHOWN = """
(() => {
  const bad = [];
  for (const e of document.querySelectorAll('[hidden]')) {
    if (getComputedStyle(e).display !== 'none') bad.push(e.id || e.className || e.tagName);
  }
  return bad.join(', ');
})()
"""

# Every link where it is drawn: does a click at its centre reach it?
LINKS_BLOCKED = """
(() => {
  const bad = [];
  const links = [...document.querySelectorAll('#main a')].slice(0, 60);
  for (const a of links) {
    // Chrome still reports a rectangle for content inside a collapsed <details>, which is not drawn and not
    // hit-testable. Asking whether it can be clicked is asking about something nobody can see.
    const det = a.closest('details');
    if (det && !det.open) continue;
    const r = a.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) continue;                       // not drawn: a different question
    if (r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) continue;   // off screen
    const hit = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
    if (!hit) continue;
    if (hit === a || a.contains(hit) || hit.contains(a)) continue;
    bad.push((a.textContent || '').trim().slice(0, 24) + ' <- ' + (hit.id || hit.className || hit.tagName));
  }
  return bad.slice(0, 4).join('; ');
})()
"""

OVERFLOW = "Math.max(0, document.documentElement.scrollWidth - innerWidth)"

TOO_SMALL = """
(() => {
  const bad = [];
  for (const e of document.querySelectorAll('button, input, #main a')) {
    const r = e.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) continue;   // deliberately not drawn
    if (r.height < 12 || r.width < 12) bad.push((e.textContent || e.tagName).trim().slice(0, 20) + ` ${Math.round(r.width)}x${Math.round(r.height)}`);
  }
  return bad.slice(0, 3).join('; ');
})()
"""


def main(base: str) -> None:
    base = base.rstrip("/")
    failures: list[str] = []
    page = Page(width=1280, height=880)
    try:
        for label, route in ROUTES:
            try:
                page.visit(f"{base}/{route}", "!!document.querySelector('#main')", timeout=90)
            except AssertionError as e:
                failures.append(f"{label}: never rendered ({e})")
                continue
            time.sleep(1.4)   # the page fills in after its fetches

            covering = page.value(COVERING)
            if covering:
                failures.append(f"{label}: something is covering the page: {covering}")

            shown = page.value(HIDDEN_BUT_SHOWN)
            if shown:
                failures.append(f"{label}: marked hidden but still painted: {shown}")

            blocked = page.value(LINKS_BLOCKED)
            if blocked:
                failures.append(f"{label}: links that cannot be clicked where they are drawn: {blocked}")

            over = page.value(OVERFLOW) or 0
            if over > 4:
                failures.append(f"{label}: runs {over}px off the side of the screen")

            small = page.value(TOO_SMALL)
            if small:
                failures.append(f"{label}: controls too small to hit: {small}")

            if not any(f.startswith(label) for f in failures):
                print(f"  {label}: nothing covering it, links clickable, fits the screen")

        # The overlays, each opened and closed, with the page taken back afterwards.
        page.visit(f"{base}/#/", "!!document.querySelector('#q')", timeout=90)
        time.sleep(1.0)
        for name, open_js in [
            ("the command palette", "document.dispatchEvent(new KeyboardEvent('keydown', {key: 'k', metaKey: true, bubbles: true}))"),
            ("the keys dialog", "document.dispatchEvent(new KeyboardEvent('keydown', {key: '?', bubbles: true}))"),
        ]:
            page.value(open_js)
            time.sleep(0.6)
            opened = page.value(COVERING)
            if not opened:
                print(f"  {name}: did not open (or does not cover, which is fine)")
            page.value("document.dispatchEvent(new KeyboardEvent('keydown', {key: 'Escape', bubbles: true}))")
            time.sleep(0.6)
            left = page.value(COVERING)
            if left:
                failures.append(f"{name}: still covering the page after Escape: {left}")
            else:
                print(f"  {name}: opens and closes, and gives the page back")

        # A phone. Half the reports about a site being unusable are about a width nobody tested.
        page2 = Page(width=390, height=780)
        try:
            for label, route in [("the home page", "#/"), ("a declaration", "#/d/Nat.add_comm"), ("the map", "#/map")]:
                try:
                    page2.visit(f"{base}/{route}", "!!document.querySelector('#main')", timeout=90)
                except AssertionError:
                    failures.append(f"{label} at 390px: never rendered")
                    continue
                time.sleep(1.4)
                over = page2.value(OVERFLOW) or 0
                cover = page2.value(COVERING)
                if over > 4:
                    failures.append(f"{label} at 390px: runs {over}px off the side")
                elif cover:
                    failures.append(f"{label} at 390px: covered by {cover}")
                else:
                    print(f"  {label} at 390px: fits, nothing covering it")
            failures += [f"on a phone: {p}" for p in page2.problems()]
        finally:
            page2.close()

        failures += [f"logged: {p}" for p in page.problems()]
    finally:
        page.close()

    if failures:
        for f in failures:
            print(f"FAIL {f}")
        raise SystemExit(1)
    print("OK the page can be seen and clicked")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8787")
