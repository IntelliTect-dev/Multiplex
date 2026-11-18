#!/usr/bin/env node
// End-to-end: a recording that ENDS DURING A PAUSE sends nothing it held. A
// real capture under -AI, a real (headless) Chrome, the stream-json stub agent
// and a local fixture site that witnesses every request it is sent. Zero-dep;
// runs with `node capture-ai-pause-ending.test.js`.
//
// capture-pause.test.js proves the order (abort, THEN remove the route) on a
// plain Playwright context. This is the same on the production path: the
// recorder's own context, its own ending, and the moment it lets go of the
// browser -- after which a request the browser still held would be released
// and sent. A file of its own to stay inside the runner's per-file limit.
//
// A GUARD, NOT A FALSIFIER. At 6bd1094 this path happened to abort too: on the
// recorder's CDP-attached persistent context the abort reached the browser
// before the route was removed, where on a plain `browser.newContext()` it did
// not (capture-pause.test.js E1 fails there). The order is now right by
// construction; this pins the outcome where it matters.
//
//   K1  the agent's second click is HELD by a long pause; the recording is
//       stopped mid-pause; the held request is ABORTED -- the site never sees
//       it, not when the recording ends and not after the recorder has gone

'use strict';

const assert = require('assert');
const fs = require('fs');
const http = require('http');
const path = require('path');

const { createE2eKit } = require(path.join(__dirname, 'capture-ai-e2e.test-support.js'));
const capture = require(path.join(__dirname, 'capture-har.js'));

const kit = createE2eKit('capture-ai-pause-end-');
const {
    makeRepo, pathWithoutAgent, agentOnPath, driverEnv, integrationBlocker, blockedOrSkipped, readJson,
    waitFor, pidAlive, killTree, cdpAnswers, startStubDrivenCapture, waitForRecordingSession
} = kit;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

/** The site: its first /api/limited is a 429 asking for a minute; `hits` is what reached it. */
async function longPauseSite() {
    const hits = [];
    const server = http.createServer((req, res) => {
        if (req.url.startsWith('/api/limited')) {
            hits.push({ at: Date.now(), url: req.url });
            if (hits.length === 1) {
                res.writeHead(429, { 'content-type': 'application/json', 'retry-after': '60' });
                res.end(JSON.stringify({ error: 'too many requests' }));
                return;
            }
            res.writeHead(200, { 'content-type': 'application/json' });
            res.end(JSON.stringify({ ok: true }));
            return;
        }
        // The page counts its own clicks: the witness that the second click was
        // MADE, whatever the site and the recorder saw of it.
        res.writeHead(200, { 'content-type': 'text/html' });
        res.end('<html><body><h1>ai drive fixture</h1>' +
            '<button id="limited" onclick="window.clicks=(window.clicks||0)+1;fetch(\'/api/limited?q=again\')">' +
            'limited</button></body></html>');
    });
    await new Promise((r) => server.listen(0, '127.0.0.1', r));
    return { server, uri: `http://127.0.0.1:${server.address().port}/`, hits };
}

test('K1: a recording stopped mid-pause aborts what it held -- the site never sees it', async () => {
    const repo = makeRepo('ai-pause-end');
    if (blockedOrSkipped('K1', integrationBlocker(repo))) return;

    const { server, uri, hits } = await longPauseSite();
    const pruned = pathWithoutAgent();
    assert.ok(!agentOnPath(pruned), 'the agent must be reachable only through the stub');
    // The stub that does NOT wait when told: it clicks again 300 ms after the
    // pause message, and that click is held by the browser.
    const childEnv = driverEnv({ PATH: pruned, HAR_STUB_AGENT_MODE: 'throttled' });

    let recorder = null;
    let browserPid = null;
    let agentPid = null;
    try {
        recorder = startStubDrivenCapture(repo, uri, childEnv);
        let recorderErr = '';
        recorder.stderr.on('data', (d) => { recorderErr += d; });
        recorder.stdout.on('data', () => {});
        const { sessionDir, session } = await waitForRecordingSession(repo, recorder, () => recorderErr);
        browserPid = session.browserPid;

        assert.ok(await waitFor(() => /capture-har: Paused until/.test(recorderErr), 120000),
            `the 429 never paused the capture:\n${recorderErr}`);
        assert.ok(await waitFor(() => fs.existsSync(session.agentLogPath) &&
            /capture-har -> agent: The site asked us to slow down/.test(fs.readFileSync(session.agentLogPath, 'utf8')),
        30000), 'the agent was told to wait');
        // The stub clicks again 300 ms after being told. THE PAGE says when it
        // has: two clicks made, one request at the site -- the second is held.
        const playwright = require(capture.resolvePlaywright({ cwd: repo, env: childEnv }).path);
        const watcher = await playwright.chromium.connectOverCDP(session.cdpEndpoint);
        try {
            let page = null;
            assert.ok(await waitFor(() => (page = watcher.contexts()[0].pages().find((p) => p.url().startsWith(uri))), 30000),
                `no tab on ${uri}`);
            assert.ok(await waitFor(async () => (await page.evaluate(() => window.clicks || 0)) >= 2, 30000),
                'the agent made its second click');
        } finally {
            await watcher.close();   // disconnects; the browser is not this test's to close
        }
        await sleep(500);
        assert.strictEqual(hits.length, 1, `the second click is held, not sent: ${JSON.stringify(hits)}`);

        // STOPPED MID-PAUSE -- the file Stop-HarRecording writes.
        fs.writeFileSync(path.join(sessionDir, 'STOP'), '', 'utf8');
        assert.ok(await waitFor(() => recorder.exitCode !== null, 120000), `the stop did not end it:\n${recorderErr}`);
        // After the recorder has let go of the browser, too.
        await sleep(2000);
        assert.strictEqual(hits.length, 1,
            `the click held for the pause reached the site after the recording ended: ${JSON.stringify(hits)}`);

        const finalSession = readJson(path.join(sessionDir, 'session.json'));
        agentPid = finalSession.agentPid;
        assert.strictEqual(finalSession.endedBy, 'stop-command');
        const [p] = finalSession.pauses || [];
        assert.ok(p && p.kind === 'pause' && p.outcome === 'ended' && p.resumedBy === 'recording-ended',
            `the pause ended with the recording: ${JSON.stringify(finalSession.pauses)}`);
        assert.ok(finalSession.postProcess, 'and was post-processed as usual');
        assert.ok(await cdpAnswers(session.cdpEndpoint) && pidAlive(browserPid), 'the browser is left running');
    } finally {
        server.close();
        if (agentPid && pidAlive(agentPid)) killTree(agentPid);
        if (browserPid) killTree(browserPid);
        if (recorder && recorder.exitCode === null) recorder.kill();
    }
});

// ---------------------------------------------------------------------------

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
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-ai-pause-ending tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All capture-ai-pause-ending tests passed (${passed})` +
        (kit.skipped.length ? ` -- SKIPPED: ${kit.skipped.join(', ')}` : ''));
    process.exit(0);
})();
