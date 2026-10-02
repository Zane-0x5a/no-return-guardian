'use strict';

const configuration = globalThis.nativeCheckpointConfiguration;
const image = Process.mainModule;
const listeners = [];
const collection = configuration.mode === 'collection-observe'
    ? globalThis.createNativeCollection(configuration, image, (kind, fields, data) => {
        if (!event(kind, fields, data)) throw new Error('Collection event budget exceeded');
    }) : null;
const operations = [
    ['menu-exit', 0xfe8fc0],
    ['update-run-checkpoint', 0x1049bf0],
    ['run-reset', 0x1048520],
    ['schedule-continue', 0xfe96f0],
    ['waiting-enter', 0xfdcd90],
    ['waiting-request', 0x1b80080],
    ['continue-callback', 0xfd7870],
    ['run-teardown', 0x1042680],
    ['run-initialize', 0x103f2c0],
    ['init-run-checkpoint', 0x1046290],
    ['add-command', 0x1b6e000],
    ['task-play', 0x1bdeed0],
    ['graph-reset', 0x1b6fa70]
];
if (collection !== null) operations.push(...collection.operations);
let finished = false;
let sequence = 0;
let eventCount = 0;
let lastState = null;
let lastPredicate = null;
let predicateCalls = 0;
let timer = null;
const subnodeGroups = new Map();
const subnodeSamples = [];
let subnodeEntered = 0;
let subnodeReturned = 0;
let subnodeDirty = false;

function subnodeSummary(reason) {
    if (!subnodeDirty) return;
    subnodeDirty = false;
    publish('load-subnode-summary', { reason, entered: subnodeEntered, returned: subnodeReturned,
        activeAtSnapshot: subnodeEntered - subnodeReturned, fullCallTrace: false,
        groups: Array.from(subnodeGroups.values(), group => ({ ...group })),
        samples: subnodeSamples.map(sample => ({ ...sample, task: { ...sample.task } })) });
}

function publish(kind, fields = {}, data) {
    send({ kind, time: Date.now(), sequence: ++sequence, nativeCallsIssued: 0,
        restoredPreparation: false, inGameAcceptance: false, ...fields }, data);
}

function stop(reason) {
    if (finished) return;
    finished = true;
    if (timer !== null) clearInterval(timer);
    const cleanupErrors = [];
    for (const listener of listeners) {
        try { listener.detach(); } catch (error) { cleanupErrors.push(error.message); }
    }
    try { Interceptor.flush(); } catch (error) { cleanupErrors.push(error.message); }
    subnodeSummary('observer-stop');
    publish('stopped', { reason: cleanupErrors.length ? `load-error: cleanup: ${cleanupErrors.join('; ')}` : reason,
        eventCount, predicateCalls, cleanupErrors, collection: collection?.stop() ?? null });
}

function guarded(action) {
    if (finished) return;
    try { action(); } catch (error) { stop(`load-error: ${error.message}`); }
}

function event(kind, fields, data) {
    if (finished) return false;
    if (++eventCount > (collection === null ? 2048 : 32768)) {
        stop('load-error: event budget exceeded');
        return false;
    }
    publish(kind, fields, data);
    return true;
}

function loaderState() {
    const manager = image.base.add(0x9341660);
    return { requestState: manager.add(0x5e0).readS32(),
        field5a8: manager.add(0x5a8).readPointer().toString(),
        workerStates: [0x58, 0x228, 0x3f8].map(offset => manager.add(offset).readS32()),
        runInitialized: image.base.add(0x415ac90 + 0x72c8).readU8(),
        runMode: image.base.add(0x415ac90 + 0xa0).readS32(),
        runSelector: image.base.add(0x415ac90 + 0x520).readS32(),
        runCheckpointCount: image.base.add(0x415ac90 + 0x1528).readS32(),
        rogue: image.base.add(0x4161f58).readU8(), coherentSnapshot: false };
}

function sample() {
    guarded(() => {
        const state = loaderState();
        const key = JSON.stringify(state);
        if (key === lastState) return;
        lastState = key;
        event('load-state', { state });
    });
}

function taskState(task) {
    if (task.isNull()) throw new Error('Null task at native boundary');
    return { address: task.toString(), state60: task.add(0x60).readS32(),
        subtaskSidHex: task.add(0x90).readU64().toString(16) };
}

function detail(operation, args) {
    if (operation === 'add-command') {
        if (args[1].isNull()) throw new Error('Null queue record');
        return { queueCount: args[0].add(0x260).readU32(),
            commandKind: args[1].readS32(), parameter18: args[1].add(0x18).readS32(),
            taskIdHex: args[1].add(8).readU64().toString(16),
            subtaskIdHex: args[1].add(0x10).readU64().toString(16) };
    }
    if (operation === 'task-play') return { task: taskState(args[0]),
        sid: args[1].toString(), parameter: args[2].toInt32() };
    if (operation === 'graph-reset') return { parameter: args[1].toInt32() };
    return {};
}

