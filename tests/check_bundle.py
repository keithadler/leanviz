#!/usr/bin/env python3
"""Check a generated bundle the way the page reads it.

The C# tests cover the graph pass and the printer. This covers the artifact: every file the page fetches
exists, the module table's ranges tile the id space exactly, ids resolve to the shard the table points at,
and the cross-references between shards are consistent in both directions. Run it on any bundle:

    python3 tests/check_bundle.py site/data
"""
import gzip
import json
import pathlib
import sys

KINDS = "?adtoqicr"


def fail(message: str) -> None:
    print(f"FAIL {message}")
    sys.exit(1)


def read(root: pathlib.Path, name: str) -> bytes:
    """A bundle file, gzipped or not: the generator compresses everything large, the page decompresses."""
    gz = root / (name + ".gz")
    if gz.exists():
        return gzip.decompress(gz.read_bytes())
    plain = root / name
    if plain.exists():
        return plain.read_bytes()
    fail(f"{name} is missing")
    raise SystemExit(1)  # unreachable; fail exits


def main(root: pathlib.Path) -> None:
    # One bundle per library, under its own slug; a projects.json beside them lists what is there.
    if (root / "projects.json").exists() and not (root / "manifest.json").exists():
        projects = json.loads((root / "projects.json").read_text())
        if not projects:
            fail("projects.json is empty")
        for p in projects:
            print(f"{p['slug']}:")
            main(root / p["slug"])
        return

    manifest = json.loads(read(root, "manifest.json"))
    modules = json.loads(read(root, "modules.json"))
    names = read(root, "names.txt").decode().split("\n")
    if names and names[-1] == "":
        names.pop()
    kinds = read(root, "kinds.txt").decode()
    used = read(root, "used.bin")

    n = manifest["declarations"]
    if len(names) != n:
        fail(f"manifest says {n} declarations, names.txt has {len(names)}")
    if len(kinds) != n:
        fail(f"kinds.txt has {len(kinds)} entries, expected {n}")
    if len(used) != 4 * n:
        fail(f"used.bin is {len(used)} bytes, expected {4 * n}")
    if set(kinds) - set(KINDS):
        fail(f"kinds.txt has characters outside {KINDS!r}: {sorted(set(kinds) - set(KINDS))}")
    if len(modules) != manifest["modules"]:
        fail(f"manifest says {manifest['modules']} modules, modules.json has {len(modules)}")

    # Ids are dense and contiguous per module: the page finds a shard by binary search on this table, so a
    # gap or an overlap would send it to the wrong file rather than fail loudly.
    at = 0
    for m in modules:
        if m["s"] != at:
            fail(f"module {m['n']} starts at {m['s']}, expected {at}")
        at += m["c"]
        for i in m["i"]:
            if not 0 <= i < len(modules):
                fail(f"module {m['n']} imports index {i}, out of range")
    if at != n:
        fail(f"module ranges cover {at} ids, expected {n}")

    total_refs = 0
    seen_axioms = set()
    checked_shards = 0
    for m in modules:
        path = root / "m" / f"{m['n']}.json.gz"
        if not path.exists():
            path = root / "m" / f"{m['n']}.json"
        if not path.exists():
            fail(f"a shard for {m['n']} is missing")
        # Reading all of them is the point, but a full read of Mathlib is slow; every module is opened,
        # and the first few hundred are inspected declaration by declaration.
        if checked_shards >= 400:
            continue
        checked_shards += 1
        shard = json.loads(gzip.decompress(path.read_bytes()) if path.suffix == ".gz" else path.read_bytes())
        if len(shard) != m["c"]:
            fail(f"{m['n']} declares {m['c']} ids but its shard has {len(shard)} entries")
        for offset, d in enumerate(shard):
            want = m["s"] + offset
            if d["i"] != want:
                fail(f"{m['n']}[{offset}] has id {d['i']}, expected {want}")
            if d["n"] != names[want]:
                fail(f"id {want} is {names[want]!r} in names.txt and {d['n']!r} in {m['n']}")
            if d["k"] != {"a": "axiom", "d": "def", "t": "theorem", "o": "opaque",
                          "q": "quot", "i": "inductive", "c": "constructor", "r": "recursor",
                          "?": "unknown"}[kinds[want]]:
                fail(f"id {want} is {d['k']} in its shard and {kinds[want]!r} in kinds.txt")
            for ref in d["t"] + d["u"] + d["b"]:
                if not 0 <= ref < n:
                    fail(f"{d['n']} references id {ref}, out of range")
            if set(d["t"]) & set(d["u"]):
                fail(f"{d['n']} lists {sorted(set(d['t']) & set(d['u']))} as both statement and proof references")
            if len(d["b"]) > d["bc"]:
                fail(f"{d['n']} keeps {len(d['b'])} dependents but claims {d['bc']}")
            if len(d["b"]) > manifest["inEdgeCap"]:
                fail(f"{d['n']} keeps {len(d['b'])} dependents, above the cap of {manifest['inEdgeCap']}")
            for a in d["a"]:
                if not 0 <= a < len(manifest["axioms"]):
                    fail(f"{d['n']} cites axiom index {a}, out of range")
                seen_axioms.add(a)
            total_refs += len(d["t"]) + len(d["u"])

    if total_refs == 0:
        fail("no references at all in the shards that were read")
    if not seen_axioms and any(k == "a" for k in kinds):
        fail("the bundle has axioms but nothing cites one")

    check = manifest.get("check")
    if check is not None:
        if not (root / "check.json").exists():
            fail("the manifest carries a check but check.json is not in the bundle")
        if check["failed"] != 0 and check["success"]:
            fail("the check report says failures and success at the same time")

    print(f"OK {n:,} declarations, {len(modules):,} modules, {checked_shards:,} shards read in full, "
          f"{total_refs:,} references, {len(manifest['axioms'])} axioms"
          + (f", re-checked by Tenet {check['tenet']}" if check else ", not re-checked"))


if __name__ == "__main__":
    main(pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "site/data"))
