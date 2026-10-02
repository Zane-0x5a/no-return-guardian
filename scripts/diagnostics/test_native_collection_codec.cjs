const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');
const { requires } = require('./local_evidence.cjs');

function encoder() {
    const context = { Checksum: { compute: (algorithm, data) =>
        crypto.createHash(algorithm).update(Buffer.from(data)).digest('hex') } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'NativeCollectionCodec.js'), 'utf8'), context);
    return context.createCollectionEncoder();
}

test('changed bytes at an unchanged address survive chunk deduplication', () => {
    const codec = encoder();
    const data = Buffer.alloc(128 * 1024, 7);
    const regions = () => [{name: 'input', address: '0x10000', size: data.length,
        bytes: Uint8Array.from(data).buffer}];
    const first = codec.encode(regions());
    const equal = codec.encode(regions());
    data[70000] = 8;
    const changed = codec.encode(regions());
    assert.equal(first.wire.byteLength, 128 * 1024);
    assert.equal(equal.wire.byteLength, 0);
    assert.equal(changed.wire.byteLength, 64 * 1024);
    assert.equal(changed.fields.byteLength, data.length);
});

test('deduplication preserves each region address and allocation status', () => {
    const codec = encoder();
    codec.encode([{name:'source', address:'0x10000', size:4, bytes:new Uint8Array([1,2,3,4]).buffer}]);
    const encoded = codec.encode([{name:'destination', address:'0x20000', size:4,
        bytes:new Uint8Array([1,2,3,4]).buffer}, {name:'unavailable', status:'unallocated', slots:3}]);
    assert.equal(encoded.wire.byteLength, 0);
    assert.equal(encoded.fields.regions[0].address, '0x20000');
    assert.equal(encoded.fields.regions[1].status, 'unallocated');
});

test('bounded dictionary evicts old chunks and retransmits them', () => {
    const codec = encoder();
    const region = number => [{name:'input', address:'0x10000', size:4,
        bytes:Uint32Array.from([number]).buffer}];
    for (let index = 0; index < 5000; index++) codec.encode(region(index));
    assert.ok(codec.entries() <= 4096);
    assert.equal(codec.encode(region(0)).wire.byteLength, 4);
});

test('all 1022 real failed-session payloads reconstruct byte for byte within the transfer budget', requires('native-runtime-20260912/collection-observe-02.payloads/manifest.json'), () => {
    const root = path.join(__dirname, '../../artifacts/native-runtime-20260912/collection-observe-02.payloads');
    const manifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));
    const codec = encoder();
    const retained = new Map();
    let transferred = 0;
    let decoded = 0;
    for (const item of manifest.payloads) {
        const original = fs.readFileSync(path.join(root, item.file));
        assert.equal(crypto.createHash('sha256').update(original).digest('hex'), item.sha256);
        const regions = item.event.regions.map(region => region.status === 'unallocated' ? region :
            {...region, bytes:Uint8Array.from(original.subarray(region.offset, region.offset + region.size)).buffer});
        const encoded = codec.encode(regions);
        const wire = Buffer.from(encoded.wire);
        transferred += wire.length;
        const chunks = encoded.fields.chunks.map(chunk => {
            if (chunk.wireOffset !== undefined) {
                const bytes = wire.subarray(chunk.wireOffset, chunk.wireOffset + chunk.size);
                assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'), chunk.sha256);
                retained.set(chunk.sha256, Buffer.from(bytes));
            }
            const bytes = retained.get(chunk.sha256);
            assert.ok(bytes, 'missing chunk reference');
            return bytes;
        });
        assert.deepEqual(Buffer.concat(chunks), original, `payload ${item.event.payloadId}`);
        decoded += original.length;
    }
    assert.equal(decoded, manifest.bytesReceived);
    assert.ok(transferred < 64 * 1024 * 1024, String(transferred));
    process.stdout.write(JSON.stringify({payloads:manifest.payloads.length, decoded, transferred}) + '\n');
});
