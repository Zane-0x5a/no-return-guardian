import hashlib
import struct
from types import SimpleNamespace
import unittest

import numpy as np

from RelocateNativeBuild import Disassembler, Image, RelocationError, Relocator, equivalent, relocate, reviewed_changes

import NativeCodeSignature
from local_evidence import build_executables, verified_executable


class Code:
    """Bytes mapped at one RVA, read the way the relocator reads an executable."""

    def __init__(self, rva, data):
        self.rva, self.data = rva, bytes(data)

    def read(self, rva, size):
        start = rva - self.rva
        return self.data[start:start + size]


def function(rva, data_target, callee, compare=2, branch=2):
    """mov rax,[rip+data]; cmp edx,compare; je +branch; call callee; nop; nop; ret"""
    code = bytearray(b'\x48\x8b\x05' + struct.pack('<i', data_target - (rva + 7)))
    code += bytes([0x83, 0xFA, compare, 0x74, branch])
    code += b'\xe8' + struct.pack('<i', callee - (rva + len(code) + 5))
    code += b'\x90\x90\xc3'
    return Code(rva, code), (rva, rva + len(code))


class EquivalenceTests(unittest.TestCase):
    disassembler = Disassembler()

    def compare(self, old, new):
        (old_code, old_unit), (new_code, new_unit) = old, new
        return equivalent(self.disassembler, old_code, old_unit, new_code, new_unit)

    def test_moved_code_is_the_same_code_and_tells_where_its_references_moved(self):
        mapping = self.compare(function(0x1000, 0x9000, 0x3000), function(0x1800, 0x9800, 0x3400))
        self.assertEqual(mapping, {0x9000: 0x9800, 0x3000: 0x3400})

    def test_changed_operands_or_branches_inside_the_unit_are_different_code(self):
        old = function(0x1000, 0x9000, 0x3000)
        self.assertIsNone(self.compare(old, function(0x1800, 0x9800, 0x3400, compare=3)))
        self.assertIsNone(self.compare(old, function(0x1800, 0x9800, 0x3400, branch=1)))

    def test_one_old_target_cannot_move_to_two_places(self):
        old_code = bytearray()
        new_code = bytearray()
        for index in range(2):
            old_code += b'\x48\x8b\x05' + struct.pack('<i', 0x9000 - (0x1000 + len(old_code) + 7))
            new_code += b'\x48\x8b\x05' + struct.pack('<i', 0x9800 + index * 8 - (0x1800 + len(new_code) + 7))
        old_code += b'\xc3'
        new_code += b'\xc3'
        self.assertIsNone(self.compare((Code(0x1000, old_code), (0x1000, 0x1000 + len(old_code))),
                                       (Code(0x1800, new_code), (0x1800, 0x1800 + len(new_code)))))


class Functions:
    """An image whose RVAs are file offsets, its runtime functions laid out one after another."""
    preferred = 0x140000000
    function_index = Image.function_index

    def __init__(self, functions, size=0x4000):
        self.data = bytearray(size)
        for begin, code in functions:
            self.data[begin:begin + len(code)] = code
        self.begins = np.array([begin for begin, _ in functions], dtype=np.int64)
        self.ends = np.array([begin + len(code) for begin, code in functions], dtype=np.int64)
        self.sizes = self.ends - self.begins
        self.text = SimpleNamespace(virtual_address=0, raw_size=size)

    def read(self, rva, size):
        return bytes(self.data[rva:rva + size])

    def section(self, name):
        return self.text


def call(rva, target):
    return b'\xe8' + struct.pack('<i', target - (rva + 5))


