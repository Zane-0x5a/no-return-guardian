import json
import os
from pathlib import Path
import subprocess
import unittest

from local_evidence import requires

ROOT = Path(__file__).resolve().parents[2]
# scripts\package.ps1 points this at its fresh build; by default the local dist build.
RUNTIME = Path(os.environ.get('NRG_RUNTIME') or ROOT / 'dist/NoReturnGuardian/native-recovery')
PACKAGED = ('NativeCheckpointTrigger.py', 'NativeRecoverySource.py', 'NativeRecoveryLock.py', 'NativeResultsUi.py',
            'NativeResultsExit.py', 'NativeRecoverySession.py', 'NativeSerializedArena.py', 'NativeSaveBuffer.py',
            'NativeCheckpointProbe.py', 'NativeRecoveryBridge.js', 'NativeRecoveryContract.js',
            'NativePreparationRecovery.js', 'NativeResultsContinue.js', 'NativeEncounterRestart.py',
            'NativeEncounterControl.js', 'NativeCodeSignature.py', 'NativeDepartureRecord.py',
            'NativeDepartureStage.py', 'NativeDepartureRecorder.py')
HELPERS = ('VerifySnapshot.exe', 'CaptureLiveSnapshot.exe', 'CaptureDepartureSnapshot.exe', 'UndoSnapshot.exe')


@unittest.skipUnless((RUNTIME / 'runtime.json').is_file(), 'build with scripts\\build.ps1 -IncludeNativeRecovery first')
class NativeRecoveryPackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.configuration = json.loads((RUNTIME / 'runtime.json').read_text(encoding='utf-8'))
        cls.python = RUNTIME / cls.configuration['PythonPath']

    def test_package_carries_its_own_isolated_python_and_every_runtime_script(self):
        self.assertFalse(Path(self.configuration['PythonPath']).is_absolute())
        self.assertTrue(self.python.is_file())
        for relative in PACKAGED:
            self.assertEqual((RUNTIME / 'scripts/diagnostics' / relative).read_bytes(),
                             (ROOT / 'scripts/diagnostics' / relative).read_bytes(), relative)
        # Exactly these scripts: one left behind by an older build must not ride along.
        shipped = {path.name for path in (RUNTIME / 'scripts/diagnostics').iterdir() if path.is_file()}
        self.assertEqual(shipped, set(PACKAGED))
        for helper in HELPERS:
            self.assertTrue((RUNTIME / 'tools' / helper).is_file(), helper)
        self.assertTrue((RUNTIME / 'tools/native-probe-deps/frida/_frida.pyd').is_file())
        self.assertFalse((RUNTIME / 'artifacts').exists())
        # Isolated: the bundled interpreter ignores PYTHONPATH and any Python installed on the machine.
        result = subprocess.run([str(self.python), '-X', 'utf8', '-B', '-c',
                                 'import json,sys; print(json.dumps([sys.flags.isolated, sys.prefix]))'],
                                cwd=RUNTIME, env={**os.environ, 'PYTHONPATH': str(ROOT / 'scripts/diagnostics')},
                                capture_output=True, timeout=30, check=False)
        self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8', errors='replace'))
        isolated, prefix = json.loads(result.stdout)
        self.assertEqual(isolated, 1)
        self.assertEqual(Path(prefix).resolve(), self.python.parent.resolve())

    @requires('native-runtime-20260912/collection-insurance-storage')
    def test_packaged_engine_imports_frida_and_verifies_the_formal_target_without_game_access(self):
        storage = ROOT / 'artifacts/native-runtime-20260912/collection-insurance-storage'
        snapshot = '20260912-072519061-manual-62fed724'
        manifest = json.loads((storage / 'snapshots' / snapshot / 'manifest.json').read_text(encoding='utf-8'))
        source = ('import json,sys,frida; from NativeRecoverySource import prepare_source; '
                  'target=prepare_source(sys.argv[1],sys.argv[2],sys.argv[3]); '
                  'print(json.dumps(dict(frida=frida.__version__,configuration=target.configuration)))')
        result = subprocess.run([str(self.python), '-X', 'utf8', '-B', '-c', source, str(storage), snapshot,
                                 manifest['SourceProfilePath']], cwd=RUNTIME,
                                capture_output=True, timeout=30, check=False)
        self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8', errors='replace'))
        receipt = json.loads(result.stdout)
        self.assertEqual(receipt['frida'], self.configuration['FridaVersion'])
        self.assertEqual(receipt['configuration']['packetSha256'],
                         'e685fa4b8d3a9d31bee8b7563e0a1e8d9fe734bd7f03229b3c6521155bd3274b')
        self.assertFalse(receipt['configuration']['inGameAcceptance'])


if __name__ == '__main__':
    unittest.main()
