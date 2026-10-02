#!/usr/bin/env python3
"""Find raw little-endian StringId64 values in binary files.

This read-only helper resolves named candidates with the game's FNV-1a hash,
then reports every encoded SID occurrence in files or directory trees.
"""

from __future__ import annotations

import argparse
import struct
from pathlib import Path

from FindPeSidStrings import string_id64
from InspectPeVa import parse_integer


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", type=Path)
    parser.add_argument(
        "--name",
        action="append",
        default=[],
        help="ASCII SID text; repeat for multiple candidates",
    )
    parser.add_argument(
        "--hash",
        action="append",
        default=[],
        type=parse_integer,
        help="numeric StringId64 value; repeat for multiple candidates",
    )
    parser.add_argument(
        "--glob",
        default="*.bin",
        help="file pattern used below directories (default: *.bin)",
    )
    return parser.parse_args()


def input_files(paths: list[Path], pattern: str):
    seen: set[Path] = set()
    for raw_path in paths:
        path = raw_path.resolve(strict=True)
        candidates = path.rglob(pattern) if path.is_dir() else (path,)
        for candidate in candidates:
            if not candidate.is_file() or candidate in seen:
                continue
            seen.add(candidate)
            yield candidate


def find_all(payload: bytes, needle: bytes):
    offset = 0
    while True:
        offset = payload.find(needle, offset)
        if offset < 0:
            return
        yield offset
        offset += 1


def main() -> int:
    args = parse_args()
    targets: dict[int, str] = {
        string_id64(name.encode("ascii")): name for name in args.name
    }
    for value in args.hash:
        normalized = value & 0xFFFFFFFFFFFFFFFF
        targets.setdefault(normalized, f"0x{normalized:016X}")
    if not targets:
        raise SystemExit("ERROR: provide at least one --name or --hash")

    counts = {value: 0 for value in targets}
    scanned = 0
    for path in input_files(args.paths, args.glob):
        payload = path.read_bytes()
        scanned += 1
        for value, label in targets.items():
            for offset in find_all(payload, struct.pack("<Q", value)):
                print(
                    f"SID=0x{value:016X} NAME={label!r} "
                    f"FILE={str(path)!r} OFFSET=0x{offset:X}"
                )
                counts[value] += 1

    for value, label in targets.items():
        print(
            f"SUMMARY SID=0x{value:016X} NAME={label!r} "
            f"MATCHES={counts[value]}"
        )
    print(f"SCANNED_FILES={scanned}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
