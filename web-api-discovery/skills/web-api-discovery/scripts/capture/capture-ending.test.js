#!/usr/bin/env node
// Behavior tests for the END of a capture (#27): `stop` waits for
// post-processing for as long as it is getting somewhere, and says which state
// it is in when it is not. Zero-dep; runs with `node capture-ending.test.js`.
//
// Nothing here waits on a real clock, a browser or a website. The clock and the
// sleep are injected through a function parameter; the "recording process" is a
// script of writes into a real session directory, played as the injected sleep
// advances the clock. The session directories live under the OS temp directory,
// never in the repository.
//
// WHY THE FIXTURES CARRY TIMESTAMPS. The state is derived from what is on disk
// -- the `ending` record in session.json, the artifacts' modification times,
// the STOP file's time -- so a fixture dated in the past puts the real command
// line in any state at once, with no test-only switch.
//
// FALSIFIERS (fail before this change, pass after):
//   E1  the ending registered and something moved within the allowance:
//       `progressing`
//   E2  the ending registered and nothing has moved for longer than the
//       allowance: `stalled`, with both times stated
//   E3  a stop was asked for and the running recorder has not acknowledged it:
//       `stop-pending` inside a minute, `not-registered` after it
//   E4  the allowance grows with the capture -- the same silence that is a
//       stall on 1 MB is ordinary on 22 MB -- and is 30 minutes for the AI pass
//   E5  only what post-processing itself writes is a sign of progress: the
//       record log and the agent's log growing are not
//   E6  finished, cancelled and recorder-gone each say so, whatever the clock
//   E7  a modification time in the future is not a sign of progress
//   E8  a recorder that finished and exited between two looks is finished
//   N1  the recorder's own record: registered once, moved at each phase
//   W1  the wait goes on through four minutes of a scrub that keeps writing
//   W2  a recorder that exits is noticed at once, not waited on
//   W3  a file that grew counts even where its modification time did not move
//   S1  `stop` on a 22 MB capture that takes five minutes: success, exit 0
//   S2  `stop` on a stall: its own state, exit 1 -- never 6, never 0 -- and
//       nothing taken over
//   S3  `stop` that never registered: its own state, exit 1, and `raw.har` is
//       NOT assembled underneath a recorder that is still recording
//   S4  the recorder exits mid-scrub: `stop` runs the phases itself, at once
//   S5  `stop` on a cancelled capture reports it and exits 0
//   S6  `status` gives the same state `stop` does, through the real command
//   R1  post-processing reports each phase as it STARTS, in order
//   R2  the AI catalogue pass is its own phase, entered before the child runs
//
// GUARD (passes before and after; it is here so the reporting is never
// hard-wired into the phases):
//   R3  post-processing called without a reporter writes no record anywhere

'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const ending = require(path.join(__dirname, 'capture-ending.js'));
const capture = require(path.join(__dirname, 'capture-har.js'));
const { initProtectedRepo } = require(path.join(__dirname, '..', 'har', 'har-test-repo.test-support.js'));

const CAPTURE_JS = path.join(__dirname, 'capture-har.js');
const tmpRoot = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'capture-ending-')));

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

const MB = 1024 * 1024;
const SECOND = 1000;
const MINUTE = 60 * SECOND;

// Fake clocks start at the real time, so nothing written to disk during a test
// carries a modification time in the fake clock's future.
const T0 = Date.now();
const iso = (ms) => new Date(ms).toISOString();

