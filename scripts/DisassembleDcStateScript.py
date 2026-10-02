#!/usr/bin/env python3
"""Disassemble TLOU2 DC state scripts for offline, read-only analysis.

Structure layouts follow the public icemesh/dc specification, which is also
the basis of DeepQuantum/dconstruct. Only state-script entries are decoded.
Unknown StringId64 values stay hexadecimal so a missing name is never guessed.
"""

from __future__ import annotations

import argparse
import re
import struct
from pathlib import Path

from FindPeSidStrings import string_id64
from LookupSidBase import read_index, read_name


DC_MAGIC = 0x44433030
STATE_SCRIPT = string_id64(b"state-script")
BLOCKS = ("start", "end", "event", "update", "virtual")
OPCODES = (
    "Return IAdd ISub IMul IDiv FAdd FSub FMul FDiv LoadStaticInt LoadStaticFloat "
    "LoadStaticPointer LoadU16Imm LoadU32 LoadFloat LoadPointer StoreInt StoreFloat "
    "StorePointer LookupInt LookupFloat LookupPointer MoveInt MoveFloat MovePointer "
    "CastInteger CastFloat Call CallFf IEqual IGreaterThan IGreaterThanEqual ILessThan "
    "ILessThanEqual FEqual FGreaterThan FGreaterThanEqual FLessThan FLessThanEqual IMod "
    "FMod IAbs FAbs GoTo Label Branch BranchIf BranchIfNot OpLogNot OpBitAnd OpBitNot "
    "OpBitOr OpBitXor OpBitNor OpLogAnd OpLogOr INeg FNeg LoadParamCnt IAddImm ISubImm "
    "IMulImm IDivImm LoadStaticI32Imm LoadStaticFloatImm LoadStaticPointerImm IntAsh "
    "Move LoadStaticU32Imm LoadStaticI8Imm LoadStaticU8Imm LoadStaticI16Imm "
    "LoadStaticU16Imm LoadStaticI64Imm LoadStaticU64Imm LoadI8 LoadU8 LoadI16 LoadU16 "
    "LoadI32 LoadI64 LoadU64 StoreI8 StoreU8 StoreI16 StoreU16 StoreI32 StoreU32 "
    "StoreI64 StoreU64 INotEqual FNotEqual StoreArray AssertPointer BreakFlag "
    "Breakpoint QEX_InRangeI"
).split()
SYMBOL_SIDS = {"LookupInt", "LookupFloat", "LookupPointer", "LoadStaticU64Imm"}
SYMBOL_INTEGERS = {"LoadStaticI32Imm", "LoadStaticU32Imm", "LoadStaticI64Imm"}
BRANCHES = {"Branch", "BranchIf", "BranchIfNot"}
TOKEN = re.compile(rb"[A-Za-z0-9_./:@?+\-*!<>=%]{2,96}")
LIMIT = 0x10000


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("script", type=Path)
    parser.add_argument("--sidbase", type=Path, help="dconstruct sidbase.bin")
    parser.add_argument(
        "--names-from",
        action="append",
        default=[],
        type=Path,
        help="file whose printable tokens are hashed as SID candidates; repeatable",
    )
    parser.add_argument(
        "--name", action="append", default=[], help="explicit SID text; repeatable"
    )
    return parser.parse_args()


def load_names(sidbase: Path | None, sources: list[Path], names: list[str]) -> dict[int, str]:
    table: dict[int, str] = {}
    if sidbase is not None:
        payload = sidbase.resolve(strict=True).read_bytes()
        hashes, offsets = read_index(payload)
        table.update((value, read_name(payload, offset)) for value, offset in zip(hashes, offsets))
    candidates = {name.encode("ascii") for name in names}
    for source in sources:
        candidates.update(match.group() for match in TOKEN.finditer(source.read_bytes()))
    for candidate in candidates:
        table.setdefault(string_id64(candidate), candidate.decode("ascii"))
    return table


class DcFile:
    def __init__(self, payload: bytes, names: dict[int, str]):
        self.payload = payload
        self.names = names

    def read(self, fmt: str, offset: int):
        size = struct.calcsize(fmt)
        if offset < 0 or offset + size > len(self.payload):
            raise ValueError(f"read of {size} bytes at 0x{offset:X} is outside the file")
        return struct.unpack_from(fmt, self.payload, offset)[0]

    def pointer(self, offset: int) -> int:
        value = self.read("<Q", offset)
        if value >= len(self.payload):
            raise ValueError(f"pointer 0x{value:X} at 0x{offset:X} is outside the file")
        return value

    def count(self, fmt: str, offset: int) -> int:
        value = self.read(fmt, offset)
        if not 0 <= value <= LIMIT:
            raise ValueError(f"implausible count {value} at 0x{offset:X}")
        return value

    def text(self, offset: int) -> str:
        pointer = self.pointer(offset)
        if not pointer:
            return ""
        end = self.payload.find(b"\0", pointer)
        if end < 0:
            raise ValueError(f"string at 0x{pointer:X} is not NUL-terminated")
        return self.payload[pointer:end].decode("latin-1")

    def sid(self, value: int) -> str:
        return self.names.get(value, f"#{value:016X}") if value else "0"


