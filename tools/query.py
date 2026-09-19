#!/usr/bin/env python3
"""Ask a published bundle a question from the command line, without a browser and without Lean.

loogle has an open request for command line search and doc-gen4 has one for a hook external tools can use. A
LeanViz bundle is already a static directory of files that answers both: this reads it over http or from disk
and prints JSON, so a script can have what the page has.

    python3 tools/query.py find 'add_comm' --kind theorem
    python3 tools/query.py show Nat.add_comm
    python3 tools/query.py uses Nat.add_comm --json
    python3 tools/query.py axioms --beyond
    python3 tools/query.py module Mathlib.Order.Basic

Defaults to the published Mathlib; --site and --library point it elsewhere, including a local directory.
"""
from __future__ import annotations

import argparse
import gzip
import json
import pathlib
import sys
import urllib.request

DEFAULT_SITE = "https://keithadler.github.io/leanviz"


class Bundle:
    """One library's bundle, read lazily: the name list is 4 MB and a shard is a few hundred KB."""

    def __init__(self, site: str, library: str) -> None:
        self.base = f"{site.rstrip('/')}/data/{library}"
        self.local = not site.startswith(("http://", "https://"))
        self._names = None
        self._modules = None
        self._kinds = None
        self.manifest = json.loads(self._get("manifest.json", gz=False))

    def _get(self, name: str, gz: bool = True) -> bytes:
        where = f"{self.base}/{name}{'.gz' if gz else ''}"
        try:
            if self.local:
                raw = pathlib.Path(where).read_bytes()
            else:
                with urllib.request.urlopen(where) as r:
                    raw = r.read()
        except FileNotFoundError:
            raise SystemExit(f"no bundle at {self.base}: {where} does not exist.\n"
                             f"For a local checkout the site directory is the one holding data/, "
                             f"so: --site site --library mathlib")
        except OSError as e:
            raise SystemExit(f"could not read {where}: {e}")
        return gzip.decompress(raw) if gz else raw

    @property
    def names(self) -> list[str]:
        if self._names is None:
            self._names = self._get("names.txt").decode().rstrip("\n").split("\n")
        return self._names

    @property
    def kinds(self) -> str:
        if self._kinds is None:
            self._kinds = self._get("kinds.txt").decode()
        return self._kinds

    @property
    def modules(self) -> list[dict]:
        if self._modules is None:
            self._modules = json.loads(self._get("modules.json"))
        return self._modules

    def kind_of(self, i: int) -> str:
        return {"a": "axiom", "d": "def", "t": "theorem", "o": "opaque", "q": "quot",
                "i": "inductive", "c": "constructor", "r": "recursor"}.get(self.kinds[i], "unknown")

    def module_of(self, i: int) -> dict:
        lo, hi = 0, len(self.modules) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if self.modules[mid]["s"] <= i:
                lo = mid
            else:
                hi = mid - 1
        return self.modules[lo]

    def record(self, i: int) -> dict:
        m = self.module_of(i)
        shard = json.loads(self._get(f"m/{m['n']}.json"))
        return shard[i - m["s"]]

    def id_of(self, name: str) -> int:
        try:
            return self.names.index(name)
        except ValueError:
            raise SystemExit(f"no declaration named {name!r} in {self.manifest.get('title')}")


def main(argv: list[str]) -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["find", "show", "uses", "usedby", "axioms", "module", "libraries"])
    ap.add_argument("target", nargs="?", default="")
    ap.add_argument("--site", default=DEFAULT_SITE)
    ap.add_argument("--library", default="mathlib")
    ap.add_argument("--kind", default=None, help="find: keep one kind")
    ap.add_argument("--module", default=None, help="find: keep a module and what is under it")
    ap.add_argument("--limit", type=int, default=25)
    ap.add_argument("--beyond", action="store_true", help="axioms: only those beyond the standard three")
    ap.add_argument("--json", action="store_true", help="print JSON rather than lines")
    a = ap.parse_args(argv)

    if a.command == "libraries":
        site = a.site.rstrip("/")
        with urllib.request.urlopen(f"{site}/data/projects.json") as r:
            out = json.load(r)
        print(json.dumps(out, indent=2) if a.json else
              "\n".join(f"  {p['slug']:<12} {p['declarations']:>9,}  {p['title']}" for p in out))
        return

    b = Bundle(a.site, a.library)

    if a.command == "find":
        terms = [w.lower() for w in a.target.split()]
        hits = []
        for i, n in enumerate(b.names):
            low = n.lower()
            if not all(term in low for term in terms):
                continue
            if a.kind and b.kind_of(i) != a.kind:
                continue
            if a.module and not (b.module_of(i)["n"] == a.module or b.module_of(i)["n"].startswith(a.module + ".")):
                continue
            hits.append(i)
            if len(hits) >= a.limit:
                break
        rows = [{"id": i, "name": b.names[i], "kind": b.kind_of(i), "module": b.module_of(i)["n"]} for i in hits]
        print(json.dumps(rows, indent=2) if a.json else
              "\n".join(f"  {r['kind']:<11} {r['name']}   {r['module']}" for r in rows) or "  nothing")
        return

    if a.command == "axioms":
        use = b.manifest.get("axiomUse")
        std = set(b.manifest["standardAxioms"])
        rows = [{"axiom": n, "declarations": (use[i] if use else None), "standard": n in std}
                for i, n in enumerate(b.manifest["axioms"])]
        if a.beyond:
            rows = [r for r in rows if not r["standard"] and (r["declarations"] or 0) > 0]
        rows.sort(key=lambda r: -(r["declarations"] or 0))
        print(json.dumps(rows, indent=2) if a.json else
              "\n".join(f"  {r['declarations'] if r['declarations'] is not None else '?':>9} {r['axiom']}"
                        f"{'  (standard)' if r['standard'] else ''}" for r in rows))
        return

    if a.command == "module":
        m = next((m for m in b.modules if m["n"] == a.target), None)
        if not m:
            raise SystemExit(f"no module named {a.target!r}")
        out = {"module": m["n"], "declarations": m["c"], "firstId": m["s"],
               "imports": [b.modules[i]["n"] for i in m["i"]]}
        print(json.dumps(out, indent=2) if a.json else
              f"  {out['module']}: {out['declarations']:,} declarations, {len(out['imports'])} imports\n"
              + "\n".join(f"    import {i}" for i in out["imports"]))
        return

    i = b.id_of(a.target)
    d = b.record(i)
    if a.command == "show":
        out = {k: d.get(k) for k in ("n", "k", "s", "v", "d", "l", "md", "fd", "bc")}
        out["module"] = b.module_of(i)["n"]
        out["axioms"] = [b.manifest["axioms"][x] for x in d.get("a", [])]
        if a.json:
            print(json.dumps(out, indent=2))
        else:
            print(f"  {out['k']} {out['n']}   {out['module']}")
            print(f"  {out['s']}")
            if out.get("v"):
                print(f"  := {out['v']}")
            if out.get("md"):
                print(f"  modifiers: {', '.join(out['md'])}")
            print(f"  axioms: {', '.join(out['axioms']) or 'none'}")
        return

    ids = (d.get("t", []) + d.get("u", [])) if a.command == "uses" else d.get("b", [])
    rows = [{"id": x, "name": b.names[x], "kind": b.kind_of(x)} for x in ids[:a.limit]]
    print(json.dumps(rows, indent=2) if a.json else
          "\n".join(f"  {r['kind']:<11} {r['name']}" for r in rows) or "  nothing")


if __name__ == "__main__":
    main(sys.argv[1:])
