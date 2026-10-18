#!/usr/bin/env python3
"""Find the verified build's native sites in another build of the game. Read-only, for development.

A site is relocated only when the code around it is instruction-for-instruction identical to the verified
build apart from RIP-relative displacements and branch targets that leave it. That unit is the site's
runtime function (.pdata) or, for a leaf function without one, the instructions up to its return. The
other build's functions are paired with the verified build's by the sequence of function sizes around the
site, so an update that inserts or resizes code elsewhere still pairs every unchanged function.

Data and read-only addresses are relocated through those displacements: equivalent functions that refer to
an old address must all refer to one new address. A vtable is relocated only when every entry points to the
relocated counterpart of the verified build's entry.

Prints a JSON report with the other build's sections, the relocation of every native address, and the bytes
NativeCodeSignature pins there. Exits 1 when any address cannot be relocated.

    python RelocateNativeBuild.py <verified tlou-ii.exe> <other tlou-ii.exe> [--output report.json]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

import numpy as np
from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP

from InspectPeVa import PeError, read_layout, va_to_offset

sys.path.insert(0, str(Path(__file__).resolve().parent / 'diagnostics'))
import NativeCodeSignature as signature  # noqa: E402

CS_GRP_BRANCH_RELATIVE = 7
# Unconditional ends of a leaf function without unwind data.
LEAF_ENDS = {'ret', 'jmp'}
LEAF_LIMIT = 64
# Sizes of the runtime functions on either side of a site that must line up in the other build.
NEIGHBOURS = 48
# How far (in runtime functions) an update may have moved a site.
REACH = 8000
# How many equivalent references must agree on a data address's new location.
WITNESSES = 3
# A data address the code reaches only through a table starting this far below it (a service slot).
TABLE_REACH = 0x100


class RelocationError(RuntimeError):
    pass


class Image:
    def __init__(self, path: Path):
        self.path = path
        self.data = path.read_bytes()
        self.sha256 = hashlib.sha256(self.data).hexdigest().upper()
        self.preferred, self.headers, self.sections = read_layout(self.data)
        self.text = self.section('.text')
        pe = struct.unpack_from('<I', self.data, 0x3C)[0]
        exception_rva, exception_size = struct.unpack_from('<II', self.data, pe + 24 + 112 + 3 * 8)
        offset = self.offset(exception_rva)
        table = np.frombuffer(self.data, dtype='<u4', count=exception_size // 4, offset=offset).reshape(-1, 3)
        self.begins = table[:, 0].astype(np.int64)
        self.ends = table[:, 1].astype(np.int64)
        self.sizes = self.ends - self.begins

    def section(self, name):
        for section in self.sections:
            if section.name == name:
                return section
        raise RelocationError(f'{self.path.name} has no {name} section')

    def section_of(self, rva):
        for section in self.sections:
            if section.virtual_address <= rva < section.virtual_address + max(section.virtual_size, section.raw_size):
                return section.name
        return None

    def offset(self, rva):
        offset, _ = va_to_offset(self.preferred + rva, self.preferred, self.headers, self.sections)
        return offset

    def read(self, rva, size):
        offset = self.offset(rva)
        return self.data[offset:offset + size]

    def function_index(self, rva):
        """The runtime function containing rva, or None."""
        index = int(np.searchsorted(self.begins, rva, side='right')) - 1
        if index >= 0 and self.begins[index] <= rva < self.ends[index]:
            return index
        return None


class Disassembler:
    def __init__(self):
        self.cs = Cs(CS_ARCH_X86, CS_MODE_64)
        self.cs.detail = True

    def unit(self, image, begin, end):
        """Instructions of [begin, end) and the bytes after the last one capstone could decode."""
        code = image.read(begin, end - begin)
        instructions = list(self.cs.disasm(code, begin))
        decoded = sum(instruction.size for instruction in instructions)
        return instructions, code[decoded:]

    def leaf(self, image, begin):
        """A leaf function without unwind data: its instructions up to the first unconditional exit."""
        code = image.read(begin, LEAF_LIMIT * 15)
        instructions = []
        for instruction in self.cs.disasm(code, begin):
            instructions.append(instruction)
            if instruction.mnemonic in LEAF_ENDS:
                return instructions[-1].address + instructions[-1].size
            if len(instructions) >= LEAF_LIMIT:
                break
        raise RelocationError(f'leaf function at {begin:#x} has no return within {LEAF_LIMIT} instructions')


def references(instruction):
    """(byte offset, size, target) of every RIP-relative operand and relative branch target."""
    result = []
    for operand in instruction.operands:
        if operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP:
            result.append((instruction.disp_offset, instruction.disp_size,
                           instruction.address + instruction.size + operand.mem.disp))
        elif operand.type == X86_OP_IMM and CS_GRP_BRANCH_RELATIVE in instruction.groups:
            result.append((instruction.imm_offset, instruction.imm_size, operand.imm & 0xFFFFFFFFFFFFFFFF))
    return result


def equivalent(disassembler, old, old_unit, new, new_unit):
    """Mapping old target -> new target when both units are the same code, else None.

    Bytes must be identical except RIP-relative displacements and branch targets that leave the unit; a
    branch inside the unit must land at the same offset. Bytes capstone cannot decode (a jump table) must
    be identical or image-relative offsets to the same place in the unit.
    """
    (old_begin, old_end), (new_begin, new_end) = old_unit, new_unit
    if old_end - old_begin != new_end - new_begin:
        return None
    old_code, old_tail = disassembler.unit(old, old_begin, old_end)
    new_code, new_tail = disassembler.unit(new, new_begin, new_end)
    if len(old_code) != len(new_code) or len(old_tail) != len(new_tail):
        return None
    mapping = {}
    for a, b in zip(old_code, new_code):
        if a.size != b.size or a.mnemonic != b.mnemonic:
            return None
        a_bytes, b_bytes = bytearray(a.bytes), bytearray(b.bytes)
        a_refs, b_refs = references(a), references(b)
        if [(offset, size) for offset, size, _ in a_refs] != [(offset, size) for offset, size, _ in b_refs]:
            return None
        for (offset, size, a_target), (_, _, b_target) in zip(a_refs, b_refs):
            if old_begin <= a_target < old_end:
                if a_target - old_begin != b_target - new_begin:
                    return None
                continue
            a_bytes[offset:offset + size] = bytes(size)
            b_bytes[offset:offset + size] = bytes(size)
            if mapping.setdefault(a_target, b_target) != b_target:
                return None
        if a_bytes != b_bytes:
            return None
    if old_tail != new_tail:
        if len(old_tail) % 4:
            return None
        for (a,), (b,) in zip(struct.iter_unpack('<I', old_tail), struct.iter_unpack('<I', new_tail)):
            if a != b and not (old_begin <= a < old_end and a - old_begin == b - new_begin):
                return None
    return mapping


class Relocator:
    def __init__(self, old: Image, new: Image):
        self.old, self.new = old, new
        self.disassembler = Disassembler()
        self.units = {}  # old unit begin -> (new unit begin, old target -> new target), verified equivalent
        self._scan = None

    def shifts(self, index):
        """Candidate index shifts for the old runtime function `index`, best aligned first."""
        old_sizes, new_sizes = self.old.sizes, self.new.sizes
        low, high = max(0, index - NEIGHBOURS), min(len(old_sizes), index + NEIGHBOURS + 1)
        window = old_sizes[low:high]
        start = max(0, low - REACH)
        stop = min(len(new_sizes) - len(window), low + REACH)
        if stop < start:
            return []
        views = np.lib.stride_tricks.sliding_window_view(new_sizes[start:stop + len(window)], len(window))
        scores = (views == window).sum(axis=1)
        order = np.argsort(-scores, kind='stable')[:8]
        return [int(start + k - low) for k in order if scores[k] >= len(window) // 2]

    def match_function(self, index):
        """The verified-equivalent runtime function of the other build for old function `index`."""
        old_unit = (int(self.old.begins[index]), int(self.old.ends[index]))
        if old_unit[0] in self.units:
            begin = self.units[old_unit[0]][0]
            return begin, begin + old_unit[1] - old_unit[0]
        for shift in self.shifts(index):
            for nearby in (0, -1, 1, -2, 2, -3, 3):
                candidate = index + shift + nearby
                if not 0 <= candidate < len(self.new.begins):
                    continue
                new_unit = (int(self.new.begins[candidate]), int(self.new.ends[candidate]))
                mapping = equivalent(self.disassembler, self.old, old_unit, self.new, new_unit)
                if mapping is not None:
                    self.units[old_unit[0]] = (new_unit[0], mapping)
                    return new_unit
        return None

    def relocate_code(self, rva):
        """(new rva, unit description) of a code address, or raise."""
        index = self.old.function_index(rva)
        if index is not None:
            new_unit = self.match_function(index)
            if new_unit is None:
                raise RelocationError(f'function {int(self.old.begins[index]):#x} containing {rva:#x} '
                                      'has no identical counterpart')
            old_begin = int(self.old.begins[index])
            target = new_unit[0] + rva - old_begin
            self.require_boundary(old_begin, int(self.old.ends[index]), rva, new_unit, target)
            return target, {'unit': 'function', 'begin': hex(old_begin), 'newBegin': hex(new_unit[0])}
        return self.relocate_leaf(rva)

    def require_boundary(self, old_begin, old_end, rva, new_unit, target):
        instructions, _ = self.disassembler.unit(self.old, old_begin, old_end)
        if rva not in {instruction.address for instruction in instructions}:
            raise RelocationError(f'{rva:#x} is not an instruction boundary in the verified build')
        instructions, _ = self.disassembler.unit(self.new, *new_unit)
        if target not in {instruction.address for instruction in instructions}:
            raise RelocationError(f'{target:#x} is not an instruction boundary in the other build')

    def relocate_leaf(self, rva):
        """A leaf function between two runtime functions moves with one of them."""
        index = int(np.searchsorted(self.old.begins, rva, side='right')) - 1
        old_end = self.disassembler.leaf(self.old, rva)
        deltas = []
        for neighbour in (index, index + 1):
            if 0 <= neighbour < len(self.old.begins):
                new_unit = self.match_function(neighbour)
                if new_unit is not None:
                    deltas.append(new_unit[0] - int(self.old.begins[neighbour]))
        for delta in dict.fromkeys(deltas):
            target = rva + delta
            try:
                new_end = self.disassembler.leaf(self.new, target)
            except RelocationError:
                continue
            mapping = equivalent(self.disassembler, self.old, (rva, old_end), self.new, (target, new_end))
            if mapping is not None:
                self.units[rva] = (target, mapping)
                return target, {'unit': 'leaf', 'begin': hex(rva), 'newBegin': hex(target)}
        raise RelocationError(f'leaf function {rva:#x} has no identical counterpart next to its neighbours')

    def scan(self):
        """Every RIP-relative displacement in the verified build's code: (instruction end rva, target)."""
        if self._scan is None:
            text = self.old.text
            raw = np.frombuffer(self.old.read(text.virtual_address, text.raw_size), dtype=np.uint8)
            words = (raw[:-3].astype(np.int64) | raw[1:-2].astype(np.int64) << 8 |
                     raw[2:-1].astype(np.int64) << 16 | raw[3:].astype(np.int64) << 24)
            self._scan = (text.virtual_address + np.arange(len(words), dtype=np.int64),
                          np.where(words >= 1 << 31, words - (1 << 32), words))
        return self._scan

    def referrers(self, rva):
        """Runtime functions of the verified build that may refer to rva or to a table that holds it, those
        referring nearest first."""
        positions, displacements = self.scan()
        found = {}
        for immediate in (0, 1, 2, 4):
            ends = positions + 4 + immediate + displacements
            hits = np.nonzero((ends > rva - TABLE_REACH) & (ends <= rva))[0]
            for hit in hits.tolist():
                index = self.old.function_index(int(positions[hit]))
                if index is not None:
                    distance = rva - int(ends[hit])
                    found[index] = min(distance, found.get(index, distance))
        # Small functions first: they are cheapest to compare and least likely to have changed.
        return sorted(found, key=lambda index: (found[index], int(self.old.sizes[index])))

    def relocate_data(self, rva):
        """New location of a data address, agreed by up to WITNESSES equivalent functions that refer to it or
        to the nearest table below it."""
        votes, nearest = {}, None
        for index in self.referrers(rva):
            if self.match_function(index) is None:
                continue
            begin = int(self.old.begins[index])
            targets = [(rva - old, new - old) for old, new in self.units[begin][1].items()
                       if rva - TABLE_REACH < old <= rva]
            if not targets:
                continue
            distance, delta = min(targets)
            if nearest is not None and distance > nearest:
                break
            nearest = distance
            votes.setdefault(delta, set()).add(begin)
            if len(votes) > 1:
                raise RelocationError(f'equivalent functions disagree where {rva:#x} moved: '
                                      + ', '.join(hex(rva + choice) for choice in votes))
            if len(votes[delta]) >= WITNESSES:
                break
        if not votes:
            raise RelocationError(f'no equivalent function refers to {rva:#x}')
        (delta, witnesses), = votes.items()
        return rva + delta, {'unit': 'data', 'via': hex(rva - nearest),
                             'witnesses': [hex(witness) for witness in sorted(witnesses)]}

    def relocate_vtable(self, rva, size):
        target, detail = self.relocate_data(rva)
        expected = bytearray()
        for (entry,) in struct.iter_unpack('<Q', self.old.read(rva, size)):
            function, _ = self.relocate_code(entry - self.old.preferred)
            expected += struct.pack('<Q', self.new.preferred + function)
        if self.new.read(target, size) != bytes(expected):
            raise RelocationError(f'vtable {rva:#x} moved to {target:#x}, but its entries are not the relocated ones')
        return target, {**detail, 'unit': 'vtable'}