def lambda_lines(dc: DcFile, address: int, indent: str) -> list[str]:
    instructions = dc.pointer(address)
    symbols = dc.pointer(address + 8)
    count = dc.count("<I", address + 0x34)
    lines = [f"{indent}lambda@0x{address:X} instructions={count}"]
    for index in range(count):
        opcode, destination, first, second = (
            dc.read("<B", instructions + 8 * index + k) for k in range(4)
        )
        if opcode >= len(OPCODES):
            raise ValueError(f"unknown opcode 0x{opcode:02X} in lambda 0x{address:X}")
        name = OPCODES[opcode]
        note = ""
        if name in SYMBOL_SIDS:
            note = f" ; {dc.sid(dc.read('<Q', symbols + 8 * first))}"
        elif name in SYMBOL_INTEGERS:
            note = f" ; 0x{dc.read('<Q', symbols + 8 * first):X}"
        elif name == "LoadStaticFloatImm":
            note = f" ; {dc.read('<d', symbols + 8 * first)!r}"
        elif name == "LoadU16Imm":
            note = f" ; {first | second << 8}"
        elif name in BRANCHES:
            note = f" ; -> {destination | second << 8} if r{first}"
        elif name in ("Call", "CallFf"):
            note = f" ; r{destination} = r{first}(argc={second})"
        lines.append(f"{indent}  {index:3d}: {name:20s} {destination:3d} {first:3d} {second:3d}{note}")
    return lines


def disassemble(payload: bytes, names: dict[int, str]) -> list[str]:
    dc = DcFile(payload, names)
    if dc.read("<I", 0) != DC_MAGIC:
        raise ValueError("not a DC00 script")
    entries = dc.pointer(0x18)
    lines: list[str] = []
    for entry_index in range(dc.count("<I", 0x14)):
        entry = entries + 0x18 * entry_index
        entry_type = dc.read("<Q", entry + 8)
        script = dc.pointer(entry + 0x10)
        lines.append(f"ENTRY {dc.sid(dc.read('<Q', entry))} type={dc.sid(entry_type)} @0x{script:X}")
        if entry_type != STATE_SCRIPT:
            continue
        lines.append(f"  initial={dc.sid(dc.read('<Q', script + 0x10))} source={dc.text(script + 0x38)}")
        states = dc.pointer(script + 0x28)
        for state_index in range(dc.count("<h", script + 0x30)):
            state = states + 0x18 * state_index
            blocks = dc.pointer(state + 0x10)
            block_count = dc.count("<q", state + 8)
            lines.append(f" STATE {dc.sid(dc.read('<Q', state))} blocks={block_count}")
            for block_index in range(block_count):
                block = blocks + 0x50 * block_index
                kind = dc.read("<I", block)
                label = BLOCKS[kind] if kind < len(BLOCKS) else f"kind-{kind}"
                event = dc.read("<Q", block + 8)
                group = block + 0x18
                track_count = dc.count("<h", group + 0xA)
                tracks = dc.pointer(group + 0x10)
                heading = f"{label} {dc.sid(event)}" if event else label
                lines.append(f"  ON {heading} tracks={track_count}")
                if dc.pointer(block + 0x10):
                    lines.extend(lambda_lines(dc, dc.pointer(block + 0x10), "    "))
                for track_index in range(track_count):
                    track = tracks + 0x18 * track_index
                    lambda_count = dc.count("<h", track + 0xA)
                    lambdas = dc.pointer(track + 0x10)
                    lines.append(f"   TRACK {dc.sid(dc.read('<Q', track))} lambdas={lambda_count}")
                    for lambda_index in range(lambda_count):
                        lines.extend(lambda_lines(dc, dc.pointer(lambdas + 0x10 * lambda_index), "     "))
    return lines


def main() -> int:
    args = parse_args()
    names = load_names(args.sidbase, args.names_from, args.name)
    for line in disassemble(args.script.resolve(strict=True).read_bytes(), names):
        print(line)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, UnicodeError, ValueError) as exc:
        raise SystemExit(f"ERROR: {exc}") from exc
