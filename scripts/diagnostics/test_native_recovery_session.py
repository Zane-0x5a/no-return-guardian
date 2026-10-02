from types import SimpleNamespace
from pathlib import Path
import subprocess
import sys
import threading
import unittest
import json
import tempfile

from NativeRecoverySession import read_failure_evidence, watch_recovery_session
from NativeCheckpointProbe import ProbeError
from local_evidence import game_executable, requires


class RecoverySessionTests(unittest.TestCase):
    @requires('native-runtime-20260912/collection-insurance-storage')
    def test_real_bridge_failure_is_persisted_before_process_termination(self):
        from NativeRecoveryDispatcherCheck import check
        from NativeRecoverySource import prepare_source
        root = Path(__file__).resolve().parents[2]
        storage = root / 'artifacts/native-runtime-20260912/collection-insurance-storage'
        snapshot = '20260912-072519061-manual-62fed724'
        manifest = json.loads((storage / 'snapshots' / snapshot / 'manifest.json').read_text(encoding='utf-8'))
        source = prepare_source(storage, snapshot, manifest['SourceProfilePath'])
        executable = game_executable()
        if executable is None or not executable.is_file():
            self.skipTest('set NRG_GAME_EXE to the verified game executable')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'failure.json'
            result = check(executable, True, source, failure_path=path)
            self.assertEqual(result['exitCode'], 5, result)
            evidence = json.loads(path.read_text(encoding='utf-8'))
            identity = {'pid': evidence['pid'], 'birth': 42, 'base': evidence['base']}
            verified = read_failure_evidence(path, identity, snapshot)
            self.assertEqual(verified['reason'], 'owned-durable-failure')
            self.assertEqual(verified['boundary']['rva'], '133ec5e')
            self.assertTrue(verified['scheduled'])
            for key in ('pid', 'birth', 'base'):
                with self.subTest(key=key), self.assertRaises(ProbeError):
                    read_failure_evidence(path, {**identity, key: 'wrong'}, snapshot)
            with self.assertRaises(ProbeError):
                read_failure_evidence(path, identity, 'other-snapshot')
            path.write_text('x' * 32769, encoding='utf-8')
            with self.assertRaises(ProbeError):
                read_failure_evidence(path, identity, snapshot)

    def test_target_loss_wakes_wait_immediately_and_preserves_crash_evidence(self):
        callbacks = {}
        session = SimpleNamespace(on=lambda kind, action: callbacks.update({kind: action}),
                                  off=lambda kind, action: callbacks.pop(kind))
        done = threading.Event()
        records, failures = [], []

        def failed(error):
            failures.append(str(error))
            done.set()

        close = watch_recovery_session(session, SimpleNamespace(), records.append, failed)
        crash = SimpleNamespace(pid=11, process_name='owned-fixture', summary='access-violation', report='fixture stack')
        callbacks['detached']('process-terminated', crash)
        self.assertTrue(done.wait(0))
        self.assertEqual(records[0]['crash']['report'], 'fixture stack')
        self.assertFalse(records[0]['inGameAcceptance'])
        self.assertIn('process-terminated', failures[0])
        close()
        self.assertFalse(callbacks)

    def test_explicit_teardown_does_not_become_a_target_failure(self):
        callbacks, failures = {}, []
        session = SimpleNamespace(on=lambda kind, action: callbacks.update({kind: action}),
                                  off=lambda kind, action: callbacks.pop(kind))
        close = watch_recovery_session(session, SimpleNamespace(), lambda record: self.fail('unexpected record'), failures.append)
        pending_detach = callbacks['detached']
        close()
        pending_detach('application-requested', None)
        self.assertFalse(failures)

    def test_real_frida_target_exit_is_observed_with_the_native_exit_code(self):
        import ctypes
        from ctypes import wintypes
        sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools/native-probe-deps'))
        import frida
        child = subprocess.Popen([sys.executable, '-c', 'import sys; input(); sys.exit(7)'],
                                 stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                 creationflags=subprocess.CREATE_NO_WINDOW)
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        process = SimpleNamespace(kernel=kernel, handle=kernel.OpenProcess(0x1000, False, child.pid))
        done = threading.Event()
        records = []
        session = None
        try:
            session = frida.attach(child.pid)
            close = watch_recovery_session(session, process, records.append, lambda error: done.set())
            child.communicate(b'\n', timeout=10)
            self.assertTrue(done.wait(5), 'Target death must wake the host, not wait for the experiment deadline')
            self.assertEqual(records[0]['reason'], 'process-terminated')
            self.assertEqual(records[0]['exitCode'], 7)
            close()
        finally:
            if session is not None:
                session.detach()
            if process.handle:
                kernel.CloseHandle(process.handle)
            if child.poll() is None:
                child.kill()
                child.communicate(timeout=5)


if __name__ == '__main__':
    unittest.main()
