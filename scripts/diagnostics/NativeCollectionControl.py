"""File-based marks and stop requests, without input simulation or game calls."""

import json
import threading
import time

from NativeCheckpointProbe import ProbeError


MARKS = frozenset(('preparation-before-exit', 'preparation-before-departure',
                   'encounter-started', 'resources-changed', 'death-result', 'post-death-menu'))


def start_transfer_heartbeat(script, archive, failed):
    stopped = threading.Event()

    def pulse():
        while not stopped.wait(0.25):
            try:
                archive.check()
                script.post({'type': 'collection-ack', 'heartbeat': True})
            except Exception as error:
                try:
                    script.post({'type': 'collection-ack', 'error': str(error)})
                finally:
                    failed(error)
                return

    worker = threading.Thread(target=pulse, name='native-collection-heartbeat', daemon=True)
    worker.start()
    return stopped, worker


def validate_command(value, last_id):
    if not isinstance(value, dict) or type(value.get('id')) is not int or value['id'] < 0:
        raise ProbeError('Invalid collection command identity')
    if value['id'] <= last_id:
        return None
    if value.get('action') == 'stop' and set(value) == {'id', 'action'}:
        return value
    if value.get('action') == 'mark' and set(value) == {'id', 'action', 'name'} and value['name'] in MARKS:
        return value
    raise ProbeError('Only named evidence marks or stop are valid collection commands')


def run_collection(script, archive, control_path, duration, done, record, file_witness=None):
    last_id = 0
    invalid_since = None
    deadline = time.monotonic() + duration + 5
    while not done.wait(0.25):
        archive.check()
        if file_witness is not None:
            file_witness.poll()
        if time.monotonic() >= deadline:
            break
        try:
            text = control_path.read_text(encoding='utf-8')
            if len(text) > 2048:
                raise ProbeError('Collection control file too large')
            value = json.loads(text)
        except (OSError, ValueError) as error:
            if invalid_since is None:
                invalid_since = time.monotonic()
            if time.monotonic() - invalid_since > 2:
                raise ProbeError('Collection control file remained unreadable') from error
            continue
        invalid_since = None
        command = validate_command(value, last_id)
        if command is None:
            continue
        last_id = command['id']
        if command['action'] == 'stop':
            script.exports_sync.stop()
            record({'kind': 'collection-control-stop', 'id': last_id})
            break
        result = script.exports_sync.mark(command['name'])
        archive.drain()
        record({'kind': 'collection-control-mark', 'id': last_id, 'name': command['name'],
                'result': result, 'playerConfirmedScene': True, 'restorable': False})
