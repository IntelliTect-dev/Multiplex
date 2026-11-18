'use strict';

/**
 * capture-run-slow-lookup.test-support.js -- make the RECORDER's run lookup
 * slow, for tests only (#26).
 *
 * Preloaded (`node -r`) into the recorder process by capture-run-identity's E8.
 * The lookup still runs for real and still answers what it would have; the
 * answer is simply held back for longer than a recording waits for it, which
 * is what a busy machine does. That is the one path a healthy test machine
 * never takes by itself, and it is the path that left real recordings without
 * a run before it existed.
 *
 * Loaded before `capture-har.js`, so the recorder's own reference to
 * `capture-run.js` is to the module this has already wrapped.
 */

const path = require('path');

const captureRun = require(path.join(__dirname, 'capture-run.js'));

/** Longer than the recorder's grace period, shorter than any test's patience. */
const HELD_BACK_MS = 9000;

const realBegin = captureRun.beginRunIdentity;
captureRun.beginRunIdentity = function beginRunIdentity(options) {
    return realBegin(options).then((run) => new Promise((resolve) => {
        setTimeout(() => resolve(run), HELD_BACK_MS);
    }));
};

module.exports = { HELD_BACK_MS };
