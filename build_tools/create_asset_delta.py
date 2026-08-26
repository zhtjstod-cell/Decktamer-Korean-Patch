from __future__ import annotations

import argparse
import gzip
import hashlib
import struct
from pathlib import Path

import numpy as np


MAGIC = b"DKTKO174"
FORMAT_VERSION = 1


def sha256(path: Path) -> bytes:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.digest()


def find_segments(original: Path, patched: Path, max_gap: int) -> list[tuple[int, int]]:
    segments: list[tuple[int, int]] = []
    offset = 0
    common_size = min(original.stat().st_size, patched.stat().st_size)
    with original.open("rb") as left, patched.open("rb") as right:
        while offset < common_size:
            block_size = min(8 * 1024 * 1024, common_size - offset)
            source = left.read(block_size)
            target = right.read(block_size)
            if len(source) != block_size or len(target) != block_size:
                raise ValueError("Unexpected end of input while creating the patch")
            changed = np.flatnonzero(
                np.frombuffer(source, dtype=np.uint8) != np.frombuffer(target, dtype=np.uint8)
            )
            if changed.size:
                starts = np.r_[0, np.flatnonzero(np.diff(changed) > max_gap + 1) + 1]
                ends = np.r_[starts[1:] - 1, len(changed) - 1]
                for first, last in zip(starts, ends):
                    start = offset + int(changed[first])
                    end = offset + int(changed[last]) + 1
                    if segments and start - segments[-1][1] <= max_gap:
                        segments[-1] = (segments[-1][0], end)
                    else:
                        segments.append((start, end))
            offset += block_size
    if patched.stat().st_size > common_size:
        start, end = common_size, patched.stat().st_size
        if segments and start - segments[-1][1] <= max_gap:
            segments[-1] = (segments[-1][0], end)
        else:
            segments.append((start, end))
    return segments


def main() -> None:
    parser = argparse.ArgumentParser(description="Create a Decktamer fixed-offset binary delta")
    parser.add_argument("original", type=Path)
    parser.add_argument("patched", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--max-gap", type=int, default=8)
    args = parser.parse_args()

    segments = find_segments(args.original, args.patched, args.max_gap)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".tmp")

    with temporary.open("wb") as compressed_file:
        with gzip.GzipFile(filename="", mode="wb", fileobj=compressed_file, compresslevel=9, mtime=0) as output:
            output.write(MAGIC)
            output.write(struct.pack("<IIqq", FORMAT_VERSION, len(segments), args.original.stat().st_size, args.patched.stat().st_size))
            output.write(sha256(args.original))
            output.write(sha256(args.patched))
            with args.patched.open("rb") as patched_stream:
                for start, end in segments:
                    length = end - start
                    patched_stream.seek(start)
                    payload = patched_stream.read(length)
                    output.write(struct.pack("<qi", start, length))
                    output.write(payload)
    temporary.replace(args.output)

    payload_bytes = sum(end - start for start, end in segments)
    print(
        {
            "output": str(args.output),
            "segments": len(segments),
            "payload_bytes": payload_bytes,
            "compressed_bytes": args.output.stat().st_size,
            "original_sha256": sha256(args.original).hex(),
            "patched_sha256": sha256(args.patched).hex(),
        }
    )


if __name__ == "__main__":
    main()
