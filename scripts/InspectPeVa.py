#!/usr/bin/env python3
"""Read bytes or strings at virtual addresses from an on-disk PE image.

The tool is intentionally read-only and dependency-free. It is useful while
following RIP-relative references in disassembly without loading the image or
attaching to a running process.
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
    virtual_address: int
    virtual_size: int
    raw_offset: int
    raw_size: int


def parse_integer(value: str) -> int:
    try:
        return int(value, 0)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"invalid integer: {value}") from exc


def read_layout(image: bytes) -> tuple[int, int, list[Section]]:
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
    size_of_headers = struct.unpack_from("<I", image, optional_offset + 60)[0]

    table_offset = optional_offset + optional_size
    table_end = table_offset + section_count * 40
    if table_end > len(image):
        raise PeError("truncated section table")

    sections: list[Section] = []
    for index in range(section_count):
        offset = table_offset + index * 40
        raw_name = image[offset : offset + 8].split(b"\0", 1)[0]
        name = raw_name.decode("ascii", errors="replace")
        virtual_size = struct.unpack_from("<I", image, offset + 8)[0]
        virtual_address = struct.unpack_from("<I", image, offset + 12)[0]
        raw_size = struct.unpack_from("<I", image, offset + 16)[0]
        raw_offset = struct.unpack_from("<I", image, offset + 20)[0]
        if raw_offset + raw_size > len(image):
            raise PeError(f"section {name!r} extends beyond the file")
        sections.append(
            Section(name, virtual_address, virtual_size, raw_offset, raw_size)
        )
    return image_base, size_of_headers, sections


def va_to_offset(
    va: int,
    image_base: int,
    size_of_headers: int,
    sections: list[Section],
) -> tuple[int, str]:
    if va < image_base:
        raise PeError(f"VA 0x{va:X} is below image base 0x{image_base:X}")
    rva = va - image_base
    if rva < size_of_headers:
        return rva, "headers"
    for section in sections:
        mapped_size = max(section.virtual_size, section.raw_size)
        if section.virtual_address <= rva < section.virtual_address + mapped_size:
            delta = rva - section.virtual_address
            if delta >= section.raw_size:
                raise PeError(
                    f"VA 0x{va:X} lies in the zero-filled tail of {section.name}"
                )
            return section.raw_offset + delta, section.name
    raise PeError(f"VA 0x{va:X} is not mapped by the PE image")


def format_hex(payload: bytes, start_va: int) -> str:
    lines: list[str] = []
    for offset in range(0, len(payload), 16):
        chunk = payload[offset : offset + 16]
        hex_bytes = " ".join(f"{byte:02X}" for byte in chunk)
        printable = "".join(chr(byte) if 32 <= byte < 127 else "." for byte in chunk)
        lines.append(f"{start_va + offset:016X}  {hex_bytes:<47}  {printable}")
    return "\n".join(lines)


def read_c_string(payload: bytes, encoding: str) -> str:
    if encoding == "ascii":
        terminator = payload.find(b"\0")
        raw = payload if terminator < 0 else payload[:terminator]
        return raw.decode("utf-8", errors="replace")

    terminator = len(payload)
    for offset in range(0, len(payload) - 1, 2):
        if payload[offset : offset + 2] == b"\0\0":
            terminator = offset
            break
    return payload[:terminator].decode("utf-16-le", errors="replace")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("addresses", nargs="+", type=parse_integer)
    parser.add_argument("--size", type=parse_integer, default=128)
    parser.add_argument(
        "--format",
        choices=("hex", "ascii", "utf16"),
        default="hex",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.size <= 0:
        raise PeError("size must be positive")
    image = args.image.resolve(strict=True).read_bytes()
    image_base, size_of_headers, sections = read_layout(image)

    for va in args.addresses:
        offset, section = va_to_offset(va, image_base, size_of_headers, sections)
        end = min(len(image), offset + args.size)
        payload = image[offset:end]
        print(f"VA=0x{va:X} RVA=0x{va - image_base:X} FILE=0x{offset:X} SECTION={section}")
        if args.format == "hex":
            print(format_hex(payload, va))
        else:
            print(read_c_string(payload, args.format))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PeError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
