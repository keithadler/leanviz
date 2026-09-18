#!/usr/bin/env python3
"""Serve a directory on a port, threaded.

`python3 -m http.server` handles one request at a time, so a browser that opens a second connection deadlocks it,
which is exactly what a page fetching a manifest and three flat files does. Used by the page check and handy for
looking at a bundle locally:

    python3 tests/serve.py 8787 site
"""
import functools
import http.server
import socketserver
import sys


class Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8787
    root = sys.argv[2] if len(sys.argv) > 2 else "site"
    handler = functools.partial(Quiet, directory=root)
    with socketserver.ThreadingTCPServer(("127.0.0.1", port), handler) as httpd:
        httpd.daemon_threads = True
        print(f"serving {root} on http://localhost:{port}")
        httpd.serve_forever()
