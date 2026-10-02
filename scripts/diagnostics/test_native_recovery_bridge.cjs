const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { requires } = require('./local_evidence.cjs');
const vm = require('node:vm');

const capacity = 0x460414;
const rawSize = capacity + 0x590;
const metadata = '00000000ffffffffffffffff0000000000000000ff000000000000000000000000000100ffffffff';
const rvas = [0x133ebb0, 0x133ec5e, 0xfe7b10, 0xfe96f0, 0x1b7fc60, 0x1e2f880, 0x120af40,
    0x120d090, 0xfd7870, 0x1042680, 0x103f2c0, 0x1b6e000, 0x1bdeed0, 0xdc1780, 0x1b6ea60,
    0x1c55df0, 0x1c55a3c, 0x1c57bb0];
const runStatSids = ['ca79be3ddf469b1a', '8dd8bbea1823e133', 'e0a765d96d4dd778', '34ae6a71b265a278',
    '524ef3c2f60dc853', '6172419d00e62123', '966ac5e93688c099', '966ac2e93688bb80',
    'c34a84af6ff26f6f', 'db3441ab83811bde'];
// The 2026-10-01 leak: protected e221c7c9 against the live values after two replays (4378b969).
const protectedStats = [1, 0, 0, 0, 0, 1, 0, 0, 3, 1];
const leakedStats = [1, 1, 1, 0, 0, 1, 0, 0, 3, 1];
const digest = data => crypto.createHash('sha256').update(Buffer.from(data)).digest('hex');

