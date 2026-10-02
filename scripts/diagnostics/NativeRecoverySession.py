"""Record target detachment promptly, including crash details, without swallowing it."""

import ctypes
import json

from NativeCheckpointProbe import ProbeError


def read_failure_evidence(path, identity, snapshot_id):
    if not path.exists():
        return None
    if path.stat().st_size > 32768:
        raise ProbeError('Recovery failure journal exceeds its bounded size')
    evidence = json.loads(path.read_text(encoding='utf-8'))
    if (not isinstance(evidence, dict) or evidence.get('kind') != 'recovery-fatal' or
            evidence.get('pid') != identity['pid'] or evidence.get('birth') != str(identity['birth']) or
            evidence.get('base') != identity['base'] or evidence.get('snapshotId') != snapshot_id or
            evidence.get('inGameAcceptance') is not False or not isinstance(evidence.get('reason'), str)):
        raise ProbeError('Recovery failure journal identity is invalid')
    return evidence


def watch_recovery_session(session, process, record, failed):
    closing = []

    def detached(reason, crash):
        if closing:
            return
        evidence = {'kind': 'recovery-target-detached', 'reason': reason, 'inGameAcceptance': False}
        if crash is not None:
            evidence['crash'] = {key: getattr(crash, key, None) for key in ('pid', 'process_name', 'summary', 'report')}
        try:
            from ctypes import byref
            from ctypes import wintypes
            process.kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
            process.kernel.GetExitCodeProcess.restype = wintypes.BOOL
            exit_code = wintypes.DWORD()
            if process.kernel.GetExitCodeProcess(process.handle, byref(exit_code)):
                evidence['exitCode'] = exit_code.value
        except (AttributeError, OSError):
            pass
        try:
            record(evidence)
        finally:
            failed(ProbeError('Native recovery target detached: ' + str(reason)))

    session.on('detached', detached)

    def close():
        closing.append(True)
        session.off('detached', detached)

    return close
