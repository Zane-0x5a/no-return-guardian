"""Bounded asynchronous evidence storage; no game access or restoration API."""

import hashlib
import json
import os
from pathlib import Path
import queue
import threading
import time

from NativeCheckpointProbe import ProbeError
from NativeCollectionCodec import ENCODING, PayloadDecoder, chunk_layout


class CollectionArchive:
    def __init__(self, directory, identity, acknowledge, max_pending=64 * 1024 * 1024,
                 max_total=1024 * 1024 * 1024, max_items=4096):
        self.directory = Path(directory)
        self.directory.mkdir(parents=False, exist_ok=False)
        self.identity = dict(identity)
        self.acknowledge = acknowledge
        self.max_pending = max_pending
        self.max_total = max_total
        self.pending = 0
        self.total = 0
        self.decoded_total = 0
        self.decoder = PayloadDecoder(self.directory)
        self.items = []
        self.accepted_ids = set()
        self.error = None
        self.closed = False
        self.lock = threading.Lock()
        self.queue = queue.Queue(maxsize=max_items)
        self.worker = threading.Thread(target=self._write_loop, name='native-evidence-writer', daemon=True)
        self.worker.start()

    def submit(self, event, data):
        identifier = event.get('payloadId')
        if event.get('encoding') == ENCODING and event.get('wireByteLength') == 0 and data is None:
            data = b''
        if type(identifier) is not int or identifier <= 0 or not isinstance(data, bytes):
            raise ProbeError('Invalid collection payload identity or bytes')
        if 'encoding' in event and event['encoding'] != ENCODING:
            raise ProbeError('Unsupported collection payload encoding')
        expected = event.get('wireByteLength') if 'encoding' in event else event.get('byteLength')
        if type(expected) is not int or len(data) != expected or len(data) > 32 * 1024 * 1024 or \
                (not data and 'encoding' not in event):
            raise ProbeError('Invalid collection payload length')
        decoded_length = event.get('byteLength')
        if type(decoded_length) is not int or not 0 < decoded_length <= 32 * 1024 * 1024:
            raise ProbeError('Invalid decoded collection payload length')
        metadata = json.loads(json.dumps(event, allow_nan=False))
        with self.lock:
            self._check()
            if identifier in self.accepted_ids:
                raise ProbeError('Duplicate collection payload identity')
            if self.pending + len(data) > self.max_pending or self.total + len(data) > self.max_total:
                raise ProbeError('Collection byte budget exceeded')
            if self.decoded_total + decoded_length > 8 * 1024 * 1024 * 1024:
                raise ProbeError('Collection reconstructed byte budget exceeded')
            try:
                self.queue.put_nowait((metadata, data))
            except queue.Full as error:
                raise ProbeError('Collection writer queue full') from error
            self.accepted_ids.add(identifier)
            self.pending += len(data)
            self.total += len(data)
            self.decoded_total += decoded_length

    def _check(self):
        if self.error is not None:
            raise ProbeError(f'Collection writer failed: {self.error}')
        if self.closed:
            raise ProbeError('Collection writer is closed')

    def _store(self, event, data):
        layout = None
        if event.get('encoding') == ENCODING:
            data, layout, additions = self.decoder.decode(event, data)
        digest = hashlib.sha256(data).hexdigest()
        filename = digest + '.bin'
        target = self.directory / filename
        if target.exists():
            if target.is_symlink() or hashlib.sha256(target.read_bytes()).hexdigest() != digest:
                raise ProbeError('Existing collection content hash mismatch')
        else:
            with target.open('xb') as output:
                output.write(data)
                output.flush()
                os.fsync(output.fileno())
        if layout is not None:
            self.decoder.remember(filename, data, layout, additions)
        self.items.append({'event': event, 'file': filename, 'sha256': digest, 'size': len(data)})

    def _write_loop(self):
        while True:
            entry = self.queue.get()
            try:
                if entry is None:
                    return
                event, data = entry
                if self.error is None:
                    self._store(event, data)
                    with self.lock:
                        self.pending -= len(data)
                    entry = None
                    self.acknowledge(event['payloadId'])
            except Exception as error:
                with self.lock:
                    self.error = str(error)
            finally:
                if entry is not None:
                    with self.lock:
                        self.pending -= len(entry[1])
                self.queue.task_done()

    def check(self):
        with self.lock:
            self._check()

    def drain(self, timeout=15):
        deadline = time.monotonic() + timeout
        while True:
            with self.lock:
                self._check()
                if self.pending == 0 and self.queue.unfinished_tasks == 0:
                    return
            if time.monotonic() >= deadline:
                raise ProbeError('Collection writer drain timed out')
            time.sleep(0.01)

    def close(self, code_restored, timeout=15):
        with self.lock:
            if self.closed:
                raise ProbeError('Collection writer already closed')
            self.closed = True
        try:
            self.queue.put(None, timeout=timeout)
        except queue.Full as error:
            raise ProbeError('Collection writer did not drain') from error
        self.worker.join(timeout)
        if self.worker.is_alive():
            raise ProbeError('Collection writer did not stop')
        if self.error is not None:
            raise ProbeError(f'Collection writer failed: {self.error}')
        if {item['event']['payloadId'] for item in self.items} != self.accepted_ids:
            raise ProbeError('Collection payload missing from writer')
        known_chunks = {}
        for item in self.items:
            payload = (self.directory / item['file']).read_bytes()
            if len(payload) != item['size'] or hashlib.sha256(payload).hexdigest() != item['sha256']:
                raise ProbeError('Collection payload failed final verification')
            if 'encoding' in item['event']:
                layout, additions = chunk_layout(item['event'], known_chunks)
                for offset, chunk in layout:
                    if hashlib.sha256(payload[offset:offset + chunk['size']]).hexdigest() != chunk['sha256']:
                        raise ProbeError('Collection archived chunk failed final verification')
                known_chunks.update(additions)
        manifest = {'schema': 1, 'kind': 'native-collection-evidence', 'identity': self.identity,
                    'restorable': False, 'coherentSnapshot': False, 'inGameAcceptance': False,
                    'collectionComplete': False, 'codeRestored': code_restored,
                    'payloadsVerified': True, 'bytesReceived': self.total,
                    'decodedBytes': self.decoded_total, 'payloads': self.items}
        with (self.directory / 'manifest.json').open('x', encoding='utf-8') as output:
            json.dump(manifest, output, ensure_ascii=True, allow_nan=False, indent=2)
            output.flush()
            os.fsync(output.fileno())
        return manifest
