#!/usr/bin/env node
// End-to-end tests for SIGNING IN FROM THE PHONE under -AI (#155): a real
// capture, a real (headless) Chrome, the stream-json stub agent, and a local
// fixture site whose start page is a sign-in form. Zero-dep; runs with
// `node capture-ai-sign-in.test.js`.
//
// The pieces are pinned on their own in mirror-fields.test.js (which boxes,
// where), mirror.test.js (typing as key presses, final values only),
// sign-in.test.js (handToPerson, addTyped) and pacing.test.js (the words).
// This is them together, as the owner meets them.
//
// FALSIFIERS (fail before this change, pass after):
//   S1  THE OWNER'S CASE: nobody at the terminal, the start page is a sign-in.
//       The run does NOT end with exit 8: the sign-in is handed over with a
//       private phone link, before the agent starts. The phone is told the
//       page's two boxes (never the password's value), types into them, and
//       submits; the site signs in. The session is saved, the person's
//       values are scrubbed from the capture (the site's field names say
//       nothing, so only the typed values can find them), and the agent --
//       never given the link or the values -- starts after the hand-back and
//       finishes.
//   S2  with no phone view (no tailnet), the unattended ending is what it was:
//       exit 8, sign-in-needed -- and the line says why there was no phone link

'use strict';

const assert = require('assert');
const fs = require('fs');
const http = require('http');
const path = require('path');

const { createE2eKit } = require(path.join(__dirname, 'capture-ai-e2e.test-support.js'));

const kit = createE2eKit('capture-ai-sign-in-');
const {
    makeRepo, pathWithoutAgent, agentOnPath, driverEnv, integrationBlocker, blockedOrSkipped, readJson,
    waitFor, pidAlive, killTree, startStubDrivenCapture, waitForRecordingSession, phoneRequest
} = kit;

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

// What the person types on the phone. Unlike anything the scrub could know by
// name: the site calls its fields `u` and `k`.
const PHONE_USER = 'phone-owner-155@example.invalid';
const PHONE_PASSWORD = 'Pw-typed-on-the-phone-155';

/** A site whose start page is a sign-in form; signed in, it is the stub agent's search page. */
async function signInSite() {
    const posts = [];
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (d) => { body += d; });
        req.on('end', () => {
            const html = (inner, status = 200, headers = {}) => {
                res.writeHead(status, Object.assign({ 'content-type': 'text/html' }, headers));
                res.end(`<!doctype html><html><body style="margin:0">${inner}</body></html>`);
            };
            if (req.url.startsWith('/api/search')) {
                res.writeHead(200, { 'content-type': 'application/json' });
                res.end(JSON.stringify({ results: [{ id: 1, name: 'fixture rate' }] }));
                return;
            }
            if (req.method === 'POST' && req.url === '/session') {
                const form = new URLSearchParams(body);
                posts.push(form);
                if (form.get('u') === PHONE_USER && form.get('k') === PHONE_PASSWORD) {
                    html('', 303, { location: '/home', 'set-cookie': 'sid=signed-in-155; Path=/; HttpOnly' });
                    return;
                }
            }
            if (req.url === '/home') {
                html('<h1>ai drive fixture</h1>' +
                    '<button id="search" onclick="fetch(\'/api/search?q=one-way\')">search</button>');
                return;
            }
            html('<form method="post" action="/session">' +
                '<input name="u" type="email" autocomplete="username" ' +
                '  style="position:absolute;left:40px;top:40px;width:320px;height:40px">' +
                '<input name="k" type="password" style="position:absolute;left:40px;top:110px;width:320px;height:40px">' +
                '<button style="position:absolute;left:40px;top:180px">Sign in</button></form>');
        });
    });
    await new Promise((r) => server.listen(0, '127.0.0.1', r));
    return { server, posts, uri: `http://127.0.0.1:${server.address().port}/signin` };
}

/** Server-Sent Events from the phone view, as the phone page reads them. */
function sseReader(res) {
    const events = [];
    let buf = '';
    res.setEncoding('utf8');
    res.on('data', (d) => {
        buf += d;
        let i;
        while ((i = buf.indexOf('\n\n')) >= 0) {
            const block = buf.slice(0, i);
            buf = buf.slice(i + 2);
            const ev = /^event: (.+)$/m.exec(block);
            const data = /^data: (.+)$/m.exec(block);
            if (ev && data) events.push({ event: ev[1], data: JSON.parse(data[1]), raw: block });
        }
    });
    return events;
}

/**
 * When the phone's typing did not reach the site: what the capture browser's
 * sign-in page actually holds (read over the recorder's CDP endpoint, as the
 * agent attaches), what the site received, what the phone was told, and the
 * recorder's last words. Never a value -- lengths only.
 */
