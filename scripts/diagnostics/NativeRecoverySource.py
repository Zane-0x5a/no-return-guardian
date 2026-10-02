"""Prepare an independent native source from a formally verified preparation snapshot."""

from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess

from NativeCheckpointProbe import TOOLS, ProbeError
from NativeSaveBuffer import ARENA_OFFSET, BODY_OFFSET, NativeSaveBuffer
from NativeSerializedArena import SerializedArena


OBSERVED_CAPACITY = 0x460414
DEFAULT_METADATA = bytes.fromhex(
    '00000000ffffffffffffffff0000000000000000ff000000000000000000000000000100ffffffff')
PROFILE_VERSION = 9
PROFILE_STATS_KEY = 0xEE639CAD45B1994C  # "stats"
# The profile stats that ss-rogue-run's start-new-run initializes and its on-resume reads back. They live in
# 0P, which an in-process load does not replace, so a recovered run would keep the dead attempt's values:
# each replayed encounter marked another dead-drop item used until no candidate remained (2026-10-01).
RUN_PROFILE_STATS = (
    0xCA79BE3DDF469B1A, 0x8DD8BBEA1823E133, 0xE0A765D96D4DD778,  # dead-drop items 0-2 used
    0x34AE6A71B265A278, 0x524EF3C2F60DC853, 0x6172419D00E62123,  # dead-drop items 3-5 used
    0x966AC5E93688C099, 0x966AC2E93688BB80,                      # dead-drop reward alternation
    0xC34A84AF6FF26F6F, 0xDB3441AB83811BDE)                      # encounters cleared, siege flag


@dataclass(frozen=True)
class RecoverySource:
    configuration: dict
    packet: bytes


def preparation_code(description):
    matches = re.findall(r'\[([0-9A-F]{24})\]', description)
    if len(matches) != 1 or description.find('[') != description.find('[' + matches[0] + ']'):
        raise ProbeError('Native source requires one unambiguous preparation state code')
    code = matches[0]
    decoded = struct.pack('<III', *(int(code[offset:offset + 8], 16) for offset in (0, 8, 16)))
    if code[8:16] not in ('00800000', '00800001') or not decoded[6] & 0x80 or decoded[6] & 0x12:
        raise ProbeError('Native source requires the observed No Return state without auxiliary metadata')
    return code


def object_entries(arena, node):
    count = arena.u32(node + 4)
    if not count:
        return {}
    values, kinds, keys = arena.u64(node + 8), arena.u64(node + 0x10), arena.u64(node + 0x18)
    if not keys:
        raise ProbeError('Profile save container has no keys')
    entries = {}
    for index in range(count):
        key = arena.u64(keys + index * 8)
        if key in entries:
            raise ProbeError('Profile save container repeats a key')
        entries[key] = (arena.data[arena.bounds(kinds + index, 1)], values + index * 8)
    return entries


def profile_run_stats(raw):
    if len(raw) < BODY_OFFSET or raw[:16] != bytes.fromhex('93ea' + '00' * 14):
        raise ProbeError('Unsupported profile working header')
    body = raw[BODY_OFFSET:]
    if len(body) < ARENA_OFFSET + 0x40:
        raise ProbeError('Profile working body is truncated')
    _, declared, version = struct.unpack_from('<III', body)
    if declared != len(body) or version != PROFILE_VERSION:
        raise ProbeError('Unsupported profile working body')
    arena = SerializedArena(body[ARENA_OFFSET:declared])
    stats = object_entries(arena, arena.u64(arena.u64(8))).get(PROFILE_STATS_KEY)
    if stats is None or stats[0] not in (5, 6):
        raise ProbeError('Profile save has no stats object')
    entries = object_entries(arena, arena.u64(stats[1]))
    result = []
    for sid in RUN_PROFILE_STATS:
        kind, slot = entries.get(sid, (None, None))
        if kind != 1:
            raise ProbeError(f'Profile save lacks integer run stat {sid:016X}')
        value = struct.unpack_from('<q', arena.data, slot)[0]
        if not 0 <= value <= 0x7FFFFFFF:
            raise ProbeError(f'Profile run stat {sid:016X} is out of range')
        result.append({'sid': f'{sid:016x}', 'value': value})
    return result


