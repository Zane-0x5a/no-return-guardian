"""Execute the exact pinned dispatcher bytes in an owned process with inert callees."""

import argparse
import json
from pathlib import Path
import subprocess
import sys
import threading

from InspectPeVa import read_layout, va_to_offset
from NativeRecoverySource import prepare_source


JAVASCRIPT = r'''
const code = CODE;
const imageBase = Memory.alloc(0x1600000);
Memory.protect(imageBase, 0x1600000, 'rwx');
const dispatcher = imageBase.add(0x133ebb0);
dispatcher.writeByteArray(code);
imageBase.add(0x156bc30).writeByteArray([0x0f,0x57,0xc0,0xc3]);
const noop = imageBase.add(0x1000);
noop.writeByteArray([0xc3]);
const vtable = Memory.alloc(0x200);
vtable.writeByteArray(new Uint8Array(0x200));
for (const offset of [0x80,0x88,0x90]) vtable.add(offset).writePointer(noop);
const owner = Memory.alloc(0x100);
owner.writeByteArray(new Uint8Array(0x100));
owner.writePointer(vtable);
const counter = Memory.alloc(8);
counter.writeU64(0);
const module = new CModule(`
extern void update(void *owner);
extern unsigned long long counter;
unsigned int run(void *owner) {
    for (unsigned int index=0; index<100000; index++) {
        update(owner);
        counter++;
    }
    return 0;
}`, {update:dispatcher,counter});
let enters=0,leaves=0;
let listener=null;
if (HOOK) listener=Interceptor.attach(dispatcher,{
    onEnter(args){this.rootUpdate=args[0].equals(owner);enters++;},
    onLeave(){if(this.rootUpdate)leaves++;}
});
Interceptor.flush();
const createThread = new NativeFunction(Module.getGlobalExportByName('CreateThread'),'pointer',
    ['pointer','size_t','pointer','pointer','uint','pointer'],{abi:'win64',scheduling:'cooperative'});
const waitThread = new NativeFunction(Module.getGlobalExportByName('WaitForSingleObject'),'uint',
    ['pointer','uint'],{abi:'win64',scheduling:'cooperative'});
const closeThread = new NativeFunction(Module.getGlobalExportByName('CloseHandle'),'bool',['pointer']);
rpc.exports={run(){
    const thread=createThread(ptr(0),0,module.run,owner,0,ptr(0));
    if(thread.isNull())throw new Error('Failed owned thread');
    const result=waitThread(thread,10000);
    if(result!==0)throw new Error('Owned dispatcher timeout');
    closeThread(thread);
    if(listener)listener.detach();
    Interceptor.flush();
    return {enters,leaves,calls:counter.readU64().toString(),hook:HOOK,gameOpened:false};
}};
'''


