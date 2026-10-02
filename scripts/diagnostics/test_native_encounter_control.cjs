const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, 'NativeEncounterControl.js'), 'utf8');
const base = 0x140000000;
const required = [0x133ec5e, 0x1b843a0, 0x1b83f70, 0x1b6e000, 0xdc1780, 0x1b6ea60, 0xfe7b10, 0x1200ac0];
const nextTask = 0xa9456e5567d0ce70n;
const abandoned = 0xdd669d5bfffeaf6cn;
const owner = 0x7000;
const hubTask = 0x8000;
const encounterTask = 0x9000;
const slot = '05000000000000000000000002000000';
// A real 2026-10-01 route-board departure: int32 route index 3, then the photo's boolean.
const board = '0300000000000000820000000200000000000000000000002400000001000000';
const none = '00'.repeat(16);

function value(number) {
    return {
        number: BigInt(number),
        equals(other) { return this.number === BigInt(other.number ?? other); },
        toString(radix) { return this.number.toString(radix); }
    };
}

function memoryFixture() {
    const bytes = new Map();
    function pointer(address) {
        const at = Number(address);
        const read = (size, signed) => {
            let result = 0n;
            for (let index = size - 1; index >= 0; index--) result = (result << 8n) | BigInt(bytes.get(at + index) ?? 0);
            return signed ? BigInt.asIntN(size * 8, result) : result;
        };
        return {
            address: at,
            add(offset) { return pointer(at + Number(offset)); },
            sub(other) { return pointer(at - (other.address ?? Number(other))); },
            equals(other) { return at === (other.address ?? Number(other)); },
            isNull() { return at === 0; },
            readU8() { return Number(read(1)); },
            readS16() { return Number(read(2, true)); },
            readS32() { return Number(read(4, true)); },
            readU64() { return value(read(8)); },
            readPointer() { return pointer(read(8)); },
            readByteArray(length) {
                return Uint8Array.from({ length }, (_, index) => bytes.get(at + index) ?? 0).buffer;
            },
            writeByteArray(values) { Array.from(values).forEach((byte, index) => bytes.set(at + index, byte)); },
            toString(radix) { return radix === 16 ? at.toString(16) : `0x${at.toString(16)}`; }
        };
    }
    function put(address, number, size) {
        let remaining = BigInt.asUintN(size * 8, BigInt(number));
        for (let index = 0; index < size; index++) { bytes.set(address + index, Number(remaining & 0xffn)); remaining >>= 8n; }
    }
    return { pointer, put, bytes };
}

function fixture(mode, extra = {}) {
    const memory = memoryFixture();
    const messages = [];
    const hooks = new Map();
    const detached = [];
    const broadcasts = [];
    let allocations = 0x100000;
    let current = hubTask;
    const game = {
        state(fields) {
            const run = base + 0x415ac90;
            const manager = base + 0x9341660;
            const next = { runActive: 1, runInitialized: 1, story: 2, slot: 20, workers: [0, 0, 0], ...fields };
            memory.put(run + 0x80, next.runActive, 1);
            memory.put(run + 0x72c8, next.runInitialized, 1);
            memory.put(base + 0x9398730, next.story, 4);
            memory.put(manager + 0x5e0, next.slot, 4);
            [0x58, 0x228, 0x3f8].forEach((offset, index) => memory.put(manager + offset, next.workers[index], 4));
        },
        task(address) { current = address; }
    };
    for (const [task, word] of [[hubTask, 2], [encounterTask, 1]]) {
        memory.put(task + 0x120, task + 0x200, 8);
        memory.put(task + 0x200 + 0x6c, word, 4);
        memory.put(task + 0x90, task === hubTask ? 0x1111 : 0x2222, 8);
    }
    game.state({});
    const natives = {
        [base + 0xfe7b10]: () => memory.pointer(owner),
        [base + 0x1b6ea60]: () => memory.pointer(current),
        [base + 0x1b843a0]: (manager, sid, ...slots) => broadcasts.push({
            manager: manager.address, sid: BigInt(sid),
            slots: slots.map(item => Buffer.from(item.readByteArray(16)).toString('hex')) })
    };
    const context = {
        nativeEncounterConfiguration: { mode, pid: 42, base: `0x${base.toString(16)}`, duration: 60,
            fingerprints: required.map(rva => ({ rva, bytes: '00'.repeat(32) })), settleUpdates: 3,
            retryMs: 1000, maximumBroadcasts: 2, slots: slot, ...extra },
        Process: { id: 42, arch: 'x64', mainModule: { name: 'tlou-ii.exe', base: memory.pointer(base), size: 0x40000000 } },
        Interceptor: {
            attach(address, callbacks) {
                hooks.set(address.address - base, callbacks);
                return { detach() { detached.push(address.address - base); } };
            },
            flush() {}
        },
        Memory: { alloc(size) { const at = allocations; allocations += Math.max(size, 16); return memory.pointer(at); } },
        NativeFunction: function(address) { return natives[address.address]; },
        rpc: { exports: {} },
        ptr: value => memory.pointer(Number(BigInt(value))),
        uint64: text => value(BigInt(text)),
        send(message) { messages.push(message); },
        setTimeout() {},
        Date: { now: () => clock.now }
    };
    const clock = { now: 1000 };
    vm.runInNewContext(source, context);
    function update(rbx = owner) {
        const callback = hooks.get(0x133ec5e);
        callback.call({ context: { rbx: memory.pointer(rbx) } });
    }
    return { memory, messages, hooks, detached, broadcasts, game, clock, update,
        kinds: () => messages.map(message => message.kind) };
}

