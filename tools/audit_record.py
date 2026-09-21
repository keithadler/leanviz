#!/usr/bin/env python3
"""One package's audit verdict, as a few hundred bytes.

The generator already computes everything this needs while writing a bundle, so an audit is a normal run whose
browsable output is thrown away. What is kept is the part that answers the question nobody can answer today:
did an independent kernel accept this package, does anything in it rest on `sorry`, and what does it reach for
beyond Lean's three standard axioms.

    python3 tools/audit_record.py <manifest.json> <owner/repo> <slug>
    python3 tools/audit_record.py --failed <owner/repo> <slug>
"""
from __future__ import annotations

import datetime
import json
import pathlib
import sys


def now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def main(argv: list[str]) -> None:
    if argv and argv[0] == "--failed":
        repo, slug = argv[1], argv[2]
        print(json.dumps({"repo": repo, "slug": slug, "built": False, "at": now()}, indent=1))
        return
    if len(argv) < 3:
        print(__doc__)
        raise SystemExit(2)

    m = json.loads(pathlib.Path(argv[0]).read_text())
    repo, slug = argv[1], argv[2]
    check = m.get("check") or {}
    std = set(m.get("standardAxioms") or [])
    use = m.get("axiomUse") or []
    axioms = m.get("axioms") or []

    # Which axioms beyond the standard three anything in this package actually reaches, and how much reaches
    # each. An axiom the bundle declares but nothing uses says nothing about the package.
    beyond = {}
    for i, name in enumerate(axioms):
        if name in std:
            continue
        n = use[i] if i < len(use) else 0
        if n:
            beyond[name] = n

    own = m.get("ownModules") or []
    print(json.dumps({
        "repo": repo,
        "slug": slug,
        "built": True,
        "at": now(),
        "lean": m.get("lean"),
        "declarations": m.get("declarations"),
        "modules": m.get("modules"),
        "ownModules": len(own),
        "reChecked": check.get("checked"),
        "rejected": check.get("failed"),
        "tenet": check.get("tenet"),
        "restOnSorry": len(m.get("holes") or []),
        "beyondStandard": m.get("beyondStandard"),
        # the ten most reached, so one package with a long tail of compiler internals stays small on disk
        "axiomsBeyondStandard": dict(sorted(beyond.items(), key=lambda kv: -kv[1])[:10]),
    }, indent=1))


if __name__ == "__main__":
    main(sys.argv[1:])
