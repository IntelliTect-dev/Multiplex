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
//   R2  the backoff schedule: about 1, 2, 4, 8, then 15 minutes, never above
//       15, jittered downwards only -- and its numbers are named constants
//   C1  429 and 503+Retry-After are throttling; a bare 503 is not
//   C2  an HTML 403 from a CDN is a block; an application's JSON 403 is not
//   C3  challenge interstitials are recognised; a sign-in page that merely
//       embeds reCAPTCHA is not
//   C4  only the SITE counts: a tracker's 429 on another host is ignored
//   C5  a page load is progress; a challenge that answered 200 is not
//   M1  a throttle pauses for EXACTLY the Retry-After it names -- two hours is
//       a two-hour pause, not a hand-over -- and a burst is one pause
//   M1b the HTTP-date form, end to end: the pause ends at that date
//   M2  a throttle that names no wait backs off and resumes, and hands over
//       only after an hour with no page loading in between
//   M3  a throttle that arrives DURING a pause is that pause (the browser holds
//       the site's requests, so it was in flight) -- never a hand-over
//   M4  a block or a challenge hands over at once, once -- and again after the
//       person hands back
//   M5  only a Retry-After beyond the 24 h ceiling hands over
//   L1  the hand-over says who has the browser, gives the phone link, and that
//       nothing evades -- and a hand-driven run gets advice, not a hand-over
//   L2  the pause and resume lines say until when, for how long, and why
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

test('R2: the backoff schedule is about 1, 2, 4, 8, then 15 minutes, and never above 15', () => {
    const MIN = 60000;
    // THE NUMBERS ARE CONSTANTS, pinned here, so a change to them is a change
    // somebody made on purpose and a reviewer sees.
    assert.strictEqual(pacing.BACKOFF_BASE_MS, 1 * MIN);
    assert.strictEqual(pacing.BACKOFF_MAX_MS, 15 * MIN);
    assert.strictEqual(pacing.BACKOFF_JITTER, 0.2);
    assert.strictEqual(pacing.THROTTLE_GIVE_UP_MS, 60 * MIN);
    assert.strictEqual(pacing.RETRY_AFTER_CEILING_MS, 24 * 60 * MIN);
    const steps = [1, 2, 4, 8, 15, 15, 15, 15].map((m) => m * MIN);
    steps.forEach((step, i) => {
        const attempt = i + 1;
        assert.strictEqual(pacing.backoffMs(attempt, { rng: fixed(0) }), step,
            `attempt ${attempt} with no jitter is the step itself`);
        for (const draw of [0.25, 0.5, 0.999999]) {
            const ms = pacing.backoffMs(attempt, { rng: fixed(draw) });
            assert.ok(ms >= step * 0.8 && ms <= step, `attempt ${attempt}: ${ms} outside [0.8 x ${step}, ${step}]`);
        }
    });
    assert.ok(pacing.backoffMs(40, { rng: fixed(0) }) <= 15 * MIN, 'the cap is hard, however long it goes on');
    // Jitter is real: draws spread the wait rather than every client waking together.
    const seen = new Set();
    for (let i = 0; i < 50; i++) seen.add(pacing.backoffMs(1));
    assert.ok(seen.size > 10, 'the jitter has to vary the wait');
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

test('C5: a page that loads is progress; a challenge that answered 200 is not', () => {
    const page = (over) => Object.assign({ url: 'https://www.example.com/', onSite: true, isDocument: true,
        mimeType: 'text/html', headers: {}, status: 200, body: '<html><title>Results</title></html>' }, over);
    assert.strictEqual(pacing.isPageLoad(page()), true);
    assert.strictEqual(pacing.isPageLoad(page({ status: 302 })), true, 'a redirect is the site answering');
    assert.strictEqual(pacing.isPageLoad(page({ status: 429 })), false);
    assert.strictEqual(pacing.isPageLoad(page({ isDocument: false })), false,
        'an image or an analytics call succeeding is not a page of the site loading');
    assert.strictEqual(pacing.isPageLoad(page({ body: '<html><title>Just a moment...</title></html>' })), false,
        'an interstitial standing in for the page is not the page');
});

// ---------------------------------------------------------------------------
// The monitor
// ---------------------------------------------------------------------------

const throttle = (retryAfterMs = null) =>
    ({ kind: 'throttled', status: 429, url: 'https://www.example.com/api/search', retryAfterMs, signature: '429 Too Many Requests' });

const MIN = 60000;

test('M1: a throttle pauses for EXACTLY the Retry-After it names, however long, and a burst is one pause', () => {
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0.5) });
    const first = monitor.observe(throttle(30000));
    assert.strictEqual(first.action, 'pause');
    assert.strictEqual(first.waitMs, 30000, 'the wait the site named');
    assert.strictEqual(first.until, time.now() + 30000, 'and when it ends');
    assert.strictEqual(first.named, true);
    time.advance(300);
    assert.strictEqual(monitor.observe(throttle(30000)).action, 'none',
        'three XHRs throttled on one page load are one pause, not three');
    // THE OWNER'S CASE: "just pause" means pause until the time is up. A site
    // asking for two hours used to be handed over at the 60 s cap.
    const long = pacing.createThrottleMonitor({ now: time.now }).observe(throttle(2 * 60 * MIN));
    assert.strictEqual(long.action, 'pause', `a two-hour Retry-After must pause, not hand over: ${JSON.stringify(long)}`);
    assert.strictEqual(long.waitMs, 2 * 60 * MIN, 'in full -- not cut short to a cap');
    // A longer wait named inside the pause lengthens it, and says so.
    time.advance(1000);
    const longer = monitor.observe(throttle(5 * MIN));
    assert.strictEqual(longer.action, 'pause');
    assert.strictEqual(longer.extended, true);
    assert.strictEqual(longer.until, time.now() + 5 * MIN);
});

