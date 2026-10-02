"""Offline model of the observed jdom full offset/pointer conversion."""

from collections import Counter
import struct

from NativeCheckpointProbe import ProbeError


class SerializedArena:
    def __init__(self, data, address=0):
        self.data = bytes(data)
        self.address = address
        self.pointer_fields = set()
        self.containers = set()
        self.visited_slots = set()
        self.types = Counter()
        if type(address) is not int or address < 0 or address % 8 or address + len(data) >= 0x800000000000:
            raise ProbeError('Invalid serialized arena address')
        if not 0x40 <= len(data) <= 16 * 1024 * 1024 or self.u32(0x38) != len(data) or self.u64(0x30) != address:
            raise ProbeError('Serialized arena descriptor or size mismatch')
        if self.data[0] not in (5, 6):
            raise ProbeError('Unsupported serialized root type')
        value = self.pointer(8, 8)
        self.pointer(0x10, 1)
        self.pointer_fields.add(0x30)
        self.visit(value, self.data[0], 0)

    def bounds(self, offset, size):
        if offset < 0 or size < 0 or offset + size > len(self.data):
            raise ProbeError('Serialized arena reference is out of bounds')
        return offset

    def u64(self, offset):
        if offset % 8:
            raise ProbeError('Serialized pointer or value slot is misaligned')
        return struct.unpack_from('<Q', self.data, self.bounds(offset, 8))[0]

    def u32(self, offset):
        return struct.unpack_from('<I', self.data, self.bounds(offset, 4))[0]

    def pointer(self, field, size, optional=False):
        value = self.u64(field)
        if value == 0 and optional:
            return None
        if value == 0 or not self.address <= value < self.address + len(self.data):
            raise ProbeError('Serialized arena contains an external or null required reference')
        self.pointer_fields.add(field)
        return self.bounds(value - self.address, size)

    def visit(self, slot, kind, depth):
        self.bounds(slot, 8)
        if slot % 8 or slot in self.visited_slots:
            raise ProbeError('Serialized value slot is misaligned or has repeated ownership')
        self.visited_slots.add(slot)
        self.types[kind] += 1
        if depth > 128 or sum(self.types.values()) > 1000000:
            raise ProbeError('Serialized arena traversal budget exceeded')
        if kind in (0, 1, 2, 3, 7):
            return
        if kind == 4:
            string = self.pointer(slot, 1, optional=True)
            if string is not None and self.data.find(b'\0', string) < 0:
                raise ProbeError('Serialized string is not terminated within the arena')
            return
        if kind not in (5, 6):
            raise ProbeError('Unsupported serialized value type')
        node = self.pointer(slot, 0x20)
        if node in self.containers:
            raise ProbeError('Serialized container has a cycle or repeated ownership')
        self.containers.add(node)
        count = self.u32(node + 4)
        if count > 1000000:
            raise ProbeError('Invalid serialized container count')
        values = self.pointer(node + 8, count * 8, optional=count == 0)
        kinds = self.pointer(node + 0x10, count, optional=count == 0)
        self.pointer(node + 0x18, 1, optional=True)
        for index in range(count):
            self.visit(values + index * 8, self.data[kinds + index], depth + 1)

    def relocate(self, destination):
        if type(destination) is not int or destination < 0 or destination % 8 or destination + len(self.data) >= 0x800000000000:
            raise ProbeError('Invalid serialized relocation destination')
        result = bytearray(self.data)
        for field in self.pointer_fields:
            value = self.u64(field)
            struct.pack_into('<Q', result, field, value - self.address + destination)
        return bytes(result)
