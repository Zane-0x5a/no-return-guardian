#!/usr/bin/env python3
"""Locate x64 PE runtime-function boundaries containing selected VAs/RVAs."""

from __future__ import annotations

import argparse
import struct
from pathlib import Path

from InspectPeVa import PeError, parse_integer, read_layout, va_to_offset


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("addresses", nargs="+", type=parse_integer)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    image = args.image.resolve(strict=True).read_bytes()
    image_base, size_of_headers, sections = read_layout(image)

    pe_offset = struct.unpack_from("<I", image, 0x3C)[0]
    optional_offset = pe_offset + 24
    exception_rva, exception_size = struct.unpack_from(
        "<II", image, optional_offset + 112 + 3 * 8
    )
    exception_va = image_base + exception_rva
    exception_offset, _ = va_to_offset(
        exception_va, image_base, size_of_headers, sections
    )
    entries = []
    for offset in range(exception_offset, exception_offset + exception_size, 12):
        begin, end, unwind = struct.unpack_from("<III", image, offset)
        entries.append((begin, end, unwind))

    for address in args.addresses:
        rva = address - image_base if address >= image_base else address
        matches = [entry for entry in entries if entry[0] <= rva < entry[1]]
        if not matches:
            print(f"ADDRESS_VA=0x{image_base + rva:X} RUNTIME_FUNCTION=none")
            continue
        for begin, end, unwind in matches:
            print(
                f"ADDRESS_VA=0x{image_base + rva:X} "
                f"BEGIN_VA=0x{image_base + begin:X} "
                f"END_VA=0x{image_base + end:X} "
                f"SIZE=0x{end - begin:X} "
                f"UNWIND_VA=0x{image_base + unwind:X}"
            )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PeError, struct.error) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