function fixture(options = {}) {
    const base = 0x10000000;
    const owner = 0x30000000;
    const temporary = 0x40000000;
    const cache = 0x50000000;
    const inner = 0x60000000;
    const descriptor = 0x70000000;
    const descriptorRef = 0x71000000;
    const applyDescriptor = 0x72000000;
    const command = 0x73000000;
    const task = 0x74000000;
    const taskDescriptor = 0x75000000;
    const story = 0x76000000;
    const statService = 0x77000000;
    const statVtable = base + 0x2af57d8;
    const statEntries = 0x78000000;
    const manager = base + 0x9341660;
    const worker = manager + 0x3c0;
    const hooks = new Map();
    const memory = new Map();
    const regions = [];
    const messages = [];
    const statWrites = [];
    let allocation = 0x80000000;
    let terminated = false;
    let schedules = 0;
    let detached = 0;
    const fields = [0x30];
    const packet = new ArrayBuffer(rawSize + fields.length * 4);
    const packetView = new DataView(packet);
    packetView.setUint32(0, 0xea93, true);
    packetView.setUint32(0x594, 0x458, true);
    packetView.setUint32(0x598, 27, true);
    packetView.setUint32(rawSize, 0x30, true);
    packetView.setUint32(0x590 + 0x418 + 0x38, 0x40, true);
    function buffer(address, size) {
        const region = regions.find(region => address >= region.address && address + size <= region.address + region.data.length);
        return region ? region.data.subarray(address - region.address, address - region.address + size) : null;
    }
    function allocate(address, length) { regions.push({ address, data: Buffer.alloc(length) }); }
    class Pointer {
        constructor(value) { this.value = value instanceof Pointer ? value.value : Number(value); }
        add(offset) { return new Pointer(this.value + offset); }
        equals(other) { return this.value === other?.value; }
        isNull() { return this.value === 0; }
        toString() { return this.value < 0 ? '-1' : `0x${this.value.toString(16)}`; }
        toUInt32() { return this.value >>> 0; }
        toInt32() { return this.value | 0; }
        readU8() { return memory.get(this.value) ?? buffer(this.value, 1)?.readUInt8() ?? 0; }
        readU32() { return memory.get(this.value) ?? buffer(this.value, 4)?.readUInt32LE() ?? 0; }
        readS32() { return this.readU32() | 0; }
        readPointer() { return new Pointer(memory.get(this.value) ?? Number(buffer(this.value, 8)?.readBigUInt64LE() ?? 0n)); }
        writeU32(value) { const data = buffer(this.value, 4); if (data) data.writeUInt32LE(value); else memory.set(this.value, value); }
        writePointer(value) { const data = buffer(this.value, 8); if (data) data.writeBigUInt64LE(BigInt(value.value)); else memory.set(this.value, value.value); }
        readByteArray(length) {
            if (options.readFault && this.value === inner + 0x80) throw new Error('read failed');
            const data = buffer(this.value, length) ?? Buffer.alloc(length);
            return Uint8Array.from(data).buffer;
        }
        writeByteArray(bytes) { Buffer.from(bytes).copy(buffer(this.value, bytes.byteLength)); }
    }
    function setState(initialized = 0, selector = 2, rogue = 0, busy = 0) {
        memory.set(base + 0x415ac90 + 0x72c8, initialized);
        memory.set(base + 0x415ac90 + 0x520, selector);
        memory.set(base + 0x4161f58, rogue);
        for (const offset of [0x58, 0x228, 0x3f8]) memory.set(manager + offset, offset === 0x3f8 ? busy : 0);
    }
    setState();
    for (const [address, value] of [[worker + 8, inner], [worker + 0x48, 2], [worker + 0x4c, 20],
        [inner + 4, 2], [inner + 0xc, 2], [inner + 0x14, 20], [inner + 0xd8, options.ioError ? 1 : 0],
        [worker + 0x10, descriptor], [descriptor + 4, options.badCapacity ? capacity - 8 : capacity],
        [descriptor + 8, cache], [manager + 0x5b0, temporary], [descriptorRef, applyDescriptor],
        [manager + 0x5e0, options.menuSlot ?? 20], [base + 0x9398730, options.menuStory ?? 2],
        [story, options.requestStory ?? 2],
        [base + 0x9398790, 0],
        [applyDescriptor + 8, temporary], [applyDescriptor + 4, capacity], [command, 1],
        [command + 0x18, 10], [task + 0x120, taskDescriptor], [taskDescriptor + 0x6c, 2]]) memory.set(address, value);
    memory.set(base + 0x4248b78, options.missingStatService ? 0 : statService);
    memory.set(statService, options.badStatVtable ? statVtable + 8 : statVtable);
    memory.set(statVtable + 0x20, base + 0x1c57bb0);
    memory.set(statService + 0x30, statEntries);
    const liveStats = options.liveStats ?? leakedStats;
    runStatSids.forEach((sid, index) => memory.set(statEntries + index * 24 + 8, liveStats[index]));
    const statValue = sid => memory.get(statEntries + runStatSids.indexOf(sid) * 24 + 8);
    allocate(inner + 0x80, 40);
    Buffer.from(metadata, 'hex').copy(buffer(inner + 0x80, 40));
    if (options.badMetadata) buffer(inner + 0x80, 40)[4] ^= 1;
    allocate(temporary, capacity);
    allocate(cache, capacity);
    const configuration = { pid: 11, base: `0x${base.toString(16)}`, mode: 'recover-preparation',
        recoverySource: { capacity, rawSize, declaredSize: 0x458, pointerCount: fields.length,
            packetSha256: digest(packet), bodySha256: digest(packet.slice(0x590, rawSize)),
            metadataHex: metadata, snapshotId: 'target',
            runStats: options.runStats === undefined ?
                runStatSids.map((sid, index) => ({ sid, value: protectedStats[index] })) : options.runStats },
        fingerprints: options.missingCode ? [] : rvas.map(rva => ({ rva, bytes: '00'.repeat(32) })) };
    const context = { Process: { id: options.wrongPid ? 12 : 11, arch: 'x64',
        mainModule: { name: 'tlou-ii.exe', base: new Pointer(base) } },
        ptr: value => new Pointer(value), uint64: value => BigInt(value),
        Uint8Array, Uint32Array, ArrayBuffer, DataView, BigInt,
        Module: { getGlobalExportByName: () => new Pointer(1) },
        NativeFunction: function(address) {
            if (address.value === 1) return () => { terminated = true; throw new Error('mock process exited'); };
            if (address.value === base + 0xfe7b10) return () => new Pointer(owner);
            if (address.value === base + 0x1c55df0) return (service, key) => {
                assert.equal(service.value, statService);
                const sid = key.toString(16).padStart(16, '0');
                return sid === options.unregisteredStat ? -1 : runStatSids.indexOf(sid);
            };
            if (address.value === base + 0x1c57bb0) return (service, value, key, quiet) => {
                assert.equal(service.value, statService);
                assert.equal(quiet, 0);
                const sid = key.toString(16).padStart(16, '0');
                statWrites.push({ sid, value });
                if (sid === options.rejectedStat && value === protectedStats[runStatSids.indexOf(sid)]) return 0;
                memory.set(statEntries + runStatSids.indexOf(sid) * 24 + 8, value);
                if (options.statSave) setState(0, 2, 0, 4);
                return 1;
            };
            if (address.value === base + 0x1b6ea60) return (manager, first, second) => {
                assert.equal(first, 0);
                assert.equal(second, 0);
                return new Pointer(task);
            };
            if (address.value === base + 0xfe96f0) return (owner, callback, checkpoint) => {
                assert.equal(checkpoint, 1);
                schedules++;
            };
            throw new Error('Unexpected native function');
        },
        Memory: { alloc(size) { const address = allocation; allocation += (size + 15) & ~15; allocate(address, size); return new Pointer(address); } },
        Interceptor: { attach(address, callbacks) {
            if (hooks.size === options.attachFailureAt) throw new Error('attach failed');
            if ([0x133ec5e, 0x1bdeed0, 0x1b6e000, 0x1042680].includes(address.value - base)) {
                assert.equal(typeof callbacks, 'function', 'Yielding boundaries must use probes without return interception');
            }
            hooks.set(address.value - base, typeof callbacks === 'function' ? { onEnter: callbacks } : callbacks);
            return { detach() { detached++; } };
        }, flush() {} },
        Checksum: { compute: (algorithm, data) => digest(data) }, send: message => messages.push(message) };
    for (const name of ['NativeRecoveryContract.js', 'NativeRecoveryBridge.js']) {
        vm.runInNewContext(fs.readFileSync(path.join(__dirname, name), 'utf8'), context);
    }
    const bridge = context.createNativeRecoveryBridge(configuration);
    if (options.corruptPacket) new Uint8Array(packet)[0] ^= 1;
    bridge.install(packet);
    function enter(rva, values = [], threadId = 10, caller = 0) {
        const invocation = { threadId, returnAddress: new Pointer(base + caller), context: { rbx: new Pointer(owner) } };
        const args = [...values, 0, 0, 0, 0].map(value => new Pointer(value));
        try { hooks.get(rva).onEnter.call(invocation, args); }
        catch (error) { if (!terminated) throw error; }
        return { invocation, args, rva };
    }
    function leave(call, result = 1) {
        if (terminated) return;
        try { hooks.get(call.rva).onLeave?.call(call.invocation, new Pointer(result)); }
        catch (error) { if (!terminated) throw error; }
    }
    function update(count = 30) { for (let index = 0; index < count && !terminated; index++) enter(0x133ec5e); }
    function copy(destination, source, direction, threadId, caller) {
        const call = enter(0x120d090, [0, destination, source, capacity, direction, -1], threadId, caller);
        if (terminated) return;
        const sourceBytes = buffer(source, capacity);
        if (direction === 0) {
            for (const field of fields) sourceBytes.writeBigUInt64LE(sourceBytes.readBigUInt64LE(0x418 + field) + BigInt(destination - source), 0x418 + field);
        }
        sourceBytes.copy(buffer(destination, capacity));
        if (direction === 2) for (const field of fields) {
            buffer(destination, capacity).writeBigUInt64LE(BigInt(destination + 0x418), 0x418 + field);
        }
        if (options.corruptLoad && direction === 2) buffer(destination, capacity)[0x14] ^= 1;
        if (options.corruptPromotion && direction === 0) buffer(destination, capacity)[0x14] ^= 1;
        leave(call);
    }
    function rebuild() {
        const selector = options.selector ?? 1;
        update(60);
        leave(enter(0x1b7fc60, [manager, 2, story, 20]));
        if (terminated) return;
        setState(0, 2, 0, 4);
        const load = enter(0x1e2f880, [inner, temporary, capacity], 20, options.wrongCaller ? 0 : 0x1b82059);
        if (terminated) return;
        assert.notEqual(load.args[0].value, inner);
        const source = load.args[0].add(0x40).readPointer().value + 0x590;
        copy(temporary, source, 2, 20, 0x1be1342);
        leave(load);
        if (terminated) return;
        const apply = enter(0x120af40, [0, descriptorRef, 0, 0], 30);
        setState(0, options.applySelector ?? selector, 0, 4);
        leave(apply, options.applyFailure ? 0 : 1);
        if (terminated) return;
        copy(cache, temporary, 0, 40, 0x1be144e);
        if (terminated) return;
        setState(0, options.callbackSelector ?? selector);
        const callback = enter(0xfd7870, [], 50);
        leave(enter(0x1042680, [], 50));
        const initialize = enter(0x103f2c0, [0, 0, options.checkpoint ?? 1], 50, options.initializeCaller ?? 0xfd7909);
        setState(1, selector, 1);
        for (const saveOptions of options.initializeSaves ?? []) {
            memory.set(story, saveOptions.story ?? 2);
            const values = [saveOptions.manager ?? manager, saveOptions.mode ?? 1, story, saveOptions.slot ?? 20];
            const save = enter(0x1b7fc60, values, saveOptions.threadId ?? 50, saveOptions.caller ?? 0x1b802c3);
            if (terminated) return;
            assert.deepEqual(save.args.slice(0, 4).map(argument => argument.value), values);
            setState(1, selector, 1, options.saveBusy ?? 0);
            if (saveOptions.profileBusy) memory.set(manager + 0x228, saveOptions.profileBusy);
            leave(save, saveOptions.result ?? 1);
            if (terminated) return;
        }
        if (options.unobservedBusy) setState(1, selector, 1, 4);
        leave(initialize);
        leave(enter(0x1b6e000, [0, command], 50));
        leave(callback);
        leave(enter(0x1bdeed0, [task, 0, 10], 60));
        if (!options.missingSpawn) leave(enter(0xdc1780, [], 70));
        update();
    }
    return { bridge, hooks, messages, enter, leave, update, rebuild, setState, statWrites, statValue,
        terminated: () => terminated, schedules: () => schedules, detached: () => detached };
}