function bytesAt(address) {
    return Array.from(new Uint8Array(address.readByteArray(32)))
        .map(value => value.toString(16).padStart(2, '0')).join('');
}

function install() {
    if (!['load-observe', 'collection-observe'].includes(configuration.mode) || Process.arch !== 'x64' ||
        Process.id !== configuration.pid || image.name.toLowerCase() !== 'tlou-ii.exe' ||
        !image.base.equals(ptr(configuration.base))) throw new Error('Process or mode mismatch');
    const required = [...operations.map(operation => operation[1]), 0x1b7d080, 0x1bdb630];
    for (const rva of required) {
        const fingerprint = configuration.fingerprints.find(record => record.rva === rva);
        if (!fingerprint || fingerprint.bytes.length !== 64 ||
            bytesAt(image.base.add(rva)) !== fingerprint.bytes) throw new Error('Live fingerprint mismatch');
    }
    for (const [operation, rva] of operations) {
        listeners.push(Interceptor.attach(image.base.add(rva), {
            onEnter(args) {
                guarded(() => {
                    if (collection?.isQuery(operation)) {
                        collection.queryEnter(operation, this, args);
                        return;
                    }
                    if (collection !== null && !collection.trace(operation, this.threadId)) return;
                    this.target = args[0];
                    this.callId = sequence + 1;
                    event('load-enter', { callId: this.callId, operation, threadId: this.threadId,
                        caller: this.returnAddress.toString(),
                        rawRegisters: [args[0], args[1], args[2], args[3]].map(value => value.toString()),
                        detail: detail(operation, args), state: loaderState() });
                    if (collection !== null) event('collection-enter-detail', { callId: this.callId,
                        operation, detail: collection.enter(operation, this, args) });
                });
            },
            onLeave(result) {
                if (collection?.isQuery(operation)) {
                    guarded(() => collection.queryLeave(operation, this, result));
                    return;
                }
                if (this.callId === undefined) return;
                guarded(() => {
                    const after = operation === 'add-command'
                        ? { queueCount: this.target.add(0x260).readU32(), acceptanceClaim: false } : {};
                    event('load-leave', { callId: this.callId, operation, threadId: this.threadId,
                        detail: after, state: loaderState() });
                    if (collection !== null) event('collection-leave-detail', { callId: this.callId,
                        operation, detail: collection.leave(operation, this, result) });
                    if (operation === 'task-play') subnodeSummary('task-play-return');
                });
            }
        }));
    }
    listeners.push(Interceptor.attach(image.base.add(0x1b7d080), {
        onLeave(result) {
            guarded(() => {
                predicateCalls++;
                const predicate = { returned: result.toUInt32() & 255, state: loaderState() };
                const key = JSON.stringify(predicate);
                if (key === lastPredicate) return;
                lastPredicate = key;
                event('load-wait-predicate', { ...predicate, predicateCalls });
            });
        }
    }));
    listeners.push(Interceptor.attach(image.base.add(0x1bdb630), {
        onEnter(args) {
            guarded(() => {
                const task = taskState(args[0]);
                const fields = { caller: this.returnAddress.toString(),
                    parameter: args[1].toInt32(), state60: task.state60 };
                let key = JSON.stringify(fields);
                if (!subnodeGroups.has(key) && subnodeGroups.size >= 64) key = 'other';
                if (!subnodeGroups.has(key)) subnodeGroups.set(key, {
                    ...(key === 'other' ? { other: true } : fields),
                    entered: 0, returnedZero: 0, returnedNonzero: 0 });
                this.group = subnodeGroups.get(key);
                this.group.entered++;
                subnodeEntered++;
                subnodeDirty = true;
                if (subnodeSamples.length < 16) {
                    this.sample = { callId: subnodeEntered, threadId: this.threadId, ...fields, task };
                    subnodeSamples.push(this.sample);
                }
            });
        },
        onLeave(result) {
            if (this.group === undefined) return;
            guarded(() => {
                const returned = result.toUInt32() & 255;
                this.group[returned === 0 ? 'returnedZero' : 'returnedNonzero']++;
                subnodeReturned++;
                subnodeDirty = true;
                if (this.sample) this.sample.returned = returned;
            });
        }
    }));
    Interceptor.flush();
    publish('installed', { mode: configuration.mode, hooks: listeners.length });
    sample();
    collection?.start();
}

rpc.exports = {
    stop() { stop('host-stop'); return { finished, eventCount }; },
    mark(name) {
        if (finished || collection === null) throw new Error('No active collection');
        let result;
        guarded(() => { result = collection.mark(name); });
        return result;
    }
};
try {
    install();
    if (!finished) {
        timer = setInterval(sample, 250);
        setTimeout(() => stop('deadline'), configuration.duration * 1000);
    }
} catch (error) { stop(`load-error: ${error.message}`); }
