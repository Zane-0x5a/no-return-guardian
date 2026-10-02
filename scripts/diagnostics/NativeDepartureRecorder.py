"""Guardian's departure recorder: wait for the player's route-board departure and record it.

Guardian starts it after an explicit protect or a native recovery of a preparation, or, with 出发自动保存,
whenever the game has run for a minute. It arms only in a playable hideout, installs one read-only probe on
the broadcast core, and writes the first `player-next-task` event's slots once the encounter is entered
(NativeDepartureRecord). With a profile it also freezes the hideout's saves and publishes the one before the
departure as a new preparation, which then owns the record (NativeDepartureStage). Guardian closes the
recorder's stdin to ask for a clean detach; every stop is written to the log with its reason.
"""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import threading
import time
import traceback

from NativeCheckpointProbe import FRIDA_DEPS, ProbeError, ReadOnlyProcess, verify_build
from NativeCheckpointTrigger import fingerprints, process_birth, verify_live
from NativeDepartureRecord import departure_path, departure_slots, new_departure, write_departure
from NativeDepartureStage import PRE_DEPARTURE_SAVE, HideoutStage, protect_departure
from NativeEncounterRestart import (arm_decision, code_restored, control_configuration, hideout_state,
                                    run_control, save_workers, task_identity)


RECORDING_LIMIT = 86400
MISSED_CHECKS = 3
ARRIVAL_TIMEOUT = 90


class Stopped(Exception):
    """The recorder ends without a record; Guardian reads the reason from the log."""

    def __init__(self, reason, **fields):
        super().__init__(reason)
        self.reason, self.fields = reason, fields


def game_exited(process):
    """Whether the game behind `process` has exited; False when that cannot be told."""
    try:
        return process is not None and process_birth(process) is None
    except ProbeError:
        return False


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('pid', type=int)
    parser.add_argument('--storage', type=Path, required=True)
    parser.add_argument('--snapshot', help="the protected preparation that owns this hideout's departure")
    parser.add_argument('--profile', type=Path, help='protect each departure as a new preparation of this profile')
    parser.add_argument('--log', type=Path, required=True)
    parser.add_argument('--duration', type=int, default=43200)
    args = parser.parse_args(argv)
    if args.pid <= 0 or not 30 <= args.duration <= RECORDING_LIMIT:
        parser.error(f'A positive PID and a 30-{RECORDING_LIMIT} second recording window are required')
    if not args.storage.is_absolute() or not args.log.is_absolute() or args.log.exists():
        parser.error('Recording requires an absolute storage and an unused absolute log path')
    if args.snapshot is None and args.profile is None:
        parser.error('Recording needs a protected preparation, a profile to protect departures from, or both')
    if args.profile is not None and (not args.profile.is_absolute() or not args.profile.is_dir()):
        parser.error('Departure protection requires an absolute, existing save profile')
    if args.snapshot is not None:
        departure_path(args.storage, args.snapshot)
        if not (args.storage / 'snapshots' / args.snapshot / 'manifest.json').is_file():
            parser.error('The protected preparation does not exist in this storage')
    return args


def watch_stdin(stopping):
    """Guardian closes our stdin to ask for a clean detach."""
    try:
        while sys.stdin.readline():
            pass
    except (OSError, ValueError):
        pass
    stopping.set()


def wait_for_hideout(args, process, base, birth, stopping, record, owner):
    """Arm only in a playable hideout; a recorder started during a rebuild waits for it.

    Returns the snapshot that still owns this hideout's departure. A protecting recorder waits through
    encounters, but the owner of a hideout left unrecorded does not carry over to the next one.
    """
    deadline = time.monotonic() + args.duration
    while True:
        observed = hideout_state(process, base)
        decision = arm_decision(observed)
        if decision == 'arm':
            record({'kind': 'departure-recorder-armed', 'observed': observed})
            return owner
        if decision == 'stop':
            if args.profile is None:
                raise Stopped('encounter-before-armed', observed=observed)
            if owner is not None:
                record({'kind': 'departure-fallback-dropped', 'snapshotId': owner, 'observed': observed})
                owner = None
        if stopping.is_set() or time.monotonic() > deadline or process_birth(process) != birth:
            # A protecting recorder waits through encounters, so its own stops stay quiet.
            raise Stopped('hideout-not-reached' if args.profile is None or time.monotonic() > deadline else
                          'guardian-stopped' if stopping.is_set() else 'game-exited', observed=observed)
        time.sleep(0.5)


