#!/usr/bin/env python3
"""Serve the extension folder over HTTPS for Twitch local testing.

Twitch will only load an extension's assets from an https Base URI, so the plain
`python -m http.server` in the README is not enough once you point the developer console at
`https://localhost:8080/`. This serves the same folder over TLS using the ASP.NET Core development
certificate, which the same machine already trusts — so the extension iframe loads without a
certificate interstitial.

    python extension/dev/serve-https.py            # https://localhost:8080/
    python extension/dev/serve-https.py --port 8443

Prerequisite, once per machine:

    dotnet dev-certs https --trust

The exported key pair is written to the OS temp directory, never into the repository.
"""

from __future__ import annotations

import argparse
import functools
import http.server
import pathlib
import ssl
import subprocess
import sys
import tempfile


def export_dev_cert(dest: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
    """Export the ASP.NET dev certificate as a PEM pair, reusing a previous export when present."""
    cert = dest / "localhost.pem"
    key = dest / "localhost.key"
    if cert.exists() and key.exists():
        return cert, key

    dest.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        ["dotnet", "dev-certs", "https", "--export-path", str(cert),
         "--format", "Pem", "--no-password"],
        capture_output=True, text=True,
    )
    if result.returncode != 0 or not cert.exists():
        sys.exit(
            "Could not export the ASP.NET development certificate.\n"
            "Run `dotnet dev-certs https --trust` first, then try again.\n\n"
            + (result.stderr or result.stdout)
        )
    return cert, key


class ExtensionAssetHandler(http.server.SimpleHTTPRequestHandler):
    """Static files with the headers Twitch needs to load an extension from this host.

    Twitch fetches extension assets cross-origin, so a server with no `Access-Control-Allow-Origin`
    fails the load outright — the developer console reports it as a CORS error. `SimpleHTTPRequestHandler`
    sends no such header, hence this subclass. Caching is disabled too, so an edit to the card shows up
    on the next reload instead of being served from Twitch's or the browser's cache.
    """

    def end_headers(self) -> None:
        # Echo the caller's origin rather than "*": Private Network Access preflights are stricter
        # about wildcards than ordinary CORS.
        origin = self.headers.get("Origin") or "*"
        self.send_header("Access-Control-Allow-Origin", origin)
        self.send_header("Vary", "Origin")
        self.send_header("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "*")
        # Private Network Access. Kept for older Chromium and for Safari/Firefox, but note that
        # Chrome PUT PNA ON HOLD and replaced it with Local Network Access, which is a user
        # permission delegated through Permissions Policy — no response header can satisfy it. In
        # Chrome 142+ every frame in the hierarchy must carry allow="local-network-access", which
        # for a Twitch extension means Twitch's own console would have to add it. It does not, so
        # serving these assets from localhost does not work in Chrome at all: host them publicly
        # (Twitch's own asset upload) or develop in Firefox, which has not implemented LNA.
        # https://developer.chrome.com/blog/local-network-access
        self.send_header("Access-Control-Allow-Private-Network", "true")
        self.send_header("Cache-Control", "no-store, max-age=0")
        super().end_headers()

    def do_OPTIONS(self) -> None:
        self.send_response(204)
        self.end_headers()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8080, help="port to listen on (default 8080)")
    parser.add_argument("--host", default="localhost", help="host to bind (default localhost)")
    args = parser.parse_args()

    root = pathlib.Path(__file__).resolve().parent.parent
    cert, key = export_dev_cert(pathlib.Path(tempfile.gettempdir()) / "ednexus-dev-cert")

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certfile=str(cert), keyfile=str(key))

    handler = functools.partial(ExtensionAssetHandler, directory=str(root))
    httpd = http.server.ThreadingHTTPServer((args.host, args.port), handler)
    httpd.socket = context.wrap_socket(httpd.socket, server_side=True)

    base = f"https://{args.host}:{args.port}/"
    print(f"Serving {root} at {base}")
    print(f"  Base URI            {base}")
    print(f"  Video overlay path  video_overlay.html")
    print(f"  Panel path          panel.html")
    print(f"  Component path      component.html")
    print(f"  Mobile path         mobile.html")
    print(f"  Config path         config.html")
    for view in ("video_overlay", "panel", "component", "mobile"):
        print(f"  Standalone preview  {base}{view}.html?mock=1")
    print("Ctrl+C to stop.")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()
