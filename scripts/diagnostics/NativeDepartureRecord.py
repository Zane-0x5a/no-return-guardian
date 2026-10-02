"""The route board's departure record: which encounter a preparation's restart repeats.

The board's photo broadcasts `player-next-task` with its int32 route index and, from the photo's
`wait-for-input` state, its own boolean (read by Marlene's trait). The recorder stores those argument
slots verbatim once the encounter is entered; a restart replays exactly them.
"""

import json
import os
from pathlib import Path
import re
import time

from NativeCheckpointProbe import ProbeError


NEXT_TASK = 0xA9456E5567D0CE70
RECORD_SCHEMA = 1
SNAPSHOT_ID = re.compile(r'[A-Za-z0-9][A-Za-z0-9_-]{0,127}')
MAXIMUM_ROUTE_INDEX = 63
INT32, BOOLEAN = 2, 1


def departure_path(storage, snapshot_id):
    if not SNAPSHOT_ID.fullmatch(snapshot_id or ''):
        raise ProbeError('Invalid departure snapshot id')
    return Path(storage).resolve() / 'departures' / f'{snapshot_id}.json'


def departure_slots(slots, count):
    """The recorded argument slots (value at byte 0, type word at byte 12): an int32 route index, then
    optionally the photo's boolean. Replay copies them verbatim, so only these two shapes are accepted."""
    if (count not in (1, 2) or not isinstance(slots, str) or
            not re.fullmatch(r'[0-9a-f]{%d}' % (32 * count), slots)):
        raise ProbeError('Departure must carry an int32 route index and at most one boolean')
    raw = bytes.fromhex(slots)
    index = int.from_bytes(raw[0:4], 'little', signed=True)
    if int.from_bytes(raw[12:14], 'little') != INT32 or not 0 <= index <= MAXIMUM_ROUTE_INDEX:
        raise ProbeError('Departure argument is not a recorded route index')
    if count == 2 and int.from_bytes(raw[28:30], 'little') != BOOLEAN:
        raise ProbeError('Second departure argument is not a boolean')
    return raw, index


def load_departure(storage, snapshot_id):
    path = departure_path(storage, snapshot_id)
    try:
        record = json.loads(path.read_text(encoding='utf-8'))
    except FileNotFoundError:
        raise ProbeError('This preparation has no recorded departure; depart once from the route board first')
    except (OSError, ValueError) as error:
        raise ProbeError(f'Departure record is unreadable: {error}')
    if (not isinstance(record, dict) or record.get('schema') != RECORD_SCHEMA or
            record.get('snapshotId') != snapshot_id or
            record.get('eventSid') != f'{NEXT_TASK:016X}' or record.get('departureConfirmed') is not True):
        raise ProbeError('Departure record does not belong to this preparation')
    raw, index = departure_slots(record.get('slots'), record.get('argumentCount'))
    if record.get('routeIndex') != index:
        raise ProbeError('Departure record index disagrees with its argument bytes')
    return record


def new_departure(owner, build_sha256, pid, birth, captured, raw, index, auto_protected, encounter):
    """The record of one captured departure; only a confirmed arrival makes it loadable."""
    return {'schema': RECORD_SCHEMA, 'snapshotId': owner, 'buildSha256': build_sha256,
            'pid': pid, 'birth': birth, 'recordedAt': time.time(),
            'eventSid': f'{NEXT_TASK:016X}', 'argumentCount': captured['count'], 'slots': raw.hex(),
            'routeIndex': index, 'photoFlag': raw[16] != 0 if captured['count'] == 2 else None,
            'caller': captured.get('caller'), 'autoProtected': auto_protected,
            'departureConfirmed': encounter is not None, 'encounter': encounter}


def write_departure(storage, record):
    path = departure_path(storage, record['snapshotId'])
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f'{path.name}.tmp-{os.getpid()}')
    temporary.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, path)
    return path
