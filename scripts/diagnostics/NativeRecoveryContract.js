'use strict';

function createRecoveryContract() {
    let phase = 'armed';
    let nativeSelector = null;
    let callbackThread = null;
    let callbackReturned = false;
    let initializationSaveWorker = null;
    const initializationSaveWorkers = new Set();
    let failure = null;
    function requirePhase(expected) {
        if (phase !== expected) throw new Error(`Expected recovery phase ${expected}, got ${phase}`);
    }
    function stateMatches(state, initialized, selector, rogue, workersIdle) {
        if (!state || state.runInitialized !== initialized || state.runSelector !== selector ||
            state.rogue !== rogue || !Array.isArray(state.workerStates) || state.workerStates.length !== 3 ||
            state.workerStates.some(value => !Number.isInteger(value) || value < 0 || value > 8) ||
            (workersIdle && state.workerStates.some(value => value !== 0))) {
            throw new Error('Recovery lifecycle state does not match its native boundary');
        }
    }
    function event(name, fields = {}) {
        if (failure !== null) throw new Error('Recovery is already failed');
        try {
            switch (name) {
            case 'schedule':
                requirePhase('armed');
                stateMatches(fields.state, 0, 2, 0, true);
                phase = 'scheduled';
                break;
            case 'request':
                requirePhase('scheduled');
                if (fields.mode !== 2 || fields.slot !== 20) throw new Error('Recovery requires native run load mode 2, slot 20');
                stateMatches(fields.state, 0, 2, 0, true);
                phase = 'requested';
                break;
            case 'source':
                requirePhase('requested');
                stateMatches(fields.state, 0, 2, 0, false);
                if (!fields.sourceVerified || !fields.metadataVerified || !fields.requestSucceeded) {
                    throw new Error('Recovery source or native request was not verified');
                }
                phase = 'source';
                break;
            case 'loaded':
                requirePhase('source');
                if (!fields.bytesVerified) throw new Error('Native load bytes did not match the independent target');
                phase = 'loaded';
                break;
            case 'apply-enter':
                requirePhase('loaded');
                stateMatches(fields.state, 0, 2, 0, false);
                phase = 'applying';
                break;
            case 'apply-leave':
                requirePhase('applying');
                if (fields.success !== true) throw new Error('Native apply rejected the target');
                if (fields.state?.runSelector !== 0 && fields.state?.runSelector !== 1) {
                    throw new Error('Native apply did not select a supported checkpoint branch');
                }
                stateMatches(fields.state, 0, fields.state.runSelector, 0, false);
                nativeSelector = fields.state.runSelector;
                phase = 'applied';
                break;
            case 'promoted':
                requirePhase('applied');
                if (!fields.bytesVerified) throw new Error('Native canonical cache does not match the target');
                phase = 'promoted';
                break;
            case 'callback':
                requirePhase('promoted');
                stateMatches(fields.state, 0, nativeSelector, 0, true);
                if (!Number.isInteger(fields.threadId)) throw new Error('Missing callback thread');
                callbackThread = fields.threadId;
                phase = 'callback';
                break;
            case 'teardown':
                requirePhase('callback');
                stateMatches(fields.state, 0, nativeSelector, 0, true);
                if (fields.threadId !== callbackThread) throw new Error('Teardown escaped the native callback');
                phase = 'torn-down';
                break;
            case 'initialize-enter':
                requirePhase('torn-down');
                if (fields.threadId !== callbackThread || fields.checkpoint !== true) {
                    throw new Error('Native run initialization is not the callback checkpoint path');
                }
                stateMatches(fields.state, 0, nativeSelector, 0, true);
                phase = 'initializing';
                break;
            case 'initialization-save':
                requirePhase('initializing');
                if (initializationSaveWorker !== null || fields.threadId !== callbackThread || fields.mode !== 1 ||
                    (fields.slot !== 20 && fields.slot !== 21)) {
                    throw new Error('Unexpected request during native checkpoint initialization');
                }
                initializationState(fields.state);
                initializationSaveWorker = fields.slot === 20 ? 2 : 1;
                break;
            case 'initialization-save-return':
                requirePhase('initializing');
                if (initializationSaveWorker === null || fields.threadId !== callbackThread || fields.success !== true) {
                    throw new Error('Native initialization save request was not accepted');
                }
                initializationSaveWorkers.add(initializationSaveWorker);
                initializationState(fields.state);
                initializationSaveWorker = null;
                break;
            case 'initialize':
                requirePhase('initializing');
                if (fields.threadId !== callbackThread || initializationSaveWorker !== null) {
                    throw new Error('Native checkpoint initialization did not finish its requests');
                }
                initializationState(fields.state);
                phase = 'initialized';
                break;
            case 'queued':
                requirePhase('initialized');
                if (fields.threadId !== callbackThread || fields.kind !== 1 || fields.parameter !== 10) {
                    throw new Error('Native callback did not queue preparation rebuild');
                }
                phase = 'queued';
                break;
            case 'callback-return':
                requirePhase('queued');
                if (fields.threadId !== callbackThread || callbackReturned) throw new Error('Invalid callback return');
                callbackReturned = true;
                break;
            case 'play':
                requirePhase('queued');
                if (!callbackReturned || fields.parameter !== 10) throw new Error('Preparation task started before callback completion');
                phase = 'playing';
                break;
            case 'rebuilt':
                requirePhase('playing');
                stateMatches(fields.state, 1, nativeSelector, 1, true);
                if (fields.taskWord !== 2 || fields.spawnObserved !== true) throw new Error('Preparation scene and new spawn are not observed');
                phase = 'awaiting-player-acceptance';
                break;
            default:
                throw new Error('Unknown recovery event');
            }
            return snapshot();
        } catch (error) {
            failure = error.message;
            phase = 'failed';
            throw error;
        }
    }
    function initializationState(state) {
        stateMatches(state, 1, nativeSelector, 1, false);
        if (state.workerStates.some((value, index) => value !== 0 && !initializationSaveWorkers.has(index))) {
            throw new Error('Unexpected worker during native initialization save');
        }
    }
    function snapshot() { return { phase, failure, nativeSelector, inGameAcceptance: false }; }
    return { event, snapshot };
}

globalThis.createRecoveryContract = createRecoveryContract;
if (typeof module !== 'undefined') module.exports = { createRecoveryContract };
