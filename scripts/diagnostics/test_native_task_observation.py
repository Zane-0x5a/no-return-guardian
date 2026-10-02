import struct
import unittest
from unittest.mock import patch

from NativeCheckpointProbe import ProbeError, inspect_current_task, verify_code


class TaskMemory:
    base = 0x7FF600000000
    context = base + 0x9334770
    sentinel = context + 0xB0
    node = 0x200000
    task = 0x300000
    descriptor = 0x400000

    def __init__(self):
        self.memory = {}
        self.put(self.context, '<B', 0)
        self.put(self.sentinel, '<Q', self.node)
        self.put(self.node, '<Q', self.sentinel)
        self.put(self.node + 0x10, '<Q', self.task)
        self.put(self.task + 0xA0, '<I', 1 << 20)
        self.put(self.task + 0x120, '<Q', self.descriptor)
        self.put(self.descriptor + 0x6C, '<I', 2)
        self.reads = []

    def put(self, address, encoding, value):
        self.memory[address] = struct.pack(encoding, value)

    def read(self, address, size):
        self.reads.append((address, size))
        if address not in self.memory:
            raise ProbeError('unreadable task memory')
        return self.memory[address][:size]


class NativeTaskObservationTests(unittest.TestCase):
    def test_direct_candidate_preserves_native_word_only(self):
        for word in [0, 1, 2, 3, 0xFFFFFFFF]:
            with self.subTest(word=word):
                memory = TaskMemory()
                memory.put(memory.descriptor + 0x6C, '<I', word)
                result = inspect_current_task(memory.read, memory.base)
                self.assertEqual(result['status'], 'direct_candidate_observed')
                self.assertEqual(result['task'], hex(memory.task))
                self.assertEqual(result['descriptor_word'], word)
                self.assertFalse(result['usable_as_restore_gate'])

    def test_context_override_never_guesses_fallback_task(self):
        for value in [1, 2, 255]:
            with self.subTest(value=value):
                memory = TaskMemory()
                memory.put(memory.context, '<B', value)
                result = inspect_current_task(memory.read, memory.base)
                self.assertEqual(result['status'], 'fallback_required')
                self.assertNotIn('descriptor_word', result)
                self.assertEqual(set(memory.reads), {(memory.context, 1)})

    def test_empty_list_is_not_a_hideout(self):
        memory = TaskMemory()
        memory.put(memory.sentinel, '<Q', memory.sentinel)
        result = inspect_current_task(memory.read, memory.base)
        self.assertEqual(result['status'], 'no_current_task')
        self.assertNotIn('descriptor_word', result)

    def test_unflagged_head_is_not_current_task(self):
        memory = TaskMemory()
        memory.put(memory.task + 0xA0, '<I', 0)
        result = inspect_current_task(memory.read, memory.base)
        self.assertEqual(result['status'], 'fallback_required')
        self.assertNotIn((memory.task + 0x120, 8), memory.reads)

    def test_second_flagged_node_selected(self):
        memory = TaskMemory()
        memory.put(memory.task + 0xA0, '<I', 0)
        memory.put(memory.node, '<Q', memory.node + 0x100)
        memory.put(memory.node + 0x110, '<Q', memory.task + 0x1000)
        memory.put(memory.task + 0x10A0, '<I', 1 << 20)
        memory.put(memory.task + 0x1120, '<Q', memory.descriptor)
        result = inspect_current_task(memory.read, memory.base)
        self.assertEqual(result['task'], hex(memory.task + 0x1000))
        self.assertEqual(result['visited_nodes'], 2)

    def test_non_sentinel_cycle_rejected(self):
        memory = TaskMemory()
        memory.put(memory.task + 0xA0, '<I', 0)
        memory.put(memory.node, '<Q', memory.node)
        with self.assertRaisesRegex(ProbeError, 'cycle'):
            inspect_current_task(memory.read, memory.base)

    def test_bound_exhaustion_rejected(self):
        memory = TaskMemory()
        memory.put(memory.task + 0xA0, '<I', 0)
        memory.put(memory.node, '<Q', memory.node + 0x100)
        with self.assertRaisesRegex(ProbeError, 'limit'):
            inspect_current_task(memory.read, memory.base, max_nodes=1)

    def test_invalid_bounds_rejected_before_reading(self):
        for bound in [-1, 0, 65, 1.5, True]:
            memory = TaskMemory()
            with self.subTest(bound=bound), self.assertRaises(ProbeError):
                inspect_current_task(memory.read, memory.base, max_nodes=bound)
            self.assertEqual(memory.reads, [])

    def test_invalid_pointers_rejected(self):
        for field in [TaskMemory.sentinel, TaskMemory.node + 0x10,
                      TaskMemory.task + 0x120]:
            for pointer in [0, 1, 0xFFFF, 0x800000000000, 0xFFFFFFFFFFFFFFFF]:
                with self.subTest(field=field, pointer=pointer):
                    memory = TaskMemory()
                    memory.put(field, '<Q', pointer)
                    with self.assertRaises(ProbeError):
                        inspect_current_task(memory.read, memory.base)

    def test_short_read_rejected_at_every_field(self):
        reference = TaskMemory()
        for field in [reference.context, reference.sentinel, reference.node + 0x10,
                      reference.task + 0xA0, reference.task + 0x120,
                      reference.descriptor + 0x6C]:
            with self.subTest(field=field):
                memory = TaskMemory()
                memory.memory[field] = memory.memory[field][:-1]
                with self.assertRaisesRegex(ProbeError, 'short'):
                    inspect_current_task(memory.read, memory.base)

    def test_each_observed_field_is_revalidated(self):
        reference = TaskMemory()
        for field in [reference.context, reference.sentinel, reference.node + 0x10,
                      reference.task + 0xA0, reference.task + 0x120,
                      reference.descriptor + 0x6C]:
            with self.subTest(field=field):
                memory = TaskMemory()
                seen = set()

                def changing(address, size):
                    payload = memory.read(address, size)
                    if address == field and address in seen:
                        return bytes([payload[0] ^ 1]) + payload[1:]
                    seen.add(address)
                    return payload

                with self.assertRaisesRegex(ProbeError, 'changed'):
                    inspect_current_task(changing, memory.base)

    def test_unreadable_memory_propagates(self):
        memory = TaskMemory()
        del memory.memory[memory.task + 0xA0]
        with self.assertRaisesRegex(ProbeError, 'unreadable'):
            inspect_current_task(memory.read, memory.base)


