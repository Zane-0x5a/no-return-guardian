const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const crypto = require('node:crypto');

function fixture(options = {}) {
    const base = 0x10000000;
    const hooks = new Map();
    const messages = [];
    const intervals = [];
    let detached = 0;
    let fault = false;
    const memory = new Map();
    const pointers = new Map();
    let readNumber = 0;
    class Pointer {
        constructor(value) { this.value = Number(value); }
        add(value) { return new Pointer(this.value + value); }
        equals(value) { return this.value === value.value; }
        isNull() { return this.value === 0; }
        toString() { return `0x${this.value.toString(16)}`; }
        toInt32() { return this.value | 0; }
        toUInt32() { return this.value >>> 0; }
        readByteArray(length) {
            if (fault) throw new Error('unreadable memory');
            readNumber++;
            const bytes = new Uint8Array(length);
            for (const [address, value] of memory) {
                if (address >= this.value && address < this.value + length) bytes[address - this.value] = value;
            }
            options.afterRead?.({ address: this.value, length, readNumber, memory, pointers });
            return bytes.buffer;
        }
        readPointer() {
            return new Pointer(pointers.get(this.value) ??
                (options.nullDescriptor ? 0 : 0x50000000 + (this.value - base) * 0x100000));
        }
        readU8() { return 1; }
        readS32() { return 2; }
    }
    const rvas = [0x1048960, 0x10434c0, 0x1042aa0, 0x1042b80, 0x1200ac0,
        0x11f9300, 0x1049bf0, 0x1046290, 0x105e930, 0x1b6ea60];
    const context = {
        nativeCheckpointConfiguration: { pid: 11, base: `0x${base.toString(16)}`,
            mode: options.mode ?? 'lifecycle', duration: 60,
            lifecycleContent: options.content === true,
            fingerprints: options.missingFingerprints ? [] : rvas.map(rva => ({ rva,
                bytes: options.mismatch ? '11'.repeat(32) : '00'.repeat(32) })) },
        Process: { id: options.pid ?? 11, arch: 'x64',
            mainModule: { name: 'tlou-ii.exe', base: new Pointer(base) } },
        ptr: value => new Pointer(value),
        Checksum: { compute: (algorithm, bytes) => crypto.createHash(algorithm)
            .update(Buffer.from(bytes)).digest('hex') },
        NativeFunction() { throw new Error('Lifecycle must not call native functions'); },
        Interceptor: {
            attach(address, callbacks) {
                if (hooks.size === options.attachFailureAt) throw new Error('attach failed');
                hooks.set(address.value - base, callbacks);
                return { detach() { detached++; } };
            }, flush() {}
        },
        send: message => messages.push(message), rpc: { exports: {} },
        setInterval: callback => intervals.push(callback), setTimeout() {}
    };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'NativeCheckpointLifecycle.js'), 'utf8'), context);
    function enter(rva, values = [0, 0, 0, 0], threadId = 7) {
        const invocation = { threadId, returnAddress: new Pointer(base + 0x1234) };
        hooks.get(rva).onEnter?.call(invocation, values.map(value => new Pointer(value)));
        return invocation;
    }
    function leave(rva, invocation, result = 0) {
        hooks.get(rva).onLeave?.call(invocation, new Pointer(result));
    }
    return { context, messages, hooks, enter, leave, intervals, base, memory, pointers,
        detached: () => detached, failReads() { fault = true; } };
}

test('passive installation samples state without invoking game functions', () => {
    const run = fixture();
    assert.equal(run.hooks.size, 10);
    assert.equal(run.messages.filter(message => message.kind === 'lifecycle-state').length, 1);
    for (let count = 0; count < 100; count++) run.intervals[0]();
    assert.equal(run.messages.filter(message => message.kind === 'lifecycle-state').length, 1);
    assert.ok(run.messages.every(message => message.restoredPreparation !== true));
});

test('wrong identity, wrong mode, missing or changed fingerprints install nothing', () => {
    for (const options of [{ pid: 12 }, { mode: 'trigger' }, { missingFingerprints: true }, { mismatch: true }]) {
        const run = fixture(options);
        assert.equal(run.hooks.size, 0);
        assert.match(run.messages.at(-1).reason, /error/);
    }
});

test('partial hook installation cleans up on failure', () => {
    const run = fixture({ attachFailureAt: 3 });
    assert.equal(run.detached(), 3);
    assert.match(run.messages.at(-1).reason, /attach failed/);
});

test('copy direction and nested calls keep separate enter/leave identities', () => {
    const run = fixture();
    const outer = run.enter(0x1042aa0);
    const inner = run.enter(0x11f9300, [0, 1, 0, 0], 8);
    run.leave(0x11f9300, inner);
    run.leave(0x1042aa0, outer);
    const entries = run.messages.filter(message => message.kind === 'lifecycle-enter');
    const exits = run.messages.filter(message => message.kind === 'lifecycle-leave');
    assert.equal(entries[1].copyIndices.source, 1);
    assert.equal(entries[1].copyIndices.destination, 0);
    assert.equal(entries[1].callId, exits[0].callId);
    assert.equal(entries[0].callId, exits[1].callId);
});

