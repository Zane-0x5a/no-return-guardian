const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, 'NativeResultsLifecycleProbe.js'), 'utf8');
const sites = [0x104c570, 0x104d4f0, 0x1b6e000, 0x1bdeed0, 0x1042680, 0x1048520, 0xfe8fc0];
const base = 0x140000000;

function pointer(value) {
    const address = Number(value);
    return {
        address,
        add(offset) { return pointer(address + offset); },
        sub(other) { return pointer(address - other.address); },
        compare(other) { return Math.sign(address - (other.address ?? other)); },
        equals(other) { return address === other.address; },
        isNull() { return address === 0; },
        readByteArray(length) { return new Uint8Array(length).buffer; },
        readS32() { return 0; },
        readU8() { return 0; },
        readU64() { return address; },
        toInt32() { return address; },
        toString(radix) { return radix === 16 ? address.toString(16) : `0x${address.toString(16)}`; }
    };
}

function fixture(fingerprints = sites.map(rva => ({rva, bytes: '00'.repeat(32)}))) {
    const messages = [];
    const hooks = new Map();
    const detached = [];
    const context = {
        nativeResultsLifecycleConfiguration: {mode: 'results-lifecycle-observe', pid: 42,
            base: `0x${base.toString(16)}`, duration: 60, fingerprints},
        Process: {id: 42, arch: 'x64', mainModule: {name: 'tlou-ii.exe', base: pointer(base), size: 0x40000000}},
        Interceptor: {
            attach(address, callback) {
                hooks.set(address.address - base, callback);
                return {detach() { detached.push(address.address - base); }};
            },
            flush() {}
        },
        Thread: {backtrace() { return [pointer(base + 0x1234)]; }},
        Backtracer: {ACCURATE: 1},
        rpc: {exports: {}},
        ptr: pointer,
        send(message) { messages.push(message); },
        setTimeout() {}
    };
    vm.runInNewContext(source, context);
    return {context, messages, hooks, detached};
}

test('records only the death command and detaches without issuing game control', () => {
    const probe = fixture();
    assert.equal(probe.hooks.size, sites.length);
    const command = {
        isNull() { return false; },
        readS32() { return 1; },
        add(offset) {
            return {readS32() { return offset === 0x18 ? 2 : 0; },
                readU64() { return {toString() { return '1'; }}; }};
        }
    };
    probe.hooks.get(0x1b6e000).call(
        {threadId: 12, returnAddress: pointer(base + 0x1b6f879), context: {}},
        [pointer(0), command, pointer(0), pointer(0)]);
    probe.hooks.get(0x1bdeed0).call(
        {threadId: 12, returnAddress: pointer(base + 0x1b701c5), context: {}},
        [pointer(0), pointer(0), pointer(10)]);
    assert.equal(probe.messages.filter(message => message.kind === 'native-results-boundary').length, 1);
    probe.hooks.get(0x1bdeed0).call(
        {threadId: 12, returnAddress: pointer(base + 0x1b701c5), context: {}},
        [pointer(0), pointer(0), pointer(2)]);
    const record = probe.messages.find(message => message.site === 'play-task');
    assert.equal(record.site, 'play-task');
    assert.equal(record.caller, 'exe+0x1b701c5');
    assert.equal(record.nativeCallsIssued, 0);
    assert.equal(record.gameTerminationIssued, false);
    probe.context.rpc.exports.stop();
    assert.equal(probe.detached.length, sites.length);
    assert.equal(probe.messages.at(-1).kind, 'stopped');
});

test('rejects missing code fingerprint before attaching anything', () => {
    const probe = fixture([]);
    assert.equal(probe.hooks.size, 0);
    assert.match(probe.messages.at(-1).reason, /fingerprint/i);
});
