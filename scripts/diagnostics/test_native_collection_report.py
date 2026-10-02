from pathlib import Path
import tempfile
import unittest

from NativeCollectionReport import LOAD_OPERATIONS, ProbeError, assess, verify_file
from local_evidence import requires


def fixture():
    records = [{'kind': 'host-preflight', 'mode': 'collection-observe'}]
    for identifier, operation in enumerate(sorted(LOAD_OPERATIONS | {'copy-checkpoint'}), 1):
        records.extend([{'kind': kind, 'callId': identifier, 'operation': operation, 'threadId': 7}
                        for kind in ('load-enter', 'load-leave')])
    for identifier, stage in enumerate(('save-apply:input-header', 'save-json-root:consumed',
            'player-spawn:checkpoint-input', 'copy-checkpoint:enter', 'copy-checkpoint:leave'), 1):
        records.extend([{'kind': 'collection-payload', 'payloadId': identifier, 'stage': stage,
                         'byteLength': 4, 'regions': [{'name': 'input', 'offset': 0, 'size': 4}]},
                        {'kind': 'collection-payload-stored', 'payloadId': identifier}])
    records.append({'kind': 'collection-files', 'twoPassEqual': True})
    for name in ('preparation-before-exit', 'preparation-before-departure', 'encounter-started',
                 'resources-changed', 'death-result', 'post-death-menu'):
        if name == 'encounter-started':
            records.append({'kind': 'collection-task', 'task': {'word': 1}})
        records.append({'kind': 'collection-control-mark', 'name': name, 'playerConfirmedScene': True,
                        'result': {'twoPassEqual': True, 'knownRegionsComplete': True}})
    records.extend([{'kind': 'collection-task', 'task': {'word': 1}},
                    {'kind': 'host-detached', 'codeRestored': True, 'cleanupErrors': []},
                    {'kind': 'collection-archive-verified'}])
    return records


class CollectionReportTests(unittest.TestCase):
    def test_mark_before_actual_encounter_cannot_claim_that_stage(self):
        records = [item for item in fixture() if item.get('kind') != 'collection-task']
        records.insert(0, {'kind':'collection-task', 'task':{'word':2}})
        report = assess(records)
        self.assertIn('encounter-started', report['missingPlayerMarks'])
        self.assertIn('encounter mark lacks an observed encounter task', report['errors'])
        self.assertFalse(report['knownCoveragePresent'])

    def test_presence_is_not_restoration_or_complete_ownership(self):
        result = assess(fixture())
        self.assertTrue(result['knownCoveragePresent'])
        self.assertFalse(result['collectionComplete'])
        self.assertFalse(result['restorable'])
        self.assertFalse(result['inGameAcceptance'])

    def test_every_missing_required_boundary_blocks_departure(self):
        for operation in LOAD_OPERATIONS:
            records = [item for item in fixture() if item.get('operation') != operation]
            with self.subTest(operation=operation):
                self.assertFalse(assess(records)['departureEvidencePresent'])

    def test_every_missing_required_payload_blocks_departure(self):
        records = fixture()
        for payload in [item for item in records if item.get('kind') == 'collection-payload']:
            changed = [item for item in records if item is not payload]
            with self.subTest(stage=payload['stage']):
                self.assertFalse(assess(changed)['departureEvidencePresent'])

    def test_cross_thread_returns_and_unpaired_calls_are_rejected(self):
        records = fixture()
        next(item for item in records if item.get('kind') == 'load-leave')['threadId'] = 8
        result = assess(records)
        self.assertIn('unmatched native return', result['errors'])
        self.assertFalse(result['departureEvidencePresent'])

    def test_unacknowledged_bytes_and_gapped_sequences_are_not_ready(self):
        records = fixture()
        records = [item for item in records if not (item.get('kind') == 'collection-payload-stored' and item['payloadId'] == 1)]
        self.assertFalse(assess(records)['departureEvidencePresent'])
        records = fixture() + [{'sequence': 3}]
        self.assertIn('agent sequence gap or duplicate', assess(records)['errors'])

    def test_overlapping_payload_regions_cannot_pass_presence_check(self):
        records = fixture()
        payload = next(item for item in records if item.get('kind') == 'collection-payload')
        payload['regions'] = [{'size': 2, 'offset': 0}, {'size': 2, 'offset': 1}]
        self.assertFalse(assess(records)['departureEvidencePresent'])

    def test_unstable_or_partial_preparation_blocks_departure(self):
        for key in ('twoPassEqual', 'knownRegionsComplete'):
            records = fixture()
            next(item for item in records if item.get('name') == 'preparation-before-departure')['result'][key] = False
            self.assertFalse(assess(records)['departureEvidencePresent'])

    def test_task_word_two_does_not_replace_player_death_confirmation(self):
        records = [item for item in fixture() if item.get('name') != 'death-result']
        records.append({'kind': 'collection-task', 'task': {'word': 2}})
        result = assess(records)
        self.assertIn('death-result', result['missingPlayerMarks'])
        self.assertFalse(result['knownCoveragePresent'])

    def test_missing_cleanup_or_observer_failure_blocks_completion(self):
        for changed in ([item for item in fixture() if item.get('kind') != 'host-detached'],
                        fixture() + [{'kind': 'stopped', 'reason': 'load-error: capacity'}]):
            self.assertFalse(assess(changed)['knownCoveragePresent'])

    @requires('native-runtime-20260906/normal-exit-load-observe-05.jsonl')
    def test_previous_real_normal_roundtrip_is_not_misreported_as_new_collection(self):
        import json
        source = Path(__file__).resolve().parents[2] / 'artifacts/native-runtime-20260906/normal-exit-load-observe-05.jsonl'
        records = [json.loads(line) for line in source.read_text(encoding='utf-8').splitlines()]
        result = assess(records)
        self.assertFalse(result['normalRebuildEvidencePresent'])
        self.assertIn('save-apply', result['missingLoadOperations'])
        self.assertIn('save-json-root:consumed', result['missingPayloadStages'])

    def test_arbitrary_manifest_paths_and_corrupt_hashes_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaisesRegex(ProbeError, 'path'):
                verify_file(root, '../escape.bin', '0' * 64, 4)
            (root / ('0' * 64 + '.bin')).write_bytes(b'data')
            with self.assertRaisesRegex(ProbeError, 'hash mismatch'):
                verify_file(root, '0' * 64 + '.bin', '0' * 64, 4)


if __name__ == '__main__':
    unittest.main()
