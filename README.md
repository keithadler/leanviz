# LeanViz

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

and open `http://localhost:8787`. The bundle for Mathlib and its dependencies is about 525 MB on disk
and 85 MB over the wire with gzip, in 10,886 files, so a host has to be happy with that many files;
GitHub Pages and Cloudflare Pages both are.

Options: `--jobs N` for parallelism, `--in-edges N` for how many dependents a shard keeps per declaration
(the most used ones, default 200; the total count is always kept), and `--check report.json` to stamp the
bundle with a Tenet verdict.

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
dotnet test tests/LeanNavigator.Tests
```

covers the graph pass (reverse edges, the axiom closure across a mutual cycle, the most-used selection,
bitsets past 64 axioms) and the printer against real statements from an installed Lean toolchain. With
`LEAN_NAVIGATOR_MATHLIB` pointing at a built Mathlib checkout, seven more cases pin the printer against
statements compared by eye with the Mathlib docs, which are Lean's own rendering.

## The bundle

| file | what |
| --- | --- |
| `manifest.json` | Lean version, counts, the axiom table, and which repository and revision each library's source is at |
| `modules.json` | every module: name, first declaration id, count, imports |
| `names.txt` | one declaration name per line; the line number is the id |
| `kinds.txt` | one character per id: axiom, def, theorem, opaque, quot, inductive, constructor, recursor |
| `used.bin` | one little-endian `uint32` per id: how many declarations reference it |
| `m/<Module>.json` | the module's declarations in id order: statement, docstring, source lines, references from the statement (`t`) and from the proof or body (`u`), dependents (`b`, most used first) and their count (`bc`), axioms (`a`) |

Ids are dense integers in module dependency order, one contiguous range per module, so the shard for
an id is a binary search on the module table.

Statements are printed by the generator's own printer, which hides universe levels and implicit and
instance arguments, groups binders, uses generalized field notation (`n.succ`, `p.degree`), and knows
Lean's and Mathlib's notation for the common operators, big operators, coercions and number types. It is
not Lean's delaborator: what it does not know it prints as plain application.

## Status

Early. What works: the generator over Mathlib master (Lean 4.35.0-rc2) with the Tenet verdict, and the
three pages: home with search, a most-depended-upon list and a module tree; a declaration page; a module
page. Not yet: a first run of the hosting workflow, instances, deprecation marks, transitive dependency
counts, a picture deeper than one step.

Until Tenet 0.9.1 is on nuget.org with the docstring and source-range readers, `generator/` references
a local copy of the built Tenet assemblies in `lib/`, which is not committed. To build it today, build
Tenet from source and copy `Tenet.Kernel.dll` and `Tenet.Olean.dll` into `lib/`; the workflow does
exactly that with `tenet/` checked out next to this repository.

## License

Dual-licensed under MIT or Apache 2.0, at your option, like Tenet.
