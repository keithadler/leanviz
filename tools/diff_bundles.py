#!/usr/bin/env python3
"""What changed between two bundles: declarations added, removed, and restated.

Every published bundle carries names.txt.gz and digest.bin.gz, a hash of each declaration's name, its statement
and, for a definition, its body.
Comparing those two files answers the question nobody can answer today, which is what a Mathlib revision did to
the library, without either bundle's shards and without Lean.

    python3 tools/diff_bundles.py old/data/mathlib new/data/mathlib
    python3 tools/diff_bundles.py https://keithadler.github.io/leanviz/data/mathlib new/data/mathlib

Writes a summary to stdout, and with --json a file the page can read.
"""
from __future__ import annotations

import gzip
import json
import pathlib
import sys
import urllib.request


def load(where: str):
    """A bundle's names and digests, from a directory or over http."""
    def get(name: str) -> bytes:
        if where.startswith(("http://", "https://")):
            with urllib.request.urlopen(f"{where.rstrip('/')}/{name}") as r:
                return gzip.decompress(r.read())
        return gzip.decompress((pathlib.Path(where) / name).read_bytes())

    names = get("names.txt.gz").decode().split("\n")
    if names and names[-1] == "":
        names.pop()
    digest = get("digest.bin.gz")
    if len(digest) != 8 * len(names):
        raise SystemExit(f"{where}: {len(names)} names but {len(digest)} digest bytes")
    # Keyed by name and which occurrence it is, never by name alone: two modules can declare the same name, and
    # a dict keyed by the name would silently drop one of them and report the count of distinct names as if it
    # were the count of declarations. In Mathlib 622 names are declared more than once, which is 1,006
    # declarations beyond the number of distinct names, so that error is not small.
    seen: dict = {}
    by_key = {}
    for i, n in enumerate(names):
        nth = seen.get(n, 0)
        seen[n] = nth + 1
        by_key[(n, nth)] = int.from_bytes(digest[8 * i:8 * i + 8], "little")
    return by_key


def main(argv: list[str]) -> None:
    if len(argv) < 2:
        print(__doc__)
        raise SystemExit(2)
    old, new = load(argv[0]), load(argv[1])
    added = sorted(set(new) - set(old))
    removed = sorted(set(old) - set(new))
    restated = sorted(k for k in set(old) & set(new) if old[k] != new[k])

    print(f"{len(old):,} declarations before, {len(new):,} after")
    print(f"  added     {len(added):,}")
    print(f"  removed   {len(removed):,}")
    print(f"  restated  {len(restated):,}")
    for label, group in (("added", added), ("removed", removed), ("restated", restated)):
        shown = [n for n, _ in group if "._" not in n and not n.endswith("✝")][:10]
        if shown:
            print(f"\n{label}:")
            for n in shown:
                print(f"  {n}")

    if "--json" in argv:
        out = pathlib.Path(argv[argv.index("--json") + 1])
        out.write_text(json.dumps({
            "before": len(old), "after": len(new),
            "added": [n for n, _ in added], "removed": [n for n, _ in removed],
            "restated": [n for n, _ in restated],
        }))
        print(f"\nwrote {out}")


if __name__ == "__main__":
    main([a for a in sys.argv[1:]])
