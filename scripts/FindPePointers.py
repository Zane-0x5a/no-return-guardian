#!/usr/bin/env python3
"""Find little-endian VA/RVA pointers to selected addresses in a PE image.

This complements RIP-relative code-reference scanning by locating pointers in
registration tables and other data sections. It is intentionally read-only.
"""

from __future__ import annotations

import argparse
import struct
from pathlib import Path

from InspectPeVa import PeError, Section, parse_integer, read_layout


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
            delta = offset - section.raw_offset
            return image_base + section.virtual_address + delta, section.name
    return None, "overlay"


def find_all(payload: bytes, needle: bytes) -> list[int]:
    matches: list[int] = []
    position = 0
    while True:
        position = payload.find(needle, position)
        if position < 0:
            return matches
        matches.append(position)
        position += 1


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument(
        "targets",
        nargs="+",
        type=parse_integer,
        help="target VAs or RVAs, for example 0x142D72710 or 0x2D72710",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    image = args.image.resolve(strict=True).read_bytes()
    image_base, size_of_headers, sections = read_layout(image)

    for target in args.targets:
        va = target if target >= image_base else image_base + target
        rva = va - image_base
        encodings = [("VA64", struct.pack("<Q", va))]
        if rva <= 0xFFFFFFFF:
            encodings.append(("RVA32", struct.pack("<I", rva)))

        total = 0
        for kind, needle in encodings:
            for offset in find_all(image, needle):
                source_va, section = describe_offset(
                    offset, image_base, size_of_headers, sections
                )
                source = "none" if source_va is None else f"0x{source_va:X}"
                print(
                    f"TARGET_VA=0x{va:X} KIND={kind} FILE=0x{offset:X} "
                    f"SOURCE_VA={source} SECTION={section}"
                )
                total += 1
        print(f"SUMMARY TARGET_VA=0x{va:X} POINTERS={total}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PeError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