test('leaked profile run stats return to the protected values on the idle menu before the load is scheduled', () => {
    const run = fixture();
    run.update(29);
    assert.equal(run.statWrites.length, 0);
    run.update(1);
    assert.deepEqual(run.statWrites, [{ sid: runStatSids[1], value: 0 }, { sid: runStatSids[2], value: 0 }]);
    assert.deepEqual(runStatSids.map(run.statValue), protectedStats);
    const restored = run.messages.filter(message => message.kind === 'run-stats-restored');
    assert.equal(restored.length, 1);
    assert.equal(restored[0].changed, 2);
    assert.deepEqual(restored[0].stats.map(stat => [stat.sid, stat.before, stat.after]),
        runStatSids.map((sid, index) => [sid, leakedStats[index], protectedStats[index]]));
    assert.equal(run.schedules(), 0, 'the idle-menu window restarts after the writes');
    run.update(29);
    assert.equal(run.schedules(), 0);
    run.update(1);
    assert.equal(run.schedules(), 1);
    assert.equal(run.statWrites.length, 2, 'stats are restored once');
});

test('matching profile run stats are left untouched and the rebuild reports that they held', () => {
    const run = fixture({ liveStats: protectedStats });
    run.rebuild();
    assert.equal(run.statWrites.length, 0);
    assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
    assert.equal(run.messages.find(message => message.kind === 'run-stats-restored').changed, 0);
    assert.equal(run.messages.find(message => message.transition === 'rebuilt').observed.runStatsHeld, true);
});

