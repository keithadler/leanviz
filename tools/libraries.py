#!/usr/bin/env python3
"""The libraries this site is about, as opposed to the guests it builds on request.

One list, because it is used for three different things that must agree: which library the site opens on, which
bundles may never be evicted, and which slugs a stranger's request may not claim. When these drifted, a guest
became the front page; and a request for any repository whose last path component is "mathlib" would have
uploaded itself over the real Mathlib bundle, because the upload clobbers by name.
"""

HOME = ["mathlib", "flt", "nse"]
