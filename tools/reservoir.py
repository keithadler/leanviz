#!/usr/bin/env python3
"""The Lean package registry, as a list of things worth auditing.

Reservoir's index is a git repository of metadata, one directory per package, and it already records whether
Reservoir's own build of each one succeeded. That is the denominator nobody had to compute: of 858 packages,
336 build today and 521 do not, 441 of those having built at some point before.

This picks the ones worth spending a runner on, newest-build-first within a star order, and prints them as the
matrix a workflow can consume.

    python3 tools/reservoir.py --clone /tmp/ri --limit 24
    python3 tools/reservoir.py --clone /tmp/ri --limit 24 --json
"""
from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys

INDEX = "https://github.com/leanprover/reservoir-index.git"


def load(where: pathlib.Path) -> list[dict]:
    if not where.exists():
        subprocess.run(["git", "clone", "--depth", "1", "-q", INDEX, str(where)], check=True)
    out = []
    for meta in where.rglob("metadata.json"):
        try:
            m = json.loads(meta.read_text())
        except Exception:
            continue
        builds = []
        b = meta.parent / "builds.json"
        if b.exists():
            try:
                builds = json.loads(b.read_text()).get("data", [])
            except Exception:
                pass
        latest = builds[0] if builds else {}
        src = next((s for s in m.get("sources", []) if s.get("host") == "github"), {})
        out.append({
            "full": m["fullName"],
            "repo": src.get("fullName") or m["fullName"],
            "stars": m.get("stars") or 0,
            "builds": bool(latest.get("built")),
            "toolchain": latest.get("toolchain") or "",
            "everBuilt": any(x.get("built") for x in builds),
            "description": (m.get("description") or "")[:100],
        })
    return out


def slug(full: str) -> str:
    """A url-safe name for the site, matching what tools/parse_request.py would make of it."""
    name = full.split("/")[-1].lower()
    out = "".join(c if c.isalnum() else "-" for c in name).strip("-")
    while "--" in out:
        out = out.replace("--", "-")
    return out[:40]


def main(argv: list[str]) -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--clone", default="/tmp/reservoir-index", help="where to keep the index checkout")
    ap.add_argument("--limit", type=int, default=24)
    ap.add_argument("--skip", type=int, default=0, help="how many of the ranked list to pass over, for batches")
    ap.add_argument("--all", action="store_true", help="include packages Reservoir could not build")
    ap.add_argument("--json", action="store_true", help="print a workflow matrix")
    ap.add_argument("--stats", action="store_true", help="print what the index says and stop")
    a = ap.parse_args(argv)

    pkgs = load(pathlib.Path(a.clone))
    if a.stats:
        ok = [p for p in pkgs if p["builds"]]
        ever = [p for p in pkgs if not p["builds"] and p["everBuilt"]]
        print(f"{len(pkgs)} packages in the index")
        print(f"  {len(ok)} build today ({100 * len(ok) / len(pkgs):.0f}%)")
        print(f"  {len(pkgs) - len(ok)} do not, of which {len(ever)} built at some point")
        return

    pool = pkgs if a.all else [p for p in pkgs if p["builds"]]
    pool.sort(key=lambda p: -p["stars"])
    picked = pool[a.skip:a.skip + a.limit]
    if a.json:
        print(json.dumps([{"repo": p["repo"], "slug": slug(p["full"])} for p in picked]))
    else:
        for p in picked:
            print(f"  {p['stars']:>5}  {p['repo']:<48} {slug(p['full']):<24} {p['toolchain']}")


if __name__ == "__main__":
    main(sys.argv[1:])
