#!/usr/bin/env node
// End-to-end tests for issue #26 -- a real recording says which run made it.
// Zero-dep; runs with `node capture-run-identity.test.js`.
//
// capture-run.test.js pins the RULE. This pins that the recorder USES it: the
// real `capture-har.js start`, a real (headless) Chrome, a page served from
// this process, and then every place the session record is surfaced.
//
// Nothing here touches the public internet. Like the other end-to-end capture
// suites it needs the Playwright driver and Chrome; without them it SKIPS on a
// laptop and FAILS in CI, where a suite that goes quiet is a suite that lies.
//
// FALSIFIERS (fail before this change, pass after):
//   E1  a recording's session.json carries a capture id and a run record from
//       its first write -- and the run is the one ANOTHER child of the same
//       invoking process resolves for itself
//   E2  `status` reports both
//   E3  the walk over the store reports both
//   E4  the recorder's one machine-readable line reports both
//   E5  a second recording by the same process, with the SAME description, is
//       the same run and a different capture
//   E6  a capture recorded before this change is still found, and is reported
//       as having neither -- by the walk and by `status`
//   E7  nothing sensitive is in what was recorded
//   E8  a recording whose lookup is slow starts anyway, says the run is not
//       known yet, and ends with it -- written with the session's own next
//       write, never by one of its own

'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

const { createE2eKit } = require(path.join(__dirname, 'capture-ai-e2e.test-support.js'));
const captureRun = require(path.join(__dirname, 'capture-run.js'));

const captureJs = path.join(__dirname, 'capture-har.js');
const storeJs = path.join(__dirname, 'capture-store.js');
const HEADLESS_PRELOAD = path.join(__dirname, 'capture-native-headless.test-support.js');
const SLOW_LOOKUP_PRELOAD = path.join(__dirname, 'capture-run-slow-lookup.test-support.js');
const LEAF = path.join(__dirname, 'capture-run.test-support.js');

const kit = createE2eKit('capture-run-identity-');

const TOKEN = /^[0-9a-f]{12}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const READ_HERE = process.platform === 'linux' || process.platform === 'win32';
const NOT_YET = captureRun.REASONS.NOT_YET;
// The one description every recording here carries: the measured case was
// three captures by one session that all read the same.
const DESCRIBE = 'run identity: load the fixture page';

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * Run a Node script to completion WITHOUT blocking this process.
 *
 * Never `spawnSync` here: the page being recorded is served by this process,
 * and a blocked event loop is a site that stopped answering -- the browser's
 * navigation then times out and the recording holds nothing.
 */
function runNode(script, args, cwd) {
    return new Promise((resolve, reject) => {
        const child = spawn(process.execPath, [script].concat(args), {
            cwd, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: kit.driverEnv()
        });
        let stdout = '';
        let stderr = '';
        child.stdout.on('data', (d) => { stdout += d; });
        child.stderr.on('data', (d) => { stderr += d; });
        child.on('error', reject);
        child.on('close', (status) => resolve({ status, stdout, stderr }));
    });
}

/** What another child of THIS process resolves as its run: the recorder's sibling. */
async function siblingIdentity() {
    const r = await runNode(LEAF, ['leaf']);
    assert.strictEqual(r.status, 0, `the sibling lookup failed: ${r.stderr}`);
    return JSON.parse(r.stdout.trim());
}

function sessionDirs(repo) {
    const root = path.join(repo, '.har-captures');
    const out = [];
    if (!fs.existsSync(root)) return out;
    for (const host of fs.readdirSync(root)) {
        const hostDir = path.join(root, host);
        if (!fs.statSync(hostDir).isDirectory()) continue;
        for (const stamp of fs.readdirSync(hostDir)) {
            if (fs.existsSync(path.join(hostDir, stamp, 'session.json'))) out.push(path.join(hostDir, stamp));
        }
    }
    return out;
}

/**
 * One whole recording: start, read the FIRST session.json it writes, wait for
 * the page to be recorded, ask `status`, end it with a separate `stop` the way
 * an agent does, and collect its stdout.
 *
 * `atStart` is the first write, `atStatus` is the file as `status` was asked,
 * and `atEnd` is the file once the recorder has exited. On an ordinary machine
 * all three carry the same run. On one busy enough that the lookup is still
 * going when recording starts, the first says the run is not known yet and the
 * last carries it -- and E8 makes that happen on purpose (`env` for the
 * recorder, and `beforeStop`, run after `status` and before the recording is
 * ended).
 */
