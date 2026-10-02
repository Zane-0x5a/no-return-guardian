"""Offline model of full native load copies and temporary-to-cache promotion."""

from dataclasses import dataclass
import struct

from NativeSerializedArena import ProbeError, SerializedArena


BODY_OFFSET = 0x590
PREFIX_SIZE = 0x414
ARENA_OFFSET = 0x418
MAX_CAPACITY = 16 * 1024 * 1024
OBSERVED_VERSION = 27


def buffer_address(address, capacity):
    if type(address) is not int or address < 0 or address % 8 or address + capacity >= 0x800000000000:
        raise ProbeError('Invalid native save buffer address')
    return address


@dataclass(frozen=True)
class SaveBufferCopy:
    source_after: bytes
    destination: bytes
    destination_address: int
    capacity: int


class NativeSaveBuffer:
    def __init__(self, data, capacity=None, address=0, serialized=True):
        self.data = bytes(data)
        self.capacity = len(self.data) if capacity is None else capacity
        if type(self.capacity) is not int or not ARENA_OFFSET + 0x40 <= self.capacity <= MAX_CAPACITY:
            raise ProbeError('Invalid native save buffer capacity')
        if not ARENA_OFFSET + 0x40 <= len(self.data) <= self.capacity:
            raise ProbeError('Native save buffer is truncated or exceeds capacity')
        self.address = buffer_address(address, self.capacity)
        if type(serialized) is not bool or (not serialized and not address):
            raise ProbeError('Native pointer buffer requires its actual address')
        self.serialized = serialized
        self.checksum, self.declared_size, self.version = struct.unpack_from('<III', self.data)
        if not ARENA_OFFSET + 0x40 <= self.declared_size <= len(self.data):
            raise ProbeError('Native save declaration exceeds available body')
        if self.version != OBSERVED_VERSION:
            raise ProbeError('Unsupported native save body version')
        end = self.data.find(b'\0', 0x14, PREFIX_SIZE)
        if end < 0:
            raise ProbeError('Native save description is not terminated in the prefix')
        try:
            self.description = self.data[0x14:end].decode('utf-8')
        except UnicodeError as error:
            raise ProbeError('Native save description is not UTF-8') from error
        arena_size = struct.unpack_from('<I', self.data, ARENA_OFFSET + 0x38)[0]
        if arena_size != self.declared_size - ARENA_OFFSET:
            raise ProbeError('Native save arena does not cover the declared body')
        self.arena = SerializedArena(self.data[ARENA_OFFSET:self.declared_size],
                                     0 if serialized else address + ARENA_OFFSET)

    @classmethod
    def from_working_raw(cls, raw, capacity=None):
        if len(raw) < BODY_OFFSET or raw[:16] != bytes.fromhex('93ea' + '00' * 14):
            raise ProbeError('Unsupported native working header')
        body = cls(raw[BODY_OFFSET:], capacity=capacity)
        if body.declared_size != len(body.data):
            raise ProbeError('Working raw length does not match its body declaration')
        return body

    def copy_and_fix_up(self, destination_address, direction, destination_capacity=None, stage=-1):
        capacity = self.capacity if destination_capacity is None else destination_capacity
        if type(direction) is not int or direction not in (0, 2) or type(stage) is not int or stage != -1:
            raise ProbeError('Only full native load and promotion copies are modeled')
        if type(capacity) is not int or not len(self.data) <= capacity <= MAX_CAPACITY:
            raise ProbeError('Native copy exceeds destination capacity')
        buffer_address(destination_address, capacity)
        if not destination_address:
            raise ProbeError('Native copy requires a non-null destination')
        if self.address and max(self.address, destination_address) < min(
                self.address + len(self.data), destination_address + len(self.data)):
            raise ProbeError('Native copy buffers overlap')
        if direction == 2 and not self.serialized:
            raise ProbeError('Native loading requires serialized input')
        if direction == 0 and (self.serialized or capacity != self.capacity or len(self.data) != capacity):
            raise ProbeError('Promotion requires pointer input and complete equal-capacity buffers')
        destination = (self.data[:ARENA_OFFSET] + self.arena.relocate(destination_address + ARENA_OFFSET)
                       + self.data[self.declared_size:])
        return SaveBufferCopy(self.data if direction == 2 else destination,
                              destination, destination_address, capacity)
