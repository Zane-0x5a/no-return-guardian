"""Drive the route board's recorded departure natively: record it, replay it, or abandon an encounter.

Recording the board's `player-next-task` event after a protected preparation, and replaying its exact
argument slots in the rebuilt hideout, starts the same encounter from the protected state through the
game's own departure flow: the hideout's respite state remembers the checkpoint, saves, plays the
encounter task, and enters `encounter` on its own. Every hook here is bounded and verified removed.
"""

import json
import os
from pathlib import Path
import queue
import sys
import time

from NativeCheckpointProbe import FRIDA_DEPS, ProbeError, inspect_current_task, verify_build


CONTROL_RVAS = (0x133EC5E, 0x1B843A0, 0x1B83F70, 0x1B6E000, 0xDC1780, 0x1B6EA60, 0xFE7B10, 0x1200AC0)


def hideout_state(process, base):
    run = base + 0x415AC90
    active, initialized = process.read(run + 0x80, 1)[0], process.read(run + 0x72C8, 1)[0]
    story = int.from_bytes(process.read(base + 0x9398730, 4), 'little', signed=True)
    try:
        task = inspect_current_task(process.read, base, include_simple_fallback=True)
    except ProbeError:
        task = {}
    return {'runActive': active, 'runInitialized': initialized, 'story': story,
            'task': task.get('task'), 'taskWord': task.get('descriptor_word')}


def arm_decision(observed):
    """Arm in the snapshot's playable hideout; an encounter seen first means its departure was missed."""
    if observed['runActive'] == 1 and observed['runInitialized'] == 1:
        if observed['taskWord'] == 2:
            return 'arm'
        if observed['taskWord'] == 1:
            return 'stop'
    return 'wait'


def save_workers(process, base):
    return [int.from_bytes(process.read(base + 0x9341660 + offset, 4), 'little', signed=True)
            for offset in (0x58, 0x228, 0x3f8)]


def task_identity(process, task):
    if task is None:
        return None
    return {'task': task, 'subnode': process.read(int(task, 16) + 0x90, 8)[::-1].hex()}


def run_control(session, configuration, record, timeout, cancel=None):
    """Run one encounter-control script on an attached session; its failures never become recovery failures."""
    messages = queue.Queue()
    source = Path(__file__).with_name('NativeEncounterControl.js').read_text(encoding='utf-8')
    script = session.create_script('globalThis.nativeEncounterConfiguration = ' +
                                   json.dumps(configuration) + ';\n' + source)
    script.on('message', lambda message, data: messages.put(message))
    events = []
    errors = []
    try:
        script.load()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if cancel is not None and cancel():
                events.append({'kind': 'encounter-control-cancelled'})
                record(events[-1])
                break
            try:
                # Short enough that the recorder's file watch sees any save finished just before a departure.
                message = messages.get(timeout=0.05)
            except queue.Empty:
                continue
            payload = (message.get('payload') if message.get('type') == 'send'
                       else {'kind': 'encounter-control-error', 'details': str(message)})
            events.append(payload)
            record(payload)
            if payload.get('kind') == 'encounter-control-stopped':
                break
        else:
            events.append({'kind': 'encounter-control-timeout'})
            record(events[-1])
    finally:
        try:
            script.exports_sync.stop()
        except Exception as error:
            errors.append(f'control stop: {error}')
        try:
            script.unload()
        except Exception as error:
            errors.append(f'control unload: {error}')
        while True:
            try:
                message = messages.get_nowait()
            except queue.Empty:
                break
            if message.get('type') == 'send':
                events.append(message.get('payload'))
                record(message.get('payload'))
    return events, errors


def control_configuration(mode, pid, base, fingerprints, duration, **fields):
    hooks = [item for item in fingerprints if item['rva'] in CONTROL_RVAS]
    if sorted(item['rva'] for item in hooks) != sorted(CONTROL_RVAS):
        raise ProbeError('Encounter control fingerprints are incomplete')
    return {'mode': mode, 'pid': pid, 'base': hex(base), 'duration': duration, 'fingerprints': hooks, **fields}


