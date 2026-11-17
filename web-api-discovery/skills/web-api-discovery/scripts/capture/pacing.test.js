#!/usr/bin/env node
// Behavior tests for pacing.js -- polite pacing, and what happens when a site
// throttles or blocks. Zero-dep; runs with `node pacing.test.js`.
//
// Nothing here waits on a clock: the pacer and the monitor take `now`, `sleep`
// and `rng`, and these cases hand them ones that record what was asked.
//
// FALSIFIERS (fail before this change, pass after):
//   P1  every wait the pacer takes is inside its range, and the range is used
//       (the low and the high random draw both land where they should)
//   P2  the first navigation does not wait; time already spent counts
//   P3  one navigation in flight: a second waits for the first to FINISH
//   P4  the per-minute ceiling holds a navigation until the minute rolls
//   P5  `--pace off` waits for nothing -- but a throttle pause still holds
//   P6  `--pace` parses, and refuses what it cannot parse instead of guessing
//   R1  Retry-After: delay-seconds, an HTTP-date, a date in the past, junk
//   R2  backoff is exponential with equal jitter: in [step/2, step], capped
//   C1  429 and 503+Retry-After are throttling; a bare 503 is not
//   C2  an HTML 403 from a CDN is a block; an application's JSON 403 is not
//   C3  challenge interstitials are recognised; a sign-in page that merely
//       embeds reCAPTCHA is not
//   C4  only the SITE counts: a tracker's 429 on another host is ignored
//   M1  a throttle pauses for Retry-After, a burst is one episode
//   M2  a throttle that persists hands over
//   M3  throttling through a pause hands over (the agent case)
//   M4  a block or a challenge hands over at once, and only once
//   M5  a Retry-After longer than the cap hands over rather than cutting it short
//   L1  the hand-over tells the human the browser is theirs, and that nothing
//       evades -- and a hand-driven run gets advice, not a hand-over
//   D1  capture-cdp.js: --throttle-ms 0 means zero, and --pace is validated

'use strict';

const assert = require('assert');
const path = require('path');

const pacing = require(path.join(__dirname, 'pacing.js'));

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

/** A clock that only moves when told to, and a sleep that moves it. */
function fakeTime(start = 1_700_000_000_000) {
    let t = start;
    const slept = [];
    return {
        now: () => t,
        advance: (ms) => { t += ms; },
        sleep: async (ms) => { slept.push(ms); t += ms; },
        slept
    };
}

const fixed = (v) => () => v;

// ---------------------------------------------------------------------------
// Pace
// ---------------------------------------------------------------------------

test('P1: every wait is inside its range, at both ends of the random draw', async () => {
    for (const draw of [0, 0.5, 0.999999]) {
        const time = fakeTime();
        const pacer = pacing.createPacer(pacing.DEFAULT_PACE, { now: time.now, sleep: time.sleep, rng: fixed(draw) });
        await pacer.navigate(async () => {});
        await pacer.navigate(async () => {});
        await pacer.act(async () => {});
        await pacer.act(async () => {});
        // A navigation gap, then an action gap after the page arrived (a person
        // looks before clicking), then one between the two actions.
        const [nav, ...acts] = time.slept;
        assert.strictEqual(time.slept.length, 3, `one navigation gap and two action gaps, got ${time.slept}`);
        assert.ok(nav >= 1500 && nav <= 4000, `navigation gap ${nav} outside 1500-4000`);
        for (const act of acts) assert.ok(act >= 300 && act <= 1200, `action gap ${act} outside 300-1200`);
        assert.strictEqual(nav, Math.round(1500 + draw * 2500), 'the draw decides where in the range');
    }
    // And randomized for real: many draws from Math.random spread over the range.
    const seen = new Set();
    for (let i = 0; i < 200; i++) {
        const v = pacing.randomBetween(1500, 4000);
        assert.ok(v >= 1500 && v <= 4000 && Number.isInteger(v), `randomBetween gave ${v}`);
        seen.add(Math.floor(v / 500));
    }
    assert.ok(seen.size >= 4, 'two hundred draws should cover most of the range, not sit on one value');
});

