"""Freeze the player's hideout saves while the recorder waits, and publish the one before the departure.

The save the game writes while handling the departure is no hideout to return to: its listeners have
already stored the chosen encounter as the run's position and reset the hideout for the next visit (the
lockbox refills, the route board moves past this node). So while the player is in the hideout each settled
hideout save is frozen in Guardian storage, and at the departure the frozen one is published, through
Guardian's hot-capture gates, only if the game finished nothing else before the event.

A new run's first hideout is the exception. The hub writes the run's opening save (play time 0-1 s) while
that hideout opens, before the recorder can have seen it playable, and usually nothing else until the
departure; sometimes the game has already moved it into its native slots, leaving no working files. That
opening save is frozen as well, and binds only if the save the game writes at the departure belongs to the
same run.
"""

import ctypes
from ctypes import wintypes
import hashlib
import json
import msvcrt
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
from datetime import datetime, timezone

from NativeCheckpointProbe import TOOLS, ProbeError
from NativeDepartureRecord import SNAPSHOT_ID
from NativeRecoverySource import object_entries
from NativeSaveBuffer import NativeSaveBuffer


WORKING_FILES = ('gamedata/R0A.save', 'gamedata/R0A.save-backup', 'gamedata/0P.save', 'gamedata/0P.save-backup',
                 'gamedata/nr.bin')
RUN_SLOT_FILES = ('savedata/SAVEFILER0A/USR-DATA', 'savedata/SAVEFILER0A/params.json',
                  'savedata/SAVEFILER0A/ICN-ID')
PROFILE_SLOT_FILES = ('savedata/SAVEFILE0P/USR-DATA', 'savedata/SAVEFILE0P/params.json',
                      'savedata/SAVEFILE0P/ICN-ID')
# Everything Guardian's hot capture reads, so the frozen copy passes the same gates as the live profile.
STAGE_FILES = WORKING_FILES + RUN_SLOT_FILES
# An opening save already in the native slots: what Guardian rebuilds its working files from.
EXPORT_FILES = ('gamedata/nr.bin',) + RUN_SLOT_FILES + PROFILE_SLOT_FILES
TRACKED = WORKING_FILES + RUN_SLOT_FILES + PROFILE_SLOT_FILES
# Written with every export; the icon identifiers may be far older and prove nothing about the write.
EXPORT_WRITES = ('gamedata/nr.bin', RUN_SLOT_FILES[0], RUN_SLOT_FILES[1], PROFILE_SLOT_FILES[0],
                 PROFILE_SLOT_FILES[1])
STAGING = 'departure-staging'
PRE_DEPARTURE_SAVE = 'pre_departure_save'
RUN_START_SAVE = 'run_start_save'
RUN_START_EXPORT = 'run_start_export'
CONFIRMED_MANUAL_SAVE = 'confirmed_manual_save'
CONFIRMED_MANUAL_EXPORT = 'confirmed_manual_export'
EXPORT_BASES = (RUN_START_EXPORT, CONFIRMED_MANUAL_EXPORT)
# The run play time, in the state code's last field, of every observed opening save.
RUN_START_SECONDS = 1
RECOGNIZED_WORDS = ('00800000', '00800001')
# The rogue run's object in R0A and its identifier string, fixed for the whole run (2026-10-11 analysis).
RUN_KEY = 0x92AD3946449C662D
RUN_ID_KEY = 0x0B55C175A3A096A0
SLOT_ENVELOPE = 36
CHECKSUM_OFFSET = 1424
DESCRIPTION_OFFSET = 1444
DEPARTURE_SAVE_TIMEOUT = 8
SAVE_SETTLE = 0.3


def timestamp_ns(text):
    """A .NET round-trip timestamp without losing the seventh fractional digit to a float."""
    match = re.fullmatch(r'(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?(Z|\+00:00)', text)
    if match is None:
        raise ProbeError('manual source timestamp is invalid')
    seconds = int(datetime.fromisoformat(match[1]).replace(tzinfo=timezone.utc).timestamp())
    return seconds * 10**9 + int((match[2] or '').ljust(9, '0'))


