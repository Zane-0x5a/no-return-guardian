"""Skill protection regressions through Guardian's formal verifier and real save format."""
import json
from pathlib import Path
import subprocess
import hashlib
import shutil
import tempfile
from datetime import datetime, timezone
import unittest

from local_evidence import evidence, requires
from NativeCheckpointProbe import TOOLS


SESSION = 'ellie-skill-tree-20261019/live-20261023-01'
SNAPSHOT = '20261023-120144718-departure-260dfad8'


class SkillProtectionEvidenceTests(unittest.TestCase):
    @requires('ellie-skill-tree-20261019/live-20261024-first-hideout/observation.json')
    def test_conflicting_live_observations_publish_nothing(self):
        sample = evidence('ellie-skill-tree-20261019/live-20261024-first-hideout')
        helper = Path(__file__).resolve().parents[2] / 'build-cache/TestSkillCapture.exe'
        observed = json.loads((sample / 'observation.json').read_text(encoding='utf-8'))
        conflicts = dict(CharacterIndex=1, RunId='0' * 32, Seed='0' * 32, CompletedEncounters=1,
                         Difficulty=5, Supplements=20, Upgrades='01' + observed['Upgrades'][2:],
                         Manuals=['2787c0e3ef93b5a5', '116353b2ccce482b'])
        for key, value in conflicts.items():
            with self.subTest(field=key), tempfile.TemporaryDirectory(prefix='guardian-skill-', dir='D:/Projects/Temp') as directory:
                root = Path(directory)
                profile = root / 'profile'
                shutil.copytree(sample / 'profile', profile)
                altered = dict(observed, **{key: value})
                observation = root / 'observation.json'
                observation.write_text(json.dumps(altered), encoding='utf-8')
                result = subprocess.run([str(helper), str(profile), str(root / 'guardian'), str(observation), 'working'],
                                        capture_output=True, timeout=30)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(list((root / 'guardian/snapshots').glob('*/manifest.json')))
                self.assertEqual((profile / 'gamedata/R0A.save').read_bytes(),
                                 (sample / 'profile/gamedata/R0A.save').read_bytes())

    @requires('ellie-skill-tree-20261019/live-20261024-first-hideout/observation.json')
    def test_manual_working_and_native_export_captures_use_the_same_completion(self):
        from NativeRecoverySource import prepare_payload
        sample = evidence('ellie-skill-tree-20261019/live-20261024-first-hideout')
        helper = Path(__file__).resolve().parents[2] / 'build-cache/TestSkillCapture.exe'
        self.assertTrue(helper.is_file(), 'Build the fixture with scripts/test-all.ps1')
        for mode in ('working', 'native'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory(prefix='guardian-skill-', dir='D:/Projects/Temp') as directory:
                root = Path(directory)
                profile, storage = root / 'profile', root / 'guardian'
                shutil.copytree(sample / 'profile', profile)
                result = subprocess.run([str(helper), str(profile), str(storage), str(sample / 'observation.json'), mode],
                                        capture_output=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8-sig'))
                receipt = json.loads(result.stdout.decode('utf-8-sig'))
                self.assertEqual((receipt['schema'], receipt['use']), (7, 'hideout'))
                point = storage / 'snapshots' / receipt['id']
                checked = subprocess.run([str(TOOLS / 'VerifySnapshot.exe'), str(storage), receipt['id']],
                                         capture_output=True, timeout=30)
                self.assertEqual(checked.returncode, 0, checked.stderr.decode('utf-8-sig'))
                raw = (point / 'payload/gamedata/R0A.save').read_bytes()
                prepared = prepare_payload(raw, json.loads(checked.stdout.decode('utf-8-sig')), profile, receipt['id'],
                                           (point / 'payload/gamedata/0P.save').read_bytes())
                self.assertEqual(prepared.configuration['rawSha256'], hashlib.sha256(raw).hexdigest())
                original = (sample / 'profile/gamedata/R0A.save').read_bytes()
                self.assertEqual(sum(a != b for a, b in zip(original, raw)), 20)
                if mode == 'working':
                    # Protecting the already completed preparation must not add another manual or cache item.
                    for name in ('R0A.save', 'R0A.save-backup'):
                        import os
                        path = profile / 'gamedata' / name
                        stamp = path.stat()
                        path.write_bytes(raw)
                        os.utime(path, ns=(stamp.st_atime_ns, stamp.st_mtime_ns))
                    again = subprocess.run([str(helper), str(profile), str(storage), str(sample / 'observation.json'), mode],
                                           capture_output=True, timeout=30)
                    self.assertEqual(again.returncode, 0, again.stderr.decode('utf-8-sig'))
                    second = storage / 'snapshots' / json.loads(again.stdout.decode('utf-8-sig'))['id']
                    self.assertEqual((second / 'payload/gamedata/R0A.save').read_bytes(), raw)

    @requires('ellie-skill-tree-20261019/live-20261024-first-hideout/observation.json')
    def test_formal_capture_completes_the_observed_tree_and_verifier_recomputes_it(self):
        sample = evidence('ellie-skill-tree-20261019/live-20261024-first-hideout')
        with tempfile.TemporaryDirectory(prefix='guardian-skill-', dir='D:/Projects/Temp') as directory:
            storage = Path(directory)
            profile = sample / 'profile'
            staged = storage / 'departure-staging' / 'test-boundary'
            shutil.copytree(profile, staged)
            observed = json.loads((sample / 'observation.json').read_text(encoding='utf-8'))
            event_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
            observed.update(ProfilePath=str(profile.resolve()), Boundary='departure',
                            DepartureObservedMs=event_ms,
                            ObservedUtc=datetime.fromtimestamp(event_ms / 1000, timezone.utc).isoformat())
            observation_path = staged / 'skill-observation.json'
            observation_path.write_text(json.dumps(observed), encoding='utf-8')
            command = [str(TOOLS / 'CaptureDepartureSnapshot.exe'), str(storage), str(profile), '0',
                       str(event_ms), observed['OriginalRunSha256'], str(staged), 'pre_departure_save', str(observation_path)]
            result = subprocess.run(command, capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8-sig'))
            receipt = json.loads(result.stdout.decode('utf-8-sig'))
            point = storage / 'snapshots' / receipt['id']
            manifest = json.loads((point / 'manifest.json').read_text(encoding='utf-8-sig'))
            self.assertEqual(manifest['SchemaVersion'], 7)
            original = (profile / 'gamedata/R0A.save').read_bytes()
            completed = (point / 'payload/gamedata/R0A.save').read_bytes()
            self.assertNotEqual(completed, original)
            self.assertEqual((point / 'evidence/skill-original-r0a.bin').read_bytes(), original)
            self.assertEqual((point / 'payload/gamedata/R0A.save-backup').read_bytes(), completed)
            # Exact allowed edits are two inventories' count, empty-slot name and quantity. The complete
            # serialized arena is parsed by Core before publication; all other bytes must stay original.
            self.assertEqual(sum(a != b for a, b in zip(original, completed)), 20)
            self.assertEqual(manifest['SkillProtection']['Observation']['Manuals'], observed['Manuals'])
            verify = [str(TOOLS / 'VerifySnapshot.exe'), str(storage), receipt['id']]
            checked = subprocess.run(verify, capture_output=True, timeout=30)
            self.assertEqual(checked.returncode, 0, checked.stderr.decode('utf-8-sig'))
            from NativeRecoverySource import prepare_payload
            prepared = prepare_payload(completed, json.loads(checked.stdout.decode('utf-8-sig')),
                                       profile, receipt['id'], (point / 'payload/gamedata/0P.save').read_bytes())
            self.assertEqual(prepared.configuration['rawSha256'], hashlib.sha256(completed).hexdigest())
            # The separately hashed original is part of the proof, never part of the restore write set.
            corrupted = bytearray(original); corrupted[-1] ^= 1
            (point / 'evidence/skill-original-r0a.bin').write_bytes(corrupted)
            checked = subprocess.run(verify, capture_output=True, timeout=30)
            self.assertNotEqual(checked.returncode, 0)
            self.assertIn(b'snapshot_skill_evidence_hash_mismatch', checked.stderr)
            self.assertEqual((profile / 'gamedata/R0A.save').read_bytes(), original)
            # A stale event observation cannot be reused for another departure.
            observed['DepartureObservedMs'] -= 1
            observation_path.write_text(json.dumps(observed), encoding='utf-8')
            refused = subprocess.run(command, capture_output=True, timeout=30)
            self.assertNotEqual(refused.returncode, 0)
            self.assertIn(b'skill_observation_mismatch', refused.stderr)

    @requires('ellie-skill-tree-20261019/live-20261024-first-hideout/profile/gamedata/R0A.save')
    def test_pre_restore_captures_missing_tree_bytes_exactly(self):
        profile = evidence('ellie-skill-tree-20261019/live-20261024-first-hideout/profile')
        with tempfile.TemporaryDirectory(prefix='guardian-skill-', dir='D:/Projects/Temp') as directory:
            result = subprocess.run([str(TOOLS / 'CaptureLiveSnapshot.exe'), directory, str(profile)],
                                    capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8-sig'))
            receipt = json.loads(result.stdout.decode('utf-8-sig'))
            point = Path(directory) / 'snapshots' / receipt['id']
            self.assertEqual((point / 'payload/gamedata/R0A.save').read_bytes(),
                             (profile / 'gamedata/R0A.save').read_bytes())

    @requires(SESSION + '/guardian-points/' + SNAPSHOT + '/manifest.json')
    def test_known_missing_ellie_tree_is_not_a_complete_preparation(self):
        # The snapshots directory name is part of the formal store; create only a diagnostic junction-free
        # temporary store, never alter the original player's snapshot or Guardian's live storage.
        import shutil
        import tempfile
        with tempfile.TemporaryDirectory(prefix='guardian-skill-', dir='D:/Projects/Temp') as directory:
            storage = Path(directory)
            shutil.copytree(evidence(SESSION) / 'guardian-points' / SNAPSHOT,
                            storage / 'snapshots' / SNAPSHOT)
            result = subprocess.run([str(TOOLS / 'VerifySnapshot.exe'), str(storage), SNAPSHOT],
                                    capture_output=True, timeout=30)
            self.assertNotEqual(result.returncode, 0,
                                'formal verifier accepts the real preparation that loses Ellie\'s starting tree')
            self.assertIn(b'skill_state_incomplete', result.stderr)


if __name__ == '__main__':
    unittest.main()
