#!/usr/bin/env python3
"""Render the ecosystem audit as a table for the README, in aggregate.

Deliberately says nothing about any individual package. A build failure here is usually a statement about this
builder rather than about someone's repository: it could not handle a project with C bindings at all until
leanhttp proved it, and publishing "leanhttp does not build" under its author's name would have been both false
and unkind. A `sorry` count is worse, because a formalization in progress is supposed to have holes and a
number next to somebody's name reads as a defect list.

What is safe and actually interesting is the shape of the whole: how much of the ecosystem an independent
kernel accepts, and what it reaches for beyond Lean's three standard axioms.

    python3 tools/audit_readme.py audit.json README.md
"""
from __future__ import annotations

import collections
import json
import pathlib
import sys

START = "<!-- audit:start -->"
END = "<!-- audit:end -->"


def render(rows: list[dict]) -> str:
    built = [r for r in rows if r.get("built")]
    checked = [r for r in built if r.get("reChecked")]
    clean = [r for r in checked if r.get("rejected") == 0]
    decls = sum(r.get("reChecked") or 0 for r in checked)

    # Which non-standard axioms the ecosystem actually reaches, counted by package rather than by declaration,
    # so one enormous library does not drown out everyone else.
    reach = collections.Counter()
    for r in checked:
        for name in (r.get("axiomsBeyondStandard") or {}):
            reach[name] += 1
    nothing_beyond = sum(1 for r in checked if not (r.get("axiomsBeyondStandard") or {}))

    out = [
        f"Audited **{len(rows)}** packages from [Reservoir](https://reservoir.lean-lang.org), "
        f"of the 858 it lists and the 336 it can build today.",
        "",
        "| | |",
        "| --- | --- |",
        f"| built here | {len(built)} |",
        f"| re-checked by an independent kernel | {len(checked)} |",
        f"| accepted with nothing rejected | {len(clean)} |",
        f"| declarations re-derived | {decls:,} |",
    ]
    if checked:
        out += ["", "What they reach for beyond `propext`, `Classical.choice` and `Quot.sound`, "
                    "counted by package:", "", "| axiom | packages |", "| --- | --- |",
                f"| *nothing beyond the standard three* | {nothing_beyond} |"]
        for name, n in reach.most_common(8):
            out.append(f"| `{name}` | {n} |")
    out += [
        "",
        "A package missing from these counts is one this builder could not build, which is usually a statement "
        "about the builder. No per-package results are published here.",
    ]
    return "\n".join(out)


def main(argv: list[str]) -> None:
    if len(argv) < 2:
        print(__doc__)
        raise SystemExit(2)
    rows = json.loads(pathlib.Path(argv[0]).read_text())
    readme = pathlib.Path(argv[1])
    text = readme.read_text()
    if START not in text or END not in text:
        raise SystemExit(f"{readme}: no {START} / {END} markers to write between")
    head, rest = text.split(START, 1)
    _, tail = rest.split(END, 1)
    readme.write_text(f"{head}{START}\n{render(rows)}\n{END}{tail}")
    print(f"README updated from {len(rows)} audited packages")


if __name__ == "__main__":
    main(sys.argv[1:])
