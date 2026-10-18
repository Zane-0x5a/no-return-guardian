#!/usr/bin/env python3
"""Find the verified build's native sites in another build of the game. Read-only, for development.

A site is relocated only when the code around it is instruction-for-instruction identical to the verified
build apart from RIP-relative displacements and branch targets that leave it. That unit is the site's
runtime function (.pdata) or, for a leaf function without one, the instructions up to its return. The
other build's functions are paired with the verified build's by the sequence of function sizes around the
site, so an update that inserts or resizes code elsewhere still pairs every unchanged function; a function
whose neighbours changed too is found where the equivalent functions that call it now call.

A function the update changed is relocated only when NATIVE_BUILDS records that someone read the change and
found it does not touch what the native scripts rely on there. The tool then still confirms that the
recorded counterpart is that function: equivalent functions refer to it, it holds the same slot of a pointer
table, or it sits next to an equivalent function at the same distance. In a changed function only its entry
and the return addresses of its calls are relocated, the latter where it makes one call to the counterpart
of the callee the verified build's return address follows.

Data and read-only addresses are relocated through those displacements: equivalent functions that refer to
an old address must all refer to one new address. A vtable is relocated only when every entry points to the
relocated counterpart of the verified build's entry; when the code that installs it changed, the one table
holding those entries is it.

Only the addresses the product's native modes use must relocate (NativeCodeSignature.product_addresses);
the rest serve experiments that run on the verified build alone and are reported as omitted.

Prints a JSON report with the other build's sections, the relocation of every native address, and the bytes
NativeCodeSignature pins there. Exits 1 when a product address cannot be relocated.

    python RelocateNativeBuild.py <verified tlou-ii.exe> <other tlou-ii.exe> [--output report.json] [--name NAME]
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
# Slots on either side of a function pointer that confirm a pointer table moved with it, and how many must.
TABLE_NEIGHBOURS = 8
TABLE_WITNESSES = 4
# Changed functions whose change was reviewed, per build: verified-build function -> counterpart and reason.
NATIVE_BUILDS = Path(__file__).resolve().parent / 'native-builds.json'


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


def pointers(image, rva):
    """Aligned 8-byte pointers to rva in the image's read-only data."""
    section = image.section('.rdata')
    data = image.read(section.virtual_address, section.raw_size)
    needle = struct.pack('<Q', image.preferred + rva)
    found, position = [], data.find(needle)
    while position >= 0:
        if position % 8 == 0:
            found.append(section.virtual_address + position)
        position = data.find(needle, position + 1)
    return found


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
    def __init__(self, old: Image, new: Image, reviewed=None):
        self.old, self.new = old, new
        self.disassembler = Disassembler()
        self.units = {}  # old unit begin -> (new unit begin, old target -> new target), verified equivalent
        self.reviewed = reviewed or {}  # old function begin -> (new function begin, reason)
        self._scan = None
        self._referred = {}

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

    def match_function(self, index, through_referrers=True):
        """The verified-equivalent runtime function of the other build for old function `index`."""
        old_unit = (int(self.old.begins[index]), int(self.old.ends[index]))
        if old_unit[0] in self.units:
            begin = self.units[old_unit[0]][0]
            return begin, begin + old_unit[1] - old_unit[0]
        candidates = [index + shift + nearby for shift in self.shifts(index) for nearby in (0, -1, 1, -2, 2, -3, 3)]
        if through_referrers:
            candidates += [self.new.function_index(begin) for begin in self.referred(old_unit[0])]
        for candidate in candidates:
            if candidate is None or not 0 <= candidate < len(self.new.begins):
                continue
            new_unit = (int(self.new.begins[candidate]), int(self.new.ends[candidate]))
            mapping = equivalent(self.disassembler, self.old, old_unit, self.new, new_unit)
            if mapping is not None:
                self.units[old_unit[0]] = (new_unit[0], mapping)
                return new_unit
        return None

    def referrers_of(self, rva):
        """Runtime functions of the verified build that may refer to rva exactly (call, jump or address)."""
        positions, targets = self.scan()
        found = {}
        for immediate in (0, 1, 2, 4):
            for hit in np.flatnonzero(targets == rva - immediate).tolist():
                index = self.old.function_index(int(positions[hit]))
                if index is not None:
                    found[index] = None
        return list(found)

    def referred(self, rva):
        """Where the other build's counterparts of the size-paired functions referring to rva refer instead:
        new address -> those functions."""
        if rva in self._referred:
            return self._referred[rva]
        targets = self._referred[rva] = {}
        for index in self.referrers_of(rva):
            if self.match_function(index, through_referrers=False) is None:
                continue
            begin = int(self.old.begins[index])
            mapping = self.units[begin][1]
            if rva in mapping:
                targets.setdefault(mapping[rva], []).append(begin)
        return targets

    def reviewed_unit(self, index):
        """The other build's counterpart of a changed function whose change was reviewed, and what confirms it
        is that function."""
        old_begin = int(self.old.begins[index])
        new_begin, _ = self.reviewed[old_begin]
        candidate = self.new.function_index(new_begin)
        if candidate is None or int(self.new.begins[candidate]) != new_begin:
            raise RelocationError(f'the reviewed counterpart {new_begin:#x} of {old_begin:#x} is not a runtime function')
        referred = self.referred(old_begin)
        if referred:
            if set(referred) != {new_begin}:
                raise RelocationError(f'equivalent functions refer to {old_begin:#x} as '
                                      + ', '.join(hex(target) for target in referred) + f', not {new_begin:#x}')
            identity = 'referred to by ' + ', '.join(hex(begin) for begin in referred[new_begin][:3])
        else:
            identity = self.table_slot(old_begin, new_begin) or self.adjacent(index, new_begin)
        if identity is None:
            raise RelocationError(f'nothing confirms {new_begin:#x} is the counterpart of {old_begin:#x}')
        return (new_begin, int(self.new.ends[candidate])), identity

    def table_slot(self, old_begin, new_begin):
        """A pointer table slot that holds old_begin and moved, with its neighbours, to one holding new_begin."""
        for old_slot in pointers(self.old, old_begin):
            neighbours = []
            for delta in range(-TABLE_NEIGHBOURS * 8, TABLE_NEIGHBOURS * 8 + 8, 8):
                (value,) = struct.unpack('<Q', self.old.read(old_slot + delta, 8))
                index = self.old.function_index(value - self.old.preferred)
                if delta and index is not None and int(self.old.begins[index]) == value - self.old.preferred:
                    unit = self.match_function(index, through_referrers=False)
                    if unit is not None:
                        neighbours.append((delta, struct.pack('<Q', self.new.preferred + unit[0])))
            if len(neighbours) < TABLE_WITNESSES:
                continue
            for new_slot in pointers(self.new, new_begin):
                if all(self.new.read(new_slot + delta, 8) == data for delta, data in neighbours):
                    return f'slot {old_slot:#x} of a table that moved to {new_slot:#x} with {len(neighbours)} neighbours'
        return None

    def adjacent(self, index, new_begin):
        """An equivalent runtime function right before or after it, moved by the same distance."""
        delta = new_begin - int(self.old.begins[index])
        for neighbour in (index - 1, index + 1):
            if 0 <= neighbour < len(self.old.begins):
                unit = self.match_function(neighbour)
                if unit is not None and unit[0] - int(self.old.begins[neighbour]) == delta:
                    return f'next to {int(self.old.begins[neighbour]):#x}, moved with it'
        return None

    def relocate_code(self, rva):
        """(new rva, unit description) of a code address, or raise."""
        index = self.old.function_index(rva)
        if index is None:
            return self.relocate_leaf(rva)
        old_begin = int(self.old.begins[index])
        new_unit = self.match_function(index)
        if new_unit is not None:
            target = new_unit[0] + rva - old_begin
            self.require_boundary(old_begin, int(self.old.ends[index]), rva, new_unit, target)
            return target, {'unit': 'function', 'begin': hex(old_begin), 'newBegin': hex(new_unit[0])}
        if old_begin not in self.reviewed:
            raise RelocationError(f'function {old_begin:#x} containing {rva:#x} has no identical counterpart')
        new_unit, identity = self.reviewed_unit(index)
        detail = {'unit': 'reviewed', 'begin': hex(old_begin), 'newBegin': hex(new_unit[0]), 'identity': identity,
                  'reason': self.reviewed[old_begin][1]}
        return (new_unit[0] if rva == old_begin else self.return_address(rva, new_unit)), detail

    def return_address(self, rva, new_unit):
        """In a changed function, the return address of its one call to the counterpart of the callee rva
        follows in the verified build."""
        call = self.old.read(rva - 5, 5)
        if call[0] != 0xE8:
            raise RelocationError(f'{rva:#x} is inside a changed function and follows no direct call')
        callee = rva + struct.unpack('<i', call[1:])[0]
        target, _ = self.relocate_code(callee)
        instructions, _ = self.disassembler.unit(self.new, *new_unit)
        ends = [instruction.address + instruction.size for instruction in instructions
                if instruction.mnemonic == 'call' and instruction.operands[0].type == X86_OP_IMM
                and instruction.operands[0].imm == target]
        if len(ends) != 1:
            raise RelocationError(f'{rva:#x} follows a call to {callee:#x}, which the changed function makes '
                                  f'{len(ends)} times in the other build')
        return ends[0]

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
        """Every 4 bytes of the verified build's code read as a RIP-relative displacement: (position, target
        when the displacement ends its instruction). An immediate of k bytes after it moves the target by k."""
        if self._scan is None:
            text = self.old.text
            raw = np.frombuffer(self.old.read(text.virtual_address, text.raw_size), dtype=np.uint8)
            words = (raw[:-3].astype(np.int64) | raw[1:-2].astype(np.int64) << 8 |
                     raw[2:-1].astype(np.int64) << 16 | raw[3:].astype(np.int64) << 24)
            positions = text.virtual_address + np.arange(len(words), dtype=np.int64)
            self._scan = (positions, positions + 4 + np.where(words >= 1 << 31, words - (1 << 32), words))
        return self._scan

    def referrers(self, rva):
        """Runtime functions of the verified build that may refer to rva or to a table that holds it, those
        referring nearest first."""
        positions, targets = self.scan()
        found = {}
        for immediate in (0, 1, 2, 4):
            ends = targets + immediate
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
            if self.match_function(index, through_referrers=False) is None:
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
        expected = bytearray()
        for (entry,) in struct.iter_unpack('<Q', self.old.read(rva, size)):
            function, _ = self.relocate_code(entry - self.old.preferred)
            expected += struct.pack('<Q', self.new.preferred + function)
        try:
            target, detail = self.relocate_data(rva)
        except RelocationError:
            # The code that installs it changed; the one table holding the relocated entries is it.
            tables = [start for start in pointers(self.new, struct.unpack_from('<Q', expected)[0] - self.new.preferred)
                      if self.new.read(start, size) == bytes(expected)]
            if len(tables) != 1:
                raise RelocationError(f'no equivalent function refers to vtable {rva:#x}, and {len(tables)} tables '
                                      'hold its relocated entries') from None
            return tables[0], {'unit': 'vtable', 'via': 'its relocated entries'}
        if self.new.read(target, size) != bytes(expected):
            raise RelocationError(f'vtable {rva:#x} moved to {target:#x}, but its entries are not the relocated ones')
        return target, {**detail, 'unit': 'vtable'}


