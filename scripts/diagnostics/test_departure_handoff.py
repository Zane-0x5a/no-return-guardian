"""Manual protection must hand its original source to the running departure watch."""
import hashlib
import os
import io
import json
import queue
import threading
from datetime import datetime, timezone
from pathlib import Path
import tempfile
import shutil
import subprocess
import unittest
from unittest import mock
from types import SimpleNamespace
from local_evidence import evidence, requires
from NativeDepartureRecorder import protect
from NativeRecoverySource import verify_formally
from NativeCodeSignature import VERIFIED_SHA256

from NativeDepartureStage import HideoutStage, STAGE_FILES, WORKING_FILES
from NativeDepartureRecorder import ManualHandoff, watch_stdin
from test_native_encounter_restart import opening, OBSERVED_MS


def inspect_opening(raw):
    from NativeDepartureStage import run_code
    return {'code': run_code(raw), 'run': raw.split(b'run=')[1].split(b';')[0].decode('ascii')}


class ManualHandoffTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.profile = self.root / 'profile'
        self.when = (OBSERVED_MS - 60_000) * 1_000_000
        for name in STAGE_FILES:
            target = self.profile / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(opening(29, 'same-run') if name in WORKING_FILES[:2] else b'profile-and-metadata')
            os.utime(target, ns=(self.when, self.when))
        self.playable = True
        self.clock = OBSERVED_MS / 1000 - 30
        self.stage = HideoutStage(self.root / 'storage', self.profile, lambda: self.playable,
                                  self.when + 10**9, birth_ns=self.when - 10**9,
                                  clock=lambda: self.clock, inspect=inspect_opening)
        self.addCleanup(self.stage.close)
        self.source = {
            'SnapshotId': '20261024-084744088-manual-daaff41a', 'ManifestSha256': 'ab' * 32,
            'CaptureBasis': 'hot_synthesized_state', 'ConfirmedUtc': '2026-10-01T00:00:00Z',
            'ProfilePath': str(self.profile),
            'Files': [{'RelativePath': name, 'Length': (self.profile / name).stat().st_size,
                       'Sha256': hashlib.sha256((self.profile / name).read_bytes()).hexdigest(),
                       'LastWriteUtc': None} for name in WORKING_FILES]}

    def depart(self):
        for name in WORKING_FILES:
            target = self.profile / name
            target.write_bytes(opening(30, 'same-run'))
            os.utime(target, ns=((OBSERVED_MS + 300) * 1_000_000,) * 2)

    def test_mid_hideout_manual_confirmation_freezes_the_exact_nonopening_original(self):
        self.stage.poll()
        self.assertIsNone(self.stage.staged)
        self.assertTrue(self.stage.confirm_manual(self.source))
        frozen = self.stage.staged
        self.assertEqual(frozen['basis'], 'confirmed_manual_save')
        self.assertEqual(frozen['manualSnapshot'], self.source['SnapshotId'])
        self.assertEqual((frozen['path'] / WORKING_FILES[0]).read_bytes(), opening(29, 'same-run'))
        self.depart()
        self.assertIs(self.stage.bind(OBSERVED_MS, [0, 0, 0], lambda: False), frozen)

    def test_a_changed_source_or_nonplayable_hideout_is_not_handed_off(self):
        for change in ('hash', 'length', 'missing', 'scene'):
            with self.subTest(change=change):
                source = {**self.source, 'Files': [dict(item) for item in self.source['Files']]}
                if change == 'hash': source['Files'][0]['Sha256'] = 'cd' * 32
                if change == 'length': source['Files'][0]['Length'] += 1
                if change == 'missing': source['Files'].pop()
                self.playable = change != 'scene'
                self.assertFalse(self.stage.confirm_manual(source))
                self.assertIsNone(self.stage.staged)

    def test_a_later_observed_hideout_save_replaces_the_confirmed_source(self):
        self.assertTrue(self.stage.confirm_manual(self.source))
        old = self.stage.staged['path']
        for name in WORKING_FILES:
            target = self.profile / name
            target.write_bytes(opening(35, 'same-run'))
            os.utime(target, ns=(self.when + 2 * 10**9,) * 2)
        self.clock += 1
        self.stage.poll()
        self.assertEqual(self.stage.staged['basis'], 'pre_departure_save')
        self.assertNotIn('manualSnapshot', self.stage.staged)
        self.assertFalse(old.exists())

    def test_handoff_does_not_replace_an_identical_continuously_observed_copy(self):
        self.stage.not_before_ns = self.when - 1
        self.stage.poll()
        old = self.stage.staged
        self.assertTrue(self.stage.confirm_manual(self.source))
        self.assertIs(self.stage.staged, old)

    def handoff(self, command=None, source=None, departed=False):
        birth = (self.when // 100) + 116444736000000000 - 10**7
        inbox, records = queue.Queue(maxsize=16), []
        source = dict(source or self.source)
        source['ConfirmedUtc'] = datetime.fromtimestamp(self.clock - 1, timezone.utc).isoformat()
        packet = {'kind': 'confirm-manual', 'snapshotId': source['SnapshotId'], 'pid': 11, 'birth': str(birth)}
        packet.update(command or {})
        inbox.put(packet)
        handoff = ManualHandoff(SimpleNamespace(snapshot=None, storage=self.root / 'storage',
                                               profile=self.profile, pid=11), birth, inbox, records.append)
        handoff.departed = departed
        receipt = dict(verified=True, id=source['SnapshotId'], kind='manual', use='hideout',
                       manifestSha256=source['ManifestSha256'], manualSource=source)
        with mock.patch('NativeDepartureRecorder.verify_formally', return_value=receipt):
            handoff.apply(self.stage, lambda: self.playable, now=lambda: self.clock)
        return handoff, records

    def test_the_running_recorder_consumes_a_manual_confirmation_without_replacing_its_stage(self):
        handoff, records = self.handoff()
        self.assertEqual(handoff.owner, self.source['SnapshotId'])
        self.assertEqual(records[-1]['kind'], 'departure-manual-handoff')
        self.assertTrue(records[-1]['sourceFrozen'])
        self.assertIsNotNone(self.stage.staged)

    def test_wrong_process_profile_expiry_or_departure_cannot_authorize_an_old_source(self):
        for variant in ('pid', 'birth', 'profile', 'expiry', 'departed', 'scene'):
            with self.subTest(variant=variant):
                source = dict(self.source)
                packet = {variant: 99} if variant in ('pid', 'birth') else {}
                if variant == 'profile': source['ProfilePath'] = str(self.root / 'different-profile')
                if variant == 'expiry': self.clock -= 120
                self.playable = variant != 'scene'
                if variant == 'expiry':
                    with mock.patch('NativeDepartureRecorder.timestamp_ns', return_value=int((self.clock - 90) * 10**9)):
                        handoff, records = self.handoff(packet, source)
                    self.clock += 120
                else:
                    handoff, records = self.handoff(packet, source, departed=variant == 'departed')
                self.assertIsNone(handoff.owner)
                self.assertEqual(records[-1]['kind'], 'departure-manual-handoff-refused')
                self.assertIsNone(self.stage.staged)

    def test_the_command_pipe_is_bounded_and_eof_still_stops_the_recorder(self):
        packet = dict(kind='confirm-manual', snapshotId=self.source['SnapshotId'])
        stopping, inbox = threading.Event(), queue.Queue(maxsize=16)
        with mock.patch('sys.stdin', io.StringIO('invalid\n' + json.dumps(packet) + '\n')):
            watch_stdin(stopping, inbox)
        self.assertTrue(stopping.is_set())
        self.assertEqual(inbox.get_nowait(), packet)
        stopping.clear()
        with mock.patch('sys.stdin', io.StringIO('x' * 5000)):
            watch_stdin(stopping, inbox)
        self.assertTrue(stopping.is_set())
        self.assertTrue(inbox.empty())


class RealManualHandoffEvidenceTests(unittest.TestCase):
    @requires('ellie-skill-tree-20261019/live-20261024-first-hideout/observation.json')
    def test_the_real_missing_tree_original_reaches_formal_departure_capture_after_a_late_start(self):
        sample = evidence('ellie-skill-tree-20261019/live-20261024-first-hideout')
        helper = Path(__file__).resolve().parents[2] / 'build-cache/TestSkillCapture.exe'
        for mode in ('working', 'native'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory(prefix='guardian-handoff-', dir='D:/Projects/Temp') as directory:
                root, logs = Path(directory), []
                profile, storage = root / 'profile', root / 'guardian'
                shutil.copytree(sample / 'profile', profile)
                original_working = {name: (profile / name).read_bytes() for name in WORKING_FILES}
                result = subprocess.run([str(helper), str(profile), str(storage), str(sample / 'observation.json'), mode],
                                        capture_output=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8-sig'))
                manual = json.loads(result.stdout.decode('utf-8-sig'))['id']
                receipt = verify_formally(storage, manual)
                source = receipt['manualSource']
                now = datetime.now(timezone.utc).timestamp()
                birth = int((now - 3600) * 10**7) + 116444736000000000
                stage = HideoutStage(storage, profile, lambda: True, int(now * 10**9),
                                      birth_ns=int((now - 3600) * 10**9))
                self.addCleanup(stage.close)
                stage.poll()
                self.assertIsNone(stage.staged, 'the 29-second source is neither newly observed nor a run opening')
                inbox = queue.Queue(maxsize=16)
                inbox.put(dict(kind='confirm-manual', snapshotId=manual, pid=11, birth=str(birth)))
                args = SimpleNamespace(storage=storage, profile=profile, snapshot=None, pid=11)
                handoff = ManualHandoff(args, birth, inbox, logs.append)
                handoff.apply(stage, lambda: True)
                self.assertTrue(logs[-1].get('sourceFrozen'), logs)
                self.assertEqual(stage.staged['manualSnapshot'], manual)
                self.assertEqual(stage.staged['basis'], 'confirmed_manual_export' if mode == 'native'
                                 else 'confirmed_manual_save')
                event_ms = int(datetime.now(timezone.utc).timestamp() * 1000) + 10
                observed = json.loads((sample / 'observation.json').read_text(encoding='utf-8'))
                captured = dict(time=event_ms, workers=[0, 0, 0], skillObservation=observed)
                for name, data in original_working.items():
                    target = profile / name
                    target.write_bytes(data)
                    os.utime(target, ns=((event_ms + 300) * 1_000_000,) * 2)
                owner, published = protect(args, stage, captured, 0, handoff.owner, threading.Event(),
                                           logs.append, birth=birth, digest=VERIFIED_SHA256)
                self.assertIsNotNone(published, logs)
                formal = verify_formally(storage, published)
                self.assertEqual(formal['use'], 'hideout')
                automatic = storage / 'snapshots' / owner
                protected = storage / 'snapshots' / manual
                self.assertEqual((automatic / 'payload/gamedata/R0A.save').read_bytes(),
                                 (protected / 'payload/gamedata/R0A.save').read_bytes())
                self.assertEqual(formal['schema'], 7)
                self.assertEqual((automatic / 'evidence/skill-original-r0a.bin').read_bytes(), original_working[WORKING_FILES[0]])
                stage.close()


if __name__ == '__main__':
    unittest.main()
