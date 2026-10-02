'use strict';

const configuration = globalThis.nativeCheckpointConfiguration;
const image = Process.mainModule;
const listeners = [];
const observations = new Map();
let finished = false;
let invoked = false;
let ticks = 0;
let issuedAt = 0;
let activeUpdates = 0;

function publish(kind, fields = {}) {
    send({ kind, time: Date.now(), ...fields });
}

function stop(reason) {
    if (finished) return;
    finished = true;
    for (const listener of listeners) listener.detach();
    Interceptor.flush();
    publish('stopped', { reason, invoked, ticks, restoredPreparation: false });
}

function bytesAt(address, length) {
    return Array.from(new Uint8Array(address.readByteArray(length)))
        .map(value => value.toString(16).padStart(2, '0')).join('');
}

function ownerKey(threadId, owner) {
    return `${threadId}:${owner}`;
}

function install() {
    if (Process.arch !== 'x64' || Process.id !== configuration.pid ||
        image.name.toLowerCase() !== 'tlou-ii.exe' ||
        !image.base.equals(ptr(configuration.base))) {
        throw new Error('Process identity mismatch');
    }
    for (const fingerprint of configuration.fingerprints) {
        if (bytesAt(image.base.add(fingerprint.rva), fingerprint.bytes.length / 2) !== fingerprint.bytes) {
            throw new Error(`Live code mismatch: ${fingerprint.rva}`);
        }
    }
    const restart = new NativeFunction(image.base.add(0x105f830), 'bool', ['bool', 'bool'],
        { abi: 'win64', exceptions: 'propagate', scheduling: 'exclusive', traps: 'default' });
    const currentTask = new NativeFunction(image.base.add(0x1b6ea60), 'pointer', ['pointer', 'bool', 'bool'],
        { abi: 'win64', exceptions: 'propagate', scheduling: 'exclusive' });

    listeners.push(Interceptor.attach(image.base.add(0xfddbf0), {
        onEnter(args) {
            if (finished) return;
            try {
                const owner = args[0].toString();
                const key = ownerKey(this.threadId, owner);
                const previous = observations.get(key) || { events: 0, idle: 0 };
                previous.events++;
                observations.set(key, previous);
                if (previous.events === 1) {
                    publish('root-event', { threadId: this.threadId, owner,
                        event: args[1].isNull() ? null : args[1].readPointer().toString() });
                }
            } catch (error) {
                stop(`event-observation-error: ${error.message}`);
            }
        }
    }));
    listeners.push(Interceptor.attach(image.base.add(0x105f830), {
        onEnter(args) {
            if (finished) return;
            this.record = true;
            publish('restart-enter', { threadId: this.threadId,
                first: args[0].toUInt32() & 255, second: args[1].toUInt32() & 255,
                returnAddress: this.returnAddress.toString(), requestedByProbe: invoked });
        },
        onLeave(result) {
            if (this.record) publish('restart-return', { value: result.toUInt32() & 255 });
        }
    }));
    listeners.push(Interceptor.attach(image.base.add(0xff6920), {
        onEnter(args) {
            this.observe = !finished;
            if (!this.observe) return;
            try {
                this.stateObject = args[0];
                this.owner = args[0].add(8).readPointer();
                if (this.owner.isNull()) throw new Error('Null RootGame owner');
                activeUpdates++;
                this.counted = true;
                if (activeUpdates !== 1) throw new Error('Concurrent RootGame updates');
            } catch (error) {
                this.observe = false;
                stop(`idle-observation-error: ${error.message}`);
            }
        },
        onLeave() {
            if (this.counted) activeUpdates--;
            if (!this.observe || finished) return;
            try {
                if (!this.stateObject.add(8).readPointer().equals(this.owner)) {
                    throw new Error('RootGame owner changed during update');
                }
                const owner = this.owner.toString();
                const caller = this.returnAddress.toString();
                if (!this.returnAddress.equals(image.base.add(0x133ec36))) {
                    throw new Error('Unexpected state-update dispatcher');
                }
                const stateObject = this.stateObject.toString();
                const key = `${owner}:${stateObject}:${caller}`;
                const record = observations.get(key) || { events: 0, idle: 0 };
                record.idle++;
                observations.set(key, record);
                ticks++;
                const rogue = image.base.add(0x4161f58).readU8();
                const mode = image.base.add(0x415ad30).readS32();
                let task = null;
                let taskWord = null;
                if (record.idle === 1 || record.idle === 30 ||
                    (configuration.mode === 'trigger' && !invoked && record.idle >= 30)) {
                    task = currentTask(image.base.add(0x9334770), 0, 0);
                    if (!task.isNull()) {
                        const descriptor = task.add(0x120).readPointer();
                        if (!descriptor.isNull()) taskWord = descriptor.add(0x6c).readS32();
                    }
                }
                if (record.idle === 1 || record.idle === 30 ||
                    (record.events > 0 && !record.matched)) {
                    record.matched = record.events > 0;
                    publish('idle-observed', { threadId: this.threadId, owner,
                        eventCount: record.events, idleCount: record.idle, rogue, mode,
                        stateObject, caller, task: task === null ? null : task.toString(), taskWord });
                }
                if (configuration.mode !== 'trigger' || invoked || record.idle < 30) return;
                if (owner !== configuration.owner || stateObject !== configuration.stateObject ||
                    caller !== configuration.caller) {
                    throw new Error('Observed execution context differs from approved observation');
                }
                if (rogue !== 1) throw new Error('Not Rogue mode');
                if (taskWord !== 1) {
                    throw new Error('Native task is not an encounter; no restart issued');
                }
                if (task.toString() !== configuration.task) throw new Error('Encounter task changed');
                invoked = true;
                issuedAt = Date.now();
                publish('invoke', { threadId: this.threadId, owner, first: false, second: false });
                const result = restart(0, 0);
                publish('invoke-returned', { result, restoredPreparation: false });
            } catch (error) {
                stop(`trigger-error: ${error.message}`);
            }
        }
    }));
    Interceptor.flush();
    publish('installed', { pid: Process.id, base: image.base.toString(), mode: configuration.mode });
}

rpc.exports = {
    stop() {
        stop('host-stop');
        return { invoked, ticks, finished };
    }
};

try {
    install();
    setTimeout(() => stop('deadline'), configuration.duration * 1000);
    setInterval(() => {
        if (!finished && invoked && Date.now() - issuedAt > 5000) stop('post-invocation-observation-ended');
    }, 250);
} catch (error) {
    stop(`install-error: ${error.message}`);
}
