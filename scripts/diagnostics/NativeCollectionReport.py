"""Assess collected evidence coverage, never infer successful preparation restoration."""

import argparse
import hashlib
import json
from pathlib import Path

from NativeCheckpointProbe import ProbeError
from NativeCollectionCodec import chunk_layout


LOAD_OPERATIONS = frozenset(('menu-exit', 'run-teardown', 'run-reset', 'schedule-continue',
    'waiting-request', 'continue-callback', 'run-initialize', 'init-run-checkpoint',
    'save-apply', 'save-json-root', 'player-destruct', 'player-construct',
    'player-initialize', 'player-draw-initialize', 'player-inventory-initialize', 'player-spawn'))


def assess(records, normal_load_baseline=None):
    errors = []
    headers = [item for item in records if item.get('kind') == 'host-preflight']
    if len(headers) != 1 or headers[0].get('mode') != 'collection-observe':
        errors.append('missing unique collection identity')
    entered = {}
    returned = set()
    completed = set()
    payloads = {}
    stored = set()
    marks = {}
    tasks = set()
    sequence = 0
    known_chunks = {}
    current_task_word = None
    for item in records:
        if 'sequence' in item:
            if type(item['sequence']) is not int or item['sequence'] != sequence + 1:
                errors.append('agent sequence gap or duplicate')
            sequence = item['sequence']
        kind = item.get('kind')
        if kind == 'load-enter':
            identifier = item.get('callId')
            if identifier in entered:
                errors.append('duplicate call identity')
            entered[identifier] = item
        elif kind == 'load-leave':
            identifier = item.get('callId')
            start = entered.get(identifier)
            if start is None or identifier in returned or any(start.get(key) != item.get(key)
                    for key in ('operation', 'threadId')):
                errors.append('unmatched native return')
            else:
                returned.add(identifier)
                completed.add(item['operation'])
        elif kind == 'collection-payload':
            identifier = item.get('payloadId')
            if type(identifier) is not int or identifier <= 0 or identifier in payloads:
                errors.append('duplicate or invalid payload identity')
            payloads[identifier] = item
            cursor = 0
            for region in item.get('regions', []):
                if region.get('status') == 'unallocated':
                    continue
                size = region.get('size')
                if type(size) is not int or size <= 0 or region.get('offset') != cursor:
                    errors.append('invalid payload region layout')
                    break
                cursor += size
            if cursor != item.get('byteLength'):
                errors.append('payload regions do not span byteLength')
            if 'encoding' in item:
                try:
                    layout, additions = chunk_layout(item, known_chunks)
                    known_chunks.update(additions)
                except (ProbeError, TypeError, KeyError, AttributeError) as error:
                    errors.append(str(error))
        elif kind == 'collection-payload-reference':
            errors.append('unsupported unverified payload reference')
        elif kind == 'collection-payload-stored':
            stored.add(item.get('payloadId'))
        elif kind == 'collection-control-mark':
            if item.get('playerConfirmedScene') is True:
                if item.get('name') == 'encounter-started' and current_task_word != 1:
                    errors.append('encounter mark lacks an observed encounter task')
                else:
                    marks[item.get('name')] = item
        elif kind == 'collection-task':
            current_task_word = item.get('task', {}).get('word')
            tasks.add(current_task_word)
        elif kind == 'script-error' or (kind == 'stopped' and 'error' in item.get('reason', '')):
            errors.append('observer failure')
        elif kind == 'host-detached' and (item.get('codeRestored') is not True or item.get('cleanupErrors')):
            errors.append('unverified hook cleanup')
    unpaired = sorted(set(entered) - returned)
    missing_payloads = sorted(set(payloads) - stored)
    if stored - set(payloads):
        errors.append('stored payload has no metadata')
    stages = {item.get('stage') for identifier, item in payloads.items() if identifier in stored}
    required_stages = {'save-apply:input-header', 'save-json-root:consumed',
                       'player-spawn:checkpoint-input', 'copy-checkpoint:enter', 'copy-checkpoint:leave'}
    missing_load = sorted(LOAD_OPERATIONS - completed)
    missing_stages = sorted(required_stages - stages)
    preparation = marks.get('preparation-before-departure', {}).get('result') or {}
    stable_preparation = preparation.get('twoPassEqual') is True and preparation.get('knownRegionsComplete') is True
    files_present = any(item.get('kind') == 'collection-files' and item.get('twoPassEqual') is True for item in records)
    baseline_valid = normal_load_baseline is not None and normal_load_baseline.get('verifiedNormalLoadPrefix') is True and \
        len(headers) == 1 and isinstance(headers[0].get('sha256'), str) and \
        normal_load_baseline.get('buildSha256') == headers[0]['sha256']
    if normal_load_baseline is not None and not baseline_valid:
        errors.append('normal load baseline is unverified or belongs to a different executable')
    load_recorded = not errors and files_present and (baseline_valid or (not missing_load and not missing_stages))
    required_marks = {'preparation-before-exit', 'preparation-before-departure',
                      'encounter-started', 'resources-changed', 'death-result', 'post-death-menu'}
    if baseline_valid:
        required_marks.remove('preparation-before-exit')
    missing_marks = sorted(required_marks - set(marks))
    detached = any(item.get('kind') == 'host-detached' and item.get('codeRestored') is True and
                   not item.get('cleanupErrors') for item in records)
    native_verified = any(item.get('kind') == 'collection-archive-verified' for item in records)
    logical_bytes = sum(item['byteLength'] for item in payloads.values()
                        if type(item.get('byteLength')) is int and item['byteLength'] > 0)
    wire_bytes = sum(item.get('wireByteLength', item.get('byteLength', 0)) for item in payloads.values()
                     if type(item.get('wireByteLength', item.get('byteLength'))) is int and
                     item.get('wireByteLength', item.get('byteLength', 0)) >= 0)
    return {'kind': 'collection-coverage', 'errors': sorted(set(errors)), 'unpairedCallIds': unpaired,
            'logicalPayloadBytes': logical_bytes, 'transmittedPayloadBytes': wire_bytes,
            'pendingPayloadIds': missing_payloads, 'missingLoadOperations': missing_load,
            'missingPayloadStages': missing_stages, 'missingPlayerMarks': missing_marks,
            'reusedNormalLoadEvidence': normal_load_baseline if baseline_valid else None,
            'normalRebuildEvidencePresent': load_recorded, 'stablePreparationMark': stable_preparation,
            'requiredPreparationBackup': 'separate formal snapshot verification is mandatory before departure',
            'departureEvidencePresent': load_recorded and stable_preparation and not unpaired and not missing_payloads,
            'observedTaskWords': sorted(value for value in tasks if type(value) is int),
            'knownCoveragePresent': load_recorded and stable_preparation and not missing_marks and
                not unpaired and not missing_payloads and detached and native_verified and 1 in tasks,
            'restorable': False, 'collectionComplete': False, 'inGameAcceptance': False,
            'limitation': 'Presence is not semantic correctness, complete ownership, or restoration acceptance.'}