test('P2: the first navigation does not wait, and time already spent counts', async () => {
    const time = fakeTime();
    const pacer = pacing.createPacer(pacing.DEFAULT_PACE, { now: time.now, sleep: time.sleep, rng: fixed(0.5) });
    await pacer.navigate(async () => {});
    assert.deepStrictEqual(time.slept, [], 'nothing to be polite to yet');
    time.advance(10000);   // a person read the page for ten seconds
    await pacer.navigate(async () => {});
    assert.deepStrictEqual(time.slept, [], 'ten seconds on the page is already more than the gap');
    time.advance(1000);
    await pacer.navigate(async () => {});
    assert.deepStrictEqual(time.slept, [2750 - 1000], 'only the rest of the gap is waited');
});

test('P3: one navigation in flight -- a second waits for the first to finish', async () => {
    const time = fakeTime();
    const pacer = pacing.createPacer(pacing.PACE_OFF, { now: time.now, sleep: time.sleep });
    const order = [];
    let release;
    const first = pacer.navigate(() => new Promise((resolve) => { order.push('first:start'); release = resolve; }));
    const second = pacer.navigate(async () => { order.push('second:start'); });
    await new Promise((r) => setImmediate(r));
    assert.deepStrictEqual(order, ['first:start'], 'the second navigation started while the first was in flight');
    release();
    await first;
    await second;
    assert.deepStrictEqual(order, ['first:start', 'second:start']);
    // A failed navigation does not wedge the queue.
    await assert.rejects(pacer.navigate(async () => { throw new Error('net::ERR_ABORTED'); }));
    await pacer.navigate(async () => { order.push('after-failure'); });
    assert.strictEqual(order[order.length - 1], 'after-failure');
});

test('P4: the per-minute ceiling holds a navigation until the minute rolls over', async () => {
    const time = fakeTime();
    const pace = { navigation: [0, 0], action: [0, 0], perMinute: 3 };
    const pacer = pacing.createPacer(pace, { now: time.now, sleep: time.sleep });
    for (let i = 0; i < 3; i++) { await pacer.navigate(async () => {}); time.advance(1000); }
    assert.deepStrictEqual(time.slept, [], 'three a minute are allowed without waiting');
    await pacer.navigate(async () => {});
    assert.deepStrictEqual(time.slept, [60000 - 3000], 'the fourth waits until the first is a minute old');
});

test('P5: --pace off waits for nothing, and a throttle pause still holds', async () => {
    const time = fakeTime();
    const pacer = pacing.createPacer(pacing.PACE_OFF, { now: time.now, sleep: time.sleep });
    for (let i = 0; i < 20; i++) { await pacer.navigate(async () => {}); await pacer.act(async () => {}); }
    assert.deepStrictEqual(time.slept, [], 'pace off is every wait zero and no ceiling');
    pacer.pauseFor(30000);
    await pacer.navigate(async () => {});
    assert.deepStrictEqual(time.slept, [30000],
        'honouring a site\'s Retry-After is not pacing; turning pacing off must not turn it off');
    await pacer.act(async () => {});
    assert.deepStrictEqual(time.slept, [30000], 'the pause is over once waited');
});

test('P6: --pace parses, and refuses what it cannot parse', () => {
    assert.strictEqual(pacing.parsePace(undefined).pace, pacing.DEFAULT_PACE, 'absent is the default');
    assert.strictEqual(pacing.parsePace('human').pace, pacing.DEFAULT_PACE);
    assert.strictEqual(pacing.parsePace('off').pace, pacing.PACE_OFF);
    assert.deepStrictEqual(pacing.DEFAULT_PACE.navigation, [1500, 4000]);
    assert.deepStrictEqual(pacing.DEFAULT_PACE.action, [300, 1200]);
    const custom = pacing.parsePace('nav=2000-5000,per-minute=6').pace;
    assert.deepStrictEqual(custom.navigation, [2000, 5000]);
    assert.deepStrictEqual(custom.action, [300, 1200], 'what is not named keeps its default');
    assert.strictEqual(custom.perMinute, 6);
    assert.deepStrictEqual(pacing.parsePace('action=500').pace.action, [500, 500], 'a single value is a fixed wait');
    for (const bad of ['fast', 'nav=4000-1500', 'nav=-1-2', 'per-minute=many', 'nav=1.5-4']) {
        assert.ok(pacing.parsePace(bad).error, `'${bad}' must be refused, not guessed`);
    }
    assert.match(pacing.describePace(pacing.DEFAULT_PACE), /1\.5-4 s between navigations, 0\.3-1\.2 s between clicks/);
    assert.match(pacing.describePace(pacing.PACE_OFF), /^off/);
});

