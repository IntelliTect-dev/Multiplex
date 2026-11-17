#!/usr/bin/env node
// End-to-end tests for PAUSE AND TELL under -AI: a real capture, a real
// (headless) Chrome, the stream-json stub agent, and a local fixture site.
// Zero-dep; runs with `node capture-ai-pause.test.js`.
//
// Split from capture-ai-drive.test.js so each file stays well inside the Node
// runner's per-file time limit; the shared fixtures are in
// capture-ai-e2e.test-support.js. The pure decisions behind these runs are
// pinned in pacing.test.js, capture-pause.test.js,
// capture-agent-messaging.test.js and mirror.test.js.
//
// FALSIFIERS (fail before this change, pass after):
//   I3  a site that throttles the agent PAUSES THE WHOLE CAPTURE: the agent is
//       told, the browser holds its next click until the Retry-After is up (the
//       fixture server is the witness), the capture carries on by itself, and
//       the agent finishes -- never killed
//   I4  a bot check hands over: the agent is told a person is taking over;
//       with nobody at the terminal and no tailnet the recording ends
//       `handed-over`, the agent is told the capture ended and exits by itself,
//       and the browser is left running
//   I5  the owner's case: nobody at the terminal, the hand-over prints a
//       private phone link, the phone sees the live browser, taps "I'm done,
//       continue", and the agent is told to carry on and finishes

'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

const { createE2eKit } = require(path.join(__dirname, 'capture-ai-e2e.test-support.js'));

const kit = createE2eKit('capture-ai-pause-');
const {
    makeRepo, pathWithoutAgent, agentOnPath, driverEnv, integrationBlocker, blockedOrSkipped, readJson,
    waitFor, pidAlive, killTree, cdpAnswers, fixtureServer, startStubDrivenCapture, waitForRecordingSession,
    phoneRequest
} = kit;

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

