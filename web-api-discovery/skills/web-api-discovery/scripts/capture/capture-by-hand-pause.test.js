#!/usr/bin/env node
// End-to-end tests for a capture WITHOUT -AI when the site throttles or puts up
// a check: it is a PERSON'S capture, whether or not a terminal is attached.
// A real capture, a real (headless) Chrome and a local fixture site; the
// "person" is this test, driving the page over the CDP endpoint as anyone
// browsing it would. Zero-dep; runs with `node capture-by-hand-pause.test.js`.
//
// Why a file of its own: the recorder runs WITHOUT a TTY here, which is
// exactly the case an AI session or a scheduled task is in when it starts a
// capture and a person then browses it by hand (SKILL.md's capture-only flow).
// The shared fixtures are in capture-ai-e2e.test-support.js.
//
// FALSIFIERS (fail before this change, pass after):
//   J1  without -AI and with nobody at the terminal, a throttle is ADVICE: the
//       person's next request reaches the site at once, not held for the wait;
//       a bot check is advice too -- no hand-over, no phone link even with a
//       tailnet, no recording ended on the person's behalf. The recording ends
//       when they end it.

'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

const { createE2eKit } = require(path.join(__dirname, 'capture-ai-e2e.test-support.js'));
const capture = require(path.join(__dirname, 'capture-har.js'));

const kit = createE2eKit('capture-by-hand-');
const {
    makeRepo, driverEnv, integrationBlocker, blockedOrSkipped, readJson, waitFor, pidAlive, killTree,
    fixtureServer, startStubDrivenCapture, waitForRecordingSession
} = kit;

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

test('J1: without -AI it is a person\'s capture -- a throttle or a check is advice, never a hold or an ending',
    async () => {
        const repo = makeRepo('by-hand');
        if (blockedOrSkipped('J1', integrationBlocker(repo))) return;

        const { server, uri, limitedHits } = await fixtureServer();
        // A TAILNET IS THERE (faked onto loopback): before, this run would
        // have handed the check to "the phone" and started its 30-minute
        // ending. A person's capture gets advice instead.
        const childEnv = driverEnv({
            HAR_CAPTURE_TAILSCALE: 'tailscale',
            HAR_STUB_TAILSCALE_JSON: JSON.stringify({ BackendState: 'Running', Self: { TailscaleIPs: ['127.0.0.1'] } })
        });

        let recorder = null;
        let browserPid = null;
        let browser = null;
        try {
            recorder = startStubDrivenCapture(repo, uri, childEnv, { ai: false });
            let recorderErr = '';
            recorder.stderr.on('data', (d) => { recorderErr += d; });
            recorder.stdout.on('data', () => {});
            const { sessionDir, session } = await waitForRecordingSession(repo, recorder, () => recorderErr);
            browserPid = session.browserPid;

            // THE PERSON, browsing the capture's own tab.
            const resolved = capture.resolvePlaywright({ cwd: repo, env: childEnv });
            const playwright = require(resolved.path);
            browser = await playwright.chromium.connectOverCDP(session.cdpEndpoint);
            const context = browser.contexts()[0];
            const page = context.pages().find((p) => p.url().startsWith(uri)) || context.pages()[0];
            await page.waitForSelector('#limited', { timeout: 30000 });
            const fetchLimited = () => page.evaluate(() => fetch('/api/limited?q=by-hand').then((r) => r.status));

            // A 429 with Retry-After: 3 -- then, once the recorder has taken it
            // in (its line is out, plus a beat, as in I3), the person's next
            // request. Held, it would not reach the site until the 3 s were up.
            assert.strictEqual(await fetchLimited(), 429);
            assert.ok(await waitFor(() => /Wait at least 3 s|Paused until/.test(recorderErr), 30000),
                `the recorder never said anything about the 429:\n${recorderErr}`);
            await new Promise((r) => setTimeout(r, 300));
            const asked = Date.now();
            assert.strictEqual(await fetchLimited(), 200);
            const took = Date.now() - asked;
            assert.strictEqual(limitedHits.length, 2);
            assert.match(recorderErr, /Wait at least 3 s/, 'the person is told how long the site asked for');
            assert.doesNotMatch(recorderErr, /Paused until/, 'no pause is announced on a person\'s capture');
            assert.ok(took < 2000, `the person's own request waited ${took} ms -- a by-hand capture is never held`);
            assert.ok(!(readJson(path.join(sessionDir, 'session.json')).pauses || []).length,
                'and no pause is recorded: nothing was held');

            // A BOT CHECK: advice, not a hand-over -- and no phone link.
            await page.goto(new URL('/challenge', uri).href, { waitUntil: 'domcontentloaded' });
            assert.ok(await waitFor(() => /the site put up a bot check/.test(recorderErr), 30000),
                `the person is told about the check:\n${recorderErr}`);
            await new Promise((r) => setTimeout(r, 2000));
            assert.doesNotMatch(recorderErr, /Handed over to you|Open on your phone/,
                `nothing is handed to a phone on a person's capture:\n${recorderErr}`);
            assert.strictEqual(recorder.exitCode, null, 'the recording is NOT ended on the person\'s behalf');
            const midway = readJson(path.join(sessionDir, 'session.json'));
            assert.strictEqual(midway.handOver.by, 'advice');
            assert.ok(!(midway.pauses || []).length, `no pause or hand-over is recorded: ${JSON.stringify(midway.pauses)}`);

            // THE PERSON ends it, the way Stop-HarRecording does.
            await browser.close();
            browser = null;
            fs.writeFileSync(path.join(sessionDir, 'STOP'), '', 'utf8');
            assert.ok(await waitFor(() => recorder.exitCode !== null, 120000), `the stop did not end it:\n${recorderErr}`);
            const finalSession = readJson(path.join(sessionDir, 'session.json'));
            assert.strictEqual(finalSession.endedBy, 'stop-command', 'it ended when the person ended it');
            assert.ok(finalSession.postProcess, 'and was post-processed as usual');
        } finally {
            if (browser) await browser.close().catch(() => {});
            server.close();
            if (browserPid && pidAlive(browserPid)) killTree(browserPid);
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
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-by-hand-pause tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All capture-by-hand-pause tests passed (${passed})` +
        (kit.skipped.length ? ` -- SKIPPED: ${kit.skipped.join(', ')}` : ''));
    process.exit(0);
})();
