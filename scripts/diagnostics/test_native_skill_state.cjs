const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, 'NativeSkillState.js'), 'utf8');
const base = 0x140000000;

function fixture() {
    const bytes = new Map();
    function put(at, value, size = 8) {
        let n = BigInt(value);
        for (let i = 0; i < size; i++, n >>= 8n) bytes.set(at + i, Number(n & 255n));
    }
    function text(at, value) { Buffer.from(value + '\0').forEach((b, i) => bytes.set(at + i, b)); }
    function u64(value) {
        const n = BigInt(value);
        return { equals: other => n === BigInt(other.toString()),
            compare: other => n < BigInt(other.toString()) ? -1 : n > BigInt(other.toString()) ? 1 : 0,
            toString: radix => n.toString(radix) };
    }
    function pointer(at) {
        at = Number(at);
        function read(n) { let value = 0n; for (let i = n - 1; i >= 0; i--) value = value * 256n + BigInt(bytes.get(at + i) || 0); return value; }
        return { add: n => pointer(at + n), sub: p => pointer(at - Number(p.toString())),
            compare: p => Math.sign(at - Number(p.toString())), equals: p => at === Number(p.toString()),
            isNull: () => at === 0, toString: () => '0x' + at.toString(16),
            readPointer: () => pointer(read(8)), readU64: () => u64(read(8)),
            readU8: () => Number(read(1)), readU32: () => Number(read(4)),
            readS32: () => Number(BigInt.asIntN(32, read(4))),
            readCString: n => Buffer.from(Array.from({ length: n }, (_, i) => bytes.get(at + i) || 0)).toString().split('\0')[0],
            readByteArray: n => Uint8Array.from({ length: n }, (_, i) => bytes.get(at + i) || 0).buffer };
    }
    const sentinel = base + 0x9334770 + 0xb0, node = 0x100000, task = 0x110000;
    put(sentinel, node); put(node, sentinel); put(node + 0x10, task);
    put(task + 0xa0, 1 << 20, 4); put(task + 0x120, 0x120000); put(0x12006c, 2, 4);
    put(base + 0x411a770, 0x200000); put(base + 0x411a778, 7, 4);
    put(0x200008, 0x210000); put(0x210094, 7, 4); put(0x210000 + 0x2ec8, 0x220000);
    put(0x220000, 0x230000); put(0x220008, 0x210000); put(0x2301d0, base + 0xbc3470);
    put(0x220010, 2, 4); put(0x220014, 72, 4); put(0x220018, 0x240000);
    put(0x240000, 0x2787c0e3ef93b5a5n); put(0x240010, 1, 4);
    put(0x240038, 0x2787bbe3ef93ad26n); put(0x240048, 1, 4);
    put(base + 0x4248b30, 0x250000); put(0x250000 + 0x40c4, 4, 4);
    text(0x250000 + 0x49b9, '123456789abcdef0123456789abcdef0');
    text(base + 0x415ac90 + 0x461, 'abcdef0123456789abcdef0123456789');
    put(base + 0x4248b78, 0x260000); put(0x260000, base + 0x2af57d8);
    put(0x260258, 0x270000); put(0x260280, 8, 4);
    const bucket = 0x270000 + 7 * 24;
    put(bucket, 0xc34a84af6ff26f6fn); put(bucket + 8, 0xc34a84af6ff26f6fn); put(bucket + 16, 0x280000);
    put(0x260030, 0x280000); put(0x260038, 1, 4); put(0x26003c, 1, 4);
    put(base + 0x410ebd0 + 32, 0x2787c0e3ef93b5a5n);
    const context = { at: rva => pointer(base + rva), uint64: u64,
        hex: data => Buffer.from(data).toString('hex') };
    vm.runInNewContext(source, context);
    return { read: context.readSkillObservation, put, bytes };
}

test('reads the real opening shape: two inventory manuals and only one deferred manual', () => {
    const f = fixture();
    const result = f.read();
    assert.equal(result.Task, '0x110000');
    assert.equal(result.TaskWord, 2);
    assert.deepEqual(Array.from(result.Manuals), ['2787bbe3ef93ad26', '2787c0e3ef93b5a5']);
    assert.equal(result.Supplements, 0);
    assert.equal(result.Upgrades.slice(64, 80), 'a5b593efe3c08727');
    assert.equal(result.Upgrades.slice(80), '00'.repeat(88));
});

test('refuses stale player handles, an encounter task, oversized inventory, and misaligned stat records', () => {
    for (const mutate of [f => f.put(0x210094, 8, 4), f => f.put(0x12006c, 1, 4),
        f => f.put(0x220010, 73, 4), f => f.put(0x270000 + 7 * 24 + 16, 0x280001)]) {
        const f = fixture(); mutate(f); assert.throws(f.read);
    }
});

test('another character needs no Ellie inventory observation', () => {
    const f = fixture();
    f.put(base + 0x415ac90 + 0x3c, 1, 4);
    f.put(0x220010, 1000, 4);
    assert.equal(f.read(), null);
});
