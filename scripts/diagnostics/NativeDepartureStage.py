"""Freeze the player's hideout saves while the recorder waits, and publish the one before the departure.

The save the game writes while handling the departure is no hideout to return to: its listeners have
already stored the chosen encounter as the run's position and reset the hideout for the next visit (the
lockbox refills, the route board moves past this node). So while the player is in the hideout each settled
hideout save is frozen in Guardian storage, and at the departure the frozen one is published, through
Guardian's hot-capture gates, only if the game finished nothing else before the event.
"""

import ctypes
from ctypes import wintypes
import hashlib
import json
import msvcrt
import os
from pathlib import Path
import shutil
import subprocess
import time

from NativeCheckpointProbe import TOOLS, ProbeError
from NativeDepartureRecord import SNAPSHOT_ID


WORKING_FILES = ('gamedata/R0A.save', 'gamedata/R0A.save-backup', 'gamedata/0P.save', 'gamedata/0P.save-backup',
                 'gamedata/nr.bin')
# Everything Guardian's hot capture reads, so the frozen copy passes the same gates as the live profile.
STAGE_FILES = WORKING_FILES + ('savedata/SAVEFILER0A/USR-DATA', 'savedata/SAVEFILER0A/params.json',
                               'savedata/SAVEFILER0A/ICN-ID')
STAGING = 'departure-staging'
PRE_DEPARTURE_SAVE = 'pre_departure_save'
DEPARTURE_SAVE_TIMEOUT = 8
SAVE_SETTLE = 0.3


def working_stamps(profile, names=WORKING_FILES):
    stamps = []
    for name in names:
        try:
            info = (profile / name).stat()
        except FileNotFoundError:
            return None
        stamps.append((info.st_size, info.st_mtime_ns))
    return tuple(stamps)


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


class HideoutStage:
    """Freezes the newest settled hideout save while the recorder waits for the route board's departure.

    That save is gone the moment the game writes its departure save, so it is copied into Guardian storage
    whenever it settles. Only saves written in this hideout are frozen: every working file must postdate
    the moment the recorder first saw the hideout playable, so an encounter's last save, which may carry
    a recognized state word, is never taken for the hideout. The copy keeps the files' timestamps, and the
    mirrors must match, so Guardian's capture reads it exactly as it would have read the live profile.
    """

    def __init__(self, storage, profile, playable, not_before_ns, clock=time.time):
        # The literal storage path Guardian passed in: it checks the staged path against the same string.
        self.root = Path(storage).absolute() / STAGING / f'{os.getpid()}-{time.time_ns()}'
        self.profile = Path(profile)
        self.playable = playable
        self.not_before_ns = not_before_ns
        self.clock = clock
        self.seen, self.since = None, None
        self.observed = []
        self.staged = None
        self.count = 0
        clear_stale_stages(Path(storage).absolute() / STAGING)

    def poll(self):
        now = self.clock()
        stamps = working_stamps(self.profile, STAGE_FILES)
        if stamps != self.seen:
            self.seen, self.since = stamps, now
            self.observed = (self.observed + [(now, stamps)])[-16:]
            return
        if (stamps is None or (self.staged is not None and self.staged['stamps'] == stamps) or
                now - self.since < SAVE_SETTLE or
                min(mtime for _, mtime in stamps[:len(WORKING_FILES)]) < self.not_before_ns or not self.playable()):
            return
        self.freeze(stamps, now)

    def freeze(self, stamps, now):
        contents = [shared_read(self.profile / name) for name in STAGE_FILES]
        if (working_stamps(self.profile, STAGE_FILES) != stamps or
                any(len(data) != size for data, (size, _) in zip(contents, stamps))):
            return
        # Divergent mirrors are no capturable save; Guardian's gate would reject the copy anyway.
        if contents[0] != contents[1] or contents[2] != contents[3]:
            return
        self.count += 1
        target = self.root / str(self.count)
        for name, data, (_, mtime) in zip(STAGE_FILES, contents, stamps):
            path = target / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            os.utime(path, ns=(mtime, mtime))
        previous = self.staged
        self.staged = {'stamps': stamps, 'sha256': hashlib.sha256(contents[0]).hexdigest(), 'path': target,
                       'frozenAt': now, 'savedAtNs': max(mtime for _, mtime in stamps[:len(WORKING_FILES)])}
        if previous is not None:
            shutil.rmtree(previous['path'], ignore_errors=True)

    def bind(self, observed_ms, workers, cancelled, timeout=DEPARTURE_SAVE_TIMEOUT):
        """Return the frozen save if it is the last one the game finished before the departure event.

        No save may have been in flight at the event, the newest change seen before it must be the frozen
        one, and until the departure save lands every file must still be either frozen or newer than the
        event: an older stamp would be a hideout save written in between that was never frozen.
        """
        event_ns = observed_ms * 1_000_000
        if list(workers or ()) != [0, 0, 0]:
            raise ProbeError('hideout-save-in-progress-at-departure')
        staged = self.staged
        if staged is None:
            raise ProbeError('no-hideout-save-before-departure')
        if staged['savedAtNs'] >= event_ns:
            raise ProbeError('frozen-save-not-before-departure')
        before = [stamps for at, stamps in self.observed if at * 1000 <= observed_ms]
        if not before or before[-1] != staged['stamps']:
            raise ProbeError('newest-hideout-save-not-frozen')
        frozen = staged['stamps'][:len(WORKING_FILES)]

        def missed(stamps):
            return stamps is not None and any(stamp != old and stamp[1] < event_ns
                                              for stamp, old in zip(stamps, frozen))

        # Polls made while the control script was still shutting down count too.
        if any(missed(stamps[:len(WORKING_FILES)]) for at, stamps in self.observed
               if at * 1000 > observed_ms and stamps is not None):
            raise ProbeError('hideout-save-missed-before-departure')
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if cancelled():
                raise ProbeError('guardian-stopped')
            stamps = working_stamps(self.profile)
            if missed(stamps):
                raise ProbeError('hideout-save-missed-before-departure')
            if stamps is not None and all(mtime >= event_ns for _, mtime in stamps):
                return staged
            time.sleep(0.02)
        raise ProbeError('departure-save-not-observed')

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


def protect_departure(storage, profile, index, observed_ms, digest, staged):
    """Publish the frozen hideout save through Guardian's own hot-capture gates."""
    result = subprocess.run([str(TOOLS / 'CaptureDepartureSnapshot.exe'), str(storage), str(profile),
                             str(index), str(int(observed_ms)), digest, str(staged)],
                            capture_output=True, timeout=30, check=False)
    if result.returncode != 0:
        raise ProbeError('departure capture rejected: ' +
                         result.stderr.decode('utf-8', errors='replace').strip())
    try:
        receipt = json.loads(result.stdout.decode('utf-8-sig'))
    except (UnicodeError, ValueError) as error:
        raise ProbeError('departure capture receipt is unreadable') from error
    if (not isinstance(receipt, dict) or receipt.get('verified') is not True or receipt.get('kind') != 'departure' or
            receipt.get('basis') != PRE_DEPARTURE_SAVE or
            str(receipt.get('runSha256', '')).lower() != digest or receipt.get('routeIndex') != index or
            not SNAPSHOT_ID.fullmatch(str(receipt.get('id', '')))):
        raise ProbeError('departure capture receipt is incomplete')
    return receipt