async function recordOnce(repo, uri, site, options = {}) {
    const before = new Set(sessionDirs(repo));
    const hitsBefore = site.hits;
    const preloads = [HEADLESS_PRELOAD].concat(options.preloads || []);
    const recorder = spawn(process.execPath, preloads.flatMap((p) => ['-r', p]).concat([
        captureJs, 'start',
        '--uri', uri,
        '--describe', DESCRIBE,
        '--browser', 'native',
        '--profile', path.join(repo, '.chrome-profile'),
        '--snapshot-seconds', '1',
        '--no-wait'
    ]), { cwd: repo, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: kit.driverEnv(options.env) });
    let out = '';
    let err = '';
    recorder.stdout.on('data', (d) => { out += d; });
    recorder.stderr.on('data', (d) => { err += d; });

    let sessionDir = null;
    let atStart = null;
    let browserPid = null;
    try {
        const deadline = Date.now() + 120000;
        while (Date.now() < deadline && !atStart) {
            sessionDir = sessionDirs(repo).find((d) => !before.has(d)) || null;
            const doc = sessionDir ? kit.readJson(path.join(sessionDir, 'session.json')) : null;
            if (doc && doc.startedUtc) { atStart = doc; break; }
            assert.ok(recorder.exitCode === null,
                `the recorder exited (${recorder.exitCode}) before it recorded anything:\n${err}`);
            await sleep(250);
        }
        assert.ok(atStart, `no capture session appeared under ${repo}\n${err}`);
        browserPid = atStart.browserPid;
        const sessionFile = path.join(sessionDir, 'session.json');

        // The page has to have been asked for and logged, or there is no
        // recording to post-process and no completion line to read.
        const recordLog = path.join(sessionDir, 'raw.ndjson');
        await kit.waitFor(() => site.hits > hitsBefore &&
            fs.existsSync(recordLog) && fs.statSync(recordLog).size > 0, 90000);
        assert.ok(site.hits > hitsBefore, `the browser never loaded the page:\n${err}`);

        const atStatus = kit.readJson(sessionFile);
        const status = await runNode(captureJs, ['status', '--session', sessionDir], repo);
        if (options.beforeStop) await options.beforeStop();
        const stopped = await runNode(captureJs, ['stop', '--session', sessionDir], repo);
        await kit.waitFor(() => recorder.exitCode !== null, 120000);
        assert.ok(recorder.exitCode !== null, `the recorder did not end after stop:\n${err}\n${stopped.stderr}`);

        return {
            sessionDir,
            atStart,
            atStatus,
            atEnd: kit.readJson(sessionFile),
            status: JSON.parse(status.stdout),
            recorderStdout: out,
            recorderStderr: err
        };
    } finally {
        if (browserPid) kit.killTree(browserPid);
        if (recorder.exitCode === null) recorder.kill();
    }
}

/** The one machine-readable line a finished recording prints. */
function completeLine(stdout) {
    for (const line of stdout.split(/\r?\n/)) {
        if (!line.trim().startsWith('{')) continue;
        try {
            const doc = JSON.parse(line);
            if (doc.capture === 'complete') return doc;
        } catch (e) { void e; }
    }
    return null;
}

/** A local page, and how often it has been asked for. */
async function fixtureSite() {
    const { server, uri } = await kit.fixtureServer();
    const site = { hits: 0, uri, close: () => server.close() };
    server.on('request', () => { site.hits++; });
    return site;
}

