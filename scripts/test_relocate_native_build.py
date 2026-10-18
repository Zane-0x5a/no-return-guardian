import hashlib
import struct
import unittest

from RelocateNativeBuild import Disassembler, Image, equivalent, relocate

import NativeCodeSignature
from local_evidence import game_executable


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


class VerifiedBuildTests(unittest.TestCase):
    def test_the_verified_build_relocates_onto_itself_unchanged(self):
        executable = game_executable()
        if executable is None or not executable.is_file():
            self.skipTest('set NRG_GAME_EXE to the verified game executable')
        if hashlib.sha256(executable.read_bytes()).hexdigest().upper() != NativeCodeSignature.VERIFIED_SHA256:
            self.skipTest('the local game is not the verified build')
        image = Image(executable)
        report = relocate(image, image)
        self.assertEqual(report['failures'], [])
        self.assertEqual({int(rva, 16): int(target, 16) for rva, target in report['relocation'].items()},
                         {rva: rva for rva in NativeCodeSignature.native_addresses()})
        self.assertEqual({int(rva, 16): data for rva, data in report['pinned'].items()}, NativeCodeSignature.PINNED)


if __name__ == '__main__':
    unittest.main()
