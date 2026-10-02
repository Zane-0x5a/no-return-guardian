'use strict';

// Supplies one host-approved continue-button command per token to the death results scripts.
const configuration = globalThis.nativeResultsContinueConfiguration;
const image = Process.mainModule;
const command = uint64('0x2300eca979d08993');
const pads = new Set([0, 2, 6]);
const sites = [
    ['joypad-command-active?', 0xd49630],
    ['joypad-command-active-and-enabled?', 0xd49530]
];
const listeners = [];
let token = null;
let finished = false;
let queries = 0;
let injected = 0;
let lastInjection = -Infinity;

function publish(kind, fields = {}) {
    send({ kind, time: Date.now(), gameTerminationIssued: false, ...fields });
}

function location(address) {
    const offset = address.sub(image.base);
    return offset.compare(0) >= 0 && offset.compare(image.size) < 0
        ? `exe+0x${offset.toString(16)}` : address.toString();
}

function stop(reason) {
    if (finished) return { injected, queries };
    finished = true;
    token = null;
    const errors = [];
    for (const listener of listeners) {
        try { listener.detach(); } catch (error) { errors.push(error.message); }
    }
    try { Interceptor.flush(); } catch (error) { errors.push(error.message); }
    publish('results-continue-stopped', { reason, injected, queries, cleanupErrors: errors });
    return { injected, queries, cleanupErrors: errors };
}

function enter(site, invocation, args) {
    if (finished || token === null) return;
    try {
        const argv = args[2];
        if (args[1].toInt32() !== 2 || argv.isNull() || !argv.readU64().equals(command)) return;
        queries++;
        const pad = argv.add(8).readS32();
        // A released frame between two presses, independent of how fast the host issues tokens.
        if (!pads.has(pad) || Date.now() - lastInjection < configuration.minimumGapMs) return;
        lastInjection = Date.now();
        invocation.continueToken = { id: token.id, site, pad, result: args[0], context: args[3] };
        token = null;
    } catch (error) {
        stop(`probe-error: ${error.message}`);
    }
}

function leave(invocation) {
    const pending = invocation.continueToken;
    if (!pending || finished) return;
    try {
        const original = pending.result.readU64().toString();
        pending.result.writeU64(1);
        injected++;
        publish('results-continue-injected', { id: pending.id, site: pending.site, pad: pending.pad,
            original, threadId: invocation.threadId, caller: location(invocation.returnAddress),
            context: pending.context.toString() });
        if (injected >= configuration.maximumInjections) stop('injection-limit');
    } catch (error) {
        stop(`probe-error: ${error.message}`);
    }
}

function press(id, lifetime) {
    if (finished) throw new Error('Results continue injector is stopped');
    if (!Number.isInteger(id) || id <= 0 || !(lifetime >= 100 && lifetime <= 2000)) {
        throw new Error('Invalid continue token');
    }
    if (token !== null) publish('results-continue-expired', { id: token.id, superseded: true });
    token = { id };
    setTimeout(() => {
        if (token !== null && token.id === id) {
            token = null;
            publish('results-continue-expired', { id, superseded: false });
        }
    }, lifetime);
    return { id, queries };
}

function install() {
    if (configuration.mode !== 'results-continue' || Process.arch !== 'x64' ||
        Process.id !== configuration.pid || image.name.toLowerCase() !== 'tlou-ii.exe' ||
        !image.base.equals(ptr(configuration.base))) throw new Error('Process identity mismatch');
    if (configuration.fingerprints.length !== sites.length) throw new Error('Incomplete fingerprints');
    if (!(configuration.minimumGapMs >= 50 && configuration.minimumGapMs <= 1000)) {
        throw new Error('Invalid continue gap');
    }
    for (const [site, rva] of sites) {
        const fingerprint = configuration.fingerprints.find(record => record.rva === rva);
        if (!fingerprint || fingerprint.bytes.length !== 64 ||
            Array.from(new Uint8Array(image.base.add(rva).readByteArray(32)))
                .map(value => value.toString(16).padStart(2, '0')).join('') !== fingerprint.bytes) {
            throw new Error(`Code fingerprint mismatch: ${site}`);
        }
    }
    for (const [site, rva] of sites) {
        listeners.push(Interceptor.attach(image.base.add(rva), {
            onEnter(args) { enter(site, this, args); },
            onLeave() { leave(this); }
        }));
    }
    Interceptor.flush();
    publish('results-continue-installed', { sites: sites.map(([site]) => site) });
}

rpc.exports = {
    press(id, lifetime) { return press(id, lifetime); },
    stop() { return stop('host-stop'); }
};
try {
    install();
    setTimeout(() => stop('deadline'), configuration.duration * 1000);
} catch (error) {
    stop(`probe-error: ${error.message}`);
}