// ---------------------------------------------------------------------------
// Retry-After and backoff
// ---------------------------------------------------------------------------

test('R1: Retry-After as delay-seconds, an HTTP-date, a past date, and junk', () => {
    const now = Date.parse('Sun, 01 Nov 2026 12:00:00 GMT');
    assert.strictEqual(pacing.parseRetryAfter('120', now), 120000);
    assert.strictEqual(pacing.parseRetryAfter(' 0 ', now), 0);
    assert.strictEqual(pacing.parseRetryAfter('Sun, 01 Nov 2026 12:00:30 GMT', now), 30000);
    assert.strictEqual(pacing.parseRetryAfter('Sun, 01 Nov 2026 11:59:00 GMT', now), 0, 'a past date means now, never negative');
    for (const junk of [undefined, null, '', 'soon', '-5', '1.5', '+3']) {
        assert.strictEqual(pacing.parseRetryAfter(junk, now), null, `'${junk}' is neither form`);
    }
    // Header lookup is case-insensitive, in both shapes the recorder sees.
    assert.strictEqual(pacing.headerValue({ 'retry-after': '5' }, 'Retry-After'), '5');
    assert.strictEqual(pacing.headerValue([{ name: 'Retry-After', value: '7' }], 'retry-after'), '7');
});

test('R2: backoff is exponential with equal jitter, and capped', () => {
    for (const draw of [0, 0.5, 0.999]) {
        for (let attempt = 1; attempt <= 8; attempt++) {
            const step = Math.min(60000, 2000 * 2 ** (attempt - 1));
            const ms = pacing.backoffMs(attempt, { rng: fixed(draw) });
            assert.ok(ms >= step / 2 && ms <= step, `attempt ${attempt}: ${ms} outside [${step / 2}, ${step}]`);
        }
    }
    assert.ok(pacing.backoffMs(3, { rng: fixed(0) }) > pacing.backoffMs(1, { rng: fixed(0.999) }),
        'the step grows with the attempt: attempt 3 at its least exceeds attempt 1 at its most');
    assert.ok(pacing.backoffMs(30, { rng: fixed(1) }) <= 60000, 'capped');
});

// ---------------------------------------------------------------------------
// Classification
// ---------------------------------------------------------------------------

const onSite = (over) => Object.assign({ url: 'https://www.example.com/api/search', onSite: true, isDocument: false,
    headers: {} }, over);

test('C1: 429 and 503+Retry-After are throttling; a bare 503 is not', () => {
    const t429 = pacing.classifyResponse(onSite({ status: 429, headers: { 'retry-after': '30' } }));
    assert.strictEqual(t429.kind, 'throttled');
    assert.strictEqual(t429.retryAfterMs, 30000);
    assert.strictEqual(pacing.classifyResponse(onSite({ status: 429 })).retryAfterMs, null);
    assert.strictEqual(pacing.classifyResponse(onSite({ status: 503, headers: { 'Retry-After': '10' } })).kind, 'throttled');
    assert.strictEqual(pacing.classifyResponse(onSite({ status: 503, mimeType: 'application/json', body: '{}' })), null,
        'a 503 that names no wait is an outage, not a request to slow down');
    assert.strictEqual(pacing.classifyResponse(onSite({ status: 200, body: '{}' })), null);
});