BRIDGE_SETUP = r'''
Process.setExceptionHandler(details=>{
    send({kind:'owned-exception',type:details.type,address:details.address.toString(),
        memory:details.memory,context:details.context});
    return false;
});
const code = CODE;
const imageBase = Memory.alloc(0x9400000);
Memory.protect(imageBase, 0x1600000, 'rwx');
for(const rva of [0x1b7fc60,0x1e2f880,0x1b6e000,0x1bdeed0,0x1b6ea60])
    Memory.protect(imageBase.add(rva & ~4095),4096,'rwx');
const dispatcher = imageBase.add(0x133ebb0);
dispatcher.writeByteArray(code);
imageBase.add(0x156bc30).writeByteArray([0x0f,0x57,0xc0,0xc3]);
const noop=imageBase.add(0x1000);
noop.writeByteArray([0xc3]);
const vtable=Memory.alloc(0x200);
vtable.writeByteArray(new Uint8Array(0x200));
for(const offset of [0x80,0x88,0x90])vtable.add(offset).writePointer(noop);
const owner=Memory.alloc(0x100);
owner.writeByteArray(new Uint8Array(0x100));owner.writePointer(vtable);
const counter=Memory.alloc(8);counter.writeU64(0);
const rvas=[0x133ebb0,0x133ec5e,0xfe7b10,0xfe96f0,0x1b7fc60,0x1e2f880,0x120af40,
    0x120d090,0xfd7870,0x1042680,0x103f2c0,0x1b6e000,0x1bdeed0,0xdc1780,0x1b6ea60,
    0x1c55df0,0x1c55a3c,0x1c57bb0];
Memory.protect(imageBase.add(0x1c55000),0x3000,'rwx');
for(const rva of rvas){
    if(rva===0x133ebb0 || rva===0x133ec5e)continue;
    imageBase.add(rva).writeByteArray(new Uint8Array(32).fill(0x90));
    imageBase.add(rva+24).writeByteArray([0xc3]);
}
const statModule=new CModule(`
static const unsigned long long keys[10]={0xca79be3ddf469b1aULL,0x8dd8bbea1823e133ULL,0xe0a765d96d4dd778ULL,
    0x34ae6a71b265a278ULL,0x524ef3c2f60dc853ULL,0x6172419d00e62123ULL,0x966ac5e93688c099ULL,
    0x966ac2e93688bb80ULL,0xc34a84af6ff26f6fULL,0xdb3441ab83811bdeULL};
int find_stat(void *service,unsigned long long key){for(int index=0;index<10;index++)if(keys[index]==key)return index;return -1;}
unsigned char set_stat(void *service,int value,unsigned long long key,unsigned char quiet){
    int index=find_stat(service,key);
    if(index<0)return 0;
    (*(int **)((char *)service+0x30))[index*6+2]=value;
    return 1;
}`);
const statService=Memory.alloc(0x40);
const statEntries=Memory.alloc(24*10);
statEntries.writeByteArray(new Uint8Array(24*10));
statService.writePointer(imageBase.add(0x2af57d8));
statService.add(0x30).writePointer(statEntries);
imageBase.add(0x2af57d8+0x20).writePointer(imageBase.add(0x1c57bb0));
imageBase.add(0x4248b78).writePointer(statService);
for(const [rva,target] of [[0x1c55df0,statModule.find_stat],[0x1c57bb0,statModule.set_stat]]){
    imageBase.add(rva).writeByteArray([0x48,0xb8]);
    imageBase.add(rva+2).writePointer(target);
    imageBase.add(rva+10).writeByteArray([0xff,0xe0]);
}
imageBase.add(0xfe7b10).writeByteArray([0x48,0xb8]);
imageBase.add(0xfe7b10+2).writePointer(owner);
imageBase.add(0xfe7b10+10).writeByteArray([0xc3]);
imageBase.add(0xfe96f0).writeByteArray([0x48,0xb8]);
imageBase.add(0xfe96f0+2).writePointer(counter);
imageBase.add(0xfe96f0+10).writeByteArray([0x48,0xff,0x00,0xc3]);
imageBase.add(0x415ac90+0x520).writeU32(2);
imageBase.add(0x9398730).writeU32(2);
imageBase.add(0x9341660+0x5e0).writeU32(20);
const fakeProcess={id:Process.id,arch:Process.arch,mainModule:{name:'tlou-ii.exe',base:imageBase}};
const bridgeEvents=[];
const termination=Module.getGlobalExportByName('TerminateProcess');
let attemptedTermination=false;
function fixtureNativeFunction(address,...args){
    if(address.equals(termination))return ()=>{attemptedTermination=true;throw new Error('Owned termination intercepted');};
    return new NativeFunction(address,...args);
}
const configuration={pid:Process.id,base:imageBase.toString(),mode:'recover-preparation',recoverySource:SOURCE_CONFIG,
    fingerprints:rvas.map(rva=>({rva,bytes:Array.from(new Uint8Array(imageBase.add(rva).readByteArray(32)),
        value=>value.toString(16).padStart(2,'0')).join('')}))};
const module=new CModule(`extern void update(void *owner);
unsigned int run(void *owner){for(unsigned int index=0;index<100;index++)update(owner);return 0;}`,{update:dispatcher});
'''

