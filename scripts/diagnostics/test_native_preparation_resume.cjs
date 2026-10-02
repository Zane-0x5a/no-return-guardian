const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function fixture(options = {}) {
    const base = 0x10000000;
    const owner = 0x20000000;
    const stateObject = 0x30000000;
    const task = 0x40000000;
    const descriptor = 0x50000000;
    const hooks = new Map();
    const messages = [];
    const calls = [];
    let detached = 0;
    const timeouts = [];
    class Pointer {
        constructor(value) { this.value = Number(value); }
        add(value) { return new Pointer(this.value + value); }
        equals(value) { return this.value === value.value; }
        isNull() { return this.value === 0; }
        toString() { return `0x${this.value.toString(16)}`; }
        toUInt32() { return this.value >>> 0; }
        readByteArray(length) { return new Uint8Array(length).buffer; }
        readPointer() {
            if (options.readFault) throw new Error('read failed');
            if (this.value === stateObject + 8) return new Pointer(options.changedOwner ? owner + 1 : owner);
            if (this.value === task + 0x120) return new Pointer(options.nullDescriptor ? 0 : descriptor);
            throw new Error('unexpected pointer read');
        }
        readU8() { return options.noRun ? 0 : 1; }
        readS32() {
            return this.value === descriptor + 0x6c ? (options.taskWord ?? 2) : (options.emptyRun ? 0 : 14);
        }
    }
    const rvas = [0xff6920, 0x133ebb0, 0x1b6ea60, 0xfe7b10, 0xfe96f0,
        0xfd7870, 0x1042680, 0x103f2c0, 0x1046290, 0x1bdf840];
    const context = {
        nativeCheckpointConfiguration: { pid: 11, base: `0x${base.toString(16)}`,
            mode: options.mode ?? 'resume-preparation', duration: 60,
            owner: `0x${owner.toString(16)}`, stateObject: `0x${stateObject.toString(16)}`,
            caller: `0x${(base + 0x133ec36).toString(16)}`, task: `0x${task.toString(16)}`,
            fingerprints: options.missing ? [] : rvas.map(rva => ({ rva, bytes: '00'.repeat(32) })) },
        Process: { arch: 'x64', id: options.pid ?? 11,
            mainModule: { name: 'tlou-ii.exe', base: new Pointer(base) } },
        ptr: value => new Pointer(value),
        NativeFunction: function(address) {
            const rva = address.value - base;
            assert.ok([0xfe96f0, 0xfe7b10, 0x1b6ea60].includes(rva));
            return (...args) => {
                calls.push({ rva, args });
                if (rva === 0xfe7b10) return new Pointer(options.wrongSingleton ? owner + 1 : owner);
                if (rva === 0x1b6ea60) return new Pointer(options.changedTask ? task + 1 : task);
                if (options.scheduleFault) throw new Error('scheduler failed');
            };
        },
        Interceptor: {
            attach(address, callbacks) {
                if (hooks.size === options.attachFailureAt) throw new Error('attach failed');
                hooks.set(address.value - base, callbacks);
                return { detach() { detached++; } };
            }, flush() {}
        },
        send: message => messages.push(message), rpc: { exports: {} },
        setTimeout: callback => timeouts.push(callback)
    };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'NativePreparationResume.js'), 'utf8'), context);
    function enter(rva, values = [0, 0, 0, 0], threadId = 7) {
        const invocation = { threadId,
            returnAddress: new Pointer(base + (options.wrongCaller ? 0 : 0x133ec36)) };
        hooks.get(rva).onEnter.call(invocation, values.map(value => new Pointer(value)));
        return invocation;
    }
    function idle(count = 30) {
        for (let index = 0; index < count; index++) {
            const invocation = enter(0xff6920, [stateObject]);
            hooks.get(0xff6920).onLeave.call(invocation);
        }
    }
    return { hooks, messages, calls, context, enter, idle, stateObject, owner, task,
        timeouts, detached: () => detached,
        schedules: () => calls.filter(call => call.rva === 0xfe96f0) };
}

test('schedules the game-owned callback once after thirty verified idle updates', () => {
    const run = fixture();
    run.idle(29);
    assert.equal(run.schedules().length, 0);
    run.idle(100);
    assert.equal(run.schedules().length, 1);
    const schedule = run.schedules()[0];
    assert.equal(schedule.args[0].value, run.owner);
    assert.equal(schedule.args[1].value, 0x10000000 + 0xfd7870);
    assert.equal(schedule.args[2], 1);
    assert.equal(run.messages.some(message => message.kind === 'resume-chain-observed'), false);
});

