from pathlib import Path
import tempfile
import unittest
from unittest import mock

from NativeCollectionFiles import FileWitness, ProbeError


class FileWitnessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        # 见证者按解析后的路径打开文件；CI 的临时目录是 8.3 短名，不先解析就对不上下面打补丁的路径。
        self.root = Path(self.temp.name).resolve()
        self.profile = self.root / 'profile'
        (self.profile / 'gamedata').mkdir(parents=True)
        self.source = self.profile / 'gamedata' / 'R0A.save'
        self.source.write_bytes(b'original')
        self.events = []
        self.witness = FileWitness(self.profile, self.root / 'files', self.events.append)

    def test_partial_envelope_retained_as_evidence_not_restorable_snapshot(self):
        self.witness.poll(True)
        event = self.events[-1]
        self.assertFalse(event['restorable'])
        self.assertFalse(event['coherentSnapshot'])
        self.assertEqual(sum(record.get('absent', False) for record in event['files']), 10)
        self.assertEqual(self.source.read_bytes(), b'original')
        self.witness.poll(True)
        self.assertEqual(len(self.events), 1)

    def test_versions_remain_independent_after_source_change_or_removal(self):
        self.witness.poll(True)
        first = self.events[-1]['files'][0]['file']
        self.source.write_bytes(b'changed')
        self.witness.poll(True)
        self.source.unlink()
        self.witness.poll(True)
        self.assertEqual(len(self.events), 3)
        self.assertEqual((self.root / 'files' / first).read_bytes(), b'original')
        self.assertTrue(self.events[-1]['files'][0]['absent'])

    def test_metadata_race_does_not_publish_a_version(self):
        before = self.witness._metadata()
        changed = list(before)
        changed[0] = ('gamedata/R0A.save', 9, 123)
        with mock.patch.object(self.witness, '_metadata', side_effect=[before, changed]):
            self.witness.poll(True)
        self.assertEqual(self.events[-1]['kind'], 'collection-files-changing')
        self.assertEqual(self.witness.versions, 0)
        self.assertEqual(list((self.root / 'files').iterdir()), [])

    def test_same_size_content_change_with_unchanged_metadata_is_rejected(self):
        before = self.witness._metadata()
        original_open = Path.open
        reads = 0

        def changing_open(path, *args, **kwargs):
            nonlocal reads
            if path == self.source and args == ('rb',):
                reads += 1
                if reads == 2:
                    with original_open(path, 'wb') as output:
                        output.write(b'mutating')
            return original_open(path, *args, **kwargs)

        with mock.patch.object(self.witness, '_metadata', return_value=before), mock.patch.object(Path, 'open', changing_open):
            self.witness.poll(True)
        self.assertEqual(self.events[-1]['kind'], 'collection-files-changing')
        self.assertEqual(self.witness.versions, 0)

    def test_corrupt_existing_archive_is_not_trusted(self):
        self.witness.poll(True)
        retained = next((self.root / 'files').iterdir())
        retained.write_bytes(b'corrupt')
        self.witness.previous = None
        with self.assertRaisesRegex(ProbeError, 'corruption'):
            self.witness.poll(True)

    def test_storage_and_version_limits_are_enforced(self):
        self.witness.versions = 256
        with self.assertRaisesRegex(ProbeError, 'version budget'):
            self.witness.poll(True)
        self.witness.versions = 0
        self.witness.retained_bytes = 512 * 1024 * 1024
        with self.assertRaisesRegex(ProbeError, 'storage budget'):
            self.witness.poll(True)


if __name__ == '__main__':
    unittest.main()
