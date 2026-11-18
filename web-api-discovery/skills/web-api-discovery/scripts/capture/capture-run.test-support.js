'use strict';

/**
 * capture-run.test-support.js -- real processes for capture-run.test.js. For
 * tests only.
 *
 * The rule in capture-run.js is about process ancestry, so the test that
 * matters most runs it against real ancestry: two long-lived DRIVER processes
 * stand in for two sessions, and each starts short-lived LEAF processes that
 * stand in for recorder invocations.
 *
 *   node capture-run.test-support.js leaf
 *       Prints one JSON line: a new capture id and the run this process
 *       resolves for itself.
 *
 *   node capture-run.test-support.js driver
 *       Reads one command per line on stdin and answers each with the leaf's
 *       JSON line on stdout:
 *         direct   start a leaf as a direct child
 *         shell    start a leaf through a command shell, which stays in the
 *                  chain between this process and the leaf
 *         quit     exit
 */

const path = require('path');
const readline = require('readline');
const { spawn } = require('child_process');

const captureRun = require(path.join(__dirname, 'capture-run.js'));

const SELF = __filename;

function leaf() {
    captureRun.beginRunIdentity().then((run) => {
        process.stdout.write(JSON.stringify({ captureId: captureRun.newCaptureId(), run }) + '\n');
    });
}

/** The leaf's argv, direct or through a shell that stays its parent. */
function leafCommand(throughShell) {
    if (!throughShell) return { command: process.execPath, args: [SELF, 'leaf'] };
    if (process.platform === 'win32') {
        return {
            command: process.env.ComSpec || 'cmd.exe',
            args: ['/d', '/s', '/c', `""${process.execPath}" "${SELF}" leaf"`],
            verbatim: true
        };
    }
    // Two commands, not one: a shell given a single simple command replaces
    // itself with it, and the layer this exists to put in the chain is gone.
    return {
        command: '/bin/sh',
        args: ['-c', `"${process.execPath}" "${SELF}" leaf; status=$?; exit $status`]
    };
}

function runLeaf(throughShell) {
    return new Promise((resolve) => {
        const { command, args, verbatim } = leafCommand(throughShell);
        const child = spawn(command, args, {
            stdio: ['ignore', 'pipe', 'inherit'],
            windowsHide: true,
            windowsVerbatimArguments: !!verbatim
        });
        let out = '';
        child.stdout.on('data', (d) => { out += d; });
        child.on('error', (e) => resolve(JSON.stringify({ error: e.message })));
        child.on('close', () => resolve(out.trim() || JSON.stringify({ error: 'the leaf printed nothing' })));
    });
}

function driver() {
    const lines = readline.createInterface({ input: process.stdin });
    let queue = Promise.resolve();
    lines.on('line', (line) => {
        const command = line.trim();
        if (command === 'quit') { queue.then(() => process.exit(0)); return; }
        queue = queue.then(async () => {
            process.stdout.write(await runLeaf(command === 'shell') + '\n');
        });
    });
    lines.on('close', () => { queue.then(() => process.exit(0)); });
}

if (require.main === module) {
    if (process.argv[2] === 'leaf') leaf();
    else if (process.argv[2] === 'driver') driver();
    else { process.stderr.write('usage: capture-run.test-support.js leaf|driver\n'); process.exit(2); }
}

module.exports = { SELF };
