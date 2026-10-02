"""Build-pinned, bounded intrusive native checkpoint experiment, not a product restore."""

import argparse
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import json
import multiprocessing
import os
from pathlib import Path
import sys
import threading
import time
from types import SimpleNamespace

from NativeCheckpointProbe import FRIDA_DEPS, ReadOnlyProcess, ProbeError, verify_build
from InspectPeVa import read_layout, va_to_offset


CODE_RVAS = (0xFDDBF0, 0xFF6920, 0x105F830, 0x1046F80, 0x1046FC0, 0x1B6EA60, 0x133EBB0)
LIFECYCLE_RVAS = (0x1048960, 0x10434C0, 0x1042AA0, 0x1042B80, 0x1200AC0,
                  0x11F9300, 0x1049BF0, 0x1046290, 0x105E930, 0x1B6EA60)
RESUME_RVAS = (0xFF6920, 0x133EBB0, 0x1B6EA60, 0xFE7B10, 0xFE96F0,
               0xFD7870, 0x1042680, 0x103F2C0, 0x1046290, 0x1BDF840)
LOAD_RVAS = (0xFE96F0, 0xFDCD90, 0x1B80080, 0xFD7870, 0x1042680,
             0x103F2C0, 0x1046290, 0x1B6E000, 0x1BDEED0, 0x1B6FA70,
             0x1B7D080, 0x1BDB630, 0xFE8FC0, 0x1048520, 0x1049BF0)
COLLECTION_RVAS = LOAD_RVAS + (0x1048960, 0x10434C0, 0x1200AC0, 0x11F9300,
    0x120AF40, 0x120D090, 0x120DAC0, 0x1B7FC60, 0xD95530, 0xD966F0,
    0xDACC20, 0xDAFEA0, 0xDAF950, 0xDC1780, 0x1B6EA60, 0x105E930)
RECOVERY_RVAS = (0x133EBB0, 0x133EC5E, 0xFE7B10, 0xFE96F0, 0x1B7FC60, 0x1E2F880,
                 0x120AF40, 0x120D090, 0xFD7870, 0x1042680, 0x103F2C0,
                 0x1B6E000, 0x1BDEED0, 0xDC1780, 0x1B6EA60, 0x1C55DF0, 0x1C55A3C, 0x1C57BB0)
RESULTS_RVAS = (0x104C700, 0x1B37160, 0x1B37310, 0x1B39BF0, 0x1E39660,
                0x1E39880, 0x1E35D60, 0x1E4F190, 0x1E4F470, 0x1E4F380,
                0x133D910, 0x133D950, 0x1B6EA60, 0x1E3B920, 0x1E4DF10, 0x1247360, 0x124C1D0,
                0xD49630, 0xD49530)
ENCOUNTER_RVAS = (0x133EC5E, 0x1B843A0, 0x1B83F70, 0x1B6E000, 0xDC1780, 0x1B6EA60, 0xFE7B10, 0x1200AC0)
IN_HIDEOUT = ('Already in a playable hideout: returning to a preparation needs the No Return menu, '
              'the results page or an encounter; nothing was changed')
# Where a recovery starts. 'auto' is worked out from the game's state when the recovery begins.
ENTRIES = ('menu', 'results', 'mid-encounter', 'auto')


@dataclass(frozen=True)
class RecoveryRequest:
    """What Guardian asked for, parsed once from the command line and never rewritten afterwards.

    rebuilt_hideout is Guardian's assertion that a playable hideout is this snapshot's own rebuild in this
    game process with no departure since; only an automatic encounter restart may then depart from it.
    """
    storage: Path
    snapshot: str
    profile: Path
    entry: str
    redeploy: bool
    rebuilt_hideout: bool


def resolve_entry(observed, unloaded_menu):
    """Where an automatic recovery starts. Anything that is neither playable nor the unloaded menu goes to
    the results entry, whose own gate rejects every state that is not a recognized results page."""
    if observed['runActive'] == 1 and observed['runInitialized'] == 1:
        if observed['taskWord'] == 1:
            return 'mid-encounter'
        if observed['taskWord'] == 2:
            return 'hideout'
    try:
        unloaded_menu()
        return 'menu'
    except ProbeError:
        return 'results'