def relocate(old: Image, new: Image, reviewed=None):
    relocator = Relocator(old, new, reviewed)
    required = signature.product_addresses()
    sites, failures, omitted = {}, [], []

    def attempt(rva, kind, action):
        try:
            target, detail = action()
            sites[rva] = {'kind': kind, 'rva': hex(target), 'delta': target - rva, **detail}
        except RelocationError as error:
            (failures if rva in required else omitted).append({'kind': kind, 'canonical': hex(rva), 'reason': str(error)})

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
        # A pinned range starts at, or a few bytes before, a native address in the same unit; it moves with
        # that address, or is left out with it.
        anchor = min((address for address in signature.native_addresses() if address >= rva), default=None)
        if anchor is None or anchor - rva > 16 or old.read(rva, len(expected) // 2).hex() != expected:
            failures.append({'kind': 'pin', 'canonical': hex(rva), 'reason': 'no native address anchors it'})
            continue
        if anchor in relocation:
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
        'omitted': omitted,
    }


def reviewed_changes(name, path=NATIVE_BUILDS):
    """The reviewed changes recorded for a build: verified-build function -> (counterpart, reason)."""
    builds = json.loads(path.read_text(encoding='utf-8')) if path.is_file() else {}
    return {int(begin, 16): (int(change['counterpart'], 16), change['reason'])
            for begin, change in builds.get(name, {}).get('reviewed', {}).items()}


