"""Audit captured load lineage and obsolete player samples entirely offline."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

from NativeCheckpointProbe import ProbeError
from NativeCollectionReport import assess, verify_file


def slice_region(data, region):
    offset, size = region['offset'], region['size']
    if type(offset) is not int or type(size) is not int or offset < 0 or size <= 0 or offset + size > len(data):
        raise ProbeError('Analysis region is outside its verified payload')
    return data[offset:offset + size]


def compare_load(source, source_after, destination, destination_address, arena, arena_address):
    if len(source) < 8:
        raise ProbeError('Load source has no declared size')
    declared = struct.unpack_from('<I', source, 4)[0]
    if not 0x458 <= declared <= len(source) or len(destination) != len(source):
        raise ProbeError('Load source declared size is outside the captured copy')
    offset = arena_address - destination_address
    if offset != ((destination_address + 0x41b) & ~7) - destination_address:
        raise ProbeError('Consumed arena is not the native aligned JSON root')
    if len(arena) < 0x40 or offset + len(arena) > declared:
        raise ProbeError('Consumed arena exceeds the declared save body')
    if struct.unpack_from('<Q', arena, 0x30)[0] != arena_address or struct.unpack_from('<I', arena, 0x38)[0] != len(arena):
        raise ProbeError('Consumed arena descriptor does not match its captured bytes')
    return {'capturedCopyBytes': len(source), 'declaredBodyBytes': declared,
            'copyTailOutsideDeclaredBody': len(source) - declared,
            'sourceUnchangedByCopy': source == source_after,
            'consumedEqualsCopyDestination': arena == destination[offset:offset + len(arena)],
            'serializedBodySha256': hashlib.sha256(source[:declared]).hexdigest(),
            'consumedArenaSha256': hashlib.sha256(arena).hexdigest()}


class CapturedEvidence:
    def __init__(self, log_path):
        self.log_path = log_path
        self.records = [json.loads(line) for line in log_path.read_text(encoding='utf-8').splitlines() if line]
        manifest = json.loads((log_path.with_suffix('.payloads') / 'manifest.json').read_text(encoding='utf-8'))
        headers = [record for record in self.records if record.get('kind') == 'host-preflight']
        if len(headers) != 1 or any(manifest['identity'].get(key) != headers[0].get(key)
                for key in ('pid', 'birth', 'sha256', 'path', 'base')):
            raise ProbeError('Analysis archive identity mismatch')
        self.identity = headers[0]
        self.items = manifest['payloads']
        expected = {record['payloadId']: record for record in self.records if record.get('kind') == 'collection-payload'}
        if len(self.items) != len(expected) or {item['event']['payloadId'] for item in self.items} != set(expected):
            raise ProbeError('Analysis archive payload set mismatch')
        self.verified = set()
        for item in self.items:
            if item['event'] != expected[item['event']['payloadId']]:
                raise ProbeError('Analysis archive metadata mismatch')
            self.read_content('.payloads', item)

    def read_content(self, suffix, item):
        directory = self.log_path.with_suffix(suffix)
        key = (suffix, item['file'], item['sha256'], item['size'])
        if key not in self.verified:
            verify_file(directory, item['file'], item['sha256'], item['size'])
            self.verified.add(key)
        return (directory / item['file']).read_bytes()

    def region(self, item, name):
        regions = [region for region in item['event']['regions'] if region['name'] == name]
        if len(regions) != 1:
            raise ProbeError('Analysis requires a unique named region')
        return regions[0], slice_region(self.read_content('.payloads', item), regions[0])

    def payload(self, call_id, stage):
        items = [item for item in self.items if item['event'].get('callId') == call_id and item['event']['stage'] == stage]
        if len(items) != 1:
            raise ProbeError('Analysis requires a unique call payload')
        return items[0]

    def loads(self, before_sequence=None):
        results = []
        for root in [item for item in self.items if item['event']['stage'] == 'save-json-root:consumed' and
                     (before_sequence is None or item['event']['sequence'] <= before_sequence)]:
            sequence = root['event']['sequence']
            applying = [record for record in self.records if record.get('kind') == 'collection-enter-detail' and
                        record.get('operation') == 'save-apply' and record['sequence'] < sequence]
            if not applying:
                raise ProbeError('Consumed root has no preceding apply')
            apply = applying[-1]
            entries = {record['callId']: record for record in self.records if record.get('kind') == 'load-enter'}
            apply_enter = entries[apply['callId']]
            getter_enter = entries[root['event']['callId']]
            returns = [record for record in self.records if record.get('kind') == 'load-leave' and record.get('callId') == apply['callId']]
            if len(returns) != 1 or returns[0]['sequence'] <= sequence or apply_enter['threadId'] != getter_enter['threadId']:
                raise ProbeError('Consumed root does not belong to a paired same-thread apply')
            copies = [record for record in self.records if record.get('kind') == 'collection-enter-detail' and
                      record.get('operation') == 'save-buffer-copy' and record['sequence'] < apply['sequence'] and
                      record['detail']['destination'] == apply['detail']['buffer']]
            if not copies:
                raise ProbeError('Apply has no observed copy into its buffer')
            copy = copies[-1]
            if (copy['detail']['direction'], copy['detail']['stage']) != (2, -1):
                raise ProbeError('Load analysis requires the observed full offset-to-pointer conversion')
            before = self.payload(copy['callId'], 'save-buffer-copy:source-before')
            after = self.payload(copy['callId'], 'save-buffer-copy:after')
            if after['event']['sequence'] >= apply_enter['sequence']:
                raise ProbeError('Copy did not finish before apply')
            _, source = self.region(before, 'source')
            _, source_after = self.region(after, 'source-after')
            destination_region, destination = self.region(after, 'destination-after')
            arena_region, arena = self.region(root, 'json-declared-arena')
            _, header = self.region(root, 'json-root')
            if header != arena[:0x40]:
                raise ProbeError('Consumed JSON header and arena disagree')
            result = compare_load(source, source_after, destination, int(destination_region['address'], 16),
                                  arena, int(arena_region['address'], 16))
            matches = []
            for event in self.records:
                if event.get('kind') != 'collection-files':
                    continue
                for entry in event['files']:
                    if entry.get('absent') or entry['path'] not in ('gamedata/R0A.save', 'savedata/SAVEFILER0A/USR-DATA'):
                        continue
                    data = self.read_content('.files', entry)
                    declared = result['declaredBodyBytes']
                    if len(data) < 1424 + declared or data[1428:1424 + declared] != source[4:declared]:
                        continue
                    matches.append({'version': event['version'], 'path': entry['path'], 'sha256': entry['sha256'],
                                    'bodyIncludingCrcEqual': data[1424:1428] == source[:4],
                                    'fileObservationTime': event['time']})
            results.append({'applyCallId': apply['callId'], 'copyCallId': copy['callId'],
                            'getterCallId': root['event']['callId'], **result, 'diskBodyMatches': matches,
                            'fileReadProven': False})
        return results

    def normal_load_baseline(self):
        for index, record in enumerate(self.records):
            if record.get('kind') != 'collection-control-mark' or record.get('name') != 'preparation-before-departure' or \
                    record.get('playerConfirmedScene') is not True:
                continue
            prefix = self.records[:index + 1]
            coverage = assess(prefix)
            if not coverage['normalRebuildEvidencePresent'] or not coverage['stablePreparationMark'] or \
                    coverage['unpairedCallIds'] or coverage['pendingPayloadIds']:
                continue
            sequence = max(item.get('sequence', 0) for item in prefix)
            loads = self.loads(before_sequence=sequence)
            if not loads or any(not load['sourceUnchangedByCopy'] or not load['consumedEqualsCopyDestination'] or
                    not any(match['bodyIncludingCrcEqual'] for match in load['diskBodyMatches']) for load in loads):
                continue
            return {'sourceLog': str(self.log_path.resolve()),
                    'sourceLogSha256': hashlib.sha256(self.log_path.read_bytes()).hexdigest(),
                    'buildSha256': self.identity['sha256'], 'confirmedMarkId': record['id'],
                    'throughSequence': sequence, 'applyCallIds': [load['applyCallId'] for load in loads],
                    'verifiedNormalLoadPrefix': True, 'sourceCollectionComplete': False,
                    'restorable': False, 'lifetimeVerified': False,
                    'scope': 'Normal load byte lineage and paired boundaries only; final target requires its own backup.'}
        raise ProbeError('No confirmed, paired and byte-verified normal load prefix')

    def players(self):
        expected_vtable = int(self.identity['base'], 16) + 0x2cf6828
        counts = Counter()
        first_invalid = {}
        for item in self.items:
            event = item['event']
            if event['stage'] != 'live-player-sample':
                continue
            data = self.read_content('.payloads', item)
            for region in event['regions']:
                payload = slice_region(data, region)
                vtable = struct.unpack_from('<Q', payload)[0]
                valid = vtable == expected_vtable
                counts['matchingVtable' if valid else 'differentVtable'] += 1
                if not valid:
                    first_invalid.setdefault(region['address'], {'sequence': event['sequence'], 'vtable': hex(vtable)})
        boundaries = [{'sequence': record['sequence'], 'operation': record['operation'], **record['detail']['player']}
                      for record in self.records if record.get('operation') in ('player-construct', 'player-destruct') and
                      record.get('kind') in ('collection-enter-detail', 'collection-leave-detail') and
                      'player' in record.get('detail', {})]
        return {'periodicRegions': dict(counts), 'firstInvalidByAddress': first_invalid, 'boundaries': boundaries,
                'lifetimeVerified': False, 'animationAcceptance': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    evidence = CapturedEvidence(args.log)
    result = {'sourceLog': str(args.log.resolve()), 'identity': evidence.identity,
              'loads': evidence.loads(), 'players': evidence.players(), 'coverage': assess(evidence.records),
              'restorable': False, 'inGameAcceptance': False,
              'limitation': 'Byte lineage is not an OS file-read trace, object lifetime proof, or recovery acceptance.'}
    args.output.write_text(json.dumps(result, ensure_ascii=True, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'output': str(args.output.resolve()), 'loadCount': len(result['loads']),
                      'periodicRegions': result['players']['periodicRegions'], 'restorable': False}))


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, ProbeError) as error:
        raise SystemExit(f'COLLECTION_ANALYSIS_FAILED: {error}')
