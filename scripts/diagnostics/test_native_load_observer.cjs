const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { requires } = require('./local_evidence.cjs');
const vm = require('node:vm');
const crypto = require('node:crypto');

test('collection pins every additional boundary before attaching', () => {
    for (const missingRva of [0x120af40, 0x120d090, 0x120dac0, 0xd966f0, 0xdc1780]) {
        const probe = fixture({collection: true, missingRva});
        assert.equal(probe.hooks.size, 0);
        assert.match(probe.messages.at(-1).reason, /fingerprint/);
    }
});

test('collection captures changed bytes at the same allocation address', () => {
    const probe = fixture({collection: true});
    probe.memory.set(probe.base + 0x35f9f40, 0x40000000);
    probe.byteMemory.set(0x40000000, new Uint8Array(0x6790).fill(7));
    const invocation = probe.enter(0x11f9300, [1, 1, 0, 0]);
    probe.byteMemory.set(0x40000000, new Uint8Array(0x6790).fill(8));
    probe.leave(0x11f9300, invocation);
    const captures = probe.messages.filter(message => message.kind === 'collection-payload' &&
        message.stage.startsWith('copy-checkpoint'));
    assert.equal(captures.length, 2);
    assert.equal(captures[0].regions[0].address, captures[1].regions[0].address);
    assert.equal(probe.binaries.get(captures[0].payloadId)[0], 7);
    assert.equal(probe.binaries.get(captures[1].payloadId)[0], 8);
    assert.equal(captures[0].callId, captures[1].callId);
    assert.equal(captures[0].coherentSnapshot, false);
});

test('collection copy captures source before mutation and both buffers after', () => {
    const probe = fixture({collection: true});
    probe.byteMemory.set(0x40000000, Uint8Array.from([1, 2, 3, 4]));
    const invocation = probe.enter(0x120d090, [1, 0x50000000, 0x40000000, 4, 2, -1]);
    probe.byteMemory.set(0x40000000, Uint8Array.from([4, 3, 2, 1]));
    probe.byteMemory.set(0x50000000, Uint8Array.from([5, 6, 7, 8]));
    probe.leave(0x120d090, invocation);
    const captures = probe.messages.filter(message => message.stage?.startsWith('save-buffer-copy'));
    assert.deepEqual(Array.from(probe.binaries.get(captures[0].payloadId)), [1, 2, 3, 4]);
    assert.deepEqual(Array.from(probe.binaries.get(captures[1].payloadId)), [4, 3, 2, 1, 5, 6, 7, 8]);
});

test('collection rejects invalid buffer lengths without a truncated payload', () => {
    for (const size of [0, 0x1000001, 0xffffffffffff]) {
        const probe = fixture({collection: true});
        probe.enter(0x120d090, [1, 0x50000000, 0x40000000, size, 2, -1]);
        assert.match(probe.messages.at(-1).reason, /Invalid collection memory range/);
        assert.equal(probe.binaries.size, 1);
        assert.equal(probe.detached(), probe.hooks.size);
    }
});

test('collection arena is read only from the same thread active apply', () => {
    const probe = fixture({collection: true});
    probe.memory.set(0x30000000, 0x31000000);
    probe.memory.set(0x31000008, 0x32000000);
    probe.memory.set(0x40000030, 0x41000000);
    probe.memory.set(0x40000038, 8);
    const apply = probe.enter(0x120af40, [1, 0x30000000, 0, 1]);
    const unrelated = probe.enter(0x120dac0, [1, 0x30000000, 0, 0], 8);
    probe.leave(0x120dac0, unrelated, 0);
    const getter = probe.enter(0x120dac0, [1, 0x30000000, 0, 0]);
    probe.leave(0x120dac0, getter, 0x40000000);
    probe.leave(0x120af40, apply);
    const captures = probe.messages.filter(message => message.stage === 'save-json-root:consumed');
    assert.equal(captures.length, 1);
    assert.equal(captures[0].regions[1].size, 8);
    assert.equal(captures[0].regions[1].address, '0x41000000');
});