BRIDGE_RUN = r'''
const bridge=createNativeRecoveryBridge(configuration);
rpc.exports={run(packet){
    bridge.install(packet);
    const createThread=new NativeFunction(Module.getGlobalExportByName('CreateThread'),'pointer',
        ['pointer','size_t','pointer','pointer','uint','pointer'],{abi:'win64',scheduling:'cooperative'});
    const waitThread=new NativeFunction(Module.getGlobalExportByName('WaitForSingleObject'),'uint',
        ['pointer','uint'],{abi:'win64',scheduling:'cooperative'});
    const closeThread=new NativeFunction(Module.getGlobalExportByName('CloseHandle'),'bool',['pointer']);
    const thread=createThread(ptr(0),0,module.run,owner,0,ptr(0));
    if(thread.isNull()||waitThread(thread,10000)!==0)throw new Error('Owned bridge update did not finish');
    closeThread(thread);
    return {phase:bridge.snapshot().phase,schedules:counter.readU64().toString(),attemptedTermination,bridgeEvents,gameOpened:false};
}};
'''


def check(image_path, hook, source=None, failure_path=None):
    image = image_path.read_bytes()
    base, headers, sections = read_layout(image)
    offset, section = va_to_offset(base + 0x133ebb0, base, headers, sections)
    code = image[offset:offset + 0xc6]
    sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools/native-probe-deps'))
    import frida
    child = subprocess.Popen([sys.executable, '-c', 'input()'], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    watchdog = threading.Timer(20, lambda: child.kill() if child.poll() is None else None)
    watchdog.start()
    session = None
    events = []
    try:
        session = frida.attach(child.pid)
        session.on('detached', lambda reason, crash: events.append({'reason':reason,'summary':getattr(crash,'summary',None),
                                                                   'report':getattr(crash,'report',None)}))
        if source is None:
            javascript = JAVASCRIPT.replace('CODE', json.dumps(list(code))).replace('HOOK', str(hook).lower())
        else:
            setup = BRIDGE_SETUP.replace('CODE', json.dumps(list(code))).replace('SOURCE_CONFIG', json.dumps(source.configuration))
            if failure_path is not None:
                setup = setup.replace("if(address.equals(termination))return ()=>{attemptedTermination=true;throw new Error('Owned termination intercepted');};",
                    "if(address.equals(imageBase.add(0xfe96f0)))return ()=>{throw new Error('owned-durable-failure');};")
                setup += '\nconfiguration.birth="42"; configuration.failurePath=' + json.dumps(str(failure_path)) + ';\n'
            bridge_source = '\n'.join(Path(__file__).with_name(name).read_text(encoding='utf-8') for name in
                                      ('NativeRecoveryContract.js', 'NativeRecoveryBridge.js'))
            javascript = setup + '\n(function(Process,NativeFunction,send){\n' + bridge_source + \
                '\n})(fakeProcess,fixtureNativeFunction,payload=>bridgeEvents.push(payload));\n' + BRIDGE_RUN
        script = session.create_script(javascript)
        script.on('message', lambda message, data: events.append(message))
        script.load()
        result = script.exports_sync.run() if source is None else script.exports_sync.run(source.packet)
        script.unload()
        session.detach()
        child.communicate(b'\n', timeout=5)
        return {**result,'exitCode':child.returncode,'events':events}
    except Exception as error:
        try:
            child.wait(timeout=2)
        except subprocess.TimeoutExpired:
            pass
        return {'hook':hook,'error':str(error),'exitCode':child.poll(),'events':events,'gameOpened':False}
    finally:
        watchdog.cancel()
        if child.poll() is None:
            child.kill()
            child.communicate(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--bridge', action='store_true')
    args = parser.parse_args()
    if args.bridge:
        root = Path(__file__).resolve().parents[2]
        storage = root / 'artifacts/native-runtime-20260912/collection-insurance-storage'
        snapshot = '20260912-072519061-manual-62fed724'
        manifest = json.loads((storage / 'snapshots' / snapshot / 'manifest.json').read_text(encoding='utf-8'))
        results = [check(args.image, True, prepare_source(storage, snapshot, manifest['SourceProfilePath']))]
    else:
        results = [check(args.image, hook) for hook in (False, True)]
    args.output.write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(results))
    return 0 if all(result['exitCode'] == 0 and (result.get('schedules') == '1' and not result.get('attemptedTermination') if args.bridge else
                                               result.get('calls') == '100000') for result in results) else 1


if __name__ == '__main__':
    raise SystemExit(main())
