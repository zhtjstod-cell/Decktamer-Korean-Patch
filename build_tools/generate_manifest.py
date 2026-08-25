from __future__ import annotations

import csv
import gzip
import hashlib
import json
import struct
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MAGIC = b"DKTKO174"
RUNTIME_PATHS = (
    "Install_Korean_Patch.bat",
    "Uninstall_Korean_Patch.bat",
    "Verify_Korean_Patch.bat",
    "tools/DecktamerKoreanPatch.ps1",
    "patches/Assembly-CSharp.dll.kpatch.gz",
    "patches/sharedassets0.assets.kpatch.gz",
    "README.md",
    "CHANGELOG.md",
    "LICENSE",
    "THIRD_PARTY_NOTICES.md",
    "licenses/OFL-NanumPenScript.txt",
    "licenses/OFL-NotoSerifKR.txt",
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read_delta_header(path: Path) -> dict[str, int | str]:
    with gzip.open(path, "rb") as stream:
        if stream.read(8) != MAGIC:
            raise ValueError(f"Unexpected patch magic: {path}")
        version, segments, original_size, patched_size = struct.unpack("<IIqq", stream.read(24))
        original_sha256 = stream.read(32).hex()
        patched_sha256 = stream.read(32).hex()
    return {
        "format_version": version,
        "segments": segments,
        "original_size": original_size,
        "patched_size": patched_size,
        "original_sha256": original_sha256,
        "patched_sha256": patched_sha256,
        "delta_sha256": sha256(path),
        "delta_size": path.stat().st_size,
    }


def main() -> None:
    localization = sorted((ROOT / "localization" / "ko").glob("*.csv"))
    rows = 0
    aggregate = hashlib.sha256()
    for path in localization:
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            rows += sum(1 for _ in csv.DictReader(stream))
        aggregate.update(path.read_bytes())

    deliverables = [ROOT / relative for relative in RUNTIME_PATHS] + localization
    missing = [str(path.relative_to(ROOT)) for path in deliverables if not path.is_file()]
    if missing:
        raise FileNotFoundError(f"Missing release files: {missing}")

    manifest = {
        "patch_name": "Decktamer Korean Patch",
        "patch_version": "1.0.0",
        "game_version": "1.7.4",
        "translation": {
            "method": "GPT direct translation with contextual and gameplay review",
            "tables": len(localization),
            "rows": rows,
            "aggregate_sha256": aggregate.hexdigest(),
        },
        "binary_deltas": {
            "Assembly-CSharp.dll": read_delta_header(ROOT / "patches" / "Assembly-CSharp.dll.kpatch.gz"),
            "sharedassets0.assets": read_delta_header(ROOT / "patches" / "sharedassets0.assets.kpatch.gz"),
        },
        "files": {
            path.relative_to(ROOT).as_posix(): {
                "sha256": sha256(path),
                "size": path.stat().st_size,
            }
            for path in sorted(deliverables)
        },
    }
    if manifest["translation"]["tables"] != 37 or manifest["translation"]["rows"] != 3039:
        raise ValueError("Unexpected localization table or row count")

    output = ROOT / "manifest.json"
    output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
