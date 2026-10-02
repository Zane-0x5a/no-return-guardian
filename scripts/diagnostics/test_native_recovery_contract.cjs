const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { requires } = require('./local_evidence.cjs');
const { createRecoveryContract } = require('./NativeRecoveryContract.js');

function state(initialized = 0, selector = 2, rogue = 0, worker = 0) {
    return { runInitialized: initialized, runSelector: selector, rogue, workerStates: [0, 0, worker] };
}

function transcript() {
    return [
        ['schedule', { state: state() }],
        ['request', { state: state(), mode: 2, slot: 20 }],
        ['source', { state: state(0, 2, 0, 4), sourceVerified: true, metadataVerified: true, requestSucceeded: true }],
        ['loaded', { bytesVerified: true }],
        ['apply-enter', { state: state(0, 2, 0, 4), threadId: 10 }],
        ['apply-leave', { state: state(0, 1, 0, 4), success: true, threadId: 10 }],
        ['promoted', { bytesVerified: true, threadId: 20 }],
        ['callback', { state: state(0, 1), threadId: 30 }],
        ['teardown', { state: state(0, 1), threadId: 30 }],
        ['initialize-enter', { state: state(0, 1), threadId: 30, checkpoint: true }],
        ['initialize', { state: state(1, 1, 1), threadId: 30, checkpoint: true }],
        ['queued', { threadId: 30, kind: 1, parameter: 10 }],
        ['callback-return', { threadId: 30 }],
        ['play', { threadId: 40, parameter: 10 }],
        ['rebuilt', { state: state(1, 1, 1), taskWord: 2, spawnObserved: true }]
    ];
}

function run(events) {
    const contract = createRecoveryContract();
    for (const [name, fields] of events) contract.event(name, fields);
    return contract;
}

test('recorded 21:54 successful native apply binds selector zero instead of terminating recovery', requires('native-runtime-20260912/native-recovery-user-2154-repro.jsonl'), () => {
    const stem = path.join(__dirname, '../../artifacts/native-runtime-20260912/native-recovery-user-2154-repro');
    const records = fs.readFileSync(stem + '.jsonl', 'utf8').trim().split(/\r?\n/).map(JSON.parse);
    const fatal = JSON.parse(fs.readFileSync(stem + '.fatal.json', 'utf8'));
    const events = records.filter(record => record.kind === 'recovery-transition')
        .map(record => [record.transition, record.observed]);
    assert.equal(fatal.pendingTransition.name, 'apply-leave');
    assert.equal(fatal.pendingTransition.fields.success, true);
    assert.equal(fatal.pendingTransition.fields.state.runSelector, 0);
    events.push([fatal.pendingTransition.name, fatal.pendingTransition.fields]);
    const contract = run(events);
    assert.equal(contract.snapshot().phase, 'applied');
    assert.equal(contract.snapshot().nativeSelector, 0);
    assert.equal(contract.snapshot().inGameAcceptance, false);
});

function selectedTranscript(selector) {
    const events = transcript();
    for (const [index, [, fields]] of events.entries()) {
        if (index >= 5 && fields.state) fields.state.runSelector = selector;
    }
    events.splice(10, 0,
        ['initialization-save', { state: state(1, selector, 1), threadId: 30, mode: 1, slot: 20 }],
        ['initialization-save-return', { state: state(1, selector, 1), threadId: 30, success: true }]);
    return events;
}

test('both native selectors remain bound across callback, saves and new scene, without leaking across attempts', () => {
    for (const selector of [0, 1, 0]) {
        const contract = run(selectedTranscript(selector));
        assert.equal(contract.snapshot().phase, 'awaiting-player-acceptance');
        assert.equal(contract.snapshot().nativeSelector, selector);
        assert.equal(contract.snapshot().inGameAcceptance, false);
    }
    assert.equal(createRecoveryContract().snapshot().nativeSelector, null);
});

test('unknown native selectors and later changes to another otherwise valid selector fail closed', () => {
    for (const invalid of [2, -1, 3, 0.5, '0', false, null, undefined]) {
        const events = selectedTranscript(0);
        events[5][1].state.runSelector = invalid;
        assert.throws(() => run(events), `apply selector ${invalid}`);
    }
    for (const selector of [0, 1]) {
        for (const [index, [name, fields]] of selectedTranscript(selector).entries()) {
            if (index <= 5 || !fields.state) continue;
            const events = selectedTranscript(selector);
            events[index][1].state.runSelector = 1 - selector;
            assert.throws(() => run(events), `${selector} drift at ${name}`);
        }
        const events = selectedTranscript(selector);
        events[5][1].success = false;
        assert.throws(() => run(events));
        events[5][1].success = true;
        events[0][1].state.runSelector = selector;
        assert.throws(() => run(events));
    }
});

test('unloaded source, native apply, cache, callback and scene are distinct ordered phases', () => {
    const contract = run(transcript());
    assert.equal(contract.snapshot().phase, 'awaiting-player-acceptance');
    assert.equal(contract.snapshot().inGameAcceptance, false);
    assert.throws(() => contract.event('schedule', { state: state() }));
});

test('every missing or duplicate transition is rejected without a success claim', () => {
    const events = transcript();
    for (let index = 0; index < events.length - 1; index++) {
        assert.throws(() => run(events.filter((event, position) => position !== index)), `missing ${index}`);
        const duplicate = events.slice();
        duplicate.splice(index, 0, duplicate[index]);
        assert.throws(() => run(duplicate), `duplicate ${index}`);
    }
});