STILL_ACTIVE = 259


def process_birth(process):
    """The process's creation time while it runs, None once it has exited.

    Comparing this with the birth taken at attach is how every script checks that it still has the same
    live game. A held handle keeps reporting its creation time after the process exits, so the exit code
    is read first; without it that comparison never noticed the game closing.
    """
    process.kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    process.kernel.GetExitCodeProcess.restype = wintypes.BOOL
    exit_code = wintypes.DWORD()
    if not process.kernel.GetExitCodeProcess(process.handle, ctypes.byref(exit_code)):
        raise ProbeError('Cannot read the game process exit code')
    if exit_code.value != STILL_ACTIVE:
        return None
    signature = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    process.kernel.GetProcessTimes.argtypes = signature
    process.kernel.GetProcessTimes.restype = wintypes.BOOL
    values = [wintypes.FILETIME() for _ in range(4)]
    if not process.kernel.GetProcessTimes(process.handle, *(ctypes.byref(value) for value in values)):
        raise ProbeError('Cannot bind experiment to process creation time')
    return (values[0].dwHighDateTime << 32) | values[0].dwLowDateTime


def fingerprints(image, mode='observe'):
    base, headers, sections = read_layout(image)
    result = []
    selected = RESUME_RVAS if mode == 'resume-preparation' else (
        LIFECYCLE_RVAS if mode == 'lifecycle' else CODE_RVAS)
    if mode == 'load-observe':
        selected = LOAD_RVAS
    if mode == 'collection-observe':
        selected = COLLECTION_RVAS
    if mode == 'recover-preparation':
        selected = RECOVERY_RVAS
    if mode == 'results-exit':
        selected = RESULTS_RVAS
    if mode == 'encounter-control':
        selected = ENCOUNTER_RVAS
    for rva in selected:
        offset, section = va_to_offset(base + rva, base, headers, sections)
        if section != '.text':
            raise ProbeError('Expected executable code section')
        result.append({'rva': rva, 'bytes': image[offset:offset + 32].hex()})
    return result


def verify_live(process, base, records):
    for record in records:
        expected = bytes.fromhex(record['bytes'])
        if process.read(base + record['rva'], len(expected)) != expected:
            raise ProbeError(f"Live fingerprint mismatch at {record['rva']:#x}")


