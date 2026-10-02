'use strict';

const recoveryConfiguration = globalThis.nativeCheckpointConfiguration;
const recoveryBridge = globalThis.createNativeRecoveryBridge(recoveryConfiguration);
let recoveryPacketReceived = false;

rpc.exports = { stop() { return recoveryBridge.stop(); } };
recv('recovery-source', (message, data) => {
    if (recoveryPacketReceived || data === null) {
        send({ kind: 'script-error', reason: 'Missing or duplicate native recovery packet', inGameAcceptance: false });
        return;
    }
    recoveryPacketReceived = true;
    recoveryBridge.install(data);
});
setTimeout(() => recoveryBridge.stop('deadline'), recoveryConfiguration.duration * 1000);
