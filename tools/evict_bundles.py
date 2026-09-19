#!/usr/bin/env python3
"""Keep the newest few bundles on the release and drop the rest.

GitHub Pages serves at most a gigabyte and a bundle is over a hundred megabytes, so the site holds about seven
libraries. Rather than fail the eighth request, the oldest bundle that nobody protected is removed. Protected
ones are the libraries the site is about; everything else is a guest and may be evicted.

    python3 tools/evict_bundles.py --keep 7 --protect mathlib,flt,nse
"""
import argparse
import json
import subprocess


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", type=int, default=7)
    ap.add_argument("--protect", default="")
    ap.add_argument("--release", default="bundles")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    protected = {s.strip() for s in args.protect.split(",") if s.strip()}

    raw = subprocess.run(["gh", "release", "view", args.release, "--json", "assets"],
                         capture_output=True, text=True, check=True).stdout
    assets = json.loads(raw)["assets"]
    bundles = [a for a in assets if a["name"].endswith("-bundle.tar.gz")]
    bundles.sort(key=lambda a: a.get("updatedAt") or a.get("createdAt") or "", reverse=True)

    kept, dropped = [], []
    for a in bundles:
        slug = a["name"].removesuffix("-bundle.tar.gz")
        if slug in protected or len(kept) < args.keep:
            kept.append(slug)
        else:
            dropped.append(a["name"])

    total = sum(a["size"] for a in bundles) / 1048576
    print(f"{len(bundles)} bundles, {total:.0f} MB; keeping {', '.join(kept)}")
    for name in dropped:
        print(f"  dropping {name}")
        if not args.dry_run:
            subprocess.run(["gh", "release", "delete-asset", args.release, name, "--yes"], check=True)


if __name__ == "__main__":
    main()
