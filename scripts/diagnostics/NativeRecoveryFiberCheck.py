"""Reproduce return-hook behavior when a native job resumes on another worker."""

import argparse
import json
from pathlib import Path
import subprocess
import sys
import threading

from InspectPeVa import read_layout, va_to_offset
from NativeRecoveryDispatcherCheck import BRIDGE_SETUP
from NativeRecoverySource import prepare_source


SOURCE = r'''
const symbols = {};
for (const name of ['ConvertThreadToFiber','CreateFiber','SwitchToFiber','CreateThread',
                    'CreateEventW','SetEvent','WaitForSingleObject','CloseHandle']) {
    symbols[name] = Module.getGlobalExportByName(name);
}
const progress = Memory.alloc(16);
progress.writeByteArray(new Uint8Array(16));
symbols.progress = progress;
EXTRA_SYMBOLS
for (const name of ['first_root','second_root','job','ready','done']) {
    symbols[name] = Memory.alloc(8);
    symbols[name].writePointer(ptr(0));
}
const fixture = new CModule(`
extern void *ConvertThreadToFiber(void *parameter);
extern void *CreateFiber(unsigned long long size, void (*entry)(void *), void *parameter);
extern void SwitchToFiber(void *fiber);
extern void *CreateThread(void *, unsigned long long, unsigned int (*)(void *), void *, unsigned int, void *);
extern void *CreateEventW(void *, int, int, void *);
extern int SetEvent(void *event);
extern unsigned int WaitForSingleObject(void *object, unsigned int timeout);
extern int CloseHandle(void *object);
extern volatile unsigned int progress[4];
EXTRA_DECLARATIONS
extern void *first_root;
extern void *second_root;
extern void *job;
extern void *ready;
extern void *done;
void checkpoint(void) {
    progress[0]++;
    CHECKPOINT_BODY
    progress[1]++;
}
void yield_job(void) {
    if (progress[0] == 1) SwitchToFiber(first_root);
}
static void job_entry(void *unused) {
    for (unsigned int index = 0; index < ITERATIONS; index++) checkpoint();
    progress[2]++;
    SwitchToFiber(second_root);
}
static unsigned int first_worker(void *unused) {
    first_root = ConvertThreadToFiber(0);
    job = CreateFiber(0, job_entry, 0);
    if (!first_root || !job) return 2;
    SwitchToFiber(job);
    SetEvent(ready);
    return WaitForSingleObject(done, 10000);
}
static unsigned int second_worker(void *unused) {
    if (WaitForSingleObject(ready, 10000) != 0) return 3;
    second_root = ConvertThreadToFiber(0);
    if (!second_root) return 4;
    SwitchToFiber(job);
    progress[3]++;
    SetEvent(done);
    return 0;
}
unsigned int run(void *unused) {
    ready = CreateEventW(0, 1, 0, 0);
    done = CreateEventW(0, 1, 0, 0);
    void *first = CreateThread(0, 0, first_worker, 0, 0, 0);
    void *second = CreateThread(0, 0, second_worker, 0, 0, 0);
    if (!ready || !done || !first || !second) return 5;
    unsigned int first_result = WaitForSingleObject(first, 10000);
    unsigned int second_result = WaitForSingleObject(second, 10000);
    CloseHandle(first); CloseHandle(second); CloseHandle(ready); CloseHandle(done);
    return first_result | second_result;
}
`, symbols);
const events = [];
Process.setExceptionHandler(details => {
    const module = Process.findModuleByAddress(details.address);
    send({kind:'owned-fiber-exception', address:details.address.toString(), memory:details.memory,
          module:module ? module.name : null,
          offset:module ? details.address.sub(module.base).toString() : null});
    return false;
});
let listener = null;
if (MODE === 'bridge') {
    vtable.add(0x80).writePointer(fixture.yield_job);
} else if (MODE === 'probe') {
    listener = Interceptor.attach(fixture.checkpoint, function() {
        events.push({kind:'probe',thread:this.threadId});
    });
} else if (MODE !== 'none') {
    const callbacks = {onEnter() { events.push({kind:'enter',thread:this.threadId}); }};
    if (MODE === 'return') callbacks.onLeave = function() { events.push({kind:'leave',thread:this.threadId}); };
    listener = Interceptor.attach(fixture.checkpoint, callbacks);
}
Interceptor.flush();
const run = new NativeFunction(fixture.run, 'uint', ['pointer'], {abi:'win64',scheduling:'cooperative'});
rpc.exports = {run(packet) {
    if (MODE === 'bridge') createNativeRecoveryBridge(configuration).install(packet);
    const result = run(ptr(0));
    if (listener) listener.detach();
    Interceptor.flush();
    return {result,progress:Array.from(new Uint32Array(progress.readByteArray(16))),events,gameOpened:false,
        bridge: MODE === 'bridge' ? {schedules:counter.readU64().toString(),attemptedTermination,bridgeEvents} : null};
}};
'''