def approved_context(path, identity, now, task_word=1):
    records = [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines() if line]
    headers = [record for record in records if record.get('kind') == 'host-preflight']
    if len(headers) != 1 or headers[0].get('mode') != 'observe':
        raise ProbeError('Requires one prior observation, not another trigger log')
    header = headers[0]
    if any(header.get(key) != value for key, value in identity.items()):
        raise ProbeError('Observation belongs to a different process or build')
    if not 0 <= now - header['time'] <= 300:
        raise ProbeError('Observation expired; maximum age is five minutes')
    if not any(record.get('kind') == 'host-detached' and record.get('codeRestored') is True
               for record in records):
        raise ProbeError('Observation did not prove hook removal')
    candidates = [record for record in records if record.get('kind') == 'idle-observed'
                  and record.get('idleCount', 0) >= 30
                  and record.get('rogue') == 1 and record.get('taskWord') == task_word]
    contexts = {(record['owner'], record['stateObject'], record['caller'], record['task'])
                for record in candidates}
    if len(contexts) != 1:
        raise ProbeError('Need one stable matching task and native update context')
    return next(iter(contexts))


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('pid', type=int)
    parser.add_argument('--mode', choices=('observe', 'trigger', 'lifecycle', 'resume-preparation',
                                         'load-observe', 'collection-observe', 'recover-preparation'), default='observe')
    parser.add_argument('--duration', type=int, default=15)
    parser.add_argument('--log', type=Path, required=True)
    parser.add_argument('--observation', type=Path)
    parser.add_argument('--confirm-intrusive-experiment', action='store_true')
    parser.add_argument('--confirm-restart-current-checkpoint', action='store_true')
    parser.add_argument('--lifecycle-content', action='store_true')
    parser.add_argument('--collection-profile', type=Path)
    parser.add_argument('--confirm-native-resume-current-preparation', action='store_true')
    parser.add_argument('--recovery-storage', type=Path)
    parser.add_argument('--recovery-snapshot')
    parser.add_argument('--recovery-profile', type=Path)
    parser.add_argument('--entry', choices=ENTRIES,
                        help='where a recovery starts: the unloaded No Return menu, the death results page, an '
                             'active encounter (abandoned first), or worked out from the game state')
    parser.add_argument('--redeploy', action='store_true')
    parser.add_argument('--hideout-rebuilt-from-snapshot', action='store_true',
                        help="Guardian's assertion that the playable hideout is this snapshot's own rebuild in "
                             'this game process, with no departure since; only then may a restart depart from it')
    args = parser.parse_args(argv)
    maximum_duration = 600 if args.mode in ('lifecycle', 'load-observe') else 60
    if args.mode == 'collection-observe':
        maximum_duration = 1800
    if args.mode == 'recover-preparation':
        maximum_duration = 300
    if args.pid <= 0 or not 5 <= args.duration <= maximum_duration:
        parser.error(f'A positive PID and duration from 5 to {maximum_duration} seconds are required')
    if not args.confirm_intrusive_experiment:
        parser.error('Even observation temporarily instruments the game; explicit confirmation required')
    if args.mode == 'trigger' and (not args.observation or not args.confirm_restart_current_checkpoint):
        parser.error('Trigger requires a recent observation and explicit restart confirmation')
    if args.mode != 'trigger' and args.confirm_restart_current_checkpoint:
        parser.error('Restart confirmation is only valid for trigger mode')
    if args.mode == 'resume-preparation':
        if not args.observation or not args.confirm_native_resume_current_preparation:
            parser.error('Native resume requires recent observation and explicit playable preparation confirmation')
    elif args.confirm_native_resume_current_preparation:
        parser.error('Native resume confirmation is only valid for resume-preparation mode')
    if args.mode not in ('trigger', 'resume-preparation') and args.observation:
        parser.error('Observation authorization is only valid for native control modes')
    if args.lifecycle_content and args.mode != 'lifecycle':
        parser.error('Content evidence is only valid for lifecycle mode')
    if args.mode != 'collection-observe' and args.collection_profile is not None:
        parser.error('Collection profile is only valid for collection-observe mode')
    if args.mode == 'resume-preparation':
        parser.error('Native preparation resume is quarantined after the 2026-09-06 A-pose/sliding failure; '
                     'confirmation flags cannot override this block. No game process was opened.')
    recovery_values = (args.recovery_storage, args.recovery_snapshot, args.recovery_profile)
    args.recovery = None
    if args.mode == 'recover-preparation':
        if not all(recovery_values) or args.entry is None:
            parser.error('Recovery requires a formal source, selected profile and an entry')
        if args.hideout_rebuilt_from_snapshot and not (args.redeploy and args.entry == 'auto'):
            parser.error('Departing from the current hideout is only an automatic encounter restart')
        if not args.log.is_absolute() or not args.log.parent.is_dir() or args.log.exists():
            parser.error('Recovery requires an unused absolute log path in an existing directory')
        if args.log.with_suffix('.fatal.json').exists():
            parser.error('Recovery failure journal already exists; use an unused log path')
        if not args.recovery_storage.is_absolute() or not args.recovery_profile.is_absolute():
            parser.error('Recovery source and selected profile paths must be absolute')
        args.recovery = RecoveryRequest(args.recovery_storage, args.recovery_snapshot, args.recovery_profile,
                                        args.entry, args.redeploy, args.hideout_rebuilt_from_snapshot)
    elif (any(value is not None for value in recovery_values) or args.entry is not None or
          args.redeploy or args.hideout_rebuilt_from_snapshot):
        parser.error('Recovery parameters are only valid for recover-preparation mode')
    if args.mode == 'collection-observe':
        if args.collection_profile is None or not args.collection_profile.is_absolute() or not args.collection_profile.is_dir():
            parser.error('Collection requires an existing absolute, explicitly selected profile directory')
        if not args.log.is_absolute() or not args.log.parent.is_dir():
            parser.error('Collection requires an absolute log path in an existing evidence directory')
        outputs = [args.log] + [args.log.with_suffix(suffix) for suffix in
                               ('.control.json', '.payloads', '.files')]
        if any(path.exists() for path in outputs):
            parser.error('Collection output already exists; will not reuse or overwrite evidence')
    return args


