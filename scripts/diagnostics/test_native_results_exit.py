import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import Mock, call, patch

from NativeCheckpointProbe import ProbeError
from NativeResultsExit import CONTINUE_RVAS, ResultsExit, SessionPrefetch, finish_results, observe_results
from NativeResultsUi import TransientUiChange, inspect_results_ui, string_id
from local_evidence import requires


HOOKS = [{'rva': rva, 'bytes': f'{index:02x}' * 32} for index, rva in enumerate(CONTINUE_RVAS, 1)]
RUN_FIELDS = (0x415ac90 + 0x80, 0x415ac90 + 0x72c8, 0x415ac90 + 0x520, 0x9398730,
              0x9341660 + 0x58, 0x9341660 + 0x228, 0x9341660 + 0x3f8, 0x9341660 + 0x5e0)


def results(page='t2r-menu-post/page-scores'):
    return {'unloaded': False, 'initialized': 1, 'selector': 1, 'workers': [0, 0, 0],
            'slot': 20, 'task': '0x20000', 'taskWord': 2,
            'ui': {'topPage': page, 'topPageAddress': '0x30000', 'page': None}}


def menu():
    return {'unloaded': True, 'initialized': 0, 'selector': 2, 'workers': [0, 0, 0],
            'slot': 20, 'task': None, 'taskWord': None, 'ui': None}


class GameCode:
    """Answers hook-site reads with the original bytes unless a hook is left behind."""

    def __init__(self, restored=True):
        self.restored = restored

    def read(self, address, size):
        hook = next(hook for hook in HOOKS if hook['rva'] == address)
        return bytes.fromhex(hook['bytes']) if self.restored else bytes(size)


def journal(directory):
    path = Path(directory) / 'test.results.jsonl'
    return [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines()]


