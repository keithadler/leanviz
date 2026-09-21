#!/usr/bin/env python3
"""Fold this run's verdicts into the audit, keeping what earlier runs found.

The audit is built a batch at a time over many runs, so merging has to be additive: a package audited today
replaces its earlier entry, and every package not in this batch is left alone. Overwriting the file with only
the current batch would quietly discard every previous run, which is the obvious way to lose weeks of builds.

    python3 tools/audit_merge.py audit.json incoming/
    python3 tools/audit_merge.py --summary audit.json
"""
from __future__ import annotations

import json
import pathlib
import sys


def summary(path: pathlib.Path) -> None:
    rows = json.loads(path.read_text())
    built = [r for r in rows if r.get("built")]
    clean = [r for r in built if r.get("rejected") == 0]
    holes = [r for r in built if (r.get("restOnSorry") or 0) > 0]
    print(f"{len(rows)} packages audited")
    print(f"  {len(built)} built, {len(rows) - len(built)} did not")
    if built:
        print(f"  {len(clean)} re-checked with nothing rejected")
        print(f"  {len(holes)} have declarations resting on sorry")
        print(f"  {sum(r.get('reChecked') or 0 for r in built):,} declarations re-derived in total")


def main(argv: list[str]) -> None:
    if argv and argv[0] == "--summary":
        summary(pathlib.Path(argv[1]))
        return
    if len(argv) < 2:
        print(__doc__)
        raise SystemExit(2)

    out = pathlib.Path(argv[0])
    rows = json.loads(out.read_text()) if out.exists() else []
    by_repo = {r["repo"]: r for r in rows}

    added = 0
    for f in sorted(pathlib.Path(argv[1]).rglob("record.json")):
        try:
            rec = json.loads(f.read_text())
        except Exception:
            continue
        by_repo[rec["repo"]] = rec
        added += 1

    merged = sorted(by_repo.values(), key=lambda r: r["repo"])
    out.write_text(json.dumps(merged, indent=1))
    print(f"merged {added} record(s); {len(merged)} packages in the audit")


if __name__ == "__main__":
    main(sys.argv[1:])
