import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

from NativeCollectionArchive import CollectionArchive
from NativeCollectionControl import start_transfer_heartbeat


ROOT = Path(__file__).resolve().parents[2]
TRACE = ROOT / 'artifacts/native-runtime-20260912/collection-observe-01.jsonl'


@unittest.skipUnless(os.environ.get('RUN_NATIVE_COLLECTION_TRANSPORT_TESTS') == '1',
                     'opt-in Frida test against a newly spawned disposable Python process only')
class CollectionTransportTests(unittest.TestCase):
    def test_real_exit_payload_burst_is_durable_without_ack_starvation(self):
        result, manifest, events, errors = self.exercise()
        self.assertEqual(errors, [])
        self.assertIsNone(result['error'], result)
        self.assertEqual(len(manifest['payloads']), result['expectedCount'])
        self.assertEqual(manifest['decodedBytes'], result['expectedBytes'])
        self.assertTrue(any(event['kind'] == 'collection-backpressure' for event in events))

    def test_slow_durable_writer_handles_repeated_real_sized_bursts(self):
        result, manifest, events, errors = self.exercise(cycles=3, writer_delay=0.03)
        self.assertEqual(errors, [])
        self.assertIsNone(result['error'], result)
        self.assertEqual(len(manifest['payloads']), result['expectedCount'])
        self.assertEqual(manifest['decodedBytes'], result['expectedBytes'])
        self.assertLessEqual(result['pendingBytes'], 64 * 1024 * 1024)

    def test_missing_acknowledgements_time_out_without_infinite_native_wait(self):
        started = time.monotonic()
        result, manifest, events, errors = self.exercise(drop_ack=True)
        self.assertEqual(errors, [])
        self.assertEqual(result['error'], 'Collection acknowledgement timed out')
        self.assertLess(time.monotonic() - started, 12)
        self.assertLess(result['payloadCount'], result['expectedCount'])

    def test_writer_failure_wakes_blocked_producer(self):
        result, manifest, events, errors = self.exercise(writer_failure=True)
        self.assertIn('fixture disk full', result['error'])
        self.assertIsNone(manifest)

    def test_real_native_threads_preserve_payloads_under_backpressure(self):
        result, manifest, events, errors = self.exercise(native_threads=True)
        self.assertEqual(errors, [])
        self.assertIsNone(result['error'], result)
        self.assertEqual(len(manifest['payloads']), result['expectedCount'])
        self.assertEqual(manifest['decodedBytes'], result['expectedBytes'])

    def exercise(self, cycles=1, writer_delay=0, drop_ack=False, writer_failure=False, native_threads=False):
        sys.path.insert(0, str(ROOT / 'tools/native-probe-deps'))
        import frida

        records = [json.loads(line) for line in TRACE.read_text(encoding='utf-8').splitlines()]
        stages = [item for item in records if item.get('kind') == 'collection-payload' and
                  item['payloadId'] >= 5]
        lengths = [item['byteLength'] for item in stages]
        lengths.append(2 * 4559176)
        lengths *= cycles
        self.assertGreater(sum(lengths), 64 * 1024 * 1024)
        fixture_source = '''
import ctypes
import sys
import threading
import time
sleep = ctypes.WinDLL('kernel32').Sleep
sleep.argtypes = [ctypes.c_uint32]
print(ctypes.cast(sleep, ctypes.c_void_p).value, flush=True)
sys.stdin.readline()
workers = [threading.Thread(target=lambda: [sleep(37) for index in range(5)]) for worker in range(4)]
for worker in workers:
    worker.start()
for worker in workers:
    worker.join()
print('done', flush=True)
time.sleep(90)
'''
        fixture = subprocess.Popen([sys.executable, '-I', '-c', fixture_source],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True,
                                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        watchdog = threading.Timer(40, fixture.terminate)
        watchdog.start()
        session = None
        script = None
        heartbeat = None
        errors = []
        events = []
        with tempfile.TemporaryDirectory() as temporary:
            writer = None
            try:
                session = frida.attach(fixture.pid)
                extension = '\n'.join(Path(__file__).with_name(name).read_text(encoding='utf-8')
                                      for name in ('NativeCollectionCodec.js', 'NativeCollectionExtension.js'))
                source = extension + '''
const image = { base: Memory.alloc(0x415ac90 + 0x72d0) };
const source = Memory.alloc(16 * 1024 * 1024);
const destination = Memory.alloc(16 * 1024 * 1024);
const collector = createNativeCollection({}, image, (kind, fields, data) => send({kind, ...fields}, data));
let nativeError = null;
let listener = null;
let nativeCallId = 0;
rpc.exports = {
    observe(address) {
        collector.start();
        listener = Interceptor.attach(ptr(address), {
            onEnter(args) {
                if (args[0].toInt32() !== 37 || nativeError !== null) return;
                this.callId = ++nativeCallId;
                try {
                    collector.enter('save-buffer-copy', this,
                        [ptr(1), destination, source, ptr(4194304), ptr(2), ptr(0)]);
                } catch (error) { nativeError = error.message; }
            },
            onLeave() {
                if (this.callId === undefined || nativeError !== null) return;
                try { collector.leave('save-buffer-copy', this, ptr(0)); }
                catch (error) { nativeError = error.message; }
            }
        });
        Interceptor.flush();
    },
    finish() {
        listener.detach();
        Interceptor.flush();
        return {error: nativeError, ...collector.stop()};
    },
    collect(lengths) {
        collector.start();
        let callId = 0;
        try {
            for (const length of lengths) {
                ++callId;
                for (let offset = 0; offset < length - 4; offset += 65536) {
                    source.add(offset).writeU32(callId * 1024 + offset / 65536);
                }
                collector.enter('save-buffer-copy', {callId},
                    [ptr(1), destination, source, ptr(length), ptr(2), ptr(0)]);
            }
            return {error: null, ...collector.stop()};
        } catch (error) {
            return {error: error.message, ...collector.stop()};
        }
    }
};
'''
                script = session.create_script(source)

                def acknowledge(identifier):
                    if not drop_ack:
                        script.post({'type': 'collection-ack', 'payloadId': identifier})

                writer = CollectionArchive(Path(temporary) / 'payloads', {'fixturePid': fixture.pid}, acknowledge)

                def message(message, data):
                    try:
                        self.assertEqual(message['type'], 'send')
                        event = message['payload']
                        events.append(event)
                        if event['kind'] == 'collection-payload':
                            writer.submit(event, data)
                    except Exception as error:
                        errors.append(str(error))
                        script.post({'type': 'collection-ack', 'error': str(error)})

                script.on('message', message)
                script.load()
                original_store = writer._store

                def store(event, data):
                    time.sleep(writer_delay)
                    if writer_failure:
                        raise OSError('fixture disk full')
                    original_store(event, data)

                heartbeat = start_transfer_heartbeat(script, writer, lambda error: errors.append(str(error)))
                with mock.patch.object(writer, '_store', side_effect=store):
                    if native_threads:
                        script.exports_sync.observe(fixture.stdout.readline().strip())
                        fixture.stdin.write('start\n')
                        fixture.stdin.flush()
                        self.assertEqual(fixture.stdout.readline().strip(), 'done')
                        result = script.exports_sync.finish()
                        result.update(expectedCount=41, expectedBytes=20 * 3 * 4194304 + 0x72d0)
                    else:
                        result = script.exports_sync.collect(lengths)
                        result.update(expectedCount=len(lengths) + 1, expectedBytes=sum(lengths) + 0x72d0)
                    if writer_failure:
                        with self.assertRaisesRegex(Exception, 'fixture disk full'):
                            writer.close(False)
                        manifest = None
                    else:
                        writer.drain()
                        heartbeat[0].set()
                        heartbeat[1].join(2)
                        manifest = writer.close(True)
                    writer = None
                return result, manifest, events, errors
            finally:
                if heartbeat is not None:
                    heartbeat[0].set()
                    heartbeat[1].join(2)
                if script is not None:
                    script.unload()
                if session is not None:
                    session.detach()
                if writer is not None:
                    writer.close(False)
                fixture.terminate()
                fixture.wait(5)
                fixture.stdin.close()
                fixture.stdout.close()
                watchdog.cancel()


if __name__ == '__main__':
    unittest.main()