const okEntry = {
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
const RAW_TEXT = JSON.stringify({
    log: { version: '1.2', creator: { name: 'ending-test', version: '1' }, pages: [], entries: [okEntry] }
});

/**
 * A session directory as the recorder leaves it WHILE RECORDING: a session.json
 * and a record log with traffic in it, no raw.har yet.
 *
 * Inside a protected repository, because one case lets the real scrub run.
 */
function fixture(name, extra = {}) {
    const root = initProtectedRepo(path.join(tmpRoot, name));
    fs.writeFileSync(path.join(root, '.har-profile.json'),
        JSON.stringify({ salt: 'test-salt', literals: {} }), 'utf8');
    const store = path.join(root, '.har-captures');
    const sessionDir = path.join(store, 'example.com', '2026-01-01-120000');
    fs.mkdirSync(sessionDir, { recursive: true });
    const f = {
        root, store, sessionDir,
        sessionFile: path.join(sessionDir, 'session.json'),
        harPath: path.join(sessionDir, 'raw.har'),
        logPath: path.join(sessionDir, 'raw.ndjson'),
        stopFile: path.join(sessionDir, 'STOP'),
        outputPath: path.join(sessionDir)
    };
    fs.writeFileSync(f.logPath, JSON.stringify(okEntry) + '\n', 'utf8');
    f.session = Object.assign({
        uri: 'https://example.com', describe: 'ending fixture',
        sessionDir, harPath: f.harPath, recordLog: f.logPath, outputPath: f.outputPath,
        startedUtc: iso(T0 - 10 * MINUTE),
        // This process: alive for as long as the test runs, which is what a
        // command spawned from here sees as a running recorder.
        pid: process.pid
    }, extra);
    f.write = () => fs.writeFileSync(f.sessionFile, JSON.stringify(f.session, null, 2) + '\n', 'utf8');
    f.read = () => JSON.parse(fs.readFileSync(f.sessionFile, 'utf8'));
    f.write();
    return f;
}

/** Write `file` and date it `at`, so what is on disk agrees with the fake clock. */
function writeAt(file, text, at) {
    fs.writeFileSync(file, text, 'utf8');
    fs.utimesSync(file, at / 1000, at / 1000);
}

/**
 * A clock, a sleep that advances it, and a "recording process": steps that run
 * when the clock reaches them, the way a recorder writes while `stop` polls.
 */
function fakeRun(steps = []) {
    // From the REAL time at this moment, not the suite's: `stop` dates its
    // request by the STOP file it writes, and that file is dated by the
    // filesystem. A clock started in the past would put it in the future.
    const start = Date.now();
    let t = start;
    const pending = steps.slice().sort((a, b) => a.at - b.at);
    const run = {
        alive: true,
        start,
        now: () => t,
        elapsed: () => t - start,
        sleep: async (ms) => {
            t += ms;
            while (pending.length && start + pending[0].at <= t) pending.shift().run(t);
        },
        /** Add a step after the run exists, for one that needs `run.start`. */
        schedule: (step) => { pending.push(step); pending.sort((a, b) => a.at - b.at); },
        isDriverAlive: () => run.alive
    };
    return run;
}

/** The phases the recorder enters, as it would record them. */
function enter(f, phase, at, by) {
    ending.notePhase(f.session, phase, { now: at, by });
    f.write();
}

/** `stop`, in this process, with its two streams recorded instead of printed. */
async function runStop(f, deps, extraArgs = {}) {
    const out = [];
    const err = [];
    const stdout = process.stdout.write;
    const stderr = process.stderr.write;
    const cwd = process.cwd();
    process.stdout.write = (chunk) => { out.push(String(chunk)); return true; };
    process.stderr.write = (chunk) => { err.push(String(chunk)); return true; };
    process.chdir(f.root);
    let code;
    try {
        code = await capture.stop(Object.assign({ session: f.sessionDir, dir: f.store, 'min-bytes': 0 }, extraArgs), deps);
    } finally {
        process.chdir(cwd);
        process.stdout.write = stdout;
        process.stderr.write = stderr;
    }
    return { code, stdout: out.join(''), stderr: err.join(''), json: jsonLine(out.join('')) };
}

/** The one line of machine output, or null when there was none. */
function jsonLine(text) {
    const lines = text.split('\n').filter((l) => l.trim().startsWith('{'));
    if (!lines.length) return null;
    try { return JSON.parse(lines[lines.length - 1]); } catch (e) { return null; }
}

/** The real command, in its own process, against a fixture -- never the store this clone records into. */
function runCommand(f, command) {
    const r = spawnSync(process.execPath, [CAPTURE_JS, command, '--session', f.sessionDir, '--dir', f.store], {
        cwd: f.root, encoding: 'utf8', windowsHide: true, timeout: 5 * MINUTE
    });
    return { status: r.status, stdout: r.stdout || '', stderr: r.stderr || '' };
}

const evidence = (over = {}) => Object.assign({
    session: {}, recorderAlive: true, recorderPid: 4242, stopRequestedMs: null,
    artifacts: [], captureBytes: 0, observedChangeMs: null
}, over);

const registered = (phase, at, over = {}) => Object.assign({
    endedUtc: iso(at),
    ending: { by: 'stop-command', registeredUtc: iso(at), phase, phaseStartedUtc: iso(at) }
}, over);

// ---------------------------------------------------------------------------
// The evaluation
// ---------------------------------------------------------------------------

test('E1: the ending registered and something moved within the allowance -- progressing', () => {
    const v = ending.evaluate(evidence({
        session: registered('scrub', T0 - 5 * MINUTE),
        artifacts: [{ name: 'session:.scrubbed.har.partial-77', size: 4096, mtimeMs: T0 - 4 * SECOND }]
    }), T0);
    assert.strictEqual(v.state, 'progressing');
    assert.strictEqual(v.phase, 'scrub');
    assert.strictEqual(v.endedBy, 'stop-command');
    assert.strictEqual(v.quietSeconds, 4, 'the silence is counted from the last thing that moved');
    assert.strictEqual(v.lastProgressUtc, iso(T0 - 4 * SECOND));
});

test('E2: the ending registered and nothing has moved for longer than the allowance -- stalled', () => {
    const before = ending.evaluate(evidence({ session: registered('verify', T0 - 59 * SECOND) }), T0);
    assert.strictEqual(before.state, 'progressing', 'one second short of the allowance is not a stall');

    const v = ending.evaluate(evidence({ session: registered('verify', T0 - 61 * SECOND) }), T0);
    assert.strictEqual(v.state, 'stalled');
    assert.strictEqual(v.phase, 'verify');
    assert.strictEqual(v.quietSeconds, 61, 'how long it has been silent is stated');
    assert.strictEqual(v.quietLimitSeconds, 60, 'and so is how long it was allowed to be');
});

test('E3: a stop the running recorder has not acknowledged -- pending for a minute, then never registered', () => {
    const asked = (ago) => ending.evaluate(evidence({
        session: { pid: 4242 }, stopRequestedMs: T0 - ago
    }), T0);

    assert.strictEqual(asked(5 * SECOND).state, 'stop-pending');
    assert.strictEqual(asked(59 * SECOND).state, 'stop-pending');
    const v = asked(60 * SECOND);
    assert.strictEqual(v.state, 'not-registered');
    assert.strictEqual(v.registeredUtc, null, 'nothing acknowledged it');
    assert.strictEqual(v.stopRequestedUtc, iso(T0 - 60 * SECOND));
    assert.strictEqual(v.quietSeconds, 60);
    assert.strictEqual(v.quietLimitSeconds, 60);

    // With no stop asked for at all, a running recorder is simply recording.
    assert.strictEqual(ending.evaluate(evidence({ session: { pid: 4242 } }), T0).state, 'recording');
});

test('E4: the allowance grows with the capture, and the AI catalogue pass has its own', () => {
    const quietFor = (ms, bytes, phase = 'scrub') => ending.evaluate(evidence({
        session: registered(phase, T0 - ms), captureBytes: bytes
    }), T0);

    // Ninety seconds of nothing: a stall on a small capture, ordinary on the
    // 22 MB one whose scrub was measured silent for 53 seconds on a fast machine.
    assert.strictEqual(quietFor(90 * SECOND, 1 * MB).state, 'stalled');
    const big = quietFor(90 * SECOND, 22 * MB);
    assert.strictEqual(big.state, 'progressing');
    assert.strictEqual(big.quietLimitSeconds, 22 * 15, 'fifteen seconds for each megabyte');
    assert.strictEqual(quietFor(22 * 15 * SECOND, 22 * MB).state, 'stalled', 'and no further');

    // The AI pass prints nothing until it ends, whatever the capture's size.
    assert.strictEqual(quietFor(29 * MINUTE, 1 * MB, 'ai-catalogue').state, 'progressing');
    const ai = quietFor(30 * MINUTE, 1 * MB, 'ai-catalogue');
    assert.strictEqual(ai.state, 'stalled');
    assert.strictEqual(ai.quietLimitSeconds, 30 * 60);
});

test('E5: only what post-processing writes is a sign of progress -- not the record log, not the agent', () => {
    const f = fixture('e5', registered('scrub', T0 - 10 * MINUTE, { summary: { exists: true, bytes: 2048 } }));
    fs.writeFileSync(f.harPath, RAW_TEXT, 'utf8');
    fs.utimesSync(f.harPath, (T0 - 10 * MINUTE) / 1000, (T0 - 10 * MINUTE) / 1000);
    const look = () => ending.evaluate(
        ending.readEvidence(f.sessionDir, { isRecorderAlive: () => true }), T0);

    // An agent that outlived the recording goes on writing its log, and a page
    // still open goes on making requests. Neither is the scrub getting anywhere.
    writeAt(path.join(f.sessionDir, 'agent-log.txt'), 'still clicking\n', T0 - SECOND);
    writeAt(path.join(f.sessionDir, 'agent-stream.jsonl'), '{}\n', T0 - SECOND);
    writeAt(f.logPath, JSON.stringify(okEntry) + '\n' + JSON.stringify(okEntry) + '\n', T0 - SECOND);
    assert.strictEqual(look().state, 'stalled',
        'files the post-processing does not write must not keep a hung one looking alive');

    // The scrub's own output growing is.
    writeAt(path.join(f.sessionDir, '.scrubbed.har.partial-4242'), 'x'.repeat(512), T0 - SECOND);
    const v = look();
    assert.strictEqual(v.state, 'progressing');
    assert.strictEqual(v.quietSeconds, 1);
});

test('E6: finished, cancelled and recorder-gone each say so, whatever the clock says', () => {
    const long = T0 - 3 * 60 * MINUTE;
    const done = ending.evaluate(evidence({
        session: registered('complete', long, { postProcess: { errors: [], warnings: [] } }),
        recorderAlive: false
    }), T0);
    assert.strictEqual(done.state, 'complete');

    const cancelled = ending.evaluate(evidence({
        session: registered('finalizing', long, { cancelled: true }), recorderAlive: false
    }), T0);
    assert.strictEqual(cancelled.state, 'not-processed');

    const gone = ending.evaluate(evidence({ session: registered('scrub', T0 - SECOND), recorderAlive: false }), T0);
    assert.strictEqual(gone.state, 'recorder-gone',
        'a recorder that has exited is not progressing, however recently it wrote');

    const crashed = ending.evaluate(evidence({ session: { pid: 4242 }, recorderAlive: false }), T0);
    assert.strictEqual(crashed.state, 'recorder-gone');
});

test('E7: a modification time in the future is not a sign of progress', () => {
    // A clock that jumped, or a file on a share with its own idea of the time.
    // Counted, it would hold the silence at zero for ever and nothing could stall.
    const v = ending.evaluate(evidence({
        session: registered('scrub', T0 - 5 * MINUTE),
        artifacts: [{ name: 'session:scrubbed.har', size: 10, mtimeMs: T0 + 60 * MINUTE }]
    }), T0);
    assert.strictEqual(v.state, 'stalled');
    assert.strictEqual(v.quietSeconds, 300);
});

test('E8: a recorder that finished and exited between two looks is finished, not gone', () => {
    // The order things are read in. session.json is read, the recorder writes
    // its result and exits, and only then is it asked whether it is running:
    // taken at face value that is "exited with the work undone", and `stop`
    // would scrub a capture that had just been scrubbed.
    const f = fixture('e8', registered('catalogue', T0 - SECOND));
    const v = ending.evaluate(ending.readEvidence(f.sessionDir, {
        isRecorderAlive: () => {
            f.session.postProcess = { errors: [], warnings: [] };
            f.write();
            return false;
        }
    }), T0);
    assert.strictEqual(v.state, 'complete');
});

// ---------------------------------------------------------------------------
// The recorder's record
// ---------------------------------------------------------------------------

test('N1: the ending is registered once, and the phase moves', () => {
    const session = {};
    ending.notePhase(session, 'finalizing', { now: T0, by: 'stop-command' });
    assert.deepStrictEqual(session.ending, {
        by: 'stop-command', registeredUtc: iso(T0), phase: 'finalizing', phaseStartedUtc: iso(T0)
    });

    ending.notePhase(session, 'scrub', { now: T0 + 20 * SECOND });
    assert.deepStrictEqual(session.ending, {
        by: 'stop-command', registeredUtc: iso(T0), phase: 'scrub', phaseStartedUtc: iso(T0 + 20 * SECOND)
    }, 'when it registered and what ended it stay; the phase and its start move');
});

// ---------------------------------------------------------------------------
// The wait
// ---------------------------------------------------------------------------

test('W1: the wait goes on through four minutes of a scrub that keeps writing', async () => {
    const f = fixture('w1');
    const partial = path.join(f.sessionDir, '.scrubbed.har.partial-4242');
    const steps = [
        { at: 1 * SECOND, run: (t) => { fs.writeFileSync(f.harPath, RAW_TEXT); f.session.endedUtc = iso(t); enter(f, 'scrub', t, 'stop-command'); } }
    ];
    // Something written every 40 seconds: never silent for a minute, and never
    // finished inside one either.
    for (let s = 40; s <= 240; s += 40) {
        steps.push({ at: s * SECOND, run: (t) => writeAt(partial, 'x'.repeat(s * 10), t) });
    }
    steps.push({ at: 250 * SECOND, run: (t) => { f.session.postProcess = { errors: [], warnings: [] }; enter(f, 'complete', t); } });
    const run = fakeRun(steps);
    fs.writeFileSync(f.stopFile, '');

    const said = [];
    const result = await ending.waitForEnding(f.sessionDir, {
        now: run.now, sleep: run.sleep, isRecorderAlive: run.isDriverAlive,
        onWaiting: (verdict, why) => said.push({ verdict, why })
    });

    assert.strictEqual(result.verdict.state, 'complete');
    assert.ok(run.elapsed() >= 250 * SECOND,
        `it returned after ${run.elapsed() / 1000}s -- before the work was finished`);
    assert.ok(run.elapsed() < 252 * SECOND, 'and no later than the work finishing');
    assert.ok(result.session.postProcess, 'the finished session is handed back');
    // It says where it is while it waits: when that changes, and otherwise
    // about every thirty seconds -- not on every poll.
    assert.deepStrictEqual(said.filter((s) => s.why === 'change').map((s) => `${s.verdict.state}/${s.verdict.phase}`),
        ['stop-pending/null', 'progressing/scrub']);
    const ticks = said.filter((s) => s.why === 'interval');
    assert.ok(ticks.length >= 7 && ticks.length <= 9, `said ${ticks.length} times in 250s`);
    assert.ok(ticks.every((s) => s.verdict.state === 'progressing' && s.verdict.phase === 'scrub'));
});

test('W2: a recorder that exits is noticed at once, not waited on', async () => {
    const run = fakeRun([{ at: 10 * SECOND, run: () => { run.alive = false; } }]);
    const f = fixture('w2', registered('scrub', run.start, { summary: { exists: true, bytes: 30 * MB } }));

    const result = await ending.waitForEnding(f.sessionDir, {
        now: run.now, sleep: run.sleep, isRecorderAlive: run.isDriverAlive
    });

    assert.strictEqual(result.verdict.state, 'recorder-gone');
    assert.ok(run.elapsed() <= 11 * SECOND,
        `waited ${run.elapsed() / 1000}s for a process that had already exited`);
});

test('W3: a file that grew counts even where its modification time did not move', async () => {
    // Some filesystems date a file that is still open for writing lazily. The
    // size is then the only thing that moves, and the wait is watching for it.
    const steps = [];
    const run = fakeRun(steps);
    const f = fixture('w3', registered('scrub', run.start, { summary: { exists: true, bytes: 2048 } }));
    const partial = path.join(f.sessionDir, '.scrubbed.har.partial-4242');
    const stale = run.start - MINUTE;
    writeAt(partial, 'x', stale);
    for (let s = 30; s <= 150; s += 30) {
        run.schedule({ at: s * SECOND, run: () => writeAt(partial, 'x'.repeat(s), stale) });
    }

    const result = await ending.waitForEnding(f.sessionDir, {
        now: run.now, sleep: run.sleep, isRecorderAlive: run.isDriverAlive
    });

    // Sixty seconds after the last growth (at 150s), and not at 60s.
    assert.strictEqual(result.verdict.state, 'stalled');
    assert.ok(run.elapsed() >= 210 * SECOND && run.elapsed() < 212 * SECOND,
        `stalled was called at ${run.elapsed() / 1000}s`);
});

// ---------------------------------------------------------------------------
// `stop`, end to end with an injected clock
// ---------------------------------------------------------------------------

test('S1: a 22 MB capture whose post-processing takes five minutes is reported as success', async () => {
    // The capture this issue was filed on: 22 MB, a scrub silent for most of a
    // minute, and `stop` giving up at 60 seconds with exit 6 on a good capture.
    const f = fixture('s1');
    const partial = path.join(f.sessionDir, '.scrubbed.har.partial-4242');
    const steps = [
        { at: 1 * SECOND, run: (t) => enter(f, 'finalizing', t, 'stop-command') },
        { at: 20 * SECOND, run: (t) => {
            writeAt(f.harPath, RAW_TEXT, t);
            f.session.endedUtc = iso(t);
            f.session.endedBy = 'stop-command';
            f.session.summary = { exists: true, bytes: 22 * MB, path: f.harPath };
            f.write();
        } },
        { at: 21 * SECOND, run: (t) => enter(f, 'scrub', t) },
        // The scrub reads the capture once before it writes a byte: 100 seconds
        // of nothing, which is longer than the old 60-second limit.
        { at: 121 * SECOND, run: (t) => writeAt(partial, 'x'.repeat(100), t) },
        { at: 180 * SECOND, run: (t) => writeAt(partial, 'x'.repeat(200), t) },
        { at: 240 * SECOND, run: (t) => { fs.unlinkSync(partial); writeAt(path.join(f.sessionDir, 'scrubbed.har'), RAW_TEXT, t); } },
        { at: 241 * SECOND, run: (t) => enter(f, 'verify', t) },
        { at: 290 * SECOND, run: (t) => enter(f, 'digest', t) },
        { at: 300 * SECOND, run: (t) => { f.session.postProcess = { errors: [], warnings: [] }; enter(f, 'complete', t); } }
    ];
    const run = fakeRun(steps);

    const r = await runStop(f, run);

    assert.strictEqual(r.code, 0, `a healthy capture must exit 0, got ${r.code}:\n${r.stderr}`);
    assert.ok(run.elapsed() >= 300 * SECOND,
        `stop returned after ${run.elapsed() / 1000}s, before post-processing had finished`);
    assert.ok(r.json, `stop must print its state as one line of JSON on stdout, got: ${r.stdout}`);
    assert.strictEqual(r.json.stop, 'complete');
    assert.strictEqual(r.json.exitCode, 0);
    assert.strictEqual(r.json.ending.state, 'complete');
    assert.strictEqual(r.json.sessionDir, f.sessionDir);
});

test('S2: a stall is reported as a stall -- exit 1, never 6 and never 0 -- and nothing is taken over', async () => {
    const run = fakeRun();   // the recorder is alive, and never writes again
    const f = fixture('s2');
    writeAt(f.harPath, RAW_TEXT, run.start);
    Object.assign(f.session, { endedUtc: iso(run.start), endedBy: 'stop-command', summary: { exists: true, bytes: 2048 } });
    enter(f, 'scrub', run.start, 'stop-command');

    const r = await runStop(f, run);

    assert.strictEqual(r.code, 1,
        `a stall is not a scrub failure (6) and not a success (0), got ${r.code}:\n${r.stderr}`);
    assert.ok(r.json, `no JSON on stdout: ${r.stdout}`);
    assert.strictEqual(r.json.stop, 'stalled');
    assert.strictEqual(r.json.exitCode, 1);
    assert.strictEqual(r.json.ending.phase, 'scrub');
    assert.ok(r.json.ending.quietSeconds >= 60, `silent for ${r.json.ending.quietSeconds}s`);
    assert.strictEqual(r.json.ending.quietLimitSeconds, 60);
    assert.strictEqual(r.json.ending.recorderAlive, true);
    assert.ok(run.elapsed() >= 60 * SECOND && run.elapsed() < 62 * SECOND,
        `stalled was called after ${run.elapsed() / 1000}s`);

    // The recorder is still running and still owns the work: `stop` must not
    // have scrubbed underneath it, or written a result for it.
    assert.strictEqual(f.read().postProcess, undefined, 'stop wrote a post-processing result for a live recorder');
    assert.ok(!fs.existsSync(path.join(f.sessionDir, 'scrubbed.har')), 'stop scrubbed underneath a live recorder');
    assert.match(r.stderr, /stalled/i);
    assert.match(r.stderr, /1m00s|60s/, 'the message states how long nothing moved');
});

test('S3: a stop that never registered says so, exits 1, and assembles nothing under a live recorder', async () => {
    const f = fixture('s3');
    const run = fakeRun();   // alive, and never looks at the STOP file

    const r = await runStop(f, run);

    assert.strictEqual(r.code, 1, `got ${r.code}:\n${r.stderr}`);
    assert.ok(r.json, `no JSON on stdout: ${r.stdout}`);
    assert.strictEqual(r.json.stop, 'not-registered');
    assert.strictEqual(r.json.ending.registeredUtc, null);
    assert.strictEqual(r.json.ending.quietLimitSeconds, 60);
    assert.ok(run.elapsed() >= 60 * SECOND && run.elapsed() < 62 * SECOND,
        `gave up after ${run.elapsed() / 1000}s`);

    // The recording is still going. Assembling raw.har from its log here raced
    // the recorder for the one file that cannot be re-made.
    assert.ok(!fs.existsSync(f.harPath), 'stop assembled raw.har underneath a recorder that is still recording');
    assert.strictEqual(f.read().postProcess, undefined);
    assert.ok(fs.existsSync(f.stopFile), 'the request stays in place, so a recorder that looks later still ends');
    assert.match(r.stderr, /never registered/i);
});

test('S3b: a stop asked for long ago and still unanswered is reported at once, not waited on again', async () => {
    const f = fixture('s3b');
    writeAt(f.stopFile, '', T0 - 10 * MINUTE);
    const run = fakeRun();

    const r = await runStop(f, run);

    assert.strictEqual(r.code, 1);
    assert.strictEqual(r.json.stop, 'not-registered');
    assert.strictEqual(run.elapsed(), 0, 'the request is ten minutes old: there is nothing left to wait for');
    assert.ok(r.json.ending.quietSeconds >= 600);
    assert.strictEqual(Math.round(fs.statSync(f.stopFile).mtimeMs / 1000), Math.round((T0 - 10 * MINUTE) / 1000),
        'asking again must not make an old request look new');
});

test('S4: the recorder exits mid-scrub -- stop notices at once and runs the phases itself', async () => {
    const run = fakeRun([{ at: 10 * SECOND, run: () => { run.alive = false; } }]);
    const f = fixture('s4');
    writeAt(f.harPath, RAW_TEXT, run.start);
    Object.assign(f.session, { endedUtc: iso(run.start), endedBy: 'stop-command', summary: { exists: true, bytes: 30 * MB } });
    enter(f, 'scrub', run.start, 'stop-command');

    const r = await runStop(f, run);

    assert.ok(run.elapsed() <= 11 * SECOND,
        `waited ${run.elapsed() / 1000}s on a recorder that had exited`);
    const after = f.read();
    assert.ok(after.postProcess, `stop did not post-process the capture the recorder abandoned:\n${r.stderr}`);
    assert.deepStrictEqual(after.postProcess.errors, [], r.stderr);
    assert.ok(fs.existsSync(path.join(f.outputPath, 'scrubbed.har')), 'the scrub really ran');
    assert.strictEqual(r.json.stop, 'complete');
    assert.strictEqual(r.code, r.json.exitCode);
    assert.notStrictEqual(r.code, 1);
});

test('S5: stop on a cancelled capture reports it and exits 0', async () => {
    const f = fixture('s5');
    fs.writeFileSync(f.harPath, RAW_TEXT, 'utf8');
    Object.assign(f.session, { endedUtc: iso(T0 - MINUTE), endedBy: 'ctrl-c', cancelled: true });
    enter(f, 'finalizing', T0 - MINUTE, 'ctrl-c');
    const run = fakeRun();
    run.alive = false;

    const r = await runStop(f, run);

    assert.strictEqual(r.code, 0, `got ${r.code}:\n${r.stderr}`);
    assert.strictEqual(r.json.stop, 'not-processed');
    assert.match(r.stderr, /not post-processed/);
    assert.ok(!fs.existsSync(path.join(f.sessionDir, 'scrubbed.har')), 'a cancelled capture is not scrubbed');
});

test('S6: status gives the same state stop does, through the real command line', () => {
    // Dated in the past, so the real command -- real clock, no injection -- is
    // in each state the moment it looks.
    const stalled = fixture('s6-stalled');
    // The raw too: it is one of the files a sign of progress is read from, and
    // one written this instant is, rightly, a recording still being saved.
    writeAt(stalled.harPath, RAW_TEXT, T0 - 2 * 60 * MINUTE);
    Object.assign(stalled.session, { endedUtc: iso(T0 - 2 * 60 * MINUTE), endedBy: 'stop-command', summary: { exists: true, bytes: 2048 } });
    enter(stalled, 'verify', T0 - 2 * 60 * MINUTE, 'stop-command');

    const unregistered = fixture('s6-unregistered');
    writeAt(unregistered.stopFile, '', T0 - 10 * MINUTE);

    const progressing = fixture('s6-progressing');
    fs.writeFileSync(progressing.harPath, RAW_TEXT, 'utf8');
    Object.assign(progressing.session, { endedUtc: iso(Date.now()), endedBy: 'enter', summary: { exists: true, bytes: 2048 } });
    enter(progressing, 'scrub', Date.now(), 'enter');

    const statusOf = (f) => {
        const r = runCommand(f, 'status');
        assert.strictEqual(r.status, 0, r.stderr);
        return JSON.parse(r.stdout);
    };

    const s1 = statusOf(stalled);
    assert.ok(s1.ending, `status carries no ending state: ${JSON.stringify(s1)}`);
    assert.strictEqual(s1.ending.state, 'stalled');
    assert.strictEqual(s1.ending.phase, 'verify');
    assert.ok(s1.ending.quietSeconds >= 7200);
    assert.strictEqual(s1.postProcess, null, 'the result is still absent: only the state says why');

    const s2 = statusOf(unregistered);
    assert.strictEqual(s2.ending.state, 'not-registered');
    assert.strictEqual(s2.recording, true, 'it is still recording -- that is what never registered means');

    const s3 = statusOf(progressing);
    assert.strictEqual(s3.ending.state, 'progressing');
    assert.strictEqual(s3.ending.phase, 'scrub');
    assert.strictEqual(s3.ending.endedBy, 'enter');

    // And `stop`, as its own process, agrees with `status` about the two it returns in.
    const stopStalled = runCommand(stalled, 'stop');
    assert.strictEqual(stopStalled.status, 1, stopStalled.stderr);
    const stalledJson = jsonLine(stopStalled.stdout);
    assert.ok(stalledJson, `stop printed no JSON: ${stopStalled.stdout}`);
    assert.strictEqual(stalledJson.stop, 'stalled');
    assert.strictEqual(stalledJson.ending.phase, s1.ending.phase);

    const stopUnregistered = runCommand(unregistered, 'stop');
    assert.strictEqual(stopUnregistered.status, 1, stopUnregistered.stderr);
    assert.strictEqual(jsonLine(stopUnregistered.stdout).stop, 'not-registered');
    assert.ok(!fs.existsSync(unregistered.harPath), 'nothing assembled under a live recorder');
});

// ---------------------------------------------------------------------------
// The recorder says where it is
// ---------------------------------------------------------------------------

function inDir(dir, fn) {
    const cwd = process.cwd();
    process.chdir(dir);
    try { return fn(); } finally { process.chdir(cwd); }
}

function quietly(fn) {
    const original = process.stderr.write;
    process.stderr.write = () => true;
    try { return fn(); } finally { process.stderr.write = original; }
}

test('R1: post-processing reports each phase as it STARTS, in order', () => {
    const f = fixture('r1');
    fs.writeFileSync(f.harPath, RAW_TEXT, 'utf8');
    const seen = [];
    const state = inDir(f.root, () => quietly(() => capture.postProcess(f.session, {
        onPhase: (phase) => seen.push(phase),
        run: (script, argv, cwd, input) => {
            // What had been reported when each child STARTED is the property: a
            // phase named after its work is over is not a sign of progress.
            seen.push(`run:${path.basename(script)}`);
            return capture.runNode(script, argv, cwd, input);
        }
    })));

    assert.deepStrictEqual(state.errors, [], 'the fixture must post-process cleanly');
    assert.deepStrictEqual(seen, [
        'scrub', 'run:sanitize-har.js', 'verify', 'run:verify-scrub.js', 'publish', 'digest', 'catalogue'
    ]);
});

test('R2: the AI catalogue pass is its own phase, entered before the child runs', () => {
    const f = fixture('r2');
    fs.writeFileSync(f.harPath, RAW_TEXT, 'utf8');
    const seen = [];
    inDir(f.root, () => quietly(() => capture.postProcess(f.session, {
        onPhase: (phase) => seen.push(phase),
        runnerContext: { env: {}, isTty: true, claudeOnPath: true },
        spawn: () => { seen.push('spawn'); return { status: 0 }; }
    })));

    assert.deepStrictEqual(seen.slice(-3), ['catalogue', 'ai-catalogue', 'spawn']);
});

test('R3: post-processing called without a reporter writes no record anywhere', () => {
    // `stop` runs these phases itself when the recorder is gone, and `catalogue`
    // runs the last of them on a capture that has no session at all.
    const f = fixture('r3');
    fs.writeFileSync(f.harPath, RAW_TEXT, 'utf8');
    const before = fs.readFileSync(f.sessionFile, 'utf8');
    const state = inDir(f.root, () => quietly(() => capture.postProcess(f.session)));

    assert.deepStrictEqual(state.errors, []);
    assert.strictEqual(fs.readFileSync(f.sessionFile, 'utf8'), before);
    assert.strictEqual(f.session.ending, undefined);
});

(async () => {
    let passed = 0;
    const failures = [];
    for (const { name, fn } of queued) {
        try { await fn(); passed++; } catch (e) {
            failures.push(name);
            process.stderr.write(`FAIL: ${name}\n  ${e.message}\n`);
            if (e.stack) process.stderr.write(e.stack.split('\n').slice(1, 4).join('\n') + '\n');
        }
    }
    try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (e) { /* best effort */ }
    if (failures.length) {
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-ending tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All capture-ending tests passed (${passed})`);
})();
