#!/usr/bin/env python3
"""Resolve StringId64 values against printable strings embedded in a PE.

The game's 64-bit string IDs use FNV-1a. This read-only helper is useful for
identifying UI and script dispatcher constants when no symbol database entry is
available.
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path

from InspectPeVa import PeError, Section, parse_integer, read_layout


PRINTABLE = re.compile(rb"[\x20-\x7E]{3,}")


def string_id64(payload: bytes) -> int:
    value = 0xCBF29CE484222325
    for byte in payload:
        value = ((value ^ byte) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return value


def describe_offset(
    offset: int,
    image_base: int,
    size_of_headers: int,
    sections: list[Section],
) -> tuple[int | None, str]:
    if offset < size_of_headers:
        return image_base + offset, "headers"
    for section in sections:
        if section.raw_offset <= offset < section.raw_offset + section.raw_size:
            return (
                image_base + section.virtual_address + offset - section.raw_offset,
                section.name,
            )
    return None, "overlay"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("hashes", nargs="+", type=parse_integer)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    image = args.image.resolve(strict=True).read_bytes()
    image_base, size_of_headers, sections = read_layout(image)
    targets = {value & 0xFFFFFFFFFFFFFFFF for value in args.hashes}
    counts = {value: 0 for value in targets}

    for match in PRINTABLE.finditer(image):
        raw = match.group()
        value = string_id64(raw)
        if value not in targets:
            continue
        source_va, section = describe_offset(
            match.start(), image_base, size_of_headers, sections
        )
        source = "none" if source_va is None else f"0x{source_va:X}"
        decoded = raw.decode("ascii")
        print(
            f"HASH=0x{value:016X} TEXT={decoded!r} FILE=0x{match.start():X} "
            f"VA={source} SECTION={section}"
        )
        counts[value] += 1

    for value in sorted(targets):
        print(f"SUMMARY HASH=0x{value:016X} STRINGS={counts[value]}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PeError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
