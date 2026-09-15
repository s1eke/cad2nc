"""Install the pinned build SDK locally; never used by the released application."""
from pathlib import Path
import hashlib
import json
import urllib.request
import zipfile

root = Path(__file__).resolve().parents[1]
version = json.loads((root / "global.json").read_text())["sdk"]["version"]
destination = root / ".tools" / "dotnet"
if (destination / "sdk" / version).is_dir():
    print(f"SDK {version} already available: {destination}")
    raise SystemExit(0)
metadata = json.load(urllib.request.urlopen("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", timeout=60))
sdk = next(s for r in metadata["releases"] for s in r.get("sdks", [r["sdk"]]) if s["version"] == version)
item = next(f for f in sdk["files"] if f["rid"] == "win-x64" and f["name"].endswith(".zip"))
cache = root / ".tools" / "downloads"
cache.mkdir(parents=True, exist_ok=True)
archive = cache / f"dotnet-sdk-{version}-win-x64.zip"
print(f"Downloading Microsoft .NET SDK {version}…", flush=True)
urllib.request.urlretrieve(item["url"], archive)
if hashlib.file_digest(archive.open("rb"), "sha512").hexdigest().lower() != item["hash"].lower():
    raise RuntimeError("SDK SHA512 verification failed")
destination.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(archive) as bundle:
    for entry in bundle.infolist():
        target = (destination / entry.filename).resolve()
        if not target.is_relative_to(destination.resolve()):
            raise RuntimeError("Unsafe SDK archive path")
    bundle.extractall(destination)
print(f"Verified SDK installed: {destination}", flush=True)