class HideoutWatch:
    """The board probe's cancel check: freezes each settled hideout save, and ends the wait once Guardian
    stops, the game exits, the run is left, or an encounter starts without a captured departure. The player
    may stay in the hideout as long as they like."""

    def __init__(self, process, base, birth, stage, stopping, record):
        self.process, self.base, self.birth = process, base, birth
        self.stage, self.stopping, self.record = stage, stopping, record
        self.checked, self.encounter, self.reason, self.stage_error = 0.0, 0, None, None

    def __call__(self):
        if self.stage is not None:
            try:
                self.stage.poll()
            except OSError as error:
                # A save file held by the game this instant; the next settled save is frozen instead.
                if self.stage_error is None:
                    self.stage_error = str(error)
                    self.record({'kind': 'departure-stage-skipped', 'reason': str(error)})
        if self.stopping.is_set():
            self.reason = 'guardian-stopped'
        elif time.monotonic() - self.checked >= 1:
            self.checked = time.monotonic()
            if process_birth(self.process) != self.birth:
                self.reason = 'game-exited'
            else:
                observed = hideout_state(self.process, self.base)
                self.encounter = self.encounter + 1 if arm_decision(observed) == 'stop' else 0
                if self.encounter >= MISSED_CHECKS:
                    self.reason = 'encounter-entered-without-capture'
                elif observed['runActive'] != 1:
                    self.reason = 'run-left'
        return self.reason is not None