class ChangedFunctionTests(unittest.TestCase):
    """A target function at 0x1100 moves to 0x2300, past a new function, so only its caller can pair it."""

    def builds(self, changed):
        callee = b'\x31\xc0\xc3'
        target = b'\x53' + call(0x1101, 0x1000) + b'\x90\x5b\xc3'
        moved = (b'\x53\x31\xc0' + call(0x2303, 0x2000) if changed else b'\x53' + call(0x2301, 0x2000)) + b'\x90\x5b\xc3'
        old = Functions([(0x1000, callee), (0x1100, target), (0x1200, call(0x1200, 0x1100) + b'\xc3')])
        new = Functions([(0x2000, callee), (0x2080, b'\xb8\x01\x00\x00\x00\xc3'),
                         (0x2200, call(0x2200, 0x2300) + b'\xc3'), (0x2300, moved)])
        return old, new

    def relocator(self, changed, reviewed=None):
        relocator = Relocator(*self.builds(changed), reviewed)
        # Sizes pair the callee and the caller with the function at their own index, never the target.
        relocator.shifts = lambda index: [] if index == 1 else [0]
        return relocator

    def test_an_identical_function_is_found_where_its_unchanged_caller_calls(self):
        self.assertEqual(self.relocator(False).relocate_code(0x1106)[0], 0x2306)

    def test_a_changed_function_relocates_only_when_its_change_was_reviewed(self):
        with self.assertRaisesRegex(RelocationError, 'no identical counterpart'):
            self.relocator(True).relocate_code(0x1100)
        relocator = self.relocator(True, {0x1100: (0x2300, 'an added instruction')})
        target, detail = relocator.relocate_code(0x1100)
        self.assertEqual((target, detail['unit'], detail['identity']), (0x2300, 'reviewed', 'referred to by 0x1200'))
        # Its call's return address follows the one call to the callee's counterpart.
        self.assertEqual(relocator.relocate_code(0x1106)[0], 0x2308)
        with self.assertRaisesRegex(RelocationError, 'follows no direct call'):
            relocator.relocate_code(0x1102)

    def test_a_reviewed_counterpart_its_caller_contradicts_is_refused(self):
        with self.assertRaisesRegex(RelocationError, 'refer to 0x1100 as 0x2300, not 0x2080'):
            self.relocator(True, {0x1100: (0x2080, 'wrong function')}).relocate_code(0x1100)


def verified_image(test):
    executable = verified_executable()
    if executable is None or not executable.is_file():
        test.skipTest('keep the verified build in artifacts/verified-build-20250413 or set NRG_GAME_EXE')
    if hashlib.sha256(executable.read_bytes()).hexdigest().upper() != NativeCodeSignature.VERIFIED_SHA256:
        test.skipTest('the local game is not the verified build')
    return Image(executable)


class KnownBuildTests(unittest.TestCase):
    def test_the_verified_build_relocates_onto_itself_unchanged(self):
        image = verified_image(self)
        report = relocate(image, image)
        self.assertEqual((report['failures'], report['omitted']), ([], []))
        self.assertEqual({int(rva, 16): int(target, 16) for rva, target in report['relocation'].items()},
                         {rva: rva for rva in NativeCodeSignature.native_addresses()})
        self.assertEqual({int(rva, 16): data for rva, data in report['pinned'].items()}, NativeCodeSignature.PINNED)

    def test_every_later_build_table_is_what_the_relocation_finds(self):
        old = verified_image(self)
        for build in NativeCodeSignature.BUILDS[1:]:
            executables = build_executables(build.name)
            if not executables:
                self.skipTest(f'keep the {build.name} build in artifacts/build-{build.name}')
            with self.subTest(build=build.name):
                report = relocate(old, Image(executables[0]), reviewed_changes(build.name))
                self.assertEqual(report['failures'], [])
                self.assertEqual({int(rva, 16): int(target, 16) for rva, target in report['relocation'].items()},
                                 build.relocation)
                self.assertEqual({int(rva, 16): data for rva, data in report['pinned'].items()}, build.pinned)
                self.assertEqual({name: int(start, 16) for name, start in report['build']['sections'].items()},
                                 build.sections)


if __name__ == '__main__':
    unittest.main()