def file_stamps(profile, names):
    """(size, mtime) of each file, None for a missing one."""
    stamps = []
    for name in names:
        try:
            info = (profile / name).stat()
        except FileNotFoundError:
            stamps.append(None)
        else:
            stamps.append((info.st_size, info.st_mtime_ns))
    return tuple(stamps)


def working_stamps(profile, names=WORKING_FILES):
    stamps = file_stamps(profile, names)
    return None if None in stamps else stamps


def project(stamps, names):
    return tuple(stamps[TRACKED.index(name)] for name in names)


_kernel = ctypes.WinDLL('kernel32', use_last_error=True)
_kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
_kernel.CreateFileW.restype = wintypes.HANDLE


def shared_read(path):
    """Read a whole file without denying the game a write, rename or delete of it while we read."""
    handle = _kernel.CreateFileW(str(path), 0x80000000, 0x7, None, 3, 0x80, None)
    if handle in (None, wintypes.HANDLE(-1).value):
        raise OSError(ctypes.get_last_error(), 'Cannot open save file for shared reading', str(path))
    descriptor = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
    with os.fdopen(descriptor, 'rb') as source:
        return source.read()


def exported_raw(export):
    """The working R0A inside a native slot export: the inverse of Guardian's verified export transform."""
    if len(export) <= CHECKSUM_OFFSET + 4 + SLOT_ENVELOPE or export[:2] != b'\x92\xea':
        raise ProbeError('Unsupported native run export')
    raw = bytearray(export[:-SLOT_ENVELOPE])
    raw[0] |= 1
    raw[CHECKSUM_OFFSET:CHECKSUM_OFFSET + 4] = bytes(4)
    return bytes(raw)


def run_code(raw):
    """A working R0A's 24-digit state code, read from its description without parsing the whole save."""
    end = raw.find(b'\0', DESCRIPTION_OFFSET, DESCRIPTION_OFFSET + 4096) if raw[:2] == b'\x93\xea' else -1
    try:
        codes = re.findall(r'\[([0-9A-F]{24})\]', raw[DESCRIPTION_OFFSET:end].decode('utf-8')) if end > 0 else []
    except UnicodeError:
        codes = []
    if len(codes) != 1:
        raise ProbeError('Run save has no single state code')
    return codes[0]


def opening_code(code):
    """Whether a state code is a run's opening save: a recognized word and play time within the opening second."""
    return code[8:16] in RECOGNIZED_WORDS and int(code[16:], 16) <= RUN_START_SECONDS


def run_save(raw):
    """A working R0A's state code and run identifier, from the whole parsed save."""
    body = NativeSaveBuffer.from_working_raw(raw)
    codes = re.findall(r'\[([0-9A-F]{24})\]', body.description)
    if len(codes) != 1:
        raise ProbeError('Run save has no single state code')
    arena = body.arena
    run = object_entries(arena, arena.u64(arena.u64(8))).get(RUN_KEY)
    if run is None or run[0] not in (5, 6):
        raise ProbeError('Run save has no run object')
    identifier = object_entries(arena, arena.u64(run[1])).get(RUN_ID_KEY)
    if identifier is None or identifier[0] != 4:
        raise ProbeError('Run save has no run identifier')
    start = arena.u64(identifier[1])
    end = arena.data.find(b'\0', arena.bounds(start, 1))
    if end <= start:
        raise ProbeError('Run save has an empty run identifier')
    try:
        return {'code': codes[0], 'run': arena.data[start:end].decode('ascii')}
    except UnicodeError as error:
        raise ProbeError('Run save identifier is not text') from error


