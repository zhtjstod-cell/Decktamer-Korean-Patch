from __future__ import annotations

import csv
import gzip
import hashlib
import json
import struct
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MAGIC = b"DKTKO174"
PATCH_VERSION = "1.1.2"
PROFILES = {
    "1.7.4": {
        "tables": 37,
        "rows": 3039,
        "original_hashes": {
            "Assembly-CSharp.dll": "ee5dc47461f2776fa83d4acbb17d0434e12e76f0ac54ebc631a8c7cbb3225b5b",
            "sharedassets0.assets": "ba21274137c1f6a8b896cc25d2a316228b6ca9861b3c25e349395ea13a4fa6cf",
        },
    },
    "1.8.6": {
        "tables": 37,
        "rows": 3288,
        "original_hashes": {
            "Assembly-CSharp.dll": "a184232a87a3ed737fdab38db8f97589766e6699ceb4cdcdf3807a0984a86080",
            "sharedassets0.assets": "83ef469e22e1e23cca03ce551a3c6e7a39c4f007f2dadd5bd98c5b80aee36787",
        },
    },
}
RUNTIME_PATHS = (
    "Install_Korean_Patch.bat",
    "Uninstall_Korean_Patch.bat",
    "Verify_Korean_Patch.bat",
    "Update_Korean_Patch.bat",
    "tools/DecktamerKoreanPatch.ps1",
    "README.md",
    "CHANGELOG.md",
    "RELEASE_NOTES.md",
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


def localization_manifest(version: str, expected: dict) -> tuple[dict, list[Path]]:
    localization = sorted((ROOT / "localization" / version / "ko").glob("*.csv"))
    rows = 0
    aggregate = hashlib.sha256()
    for path in localization:
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            rows += sum(1 for _ in csv.DictReader(stream))
        aggregate.update(path.read_bytes())

    if len(localization) != expected["tables"] or rows != expected["rows"]:
        raise ValueError(
            f"Unexpected {version} localization count: {len(localization)} tables, {rows} rows"
        )
    return {
        "method": "GPT direct translation with contextual and gameplay review",
        "tables": len(localization),
        "rows": rows,
        "aggregate_sha256": aggregate.hexdigest(),
    }, localization


def main() -> None:
    deliverables = [ROOT / relative for relative in RUNTIME_PATHS]
    builds = {}

    for version, expected in PROFILES.items():
        translation, localization = localization_manifest(version, expected)
        deliverables.extend(localization)

        deltas = {}
        for file_name in ("Assembly-CSharp.dll", "sharedassets0.assets"):
            delta_path = ROOT / "patches" / version / f"{file_name}.kpatch.gz"
            deliverables.append(delta_path)
            header = read_delta_header(delta_path)
            expected_hash = expected["original_hashes"][file_name]
            if header["original_sha256"] != expected_hash:
                raise ValueError(
                    f"{version} {file_name} source hash mismatch: "
                    f"{header['original_sha256']} != {expected_hash}"
                )
            deltas[file_name] = header

        builds[version] = {
            "game_version": version,
            "translation": translation,
            "binary_deltas": deltas,
        }

    missing = [str(path.relative_to(ROOT)) for path in deliverables if not path.is_file()]
    if missing:
        raise FileNotFoundError(f"Missing release files: {missing}")

    manifest = {
        "patch_name": "Decktamer Korean Patch",
        "patch_version": PATCH_VERSION,
        "supported_game_versions": list(PROFILES),
        "compatibility_mode": {
            "enabled": True,
            "translation_profile": "latest supported profile",
            "matching": "intersection of current English template keys and translated keys",
            "unknown_binaries": "never modified",
        },
        "builds": builds,
        "files": {
            path.relative_to(ROOT).as_posix(): {
                "sha256": sha256(path),
                "size": path.stat().st_size,
            }
            for path in sorted(set(deliverables))
        },
    }

    output = ROOT / "manifest.json"
    with output.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    print(output)


if __name__ == "__main__":
    main()
