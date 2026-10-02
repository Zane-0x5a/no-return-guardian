import copy
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

from NativeRecoverySource import (DEFAULT_METADATA, OBSERVED_CAPACITY, RUN_PROFILE_STATS, object_entries,
                                  prepare_payload, prepare_source, preparation_code, profile_run_stats,
                                  rollback_failed_recovery, verify_unloaded_menu)
from NativeSaveBuffer import BODY_OFFSET, ProbeError
from NativeSerializedArena import SerializedArena
from local_evidence import requires


def receipt(manifest, use='hideout'):
    """What VerifySnapshot prints for a manifest it verified."""
    return {'verified': True, 'id': manifest['Id'], 'schema': manifest['SchemaVersion'],
            'purpose': manifest['Purpose'], 'captureBasis': manifest['CaptureBasis'], 'kind': manifest['Kind'],
            'runStateCode': manifest['RunStateCode'], 'sourceProfilePath': manifest['SourceProfilePath'],
            'compositeSha256': manifest['CompositeSha256'], 'manifestSha256': manifest['ManifestSha256'],
            'use': use, 'files': {entry['RelativePath'].replace('\\', '/'): {'length': entry['Length'],
                                                                              'sha256': entry['Sha256']}
                                  for entry in manifest['Files']}}


def completed(code, stdout=b'', stderr=b''):
    return type('Result', (), {'returncode': code, 'stdout': stdout, 'stderr': stderr})()