class NativeFallbackObservationTests(unittest.TestCase):
    def setUp(self):
        self.memory = TaskMemory()
        self.memory.put(self.memory.context, '<B', 1)
        self.memory.put(self.memory.task + 0x90, '<Q', 0xEBADA5168620C5FE)
        self.memory.put(self.memory.task + 0xA0, '<I', 0)
        self.memory.put(self.memory.task + 0xB8, '<Q', 0x500000)
        self.memory.put(0x500058, '<B', 6)

    def observe(self, read=None):
        return inspect_current_task(read or self.memory.read, self.memory.base,
                                    include_simple_fallback=True)

    def test_verified_self_branch(self):
        result = self.observe()
        self.assertEqual(result['status'], 'fallback_self_observed')
        self.assertEqual(result['descriptor_word'], 2)
        self.assertFalse(result['usable_as_restore_gate'])

    def test_direct_miss_uses_simple_fallback(self):
        self.memory.put(self.memory.context, '<B', 0)
        self.assertEqual(self.observe()['status'], 'fallback_self_observed')

    def test_non_default_sid_needs_lookup_even_if_local_activity_valid(self):
        self.memory.put(self.memory.task + 0x90, '<Q', 0x1234)
        result = self.observe()
        self.assertEqual(result['status'], 'sid_lookup_required')
        self.assertNotIn('descriptor_word', result)

    def test_recursive_branch_cannot_be_replaced_by_root(self):
        self.memory.put(self.memory.task + 0xA0, '<I', 1)
        result = self.observe()
        self.assertEqual(result['status'], 'recursive_selector_required')
        self.assertNotIn('descriptor_word', result)

    def test_null_activity_is_inactive(self):
        self.memory.put(self.memory.task + 0xB8, '<Q', 0)
        self.assertEqual(self.observe()['status'], 'no_current_task')

    def test_unrelated_activity_bits_are_not_active(self):
        for flags in [0, 1, 16, 128, 241]:
            with self.subTest(flags=flags):
                self.memory.put(0x500058, '<B', flags)
                self.assertEqual(self.observe()['status'], 'no_current_task')

    def test_invalid_activity_pointer_rejected(self):
        for value in [1, 0xFFFF, 0xFFFFFFFFFFFFFFFF]:
            with self.subTest(value=value):
                self.memory.put(self.memory.task + 0xB8, '<Q', value)
                with self.assertRaises(ProbeError):
                    self.observe()

    def test_inactive_cycle_rejected(self):
        self.memory.put(0x500058, '<B', 0)
        self.memory.put(self.memory.node, '<Q', self.memory.node)
        with self.assertRaisesRegex(ProbeError, 'cycle'):
            self.observe()

    def test_fallback_fields_are_revalidated(self):
        for field in [self.memory.task + 0x90, self.memory.task + 0xB8, 0x500058]:
            with self.subTest(field=field):
                seen = set()

                def changing(address, size):
                    payload = self.memory.read(address, size)
                    if address == field and address in seen:
                        return bytes([payload[0] ^ 1]) + payload[1:]
                    seen.add(address)
                    return payload

                with self.assertRaisesRegex(ProbeError, 'changed'):
                    self.observe(changing)


class NativeTaskFingerprintTests(unittest.TestCase):
    def test_entire_supported_selector_bodies_are_checked(self):
        ranges = [(0x1B6EA60, 0xBF), (0x1046F80, 0x32), (0x1046FC0, 0x32),
                  (0x1BDE410, 0x4F), (0x1BDDB60, 0xD0)]
        for rva, size in ranges:
            with self.subTest(rva=rva):
                process = TaskMemory()

                def changed_body(address, length):
                    if address == process.base + rva and length == size:
                        return bytes(length - 1) + b'\1'
                    return bytes(length)

                process.read = changed_body
                with patch('NativeCheckpointProbe.read_layout', return_value=(0x140000000, 0, [])), \
                     patch('NativeCheckpointProbe.va_to_offset', return_value=(0, '.text')), \
                     self.assertRaisesRegex(ProbeError, 'task selector fingerprint'):
                    verify_code(process, bytes(256), process.base, include_task=True)


if __name__ == '__main__':
    unittest.main()