test('a profile save requested by a stat write delays scheduling until the workers are idle again', () => {
    const run = fixture({ statSave: true });
    run.update(30);
    run.update(60);
    assert.equal(run.schedules(), 0);
    run.setState();
    run.update(30);
    assert.equal(run.schedules(), 1);
});

test('a rejected stat write reverts the earlier writes, is reported, and still rebuilds the hideout', () => {
    const run = fixture({ rejectedStat: runStatSids[2] });
    run.update(30);
    assert.deepEqual(runStatSids.map(run.statValue), leakedStats);
    assert.deepEqual(run.statWrites.map(write => [write.sid, write.value]),
        [[runStatSids[1], 0], [runStatSids[2], 0], [runStatSids[2], 1], [runStatSids[1], 1]]);
    const unrestored = run.messages.find(message => message.kind === 'run-stats-unrestored');
    assert.equal(unrestored.reason, 'Native profile stat write was rejected');
    assert.equal(unrestored.attempted, 2);
    assert.equal(unrestored.reverted, true);
    assert.equal(run.messages.some(message => message.kind === 'run-stats-restored'), false);
    run.rebuild();
    assert.equal(run.terminated(), false);
    assert.equal(run.schedules(), 1);
    assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
    assert.equal(run.messages.find(message => message.transition === 'rebuilt').observed.runStatsHeld, null);
});

