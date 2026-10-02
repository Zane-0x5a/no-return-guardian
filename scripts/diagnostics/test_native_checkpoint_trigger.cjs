const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function fixture(options = {}) {
    const base = 0x10000000;
    const root = 0x20000000;
    const stateObject = 0x30000000;
    const hooks = new Map();
    const messages = [];
    const calls = [];
    const timers = [];
    let detached = 0;
    class Pointer {
        constructor(value) { this.value = Number(value); }
        add(value) { return new Pointer(this.value + value); }
        equals(value) { return this.value === value.value; }
        isNull() { return this.value === 0; }
        toString() { return `0x${this.value.toString(16)}`; }
        toUInt32() { return this.value >>> 0; }
        readByteArray(length) { return new Uint8Array(length).buffer; }
        readPointer() {
            if (this.value === stateObject + 8) return new Pointer(root);
            if (this.value === 0x50000000 + 0x120) return new Pointer(0x60000000);
            return new Pointer(0x1234);
        }
        readU8() { return options.rogue ?? 1; }
        readS32() {
            if (this.value === 0x60000000 + 0x6c) return options.taskWord ?? 1;
            return 0;
        }
    }
    const context = {
        nativeCheckpointConfiguration: { pid: 11, base: `0x${base.toString(16)}`,
            fingerprints: [], mode: options.action ?? 'trigger', duration: 15,
            owner: `0x${root.toString(16)}`, stateObject: `0x${stateObject.toString(16)}`,
            caller: `0x${(base + 0x133ec36).toString(16)}`, task: '0x50000000' },
        Process: { id: 11, arch: 'x64', mainModule: { name: 'tlou-ii.exe', base: new Pointer(base) } },
        ptr: value => new Pointer(value),
        NativeFunction: function(address, returnType, argumentTypes, nativeOptions) {
            assert.notEqual(nativeOptions.traps, 'all');
            return (...args) => {
                const rva = address.value - base;
                if (rva === 0x1b6ea60 && args.slice(1).some(value => !Number.isInteger(value))) {
                    throw new Error('expected an integer');
                }
                if (rva === 0x105f830 && args.some(value => !Number.isInteger(value))) {
                    throw new Error('expected an integer');
                }
                calls.push({ rva, args });
                if (rva === 0x1b6ea60) return new Pointer(options.noTask ? 0 : 0x50000000);
                if (rva === 0x105f830) return true;
                throw new Error('Unexpected native function');
            };
        },
        Interceptor: { attach(address, callbacks) {
            hooks.set(address.value - base, callbacks);
            return { detach() { detached++; } };
        }, flush() {} },
        send: message => messages.push(message),
        rpc: { exports: {} },
        setTimeout: callback => timers.push(callback),
        setInterval: callback => timers.push(callback),
    };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'NativeCheckpointTrigger.js'), 'utf8'), context);
    function tick(threadId = 33) {
        const invocation = { threadId,
            returnAddress: new Pointer(options.wrongCaller ? 0x40000000 : base + 0x133ec36) };
        const hook = hooks.get(0xff6920);
        hook.onEnter.call(invocation, [new Pointer(stateObject)]);
        hook.onLeave.call(invocation);
    }
    return { tick, calls, messages, context, timers, detached: () => detached };
}

test('observe only queries current task and never restarts', () => {
    const run = fixture({ action: 'observe' });
    for (let count = 0; count < 100; count++) run.tick();
    assert.ok(run.calls.every(call => call.rva === 0x1b6ea60));
});

test('trigger calls the ordinary checkpoint exactly once after stable ticks', () => {
    const run = fixture();
    for (let count = 0; count < 29; count++) run.tick();
    assert.equal(run.calls.filter(call => call.rva === 0x105f830).length, 0);
    for (let count = 0; count < 100; count++) run.tick();
    const restarts = run.calls.filter(call => call.rva === 0x105f830);
    assert.equal(restarts.length, 1);
    assert.deepEqual(restarts[0].args, [0, 0]);
    assert.equal(run.messages.find(message => message.kind === 'invoke-returned').restoredPreparation, false);
});

for (const options of [{ taskWord: 2 }, { taskWord: 3 }, { rogue: 0 }, { noTask: true }]) {
    test(`rejects incorrect native scene ${JSON.stringify(options)}`, () => {
        const run = fixture(options);
        for (let count = 0; count < 100; count++) run.tick();
        assert.equal(run.calls.filter(call => call.rva === 0x105f830).length, 0);
        assert.equal(run.detached(), 3);
    });
}

test('unrecognized caller cannot consume the request', () => {
    const run = fixture({ wrongCaller: true });
    for (let count = 0; count < 100; count++) run.tick();
    assert.equal(run.calls.length, 0);
    assert.equal(run.detached(), 3);
});

test('native worker thread migration preserves a single invocation', () => {
    const run = fixture();
    for (let count = 0; count < 100; count++) run.tick(33 + count % 4);
    assert.equal(run.calls.filter(call => call.rva === 0x105f830).length, 1);
});

test('deadline detaches and prevents any later invocation', () => {
    const run = fixture();
    run.timers[0]();
    for (let count = 0; count < 100; count++) run.tick();
    assert.equal(run.calls.length, 0);
    assert.equal(run.detached(), 3);
    run.context.rpc.exports.stop();
    assert.equal(run.detached(), 3);
});
