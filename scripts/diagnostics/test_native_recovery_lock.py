import multiprocessing
import os
import unittest

from NativeCheckpointProbe import ProbeError
from NativeRecoveryLock import RecoveryLock


def contend(pid, output):
    try:
        lock = RecoveryLock(pid)
    except ProbeError as error:
        output.put(str(error))
    else:
        lock.close()
        output.put('unexpected acquisition')


class RecoveryLockTests(unittest.TestCase):
    def test_other_process_is_rejected_and_normal_release_allows_retry(self):
        lock = RecoveryLock(os.getpid())
        context = multiprocessing.get_context('spawn')
        output = context.Queue()
        worker = context.Process(target=contend, args=(os.getpid(), output))
        try:
            worker.start()
            self.assertIn('already owns', output.get(timeout=10))
            worker.join(10)
            self.assertEqual(worker.exitcode, 0)
        finally:
            if worker.is_alive():
                worker.terminate()
                worker.join(5)
            output.close()
            lock.close()
        retry = RecoveryLock(os.getpid())
        retry.close()
        retry.close()


if __name__ == '__main__':
    unittest.main()