test('I3: a site that throttles the agent pauses the WHOLE capture, holds the browser, and carries on',
    async () => {
        // THE OWNER'S "JUST PAUSE", end to end. The fixture answers the first
        // /api/limited with 429 and Retry-After: 3, and the stub agent clicks
        // it again at once -- an agent that does not wait when told. The
        // recorder must: pause the capture, TELL the agent, HOLD the second
        // click in the browser until the three seconds are up (the server is
        // the witness: it must not see it sooner), carry on by itself, tell the
        // agent to continue, and let it finish -- never killing it.
        const repo = makeRepo('ai-throttled');
        if (blockedOrSkipped('I3', integrationBlocker(repo))) return;

        const { server, uri, limitedHits } = await fixtureServer();
        const pruned = pathWithoutAgent();
        assert.ok(!agentOnPath(pruned), 'the agent must be reachable only through the stub');
        const childEnv = driverEnv({ PATH: pruned, HAR_STUB_AGENT_MODE: 'throttled' });

        let recorder = null;
        let browserPid = null;
        let agentPid = null;
        try {
            recorder = startStubDrivenCapture(repo, uri, childEnv);
            let recorderErr = '';
            let recorderOut = '';
            recorder.stderr.on('data', (d) => { recorderErr += d; });
            recorder.stdout.on('data', (d) => { recorderOut += d; });

            const { sessionDir, session } =
                await waitForRecordingSession(repo, recorder, () => recorderErr);
            browserPid = session.browserPid;
            assert.deepStrictEqual(session.pace.navigation, [1500, 4000], 'human pace is the default');

            const ended = await waitFor(() => recorder.exitCode !== null, 180000);
            assert.ok(ended, `the recording never ended:\n${recorderErr}`);

            const finalSession = readJson(path.join(sessionDir, 'session.json'));
            agentPid = finalSession.agentPid;

            // NOTHING REACHED THE SITE DURING THE PAUSE.
            assert.strictEqual(limitedHits.length, 2, `two clicks, two requests: ${limitedHits.length}`);
            const gap = limitedHits[1] - limitedHits[0];
            assert.ok(gap >= 2800, `the held click reached the site ${gap} ms after the 429 -- inside the 3 s pause`);

            // THE PAUSE, RECORDED: why, how long, who was driving, how it ended.
            const pauses = finalSession.pauses || [];
            assert.strictEqual(pauses.length, 1, `one pause: ${JSON.stringify(pauses)}`);
            const [p] = pauses;
            assert.strictEqual(p.kind, 'pause');
            assert.strictEqual(p.status, 429);
            assert.strictEqual(p.waitMs, 3000, 'exactly as long as the site asked');
            assert.strictEqual(p.named, true);
            assert.strictEqual(p.driver, 'agent');
            assert.strictEqual(p.resumedBy, 'timer', 'it carried on by itself');
            assert.strictEqual(p.outcome, 'continued');
            assert.ok(p.startedUtc && p.untilUtc && p.endedUtc, JSON.stringify(p));
            assert.ok(!/\?/.test(p.url), 'session.json records where, never the query');
            assert.ok(!finalSession.handOver, 'a pause is not a hand-over');

            // THE AGENT WAS TOLD, AND FINISHED -- the capture was never ended for it.
            assert.strictEqual(finalSession.endedBy, 'agent-finished',
                `the agent finishing is the ending, after the pause:\n${recorderErr}`);
            assert.strictEqual(finalSession.agentExitCode, 0);
            const stepLog = fs.readFileSync(session.agentLogPath, 'utf8');
            assert.match(stepLog, /capture-har -> agent: The site asked us to slow down \(HTTP 429, wait 3 s\)\. The capture is paused until \d\d:\d\d local\. Don't use the browser; end your turn and wait; I'll tell you when to continue\./,
                `the pause message:\n${stepLog}`);
            assert.match(stepLog, /capture-har -> agent: The wait is over \(paused \d+ s\)\. Re-check the page, then continue where you left off\./);
            assert.match(stepLog, /the recorder told me to wait/);
            assert.match(stepLog, /agent ended .* \(exit 0\)/, 'it exited by itself');
            assert.ok(await waitFor(() => !pidAlive(agentPid), 15000), `the agent (pid ${agentPid}) is still running`);

            // THE OPERATOR -- and the PowerShell front door -- WAS TOLD.
            assert.match(recorderErr, /capture-har: Paused until \d\d:\d\d -- the site asked us to wait 3 s \(HTTP 429\)/);
            assert.match(recorderErr, /capture-har: Resumed after \d+ s; the agent has been told to carry on\./);
            assert.match(recorderErr, /this capture paused once \(\d+ s in all\)/);
            const line = recorderOut.trim().split('\n').map((l) => { try { return JSON.parse(l); } catch (e) { return null; } })
                .find((d) => d && d.capture === 'complete');
            assert.ok(line, `no machine-readable line:\n${recorderOut}`);
            assert.deepStrictEqual({ count: line.pauses.count, handOvers: line.pauses.handOvers }, { count: 1, handOvers: 0 });

            // The 429 is in the capture, and it was post-processed.
            const har = readJson(path.join(sessionDir, 'raw.har'));
            assert.ok(har && har.log.entries.some((e) =>
                e.request.url.includes('/api/limited') && e.response.status === 429),
            'the 429 the site answered must be in the capture -- it is part of its error taxonomy');
            assert.ok(finalSession.postProcess, 'a paused capture is post-processed as usual');
            assert.ok(await cdpAnswers(session.cdpEndpoint) && pidAlive(browserPid), 'the browser is left running');
        } finally {
            server.close();
            if (agentPid && pidAlive(agentPid)) killTree(agentPid);
            if (browserPid) killTree(browserPid);
            if (recorder && recorder.exitCode === null) recorder.kill();
        }
    });

