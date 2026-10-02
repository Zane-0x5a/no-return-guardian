import hashlib
import struct
import unittest
from unittest import mock

import NativeCodeSignature
from NativeCheckpointProbe import ProbeError, verify_build
from NativeCheckpointTrigger import (CODE_RVAS, COLLECTION_RVAS, ENCOUNTER_RVAS, LIFECYCLE_RVAS, LOAD_RVAS,
                                     RECOVERY_RVAS, RESULTS_RVAS, RESUME_RVAS)
from NativeEncounterRestart import CONTROL_RVAS
from NativeResultsExit import CONTINUE_RVAS
from local_evidence import game_executable


def portable_image(sections, base=0x140000000):
    """A minimal PE32+ image: headers in the first 0x400 bytes, then each (name, rva, data) section."""
    optional_size = 0xF0
    header = bytearray(0x400)
    header[:2] = b'MZ'
    struct.pack_into('<I', header, 0x3C, 0x40)
    header[0x40:0x44] = b'PE\0\0'
    struct.pack_into('<HH12xH2x', header, 0x44, 0x8664, len(sections), optional_size)
    optional = 0x44 + 20
    struct.pack_into('<H', header, optional, 0x20B)
    struct.pack_into('<Q', header, optional + 24, base)
    struct.pack_into('<I', header, optional + 60, 0x400)
    raw = bytearray()
    for index, (name, rva, data) in enumerate(sections):
        entry = optional + optional_size + index * 40
        struct.pack_into('<8sIIII', header, entry, name.encode(), len(data), rva, len(data), 0x400 + len(raw))
        raw += data
    return bytes(header + raw)


class CodeSignatureTests(unittest.TestCase):
    def test_every_native_site_is_pinned(self):
        pinned = NativeCodeSignature.PINNED
        for group in (CODE_RVAS, LIFECYCLE_RVAS, RESUME_RVAS, LOAD_RVAS, COLLECTION_RVAS, RECOVERY_RVAS,
                      RESULTS_RVAS, ENCOUNTER_RVAS, CONTROL_RVAS, CONTINUE_RVAS):
            for rva in group:
                with self.subTest(rva=hex(rva)):
                    self.assertGreaterEqual(len(pinned[rva]) // 2, NativeCodeSignature.CODE_BYTES)
        for rva, size in NativeCodeSignature.EXTRA_SITES:
            self.assertEqual(len(pinned[rva]) // 2, size)

    def test_a_build_is_accepted_only_where_its_native_code_matches(self):
        code = bytes(range(0x40)) * 4
        image = portable_image([('.text', 0x1000, code), ('.data', 0x2000, bytes(0x80))])
        pins = {'SECTIONS': {'.text': 0x1000, '.data': 0x2000}, 'PINNED': {0x1010: code[0x10:0x30].hex()}}
        with mock.patch.multiple(NativeCodeSignature, **pins):
            self.assertEqual(verify_build(image), hashlib.sha256(image).hexdigest().upper())
            elsewhere = image[:-1] + b'\x01'
            self.assertEqual(verify_build(elsewhere), hashlib.sha256(elsewhere).hexdigest().upper())
            changed = bytearray(image)
            changed[0x400 + 0x18] ^= 0xFF
            with self.assertRaisesRegex(ProbeError, 'differs from the verified build \\(code at 0x1010\\); nothing'):
                verify_build(bytes(changed))
            moved = portable_image([('.text', 0x1000, code), ('.data', 0x3000, bytes(0x80))])
            with self.assertRaisesRegex(ProbeError, 'section layout'):
                verify_build(moved)

    def test_the_verified_build_matches_its_own_table(self):
        executable = game_executable()
        if executable is None or not executable.is_file():
            self.skipTest('set NRG_GAME_EXE to the verified game executable')
        image = executable.read_bytes()
        if hashlib.sha256(image).hexdigest().upper() != NativeCodeSignature.VERIFIED_SHA256:
            self.skipTest('the local game is not the verified build')
        self.assertIsNone(NativeCodeSignature.signature_mismatch(image))


if __name__ == '__main__':
    unittest.main()
