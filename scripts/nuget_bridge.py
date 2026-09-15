"""Loopback-only NuGet transport for Windows sandboxes without Schannel credentials.

Upstream HTTPS certificate verification remains enabled. No credentials, uploads,
arbitrary hosts or filesystem paths are accepted. Normal builds use nuget.org.
"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError
import argparse
import json
import re
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=18761)
args = parser.parse_args()
cache = Path(__file__).resolve().parents[1] / ".tools" / "nuget-transport"
cache.mkdir(parents=True, exist_ok=True)

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/index.json":
            payload = json.dumps({"version": "3.0.0", "resources": [{"@id": f"http://127.0.0.1:{args.port}/flat/", "@type": "PackageBaseAddress/3.0.0"}]}).encode()
        elif re.fullmatch(r"/flat/[a-z0-9._-]+/(?:index.json|[a-z0-9._-]+/[a-z0-9._-]+\.(?:nupkg|nuspec|sha512))", self.path):
            key = self.path.removeprefix("/flat/")
            target = cache / key
            try:
                if target.is_file():
                    payload = target.read_bytes()
                else:
                    payload = urllib.request.urlopen("https://api.nuget.org/v3-flatcontainer/" + key, timeout=120).read()
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(payload)
            except HTTPError as error:
                self.send_error(error.code)
                return
            except Exception as error:
                print(f"Upstream failure: {key}: {error}", flush=True)
                self.send_error(502)
                return
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/json" if self.path.endswith("json") else "application/octet-stream")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, format, *values):
        pass

print(f"NuGet bridge listening on loopback port {args.port}", flush=True)
ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
