"""Finish recognized death results with native continue commands before recovery is armed."""

import json
import os
from pathlib import Path
import queue
import struct
import sys
import threading
import time

from NativeCheckpointProbe import FRIDA_DEPS, ProbeError, inspect_current_task
from NativeRecoverySource import verify_unloaded_menu
from NativeResultsUi import TransientUiChange, inspect_results_ui


CONTINUE_RVAS = (0xD49630, 0xD49530)
POLL_SECONDS = 0.05
# Token cadence of a player mashing the key; the hook adds its own minimum gap between injections.
PRESS_INTERVAL = 0.15
INJECTION_GAP_MS = 100
TOKEN_LIFETIME_MS = 400
MAXIMUM_PRESSES = 80
MAXIMUM_TOKENS = 800
# A stall needs both many accepted presses and a long time without any visible results change.
STALLED_PRESSES = 10
STALLED_SECONDS = 8
MENU_STABLE_SECONDS = 1.0
JOURNAL_LIMIT = 262144


def observe_results(process, base, attempts=3):
    fields = ((base + 0x415ac90 + 0x80, '<B'), (base + 0x415ac90 + 0x72c8, '<B'),
              (base + 0x415ac90 + 0x520, '<i'), (base + 0x9398730, '<i'))
    fields += tuple((base + 0x9341660 + offset, '<i') for offset in (0x58, 0x228, 0x3f8, 0x5e0))
    for _ in range(attempts):
        payloads = [process.read(address, struct.calcsize(encoding)) for address, encoding in fields]
        active, initialized, selector, story, *workers_slot = [
            struct.unpack(encoding, payload)[0] for (_, encoding), payload in zip(fields, payloads)]
        if active != 0 or story != 2 or initialized not in (0, 1) or selector not in (0, 1, 2):
            raise ProbeError('Results exit requires an inactive No Return run; playable runs are rejected')
        workers, slot = workers_slot[:3], workers_slot[3]
        if any(worker < 0 or worker > 8 for worker in workers):
            raise ProbeError('Unknown native save worker state during results exit')
        task = inspect_current_task(process.read, base, include_simple_fallback=True)
        unloaded = initialized == 0 and selector == 2 and workers == [0, 0, 0] and slot == 20
        unloaded = unloaded and task['status'] == 'no_current_task'
        try:
            ui = inspect_results_ui(process.read, base) if initialized == 1 else None
        except TransientUiChange:
            continue
        if all(process.read(address, len(payload)) == payload
               for (address, _), payload in reversed(list(zip(fields, payloads)))):
            return {'unloaded': unloaded, 'initialized': initialized, 'selector': selector,
                    'workers': workers, 'slot': slot, 'task': task.get('task'),
                    'taskWord': task.get('descriptor_word'), 'ui': ui}
    return {'unstable': True}


def gate(observed):
    """The fields that authorize a continue token; focus and unrelated stacks may animate freely."""
    ui = observed.get('ui') or {}
    return (observed.get('unstable'), observed.get('unloaded'), observed.get('initialized'),
            observed.get('selector'), observed.get('workers'), observed.get('slot'),
            observed.get('taskWord'), ui.get('topPage'), ui.get('topPageAddress'))


def signature(observed):
    """Visible results progress; a pressed page may advance internally without changing its top page."""
    ui = observed.get('ui') or {}
    return (ui.get('topPage'), ui.get('topPageAddress'), ui.get('focusId'),
            tuple(page.get('resourceId') for page in ui.get('topPages') or ()))


