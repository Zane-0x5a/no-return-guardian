#!/usr/bin/env python3
"""Resolve StringId64 values against strings stored in a PSARC archive.

The scanner is read-only and supports path filters so large game archives can
be searched incrementally without extracting their contents to disk.
"""

from __future__ import annotations

import argparse
import re
import struct
import sys
from pathlib import Path

from ExtractPsarcEntry import ArchiveError, open_archive
from FindPeSidStrings import string_id64
from InspectPeVa import parse_integer


PRINTABLE = re.compile(rb"[\x20-\x7E]{3,}")
TOKEN = re.compile(rb"[A-Za-z0-9_./:@?+\-]{3,}")


def candidates(payload: bytes):
    """Yield full printable spans and useful tokens within those spans."""
    seen: set[bytes] = set()
    for match in PRINTABLE.finditer(payload):
        span = match.group()
        for candidate in (span, *TOKEN.findall(span)):
            if candidate not in seen:
                seen.add(candidate)
                yield candidate, match.start()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("hashes", nargs="+", type=parse_integer)
    parser.add_argument(
        "--path-filter",
        action="append",
        default=[],
        help="case-insensitive path substring; repeat to search multiple groups",
    )
    parser.add_argument(
        "--max-entry-size",
        type=parse_integer,
        default=16 * 1024 * 1024,
        help="skip larger entries (default: 16 MiB)",
    )
    parser.add_argument(
        "--raw",
        action="store_true",
        help="also report raw little-endian 64-bit SID occurrences",
    )
    return parser.parse_args()


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
    if args.max_entry_size <= 0:
        raise ArchiveError("--max-entry-size must be positive")

    targets = {value & 0xFFFFFFFFFFFFFFFF for value in args.hashes}
    filters = [value.casefold() for value in args.path_filter]
    base, archive = open_archive(args.archive.resolve(strict=True))
    scanned = 0
    skipped = 0
    matches = 0
    try:
        for name, entry in archive.named_entries():
            folded = name.casefold()
            if filters and not any(value in folded for value in filters):
                continue
            if entry.uncompressed_size > args.max_entry_size:
                skipped += 1
                continue

            payload = archive.read_entry(entry)
            scanned += 1
            if args.raw:
                for value in targets:
                    for offset in find_all(payload, struct.pack("<Q", value)):
                        print(
                            f"RAW_SID=0x{value:016X} ENTRY={name!r} "
                            f"ENTRY_OFFSET=0x{offset:X}"
                        )
                        matches += 1
            for candidate, offset in candidates(payload):
                value = string_id64(candidate)
                if value not in targets:
                    continue
                print(
                    f"HASH=0x{value:016X} TEXT={candidate.decode('ascii')!r} "
                    f"ENTRY={name!r} ENTRY_OFFSET=0x{offset:X}"
                )
                matches += 1
    finally:
        base.close()

    for value in sorted(targets):
        print(f"TARGET=0x{value:016X}")
    print(
        f"SUMMARY MATCHES={matches} SCANNED_ENTRIES={scanned} "
        f"SKIPPED_LARGE_ENTRIES={skipped}"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ArchiveError, OSError, UnicodeError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
