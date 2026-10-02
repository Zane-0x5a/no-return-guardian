'use strict';

const configuration = globalThis.nativeCheckpointConfiguration;
const image = Process.mainModule;
const listeners = [];
let finished = false;
let sequence = 0;
let lastState = null;
let lastTask = null;
const selections = new Map();
let eventCount = 0;
let lastContent = null;
const contentAllocations = [
    ['gameplay', 0x35f9f40, 0x6790, 3],
    ['records', 0x35f9f48, 0x26930, 3],
    ['body-manager', 0x35f9f50, 0xbc10, 3],
    ['clocks', 0x35f9f58, 0x238, 3],
    ['permadeath-clock', 0x35f9f60, 0x238, 1],
    ['tasks', 0x35f9f68, 0xaa08, 3],
    ['stats', 0x35f9f70, 0x6708, 3],
    ['chapter', 0x35f9f78, 0xd7d10, 1],
    ['player-killed', 0x35f9f80, 0x2080, 1],
    ['npc-killed', 0x35f9f88, 0x1f70, 1]
];
const operations = [
    ['store-hub-fields', 0x1048960],
    ['force-remember', 0x10434c0],
    ['debug-backup', 0x1042aa0],
    ['debug-restore-backup', 0x1042b80],
    ['remember-checkpoint', 0x1200ac0],
    ['copy-checkpoint', 0x11f9300],
    ['update-run-checkpoint', 0x1049bf0],
    ['init-run-from-checkpoint', 0x1046290]
];

function publish(kind, fields = {}) {
    send({ kind, time: Date.now(), sequence: ++sequence, ...fields });
}

function stop(reason) {
    if (finished) return;
    finished = true;
    for (const listener of listeners) listener.detach();
    Interceptor.flush();
    publish('stopped', { reason, eventCount, restoredPreparation: false });
}

function bytesAt(address, length) {
    return Array.from(new Uint8Array(address.readByteArray(length)))
        .map(value => value.toString(16).padStart(2, '0')).join('');
}

function state() {
    const manager = image.base.add(0x415ac90);
    return {
        rogue: image.base.add(0x4161f58).readU8(),
        mode: image.base.add(0x415ad30).readS32(),
        checkpointSelector: manager.add(0x520).readS32(),
        runCheckpointCount: manager.add(0x1528).readS32(),
        initializedByte: manager.add(0x72c8).readU8(),
        hubFieldsHex: bytesAt(manager.add(0x50), 16),
        checkpointPointers: [0x35f9f40, 0x35f9f48, 0x35f9f50, 0x35f9f58,
            0x35f9f68, 0x35f9f70].map(rva => image.base.add(rva).readPointer().toString())
    };
}

function contentLayout() {
    const regions = [];
    for (const [name, pointerRva, size, count] of contentAllocations) {
        const address = image.base.add(pointerRva).readPointer();
        if (address.isNull()) throw new Error(`Unallocated checkpoint member: ${name}`);
        for (let slot = 0; slot < count; slot++) {
            regions.push({ name: count === 1 ? name : `${name}[${slot}]`,
                address: address.add(slot * size).toString(), size });
        }
    }
    regions.push({ name: 'run-checkpoint-observed-span',
        address: image.base.add(0x415ac90 + 0x528).toString(), size: 0x6d8c });
    return regions;
}

function contentEvidence(stage, callId = null) {
    if (!configuration.lifecycleContent || finished) return;
    if (!reserveEvent()) return;
    const started = Date.now();
    const layout = contentLayout();
    const digest = region => {
        const bytes = ptr(region.address).readByteArray(region.size);
        if (bytes === null || bytes.byteLength !== region.size) {
            throw new Error(`Incomplete checkpoint member read: ${region.name}`);
        }
        return Checksum.compute('sha256', bytes);
    };
    const first = layout.map(digest);
    const second = layout.map(digest);
    const sameAddresses = JSON.stringify(layout) === JSON.stringify(contentLayout());
    const stable = sameAddresses && first.every((value, index) => value === second[index]);
    const regions = layout.map((region, index) => ({ ...region,
        sha256: first[index], secondSha256: second[index] }));
    const previous = lastContent === null ? null : new Map(lastContent.map(region => [region.name, region]));
    const changed = stable && previous !== null ? regions.filter(region => {
        const before = previous.get(region.name);
        return before.address !== region.address || before.sha256 !== region.sha256;
    }).map(region => region.name) : null;
    publish('lifecycle-content', { stage, callId, regions, stable, sameAddresses, changed,
        elapsedMs: Date.now() - started, coherentSnapshot: false, restorable: false,
        scope: 'fixed native allocations and observed run span; ownership and restore boundary unproven' });
    lastContent = stable ? regions : null;
}