test('E1-E7: a real recording says which run made it, everywhere the session is surfaced', async () => {
    const repo = kit.makeRepo('run-identity');
    if (kit.blockedOrSkipped('E1-E7', kit.integrationBlocker(repo))) return;

    const site = await fixtureSite();
    const uri = site.uri;
    try {
        const sibling = await siblingIdentity();
        const first = await recordOnce(repo, uri, site);

        // E1 -- from the FIRST write, not only once it is over.
        assert.match(String(first.atStart.captureId), UUID,
            `session.json has no capture id from its first write: ${JSON.stringify(
                { captureId: first.atStart.captureId, run: first.atStart.run })}`);
        assert.ok(first.atStart.run && typeof first.atStart.run === 'object',
            'session.json has no run record from its first write');
        // The first write carries the run itself unless this machine is busy
        // enough that the lookup was still going (E8 is that case on purpose).
        // What must hold either way: once known it is never replaced.
        if (first.atStart.run.reason !== NOT_YET) assert.deepStrictEqual(first.atEnd.run, first.atStart.run);
        assert.strictEqual(first.atEnd.captureId, first.atStart.captureId, 'the capture id does not change as it ends');
        if (READ_HERE) {
            assert.match(String(first.atEnd.run.id), TOKEN,
                `this platform is read, so the recording has a run: ${JSON.stringify(first.atEnd.run)}`);
            assert.strictEqual(first.atEnd.run.id, sibling.run.id,
                'the recorder and another child of the same invoking process are one run: ' +
                JSON.stringify({ recorder: first.atEnd.run, sibling: sibling.run }));
            assert.ok(['terminal', 'process', 'shell'].includes(first.atEnd.run.basis));
        } else {
            assert.deepStrictEqual(first.atEnd.run, { id: null, reason: captureRun.REASONS.UNSUPPORTED });
        }
        const runId = first.atEnd.run.id;

        // E2 -- `status` reports what the session held when it was asked.
        assert.strictEqual(first.status.captureId, first.atStart.captureId);
        assert.strictEqual(first.status.runId, first.atStatus.run.id);
        if (first.atStart.run.reason !== NOT_YET) assert.strictEqual(first.status.runId, runId);

        // E4 -- the recorder's one machine-readable line.
        const line = completeLine(first.recorderStdout);
        assert.ok(line, `the recorder printed no completion line:\n${first.recorderStdout}\n${first.recorderStderr}`);
        assert.strictEqual(line.captureId, first.atStart.captureId);
        assert.strictEqual(line.runId, runId);

        // E5 -- again, by the same process, described identically.
        const second = await recordOnce(repo, uri, site);
        assert.strictEqual(second.atStart.describe, first.atStart.describe);
        assert.strictEqual(second.atEnd.run.id, runId, 'one invoking process, one run');
        assert.match(String(second.atStart.captureId), UUID);
        assert.notStrictEqual(second.atStart.captureId, first.atStart.captureId,
            'two recordings that read the same are still two captures');

        // E6 -- a capture from before any of this existed, in the same store.
        const old = path.join(path.dirname(first.sessionDir), '2020-01-01-000000');
        fs.mkdirSync(old, { recursive: true });
        fs.writeFileSync(path.join(old, 'raw.har'), JSON.stringify({ log: { version: '1.2', entries: [] } }));
        fs.writeFileSync(path.join(old, 'session.json'), JSON.stringify({
            uri, describe: DESCRIBE, sessionDir: old, harPath: path.join(old, 'raw.har'),
            startedUtc: '2020-01-01T00:00:00.000Z', endedUtc: '2020-01-01T00:01:00.000Z'
        }));
        const oldStatus = JSON.parse((await runNode(captureJs, ['status', '--session', old], repo)).stdout);
        assert.strictEqual(oldStatus.captureId, null, 'a capture with no id reports none, not a made-up one');
        assert.strictEqual(oldStatus.runId, null);

        // E3 -- the walk, over the whole store.
        const walked = JSON.parse((await runNode(storeJs, [path.join(repo, '.har-captures')], repo)).stdout);
        const byDir = new Map(walked.map((c) => [c.dir, c]));
        assert.strictEqual(byDir.get(first.sessionDir).captureId, first.atStart.captureId);
        assert.strictEqual(byDir.get(first.sessionDir).runId, runId);
        assert.strictEqual(byDir.get(second.sessionDir).captureId, second.atStart.captureId);
        assert.strictEqual(byDir.get(second.sessionDir).runId, runId);
        assert.strictEqual(byDir.get(old).captureId, null);
        assert.strictEqual(byDir.get(old).runId, null);
        if (READ_HERE) {
            // SELECTING BY THE RUN finds both recordings and not the old one.
            assert.deepStrictEqual(
                walked.filter((c) => c.runId && c.runId === runId).map((c) => c.dir).sort(),
                [first.sessionDir, second.sessionDir].sort());
        }

        // E7 -- what was added to session.json is ids and a basis, and nothing
        // that names this machine or the person at it.
        const added = JSON.stringify({ captureId: first.atEnd.captureId, run: first.atEnd.run }).toLowerCase();
        for (const secret of [os.userInfo().username, os.hostname(), path.basename(os.homedir())]) {
            if (!secret || secret.length < 3) continue;
            assert.ok(!added.includes(secret.toLowerCase()), `${secret} must not be in ${added}`);
        }
        const allowed = first.atEnd.run.id === null ? ['id', 'reason'] : ['basis', 'id', 'lineage'];
        assert.deepStrictEqual(Object.keys(first.atEnd.run).sort(), allowed);
    } finally {
        site.close();
    }
});