USES = {False: ('hideout',), True: ('hideout', 'restart')}


def prepare_payload(raw, verified, profile, snapshot_id, profile_raw, redeploy=False):
    """Build the recovery packet from the bytes VerifySnapshot's receipt vouches for.

    The verifier alone decides whether this snapshot is a confirmed preparation and how it may be used
    (SnapshotPolicy.Use): 'hideout' returns to its hideout, 'restart' only starts its own encounter again,
    because the first departure captures bound the save written while the game handled the departure. The
    native packet additionally needs a hot working-state copy of the selected profile.
    """
    use = verified.get('use')
    if use not in USES[redeploy]:
        raise ProbeError('This departure capture was saved after the departure; it can only restart its encounter'
                         if use == 'restart' else
                         'The formal verifier does not allow this preparation for ' +
                         ('restarting its encounter' if redeploy else 'returning to its hideout'))
    if (verified.get('id') != snapshot_id or verified.get('schema') != 5 or
            verified.get('captureBasis') != 'hot_synthesized_state' or
            Path(verified.get('sourceProfilePath') or '').resolve() != profile.resolve()):
        raise ProbeError('Native source is not a confirmed preparation for the selected profile')
    digest = hashlib.sha256(raw).hexdigest()
    profile_digest = hashlib.sha256(profile_raw).hexdigest()
    files = verified.get('files')
    for relative, data, data_digest in (('gamedata/R0A.save', raw, digest),
                                        ('gamedata/0P.save', profile_raw, profile_digest)):
        entry = files.get(relative) if isinstance(files, dict) else None
        if (not isinstance(entry, dict) or entry.get('length') != len(data) or
                str(entry.get('sha256', '')).lower() != data_digest):
            raise ProbeError('Native preparation raw payload changed after formal verification')
    run_stats = profile_run_stats(profile_raw)
    body = NativeSaveBuffer.from_working_raw(raw, capacity=OBSERVED_CAPACITY)
    code = preparation_code(body.description)
    if code != verified.get('runStateCode'):
        raise ProbeError('Native source description disagrees with the formal snapshot')
    padded_raw = raw + bytes(OBSERVED_CAPACITY - len(body.data))
    fields = sorted(body.arena.pointer_fields)
    pointers = struct.pack('<' + 'I' * len(fields), *fields)
    configuration = {'snapshotId': snapshot_id,
                     'profile': str(profile.resolve()), 'stateCode': code, 'rawSha256': digest,
                     'compositeSha256': verified['compositeSha256'], 'capacity': OBSERVED_CAPACITY,
                     'declaredSize': body.declared_size, 'rawSize': len(padded_raw),
                     'pointerCount': len(fields), 'packetSha256': hashlib.sha256(padded_raw + pointers).hexdigest(),
                     'bodySha256': hashlib.sha256(padded_raw[BODY_OFFSET:]).hexdigest(),
                     'metadataHex': DEFAULT_METADATA.hex(), 'profileRawSha256': profile_digest,
                     'runStats': run_stats, 'inGameAcceptance': False}
    return RecoverySource(configuration, padded_raw + pointers)


def verify_formally(storage, snapshot_id):
    """VerifySnapshot's receipt: what it verified, the snapshot's use, and the hash of every payload file."""
    result = subprocess.run([str(TOOLS / 'VerifySnapshot.exe'), str(storage), snapshot_id],
                            capture_output=True, timeout=30, check=False)
    if result.returncode != 0:
        raise ProbeError('Formal preparation snapshot verification failed: ' +
                         result.stderr.decode('utf-8', errors='replace').strip())
    try:
        receipt = json.loads(result.stdout.decode('utf-8-sig'))
    except (UnicodeError, ValueError) as error:
        raise ProbeError('Formal verifier receipt is unreadable') from error
    if not isinstance(receipt, dict) or receipt.get('verified') is not True or receipt.get('id') != snapshot_id:
        raise ProbeError('Formal verifier receipt does not vouch for this snapshot')
    return receipt


