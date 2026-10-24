"""Read the player's skill state without hooks, game calls or game writes.

The departure probe uses the matching NativeSkillState.js on the broadcasting thread. Manual capture
uses this bounded read and revalidates every byte plus the selected task and process identity.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

from NativeCheckpointProbe import ProbeError, ReadOnlyProcess, inspect_current_task, verify_build
from NativeCodeSignature import SKILL_RVAS, active, at

MANUALS = frozenset('''116353b2ccce482b 1485f600eec2cd27 17e95e53f2bd3736 1cb4067c1e589294 1cb4087c1e5895fa
1cb4097c1e5897ad 227a2d8497cb4f54 2787b9e3ef93a9c0 2787bae3ef93ab73 2787bbe3ef93ad26 2787bce3ef93aed9
2787bde3ef93b08c 2787bee3ef93b23f 2787c0e3ef93b5a5 2787c7e3ef93c18a 2787c8e3ef93c33d 2e15d6fbabb53a75
2e2c201b46157b08 2f3a2595eccdde5b 2f3a2695eccde00e 2f3a2795eccde1c1 3fd45fee7a7b986a 42418fecbfc2baa2
4424c86fb3e9a740 4424c96fb3e9a8f3 4424ca6fb3e9aaa6 4424cb6fb3e9ac59 4424cd6fb3e9afbf 4424ce6fb3e9b172
4424d66fb3e9bf0a 4424d76fb3e9c0bd 654afd95437c201b 7687929d79c6bd40 7687939d79c6bef3 76879b9d79c6cc8b
76879c9d79c6ce3e 76879e9d79c6d1a4 76879f9d79c6d357 7687a09d79c6d50a 7687a19d79c6d6bd 87a912d7db60a132
9ee918e828168396 9ee919e828168549 9ee91ee828168dc8 9ee91fe828168f7b 9ee920e82816912e 9ee922e828169494
9ee923e828169647 9ee924e8281697fa 9ee925e8281699ad a643199c571b34ff ad649bae8b5d4b47 b43c09ceb6183326
b43c0aceb61834d9 b4838e6a0f11c975 bf62575017fd8a7c bf62585017fd8c2f c8674c79d0b61967 cf17078265fc2fde
d80b06f7fa938693 de9e7bf3170b57a2 e7ca3044b9e11e1a f0867bbbeafd3b4f f476073b9a8af012'''.split())


def read_skill_state(read, base, task):
    """Only typed bounded reads; no game function, including the side-effecting player accessor, is called."""
    seen = []

    def block(address, size):
        if not 0x10000 <= address < 0x800000000000 - size or not 0 < size <= 4096:
            raise ProbeError('Skill memory reference is outside the bounded model')
        data = read(address, size)
        if len(data) != size:
            raise ProbeError('Short skill state read')
        seen.append((address, data))
        return data

    def integer(address, fmt='<Q'):
        return struct.unpack(fmt, block(address, struct.calcsize(fmt)))[0]

    def pointer(address):
        value = integer(address)
        if not 0x10000 <= value < 0x800000000000 or value % 8:
            raise ProbeError('Invalid skill state pointer')
        return value

    def text(address):
        data = block(address, 33).split(b'\0', 1)
        if len(data) != 2 or re.fullmatch(rb'[0-9a-f]{16,32}', data[0]) is None:
            raise ProbeError('Invalid live run identity')
        return data[0].decode('ascii')

    run = base + at(0x415AC90)
    handle = pointer(base + at(0x411A770))
    generation = integer(base + at(0x411A770) + 8, '<I')
    actor = pointer(handle + 8)
    if generation == 0 or integer(actor + 0x94, '<I') != generation:
        raise ProbeError('Player handle generation mismatch')
    inventory = pointer(actor + 0x2EC8)
    vtable = pointer(inventory)
    if pointer(inventory + 8) != actor or pointer(vtable + 0x1D0) != base + at(0xBC3470):
        raise ProbeError('Player inventory service identity mismatch')
    count, capacity, items = struct.unpack('<IIQ', block(inventory + 0x10, 16))
    if capacity != 72 or not 0 < count <= capacity or items % 8:
        raise ProbeError('Unsupported active inventory layout')
    names, manuals, supplements = set(), [], 0
    # The active entries have stride 0x38. AB3CA0 reads the persistent quantity at +0x10;
    # +0x20 is a different counter and must never be used as the player's amount.
    for index in range(count):
        entry = block(items + index * 0x38, 0x14)
        name, amount = struct.unpack_from('<Q', entry)[0], struct.unpack_from('<i', entry, 0x10)[0]
        if not name or name in names or not 0 <= amount <= 32767:
            raise ProbeError('Invalid active inventory item')
        names.add(name)
        key = f'{name:016x}'
        if key in MANUALS and amount:
            if amount != 1:
                raise ProbeError('Unexpected training manual quantity')
            manuals.append(key)
        if name == 0xE7052D97197FB860:
            supplements = amount
    info = pointer(base + at(0x4248B30))
    difficulty = integer(info + 0x40C4, '<i')
    if not 0 <= difficulty <= 5:
        raise ProbeError('Unrecognized live difficulty')
    service = pointer(base + at(0x4248B78))
    if pointer(service) != base + at(0x2AF57D8):
        raise ProbeError('Profile stat service identity mismatch')
    table, capacity = pointer(service + 0x258), integer(service + 0x280, '<I')
    if not 1 <= capacity <= 65536:
        raise ProbeError('Invalid stat table capacity')
    stat = 0xC34A84AF6FF26F6F
    completed = None
    for index in range(min(capacity, 128)):
        key, duplicate, value = struct.unpack('<QQQ', block(table + (stat % capacity + index) % capacity * 24, 24))
        if key == duplicate == stat:
            data, allocated, used = struct.unpack('<QII', block(service + 0x30, 16))
            if not 0 < used <= allocated <= 65536 or not data <= value < data + used * 24 or (value - data) % 24:
                raise ProbeError('Stat record is outside the live vector')
            completed = integer(value + 8, '<i')
            break
        if key == 0xFFFFFFFFFFFFFFFE:
            break
    if completed is None or not 0 <= completed <= 100:
        raise ProbeError('Run encounter count is unavailable')
    result = dict(CharacterIndex=integer(run + 0x3C, '<i'), RunId=text(run + 0x461),
                  Seed=text(info + 0x49B9), CompletedEncounters=completed, Difficulty=difficulty,
                  Task=task['task'], TaskWord=task['descriptor_word'], Manuals=sorted(manuals),
                  Supplements=supplements, Upgrades=block(base + at(0x410EBD0), 128).hex())
    for address, data in reversed(seen):
        if read(address, len(data)) != data:
            raise ProbeError('Skill state changed during revalidation')
    return result


def observe(pid, allow_encounter=False):
    from NativeCheckpointTrigger import process_birth
    from NativeEncounterRestart import hideout_state, save_workers
    process = ReadOnlyProcess(pid)
    try:
        digest = verify_build(process.image_path().read_bytes())
        base, birth = process.image_base(), process_birth(process)
        for rva in SKILL_RVAS:
            expected = bytes.fromhex(active().pinned[at(rva)])
            if process.read(base + at(rva), len(expected)) != expected:
                raise ProbeError('Live skill code fingerprint mismatch')
        before = hideout_state(process, base)
        task = inspect_current_task(process.read, base, include_simple_fallback=True)
        if not birth or before['runActive'] != 1 or before['runInitialized'] != 1 or before['story'] != 2 \
                or task.get('descriptor_word') not in ((1, 2) if allow_encounter else (2,)) \
                or save_workers(process, base) != [0, 0, 0]:
            raise ProbeError('A stable playable hideout is required for skill protection')
        result = read_skill_state(process.read, base, task)
        if hideout_state(process, base) != before or process_birth(process) != birth \
                or save_workers(process, base) != [0, 0, 0]:
            raise ProbeError('The preparation boundary changed during skill observation')
        return dict(Version=1, ProcessId=pid, ProcessBirth=str(birth), BuildSha256=digest,
                    ObservedUtc=datetime.now(timezone.utc).isoformat(), Boundary='manual', **result)
    finally:
        process.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('pid', type=int)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--raw', type=Path)
    parser.add_argument('--inspect-encounter', action='store_true', help='diagnostic only: cannot become a preparation observation')
    args = parser.parse_args()
    result = observe(args.pid, args.inspect_encounter)
    result['ProfilePath'] = str(args.profile.resolve())
    result['OriginalRunSha256'] = hashlib.sha256(args.raw.read_bytes()).hexdigest() if args.raw else None
    print(json.dumps(result))


if __name__ == '__main__':
    try:
        main()
    except (ProbeError, OSError, ValueError, KeyError) as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(1)