test('I4: a bot check hands over -- the agent is told, and with nobody to take over it is told the end',
    async () => {
        // A "Just a moment..." interstitial on the site. The agent is told a
        // person is taking over. Nobody is at this terminal and there is no
        // tailnet (the tests point the recorder at a Tailscale that is not
        // there), so nobody can take over: the recording ends `handed-over`,
        // the agent is told the capture ended, and it exits by itself.
        const repo = makeRepo('ai-challenge');
        if (blockedOrSkipped('I4', integrationBlocker(repo))) return;

        const { server, uri } = await fixtureServer();
        const pruned = pathWithoutAgent();
        assert.ok(!agentOnPath(pruned), 'the agent must be reachable only through the stub');
        const childEnv = driverEnv({ PATH: pruned, HAR_STUB_AGENT_MODE: 'challenge' });

        let recorder = null;
        let browserPid = null;
        let agentPid = null;
        try {
            recorder = startStubDrivenCapture(repo, uri, childEnv);
            let recorderErr = '';
            recorder.stderr.on('data', (d) => { recorderErr += d; });
            recorder.stdout.on('data', () => {});
            const { sessionDir, session } =
                await waitForRecordingSession(repo, recorder, () => recorderErr);
            browserPid = session.browserPid;

            const ended = await waitFor(() => recorder.exitCode !== null, 180000);
            assert.ok(ended, `the recording never ended:\n${recorderErr}`);
            const finalSession = readJson(path.join(sessionDir, 'session.json'));
            agentPid = finalSession.agentPid;
            assert.strictEqual(finalSession.endedBy, 'handed-over',
                `the hand-over, with nobody to take over, is the ending:\n${recorderErr}`);
            assert.strictEqual(finalSession.handOver.why, 'challenge');
            const [h] = finalSession.pauses || [];
            assert.ok(h && h.kind === 'hand-over' && h.driver === 'agent' && h.outcome === 'ended' && h.phone === false,
                `the hand-over is recorded: ${JSON.stringify(finalSession.pauses)}`);

            assert.match(recorderErr, /capture-har: Handed over to you: the site put up a bot check/);
            assert.match(recorderErr, /No phone link: Tailscale is not installed on this PC\./);
            assert.match(recorderErr, /The agent has been told to wait/);
            assert.match(recorderErr, /Nothing here retries it, solves it or works around it/);

            const stepLog = fs.readFileSync(session.agentLogPath, 'utf8');
            assert.match(stepLog, /capture-har -> agent: The site showed a "prove you're human" check\. A person is taking over the browser\. Don't use the browser; end your turn and wait\./,
                `the hand-over message:\n${stepLog}`);
            assert.match(stepLog, /capture-har -> agent: The capture has ended; stop\./);
            assert.match(stepLog, /agent ended .* \(exit 0\)/, `it exited by itself:\n${stepLog}`);
            assert.ok(await waitFor(() => !pidAlive(agentPid), 15000), `the agent (pid ${agentPid}) is still running`);
            assert.ok(finalSession.postProcess, 'a handed-over capture is post-processed as usual');
            assert.ok(await cdpAnswers(session.cdpEndpoint) && pidAlive(browserPid),
                'a hand-over never touches the browser');
        } finally {
            server.close();
            if (agentPid && pidAlive(agentPid)) killTree(agentPid);
            if (browserPid) killTree(browserPid);
            if (recorder && recorder.exitCode === null) recorder.kill();
        }
    });