test('null task and null descriptor do not invent a hub or encounter', () => {
    for (const nullDescriptor of [false, true]) {
        const run = fixture({ nullDescriptor });
        const call = run.enter(0x1b6ea60, [run.base + 0x9334770, 0, 0, 0]);
        run.leave(0x1b6ea60, call, nullDescriptor ? 0x30000000 : 0);
        assert.equal(run.messages.at(-1).task.word, null);
    }
});

test('non-current task queries are ignored', () => {
    const run = fixture();
    for (const values of [[0, 0, 0, 0], [run.base + 0x9334770, 1, 0, 0]]) {
        run.leave(0x1b6ea60, run.enter(0x1b6ea60, values));
    }
    assert.equal(run.messages.filter(message => message.kind === 'lifecycle-task').length, 0);
});

test('repeated selectors are deduplicated, changed slot remains visible', () => {
    const run = fixture();
    for (let count = 0; count < 2000; count++) run.leave(0x105e930, run.enter(0x105e930), 1);
    run.leave(0x105e930, run.enter(0x105e930), 0);
    assert.equal(run.messages.filter(message => message.kind === 'lifecycle-selection').length, 2);
    assert.equal(run.detached(), 0);
});

test('read fault and event flood stop rather than silently dropping evidence', () => {
    const fault = fixture();
    fault.failReads();
    fault.intervals[0]();
    assert.match(fault.messages.at(-1).reason, /unreadable memory/);
    assert.equal(fault.detached(), 10);
    const flood = fixture();
    for (let count = 0; count < 1100; count++) flood.enter(0x1042aa0);
    assert.match(flood.messages.at(-1).reason, /event budget exceeded/);
    assert.equal(flood.detached(), 10);
});

test('stop is idempotent and late returns do not claim completion', () => {
    const run = fixture();
    const call = run.enter(0x1042aa0);
    run.context.rpc.exports.stop();
    run.context.rpc.exports.stop();
    run.leave(0x1042aa0, call);
    assert.equal(run.detached(), 10);
    assert.equal(run.messages.at(-1).kind, 'stopped');
    assert.equal(run.messages.at(-1).restoredPreparation, false);
});

test('content evidence covers all three native slots and shared allocations without claiming restoration', () => {
    const run = fixture({ content: true });
    const content = run.messages.find(message => message.kind === 'lifecycle-content');
    assert.equal(content.regions.length, 23);
    assert.equal(content.regions.find(region => region.name === 'gameplay[2]').size, 0x6790);
    assert.equal(content.regions.find(region => region.name === 'chapter').size, 0xd7d10);
    assert.equal(content.regions.find(region => region.name === 'run-checkpoint-observed-span').size, 0x6d8c);
    assert.equal(content.stable, true);
    assert.equal(content.changed, null);
    assert.equal(content.coherentSnapshot, false);
    assert.equal(content.restorable, false);
});

test('fixed pointers do not hide payload changes in any member', () => {
    const run = fixture({ content: true });
    const initial = run.messages.find(message => message.kind === 'lifecycle-content');
    for (const region of initial.regions) {
        run.memory.set(Number(region.address) + region.size - 1, 37);
        const call = run.enter(0x1049bf0);
        const content = run.messages.at(-1);
        assert.equal(content.kind, 'lifecycle-content');
        assert.deepEqual(Array.from(content.changed), [region.name]);
        assert.equal(content.callId, call.callId);
        assert.equal(content.sameAddresses, true);
    }
});

test('mutation between passes is unstable and cannot become the comparison baseline', () => {
    let mutate = true;
    const run = fixture({ content: true, afterRead({ address, length, memory }) {
        if (length === 0x6790 && mutate) {
            mutate = false;
            memory.set(address, 1);
        }
    } });
    const initial = run.messages.find(message => message.kind === 'lifecycle-content');
    assert.equal(initial.stable, false);
    assert.equal(initial.changed, null);
    run.enter(0x1049bf0);
    assert.equal(run.messages.at(-1).stable, true);
    assert.equal(run.messages.at(-1).changed, null);
});

test('allocation changes between passes reject a stable-content claim', () => {
    let moved = false;
    const run = fixture({ content: true, afterRead({ length, pointers }) {
        if (length === 0x6d8c && !moved) {
            moved = true;
            pointers.set(0x10000000 + 0x35f9f40, 0x61000000);
        }
    } });
    const content = run.messages.find(message => message.kind === 'lifecycle-content');
    assert.equal(content.sameAddresses, false);
    assert.equal(content.stable, false);
    assert.equal(content.changed, null);
});

test('null member and read fault stop content evidence without publishing a usable capture', () => {
    const missing = fixture({ content: true, nullDescriptor: true });
    assert.match(missing.messages.at(-1).reason, /Unallocated checkpoint member/);
    assert.equal(missing.messages.filter(message => message.kind === 'lifecycle-content').length, 0);
    const fault = fixture({ content: true });
    fault.failReads();
    fault.enter(0x1049bf0);
    assert.equal(fault.detached(), 10);
    assert.match(fault.messages.at(-1).reason, /unreadable memory/);
});
