import contextlib
import io
import itertools
import json
import os
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest import mock

from NativeCheckpointProbe import ProbeError
from NativeCodeSignature import VERIFIED_SHA256
from NativeDepartureRecord import departure_slots, load_departure, write_departure
from NativeDepartureRecorder import record_departure
from NativeDepartureStage import STAGE_FILES, WORKING_FILES, HideoutStage, protect_departure
from NativeEncounterRestart import (AttachedSession, CONTROL_RVAS, abandon_encounter, arm_decision,
                                    redeploy_departure, replay_in_hideout, same_encounter)
from NativeCheckpointTrigger import IN_HIDEOUT, RecoveryRequest, arguments, resolve_entry, supervised_main


SLOT = '05000000000000000000000002000000'
# Real 2026-10-01 route-board departures (wait-for-input state): int32 route index, then the photo's boolean.
BOARD = '0300000000000000820000000200000000000000000000002400000001000000'
BOARD_FIRST = '0000000000000000000000000200000000000000000000000000000001000000'
CODE = [{'rva': rva, 'bytes': f'{index:02x}' * 32} for index, rva in enumerate(CONTROL_RVAS, 1)]
OBSERVED_MS = 1790836977984
BOUND = 'ab' * 32


def departure(snapshot='target', **fields):
    record = {'schema': 1, 'snapshotId': snapshot, 'buildSha256': VERIFIED_SHA256,
              'eventSid': 'A9456E5567D0CE70', 'argumentCount': 1, 'slots': SLOT, 'routeIndex': 5,
              'departureConfirmed': True, 'encounter': {'task': '0x9000', 'subnode': '0000000000002222'}}
    record.update(fields)
    return record


class GameCode:
    def __init__(self, restored=True):
        self.restored = restored

    def read(self, address, size):
        item = next(item for item in CODE if item['rva'] == address)
        return bytes.fromhex(item['bytes']) if self.restored else bytes(size)


class DepartureRecordTests(unittest.TestCase):
    def test_only_a_route_index_and_an_optional_photo_boolean_are_accepted(self):
        self.assertEqual(departure_slots(SLOT, 1)[1], 5)
        self.assertEqual(departure_slots(BOARD, 2)[1], 3)
        self.assertEqual(departure_slots(BOARD_FIRST, 2)[1], 0)
        for slots, count in (('05000000000000000000000001000000', 1), ('ff000000000000000000000002000000', 1),
                             (SLOT * 2, 2), (SLOT, 2), (BOARD, 1), (BOARD + SLOT, 3), (SLOT[:-2], 1),
                             ('not hex', 1), (None, 1), (SLOT, None)):
            with self.subTest(slots=slots, count=count), self.assertRaises(ProbeError):
                departure_slots(slots, count)

    def test_record_round_trips_and_rejects_other_preparations_builds_and_unconfirmed_departures(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ProbeError, 'no recorded departure'):
                load_departure(directory, 'target')
            write_departure(directory, departure(argumentCount=2, slots=BOARD, routeIndex=3))
            self.assertEqual(load_departure(directory, 'target')['routeIndex'], 3)
            write_departure(directory, departure())
            self.assertEqual(load_departure(directory, 'target')['routeIndex'], 5)
            self.assertEqual(list((Path(directory) / 'departures').iterdir()), [Path(directory) / 'departures/target.json'])
            for variant in (dict(snapshotId='other'), dict(eventSid='0'),
                            dict(departureConfirmed=False), dict(routeIndex=4), dict(argumentCount=2),
                            dict(argumentCount=None), dict(schema=2)):
                with self.subTest(variant=variant):
                    (Path(directory) / 'departures/target.json').write_text(
                        json.dumps(departure(**variant)), encoding='utf-8')
                    with self.assertRaises(ProbeError):
                        load_departure(directory, 'target')
            with self.assertRaises(ProbeError):
                load_departure(directory, '../escape')

    def test_recorder_arms_only_in_the_snapshot_hideout_and_never_after_a_missed_departure(self):
        def state(active, initialized, word):
            return {'runActive': active, 'runInitialized': initialized, 'taskWord': word}
        self.assertEqual(arm_decision(state(1, 1, 2)), 'arm')
        self.assertEqual(arm_decision(state(1, 1, 1)), 'stop')
        for waiting in (state(0, 0, None), state(0, 1, 2), state(1, 0, 2), state(1, 1, None)):
            self.assertEqual(arm_decision(waiting), 'wait')

    def test_encounter_identity_needs_the_task_object_of_the_recording_process(self):
        # Three live 2026-10-01 departures to different routes all carried subnode ebada5168620c5fe.
        recorded = {'task': '0x11b6bba790', 'subnode': 'ebada5168620c5fe'}
        self.assertTrue(same_encounter(recorded, {'task': '0x11b6bba790', 'subnode': 'ebada5168620c5fe'}, True))
        self.assertFalse(same_encounter(recorded, {'task': '0x11b6bc0198', 'subnode': 'ebada5168620c5fe'}, True))
        self.assertIsNone(same_encounter(recorded, {'task': '0x11b6bba790', 'subnode': 'ebada5168620c5fe'}))
        self.assertFalse(same_encounter({'task': '0x1', 'subnode': '1111'}, {'task': '0x1', 'subnode': '2222'}, True))
        self.assertIsNone(same_encounter(None, {'subnode': '2222'}))


