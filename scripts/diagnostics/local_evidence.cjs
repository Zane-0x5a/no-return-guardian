// Local evidence for tests that replay real game sessions (see local_evidence.py). The player's
// transcripts and saves under artifacts/ are not in the repository; these tests skip without them.
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '../../artifacts');

function evidence(relative) {
    return path.join(root, relative);
}

/** node:test options that skip the test when any of the evidence files is missing. */
function requires(...relative) {
    const missing = relative.filter(item => !fs.existsSync(evidence(item)));
    return { skip: missing.length > 0 ? 'local evidence missing: ' + missing.join(', ') : false };
}

module.exports = { evidence, requires };
