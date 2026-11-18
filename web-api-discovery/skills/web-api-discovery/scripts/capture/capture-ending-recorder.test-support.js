#!/usr/bin/env node
'use strict';

/**
 * capture-ending-recorder.test-support.js -- a stand-in for the recording
 * process, for exercising `stop` and `Stop-HarRecording.ps1` as real commands
 * against a real, separate process (#27).
 *
 * It does what the recorder does at the END of a capture and nothing else:
 * takes over a session directory, waits for the STOP file, acknowledges it,
 * "saves" a raw.har, and walks the post-processing phases on a timetable --
 * writing the same record into session.json through the same function the
 * recorder uses (capture-ending.js `notePhase`). No browser, no scrub, no
 * website; the phases take the time the plan says and write a growing file
 * where the real scrub does.
 *
 * WHY A PROCESS AND NOT A FAKE CLOCK. The clock is injected in
 * capture-ending.test.js, which is where each state is pinned. This is the
 * other half: that the command line, the exit code and the PowerShell front
 * door carry those states when the recorder is genuinely another process, with
 * a pid `stop` can ask about.
 *
 * Usage (a test's own, not an operator-facing command):
 *   node capture-ending-recorder.test-support.js <sessionDir> '<plan as JSON>'
 *
 * The plan:
 *   ignoreStop   true: never look for the STOP file -- the stop never registers
 *   finalizeMs   how long "saving the recording" takes (default 200)
 *   scrubMs      how long the scrub takes, writing its partial file throughout
 *   silentMs     how long the scrub writes NOTHING before it starts writing
 *   hangIn       a phase name: enter it, then stay alive and write nothing more
 *   bytes        the size session.json says the capture is (default: the raw's)
 *   lingerMs     how long to stay alive after finishing (default 0)
 *
 * It prints `ready <pid>` once session.json carries its pid, so a caller knows
 * when asking it to stop means something.
 */

const fs = require('fs');
const path = require('path');

const ending = require(path.join(__dirname, 'capture-ending.js'));

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const ENTRY = {
    startedDateTime: '2026-01-01T12:00:00Z', time: 5,
    request: {
        method: 'GET', url: 'https://example.com/api/thing/1',
        headers: [], queryString: [], cookies: [], headersSize: 10, bodySize: 0
    },
    response: {
        status: 200, statusText: 'OK', headers: [], cookies: [], redirectURL: '',
        headersSize: 10, bodySize: 2,
        content: { size: 2, mimeType: 'application/json', text: '{}' }
    },
    cache: {}, timings: { send: 1, wait: 3, receive: 1 }
};

async function main() {
    const [sessionDir, planText] = process.argv.slice(2);
    if (!sessionDir) {
        process.stderr.write('usage: capture-ending-recorder.test-support.js <sessionDir> [<plan as JSON>]\n');
        return 2;
    }
    const plan = Object.assign({ finalizeMs: 200, scrubMs: 400, silentMs: 0, lingerMs: 0 },
        planText ? JSON.parse(planText) : {});
    const sessionFile = path.join(sessionDir, 'session.json');
    const session = JSON.parse(fs.readFileSync(sessionFile, 'utf8'));
    const write = () => fs.writeFileSync(sessionFile, JSON.stringify(session, null, 2) + '\n', 'utf8');
    const enter = (phase, by) => { ending.notePhase(session, phase, { by }); write(); };
    const hang = async () => { for (;;) await sleep(1000); };

    session.pid = process.pid;
    write();
    process.stdout.write(`ready ${process.pid}\n`);

    if (plan.ignoreStop) await hang();
    while (!fs.existsSync(path.join(sessionDir, 'STOP'))) await sleep(50);

    enter(ending.PHASES.FINALIZING, 'stop-command');
    if (plan.hangIn === ending.PHASES.FINALIZING) await hang();
    await sleep(plan.finalizeMs);
    fs.writeFileSync(session.harPath, JSON.stringify({
        log: { version: '1.2', creator: { name: 'ending-recorder', version: '1' }, pages: [], entries: [ENTRY] }
    }), 'utf8');
    session.endedUtc = new Date().toISOString();
    session.endedBy = 'stop-command';
    session.summary = {
        exists: true, path: session.harPath, entries: 1, hosts: ['example.com'],
        bytes: plan.bytes || fs.statSync(session.harPath).size
    };
    write();

    enter(ending.PHASES.SCRUB);
    if (plan.hangIn === ending.PHASES.SCRUB) await hang();
    await sleep(plan.silentMs);
    const partial = path.join(sessionDir, `.scrubbed.har.partial-${process.pid}`);
    const until = Date.now() + plan.scrubMs;
    do {
        fs.appendFileSync(partial, 'x'.repeat(1024));
        await sleep(Math.min(250, Math.max(0, until - Date.now())));
    } while (Date.now() < until);
    fs.renameSync(partial, path.join(sessionDir, 'scrubbed.har'));

    for (const phase of [ending.PHASES.VERIFY, ending.PHASES.PUBLISH, ending.PHASES.DIGEST, ending.PHASES.CATALOGUE]) {
        enter(phase);
        if (plan.hangIn === phase) await hang();
        await sleep(50);
    }

    session.postProcess = {
        startedUtc: session.endedUtc, completedUtc: new Date().toISOString(), errors: [], warnings: []
    };
    enter(ending.PHASES.COMPLETE);
    await sleep(plan.lingerMs);
    return 0;
}

main().then((code) => process.exit(code)).catch((e) => {
    process.stderr.write(`capture-ending-recorder: ${e.stack || e.message}\n`);
    process.exit(1);
});