def check(mode, preparation=None, setup=''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools/native-probe-deps'))
    import frida
    child = subprocess.Popen([sys.executable, '-c', 'input()'], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    watchdog = threading.Timer(25, lambda: child.kill() if child.poll() is None else None)
    watchdog.start()
    events = []
    session = None
    try:
        session = frida.attach(child.pid)
        source = SOURCE.replace('MODE', json.dumps(mode))
        source = source.replace('EXTRA_SYMBOLS',
            'symbols.update=dispatcher; symbols.owner=Memory.alloc(8); symbols.owner.writePointer(owner);' if preparation else '')
        source = source.replace('EXTRA_DECLARATIONS', 'extern void update(void *); extern void *owner;' if preparation else '')
        source = source.replace('CHECKPOINT_BODY', 'update(owner);' if preparation else 'SwitchToFiber(first_root);')
        source = source.replace('ITERATIONS', '40' if preparation else '1')
        script = session.create_script(setup + source)
        script.on('message', lambda message, data: events.append(message))
        script.load()
        result = script.exports_sync.run(preparation.packet) if preparation else script.exports_sync.run()
        script.unload()
        session.detach()
        child.communicate(b'\n', timeout=5)
        return {'mode': mode, **result, 'exitCode': child.returncode, 'messages': events}
    except Exception as error:
        try:
            child.wait(timeout=3)
        except subprocess.TimeoutExpired:
            pass
        return {'mode': mode, 'error': str(error), 'exitCode': child.poll(), 'messages': events, 'gameOpened': False}
    finally:
        watchdog.cancel()
        if child.poll() is None:
            child.kill()
            child.communicate(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--bridge-image', type=Path)
    args = parser.parse_args()
    if args.bridge_image:
        root = Path(__file__).resolve().parents[2]
        storage = root / 'artifacts/native-runtime-20260912/collection-insurance-storage'
        snapshot = '20260912-072519061-manual-62fed724'
        manifest = json.loads((storage / 'snapshots' / snapshot / 'manifest.json').read_text(encoding='utf-8'))
        preparation = prepare_source(storage, snapshot, manifest['SourceProfilePath'])
        image = args.bridge_image.read_bytes()
        base, headers, sections = read_layout(image)
        offset, _ = va_to_offset(base + 0x133ebb0, base, headers, sections)
        setup = BRIDGE_SETUP.replace('CODE', json.dumps(list(image[offset:offset + 0xc6])))
        setup = setup.replace('SOURCE_CONFIG', json.dumps(preparation.configuration))
        bridge = '\n'.join(Path(__file__).with_name(name).read_text(encoding='utf-8') for name in
                           ('NativeRecoveryContract.js', 'NativeRecoveryBridge.js'))
        setup += '\n(function(Process,NativeFunction,send){\n' + bridge + \
                 '\n})(fakeProcess,fixtureNativeFunction,payload=>bridgeEvents.push(payload));\n'
        results = [check('bridge', preparation, setup)]
    else:
        results = [check(mode) for mode in ('none', 'return', 'entry', 'probe')]
    args.output.write_text(json.dumps(results, indent=2), encoding='utf-8')
    print(json.dumps(results, indent=2))
    if args.bridge_image:
        result = results[0]
        passed = (result.get('exitCode') == 0 and result.get('result') == 0 and
                  result.get('progress') == [40, 40, 1, 1] and
                  result.get('bridge', {}).get('schedules') == '1' and
                  result.get('bridge', {}).get('attemptedTermination') is False)
    else:
        passed = all(result.get('exitCode') == (3221225477 if result['mode'] in ('return', 'entry') else 0)
                     for result in results)
        passed = passed and all(result.get('progress') == [1, 1, 1, 1]
                                for result in results if result['mode'] in ('none', 'probe'))
    if not passed:
        raise SystemExit('Fiber reproduction or recovery probe validation failed')


if __name__ == '__main__':
    main()
