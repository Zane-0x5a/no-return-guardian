#!/usr/bin/env python3
"""Find common x64 RIP-relative references to selected PE addresses.

The scanner recognizes the usual one-byte LEA/MOV/CMP encodings with an
optional REX prefix. Results are candidates and must be confirmed in a real
disassembler because scanning every byte does not establish instruction
boundaries.
"""

from __future__ import annotations

import argparse
import struct
from pathlib import Path

from FindPeDirectCalls import PeError, parse_integer, read_sections


OPCODES = {
    0x39: ("cmp-rm-r", 0),
    0x3B: ("cmp-r-rm", 0),
    0x80: ("group-rm8-imm8", 1),
    0x81: ("group-rm-imm32", 4),
    0x83: ("group-rm-imm8", 1),
    0x88: ("mov-rm8-r8", 0),
    0x89: ("mov-rm-r", 0),
    0x8A: ("mov-r8-rm8", 0),
    0x8B: ("mov-r-rm", 0),
    0x8D: ("lea", 0),
    0xC6: ("mov-rm8-imm8", 1),
    0xC7: ("mov-rm-imm32", 4),
}


def find_references(
    image: bytes,
    image_base: int,
    sections: list,
    target_rvas: set[int],
) -> None:
    counts = {target: 0 for target in target_rvas}
    for section in sections:
        if not section.executable:
            continue
        payload = memoryview(image)[
            section.raw_offset : section.raw_offset + section.raw_size
        ]
        for offset in range(max(0, len(payload) - 11)):
            first = payload[offset]
            if 0x40 <= first <= 0x4F:
                prefix_size = 1
                opcode = payload[offset + 1]
            else:
                prefix_size = 0
                opcode = first
                if offset > 0 and 0x40 <= payload[offset - 1] <= 0x4F:
                    continue
            if opcode not in OPCODES:
                continue

            operation, immediate_size = OPCODES[opcode]

            modrm_offset = offset + prefix_size + 1
            modrm = payload[modrm_offset]
            if modrm & 0xC7 != 0x05:
                continue

            displacement_offset = modrm_offset + 1
            displacement = struct.unpack_from("<i", payload, displacement_offset)[0]
            instruction_size = prefix_size + 6 + immediate_size
            source_rva = section.rva + offset
            target_rva = (source_rva + instruction_size + displacement) & 0xFFFFFFFFFFFFFFFF
            if target_rva not in target_rvas:
                continue

            counts[target_rva] += 1
            rex = f"0x{first:02X}" if prefix_size else "none"
            print(
                f"TARGET_VA=0x{image_base + target_rva:X} "
                f"SOURCE_VA=0x{image_base + source_rva:X} "
                f"SOURCE_RVA=0x{source_rva:X} OPCODE={operation} "
                f"REX={rex} SECTION={section.name}"
            )

    for target in sorted(target_rvas):
        print(
            f"SUMMARY TARGET_VA=0x{image_base + target:X} "
            f"REFERENCES={counts[target]}"
        )


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
    image_base, sections = read_sections(image)
    target_rvas = {
        target - image_base if target >= image_base else target for target in args.targets
    }
    print(f"IMAGE_BASE=0x{image_base:X}")
    find_references(image, image_base, sections, target_rvas)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PeError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
