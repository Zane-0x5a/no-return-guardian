// Bounded read-only skill observation for the route-board broadcast probe.
// It deliberately uses Frida's typed memory reads only: no NativeFunction, hook, write, or game call.
const skillManuals = new Set([
    '116353b2ccce482b','1485f600eec2cd27','17e95e53f2bd3736','1cb4067c1e589294','1cb4087c1e5895fa',
    '1cb4097c1e5897ad','227a2d8497cb4f54','2787b9e3ef93a9c0','2787bae3ef93ab73','2787bbe3ef93ad26',
    '2787bce3ef93aed9','2787bde3ef93b08c','2787bee3ef93b23f','2787c0e3ef93b5a5','2787c7e3ef93c18a',
    '2787c8e3ef93c33d','2e15d6fbabb53a75','2e2c201b46157b08','2f3a2595eccdde5b','2f3a2695eccde00e',
    '2f3a2795eccde1c1','3fd45fee7a7b986a','42418fecbfc2baa2','4424c86fb3e9a740','4424c96fb3e9a8f3',
    '4424ca6fb3e9aaa6','4424cb6fb3e9ac59','4424cd6fb3e9afbf','4424ce6fb3e9b172','4424d66fb3e9bf0a',
    '4424d76fb3e9c0bd','654afd95437c201b','7687929d79c6bd40','7687939d79c6bef3','76879b9d79c6cc8b',
    '76879c9d79c6ce3e','76879e9d79c6d1a4','76879f9d79c6d357','7687a09d79c6d50a','7687a19d79c6d6bd',
    '87a912d7db60a132','9ee918e828168396','9ee919e828168549','9ee91ee828168dc8','9ee91fe828168f7b',
    '9ee920e82816912e','9ee922e828169494','9ee923e828169647','9ee924e8281697fa','9ee925e8281699ad',
    'a643199c571b34ff','ad649bae8b5d4b47','b43c09ceb6183326','b43c0aceb61834d9','b4838e6a0f11c975',
    'bf62575017fd8a7c','bf62585017fd8c2f','c8674c79d0b61967','cf17078265fc2fde','d80b06f7fa938693',
    'de9e7bf3170b57a2','e7ca3044b9e11e1a','f0867bbbeafd3b4f','f476073b9a8af012'
]);
const supplementsSid = uint64('0xe7052d97197fb860');

function skillCString(address) {
    const bytes = new Uint8Array(address.readByteArray(33));
    const end = bytes.indexOf(0);
    if (end < 0) throw new Error('Unterminated skill-state text');
    const value = String.fromCharCode(...bytes.slice(0, end));
    if (!/^[0-9a-f]{16,32}$/.test(value)) throw new Error('Invalid skill-state text');
    return value;
}

function skillTask() {
    // The same bounded selector branches as NativeCheckpointProbe.inspect_current_task.
    // Never call the game's selector from the broadcast probe.
    const context = at(0x9334770);
    const sentinel = context.add(0xb0);
    function walk(visitor) {
        let node = sentinel.readPointer();
        const visited = new Set();
        while (!node.equals(sentinel)) {
            if (node.isNull() || visited.has(node.toString()) || visited.size >= 64)
                throw new Error('Invalid skill task list');
            visited.add(node.toString());
            const current = node.add(0x10).readPointer();
            const result = visitor(current);
            if (result) return result;
            node = node.readPointer();
        }
        return null;
    }
    let selected = null;
    if (context.readU8() === 0)
        selected = walk(current => (current.add(0xa0).readU32() & (1 << 20)) ? current : null);
    if (selected === null) selected = walk(current => {
        if (!current.add(0x90).readU64().equals(uint64('0xebada5168620c5fe')))
            throw new Error('Unsupported skill task selector');
        const activity = current.add(0xb8).readPointer();
        if (activity.isNull() || !(activity.add(0x58).readU8() & 0x0e)) return null;
        if (current.add(0xa0).readU32() & 1) throw new Error('Recursive skill task selector');
        return current;
    });
    if (selected === null || selected.add(0x120).readPointer().add(0x6c).readS32() !== 2)
        throw new Error('Skill observation requires the current hideout task');
    return selected.toString();
}

function skillPointer(address) {
    const value = address.readPointer();
    const numeric = Number(value.toString());
    if (!Number.isSafeInteger(numeric) || numeric < 0x10000 || numeric >= 0x800000000000 || numeric % 8)
        throw new Error('Invalid skill state pointer');
    return value;
}