test('C2: an HTML 403 from a CDN is a block; an application JSON 403 is not', () => {
    const edge = pacing.classifyResponse(onSite({
        status: 403, isDocument: true, mimeType: 'text/html', headers: { server: 'cloudflare', 'cf-ray': 'x' },
        body: Buffer.from('<html><body>Sorry, you have been blocked</body></html>')
    }));
    assert.strictEqual(edge.kind, 'blocked');
    const imperva = pacing.classifyResponse(onSite({
        status: 403, isDocument: true, mimeType: 'text/html',
        body: '<html><iframe src="/_Incapsula_Resource?CWUDNSAI=1"></iframe>Incapsula incident ID: 1</html>'
    }));
    assert.strictEqual(imperva.kind, 'blocked', 'Hertz\'s "Access denied, Error 15" is this shape');
    assert.strictEqual(pacing.classifyResponse(onSite({
        status: 403, mimeType: 'application/json', headers: { 'cf-ray': 'x' }, body: '{"error":"forbidden"}'
    })), null, 'the application refusing a request is not the edge refusing the visitor');
    assert.strictEqual(pacing.classifyResponse(onSite({
        status: 403, mimeType: 'text/html', body: '<html>You do not have access to this project</html>'
    })), null, 'an HTML 403 with no edge evidence is the application\'s');
    // EDGE HEADERS ALONE ARE WEAK: a CDN-fronted site puts them on everything.
    const edgeOnly = { mimeType: 'text/html', headers: { 'cf-ray': 'x', server: 'cloudflare' },
        body: '<html><h1>Not allowed</h1></html>' };
    assert.strictEqual(pacing.classifyResponse(onSite(Object.assign({ status: 403, isDocument: false }, edgeOnly))),
        null, 'a sub-resource\'s HTML 403 through a CDN, with no block signature, is not a block');
    assert.strictEqual(pacing.classifyResponse(onSite(Object.assign({ status: 503, isDocument: true }, edgeOnly))),
        null, 'a bare HTML 503 through a CDN is an outage, not a refusal');
    assert.strictEqual(pacing.classifyResponse(onSite({ status: 403, isDocument: false, mimeType: 'text/html',
        body: '<script src="https://geo.captcha-delivery.com/captcha/"></script>' })).kind, 'blocked',
    'with a known signature a sub-resource counts');
    assert.strictEqual(pacing.classifyResponse(onSite({
        status: 200, mimeType: 'text/html', headers: { 'cf-mitigated': 'challenge' }, body: '<html></html>'
    })).kind, 'challenge');
});

test('C3: challenge interstitials are recognised; a page that merely embeds reCAPTCHA is not', () => {
    const doc = (body) => pacing.classifyResponse(onSite({ status: 200, isDocument: true, mimeType: 'text/html', body }));
    assert.strictEqual(doc('<html><head><title>Just a moment...</title></head><script src="/cdn-cgi/challenge-platform/h/g/orchestrate"></script></html>').kind, 'challenge');
    assert.strictEqual(doc('<html><script src="https://geo.captcha-delivery.com/captcha/"></script></html>').kind, 'challenge');
    assert.strictEqual(doc('<html><div id="px-captcha"></div><p>Press &amp; Hold to confirm you are a human</p></html>').kind, 'challenge');
    assert.strictEqual(doc('<html><body>Our systems have detected unusual traffic from your computer network.</body></html>').kind, 'challenge');
    assert.strictEqual(doc('<html><title>Sign in</title><form><input type=password></form>' +
        '<script src="https://www.google.com/recaptcha/api.js?render=abc"></script></html>'), null,
    'reCAPTCHA v3 runs invisibly on ordinary sign-in pages; handing over on it would end every sign-in');
    const big = '<html><title>Just a moment...</title>' + 'x'.repeat(pacing.CHALLENGE_BODY_MAX + 10) + '</html>';
    assert.strictEqual(doc(big), null, 'a full-size page that happens to contain the words is a page, not an interstitial');
    assert.strictEqual(pacing.classifyResponse(onSite({ status: 200, isDocument: false, mimeType: 'text/html',
        body: '<title>Just a moment...</title>' })), null, 'a 200 is judged as an interstitial only as the main document');
});