def code_restored(process, base, fingerprints):
    return all(process.read(base + item['rva'], 32) == bytes.fromhex(item['bytes'])
               for item in fingerprints if item['rva'] in CONTROL_RVAS)


class AttachedSession:
    """Hands an already attached session on to the results exit, which owns it from then on."""

    def __init__(self, session):
        self.session = session

    def take(self, timeout=15):
        session, self.session = self.session, None
        return session

    def close(self):
        if self.session is not None:
            try:
                self.session.detach()
            except Exception:
                pass
            self.session = None


def abandon_encounter(process, base, pid, birth, log_path, identity_check, fingerprints, prefetch,
                      results_timeout=45):
    """Broadcast the pause menu's abandon event once, then wait for the death results page.

    Runs before any shutdown guard is armed: every failure leaves the game running. Once the event was
    broadcast the run is failing by the game's own rules, and the results page can still be continued.
    """
    from NativeResultsExit import observe_results

    journal_path = log_path.with_suffix('.encounter.jsonl')
    session = prefetch.take()
    with journal_path.open('x', encoding='utf-8') as journal:
        def record(payload):
            line = json.dumps({'pid': pid, 'birth': birth, 'time': time.time(), **payload}, ensure_ascii=False)
            if journal.tell() + len(line.encode('utf-8')) > 131072:
                raise ProbeError('Encounter journal reached its bound')
            journal.write(line + '\n')
            journal.flush()
            os.fsync(journal.fileno())
            print(line, flush=True)

        try:
            identity_check()
            record({'kind': 'abandon-start'})
            events, errors = run_control(session, control_configuration('abandon', pid, base, fingerprints, 30),
                                         record, 35)
            restored = code_restored(process, base, fingerprints)
            record({'kind': 'abandon-detached', 'codeRestored': restored, 'cleanupErrors': errors})
            broadcast = any(event.get('kind') == 'abandon-broadcast' for event in events)
            if errors or not restored:
                raise ProbeError('Abandon hooks were not proven removed; recovery was not started')
            if not any(event.get('kind') == 'abandon-observed' for event in events):
                raise ProbeError('The encounter did not end after the abandon event; recovery was not started'
                                 if broadcast else 'No stable encounter was observed; nothing was changed')
            deadline = time.monotonic() + results_timeout
            previous = None
            while time.monotonic() < deadline:
                identity_check()
                try:
                    observed = observe_results(process, base)
                except ProbeError:
                    observed = {'unstable': True}
                ui = observed.get('ui') or {}
                marker = (observed.get('initialized'), observed.get('taskWord'), ui.get('topPage'))
                if marker != previous:
                    record({'kind': 'abandon-progress', 'observed': observed})
                    previous = marker
                if observed.get('initialized') == 1 and observed.get('taskWord') == 2 and ui.get('topPage'):
                    receipt = {'journal': str(journal_path), 'resultsPage': ui.get('topPage'),
                               'abandoned': True, 'nativeRecoveryStarted': False}
                    record({'kind': 'abandon-complete', **receipt})
                    return receipt, AttachedSession(session)
                time.sleep(0.1)
            raise ProbeError('The abandoned run did not reach its results page; the game keeps running')
        except BaseException as error:
            record({'kind': 'abandon-stopped', 'reason': str(error), 'nativeRecoveryStarted': False})
            try:
                session.detach()
            except Exception:
                pass
            raise


def same_encounter(expected, arrived, same_process=False):
    """Every encounter task observed live shares one subnode id, so only the task object tells encounters
    apart, and only inside the game process that recorded the departure; elsewhere the answer is unknown."""
    if not expected or not arrived:
        return None
    try:
        if int(expected['subnode'], 16) != int(arrived['subnode'], 16):
            return False
        return int(expected['task'], 16) == int(arrived['task'], 16) if same_process else None
    except (KeyError, TypeError, ValueError):
        return None