class RedeployTests(unittest.TestCase):
    def run_redeploy(self, events, restored=True, budget=100, birth=22):
        records = []
        with mock.patch('NativeEncounterRestart.run_control', return_value=(events, [])) as control:
            errors = redeploy_departure(mock.Mock(), GameCode(restored), 0, 11, records.append,
                                        departure(pid=11, birth=22), CODE, budget, birth=birth)
        return errors, records, control

    def test_arrival_in_the_same_encounter_is_reported_without_claiming_player_acceptance(self):
        arrived = [{'kind': 'redeployed', 'task': {'task': '0x9000', 'subnode': '2222'}},
                   {'kind': 'encounter-control-stopped'}]
        errors, records, control = self.run_redeploy(arrived)
        self.assertEqual(errors, [])
        complete = next(record for record in records if record['kind'] == 'redeploy-complete')
        self.assertTrue(complete['sameEncounter'])
        self.assertFalse(complete['inGameAcceptance'])
        _, records, _ = self.run_redeploy(arrived, birth=33)
        self.assertIsNone(next(record for record in records if record['kind'] == 'redeploy-complete')['sameEncounter'])
        configuration = control.call_args.args[1]
        self.assertEqual((configuration['mode'], configuration['slots']), ('redeploy', SLOT))

    def test_missing_arrival_is_not_fatal_but_unproven_hook_removal_is(self):
        errors, records, _ = self.run_redeploy(
            [{'kind': 'encounter-control-stopped', 'reason': 'redeploy-not-observed'}])
        self.assertEqual(errors, [])
        self.assertEqual(records[-1]['kind'], 'redeploy-incomplete')
        errors, _, _ = self.run_redeploy([{'kind': 'redeployed', 'task': {}}], restored=False)
        self.assertEqual(len(errors), 1)

    def test_an_exhausted_supervisor_budget_skips_the_departure(self):
        errors, records, control = self.run_redeploy([], budget=10)
        self.assertEqual((errors, records[-1]['kind']), ([], 'redeploy-skipped'))
        control.assert_not_called()


class AbandonTests(unittest.TestCase):
    def results(self, page='t2r-menu-post/page-scores'):
        return {'initialized': 1, 'taskWord': 2, 'ui': {'topPage': page}}

    def run_abandon(self, events, observations, restored=True):
        session = mock.Mock()
        prefetch = mock.Mock()
        prefetch.take.return_value = session
        with tempfile.TemporaryDirectory() as directory, \
                mock.patch('NativeEncounterRestart.run_control', return_value=(events, [])), \
                mock.patch('NativeResultsExit.observe_results', side_effect=observations), \
                mock.patch('NativeEncounterRestart.time.sleep'):
            outcome = None
            error = None
            try:
                outcome = abandon_encounter(GameCode(restored), 0, 11, 22, Path(directory) / 'test.jsonl',
                                            mock.Mock(), CODE, prefetch, results_timeout=5)
            except ProbeError as caught:
                error = caught
            journal = [json.loads(line) for line in
                       (Path(directory) / 'test.encounter.jsonl').read_text(encoding='utf-8').splitlines()]
        return outcome, error, session, journal

    def test_abandon_hands_the_same_session_to_the_results_exit_once_results_are_visible(self):
        outcome, error, session, journal = self.run_abandon(
            [{'kind': 'abandon-broadcast'}, {'kind': 'abandon-observed'}],
            [ProbeError('playable'), self.results(None), self.results()])
        self.assertIsNone(error)
        receipt, holder = outcome
        self.assertTrue(receipt['abandoned'])
        self.assertIs(holder.take(), session)
        session.detach.assert_not_called()
        self.assertEqual(journal[-1]['kind'], 'abandon-complete')

    def test_unobserved_abandon_or_unproven_hook_removal_never_reaches_results(self):
        for events, restored, message in (([], True, 'nothing was changed'),
                                          ([{'kind': 'abandon-broadcast'}], True, 'did not end'),
                                          ([{'kind': 'abandon-observed'}], False, 'not proven removed')):
            with self.subTest(message=message):
                outcome, error, session, journal = self.run_abandon(events, [self.results()], restored)
                self.assertRegex(str(error), message)
                session.detach.assert_called_once_with()
                self.assertEqual(journal[-1]['kind'], 'abandon-stopped')
                self.assertFalse(journal[-1]['nativeRecoveryStarted'])

    def test_attached_session_is_handed_over_once(self):
        session = mock.Mock()
        holder = AttachedSession(session)
        self.assertIs(holder.take(), session)
        holder.close()
        session.detach.assert_not_called()


