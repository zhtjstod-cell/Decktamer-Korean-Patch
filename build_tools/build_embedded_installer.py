from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "build_tools" / "embedded_installer"
PAYLOAD = PROJECT / "payload.zip"
RELEASE = ROOT / "release"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description="Build the self-contained Windows installer")
    parser.add_argument("--replace", action="store_true")
    args = parser.parse_args()

    manifest_path = ROOT / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    version = manifest["patch_version"]
    versioned = RELEASE / f"Decktamer-Korean-Patch-v{version}.exe"
    generic = RELEASE / "Decktamer_Korean_Patch.exe"
    checksum = RELEASE / f"{versioned.name}.sha256"
    occupied = [path for path in (versioned, generic, checksum) if path.exists()]
    if occupied and not args.replace:
        raise FileExistsError("Refusing to overwrite: " + ", ".join(map(str, occupied)))

    payload_files = [
        relative
        for relative in manifest["files"]
        if relative.startswith("localization/") or relative.startswith("patches/")
    ]
    payload_files.extend(
        [
            "manifest.json",
            "LICENSE",
            "THIRD_PARTY_NOTICES.md",
            "licenses/OFL-NanumPenScript.txt",
            "licenses/OFL-NotoSerifKR.txt",
        ]
    )
    with zipfile.ZipFile(PAYLOAD, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as output:
        for relative in sorted(set(payload_files)):
            source = ROOT / relative
            if not source.is_file():
                raise FileNotFoundError(source)
            output.write(source, relative)

    subprocess.run(
        [
            "dotnet",
            "publish",
            str(PROJECT / "DecktamerEmbeddedInstaller.csproj"),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:PublishSingleFile=true",
        ],
        check=True,
    )
    built = PROJECT / "bin" / "Release" / "net8.0-windows" / "win-x64" / "publish" / "Decktamer_Korean_Patch.exe"
    if not built.is_file():
        raise FileNotFoundError(built)

    RELEASE.mkdir(exist_ok=True)
    shutil.copy2(built, versioned)
    shutil.copy2(built, generic)
    checksum.write_text(f"{sha256(versioned)}  {versioned.name}\n", encoding="ascii")
    print(versioned)
    print(checksum)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
