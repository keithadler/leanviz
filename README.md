# LeanViz

### [keithadler.github.io/leanviz](https://keithadler.github.io/leanviz/)

[![ci](https://github.com/keithadler/leanviz/actions/workflows/ci.yml/badge.svg)](https://github.com/keithadler/leanviz/actions/workflows/ci.yml)
[![pages](https://github.com/keithadler/leanviz/actions/workflows/pages.yml/badge.svg)](https://github.com/keithadler/leanviz/actions/workflows/pages.yml)
[![MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**A visual navigator for Lean 4 declarations, starting with Mathlib.** Every declaration gets a page: its
statement, its docstring, a link to the source line, what it uses, what uses it, the axioms it rests on,
and a picture of its neighborhood you can walk one step at a time.

The home page introduces itself by role, so the same site serves someone who has never heard of Lean and
someone who writes it every day: **I'm new here** explains what Lean and Mathlib are and walks through one
page; **I use Lean** says what is here that the docs and `#check` do not give; **I want to verify a proof**
covers the independent re-check, the report and its attestation; **I run a Lean project** is the recipe for
pointing it at your own. Declaration pages carry a "What am I looking at?" panel, open by default for the
newcomer.

It reads the compiled `.olean` files directly with [Tenet](https://github.com/keithadler/tenet), an
independent Lean 4 kernel on .NET. No Lean toolchain runs, no kernel check runs: extracting which
constants a declaration references needs only the reader, so all of Mathlib and its dependencies,
791,453 declarations across 10,881 modules, become a browsable bundle in about two minutes on a laptop.

## What it is

Two parts, in two directories:

- `generator/`: a .NET program. Point it at a built Lake project and it writes a static bundle: one JSON
  shard per module, a name list, a module table, a manifest.
- `site/`: a static page, plain HTML and JavaScript with no build step, that reads the bundle. Host it on
  any static file server.

Visitors of a hosted copy need nothing installed.

## Running it yourself

You need the .NET 10 SDK and a Lake project that has been built (for Mathlib, a checkout after
`lake exe cache get`; that step needs `elan`, the navigator itself does not).

```bash
dotnet build generator -c Release
dotnet generator/bin/Release/net10.0/leanviz.dll /path/to/mathlib4 --out site/data
```

Then serve the `site/` directory with any static server, for example:

```bash
python3 -m http.server 8787 --directory site
```

and open `http://localhost:8787`. The bundle for Mathlib and its dependencies is 82 MB in 10,887 files,
stored gzipped and decompressed by the page, so a host has to be happy with that many files; GitHub Pages
and Cloudflare Pages both are. Generate more than one library into the same directory, each with its own
`--slug`, and the page offers a switch between them.

Options: `--jobs N` for parallelism, `--in-edges N` for how many dependents a shard keeps per declaration
(the most used ones, default 200; the total count is always kept), and `--check report.json` to stamp the
bundle with a Tenet verdict.

## Ways in

Besides searching a name and walking the picture:

- **[The map](https://keithadler.github.io/leanviz/#/map)**: the library as areas, each box sized by how many
  declarations are under it, clicking to descend from Mathlib to `Mathlib.Analysis.Calculus` and into a module.
- **[What nothing uses](https://keithadler.github.io/leanviz/#/unused)**: declarations no other declaration
  references, filtered by name. A top-level theorem belongs there; a lemma usually does not.
- **[Unfinished proofs](https://keithadler.github.io/leanviz/#/holes)**: everything resting on `sorry`, grouped
  by module, with how much stands on each hole. Empty for Mathlib, which is the point of showing it.
- **Search by what a statement mentions**: `+Finset.sum +Nat.Prime` finds the lemmas whose statement mentions
  both. The first such query loads the graph, 32 MB for Mathlib.
- **On a declaration page**: count everything it transitively rests on, and find the shortest chain from it to
  any other declaration, an axiom included.
- **Keys**: `/` searches, `j` and `k` walk a list, Enter opens, `u` and `b` step to what this uses or what uses
  it, `g` goes home, `?` lists them.

## What changed

Every bundle carries a hash of each declaration's name and statement, so two of them can be compared without
either one's shards and without Lean:

```bash
python3 tools/diff_bundles.py https://keithadler.github.io/leanviz/data/mathlib site/data/mathlib
```

prints what was added, removed and restated. Across two Lean toolchains it reports 5,781 added, 3,493 removed
and 1,779 restated, which is a question nothing else answers today.

## The verdict

The axiom lists say what each proof cites. Whether the proofs hold is a separate question, and Tenet answers
it: `tenet check <project> --all --report check.json` re-derives every declaration through an independent
kernel, about six minutes for all of Mathlib. Pass that report to the generator and the site says so on every
page: "every one of N declarations re-checked by Tenet, none rejected" on the home page, a check mark next to
each verdict, and, for any declaration the kernel rejected, a red card with the kernel's message. A bundle
generated without a report says plainly that it was not re-checked.

## The certificate

A verdict only means something if you can tell who ran what over which bytes. The check report already
names its inputs by SHA-256. The workflow goes one step further and attests the report and the manifest
with GitHub's artifact attestation: a signature bound to the workflow, the commit and the run, recorded in
a public transparency log with a timestamp. The report is published with the bundle as `data/check.json`,
its hash is in the manifest, and the home page cites it. To confirm a published verdict:

```bash
gh attestation verify check.json --owner <owner>
```

That proves the run happened as described. It does not make the verdict true: a signed report from a
buggy kernel is a signed mistake. The claim that holds up is reproducibility, the same files through the
same Tenet giving the same answer, and Lean's own kernel agreeing. The attestation fixes the first half so
that anyone can attempt the second.

## Hosting

`.github/workflows/pages.yml` regenerates the bundle and publishes `site/` to GitHub Pages, weekly and on
demand: it fetches Mathlib and its cache, builds the reader from Tenet's source, re-checks with Tenet,
generates, attests, deploys.

## Tests

```bash
dotnet test tests/LeanViz.Tests
```

covers the graph pass (reverse edges, the axiom closure across a mutual cycle, the most-used selection,
bitsets past 64 axioms) and the printer against real statements from an installed Lean toolchain. With
`LEANVIZ_MATHLIB` pointing at a built Mathlib checkout, seven more cases pin the printer against
statements compared by eye with the Mathlib docs, which are Lean's own rendering.

```bash
python3 tests/check_bundle.py site/data
```

checks a generated bundle the way the page reads it: every file present, the module table tiling the id space
exactly, ids resolving to the shard the table points at, references in range, dependents within their cap.

```bash
python3 tests/serve.py 8787 site &
python3 tests/check_page.py http://localhost:8787
```

loads the site in a headless Chrome and fails on anything it throws or logs, because parsing `app.js` is not the
same as running it. CI runs all three, over a bundle it generates from Lean's core library.

## The bundle

The generator writes a directory of static files and the page reads them with `fetch`; there is no server and no
API between them. [docs/format.md](docs/format.md) specifies it in full and `tests/check_bundle.py` enforces it.
In short:

| file | what |
| --- | --- |
| `manifest.json` | Lean version, counts, the axiom table, where each library's source lives, the check verdict |
| `modules.json` | every module: name, first declaration id, count, imports |
| `names.txt`, `kinds.txt`, `used.bin` | one name, one kind character and one reference count per id |
| `m/<Module>.json` | the module's declarations: statement, docstring, source lines, references out and in, axioms |

Ids are dense integers in module dependency order, one contiguous range per module, so the shard for an id is a
binary search on the module table.

Statements are printed by the generator's own printer, which hides universe levels and implicit and instance
arguments, groups binders, uses generalized field notation (`n.succ`, `p.degree`), and knows Lean's and Mathlib's
notation for the common operators, big operators, coercions and number types. It is not Lean's delaborator: what
it does not know it prints as plain application. [docs/design.md](docs/design.md) says why it is not.

## Status

0.1.0, published and running over Mathlib master with the Tenet verdict.

Known gaps: `@[pp_nodot]` is not honored, so a few names read as `p.Prime` where the Mathlib docs write
`Nat.Prime p`; instance and `simp` marks are not shown, because those extensions are not name-keyed and the
reader reports nothing rather than guessing; universe names come out as the compiled file spells them, `u_1`
where the docs show `u₁`; and the bundle keeps the 200 most depended-upon dependents per declaration rather
than all of them.

`generator/` references the Tenet assemblies from a local `lib/`, which is not committed, because the reader
calls it needs are newer than the published package. CI and the deploy clone Tenet and build it, which takes
about a minute; [CONTRIBUTING.md](CONTRIBUTING.md) has the same three commands for a local build.

## Documentation

- [docs/format.md](docs/format.md): the bundle, file by file and key by key.
- [docs/design.md](docs/design.md): why a static site, why ids, why the printer is not Lean's, why the reverse
  edges are capped, why the axiom closure needs a fixpoint.
- [CONTRIBUTING.md](CONTRIBUTING.md): how to build without a published Tenet, the two kinds of test, and how to
  improve the printer without guessing.
- [CHANGELOG.md](CHANGELOG.md): what changed, newest first.

## License

[MIT](LICENSE). Use it, fork it, ship it.
