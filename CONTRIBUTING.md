# Contributing

## Getting a build

You need the .NET 10 SDK. The generator references `Tenet.Kernel` and `Tenet.Olean` as assemblies in `lib/`,
which is not committed, because the version on nuget.org does not yet expose the docstring and source-range
readers. Until 0.9.1 ships, build them from source:

```bash
git clone --depth 1 https://github.com/keithadler/tenet.git
dotnet build tenet/src/Tenet.Olean -c Release -f net10.0
mkdir -p lib && cp tenet/src/Tenet.Olean/bin/Release/net10.0/Tenet.{Kernel,Olean}.dll lib/
dotnet build generator -c Release
dotnet test tests/LeanViz.Tests
```

CI does exactly this, so if it works there it works here.

## Two kinds of test

- `tests/LeanViz.Tests` covers the pieces: the graph pass with hand-written graphs, and the printer against real
  statements. Printer cases from Lean's core library run whenever an elan toolchain is installed; the Mathlib
  cases need `LEANVIZ_MATHLIB` pointing at a built checkout.
- `tests/check_bundle.py` covers the artifact: it reads a generated bundle the way the page does and checks every
  invariant the page relies on. Run it on any bundle you generate.
- `tests/check_page.py` covers the page: it loads the site in a headless Chrome, visits every kind of page,
  exercises the graph-backed answers (the weight, a chain, a statement search) and fails on anything thrown or
  logged. Serve a bundle with `python3 tests/serve.py 8787 site` first. It takes a base URL, so it also works
  against a published site.

A change to the printer wants a case in the first. A change to the bundle format wants a check in the second and
a line in [docs/format.md](docs/format.md). A change to the page wants nothing new, but run the third: syntax
checks do not catch a statement that throws at load.

## The printer

The way to improve it is not to guess. Sample random statements from a generated bundle, compare them with the
same declarations in the [Mathlib docs](https://leanprover-community.github.io/mathlib4_docs/), which are Lean's
own rendering, fix what differs, and pin the fixed case in `MathlibPrettyTests`. Every notation currently
supported arrived that way.

## The page

`site/` has no build step on purpose: three files, no dependencies, no bundler. It must keep working as files
served by any static host, so no imports from a CDN and no framework. CI parses `app.js` and checks that every
path it fetches is one the generator writes.

## License

Contributions are accepted under the [MIT License](LICENSE), the same terms the project ships under.

## Style

American English, no em dashes, and comments that say why rather than what. A comment that repeats the code is
worse than none; a comment explaining a decision that looks wrong until you know the constraint is worth three
lines.
