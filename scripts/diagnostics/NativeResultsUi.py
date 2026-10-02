"""Bounded, read-only model of the pinned build's menu stack and focus getters."""

import struct

from NativeCheckpointProbe import ProbeError


class TransientUiChange(ProbeError):
    """The menu graph changed while it was being read; a later read may be stable."""


def string_id(value):
    result = 0xcbf29ce484222325
    for byte in value.encode('utf-8'):
        result = ((result ^ byte) * 0x100000001b3) & 0xffffffffffffffff
    return result


RESULT_PAGES = {string_id(name): name for name in (
    't2r-menu-post/page-scores', 't2r-menu-post/page-meta',
    't2r-menu-post/page-leaderboard-post',
    # Popped by name in ss-post-encounter-flow show-rewards; its +130 hash is not yet observed live.
    't2r-meta-challenges/message-box-ok-meta-challenge-unlock')}


def inspect_results_ui(read, base):
    evidence = []
    budget = 8192

    def scalar(address, encoding):
        nonlocal budget
        size = struct.calcsize(encoding)
        budget -= 1
        if budget < 0 or not 0x10000 <= address < 0x800000000000 - size:
            raise ProbeError('Results UI read exceeds its bounded address model')
        payload = read(address, size)
        if len(payload) != size:
            raise ProbeError('Short results UI read')
        evidence.append((address, payload))
        return struct.unpack(encoding, payload)[0]

    def pointer(address):
        return scalar(address, '<Q')

    def handle(address):
        node = pointer(address)
        generation = scalar(address + 8, '<I')
        if not node or not generation:
            return 0
        instance = pointer(node + 8)
        if not instance or scalar(instance + 0x94, '<I') != generation:
            return 0
        return instance

    def has_type(instance, type_rva):
        type_index = scalar(base + type_rva, '<I')
        if type_index >= 4096:
            raise ProbeError('Unknown results UI type index')
        descriptor = pointer(instance + 0x40)
        return bool(scalar(descriptor + 0x28 + (type_index // 64) * 8, '<Q') & (1 << (type_index % 64)))

    def relative(instance, offset):
        node = pointer(instance + 0x80)
        related = pointer(node + offset) if node else 0
        return pointer(related + 8) if related else 0

    def components(instance):
        child = relative(instance, 0x30)
        seen = set()
        while child:
            if child in seen or len(seen) >= 256:
                raise ProbeError('Results UI child list is cyclic or too large')
            seen.add(child)
            if has_type(child, 0x9497e88) and not scalar(child + 0xc, '<B') & 1:
                yield child
            child = relative(child, 0x38)

    def contains_focus(page, focus):
        pending = [page]
        seen = set()
        while pending:
            child = pending.pop()
            if child in seen or len(seen) >= 512:
                raise ProbeError('Results UI component tree is cyclic or too large')
            seen.add(child)
            if child == focus:
                return True
            pending.extend(components(child))
        return False

    manager = handle(base + 0x92d08d8)
    if not manager:
        raise ProbeError('Results UI manager is unavailable')
    focus = handle(manager + 0x4c0)
    bucket_count = scalar(base + 0x37d69b8, '<I')
    if not 1 <= bucket_count <= 4096:
        raise ProbeError('Unknown results UI stack registry size')
    buckets = pointer(base + 0x37d6928)
    sentinel = pointer(buckets + bucket_count * 8)
    seen = set()
    pages = []
    focused = []
    top_results = []
    for bucket in range(bucket_count):
        node = pointer(buckets + bucket * 8)
        while node and node != sentinel:
            if node in seen or len(seen) >= 64:
                raise ProbeError('Results UI stack registry is cyclic or too large')
            seen.add(node)
            stack = handle(pointer(node + 0x18))
            if stack and has_type(stack, 0x9493ba8):
                children = list(components(stack))
                for page in reversed(children):
                    if not has_type(page, 0x9493978):
                        break
                    if scalar(page + 0x8a1, '<B') & 1:
                        continue
                    page_id = pointer(page + 0x130)
                    pages.append({'instanceId': hex(pointer(page + 0xc0)), 'resourceId': hex(page_id)})
                    if page_id in RESULT_PAGES:
                        top_results.append((page, page_id))
                    if page_id in RESULT_PAGES and focus and contains_focus(page, focus):
                        focused.append((page, page_id))
                    break
            node = pointer(node)
    if len(focused) > 1 or len(top_results) > 1:
        raise ProbeError('Results pages are on top of multiple page stacks')
    focus_id = hex(pointer(focus + 0xc0)) if focus else None
    for address, payload in reversed(evidence):
        if read(address, len(payload)) != payload:
            raise TransientUiChange('Results UI changed during observation')
    return {'page': RESULT_PAGES[focused[0][1]] if focused else None,
            'pageAddress': hex(focused[0][0]) if focused else None,
            'topPage': RESULT_PAGES[top_results[0][1]] if top_results else None,
            'topPageAddress': hex(top_results[0][0]) if top_results else None,
            'focusAddress': hex(focus), 'focusId': focus_id, 'topPages': pages}
