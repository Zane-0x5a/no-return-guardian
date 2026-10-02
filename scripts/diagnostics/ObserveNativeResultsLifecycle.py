"""Record narrow native death-exit boundaries without controlling or stopping the game."""

import argparse
import json
from pathlib import Path
import sys
import threading
import time

from InspectPeVa import read_layout, va_to_offset
from NativeCheckpointProbe import ReadOnlyProcess, ProbeError, verify_build
from NativeCheckpointTrigger import process_birth


SITES = (0x104c570, 0x104d4f0, 0x1b6e000, 0x1bdeed0, 0x1042680, 0x1048520, 0xfe8fc0)


def arguments(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('pid', type=int)
    parser.add_argument('--log', type=Path, required=True)
    parser.add_argument('--duration', type=int, default=300)
    parser.add_argument('--confirm-intrusive-observation', action='store_true')
    args = parser.parse_args(argv)
    if args.pid <= 0 or not 5 <= args.duration <= 600:
        parser.error('A positive PID and duration from 5 to 600 seconds are required')
    if not args.confirm_intrusive_observation:
        parser.error('Frida observation requires explicit confirmation')
    if not args.log.is_absolute() or not args.log.parent.is_dir() or args.log.exists():
        parser.error('Use an unused absolute log path in an existing directory')
    return args


def fingerprints(image):
    base, headers, sections = read_layout(image)
    records = []
    for rva in SITES:
        offset, section = va_to_offset(base + rva, base, headers, sections)
        if section != '.text':
            raise ProbeError('Native boundary is outside executable code')
        records.append({'rva': rva, 'bytes': image[offset:offset + 32].hex()})
    return records


def main(argv=None):
    args = arguments(argv)
    process = ReadOnlyProcess(args.pid)
    session = None
    script = None
    done = threading.Event()
    errors = []
    try:
        path = process.image_path()
        image = path.read_bytes()
        digest = verify_build(image)
        base = process.image_base()
        birth = process_birth(process)
        records = fingerprints(image)
        for record in records:
            if process.read(base + record['rva'], 32).hex() != record['bytes']:
                raise ProbeError('Live code fingerprint mismatch')
        sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools/native-probe-deps'))
        import frida

        with args.log.open('x', encoding='utf-8') as output:
            output_lock = threading.Lock()

            def write(record):
                with output_lock:
                    output.write(json.dumps(record, separators=(',', ':')) + '\n')
                    output.flush()

            write({'kind': 'host-preflight', 'pid': args.pid, 'birth': birth,
                   'base': hex(base), 'sha256': digest, 'gameTerminationIssued': False})

            def message(payload, data):
                if payload['type'] == 'send':
                    record = payload['payload']
                    write(record)
                    if record.get('kind') == 'stopped':
                        if record.get('cleanupErrors') or 'error' in record.get('reason', ''):
                            errors.append(record.get('reason', 'hook cleanup failed'))
                        done.set()
                else:
                    errors.append(str(payload))
                    done.set()

            session = frida.attach(args.pid)
            session.on('detached', lambda reason, crash: done.set())
            if process_birth(process) != birth:
                raise ProbeError('Process changed during attach')
            source = Path(__file__).with_name('NativeResultsLifecycleProbe.js').read_text(encoding='utf-8')
            configuration = {'mode': 'results-lifecycle-observe', 'pid': args.pid,
                             'base': hex(base), 'duration': args.duration, 'fingerprints': records}
            script = session.create_script('globalThis.nativeResultsLifecycleConfiguration = ' +
                                           json.dumps(configuration) + ';\n' + source)
            script.on('message', message)
            script.load()
            done.wait(args.duration + 5)
            try:
                script.exports_sync.stop()
                script.unload()
                session.detach()
                write({'kind': 'host-detached', 'time': time.time(),
                       'gameTerminationIssued': False, 'errors': errors})
            except Exception as error:
                errors.append(str(error))
                write({'kind': 'host-detach-error', 'time': time.time(),
                       'gameTerminationIssued': False, 'reason': str(error)})
            script = None
            session = None
    finally:
        if script is not None:
            try:
                script.unload()
            except Exception:
                pass
        if session is not None:
            try:
                session.detach()
            except Exception:
                pass
        process.close()
    if errors:
        raise ProbeError('; '.join(errors))
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ProbeError, OSError, ValueError) as error:
        raise SystemExit(f'OBSERVATION_FAILED: {error}') from error
