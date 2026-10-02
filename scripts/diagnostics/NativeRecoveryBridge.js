'use strict';

// Profile stats that ss-rogue-run's start-new-run initializes and its on-resume reads back; see
// NativeRecoverySource.RUN_PROFILE_STATS. The in-process load replaces only the run save, so they are
// restored to the protected values while the run is unloaded, before the run scripts start again.
const RECOVERY_RUN_STATS = ['ca79be3ddf469b1a', '8dd8bbea1823e133', 'e0a765d96d4dd778', '34ae6a71b265a278',
    '524ef3c2f60dc853', '6172419d00e62123', '966ac5e93688c099', '966ac2e93688bb80',
    'c34a84af6ff26f6f', 'db3441ab83811bde'];

function createNativeRecoveryBridge(configuration) {
    const image = Process.mainModule;
    const target = configuration.recoverySource;
    const contract = globalThis.createRecoveryContract();
    const listeners = [];
    const options = { abi: 'win64', exceptions: 'propagate', scheduling: 'exclusive' };
    let finished = false;
    let scheduled = false;
    let ticks = 0;
    let readyTicks = 0;
    let owner = null;
    let sourceMemory = null;
    let sourceContainer = null;
    let pointerFields = null;
    let temporary = null;
    let cache = null;
    let sourceThread = null;
    let rawSourceHash = null;
    let spawned = false;
    let terminate = null;
    let sequence = 0;
    let failing = false;
    let boundary = null;
    let pendingTransition = null;
    let statService = null;
    let statServiceProblem = null;
    let findStat = null;
    let setStat = null;
    let runStats = null;

    function publish(kind, fields = {}) {
        send({ kind, sequence: ++sequence, time: Date.now(), ...contract.snapshot(),
            snapshotId: target.snapshotId, ...fields, inGameAcceptance: false });
    }
    function stop(reason = 'host-stop') {
        if (finished) return contract.snapshot();
        if (scheduled && contract.snapshot().phase !== 'awaiting-player-acceptance') {
            fail(new Error('Cannot detach an unfinished native recovery'));
            return contract.snapshot();
        }
        finished = true;
        for (const listener of listeners) listener.detach();
        Interceptor.flush();
        publish('stopped', { reason });
        return contract.snapshot();
    }
    function fail(error) {
        if (finished || failing) return;
        failing = true;
        const evidence = { kind: 'recovery-fatal', time: Date.now(), pid: configuration.pid,
            birth: configuration.birth, base: configuration.base, snapshotId: target.snapshotId,
            ...contract.snapshot(), reason: error.message, scheduled, boundary,
            pendingTransition, inGameAcceptance: false };
        try { evidence.observedState = state(); } catch (stateError) { evidence.stateReadError = stateError.message; }
        if (configuration.failurePath) {
            try {
                const journal = new File(configuration.failurePath, 'w');
                try { journal.write(JSON.stringify(evidence)); journal.flush(); } finally { journal.close(); }
            } catch (journalError) { evidence.journalError = journalError.message; }
        }
        publish('recovery-fatal', evidence);
        if (scheduled) {
            terminate(ptr(-1), 5);
            throw new Error('Emergency process termination returned unexpectedly');
        }
        stop('recovery-error: ' + error.message);
    }
    function guard(action) {
        if (finished || failing) return;
        try { action(); } catch (error) { fail(error); }
    }
    function state() {
        const manager = image.base.add(0x9341660);
        const run = image.base.add(0x415ac90);
        const initialized = run.add(0x72c8).readU8();
        return { runInitialized: initialized, runSelector: run.add(0x520).readS32(),
            rogue: initialized, story: image.base.add(0x9398730).readS32(),
            workerStates: [0x58, 0x228, 0x3f8].map(offset => manager.add(offset).readS32()) };
    }
    function transition(name, fields = {}) {
        const observed = { state: state(), ...fields };
        pendingTransition = { name, fields: observed };
        contract.event(name, observed);
        publish('recovery-transition', { transition: name, observed });
        pendingTransition = null;
    }
    function hex(data) { return Array.from(new Uint8Array(data), value => value.toString(16).padStart(2, '0')).join(''); }
    function hash(data) { return Checksum.compute('sha256', data); }
    // Same entry layout stat-value? reads: 24-byte records at service+0x30 with the int32 value at +8.
    function statValue(index) { return statService.add(0x30).readPointer().add(index * 24 + 8).readS32(); }
    // The run save is still the accepted recovery, so a stat that cannot be restored is reported rather than
    // withholding the hideout; earlier writes are reverted so the stats stay one consistent generation.
    function restoreRunStats() {
        const written = [];
        let stats = [];
        try {
            if (statService === null) throw new Error(statServiceProblem);
            stats = target.runStats.map(stat => {
                const key = uint64('0x' + stat.sid);
                const index = findStat(statService, key);
                if (index < 0) throw new Error('Profile run stat is not registered');
                return { sid: stat.sid, value: stat.value, key, index, before: statValue(index) };
            });
            for (const stat of stats) {
                if (stat.before === stat.value) continue;
                written.push(stat);
                if ((setStat(statService, stat.value, stat.key, 0) & 255) !== 1 || statValue(stat.index) !== stat.value) {
                    throw new Error('Native profile stat write was rejected');
                }
            }
        } catch (error) {
            for (const stat of written.slice().reverse()) {
                try { setStat(statService, stat.before, stat.key, 0); } catch (revertError) { /* checked below */ }
            }
            const reverted = written.every(stat => statValue(stat.index) === stat.before);
            runStats = [];
            publish('run-stats-unrestored', { observed: state(), reason: error.message, attempted: written.length, reverted });
            return;
        }
        runStats = stats;
        publish('run-stats-restored', { observed: state(), changed: written.length,
            stats: stats.map(({ sid, before, value }) => ({ sid, before, after: value })) });
    }
    function verifiedBody(address) {
        const data = address.readByteArray(target.capacity);
        const view = new DataView(data);
        const basis = BigInt(address.add(0x418).toString());
        for (const field of pointerFields) {
            const value = view.getBigUint64(0x418 + field, true);
            if (value < basis || value >= basis + BigInt(target.declaredSize - 0x418)) {
                throw new Error('Native target has an external typed pointer');
            }
            view.setBigUint64(0x418 + field, value - basis, true);
        }
        if (hash(data) !== target.bodySha256) throw new Error('Native body differs from the protected preparation');
        return true;
    }
    function hook(rva, onEnter, onLeave) {
        const entry = function(args) { guard(() => {
            boundary = { rva: rva.toString(16), direction: 'enter', threadId: this.threadId,
                caller: this.returnAddress.toString(), phase: contract.snapshot().phase };
            if (rva === 0x103f2c0 || rva === 0x1b7fc60) {
                boundary.arguments = [0, 1, 2, 3].map(index => args[index].toString());
            }
            if (rva === 0x1b7fc60 && !args[2].isNull()) {
                boundary.requestStory = args[2].readS32();
                boundary.profileStory = image.base.add(0x9398790).readS32();
            }
            onEnter.call(this, args);
        }); };
        const callbacks = onLeave ? {
            onEnter: entry,
            onLeave(result) { guard(() => {
                boundary = { rva: rva.toString(16), direction: 'leave', threadId: this.threadId,
                    phase: contract.snapshot().phase, result: result.toString() };
                onLeave.call(this, result);
            }); }
        } : entry;
        listeners.push(Interceptor.attach(image.base.add(rva), callbacks));
    }
    function install(packet) {
        if (configuration.mode !== 'recover-preparation' || Process.arch !== 'x64' ||
            Process.id !== configuration.pid || image.name.toLowerCase() !== 'tlou-ii.exe' ||
            !image.base.equals(ptr(configuration.base))) throw new Error('Recovery process identity mismatch');
        if (target.capacity !== 0x460414 || target.rawSize !== target.capacity + 0x590 ||
            !Number.isInteger(target.pointerCount) || target.pointerCount <= 0 || target.pointerCount > 1000000 ||
            packet.byteLength !== target.rawSize + target.pointerCount * 4 || hash(packet) !== target.packetSha256) {
            throw new Error('Recovery source packet integrity failed');
        }
        pointerFields = new Uint32Array(packet.slice(target.rawSize));
        const packetView = new DataView(packet);
        let previous = -1;
        for (const field of pointerFields) {
            if (field <= previous || field % 8 || field + 8 > target.declaredSize - 0x418) {
                throw new Error('Invalid recovery pointer field table');
            }
            previous = field;
        }
        if (packetView.getUint32(0x594, true) !== target.declaredSize ||
            hash(packet.slice(0x590, target.rawSize)) !== target.bodySha256) {
            throw new Error('Recovery body integrity failed');
        }
        if (!Array.isArray(target.runStats) || target.runStats.length !== RECOVERY_RUN_STATS.length ||
            target.runStats.some((stat, index) => stat?.sid !== RECOVERY_RUN_STATS[index] ||
                !Number.isInteger(stat.value) || stat.value < 0 || stat.value > 0x7fffffff)) {
            throw new Error('Recovery profile run stats are incomplete');
        }
        const required = [0x133ebb0, 0x133ec5e, 0xfe7b10, 0xfe96f0, 0x1b7fc60, 0x1e2f880, 0x120af40,
            0x120d090, 0xfd7870, 0x1042680, 0x103f2c0, 0x1b6e000, 0x1bdeed0, 0xdc1780, 0x1b6ea60,
            0x1c55df0, 0x1c55a3c, 0x1c57bb0];
        for (const rva of required) {
            const fingerprint = configuration.fingerprints.find(record => record.rva === rva);
            if (!fingerprint || fingerprint.bytes.length !== 64 ||
                hex(image.base.add(rva).readByteArray(32)) !== fingerprint.bytes) {
                throw new Error('Recovery live code fingerprint mismatch');
            }
        }
        terminate = new NativeFunction(Module.getGlobalExportByName('TerminateProcess'), 'bool', ['pointer', 'uint'], options);
        const rootOwner = new NativeFunction(image.base.add(0xfe7b10), 'pointer', [], options);
        const schedule = new NativeFunction(image.base.add(0xfe96f0), 'void', ['pointer', 'pointer', 'bool'], options);
        const currentTask = new NativeFunction(image.base.add(0x1b6ea60), 'pointer', ['pointer', 'bool', 'bool'], options);
        owner = rootOwner();
        if (owner.isNull()) throw new Error('Root game is unavailable');
        // Service slot 9 is what set-svar-value and stat-value? resolve. Its constructor stores the base vtable
        // (0x2ab10b) and then the derived one (0x2ab19c); slot 0x20 of both is the setter those natives call.
        // An unrecognized service is never called.
        const service = image.base.add(0x4248b78).readPointer();
        if (service.isNull() || !service.readPointer().equals(image.base.add(0x2af57d8)) ||
            !service.readPointer().add(0x20).readPointer().equals(image.base.add(0x1c57bb0))) {
            statServiceProblem = 'Profile stat service identity mismatch';
        } else {
            statService = service;
            findStat = new NativeFunction(image.base.add(0x1c55df0), 'int', ['pointer', 'uint64'], options);
            setStat = new NativeFunction(image.base.add(0x1c57bb0), 'uint8', ['pointer', 'int', 'uint64', 'uint8'], options);
        }
        sourceMemory = Memory.alloc(target.rawSize);
        sourceMemory.writeByteArray(packet.slice(0, target.rawSize));
        rawSourceHash = hash(packet.slice(0, target.rawSize));
        sourceContainer = Memory.alloc(0x50);
        sourceContainer.writeByteArray(new Uint8Array(0x50));
        sourceContainer.add(0xc).writeU32(2);
        sourceContainer.add(0x40).writePointer(sourceMemory);
        sourceContainer.add(0x48).writeU32(target.rawSize);

        hook(0x1b7fc60, function(args) {
            if (!scheduled) return;
            if (contract.snapshot().phase === 'awaiting-player-acceptance') return;
            if (contract.snapshot().phase === 'playing' || contract.snapshot().phase === 'queued') return;
            if (!args[0].equals(image.base.add(0x9341660))) throw new Error('Unexpected save manager');
            this.initializationSave = contract.snapshot().phase === 'initializing';
            const profileSave = this.initializationSave && args[3].toInt32() === 21;
            const requestStory = profileSave ? image.base.add(0x9398790).readS32() : 2;
            if (args[2].isNull() || args[2].readS32() !== requestStory || state().story !== 2) {
                throw new Error('Native request story does not match its save domain');
            }
            const saveCaller = profileSave ? 0x1b8152f : 0x1b802c3;
            if (this.initializationSave && !this.returnAddress.equals(image.base.add(saveCaller))) {
                throw new Error('Unexpected native initialization save caller');
            }
            transition(this.initializationSave ? 'initialization-save' : 'request', {
                mode: args[1].toInt32(), slot: args[3].toInt32(), threadId: this.threadId, requestStory });
            this.request = !this.initializationSave;
        }, function(result) {
            if (this.initializationSave) transition('initialization-save-return', {
                threadId: this.threadId, success: (result.toUInt32() & 255) === 1 });
            if (this.request && (result.toUInt32() & 255) !== 1) throw new Error('Native load request was rejected');
        });
        hook(0x1e2f880, function(args) {
            if (!scheduled) return;
            if (!this.returnAddress.equals(image.base.add(0x1b82059))) throw new Error('Unexpected native load extraction caller');
            const worker = image.base.add(0x9341660 + 0x3c0);
            const inner = args[0];
            if (!worker.add(8).readPointer().equals(inner) || worker.add(0x48).readS32() !== 2 ||
                worker.add(0x4c).readS32() !== 20 || inner.add(4).readS32() !== 2 ||
                inner.add(0xc).readS32() !== 2 || inner.add(0x14).readS32() !== 20 ||
                inner.add(0xd8).readS32() !== 0 || inner.add(0x38).readU8() !== 0) {
                throw new Error('Native completed request is not the expected successful run load');
            }
            const canonicalDescriptor = worker.add(0x10).readPointer();
            if (args[2].toUInt32() !== target.capacity || canonicalDescriptor.add(4).readU32() !== target.capacity ||
                canonicalDescriptor.readU8() !== 0 || !args[1].equals(image.base.add(0x9341660 + 0x5b0).readPointer())) {
                throw new Error('Native scratch and canonical descriptors do not match');
            }
            if (hex(inner.add(0x80).readByteArray(40)) !== target.metadataHex) {
                throw new Error('Current request carries incompatible auxiliary metadata');
            }
            temporary = args[1];
            cache = canonicalDescriptor.add(8).readPointer();
            if (temporary.isNull() || cache.isNull() || temporary.equals(cache)) throw new Error('Invalid native buffers');
            sourceThread = this.threadId;
            transition('source', { sourceVerified: true, metadataVerified: true, requestSucceeded: true });
            args[0] = sourceContainer;
            this.recoverySource = true;
        }, function(result) {
            if (!this.recoverySource) return;
            if ((result.toUInt32() & 255) !== 1 || hash(sourceMemory.readByteArray(target.rawSize)) !== rawSourceHash) {
                throw new Error('Native extraction failed or changed the serialized source');
            }
            transition('loaded', { bytesVerified: verifiedBody(temporary) });
        });
        hook(0x120af40, function(args) {
            if (!scheduled) return;
            const descriptor = args[1].readPointer();
            if (!descriptor.add(8).readPointer().equals(temporary) || descriptor.add(4).readU32() !== target.capacity ||
                descriptor.readU8() !== 0 || args[3].toInt32() !== 0) throw new Error('Unexpected native apply input');
            verifiedBody(temporary);
            transition('apply-enter');
            this.recoveryApply = true;
        }, function(result) {
            if (this.recoveryApply) transition('apply-leave', { success: (result.toUInt32() & 255) === 1 });
        });
        hook(0x120d090, function(args) {
            if (!scheduled) return;
            const phase = contract.snapshot().phase;
            if (phase !== 'source' && phase !== 'applied') return;
            const direction = args[4].toInt32();
            if (args[5].toInt32() !== -1 || Number(args[3].toString()) !== target.capacity) {
                throw new Error('Unexpected native copy stage or length');
            }
            if (phase === 'source') {
                if (direction !== 2 || this.threadId !== sourceThread || !args[1].equals(temporary) ||
                    !args[2].equals(sourceMemory.add(0x590))) throw new Error('Unexpected target load copy');
            } else {
                if (direction !== 0 || !args[1].equals(cache) || !args[2].equals(temporary) ||
                    !this.returnAddress.equals(image.base.add(0x1be144e))) throw new Error('Unexpected cache promotion');
                verifiedBody(temporary);
                this.promotion = true;
            }
        }, function() {
            if (!this.promotion) return;
            if (hash(cache.readByteArray(target.capacity)) !== hash(temporary.readByteArray(target.capacity))) {
                throw new Error('Native promotion source mutation does not match the canonical cache');
            }
            transition('promoted', { bytesVerified: verifiedBody(cache) });
        });
        hook(0xfd7870, function() {
            if (!scheduled) return;
            verifiedBody(cache);
            transition('callback', { threadId: this.threadId });
            this.recoveryCallback = true;
        }, function() {
            if (this.recoveryCallback) transition('callback-return', { threadId: this.threadId });
        });
        hook(0x1042680, function() {
            if (scheduled) transition('teardown', { threadId: this.threadId });
        });
        hook(0x103f2c0, function(args) {
            this.recoveryInitialize = scheduled;
            if (!this.recoveryInitialize) return;
            if (!this.returnAddress.equals(image.base.add(0xfd7909))) throw new Error('Unexpected native checkpoint initialization caller');
            transition('initialize-enter', { threadId: this.threadId, checkpoint: (args[2].toUInt32() & 255) === 1 });
        }, function() {
            if (this.recoveryInitialize) transition('initialize', { threadId: this.threadId });
        });
        hook(0x1b6e000, function(args) {
            if (contract.snapshot().phase !== 'initialized') return;
            transition('queued', { threadId: this.threadId, kind: args[1].readS32(), parameter: args[1].add(0x18).readS32() });
        });
        hook(0x1bdeed0, function(args) {
            if (contract.snapshot().phase === 'queued') transition('play', { parameter: args[2].toInt32() });
        });
        hook(0xdc1780, function() {
            this.recoverySpawn = contract.snapshot().phase === 'playing';
        }, function() {
            if (this.recoverySpawn) spawned = true;
        });
        hook(0x133ec5e, function() {
            if (!this.context.rbx.equals(owner)) return;
            if (!scheduled) {
                const observed = state();
                if (observed.runInitialized !== 0 || observed.runSelector !== 2 || observed.story !== 2 ||
                    observed.workerStates.some(value => value !== 0) || image.base.add(0x9341660 + 0x5e0).readS32() !== 20) {
                    ticks = 0;
                    return;
                }
                if (++ticks < 30) return;
                if (!rootOwner().equals(owner)) throw new Error('Root game identity changed');
                if (runStats === null) {
                    // A changed stat may request its own profile save, so the idle-menu window starts over.
                    restoreRunStats();
                    ticks = 0;
                    return;
                }
                transition('schedule');
                scheduled = true;
                schedule(owner, image.base.add(0xfd7870), 1);
            } else if (contract.snapshot().phase === 'playing' && spawned) {
                const observed = state();
                if (observed.runSelector !== contract.snapshot().nativeSelector) {
                    throw new Error('Native checkpoint selection changed during scene rebuild');
                }
                if (observed.runInitialized !== 1 || observed.rogue !== 1 ||
                    observed.workerStates.some(value => value !== 0)) { readyTicks = 0; return; }
                const task = currentTask(image.base.add(0x9334770), 0, 0);
                if (task.isNull() || task.add(0x120).readPointer().isNull() ||
                    task.add(0x120).readPointer().add(0x6c).readS32() !== 2) { readyTicks = 0; return; }
                if (++readyTicks < 30) return;
                const held = runStats.length ? runStats.every(stat => statValue(stat.index) === stat.value) : null;
                transition('rebuilt', { taskWord: 2, spawnObserved: true, runStatsHeld: held });
                publish('native-recovery-rebuilt');
                stop('recovery-awaiting-player-acceptance');
            }
        });
        Interceptor.flush();
        publish('installed', { mode: configuration.mode, rawBytes: target.rawSize, diagnosticDumps: 0 });
    }
    return { install: packet => guard(() => install(packet)), stop, snapshot: contract.snapshot };
}

globalThis.createNativeRecoveryBridge = createNativeRecoveryBridge;
if (typeof module !== 'undefined') module.exports = { createNativeRecoveryBridge };
