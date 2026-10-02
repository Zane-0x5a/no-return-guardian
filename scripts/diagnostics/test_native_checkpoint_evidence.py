import copy
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest import mock
import zipfile

from NativeCheckpointEvidence import ALLOCATIONS, RUN_RVA, capture, layout, read_exact, write_archive
from NativeCheckpointProbe import ProbeError


class Memory:
    base = 0x7FF600000000

    def __init__(self):
        self.pointers = {self.base + rva: 0x1000000000 + index * 0x200000
                         for index, (_, rva, _, _) in enumerate(ALLOCATIONS)}
        self.reads = {}
        self.maximum_read = 0

    def read(self, address, size):
        self.maximum_read = max(self.maximum_read, size)
        self.reads[address] = self.reads.get(address, 0) + 1
        if address in self.pointers:
            return struct.pack('<Q', self.pointers[address])
        return bytes(size)

    def capture(self, read=None, context=None):
        return capture(read or self.read, self.base, context or (lambda: {'pid': 1, 'birth': 2}))


class NativeCheckpointEvidenceTests(unittest.TestCase):
    def test_all_native_slots_and_shared_regions_retained_but_not_restorable(self):
        memory = Memory()
        manifest, payloads = memory.capture()
        self.assertEqual(len(payloads), 23)
        self.assertIn('records-2', payloads)
        self.assertIn('chapter', payloads)
        self.assertEqual(len(payloads['run-checkpoint-observed-span']), 0x6D8C)
        self.assertLessEqual(memory.maximum_read, 4096)
        self.assertTrue(manifest['twoPassEqual'])
        self.assertFalse(manifest['coherentSnapshot'])
        self.assertFalse(manifest['restorable'])

    def test_null_out_of_range_and_overlapping_allocations_rejected(self):
        for value in (0, 0xFFFF, 0x7FFFFFFFFFFF, 0xFFFFFFFFFFFFFFFF, 0x1000200000):
            memory = Memory()
            memory.pointers[memory.base + ALLOCATIONS[0][1]] = value
            with self.subTest(value=value), self.assertRaises(ProbeError):
                layout(memory.read, memory.base)

    def test_reallocation_between_passes_rejected_even_when_bytes_match(self):
        memory = Memory()
        target = memory.base + ALLOCATIONS[0][1]

        def moving(address, size):
            if address == target and memory.reads.get(address, 0) == 1:
                memory.pointers[address] += 0x1000
            return memory.read(address, size)

        with self.assertRaisesRegex(ProbeError, 'allocation changed'):
            memory.capture(read=moving)

    def test_each_member_mutation_is_rejected_not_just_first_slot(self):
        memory = Memory()
        regions = layout(memory.read, memory.base)
        for region in regions:
            memory = Memory()
            target = region['address']

            def changing(address, size):
                payload = memory.read(address, size)
                if address == target and memory.reads[address] == 2:
                    return b'\1' + payload[1:]
                return payload

            with self.subTest(region=region['name']), self.assertRaisesRegex(ProbeError, region['name']):
                memory.capture(read=changing)

    def test_changed_process_or_task_context_rejected(self):
        memory = Memory()
        context = mock.Mock(side_effect=[{'pid': 1, 'birth': 2, 'task': 3},
                                        {'pid': 1, 'birth': 2, 'task': 4}])
        with self.assertRaisesRegex(ProbeError, 'context changed'):
            memory.capture(context=context)

    def test_short_and_unreadable_pages_propagate_without_archive(self):
        for reader in (lambda address, size: bytes(size - 1), mock.Mock(side_effect=ProbeError('unreadable'))):
            with self.subTest(reader=reader), self.assertRaises(ProbeError):
                read_exact(reader, 0x10000000, 8192)

    def test_memory_bounds_checked_before_reader(self):
        reader = mock.Mock()
        for address, size in ((0, 8), (0x10000, 0), (0x10000, 0x100001), (0x7FFFFFFFFFFF, 8)):
            with self.subTest(address=address, size=size), self.assertRaises(ProbeError):
                read_exact(reader, address, size)
        reader.assert_not_called()

    def test_archive_roundtrip_and_existing_file_preservation(self):
        manifest, payloads = Memory().capture()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'evidence.zip'
            write_archive(output, manifest, payloads)
            before = output.read_bytes()
            with zipfile.ZipFile(output) as archive:
                self.assertIsNone(archive.testzip())
                saved = json.loads(archive.read('manifest.json'))
                self.assertFalse(saved['restorable'])
                for region in manifest['regions']:
                    self.assertEqual(archive.read(region['file']), payloads[region['name']])
            with self.assertRaises(FileExistsError):
                write_archive(output, manifest, payloads)
            self.assertEqual(output.read_bytes(), before)

    def test_corrupt_payload_or_unsafe_name_never_creates_output(self):
        original, payloads = Memory().capture()
        for mutation in ('hash', 'name', 'restorable', 'coherent', 'missing', 'duplicate'):
            manifest = copy.deepcopy(original)
            altered = dict(payloads)
            if mutation == 'hash':
                manifest['regions'][0]['sha256'] = '0' * 64
            elif mutation == 'name':
                manifest['regions'][0]['file'] = '../outside.bin'
            elif mutation == 'restorable':
                manifest['restorable'] = True
            elif mutation == 'coherent':
                manifest['coherentSnapshot'] = True
            elif mutation == 'missing':
                altered.pop(next(iter(altered)))
            else:
                manifest['regions'].append(manifest['regions'][0])
            with tempfile.TemporaryDirectory() as directory, self.subTest(mutation=mutation):
                output = Path(directory) / 'evidence.zip'
                with self.assertRaises(ProbeError):
                    write_archive(output, manifest, altered)
                self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
