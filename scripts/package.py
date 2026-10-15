#!/usr/bin/env python3
"""Package the fork DLL with the exact upstream v3.2.2 runtime dependencies."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
UPSTREAM_URL = "https://github.com/TianxSky/DiscordStatus/releases/download/v3.2.2/release.zip"
UPSTREAM_SHA256 = "ef9938ff1d47df4ac16f3a55e71697c6f5bd2c13b53be2d04358104208778a47"
PLUGIN_ROOT = "release/addons/counterstrikesharp/plugins/DiscordStatus/"


def checked_upstream(data: bytes) -> bytes:
    if hashlib.sha256(data).hexdigest() != UPSTREAM_SHA256:
        raise ValueError("Upstream archive SHA-256 mismatch; refusing to package")
    return data


def make_zip(files: dict[str, bytes]) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, content in sorted(files.items()):
            info = zipfile.ZipInfo(name, date_time=(2000, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, content)
    return output.getvalue()


def package_plugin(upstream: bytes, plugin_dir: Path, metadata: dict) -> bytes:
    with zipfile.ZipFile(io.BytesIO(upstream)) as archive:
        files = {name: archive.read(name) for name in archive.namelist()
                 if name.startswith(PLUGIN_ROOT) and not name.endswith("/")}
    for name in ("DiscordStatus.dll", "DiscordStatus.pdb", "DiscordStatus.deps.json"):
        if PLUGIN_ROOT + name not in files:
            raise ValueError(f"Missing expected upstream file: {name}")
    # Keep the released dependency DLLs and dependency metadata unchanged.
    # Do not package CounterStrikeSharp.API or other host-owned assemblies.
    for name in ("DiscordStatus.dll", "DiscordStatus.pdb"):
        files[PLUGIN_ROOT + name] = (plugin_dir / name).read_bytes()
    files[PLUGIN_ROOT + "LICENSE"] = (ROOT / "LICENSE").read_bytes()
    files[PLUGIN_ROOT + "FORK.md"] = (ROOT / "FORK.md").read_bytes()
    files[PLUGIN_ROOT + "BUILD.json"] = (json.dumps(metadata, indent=2, sort_keys=True) + "\n").encode()
    return make_zip(files)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin-dir", type=Path, default=ROOT / ".artifacts/plugin")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    parser.add_argument("--upstream-zip", type=Path, help="Optional cached upstream ZIP; checksum is still mandatory")
    args = parser.parse_args()
    version = ET.parse(ROOT / "Source/DiscordStatus.csproj").findtext(".//Version")
    if not version or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+-[A-Za-z0-9.-]+", version):
        raise ValueError("Expected an explicit fork prerelease version")
    manifest = (ROOT / "Source/Manifests.cs").read_text()
    if f'ModuleVersion => "{version}"' not in manifest:
        raise ValueError("Project and plugin module versions disagree")
    tag = "v" + version
    if args.upstream_zip:
        upstream = checked_upstream(args.upstream_zip.read_bytes())
    else:
        with urllib.request.urlopen(UPSTREAM_URL, timeout=60) as response:
            upstream = checked_upstream(response.read())
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT).strip():
        raise ValueError("Commit the reviewed source before creating release artifacts")
    source = subprocess.check_output(["git", "archive", "--format=zip", f"--prefix=DiscordStatus-{tag}/", "HEAD"], cwd=ROOT)
    metadata = {"version": version, "source_commit": sha,
                "source_repository": "https://github.com/ZdravkoChiflishki/DiscordStatus",
                "upstream_archive_sha256": UPSTREAM_SHA256, "upstream_release": "v3.2.2",
                "status": "prerelease; native CS2 loading and real Discord delivery not yet verified"}
    binary_name = f"DiscordStatus-{tag}.zip"
    source_name = f"DiscordStatus-{tag}-source.zip"
    artifacts = {binary_name: package_plugin(upstream, args.plugin_dir, metadata), source_name: source}
    args.output.mkdir(parents=True, exist_ok=True)
    for name, data in artifacts.items():
        (args.output / name).write_bytes(data)
    sums = "".join(f"{hashlib.sha256(data).hexdigest()}  {name}\n" for name, data in sorted(artifacts.items()))
    (args.output / "SHA256SUMS").write_text(sums)
    print(sums, end="")


if __name__ == "__main__":
    main()
