import ast
import hashlib
from pathlib import Path
import re
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

HERE = Path(__file__).resolve().parent
# The native scripts a release ships for the product's modes (scripts\build-native-recovery.ps1).
SHIPPED_PYTHON = ('NativeCheckpointTrigger.py', 'NativeCheckpointProbe.py', 'NativeRecoverySource.py',
                  'NativeResultsExit.py', 'NativeResultsUi.py', 'NativeEncounterRestart.py',
                  'NativeDepartureRecorder.py', 'NativeDepartureStage.py', 'NativeDepartureRecord.py')
SHIPPED_JAVASCRIPT = ('NativeRecoveryBridge.js', 'NativeRecoveryContract.js', 'NativePreparationRecovery.js',
                      'NativeResultsContinue.js', 'NativeEncounterControl.js')
# Experiment code that refuses every build but the verified one (require_verified_build).
VERIFIED_ONLY = {('NativeCheckpointProbe.py', name) for name in ('inspect_registrations', 'verify_code', 'sample')}
# Where code and game data lie in the verified build; smaller constants are sizes and field offsets.
ADDRESS_RANGE = range(0xC00000, 0xA000000)


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
    def tearDown(self):
        NativeCodeSignature.activate(None)

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
        known = NativeCodeSignature.Build('fixture', {'.text': 0x1000, '.data': 0x2000}, {0x1010: code[0x10:0x30].hex()})
        with mock.patch.object(NativeCodeSignature, 'BUILDS', (known,)):
            self.assertEqual(verify_build(image), hashlib.sha256(image).hexdigest().upper())
            self.assertIs(NativeCodeSignature.active(), known)
            elsewhere = image[:-1] + b'\x01'
            self.assertEqual(verify_build(elsewhere), hashlib.sha256(elsewhere).hexdigest().upper())
            NativeCodeSignature.activate(None)
            changed = bytearray(image)
            changed[0x400 + 0x18] ^= 0xFF
            with self.assertRaisesRegex(ProbeError, 'differs from the verified build \\(code at 0x1010\\); nothing'):
                verify_build(bytes(changed))
            moved = portable_image([('.text', 0x1000, code), ('.data', 0x3000, bytes(0x80))])
            with self.assertRaisesRegex(ProbeError, 'section layout'):
                verify_build(moved)
            self.assertRaisesRegex(ValueError, 'not been verified', NativeCodeSignature.at, 0x415AC90)

    def test_another_build_is_recognized_by_its_own_table_and_relocates_every_address(self):
        code = bytes(range(0x40)) * 4
        image = portable_image([('.text', 0x1000, code), ('.data', 0x3000, bytes(0x80))])
        other = NativeCodeSignature.Build('later', {'.text': 0x1000, '.data': 0x3000}, {0x1020: code[0x20:0x40].hex()},
                                          {0x415AC90: 0x4160C90, 0x1B6EA60: 0x1B70A60})
        with mock.patch.object(NativeCodeSignature, 'BUILDS', (NativeCodeSignature.VERIFIED, other)):
            verify_build(image)
            self.assertIs(NativeCodeSignature.active(), other)
            self.assertEqual(NativeCodeSignature.at(0x415AC90), 0x4160C90)
            self.assertEqual(NativeCodeSignature.addresses(), {'0x1b6ea60': 0x1B70A60, '0x415ac90': 0x4160C90})
            # An address this build has no counterpart for, and one no native mode uses, are refused.
            self.assertRaisesRegex(ValueError, 'later build', NativeCodeSignature.at, 0x9341660)
            self.assertRaisesRegex(ValueError, 'later build', NativeCodeSignature.at, 0x415AC91)
            # A build sharing a known layout but not its code is reported by where that code differs.
            changed = bytearray(image)
            changed[0x400 + 0x28] ^= 0xFF
            self.assertEqual(NativeCodeSignature.signature_mismatch(bytes(changed)), 'code at 0x1020')

    def test_the_verified_build_names_every_native_address_where_it_is(self):
        NativeCodeSignature.activate(NativeCodeSignature.VERIFIED)
        native = NativeCodeSignature.native_addresses()
        self.assertEqual(NativeCodeSignature.addresses(), {hex(rva): rva for rva in native})
        self.assertRaises(ValueError, NativeCodeSignature.at, 0x4166E90)

    def test_the_shipped_scripts_reach_game_addresses_only_through_the_build_table(self):
        native = NativeCodeSignature.native_addresses()

        def address(node):
            return isinstance(node, ast.Constant) and type(node.value) is int and node.value in ADDRESS_RANGE

        for name in SHIPPED_JAVASCRIPT:
            lines = (HERE / name).read_text(encoding='utf-8').splitlines()
            with self.subTest(script=name):
                self.assertEqual([line.strip() for line in lines if re.search(r'image\.base\.add\((?!actual\))', line)], [])
                literals = {int(literal, 16) for line in lines for literal in re.findall(r'\b0x[0-9a-f]+\b', line)}
                self.assertEqual({hex(value) for value in literals if value in ADDRESS_RANGE and value not in native},
                                 set())
        for name in SHIPPED_PYTHON:
            tree = ast.parse((HERE / name).read_text(encoding='utf-8'))
            for function in [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef)]:
                if (name, function.name) in VERIFIED_ONLY:
                    continue
                for node in ast.walk(function):
                    if isinstance(node, ast.Call) and getattr(node.func, 'id', None) == 'at':
                        if isinstance(node.args[0], ast.Constant):
                            self.assertIn(node.args[0].value, native, f'{name}:{node.lineno}')
                    elif isinstance(node, ast.BinOp) and isinstance(node.op, ast.Add):
                        self.assertFalse(address(node.left) or address(node.right),
                                         f'{name}:{node.lineno} adds a raw game address')

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
