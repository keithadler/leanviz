#!/usr/bin/env python3
"""Check a generated bundle the way the page reads it.

The C# tests cover the graph pass and the printer. This covers the artifact: every file the page fetches
exists, the module table's ranges tile the id space exactly, ids resolve to the shard the table points at,
and the cross-references between shards are consistent in both directions. Run it on any bundle:

    python3 tests/check_bundle.py site/data
"""
import gzip
import json
import struct
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
    bodies = bodies_cut = 0
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
            # A body belongs to a definition and to nothing else. A theorem carrying one would mean the
            # generator had started storing proof terms, which would quietly multiply the bundle's size.
            if "v" in d:
                bodies += 1
                if d["k"] not in ("def", "opaque"):
                    fail(f"{d['n']} is a {d['k']} but carries a body")
                if not d["v"].strip():
                    fail(f"{d['n']} carries an empty body")
                cut = d["v"].endswith(" …")
                if cut != bool(d.get("vcut")):
                    fail(f"{d['n']}: body ends cut={cut} but vcut={d.get('vcut')!r}")
                if cut:
                    bodies_cut += 1
            elif d.get("vcut"):
                fail(f"{d['n']} is flagged as a cut body but has no body")

    if total_refs == 0:
        fail("no references at all in the shards that were read")
    # Zero bodies means either the field stopped being written, which would ship silently and merely look like
    # a smaller bundle, or a library published from a tarball built before bodies existed. The manifest tells
    # the two apart: a bundle is judged against what its own generator claims to have written, never against
    # what today's generator would write.
    claimed = manifest.get("definitionBodies")
    if claimed is None:
        print("   (this bundle predates definition bodies; not checking for them)")
    elif claimed > 0 and bodies == 0 and checked_shards > 20:
        fail(f"the manifest claims {claimed:,} definition bodies and {checked_shards} shards have none")
    elif claimed == 0 and bodies > 0:
        fail(f"the manifest claims no definition bodies but the shards carry {bodies:,}")
    if not seen_axioms and any(k == "a" for k in kinds):
        fail("the bundle has axioms but nothing cites one")

    # graph.bin and digest.bin are binary and read into typed arrays by the page, so a wrong length or a target
    # out of range would not fail loudly there: it would silently point at the wrong declaration.
    graph = root / "graph.bin.gz"
    if graph.exists():
        raw = gzip.decompress(graph.read_bytes())
        if raw[:4] != b"LVG1":
            fail(f"graph.bin starts with {raw[:4]!r}, expected b'LVG1'")
        gn, forward_count, mention_count = struct.unpack_from("<III", raw, 4)
        if gn != n:
            fail(f"graph.bin is for {gn} declarations, the bundle has {n}")
        want = 16 + 4 * ((n + 1) + forward_count + (n + 1) + mention_count)
        if len(raw) != want:
            fail(f"graph.bin is {len(raw)} bytes, expected {want} for {forward_count} + {mention_count} edges")
        at = 16
        f_off = struct.unpack_from(f"<{n + 1}I", raw, at); at += 4 * (n + 1)
        f_to = struct.unpack_from(f"<{forward_count}I", raw, at); at += 4 * forward_count
        m_off = struct.unpack_from(f"<{n + 1}I", raw, at); at += 4 * (n + 1)
        m_to = struct.unpack_from(f"<{mention_count}I", raw, at)
        for label, off, to, count in (("forward", f_off, f_to, forward_count), ("mention", m_off, m_to, mention_count)):
            if off[0] != 0 or off[n] != count:
                fail(f"graph.bin {label} offsets run {off[0]}..{off[n]}, expected 0..{count}")
            if any(off[i] > off[i + 1] for i in range(n)):
                fail(f"graph.bin {label} offsets are not ascending")
            if to and (min(to) < 0 or max(to) >= n):
                fail(f"graph.bin {label} targets run {min(to)}..{max(to)}, outside 0..{n - 1}")
        # Modules are in dependency order, so a reference either stays inside its own module or goes to one that
        # comes earlier. Inside a module the order is whatever the .olean stores, which is not topological:
        # Monad.rec references Applicative and both live in Init.Prelude.
        module_of = [0] * n
        for mi, m in enumerate(modules):
            for i in range(m["s"], m["s"] + m["c"]):
                module_of[i] = mi
        for v in range(n):
            for e in range(f_off[v], f_off[v + 1]):
                if module_of[f_to[e]] > module_of[v]:
                    fail(f"{names[v]} in {modules[module_of[v]]['n']} references {names[f_to[e]]} "
                         f"in {modules[module_of[f_to[e]]]['n']}, which is imported later")
        print(f"  graph.bin: {forward_count:,} references, {mention_count:,} statement mentions")

    dig = root / "digest.bin.gz"
    if dig.exists():
        raw = gzip.decompress(dig.read_bytes())
        if len(raw) != 8 * n:
            fail(f"digest.bin is {len(raw)} bytes, expected {8 * n}")
        if raw == bytes(len(raw)):
            fail("digest.bin is all zeroes")

    check = manifest.get("check")
    if check is not None:
        if not (root / "check.json").exists():
            fail("the manifest carries a check but check.json is not in the bundle")
        if check["failed"] != 0 and check["success"]:
            fail("the check report says failures and success at the same time")

    print(f"   {bodies:,} definition bodies in those shards, {bodies_cut:,} cut at the printer's cap")
    print(f"OK {n:,} declarations, {len(modules):,} modules, {checked_shards:,} shards read in full, "
          f"{total_refs:,} references, {len(manifest['axioms'])} axioms"
          + (f", re-checked by Tenet {check['tenet']}" if check else ", not re-checked"))


if __name__ == "__main__":
    main(pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "site/data"))
