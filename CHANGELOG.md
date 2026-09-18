# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] - 2026-09-18

First release: a generator, a page, and a hosted bundle of Mathlib.

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
- **Tests.** The graph pass, the printer and the command line in xunit, the bundle format in
  `tests/check_bundle.py`, the page itself in a headless browser in `tests/check_page.py`, and CI that builds
  with warnings as errors, generates a real bundle from Lean's core library, then verifies and runs it.

[Unreleased]: https://github.com/keithadler/leanviz/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/keithadler/leanviz/releases/tag/v0.1.0