class HideoutStage:
    """Freezes the newest settled hideout save while the recorder waits for the route board's departure.

    That save is gone the moment the game writes its departure save, so it is copied into Guardian storage
    whenever it settles. Only saves written in this hideout are frozen: every working file must postdate
    the moment the recorder first saw the hideout playable, so an encounter's last save, which may carry
    a recognized state word, is never taken for the hideout. The copy keeps the files' timestamps, and the
    mirrors must match, so Guardian's capture reads it exactly as it would have read the live profile.

    With the game process's creation time (`birth_ns`) it also freezes a run's opening save written before
    that first sight: this process wrote it, it is the newest save, its word is recognized and its play time
    is within the opening second, so no encounter can lie in it. When the game has already moved it into
    its native slots (no working save files left), the exports are frozen instead.
    """

    def __init__(self, storage, profile, playable, not_before_ns, birth_ns=None, clock=time.time,
                 inspect=run_save):
        # The literal storage path Guardian passed in: it checks the staged path against the same string.
        self.root = Path(storage).absolute() / STAGING / f'{os.getpid()}-{time.time_ns()}'
        self.profile = Path(profile)
        self.playable = playable
        self.not_before_ns = not_before_ns
        self.birth_ns = birth_ns
        self.clock = clock
        self.inspect = inspect
        self.seen, self.since = None, None
        self.observed = []
        self.staged = None
        self.judged = None
        self.count = 0
        clear_stale_stages(Path(storage).absolute() / STAGING)

    def candidate(self, stamps):
        """What the profile holds now: the basis it could be frozen as and the files to copy, or None."""
        present = {name for name, stamp in zip(TRACKED, stamps) if stamp is not None}
        oldest = lambda names: min(stamp[1] for stamp in project(stamps, names))
        if present.issuperset(STAGE_FILES):
            if oldest(WORKING_FILES) >= self.not_before_ns:
                return PRE_DEPARTURE_SAVE, STAGE_FILES
            if self.birth_ns is not None and oldest(WORKING_FILES) >= self.birth_ns:
                return RUN_START_SAVE, STAGE_FILES
        elif (self.birth_ns is not None and present.isdisjoint(WORKING_FILES[:4]) and
              present.issuperset(EXPORT_FILES) and oldest(EXPORT_WRITES) >= self.birth_ns):
            return RUN_START_EXPORT, EXPORT_FILES
        return None

    def poll(self):
        now = self.clock()
        stamps = file_stamps(self.profile, TRACKED)
        if stamps != self.seen:
            self.seen, self.since = stamps, now
            self.observed = (self.observed + [(now, stamps)])[-16:]
        candidate = self.candidate(stamps)
        if candidate is None:
            return
        basis, names = candidate
        watch = TRACKED if basis == RUN_START_EXPORT else STAGE_FILES
        if self.staged is not None and self.staged['basis'] == basis and self.staged['stamps'] == project(stamps, watch):
            return
        # Settled: unchanged since we first saw it, or last written long enough ago (an opening save
        # frozen on the recorder's first look).
        newest = max(stamp[1] for stamp in project(stamps, names))
        if (now - self.since < SAVE_SETTLE and now * 1e9 - newest < SAVE_SETTLE * 1e9) or not self.playable():
            return
        if basis != PRE_DEPARTURE_SAVE and self.judged == stamps:
            return
        self.freeze(basis, names, watch, stamps, now)

    def confirm_manual(self, source):
        """Freeze only the exact original authenticated by a fresh formal manual-capture receipt.

        The caller verifies the receipt, process, profile, freshness and playable scene. All source hashes
        are checked again against this copy, so repaired snapshot bytes can never stand in for live bytes.
        A continuously observed identical copy already has stronger provenance and is kept as-is.
        """
        if not isinstance(source, dict) or not self.playable():
            return False
        exported = source.get('CaptureBasis') == 'native_exported_state'
        if source.get('CaptureBasis') not in ('native_exported_state', 'hot_synthesized_state'):
            return False
        stamps = file_stamps(self.profile, TRACKED)
        names = EXPORT_FILES if exported else STAGE_FILES
        if None in project(stamps, names) or exported and any(project(stamps, WORKING_FILES[:4])):
            return False
        expected = EXPORT_FILES if exported else WORKING_FILES
        entries = source.get('Files')
        if (not isinstance(entries, list) or len(entries) != len(expected) or
                {entry.get('RelativePath') for entry in entries if isinstance(entry, dict)} != set(expected)):
            return False
        contents = {name: shared_read(self.profile / name) for name in names}
        for entry in entries:
            data = contents[entry['RelativePath']]
            if len(data) != entry.get('Length') or hashlib.sha256(data).hexdigest() != str(entry.get('Sha256')).lower():
                return False
            written = entry.get('LastWriteUtc')
            if written is not None and timestamp_ns(written) != project(stamps, (entry['RelativePath'],))[0][1]:
                return False
        if stamps != file_stamps(self.profile, TRACKED) or not self.playable():
            return False
        now = self.clock()
        self.seen, self.since = stamps, now
        self.observed = (self.observed + [(now, stamps)])[-16:]
        if self.staged is not None and self.staged['stamps'] == project(stamps, self.staged['watch']):
            return True
        basis = CONFIRMED_MANUAL_EXPORT if exported else CONFIRMED_MANUAL_SAVE
        self.freeze(basis, names, TRACKED if exported else STAGE_FILES, stamps, now, contents=contents)
        if self.staged is None or self.staged['basis'] != basis or self.staged['frozenAt'] != now:
            return False
        self.staged['manualSnapshot'] = source['SnapshotId']
        return True

    def freeze(self, basis, names, watch, stamps, now, contents=None):
        contents = ([contents[name] for name in names] if contents is not None else
                    [shared_read(self.profile / name) for name in names])
        chosen = project(stamps, names)
        if (project(file_stamps(self.profile, TRACKED), names) != chosen or
                any(len(data) != size for data, (size, _) in zip(contents, chosen))):
            return
        # Divergent mirrors are no capturable save; Guardian's gate would reject the copy anyway.
        if basis not in EXPORT_BASES and (contents[0] != contents[1] or contents[2] != contents[3]):
            return
        try:
            raw = exported_raw(contents[names.index(RUN_SLOT_FILES[0])]) if basis in EXPORT_BASES else contents[0]
            if basis in (RUN_START_SAVE, RUN_START_EXPORT) and not opening_code(run_code(raw)):
                raise ProbeError('not a run opening save')
            if basis in (CONFIRMED_MANUAL_SAVE, CONFIRMED_MANUAL_EXPORT) and run_code(raw)[8:16] not in RECOGNIZED_WORDS:
                raise ProbeError('unrecognized confirmed manual save')
        except ProbeError:
            # An encounter's last save, a later save or an unknown shape: no opening save, never frozen.
            self.judged = stamps
            return
        self.count += 1
        target = self.root / str(self.count)
        for name, data, (_, mtime) in zip(names, contents, chosen):
            path = target / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            os.utime(path, ns=(mtime, mtime))
        previous = self.staged
        written = WORKING_FILES if basis not in EXPORT_BASES else EXPORT_WRITES
        self.staged = {'basis': basis, 'watch': watch, 'stamps': project(stamps, watch),
                       'sha256': hashlib.sha256(raw).hexdigest(), 'path': target, 'frozenAt': now,
                       'savedAtNs': max(stamp[1] for stamp in project(stamps, written))}
        if previous is not None:
            shutil.rmtree(previous['path'], ignore_errors=True)

    def bind(self, observed_ms, workers, cancelled, timeout=DEPARTURE_SAVE_TIMEOUT):
        """Return the frozen save if it is the last one the game finished before the departure event.

        No save may have been in flight at the event, the newest change seen before it must be the frozen
        one, and until the departure save lands every file must still be either frozen or newer than the
        event: an older stamp would be a hideout save written in between that was never frozen. A frozen
        opening save also needs the departure save to carry its run.
        """
        event_ns = observed_ms * 1_000_000
        if list(workers or ()) != [0, 0, 0]:
            raise ProbeError('hideout-save-in-progress-at-departure')
        staged = self.staged
        if staged is None:
            raise ProbeError('no-hideout-save-before-departure')
        if staged['savedAtNs'] >= event_ns:
            raise ProbeError('frozen-save-not-before-departure')
        watch = staged['watch']
        before = [project(stamps, watch) for at, stamps in self.observed if at * 1000 <= observed_ms]
        if not before or before[-1] != staged['stamps']:
            raise ProbeError('newest-hideout-save-not-frozen')
        # A working-file save is frozen whole; an opening export must also keep the working files away.
        guarded = WORKING_FILES if staged['basis'] not in EXPORT_BASES else TRACKED
        frozen = tuple(dict(zip(watch, staged['stamps'])).get(name) for name in guarded)

        def missed(stamps):
            return any(stamp is not None and stamp != old and stamp[1] < event_ns
                       for stamp, old in zip(project(stamps, guarded), frozen))

        # Polls made while the control script was still shutting down count too.
        if any(missed(stamps) for at, stamps in self.observed if at * 1000 > observed_ms):
            raise ProbeError('hideout-save-missed-before-departure')
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if cancelled():
                raise ProbeError('guardian-stopped')
            stamps = file_stamps(self.profile, TRACKED)
            if missed(stamps):
                raise ProbeError('hideout-save-missed-before-departure')
            landed = project(stamps, WORKING_FILES)
            if None not in landed and all(mtime >= event_ns for _, mtime in landed):
                if staged['basis'] != PRE_DEPARTURE_SAVE:
                    self.same_run(staged, deadline)
                return staged
            time.sleep(0.02)
        raise ProbeError('departure-save-not-observed')

    def same_run(self, staged, deadline):
        """The frozen opening save and the run save the game now holds belong to the same run."""
        frozen = (staged['path'] / RUN_SLOT_FILES[0] if staged['basis'] in EXPORT_BASES else
                  staged['path'] / WORKING_FILES[0])
        path = self.profile / WORKING_FILES[0]
        while True:
            stamp = file_stamps(self.profile, WORKING_FILES[:1])[0]
            try:
                raw = shared_read(path)
            except OSError:
                raw = None
            if (raw is not None and stamp is not None and len(raw) == stamp[0] and
                    file_stamps(self.profile, WORKING_FILES[:1])[0] == stamp):
                break
            if time.monotonic() >= deadline:
                raise ProbeError('departure-save-not-observed')
            time.sleep(0.02)
        try:
            opening = frozen.read_bytes()
            opening = self.inspect(exported_raw(opening) if staged['basis'] in EXPORT_BASES else opening)
            departed = self.inspect(raw)
        except (OSError, ProbeError) as error:
            raise ProbeError('run-identity-unreadable') from error
        if staged['basis'] in (RUN_START_SAVE, RUN_START_EXPORT) and not opening_code(opening['code']):
            raise ProbeError('not-a-run-opening-save')
        if departed['run'] != opening['run']:
            raise ProbeError('opening-save-of-another-run')

    def close(self):
        shutil.rmtree(self.root, ignore_errors=True)