async function diagnose(session, posts, events, recorderErr) {
    const lines = [];
    try {
        const capture = require(path.join(__dirname, 'capture-har.js'));
        const resolved = capture.resolvePlaywright({ cwd: process.cwd(), env: process.env });
        const browser = await require(resolved.path).chromium.connectOverCDP(session.cdpEndpoint);
        try {
            for (const p of browser.contexts()[0].pages()) {
                const state = await p.evaluate(() => {
                    const u = document.querySelector('input[name=u]');
                    const k = document.querySelector('input[name=k]');
                    const a = document.activeElement;
                    return { url: location.pathname, u: u ? u.value.length : null, k: k ? k.value.length : null,
                        uValid: u ? u.checkValidity() : null, active: a ? (a.name || a.tagName) : null };
                }).catch((e) => ({ error: e.message }));
                lines.push(`page: ${JSON.stringify(state)}`);
            }
        } finally {
            await browser.close().catch(() => {});
        }
    } catch (e) {
        lines.push(`could not read the page: ${e.message}`);
    }
    lines.push(`site posts: ${posts.length} (${posts.map((p) => `u=${(p.get('u') || '').length} k=${(p.get('k') || '').length}`).join(', ')})`);
    const fields = events.filter((e) => e.event === 'fields');
    lines.push(`phone events: ${events.length}; fields generations: ${fields.map((e) => `${e.data.g}:${e.data.f.length}`).join(' ')}`);
    lines.push(`recorder (last 1500 chars):\n${recorderErr.slice(-1500)}`);
    return lines.join('\n');
}

