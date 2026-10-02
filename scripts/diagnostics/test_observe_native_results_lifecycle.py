import contextlib
import io
from pathlib import Path
import tempfile
import unittest

from ObserveNativeResultsLifecycle import arguments


class ObserveNativeResultsLifecycleTests(unittest.TestCase):
    def test_requires_explicit_consent_and_unused_absolute_log(self):
        with tempfile.TemporaryDirectory() as directory:
            log = str(Path(directory) / 'results.jsonl')
            common = ['42', '--log', log, '--duration', '600']
            self.assertEqual(arguments(common + ['--confirm-intrusive-observation']).pid, 42)
            for values in (common, common + ['--confirm-intrusive-observation', '--duration', '601'],
                           ['42', '--log', 'relative.jsonl', '--confirm-intrusive-observation']):
                with self.subTest(values=values), contextlib.redirect_stderr(io.StringIO()):
                    with self.assertRaises(SystemExit):
                        arguments(values)
            Path(log).touch()
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                arguments(common + ['--confirm-intrusive-observation'])