def prepare_source(storage, snapshot_id, profile, redeploy=False):
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,127}', snapshot_id):
        raise ProbeError('Invalid native source snapshot id')
    storage, profile = Path(storage).resolve(strict=True), Path(profile).resolve(strict=True)
    directory = (storage / 'snapshots' / snapshot_id).resolve(strict=True)
    if not directory.is_relative_to(storage / 'snapshots'):
        raise ProbeError('Native source snapshot escapes its storage')
    verified = verify_formally(storage, snapshot_id)
    raw_path = (directory / 'payload/gamedata/R0A.save').resolve(strict=True)
    profile_path = (directory / 'payload/gamedata/0P.save').resolve(strict=True)
    if not raw_path.is_relative_to(directory) or not profile_path.is_relative_to(directory):
        raise ProbeError('Native source payload escapes its snapshot')
    return prepare_payload(raw_path.read_bytes(), verified, profile, snapshot_id, profile_path.read_bytes(), redeploy)


def verify_unloaded_menu(process, base):
    fields = [(base + 0x415ac90 + 0x72c8, b'\0'),
              (base + 0x415ac90 + 0x520, struct.pack('<i', 2)),
              (base + 0x9398730, struct.pack('<i', 2))]
    fields += [(base + 0x9341660 + offset, bytes(4)) for offset in (0x58, 0x228, 0x3f8)]
    fields.append((base + 0x9341660 + 0x5e0, struct.pack('<i', 20)))
    for address, expected in fields + list(reversed(fields)):
        if process.read(address, len(expected)) != expected:
            raise ProbeError('Native recovery requires the unloaded No Return menu, slot R0A and idle save workers')


def secure_live_state(process, base, storage, profile):
    verify_unloaded_menu(process, base)
    result = subprocess.run([str(TOOLS / 'CaptureLiveSnapshot.exe'), str(storage), str(profile)],
                            capture_output=True, timeout=30, check=False)
    if result.returncode != 0:
        raise ProbeError('Pre-recovery live disk snapshot failed: ' + result.stderr.decode('utf-8', errors='replace').strip())
    try:
        captured = json.loads(result.stdout.decode('utf-8-sig'))
    except (UnicodeError, ValueError) as error:
        raise ProbeError('Invalid pre-recovery snapshot receipt') from error
    if captured.get('purpose') != 'live_state' or captured.get('verified') is not True or not captured.get('id'):
        raise ProbeError('Pre-recovery snapshot receipt is incomplete')
    verify_unloaded_menu(process, base)
    return captured


def rollback_failed_recovery(log_path, storage, profile, identity):
    records = []
    if not log_path.exists():
        return {'required': False, 'reason': 'Native recovery did not publish its preflight'}
    for line in log_path.read_text(encoding='utf-8').splitlines():
        try:
            record = json.loads(line)
            if not isinstance(record, dict):
                raise ValueError('Recovery record is not an object')
            records.append(record)
        except ValueError:
            raise ProbeError('Native recovery log is damaged; automatic disk rollback was not attempted')
    headers = [record for record in records if record.get('kind') == 'host-preflight']
    sources = [record for record in records if record.get('kind') == 'recovery-source-verified']
    if not sources:
        return {'required': False, 'reason': 'No secured native recovery source was published'}
    if len(headers) != 1 or len(sources) != 1 or any(headers[0].get(key) != value for key, value in identity.items()):
        raise ProbeError('Native recovery rollback receipt belongs to another process')
    undo_id = sources[0].get('undo', {}).get('id', '')
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,127}', undo_id):
        raise ProbeError('Invalid native recovery disk undo identifier')
    result = subprocess.run([str(TOOLS / 'UndoSnapshot.exe'), str(storage), str(profile), undo_id],
                            capture_output=True, timeout=30, check=False)
    if result.returncode != 0:
        raise ProbeError('Native recovery disk rollback failed: ' + result.stderr.decode('utf-8', errors='replace').strip())
    receipt = json.loads(result.stdout.decode('utf-8-sig'))
    if not isinstance(receipt, dict) or receipt.get('restoredSnapshotId') != undo_id or receipt.get('diskRollbackVerified') is not True:
        raise ProbeError('Native recovery disk rollback receipt is incomplete')
    return {'required': True, **receipt}
