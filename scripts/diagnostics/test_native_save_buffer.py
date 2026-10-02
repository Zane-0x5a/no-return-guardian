from pathlib import Path
import struct
import unittest

from NativeCollectionAnalysis import CapturedEvidence
from NativeSaveBuffer import ARENA_OFFSET, BODY_OFFSET, NativeSaveBuffer, ProbeError
from test_native_serialized_arena import fixture as arena_fixture
from local_evidence import requires


def fixture(tail=0):
    arena = arena_fixture()
    data = bytearray(ARENA_OFFSET + len(arena) + tail)
    struct.pack_into('<III', data, 0, 0, ARENA_OFFSET + len(arena), 27)
    data[0x14:0x18] = b'run\0'
    data[ARENA_OFFSET:ARENA_OFFSET + len(arena)] = arena
    if tail:
        data[-tail:] = b'\xa5' * tail
    return bytes(data)


class NativeSaveBufferTests(unittest.TestCase):
    def test_load_preserves_source_prefix_and_unused_tail(self):
        source = fixture(32)
        result = NativeSaveBuffer(source).copy_and_fix_up(0x100000, 2)
        self.assertEqual(result.source_after, source)
        self.assertEqual(result.destination[:ARENA_OFFSET], source[:ARENA_OFFSET])
        self.assertEqual(result.destination[-32:], source[-32:])

    def test_promotion_mutates_source_to_destination_pointers_before_copy(self):
        source = NativeSaveBuffer(fixture(32)).copy_and_fix_up(0x100000, 2)
        pointer = NativeSaveBuffer(source.destination, address=0x100000, serialized=False)
        result = pointer.copy_and_fix_up(0x200000, 0)
        self.assertEqual(result.source_after, result.destination)
        self.assertNotEqual(result.source_after, source.destination)
        self.assertEqual(struct.unpack_from('<Q', result.source_after, ARENA_OFFSET + 0x30)[0], 0x200418)
        self.assertEqual(NativeSaveBuffer(result.destination, address=0x200000, serialized=False)
                         .copy_and_fix_up(0x100000, 0).destination, source.destination)

    def test_partial_copy_does_not_invent_a_cache_tail(self):
        body = NativeSaveBuffer(fixture(), capacity=len(fixture()) + 32)
        loaded = body.copy_and_fix_up(0x100000, 2)
        self.assertEqual(len(loaded.destination), len(fixture()))
        with self.assertRaisesRegex(ProbeError, 'complete equal-capacity'):
            NativeSaveBuffer(loaded.destination, capacity=loaded.capacity, address=0x100000, serialized=False) \
                .copy_and_fix_up(0x200000, 0)

    def test_invalid_declarations_versions_descriptions_and_arena_sizes(self):
        for field, value in ((4, 0), (4, len(fixture()) + 1), (8, 26),
                             (ARENA_OFFSET + 0x38, 0), (ARENA_OFFSET + 0x38, 0xffffffff)):
            data = bytearray(fixture())
            struct.pack_into('<I', data, field, value)
            with self.subTest(field=field, value=value), self.assertRaises(ProbeError):
                NativeSaveBuffer(data)
        for text in (b'x' * 0x400, b'\xff\0' + b'x' * 0x3fe):
            data = bytearray(fixture())
            data[0x14:0x414] = text
            with self.assertRaises(ProbeError):
                NativeSaveBuffer(data)

    def test_invalid_capacity_address_direction_stage_and_pointer_basis(self):
        for capacity in (True, -1, len(fixture()) - 1, 16 * 1024 * 1024 + 1):
            with self.subTest(capacity=capacity), self.assertRaises(ProbeError):
                NativeSaveBuffer(fixture(), capacity=capacity)
        source = NativeSaveBuffer(fixture(), address=0x100000)
        for address in (0, -8, 0x100000, 0x100008, 0x800000000000, 0x200003):
            with self.subTest(address=address), self.assertRaises(ProbeError):
                source.copy_and_fix_up(address, 2)
        for direction, stage in ((1, -1), (3, -1), (2, 0), (2, 1), (True, -1), (2, True)):
            with self.subTest(direction=direction, stage=stage), self.assertRaises(ProbeError):
                source.copy_and_fix_up(0x200000, direction, stage=stage)
        with self.assertRaises(ProbeError):
            source.copy_and_fix_up(0x200000, 0)
        loaded = source.copy_and_fix_up(0x200000, 2)
        pointer = NativeSaveBuffer(loaded.destination, address=0x200000, serialized=False)
        with self.assertRaises(ProbeError):
            pointer.copy_and_fix_up(0x300000, 2)
        with self.assertRaises(ProbeError):
            pointer.copy_and_fix_up(0x300000, 0, destination_capacity=pointer.capacity + 8)
        with self.assertRaises(ProbeError):
            NativeSaveBuffer(loaded.destination)
        with self.assertRaises(ProbeError):
            NativeSaveBuffer(loaded.destination, address=0x300000, serialized=False)

    def test_working_header_and_export_tail_are_not_interchangeable(self):
        raw = bytes.fromhex('93ea' + '00' * 14) + bytes(BODY_OFFSET - 16) + fixture()
        self.assertEqual(NativeSaveBuffer.from_working_raw(raw).data, fixture())
        for invalid in (b'', b'\x92' + raw[1:], raw + bytes(36), raw[:-1], raw[:2] + b'\1' + raw[3:]):
            with self.assertRaises(ProbeError):
                NativeSaveBuffer.from_working_raw(invalid)