test('E8: a recording whose lookup is slow starts anyway, and ends with its run', async () => {
    // FOUND BY RECORDING FOR REAL. The first version waited for the lookup and
    // then gave up, and on a busy machine two recordings of four were written
    // with no run at all. Waiting longer would hold up somebody sitting in
    // front of a browser that has opened and not navigated. So the recording
    // starts, the session says the run is not known yet, and the run joins the
    // session when it arrives -- written with the session's next write, which
    // is the ending at the latest.
    const repo = kit.makeRepo('run-identity-slow');
    if (kit.blockedOrSkipped('E8', kit.integrationBlocker(repo))) return;

    const site = await fixtureSite();
    try {
        const sibling = await siblingIdentity();
        // The preload withholds the lookup's answer until this file exists, so
        // the recording MUST start without its run -- and the answer arrives
        // exactly when this test lets it, after `status` has been asked.
        const releaseFile = path.join(repo, 'release-the-run-lookup');
        const slow = await recordOnce(repo, site.uri, site, {
            preloads: [SLOW_LOOKUP_PRELOAD],
            env: { HAR_TEST_RUN_LOOKUP_RELEASE: releaseFile },
            beforeStop: async () => {
                fs.writeFileSync(releaseFile, '');
                // Nothing on disk changes when it arrives -- that is the point
                // -- so there is nothing to wait ON. Long enough for a poll.
                await sleep(4000);
            }
        });

        assert.match(String(slow.atStart.captureId), UUID, 'the capture id never waits for anything');
        assert.deepStrictEqual(slow.atStart.run, { id: null, reason: NOT_YET },
            'the recording started before its lookup answered, and its first write must say so: ' +
            JSON.stringify(slow.atStart.run));

        // NOT WRITTEN BY A WRITE OF ITS OWN. session.json is not written
        // atomically and the driving agent reads it as it starts; a write at a
        // moment nothing chose is one more chance of it reading half a file.
        assert.deepStrictEqual(slow.atStatus.run, { id: null, reason: NOT_YET },
            'nothing else wrote the session meanwhile, so the file still says what its first write said');
        assert.strictEqual(slow.status.runId, null);

        assert.strictEqual(slow.atEnd.captureId, slow.atStart.captureId);
        if (READ_HERE) {
            assert.match(String(slow.atEnd.run.id), TOKEN,
                `the run arrived during the recording and must be in its last write: ${JSON.stringify(slow.atEnd.run)}`);
            assert.strictEqual(slow.atEnd.run.id, sibling.run.id,
                'the run that arrived late is the one this process\'s other children resolve');
        } else {
            assert.deepStrictEqual(slow.atEnd.run, sibling.run);
        }

        const line = completeLine(slow.recorderStdout);
        assert.ok(line, `the recorder printed no completion line:\n${slow.recorderStderr}`);
        assert.strictEqual(line.runId, slow.atEnd.run.id, 'the completion line carries the run that arrived');
    } finally {
        site.close();
    }
});

// FAILED UNTIL PROVEN OTHERWISE: a wait that nothing holds open would end this
// process quietly with status 0, and the runner reads only the exit code.
process.exitCode = 1;

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
    kit.cleanup();
    if (failures.length) {
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-run-identity tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    process.exitCode = 0;
    console.log(`All capture-run-identity tests passed (${passed})` +
        (kit.skipped.length ? ` -- SKIPPED: ${kit.skipped.join(', ')}` : ''));
})();