def clear_stale_stages(root, age=3600):
    """Frozen copies left by a recorder that did not get to clean up."""
    try:
        for directory in root.iterdir():
            if directory.is_dir() and time.time() - directory.stat().st_mtime > age:
                shutil.rmtree(directory, ignore_errors=True)
    except FileNotFoundError:
        pass


def protect_departure(storage, profile, index, observed_ms, digest, staged, basis=PRE_DEPARTURE_SAVE,
                      observation_path=None, manual_snapshot=None):
    """Publish the frozen hideout save through Guardian's own hot-capture gates."""
    command = [str(TOOLS / 'CaptureDepartureSnapshot.exe'), str(storage), str(profile),
               str(index), str(int(observed_ms)), digest, str(staged), basis]
    if observation_path or manual_snapshot:
        command.append(str(observation_path) if observation_path else '')
    if manual_snapshot:
        command.append(manual_snapshot)
    result = subprocess.run(command,
                            capture_output=True, timeout=30, check=False)
    if result.returncode != 0:
        raise ProbeError('departure capture rejected: ' +
                         result.stderr.decode('utf-8', errors='replace').strip())
    try:
        receipt = json.loads(result.stdout.decode('utf-8-sig'))
    except (UnicodeError, ValueError) as error:
        raise ProbeError('departure capture receipt is unreadable') from error
    if (not isinstance(receipt, dict) or receipt.get('verified') is not True or receipt.get('kind') != 'departure' or
            receipt.get('basis') != basis or
            str(receipt.get('runSha256', '')).lower() != digest or receipt.get('routeIndex') != index or
            not SNAPSHOT_ID.fullmatch(str(receipt.get('id', '')))):
        raise ProbeError('departure capture receipt is incomplete')
    return receipt
