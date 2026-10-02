"""Read bounded exception, module and stack evidence from an existing Windows dump."""

import argparse
import json
from pathlib import Path
import struct

from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def inspect_dump(path):
    data = path.read_bytes()
    if data[:4] != b'MDMP':
        raise ValueError('Not a Windows minidump')
    count, directory = struct.unpack_from('<II', data, 8)
    streams = {}
    for index in range(count):
        kind, size, offset = struct.unpack_from('<III', data, directory + index * 12)
        streams[kind] = (offset, size)
    modules = []
    offset = streams[4][0]
    for index in range(struct.unpack_from('<I', data, offset)[0]):
        record = offset + 4 + index * 108
        base, size, checksum, timestamp, name = struct.unpack_from('<QIIII', data, record)
        length = struct.unpack_from('<I', data, name)[0]
        modules.append({'base': base, 'size': size,
                        'path': data[name + 4:name + 4 + length].decode('utf-16-le')})
    ranges = []
    if 9 in streams:
        offset = streams[9][0]
        count, location = struct.unpack_from('<QQ', data, offset)
        for index in range(count):
            base, size = struct.unpack_from('<QQ', data, offset + 16 + index * 16)
            ranges.append((base, size, location))
            location += size
    if 5 in streams:
        offset = streams[5][0]
        for index in range(struct.unpack_from('<I', data, offset)[0]):
            base, size, location = struct.unpack_from('<QII', data, offset + 4 + index * 16)
            ranges.append((base, size, location))

    def read_memory(address, size):
        for base, length, location in ranges:
            if base <= address and address + size <= base + length:
                return data[location + address - base:location + address - base + size]
        return b''

    def label(address):
        for module in modules:
            if module['base'] <= address < module['base'] + module['size']:
                return Path(module['path']).name + '+' + hex(address - module['base'])
        return None

    offset = streams[6][0]
    thread = struct.unpack_from('<I', data, offset)[0]
    code, flags, nested, address, count = struct.unpack_from('<IIQQI', data, offset + 8)
    parameters = struct.unpack_from('<' + 'Q' * count, data, offset + 40)
    context_size, context = struct.unpack_from('<II', data, offset + 160)
    registers = dict(zip(('rax', 'rcx', 'rdx', 'rbx', 'rsp', 'rbp', 'rsi', 'rdi',
                         'r8', 'r9', 'r10', 'r11', 'r12', 'r13', 'r14', 'r15', 'rip'),
                        struct.unpack_from('<17Q', data, context + 120)))
    stack = []
    for displacement in range(0, 2048, 8):
        raw = read_memory(registers['rsp'] + displacement, 8)
        if not raw:
            break
        value = struct.unpack('<Q', raw)[0]
        if label(value):
            stack.append({'offset': hex(displacement), 'value': hex(value), 'module': label(value)})
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    code_bytes = read_memory(address, 96)
    disassembly = [f'{instruction.address:#x}: {instruction.mnemonic} {instruction.op_str}'
                   for instruction in decoder.disasm(code_bytes, address)]
    return {'dump': str(path), 'thread': thread, 'exceptionCode': hex(code),
            'exceptionAddress': hex(address), 'exceptionModule': label(address),
            'parameters': [hex(value) for value in parameters],
            'registers': {name: hex(value) for name, value in registers.items()},
            'instructions': disassembly, 'stackModuleCandidatesNotUnwound': stack,
            'registerMemory': {name: read_memory(value, 64).hex() for name, value in registers.items()},
            'modules': [{**module, 'base': hex(module['base'])} for module in modules]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dump', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    evidence = inspect_dump(args.dump)
    args.output.write_text(json.dumps(evidence, indent=2), encoding='utf-8')
    print(json.dumps({key: value for key, value in evidence.items() if key != 'modules'}, indent=2))


if __name__ == '__main__':
    main()
