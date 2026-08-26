from __future__ import annotations

import argparse
import hashlib
import json
import sys
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RELEASE_ROOT = ROOT / "release"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description="Build a verified Decktamer Korean Patch release ZIP")
    parser.add_argument(
        "--replace",
        action="store_true",
        help="replace only the generated ZIP and checksum for the current manifest version",
    )
    args = parser.parse_args()

    manifest_path = ROOT / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    version = manifest["patch_version"]
    package_name = f"Decktamer-Korean-Patch-v{version}"
    archive = RELEASE_ROOT / f"{package_name}.zip"
    checksum = RELEASE_ROOT / f"{package_name}.zip.sha256"

    occupied = [path for path in (archive, checksum) if path.exists()]
    if occupied and not args.replace:
        joined = ", ".join(str(path) for path in occupied)
        raise FileExistsError(f"Refusing to overwrite existing release output: {joined}")

    for relative, expected in manifest["files"].items():
        source = ROOT / relative
        if not source.is_file():
            raise FileNotFoundError(source)
        actual_hash = sha256(source)
        if actual_hash != expected["sha256"] or source.stat().st_size != expected["size"]:
            raise ValueError(f"Manifest mismatch: {relative}")
        if source.suffix.lower() == ".bat":
            data = source.read_bytes()
            if b"\r\n" not in data or b"\n" in data.replace(b"\r\n", b""):
                raise ValueError(f"Windows batch file must use CRLF only: {relative}")

    RELEASE_ROOT.mkdir(exist_ok=True)
    mode = "w" if args.replace else "x"
    with zipfile.ZipFile(archive, mode, compression=zipfile.ZIP_DEFLATED, compresslevel=9) as output:
        for relative in sorted([*manifest["files"], "manifest.json"]):
            output.write(ROOT / relative, Path(package_name) / relative)
        embedded_installer = RELEASE_ROOT / "Decktamer_Korean_Patch.exe"
        if not embedded_installer.is_file():
            raise FileNotFoundError(f"Missing self-contained installer: {embedded_installer}")
        output.write(embedded_installer, Path(package_name) / embedded_installer.name)

    checksum.write_text(f"{sha256(archive)}  {archive.name}\n", encoding="ascii")
    print(archive)
    print(checksum)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