class ResultsExit:
    def __init__(self):
        self.last_page = None
        self.last_token = None
        self.tokens = 0
        self.total_presses = 0
        self.signature = None
        self.signature_since = None
        self.stalled_presses = 0
        self.seen_pages = set()
        self.menu_since = None
        self.started = False

    def injected(self):
        """Counts a continue the game actually consumed; unconsumed tokens are not presses."""
        self.total_presses += 1
        self.stalled_presses += 1

    def step(self, observed, now, issue=True):
        """With issue=False the entry is validated and the flow adopted, but no token is counted."""
        if observed.get('unstable'):
            if not self.started:
                raise ProbeError('Results state kept changing during observation; no continue was issued')
            return 'wait'
        if observed['unloaded']:
            if not self.started:
                raise ProbeError('Results entry was selected, but the game is already at the menu')
            self.menu_since = now if self.menu_since is None else self.menu_since
            return 'ready' if now - self.menu_since >= MENU_STABLE_SECONDS else 'wait'
        self.menu_since = None
        if observed['initialized'] == 0:
            if not self.started:
                raise ProbeError('Results entry requires a visible death results page')
            return 'wait'
        if self.started and observed['taskWord'] is None:
            return 'wait'
        if observed['taskWord'] != 2 or observed['selector'] not in (0, 1):
            raise ProbeError('Results task changed; no continue or recovery was issued')
        ui = observed['ui'] or {}
        page = (ui.get('topPage'), ui.get('topPageAddress'))
        if not page[0]:
            if not self.started:
                raise ProbeError('The top page is not a supported death results page')
            return 'wait'
        self.started = True
        if page != self.last_page:
            if page[0] in self.seen_pages and self.last_page and page[0] != self.last_page[0]:
                raise ProbeError('Results returned to an earlier page; native continue stopped')
            self.seen_pages.add(page[0])
            self.last_page = page
        visible = signature(observed)
        if visible != self.signature:
            self.signature, self.signature_since, self.stalled_presses = visible, now, 0
        if observed['workers'] != [0, 0, 0] or observed['slot'] != 20:
            return 'wait'
        if (self.stalled_presses >= STALLED_PRESSES and now - self.signature_since >= STALLED_SECONDS) \
                or self.total_presses >= MAXIMUM_PRESSES:
            raise ProbeError('Results did not advance after bounded native continue commands')
        if not issue or (self.last_token is not None and now - self.last_token < PRESS_INTERVAL):
            return 'wait'
        if self.tokens >= MAXIMUM_TOKENS:
            raise ProbeError('Results kept ignoring bounded native continue commands')
        self.tokens += 1
        self.last_token = now
        return 'confirm'


def attach_session(pid):
    sys.path.insert(0, str(FRIDA_DEPS))
    import frida

    return frida.attach(pid)


class SessionPrefetch:
    """Attaches Frida while the source is still being verified; it loads no script and installs no hook."""

    def __init__(self, pid):
        self.session = None
        self.error = None
        self.thread = threading.Thread(target=self.attach, args=(pid,), daemon=True)
        self.thread.start()

    def attach(self, pid):
        try:
            self.session = attach_session(pid)
        except Exception as error:
            self.error = error

    def take(self, timeout=15):
        self.thread.join(timeout)
        if self.thread.is_alive():
            raise ProbeError('Native continue attach did not finish; no command was issued')
        if self.error is not None:
            raise ProbeError(f'Native continue attach failed: {self.error}')
        session, self.session = self.session, None
        return session

    def close(self):
        self.thread.join(15)
        if self.session is not None:
            try:
                self.session.detach()
            except Exception:
                pass
            self.session = None


class ContinueInjector:
    """Owns the only Frida session that may answer continue-button queries."""

    def __init__(self, pid, base, fingerprints, duration=180, session=None):
        self.messages = queue.Queue()
        self.session = session
        self.script = None
        self.stopped = None
        self.next_id = 0
        try:
            if self.session is None:
                self.session = attach_session(pid)
            source = Path(__file__).with_name('NativeResultsContinue.js').read_text(encoding='utf-8')
            configuration = {'mode': 'results-continue', 'pid': pid, 'base': hex(base),
                             'duration': duration, 'maximumInjections': MAXIMUM_PRESSES,
                             'minimumGapMs': INJECTION_GAP_MS, 'fingerprints': fingerprints}
            self.script = self.session.create_script(
                'globalThis.nativeResultsContinueConfiguration = ' + json.dumps(configuration) + ';\n' + source)
            self.script.on('message', lambda message, data: self.messages.put(message))
            self.script.load()
        except Exception:
            self.close()
            raise

    def drain(self):
        events = []
        while True:
            try:
                message = self.messages.get_nowait()
            except queue.Empty:
                return events
            payload = (message.get('payload') if message.get('type') == 'send'
                       else {'kind': 'results-continue-error', 'details': str(message)})
            events.append(payload)
            if payload.get('kind') in ('results-continue-stopped', 'results-continue-error'):
                self.stopped = payload

    def installed(self, record, timeout=5):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for event in self.drain():
                record('results-continue-event', event=event)
                if event.get('kind') == 'results-continue-installed':
                    return
            if self.stopped is not None:
                break
            time.sleep(0.05)
        raise ProbeError('Native continue hooks were not installed; no command was issued')

    def press(self):
        if self.stopped is not None:
            raise ProbeError('Native continue injector stopped: ' + str(self.stopped.get('reason')))
        self.next_id += 1
        try:
            self.script.exports_sync.press(self.next_id, TOKEN_LIFETIME_MS)
        except Exception as error:
            raise ProbeError(f'Native continue token was refused: {error}') from error
        return self.next_id

    def close(self):
        errors = []
        if self.script is not None:
            try:
                self.script.exports_sync.stop()
            except Exception as error:
                errors.append(f'continue stop: {error}')
            try:
                self.script.unload()
            except Exception as error:
                errors.append(f'continue unload: {error}')
            self.script = None
        if self.session is not None:
            try:
                self.session.detach()
            except Exception as error:
                errors.append(f'continue detach: {error}')
            self.session = None
        return errors


