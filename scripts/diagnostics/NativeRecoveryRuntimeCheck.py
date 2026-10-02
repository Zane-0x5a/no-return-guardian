"""Check Frida source transport and native argument substitution in an owned helper process."""

import argparse
import json
from pathlib import Path
import subprocess
import sys

from NativeRecoverySource import prepare_source


SOURCE = r'''
const sourceConfig = CONFIG;
const nativeModule = new CModule(`int extract(unsigned char *worker, unsigned char *destination, unsigned int capacity) {
    if (*(unsigned int *)(worker + 12) != 2) return 0;
    unsigned int length = *(unsigned int *)(worker + 72) - 1424;
    unsigned char *source = *(unsigned char **)(worker + 64);
    if (length > capacity) return 0;
    for (unsigned int index = 0; index < length; index++) destination[index] = source[1424 + index];
    return 1;
}
unsigned int run(void *context) {
    void **arguments = (void **)context;
    return extract(arguments[0], arguments[1], (unsigned int)(unsigned long long)arguments[2]);
}`);
rpc.exports = { check(packet) {
    if (Checksum.compute('sha256', packet) !== sourceConfig.packetSha256) throw new Error('packet hash');
    const input = Memory.alloc(sourceConfig.rawSize);
    input.writeByteArray(packet.slice(0, sourceConfig.rawSize));
    const shadow = Memory.alloc(0x50);
    shadow.writeByteArray(new Uint8Array(0x50));
    shadow.add(12).writeU32(2);
    shadow.add(64).writePointer(input);
    shadow.add(72).writeU32(sourceConfig.rawSize);
    const original = Memory.alloc(0x50);
    original.writeByteArray(new Uint8Array(0x50));
    const destination = Memory.alloc(sourceConfig.capacity);
    let entries = 0;
    const boundaries = [];
    const listener = Interceptor.attach(nativeModule.extract, { onEnter(args) {
        entries++;
        boundaries.push({ before: args[0].toString(), destination: args[1].toString(), capacity: args[2].toUInt32(),
            shadow: shadow.toString(), status: shadow.add(12).readU32(), source: shadow.add(64).readPointer().toString(),
            size: shadow.add(72).readU32() });
        args[0] = shadow;
        boundaries.at(-1).after = args[0].toString();
    } });
    Interceptor.flush();
    const createThread = new NativeFunction(Module.getGlobalExportByName('CreateThread'), 'pointer',
        ['pointer','size_t','pointer','pointer','uint','pointer'], { abi:'win64', scheduling:'cooperative' });
    const waitThread = new NativeFunction(Module.getGlobalExportByName('WaitForSingleObject'), 'uint',
        ['pointer','uint'], { abi:'win64', scheduling:'cooperative' });
    const threadResult = new NativeFunction(Module.getGlobalExportByName('GetExitCodeThread'), 'bool', ['pointer','pointer']);
    const closeThread = new NativeFunction(Module.getGlobalExportByName('CloseHandle'), 'bool', ['pointer']);
    const callArguments = Memory.alloc(24);
    callArguments.writePointer(original);
    callArguments.add(8).writePointer(destination);
    callArguments.add(16).writePointer(ptr(sourceConfig.capacity));
    const thread = createThread(ptr(0), 0, nativeModule.run, callArguments, 0, ptr(0));
    if (thread.isNull() || waitThread(thread, 10000) !== 0) throw new Error('Owned native thread failed to finish');
    const exitCode = Memory.alloc(4);
    if (!threadResult(thread, exitCode)) throw new Error('Native thread result is unavailable');
    closeThread(thread);
    const result = exitCode.readU32();
    listener.detach();
    Interceptor.flush();
    const data = destination.readByteArray(sourceConfig.capacity);
    const nativeCopyHash = Checksum.compute('sha256', data);
    const view = new DataView(data);
    const basis = BigInt(destination.add(0x418).toString());
    const fields = new Uint32Array(packet.slice(sourceConfig.rawSize));
    for (const field of fields) view.setBigUint64(0x418 + field, view.getBigUint64(0x418 + field, true) + basis, true);
    for (const field of fields) view.setBigUint64(0x418 + field, view.getBigUint64(0x418 + field, true) - basis, true);
    const normalizedHash = Checksum.compute('sha256', data);
    return { runtime: Script.runtime, fridaVersion: Frida.version, entries, result, boundaries,
        originalStatus: original.add(12).readU32(), normalizedHash, nativeCopyHash,
        expectedHash: sourceConfig.bodySha256,
        argumentReplacement: result === 1 && entries === 1 && original.add(12).readU32() === 0 && nativeCopyHash === sourceConfig.bodySha256,
        bigintRoundtrip: normalizedHash === nativeCopyHash, packetBytes:packet.byteLength,
        bodyBytes:sourceConfig.capacity,
        shutdownExportResolved:!Module.getGlobalExportByName('TerminateProcess').isNull(), gameOpened:false };
}};
'''


def check(source):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools/native-probe-deps'))
    import frida
    child = subprocess.Popen([sys.executable, '-c', 'input()'], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    session = None
    script = None
    try:
        session = frida.attach(child.pid)
        script = session.create_script(SOURCE.replace('CONFIG', json.dumps(source.configuration)))
        script.load()
        return script.exports_sync.check(source.packet)
    finally:
        try:
            if script is not None:
                script.unload()
            if session is not None:
                session.detach()
        finally:
            try:
                child.communicate(b'\n', timeout=10)
            except subprocess.TimeoutExpired:
                child.kill()
                child.communicate(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('storage', type=Path)
    parser.add_argument('snapshot_id')
    parser.add_argument('profile', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = check(prepare_source(args.storage, args.snapshot_id, args.profile))
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result))
    return 0 if result['argumentReplacement'] and result['bigintRoundtrip'] and result['shutdownExportResolved'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
