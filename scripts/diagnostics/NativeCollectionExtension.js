'use strict';

globalThis.createNativeCollection = function (configuration, image, publish) {
    const operations = [
        ['store-hub-fields', 0x1048960], ['force-remember', 0x10434c0],
        ['remember-checkpoint', 0x1200ac0], ['copy-checkpoint', 0x11f9300],
        ['save-apply', 0x120af40], ['save-buffer-copy', 0x120d090],
        ['save-json-root', 0x120dac0], ['save-start', 0x1b7fc60],
        ['player-construct', 0xd95530], ['player-destruct', 0xd966f0],
        ['player-initialize', 0xdacc20], ['player-draw-initialize', 0xdafea0],
        ['player-inventory-initialize', 0xdaf950], ['player-spawn', 0xdc1780],
        ['current-task-query', 0x1b6ea60], ['checkpoint-selector-query', 0x105e930]
    ];
    const allocations = [
        ['gameplay', 0x35f9f40, 0x6790, 3], ['records', 0x35f9f48, 0x26930, 3],
        ['body-manager', 0x35f9f50, 0xbc10, 3], ['clocks', 0x35f9f58, 0x238, 3],
        ['permadeath-clock', 0x35f9f60, 0x238, 1], ['tasks', 0x35f9f68, 0xaa08, 3],
        ['stats', 0x35f9f70, 0x6708, 3], ['chapter', 0x35f9f78, 0xd7d10, 1],
        ['player-killed', 0x35f9f80, 0x2080, 1], ['npc-killed', 0x35f9f88, 0x1f70, 1]
    ];
    const snapshotOperations = new Set(['menu-exit', 'run-teardown', 'run-reset',
        'run-initialize', 'init-run-checkpoint', 'store-hub-fields', 'force-remember',
        'remember-checkpoint', 'copy-checkpoint', 'update-run-checkpoint', 'save-apply']);
    const applying = new Map();
    const pending = new Map();
    const objectGenerations = new Map();
    const selections = new Map();
    let lastTask = null;
    let payloadId = 0;
    let totalBytes = 0;
    let pendingBytes = 0;
    let objectGeneration = 0;
    let active = true;
    let acknowledgement = null;
    let transferError = null;
    const encoder = createCollectionEncoder();

    function exact(address, size) {
        const numeric = Number(address.toString());
        if (!Number.isSafeInteger(size) || size <= 0 || size > 16 * 1024 * 1024 ||
            numeric < 0x10000 || numeric + size >= 0x800000000000) {
            throw new Error('Invalid collection memory range');
        }
        const bytes = address.readByteArray(size);
        if (bytes === null || bytes.byteLength !== size) throw new Error('Short collection read');
        return bytes;
    }

    function emitPayload(stage, callId, regions) {
        const available = regions.filter(region => region.bytes !== undefined);
        const size = available.reduce((total, region) => total + region.bytes.byteLength, 0);
        if (size === 0 || size > 32 * 1024 * 1024) throw new Error('Invalid collection bundle size');
        if (pendingBytes + size > 64 * 1024 * 1024) {
            const deadline = Date.now() + 5000;
            publish('collection-backpressure', { pendingBytes, requestedBytes: size, phase: 'waiting' });
            while (pendingBytes + size > 64 * 1024 * 1024) {
                if (transferError !== null) throw new Error(transferError);
                if (Date.now() >= deadline) throw new Error('Collection acknowledgement timed out');
                acknowledgement.wait();
            }
            publish('collection-backpressure', { pendingBytes, requestedBytes: size, phase: 'resumed' });
        }
        if (transferError !== null) throw new Error(transferError);
        const encoded = encoder.encode(regions);
        const wireSize = encoded.wire.byteLength;
        if (!active || totalBytes + wireSize > 1024 * 1024 * 1024) {
            throw new Error('Collection stopped or cumulative transfer budget exceeded');
        }
        const identifier = ++payloadId;
        pending.set(identifier, wireSize);
        pendingBytes += wireSize;
        totalBytes += wireSize;
        publish('collection-payload', { payloadId: identifier, ...encoded.fields,
            stage, callId, coherentSnapshot: false, restorable: false }, encoded.wire);
    }

    function region(name, address, size) {
        return { name, address: address.toString(), size, bytes: exact(address, size) };
    }

    function checkpointRegions() {
        const regions = [];
        for (const [name, rva, size, count] of allocations) {
            const address = image.base.add(rva).readPointer();
            if (address.isNull()) {
                regions.push({ name, status: 'unallocated', slots: count });
                continue;
            }
            for (let slot = 0; slot < count; slot++) {
                regions.push(region(`${name}-${slot}`, address.add(slot * size), size));
            }
        }
        regions.push(region('run-observed', image.base.add(0x415ac90), 0x72d0));
        return regions;
    }

    function snapshot(stage, callId) {
        emitPayload(stage, callId, checkpointRegions());
    }

    function stableMark(name) {
        const first = checkpointRegions();
        const second = checkpointRegions();
        const equal = first.length === second.length && first.every((before, index) => {
            const after = second[index];
            if (before.address !== after.address || before.size !== after.size || before.status !== after.status) return false;
            if (before.bytes === undefined) return true;
            const bytes = new Uint8Array(before.bytes);
            const other = new Uint8Array(after.bytes);
            return bytes.length === other.length && bytes.every((value, offset) => value === other[offset]);
        });
        emitPayload(`mark:${name}:first`, null, first);
        emitPayload(`mark:${name}:second`, null, second);
        const complete = first.length === 23 && first.every(item => item.status !== 'unallocated');
        publish('collection-mark', { name, twoPassEqual: equal, knownRegionsComplete: complete,
            coherentSnapshot: false, restorable: false });
        return { twoPassEqual: equal, knownRegionsComplete: complete };
    }

    function rootPayload(stage, callId, root) {
        const header = region('json-root', root, 0x40);
        const arena = root.add(0x30).readPointer();
        const size = root.add(0x38).readU32();
        emitPayload(stage, callId, [header, region('json-declared-arena', arena, size)]);
    }

    function objectDetails(address) {
        return { address: address.toString(), observedGeneration: objectGenerations.get(address.toString()) ?? null,
            generationBasis: 'constructor-address-observation-only', lifetimeVerified: false,
            processId94: address.add(0x94).readU32(), vtable: address.readPointer().toString(),
            field1b0: address.add(0x1b0).readPointer().toString(),
            field2c00: address.add(0x2c00).readPointer().toString(),
            field5a60: address.add(0x5a60).readPointer().toString(), animationAcceptance: false };
    }

    function enter(operation, invocation, args) {
        const detail = {};
        if (operation === 'save-apply') {
            applying.set(invocation.threadId, (applying.get(invocation.threadId) ?? 0) + 1);
            detail.saveData = args[1].toString();
            detail.restoreKind = args[3].toInt32();
            const descriptor = args[1].readPointer();
            detail.buffer = descriptor.add(8).readPointer().toString();
            emitPayload('save-apply:input-header', invocation.callId,
                [region('save-data-descriptor', descriptor, 0x10),
                    region('save-buffer-prefix', ptr(detail.buffer), 0x414)]);
        }
        if (operation === 'save-buffer-copy') {
            invocation.copySource = args[2];
            invocation.copyDestination = args[1];
            invocation.copyLength = Number(args[3].toString());
            detail.source = args[2].toString();
            detail.destination = args[1].toString();
            detail.length = invocation.copyLength;
            detail.direction = args[4].toInt32();
            detail.stage = args[5].toInt32();
            emitPayload('save-buffer-copy:source-before', invocation.callId,
                [region('source', args[2], invocation.copyLength)]);
        }
        if (operation === 'save-json-root') {
            invocation.captureRoot = (applying.get(invocation.threadId) ?? 0) > 0;
            detail.withinApply = invocation.captureRoot;
            detail.saveData = args[1].toString();
        }
        if (operation === 'save-start') {
            detail.mode = args[1].toInt32();
            detail.slot = args[3].toInt32();
        }
        if (operation === 'copy-checkpoint') {
            detail.sourceSlot = args[1].toInt32();
            detail.destinationSlot = args[2].toInt32();
        }
        if (operation.startsWith('player-') && operation !== 'player-spawn') {
            invocation.player = args[0];
            if (operation !== 'player-construct') {
                detail.player = objectDetails(args[0]);
                emitPayload(`${operation}:before`, invocation.callId, [region('player-observed', args[0], 0x5d80)]);
            }
            if (operation === 'player-destruct') objectGenerations.delete(args[0].toString());
        }
        if (operation === 'player-spawn') {
            invocation.spawnHandle = args[0];
            detail.character = args[1].toInt32();
            detail.checkpoint = args[5].toString();
            if (!args[5].isNull()) {
                emitPayload('player-spawn:checkpoint-input', invocation.callId,
                    [region('player-checkpoint', args[5], 0x2080)]);
            }
        }
        if (snapshotOperations.has(operation)) snapshot(`${operation}:enter`, invocation.callId);
        return detail;
    }

    function leave(operation, invocation, result) {
        const detail = {};
        if (operation === 'save-json-root' && invocation.captureRoot) {
            detail.root = result.toString();
            rootPayload('save-json-root:consumed', invocation.callId, result);
        }
        if (operation === 'save-buffer-copy') {
            emitPayload('save-buffer-copy:after', invocation.callId,
                [region('source-after', invocation.copySource, invocation.copyLength),
                    region('destination-after', invocation.copyDestination, invocation.copyLength)]);
        }
        if (operation === 'save-apply') {
            applying.set(invocation.threadId, applying.get(invocation.threadId) - 1);
        }
        if (operation === 'player-construct') {
            if (objectGenerations.size >= 256) throw new Error('Player generation budget exceeded');
            objectGenerations.set(invocation.player.toString(), ++objectGeneration);
        }
        if (invocation.player && operation !== 'player-destruct') {
            detail.player = objectDetails(invocation.player);
            emitPayload(`${operation}:after`, invocation.callId,
                [region('player-observed', invocation.player, 0x5d80)]);
        }
        if (operation === 'player-spawn') {
            emitPayload('player-spawn:result-handle', invocation.callId,
                [region('process-handle', invocation.spawnHandle, 0x10)]);
        }
        if (snapshotOperations.has(operation)) snapshot(`${operation}:leave`, invocation.callId);
        return detail;
    }

    function acknowledge(message) {
        if (typeof message.error === 'string') transferError = `Collection transfer failed: ${message.error}`;
        const size = pending.get(message.payloadId);
        if (size !== undefined) {
            pending.delete(message.payloadId);
            pendingBytes -= size;
        }
        if (active) acknowledgement = recv('collection-ack', acknowledge);
    }

    return { operations, enter, leave,
        trace(operation, threadId) {
            return operation !== 'save-json-root' || (applying.get(threadId) ?? 0) > 0;
        },
        isQuery(operation) { return operation.endsWith('-query'); },
        queryEnter(operation, invocation, args) {
            invocation.relevant = operation === 'checkpoint-selector-query' ||
                (args[0].equals(image.base.add(0x9334770)) && (args[1].toUInt32() & 255) === 0 &&
                    (args[2].toUInt32() & 255) === 0);
        },
        queryLeave(operation, invocation, result) {
            if (!invocation.relevant) return;
            if (operation === 'checkpoint-selector-query') {
                const caller = invocation.returnAddress.toString();
                const slot = result.toInt32();
                if (selections.get(caller) === slot) return;
                if (selections.size >= 256 && !selections.has(caller)) throw new Error('Selector caller budget exceeded');
                selections.set(caller, slot);
                publish('collection-selection', { caller, slot });
                return;
            }
            const descriptor = result.isNull() ? null : result.add(0x120).readPointer();
            const task = { address: result.toString(),
                word: descriptor === null || descriptor.isNull() ? null : descriptor.add(0x6c).readS32(),
                field1e0: result.isNull() ? null : result.add(0x1e0).readS32() };
            const key = JSON.stringify(task);
            if (key === lastTask) return;
            lastTask = key;
            publish('collection-task', { task, caller: invocation.returnAddress.toString(),
                playableHideoutClaim: false });
            snapshot('task-change', null);
        },
        start() {
            acknowledgement = recv('collection-ack', acknowledge);
            publish('collection-player-policy', { sampling: 'native-callback-boundaries-only',
                cachedAddressSampling: false, lifetimeVerified: false, animationAcceptance: false });
            snapshot('installed', null);
        },
        stop() { active = false; return { payloadCount: payloadId, totalBytes, pendingBytes }; },
        mark(name) {
            if (!active || !/^[a-z][a-z0-9-]{0,47}$/.test(name)) throw new Error('Invalid collection mark');
            return { ...stableMark(name), payloadCount: payloadId, totalBytes, pendingBytes };
        }
    };
};
