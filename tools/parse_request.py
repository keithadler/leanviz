#!/usr/bin/env python3
"""Work out which repository a request names, and refuse anything that is not one.

The input is an issue title someone typed, or a workflow input. It reaches a step that clones and builds, so it
is the boundary between what a stranger wrote and what this runner executes: the only thing that may come out of
here is `owner/name` made of characters GitHub allows in those, and a slug made of characters a path allows.
Anything else stops the run rather than being cleaned up and passed along.
"""
import os
import pathlib
import re
import sys

from libraries import HOME

REPO = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?/[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$")

# Bundles are uploaded by name and the upload clobbers, so a slug is a write target. "m", "data" and "assets"
# would collide with the bundle's own layout; HOME would let a request for, say, leanprover-community/mathlib
# upload itself over the real Mathlib. Eviction already refused to delete those, which is a different door.
RESERVED = {"m", "data", "assets"} | set(HOME)


def slugify(text: str) -> str:
    out = "".join(c if c.isalnum() else "-" for c in text.lower()).strip("-")
    while "--" in out:
        out = out.replace("--", "-")
    return out[:40]


def main() -> None:
    dispatch = os.environ.get("DISPATCH_REPO", "").strip()
    title = os.environ.get("TITLE", "").strip()
    if dispatch:
        repo = dispatch
        slug = slugify(os.environ.get("DISPATCH_SLUG", "") or repo.split("/")[-1])
    else:
        # "Add library: owner/name" is what the site's link produces; anything else is a person typing.
        after = title.split(":", 1)[1].strip() if ":" in title else ""
        repo = after.removeprefix("https://github.com/").removesuffix(".git").strip().strip("/")
        slug = slugify(repo.split("/")[-1] if "/" in repo else repo)

    if not REPO.match(repo):
        print(f"::error::'{repo}' is not an owner/name of a GitHub repository", file=sys.stderr)
        raise SystemExit(1)
    if not slug or slug in RESERVED:
        print(f"::error::'{slug}' is not a usable name for a bundle", file=sys.stderr)
        raise SystemExit(1)

    title_text = repo.split("/")[-1]
    out = pathlib.Path(os.environ["GITHUB_OUTPUT"]) if "GITHUB_OUTPUT" in os.environ else None
    lines = [f"repo={repo}", f"slug={slug}", f"title={title_text}"]
    if out is not None:
        with out.open("a") as f:
            f.write("\n".join(lines) + "\n")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
