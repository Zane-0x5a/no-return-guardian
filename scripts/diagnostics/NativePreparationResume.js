'use strict';

const configuration = globalThis.nativeCheckpointConfiguration;
const image = Process.mainModule;
const listeners = [];
const chain = [0xfd7870, 0x1042680, 0x103f2c0, 0x1046290, 0x1bdf840];
let finished = false;
let invoked = false;
let activeUpdates = 0;
let ticks = 0;
let chainIndex = 0;
let invocationThread = null;
let callbackThread = null;

function publish(kind, fields = {}) {
    send({ kind, time: Date.now(), restoredPreparation: false, ...fields });
}

function stop(reason) {
    if (finished) return;
    finished = true;
    for (const listener of listeners) listener.detach();
    Interceptor.flush();
    publish('stopped', { reason, invoked, ticks, chainIndex });
}

function guard(action) {
    if (finished) return;
    try { action(); } catch (error) { stop(`resume-error: ${error.message}`); }
}

function install() {
    if (configuration.mode !== 'resume-preparation' || Process.arch !== 'x64' ||
        Process.id !== configuration.pid || image.name.toLowerCase() !== 'tlou-ii.exe' ||
        !image.base.equals(ptr(configuration.base))) throw new Error('Process or mode mismatch');
    const required = [0xff6920, 0x133ebb0, 0x1b6ea60, 0xfe7b10, 0xfe96f0, ...chain];
    for (const rva of required) {
        const fingerprint = configuration.fingerprints.find(record => record.rva === rva);
        if (!fingerprint || fingerprint.bytes.length !== 64) throw new Error('Missing fingerprint');
        const bytes = Array.from(new Uint8Array(image.base.add(rva).readByteArray(32)))
            .map(value => value.toString(16).padStart(2, '0')).join('');
        if (bytes !== fingerprint.bytes) throw new Error('Live code fingerprint mismatch');
    }
    const options = { abi: 'win64', exceptions: 'propagate', scheduling: 'exclusive' };
    const currentTask = new NativeFunction(image.base.add(0x1b6ea60), 'pointer',
        ['pointer', 'bool', 'bool'], options);
    const rootOwner = new NativeFunction(image.base.add(0xfe7b10), 'pointer', [], options);
    const schedule = new NativeFunction(image.base.add(0xfe96f0), 'void',
        ['pointer', 'pointer', 'bool'], options);
    for (const rva of chain) {
        listeners.push(Interceptor.attach(image.base.add(rva), {
            onEnter(args) {
                guard(() => {
                    if (!invoked) throw new Error('Native resume already active before experiment');
                    if (chainIndex === 0 && rva === chain[0]) callbackThread = this.threadId;
                    if (this.threadId !== callbackThread || chain[chainIndex] !== rva) {
                        throw new Error('Unexpected native resume order or thread');
                    }
                    if (rva === 0x103f2c0 && (args[2].toUInt32() & 255) !== 1) {
                        throw new Error('Run initialization is not the checkpoint branch');
                    }
                    if (rva === 0x1bdf840 && (args[0].toString() !== configuration.task ||
                        args[2].toUInt32() !== 10 || (args[3].toUInt32() & 255) !== 1)) {
                        throw new Error('Native request is not the same preparation task');
                    }
                    chainIndex++;
                    publish('resume-native-enter', { rva, threadId: this.threadId, chainIndex });
                    if (chainIndex === chain.length) publish('resume-chain-observed', {
                        inGameAcceptance: false, retainedPreparation: false });
                });
            }
        }));
    }
    listeners.push(Interceptor.attach(image.base.add(0xff6920), {
        onEnter(args) {
            guard(() => {
                this.observed = true;
                this.stateObject = args[0];
                this.owner = args[0].add(8).readPointer();
                activeUpdates++;
                this.counted = true;
                if (activeUpdates !== 1) throw new Error('Concurrent RootGame updates');
            });
        },
        onLeave() {
            if (this.counted) activeUpdates--;
            if (!this.observed) return;
            guard(() => {
                if (invoked) return;
                if (!this.stateObject.add(8).readPointer().equals(this.owner) ||
                    this.owner.toString() !== configuration.owner ||
                    this.stateObject.toString() !== configuration.stateObject ||
                    this.returnAddress.toString() !== configuration.caller ||
                    !this.returnAddress.equals(image.base.add(0x133ec36))) {
                    throw new Error('Approved idle context changed');
                }
                if (++ticks < 30) return;
                if (!rootOwner().equals(this.owner)) throw new Error('RootGame singleton mismatch');
                if (image.base.add(0x4161f58).readU8() !== 1) throw new Error('Not Rogue mode');
                const task = currentTask(image.base.add(0x9334770), 0, 0);
                if (task.isNull() || task.toString() !== configuration.task) throw new Error('Task changed');
                const descriptor = task.add(0x120).readPointer();
                if (descriptor.isNull() || descriptor.add(0x6c).readS32() !== 2) {
                    throw new Error('Task is no longer preparation-shaped');
                }
                const manager = image.base.add(0x415ac90);
                if (manager.add(0x72c8).readU8() !== 1 || manager.add(0x1528).readS32() <= 0) {
                    throw new Error('No initialized checkpointed run');
                }
                invoked = true;
                invocationThread = this.threadId;
                publish('resume-schedule', { owner: this.owner.toString(), task: task.toString(),
                    threadId: invocationThread, retainedPreparation: false });
                schedule(this.owner, image.base.add(0xfd7870), 1);
                publish('resume-schedule-returned');
            });
        }
    }));
    Interceptor.flush();
    publish('installed', { mode: configuration.mode });
}

rpc.exports = { stop() { stop('host-stop'); return { invoked, ticks, finished }; } };
try {
    install();
    setTimeout(() => stop('deadline'), configuration.duration * 1000);
} catch (error) { stop(`install-error: ${error.message}`); }