test('collection destructor never reads the object after destruction', () => {
    const probe = fixture({collection: true});
    const construction = probe.enter(0xd95530);
    probe.leave(0xd95530, construction);
    const destruction = probe.enter(0xd966f0);
    destruction.player.readU32 = () => { throw new Error('use after free'); };
    destruction.player.add = () => { throw new Error('use after free'); };
    probe.leave(0xd966f0, destruction);
    assert.equal(probe.detached(), 0);
    const rebuilt = probe.enter(0xd95530);
    probe.leave(0xd95530, rebuilt);
    const details = probe.messages.filter(message => message.kind === 'collection-leave-detail' &&
        message.operation === 'player-construct');
    assert.deepEqual(details.map(message => message.detail.player.observedGeneration), [1, 2]);
});

test('collection short reads stop and never publish a matching-size bundle', () => {
    const probe = fixture({collection: true, shortReadAt: 0x40000000});
    probe.enter(0x120d090, [1, 0x50000000, 0x40000000, 4, 2, -1]);
    assert.match(probe.messages.at(-1).reason, /Short collection read/);
    assert.equal(probe.binaries.size, 1);
});

test('collection acknowledgements release pending but not cumulative bytes', () => {
    const probe = fixture({collection: true});
    const installed = probe.messages.find(message => message.kind === 'collection-payload');
    probe.ack(installed.payloadId);
    probe.ack(installed.payloadId);
    probe.ack(999);
    probe.deadline();
    const stopped = probe.messages.at(-1);
    assert.equal(stopped.collection.pendingBytes, 0);
    assert.equal(stopped.collection.totalBytes, installed.byteLength);
});

test('collection references repeated full checkpoint bytes while retaining all events', () => {
    const probe = fixture({collection: true});
    const first = probe.enter(0x10434c0);
    probe.leave(0x10434c0, first);
    for (let index = 0; index < 1000; index++) {
        const invocation = probe.enter(0x10434c0);
        probe.leave(0x10434c0, invocation);
    }
    const payloads = probe.messages.filter(message => message.kind === 'collection-payload');
    assert.equal(payloads.length, 2003);
    assert.ok(payloads.slice(1).every(payload => payload.wireByteLength === 0));
    assert.equal(probe.detached(), 0);
});

test('collection retains normal subnode aggregation under a real-sized burst', () => {
    const probe = fixture({collection: true});
    for (let index = 0; index < 20000; index++) {
        const invocation = probe.enter(0x1bdb630);
        probe.leave(0x1bdb630, invocation, index % 2);
    }
    probe.deadline();
    assert.equal(probe.messages.at(-1).reason, 'deadline');
    assert.equal(probe.binaries.size, 1);
    const summary = probe.messages.find(message => message.kind === 'load-subnode-summary');
    assert.equal(summary.returned, 20000);
});

test('collection scene queries deduplicate high frequency without a hideout claim', () => {
    const probe = fixture({collection: true});
    for (let index = 0; index < 20000; index++) {
        const invocation = probe.enter(0x1b6ea60, [probe.base + 0x9334770, 0, 0, 0]);
        probe.leave(0x1b6ea60, invocation, 0x40000000);
    }
    const events = probe.messages.filter(message => message.kind === 'collection-task');
    assert.equal(events.length, 1);
    assert.equal(events[0].playableHideoutClaim, false);
    assert.equal(probe.detached(), 0);
});

test('collection stable marks reject unavailable allocations as a complete preparation', () => {
    const probe = fixture({collection: true});
    const result = probe.context.rpc.exports.mark('preparation-before-departure');
    assert.equal(result.twoPassEqual, true);
    assert.equal(result.knownRegionsComplete, false);
    probe.deadline();
    assert.throws(() => probe.context.rpc.exports.mark('post-death-menu'), /No active collection/);
});

test('collection no longer samples an object while its destructor is running', () => {
    const probe = fixture({collection: true});
    const construction = probe.enter(0xd95530);
    probe.leave(0xd95530, construction);
    const destruction = probe.enter(0xd966f0);
    const count = probe.binaries.size;
    probe.sample();
    assert.equal(probe.binaries.size, count);
    probe.leave(0xd966f0, destruction);
});