def wait_worker(worker, timeout, shutdown):
    worker.join(timeout)
    if not worker.is_alive() and worker.exitcode == 0:
        return 0
    try:
        shutdown()
    finally:
        worker.join(5)
        if worker.is_alive():
            worker.terminate()
            worker.join(5)
        if worker.is_alive():
            raise ProbeError('Experiment worker could not be stopped')
    return 5


def stop_guarded_process(kernel, handle):
    status = kernel.WaitForSingleObject(handle, 0)
    termination_error = None
    if status == 258:
        if not kernel.TerminateProcess(handle, 5):
            termination_error = ctypes.get_last_error()
        status = kernel.WaitForSingleObject(handle, 10000)
    if status != 0:
        raise ProbeError(f'Target exit was not verified: wait={status}, terminationError={termination_error}')


def experiment_worker(argv, recovery_preflight=None):
    try:
        raise SystemExit(main(argv, recovery_preflight=recovery_preflight))
    except (ProbeError, OSError, ValueError) as error:
        raise SystemExit(f'EXPERIMENT_FAILED: {error}')


def supervise_worker(worker, timeout, shutdown, rollback):
    try:
        result = wait_worker(worker, timeout, shutdown)
    except BaseException:
        shutdown()
        worker.join(5)
        if worker.is_alive():
            worker.terminate()
            worker.join(5)
        if worker.is_alive():
            raise ProbeError('Interrupted recovery worker could not be stopped; disk rollback deferred')
        rollback()
        raise
    if result != 0:
        rollback()
    return result