@requires('native-runtime-20260912/collection-insurance-storage', 'native-runtime-20261001/dead-drop')
class RecoverySourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.storage = Path(__file__).resolve().parents[2] / 'artifacts/native-runtime-20260912/collection-insurance-storage'
        cls.snapshot_id = '20260912-072519061-manual-62fed724'
        directory = cls.storage / 'snapshots' / cls.snapshot_id
        cls.manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
        cls.profile = Path(cls.manifest['SourceProfilePath'])
        cls.raw = (directory / 'payload/gamedata/R0A.save').read_bytes()
        cls.profile_raw = (directory / 'payload/gamedata/0P.save').read_bytes()
        cls.dead_drop = Path(__file__).resolve().parents[2] / 'artifacts/native-runtime-20261001/dead-drop'
        cls.verified = receipt(cls.manifest)

    def payload(self, verified=None, raw=None, profile_raw=None, redeploy=False):
        return prepare_payload(self.raw if raw is None else raw, self.verified if verified is None else verified,
                               self.profile, self.snapshot_id,
                               self.profile_raw if profile_raw is None else profile_raw, redeploy)

    def test_formal_verifier_and_packet_keep_the_exact_preparation_body(self):
        source = prepare_source(self.storage, self.snapshot_id, self.profile)
        self.assertEqual(source.packet[:len(self.raw)], self.raw)
        self.assertEqual(source.packet[len(self.raw):BODY_OFFSET + OBSERVED_CAPACITY],
                         bytes(OBSERVED_CAPACITY + BODY_OFFSET - len(self.raw)))
        config = source.configuration
        self.assertEqual(config['packetSha256'], hashlib.sha256(source.packet).hexdigest())
        self.assertEqual(config['bodySha256'], hashlib.sha256(source.packet[BODY_OFFSET:config['rawSize']]).hexdigest())
        self.assertEqual(config['pointerCount'], 217337)
        fields = struct.unpack('<' + 'I' * config['pointerCount'], source.packet[config['rawSize']:])
        self.assertEqual(list(fields), sorted(set(fields)))
        self.assertFalse(config['inGameAcceptance'])
        self.assertEqual(config['profileRawSha256'], hashlib.sha256(self.profile_raw).hexdigest())
        self.assertEqual([stat['sid'] for stat in config['runStats']], [f'{sid:016x}' for sid in RUN_PROFILE_STATS])
        self.assertEqual([stat['value'] for stat in config['runStats']], [0, 0, 0, 0, 0, 0, 0, 1, 0, 0])

    def test_profile_run_stats_read_the_protected_values_the_replays_leaked_past(self):
        protected = profile_run_stats((self.dead_drop / '0P-e221c7c9-protected-1516.save').read_bytes())
        leaked = profile_run_stats((self.dead_drop / '0P-4378b969-after-replays-1542.save').read_bytes())
        self.assertEqual([stat['value'] for stat in protected], [1, 0, 0, 0, 0, 1, 0, 0, 3, 1])
        # Two replays of the same encounter each marked another dead-drop item used; one encounter was cleared.
        self.assertEqual([stat['value'] for stat in leaked], [1, 1, 1, 0, 0, 1, 0, 0, 4, 1])

    def test_profile_run_stats_reject_changed_profile_bytes_and_foreign_saves(self):
        changed = self.profile_raw[:-1] + bytes([self.profile_raw[-1] ^ 1])
        with self.assertRaisesRegex(ProbeError, 'changed after formal verification'):
            self.payload(profile_raw=changed)
        verified = copy.deepcopy(self.verified)
        del verified['files']['gamedata/0P.save']
        with self.assertRaisesRegex(ProbeError, 'changed after formal verification'):
            self.payload(verified)
        body = bytearray(self.profile_raw)
        struct.pack_into('<I', body, BODY_OFFSET + 8, 27)
        for data in (self.raw, bytes(body), self.profile_raw[:BODY_OFFSET + 0x100], bytes([0x92]) + self.profile_raw[1:]):
            with self.subTest(size=len(data)), self.assertRaises(ProbeError):
                profile_run_stats(data)

    def test_profile_run_stats_require_every_integer_stat_in_range(self):
        stats_key = struct.pack('<Q', RUN_PROFILE_STATS[0])
        offset = self.profile_raw.index(stats_key)
        renamed = bytearray(self.profile_raw)
        renamed[offset:offset + 8] = struct.pack('<Q', 0x1111111111111111)
        with self.assertRaisesRegex(ProbeError, 'lacks integer run stat CA79BE3DDF469B1A'):
            profile_run_stats(bytes(renamed))
        arena = SerializedArena(self.profile_raw[BODY_OFFSET + 0x418:])
        stats = object_entries(arena, arena.u64(arena.u64(8)))[0xEE639CAD45B1994C]
        kind, slot = object_entries(arena, arena.u64(stats[1]))[RUN_PROFILE_STATS[7]]
        self.assertEqual(kind, 1)
        for value in (-1, 0x80000000):
            data = bytearray(self.profile_raw)
            struct.pack_into('<q', data, BODY_OFFSET + 0x418 + slot, value)
            with self.subTest(value=value), self.assertRaisesRegex(ProbeError, 'out of range'):
                profile_run_stats(bytes(data))

    def test_invalid_snapshot_ids_are_rejected_before_running_verifier(self):
        with patch('NativeRecoverySource.subprocess.run') as verifier:
            for value in ('../escape', 'a/b', 'a\\b', 'a:b', '', '.', 'a' * 129):
                with self.subTest(value=value), self.assertRaises(ProbeError):
                    prepare_source(self.storage, value, self.profile)
            verifier.assert_not_called()

    def test_formal_verifier_rejection_cannot_publish_a_source(self):
        with patch('NativeRecoverySource.subprocess.run') as verifier:
            verifier.return_value.returncode = 3
            verifier.return_value.stderr = b'corrupt formal manifest'
            with self.assertRaisesRegex(ProbeError, 'Formal preparation snapshot verification failed'):
                prepare_source(self.storage, self.snapshot_id, self.profile)

    def test_only_a_receipt_that_vouches_for_this_snapshot_is_used(self):
        for stdout, reason in ((b'VERIFIED id=x policy=hideout', 'unreadable'), (b'[]', 'does not vouch'),
                               (json.dumps({**self.verified, 'verified': False}).encode(), 'does not vouch'),
                               (json.dumps({**self.verified, 'id': 'other'}).encode(), 'does not vouch')):
            with self.subTest(stdout=stdout[:40]), \
                    patch('NativeRecoverySource.subprocess.run', return_value=completed(0, stdout)), \
                    self.assertRaisesRegex(ProbeError, reason):
                prepare_source(self.storage, self.snapshot_id, self.profile)

    def test_only_a_hot_preparation_of_the_selected_profile_is_a_source(self):
        for key, value in (('schema', 6), ('captureBasis', 'native_export_reconstruction'), ('id', 'other'),
                           ('sourceProfilePath', str(self.profile.parent / 'other'))):
            with self.subTest(key=key), self.assertRaisesRegex(ProbeError, 'not a confirmed preparation'):
                self.payload({**self.verified, key: value})

    def test_the_formal_verifier_alone_decides_hideout_or_restart_use(self):
        # An early departure capture was saved while the game handled the departure: its hideout has the route
        # board past this node and a refilled lockbox (2026-10-01), so it only replays the recorded departure.
        for use, redeploy, outcome in (('hideout', False, None), ('hideout', True, None),
                                       ('restart', False, 'can only restart its encounter'), ('restart', True, None),
                                       ('none', False, 'does not allow'), ('none', True, 'does not allow'),
                                       (None, False, 'does not allow')):
            with self.subTest(use=use, redeploy=redeploy):
                verified = {**self.verified, 'use': use}
                if outcome is None:
                    self.assertEqual(self.payload(verified, redeploy=redeploy).configuration['stateCode'],
                                     self.manifest['RunStateCode'])
                else:
                    with self.assertRaisesRegex(ProbeError, outcome):
                        self.payload(verified, redeploy=redeploy)
        with patch('NativeRecoverySource.subprocess.run',
                   return_value=completed(0, json.dumps({**self.verified, 'use': 'restart'}).encode())):
            with self.assertRaisesRegex(ProbeError, 'can only restart'):
                prepare_source(self.storage, self.snapshot_id, self.profile)
            self.assertEqual(prepare_source(self.storage, self.snapshot_id, self.profile, True)
                             .configuration['compositeSha256'], self.manifest['CompositeSha256'])

    def test_bytes_other_than_the_verified_ones_and_state_mismatch_are_rejected(self):
        with self.assertRaisesRegex(ProbeError, 'changed after formal verification'):
            self.payload(raw=self.raw[:-1] + bytes([self.raw[-1] ^ 1]))
        for change in ({'length': len(self.raw) + 1}, {'sha256': 'ab' * 32}, None):
            verified = copy.deepcopy(self.verified)
            if change is None:
                del verified['files']['gamedata/R0A.save']
            else:
                verified['files']['gamedata/R0A.save'].update(change)
            with self.subTest(change=change), self.assertRaisesRegex(ProbeError, 'changed after formal verification'):
                self.payload(verified)
        with self.assertRaisesRegex(ProbeError, 'disagrees'):
            self.payload({**self.verified, 'runStateCode': '042EFF03008000000000006D'})

    def test_metadata_uses_three_little_endian_words_and_rejects_ambiguous_or_unknown_codes(self):
        self.assertEqual(preparation_code('description\n[042EFF030080000000000001]'), self.manifest['RunStateCode'])
        self.assertEqual(len(DEFAULT_METADATA), 40)
        self.assertEqual(struct.unpack_from('<Q', DEFAULT_METADATA, 4)[0], 0xffffffffffffffff)
        self.assertEqual(struct.unpack_from('<I', DEFAULT_METADATA, 20)[0], 255)
        self.assertEqual(struct.unpack_from('<I', DEFAULT_METADATA, 32)[0], 0x10000)
        self.assertEqual(struct.unpack_from('<I', DEFAULT_METADATA, 36)[0], 0xffffffff)
        for text in ('', '[bad][042EFF030080000000000001]', '[042EFF030080000000000001]' * 2,
                     '[042EFF030080000200000001]', '[042EFF030082000000000001]',
                     '[000000010000000000000000]'):
            with self.subTest(text=text), self.assertRaises(ProbeError):
                preparation_code(text)

    def test_every_unloaded_menu_field_is_rechecked_before_native_access(self):
        from unittest.mock import Mock
        process = Mock()
        base = 0x10000000
        expected = {base + 0x415ac90 + 0x72c8: b'\0', base + 0x415ac90 + 0x520: struct.pack('<i', 2),
                    base + 0x9398730: struct.pack('<i', 2)}
        expected.update({base + 0x9341660 + offset: bytes(4) for offset in (0x58, 0x228, 0x3f8)})
        expected[base + 0x9341660 + 0x5e0] = struct.pack('<i', 20)
        process.read.side_effect = lambda address, size: expected[address]
        verify_unloaded_menu(process, base)
        self.assertEqual(process.read.call_count, 14)
        for bad_read in range(14):
            fields = [b'\0', struct.pack('<i', 2), struct.pack('<i', 2), bytes(4), bytes(4), bytes(4), struct.pack('<i', 20)]
            values = fields + list(reversed(fields))
            values[bad_read] = b'\xff' * len(values[bad_read])
            process.read.side_effect = values
            with self.subTest(bad_read=bad_read), self.assertRaises(ProbeError):
                verify_unloaded_menu(process, base)

    def test_failure_rollback_uses_the_bound_pre_restore_id_and_requires_verified_receipt(self):
        identity = {'pid': 11, 'birth': 22}
        records = [{'kind': 'host-preflight', **identity},
                   {'kind': 'recovery-source-verified', 'undo': {'id': 'pre-restore-id'}}]
        with tempfile.TemporaryDirectory() as directory, patch('NativeRecoverySource.subprocess.run') as undo:
            log = Path(directory) / 'recovery.jsonl'
            log.write_text('\n'.join(json.dumps(record) for record in records), encoding='utf-8')
            undo.return_value.returncode = 0
            undo.return_value.stdout = b'{"restoredSnapshotId":"pre-restore-id","diskRollbackVerified":true}'
            result = rollback_failed_recovery(log, self.storage, self.profile, identity)
            self.assertTrue(result['required'])
            self.assertEqual(undo.call_args.args[0][-1], 'pre-restore-id')
            undo.reset_mock()
            with self.assertRaisesRegex(ProbeError, 'another process'):
                rollback_failed_recovery(log, self.storage, self.profile, {'pid': 11, 'birth': 23})
            undo.assert_not_called()
            for receipt in (b'{}', b'[]', b'{"restoredSnapshotId":"other","diskRollbackVerified":true}'):
                undo.return_value.stdout = receipt
                with self.assertRaises(ProbeError):
                    rollback_failed_recovery(log, self.storage, self.profile, identity)
            undo.return_value.returncode = 4
            undo.return_value.stderr = b'game_running'
            with self.assertRaisesRegex(ProbeError, 'game_running'):
                rollback_failed_recovery(log, self.storage, self.profile, identity)

    def test_damaged_or_missing_recovery_logs_never_guess_an_undo(self):
        with tempfile.TemporaryDirectory() as directory, patch('NativeRecoverySource.subprocess.run') as undo:
            log = Path(directory) / 'recovery.jsonl'
            self.assertFalse(rollback_failed_recovery(log, self.storage, self.profile, {})['required'])
            for content in ('[]', '{'):
                log.write_text(content, encoding='utf-8')
                with self.assertRaisesRegex(ProbeError, 'damaged'):
                    rollback_failed_recovery(log, self.storage, self.profile, {})
            undo.assert_not_called()


if __name__ == '__main__':
    unittest.main()
