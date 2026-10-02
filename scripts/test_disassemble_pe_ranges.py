import contextlib
import io
import struct
import unittest

from capstone import CS_ARCH_X86, CS_MODE_64, Cs

from DisassemblePeRanges import describe_string, disassemble, format_target, mapped_payload
from InspectPeVa import PeError, Section


class DisassemblyEvidenceTests(unittest.TestCase):
    base = 0x140000000
    sections = [
        Section(".text", 0x1000, 0x100, 0, 0x100),
        Section(".rdata", 0x2000, 0x200, 0x100, 0x100),
    ]

    def annotation(self, encoded, address=None):
        decoder = Cs(CS_ARCH_X86, CS_MODE_64)
        decoder.detail = True
        instruction = next(decoder.disasm(encoded, address or self.base + 0x1000))
        return format_target(instruction, self.base)

    def test_positive_rip_uses_instruction_end(self):
        encoded = b"\x48\x8d\x0d" + struct.pack("<i", 0xFF9)
        self.assertIn("RIP_TARGET=0x140002000", self.annotation(encoded))

    def test_negative_rip_displacement(self):
        encoded = b"\x48\x8b\x05" + struct.pack("<i", -0x1007)
        self.assertIn("RIP_TARGET=0x140001000", self.annotation(encoded, self.base + 0x2000))

    def test_register_relative_is_not_static(self):
        self.assertEqual("", self.annotation(b"\x48\x8b\x43\x10"))

    def test_missing_image_does_not_invent_string(self):
        self.assertNotIn("ASCII", self.annotation(b"\x48\x8b\x05\0\0\0\0"))

    def test_exact_section_end_allowed(self):
        payload, offset, section = mapped_payload(bytes(512), self.base, 0, self.sections, self.base + 0x1000, 256)
        self.assertEqual((256, 0, ".text"), (len(payload), offset, section))

    def test_unmapped_and_invalid_ranges_rejected(self):
        cases = [
            (self.base - 1, 1),
            (self.base + 0x1000, 0),
            (self.base + 0x1000, -1),
            (self.base + 0x10FF, 2),
            (self.base + 0x2100, 1),
            (self.base + 0x20FF, 2),
            (self.base + 0x3000, 1),
            (self.base + 0x1000, 0x1001),
        ]
        for start, size in cases:
            with self.subTest(start=start, size=size), self.assertRaises(PeError):
                mapped_payload(bytes(512), self.base, 0, self.sections, start, size)

    def test_adjacent_sections_cannot_be_combined(self):
        sections = [self.sections[0], Section(".rdata", 0x1100, 256, 256, 256)]
        with self.assertRaises(PeError):
            mapped_payload(bytes(512), self.base, 0, sections, self.base + 0x10FF, 2)

    def test_file_truncation_rejected(self):
        with self.assertRaises(PeError):
            mapped_payload(bytes(8), self.base, 0, self.sections, self.base + 0x1000, 16)

    def test_incomplete_instruction_is_failure(self):
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(PeError, "incomplete decode"):
            disassemble(b"\x90\x48", self.base, 0, self.sections, self.base + 0x1000, 2)

    def test_complete_instruction_range(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            disassemble(b"\x90\xc3", self.base, 0, self.sections, self.base + 0x1000, 2)
        self.assertIn("ret", output.getvalue())

    def test_ascii_is_only_a_candidate(self):
        image = bytes(256) + b"restart-checkpoint\0" + bytes(238)
        self.assertEqual("ASCII_CANDIDATE='restart-checkpoint'", describe_string(image, self.base, 0, self.sections, self.base + 0x2000))

    def test_invalid_string_candidates_rejected(self):
        for payload in [b"ab\0", b"a\x01b\0", b"a\xffb\0", b"abc\n\0", b"A" * 256]:
            with self.subTest(payload=payload[:8]):
                image = bytes(256) + payload.ljust(256, b"X")
                self.assertEqual("", describe_string(image, self.base, 0, self.sections, self.base + 0x2000))

    def test_string_does_not_cross_section(self):
        image = bytes(256) + b"A" * 256 + b"\0"
        self.assertEqual("", describe_string(image, self.base, 0, self.sections, self.base + 0x20FC))

    def test_code_and_unmapped_addresses_not_strings(self):
        image = b"looks-like-string\0".ljust(512, b"\0")
        for address in [self.base + 0x1000, self.base + 0x2100, self.base + 0x3000]:
            with self.subTest(address=address):
                self.assertEqual("", describe_string(image, self.base, 0, self.sections, address))


if __name__ == "__main__":
    unittest.main()
