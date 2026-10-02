#!/usr/bin/env python3
"""Locate PE imports and direct RIP-relative calls through their IAT slots.

The scanner is intentionally read-only and dependency-free. It parses the PE
import directory, reports the exact IAT address for selected symbols, and scans
executable sections for x86-64 ``call [rip+disp32]`` / ``jmp [rip+disp32]``
sites that reference those slots. Results are exact for those encodings; callers
that first load an IAT entry into a register require a separate RIP-reference
scan.
"""

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path

from FindPeDirectCalls import PeError, Section, read_sections


@dataclass(frozen=True)
class Import:
    module: str
    name: str
    iat_rva: int


def read_c_string(image: bytes, offset: int) -> str:
    if not 0 <= offset < len(image):
        raise PeError(f"string offset 0x{offset:X} is outside the image")
    end = image.find(b"\0", offset)
    if end < 0:
        raise PeError(f"unterminated string at file offset 0x{offset:X}")
    return image[offset:end].decode("ascii", errors="replace")


def rva_to_offset(rva: int, image: bytes, sections: list[Section]) -> int:
    if rva < 0:
        raise PeError(f"negative RVA 0x{rva:X}")
    for section in sections:
        if section.rva <= rva < section.rva + section.raw_size:
            return section.raw_offset + (rva - section.rva)
    if rva < len(image):
        return rva
    raise PeError(f"RVA 0x{rva:X} is not backed by file data")


def read_import_directory(image: bytes) -> tuple[int, int, int]:
    if len(image) < 0x40 or image[:2] != b"MZ":
        raise PeError("not a DOS/PE image")
    pe_offset = struct.unpack_from("<I", image, 0x3C)[0]
    if pe_offset + 24 > len(image) or image[pe_offset : pe_offset + 4] != b"PE\0\0":
        raise PeError("missing PE signature")

    coff_offset = pe_offset + 4
    optional_size = struct.unpack_from("<H", image, coff_offset + 16)[0]
    optional_offset = coff_offset + 20
    if optional_offset + optional_size > len(image):
        raise PeError("truncated optional header")

    magic = struct.unpack_from("<H", image, optional_offset)[0]
    if magic == 0x20B:
        pointer_size = 8
        directory_offset = optional_offset + 112
    elif magic == 0x10B:
        pointer_size = 4
        directory_offset = optional_offset + 96
    else:
        raise PeError(f"unsupported optional-header magic: 0x{magic:04X}")

    import_entry = directory_offset + 8  # IMAGE_DIRECTORY_ENTRY_IMPORT
    if import_entry + 8 > optional_offset + optional_size:
        raise PeError("optional header has no import directory")
    import_rva, import_size = struct.unpack_from("<II", image, import_entry)
    return pointer_size, import_rva, import_size


def read_imports(image: bytes, sections: list[Section]) -> list[Import]:
    pointer_size, import_rva, import_size = read_import_directory(image)
    if import_rva == 0 or import_size == 0:
        return []

    descriptor_offset = rva_to_offset(import_rva, image, sections)
    imports: list[Import] = []
    ordinal_mask = 1 << (pointer_size * 8 - 1)
    thunk_format = "<Q" if pointer_size == 8 else "<I"

    for descriptor_index in range(import_size // 20 + 1):
        offset = descriptor_offset + descriptor_index * 20
        if offset + 20 > len(image):
            raise PeError("truncated import descriptor table")
        original_thunk, _, _, name_rva, first_thunk = struct.unpack_from(
            "<IIIII", image, offset
        )
        if original_thunk == name_rva == first_thunk == 0:
            break
        if name_rva == 0 or first_thunk == 0:
            raise PeError(f"malformed import descriptor {descriptor_index}")

        module = read_c_string(image, rva_to_offset(name_rva, image, sections))
        lookup_rva = original_thunk or first_thunk
        lookup_offset = rva_to_offset(lookup_rva, image, sections)

        for thunk_index in range(1_000_000):
            thunk_offset = lookup_offset + thunk_index * pointer_size
            if thunk_offset + pointer_size > len(image):
                raise PeError(f"truncated thunk table for {module}")
            value = struct.unpack_from(thunk_format, image, thunk_offset)[0]
            if value == 0:
                break
            if value & ordinal_mask:
                name = f"ordinal_{value & 0xFFFF}"
            else:
                hint_name_offset = rva_to_offset(value, image, sections)
                name = read_c_string(image, hint_name_offset + 2)
            imports.append(
                Import(module=module, name=name, iat_rva=first_thunk + thunk_index * pointer_size)
            )
        else:
            raise PeError(f"unterminated thunk table for {module}")
    return imports


def find_indirect_branches(
    image: bytes,
    image_base: int,
    sections: list[Section],
    selected: list[Import],
) -> dict[int, list[tuple[int, str]]]:
    targets = {item.iat_rva for item in selected}
    results: dict[int, list[tuple[int, str]]] = {target: [] for target in targets}
    for section in sections:
        if not section.executable:
            continue
        payload = memoryview(image)[
            section.raw_offset : section.raw_offset + section.raw_size
        ]
        for offset in range(max(0, len(payload) - 6)):
            if payload[offset] != 0xFF or payload[offset + 1] not in (0x15, 0x25):
                continue
            displacement = struct.unpack_from("<i", payload, offset + 2)[0]
            source_rva = section.rva + offset
            target_rva = (source_rva + 6 + displacement) & 0xFFFFFFFFFFFFFFFF
            if target_rva in targets:
                operation = "call" if payload[offset + 1] == 0x15 else "jmp"
                results[target_rva].append((image_base + source_rva, operation))
    return results


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument(
        "symbols",
        nargs="+",
        help="case-insensitive import names; use '*' to list every import",
    )
    parser.add_argument(
        "--module",
        help="optional case-insensitive module filter, for example KERNEL32.dll",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    image = args.image.resolve(strict=True).read_bytes()
    image_base, sections = read_sections(image)
    imports = read_imports(image, sections)

    module_filter = args.module.casefold() if args.module else None
    symbol_filters = {symbol.casefold() for symbol in args.symbols}
    selected = [
        item
        for item in imports
        if (module_filter is None or item.module.casefold() == module_filter)
        and ("*" in symbol_filters or item.name.casefold() in symbol_filters)
    ]
    selected.sort(key=lambda item: (item.module.casefold(), item.iat_rva))
    branches = find_indirect_branches(image, image_base, sections, selected)

    for item in selected:
        sites = branches[item.iat_rva]
        print(
            f"IMPORT={item.module}!{item.name} "
            f"IAT_VA=0x{image_base + item.iat_rva:X} BRANCHES={len(sites)}"
        )
        for source_va, operation in sites:
            print(f"  {operation.upper()}_VA=0x{source_va:X}")

    missing = sorted(
        symbol for symbol in args.symbols
        if symbol != "*" and not any(item.name.casefold() == symbol.casefold() for item in selected)
    )
    for symbol in missing:
        print(f"MISSING={symbol}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PeError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
