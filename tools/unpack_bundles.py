#!/usr/bin/env python3
"""Add stored bundles to a site directory without displacing the ones already there.

Bundles reach a deploy two ways: as artifacts a run has just built, and as tarballs kept on a release for the
libraries too slow to build in CI and for guests nobody is rebuilding today. Both matter, and the rule between
them is simple: a bundle built in this run wins, a stored one fills a gap.

Getting that backwards cost both directions. The weekly deploy gathered artifacts only, so every scheduled
Mathlib rebuild quietly removed every guest library from the site, which undid the whole point of letting people
ask for one. And the publish workflow extracted tarballs on top of artifacts, so a stale stored bundle could
overwrite the one the run had just built.

    python3 tools/unpack_bundles.py tarballs site/data
"""
import pathlib
import sys
import tarfile


def main(src: pathlib.Path, dest: pathlib.Path) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    have = {p.name for p in dest.iterdir() if p.is_dir()}
    added, skipped = [], []
    for tar in sorted(src.glob("*.tar.gz")):
        slug = tar.name.removesuffix("-bundle.tar.gz").removesuffix(".tar.gz")
        if slug in have:
            skipped.append(slug)
            continue
        with tarfile.open(tar) as t:
            # A tarball names its own directory, and a member escaping the destination would be writing
            # somewhere nobody asked for. These are our own tarballs; the check costs nothing.
            for member in t.getmembers():
                target = (dest / member.name).resolve()
                if not str(target).startswith(str(dest.resolve())):
                    raise SystemExit(f"{tar.name}: member {member.name!r} escapes {dest}")
            t.extractall(dest)
        added.append(slug)
        have.add(slug)
    print(f"stored bundles: added {', '.join(added) or 'none'}; "
          f"kept the freshly built {', '.join(skipped) or 'none'}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        raise SystemExit(2)
    main(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]))