@requires('native-runtime-20260912/collection-observe-02.jsonl')
class CapturedSaveBufferTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(__file__).resolve().parents[2] / 'artifacts/native-runtime-20260912'
        cls.evidence = CapturedEvidence(cls.root / 'collection-observe-02.jsonl')
        cls.evidence.normal_load_baseline()

    def test_both_real_loads_and_promotions_match_all_bytes(self):
        for call_id, direction in ((165, 2), (646, 2), (189, 0), (670, 0)):
            before = self.evidence.payload(call_id, 'save-buffer-copy:source-before')
            after = self.evidence.payload(call_id, 'save-buffer-copy:after')
            source_region, source = self.evidence.region(before, 'source')
            _, source_after = self.evidence.region(after, 'source-after')
            destination_region, destination = self.evidence.region(after, 'destination-after')
            with self.subTest(call_id=call_id):
                body = NativeSaveBuffer(source, capacity=0x460414,
                                        address=int(source_region['address'], 16), serialized=direction == 2)
                copied = body.copy_and_fix_up(int(destination_region['address'], 16), direction)
                self.assertEqual(copied.source_after, source_after)
                self.assertEqual(copied.destination, destination)
                self.assertEqual(body.declared_size, 4559176)

    def test_formal_preparation_source_supports_independent_load_and_promotion(self):
        path = self.root / ('collection-insurance-storage/snapshots/'
                            '20260912-072519061-manual-62fed724/payload/gamedata/R0A.save')
        body = NativeSaveBuffer.from_working_raw(path.read_bytes(), capacity=0x460414)
        self.assertIn('[042EFF030080000000000001]', body.description)
        loaded = body.copy_and_fix_up(0x1000000, 2)
        tail = bytes(loaded.capacity - len(loaded.destination))
        temporary = NativeSaveBuffer(loaded.destination + tail, address=loaded.destination_address, serialized=False)
        promoted = temporary.copy_and_fix_up(0x2000000, 0)
        rebuilt = NativeSaveBuffer(promoted.destination, address=promoted.destination_address, serialized=False)
        self.assertEqual(rebuilt.description, body.description)
        self.assertEqual(rebuilt.arena.relocate(0), body.arena.data)
        self.assertEqual(promoted.destination[-len(tail):], tail)


if __name__ == '__main__':
    unittest.main()