def relocate(old: Image, new: Image):
    relocator = Relocator(old, new)
    sites, failures = {}, []

    def attempt(rva, kind, action):
        try:
            target, detail = action()
            sites[rva] = {'kind': kind, 'rva': hex(target), 'delta': target - rva, **detail}
        except RelocationError as error:
            failures.append({'kind': kind, 'canonical': hex(rva), 'reason': str(error)})

    vtables = dict(signature.VTABLES)
    for rva in sorted(signature.code_addresses()):
        attempt(rva, 'code', lambda rva=rva: relocator.relocate_code(rva))
    for rva in sorted(signature.DATA_ADDRESSES):
        if rva in vtables:
            attempt(rva, 'vtable', lambda rva=rva: relocator.relocate_vtable(rva, vtables[rva]))
        else:
            attempt(rva, 'data', lambda rva=rva: relocator.relocate_data(rva))

    relocation = {rva: int(site['rva'], 16) for rva, site in sites.items()}
    pinned = {}
    for rva, expected in signature.PINNED.items():
        # A pinned range starts at, or a few bytes before, a relocated address in the same unit.
        anchor = min((address for address in relocation if address >= rva), default=None)
        if anchor is None or anchor - rva > 16 or old.read(rva, len(expected) // 2).hex() != expected:
            failures.append({'kind': 'pin', 'canonical': hex(rva), 'reason': 'no relocated address anchors it'})
            continue
        target = relocation[anchor] - (anchor - rva)
        pinned[target] = new.read(target, len(expected) // 2).hex()

    return {
        'verified': {'path': str(old.path), 'sha256': old.sha256},
        'build': {'path': str(new.path), 'sha256': new.sha256, 'size': len(new.data),
                  'sections': {section.name: hex(section.virtual_address) for section in new.sections}},
        'relocation': {hex(rva): hex(target) for rva, target in sorted(relocation.items())},
        'pinned': {hex(rva): data for rva, data in sorted(pinned.items())},
        'sites': {hex(rva): site for rva, site in sorted(sites.items())},
        'failures': failures,
    }


def table(report, name):
    """The other build's entry for NativeCodeSignature.BUILDS."""
    lines = [f"# {report['build']['sha256']} ({report['build']['size']} bytes), relocated from the verified build "
             'by RelocateNativeBuild.py.',
             f'Build({name!r},',
             '      {' + ', '.join(f'{section!r}: {start}' for section, start in report['build']['sections'].items()) + '},',
             '      {']
    lines += [f"          {rva}: '{data}'," for rva, data in report['pinned'].items()]
    lines += ['      },', '      {']
    lines += [f'          {canonical}: {rva},' for canonical, rva in report['relocation'].items()]
    lines += ['      })']
    return '\n'.join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('verified', type=Path)
    parser.add_argument('other', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--name', help="print the other build's NativeCodeSignature entry under this name")
    args = parser.parse_args(argv)
    old, new = Image(args.verified), Image(args.other)
    if old.sha256 != signature.VERIFIED_SHA256:
        raise RelocationError('the first executable must be the verified build')
    report = relocate(old, new)
    text = json.dumps(report, indent=2)
    if args.output:
        args.output.write_text(text + '\n', encoding='utf-8')
    elif not args.name:
        print(text)
    if args.name and not report['failures']:
        print(table(report, args.name))
    for canonical, site in report['sites'].items():
        if site.get('via', canonical) != canonical:
            print(f"NOTE data {canonical} has no equivalent reference of its own; it moved with {site['via']}",
                  file=sys.stderr)
    for failure in report['failures']:
        print(f"FAILED {failure['kind']} {failure['canonical']}: {failure['reason']}", file=sys.stderr)
    print(f"{len(report['relocation'])} addresses relocated, {len(report['failures'])} failed", file=sys.stderr)
    return 1 if report['failures'] else 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, PeError, RelocationError) as error:
        raise SystemExit(f'ERROR: {error}') from error