test('collection timer never follows a constructor address after the callback returns', () => {
    const inaccessible = new Set();
    const probe = fixture({collection: true, inaccessible});
    const construction = probe.enter(0xd95530);
    probe.leave(0xd95530, construction);
    inaccessible.add(0x20000000);
    const count = probe.binaries.size;
    probe.sample();
    assert.equal(probe.detached(), 0);
    assert.equal(probe.binaries.size, count);
});

test('collection never labels reused constructor storage as a live player', () => {
    const probe = fixture({collection: true});
    const construction = probe.enter(0xd95530);
    probe.leave(0xd95530, construction);
    probe.byteMemory.set(0x20000000, new Uint8Array(0x5d80).fill(0xee));
    probe.sample();
    assert.equal(probe.messages.some(message => message.stage === 'live-player-sample'), false);
});

function fixture(options = {}) {
    const base = 0x10000000;
    const memory = new Map();
    const hooks = new Map();
    const messages = [];
    const binaries = new Map();
    const byteMemory = new Map();
    const chunks = new Map();
    let receiver = null;
    let detached = 0;
    let interval = null;
    let timeout = null;
    const rvas = [0xfe96f0, 0xfdcd90, 0x1b80080, 0xfd7870, 0x1042680,
        0x103f2c0, 0x1046290, 0x1b6e000, 0x1bdeed0, 0x1b6fa70, 0x1b7d080, 0x1bdb630,
        0xfe8fc0, 0x1048520, 0x1049bf0];
    if (options.collection) rvas.push(0x1048960, 0x10434c0, 0x1200ac0, 0x11f9300,
        0x120af40, 0x120d090, 0x120dac0, 0x1b7fc60, 0xd95530, 0xd966f0,
        0xdacc20, 0xdafea0, 0xdaf950, 0xdc1780, 0x1b6ea60, 0x105e930);
    class Pointer {
        constructor(value) { this.value = Number(value); }
        add(offset) { return new Pointer(this.value + offset); }
        equals(other) { return this.value === other.value; }
        isNull() { return this.value === 0; }
        toString() { return `0x${this.value.toString(16)}`; }
        toInt32() { return this.value | 0; }
        toUInt32() { return this.value >>> 0; }
        readS32() {
            if (options.readFault) throw new Error('unreadable');
            return memory.get(this.value) ?? 0;
        }
        readU32() { return this.readS32() >>> 0; }
        readU8() { return this.readS32() & 255; }
        readU64() { return BigInt(this.readS32()); }
        readPointer() { return new Pointer(this.readS32()); }
        readByteArray(length) {
            if (options.inaccessible?.has(this.value)) throw new Error('expired constructor address');
            if (options.shortReadAt === this.value) return new ArrayBuffer(length - 1);
            const bytes = byteMemory.get(this.value);
            return bytes ? bytes.slice(0, length).buffer
                : new Uint8Array(length).fill(options.codeChanged ? 1 : 0).buffer;
        }
    }
    const context = {
        nativeCheckpointConfiguration: { mode: options.mode ?? (options.collection ? 'collection-observe' : 'load-observe'), pid: 11,
            base: `0x${base.toString(16)}`, duration: 30,
            fingerprints: options.missing ? [] : rvas.filter(rva => rva !== options.missingRva)
                .map(rva => ({rva, bytes: '00'.repeat(32)})) },
        Process: { arch: 'x64', id: options.pid ?? 11,
            mainModule: { name: 'tlou-ii.exe', base: new Pointer(base) } },
        ptr: value => new Pointer(value),
        Checksum: { compute: (algorithm, data) => crypto.createHash(algorithm).update(Buffer.from(data)).digest('hex') },
        NativeFunction() { throw new Error('Observer must not call native functions'); },
        Interceptor: { attach(address, callbacks) {
            if (hooks.size === options.attachFailureAt) throw new Error('attach failed');
            hooks.set(address.value - base, callbacks);
            return { detach() { detached++; if (options.detachFault && detached === 1) throw new Error('detach failed'); } };
        }, flush() {} },
        send: (message, data) => {
            messages.push(message);
            if (data !== undefined) {
                const wire = Buffer.from(data);
                const decoded = message.chunks?.map(chunk => {
                    if (chunk.wireOffset !== undefined) chunks.set(chunk.sha256,
                        wire.subarray(chunk.wireOffset, chunk.wireOffset + chunk.size));
                    assert.ok(chunks.has(chunk.sha256), 'reference has no prior content');
                    return chunks.get(chunk.sha256);
                });
                binaries.set(message.payloadId, new Uint8Array(decoded ? Buffer.concat(decoded) : wire));
            }
        },
        recv: (kind, callback) => { receiver = callback; }, rpc: { exports: {} },
        setInterval: callback => { interval = callback; return 1; },
        clearInterval: () => { interval = null; },
        setTimeout: callback => { timeout = callback; }
    };
    if (options.collection) for (const name of ['NativeCollectionCodec.js', 'NativeCollectionExtension.js']) {
        vm.runInNewContext(fs.readFileSync(path.join(__dirname, name), 'utf8'), context);
    }
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'NativeLoadObserver.js'), 'utf8'), context);
    function enter(rva, values = [0x20000000, 0x30000000, 10, 1], threadId = 7) {
        const invocation = { threadId, returnAddress: new Pointer(base + 0x1234) };
        hooks.get(rva).onEnter.call(invocation, values.map(value => new Pointer(value)));
        return invocation;
    }
    function leave(rva, invocation = {}, value = 0) {
        hooks.get(rva).onLeave.call(invocation, new Pointer(value));
    }
    return { base, memory, hooks, messages, binaries, byteMemory, context, enter, leave,
        ack: payloadId => receiver?.({payloadId}),
        detached: () => detached, sample: () => interval?.(), deadline: () => timeout?.() };
}