def protect(args, stage, captured, index, owner, stopping, record):
    """Publish the hideout save frozen before the departure as the preparation that owns the record.

    Returns the record's owner and the new snapshot (None when nothing was published). Without a fallback
    owner a departure that cannot be protected is not recorded at all.
    """
    if stage is None:
        return owner, None
    try:
        # Bound straight away: the departure save overwrites the hideout save within a second.
        frozen = stage.bind(captured['time'], captured.get('workers'), stopping.is_set)
        record({'kind': 'departure-save-bound', 'runSha256': frozen['sha256'], 'basis': PRE_DEPARTURE_SAVE,
                'savedAtMs': frozen['savedAtNs'] // 1_000_000, 'eventMs': captured['time']})
        receipt = protect_departure(args.storage, args.profile, index, captured['time'], frozen['sha256'],
                                    frozen['path'])
        record({'kind': 'departure-protected', 'snapshotId': receipt['id'], 'receipt': receipt})
        return receipt['id'], receipt['id']
    except (ProbeError, OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        record({'kind': 'departure-protect-failed', 'reason': str(error), 'fallbackSnapshotId': owner})
        if owner is None or stopping.is_set():
            raise Stopped('guardian-stopped' if stopping.is_set() else f'departure-not-protected: {error}')
        return owner, None


def await_arrival(process, base, birth, stopping):
    """The encounter task the departure entered, or None when it was not entered in time."""
    deadline = time.monotonic() + ARRIVAL_TIMEOUT
    while time.monotonic() < deadline and process_birth(process) == birth and not stopping.is_set():
        observed = hideout_state(process, base)
        if observed['taskWord'] == 1 and observed['runActive'] == 1:
            return task_identity(process, observed['task'])
        time.sleep(0.25)
    return None


def record_departure(argv=None):
    args = arguments(argv)
    process = session = stage = code = None
    stopping = threading.Event()
    threading.Thread(target=watch_stdin, args=(stopping,), daemon=True).start()
    with args.log.open('x', encoding='utf-8') as log:
        def record(payload):
            log.write(json.dumps(payload, ensure_ascii=False) + '\n')
            log.flush()
            print(json.dumps(payload, ensure_ascii=False), flush=True)

        try:
            process = ReadOnlyProcess(args.pid)
            image = process.image_path().read_bytes()
            digest = verify_build(image)
            base = process.image_base()
            birth = process_birth(process)
            if birth is None:
                raise Stopped('game-exited')
            code = fingerprints(image, 'encounter-control')
            verify_live(process, base, code)
            record({'kind': 'departure-recorder-start', 'pid': args.pid, 'birth': birth, 'time': time.time(),
                    'snapshotId': args.snapshot, 'autoProtect': args.profile is not None, 'sha256': digest})
            # Owns this hideout's departure when it cannot be protected as a preparation of its own.
            owner = wait_for_hideout(args, process, base, birth, stopping, record, args.snapshot)
            if args.profile is not None:
                def playable():
                    current = hideout_state(process, base)
                    return (arm_decision(current) == 'arm' and current['runActive'] == 1 and
                            save_workers(process, base) == [0, 0, 0])

                stage = HideoutStage(args.storage, args.profile, playable, time.time_ns())
            sys.path.insert(0, str(FRIDA_DEPS))
            import frida

            session = frida.attach(args.pid)
            if process_birth(process) != birth:
                raise ProbeError('Process identity changed during attach')
            watch = HideoutWatch(process, base, birth, stage, stopping, record)
            events, errors = run_control(session, control_configuration('record', args.pid, base, code, args.duration),
                                         record, args.duration + 5, watch)
            captured = next((event for event in events if event.get('kind') == 'departure-captured'), None)
            if errors:
                record({'kind': 'departure-recorder-cleanup', 'errors': errors})
            if captured is None:
                raise Stopped(watch.reason or 'no-departure-captured')
            raw, index = departure_slots(captured.get('slots'), captured.get('count'))
            owner, protected = protect(args, stage, captured, index, owner, stopping, record)
            session.detach()
            session = None
            arrival = await_arrival(process, base, birth, stopping)
            departure = new_departure(owner, digest, args.pid, birth, captured, raw, index, protected is not None,
                                      arrival)
            if arrival is None:
                raise Stopped('guardian-stopped' if stopping.is_set() else 'encounter-not-entered',
                              departure=departure)
            path = write_departure(args.storage, departure)
            record({'kind': 'departure-recorded', 'path': str(path), 'departure': departure,
                    'recordSha256': hashlib.sha256(path.read_bytes()).hexdigest()})
            return 0
        except Stopped as stop:
            record({'kind': 'departure-recorder-stopped', 'reason': stop.reason, **stop.fields})
            return 4
        except (ProbeError, OSError, ValueError) as error:
            # The game closing fails the next read before any check sees the exit: that is a game exit.
            if game_exited(process):
                record({'kind': 'departure-recorder-stopped', 'reason': 'game-exited', 'error': str(error)})
                return 4
            record({'kind': 'departure-recorder-stopped', 'reason': str(error)})
            return 3
        except Exception as error:
            # Guardian only tells the player that the recorder stopped; the traceback is for a bug report.
            record({'kind': 'departure-recorder-stopped', 'reason': f'unexpected {type(error).__name__}: {error}',
                    'traceback': traceback.format_exc(limit=12)})
            return 3
        finally:
            if stage is not None:
                stage.close()
            if session is not None:
                try:
                    session.detach()
                except Exception:
                    pass
            if process is not None:
                try:
                    restored = code_restored(process, process.image_base(), code) if code is not None else None
                    record({'kind': 'departure-recorder-detached', 'codeRestored': restored})
                except Exception as error:
                    record({'kind': 'departure-recorder-detached', 'codeRestored': False, 'error': str(error)})
                process.close()


if __name__ == '__main__':
    raise SystemExit(record_departure())
