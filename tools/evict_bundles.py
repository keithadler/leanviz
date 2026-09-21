#!/usr/bin/env python3
"""Drop the oldest guest bundles when the site would outgrow what GitHub Pages will serve.

The limit is a gigabyte of published site, so the thing to count is megabytes, not libraries. Counting
libraries threw away a 34 MB guest while 456 MB sat unused: the three protected libraries are most of the
weight (Mathlib, FLT and Navier-Stokes are about 450 MB between them) and a guest averages around 20 MB, so a
fixed count of seven was wrong by more than a factor of ten.

Protected bundles are never removed and always counted first. Guests are kept newest-first until the budget
runs out, and the rest are deleted from the release.

    python3 tools/evict_bundles.py --budget-mb 820
"""
import argparse
import json
import subprocess

from libraries import HOME


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", type=int, default=0, help="a hard cap on guests as well, 0 for none")
    ap.add_argument("--budget-mb", type=int, default=820,
                    help="how much published site to allow; GitHub Pages serves at most 1024 MB")
    ap.add_argument("--protect", default=",".join(HOME))
    ap.add_argument("--release", default="bundles")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    protected = {s.strip() for s in args.protect.split(",") if s.strip()}

    raw = subprocess.run(["gh", "release", "view", args.release, "--json", "assets"],
                         capture_output=True, text=True, check=True).stdout
    assets = json.loads(raw)["assets"]
    bundles = [a for a in assets if a["name"].endswith("-bundle.tar.gz")]
    bundles.sort(key=lambda a: a.get("updatedAt") or a.get("createdAt") or "", reverse=True)

    budget = args.budget_mb * 1048576
    kept, dropped, used = [], [], 0

    # Protected first: they are the site and their size is not negotiable, so whatever they cost is spent
    # before any guest is considered.
    for a in bundles:
        if a["name"].removesuffix("-bundle.tar.gz") in protected:
            kept.append(a["name"].removesuffix("-bundle.tar.gz"))
            used += a["size"]

    guests = 0
    for a in bundles:
        slug = a["name"].removesuffix("-bundle.tar.gz")
        if slug in protected:
            continue
        over_budget = used + a["size"] > budget
        over_count = args.keep and guests >= args.keep
        if over_budget or over_count:
            dropped.append(a["name"])
            continue
        kept.append(slug)
        used += a["size"]
        guests += 1

    total = sum(a["size"] for a in bundles) / 1048576
    print(f"{len(bundles)} bundles, {total:.0f} MB; keeping {used / 1048576:.0f} MB of a "
          f"{args.budget_mb} MB budget: {', '.join(kept)}")
    for name in dropped:
        print(f"  dropping {name}")
        if not args.dry_run:
            subprocess.run(["gh", "release", "delete-asset", args.release, name, "--yes"], check=True)


if __name__ == "__main__":
    main()
