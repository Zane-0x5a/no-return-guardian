from pathlib import Path
import struct
import unittest

from NativeCollectionAnalysis import CapturedEvidence
from NativeSerializedArena import ProbeError, SerializedArena
from local_evidence import requires


def fixture():
    data = bytearray(0xa0)
    data[0] = 5
    struct.pack_into('<I', data, 4, 1)
    struct.pack_into('<Q', data, 8, 0x20)
    struct.pack_into('<Q', data, 0x10, 0x28)
    struct.pack_into('<Q', data, 0x20, 0x50)
    struct.pack_into('<I', data, 0x38, len(data))
    struct.pack_into('<I', data, 0x54, 2)
    struct.pack_into('<Q', data, 0x58, 0x70)
    struct.pack_into('<Q', data, 0x60, 0x80)
    struct.pack_into('<Q', data, 0x70, 0x90)
    struct.pack_into('<Q', data, 0x78, 0x90)
    data[0x80:0x82] = bytes((1, 4))
    data[0x90:0x94] = b'run\0'
    return data


class SerializedArenaTests(unittest.TestCase):
    def test_typed_pointer_relocation_preserves_identical_scalar_integer(self):
        source = fixture()
        arena = SerializedArena(source)
        relocated = arena.relocate(0x100000)
        self.assertEqual(struct.unpack_from('<Q', relocated, 0x70)[0], 0x90)
        self.assertEqual(struct.unpack_from('<Q', relocated, 0x78)[0], 0x100090)
        self.assertEqual(SerializedArena(relocated, 0x100000).relocate(0), source)
        self.assertEqual(arena.relocate(0), source)

    def test_unknown_type_cycle_and_unbounded_counts_are_rejected(self):
        for field, encoding, value in ((0x80, 'B', 8), (0x80, 'B', 5), (0x54, 'I', 0xffffffff)):
            data = fixture()
            struct.pack_into('<' + encoding, data, field, value)
            if value == 5:
                struct.pack_into('<Q', data, 0x70, 0x50)
            with self.subTest(field=field, value=value), self.assertRaises(ProbeError):
                SerializedArena(data)

    def test_null_external_misaligned_and_truncated_references_are_rejected(self):
        for field, value in ((8, 0), (8, 0x21), (0x58, 0x98), (0x60, 0x1000000), (0x78, 0x1000000)):
            data = fixture()
            struct.pack_into('<Q', data, field, value)
            with self.subTest(field=field, value=value), self.assertRaises(ProbeError):
                SerializedArena(data)

    def test_string_cannot_terminate_outside_the_arena(self):
        data = fixture()
        data[0x90:] = b'x' * (len(data) - 0x90)
        with self.assertRaisesRegex(ProbeError, 'not terminated'):
            SerializedArena(data)

    def test_truncated_wrong_descriptor_and_invalid_destinations_are_rejected(self):
        for source in (b'', fixture()[:-1]):
            with self.assertRaises(ProbeError):
                SerializedArena(source)
        data = fixture()
        struct.pack_into('<Q', data, 0x30, 0x100000)
        with self.assertRaises(ProbeError):
            SerializedArena(data)
        for address in (-1, 3, 0x800000000000):
            with self.subTest(address=address), self.assertRaises(ProbeError):
                SerializedArena(fixture()).relocate(address)

    @requires('native-runtime-20260912/collection-observe-02.jsonl')
    def test_both_real_normal_loads_and_reverse_transform_match_every_byte(self):
        source = Path(__file__).resolve().parents[2] / 'artifacts/native-runtime-20260912/collection-observe-02.jsonl'
        evidence = CapturedEvidence(source)
        for copy_id, getter_id in ((165, 175), (646, 656)):
            _, serialized = evidence.region(evidence.payload(copy_id, 'save-buffer-copy:source-before'), 'source')
            region, consumed = evidence.region(evidence.payload(getter_id, 'save-json-root:consumed'), 'json-declared-arena')
            encoded = serialized[0x418:0x418 + len(consumed)]
            with self.subTest(copy=copy_id):
                arena = SerializedArena(encoded)
                address = int(region['address'], 16)
                self.assertEqual(arena.relocate(address), consumed)
                self.assertEqual(SerializedArena(consumed, address).relocate(0), encoded)
                self.assertEqual(len(arena.pointer_fields), 217337)


if __name__ == '__main__':
    unittest.main()
