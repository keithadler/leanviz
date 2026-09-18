# Design notes

## What this is not

It does not check proofs. Reading a dependency graph and checking a proof are different jobs with different
costs, and conflating them is the mistake this design exists to avoid. Extracting "which constants does this
declaration reference" needs only the `.olean` reader; it takes about two minutes for all of Mathlib. Re-deriving
every proof needs the kernel and takes about eleven. The generator does the first. `tenet check` does the second,
separately, and its report is stamped into the bundle so a page can say which declarations were re-checked.

## Why a static site

Mathlib is 791,453 declarations. Three shapes were possible.

- **A desktop application.** This is what MathlibExplorer was, and why it died: a binary per platform, with data
  frozen at whatever its author last exported. Browsing tools need a URL.
- **A server.** A live query interface could answer things the bundle cannot, such as transitive dependency counts
  on demand. It also needs hosting that can run code, and it puts a machine between the reader and the answer.
- **A bundle generated ahead of time.** Everything a page needs is computed once, in bulk, where the whole graph
  is in memory at once, and served as files. Hosting is a static file server. The cost is size: 474 MB on disk
  and about 85 MB over the wire.

The third was chosen because the questions worth answering are the same for every visitor, so answering them once
per Mathlib revision rather than once per visit is strictly cheaper, and because a static host is something anyone
can point at their own project.

## Why ids rather than names

Names in Mathlib are long: 48 characters on average, and the graph has 20.9 million edges. Storing edges by name
would make the bundle several gigabytes of repeated text. Dense integer ids make an edge four bytes in memory and
a handful of characters in JSON, and they give the page an id-to-kind and id-to-popularity lookup as flat arrays
rather than maps. The cost is the module table, which turns an id back into a shard, and the invariant that ids
are contiguous per module, which `check_bundle.py` verifies.

## Why the statement printer is not Lean's

Lean's delaborator is part of Lean: running it means running Lean, which is the dependency this whole project
avoids. So statements are printed here, from the kernel terms, with a table of the notation Mathlib actually uses.
That is a deliberate approximation with two consequences worth stating plainly:

- What the printer does not know, it prints as a plain application. That is always correct, only less pretty.
- Attributes that change how Lean prints a name, such as `@[pp_nodot]`, live in environment extensions the reader
  does not decode, so a few names read differently from the docs.

The printer is pinned by tests against real statements from Lean's core library and from Mathlib, compared with
the rendering in the official docs.

## Why the reverse edges are capped

A foundational constant such as `Eq` is referenced by 487,483 declarations. Shipping that list would make one
shard larger than the rest of the bundle, and nobody scrolls half a million names. Each declaration keeps the 200
most depended-upon dependents, chosen by a bounded selection during generation, and the true count is always
shown next to them. The page says "the 200 most used shown" rather than pretending the list is complete.

## Why the axiom closure is a bitset

Mathlib has 84 distinct axioms. One bit per axiom per declaration is 84 bits, or two 64-bit words, times 791,453
declarations: 13 MB, which fits in memory comfortably. The closure is computed by a depth-first pass followed by a
fixpoint, because mutual blocks make the reference graph cyclic and a single topological pass would be wrong. The
generator prints how many fixpoint passes were needed; on Mathlib it is three.