test('M1b: the HTTP-date form of Retry-After, end to end -- the pause ends at that date', () => {
    const at = Date.parse('Sun, 01 Nov 2026 12:00:00 GMT');
    const monitor = pacing.createThrottleMonitor({ now: () => at });
    const signal = pacing.classifyResponse({
        url: 'https://www.example.com/api/search', onSite: true, isDocument: false, status: 503,
        headers: { 'Retry-After': 'Sun, 01 Nov 2026 12:45:00 GMT' }, mimeType: 'application/json', body: '{}'
    }, at);
    assert.strictEqual(signal.kind, 'throttled', '503 with a dated Retry-After is the site asking for a pause');
    const d = monitor.observe(signal);
    assert.strictEqual(d.action, 'pause');
    assert.strictEqual(d.waitMs, 45 * MIN);
    assert.strictEqual(d.until, Date.parse('Sun, 01 Nov 2026 12:45:00 GMT'));
});

test('M2: no wait named -- back off 1, 2, 4, 8, 15 min and resume; hand over only after an hour without a page', () => {
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0) });
    const waits = [];
    let d;
    for (;;) {
        d = monitor.observe(throttle(null));
        if (d.action !== 'pause') break;
        waits.push(d.waitMs / MIN);
        time.advance(d.waitMs);   // the pause runs out, the drive resumes, and is throttled again
    }
    assert.deepStrictEqual(waits, [1, 2, 4, 8, 15, 15, 15],
        'every wait is a pause that resumes by itself; the hand-over comes only once the hour is up');
    assert.strictEqual(d.action, 'hand-over');
    assert.strictEqual(d.why, 'persistent');
    assert.strictEqual(waits.reduce((a, b) => a + b), 60, 'the hand-over comes an hour into the throttling');

    // A PAGE LOADING IN BETWEEN STARTS THE COUNT AGAIN: the pause worked.
    const t2 = fakeTime();
    const m2 = pacing.createThrottleMonitor({ now: t2.now, rng: fixed(0) });
    for (let i = 0; i < 6; i++) t2.advance(m2.observe(throttle(null)).waitMs);   // 45 min of throttling
    m2.progress();
    const again = [];
    for (let i = 0; i < 6; i++) {
        const d2 = m2.observe(throttle(null));
        assert.strictEqual(d2.action, 'pause', `pause ${i + 1} after the page loaded must still be a pause`);
        again.push(d2.waitMs / MIN);
        t2.advance(d2.waitMs);
    }
    assert.deepStrictEqual(again, [1, 2, 4, 8, 15, 15], 'the backoff starts over after a page loaded');
    assert.strictEqual(m2.handedOver(), null, '90 minutes in all, but never an hour without a page: no hand-over');
});

