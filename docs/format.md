# The bundle

The generator writes a directory of static files. The page reads them with `fetch` and nothing else: there is
no server, no database, and no API. This is the contract between the two halves, and
[`tests/check_bundle.py`](../tests/check_bundle.py) enforces it on every CI run.

## Ids

Every declaration has an id: a dense integer, assigned in module dependency order, with each module's
declarations occupying one contiguous range. Three consequences the page depends on:

- The shard holding an id is found by binary search over the module table. No index from id to file is stored.
- A declaration's id is lower than that of anything defined in a module importing it, so the id order is close
  to a topological order of the library.
- A name that appears in two modules keeps the id of the first, in dependency order, that defines it.

## Files

| file | content |
| --- | --- |
| `manifest.json` | one object: counts, the Lean version, the axiom table, where each library's source lives, and the check verdict |
| `modules.json` | one array, in dependency order, of `{n, s, c, i}`: name, first id, declaration count, indices of imported modules |
| `names.txt` | one name per line; the line number, counting from zero, is the id |
| `kinds.txt` | one character per id: `a` axiom, `d` def, `t` theorem, `o` opaque, `q` quot, `i` inductive, `c` constructor, `r` recursor, `?` unknown |
| `used.bin` | one little-endian `uint32` per id: how many declarations reference it |
| `check.json` | the report from `tenet check --report`, copied verbatim, when `--check` was given |
| `m/<Module>.json` | one array, in id order, of the module's declarations |

`names.txt`, `kinds.txt` and `used.bin` are what the search box and every list need about a declaration it is
not showing in full: its name, its kind, and how load-bearing it is. Together they are about 15 MB for Mathlib
and are fetched once.

## A declaration

```json
{
  "i": 5631,
  "n": "Nat.add_comm",
  "k": "theorem",
  "s": "∀ (n m : ℕ), n + m = m + n",
  "d": "Addition is commutative…",
  "l": [158, 163],
  "t": [1183, 1318, 1410, 1625, 1959],
  "u": [730, 5293],
  "b": [235187, 11325],
  "bc": 342,
  "a": [],
  "f": "the kernel's message"
}
```

| key | meaning |
| --- | --- |
| `i` | the id; equal to the module's `s` plus this entry's index, so it is redundant and checked |
| `n` | the fully qualified name |
| `k` | the kind, spelled out |
| `s` | the statement, printed for reading rather than for Lean; absent if the constant could not be decoded |
| `d` | the docstring, absent when there is none |
| `l` | first and last source line, absent when Lean recorded no range (recursors, `noConfusion`, and other generated constants have none) |
| `t` | ids referenced by the statement |
| `u` | ids referenced only by the proof or body; disjoint from `t` |
| `b` | dependents, the most depended-upon first, capped by `--in-edges` |
| `bc` | how many dependents there are in all, before the cap |
| `a` | indices into `manifest.axioms`: every axiom this declaration transitively rests on |
| `f` | present only when the checker rejected this declaration; the kernel's message |

Sizes for Mathlib and its dependencies: 791,453 declarations, 10,881 shards, 474 MB on disk, about 85 MB over
the wire with gzip. The largest single shard is under 3 MB.

## The manifest

```json
{
  "generated": "2026-09-18 17:41:02Z",
  "lean": "4.35.0-rc2",
  "modules": 10881,
  "declarations": 791453,
  "references": 20865515,
  "inEdgeCap": 200,
  "axioms": ["propext", "Classical.choice", "…"],
  "standardAxioms": ["propext", "Classical.choice", "Quot.sound"],
  "kinds": ["unknown", "axiom", "def", "…"],
  "libraries": [{"prefixes": ["Init", "Std", "Lean"], "name": "Lean", "url": "…", "rev": "…", "path": "src/"}],
  "repository": "https://github.com/keithadler/leanviz",
  "check": {"tenet": "0.9.0", "checked": 774948, "failed": 0, "report": "check.json", "sha256": "67adc7b8…"}
}
```

`libraries` is how a page turns a module name into a source link: the first entry whose `prefixes` contains the
module's first component wins, and an entry with no prefixes is the fallback for the project itself. `check` is
absent when the bundle was generated without `--check`, and the page then says so rather than implying a verdict.

## Compatibility

The format has no version number because nothing reads a bundle it did not generate: the page and the data are
deployed together. If that ever stops being true, add one to the manifest before changing a key.