test('an unregistered stat or a foreign stat service writes nothing but leaves the recovery available', () => {
    for (const [options, reason] of [[{ unregisteredStat: runStatSids[9] }, 'Profile run stat is not registered'],
        [{ badStatVtable: true }, 'Profile stat service identity mismatch'],
        [{ missingStatService: true }, 'Profile stat service identity mismatch']]) {
        const run = fixture(options);
        run.rebuild();
        assert.equal(run.statWrites.length, 0, JSON.stringify(options));
        assert.equal(run.messages.find(message => message.kind === 'run-stats-unrestored').reason, reason);
        assert.equal(run.messages.find(message => message.kind === 'run-stats-unrestored').reverted, true);
        assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
    }
});

test('incomplete or out-of-range run stat targets stop installation before any hook', () => {
    for (const runStats of [undefined, [], runStatSids.slice(1).map(sid => ({ sid, value: 0 })),
        runStatSids.map((sid, index) => ({ sid: index === 3 ? 'ee639cad45b1994c' : sid, value: 0 })),
        runStatSids.map(sid => ({ sid, value: -1 })), runStatSids.map(sid => ({ sid, value: 0x80000000 })),
        runStatSids.map(sid => ({ sid, value: 0.5 }))]) {
        const run = fixture({ runStats: runStats ?? null });
        assert.equal(run.hooks.size, 0, JSON.stringify(runStats));
        assert.equal(run.statWrites.length, 0);
        assert.equal(run.schedules(), 0);
        assert.equal(run.terminated(), false);
        assert.equal(run.messages.at(-1).kind, 'stopped');
    }
});

test('bridge follows native ownership, preserves independent input and verifies cache before callback', () => {
    const run = fixture();
    run.rebuild();
    assert.equal(run.terminated(), false);
    assert.equal(run.schedules(), 1);
    assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
    assert.equal(run.detached(), run.hooks.size);
    assert.equal(run.messages.filter(message => message.kind === 'native-recovery-rebuilt').length, 1);
    assert.ok(run.messages.every(message => message.inGameAcceptance === false));
    run.bridge.stop();
    assert.equal(run.detached(), run.hooks.size);
});

test('actual selector-zero failure reaches rebuilt and detaches without changing the selected branch', requires('native-runtime-20260912/native-recovery-user-2154-repro.fatal.json'), () => {
    const fatal = JSON.parse(fs.readFileSync(path.join(__dirname,
        '../../artifacts/native-runtime-20260912/native-recovery-user-2154-repro.fatal.json'), 'utf8'));
    const selector = fatal.pendingTransition.fields.state.runSelector;
    const run = fixture({ selector, initializeSaves: [{}, { slot: 21, story: 0, caller: 0x1b8152f }] });
    run.rebuild();
    assert.equal(run.terminated(), false, run.messages.find(message => message.kind === 'recovery-fatal')?.reason);
    assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
    assert.equal(run.bridge.snapshot().nativeSelector, selector);
    assert.equal(run.detached(), run.hooks.size);
    assert.equal(run.messages.filter(message => message.kind === 'native-recovery-rebuilt').length, 1);
});