test('installation and sampling never invoke native functions or claim acceptance', () => {
    const probe = fixture();
    assert.equal(probe.hooks.size, 15);
    assert.ok(probe.messages.some(message => message.kind === 'load-state'));
    probe.sample();
    assert.equal(probe.messages.filter(message => message.kind === 'load-state').length, 1);
    for (const message of probe.messages) {
        assert.equal(message.nativeCallsIssued, 0);
        assert.equal(message.inGameAcceptance, false);
        assert.equal(message.restoredPreparation, false);
    }
});

test('identity and fingerprint failures install no hooks', () => {
    for (const options of [{pid: 12}, {mode: 'trigger'}, {missing: true}, {codeChanged: true},
        ...[0xfe8fc0, 0x1048520, 0x1049bf0].map(missingRva => ({missingRva}))]) {
        const probe = fixture(options);
        assert.equal(probe.hooks.size, 0);
        assert.match(probe.messages.at(-1).reason, /load-error/);
    }
});

test('native menu exit records checkpoint update before nested teardown and run reset', () => {
    const probe = fixture();
    const run = probe.base + 0x415ac90;
    probe.memory.set(run + 0x72c8, 1);
    probe.memory.set(run + 0x520, 1);
    probe.memory.set(run + 0xa0, 1);
    const menu = probe.enter(0xfe8fc0);
    const update = probe.enter(0x1049bf0);
    probe.leave(0x1049bf0, update);
    const teardown = probe.enter(0x1042680);
    const reset = probe.enter(0x1048520);
    probe.memory.set(run + 0x72c8, 0);
    probe.memory.set(run + 0x520, 2);
    probe.leave(0x1048520, reset);
    probe.leave(0x1042680, teardown);
    probe.leave(0xfe8fc0, menu);
    const events = probe.messages.filter(message => message.callId !== undefined);
    assert.deepEqual(events.map(event => event.operation), [
        'menu-exit', 'update-run-checkpoint', 'update-run-checkpoint',
        'run-teardown', 'run-reset', 'run-reset', 'run-teardown', 'menu-exit'
    ]);
    assert.equal(events[0].state.runMode, 1);
    assert.equal(events[4].state.runInitialized, 1);
    assert.equal(events[4].state.runSelector, 1);
    assert.equal(events[5].state.runInitialized, 0);
    assert.equal(events[5].state.runSelector, 2);
    assert.equal(events[4].callId, events[5].callId);
    assert.equal(events[0].state.runInitialized, 1);
    assert.ok(events.every(event => event.nativeCallsIssued === 0 && !event.inGameAcceptance));
});

