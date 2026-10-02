"""Exercise the nested native-call bridge in a disposable child, never the game."""

import argparse
import json
from pathlib import Path
import subprocess
import sys
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--traps', choices=('all', 'default'), default='default')
    parser.add_argument('--scheduling', choices=('exclusive', 'cooperative'), default='exclusive')
    parser.add_argument('--iterations', type=int, default=100)
    parser.add_argument('--hang', action='store_true')
    args = parser.parse_args()
    if not 1 <= args.iterations <= 1000:
        parser.error('iterations must be between 1 and 1000')
    root = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(root / 'tools' / 'native-probe-deps'))
    import frida

    child = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(60)'],
                             creationflags=subprocess.CREATE_NO_WINDOW)
    session = None
    script = None
    completed = threading.Event()
    messages = []
    expired = threading.Event()

    def deadline():
        expired.set()
        if child.poll() is None:
            child.terminate()
        completed.set()

    watchdog = threading.Timer(15, deadline)
    watchdog.daemon = True
    watchdog.start()

    def receive(message, data):
        messages.append(message)
        print(json.dumps(message), flush=True)
        if message.get('type') == 'error' or message.get('payload', {}).get('kind') == 'done':
            completed.set()

    try:
        session = frida.attach(child.pid)
        session.on('detached', lambda *unused: completed.set())
        configuration = json.dumps(vars(args))
        source = 'const configuration = ' + configuration + ';\n' + r'''
const counter = Memory.alloc(4);
const nativeCode = new CModule(`
extern volatile int counter;
int checkpoint(int first, int second) { counter++; return 1 + first + second; }
int idle(int value) { return value + 1; }
void hang(void) { for (;;) {} }
`, {counter});
let invoked = false;
let entries = 0;
let returned = 0;
const originals = [nativeCode.idle, nativeCode.checkpoint].map(address =>
    Array.from(new Uint8Array(address.readByteArray(32))));
const listeners = [];
const checkpoint = new NativeFunction(nativeCode.checkpoint, 'bool', ['bool', 'bool'], {
    abi: 'win64', exceptions: 'propagate', scheduling: configuration.scheduling,
    traps: configuration.traps
});
listeners.push(Interceptor.attach(nativeCode.checkpoint, {
    onEnter() { entries++; send({kind: 'checkpoint-enter'}); },
    onLeave(result) { send({kind: 'checkpoint-return', result: result.toInt32()}); }
}));
listeners.push(Interceptor.attach(nativeCode.idle, {
    onLeave() {
        if (invoked) return;
        invoked = true;
        send({kind: 'invoke'});
        const result = checkpoint(0, 0);
        if (result !== 1) throw new Error('Wrong checkpoint result');
        returned++;
        send({kind: 'invoke-returned', result});
    }
}));
Interceptor.flush();
const idle = new NativeFunction(nativeCode.idle, 'int', ['int'], {traps: 'all'});
setTimeout(() => {
    if (configuration.hang) {
        new NativeFunction(nativeCode.hang, 'void', [], {scheduling: 'exclusive'})();
        throw new Error('Hang unexpectedly returned');
    }
    for (let iteration = 0; iteration < configuration.iterations; iteration++) {
        invoked = false;
        if (idle(10) !== 11) throw new Error('Wrong idle result');
    }
    for (const listener of listeners) listener.detach();
    Interceptor.flush();
    const restored = [nativeCode.idle, nativeCode.checkpoint].every((address, index) =>
        Array.from(new Uint8Array(address.readByteArray(32))).every(
            (value, offset) => value === originals[index][offset]));
    const executions = counter.readS32();
    send({kind: 'done', entries, invoked, returned, executions, restored});
}, 20);
'''
        script = session.create_script(source)
        script.on('message', receive)
        script.load()
        completed.wait(16)
        return 0 if not expired.is_set() and any(
            message.get('payload', {}).get('kind') == 'done' and
            message['payload'].get('returned') == args.iterations and
            message['payload'].get('executions') == args.iterations and
            message['payload'].get('restored') is True for message in messages) else 3
    finally:
        if child.poll() is None:
            child.terminate()
        child.wait(timeout=10)
        watchdog.cancel()
        print(json.dumps({'kind': 'child-exited', 'pid': child.pid,
                          'exitCode': child.returncode, 'deadlineExpired': expired.is_set(),
                          'time': time.time()}), flush=True)
        if session is not None:
            try:
                session.detach()
            except frida.InvalidOperationError:
                pass


if __name__ == '__main__':
    raise SystemExit(main())
