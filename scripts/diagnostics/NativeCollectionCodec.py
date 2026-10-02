"""Validate and reconstruct lossless collection chunks without game access."""

from collections import OrderedDict
import hashlib

from NativeCheckpointProbe import ProbeError


ENCODING = 'sha256-chunks-v1'


def chunk_layout(event, known):
    if event.get('encoding') != ENCODING:
        raise ProbeError('Unsupported collection payload encoding')
    length = event.get('byteLength')
    wire_length = event.get('wireByteLength')
    if type(length) is not int or not 0 < length <= 32 * 1024 * 1024 or \
            type(wire_length) is not int or not 0 <= wire_length <= length:
        raise ProbeError('Invalid collection encoded lengths')
    chunks = event.get('chunks')
    if not isinstance(chunks, list) or not 0 < len(chunks) <= 4096:
        raise ProbeError('Invalid collection chunk count')
    cursor = 0
    wire_cursor = 0
    additions = {}
    result = []
    for chunk in chunks:
        if not isinstance(chunk, dict) or set(chunk) not in (
                {'sha256', 'size'}, {'sha256', 'size', 'wireOffset'}):
            raise ProbeError('Invalid collection chunk descriptor')
        digest = chunk['sha256']
        size = chunk['size']
        if not isinstance(digest, str) or len(digest) != 64 or any(
                value not in '0123456789abcdef' for value in digest):
            raise ProbeError('Invalid collection chunk digest')
        if type(size) is not int or not 0 < size <= 65536:
            raise ProbeError('Invalid collection chunk size')
        if digest in known and known[digest] != size:
            raise ProbeError('Collection chunk size mismatch')
        if 'wireOffset' in chunk:
            if type(chunk['wireOffset']) is not int or chunk['wireOffset'] != wire_cursor:
                raise ProbeError('Invalid collection chunk wire offset')
            if digest in additions and additions[digest] != size:
                raise ProbeError('Conflicting collection chunk sizes')
            additions[digest] = size
            wire_cursor += size
        elif digest not in known:
            raise ProbeError('Missing prior collection chunk')
        result.append((cursor, chunk))
        cursor += size
    if cursor != length or wire_cursor != wire_length:
        raise ProbeError('Collection chunks do not span declared lengths')
    regions = event.get('regions')
    if not isinstance(regions, list) or not 0 < len(regions) <= 256:
        raise ProbeError('Invalid collection region count')
    cursor = 0
    for region in regions:
        if region.get('status') == 'unallocated':
            if 'offset' in region:
                raise ProbeError('Unallocated collection region has bytes')
            continue
        if type(region.get('size')) is not int or not 0 < region['size'] <= 16 * 1024 * 1024 or \
                type(region.get('offset')) is not int or region['offset'] != cursor:
            raise ProbeError('Invalid collection region layout')
        cursor += region['size']
    if cursor != length:
        raise ProbeError('Collection regions do not span byteLength')
    if len(known.keys() | additions.keys()) > 65536:
        raise ProbeError('Collection chunk index budget exceeded')
    return result, additions


class PayloadDecoder:
    def __init__(self, directory):
        self.directory = directory
        self.known = {}
        self.locations = {}
        self.cached = OrderedDict()
        self.cached_bytes = 0

    def decode(self, event, wire):
        layout, additions = chunk_layout(event, self.known)
        if len(wire) != event['wireByteLength']:
            raise ProbeError('Collection wire length mismatch')
        decoded = bytearray(event['byteLength'])
        for offset, chunk in layout:
            digest = chunk['sha256']
            if 'wireOffset' in chunk:
                start = chunk['wireOffset']
                content = wire[start:start + chunk['size']]
            else:
                content = self.cached.get(digest)
                if content is None:
                    filename, start, size = self.locations[digest]
                    target = self.directory / filename
                    if target.is_symlink() or not target.resolve().is_relative_to(self.directory.resolve()):
                        raise ProbeError('Collection chunk source escapes archive')
                    with target.open('rb') as source:
                        source.seek(start)
                        content = source.read(size)
            if len(content) != chunk['size'] or hashlib.sha256(content).hexdigest() != digest:
                raise ProbeError('Collection chunk hash mismatch')
            decoded[offset:offset + chunk['size']] = content
        return bytes(decoded), layout, additions

    def remember(self, filename, decoded, layout, additions):
        self.known.update(additions)
        for offset, chunk in layout:
            digest = chunk['sha256']
            if 'wireOffset' in chunk:
                self.locations[digest] = (filename, offset, chunk['size'])
            previous = self.cached.pop(digest, None)
            if previous is not None:
                self.cached_bytes -= len(previous)
            self.cached[digest] = decoded[offset:offset + chunk['size']]
            self.cached_bytes += chunk['size']
            while self.cached_bytes > 8 * 1024 * 1024:
                self.cached_bytes -= len(self.cached.popitem(last=False)[1])