def supervised_main(argv=None):
    values = list(sys.argv[1:] if argv is None else argv)
    args = arguments(values)
    if args.mode == 'recover-preparation' and any(args.log.with_suffix(suffix).exists()
                                               for suffix in ('.results.jsonl', '.encounter.jsonl', '.supervisor.jsonl')):
        raise ProbeError('Recovery navigation/supervisor journal already exists; use an unused log path')
    recovery_lock = None
    if args.mode == 'recover-preparation':
        from NativeRecoveryLock import RecoveryLock
        recovery_lock = RecoveryLock(args.pid)
    process = None
    guard_handle = None
    worker = None
    prefetch = None
    try:
        process = ReadOnlyProcess(args.pid)
        verify_build(process.image_path().read_bytes())
        birth = process_birth(process)
        if birth is None:
            raise ProbeError('The game has exited')
        recovery_preflight = None
        if args.mode == 'recover-preparation':
            from NativeRecoverySource import prepare_source, secure_live_state, verify_unloaded_menu
            from NativeEncounterRestart import hideout_state
            request = args.recovery
            base = process.image_base()
            departure = None
            if request.redeploy:
                from NativeDepartureRecord import load_departure
                departure = load_departure(request.storage, request.snapshot)
            entry = request.entry
            if entry == 'auto':
                # Work out where the player is: the menu, the results page, alive in an encounter (abandon
                # first), or already back in the hideout.
                entry = resolve_entry(hideout_state(process, base), lambda: verify_unloaded_menu(process, base))
                if entry == 'hideout':
                    if not (request.redeploy and request.rebuilt_hideout):
                        raise ProbeError(IN_HIDEOUT)
                    # The hideout this preparation was rebuilt into: only its departure is left to replay.
                    from NativeEncounterRestart import replay_in_hideout
                    prepare_source(request.storage, request.snapshot, request.profile, True)
                    return replay_in_hideout(process, base, args.pid, birth, departure, args.log)
            if entry == 'mid-encounter':
                observed = hideout_state(process, base)
                if observed['runActive'] != 1 or observed['runInitialized'] != 1 or observed['taskWord'] != 1:
                    raise ProbeError('Mid-encounter entry requires an active No Return encounter; nothing was changed')
            if entry == 'menu':
                verify_unloaded_menu(process, base)
            else:
                from NativeResultsExit import SessionPrefetch
                prefetch = SessionPrefetch(args.pid)
            source = prepare_source(request.storage, request.snapshot, request.profile, request.redeploy)
            verify_live(process, base, fingerprints(process.image_path().read_bytes(), args.mode))
            if request.redeploy or entry == 'mid-encounter':
                verify_live(process, base, fingerprints(process.image_path().read_bytes(), 'encounter-control'))
            results_exit = None
            abandon = None
            if entry != 'menu':
                from NativeResultsExit import finish_results
                results_code = fingerprints(process.image_path().read_bytes(), 'results-exit')
                verify_live(process, base, results_code)

                def identity_check():
                    if process_birth(process) != birth or process.image_base() != base:
                        raise ProbeError('Results exit target process changed')

                if entry == 'mid-encounter':
                    from NativeEncounterRestart import abandon_encounter
                    abandon, prefetch = abandon_encounter(process, base, args.pid, birth, args.log, identity_check,
                                                          fingerprints(process.image_path().read_bytes(),
                                                                       'encounter-control'), prefetch)
                results_exit = finish_results(process, base, args.pid, birth, args.log, identity_check,
                                              results_code, prefetch=prefetch)
                prefetch = None
            undo = secure_live_state(process, base, request.storage, request.profile)
            recovery_preflight = {'pid': args.pid, 'birth': birth, 'base': base, 'source': source,
                                  'undo': undo, 'resultsExit': results_exit, 'abandon': abandon,
                                  'departure': departure, 'entry': entry}
        kernel = process.kernel
        guard_handle = kernel.OpenProcess(0x101001, False, args.pid)
        if not guard_handle:
            raise ProbeError('Cannot establish bounded-experiment shutdown guard')
        guard = SimpleNamespace(kernel=kernel, handle=guard_handle)
        if process_birth(guard) != birth:
            raise ProbeError('Process identity changed before guard acquisition')
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        kernel.TerminateProcess.restype = wintypes.BOOL
        args.log.parent.mkdir(parents=True, exist_ok=True)
        guard_log = args.log.with_suffix('.supervisor.jsonl')
        with guard_log.open('x', encoding='utf-8') as output:
            def note(kind, **fields):
                line = json.dumps({'kind': kind, 'time': time.time(), 'pid': args.pid,
                                   'birth': birth, **fields})
                output.write(line + '\n')
                output.flush()
                os.fsync(output.fileno())
                print(line, flush=True)

            def shutdown():
                stop_guarded_process(kernel, guard_handle)
                note('supervisor-target-exited', restorationClaim=False)

            note('supervisor-armed', timeoutSeconds=args.duration + 45,
                 failurePolicy='stop exact guarded game process on worker failure or timeout')
            if recovery_preflight is not None:
                note('recovery-preflight-secured', undo=recovery_preflight['undo'], entry=recovery_preflight['entry'],
                     redeploy=recovery_preflight['departure'] is not None,
                     snapshotId=args.recovery.snapshot, sourceSha256=recovery_preflight['source'].configuration['rawSha256'])
            worker = multiprocessing.get_context('spawn').Process(
                target=experiment_worker, args=(values, recovery_preflight))

            def rollback():
                if args.mode != 'recover-preparation':
                    return
                from NativeRecoverySource import rollback_failed_recovery
                try:
                    receipt = rollback_failed_recovery(args.log, args.recovery.storage, args.recovery.profile,
                                                       {'pid': args.pid, 'birth': birth})
                    note('recovery-disk-rollback', **receipt)
                except (ProbeError, OSError, ValueError) as error:
                    note('recovery-disk-rollback-failed', reason=str(error), inGameAcceptance=False)

            worker.start()
            result = supervise_worker(worker, args.duration + 45, shutdown, rollback)
            note('supervisor-finished', workerExitCode=worker.exitcode, result=result)
            return result
    finally:
        if prefetch is not None:
            prefetch.close()
        if worker is not None and worker.pid is not None and worker.is_alive():
            worker.terminate()
            worker.join(5)
        if guard_handle:
            process.kernel.CloseHandle(guard_handle)
        if process is not None:
            process.close()
        if recovery_lock is not None:
            recovery_lock.close()