def redeploy_departure(session, process, base, pid, record, departure, fingerprints, budget, birth=None):
    """Replay the recorded departure in the rebuilt hideout. Returns only fatal, hook-removal errors."""
    if budget < 25:
        record({'kind': 'redeploy-skipped', 'reason': 'insufficient supervisor budget', 'budget': budget})
        return []
    duration = int(min(75, budget - 5))
    configuration = control_configuration('redeploy', pid, base, fingerprints, duration,
                                          slots=departure['slots'], settleUpdates=90, retryMs=6000,
                                          maximumBroadcasts=2)
    record({'kind': 'redeploy-start', 'routeIndex': departure['routeIndex'], 'duration': duration})
    events, errors = run_control(session, configuration, record, duration + 5)
    restored = code_restored(process, base, fingerprints)
    record({'kind': 'redeploy-detached', 'codeRestored': restored, 'cleanupErrors': errors})
    arrived = next((event for event in events if event.get('kind') == 'redeployed'), None)
    if arrived is not None:
        record({'kind': 'redeploy-complete', 'routeIndex': departure['routeIndex'],
                'sameEncounter': same_encounter(departure.get('encounter'), arrived.get('task'),
                                                birth is not None and (departure.get('pid'), departure.get('birth')) ==
                                                (pid, birth)),
                'encounter': arrived.get('task'), 'inGameAcceptance': False})
    else:
        record({'kind': 'redeploy-incomplete', 'routeIndex': departure['routeIndex'],
                'reason': next((event.get('reason') for event in reversed(events)
                                if event.get('kind') == 'encounter-control-stopped'), 'no stop event')})
    return [] if restored else ['Redeploy hooks were not proven removed']


def replay_in_hideout(process, base, pid, birth, departure, log_path, budget=80):
    """Start the recorded encounter from the hideout the player is already standing in.

    Guardian asserts that this hideout is the preparation's own rebuild in this game process, with no
    departure since; the replay is then the same departure a recovery would have issued, without the
    recovery. Nothing else is changed: a lost departure just leaves the player in the hideout.
    Returns 0 when the encounter was entered, 4 when it was not, and 3 when the hook was not proven removed.
    """
    from NativeCheckpointTrigger import fingerprints, verify_live

    image = process.image_path().read_bytes()
    digest = verify_build(image)
    code = fingerprints(image, 'encounter-control')
    verify_live(process, base, code)
    observed = hideout_state(process, base)
    if observed['runActive'] != 1 or observed['runInitialized'] != 1 or observed['taskWord'] != 2:
        raise ProbeError('Departing from the hideout requires a playable hideout; nothing was changed')
    with log_path.open('x', encoding='utf-8') as log:
        def record(payload):
            log.write(json.dumps(payload, ensure_ascii=False) + '\n')
            log.flush()
            os.fsync(log.fileno())
            print(json.dumps(payload, ensure_ascii=False), flush=True)

        record({'kind': 'host-preflight', 'time': time.time(), 'mode': 'hideout-redeploy', 'pid': pid,
                'birth': birth, 'sha256': digest, 'base': hex(base), 'routeIndex': departure['routeIndex'],
                'observed': observed})
        sys.path.insert(0, str(FRIDA_DEPS))
        import frida

        session = frida.attach(pid)
        errors = []
        try:
            from NativeCheckpointTrigger import process_birth
            if process_birth(process) != birth:
                raise ProbeError('Process identity changed during attach')
            errors = redeploy_departure(session, process, base, pid, record, departure, code, budget, birth=birth)
        finally:
            try:
                session.detach()
            except Exception as error:
                errors.append(f'session detach: {error}')
            restored = code_restored(process, base, code)
            record({'kind': 'host-detached', 'codeRestored': restored, 'cleanupErrors': errors})
    if errors or not restored:
        return 3
    entered = any(json.loads(line).get('kind') == 'redeploy-complete'
                  for line in log_path.read_text(encoding='utf-8').splitlines())
    return 0 if entered else 4