test('M3: a throttle DURING a pause is that pause -- never a hand-over', () => {
    // The browser holds the site's requests while a pause runs, so a 429 that
    // arrives inside one was in flight when it began. It used to hand over as
    // "the driver ignored the pause"; now the driver cannot reach the site.
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0.5) });
    assert.strictEqual(monitor.observe(throttle(30000)).action, 'pause');
    time.advance(10000);   // well after the burst, well before the pause ends
    assert.strictEqual(monitor.observe(throttle(20000)).action, 'none', 'the same end, said again');
    assert.strictEqual(monitor.observe(throttle(null)).action, 'none', 'nor one that names no wait');
    // Naming a LATER end is the site asking for longer: the pause grows.
    const later = monitor.observe(throttle(30000));
    assert.strictEqual(later.action, 'pause');
    assert.strictEqual(later.extended, true);
    assert.strictEqual(monitor.handedOver(), null, 'never a hand-over');
});

test('M4: a block or a challenge hands over at once, once -- and again after the person hands back', () => {
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now });
    const block = { kind: 'blocked', status: 403, url: 'https://www.example.com/', retryAfterMs: null, signature: '403 Imperva' };
    const first = monitor.observe(block);
    assert.strictEqual(first.action, 'hand-over');
    assert.strictEqual(first.why, 'blocked');
    assert.strictEqual(monitor.observe(Object.assign({}, block, { kind: 'challenge' })).action, 'none',
        'one hand-over at a time');
    assert.strictEqual(monitor.observe(null), null, 'no signal, no decision');
    // The person answered the check and handed back. A second check later in
    // the same recording is a second hand-over, not silence.
    monitor.handedBack();
    const again = monitor.observe(Object.assign({}, block, { kind: 'challenge' }));
    assert.strictEqual(again.action, 'hand-over');
    assert.strictEqual(again.why, 'challenge');
    // A challenge hands over even in the middle of a throttle pause.
    const m2 = pacing.createThrottleMonitor({ now: time.now });
    m2.observe(throttle(60000));
    assert.strictEqual(m2.observe(Object.assign({}, block, { kind: 'challenge' })).action, 'hand-over');
});

test('M5: only a Retry-After beyond the 24-hour ceiling hands over', () => {
    const monitor = pacing.createThrottleMonitor({ now: fakeTime().now });
    const day = pacing.createThrottleMonitor({ now: fakeTime().now }).observe(throttle(pacing.RETRY_AFTER_CEILING_MS));
    assert.strictEqual(day.action, 'pause', 'a day is still a pause');
    const d = monitor.observe(throttle(pacing.RETRY_AFTER_CEILING_MS + 1000));
    assert.strictEqual(d.action, 'hand-over');
    assert.strictEqual(d.why, 'retry-after-too-long');
});

test('L1: the hand-over says who has the browser, gives the phone link, and evades nothing', () => {
    const decision = { action: 'hand-over', why: 'challenge',
        signal: { kind: 'challenge', status: 200, url: 'https://www.example.com/search', retryAfterMs: null,
            signature: 'PerimeterX "Press & Hold"' } };
    const link = 'http://192.0.2.10:41234/m/abc/';
    const agent = pacing.throttleLines(decision, 'agent', { link }).map(([, t]) => t).join('\n');
    assert.match(agent, /^capture-har: Handed over to you: the site put up a bot check/);
    assert.ok(agent.includes(`Open on your phone: ${link}`), agent);
    assert.match(agent, /The agent has been told to wait/);
    assert.doesNotMatch(agent, /stopped|killed/, 'nothing is stopped: the agent is told, not ended');
    assert.match(agent, /THE BROWSER IS YOURS/);
    assert.match(agent, /still open, and\s+still recording/);
    assert.match(agent, /press ENTER here/);
    assert.match(agent, /Nothing here retries it, solves it or works around it/);
    assert.match(agent, /https:\/\/www\.example\.com\/search/, 'says where');
    assert.ok(pacing.throttleLines(decision, 'agent').every(([level]) => level === 'warn'),
        'a hand-over is never hidden behind -Verbose');
    // No Tailscale: no link, and the reason instead.
    const noPhone = pacing.throttleLines(decision, 'agent', { noLink: 'No phone link: Tailscale is not running.' })
        .map(([, t]) => t).join('\n');
    assert.doesNotMatch(noPhone, /Open on your phone/);
    assert.match(noPhone, /No phone link: Tailscale is not running/);
    const byHand = pacing.throttleLines(decision, 'you').map(([, t]) => t).join('\n');
    assert.doesNotMatch(byHand, /agent/i, 'nobody was told anything in a hand-driven run');
    assert.match(byHand, /Answer it yourself in the browser if you choose/);
    assert.deepStrictEqual(pacing.throttleLines({ action: 'none', signal: throttle() }), []);
    // Never the query string: that is where sites put search terms and emails.
    const queried = Object.assign({}, decision, { signal: Object.assign({}, decision.signal,
        { url: 'https://www.example.com/search?email=someone%40example.com&q=DCA' }) });
    const said = pacing.throttleLines(queried, 'agent').map(([, t]) => t).join('\n');
    assert.match(said, /https:\/\/www\.example\.com\/search\./);
    assert.doesNotMatch(said, /email|q=DCA/, `the query reached the terminal:\n${said}`);
    // An -AI run refused before the agent was launched says so.
    const unstarted = pacing.throttleLines(decision, 'agent-unstarted').map(([, t]) => t).join('\n');
    assert.match(unstarted, /The agent has not been started/);
    // Headless: nobody to hand the browser to, so the message must not offer it.
    const headless = pacing.throttleLines(decision, 'script',
        { tool: 'capture-cdp', noHuman: 'Nobody can take over a headless browser, so this run ends (exit 3).' })
        .map(([, t]) => t).join('\n');
    assert.match(headless, /^capture-cdp: /);
    assert.match(headless, /Nobody can take over a headless browser/);
    assert.doesNotMatch(headless, /THE BROWSER IS YOURS/);
});