test('selector-zero rebuild still waits for idle workers and a new spawn', () => {
    for (const selector of [0, 1]) {
        const run = fixture({ selector, initializeSaves: [{}], saveBusy: 4 });
        run.rebuild();
        assert.equal(run.terminated(), false);
        assert.equal(run.bridge.snapshot().phase, 'playing');
        run.setState(1, selector, 1);
        run.update();
        assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
        const missing = fixture({ selector, missingSpawn: true });
        missing.rebuild();
        assert.equal(missing.terminated(), false);
        assert.equal(missing.bridge.snapshot().phase, 'playing');
        assert.equal(missing.messages.some(message => message.kind === 'native-recovery-rebuilt'), false);
    }
});

test('invalid apply selection and valid-selector drift cannot bypass callback checks', () => {
    for (const options of [{ selector: 0, callbackSelector: 1 }, { selector: 1, callbackSelector: 0 },
        { applySelector: 2 }, { applySelector: -1 }, { applySelector: 3 }, { selector: 0, applyFailure: true }]) {
        const run = fixture(options);
        run.rebuild();
        assert.equal(run.terminated(), true, JSON.stringify(options));
        assert.equal(run.messages.some(message => message.kind === 'native-recovery-rebuilt'), false);
    }
});

test('recorded 20:44 initialization save does not become a second recovery load', requires('native-runtime-20260912/native-recovery-user-2035-repro.fatal.json'), () => {
    const fatal = JSON.parse(fs.readFileSync(path.join(__dirname,
        '../../artifacts/native-runtime-20260912/native-recovery-user-2035-repro.fatal.json'), 'utf8'));
    assert.equal(fatal.pendingTransition.name, 'request');
    assert.equal(fatal.boundary.phase, 'torn-down');
    const fields = fatal.pendingTransition.fields;
    const caller = Number(BigInt(fatal.boundary.caller) - BigInt(fatal.base));
    const run = fixture({ initializeSaves: [{ mode: fields.mode, slot: fields.slot, caller }] });
    run.rebuild();
    assert.equal(run.terminated(), false, run.messages.find(message => message.kind === 'recovery-fatal')?.reason);
    assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
    assert.equal(run.messages.filter(message => message.transition === 'source').length, 1);
    assert.equal(run.messages.filter(message => message.transition === 'initialization-save').length, 1);
});

test('accepted initialization save must settle before recovery completion', () => {
    const run = fixture({ initializeSaves: [{}], saveBusy: 4 });
    run.rebuild();
    assert.equal(run.terminated(), false);
    assert.equal(run.bridge.snapshot().phase, 'playing');
    assert.equal(run.messages.some(message => message.kind === 'native-recovery-rebuilt'), false);
    run.setState(1, 1, 1);
    run.update();
    assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
});

test('recorded initialization run save and companion profile save retain their own story and worker', requires('native-runtime-20260912/native-recovery-user-2035-fixed-01.fatal.json'), () => {
    const fatal = JSON.parse(fs.readFileSync(path.join(__dirname,
        '../../artifacts/native-runtime-20260912/native-recovery-user-2035-fixed-01.fatal.json'), 'utf8'));
    const caller = Number(BigInt(fatal.boundary.caller) - BigInt(fatal.base));
    assert.equal(Number(fatal.boundary.arguments[3]), 21);
    const run = fixture({ initializeSaves: [{}, { slot: 21, story: 0, caller, profileBusy: 1 }], saveBusy: 1 });
    run.rebuild();
    assert.equal(run.terminated(), false, run.messages.find(message => message.kind === 'recovery-fatal')?.reason);
    assert.equal(run.bridge.snapshot().phase, 'playing');
    run.setState(1, 1, 1);
    run.update();
    assert.equal(run.bridge.snapshot().phase, 'awaiting-player-acceptance');
    assert.equal(run.messages.filter(message => message.transition === 'source').length, 1);
    assert.equal(run.messages.filter(message => message.transition === 'initialization-save-return').length, 2);
});

