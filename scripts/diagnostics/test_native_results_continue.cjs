const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, 'NativeResultsContinue.js'), 'utf8');
const sites = [0xd49630, 0xd49530];
const base = 0x140000000;
const command = 0x2300eca979d08993n;

function value(number) {
    return {
        number: BigInt(number),
        equals(other) { return this.number === other.number; },
        toString() { return this.number.toString(); }
    };
}

function pointer(address, memory = new Map()) {
    const at = Number(address);
    return {
        address: at,
        add(offset) { return pointer(at + offset, memory); },
        sub(other) { return pointer(at - other.address, memory); },
        compare(other) { return Math.sign(at - (other.address ?? other)); },
        equals(other) { return at === other.address; },
        isNull() { return at === 0; },
        readByteArray(length) { return new Uint8Array(length).buffer; },
        readU64() { return value(memory.get(at) ?? 0n); },
        readS32() { return Number(BigInt.asIntN(32, memory.get(at) ?? 0n)); },
        writeU64(next) { memory.set(at, BigInt(next)); },
        toInt32() { return at; },
        toString(radix) { return radix === 16 ? at.toString(16) : `0x${at.toString(16)}`; }
    };
}

function fixture(fingerprints = sites.map(rva => ({rva, bytes: '00'.repeat(32)}))) {
    const messages = [];
    const hooks = new Map();
    const detached = [];
    const timers = [];
    const memory = new Map();
    const clock = {now: 1000};
    const context = {
        nativeResultsContinueConfiguration: {mode: 'results-continue', pid: 42,
            base: `0x${base.toString(16)}`, duration: 60, maximumInjections: 3, minimumGapMs: 100, fingerprints},
        Date: {now: () => clock.now},
        Process: {id: 42, arch: 'x64', mainModule: {name: 'tlou-ii.exe', base: pointer(base), size: 0x40000000}},
        Interceptor: {
            attach(address, callbacks) {
                hooks.set(address.address - base, callbacks);
                return {detach() { detached.push(address.address - base); }};
            },
            flush() {}
        },
        rpc: {exports: {}},
        ptr: pointer,
        uint64: text => value(BigInt(text)),
        send(message) { messages.push(message); },
        setTimeout(callback) { timers.push(callback); }
    };
    vm.runInNewContext(source, context);
    return {context, messages, hooks, detached, timers, memory, clock};
}

function query(probe, site, sid, pad, original = 0n) {
    const argv = 0x50000;
    const result = 0x60000;
    probe.memory.set(argv, sid);
    probe.memory.set(argv + 8, BigInt(pad));
    probe.memory.set(result, original);
    const invocation = {threadId: 7, returnAddress: pointer(base + 0x1234)};
    const args = [pointer(result, probe.memory), pointer(2), pointer(argv, probe.memory), pointer(0x70000)];
    probe.hooks.get(site).onEnter.call(invocation, args);
    probe.hooks.get(site).onLeave.call(invocation, pointer(result, probe.memory));
    return probe.memory.get(result);
}

test('one token becomes exactly one continue-button result and nothing else changes', () => {
    const probe = fixture();
    assert.equal(probe.hooks.size, sites.length);
    assert.equal(query(probe, 0xd49630, command, 6), 0n);
    probe.context.rpc.exports.press(1, 1000);
    assert.equal(query(probe, 0xd49630, 0x1234n, 6), 0n);
    assert.equal(query(probe, 0xd49630, command, 5), 0n);
    assert.equal(query(probe, 0xd49630, command, 6), 1n);
    assert.equal(query(probe, 0xd49530, command, 2), 0n);
    const injected = probe.messages.filter(message => message.kind === 'results-continue-injected');
    assert.equal(injected.length, 1);
    assert.equal(injected[0].id, 1);
    assert.equal(injected[0].pad, 6);
    assert.equal(injected[0].original, '0');
    assert.equal(injected[0].gameTerminationIssued, false);
});

test('unused tokens expire, replacements are reported and the injection count is bounded', () => {
    const probe = fixture();
    probe.context.rpc.exports.press(1, 1000);
    probe.context.rpc.exports.press(2, 1000);
    assert.equal(probe.messages.at(-1).kind, 'results-continue-expired');
    assert.equal(probe.messages.at(-1).superseded, true);
    probe.timers.at(-1)();
    assert.equal(query(probe, 0xd49530, command, 2), 0n);
    for (const id of [3, 4, 5]) {
        probe.clock.now += 100;
        probe.context.rpc.exports.press(id, 1000);
        assert.equal(query(probe, 0xd49530, command, 2), 1n);
    }
    assert.equal(probe.messages.at(-1).kind, 'results-continue-stopped');
    assert.equal(probe.messages.at(-1).reason, 'injection-limit');
    assert.equal(probe.detached.length, sites.length);
    assert.throws(() => probe.context.rpc.exports.press(6, 1000), /stopped/);
});

test('back-to-back tokens still leave a released gap between two presses', () => {
    const probe = fixture();
    probe.context.rpc.exports.press(1, 400);
    assert.equal(query(probe, 0xd49630, command, 6), 1n);
    probe.clock.now += 40;
    probe.context.rpc.exports.press(2, 400);
    assert.equal(query(probe, 0xd49630, command, 6), 0n);
    probe.clock.now += 60;
    assert.equal(query(probe, 0xd49630, command, 6), 1n);
    const injected = probe.messages.filter(message => message.kind === 'results-continue-injected');
    assert.deepEqual(injected.map(message => message.id), [1, 2]);
});

test('invalid tokens and missing fingerprints install nothing', () => {
    const probe = fixture();
    assert.throws(() => probe.context.rpc.exports.press(0, 1000), /Invalid/);
    assert.throws(() => probe.context.rpc.exports.press(1, 5000), /Invalid/);
    const missing = fixture([]);
    assert.equal(missing.hooks.size, 0);
    assert.match(missing.messages.at(-1).reason, /fingerprint/i);
});
