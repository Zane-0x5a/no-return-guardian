'use strict';

// Records the route board's departure event, replays it in the rebuilt hideout, or broadcasts the pause
// menu's abandon event in an encounter. Every native call happens once on the RootGame update thread.
// Entry-only hooks are function probes: an onEnter-only object still intercepts the return in this Frida
// build and crashes once a script fiber resumes on another thread.
const configuration = globalThis.nativeEncounterConfiguration;
const image = Process.mainModule;
const options = { abi: 'win64', exceptions: 'propagate', scheduling: 'exclusive' };
const nextTask = uint64('0xa9456e5567d0ce70');
const abandoned = uint64('0xdd669d5bfffeaf6c');
const required = [0x133ec5e, 0x1b843a0, 0x1b83f70, 0x1b6e000, 0xdc1780, 0x1b6ea60, 0xfe7b10, 0x1200ac0];
const listeners = [];
const empty = Memory.alloc(16);
let departure = null;
let finished = false;
let owner = null;
let rootOwner = null;
let currentTask = null;
let broadcast = null;
let stable = 0;
let broadcasts = 0;
let issuedAt = null;
let queued = null;
let remembered = false;
let spawned = false;
let arrived = 0;
let seen = 0;

function publish(kind, fields = {}) {
    send({ kind, time: Date.now(), mode: configuration.mode, gameTerminationIssued: false, ...fields });
}

function stop(reason) {
    if (finished) return { broadcasts };
    finished = true;
    const errors = [];
    for (const listener of listeners) {
        try { listener.detach(); } catch (error) { errors.push(error.message); }
    }
    try { Interceptor.flush(); } catch (error) { errors.push(error.message); }
    publish('encounter-control-stopped', { reason, broadcasts, seen, cleanupErrors: errors });
    return { broadcasts, cleanupErrors: errors };
}

function guard(action) {
    if (finished) return;
    try { action(); } catch (error) { stop(`control-error: ${error.message}`); }
}

function hex(data) {
    return Array.from(new Uint8Array(data), value => value.toString(16).padStart(2, '0')).join('');
}

function state() {
    const run = image.base.add(0x415ac90);
    const manager = image.base.add(0x9341660);
    return { runActive: run.add(0x80).readU8(), runInitialized: run.add(0x72c8).readU8(),
        story: image.base.add(0x9398730).readS32(), slot: manager.add(0x5e0).readS32(),
        workers: [0x58, 0x228, 0x3f8].map(offset => manager.add(offset).readS32()) };
}

function task() {
    const current = currentTask(image.base.add(0x9334770), 0, 0);
    if (current.isNull()) return null;
    const descriptor = current.add(0x120).readPointer();
    if (descriptor.isNull()) return null;
    return { task: current.toString(), word: descriptor.add(0x6c).readS32(),
        subnode: current.add(0x90).readU64().toString(16) };
}

function playable(observed) {
    return observed.runActive === 1 && observed.runInitialized === 1 && observed.story === 2 &&
        observed.slot === 20 && observed.workers.every(value => value === 0);
}

function record(args) {
    seen++;
    const event = args[2];
    if (event.isNull() || !event.readU64().equals(nextTask)) return;
    const count = event.add(0x128).readS16();
    if (count < 1 || count > 4) throw new Error(`Unsupported departure argument count ${count}`);
    // A probe sits on the first instruction, so the return address is still on top of the stack.
    // The save workers at this instant tell the host whether a hideout save was still being written
    // when the player departed; the departure save itself starts only after the respite handles this event.
    const observed = state();
    publish('departure-captured', { count, slots: hex(event.add(0x28).readByteArray(count * 16)),
        threadId: this.threadId, caller: this.context.rsp.readPointer().sub(image.base).toString(16),
        workers: observed.workers, runActive: observed.runActive });
    stop('captured');
}

function issue(sid, slots) {
    broadcasts++;
    issuedAt = Date.now();
    publish(configuration.mode === 'redeploy' ? 'redeploy-broadcast' : 'abandon-broadcast',
        { attempt: broadcasts, task: task(), state: state() });
    // The native appends a0..a3 in order and stops at the first slot whose type word is 0.
    broadcast(image.base.add(0x9342080), sid, slots[0], slots[1] || empty, empty, empty);
}

