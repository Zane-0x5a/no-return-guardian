"""Run one directory's Python tests for scripts\\test-all.ps1 and print a summary line it reads.

unittest reports to stderr as usual. stdout ends with `SUMMARY {...}`: the counts and every skip reason, so
a run that skipped the replays of local evidence never reads like one that exercised them.
"""

from collections import Counter
import contextlib
import json
import sys
import unittest


def main(directory):
    suite = unittest.defaultTestLoader.discover(directory, pattern='test_*.py', top_level_dir=directory)
    # Some tests print the journal lines they write; keep them off the summary channel.
    with contextlib.redirect_stdout(sys.stderr):
        result = unittest.TextTestRunner(stream=sys.stderr).run(suite)
    failed = len(result.failures) + len(result.errors) + len(result.unexpectedSuccesses)
    summary = {'run': result.testsRun, 'failed': failed, 'skipped': len(result.skipped),
               'reasons': dict(Counter(reason for _, reason in result.skipped))}
    print('SUMMARY ' + json.dumps(summary, ensure_ascii=False))
    return 1 if failed or not result.testsRun else 0


if __name__ == '__main__':
    raise SystemExit(main(sys.argv[1]))
