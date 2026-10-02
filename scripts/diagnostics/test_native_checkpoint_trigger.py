import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import multiprocessing
import time

from NativeCheckpointTrigger import ProbeError, approved_context, arguments, main, stop_guarded_process, supervise_worker, supervised_main, wait_worker


def hung_worker():
    time.sleep(60)


def failed_worker():
    raise SystemExit(3)


def successful_worker():
    pass


class NativeCheckpointTriggerTests(unittest.TestCase):
    identity = {'pid': 11, 'birth': 22, 'sha256': 'verified', 'path': 'game', 'base': '0x10000'}

    def test_load_observer_is_bounded_and_cannot_enable_native_control(self):
        common = ['11', '--mode', 'load-observe', '--log', 'trace.jsonl',
                  '--confirm-intrusive-experiment']
        self.assertEqual(arguments(common + ['--duration', '600']).mode, 'load-observe')
        invalid = [common[:-1]] + [common + extra for extra in (
            ['--duration', '601'], ['--observation', 'old.jsonl'], ['--lifecycle-content'],
            ['--confirm-restart-current-checkpoint'], ['--confirm-native-resume-current-preparation'])]
        for values in invalid:
            with self.subTest(values=values), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    arguments(values)

    def test_load_observer_log_cannot_authorize_trigger(self):
        records = self.observation()
        records[0]['mode'] = 'load-observe'
        with self.assertRaises(ProbeError):
            self.evaluate(records)

    def test_content_evidence_cannot_enable_native_control(self):
        common = ['11', '--log', 'trace.jsonl', '--confirm-intrusive-experiment',
                  '--lifecycle-content']
        self.assertTrue(arguments(common + ['--mode', 'lifecycle']).lifecycle_content)
        for mode in ('observe', 'trigger'):
            with self.subTest(mode=mode), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    arguments(common + ['--mode', mode])

    def test_resume_needs_its_own_confirmation_and_observation(self):
        common = ['11', '--log', 'trace.jsonl', '--confirm-intrusive-experiment']
        confirm = '--confirm-native-resume-current-preparation'
        valid = common + ['--mode', 'resume-preparation', '--observation', 'observe.jsonl', confirm]
        with contextlib.redirect_stderr(io.StringIO()) as errors, self.assertRaises(SystemExit) as rejected:
            arguments(valid)
        self.assertEqual(rejected.exception.code, 2)
        self.assertIn('A-pose', errors.getvalue())
        invalid = [valid[:-1], common + ['--mode', 'resume-preparation', confirm],
                   valid + ['--confirm-restart-current-checkpoint'], valid + ['--lifecycle-content'],
                   valid + ['--duration', '61']]
        invalid += [common + ['--mode', mode, confirm] for mode in ('observe', 'trigger', 'lifecycle')]
        for values in invalid:
            with self.subTest(values=values), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    arguments(values)

    def test_failed_resume_is_blocked_before_process_access_in_both_entrypoints(self):
        values = ['11', '--mode', 'resume-preparation', '--log', 'trace.jsonl',
                  '--observation', 'observe.jsonl', '--confirm-intrusive-experiment',
                  '--confirm-native-resume-current-preparation']
        for entrypoint in (main, supervised_main):
            with self.subTest(entrypoint=entrypoint.__name__), mock.patch(
                    'NativeCheckpointTrigger.ReadOnlyProcess',
                    side_effect=AssertionError('Quarantined resume accessed the game')) as process:
                with contextlib.redirect_stderr(io.StringIO()) as errors:
                    with self.assertRaises(SystemExit) as rejected:
                        entrypoint(values)
                self.assertEqual(rejected.exception.code, 2)
                self.assertIn('A-pose', errors.getvalue())
                process.assert_not_called()

    def test_new_recovery_requires_a_distinct_source_profile_menu_confirmation_and_unused_log(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            values = ['11', '--mode', 'recover-preparation', '--log', str(root / 'recovery.jsonl'),
                      '--confirm-intrusive-experiment', '--recovery-storage', str(root),
                      '--recovery-profile', str(root), '--recovery-snapshot', 'formal-target',
                      '--entry', 'menu', '--duration', '300']
            self.assertEqual(arguments(values).mode, 'recover-preparation')
            invalid = [values[:-4], values + ['--duration', '301'], values + ['--observation', 'old.jsonl'],
                       values + ['--confirm-native-resume-current-preparation']]
            for sample in invalid:
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                    arguments(sample)
            (root / 'recovery.jsonl').write_text('existing evidence', encoding='utf-8')
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                arguments(values)

    def test_recovery_flags_cannot_change_observer_or_quarantined_resume_semantics(self):
        for mode in ('observe', 'load-observe', 'collection-observe', 'resume-preparation'):
            with self.subTest(mode=mode), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                arguments(['11', '--mode', mode, '--log', 'unused.jsonl', '--confirm-intrusive-experiment',
                           '--recovery-storage', 'source', '--recovery-snapshot', 'target',
                           '--recovery-profile', 'profile', '--entry', 'menu'])

    def test_a_recovery_names_exactly_one_known_entry(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            values = ['11', '--mode', 'recover-preparation', '--log', str(root / 'recover.jsonl'),
                      '--confirm-intrusive-experiment', '--recovery-storage', str(root),
                      '--recovery-profile', str(root), '--recovery-snapshot', 'target']
            self.assertEqual(arguments(values + ['--entry', 'results']).recovery.entry, 'results')
            for sample in (values, values + ['--entry', 'results-page'], values + ['--confirm-post-death-results'],
                           ['11', '--mode', 'observe', '--log', 'unused.jsonl',
                            '--confirm-intrusive-experiment', '--entry', 'results']):
                with self.subTest(sample=sample), contextlib.redirect_stderr(io.StringIO()),                         self.assertRaises(SystemExit):
                    arguments(sample)

    def test_results_entry_finishes_results_before_securing_state_or_arming_shutdown(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            values = ['11', '--mode', 'recover-preparation', '--log', str(root / 'recovery.jsonl'),
                      '--confirm-intrusive-experiment', '--recovery-storage', str(root),
                      '--recovery-profile', str(root), '--recovery-snapshot', 'formal-target',
                      '--entry', 'results']
            with mock.patch('NativeCheckpointTrigger.ReadOnlyProcess') as process_type,                     mock.patch('NativeCheckpointTrigger.verify_build'),                     mock.patch('NativeCheckpointTrigger.process_birth', return_value=22),                     mock.patch('NativeCheckpointTrigger.fingerprints', return_value=[]),                     mock.patch('NativeCheckpointTrigger.verify_live'),                     mock.patch('NativeRecoveryLock.RecoveryLock'),                     mock.patch('NativeRecoverySource.verify_unloaded_menu') as menu,                     mock.patch('NativeRecoverySource.prepare_source'),                     mock.patch('NativeRecoverySource.secure_live_state') as secure,                     mock.patch('NativeResultsExit.SessionPrefetch') as prefetch_type,                     mock.patch('NativeResultsExit.finish_results', side_effect=ProbeError('results stalled')) as finish:
                process = process_type.return_value
                with self.assertRaisesRegex(ProbeError, 'results stalled'):
                    supervised_main(values)
                menu.assert_not_called()
                finish.assert_called_once()
                self.assertIs(finish.call_args.kwargs['prefetch'], prefetch_type.return_value)
                prefetch_type.return_value.close.assert_called_once_with()
                secure.assert_not_called()
                process.kernel.OpenProcess.assert_not_called()

    def test_results_entry_cannot_bypass_the_supervised_preflight(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            values = ['11', '--mode', 'recover-preparation', '--log', str(root / 'recovery.jsonl'),
                      '--confirm-intrusive-experiment', '--recovery-storage', str(root),
                      '--recovery-profile', str(root), '--recovery-snapshot', 'formal-target',
                      '--entry', 'results']
            with mock.patch('NativeCheckpointTrigger.ReadOnlyProcess'),                     mock.patch('NativeCheckpointTrigger.verify_build', return_value='verified'),                     mock.patch('NativeCheckpointTrigger.process_birth', return_value=22),                     mock.patch('NativeCheckpointTrigger.fingerprints', return_value=[]),                     mock.patch('NativeCheckpointTrigger.verify_live'),                     mock.patch('NativeRecoverySource.prepare_source') as prepare:
                with self.assertRaisesRegex(ProbeError, 'supervised preflight'):
                    main(values)
                prepare.assert_not_called()

    def test_recovery_menu_preflight_rejects_before_arming_process_shutdown(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            values = ['11', '--mode', 'recover-preparation', '--log', str(root / 'recovery.jsonl'),
                      '--confirm-intrusive-experiment', '--recovery-storage', str(root),
                      '--recovery-profile', str(root), '--recovery-snapshot', 'formal-target',
                      '--entry', 'menu']
            with mock.patch('NativeCheckpointTrigger.ReadOnlyProcess') as process_type, \
                    mock.patch('NativeCheckpointTrigger.verify_build'), \
                    mock.patch('NativeCheckpointTrigger.process_birth', return_value=22), \
                    mock.patch('NativeRecoverySource.verify_unloaded_menu', side_effect=ProbeError('active hideout')), \
                    mock.patch('NativeRecoverySource.prepare_source') as prepare:
                process = process_type.return_value
                with self.assertRaisesRegex(ProbeError, 'active hideout'):
                    supervised_main(values)
                process.kernel.OpenProcess.assert_not_called()
                process.kernel.TerminateProcess.assert_not_called()
                prepare.assert_not_called()
                process.close.assert_called_once()

    def test_preparation_observation_does_not_authorize_encounter_restart(self):
        records = self.observation()
        records[1]['taskWord'] = 2
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'observation.jsonl'
            path.write_text('\n'.join(json.dumps(record) for record in records), encoding='utf-8')
            self.assertEqual(approved_context(path, self.identity, 1100, 2)[-1], '0x50000')
            with self.assertRaises(ProbeError):
                approved_context(path, self.identity, 1100)

    def test_lifecycle_is_bounded_and_cannot_authorize_restart(self):
        common = ['11', '--mode', 'lifecycle', '--log', 'trace.jsonl',
                  '--confirm-intrusive-experiment']
        self.assertEqual(arguments(common + ['--duration', '600']).duration, 600)
        for extra in (['--duration', '601'], ['--duration', '4'],
                      ['--confirm-restart-current-checkpoint'], ['--observation', 'old.jsonl']):
            with self.subTest(extra=extra), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    arguments(common + extra)

    def test_lifecycle_does_not_replace_explicit_intrusive_permission(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            arguments(['11', '--mode', 'lifecycle', '--log', 'trace.jsonl'])

    def test_lifecycle_log_never_authorizes_trigger(self):
        records = self.observation()
        records[0]['mode'] = 'lifecycle'
        with self.assertRaises(ProbeError):
            self.evaluate(records)

    def observation(self):
        return [
            {'kind': 'host-preflight', 'time': 1000, 'mode': 'observe', **self.identity},
            {'kind': 'idle-observed', 'threadId': 33, 'owner': '0x20000',
             'eventCount': 0, 'idleCount': 30, 'rogue': 1, 'mode': 0,
             'stateObject': '0x30000', 'caller': '0x40000', 'task': '0x50000', 'taskWord': 1},
            {'kind': 'host-detached', 'codeRestored': True},
        ]

    def evaluate(self, records, now=1100):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'observation.jsonl'
            path.write_text('\n'.join(json.dumps(record) for record in records), encoding='utf-8')
            return approved_context(path, self.identity, now)

    def test_matching_process_context(self):
        self.assertEqual(self.evaluate(self.observation()), ('0x20000', '0x30000', '0x40000', '0x50000'))

    def test_process_reuse_and_build_mismatch(self):
        for key in self.identity:
            with self.subTest(key=key):
                records = self.observation()
                records[0][key] = 'different'
                with self.assertRaises(ProbeError):
                    self.evaluate(records)

    def test_stale_or_future_observation(self):
        for now in (999, 1301):
            with self.assertRaises(ProbeError):
                self.evaluate(self.observation(), now)

    def test_missing_or_failed_cleanup(self):
        for records in (self.observation()[:-1], self.observation()):
            records[-1]['codeRestored'] = False
            with self.assertRaises(ProbeError):
                self.evaluate(records)

    def test_missing_stability_or_wrong_task(self):
        for key, value in (('taskWord', 2), ('idleCount', 29), ('rogue', 0), ('taskWord', None)):
            records = self.observation()
            records[1][key] = value
            with self.assertRaises(ProbeError):
                self.evaluate(records)

    def test_ambiguous_owner_or_task(self):
        for key, value in (('task', '0x60000'), ('owner', '0x30000')):
            records = self.observation()
            records.insert(2, {**records[1], key: value})
            with self.assertRaises(ProbeError):
                self.evaluate(records)

    def test_trigger_log_cannot_authorize_trigger(self):
        records = self.observation()
        records[0]['mode'] = 'trigger'
        with self.assertRaises(ProbeError):
            self.evaluate(records)

    def test_confirmation_and_timeout_validation(self):
        base = ['11', '--log', 'unused.jsonl']
        invalid = [base, base + ['--confirm-intrusive-experiment', '--duration', '61'],
                   base + ['--confirm-intrusive-experiment', '--mode', 'trigger'],
                   base + ['--confirm-intrusive-experiment', '--confirm-restart-current-checkpoint']]
        for values in invalid:
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                arguments(values)

    def test_independent_supervisor_stops_hung_or_failed_worker(self):
        for target in (hung_worker, failed_worker):
            with self.subTest(target=target.__name__):
                worker = multiprocessing.get_context('spawn').Process(target=target)
                shutdowns = []
                worker.start()
                try:
                    result = wait_worker(worker, 0.5, lambda: shutdowns.append(True))
                    self.assertEqual(result, 5)
                    self.assertEqual(shutdowns, [True])
                    self.assertFalse(worker.is_alive())
                finally:
                    if worker.is_alive():
                        worker.terminate()
                        worker.join(5)

    def test_successful_worker_does_not_shutdown_target(self):
        worker = multiprocessing.get_context('spawn').Process(target=successful_worker)
        worker.start()
        self.assertEqual(wait_worker(worker, 10, lambda: self.fail('unexpected shutdown')), 0)

    def test_interrupted_supervisor_stops_worker_before_disk_rollback_and_preserves_interrupt(self):
        events = []
        worker = mock.Mock()
        worker.is_alive.side_effect = [True, False]
        worker.join.side_effect = lambda timeout: events.append('join')
        worker.terminate.side_effect = lambda: events.append('worker-stop')
        with mock.patch('NativeCheckpointTrigger.wait_worker', side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                supervise_worker(worker, 10, lambda: events.append('game-stop'), lambda: events.append('rollback'))
        self.assertEqual(events, ['game-stop', 'join', 'worker-stop', 'join', 'rollback'])

    def test_supervisor_never_rolls_back_with_an_unstopped_target_or_worker(self):
        for game_stops in (False, True):
            worker = mock.Mock()
            worker.is_alive.return_value = True
            shutdown = mock.Mock(side_effect=None if game_stops else ProbeError('target still running'))
            rollback = mock.Mock()
            with mock.patch('NativeCheckpointTrigger.wait_worker', side_effect=KeyboardInterrupt):
                with self.assertRaises(ProbeError):
                    supervise_worker(worker, 10, shutdown, rollback)
            rollback.assert_not_called()

    def test_supervisor_rolls_back_only_failed_results(self):
        for result in (0, 5):
            rollback = mock.Mock()
            with mock.patch('NativeCheckpointTrigger.wait_worker', return_value=result):
                self.assertEqual(supervise_worker(mock.Mock(), 10, mock.Mock(), rollback), result)
            self.assertEqual(rollback.call_count, 0 if result == 0 else 1)

    def test_crash_exit_racing_termination_still_reaches_disk_rollback(self):
        kernel = mock.Mock()
        kernel.WaitForSingleObject.side_effect = [258, 0]
        kernel.TerminateProcess.return_value = False
        worker = mock.Mock()
        worker.is_alive.return_value = False
        worker.exitcode = 1
        rollback = mock.Mock()
        result = supervise_worker(worker, 1, lambda: stop_guarded_process(kernel, 99), rollback)
        self.assertEqual(result, 5)
        self.assertEqual(kernel.WaitForSingleObject.call_args_list, [mock.call(99, 0), mock.call(99, 10000)])
        rollback.assert_called_once_with()

    def test_denied_termination_without_signaled_exit_cannot_authorize_rollback(self):
        kernel = mock.Mock()
        kernel.WaitForSingleObject.return_value = 258
        kernel.TerminateProcess.return_value = False
        worker = mock.Mock()
        worker.is_alive.return_value = False
        worker.exitcode = 1
        rollback = mock.Mock()
        with self.assertRaisesRegex(ProbeError, 'Target exit was not verified'):
            supervise_worker(worker, 1, lambda: stop_guarded_process(kernel, 99), rollback)
        rollback.assert_not_called()

    def test_already_exited_guard_is_not_terminated_again(self):
        kernel = mock.Mock()
        kernel.WaitForSingleObject.return_value = 0
        stop_guarded_process(kernel, 99)
        kernel.TerminateProcess.assert_not_called()


if __name__ == '__main__':
    unittest.main()