test('C4: only the site counts -- a tracker\'s 429 on another host is ignored', () => {
    const tracker = { url: 'https://collector.tracker.example.net/t', status: 429, onSite: false, isDocument: false,
        headers: { 'retry-after': '60' } };
    assert.strictEqual(pacing.classifyResponse(tracker), null);
    assert.strictEqual(pacing.classifyResponse(Object.assign({}, tracker, { isDocument: true })).kind, 'throttled',
        'the page itself being throttled counts wherever it is hosted');
});

// ---------------------------------------------------------------------------
// The monitor
// ---------------------------------------------------------------------------

const throttle = (retryAfterMs = null) =>
    ({ kind: 'throttled', status: 429, url: 'https://www.example.com/api/search', retryAfterMs, signature: '429 Too Many Requests' });

test('M1: a throttle pauses for Retry-After, and a burst is one episode', () => {
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0.5) });
    const first = monitor.observe(throttle(30000));
    assert.strictEqual(first.action, 'pause');
    assert.strictEqual(first.waitMs, 30000, 'the wait the site named');
    time.advance(300);
    assert.strictEqual(monitor.observe(throttle(30000)).action, 'none',
        'three XHRs throttled on one page load are one episode, not persistence');
    const noHeader = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0.5) }).observe(throttle(null));
    assert.strictEqual(noHeader.action, 'pause');
    assert.ok(noHeader.waitMs >= 1000 && noHeader.waitMs <= 2000, `backoff ${noHeader.waitMs} for a first episode`);
});

test('M2: a throttle that persists hands over', () => {
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0.5), maxEpisodes: 3 });
    assert.strictEqual(monitor.observe(throttle(5000)).action, 'pause');
    time.advance(6000);
    assert.strictEqual(monitor.observe(throttle(5000)).action, 'pause', 'a second episode after the pause still pauses');
    time.advance(6000);
    const third = monitor.observe(throttle(5000));
    assert.strictEqual(third.action, 'hand-over');
    assert.strictEqual(third.why, 'persistent');
    assert.ok(monitor.handedOver(), 'and it is remembered');
    time.advance(6000);
    assert.strictEqual(monitor.observe(throttle(5000)).action, 'none', 'handed over once, not on every signal after');
    // Episodes age out of the window.
    const slow = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0.5), maxEpisodes: 2, windowMs: 60000 });
    assert.strictEqual(slow.observe(throttle(1000)).action, 'pause');
    time.advance(120000);
    assert.strictEqual(slow.observe(throttle(1000)).action, 'pause', 'an episode two minutes ago is outside a one-minute window');
});

test('M3: throttling THROUGH a pause hands over -- the driver did not wait', () => {
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0.5) });
    assert.strictEqual(monitor.observe(throttle(30000)).action, 'pause');
    time.advance(10000);   // well after the burst, well before the pause ends
    const next = monitor.observe(throttle(30000));
    assert.strictEqual(next.action, 'hand-over');
    assert.strictEqual(next.why, 'ignored-pause');
});

test('M4: a block or a challenge hands over at once, and only once', () => {
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now });
    const block = { kind: 'blocked', status: 403, url: 'https://www.example.com/', retryAfterMs: null, signature: '403 Imperva' };
    const first = monitor.observe(block);
    assert.strictEqual(first.action, 'hand-over');
    assert.strictEqual(first.why, 'blocked');
    assert.strictEqual(monitor.observe(Object.assign({}, block, { kind: 'challenge' })).action, 'none');
    assert.strictEqual(monitor.observe(null), null, 'no signal, no decision');
});

test('M5: a Retry-After longer than the cap hands over rather than being cut short', () => {
    const monitor = pacing.createThrottleMonitor({ now: fakeTime().now, retryAfterCapMs: 60000 });
    const d = monitor.observe(throttle(3600000));
    assert.strictEqual(d.action, 'hand-over');
    assert.strictEqual(d.why, 'retry-after-too-long');
});

