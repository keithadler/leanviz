#!/usr/bin/env python3
"""List the dated digest snapshots, and diff any two of them.

Every deploy overwrites the published bundle, so until these existed there was no way to ask what a week of
Mathlib did to the library: the previous answer was gone the moment the new one went up. Each deploy now keeps
a `<slug>-<date>.tar.gz` on the `history` release holding only names.txt.gz and digest.bin.gz, which is about
10 MB against the bundle's 146 MB and is exactly the pair `diff_bundles.py` reads.

    python3 tools/history.py keep mathlib          # snapshot what is live now
    python3 tools/history.py list mathlib
    python3 tools/history.py diff mathlib 2026-09-19 2026-09-26
    python3 tools/history.py fetch mathlib 2026-09-19 /tmp/snap

`diff` fetches both snapshots and hands them to diff_bundles.py, so its output and its rules are the same.
"""
from __future__ import annotations   # macOS ships Python 3.9, where `str | None` in a signature is a TypeError

import json
import pathlib
import subprocess
import sys
import tarfile
import tempfile

REPO = "keithadler/leanviz"
RELEASE = "history"


def assets() -> list[dict]:
    out = subprocess.run(["gh", "release", "view", RELEASE, "--repo", REPO, "--json", "assets"],
                         capture_output=True, text=True)
    if out.returncode != 0:
        raise SystemExit(f"no `{RELEASE}` release on {REPO} yet; it is created by the first deploy that keeps one")
    return json.loads(out.stdout)["assets"]


def snapshots(slug: str) -> list[tuple[str, dict]]:
    """Every dated snapshot of one library, oldest first."""
    found = []
    for a in assets():
        name = a["name"]
        if not name.startswith(f"{slug}-") or not name.endswith(".tar.gz"):
            continue
        date = name[len(slug) + 1:-len(".tar.gz")]
        # a bundle tarball is not a snapshot, and "bundle" is not a date
        if len(date) == 10 and date[4] == "-" and date[7] == "-":
            found.append((date, a))
    return sorted(found)


def fetch(slug: str, date: str, into: pathlib.Path) -> pathlib.Path:
    into.mkdir(parents=True, exist_ok=True)
    name = f"{slug}-{date}.tar.gz"
    subprocess.run(["gh", "release", "download", RELEASE, "--repo", REPO, "--pattern", name,
                    "--dir", str(into), "--clobber"], check=True)
    with tarfile.open(into / name) as t:
        for member in t.getmembers():
            if not str((into / member.name).resolve()).startswith(str(into.resolve())):
                raise SystemExit(f"{name}: member {member.name!r} escapes {into}")
        t.extractall(into)
    return into


def keep(slug: str, site: str, date: str | None = None) -> str:
    """Snapshot what a published site is serving right now, without waiting for a deploy.

    The deploy keeps one on its way past, which covers every day from here on. This covers today: the live
    bundle still holds the answer, and it stops holding it the next time anything is published over it.
    """
    import datetime
    import urllib.request
    date = date or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    base = f"{site.rstrip('/')}/data/{slug}"
    with tempfile.TemporaryDirectory() as tmp:
        snap = pathlib.Path(tmp) / "snap"
        snap.mkdir()
        for name in ("names.txt.gz", "digest.bin.gz"):
            with urllib.request.urlopen(f"{base}/{name}") as r:
                (snap / name).write_bytes(r.read())
        with urllib.request.urlopen(f"{base}/manifest.json") as r:
            m = json.load(r)
        (snap / "meta.json").write_text(json.dumps(
            {k: m.get(k) for k in ("generated", "lean", "declarations", "modules", "slug", "title", "libraries", "check")},
            indent=2))
        tar = pathlib.Path(tmp) / f"{slug}-{date}.tar.gz"
        with tarfile.open(tar, "w:gz") as out:
            for f in sorted(snap.iterdir()):
                out.add(f, arcname=f.name)
        subprocess.run(["gh", "release", "create", RELEASE, "--repo", REPO, "--title", "Digest history",
                        "--notes", "A dated names and digest snapshot per library per deploy."],
                       capture_output=True, text=True)
        subprocess.run(["gh", "release", "upload", RELEASE, str(tar), "--clobber", "--repo", REPO], check=True)
        return f"{tar.name} ({tar.stat().st_size / 1048576:.1f} MB), {m.get('declarations'):,} declarations"


def main(argv: list[str]) -> None:
    if not argv:
        print(__doc__)
        raise SystemExit(2)
    what = argv[0]
    if what == "list":
        slug = argv[1] if len(argv) > 1 else "mathlib"
        rows = snapshots(slug)
        if not rows:
            print(f"no snapshots of {slug} yet")
            return
        for date, a in rows:
            print(f"  {date}  {a['size'] / 1048576:.1f} MB")
        print(f"{len(rows)} snapshot{'' if len(rows) == 1 else 's'} of {slug}, "
              f"{rows[0][0]} to {rows[-1][0]}")
    elif what == "keep":
        slug = argv[1]
        site = argv[2] if len(argv) > 2 else "https://keithadler.github.io/leanviz"
        print("  kept " + keep(slug, site))
    elif what == "fetch":
        slug, date, where = argv[1], argv[2], pathlib.Path(argv[3])
        print(fetch(slug, date, where))
    elif what == "diff":
        slug, old, new = argv[1], argv[2], argv[3]
        with tempfile.TemporaryDirectory() as tmp:
            a = fetch(slug, old, pathlib.Path(tmp) / old)
            b = fetch(slug, new, pathlib.Path(tmp) / new)
            print(f"{slug}: {old} -> {new}\n")
            subprocess.run([sys.executable, "tools/diff_bundles.py", str(a), str(b)] + argv[4:], check=False)
    else:
        print(__doc__)
        raise SystemExit(2)


if __name__ == "__main__":
    main(sys.argv[1:])
