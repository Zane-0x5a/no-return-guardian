import struct
import unittest

from DisassembleDcStateScript import disassemble
from FindPeSidStrings import string_id64


def sid(text):
    return string_id64(text.encode("ascii"))


def instruction(opcode, destination, first=0, second=0):
    return bytes((opcode, destination, first, second)) + bytes(4)


def fixture():
    payload = bytearray(0x400)

    def put(fmt, offset, *values):
        struct.pack_into(fmt, payload, offset, *values)

    put("<IIII", 0, 0x44433030, 1, 0x3F0, 0)
    put("<IIQ", 0x10, 1, 1, 0x28)
    put("<QQQ", 0x28, sid("ss-test"), sid("state-script"), 0x40)
    put("<QQQ", 0x40, sid("ss-test"), 0, sid("default"))
    put("<Q", 0x68, 0x90)
    put("<hh", 0x70, 1, 1)
    put("<Q", 0x78, 0x300)
    put("<QqQ", 0x90, sid("default"), 1, 0xA8)
    put("<IIQQ", 0xA8, 3, 0, 0, 0)
    put("<HhIQ", 0xC0 + 8, 1, 1, 0, 0x100)
    put("<QHhIQ", 0x100, sid("main"), 0, 1, 0, 0x120)
    put("<QQ", 0x120, 0x140, 0)
    put("<QQ", 0x140, 0x200, 0x2C0)
    put("<I", 0x140 + 0x34, 6)
    code = (
        instruction(0x15, 0, 0)
        + instruction(0x4A, 1, 1)
        + instruction(0x43, 49, 1)
        + instruction(0x1C, 0, 0, 1)
        + instruction(0x2F, 5, 0)
        + instruction(0x00, 0)
    )
    payload[0x200:0x200 + len(code)] = code
    put("<QQ", 0x2C0, sid("joypad-command-active?"), 0x1234567890ABCDEF)
    payload[0x300:0x30C] = b"ss-test.dcx\0"
    return payload


class DcStateScriptTests(unittest.TestCase):
    def test_resolves_known_names_and_keeps_unknown_sids_hexadecimal(self):
        names = {sid(name): name for name in ("ss-test", "state-script", "default", "main",
                                              "joypad-command-active?")}
        text = "\n".join(disassemble(bytes(fixture()), names))
        self.assertIn("ENTRY ss-test type=state-script", text)
        self.assertIn("source=ss-test.dcx", text)
        self.assertIn("ON update tracks=1", text)
        self.assertIn("LookupPointer          0   0   0 ; joypad-command-active?", text)
        self.assertIn("#1234567890ABCDEF", text)
        self.assertIn("-> 5 if r0", text)
        self.assertIn("r0 = r0(argc=1)", text)

    def test_rejects_non_dc_payload_and_out_of_range_pointers(self):
        with self.assertRaises(ValueError):
            disassemble(bytes(0x40), {})
        payload = fixture()
        struct.pack_into("<Q", payload, 0x78, 0x10000)
        with self.assertRaises(ValueError):
            disassemble(bytes(payload), {})

    def test_rejects_unknown_opcode(self):
        payload = fixture()
        payload[0x200] = 0xFF
        with self.assertRaises(ValueError):
            disassemble(bytes(payload), {})


if __name__ == "__main__":
    unittest.main()