class ResultsExitTests(unittest.TestCase):
    def test_results_saves_and_unload_must_complete_before_ready(self):
        controller = ResultsExit()
        self.assertEqual(controller.step(results(), 0), 'confirm')
        controller.injected()
        self.assertEqual(controller.step(results(), 0.1), 'wait')
        saving = results()
        saving['workers'] = [0, 0, 4]
        self.assertEqual(controller.step(saving, 3), 'wait')
        leaving = results(None)
        leaving['taskWord'] = None
        self.assertEqual(controller.step(leaving, 4), 'wait')
        self.assertEqual(controller.step({'unstable': True}, 4.5), 'wait')
        self.assertEqual(controller.step(menu(), 5), 'wait')
        busy_menu = menu()
        busy_menu.update(unloaded=False, workers=[0, 4, 0])
        self.assertEqual(controller.step(busy_menu, 5.9), 'wait')
        self.assertEqual(controller.step(menu(), 6), 'wait')
        self.assertEqual(controller.step(menu(), 6.9), 'wait')
        self.assertEqual(controller.step(menu(), 7), 'ready')
        self.assertEqual(controller.total_presses, 1)

    def test_wrong_entry_unknown_page_unstable_state_and_combat_never_press(self):
        for observed in (menu(), results(None), dict(results(), taskWord=1), {'unstable': True},
                         dict(results(), selector=2), dict(results(), taskWord=None)):
            with self.subTest(observed=observed), self.assertRaises(ProbeError):
                ResultsExit().step(observed, 0)

    def test_tokens_follow_a_mashing_cadence_without_a_page_dwell(self):
        controller = ResultsExit()
        actions = [controller.step(results(), now) for now in (0, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3)]
        self.assertEqual(actions, ['confirm', 'wait', 'wait', 'confirm', 'wait', 'wait', 'confirm'])
        self.assertEqual(controller.total_presses, 0)

    def test_unconsumed_tokens_are_not_presses_and_cannot_trip_the_stall_bound(self):
        controller = ResultsExit()
        now = 0
        while now < 30:
            controller.step(results(), now)
            now += 0.15
        self.assertEqual(controller.total_presses, 0)

    def test_presses_without_visible_change_are_bounded_by_count_and_time(self):
        controller = ResultsExit()
        now = 0
        for _ in range(9):
            self.assertEqual(controller.step(results(), now), 'confirm')
            controller.injected()
            now += 1
        self.assertEqual(controller.step(results(), now), 'confirm')
        controller.injected()
        with self.assertRaisesRegex(ProbeError, 'bounded'):
            controller.step(results(), now + 1)

    def test_total_presses_are_bounded_even_when_focus_keeps_changing(self):
        controller = ResultsExit()
        for index in range(80):
            animated = results()
            animated['ui'] = dict(animated['ui'], focusId=hex(index))
            self.assertEqual(controller.step(animated, index * 0.2), 'confirm')
            controller.injected()
        with self.assertRaisesRegex(ProbeError, 'bounded'):
            controller.step(results(), 17)

    def test_scores_page_that_advances_internally_is_not_a_false_stop(self):
        # 2026-09-25 15:44: four accepted presses changed only the focus on page-scores; the fifth left it.
        controller = ResultsExit()
        focus = ['0x9a63d92c4608ac83'] * 3 + ['0xf076ba54a1c91eb4', '0xf0f78682c61d08df']
        for index, focus_id in enumerate(focus):
            observed = results()
            observed['ui'] = dict(observed['ui'], focusId=focus_id)
            self.assertEqual(controller.step(observed, index * 0.2), 'confirm')
            controller.injected()
        self.assertEqual(controller.step(results(None), 2), 'wait')
        self.assertEqual(controller.total_presses, 5)

    def test_page_change_and_back_navigation_is_rejected(self):
        controller = ResultsExit()
        self.assertEqual(controller.step(results(), 0), 'confirm')
        self.assertEqual(controller.step(results('t2r-menu-post/page-meta'), 0.1), 'wait')
        self.assertEqual(controller.step(results('t2r-menu-post/page-meta'), 0.2), 'confirm')
        self.assertEqual(controller.step(results(None), 1), 'wait')
        with self.assertRaisesRegex(ProbeError, 'earlier'):
            controller.step(results(), 2)

    @requires('native-runtime-20260912/collection-observe-03.jsonl')
    def test_actual_death_exit_transcript_never_marks_pending_saves_as_ready(self):
        path = Path(__file__).resolve().parents[2] / 'artifacts/native-runtime-20260912/collection-observe-03.jsonl'
        records = [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines()]
        controller = ResultsExit()
        controller.step(results(), 0)
        controller.injected()
        start = next(record['time'] for record in records if record.get('sequence') == 710)
        for record in records:
            if not 710 <= record.get('sequence', 0) <= 812 or 'state' not in record:
                continue
            state = record['state']
            observed = results(None)
            observed.update(initialized=state['runInitialized'], selector=state['runSelector'],
                            workers=state['workerStates'], taskWord=None)
            observed['unloaded'] = (observed['initialized'] == 0 and observed['selector'] == 2
                                    and observed['workers'] == [0, 0, 0])
            self.assertNotEqual(controller.step(observed, 2 + (record['time'] - start) / 1000), 'ready')
        self.assertEqual(controller.step(menu(), 20), 'ready')

    def test_live_run_rejection_precedes_ui_reads(self):
        process = Mock()
        payloads = [struct.pack(encoding, value) for encoding, value in
                    (('<B', 1), ('<B', 1), ('<i', 1), ('<i', 2),
                     ('<i', 0), ('<i', 0), ('<i', 0), ('<i', 20))]
        process.read.side_effect = payloads
        with patch('NativeResultsExit.inspect_results_ui') as ui, self.assertRaisesRegex(ProbeError, 'inactive'):
            observe_results(process, 0x10000000)
        ui.assert_not_called()

    def test_repeated_ui_changes_are_reported_as_unstable_instead_of_authorizing_continue(self):
        values = dict(zip(RUN_FIELDS, (('<B', 0), ('<B', 1), ('<i', 1), ('<i', 2),
                                       ('<i', 0), ('<i', 0), ('<i', 0), ('<i', 20))))
        process = Mock()
        process.read.side_effect = lambda address, size: struct.pack(*values[address])
        with patch('NativeResultsExit.inspect_current_task', return_value={'status': 'current_task'}), \
                patch('NativeResultsExit.inspect_results_ui', side_effect=TransientUiChange('changed')) as ui:
            self.assertEqual(observe_results(process, 0), {'unstable': True})
        self.assertEqual(ui.call_count, 3)

    def run_exit(self, directory, observations, monotonic, restored=True, identity=None):
        with patch('NativeResultsExit.observe_results', side_effect=observations), \
                patch('NativeResultsExit.ContinueInjector') as injector_type, \
                patch('NativeResultsExit.time.monotonic', side_effect=monotonic), \
                patch('NativeResultsExit.time.sleep'), \
                patch('NativeResultsExit.verify_unloaded_menu') as verified:
            injector = injector_type.return_value
            injector.stopped = None
            pending = []
            injector.press.side_effect = lambda: pending.append({'kind': 'results-continue-injected'}) or 1

            def drain():
                drained = list(pending)
                pending.clear()
                return drained
            injector.drain.side_effect = drain
            injector.close.return_value = []
            events = Mock()
            events.attach_mock(injector.close, 'close')
            events.attach_mock(verified, 'verified')
            receipt, error = None, None
            try:
                receipt = finish_results(GameCode(restored), 0, 11, 22, Path(directory) / 'test.jsonl',
                                         identity or Mock(), HOOKS)
            except ProbeError as caught:
                error = caught
            return receipt, error, injector, verified, events

    def test_one_native_continue_finishes_results_and_hooks_are_removed_before_menu_checks(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, error, injector, verified, events = self.run_exit(
                directory, [results(), results(), results(), menu(), menu()], [0, 0, 1, 1.1, 1.3, 1.4, 1.5, 3.5])
            self.assertIsNone(error)
            self.assertEqual(receipt['presses'], 1)
            self.assertFalse(receipt['nativeRecoveryStarted'])
            injector.press.assert_called_once_with()
            self.assertEqual(events.mock_calls[0], call.close())
            self.assertEqual(events.mock_calls[1][0], 'verified')
            records = journal(directory)
            self.assertEqual([record['kind'] for record in records][-2:],
                             ['results-continue-detached', 'results-exit-complete'])
            self.assertTrue(records[-2]['codeRestored'])

    def test_unproven_hook_removal_never_reaches_menu_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            _, error, _, verified, _ = self.run_exit(
                directory, [results(), results(), results(), menu(), menu()], [0, 0, 1, 1.1, 1.3, 1.4, 1.5, 3.5],
                restored=False)
            self.assertRegex(str(error), 'not proven removed')
            verified.assert_not_called()
            records = journal(directory)
            stopped = [record for record in records if record['kind'] == 'results-exit-stopped']
            self.assertEqual(len(stopped), 1)
            self.assertFalse(stopped[0]['nativeRecoveryStarted'])
            detached = [record for record in records if record['kind'] == 'results-continue-detached']
            self.assertEqual([record['codeRestored'] for record in detached], [False])
            self.assertNotIn('results-exit-complete', [record['kind'] for record in records])

    def test_late_gate_change_skips_that_continue_and_focus_animation_does_not(self):
        with tempfile.TemporaryDirectory() as directory:
            _, error, injector, verified, _ = self.run_exit(
                directory, [results(), results(), menu()] + [results(None)] * 3, [0, 0, 1, 1.1, 1.2, 1.3, 121])
            self.assertRegex(str(error), 'timed out')
            injector.press.assert_not_called()
            verified.assert_not_called()
            kinds = [record['kind'] for record in journal(directory)]
            self.assertIn('results-continue-skipped', kinds)
            self.assertEqual(kinds[-1], 'results-continue-detached')
        animated = results()
        animated['ui'] = dict(animated['ui'], page='t2r-menu-post/page-scores', focusAddress='0x9')
        with tempfile.TemporaryDirectory() as directory:
            _, error, injector, _, _ = self.run_exit(
                directory, [results(), results(), animated, menu(), menu()], [0, 0, 1, 1.1, 1.3, 1.4, 1.5, 3.5])
            self.assertIsNone(error)
            injector.press.assert_called_once_with()

    def test_full_journal_still_records_hook_removal_and_stop(self):
        with tempfile.TemporaryDirectory() as directory, patch('NativeResultsExit.JOURNAL_LIMIT', 16384 + 1500):
            animated = [dict(results(), ui=dict(results()['ui'], focusId=hex(index))) for index in range(40)]
            _, error, _, verified, _ = self.run_exit(
                directory, [results()] + animated * 2, [0, 0] + [1 + index * 0.1 for index in range(400)])
            self.assertRegex(str(error), 'journal')
            verified.assert_not_called()
            kinds = [record['kind'] for record in journal(directory)]
            self.assertEqual(kinds[-2:], ['results-exit-stopped', 'results-continue-detached'])
            self.assertTrue(journal(directory)[-1]['codeRestored'])

    def test_identity_loss_never_issues_continue(self):
        with tempfile.TemporaryDirectory() as directory:
            identity = Mock(side_effect=[None, ProbeError('identity lost')])
            _, error, injector, _, _ = self.run_exit(directory, [results()], [0, 0, 1], identity=identity)
            self.assertRegex(str(error), 'identity lost')
            injector.press.assert_not_called()
            injector.close.assert_called_once_with()

    def test_unknown_popup_times_out_without_continue_or_native_load(self):
        with tempfile.TemporaryDirectory() as directory:
            _, error, injector, verified, _ = self.run_exit(
                directory, [results(), results(None)], [0, 0, 1, 1, 121])
            self.assertRegex(str(error), 'timed out')
            injector.press.assert_not_called()
            verified.assert_not_called()

    def test_injector_stop_is_fatal_before_any_further_observation(self):
        with tempfile.TemporaryDirectory() as directory, \
                patch('NativeResultsExit.observe_results', return_value=results()) as observed, \
                patch('NativeResultsExit.ContinueInjector') as injector_type, \
                patch('NativeResultsExit.time.monotonic', side_effect=[0, 0, 1]):
            injector = injector_type.return_value
            injector.stopped = {'reason': 'probe-error: fingerprint'}
            injector.drain.return_value = []
            injector.close.return_value = []
            with self.assertRaisesRegex(ProbeError, 'fingerprint'):
                finish_results(GameCode(), 0, 11, 22, Path(directory) / 'test.jsonl', Mock(), HOOKS)
            self.assertEqual(observed.call_count, 1)
            injector.press.assert_not_called()

    def test_prefetched_attach_is_used_only_after_a_results_entry_is_observed(self):
        for initial, taken in ((results(), True), (menu(), False)):
            with self.subTest(taken=taken), tempfile.TemporaryDirectory() as directory,                     patch('NativeResultsExit.observe_results', return_value=initial),                     patch('NativeResultsExit.ContinueInjector') as injector_type,                     patch('NativeResultsExit.time.monotonic', side_effect=[0, 0, 1]):
                injector = injector_type.return_value
                injector.stopped = {'reason': 'probe-error: stop after install'}
                injector.drain.return_value = []
                injector.close.return_value = []
                prefetch = Mock()
                with self.assertRaises(ProbeError):
                    finish_results(GameCode(), 0, 11, 22, Path(directory) / 'test.jsonl', Mock(), HOOKS,
                                   prefetch=prefetch)
                self.assertEqual(prefetch.take.called, taken)
                if taken:
                    self.assertIs(injector_type.call_args.kwargs['session'], prefetch.take.return_value)
                else:
                    injector_type.assert_not_called()

    def test_session_prefetch_detaches_only_an_untaken_session_and_reports_attach_errors(self):
        for taken in (False, True):
            with self.subTest(taken=taken), patch('NativeResultsExit.attach_session') as attach:
                prefetch = SessionPrefetch(11)
                if taken:
                    self.assertIs(prefetch.take(), attach.return_value)
                prefetch.close()
                self.assertEqual(attach.return_value.detach.called, not taken)
        with patch('NativeResultsExit.attach_session', side_effect=RuntimeError('denied')):
            with self.assertRaisesRegex(ProbeError, 'denied'):
                SessionPrefetch(11).take()

    def test_missing_hook_fingerprints_are_rejected_before_attaching(self):
        with tempfile.TemporaryDirectory() as directory, \
                patch('NativeResultsExit.ContinueInjector') as injector_type:
            with self.assertRaisesRegex(ProbeError, 'incomplete'):
                finish_results(GameCode(), 0, 11, 22, Path(directory) / 'test.jsonl', Mock(), HOOKS[:1])
            injector_type.assert_not_called()


class UiMemory:
    base = 0x10000000

    def __init__(self):
        self.data = {}
        self.manager, self.stack, self.page, self.focus = 0x20000, 0x21000, 0x22000, 0x23000
        for index, instance in enumerate((self.manager, self.stack, self.page, self.focus)):
            self.put(instance + 0x94, 7, '<I')
            self.put(instance + 0xc, 0, '<B')
            self.put(instance + 0x40, 0x30000 + index * 0x100)
            self.put(0x30000 + index * 0x100 + 0x28, 7)
            self.put(instance + 0x80, 0x40000 + index * 0x100)
        for rva, index in ((0x9493ba8, 0), (0x9493978, 1), (0x9497e88, 2)):
            self.put(self.base + rva, index, '<I')
        self.put(self.base + 0x92d08d8, 0x50000)
        self.put(self.base + 0x92d08e0, 7, '<I')
        self.put(0x50008, self.manager)
        self.put(self.manager + 0x4c0, 0x50100)
        self.put(self.manager + 0x4c8, 7, '<I')
        self.put(0x50108, self.focus)
        self.put(self.base + 0x37d69b8, 1, '<I')
        self.put(self.base + 0x37d6928, 0x60000)
        self.put(0x60000, 0x61000)
        self.put(0x60008, 0x62000)
        self.put(0x61018, 0x63000)
        self.put(0x63000, 0x64000)
        self.put(0x63008, 7, '<I')
        self.put(0x64008, self.stack)
        self.put(0x40130, 0x40200)
        self.put(0x40208, self.page)
        self.put(0x40230, 0x40300)
        self.put(0x40308, self.focus)
        self.put(self.page + 0xc0, 987)
        self.put(self.page + 0x130, string_id('t2r-menu-post/page-scores'))
        self.put(self.focus + 0xc0, 123)

    def put(self, address, value, encoding='<Q'):
        self.data.update({address + offset: byte for offset, byte in enumerate(struct.pack(encoding, value))})

    def read(self, address, size):
        return bytes(self.data.get(address + offset, 0) for offset in range(size))


class ResultsUiTests(unittest.TestCase):
    def test_focused_results_page_is_required_and_returns_only_small_metadata(self):
        memory = UiMemory()
        observed = inspect_results_ui(memory.read, memory.base)
        self.assertEqual(observed['page'], 't2r-menu-post/page-scores')
        self.assertLess(len(json.dumps(observed)), 512)

    def test_unknown_page_modal_focus_hidden_page_and_recycled_handle_do_not_authorize_input(self):
        for variant in ('unknown', 'modal', 'hidden', 'recycled', 'disabled'):
            with self.subTest(variant=variant):
                memory = UiMemory()
                if variant == 'unknown':
                    memory.put(memory.page + 0x130, string_id('menu/message-box-ok'))
                if variant == 'modal':
                    memory.put(0x50108, memory.manager)
                if variant == 'hidden':
                    memory.put(memory.page + 0x8a1, 1, '<B')
                if variant == 'recycled':
                    memory.put(memory.focus + 0x94, 8, '<I')
                if variant == 'disabled':
                    memory.put(memory.focus + 0xc, 1, '<B')
                observed = inspect_results_ui(memory.read, memory.base)
                self.assertIsNone(observed['page'])
                expected = None if variant in ('unknown', 'hidden') else 't2r-menu-post/page-scores'
                self.assertEqual(observed['topPage'], expected)

    def test_cycles_oversized_registry_and_changing_reads_fail_closed(self):
        for variant in ('cycle', 'oversized', 'changed'):
            with self.subTest(variant=variant), self.assertRaises(ProbeError):
                memory = UiMemory()
                read = memory.read
                if variant == 'cycle':
                    memory.put(0x40238, 0x40200)
                if variant == 'oversized':
                    memory.put(memory.base + 0x37d69b8, 4097, '<I')
                if variant == 'changed':
                    calls = set()

                    def read(address, size):
                        payload = memory.read(address, size)
                        if address == memory.page + 0x130 and address in calls:
                            return bytes(size)
                        calls.add(address)
                        return payload
                inspect_results_ui(read, memory.base)

    def test_instance_id_cannot_impersonate_a_results_resource(self):
        memory = UiMemory()
        memory.put(memory.page + 0xc0, string_id('t2r-menu-post/page-scores'))
        memory.put(memory.page + 0x130, string_id('menu/message-box-ok'))
        observed = inspect_results_ui(memory.read, memory.base)
        self.assertIsNone(observed['page'])
        self.assertIsNone(observed['topPage'])

    def test_changing_graph_is_a_transient_change(self):
        memory = UiMemory()
        seen = set()

        def read(address, size):
            if address == memory.page + 0x130 and address in seen:
                return bytes(size)
            seen.add(address)
            return memory.read(address, size)
        with self.assertRaises(TransientUiChange):
            inspect_results_ui(read, memory.base)

    def test_reward_unlock_box_is_a_recognized_top_page(self):
        memory = UiMemory()
        memory.put(memory.page + 0x130, string_id('t2r-meta-challenges/message-box-ok-meta-challenge-unlock'))
        self.assertEqual(inspect_results_ui(memory.read, memory.base)['topPage'],
                         't2r-meta-challenges/message-box-ok-meta-challenge-unlock')


if __name__ == '__main__':
    unittest.main()
