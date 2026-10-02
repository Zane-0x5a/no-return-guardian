#!/usr/bin/env python3
"""Disassemble selected x64 PE ranges for offline, read-only analysis."""

from __future__ import annotations

import argparse
from pathlib import Path

from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP

from InspectPeVa import PeError, read_layout, va_to_offset


def parse_range(value: str) -> tuple[int, int]:
    if ":" not in value:
        raise argparse.ArgumentTypeError("range must be START:SIZE")
    start_text, size_text = value.split(":", 1)
    try:
        start = int(start_text, 0)
        size = int(size_text, 0)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(f"invalid range: {value}") from exc
    if size <= 0:
        raise argparse.ArgumentTypeError("range size must be positive")
    return start, size


def mapped_payload(image, image_base, headers, sections, start, size):
    if size <= 0:
        raise PeError("range size must be positive")
    offset, section = va_to_offset(start, image_base, headers, sections)
    end_offset, end_section = va_to_offset(start + size - 1, image_base, headers, sections)
    if end_section != section or end_offset != offset + size - 1:
        raise PeError("range crosses a PE mapping boundary")
    if offset + size > len(image):
        raise PeError("range extends beyond the file")
    return image[offset : offset + size], offset, section


def describe_string(image, image_base, headers, sections, target):
    try:
        offset, section = va_to_offset(target, image_base, headers, sections)
        if section != ".rdata":
            return ""
        available = next(
            item.raw_offset + item.raw_size - offset
            for item in sections
            if item.name == section and item.raw_offset <= offset < item.raw_offset + item.raw_size
        )
        payload = image[offset : offset + min(available, 256)]
        terminator = payload.find(b"\0")
        if terminator < 3:
            return ""
        raw = payload[:terminator]
        if not all(32 <= value < 127 for value in raw):
            return ""
        text = raw.decode("ascii")
        return f"ASCII_CANDIDATE={text!r}"
    except (PeError, StopIteration):
        return ""


def format_target(insn, image_base: int, image=b"", headers=0, sections=()) -> str:
    targets: list[str] = []
    for operand in insn.operands:
        if operand.type == X86_OP_IMM:
            value = operand.imm
            if image_base <= value < image_base + 0x100000000:
                targets.append(f"VA=0x{value:X}")
        elif operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP:
            value = insn.address + insn.size + operand.mem.disp
            targets.append(f"RIP_TARGET=0x{value:X}")
            description = describe_string(image, image_base, headers, sections, value)
            if description:
                targets.append(description)
        elif operand.type == X86_OP_MEM and operand.mem.base == 0 and operand.mem.index == 0:
            value = operand.mem.disp
            if image_base <= value < image_base + 0x100000000:
                targets.append(f"MEM=0x{value:X}")
    return " " + " ".join(targets) if targets else ""


def disassemble(image: bytes, image_base: int, headers, sections, start: int, size: int) -> None:
    payload, offset, section = mapped_payload(image, image_base, headers, sections, start, size)
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    decoder.detail = True
    print(f"RANGE VA=0x{start:X} SIZE=0x{size:X} FILE=0x{offset:X} SECTION={section}")
    decoded_end = start
    for insn in decoder.disasm(payload, start):
        bytes_text = insn.bytes.hex(" ").upper()
        print(f"0x{insn.address:016X}: {bytes_text:<24} {insn.mnemonic:<8} {insn.op_str}{format_target(insn, image_base, image, headers, sections)}")
        decoded_end = insn.address + insn.size
    if decoded_end != start + size:
        raise PeError(f"incomplete decode at VA 0x{decoded_end:X}; range is not fully decoded")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("ranges", nargs="+", type=parse_range, metavar="START:SIZE")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    image = args.image.resolve(strict=True).read_bytes()
    image_base, headers, sections = read_layout(image)
    print(f"IMAGE_BASE=0x{image_base:X}")
    for start, size in args.ranges:
        if start < image_base:
            start += image_base
        disassemble(image, image_base, headers, sections, start, size)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PeError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