function guarded(action) {
    if (finished) return;
    try { action(); }
    catch (error) { stop(`lifecycle-error: ${error.message}`); }
}

function sample() {
    guarded(() => {
        const current = state();
        const key = JSON.stringify(current);
        if (key !== lastState) {
            if (!reserveEvent()) return;
            lastState = key;
            publish('lifecycle-state', { state: current, coherentSnapshot: false });
        }
    });
}

function reserveEvent() {
    if (++eventCount > 1024) {
        stop('lifecycle-error: event budget exceeded');
        return false;
    }
    return true;
}

function install() {
    if (configuration.mode !== 'lifecycle' || Process.arch !== 'x64' ||
        Process.id !== configuration.pid || image.name.toLowerCase() !== 'tlou-ii.exe' ||
        !image.base.equals(ptr(configuration.base))) throw new Error('Process or mode mismatch');
    const required = [...operations.map(entry => entry[1]), 0x105e930, 0x1b6ea60];
    if (required.some(rva => !configuration.fingerprints.some(record => record.rva === rva))) {
        throw new Error('Missing lifecycle code fingerprints');
    }
    for (const fingerprint of configuration.fingerprints) {
        if (bytesAt(image.base.add(fingerprint.rva), fingerprint.bytes.length / 2) !== fingerprint.bytes) {
            throw new Error('Live code fingerprint mismatch');
        }
    }
    for (const [operation, rva] of operations) {
        listeners.push(Interceptor.attach(image.base.add(rva), {
            onEnter(args) {
                guarded(() => {
                    if (!reserveEvent()) return;
                    this.callId = sequence + 1;
                    publish('lifecycle-enter', { callId: this.callId, operation,
                        threadId: this.threadId, caller: this.returnAddress.toString(),
                        rawRegisters: [args[0], args[1], args[2], args[3]].map(value => value.toString()),
                        copyIndices: operation === 'copy-checkpoint'
                            ? { source: args[1].toInt32(), destination: args[2].toInt32() } : null,
                        state: state() });
                    contentEvidence(`${operation}:enter`, this.callId);
                });
            },
            onLeave() {
                if (this.callId === undefined) return;
                guarded(() => {
                    if (!reserveEvent()) return;
                    publish('lifecycle-leave', { callId: this.callId, operation, state: state() });
                    contentEvidence(`${operation}:leave`, this.callId);
                });
            }
        }));
    }
    listeners.push(Interceptor.attach(image.base.add(0x1b6ea60), {
        onEnter(args) {
            this.relevant = args[0].equals(image.base.add(0x9334770)) &&
                (args[1].toUInt32() & 255) === 0 && (args[2].toUInt32() & 255) === 0;
        },
        onLeave(result) {
            if (!this.relevant) return;
            guarded(() => {
                const descriptor = result.isNull() ? null : result.add(0x120).readPointer();
                const task = { pointer: result.toString(),
                    word: descriptor === null || descriptor.isNull() ? null : descriptor.add(0x6c).readS32(),
                    field1e0: result.isNull() ? null : result.add(0x1e0).readS32() };
                const key = JSON.stringify(task);
                if (key === lastTask || !reserveEvent()) return;
                lastTask = key;
                publish('lifecycle-task', { task, caller: this.returnAddress.toString() });
                contentEvidence('task-change');
            });
        }
    }));
    listeners.push(Interceptor.attach(image.base.add(0x105e930), {
        onLeave(result) {
            guarded(() => {
                const selection = { slot: result.toInt32(), caller: this.returnAddress.toString() };
                if (selections.get(selection.caller) === selection.slot || !reserveEvent()) return;
                selections.set(selection.caller, selection.slot);
                publish('lifecycle-selection', selection);
            });
        }
    }));
    Interceptor.flush();
    publish('installed', { pid: Process.id, base: image.base.toString(), mode: 'lifecycle',
        nativeCallsIssued: 0, restorationClaim: false,
        lifecycleContent: configuration.lifecycleContent === true });
    sample();
    contentEvidence('installed');
}

rpc.exports = { stop() { stop('host-stop'); return { finished, eventCount }; } };
try {
    install();
    setInterval(sample, 250);
    setTimeout(() => stop('deadline'), configuration.duration * 1000);
} catch (error) { stop(`lifecycle-error: ${error.message}`); }
