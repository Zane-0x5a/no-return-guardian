import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from NativeCollectionArchive import CollectionArchive
from NativeCollectionCodec import ENCODING, PayloadDecoder, chunk_layout
from NativeCheckpointProbe import ProbeError
from NativeCollectionReport import assess
from local_evidence import requires


def encoded(identifier=1, content=b'data', reference=False):
    chunk = {'sha256': hashlib.sha256(content).hexdigest(), 'size': len(content)}
    if not reference:
        chunk['wireOffset'] = 0
    return {'kind': 'collection-payload', 'payloadId': identifier, 'encoding': ENCODING,
            'byteLength': len(content), 'wireByteLength': 0 if reference else len(content),
            'regions': [{'name': 'input', 'offset': 0, 'size': len(content)}], 'chunks': [chunk]}


class CollectionCodecTests(unittest.TestCase):
    def test_reference_only_payloads_are_reconstructed_and_acknowledged(self):
        with tempfile.TemporaryDirectory() as temporary:
            acknowledgements = []
            writer = CollectionArchive(Path(temporary) / 'payloads', {}, acknowledgements.append, max_total=4)
            writer.submit(encoded(), b'data')
            writer.submit(encoded(2, reference=True), None)
            writer.drain()
            manifest = writer.close(True)
            self.assertEqual(acknowledgements, [1, 2])
            self.assertEqual(manifest['bytesReceived'], 4)
            self.assertEqual(manifest['decodedBytes'], 8)
            self.assertEqual(len(list((Path(temporary) / 'payloads').glob('*.bin'))), 1)

    def test_missing_forward_and_self_references_fail_before_ack(self):
        with tempfile.TemporaryDirectory() as temporary:
            acknowledged = []
            writer = CollectionArchive(Path(temporary) / 'payloads', {}, acknowledged.append)
            writer.submit(encoded(reference=True), b'')
            with self.assertRaisesRegex(ProbeError, 'Missing prior'):
                writer.close(False)
            self.assertEqual(acknowledged, [])

    def test_corrupt_wire_is_not_acknowledged(self):
        with tempfile.TemporaryDirectory() as temporary:
            acknowledged = []
            writer = CollectionArchive(Path(temporary) / 'payloads', {}, acknowledged.append)
            writer.submit(encoded(), b'evil')
            with self.assertRaisesRegex(ProbeError, 'hash mismatch'):
                writer.close(False)
            self.assertEqual(acknowledged, [])

    def test_reference_corruption_on_disk_is_detected_without_cached_copy(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            decoder = PayloadDecoder(root)
            event = encoded()
            decoded, layout, additions = decoder.decode(event, b'data')
            filename = hashlib.sha256(decoded).hexdigest() + '.bin'
            (root / filename).write_bytes(decoded)
            decoder.remember(filename, decoded, layout, additions)
            decoder.cached.clear()
            (root / filename).write_bytes(b'evil')
            with self.assertRaisesRegex(ProbeError, 'hash mismatch'):
                decoder.decode(encoded(2, reference=True), b'')

    def test_invalid_layouts_cannot_claim_coverage(self):
        mutations = (
            lambda event: event.update(encoding='unknown'),
            lambda event: event.update(byteLength=5),
            lambda event: event.update(wireByteLength=-1),
            lambda event: event['chunks'][0].update(size=0),
            lambda event: event['chunks'][0].update(size=True),
            lambda event: event['chunks'][0].update(sha256='../escape'),
            lambda event: event['chunks'][0].update(wireOffset=1),
            lambda event: event['chunks'][0].update(wireOffset=False),
            lambda event: event['regions'][0].update(offset=1),
            lambda event: event.update(chunks=[]),
        )
        for mutate in mutations:
            event = copy.deepcopy(encoded())
            mutate(event)
            with self.subTest(event=event):
                with self.assertRaises(ProbeError):
                    chunk_layout(event, {})
                self.assertTrue(assess([event])['errors'])

    def test_reference_does_not_hide_an_unacknowledged_dependency(self):
        records = [encoded(), encoded(2, reference=True), {'kind':'collection-payload-stored', 'payloadId':2}]
        report = assess(records)
        self.assertIn(1, report['pendingPayloadIds'])
        self.assertFalse(report['departureEvidencePresent'])
        self.assertEqual(report['logicalPayloadBytes'], 8)
        self.assertEqual(report['transmittedPayloadBytes'], 4)

    def test_unimplemented_whole_bundle_references_are_rejected(self):
        report = assess([{'kind':'collection-payload-reference', 'referenceId':1, 'basePayloadId':0}])
        self.assertIn('unsupported unverified payload reference', report['errors'])

    @requires('native-runtime-20260912/collection-observe-02.payloads')
    @unittest.skipUnless(os.environ.get('COLLECTION_NODE_EXE'), 'requires explicit Node executable for real archive replay')
    def test_real_javascript_encoder_and_python_writer_reconstruct_all_failed_session_bytes(self):
        root = Path(__file__).resolve().parents[2]
        evidence = root / 'artifacts/native-runtime-20260912/collection-observe-02.payloads'
        original = json.loads((evidence / 'manifest.json').read_text(encoding='utf-8'))
        source = r'''
const fs = require('node:fs');
const crypto = require('node:crypto');
const vm = require('node:vm');
const path = require('node:path');
const context = {Checksum:{compute:(algorithm, bytes) => crypto.createHash(algorithm).update(Buffer.from(bytes)).digest('hex')}};
vm.runInNewContext(fs.readFileSync(process.argv[1], 'utf8'), context);
const codec = context.createCollectionEncoder();
const manifest = JSON.parse(fs.readFileSync(path.join(process.argv[2], 'manifest.json'), 'utf8'));
for (const item of manifest.payloads) {
    const bytes = fs.readFileSync(path.join(process.argv[2], item.file));
    if (crypto.createHash('sha256').update(bytes).digest('hex') !== item.sha256) throw new Error('Source hash mismatch');
    const regions = item.event.regions.map(region => region.status === 'unallocated' ? region :
        {...region, bytes:Uint8Array.from(bytes.subarray(region.offset, region.offset + region.size)).buffer});
    const encoded = codec.encode(regions);
    const line = Buffer.from(JSON.stringify({event:{...item.event,...encoded.fields}, wire:Buffer.from(encoded.wire).toString('base64')}) + '\n');
    let offset = 0;
    while (offset < line.length) offset += fs.writeSync(1, line, offset, line.length - offset);
}
'''
        import base64
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / 'payloads'
            writer = CollectionArchive(directory, original['identity'], lambda identifier: None)
            process = subprocess.Popen([os.environ['COLLECTION_NODE_EXE'], '-e', source,
                                        str(Path(__file__).with_name('NativeCollectionCodec.js')), str(evidence)],
                                       stdout=subprocess.PIPE, text=True, encoding='utf-8',
                                       creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            try:
                for line in process.stdout:
                    entry = json.loads(line)
                    writer.submit(entry['event'], base64.b64decode(entry['wire']))
                self.assertEqual(process.wait(30), 0)
                writer.drain(timeout=30)
                result = writer.close(True, timeout=30)
                self.assertEqual(len(result['payloads']), len(original['payloads']))
                self.assertEqual(result['decodedBytes'], original['bytesReceived'])
                self.assertLess(result['bytesReceived'], 64 * 1024 * 1024)
                for before, after in zip(original['payloads'], result['payloads']):
                    self.assertEqual(before['sha256'], after['sha256'])
                    self.assertEqual(before['size'], after['size'])
                print(json.dumps({'realPayloadsVerified':len(result['payloads']),
                                  'decodedBytes':result['decodedBytes'], 'wireBytes':result['bytesReceived']}))
            finally:
                if process.poll() is None:
                    process.terminate()
                    process.wait(5)
                process.stdout.close()
                if not writer.closed:
                    writer.close(False)


if __name__ == '__main__':
    unittest.main()