test('record captures only the departure event slots and detaches itself', () => {
    const probe = fixture('record');
    const event = 0x50000;
    const stack = 0x58000;
    probe.memory.put(event, 0x1234n, 8);
    probe.memory.put(stack, base + 0x164d128, 8);
    const broadcast = probe.hooks.get(0x1b83f70);
    assert.equal(typeof broadcast, 'function', 'entry-only hooks must be function probes');
    const call = { threadId: 5, context: { rsp: probe.memory.pointer(stack) } };
    broadcast.call(call, [null, null, probe.memory.pointer(event)]);
    assert.equal(probe.messages.filter(message => message.kind === 'departure-captured').length, 0);
    probe.memory.put(event, nextTask, 8);
    probe.memory.put(event + 0x128, 2, 2);
    probe.memory.pointer(event + 0x28).writeByteArray(Buffer.from(board, 'hex'));
    probe.game.state({ workers: [0, 3, 0] });
    broadcast.call(call, [null, null, probe.memory.pointer(event)]);
    const captured = probe.messages.find(message => message.kind === 'departure-captured');
    assert.equal(captured.slots, board);
    assert.equal(captured.count, 2);
    assert.equal(captured.caller, '164d128');
    // The host binds the hideout save from before the event, so it needs the save workers at that instant.
    assert.deepEqual(Array.from(captured.workers), [0, 3, 0]);
    assert.equal(captured.runActive, 1);
    assert.deepEqual(probe.detached, [0x1b83f70]);
    assert.equal(probe.messages.at(-1).reason, 'captured');
    assert.equal(probe.messages.at(-1).seen, 2);
});

test('redeploy waits for a settled hideout, replays the recorded slot once and confirms arrival', () => {
    const probe = fixture('redeploy');
    probe.update(0x1);
    probe.game.state({ workers: [0, 4, 0] });
    probe.update();
    probe.update();
    probe.update();
    assert.equal(probe.broadcasts.length, 0);
    probe.game.state({});
    for (let index = 0; index < 3; index++) probe.update();
    assert.equal(probe.broadcasts.length, 1);
    assert.deepEqual(probe.broadcasts[0], { manager: base + 0x9342080, sid: nextTask, slots: [slot, none, none, none] });
    probe.hooks.get(0x1200ac0).call({});
    probe.hooks.get(0x1b6e000).call({ threadId: 9 }, [null, probe.memory.pointer(0x60000)]);
    for (const rva of [0x133ec5e, 0x1b6e000, 0x1200ac0]) assert.equal(typeof probe.hooks.get(rva), 'function');
    probe.hooks.get(0xdc1780).onLeave.call({});
    probe.game.task(encounterTask);
    probe.clock.now += 5000;
    for (let index = 0; index < 29; index++) probe.update();
    assert.ok(!probe.kinds().includes('redeployed'));
    probe.update();
    const arrived = probe.messages.find(message => message.kind === 'redeployed');
    assert.equal(arrived.task.subnode, '2222');
    assert.equal(probe.broadcasts.length, 1);
    assert.equal(probe.messages.at(-1).reason, 'redeployed');
});

test('a lost departure is retried once only while nothing of the departure started', () => {
    const probe = fixture('redeploy');
    for (let index = 0; index < 3; index++) probe.update();
    probe.clock.now += 1500;
    for (let index = 0; index < 3; index++) probe.update();
    assert.equal(probe.broadcasts.length, 2);
    probe.clock.now += 1500;
    for (let index = 0; index < 3; index++) probe.update();
    assert.equal(probe.broadcasts.length, 2);
    assert.ok(probe.kinds().includes('redeploy-not-observed'));

    const started = fixture('redeploy');
    for (let index = 0; index < 3; index++) started.update();
    started.hooks.get(0x1200ac0).call({});
    started.clock.now += 5000;
    for (let index = 0; index < 10; index++) started.update();
    assert.equal(started.broadcasts.length, 1);
});

test('abandon needs a stable active encounter and waits for the run to become inactive', () => {
    const probe = fixture('abandon');
    for (let index = 0; index < 40; index++) probe.update();
    assert.equal(probe.broadcasts.length, 0);
    probe.game.task(encounterTask);
    for (let index = 0; index < 30; index++) probe.update();
    assert.equal(probe.broadcasts.length, 1);
    assert.equal(probe.broadcasts[0].sid, abandoned);
    assert.deepEqual(probe.broadcasts[0].slots, [none, none, none, none]);
    probe.update();
    assert.ok(!probe.kinds().includes('abandon-observed'));
    probe.game.state({ runActive: 0 });
    probe.update();
    assert.ok(probe.kinds().includes('abandon-observed'));
    assert.equal(probe.broadcasts.length, 1);
});

test('the board departure replays both recorded slots in order', () => {
    const probe = fixture('redeploy', { slots: board });
    for (let index = 0; index < 3; index++) probe.update();
    assert.deepEqual(probe.broadcasts, [{ manager: base + 0x9342080, sid: nextTask,
        slots: [board.slice(0, 32), board.slice(32), none, none] }]);
});

test('invalid departure slots and missing fingerprints install nothing', () => {
    for (const slots of ['05000000000000000000000001000000', slot + slot, board + slot, board.slice(0, 48)]) {
        const wrong = fixture('redeploy', { slots });
        assert.equal(wrong.hooks.size, 0);
        assert.match(wrong.messages.at(-1).reason, /int32/);
    }
    const missing = fixture('record', { fingerprints: [] });
    assert.equal(missing.hooks.size, 0);
    assert.match(missing.messages.at(-1).reason, /fingerprint/i);
});
