# The bundle

The generator writes a directory of static files. The page reads them with `fetch` and nothing else: there is
no server, no database, and no API. This is the contract between the two halves, and
[`tests/check_bundle.py`](../tests/check_bundle.py) enforces it on every CI run.

## Ids

Every declaration has an id: a dense integer, assigned in module dependency order, with each module's
declarations occupying one contiguous range. Three consequences the page depends on:

- The shard holding an id is found by binary search over the module table. No index from id to file is stored.
- A declaration's id is lower than that of anything defined in a module importing it. Inside one module the
  order is whatever the `.olean` stores, which is not topological: `Monad.rec` references `Applicative` and both
  live in `Init.Prelude`. Code that walks the graph must not assume otherwise, and `check_bundle.py` enforces
  the module-level version of this rather than the declaration-level one.
- A name two modules both declare gets an id for each. That happens: OpenAI's Navier-Stokes repository has an
  `Euler.euler_breakdown_R3` that is a challenge stub containing `sorry` and another that is the real proof, in
  modules never imported together, and 1,081 names in that bundle are declared more than once. A reference
  resolves to the occurrence its own module can see, which is well defined rather than a guess, because Lean
  refuses an environment where two modules declaring the same name are imported together.

## Several libraries

A site can carry more than one. Each bundle lives in its own directory under the output, named by its slug, and
`projects.json` beside them lists what is there. The page reads that file, picks a bundle from `?p=<slug>` or
takes the first, and offers a switch. A site with one bundle and no `projects.json` still works.

## Compression

Everything large is stored gzipped, with `.gz` on the name. A static host stores what it is given and GitHub
Pages allows a gigabyte for the whole site, so Mathlib at 474 MB of JSON would leave no room for a second
library; gzipped it is 82 MB. The page fetches the `.gz` and decompresses with `DecompressionStream`. The
manifest and `check.json` stay uncompressed, because they are small and worth being able to fetch with `curl`.

## Files

| file | content |
| --- | --- |
| `../projects.json` | the libraries this site carries: slug, title, counts, and whether each was re-checked |
| `manifest.json` | one object: counts, the Lean version, the axiom table, where each library's source lives, the hole list and the check verdict |
| `modules.json.gz` | one array, in dependency order, of `{n, s, c, i}`: name, first id, declaration count, indices of imported modules |
| `names.txt.gz` | one name per line; the line number, counting from zero, is the id |
| `kinds.txt.gz` | one character per id: `a` axiom, `d` def, `t` theorem, `o` opaque, `q` quot, `i` inductive, `c` constructor, `r` recursor, `?` unknown |
| `used.bin.gz` | one little-endian `uint32` per id: how many declarations reference it |
| `check.json` | the report from `tenet check --report`, copied verbatim, when `--check` was given |
| `m/<Module>.json.gz` | one array, in id order, of the module's declarations |
| `graph.bin.gz` | the whole reference graph, for the questions one shard cannot answer |
| `digest.bin.gz` | one little-endian `uint64` per id: a hash of the name and statement, for comparing two bundles |

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
| `x` | present only when `@[deprecated]`: `to` the replacement, `why` the note, `since` the version |
| `x` | present only when `@[deprecated]`: `to` the replacement, `why` the note, `since` the version |
| `f` | present only when the checker rejected this declaration; the kernel's message |

Sizes for Mathlib and its dependencies: 791,453 declarations, 10,881 shards, 114 MB on disk including the
32 MB graph, and 82 MB for a visitor who never asks a question that needs the graph. The largest shard is under
300 KB.

## graph.bin

The largest file in a bundle, 32 MB for Mathlib, fetched only when a question needs it: how much a declaration
rests on in total, the chain between two declarations, and which statements mention a given constant.
Little-endian `uint32` throughout, so a browser reads it into typed arrays without parsing.

```
"LVG1"                            4 bytes
n, forwardCount, mentionCount     3 x uint32
forwardOffsets[n + 1]             CSR offsets into forwardTargets
forwardTargets[forwardCount]      what each declaration references
mentionOffsets[n + 1]             CSR offsets into mentionTargets
mentionTargets[mentionCount]      the declarations whose statement mentions each id
```

The forward half is the same edges the shards carry, gathered in one place. The mention half is the reverse of
the statement references only, which is what makes `+Finset.sum +Nat.Prime` an intersection of two short lists.

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
