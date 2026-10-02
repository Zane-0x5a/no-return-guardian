#!/usr/bin/env python3
"""Disassemble PE ranges in an owned Frida process without loading the game."""

import argparse
from pathlib import Path
import subprocess
import sys

from InspectPeVa import read_layout, va_to_offset


def parse_range(value):
    start, size = value.split(':', 1)
    return int(start, 0), int(size, 0)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image', type=Path)
    parser.add_argument('ranges', nargs='+', type=parse_range)
    args = parser.parse_args()
    image = args.image.read_bytes()
    image_base, headers, sections = read_layout(image)
    ranges = []
    for start, size in args.ranges:
        address = start if start >= image_base else image_base + start
        offset, section = va_to_offset(address, image_base, headers, sections)
        end_offset, end_section = va_to_offset(address + size - 1, image_base, headers, sections)
        if size <= 0 or section != end_section or end_offset != offset + size - 1:
            raise ValueError('Range crosses a PE section boundary')
        ranges.append({'address': address, 'bytes': list(image[offset:offset + size])})

    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools/native-probe-deps'))
    import frida

    child = subprocess.Popen([sys.executable, '-c', 'input()'], stdin=subprocess.PIPE,
                             stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                             creationflags=subprocess.CREATE_NO_WINDOW)
    session = None
    try:
        session = frida.attach(child.pid)
        script = session.create_script('''
            rpc.exports = { disassemble(ranges) {
                return ranges.map(range => {
                    const memory = Memory.alloc(range.bytes.length);
                    memory.writeByteArray(range.bytes);
                    const lines = [];
                    let offset = 0;
                    while (offset < range.bytes.length) {
                        const instruction = Instruction.parse(memory.add(offset));
                        if (offset + instruction.size > range.bytes.length) break;
                        let operands = instruction.opStr;
                        if (/^(call|j[a-z]+|loop[a-z]*)$/.test(instruction.mnemonic) &&
                            /^0x[0-9a-f]+$/i.test(operands)) {
                            const relocated = ptr(operands);
                            operands = '0x' + ptr(range.address).add(relocated.sub(memory)).toString(16);
                        }
                        lines.push({address: range.address + offset,
                            mnemonic: instruction.mnemonic, operands,
                            size: instruction.size});
                        offset += instruction.size;
                    }
                    return {address: range.address, lines, complete: offset === range.bytes.length};
                });
            }};
        ''')
        script.load()
        for result in script.exports_sync.disassemble(ranges):
            print(f"RANGE VA=0x{result['address']:X} COMPLETE={result['complete']}")
            for instruction in result['lines']:
                print(f"0x{instruction['address']:X}: {instruction['mnemonic']:<8} {instruction['operands']}")
        script.unload()
    finally:
        if session is not None:
            session.detach()
        child.communicate(b'\n', timeout=5)


if __name__ == '__main__':
    main()