def main(argv=None, recovery_preflight=None):
    worker_started = time.monotonic()
    args = arguments(argv)
    process = ReadOnlyProcess(args.pid)
    session = None
    script = None
    log = None
    log_lock = threading.Lock()
    done = threading.Event()
    results = []
    collection_archive = None
    collection_failure = threading.Event()
    collection_errors = []
    file_witness = None
    transfer_heartbeat = None
    close_recovery_session = None

    def transfer_failed(error):
        collection_errors.append(str(error))
        collection_failure.set()
        done.set()

    def record(payload):
        with log_lock:
            results.append(payload)
            line = json.dumps(payload, ensure_ascii=False)
            log.write(line + '\n')
            log.flush()
            os.fsync(log.fileno())
            print(line, flush=True)

    def message(message, data):
        payload = message.get('payload') if message.get('type') == 'send' else {
            'kind': 'script-error', 'details': message}
        try:
            record(payload)
            if payload.get('kind') == 'collection-payload':
                if collection_archive is None:
                    raise ProbeError('Unexpected binary evidence outside collection mode')
                collection_archive.submit(payload, data)
            elif data is not None:
                raise ProbeError('Unindexed binary evidence')
            if payload.get('kind') in ('stopped', 'script-error', 'recovery-fatal'):
                done.set()
        except Exception as error:
            if script is not None and collection_archive is not None:
                try:
                    script.post({'type': 'collection-ack', 'error': str(error)})
                except Exception:
                    pass
            transfer_failed(error)

    try:
        path = process.image_path()
        image = path.read_bytes()
        digest = verify_build(image)
        base = process.image_base()
        code = fingerprints(image, args.mode)
        verify_live(process, base, code)
        identity = {'pid': args.pid, 'birth': process_birth(process), 'sha256': digest,
                    'path': str(path), 'base': hex(base)}
        if identity['birth'] is None:
            raise ProbeError('The game has exited')
        configuration = {'pid': args.pid, 'base': hex(base), 'fingerprints': code,
                         'mode': args.mode, 'duration': args.duration,
                         'lifecycleContent': args.lifecycle_content}
        recovery_source = None
        if args.mode == 'recover-preparation':
            configuration['birth'] = str(identity['birth'])
            configuration['failurePath'] = str(args.log.with_suffix('.fatal.json').resolve())
            if Path(configuration['failurePath']).exists():
                raise ProbeError('Recovery failure journal already exists')
            from NativeRecoverySource import prepare_source, secure_live_state, verify_unloaded_menu
            if recovery_preflight is None:
                if args.recovery.entry != 'menu' or args.recovery.redeploy:
                    raise ProbeError('Results, mid-encounter and redeploy entries require the supervised preflight')
                recovery_source = prepare_source(args.recovery.storage, args.recovery.snapshot, args.recovery.profile)
                undo = secure_live_state(process, base, args.recovery.storage, args.recovery.profile)
            else:
                if any(recovery_preflight[key] != value for key, value in
                       (('pid', args.pid), ('birth', identity['birth']), ('base', base))):
                    raise ProbeError('Prepared recovery belongs to a different process')
                recovery_source, undo = recovery_preflight['source'], recovery_preflight['undo']
                verify_unloaded_menu(process, base)
                configuration['resultsExit'] = recovery_preflight.get('resultsExit')
                configuration['abandon'] = recovery_preflight.get('abandon')
            configuration['recoverySource'] = recovery_source.configuration
            configuration['recoveryUndo'] = undo
        if args.mode in ('trigger', 'resume-preparation'):
            owner, state_object, caller, task = approved_context(
                args.observation, identity, time.time(), 2 if args.mode == 'resume-preparation' else 1)
            if caller != hex(base + 0x133EC36):
                raise ProbeError('Unrecognized state-update dispatcher')
            configuration.update(owner=owner, stateObject=state_object, caller=caller, task=task)
        args.log.parent.mkdir(parents=True, exist_ok=True)
        log = args.log.open('x', encoding='utf-8')
        record({'kind': 'host-preflight', 'time': time.time(), 'mode': args.mode,
                'lifecycleContent': args.lifecycle_content, **identity})
        if recovery_source is not None:
            record({'kind': 'recovery-source-verified', **recovery_source.configuration,
                    'undo': configuration['recoveryUndo'], 'resultsExit': configuration.get('resultsExit')})
        control_path = args.log.with_suffix('.control.json')
        if args.mode == 'collection-observe':
            from NativeCollectionArchive import CollectionArchive
            from NativeCollectionFiles import FileWitness

            def acknowledge(identifier):
                record({'kind': 'collection-payload-stored', 'payloadId': identifier})
                script.post({'type': 'collection-ack', 'payloadId': identifier})

            collection_archive = CollectionArchive(args.log.with_suffix('.payloads'), identity, acknowledge)
            file_witness = FileWitness(args.collection_profile, args.log.with_suffix('.files'), record)
            file_witness.poll(True)
            with control_path.open('x', encoding='utf-8') as control:
                json.dump({'id': 0, 'action': 'idle'}, control)
        sys.path.insert(0, str(FRIDA_DEPS))
        import frida
        session = frida.attach(args.pid)
        if args.mode == 'recover-preparation':
            from NativeRecoverySession import watch_recovery_session
            close_recovery_session = watch_recovery_session(session, process, record, transfer_failed)
        if process_birth(process) != identity['birth']:
            raise ProbeError('Process identity changed during attach')
        source_path = (Path(__file__).with_name('NativeCheckpointLifecycle.js')
                       if args.mode == 'lifecycle' else Path(__file__).with_suffix('.js'))
        if args.mode == 'resume-preparation':
            source_path = Path(__file__).with_name('NativePreparationResume.js')
        if args.mode in ('load-observe', 'collection-observe'):
            source_path = Path(__file__).with_name('NativeLoadObserver.js')
        if args.mode == 'recover-preparation':
            source_path = Path(__file__).with_name('NativePreparationRecovery.js')
        source = source_path.read_text(encoding='utf-8')
        if args.mode == 'recover-preparation':
            source = '\n'.join(Path(__file__).with_name(name).read_text(encoding='utf-8')
                               for name in ('NativeRecoveryContract.js', 'NativeRecoveryBridge.js')) + '\n' + source
        if args.mode == 'collection-observe':
            source = '\n'.join(Path(__file__).with_name(name).read_text(encoding='utf-8')
                               for name in ('NativeCollectionCodec.js', 'NativeCollectionExtension.js')) + '\n' + source
        script = session.create_script('globalThis.nativeCheckpointConfiguration = ' +
                                       json.dumps(configuration) + ';\n' + source)
        script.on('message', message)
        if args.mode == 'collection-observe':
            from NativeCollectionControl import start_transfer_heartbeat
            transfer_heartbeat = start_transfer_heartbeat(script, collection_archive, transfer_failed)
        script.load()
        if recovery_source is not None:
            script.post({'type': 'recovery-source'}, recovery_source.packet)
        if args.mode == 'collection-observe':
            from NativeCollectionControl import run_collection
            run_collection(script, collection_archive, control_path, args.duration, done, record, file_witness)
        else:
            done.wait(args.duration + 5)
        if (args.mode == 'recover-preparation' and args.recovery.redeploy and
                any(item.get('kind') == 'native-recovery-rebuilt' for item in results)):
            # The recovery bridge is fully removed before the departure script loads, so a failed
            # departure leaves a rebuilt hideout instead of reaching the bridge's termination policy.
            script.exports_sync.stop()
            script.unload()
            script = None
            verify_live(process, base, code)
            from NativeEncounterRestart import redeploy_departure
            redeploy_errors = redeploy_departure(session, process, base, args.pid, record,
                                                 recovery_preflight['departure'],
                                                 fingerprints(image, 'encounter-control'),
                                                 args.duration + 25 - (time.monotonic() - worker_started),
                                                 birth=identity['birth'])
            if redeploy_errors:
                collection_errors.extend(redeploy_errors)
                collection_failure.set()
    finally:
        cleanup_errors = []
        if script is not None:
            try:
                script.exports_sync.stop()
                if collection_archive is not None:
                    collection_archive.drain()
            except Exception as error:
                cleanup_errors.append(f'script stop or collection drain: {error}')
            if transfer_heartbeat is not None:
                transfer_heartbeat[0].set()
                transfer_heartbeat[1].join(2)
                if transfer_heartbeat[1].is_alive():
                    cleanup_errors.append('collection heartbeat did not stop')
            try:
                script.unload()
            except Exception as error:
                cleanup_errors.append(f'script cleanup: {error}')
        if session is not None:
            try:
                if close_recovery_session is not None:
                    close_recovery_session()
                session.detach()
            except Exception as error:
                cleanup_errors.append(f'session detach: {error}')
        if log is not None:
            if args.mode == 'recover-preparation':
                try:
                    from NativeRecoverySession import read_failure_evidence
                    failure = read_failure_evidence(args.log.with_suffix('.fatal.json'), identity, args.recovery.snapshot)
                    if failure is not None:
                        record({**failure, 'recoveredFromSynchronousJournal': True})
                        collection_errors.append('Native recovery rejected: ' + failure['reason'])
                        collection_failure.set()
                except (ProbeError, OSError, ValueError) as error:
                    cleanup_errors.append('failure journal: ' + str(error))
            code_restored = False
            try:
                verify_live(process, base, code)
                code_restored = True
                record({'kind': 'host-detached', 'codeRestored': True, 'cleanupErrors': cleanup_errors})
            except Exception as error:
                cleanup_errors.append(f'post-detach verification: {error}')
                record({'kind': 'host-detached', 'codeRestored': False, 'cleanupErrors': cleanup_errors})
            if collection_archive is not None:
                try:
                    manifest = collection_archive.close(code_restored)
                    record({'kind': 'collection-archive-verified', 'payloadCount': len(manifest['payloads']),
                            'collectionComplete': False, 'restorable': False})
                except Exception as error:
                    cleanup_errors.append(f'collection archive: {error}')
            log.close()
        process.close()
        if cleanup_errors or collection_failure.is_set():
            raise ProbeError('; '.join(cleanup_errors + collection_errors))
    if any(item.get('kind') in ('script-error', 'recovery-fatal') or
           (item.get('kind') == 'stopped' and 'error' in item.get('reason', '')) for item in results):
        return 3
    if args.mode == 'trigger':
        return 0 if any(item.get('kind') == 'invoke-returned' for item in results) else 4
    if args.mode == 'resume-preparation':
        return 0 if any(item.get('kind') == 'resume-chain-observed' for item in results) else 4
    if args.mode == 'recover-preparation':
        return 0 if any(item.get('kind') == 'native-recovery-rebuilt' for item in results) else 4
    if args.mode == 'lifecycle':
        return 0 if any(item.get('kind') == 'lifecycle-state' for item in results) else 4
    if args.mode in ('load-observe', 'collection-observe'):
        return 0 if any(item.get('kind') == 'load-state' for item in results) else 4
    return 0 if any(item.get('kind') == 'idle-observed' for item in results) else 4


if __name__ == '__main__':
    try:
        raise SystemExit(supervised_main())
    except (ProbeError, OSError, ValueError) as error:
        raise SystemExit(f'EXPERIMENT_FAILED: {error}')