class EntryTests(unittest.TestCase):
    def values(self, root, *entry):
        return ['11', '--mode', 'recover-preparation', '--log', str(root / 'recovery.jsonl'),
                '--confirm-intrusive-experiment', '--recovery-storage', str(root),
                '--recovery-profile', str(root), '--recovery-snapshot', 'target', *entry]

    def test_mid_encounter_is_a_third_exclusive_entry_and_redeploy_only_modifies_recovery(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.assertEqual(arguments(self.values(root, '--entry', 'mid-encounter')).recovery,
                             RecoveryRequest(root, 'target', root, 'mid-encounter', False, False))
            self.assertTrue(arguments(self.values(root, '--entry', 'menu', '--redeploy')).recovery.redeploy)
            for sample in (self.values(root, '--entry', 'mid-encounter', '--entry', 'restart'),
                           self.values(root, '--entry', 'encounter-restart'),
                           self.values(root, '--redeploy'),
                           ['11', '--mode', 'observe', '--log', 'unused.jsonl', '--confirm-intrusive-experiment',
                            '--redeploy']):
                with self.subTest(sample=sample), contextlib.redirect_stderr(io.StringIO()), \
                        self.assertRaises(SystemExit):
                    arguments(sample)

    def preflight(self, root, entry, departure_error=None, observed=None):
        holder = mock.Mock()
        patches = contextlib.ExitStack()
        patches.enter_context(mock.patch('NativeCheckpointTrigger.ReadOnlyProcess'))
        patches.enter_context(mock.patch('NativeCheckpointTrigger.verify_build'))
        patches.enter_context(mock.patch('NativeCheckpointTrigger.process_birth', return_value=22))
        patches.enter_context(mock.patch('NativeCheckpointTrigger.fingerprints', return_value=[]))
        patches.enter_context(mock.patch('NativeCheckpointTrigger.verify_live'))
        patches.enter_context(mock.patch('NativeRecoveryLock.RecoveryLock'))
        state = observed or {'runActive': 1, 'runInitialized': 1, 'taskWord': 1}

        def unloaded_menu(process, base):
            if state['runInitialized'] != 0:
                raise ProbeError('Native recovery requires the unloaded No Return menu')

        patches.enter_context(mock.patch('NativeRecoverySource.verify_unloaded_menu', side_effect=unloaded_menu))
        mocks = {
            'holder': holder,
            'prepare': patches.enter_context(mock.patch('NativeRecoverySource.prepare_source')),
            # Stops a menu entry before the supervisor would start a real worker process.
            'secure': patches.enter_context(mock.patch('NativeRecoverySource.secure_live_state',
                                                       side_effect=ProbeError('live state secured'))),
            'prefetch': patches.enter_context(mock.patch('NativeResultsExit.SessionPrefetch')),
            'finish': patches.enter_context(mock.patch('NativeResultsExit.finish_results',
                                                       side_effect=ProbeError('results stalled'))),
            'abandon': patches.enter_context(mock.patch('NativeEncounterRestart.abandon_encounter',
                                                        return_value=({'abandoned': True}, holder))),
            'state': patches.enter_context(mock.patch('NativeEncounterRestart.hideout_state', return_value=state)),
            'departure': patches.enter_context(mock.patch(
                'NativeDepartureRecord.load_departure',
                side_effect=departure_error, return_value=departure())),
        }
        with patches:
            error = None
            try:
                supervised_main(self.values(root, *entry))
            except ProbeError as caught:
                error = caught
        return error, mocks

    def test_missing_departure_record_stops_before_any_attach_or_game_change(self):
        with tempfile.TemporaryDirectory() as directory:
            error, mocks = self.preflight(Path(directory), ('--entry', 'mid-encounter', '--redeploy'),
                                          departure_error=ProbeError('no recorded departure'))
            self.assertRegex(str(error), 'no recorded departure')
            for name in ('prefetch', 'prepare', 'abandon', 'finish', 'secure'):
                getattr(mocks[name], 'assert_not_called')()

    def test_mid_encounter_entry_rejects_non_encounter_states_before_attaching(self):
        with tempfile.TemporaryDirectory() as directory:
            error, mocks = self.preflight(Path(directory), ('--entry', 'mid-encounter'),
                                          observed={'runActive': 1, 'runInitialized': 1, 'taskWord': 2})
            self.assertRegex(str(error), 'active No Return encounter')
            mocks['prefetch'].assert_not_called()
            mocks['abandon'].assert_not_called()

    def test_mid_encounter_abandons_then_continues_results_with_the_same_session(self):
        with tempfile.TemporaryDirectory() as directory:
            error, mocks = self.preflight(Path(directory), ('--entry', 'mid-encounter', '--redeploy'))
            self.assertRegex(str(error), 'results stalled')
            mocks['abandon'].assert_called_once()
            self.assertIs(mocks['abandon'].call_args.args[7], mocks['prefetch'].return_value)
            self.assertIs(mocks['finish'].call_args.kwargs['prefetch'], mocks['holder'])
            mocks['holder'].close.assert_called_once_with()
            mocks['secure'].assert_not_called()


    def test_an_automatic_restart_abandons_only_inside_an_active_encounter(self):
        with tempfile.TemporaryDirectory() as directory:
            _, mocks = self.preflight(Path(directory), ('--entry', 'auto', '--redeploy'))
            mocks['abandon'].assert_called_once()
            _, mocks = self.preflight(Path(directory), ('--entry', 'auto', '--redeploy'),
                                      observed={'runActive': 0, 'runInitialized': 1, 'taskWord': 2})
            mocks['abandon'].assert_not_called()
            mocks['finish'].assert_called_once()

    def test_the_automatic_entry_starts_wherever_the_player_is(self):
        menu = mock.Mock()
        not_menu = mock.Mock(side_effect=ProbeError('not the menu'))
        for observed, unloaded, expected in (
                ({'runActive': 1, 'runInitialized': 1, 'taskWord': 1}, not_menu, 'mid-encounter'),
                ({'runActive': 1, 'runInitialized': 1, 'taskWord': 2}, not_menu, 'hideout'),
                ({'runActive': 0, 'runInitialized': 0, 'taskWord': None}, menu, 'menu'),
                ({'runActive': 0, 'runInitialized': 1, 'taskWord': 2}, not_menu, 'results'),
                # A loading run is neither playable nor the menu; the results gate then refuses it.
                ({'runActive': 1, 'runInitialized': 0, 'taskWord': None}, not_menu, 'results')):
            with self.subTest(expected=expected, observed=observed):
                self.assertEqual(resolve_entry(observed, unloaded), expected)

    def test_a_hideout_departs_only_when_guardian_vouches_for_its_rebuild(self):
        hideout = {'runActive': 1, 'runInitialized': 1, 'taskWord': 2}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for entry in (('--entry', 'auto'), ('--entry', 'auto', '--redeploy')):
                with self.subTest(entry=entry):
                    error, mocks = self.preflight(root, entry, observed=hideout)
                    self.assertEqual(str(error), IN_HIDEOUT)
                    for name in ('prefetch', 'prepare', 'abandon', 'finish', 'secure'):
                        getattr(mocks[name], 'assert_not_called')()
            with mock.patch('NativeEncounterRestart.replay_in_hideout', return_value=0) as replay:
                error, mocks = self.preflight(root, ('--entry', 'auto', '--redeploy', '--hideout-rebuilt-from-snapshot'),
                                              observed=hideout)
            self.assertIsNone(error)
            replay.assert_called_once()
            self.assertEqual(replay.call_args.args[4], departure())
            # The verifier must still allow this preparation's encounter to be restarted.
            self.assertEqual(mocks['prepare'].call_args.args[3], True)
            for name in ('prefetch', 'abandon', 'finish', 'secure'):
                getattr(mocks[name], 'assert_not_called')()
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                arguments(self.values(root, '--entry', 'menu', '--redeploy', '--hideout-rebuilt-from-snapshot'))

    def test_the_automatic_entry_takes_the_menu_and_the_results_page_like_their_own_entries(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with mock.patch('NativeCheckpointTrigger.resolve_entry', return_value='menu'):
                error, mocks = self.preflight(root, ('--entry', 'auto', '--redeploy'),
                                              observed={'runActive': 0, 'runInitialized': 0, 'taskWord': None})
            self.assertRegex(str(error), 'live state secured')
            mocks['prefetch'].assert_not_called()
            mocks['secure'].assert_called_once()
            self.assertEqual(mocks['prepare'].call_args.args[3], True)
            with mock.patch('NativeCheckpointTrigger.resolve_entry', return_value='results'):
                error, mocks = self.preflight(root, ('--entry', 'auto'))
            self.assertRegex(str(error), 'results stalled')
            mocks['abandon'].assert_not_called()
            mocks['finish'].assert_called_once()
            self.assertEqual(mocks['prepare'].call_args.args[3], False)


class RecorderTests(unittest.TestCase):
    def hideout(self, word=2, active=1):
        return {'runActive': active, 'runInitialized': 1, 'story': 2, 'task': '0x9000', 'taskWord': word}

    def record(self, events, watched=(), arrival=None, snapshot='target', protecting=False, protect=None,
               before_armed=()):
        """Arm in the hideout, let the control loop poll `watched` states, then return `events`.

        `protecting` passes a save profile; `protect` then stands in for binding the departure save and
        publishing it, returning the new snapshot id or raising.
        """
        states = [*before_armed, self.hideout(), *watched, *([arrival] if arrival else [])]
        self.protected = []

        def publish(storage, profile, index, observed_ms, digest, staged):
            self.protected.append((profile.name, index, observed_ms, digest, staged))
            return {'id': protect(), 'kind': 'departure', 'runSha256': digest, 'routeIndex': index, 'verified': True}

        test = self

        class Stage:
            """Stands in for the frozen hideout save; `protect` decides whether binding it succeeds."""

            def __init__(self, storage, profile, playable, not_before_ns):
                test.stage = self
                self.polls, self.closed = 0, False

            def poll(self):
                self.polls += 1

            def bind(self, observed_ms, workers, cancelled):
                return {'sha256': BOUND, 'path': Path('frozen'), 'savedAtNs': (observed_ms - 4000) * 1_000_000}

            def close(self):
                self.closed = True

        def control(session, configuration, record, timeout, cancel=None):
            self.configuration = configuration
            self.cancelled = [cancel() for _ in watched]
            return events, []

        with tempfile.TemporaryDirectory() as directory, contextlib.ExitStack() as patches:
            root = Path(directory)
            (root / 'snapshots' / 'target').mkdir(parents=True)
            (root / 'snapshots' / 'target' / 'manifest.json').write_text('{}', encoding='utf-8')
            process = mock.Mock(image_base=mock.Mock(return_value=0))
            for target, value in (('NativeDepartureRecorder.ReadOnlyProcess', mock.Mock(return_value=process)),
                                  ('NativeDepartureRecorder.verify_build', mock.Mock(return_value=VERIFIED_SHA256)),
                                  ('NativeDepartureRecorder.hideout_state', mock.Mock(side_effect=states)),
                                  ('NativeDepartureRecorder.task_identity',
                                   mock.Mock(return_value={'task': '0x9000', 'subnode': '2222'})),
                                  ('NativeDepartureRecorder.code_restored', mock.Mock(return_value=True)),
                                  ('NativeDepartureRecorder.run_control', mock.Mock(side_effect=control)),
                                  ('NativeDepartureRecorder.watch_stdin', mock.Mock()),
                                  ('NativeDepartureRecorder.time.sleep', mock.Mock()),
                                  ('NativeEncounterRestart.time.monotonic',
                                   mock.Mock(side_effect=itertools.count(1000, 2))),
                                  ('NativeDepartureRecorder.fingerprints', mock.Mock(return_value=CODE)),
                                  ('NativeDepartureRecorder.process_birth', mock.Mock(return_value=22)),
                                  ('NativeDepartureRecorder.verify_live', mock.Mock()),
                                  ('NativeDepartureRecorder.HideoutStage', Stage),
                                  ('NativeDepartureRecorder.protect_departure', mock.Mock(side_effect=publish))):
                patches.enter_context(mock.patch(target, value))
            patches.enter_context(mock.patch.dict('sys.modules', {'frida': SimpleNamespace(
                attach=lambda pid: mock.Mock())}))
            patches.enter_context(contextlib.redirect_stdout(io.StringIO()))
            argv = ['11', '--storage', str(root), '--log', str(root / 'departure.jsonl')]
            if snapshot:
                argv += ['--snapshot', snapshot]
            if protecting:
                (root / 'profile').mkdir()
                argv += ['--profile', str(root / 'profile')]
            code = record_departure(argv)
            log = [json.loads(line) for line in (root / 'departure.jsonl').read_text(encoding='utf-8').splitlines()]
            self.departures = {path.stem: load_departure(root, path.stem)
                               for path in (root / 'departures').glob('*.json')}
            saved = self.departures.get('target')
        return code, log, saved

    def test_the_real_two_argument_board_departure_is_recorded_after_the_encounter_is_entered(self):
        code, log, saved = self.record([{'kind': 'departure-captured', 'count': 2, 'slots': BOARD},
                                        {'kind': 'encounter-control-stopped', 'reason': 'captured'}],
                                       arrival=self.hideout(word=1))
        self.assertEqual(code, 0)
        self.assertEqual((saved['argumentCount'], saved['slots'], saved['routeIndex'], saved['photoFlag']),
                         (2, BOARD, 3, False))
        self.assertEqual(saved['encounter']['subnode'], '2222')
        self.assertEqual(self.configuration['duration'], 43200)
        self.assertEqual(log[-2]['kind'], 'departure-recorded')

    def test_waiting_ends_when_the_hideout_is_left_and_nothing_is_recorded(self):
        for watched, reason in (([self.hideout(), self.hideout(active=0)], 'run-left'),
                                ([self.hideout(word=1)] * 2 + [self.hideout()] + [self.hideout(word=1)] * 3,
                                 'encounter-entered-without-capture')):
            with self.subTest(reason=reason):
                code, log, saved = self.record([{'kind': 'encounter-control-cancelled'}], watched)
                self.assertEqual((code, saved), (4, None))
                self.assertEqual(self.cancelled[-1], True)
                self.assertNotIn(True, self.cancelled[:-1])
                self.assertEqual(log[-2], {'kind': 'departure-recorder-stopped', 'reason': reason})

    def test_an_unknown_argument_shape_is_rejected_without_a_record(self):
        code, log, saved = self.record([{'kind': 'departure-captured', 'count': 2, 'slots': SLOT * 2}],
                                       arrival=self.hideout(word=1))
        self.assertEqual((code, saved), (3, None))
        self.assertRegex(log[-2]['reason'], 'not a boolean')

    def captured(self):
        return [{'kind': 'departure-captured', 'time': OBSERVED_MS, 'count': 2, 'slots': BOARD},
                {'kind': 'encounter-control-stopped', 'reason': 'captured'}]

    def test_a_protected_departure_save_becomes_the_preparation_that_owns_the_departure(self):
        for owner in (None, 'target'):
            with self.subTest(owner=owner):
                code, log, _ = self.record(self.captured(), arrival=self.hideout(word=1), snapshot=owner,
                                           protecting=True, protect=lambda: 'departure-new')
                self.assertEqual(code, 0)
                self.assertEqual(list(self.departures), ['departure-new'])
                saved = self.departures['departure-new']
                self.assertEqual((saved['snapshotId'], saved['autoProtected'], saved['routeIndex'], saved['slots']),
                                 ('departure-new', True, 3, BOARD))
                self.assertEqual(self.protected, [('profile', 3, OBSERVED_MS, BOUND, Path('frozen'))])
                kinds = [entry['kind'] for entry in log]
                self.assertLess(kinds.index('departure-protected'), kinds.index('departure-recorded'))
                bound = next(entry for entry in log if entry['kind'] == 'departure-save-bound')
                self.assertEqual((bound['basis'], bound['savedAtMs']), ('pre_departure_save', OBSERVED_MS - 4000))
                self.assertTrue(self.stage.closed)

    def test_a_recorder_without_a_profile_freezes_nothing(self):
        self.stage = None
        code, _, saved = self.record(self.captured(), watched=[self.hideout()], arrival=self.hideout(word=1))
        self.assertEqual((code, saved['autoProtected'], self.stage), (0, False, None))

    def test_the_hideout_is_watched_for_saves_while_waiting_for_the_departure(self):
        self.record(self.captured(), watched=[self.hideout()] * 3, arrival=self.hideout(word=1), protecting=True,
                    protect=lambda: 'departure-new')
        self.assertEqual(self.stage.polls, 3)

    def test_an_unprotected_departure_falls_back_to_its_owner_or_records_nothing(self):
        def missed():
            raise ProbeError('departure-save-not-observed')

        code, log, saved = self.record(self.captured(), arrival=self.hideout(word=1), protecting=True, protect=missed)
        self.assertEqual((code, saved['snapshotId'], saved['autoProtected']), (0, 'target', False))
        self.assertIn({'kind': 'departure-protect-failed', 'reason': 'departure-save-not-observed',
                       'fallbackSnapshotId': 'target'}, log)
        code, log, _ = self.record(self.captured(), arrival=self.hideout(word=1), snapshot=None, protecting=True,
                                   protect=missed)
        self.assertEqual((code, self.departures), (4, {}))
        self.assertEqual(log[-2], {'kind': 'departure-recorder-stopped',
                                   'reason': 'departure-not-protected: departure-save-not-observed'})

    def test_a_protecting_recorder_waits_through_encounters_but_their_owner_does_not_carry_over(self):
        code, log, _ = self.record(self.captured(), arrival=self.hideout(word=1), protecting=True,
                                   protect=lambda: 'departure-new', before_armed=[self.hideout(word=1)])
        self.assertEqual(code, 0)
        self.assertEqual(log[1]['kind'], 'departure-fallback-dropped')
        self.assertEqual(list(self.departures), ['departure-new'])
        code, log, _ = self.record(self.captured(), before_armed=[self.hideout(word=1)])
        self.assertEqual((code, log[-2]['reason'], self.departures), (4, 'encounter-before-armed', {}))


class HideoutStageTests(unittest.TestCase):
    """Only a settled save written in this hideout is frozen, and it binds only if it was the last one before
    the departure: the game's departure save has already reset the hideout for the next visit."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        root = Path(self.directory.name)
        self.storage, self.profile = root / 'storage', root / 'profile'
        self.storage.mkdir()
        self.clock = [OBSERVED_MS / 1000 - 60]
        self.playable = True
        # The encounter's last save is still on disk when the hideout first becomes playable.
        self.save(OBSERVED_MS - 50_000, b'encounter', STAGE_FILES)
        self.stage = HideoutStage(self.storage, self.profile, lambda: self.playable,
                                  (OBSERVED_MS - 40_000) * 1_000_000, clock=lambda: self.clock[0])

    def tearDown(self):
        self.directory.cleanup()

    def save(self, at_ms, content, names=WORKING_FILES):
        for name in names:
            path = self.profile / name
            path.parent.mkdir(parents=True, exist_ok=True)
            # Each mirror pair carries the same bytes, like a coherent game save.
            path.write_bytes(content + b' ' + name.split('/')[-1].split('.')[0].encode())
            os.utime(path, ns=(at_ms * 1_000_000,) * 2)

    def tick(self, polls=1, seconds=0.2):
        for _ in range(polls):
            self.clock[0] += seconds
            self.stage.poll()

    def departing(self, write=True):
        """time.sleep inside bind: the game writes its departure save after the event."""
        def sleep(seconds):
            if write:
                self.save(OBSERVED_MS + 300, b'departure')
        return mock.patch('NativeDepartureStage.time.sleep', sleep)

    def test_a_save_from_before_the_hideout_is_never_frozen(self):
        self.tick(10)
        self.assertIsNone(self.stage.staged)
        with self.assertRaisesRegex(ProbeError, 'no-hideout-save-before-departure'):
            self.stage.bind(OBSERVED_MS, [0, 0, 0], lambda: False)

    def test_a_settled_hideout_save_is_frozen_with_its_bytes_and_timestamps(self):
        self.save(OBSERVED_MS - 10_000, b'hideout-1')
        self.tick()
        self.assertIsNone(self.stage.staged, 'a save that has not settled is not frozen')
        self.playable = False
        self.tick(3)
        self.assertIsNone(self.stage.staged, 'nothing is frozen while the hideout is not playable')
        self.playable = True
        self.tick()
        staged = self.stage.staged
        for name in STAGE_FILES:
            self.assertEqual((staged['path'] / name).read_bytes(), (self.profile / name).read_bytes())
            self.assertEqual((staged['path'] / name).stat().st_mtime_ns, (self.profile / name).stat().st_mtime_ns)
        self.assertEqual(staged['sha256'],
                         __import__('hashlib').sha256((self.profile / 'gamedata/R0A.save').read_bytes()).hexdigest())
        first = staged['path']
        self.save(OBSERVED_MS - 5_000, b'hideout-2')
        self.tick(3)
        self.assertNotEqual(self.stage.staged['path'], first)
        self.assertFalse(first.exists(), 'the older frozen copy is removed')
        self.stage.close()
        self.assertFalse(self.stage.root.exists())

    def test_divergent_mirrors_are_not_frozen(self):
        self.save(OBSERVED_MS - 10_000, b'hideout-1')
        self.save(OBSERVED_MS - 10_000, b'other', ('gamedata/R0A.save-backup',))
        self.tick(4)
        self.assertIsNone(self.stage.staged)

    def frozen(self):
        self.save(OBSERVED_MS - 10_000, b'hideout-1')
        self.tick(3)
        self.assertIsNotNone(self.stage.staged)
        return self.stage.staged

    def test_the_last_hideout_save_binds_once_the_departure_save_lands(self):
        staged = self.frozen()
        with self.departing():
            self.assertIs(self.stage.bind(OBSERVED_MS, [0, 0, 0], lambda: False), staged)

    def test_anything_but_the_last_save_before_the_event_binds_nothing(self):
        cases = {
            'hideout-save-in-progress-at-departure': lambda: {'workers': [0, 4, 0]},
            'newest-hideout-save-not-frozen': lambda: (self.save(OBSERVED_MS - 2_000, b'hideout-2'), self.tick())[-1],
            # Finished between the last poll and the event, then overwritten by the departure save.
            'hideout-save-missed-before-departure': lambda: self.save(OBSERVED_MS - 20, b'hideout-2'),
            'guardian-stopped': lambda: {'cancelled': lambda: True},
        }
        for reason, change in cases.items():
            with self.subTest(reason=reason):
                self.setUp()
                try:
                    self.frozen()
                    options = change() or {}
                    with self.departing(), self.assertRaisesRegex(ProbeError, reason):
                        self.stage.bind(OBSERVED_MS, options.get('workers', [0, 0, 0]),
                                        options.get('cancelled', lambda: False))
                finally:
                    self.tearDown()

    def test_a_save_seen_after_the_event_but_written_before_it_binds_nothing(self):
        self.frozen()
        self.save(OBSERVED_MS - 20, b'hideout-2')
        self.clock[0] = OBSERVED_MS / 1000 + 0.05
        self.stage.poll()
        self.save(OBSERVED_MS + 300, b'departure')
        with self.assertRaisesRegex(ProbeError, 'hideout-save-missed-before-departure'):
            self.stage.bind(OBSERVED_MS, [0, 0, 0], lambda: False)

    def test_a_departure_save_that_never_lands_binds_nothing(self):
        self.frozen()
        with self.departing(write=False), \
                mock.patch('NativeDepartureStage.time.monotonic', side_effect=itertools.count(0, 1)), \
                self.assertRaisesRegex(ProbeError, 'departure-save-not-observed'):
            self.stage.bind(OBSERVED_MS, [0, 0, 0], lambda: False, timeout=5)

    def test_only_a_verified_pre_departure_receipt_for_the_bound_bytes_is_accepted(self):
        receipt = {'id': '20261001-070000000-departure-0a1b2c3d', 'kind': 'departure', 'basis': 'pre_departure_save',
                   'runSha256': BOUND, 'routeIndex': 3, 'verified': True}
        calls = []

        def run(arguments, **options):
            calls.append(arguments)
            return SimpleNamespace(returncode=self.code, stdout=json.dumps(self.receipt).encode(),
                                   stderr=b'CAPTURE_FAILED code=departure_save_changed')

        with mock.patch('NativeDepartureStage.subprocess.run', run):
            self.code, self.receipt = 0, receipt
            self.assertEqual(protect_departure(Path('S'), Path('P'), 3, OBSERVED_MS, BOUND, Path('F')), receipt)
            self.assertEqual(calls[0][1:], ['S', 'P', '3', str(OBSERVED_MS), BOUND, 'F'])
            self.assertTrue(calls[0][0].endswith('CaptureDepartureSnapshot.exe'))
            for variant in (dict(runSha256='cd' * 32), dict(kind='manual'), dict(verified=False), dict(routeIndex=4),
                            dict(id='../escape'), dict(basis=None)):
                with self.subTest(variant=variant), self.assertRaisesRegex(ProbeError, 'incomplete'):
                    self.receipt = {**receipt, **variant}
                    protect_departure(Path('S'), Path('P'), 3, OBSERVED_MS, BOUND, Path('F'))
            self.code = 4
            with self.assertRaisesRegex(ProbeError, 'departure_save_changed'):
                protect_departure(Path('S'), Path('P'), 3, OBSERVED_MS, BOUND, Path('F'))


class WorkerRedeployTests(unittest.TestCase):
    def exercise(self, redeploy_errors):
        import NativeCheckpointTrigger as host
        from types import SimpleNamespace

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / 'fixture.exe'
            executable.write_bytes(b'fixture')
            events = []
            args = SimpleNamespace(pid=11, mode='recover-preparation', log=root / 'recovery.jsonl', duration=180,
                                   lifecycle_content=False, observation=None, collection_profile=None,
                                   recovery=RecoveryRequest(root, 'target', root, 'results', True, False))
            process = SimpleNamespace(image_path=lambda: executable, image_base=lambda: 0x10000000,
                                      close=lambda: events.append('process-close'))

            class Script:
                def __init__(self):
                    self.exports_sync = self

                def on(self, kind, handler):
                    self.handler = handler

                def load(self):
                    self.handler({'type': 'send', 'payload': {'kind': 'native-recovery-rebuilt'}}, None)
                    self.handler({'type': 'send', 'payload': {'kind': 'stopped', 'reason': 'recovery-awaiting'}}, None)

                def post(self, message, data=None):
                    events.append('packet')

                def stop(self):
                    events.append('recovery-stop')

                def unload(self):
                    events.append('recovery-unload')

            session = SimpleNamespace(create_script=lambda source: Script(), detach=lambda: events.append('detach'))
            source = SimpleNamespace(configuration={'rawSha256': '0'}, packet=b'')
            preflight = {'pid': 11, 'birth': 22, 'base': 0x10000000, 'source': source, 'undo': {'id': 'undo'},
                         'resultsExit': {'presses': 1}, 'abandon': None, 'departure': departure()}

            def redeploy(*call, birth=None):
                events.append('redeploy')
                events.append(('birth', birth))
                call[4]({'kind': 'redeploy-incomplete'})
                return redeploy_errors

            with mock.patch.object(host, 'arguments', return_value=args), \
                    mock.patch.object(host, 'ReadOnlyProcess', return_value=process), \
                    mock.patch.object(host, 'verify_build', return_value='fixed'), \
                    mock.patch.object(host, 'fingerprints', return_value=[]), \
                    mock.patch.object(host, 'verify_live', side_effect=lambda *call: events.append('verify')), \
                    mock.patch.object(host, 'process_birth', return_value=22), \
                    mock.patch('NativeRecoverySession.watch_recovery_session', return_value=lambda: None), \
                    mock.patch('NativeRecoverySession.read_failure_evidence', return_value=None), \
                    mock.patch('NativeRecoverySource.verify_unloaded_menu'), \
                    mock.patch('NativeEncounterRestart.redeploy_departure', side_effect=redeploy), \
                    mock.patch.dict('sys.modules', {'frida': SimpleNamespace(attach=lambda pid: session)}), \
                    contextlib.redirect_stdout(io.StringIO()):
                outcome, error = None, None
                try:
                    outcome = host.main([], recovery_preflight=preflight)
                except ProbeError as caught:
                    error = caught
            return outcome, error, events

    def test_departure_runs_only_after_the_recovery_bridge_is_removed_and_failure_to_depart_is_not_fatal(self):
        outcome, error, events = self.exercise([])
        self.assertIsNone(error)
        self.assertEqual(outcome, 0)
        self.assertLess(events.index('recovery-unload'), events.index('redeploy'))
        self.assertLess(events.index('recovery-unload'), events.index('verify', events.index('recovery-unload')))
        self.assertLess(events.index('redeploy'), events.index('detach'))
        self.assertEqual(events.count('recovery-unload'), 1)
        self.assertIn(('birth', 22), events)

    def test_unproven_departure_hook_removal_fails_the_worker(self):
        _, error, _ = self.exercise(['Redeploy hooks were not proven removed'])
        self.assertRegex(str(error), 'not proven removed')


if __name__ == '__main__':
    unittest.main()
