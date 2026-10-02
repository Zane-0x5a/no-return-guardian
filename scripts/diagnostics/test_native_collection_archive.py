import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest import mock

from NativeCollectionArchive import CollectionArchive, ProbeError


class CollectionArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'evidence'

    def event(self, identifier=1, length=4):
        return {'kind': 'collection-payload', 'payloadId': identifier, 'byteLength': length}

    def test_payloads_are_durable_deduplicated_and_not_restore_claims(self):
        acknowledgements = []
        writer = CollectionArchive(self.path, {'pid': 11}, acknowledgements.append)
        event = self.event()
        writer.submit(event, b'data')
        event['payloadId'] = 9
        writer.submit(self.event(2), b'data')
        manifest = writer.close(True)
        self.assertEqual(acknowledgements, [1, 2])
        self.assertEqual(len(list(self.path.glob('*.bin'))), 1)
        self.assertEqual(manifest['bytesReceived'], 8)
        for key in ('restorable', 'coherentSnapshot', 'collectionComplete', 'inGameAcceptance'):
            self.assertFalse(manifest[key])
        self.assertTrue(manifest['payloadsVerified'])
        self.assertEqual(json.loads((self.path / 'manifest.json').read_text())['identity'], {'pid': 11})

    def test_duplicate_identity_bad_lengths_and_nonbytes_are_rejected(self):
        writer = CollectionArchive(self.path, {}, lambda identifier: None)
        writer.submit(self.event(), b'data')
        for event, data in [(self.event(), b'data'), (self.event(2), b'bad'),
                            (self.event(2), 'data'), (self.event(True), b'data'),
                            (self.event(0), b'data'), (self.event(2, 0), b'')]:
            with self.subTest(event=event), self.assertRaises(ProbeError):
                writer.submit(event, data)
        writer.close(False)

    def test_slow_writer_is_bounded_and_no_truncation_is_accepted(self):
        entered = threading.Event()
        release = threading.Event()
        original = CollectionArchive._store

        def slow_store(writer, event, data):
            entered.set()
            release.wait(5)
            original(writer, event, data)

        with mock.patch.object(CollectionArchive, '_store', slow_store):
            writer = CollectionArchive(self.path, {}, lambda identifier: None, max_pending=4)
            try:
                writer.submit(self.event(), b'data')
                self.assertTrue(entered.wait(2))
                with self.assertRaisesRegex(ProbeError, 'budget'):
                    writer.submit(self.event(2), b'more')
            finally:
                release.set()
                manifest = writer.close(True)
        self.assertEqual(len(manifest['payloads']), 1)

    def test_cumulative_budget_is_not_reset_by_acknowledgement(self):
        completed = threading.Event()
        writer = CollectionArchive(self.path, {}, lambda identifier: completed.set(), max_total=4)
        writer.submit(self.event(), b'data')
        self.assertTrue(completed.wait(2))
        with self.assertRaisesRegex(ProbeError, 'budget'):
            writer.submit(self.event(2), b'more')
        writer.close(True)

    def test_disk_failure_is_not_acknowledged_or_published_as_success(self):
        acknowledged = []
        with mock.patch.object(CollectionArchive, '_store', side_effect=OSError('disk full')):
            writer = CollectionArchive(self.path, {}, acknowledged.append)
            writer.submit(self.event(), b'data')
            with self.assertRaisesRegex(ProbeError, 'disk full'):
                writer.close(False)
        self.assertEqual(acknowledged, [])
        self.assertFalse((self.path / 'manifest.json').exists())

    def test_corruption_after_ack_is_detected_on_close(self):
        completed = threading.Event()
        writer = CollectionArchive(self.path, {}, lambda identifier: completed.set())
        writer.submit(self.event(), b'data')
        self.assertTrue(completed.wait(2))
        next(self.path.glob('*.bin')).write_bytes(b'evil')
        with self.assertRaisesRegex(ProbeError, 'verification'):
            writer.close(True)

    def test_existing_directory_is_never_reused(self):
        self.path.mkdir()
        with self.assertRaises(FileExistsError):
            CollectionArchive(self.path, {}, lambda identifier: None)

    def test_ack_failure_is_a_failed_archive(self):
        def failed_ack(identifier):
            raise RuntimeError('disconnected')

        writer = CollectionArchive(self.path, {}, failed_ack)
        writer.submit(self.event(), b'data')
        with self.assertRaisesRegex(ProbeError, 'disconnected'):
            writer.close(False)

    def test_ack_can_admit_next_payload_without_exceeding_host_pending(self):
        completed = threading.Event()
        writer = None

        def acknowledge(identifier):
            if identifier == 1:
                writer.submit(self.event(2), b'next')
            else:
                completed.set()

        writer = CollectionArchive(self.path, {}, acknowledge, max_pending=4)
        writer.submit(self.event(), b'data')
        self.assertTrue(completed.wait(2))
        manifest = writer.close(True)
        self.assertEqual(len(manifest['payloads']), 2)

    def test_drain_waits_for_ack_completion_after_durable_write(self):
        entered = threading.Event()
        release = threading.Event()

        def acknowledge(identifier):
            entered.set()
            release.wait(5)

        writer = CollectionArchive(self.path, {}, acknowledge)
        try:
            writer.submit(self.event(), b'data')
            self.assertTrue(entered.wait(2))
            with self.assertRaisesRegex(ProbeError, 'drain timed out'):
                writer.drain(timeout=0.02)
        finally:
            release.set()
            writer.close(True)

    def test_closed_writer_cannot_receive_payloads(self):
        writer = CollectionArchive(self.path, {}, lambda identifier: None)
        writer.close(True)
        with self.assertRaisesRegex(ProbeError, 'closed'):
            writer.submit(self.event(), b'data')


if __name__ == '__main__':
    unittest.main()
