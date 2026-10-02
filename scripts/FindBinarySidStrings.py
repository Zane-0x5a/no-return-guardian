#!/usr/bin/env python3
"""Resolve StringId64 values against printable strings in arbitrary files.

Unlike FindPeSidStrings.py, this helper accepts non-PE resources such as PAK
files and text manifests. It is intentionally read-only.
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path

from FindPeSidStrings import string_id64
from InspectPeVa import parse_integer


PRINTABLE = re.compile(rb"[\x20-\x7E]{3,}")
TOKEN = re.compile(rb"[A-Za-z0-9_./:@?+\-]{3,}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", type=Path)
    parser.add_argument(
        "--hash",
        action="append",
        dest="hashes",
        required=True,
        type=parse_integer,
        help="numeric StringId64 value; repeat for multiple candidates",
    )
    parser.add_argument(
        "--glob",
        default="*",
        help="file pattern used below directories (default: *)",
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


def candidates(payload: bytes):
    seen: set[bytes] = set()
    for match in PRINTABLE.finditer(payload):
        span = match.group()
        values = [(span, match.start())]
        values.extend(
            (token.group(), match.start() + token.start())
            for token in TOKEN.finditer(span)
        )
        for candidate, offset in values:
            if candidate in seen:
                continue
            seen.add(candidate)
            yield candidate, offset


def main() -> int:
    args = parse_args()
    targets = {value & 0xFFFFFFFFFFFFFFFF for value in args.hashes}
    counts = {value: 0 for value in targets}
    scanned = 0

    for path in input_files(args.paths, args.glob):
        payload = path.read_bytes()
        scanned += 1
        for candidate, offset in candidates(payload):
            value = string_id64(candidate)
            if value not in targets:
                continue
            print(
                f"HASH=0x{value:016X} TEXT={candidate.decode('ascii')!r} "
                f"FILE={str(path)!r} OFFSET=0x{offset:X}"
            )
            counts[value] += 1

    for value in sorted(targets):
        print(f"SUMMARY HASH=0x{value:016X} STRINGS={counts[value]}")
    print(f"SCANNED_FILES={scanned}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
