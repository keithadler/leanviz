# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] - 2026-09-18

First release: a generator, a page, and a hosted bundle of Mathlib, under the MIT License.

### Added
- **The generator.** Reads a built Lake project's `.olean` files with Tenet's reader, never the kernel, and
  writes a static bundle: a shard per module with each declaration's statement, docstring, source lines,
  references split into statement and proof, its most depended-upon dependents, and the axioms it transitively
  rests on; plus a name list, a kind table, an in-degree table, a module table and a manifest. Mathlib and its
  dependencies, 791,453 declarations in 10,881 modules, take about two minutes.
- **A statement printer** that hides universes and implicit and instance arguments, groups binders, uses
  generalized field notation, and knows Lean's and Mathlib's notation for the common operators, big operators,
  bounded quantifiers, linear and algebra map arrows, category composition, coercions, structure instances and
  the number types.
- **The page.** Search over names and modules, a module tree, a most depended-upon list, a declaration page with
  statement, docstring, source link, axiom verdict, uses split by statement and proof, dependents, and a
  neighborhood picture that walks one or two steps in either direction. No build step.
- **A welcome by role**: separate introductions for a newcomer, a Lean user, someone verifying a proof, and
  someone running their own project, plus a plain-words explainer on every declaration page.
- **The verdict.** `--check` takes a report from `tenet check --report` and stamps the bundle: the manifest
  carries the run, rejected declarations carry the kernel's message, and the page says whether it was re-checked.
  The report ships inside the bundle under its SHA-256, and the Pages workflow attests it with GitHub's artifact
  attestation, so a published verdict is verifiable with `gh attestation verify`.
- **Hosting.** A workflow that fetches Mathlib and its cache, builds the reader from Tenet's source, re-checks,
  generates, attests, runs the page against the fresh bundle and deploys to GitHub Pages, weekly and on demand.
  Live at <https://keithadler.github.io/leanviz/>.
- **More than one library on a site.** Each bundle lives under its own slug with a `projects.json` beside them,
  the page takes `?p=<slug>` and offers a switch, and the deploy adds OpenAI's Navier-Stokes and Euler
  formalization, complete and re-checked, as a second library. Its steps may fail without holding up Mathlib's.
- **Compressed bundles.** Everything large is stored gzipped and decompressed by the page, which takes Mathlib
  from 474 MB to 82 MB and is what makes room for a second library inside the size a static host allows.
- **What changed between two bundles.** Each carries a digest per declaration, a hash of its name and statement,
  and `tools/diff_bundles.py` compares two of them, locally or over http, into added, removed and restated.
- **The whole graph, on request.** `graph.bin` carries every reference and the reverse of the statement
  references, fetched only when asked. It answers three things one shard cannot: how much a declaration rests on
  in total (`Real.pi_gt_three` rests on 16,358 constants), the shortest chain from one declaration to another
  (three steps from that theorem to `Classical.choice`), and which statements mention a set of constants
  (`+Finset.sum +Nat.Prime`).
- **More ways in.** A treemap of the library that descends area by area, a list of declarations nothing uses, a
  page of everything resting on `sorry`, a cite button, and keyboard navigation.
- **Deprecation.** A deprecated declaration says so at the top of its page, with the replacement to use and the
  date, and is struck through wherever it is listed: 5,423 of them in Mathlib. Tenet gained `DeprecationOf` and
  `KeysInExtension` for this.
- **Tests.** The graph pass, the printer and the command line in xunit, the bundle format in
  `tests/check_bundle.py`, the page itself in a headless browser in `tests/check_page.py`, and CI that builds
  with warnings as errors, generates a real bundle from Lean's core library, then verifies and runs it.

[Unreleased]: https://github.com/keithadler/leanviz/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/keithadler/leanviz/releases/tag/v0.1.0