test('I5: THE OWNER\'S CASE -- a bot check, the live view on the phone, and the phone hands back',
    async () => {
        // Nobody at this terminal; the person is on their phone. The recorder
        // hands over, prints a private link, serves the live browser there, and
        // when the phone taps "I'm done, continue" the agent is told to carry
        // on and finishes. Tailscale is faked to answer LOOPBACK (the stub's
        // seam), so the view is real but no test touches a real tailnet.
        const repo = makeRepo('ai-phone');
        if (blockedOrSkipped('I5', integrationBlocker(repo))) return;

        const { server, uri } = await fixtureServer();
        const pruned = pathWithoutAgent();
        assert.ok(!agentOnPath(pruned), 'the agent must be reachable only through the stub');
        const childEnv = driverEnv({ PATH: pruned, HAR_STUB_AGENT_MODE: 'challenge',
            HAR_CAPTURE_TAILSCALE: 'tailscale',
            HAR_STUB_TAILSCALE_JSON: JSON.stringify({ BackendState: 'Running', Self: { TailscaleIPs: ['127.0.0.1'] } }) });

        let recorder = null;
        let browserPid = null;
        let agentPid = null;
        let events = null;
        try {
            recorder = startStubDrivenCapture(repo, uri, childEnv);
            let recorderErr = '';
            recorder.stderr.on('data', (d) => { recorderErr += d; });
            recorder.stdout.on('data', () => {});
            const { sessionDir, session } = await waitForRecordingSession(repo, recorder, () => recorderErr);
            browserPid = session.browserPid;

            // THE LINK, as the operator (or the AI session relaying it) reads it.
            const linked = await waitFor(() => /Open on your phone: (http:\/\/\S+)/.test(recorderErr), 120000);
            assert.ok(linked, `no phone link was given:\n${recorderErr}`);
            const link = /Open on your phone: (http:\/\/\S+)/.exec(recorderErr)[1];
            assert.match(link, /^http:\/\/127\.0\.0\.1:\d+\/m\/[A-Za-z0-9_-]{43}\/$/, 'the address Tailscale gave, and a long token');
            assert.match(recorderErr, /capture-har: Handed over to you: the site put up a bot check/);

            // THE PHONE: open the page (claiming it), watch the live frames.
            const page = await phoneRequest(link);
            assert.strictEqual(page.status, 200);
            assert.match(page.body, /I'm done, continue/);
            const cookie = String(page.headers['set-cookie']).split(';')[0];
            events = await phoneRequest(link + 'events', { stream: true, headers: { cookie } });
            let streamed = '';
            events.setEncoding('utf8');
            events.on('data', (d) => { streamed += d; });
            // ONE BROWSER AT A TIME: another one with the same link cannot watch.
            const other = String((await phoneRequest(link)).headers['set-cookie']).split(';')[0];
            const intruder = await phoneRequest(link + 'events', { stream: true, headers: { cookie: other } });
            assert.strictEqual(intruder.statusCode, 404, 'a second browser cannot watch while the phone does');
            intruder.resume();
            assert.ok(await waitFor(() => /event: frame\ndata: \{"d":"\/9j\//.test(streamed), 30000),
                'a live JPEG frame of the capture browser reaches the phone');
            assert.match(streamed, /event: status\ndata: .*Handed over to you/);

            // "I'm done, continue."
            const done = await phoneRequest(link + 'input', { method: 'POST', body: '{"t":"done"}',
                headers: { cookie, 'content-type': 'application/json', origin: new URL(link).origin } });
            assert.strictEqual(done.status, 204);

            const ended = await waitFor(() => recorder.exitCode !== null, 120000);
            assert.ok(ended, `the recording never ended after the phone handed back:\n${recorderErr}`);
            assert.match(streamed, /event: end\ndata: .*Handed back/, 'the phone is told the hand-over is over');

            const finalSession = readJson(path.join(sessionDir, 'session.json'));
            agentPid = finalSession.agentPid;
            assert.strictEqual(finalSession.endedBy, 'agent-finished', `the agent carried on and finished:\n${recorderErr}`);
            const [h] = finalSession.pauses || [];
            assert.ok(h && h.kind === 'hand-over' && h.resumedBy === 'phone' && h.outcome === 'continued' && h.phone === true,
                `the hand-over and who handed back: ${JSON.stringify(finalSession.pauses)}`);
            assert.ok(!JSON.stringify(finalSession).includes(new URL(link).pathname.split('/')[2]),
                'the token never goes into session.json -- the agent reads that file');
            assert.match(recorderErr, /capture-har: Resumed after \d+ (s|min)[^\n]* -- you handed the browser back on your phone; the agent has been told to carry on\./);

            const stepLog = fs.readFileSync(session.agentLogPath, 'utf8');
            assert.match(stepLog, /capture-har -> agent: The person has finished\. Re-check the page and continue where you left off\./);
            assert.match(stepLog, /the person has finished; re-checked the page and carried on/);
            assert.ok(!stepLog.includes('/m/'), 'the agent is never given the phone link');
            assert.match(stepLog, /agent ended .* \(exit 0\)/);
            assert.ok(await waitFor(() => !pidAlive(agentPid), 15000), 'the agent ended by itself');

            // The view is gone with the hand-over.
            const after = await phoneRequest(link).then(() => null, (e) => e);
            assert.ok(after, 'nothing listens once the hand-over is over');
            assert.ok(await cdpAnswers(session.cdpEndpoint) && pidAlive(browserPid), 'the browser is left running');
        } finally {
            if (events) events.destroy();
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
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-ai-pause tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All capture-ai-pause tests passed (${passed})` +
        (kit.skipped.length ? ` -- SKIPPED: ${kit.skipped.join(', ')}` : ''));
    process.exit(0);
})();