test('L1: the hand-over hands the browser to the human, and evades nothing', () => {
    const decision = { action: 'hand-over', why: 'challenge',
        signal: { kind: 'challenge', status: 200, url: 'https://www.example.com/search', retryAfterMs: null,
            signature: 'PerimeterX "Press & Hold"' } };
    const agent = pacing.throttleLines(decision, 'agent').map(([, t]) => t).join('\n');
    assert.match(agent, /The agent has been stopped/);
    assert.match(agent, /THE BROWSER IS YOURS/);
    assert.match(agent, /still open, and\s+still recording/);
    assert.match(agent, /press ENTER here/);
    assert.match(agent, /Nothing here retries it, solves it or works around it/);
    assert.match(agent, /https:\/\/www\.example\.com\/search/, 'says where');
    assert.ok(pacing.throttleLines(decision, 'agent').every(([level]) => level === 'warn'),
        'a hand-over is never hidden behind -Verbose');
    const byHand = pacing.throttleLines(decision, 'you').map(([, t]) => t).join('\n');
    assert.doesNotMatch(byHand, /agent/i, 'nobody was stopped in a hand-driven run');
    assert.match(byHand, /Answer it yourself in the browser if you choose/);
    const pause = pacing.throttleLines({ action: 'pause', waitMs: 30000, signal: throttle(30000) }, 'you')
        .map(([, t]) => t).join('\n');
    assert.match(pause, /Wait at least 30 s/);
    assert.deepStrictEqual(pacing.throttleLines({ action: 'none', signal: throttle() }), []);
    // Never the query string: that is where sites put search terms and emails.
    const queried = Object.assign({}, decision, { signal: Object.assign({}, decision.signal,
        { url: 'https://www.example.com/search?email=someone%40example.com&q=DCA' }) });
    const said = pacing.throttleLines(queried, 'agent').map(([, t]) => t).join('\n');
    assert.match(said, /https:\/\/www\.example\.com\/search\)/);
    assert.doesNotMatch(said, /email|q=DCA/, `the query reached the terminal:\n${said}`);
    // An -AI run refused before the agent was launched says so, rather than
    // claiming to have stopped something that never ran.
    const unstarted = pacing.throttleLines(decision, 'agent-unstarted').map(([, t]) => t).join('\n');
    assert.match(unstarted, /The agent was not started, and will not be/);
    assert.doesNotMatch(unstarted, /has been stopped/);
    // Headless: nobody to hand the browser to, so the message must not offer it.
    const headless = pacing.throttleLines(decision, 'script',
        { tool: 'capture-cdp', noHuman: 'Nobody can take over a headless browser, so this run ends (exit 3).' })
        .map(([, t]) => t).join('\n');
    assert.match(headless, /^capture-cdp: /);
    assert.match(headless, /Nobody can take over a headless browser/);
    assert.doesNotMatch(headless, /THE BROWSER IS YOURS/);
});

// ---------------------------------------------------------------------------
// capture-cdp.js -- the other scripted navigation
// ---------------------------------------------------------------------------

test('D1: capture-cdp takes --throttle-ms 0 as zero and refuses a --pace it cannot read', () => {
    const { spawnSync } = require('child_process');
    const cdp = path.join(__dirname, 'capture-cdp.js');
    const run = (extra) => spawnSync(process.execPath, [cdp, '--url', 'https://example.invalid',
        '--out', 'never-written.har', '--validate-only', ...extra], { encoding: 'utf8', windowsHide: true });
    // `Number(x) || 1000` read an explicit 0 as "the default": there was no way
    // to turn the per-request delay off.
    const zero = run(['--throttle-ms', '0', '--pace', 'off']);
    assert.strictEqual(zero.status, 0, `--throttle-ms 0 --pace off must be accepted:\n${zero.stderr}`);
    for (const bad of [['--pace', 'fast'], ['--throttle-ms', '-5'], ['--throttle-ms', 'soon']]) {
        const r = run(bad);
        assert.strictEqual(r.status, 1, `${bad.join(' ')} must be refused, got ${r.status}`);
        assert.match(r.stderr, /--pace|--throttle-ms/);
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
    if (failures.length) {
        process.stderr.write(`\n${failures.length} of ${queued.length} pacing tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All pacing tests passed (${passed})`);
})();