test('partial installation and read faults detach all installed hooks', () => {
    for (const options of [{attachFailureAt: 3}, {readFault: true}]) {
        const probe = fixture(options);
        assert.equal(probe.detached(), probe.hooks.size);
        assert.match(probe.messages.at(-1).reason, /load-error/);
    }
});

test('command kind and parameter are distinct; unchanged queue count does not claim acceptance', () => {
    const probe = fixture();
    probe.memory.set(0x30000000, 1);
    probe.memory.set(0x30000018, 10);
    const invocation = probe.enter(0x1b6e000);
    probe.leave(0x1b6e000, invocation);
    const entered = probe.messages.find(message => message.kind === 'load-enter');
    assert.equal(entered.detail.commandKind, 1);
    assert.equal(entered.detail.parameter18, 10);
    const returned = probe.messages.at(-1);
    assert.equal(returned.detail.queueCount, 0);
    assert.equal(returned.detail.acceptanceClaim, false);
});

test('nested and cross-thread calls retain separate entry-return identities', () => {
    const probe = fixture();
    const first = probe.enter(0x1bdeed0);
    const second = probe.enter(0x1b6fa70, undefined, 8);
    probe.leave(0x1b6fa70, second);
    probe.leave(0x1bdeed0, first);
    const events = probe.messages.filter(message => message.callId !== undefined);
    assert.equal(events[0].callId, events[3].callId);
    assert.equal(events[1].callId, events[2].callId);
    assert.notEqual(events[0].callId, events[1].callId);
});

test('wait predicate deduplicates unchanged samples but records changed loader state', () => {
    const probe = fixture();
    probe.leave(0x1b7d080, {}, 0);
    probe.leave(0x1b7d080, {}, 0);
    probe.memory.set(probe.base + 0x9341660 + 0x5e0, 20);
    probe.leave(0x1b7d080, {}, 0);
    const events = probe.messages.filter(message => message.kind === 'load-wait-predicate');
    assert.equal(events.length, 2);
    assert.equal(events[1].predicateCalls, 3);
    assert.equal(events[1].state.requestState, 20);
    assert.equal(events[1].inGameAcceptance, false);
});

test('null queue record and null task fail closed', () => {
    for (const [rva, values] of [[0x1b6e000, [1, 0, 0, 0]], [0x1bdeed0, [0, 0, 0, 0]],
        [0x1bdb630, [0, 0, 0, 0]]]) {
        const probe = fixture();
        const invocation = probe.enter(rva, values);
        probe.leave(rva, invocation);
        assert.match(probe.messages.at(-1).reason, /load-error/);
        assert.equal(probe.detached(), 15);
    }
});

test('subnode outcome is recorded without post-return dereference or success claim', () => {
    const probe = fixture();
    const invocation = probe.enter(0x1bdb630);
    probe.leave(0x1bdb630, invocation, 0);
    probe.deadline();
    const summary = probe.messages.find(message => message.kind === 'load-subnode-summary');
    assert.equal(summary.entered, 1);
    assert.equal(summary.returned, 1);
    assert.equal(summary.groups[0].returnedZero, 1);
    assert.equal(probe.messages.at(-1).inGameAcceptance, false);
});

test('normal-load subnode burst preserves totals without exhausting boundary log budget', () => {
    const probe = fixture();
    const play = probe.enter(0x1bdeed0);
    for (let index = 0; index < 20000; index++) {
        const invocation = probe.enter(0x1bdb630);
        probe.leave(0x1bdb630, invocation, index % 2);
    }
    probe.leave(0x1bdeed0, play);
    assert.equal(probe.detached(), 0);
    assert.ok(probe.messages.length < 10);
    const summary = probe.messages.find(message => message.kind === 'load-subnode-summary');
    assert.equal(summary.entered, 20000);
    assert.equal(summary.returned, 20000);
    assert.equal(summary.groups[0].returnedZero, 10000);
    assert.equal(summary.groups[0].returnedNonzero, 10000);
    assert.equal(summary.samples.length, 16);
    assert.equal(summary.fullCallTrace, false);
});