def progress_marker(observed):
    ui = observed.get('ui') or {}
    return (observed.get('unstable'), observed.get('unloaded'), observed.get('initialized'),
            observed.get('selector'), observed.get('workers'), observed.get('slot'),
            observed.get('taskWord')) + signature(observed) + (ui.get('page'),)


def finish_results(process, base, pid, birth, log_path, identity_check, fingerprints, timeout=90,
                   prefetch=None):
    controller = ResultsExit()
    hooks = [record for record in fingerprints if record['rva'] in CONTINUE_RVAS]
    if sorted(record['rva'] for record in hooks) != sorted(CONTINUE_RVAS):
        raise ProbeError('Native continue hook fingerprints are incomplete')
    journal_path = log_path.with_suffix('.results.jsonl')
    with journal_path.open('x', encoding='utf-8') as journal:
        def record(kind, **fields):
            line = json.dumps({'kind': kind, 'pid': pid, 'birth': birth, 'time': time.time(), **fields})
            # Terminal records keep headroom so a full journal still reaches hook removal and its receipt.
            terminal = kind in ('results-exit-stopped', 'results-continue-detached', 'results-exit-complete')
            limit = JOURNAL_LIMIT if terminal else JOURNAL_LIMIT - 16384
            if journal.tell() + len(line.encode('utf-8')) > limit:
                raise ProbeError('Results journal reached its bound')
            journal.write(line + '\n')
            journal.flush()
            os.fsync(journal.fileno())
            print(line, flush=True)

        injector = None
        expired = 0
        skipped = 0

        def drain():
            nonlocal expired
            for event in injector.drain():
                if event.get('kind') == 'results-continue-expired':
                    expired += 1
                    continue
                if event.get('kind') == 'results-continue-injected':
                    controller.injected()
                record('results-continue-event', event=event)

        def release():
            nonlocal injector
            if injector is None:
                return
            errors = injector.close()
            try:
                drain()
            except ProbeError as error:
                errors.append(str(error))
            injector = None
            restored = all(process.read(base + hook['rva'], 32) == bytes.fromhex(hook['bytes'])
                           for hook in hooks)
            record('results-continue-detached', codeRestored=restored, cleanupErrors=errors)
            if errors or not restored:
                raise ProbeError('Native continue hooks were not proven removed; recovery was not started')

        record('results-exit-start', method='native-continue-command', pressIntervalSeconds=PRESS_INTERVAL,
               menuStableSeconds=MENU_STABLE_SECONDS, prefetchedAttach=prefetch is not None)
        try:
            identity_check()
            initial = observe_results(process, base)
            record('results-entry-observed', observed=initial)
            controller.step(initial, time.monotonic(), issue=False)
            injector = ContinueInjector(pid, base, hooks,
                                        session=prefetch.take() if prefetch is not None else None)
            injector.installed(record)
            started = time.monotonic()
            previous = None
            while time.monotonic() - started < timeout:
                identity_check()
                drain()
                if injector.stopped is not None:
                    raise ProbeError('Native continue injector stopped: ' + str(injector.stopped.get('reason')))
                observed = observe_results(process, base)
                action = controller.step(observed, time.monotonic())
                marker = progress_marker(observed)
                if marker != previous:
                    record('results-exit-progress', action=action, observed=observed,
                           presses=controller.total_presses, tokens=controller.tokens, expired=expired)
                    previous = marker
                if action == 'ready':
                    release()
                    verify_unloaded_menu(process, base)
                    receipt = {'journal': str(journal_path), 'presses': controller.total_presses,
                               'tokens': controller.tokens, 'expiredTokens': expired, 'skipped': skipped,
                               'menuStableSeconds': MENU_STABLE_SECONDS, 'nativeRecoveryStarted': False}
                    record('results-exit-complete', **receipt)
                    return receipt
                if action == 'confirm':
                    identity_check()
                    if gate(observe_results(process, base)) != gate(observed):
                        skipped += 1
                        record('results-continue-skipped', reason='results changed immediately before continue')
                    else:
                        injector.press()
                time.sleep(POLL_SECONDS)
            raise ProbeError('Results exit timed out; native recovery was not started and the game remains running')
        except BaseException as error:
            record('results-exit-stopped', reason=str(error), nativeRecoveryStarted=False,
                   presses=controller.total_presses, tokens=controller.tokens)
            try:
                release()
            except ProbeError as cleanup:
                raise ProbeError(f'{error}; {cleanup}') from error
            raise