test('rejects wrong process, mode and incomplete fingerprints before hooking', () => {
    for (const options of [{ pid: 12 }, { mode: 'trigger' }, { missing: true }]) {
        const run = fixture(options);
        assert.equal(run.hooks.size, 0);
        assert.equal(run.schedules().length, 0);
        assert.match(run.messages.at(-1).reason, /error/);
    }
});

test('rejects stale task, encounter, changed owner, singleton, caller, empty run and read failure', () => {
    for (const options of [{ changedTask: true }, { taskWord: 1 }, { taskWord: 3 },
        { changedOwner: true }, { wrongSingleton: true }, { wrongCaller: true },
        { noRun: true }, { emptyRun: true }, { nullDescriptor: true }, { readFault: true }]) {
        const run = fixture(options);
        run.idle();
        assert.equal(run.schedules().length, 0, JSON.stringify(options));
        assert.match(run.messages.at(-1).reason, /error/);
        assert.equal(run.detached(), run.hooks.size);
    }
});

test('concurrent idle updates detach without scheduling', () => {
    const run = fixture();
    run.enter(0xff6920, [run.stateObject]);
    run.enter(0xff6920, [run.stateObject]);
    assert.equal(run.schedules().length, 0);
    assert.match(run.messages.at(-1).reason, /Concurrent/);
});

test('partial install and scheduling exceptions detach every installed hook', () => {
    for (const options of [{ attachFailureAt: 3 }, { scheduleFault: true }]) {
        const run = fixture(options);
        if (!options.attachFailureAt) run.idle();
        assert.match(run.messages.at(-1).reason, /error/);
        assert.equal(run.detached(), run.hooks.size);
    }
});

test('requires ordered same-thread checkpoint initialization and same-task type10 request', () => {
    const run = fixture();
    run.idle();
    run.enter(0xfd7870);
    run.enter(0x1042680);
    run.enter(0x103f2c0, [0, 0, 1]);
    run.enter(0x1046290);
    run.enter(0x1bdf840, [run.task, 0, 10, 1]);
    assert.equal(run.messages.filter(message => message.kind === 'resume-chain-observed').length, 1);
    assert.ok(run.messages.every(message => message.restoredPreparation === false));
    const result = run.messages.at(-1);
    assert.equal(result.inGameAcceptance, false);
    assert.equal(result.retainedPreparation, false);
});

test('spontaneous and out-of-order callbacks cannot satisfy the experiment', () => {
    for (const scenario of ['spontaneous', 'order']) {
        const run = fixture();
        if (scenario !== 'spontaneous') run.idle();
        run.enter(scenario === 'order' ? 0x1046290 : 0xfd7870);
        assert.match(run.messages.at(-1).reason, /error/);
        assert.equal(run.messages.some(message => message.kind === 'resume-chain-observed'), false);
    }
});

test('asynchronous callback can migrate workers but nested calls cannot change threads', () => {
    const run = fixture();
    run.idle();
    run.enter(0xfd7870, [0, 0, 0, 0], 8);
    assert.equal(run.messages.at(-1).kind, 'resume-native-enter');
    run.enter(0x1042680, [0, 0, 0, 0], 7);
    assert.match(run.messages.at(-1).reason, /thread/);
});

test('wrong initialization flag, request type and task fail rather than claiming acceptance', () => {
    for (const scenario of ['init', 'type', 'task', 'flag']) {
        const run = fixture();
        run.idle();
        run.enter(0xfd7870);
        run.enter(0x1042680);
        run.enter(0x103f2c0, [0, 0, scenario === 'init' ? 0 : 1]);
        if (scenario !== 'init') {
            run.enter(0x1046290);
            run.enter(0x1bdf840, [run.task + (scenario === 'task' ? 1 : 0), 0,
                scenario === 'type' ? 3 : 10, scenario === 'flag' ? 0 : 1]);
        }
        assert.match(run.messages.at(-1).reason, /error/);
        assert.equal(run.messages.some(message => message.kind === 'resume-chain-observed'), false);
    }
});

test('deadline and repeated host stop are idempotent and never claim restored preparation', () => {
    const run = fixture();
    run.timeouts[0]();
    run.context.rpc.exports.stop();
    run.context.rpc.exports.stop();
    assert.equal(run.detached(), run.hooks.size);
    assert.equal(run.messages.filter(message => message.kind === 'stopped').length, 1);
    assert.equal(run.messages.at(-1).restoredPreparation, false);
});
