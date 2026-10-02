from pathlib import Path
import struct
import unittest

from NativeCollectionAnalysis import CapturedEvidence, ProbeError, compare_load, slice_region
from NativeCollectionReport import assess
from test_native_collection_report import fixture as report_fixture
from local_evidence import requires


def load_fixture():
    declared = 0x500
    address = 0x100000
    source = bytearray(declared + 32)
    struct.pack_into('<I', source, 4, declared)
    arena = bytearray(declared - 0x418)
    struct.pack_into('<Q', arena, 0x30, address + 0x418)
    struct.pack_into('<I', arena, 0x38, len(arena))
    destination = source[:]
    destination[0x418:declared] = arena
    return [source, source[:], destination, address, arena, address + 0x418]


class CollectionAnalysisTests(unittest.TestCase):
    def test_copy_tail_is_not_part_of_declared_serialized_body(self):
        values = load_fixture()
        result = compare_load(*values)
        self.assertEqual(result['copyTailOutsideDeclaredBody'], 32)
        self.assertTrue(result['consumedEqualsCopyDestination'])
        values[0][-1] = 8
        result = compare_load(*values)
        self.assertFalse(result['sourceUnchangedByCopy'])

    def test_same_pointer_with_changed_contents_is_not_a_load_match(self):
        values = load_fixture()
        values[2][0x460] ^= 1
        self.assertFalse(compare_load(*values)['consumedEqualsCopyDestination'])

    def test_invalid_address_or_truncated_copy_is_rejected(self):
        for index, value in ((5, 0x100420), (0, b'bad'), (2, b'short')):
            values = load_fixture()
            values[index] = value
            with self.subTest(index=index), self.assertRaises(ProbeError):
                compare_load(*values)

    def test_arena_cannot_extend_into_unrelated_copy_tail(self):
        values = load_fixture()
        values[4].extend(b'\0' * 16)
        struct.pack_into('<I', values[4], 0x38, len(values[4]))
        with self.assertRaisesRegex(ProbeError, 'declared save body'):
            compare_load(*values)

    def test_arena_descriptor_and_region_bounds_must_match(self):
        for offset in (0x30, 0x38):
            values = load_fixture()
            values[4][offset] ^= 1
            with self.subTest(offset=offset), self.assertRaisesRegex(ProbeError, 'descriptor'):
                compare_load(*values)
        for region in ({'offset': -1, 'size': 1}, {'offset': 0, 'size': 9}, {'offset': 0, 'size': 0}):
            with self.subTest(region=region), self.assertRaises(ProbeError):
                slice_region(b'four', region)

    @requires('native-runtime-20260912/collection-observe-02.jsonl')
    def test_real_failed_collection_preserves_only_a_verified_normal_load_prefix(self):
        source = Path(__file__).resolve().parents[2] / 'artifacts/native-runtime-20260912/collection-observe-02.jsonl'
        evidence = CapturedEvidence(source)
        loads = evidence.loads()
        self.assertEqual([load['applyCallId'] for load in loads], [171, 652])
        self.assertTrue(all(load['consumedEqualsCopyDestination'] for load in loads))
        self.assertEqual([load['copyTailOutsideDeclaredBody'] for load in loads], [29388, 0])
        baseline = evidence.normal_load_baseline()
        self.assertEqual(baseline['throughSequence'], 1012)
        self.assertFalse(baseline['sourceCollectionComplete'])
        self.assertFalse(assess(evidence.records, baseline)['knownCoveragePresent'])
        self.assertEqual(evidence.players()['periodicRegions']['differentVtable'], 1306)
        evidence.records = [record for record in evidence.records if record.get('name') != 'preparation-before-departure']
        with self.assertRaisesRegex(ProbeError, 'No confirmed'):
            evidence.normal_load_baseline()

    def test_reusing_normal_load_does_not_waive_current_phase_or_failure_checks(self):
        baseline = {'verifiedNormalLoadPrefix': True, 'buildSha256': 'fixed'}
        records = [record for record in report_fixture() if record.get('kind') not in ('load-enter', 'load-leave') and
                   record.get('name') != 'preparation-before-exit']
        records[0]['sha256'] = 'fixed'
        self.assertFalse(assess(records)['departureEvidencePresent'])
        self.assertTrue(assess(records, baseline)['knownCoveragePresent'])
        for kind in ('script-error',):
            self.assertFalse(assess(records + [{'kind': kind}], baseline)['knownCoveragePresent'])
        for name in ('preparation-before-departure', 'encounter-started', 'resources-changed', 'death-result', 'post-death-menu'):
            changed = [record for record in records if record.get('name') != name]
            with self.subTest(name=name):
                self.assertFalse(assess(changed, baseline)['knownCoveragePresent'])
        for key in ('sha256',):
            records[0][key] = 'another-build'
            result = assess(records, baseline)
            self.assertFalse(result['departureEvidencePresent'])
            self.assertTrue(any('different executable' in error for error in result['errors']))


if __name__ == '__main__':
    unittest.main()
