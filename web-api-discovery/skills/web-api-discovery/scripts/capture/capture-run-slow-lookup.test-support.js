'use strict';

/**
 * capture-run-slow-lookup.test-support.js -- make the RECORDER's run lookup
 * slow, for tests only (#26).
 *
 * Preloaded (`node -r`) into the recorder process by capture-run-identity's E8.
 * The lookup still runs for real and still answers what it would have; the
 * answer is simply WITHHELD until the test says so, by creating the file named
 * in HAR_TEST_RUN_LOOKUP_RELEASE. That is what a busy machine does to a
 * recorder, made exact: the recording has to start without its run, and the
 * test decides when the run arrives rather than hoping a delay was long enough.
 *
 * It is the one path a healthy test machine never takes by itself, and it is
 * the path that left real recordings without a run before it existed.
 *
 * Loaded before `capture-har.js`, so the recorder's own reference to
 * `capture-run.js` is to the module this has already wrapped. Without the
 * variable the answer never arrives, which is also a case worth being able to
 * make: a recording that ends before its lookup does.
 */

const fs = require('fs');
const path = require('path');

const captureRun = require(path.join(__dirname, 'capture-run.js'));

const RELEASE_FILE = process.env.HAR_TEST_RUN_LOOKUP_RELEASE;
const POLL_MS = 100;

const realBegin = captureRun.beginRunIdentity;
captureRun.beginRunIdentity = function beginRunIdentity(options) {
    return realBegin(options).then((run) => new Promise((resolve) => {
        const poll = setInterval(() => {
            if (!RELEASE_FILE || !fs.existsSync(RELEASE_FILE)) return;
            clearInterval(poll);
            resolve(run);
        }, POLL_MS);
    }));
};
