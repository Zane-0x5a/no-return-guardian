"""Retain bounded native checkpoint bytes for offline analysis, never for restoration."""

import argparse
import hashlib
import json
from pathlib import Path
import struct
import time
import zipfile

from NativeCheckpointProbe import ReadOnlyProcess, ProbeError, inspect_current_task, verify_build, verify_code
from NativeCheckpointTrigger import process_birth


ALLOCATIONS = (
    ('gameplay', 0x35F9F40, 0x6790, 3),
    ('records', 0x35F9F48, 0x26930, 3),
    ('body-manager', 0x35F9F50, 0xBC10, 3),
    ('clocks', 0x35F9F58, 0x238, 3),
    ('permadeath-clock', 0x35F9F60, 0x238, 1),
    ('tasks', 0x35F9F68, 0xAA08, 3),
    ('stats', 0x35F9F70, 0x6708, 3),
    ('chapter', 0x35F9F78, 0xD7D10, 1),
    ('player-killed', 0x35F9F80, 0x2080, 1),
    ('npc-killed', 0x35F9F88, 0x1F70, 1),
)
RUN_RVA = 0x415AC90


def read_exact(read, address, size):
    if not 0 < size <= 0x100000 or not 0x10000 <= address < 0x800000000000 - size:
        raise ProbeError('Invalid evidence memory range')
    result = bytearray()
    for offset in range(0, size, 4096):
        length = min(4096, size - offset)
        payload = read(address + offset, length)
        if len(payload) != length:
            raise ProbeError('Short evidence memory read')
        result.extend(payload)
    return bytes(result)


def layout(read, base):
    regions = []
    for name, rva, size, count in ALLOCATIONS:
        pointer = struct.unpack('<Q', read_exact(read, base + rva, 8))[0]
        if not 0x10000 <= pointer < 0x800000000000 - size * count:
            raise ProbeError(f'Unallocated or invalid native member: {name}')
        for slot in range(count):
            suffix = f'-{slot}' if count > 1 else ''
            regions.append({'name': name + suffix, 'address': pointer + slot * size, 'size': size})
    regions.append({'name': 'run-checkpoint-observed-span',
                    'address': base + RUN_RVA + 0x528, 'size': 0x6D8C})
    ordered = sorted(regions, key=lambda region: region['address'])
    if any(left['address'] + left['size'] > right['address']
           for left, right in zip(ordered, ordered[1:])):
        raise ProbeError('Overlapping native allocations')
    return regions


def state(read, base):
    result = {}
    for name, rva, encoding in (
        ('rogue', 0x4161F58, '<B'), ('mode', RUN_RVA + 0xA0, '<i'),
        ('initialized', RUN_RVA + 0x72C8, '<B'), ('selector', RUN_RVA + 0x520, '<i'),
        ('runCheckpointCount', RUN_RVA + 0x1528, '<i'),
    ):
        result[name] = struct.unpack(encoding, read_exact(read, base + rva, struct.calcsize(encoding)))[0]
    result['hubFieldsHex'] = read_exact(read, base + RUN_RVA + 0x50, 16).hex()
    return result


def capture(read, base, context):
    before = context()
    regions = layout(read, base)
    payloads = {region['name']: read_exact(read, region['address'], region['size']) for region in regions}
    if regions != layout(read, base):
        raise ProbeError('Native allocation changed after first pass')
    changed = [region['name'] for region in reversed(regions)
               if read_exact(read, region['address'], region['size']) != payloads[region['name']]]
    if changed:
        raise ProbeError('Native bytes changed during capture: ' + ', '.join(changed))
    if regions != layout(read, base) or before != context():
        raise ProbeError('Process, task, or native context changed during capture')
    manifest = {
        'schema': 1, 'kind': 'native-checkpoint-evidence', 'createdUtcSeconds': time.time(),
        'restorable': False, 'coherentSnapshot': False, 'twoPassEqual': True,
        'scope': 'Observed allocations only; pointer ownership and restore boundary unproven',
        'context': before,
        'regions': [{**region, 'address': hex(region['address']),
                     'file': 'payload/' + region['name'] + '.bin',
                     'sha256': hashlib.sha256(payloads[region['name']]).hexdigest()} for region in regions],
    }
    return manifest, payloads


def write_archive(output, manifest, payloads):
    if manifest.get('restorable') is not False or manifest.get('coherentSnapshot') is not False:
        raise ProbeError('Evidence archive must not claim restoration or atomic capture')
    expected_names = {region['name'] for region in manifest['regions']}
    if set(payloads) != expected_names or len(expected_names) != len(manifest['regions']):
        raise ProbeError('Evidence payload set mismatch')
    for region in manifest['regions']:
        if region['file'] != 'payload/' + region['name'] + '.bin' or any(
                character not in 'abcdefghijklmnopqrstuvwxyz0123456789-' for character in region['name']):
            raise ProbeError('Invalid evidence entry name')
        data = payloads[region['name']]
        if len(data) != region['size'] or hashlib.sha256(data).hexdigest() != region['sha256']:
            raise ProbeError('Evidence bytes do not match manifest')
    with zipfile.ZipFile(output, 'x', compression=zipfile.ZIP_STORED) as archive:
        for region in manifest['regions']:
            archive.writestr(region['file'], payloads[region['name']])
        archive.writestr('manifest.json', json.dumps(manifest, ensure_ascii=True, indent=2))
    with zipfile.ZipFile(output) as archive:
        if archive.testzip() is not None:
            raise ProbeError('Written evidence archive failed CRC validation')
        for region in manifest['regions']:
            if hashlib.sha256(archive.read(region['file'])).hexdigest() != region['sha256']:
                raise ProbeError('Written evidence archive failed hash validation')


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('pid', type=int)
    parser.add_argument('--birth', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args(argv)
    if args.pid <= 0 or args.birth <= 0 or args.output.suffix.lower() != '.zip':
        parser.error('Positive process identity and a .zip output are required')
    if args.output.exists():
        parser.error('Evidence output already exists; will not overwrite')
    return args


def main(argv=None):
    args = arguments(argv)
    process = ReadOnlyProcess(args.pid)
    try:
        if process_birth(process) != args.birth:
            raise ProbeError('Game process identity changed')
        path = process.image_path()
        image = path.read_bytes()
        digest = verify_build(image)
        base = process.image_base()
        verify_code(process, image, base, include_task=True)

        def context():
            if process_birth(process) != args.birth or process.image_base() != base:
                raise ProbeError('Game process identity changed')
            return {'pid': args.pid, 'birth': args.birth, 'base': hex(base), 'sha256': digest,
                    'state': state(process.read, base),
                    'task': inspect_current_task(process.read, base, include_simple_fallback=True)}

        manifest, payloads = capture(process.read, base, context)
        verify_code(process, image, base, include_task=True)
        write_archive(args.output, manifest, payloads)
        print(json.dumps({'event': 'native-evidence-retained', 'output': str(args.output.resolve()),
                          'regions': len(payloads), 'bytes': sum(map(len, payloads.values())),
                          'context': manifest['context'], 'restorable': False, 'coherentSnapshot': False}))
        return 0
    finally:
        process.close()


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ProbeError, OSError, ValueError, zipfile.BadZipFile) as error:
        raise SystemExit(f'EVIDENCE_FAILED: {error}')
