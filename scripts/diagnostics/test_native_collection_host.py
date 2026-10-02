import contextlib
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

import NativeCheckpointTrigger as host


class CollectionHostTests(unittest.TestCase):
    def test_collection_requires_explicit_profile_before_game_access(self):
        values = ['11', '--mode', 'collection-observe', '--log', 'trace.jsonl',
                  '--confirm-intrusive-experiment']
        for entry in (host.main, host.supervised_main):
            with mock.patch.object(host, 'ReadOnlyProcess') as process:
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                    entry(values)
                process.assert_not_called()

    def test_collection_cannot_reuse_outputs_or_enable_control_modes(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            common = ['11', '--mode', 'collection-observe', '--log', str(directory / 'trace.jsonl'),
                      '--collection-profile', str(directory), '--confirm-intrusive-experiment']
            self.assertEqual(host.arguments(common + ['--duration', '1800']).duration, 1800)
            (directory / 'trace.supervisor.jsonl').write_text('parent owns this log', encoding='utf-8')
            self.assertEqual(host.arguments(common).mode, 'collection-observe')
            for extra in (['--duration', '1801'], ['--lifecycle-content'], ['--observation', 'previous'],
                          ['--confirm-native-resume-current-preparation'], ['--confirm-restart-current-checkpoint']):
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                    host.arguments(common + extra)
            (directory / 'trace.files').mkdir()
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                host.arguments(common)

    def exercise(self, corrupt_length=False):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            profile = root / 'profile'
            profile.mkdir()
            executable = root / 'fixture.exe'
            executable.write_bytes(b'fixture')
            events = []
            args = SimpleNamespace(pid=11, mode='collection-observe', log=root / 'trace.jsonl',
                                   collection_profile=profile, duration=5, lifecycle_content=False)
            process = SimpleNamespace(image_path=lambda: executable, image_base=lambda: 0x10000000,
                                      close=lambda: events.append('process-close'))

            class Script:
                exports_sync = None

                def __init__(self):
                    self.exports_sync = self

                def on(self, kind, handler):
                    self.handler = handler

                def load(self):
                    self.handler({'type': 'send', 'payload': {'kind': 'load-state'}}, None)
                    self.handler({'type': 'send', 'payload': {'kind': 'collection-payload',
                        'payloadId': 1, 'byteLength': 5 if corrupt_length else 4}}, b'data')
                    self.handler({'type': 'send', 'payload': {'kind': 'stopped', 'reason': 'deadline'}}, None)

                def post(self, message):
                    events.append('ack')

                def stop(self):
                    events.append('stop')

                def unload(self):
                    events.append('unload')

            script = Script()
            session = SimpleNamespace(create_script=lambda source: script,
                                      detach=lambda: events.append('detach'))
            fake_frida = SimpleNamespace(attach=lambda pid: session)
            with mock.patch.object(host, 'arguments', return_value=args), \
                    mock.patch.object(host, 'ReadOnlyProcess', return_value=process), \
                    mock.patch.object(host, 'verify_build', return_value='fixed'), \
                    mock.patch.object(host, 'fingerprints', return_value=[]), \
                    mock.patch.object(host, 'verify_live', side_effect=lambda *args: events.append('verify')), \
                    mock.patch.object(host, 'process_birth', return_value=22), \
                    mock.patch.dict('sys.modules', {'frida': fake_frida}), \
                    contextlib.redirect_stdout(io.StringIO()):
                if corrupt_length:
                    with self.assertRaisesRegex(host.ProbeError, 'payload length'):
                        host.main([])
                else:
                    self.assertEqual(host.main([]), 0)
            manifest = json.loads((root / 'trace.payloads/manifest.json').read_text(encoding='utf-8'))
            self.assertEqual(manifest['codeRestored'], True)
            self.assertFalse(manifest['collectionComplete'])
            self.assertLess(events.index('stop'), events.index('unload'))
            self.assertLess(events.index('unload'), events.index('detach'))
            if not corrupt_length:
                self.assertLess(events.index('ack'), events.index('unload'))
                self.assertEqual(len(manifest['payloads']), 1)

    def test_binary_data_is_drained_before_script_unload(self):
        self.exercise()

    def test_transport_failure_surfaces_after_hook_cleanup(self):
        self.exercise(corrupt_length=True)


if __name__ == '__main__':
    unittest.main()