function readSkillObservation() {
    const run = at(0x415ac90);
    const character = run.add(0x3c).readS32();
    if (character !== 0) return null; // Other characters keep the existing departure path.
    const task = skillTask();
    const handleAddress = at(0x411a770);
    const handle = skillPointer(handleAddress);
    const generation = handleAddress.add(8).readU32();
    const actor = skillPointer(handle.add(8));
    if (generation === 0 || actor.add(0x94).readU32() !== generation) throw new Error('Player handle mismatch');
    const inventory = skillPointer(actor.add(0x2ec8));
    if (inventory.add(8).readPointer().compare(actor) !== 0 ||
        inventory.readPointer().add(0x1d0).readPointer().compare(at(0xbc3470)) !== 0) {
        throw new Error('Player inventory service mismatch');
    }
    const count = inventory.add(0x10).readU32();
    const capacity = inventory.add(0x14).readU32();
    const items = skillPointer(inventory.add(0x18));
    if (capacity !== 72 || count < 1 || count > capacity) throw new Error('Unsupported active inventory');
    const names = new Set();
    const manuals = [];
    let supplements = 0;
    for (let index = 0; index < count; index++) {
        const entry = items.add(index * 0x38);
        const name = entry.readU64();
        const amount = entry.add(0x10).readS32();
        const key = name.toString(16).padStart(16, '0');
        if (name.equals(uint64('0')) || names.has(key) || amount < 0 || amount > 32767) throw new Error('Invalid active item');
        names.add(key);
        if (skillManuals.has(key) && amount) {
            if (amount !== 1) throw new Error('Unexpected training manual quantity');
            manuals.push(key);
        }
        if (name.compare(supplementsSid) === 0) supplements = amount;
    }
    const info = skillPointer(at(0x4248b30));
    const difficulty = info.add(0x40c4).readS32();
    if (difficulty < 0 || difficulty > 5) throw new Error('Unrecognized difficulty');
    const stats = skillPointer(at(0x4248b78));
    if (stats.readPointer().compare(at(0x2af57d8)) !== 0) throw new Error('Profile stat service mismatch');
    const table = skillPointer(stats.add(0x258));
    const tableCapacity = stats.add(0x280).readU32();
    if (tableCapacity < 1 || tableCapacity > 65536) throw new Error('Invalid stat table');
    const completedSid = uint64('0xc34a84af6ff26f6f');
    let completed = null;
    let bucket = 0;
    for (const digit of completedSid.toString(16)) bucket = (bucket * 16 + parseInt(digit, 16)) % tableCapacity;
    for (let index = 0; index < Math.min(tableCapacity, 128); index++) {
        const slot = table.add(((bucket + index) % tableCapacity) * 24);
        const key = slot.readU64();
        const duplicate = slot.add(8).readU64();
        if (key.compare(completedSid) === 0 && duplicate.compare(completedSid) === 0) {
            const value = slot.add(16).readPointer();
            const vector = skillPointer(stats.add(0x30));
            const allocated = stats.add(0x38).readU32();
            const used = stats.add(0x3c).readU32();
            if (used === 0 || used > allocated || allocated > 65536 || value.compare(vector) < 0 ||
                value.compare(vector.add(used * 24)) >= 0 || Number(value.sub(vector).toString()) % 24) {
                throw new Error('Invalid completed encounter record');
            }
            completed = value.add(8).readS32();
            break;
        }
        if (key.equals(uint64('0xfffffffffffffffe'))) break;
    }
    if (completed === null || completed < 0 || completed > 100) throw new Error('Completed encounters unavailable');
    const result = {
        Version: 1,
        CharacterIndex: character,
        RunId: skillCString(run.add(0x461)),
        Seed: skillCString(info.add(0x49b9)),
        CompletedEncounters: completed,
        Difficulty: difficulty,
        Task: task,
        TaskWord: 2,
        Manuals: manuals.sort(),
        Supplements: supplements,
        Upgrades: hex(at(0x410ebd0).readByteArray(128))
    };
    if (skillTask() !== task || handleAddress.readPointer().compare(handle) !== 0 ||
        handleAddress.add(8).readU32() !== generation) throw new Error('Skill observation boundary changed');
    return result;
}

globalThis.readSkillObservation = readSkillObservation;
