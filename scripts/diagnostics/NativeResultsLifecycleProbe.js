'use strict';

const configuration = globalThis.nativeResultsLifecycleConfiguration;
const image = Process.mainModule;
const listeners = [];
const sites = [
    ['force-remember-script', 0x104c570],
    ['update-run-script', 0x104d4f0],
    ['queue-command', 0x1b6e000],
    ['play-task', 0x1bdeed0],
    ['run-teardown', 0x1042680],
    ['run-reset', 0x1048520],
    ['pause-menu-exit', 0xfe8fc0]
];
const counts = new Map();
let finished = false;
let eventCount = 0;

function publish(kind, fields = {}) {
    send({ kind, time: Date.now(), nativeCallsIssued: 0, gameTerminationIssued: false, ...fields });
}

function state() {
    const run = image.base.add(0x415ac90);
    const save = image.base.add(0x9341660);
    return { initialized: run.add(0x72c8).readU8(), selector: run.add(0x520).readS32(),
        story: image.base.add(0x9398730).readS32(),
        workers: [0x58, 0x228, 0x3f8].map(offset => save.add(offset).readS32()) };
}

function location(address) {
    const offset = address.sub(image.base);
    return offset.compare(0) >= 0 && offset.compare(image.size) < 0
        ? `exe+0x${offset.toString(16)}` : address.toString();
}

function stop(reason) {
    if (finished) return { eventCount };
    finished = true;
    const errors = [];
    for (const listener of listeners) {
        try { listener.detach(); } catch (error) { errors.push(error.message); }
    }
    try { Interceptor.flush(); } catch (error) { errors.push(error.message); }
    publish('stopped', { reason, eventCount, cleanupErrors: errors });
    return { eventCount, cleanupErrors: errors };
}

function observe(site, invocation, args) {
    if (finished) return;
    try {
        let command = null;
        if (site === 'queue-command') {
            if (args[1].isNull()) return;
            command = { kind: args[1].readS32(), parameter: args[1].add(0x18).readS32(),
                taskId: args[1].add(8).readU64().toString(16),
                subtaskId: args[1].add(0x10).readU64().toString(16) };
            if (command.kind !== 1 || command.parameter !== 2) return;
        }
        if (site === 'play-task' && args[2].toInt32() !== 2) return;
        const count = (counts.get(site) || 0) + 1;
        counts.set(site, count);
        if (count > 6 || eventCount >= 48) return;
        const stack = Thread.backtrace(invocation.context, Backtracer.ACCURATE)
            .slice(0, 12).map(location);
        eventCount++;
        publish('native-results-boundary', { site, count, threadId: invocation.threadId,
            caller: location(invocation.returnAddress), stack, command, state: state() });
    } catch (error) {
        stop(`probe-error: ${error.message}`);
    }
}

function install() {
    if (configuration.mode !== 'results-lifecycle-observe' ||
        Process.arch !== 'x64' || Process.id !== configuration.pid ||
        image.name.toLowerCase() !== 'tlou-ii.exe' ||
        !image.base.equals(ptr(configuration.base))) throw new Error('Process identity mismatch');
    if (configuration.fingerprints.length !== sites.length) throw new Error('Incomplete fingerprints');
    for (const [site, rva] of sites) {
        const fingerprint = configuration.fingerprints.find(record => record.rva === rva);
        if (!fingerprint || fingerprint.bytes.length !== 64 ||
            Array.from(new Uint8Array(image.base.add(rva).readByteArray(32)))
                .map(value => value.toString(16).padStart(2, '0')).join('') !== fingerprint.bytes) {
            throw new Error(`Code fingerprint mismatch: ${site}`);
        }
    }
    for (const [site, rva] of sites) {
        listeners.push(Interceptor.attach(image.base.add(rva), function (args) {
            observe(site, this, args);
        }));
    }
    Interceptor.flush();
    publish('installed', { sites: sites.map(([site]) => site), state: state() });
}

rpc.exports = { stop() { return stop('host-stop'); } };
try {
    install();
    setTimeout(() => stop('deadline'), configuration.duration * 1000);
} catch (error) {
    stop(`probe-error: ${error.message}`);
}