function redeploy() {
    const observed = state();
    const current = task();
    // A lost event may be retried only while nothing of the departure has started: no remember, no queued task.
    if (broadcasts === 0 || (queued === null && !remembered && Date.now() - issuedAt > configuration.retryMs)) {
        if (!playable(observed) || current === null || current.word !== 2) { stable = 0; return; }
        if (++stable < configuration.settleUpdates) return;
        if (broadcasts >= configuration.maximumBroadcasts) {
            publish('redeploy-not-observed', { broadcasts, task: current, state: observed });
            stop('redeploy-not-observed');
            return;
        }
        stable = 0;
        issue(nextTask, departure);
        return;
    }
    if (queued === null || !spawned || current === null || current.word !== 1 || !playable(observed)) {
        arrived = 0;
        return;
    }
    if (++arrived < 30) return;
    publish('redeployed', { broadcasts, queued, task: current, state: observed,
        seconds: (Date.now() - issuedAt) / 1000 });
    stop('redeployed');
}

function abandon() {
    const observed = state();
    if (broadcasts === 0) {
        const current = task();
        if (!playable(observed) || current === null || current.word !== 1) { stable = 0; return; }
        if (++stable < 30) return;
        issue(abandoned, [empty]);
        return;
    }
    if (observed.runActive !== 0) return;
    publish('abandon-observed', { state: observed, seconds: (Date.now() - issuedAt) / 1000 });
    stop('abandon-observed');
}

function install() {
    if (!['record', 'redeploy', 'abandon'].includes(configuration.mode) || Process.arch !== 'x64' ||
        Process.id !== configuration.pid || image.name.toLowerCase() !== 'tlou-ii.exe' ||
        !image.base.equals(ptr(configuration.base))) throw new Error('Process identity mismatch');
    for (const rva of required) {
        const fingerprint = configuration.fingerprints.find(item => item.rva === rva);
        if (!fingerprint || fingerprint.bytes.length !== 64 ||
            hex(image.base.add(rva).readByteArray(32)) !== fingerprint.bytes) {
            throw new Error(`Code fingerprint mismatch: ${rva.toString(16)}`);
        }
    }
    if (configuration.mode === 'record') {
        listeners.push(Interceptor.attach(image.base.add(0x1b83f70), function(args) {
            guard(() => record.call(this, args));
        }));
        Interceptor.flush();
        publish('encounter-control-installed');
        return;
    }
    rootOwner = new NativeFunction(image.base.add(0xfe7b10), 'pointer', [], options);
    currentTask = new NativeFunction(image.base.add(0x1b6ea60), 'pointer', ['pointer', 'bool', 'bool'], options);
    broadcast = new NativeFunction(image.base.add(0x1b843a0), 'void',
        ['pointer', 'uint64', 'pointer', 'pointer', 'pointer', 'pointer'], options);
    owner = rootOwner();
    if (owner.isNull()) throw new Error('Root game is unavailable');
    if (configuration.mode === 'redeploy') {
        const slots = configuration.slots;
        // Each slot: value in bytes 0-7, little-endian type word at byte 12. The board sends the int32
        // (type 2) route index, then, from its wait-for-input state, the photo's boolean (type 1).
        if (typeof slots !== 'string' || !/^([0-9a-f]{32}){1,2}$/.test(slots) || slots.slice(24, 28) !== '0200' ||
            (slots.length === 64 && slots.slice(56, 60) !== '0100')) {
            throw new Error('Departure arguments are not a recorded int32 route index and optional boolean');
        }
        departure = slots.match(/.{32}/g).map(text => {
            const slot = Memory.alloc(16);
            slot.writeByteArray(text.match(/../g).map(value => parseInt(value, 16)));
            return slot;
        });
        listeners.push(Interceptor.attach(image.base.add(0x1b6e000), function(args) {
            guard(() => {
                if (broadcasts === 0 || queued !== null) return;
                queued = { kind: args[1].readS32(), parameter: args[1].add(0x18).readS32(), threadId: this.threadId };
                publish('redeploy-queued', queued);
            });
        }));
        listeners.push(Interceptor.attach(image.base.add(0x1200ac0), function() {
            guard(() => { if (broadcasts > 0 && !remembered) { remembered = true; publish('redeploy-remembered'); } });
        }));
        // Spawn completion needs the return; the recovery bridge hooks this site the same way.
        listeners.push(Interceptor.attach(image.base.add(0xdc1780), {
            onEnter() {},
            onLeave() { guard(() => { if (queued !== null) spawned = true; }); }
        }));
    }
    listeners.push(Interceptor.attach(image.base.add(0x133ec5e), function() {
        guard(() => {
            if (!this.context.rbx.equals(owner)) return;
            if (configuration.mode === 'redeploy') redeploy();
            else abandon();
        });
    }));
    Interceptor.flush();
    publish('encounter-control-installed');
}

rpc.exports = { stop() { return stop('host-stop'); } };
try {
    install();
    setTimeout(() => stop('deadline'), configuration.duration * 1000);
} catch (error) {
    stop(`install-error: ${error.message}`);
}