test('L2: the pause and resume lines say until when, for how long, and why', () => {
    const start = Date.parse('2026-11-17T10:37:00Z');
    const clock = (ms) => `T+${(ms - start) / MIN}`;
    const say = (d, who) => pacing.throttleLines(d, who, { now: start, clock }).map(([, t]) => t).join('\n');
    const named = say({ action: 'pause', waitMs: 5 * MIN, until: start + 5 * MIN, signal: throttle(5 * MIN) }, 'agent');
    assert.match(named, /^capture-har: Paused until T\+5 -- the site asked us to wait 5 min \(HTTP 429\)/,
        `the owner's line, word for word apart from the dash:\n${named}`);
    assert.match(named, /agent has been told to wait/);
    assert.match(named, /browser holds this capture's requests to the site/);
    assert.match(named, /carries on by itself/);
    const backoff = say({ action: 'pause', waitMs: MIN, until: start + MIN, signal: throttle(null) }, 'script');
    assert.match(backoff, /Paused until T\+1 -- the site is throttling \(HTTP 429\) and named no wait, so backing off 1 min/);
    // A tool that does not hold the browser (capture-cdp.js) does not claim to.
    const cdp = pacing.throttleLines({ action: 'pause', waitMs: MIN, until: start + MIN, signal: throttle(null) },
        'script', { now: start, clock, noHold: true, tool: 'capture-cdp' }).map(([, t]) => t).join('\n');
    assert.doesNotMatch(cdp, /browser holds/);
    assert.match(cdp, /the next step waits/);
    const extended = say({ action: 'pause', extended: true, waitMs: 7 * MIN, until: start + 7 * MIN,
        signal: throttle(7 * MIN) }, 'agent');
    assert.match(extended, /^capture-har: Pause extended until T\+7/);
    const byHand = say({ action: 'pause', waitMs: 30000, until: start + 30000, signal: throttle(30000) }, 'you');
    assert.match(byHand, /Wait at least 30 s/);
    assert.doesNotMatch(byHand, /Paused until/, 'a hand-driven run is advised, not paused');
    const resumed = pacing.resumeLines({ pausedMs: 5 * MIN, resumedBy: 'timer', driver: 'agent' })
        .map(([, t]) => t).join('\n');
    assert.strictEqual(resumed, 'capture-har: Resumed after 5 min; the agent has been told to carry on.');
    assert.match(pacing.resumeLines({ pausedMs: 4.5 * MIN, resumedBy: 'phone', driver: 'agent' })[0][1],
        /Resumed after 4 min 30 s -- you handed the browser back on your phone/);
    // Durations as a person says them, and the clock as one reads it.
    assert.strictEqual(pacing.describeDuration(52341), '52 s');
    assert.strictEqual(pacing.describeDuration(65 * MIN), '1 h 5 min');
    const local = new Date(2026, 10, 17, 9, 5).getTime();
    assert.strictEqual(pacing.formatClock(local, local), '09:05');
    assert.match(pacing.formatClock(local + 24 * 60 * MIN, local), /^Nov 18 09:05$/, 'tomorrow says which day');
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