def verify_artifacts(log_path, records):
    directory = log_path.with_suffix('.payloads')
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    headers = [item for item in records if item.get('kind') == 'host-preflight']
    if len(headers) != 1 or any(manifest.get('identity', {}).get(key) != headers[0].get(key)
                                for key in ('pid', 'birth', 'sha256', 'path', 'base')):
        raise ProbeError('Collection archive identity mismatch')
    expected = {item['payloadId']: item for item in records if item.get('kind') == 'collection-payload'}
    actual = manifest.get('payloads', [])
    if len(actual) != len(expected) or {item['event']['payloadId'] for item in actual} != set(expected):
        raise ProbeError('Collection archive payload set mismatch')
    known_chunks = {}
    for item in actual:
        if item['event'] != expected[item['event']['payloadId']]:
            raise ProbeError('Collection archive metadata mismatch')
        verify_file(directory, item['file'], item['sha256'], item['size'])
        if 'encoding' in item['event']:
            layout, additions = chunk_layout(item['event'], known_chunks)
            payload = (directory / item['file']).read_bytes()
            for offset, chunk in layout:
                if hashlib.sha256(payload[offset:offset + chunk['size']]).hexdigest() != chunk['sha256']:
                    raise ProbeError('Collection artifact chunk mismatch')
            known_chunks.update(additions)
    for event in records:
        if event.get('kind') == 'collection-files':
            for item in event['files']:
                if not item.get('absent'):
                    verify_file(log_path.with_suffix('.files'), item['file'], item['sha256'], item['size'])
    supervisors = [json.loads(line) for line in log_path.with_suffix('.supervisor.jsonl').read_text(encoding='utf-8').splitlines()]
    if not supervisors or supervisors[-1].get('kind') != 'supervisor-finished' or any(
            supervisors[-1].get(key) != 0 for key in ('workerExitCode', 'result')):
        raise ProbeError('Collection supervisor has not finished normally')
    if any(item.get('kind') == 'supervisor-target-exited' for item in supervisors):
        raise ProbeError('Collection supervisor terminated the game')


def verify_file(directory, filename, digest, size):
    if not isinstance(digest, str) or len(digest) != 64 or any(value not in '0123456789abcdef' for value in digest):
        raise ProbeError('Invalid collection content digest')
    if filename != digest + '.bin':
        raise ProbeError('Collection content path does not match digest')
    target = directory / filename
    if target.is_symlink() or not target.resolve().is_relative_to(directory.resolve()):
        raise ProbeError('Collection content escapes archive')
    data = target.read_bytes()
    if len(data) != size or hashlib.sha256(data).hexdigest() != digest:
        raise ProbeError('Collection artifact hash mismatch')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log', type=Path)
    parser.add_argument('--verify-artifacts', action='store_true')
    parser.add_argument('--normal-load-evidence', type=Path)
    args = parser.parse_args()
    records = [json.loads(line) for line in args.log.read_text(encoding='utf-8').splitlines() if line]
    baseline = None
    if args.normal_load_evidence is not None:
        from NativeCollectionAnalysis import CapturedEvidence
        baseline = CapturedEvidence(args.normal_load_evidence).normal_load_baseline()
    result = assess(records, baseline)
    if args.verify_artifacts:
        verify_artifacts(args.log, records)
        result['artifactsVerified'] = True
    print(json.dumps(result, ensure_ascii=True, indent=2))
    return 0 if result['departureEvidencePresent'] else 2


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError, ProbeError, KeyError, TypeError) as error:
        raise SystemExit(f'COLLECTION_REPORT_FAILED: {error}')
