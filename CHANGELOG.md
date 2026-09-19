# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
versions follow [Semantic Versioning](https://semver.org/).

## [0.4.0]

Ten things the whole graph makes possible, which is the part of this project nothing else has.

### Added
- **The source, in the page.** The bundle knows the repository, the exact commit and the first and last line,
  and raw.githubusercontent serves with an open CORS header, so the lines a person wrote sit under the term the
  kernel checked instead of behind a link that takes you away.
- **What would fall if this were wrong**: the transitive set of declarations depending on one, computed by
  inverting the reference graph in the browser rather than shipping a reverse copy of it. `Nat.add_comm` carries
  399,960 declarations, 50.5% of the bundle.
- **Concluding the same thing**: other declarations ending in the same constant, and on a type, the declarations
  that produce it, which for a class is its instances. The generator stores each declaration's conclusion head;
  the reverse index would cost tens of megabytes, so the statement-mention index narrows the candidates instead.
- **`c:` search**: `c:Finset.sum` is everything whose conclusion is about `Finset.sum`, which is a different
  question from everything that mentions it and usually the one a person hunting a lemma means.
- **Namespace pages** at `#/ns/Nat`: the unit people think in, which is neither a module nor a directory.
- **A deprecated page**: everything marked `@[deprecated]` with its replacement, ordered by how much still
  depends on it, which is the order to fix them in.
- **Comparing two libraries**: what one has that the other does not, and where they disagree. Only possible
  because a bundle carries a digest per declaration and this site holds several libraries at once.
- **A command palette** on ctrl/cmd-K, reaching any page or any declaration.
- **Offline for what you have already seen.** A service worker caches the shell and every bundle file on the way
  past. The bundle is 147 MB so it is never cached whole, but the part you want again is the part you just had.
- **Light and dark as a choice.** The theme followed the operating system and could not be overridden.

### Fixed
- Comparing a bundle built before definition bodies with one built after marked every definition as changed:
  217,503 of them between two libraries that agree, because one digest covered the body and the other did not.
  The page detects that the two were built differently and says so rather than reporting a number about the
  generator as though it were about the mathematics.
- The generated-helper filter missed `.eq_1` and `.eq_def`, which are equation lemmas written with a dot rather
  than an underscore. They were the bulk of every pair of declarations sharing a statement.
- Field types printed under an empty binder stack, so a field read `Module.Projective #4 #3`.

## [0.3.0]

Ten features, each one a request a real Lean user wrote in a public issue rather than something that seemed
like a good idea here. The citation is in each line.

### Added
- **Structure and class fields**, with types, and the constructors of an inductive that is not a structure.
  Lean stores no field list: a structure is an inductive with one constructor and the fields are that
  constructor's telescope past the type's parameters. 5,972 structures in Mathlib. (doc-gen4#229, #184)
- **Modifiers**: `unsafe`, `partial`, `private` and `protected`, on the declaration and in module listings,
  each with a line saying why it matters. `noncomputable` is deliberately absent: it has an extension, the
  reader decodes no keys from it, and a mark that is silently never shown is worse than no mark at all.
  (doc-gen4#195, #180)
- **Search filters and words in any order**: `k:theorem`, `m:Mathlib.Order`, `lib:Mathlib`, combinable, and
  several bare words match a name containing all of them in any order. A query of only filters is a query, so
  `k:axiom` lists the axioms. (loogle#61, doc-gen4#320, loogle#46)
- **A copy menu**: the name, a `#check` line, the import, and a link to the page. (loogle#21)
- **Minimal imports** for a declaration: the fewest modules whose import closures cover every constant its
  statement mentions, which is `#min_imports` for one declaration. (import-graph#108, #109)
- **Module impact**: how many modules and declarations are downstream of a module, transitively, against the
  number that import it directly. `Mathlib.Order.Basic` is imported by 21 and reaches 7,770. (import-graph#59)
- **Treemap colouring by metric**: by area, by where the unfinished proofs are, or by what nothing depends on.
  (import-graph#53)
- **Why an axiom**: a button on every non-standard axiom that shows the chain carrying it, so
  `unsafeCast` explains itself as `unsafeCast → unsafeCast._proof_1 → lcProof`. (doc-gen4#270)
- **A command line query tool**, `tools/query.py`: find, show, uses, usedby, axioms, module and libraries,
  against the published site or a local bundle, as lines or as JSON. (loogle#48, doc-gen4#343)

### Fixed
- Field types printed their loose de Bruijn indices, so a field read `Module.Projective #4 #3`. The printer now
  carries the binder stack into the field, and it reads `Module.Projective R A`.

## [0.2.1]

### Added
- **Dated digest history.** Every deploy keeps a `<slug>-<date>.tar.gz` on a `history` release holding each
  library's `names.txt.gz` and `digest.bin.gz`: 10 MB against the bundle's 146 MB, and exactly what answers
  "what did this week do to the library". Until now the published bundle was overwritten on every deploy and the
  previous answer was gone the moment the new one went up. `tools/history.py` lists, fetches and diffs any two
  dates, and `keep` snapshots what a live site is serving without waiting for a deploy.

### Fixed
- The weekly deploy removed every guest library from the site. It gathered the bundles its own jobs built and
  nothing else, while libraries built from a request live on the release, so rebuilding Mathlib silently took
  `batteries` and `lean4-cli` down with it and undid the point of letting anyone ask for one.
- The publish workflow extracted release tarballs on top of run artifacts, so a stored bundle could overwrite
  the one the run had just built. Both now follow one rule, in `tools/unpack_bundles.py`: a bundle built in this
  run wins, a stored one fills a gap.
- The deploy left the stored tarballs untouched, so after publishing new bundles the release still held the
  previous ones. Since a requested library ends by publishing from the release, the next request would have
  rebuilt the site from six-hour-old bundles and undone the deploy. Every deploy now refreshes its own tarball.
- `tools/history.py` used `str | None` in a signature, which is a TypeError on the Python 3.9 macOS ships.

## [0.2.0]

### Added
- **Definitions show how they are defined.** A page showed a type signature and called it the definition; the
  body was in the `.olean` the whole time and nothing ever asked the printer for it. 223,300 definitions in
  Mathlib now carry theirs, 56 MB, averaging 261 characters. What is shown is the elaborated term the kernel
  stores, not the source text, and the page says so and links to the source for the other half. Theorems do not
  carry one: a proof term averages 22,000 characters, four hundred of them run past 100,000 and some do not
  finish inside 400,000, so they would cost more than everything else in the bundle and nobody would read one.
- **An axioms page**, at `#/axioms`: every axiom the library rests on, how much rests on each, and for the ones
  beyond Lean's three, which declarations. The question "what does this library assume" could previously only be
  answered by opening every page in it. Mathlib's answer is 583 declarations out of 792,459, nearly all through
  compiler and build-tool internals.
- **The import line** on every declaration and module page, with a copy button.
- **"Only this project"**, a search switch for a formalization sitting on top of Mathlib.
- **A README badge** per library, written into the bundle, saying what the independent kernel found.
- **"Show me something"**, which lands a newcomer on a real theorem with a docstring.
- **A Map button in the header**, with a treemap for an icon. The map existed and nothing pointed at it.

### Changed
- The header is two rows. One row had a brand, a search field, five library pills, a long checkbox label and a
  status line.
- The treemap pools parts too small to draw into one box and lists them underneath, so a namespace with 178
  immediate parts is 144 boxes you can click rather than a row of slivers a pixel wide.
- Treemap boxes have a tooltip: declarations, share of the parent, how many parts and modules, and whether the
  box is a file rather than an area.

### Fixed
- Drilling into a leaf on the map drew an empty treemap saying "0 parts". It opens the module.
- A part declaring nothing had no area, and the treemap dropped anything with none, so `Mathlib.Tactic.ToAdditive`
  and four others like it were on no page at all.
- Searching with "only this project" could not find the project. The scan stops at 400 substring hits and ids run
  in dependency order, so it filled up inside Mathlib and never reached the project's own declarations; the scope
  test now happens inside the scan rather than on its output.
- A request for any repository whose name ends in `mathlib`, `flt` or `nse` would have uploaded itself over that
  library's bundle, because the upload clobbers by name. Eviction protected those from deletion, which is a
  different door. The list of protected libraries now lives in one file instead of three.
- Regenerating one library reordered `projects.json` alphabetically, so rebuilding Mathlib on a site that also
  carried FLT made FLT the page everyone landed on.
- A declaration page gave two different counts of what it uses, neither labeled, and reported helpers hidden from
  one list as though they had been hidden from another.
- A body cut at the printer's cap was shown as though it were whole.
- The per-declaration digest hashed only the statement, so a definition could be rewritten and every diff would
  call the bundle unchanged.
- The build ticker said "Nothing has been built this way yet" whenever the GitHub API refused it, which is a
  claim about the past decided by the weather. Rate limiting, an unreachable API, an empty history and a finished
  run are now four different sentences.
- The home page offered Mathlib's landmarks on every library, so on a small project most were dead links.
- README and docs said 791,453 declarations. That is the count of distinct names; there are 792,459 declarations.

## [Unreleased]

### Added
- Anyone can ask for a library. A form opens a prefilled GitHub issue, a workflow builds the project, re-checks
  it and publishes it, and the issue gets the link. The site holds about seven, so a new guest may evict the
  least recently added one. Waiting is accompanied by a mouth eating module names, after WinDirStat.

### Fixed
- A name declared by two modules lost one of them, and every reference to it resolved to whichever came first.
  In OpenAI's Navier-Stokes repository that meant the page for `Euler.euler_breakdown_R3` showed a challenge
  stub whose proof is `sorry`, rather than the real proof, and reported the theorem as resting on a hole. 1,081
  names in that bundle are declared more than once. Every declaration now gets its own id and a reference
  resolves to the occurrence its module can see.

### Added
- Fermat's Last Theorem as a third library. An unfinished formalization is what the `sorry` page was built for,
  and adding one is now a row in a matrix rather than a copy of a job.

### Changed
- The second library builds in its own job, in parallel, and the site assembles whatever bundles arrive. It pins
  its own Mathlib and its own toolchain and takes over an hour to compile, which as a step inside the Mathlib job
  meant either a cap that cut it off or a wait that held Mathlib's page hostage. Now neither.

### Added
- Statements are coloured: binders, arrows, relations and big operators, in one pass that leaves linked names
  alone.
- Docstring maths renders. Mathlib writes Lean notation in backticks rather than LaTeX, so of 112,670
  docstrings only 1,178 carry inline `$…$`, and what they use is a short list of symbols plus sub- and
  superscripts. That subset renders in about eighty lines rather than by pulling in a TeX engine.
- A page fetching a shard says "loading" instead of showing the last page or nothing.
- The namespace in a declaration's title links to that area of the map.
- The page check exercises the graph-backed answers too: the transitive weight, a chain between two
  declarations, and a statement search. Those are the answers that look confident when they are wrong, and CI
  could not see them before.

## [0.1.1] - 2026-09-18

### Fixed
- The shortest-chain search could report no chain where one exists. It pruned on the id order, which holds
  between modules but not inside one: `Monad.rec` references `Applicative` and both live in `Init.Prelude`. The
  walk no longer prunes, which costs nothing measurable and cannot be wrong.

### Added
- `check_bundle.py` validates `graph.bin` and `digest.bin`: the header, the lengths, ascending offsets, targets
  in range, and that a reference never crosses into a module imported later. That last check is what found the
  bug above.

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
