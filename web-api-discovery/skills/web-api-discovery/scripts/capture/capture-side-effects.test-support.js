'use strict';

/**
 * capture-side-effects.test-support.js -- WHAT DID THE RECORDER DO? For tests
 * only (#25).
 *
 * PRELOADED (`node -r`) into the recorder process, before capture-har.js takes
 * its references, it records every way that process can reach outside itself:
 *
 *   spawn    every child process: the command and its arguments
 *   fs       every call that creates, changes or removes something on disk
 *   connect  every outbound socket, which is what every HTTP client -- fetch
 *            included -- ends in
 *
 * and writes the list, as JSON, to the file HAR_TEST_SIDE_EFFECTS names when
 * the process exits. Every call still goes through to the real function: this
 * WATCHES a run, it does not change one.
 *
 * WHY, when a before-and-after listing of the folder already shows what was
 * written. The listing sees only the folders a test thought to redirect and
 * look in, and it cannot see a process that was started and left nothing
 * behind. "The check-only start launches nothing" is a claim about a browser
 * that was never spawned, and a spawn is only visible from inside.
 *
 * HAR_TEST_STDIN_IS_TTY=1 also presents stdin as a terminal. A child of a test
 * never has one, so without this "it asks nothing even at a terminal" could
 * only ever be tested where the answer is already no.
 *
 * Both variables are read here and nowhere in the product.
 */

const fs = require('fs');
const net = require('net');
const childProcess = require('child_process');

const events = [];
let recording = true;

function record(kind, name, detail) {
    if (recording) events.push(Object.assign({ kind, name }, detail));
}

/** Replace `owner[name]` with a version that records, then calls through. */
function watch(owner, name, kind, describe) {
    const real = owner[name];
    if (typeof real !== 'function') return;
    owner[name] = function watched(...args) {
        record(kind, name, describe(args));
        return real.apply(this, args);
    };
}

const spawned = ([command, args]) => ({
    command: String(command),
    args: Array.isArray(args) ? args.map(String) : []
});
for (const name of ['spawn', 'spawnSync', 'exec', 'execSync', 'execFile', 'execFileSync', 'fork']) {
    watch(childProcess, name, 'spawn', spawned);
}

const onPath = ([target]) => ({ path: String(target) });
const MUTATORS = [
    'writeFile', 'appendFile', 'mkdir', 'mkdtemp', 'rename', 'rm', 'rmdir', 'unlink',
    'copyFile', 'cp', 'symlink', 'link', 'truncate', 'chmod', 'utimes'
];
for (const name of MUTATORS) {
    watch(fs, name, 'fs', onPath);
    watch(fs, name + 'Sync', 'fs', onPath);
    watch(fs.promises, name, 'fs', onPath);
}
watch(fs, 'createWriteStream', 'fs', onPath);

// Opening is only a change when it is opened to be changed. Reading -- which is
// most of what a path-resolving run does -- is 'r' or no flag at all.
const opensForWriting = (flags) => flags !== undefined && flags !== 'r' && flags !== fs.constants.O_RDONLY;
for (const [owner, name] of [[fs, 'open'], [fs, 'openSync'], [fs.promises, 'open']]) {
    const real = owner[name];
    owner[name] = function watchedOpen(target, flags, ...rest) {
        if (opensForWriting(typeof flags === 'function' ? undefined : flags)) {
            record('fs', name, { path: String(target), flags: String(flags) });
        }
        return real.call(this, target, flags, ...rest);
    };
}

watch(net.Socket.prototype, 'connect', 'connect', (args) => ({ target: JSON.stringify(args[0]) }));

if (process.env.HAR_TEST_STDIN_IS_TTY === '1') {
    Object.defineProperty(process.stdin, 'isTTY', { value: true, configurable: true });
}

const report = process.env.HAR_TEST_SIDE_EFFECTS;
const realWriteFileSync = fs.writeFileSync.bind(fs);
process.on('exit', () => {
    // The report is this file's own write, made after the run it describes.
    recording = false;
    if (report) realWriteFileSync(report, JSON.stringify(events), 'utf8');
});