test('death results, existing hideout, workers busy and malformed state cannot schedule', () => {
    for (const invalid of [state(1, 1, 1), state(0, 2, 0, 4), state(0, 1), null,
        { ...state(), workerStates: [] }, { ...state(), workerStates: [0, -1, 0] }]) {
        const contract = createRecoveryContract();
        assert.throws(() => contract.event('schedule', { state: invalid }));
        assert.equal(contract.snapshot().phase, 'failed');
        assert.throws(() => contract.event('schedule', { state: state() }));
    }
});

test('wrong request, source, metadata, bytes, apply, callback thread and scene all fail closed', () => {
    for (const [index, change] of [[1, { mode: 1 }], [1, { slot: 21 }], [2, { metadataVerified: false }],
        [2, { sourceVerified: false }], [2, { requestSucceeded: false }], [3, { bytesVerified: false }],
        [4, { state: state(1, 1, 1) }], [5, { success: false }], [6, { bytesVerified: false }],
        [7, { state: state(0, 1, 0, 4) }], [8, { threadId: 31 }], [9, { checkpoint: false }],
        [10, { threadId: 31 }], [10, { state: state(1, 1, 1, 4) }],
        [11, { kind: 2 }], [11, { parameter: 1 }], [12, { threadId: 31 }],
        [13, { parameter: 1 }], [14, { taskWord: 1 }], [14, { spawnObserved: false }]]) {
        const events = transcript();
        events[index][1] = { ...events[index][1], ...change };
        assert.throws(() => run(events), `${index} ${JSON.stringify(change)}`);
    }
});

test('both captured normal rebuilds fit the contract while allowing worker migration', requires('native-runtime-20260912/collection-observe-02.jsonl'), () => {
    const records = fs.readFileSync(path.join(__dirname,
        '../../artifacts/native-runtime-20260912/collection-observe-02.jsonl'), 'utf8')
        .trim().split(/\r?\n/).map(line => JSON.parse(line));
    const find = (kind, callId) => records.find(record => record.kind === kind && record.callId === callId);
    for (const calls of [[157, 171, 189, 197, 199, 205, 217, 223, 256],
        [638, 652, 670, 681, 683, 689, 701, 707, 744]]) {
        const [request, apply, promote, callback, teardown, initialize, queue, play, spawn] = calls;
        const events = transcript();
        events[0][1].state = find('load-enter', request).state;
        events[1][1].state = find('load-enter', request).state;
        events[4][1].state = find('load-enter', apply).state;
        events[5][1].state = find('load-leave', apply).state;
        events[6][1].threadId = find('load-enter', promote).threadId;
        for (const [index, callId, kind] of [[7, callback, 'load-enter'], [8, teardown, 'load-enter'],
            [9, initialize, 'load-enter'], [10, initialize, 'load-leave'], [11, queue, 'load-enter'],
            [12, callback, 'load-leave'], [13, play, 'load-enter']]) {
            const record = find(kind, callId);
            events[index][1].threadId = record.threadId;
            events[index][1].state = record.state;
        }
        assert.ok(find('load-enter', spawn).sequence > find('load-enter', play).sequence);
        assert.notEqual(events[6][1].threadId, events[7][1].threadId);
        assert.equal(run(events).snapshot().phase, 'awaiting-player-acceptance');
    }
});

test('initialization saves are paired, native-owned and optional, with idle workers required at completion', () => {
    const prefix = transcript().slice(0, 10);
    const save = ['initialization-save', { state: state(1, 1, 1), threadId: 30, mode: 1, slot: 20 }];
    const accepted = ['initialization-save-return', { state: state(1, 1, 1, 4), threadId: 30, success: true }];
    const initialized = ['initialize', { state: state(1, 1, 1, 4), threadId: 30 }];
    assert.equal(run([...prefix, save, accepted, initialized, ...transcript().slice(11)]).snapshot().phase,
        'awaiting-player-acceptance');
    assert.equal(run([...prefix, save, accepted, save, accepted, initialized, ...transcript().slice(11)]).snapshot().phase,
        'awaiting-player-acceptance');
    for (const invalid of [
        [...prefix, save, initialized],
        [...prefix, accepted],
        [...prefix, save, save],
        [...prefix, save, accepted, accepted],
        [...prefix, save, ['initialization-save-return', { ...accepted[1], threadId: 31 }]],
        [...prefix, save, ['initialization-save-return', { ...accepted[1], success: false }]],
        [...prefix, save, ['initialization-save-return', { ...accepted[1], state: {
            ...state(1, 1, 1), workerStates: [0, 4, 0] } }]],
        [...prefix.slice(0, -1), save],
        [...prefix, ['initialize', { state: state(1, 1, 1), threadId: 30 }], save],
        [...prefix, save, accepted, initialized, ...transcript().slice(11, 14),
            ['rebuilt', { state: state(1, 1, 1, 4), taskWord: 2, spawnObserved: true }]]
    ]) assert.throws(() => run(invalid));
});

test('complete repaired game transcript replays both save domains and rejects missing save evidence', requires('native-runtime-20260912/native-recovery-user-2035-fixed-02.jsonl'), () => {
    const records = fs.readFileSync(path.join(__dirname,
        '../../artifacts/native-runtime-20260912/native-recovery-user-2035-fixed-02.jsonl'), 'utf8')
        .trim().split(/\r?\n/).map(line => JSON.parse(line));
    const events = records.filter(record => record.kind === 'recovery-transition')
        .map(record => [record.transition, record.observed]);
    assert.deepEqual(events.filter(([name]) => name === 'initialization-save')
        .map(([, fields]) => [fields.slot, fields.requestStory]), [[20, 2], [21, 0]]);
    assert.equal(run(events).snapshot().phase, 'awaiting-player-acceptance');
    for (const [index, [name]] of events.entries()) {
        if (name.startsWith('initialization-save')) {
            assert.throws(() => run(events.filter((event, position) => position !== index)));
        }
    }
});