test('profile saves reject run story, wrong wrapper, other slots and native rejection', () => {
    for (const invalid of [{ story: 2 }, { caller: 0x1b802c3 }, { slot: 22 }, { mode: 2 },
        { threadId: 51 }, { result: 0 }]) {
        const run = fixture({ initializeSaves: [{}, { slot: 21, story: 0, caller: 0x1b8152f, ...invalid }] });
        run.rebuild();
        assert.equal(run.terminated(), true, JSON.stringify(invalid));
        assert.equal(run.messages.some(message => message.kind === 'native-recovery-rebuilt'), false);
    }
});

test('invalid initialization, load reentry and invalid or rejected saves still fail closed', () => {
    for (const options of [{ checkpoint: 0 }, { initializeCaller: 0 }, { unobservedBusy: true },
        ...[{ mode: 2 }, { slot: 21 }, { manager: 0 }, { story: 1 }, { threadId: 51 },
            { caller: 0 }, { result: 0 }].map(invalid => ({ initializeSaves: [invalid] }))]) {
        const run = fixture(options);
        run.rebuild();
        assert.equal(run.terminated(), true, JSON.stringify(options));
        assert.equal(run.messages.some(message => message.kind === 'native-recovery-rebuilt'), false);
    }
});

test('wrong process, source packet, code and partial installation issue no native load', () => {
    for (const options of [{ wrongPid: true }, { corruptPacket: true }, { missingCode: true }, { attachFailureAt: 3 }]) {
        const run = fixture(options);
        assert.equal(run.schedules(), 0);
        assert.equal(run.terminated(), false);
        assert.equal(run.detached(), run.hooks.size);
        assert.equal(run.messages.at(-1).kind, 'stopped');
    }
});

test('active hideout, death-result state and busy workers never trigger recovery', () => {
    for (const values of [[1, 1, 1], [0, 2, 0, 4]]) {
        const run = fixture();
        run.setState(...values);
        run.update(60);
        assert.equal(run.schedules(), 0);
        run.bridge.stop();
        assert.equal(run.terminated(), false);
    }
});

test('I/O error, metadata mismatch, short capacity, wrong caller and bad reads terminate after scheduling', () => {
    for (const options of [{ ioError: true }, { badMetadata: true }, { badCapacity: true },
        { wrongCaller: true }, { readFault: true }, { requestStory: 0 }, { requestStory: 1 }]) {
        const run = fixture(options);
        run.rebuild();
        assert.equal(run.terminated(), true, JSON.stringify(options));
        assert.equal(run.messages.some(message => message.kind === 'native-recovery-rebuilt'), false);
    }
});

test('story campaign and non-run slots cannot schedule a native recovery', () => {
    for (const options of [{ menuStory: 0 }, { menuStory: 1 }, { menuStory: 3 }, { menuSlot: 21 }]) {
        const run = fixture(options);
        run.update(60);
        assert.equal(run.schedules(), 0);
        run.bridge.stop();
        assert.equal(run.terminated(), false);
    }
});

test('corrupt native load, rejected apply and corrupt promotion cannot reach the continue callback', () => {
    for (const options of [{ corruptLoad: true }, { applyFailure: true }, { corruptPromotion: true }]) {
        const run = fixture(options);
        run.rebuild();
        assert.equal(run.terminated(), true);
        assert.equal(run.messages.some(message => message.transition === 'callback'), false);
    }
});

test('missing spawn never counts as rebuilt and an unfinished detach uses the shutdown guard', () => {
    const run = fixture({ missingSpawn: true });
    run.rebuild();
    assert.equal(run.messages.some(message => message.kind === 'native-recovery-rebuilt'), false);
    assert.throws(() => run.bridge.stop(), /mock process exited/);
    assert.equal(run.terminated(), true);
});
