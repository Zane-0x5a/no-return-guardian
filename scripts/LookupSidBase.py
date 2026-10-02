#!/usr/bin/env python3
"""Resolve 64-bit string IDs from a dconstruct sidbase.bin file."""

from __future__ import annotations

import argparse
import bisect
import struct
from pathlib import Path

from InspectPeVa import parse_integer


ENTRY = struct.Struct("<QQ")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sidbase", type=Path)
    parser.add_argument("hashes", nargs="*", type=parse_integer)
    parser.add_argument(
        "--contains",
        action="append",
        default=[],
        help="case-insensitive name substring; repeat to search for alternatives",
    )
    return parser.parse_args()


def read_index(payload: bytes) -> tuple[list[int], list[int]]:
    if len(payload) < 8:
        raise ValueError("sidbase header is truncated")

    # dconstruct sidbase files have appeared with both a 32-bit count and
    # string-pool-relative offsets, and a 64-bit count with absolute offsets.
    # Validate the complete index instead of guessing from file size.
    layouts = (("<I", 4, True), ("<Q", 8, False))
    errors: list[str] = []
    for count_format, table_offset, relative_offsets in layouts:
        entry_count = struct.unpack_from(count_format, payload, 0)[0]
        table_end = table_offset + entry_count * ENTRY.size
        if table_end > len(payload):
            errors.append(f"{table_offset * 8}-bit count truncates the table")
            continue

        hashes: list[int] = []
        offsets: list[int] = []
        valid = True
        for index in range(entry_count):
            value, stored_offset = ENTRY.unpack_from(
                payload, table_offset + index * ENTRY.size
            )
            offset = table_end + stored_offset if relative_offsets else stored_offset
            if offset < table_end or offset >= len(payload):
                errors.append(
                    f"{table_offset * 8}-bit count gives an invalid string "
                    f"offset at entry {index}"
                )
                valid = False
                break
            hashes.append(value)
            offsets.append(offset)
        if valid and hashes == sorted(hashes):
            return hashes, offsets
        if valid:
            errors.append(f"{table_offset * 8}-bit count gives an unsorted index")

    raise ValueError("unsupported sidbase layout: " + "; ".join(errors))


def read_name(payload: bytes, offset: int) -> str:
    end = payload.find(b"\0", offset)
    if end < 0:
        raise ValueError("sidbase string is not NUL-terminated")
    return payload[offset:end].decode("utf-8")


def main() -> int:
    args = parse_args()
    if not args.hashes and not args.contains:
        raise ValueError("provide at least one hash or --contains value")
    payload = args.sidbase.resolve(strict=True).read_bytes()
    hashes, offsets = read_index(payload)

    for raw_value in args.hashes:
        value = raw_value & 0xFFFFFFFFFFFFFFFF
        index = bisect.bisect_left(hashes, value)
        if index >= len(hashes) or hashes[index] != value:
            print(f"SID=0x{value:016X} NAME=<unknown>")
            continue
        print(f"SID=0x{value:016X} NAME={read_name(payload, offsets[index])!r}")

    needles = [value.casefold() for value in args.contains]
    matches = 0
    if needles:
        for value, offset in zip(hashes, offsets, strict=True):
            name = read_name(payload, offset)
            if not any(needle in name.casefold() for needle in needles):
                continue
            print(f"SID=0x{value:016X} NAME={name!r}")
            matches += 1
        print(f"SEARCH_MATCHES={matches}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError, ValueError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