test('captured failed observation replays its real subnode burst without observer failure', requires('native-runtime-20260906/normal-load-observe-02.jsonl'), () => {
    const source = path.join(__dirname, '../../artifacts/native-runtime-20260906/normal-load-observe-02.jsonl');
    const records = fs.readFileSync(source, 'utf8').trim().split(/\r?\n/).map(JSON.parse);
    const probe = fixture();
    const calls = new Map();
    let entered = 0;
    let returned = 0;
    for (const record of records) {
        if (record.kind === 'load-subnode-enter') {
            const address = Number(record.task.address);
            probe.memory.set(address + 0x60, record.task.state60);
            calls.set(record.callId, probe.enter(0x1bdb630, [address, record.parameter, 1, 0], record.threadId));
            entered++;
        } else if (record.kind === 'load-subnode-leave') {
            probe.leave(0x1bdb630, calls.get(record.callId), record.returned);
            returned++;
        }
    }
    probe.deadline();
    const stopped = probe.messages.at(-1);
    assert.equal(stopped.reason, 'deadline');
    const summary = probe.messages.find(message => message.kind === 'load-subnode-summary');
    assert.equal(summary.entered, entered);
    assert.equal(summary.returned, returned);
    assert.equal(summary.activeAtSnapshot, entered - returned);
    assert.ok(entered >= 1000);
    assert.ok(probe.messages.length < 10);
});

test('aggregation bounds distinct groups without losing totals and preserves in-flight calls', () => {
    const probe = fixture();
    const pending = probe.enter(0x1bdb630, [0x20000000, -1, 1, 0]);
    for (let parameter = 0; parameter < 1000; parameter++) {
        const invocation = probe.enter(0x1bdb630, [0x20000000, parameter, 1, 0]);
        probe.leave(0x1bdb630, invocation, 1);
    }
    probe.deadline();
    const summary = probe.messages.find(message => message.kind === 'load-subnode-summary');
    assert.equal(summary.groups.length, 65);
    assert.ok(summary.groups.some(group => group.other));
    assert.equal(summary.groups.reduce((sum, group) => sum + group.entered, 0), 1001);
    assert.equal(summary.activeAtSnapshot, 1);
    const count = probe.messages.length;
    probe.leave(0x1bdb630, pending, 0);
    assert.equal(probe.messages.length, count);
    assert.equal(summary.returned, 1000);
});

test('successive summaries snapshot nested returns without mutating earlier evidence', () => {
    const probe = fixture();
    const first = probe.enter(0x1bdb630);
    const second = probe.enter(0x1bdb630, undefined, 8);
    probe.leave(0x1bdb630, second, 0);
    const play = probe.enter(0x1bdeed0);
    probe.leave(0x1bdeed0, play);
    const firstSummary = probe.messages.find(message => message.kind === 'load-subnode-summary');
    probe.leave(0x1bdb630, first, 1);
    probe.deadline();
    const summaries = probe.messages.filter(message => message.kind === 'load-subnode-summary');
    assert.equal(firstSummary.activeAtSnapshot, 1);
    assert.equal(firstSummary.samples[0].returned, undefined);
    assert.equal(summaries[1].activeAtSnapshot, 0);
    assert.equal(summaries[1].samples[0].returned, 1);
    assert.equal(summaries[1].samples[1].returned, 0);
});

test('event flood stops rather than dropping evidence and pretending success', () => {
    const probe = fixture();
    for (let index = 0; index < 1030; index++) {
        const invocation = probe.enter(0xfd7870);
        probe.leave(0xfd7870, invocation);
    }
    assert.match(probe.messages.at(-1).reason, /event budget/);
    assert.equal(probe.detached(), probe.hooks.size);
});

test('deadline prevents late returns and repeated stop is idempotent', () => {
    const probe = fixture();
    const invocation = probe.enter(0xfd7870);
    probe.deadline();
    const count = probe.messages.length;
    probe.leave(0xfd7870, invocation);
    probe.context.rpc.exports.stop();
    probe.sample();
    assert.equal(probe.messages.length, count);
    assert.equal(probe.detached(), probe.hooks.size);
});

test('one detach failure does not skip remaining cleanup', () => {
    const probe = fixture({detachFault: true});
    probe.deadline();
    assert.equal(probe.detached(), probe.hooks.size);
    assert.match(probe.messages.at(-1).reason, /load-error: cleanup/);
});