def table(report, name):
    """The other build's entry for NativeCodeSignature.BUILDS."""
    lines = [f"# {report['build']['sha256']} ({report['build']['size']} bytes), relocated from the verified build "
             'by RelocateNativeBuild.py.',
             'Build(',
             f'    {name!r},',
             '    {' + ', '.join(f'{section!r}: {start}' for section, start in report['build']['sections'].items()) + '},',
             '    {']
    lines += [f"        {rva}: '{data}'," for rva, data in report['pinned'].items()]
    lines += ['    },', '    {']
    lines += [f'        {canonical}: {rva},' for canonical, rva in report['relocation'].items()]
    lines += ['    })']
    return '\n'.join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('verified', type=Path)
    parser.add_argument('other', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--name', help="print the other build's NativeCodeSignature entry under this name, "
                                       'accepting the changes NATIVE_BUILDS records as reviewed for it')
    args = parser.parse_args(argv)
    old, new = Image(args.verified), Image(args.other)
    if old.sha256 != signature.VERIFIED_SHA256:
        raise RelocationError('the first executable must be the verified build')
    report = relocate(old, new, reviewed_changes(args.name) if args.name else None)
    text = json.dumps(report, indent=2)
    if args.output:
        args.output.write_text(text + '\n', encoding='utf-8')
    elif not args.name:
        print(text)
    if args.name and not report['failures']:
        print(table(report, args.name))
    for canonical, site in report['sites'].items():
        if site['unit'] == 'reviewed':
            print(f"REVIEWED {canonical} -> {site['rva']} in changed {site['begin']} ({site['identity']})", file=sys.stderr)
        elif site.get('via', canonical) != canonical:
            print(f"NOTE {site['unit']} {canonical} has no equivalent reference of its own; it moved with {site['via']}",
                  file=sys.stderr)
    for failure in report['omitted']:
        print(f"OMITTED {failure['kind']} {failure['canonical']} (experiments only): {failure['reason']}", file=sys.stderr)
    for failure in report['failures']:
        print(f"FAILED {failure['kind']} {failure['canonical']}: {failure['reason']}", file=sys.stderr)
    print(f"{len(report['relocation'])} addresses relocated, {len(report['omitted'])} omitted, "
          f"{len(report['failures'])} failed", file=sys.stderr)
    return 1 if report['failures'] else 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, PeError, RelocationError) as error:
        raise SystemExit(f'ERROR: {error}') from error
