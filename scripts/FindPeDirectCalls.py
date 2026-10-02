#!/usr/bin/env python3
"""Find x64/x86 PE call-rel32 sites that target selected RVAs.

This is a deliberately read-only, dependency-free helper for offline binary
analysis. It scans executable sections for E8 rel32 encodings and reports the
source RVA for exact target matches. Candidate sites should still be confirmed
with a disassembler because a byte scan does not prove instruction boundaries.
"""

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path


class PeError(RuntimeError):
    pass


@dataclass(frozen=True)
class Section:
    name: str
    rva: int
    raw_offset: int
    raw_size: int
    executable: bool


def parse_integer(value: str) -> int:
    try:
        return int(value, 0)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"invalid integer: {value}") from exc


def read_sections(image: bytes) -> tuple[int, list[Section]]:
    if len(image) < 0x40 or image[:2] != b"MZ":
        raise PeError("not a DOS/PE image")
    pe_offset = struct.unpack_from("<I", image, 0x3C)[0]
    if pe_offset + 24 > len(image) or image[pe_offset : pe_offset + 4] != b"PE\0\0":
        raise PeError("missing PE signature")

    coff_offset = pe_offset + 4
    section_count = struct.unpack_from("<H", image, coff_offset + 2)[0]
    optional_size = struct.unpack_from("<H", image, coff_offset + 16)[0]
    optional_offset = coff_offset + 20
    if optional_offset + optional_size > len(image):
        raise PeError("truncated optional header")

    magic = struct.unpack_from("<H", image, optional_offset)[0]
    if magic == 0x20B:
        image_base = struct.unpack_from("<Q", image, optional_offset + 24)[0]
    elif magic == 0x10B:
        image_base = struct.unpack_from("<I", image, optional_offset + 28)[0]
    else:
        raise PeError(f"unsupported optional-header magic: 0x{magic:04X}")

    table_offset = optional_offset + optional_size
    table_end = table_offset + section_count * 40
    if table_end > len(image):
        raise PeError("truncated section table")

    sections: list[Section] = []
    for index in range(section_count):
        offset = table_offset + index * 40
        raw_name = image[offset : offset + 8].split(b"\0", 1)[0]
        name = raw_name.decode("ascii", errors="replace")
        rva = struct.unpack_from("<I", image, offset + 12)[0]
        raw_size = struct.unpack_from("<I", image, offset + 16)[0]
        raw_offset = struct.unpack_from("<I", image, offset + 20)[0]
        characteristics = struct.unpack_from("<I", image, offset + 36)[0]
        if raw_offset + raw_size > len(image):
            raise PeError(f"section {name!r} extends beyond the file")
        sections.append(
            Section(
                name=name,
                rva=rva,
                raw_offset=raw_offset,
                raw_size=raw_size,
                executable=bool(characteristics & 0x20000000),
            )
        )
    return image_base, sections


def find_calls(image: bytes, sections: list[Section], targets: set[int]) -> None:
    counts = {target: 0 for target in targets}
    for section in sections:
        if not section.executable:
            continue
        payload = memoryview(image)[
            section.raw_offset : section.raw_offset + section.raw_size
        ]
        for offset in range(max(0, len(payload) - 4)):
            if payload[offset] != 0xE8:
                continue
            displacement = struct.unpack_from("<i", payload, offset + 1)[0]
            source_rva = section.rva + offset
            target_rva = (source_rva + 5 + displacement) & 0xFFFFFFFFFFFFFFFF
            if target_rva in targets:
                counts[target_rva] += 1
                print(
                    f"TARGET_RVA=0x{target_rva:X} "
                    f"SOURCE_RVA=0x{source_rva:X} SECTION={section.name}"
                )

    for target in sorted(targets):
        print(f"SUMMARY TARGET_RVA=0x{target:X} CALLS={counts[target]}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument(
        "targets",
        nargs="+",
        type=parse_integer,
        help="target RVAs, for example 0x1B80310",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    image = args.image.resolve(strict=True).read_bytes()
    image_base, sections = read_sections(image)
    targets = set(args.targets)
    print(f"IMAGE_BASE=0x{image_base:X} EXECUTABLE_SECTIONS=" + ",".join(
        section.name for section in sections if section.executable
    ))
    find_calls(image, sections, targets)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PeError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