test('S1: THE OWNER\'S CASE -- the start page is a sign-in; the person signs in on the phone; the agent then drives',
    async () => {
        const repo = makeRepo('ai-sign-in-phone');
        if (blockedOrSkipped('S1', integrationBlocker(repo))) return;
        // The saved session goes where git ignores it, as a real repo's does.
        fs.appendFileSync(path.join(repo, '.gitignore'), '.har-storage-state.json\n');

        const { server, posts, uri } = await signInSite();
        const pruned = pathWithoutAgent();
        assert.ok(!agentOnPath(pruned), 'the agent must be reachable only through the stub');
        const childEnv = driverEnv({ PATH: pruned, HAR_CAPTURE_TAILSCALE: 'tailscale',
            HAR_STUB_TAILSCALE_JSON: JSON.stringify({ BackendState: 'Running', Self: { TailscaleIPs: ['127.0.0.1'] } }) });

        let recorder = null;
        let browserPid = null;
        let agentPid = null;
        let stream = null;
        try {
            recorder = startStubDrivenCapture(repo, uri, childEnv);
            let recorderErr = '';
            recorder.stderr.on('data', (d) => { recorderErr += d; });
            recorder.stdout.on('data', () => {});
            const { sessionDir, session } = await waitForRecordingSession(repo, recorder, () => recorderErr);
            browserPid = session.browserPid;

            // HANDED OVER, NOT ENDED: the line the owner (or the AI session
            // relaying it) reads.
            const linked = await waitFor(() => /Open on your phone: (http:\/\/\S+)/.test(recorderErr) ||
                recorder.exitCode !== null, 120000);
            assert.ok(linked && recorder.exitCode === null, `no hand-over, or the run ended:\n${recorderErr}`);
            assert.match(recorderErr, /capture-har: Handed over to you: the site asks you to sign in\. Open on your phone:/);
            assert.match(recorderErr, /The agent has not been started/);
            assert.doesNotMatch(recorderErr, /nobody is at this terminal to start one/, 'not the exit-8 ending');
            const link = /Open on your phone: (http:\/\/\S+)/.exec(recorderErr)[1];

            // THE PHONE: claim the view, watch it.
            const page = await phoneRequest(link);
            assert.strictEqual(page.status, 200);
            const cookie = String(page.headers['set-cookie']).split(';')[0];
            stream = await phoneRequest(link + 'events', { stream: true, headers: { cookie } });
            const events = sseReader(stream);
            const input = (doc) => phoneRequest(link + 'input', { method: 'POST', body: JSON.stringify(doc),
                headers: { cookie, 'content-type': 'application/json', origin: new URL(link).origin } });
            // THE PAGE'S TWO BOXES, told to the phone.
            assert.ok(await waitFor(() => events.some((e) => e.event === 'fields' && e.data.f.length === 2) &&
                events.some((e) => e.event === 'frame'), 30000), `the phone was not told the boxes: ${JSON.stringify(events.map((e) => e.event))}`);
            const { g, f } = events.filter((e) => e.event === 'fields').pop().data;
            assert.deepStrictEqual(f.map((b) => [b.k, b.t, b.ac]),
                [['username', 'email', 'username'], ['password', 'password', 'current-password']]);

            // TYPED INTO THEM, as the phone page does: focus, the box's text, Enter.
            for (const doc of [{ t: 'focus', g, i: 0 }, { t: 'value', v: PHONE_USER },
                { t: 'focus', g, i: 1 }, { t: 'value', v: PHONE_PASSWORD }, { t: 'key', k: 'Enter' }]) {
                assert.strictEqual((await input(doc)).status, 204, JSON.stringify(doc.t));
            }
            if (!await waitFor(() => posts.some((p) => p.get('k') === PHONE_PASSWORD), 60000)) {
                assert.fail(`the site did not receive the sign-in typed on the phone.\n${await diagnose(session, posts, events, recorderErr)}`);
            }
            assert.ok(await waitFor(() => /signed in -- session saved to/.test(recorderErr), 30000),
                `the session was not saved:\n${recorderErr}`);

            // "I'm done, continue": the agent starts, and drives.
            assert.strictEqual((await input({ t: 'done' })).status, 204);
            const ended = await waitFor(() => recorder.exitCode !== null, 180000);
            assert.ok(ended, `the recording never ended:\n${recorderErr}`);

            const finalSession = readJson(path.join(sessionDir, 'session.json'));
            agentPid = finalSession.agentPid;
            assert.strictEqual(finalSession.endedBy, 'agent-finished', `the agent started and finished:\n${recorderErr}`);
            assert.notStrictEqual(recorder.exitCode, 8, 'not sign-in-needed');
            assert.deepStrictEqual(finalSession.signIn, { source: 'person', signedIn: true, needed: true });
            const [h] = finalSession.pauses || [];
            assert.ok(h && h.kind === 'hand-over' && h.why === 'sign-in' && h.resumedBy === 'phone' && h.phone === true,
                `the sign-in hand-over: ${JSON.stringify(finalSession.pauses)}`);
            assert.ok(!JSON.stringify(finalSession).includes(PHONE_PASSWORD), 'session.json -- which the agent reads -- holds no password');

            // THE SESSION IS SAVED, the password is not.
            const state = fs.readFileSync(path.join(repo, '.har-storage-state.json'), 'utf8');
            assert.match(state, /signed-in-155/, 'the site\'s session cookie');
            assert.ok(!state.includes(PHONE_PASSWORD));

            // THE SCRUB: the raw capture holds what was typed (the site really
            // got it); the scrubbed one does not -- under field names `u` and
            // `k`, only the typed values themselves could have found it.
            const raw = fs.readFileSync(path.join(sessionDir, 'raw.har'), 'utf8');
            assert.ok(raw.includes(encodeURIComponent(PHONE_PASSWORD)) || raw.includes(PHONE_PASSWORD),
                'the raw capture recorded the sign-in post');
            const scrubbed = fs.readFileSync(path.join(sessionDir, 'scrubbed.har'), 'utf8');
            for (const v of [PHONE_PASSWORD, encodeURIComponent(PHONE_PASSWORD), PHONE_USER, encodeURIComponent(PHONE_USER)]) {
                assert.ok(!scrubbed.includes(v), `the scrubbed capture still holds ${v.slice(0, 6)}...`);
            }

            // THE AGENT was never given the link or the values, and started after.
            const stepLog = fs.readFileSync(session.agentLogPath, 'utf8');
            assert.ok(!stepLog.includes('/m/'), 'the agent is never given the phone link');
            assert.ok(!stepLog.includes(PHONE_PASSWORD) && !stepLog.includes(PHONE_USER));
            assert.match(stepLog, /clicked #search and the search response arrived/, 'it drove the signed-in page');
            assert.ok(await waitFor(() => !pidAlive(agentPid), 15000), 'the agent ended by itself');
        } finally {
            if (stream) stream.destroy();
            server.close();
            if (agentPid && pidAlive(agentPid)) killTree(agentPid);
            if (browserPid) killTree(browserPid);
            if (recorder && recorder.exitCode === null) recorder.kill();
        }
    });

test('S2: no phone view -- a sign-in on the start page ends the run as before (exit 8), and says why there is no link',
    async () => {
        const repo = makeRepo('ai-sign-in-no-phone');
        if (blockedOrSkipped('S2', integrationBlocker(repo))) return;
        const { server, uri } = await signInSite();
        const pruned = pathWithoutAgent();
        // driverEnv points the Tailscale CLI at one that is not there.
        const childEnv = driverEnv({ PATH: pruned });
        let recorder = null;
        let browserPid = null;
        try {
            recorder = startStubDrivenCapture(repo, uri, childEnv);
            let recorderErr = '';
            recorder.stderr.on('data', (d) => { recorderErr += d; });
            recorder.stdout.on('data', () => {});
            const { sessionDir, session } = await waitForRecordingSession(repo, recorder, () => recorderErr);
            browserPid = session.browserPid;
            const ended = await waitFor(() => recorder.exitCode !== null, 120000);
            assert.ok(ended, `the run did not end:\n${recorderErr}`);
            assert.strictEqual(recorder.exitCode, 8, `sign-in-needed:\n${recorderErr}`);
            assert.strictEqual(readJson(path.join(sessionDir, 'session.json')).endedBy, 'sign-in-needed');
            assert.match(recorderErr, /needs a sign-in, and nobody is at this terminal to start one/);
            assert.match(recorderErr, /No phone link: Tailscale/, 'why nobody could take it over from a phone');
        } finally {
            server.close();
            if (browserPid) killTree(browserPid);
            if (recorder && recorder.exitCode === null) recorder.kill();
        }
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
    kit.cleanup();
    if (failures.length) {
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-ai-sign-in tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All capture-ai-sign-in tests passed (${passed})` +
        (kit.skipped.length ? ` -- SKIPPED: ${kit.skipped.join(', ')}` : ''));
    process.exit(0);
})();
