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
//   P7  a pause that arrives while a step is already waiting holds that step
//   P8  a pause ended early (resumeNow) wakes a step asleep in it; the pace's
//       own gap is not cut short
//   R1  Retry-After: delay-seconds, an HTTP-date, a date in the past, junk
//   R2  the backoff schedule: about 1, 2, 4, 8, then 15 minutes, never above
//       15, jittered downwards only -- and its numbers are named constants
//   C1  429 and 503+Retry-After are throttling; a bare 503 is not
//   C2  an HTML 403 from a CDN is a block; an application's JSON 403 is not
//   C3  challenge interstitials are recognised by markers only they carry; a
//       sign-in page that merely embeds reCAPTCHA is not
//   C4  only the SITE counts: a tracker's 429 on another host is ignored
//   C5  a page load is progress; a challenge that answered 200 is not
//   C6  an ordinary page carrying a vendor's tag or the wording is not a
//       challenge -- nor is AWS WAF's 202 script check
//   C7  Amazon's checkpoint is found deep in a 300 KB page
//   C8  a block delivered to an XHR is seen whatever its content type:
//       Akamai's encoded page, DataDome's and PerimeterX's JSON, x-datadome
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
//   M6  a burst is one episode even with Retry-After: 0
//   M7  a named wait of zero (Retry-After: 0, or a date already past) is a
//       short real pause -- the burst window -- not a zero-length one; the
//       third DISTINCT one inside ten minutes hands over, as repeated
//       throttling did before
//   M8  time spent honouring a NAMED wait never counts towards the hour
//   L1  the hand-over says who has the browser, gives the phone link, and that
//       nothing evades -- and a hand-driven run gets advice, not a hand-over
//   L2  the pause and resume lines say until when, for how long, and why
//   D1  capture-cdp.js: --throttle-ms 0 means zero, and --pace is validated
//   D2  capture-cdp.js reads the body of a JSON refusal, so C8 can see it
//   D3  headed, capture-cdp.js advises the person browsing; it claims no pause

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

test('P8: a pause ended early wakes a step asleep in it -- it does not sleep on to the original end', async () => {
    // A person took over, or handed back: resumeNow(). A step already asleep
    // in the pause (a held request's turn in the drain, a sign-in keystroke)
    // must go now, not when the cancelled pause would have ended -- which,
    // for a named wait, can be hours away.
    let t = 1_700_000_000_000;
    const pending = [];
    const sleep = (ms) => new Promise((resolve) => pending.push({ ms, resolve }));   // never ends by itself
    // A step that is never woken would leave nothing for Node to wait on, and
    // the run would end quietly as if it had passed: fail it, loudly, instead.
    const within = (p, what) => {
        let timer;
        return Promise.race([p, new Promise((resolve, reject) => {
            timer = setTimeout(() => reject(new Error(`${what} did not happen within 2 s`)), 2000);
        })]).finally(() => clearTimeout(timer));
    };
    const pacer = pacing.createPacer(pacing.PACE_OFF, { now: () => t, sleep });
    pacer.pauseFor(5 * 60 * 60 * 1000);   // a five-hour Retry-After
    let ran = false;
    const step = pacer.act(async () => { ran = true; });
    await new Promise((r) => setImmediate(r));
    assert.strictEqual(ran, false, 'the step waits in the pause');
    assert.deepStrictEqual(pending.map((p) => p.ms), [5 * 60 * 60 * 1000]);
    pacer.resumeNow();
    await within(step, 'the step asleep in the ended pause going on');
    assert.strictEqual(ran, true, 'and goes the moment the pause is ended, not five hours later');
    // The pace's own gap is NOT cut short: only a pause is.
    t += 1;
    const gapped = pacing.createPacer({ navigation: [0, 0], action: [500, 500], perMinute: 0 }, { now: () => t, sleep });
    await Promise.race([gapped.act(async () => {}), new Promise((r) => setImmediate(r))]);
    pending.length = 0;
    let second = false;
    const next = gapped.act(async () => { second = true; });
    await new Promise((r) => setImmediate(r));
    gapped.resumeNow();
    await new Promise((r) => setImmediate(r));
    assert.strictEqual(second, false, 'a person between two actions is not hurried by resumeNow()');
    pending.shift().resolve();
    await within(next, 'the second action after its gap');
    assert.strictEqual(second, true);
});

test('P7: a pause that arrives while a step is already waiting holds that step too', async () => {
    // The 429 lands during the 0.3-1.2 s wait before a sign-in keystroke: the
    // keystroke must not go out before the site's Retry-After is up.
    const time = fakeTime();
    let pacer;
    let interrupted = false;
    const sleep = async (ms) => {
        time.slept.push(ms);
        if (!interrupted) {
            interrupted = true;
            time.advance(Math.floor(ms / 2));
            pacer.pauseFor(30000);              // the site throttles mid-wait
            time.advance(ms - Math.floor(ms / 2));
            return;
        }
        time.advance(ms);
    };
    pacer = pacing.createPacer({ navigation: [0, 0], action: [750, 750], perMinute: 0 },
        { now: time.now, sleep, rng: fixed(0) });
    await pacer.act(async () => {});            // the first step: no gap yet
    let ranAt = null;
    await pacer.act(async () => { ranAt = time.now(); });
    assert.ok(ranAt >= pacer.pausedUntil(),
        `the step ran at +${ranAt - (pacer.pausedUntil() - 30375)} ms, inside a pause that ends at +30375 ms`);
});

test('P8: a --pace wait is at most 10 minutes, and 0 everywhere is off', async () => {
    // Node runs a setTimeout above 2^31-1 ms after 1 ms: nav=3000000000 was,
    // in effect, no pacing at all.
    const huge = pacing.parsePace('nav=3000000000');
    assert.deepStrictEqual(huge.pace.navigation, [pacing.MAX_PACE_WAIT_MS, pacing.MAX_PACE_WAIT_MS]);
    assert.strictEqual(pacing.MAX_PACE_WAIT_MS, 10 * 60 * 1000);
    assert.ok(huge.notes && huge.notes.some((n) => /nav=3000000000/.test(n) && /10 minutes/.test(n)),
        `the clamp is said, not silent: ${JSON.stringify(huge.notes)}`);
    assert.deepStrictEqual(pacing.parsePace('action=500-900000').pace.action, [500, pacing.MAX_PACE_WAIT_MS]);
    assert.deepStrictEqual(pacing.parsePace('nav=2000-5000').notes || [], [], 'a value in bounds says nothing');
    const time = fakeTime();
    const pacer = pacing.createPacer(huge.pace, { now: time.now, sleep: time.sleep, rng: fixed(0.5) });
    await pacer.navigate(async () => {});
    await pacer.navigate(async () => {});
    assert.deepStrictEqual(time.slept, [pacing.MAX_PACE_WAIT_MS], 'the pacer waits the clamped 10 minutes');
    // ZERO IS OFF, by either spelling, and says so.
    assert.strictEqual(pacing.parsePace('0').pace, pacing.PACE_OFF);
    const zeros = pacing.parsePace('nav=0,action=0,per-minute=0').pace;
    assert.strictEqual(zeros, pacing.PACE_OFF, 'every wait zero and no ceiling is off, and is marked as test mode');
    assert.match(pacing.describePace(zeros), /^off -- .*test mode/);
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

// THE VENDOR INTERSTITIALS ARE HAND-WRITTEN. There is no captured vendor page
// to test against, so each is a minimal page shaped after what that vendor's
// interstitial is known to carry, cut down to the marker the signature keys on
// and a little of what surrounds it. Amazon's checkpoint (amazonCheckpointPage)
// is the one derived from a real capture.
const VENDOR_PAGES = {
    Cloudflare: '<!DOCTYPE html><html lang="en-US"><head><title>Just a moment...</title></head><body>' +
        '<noscript><span id="challenge-error-text">Enable JavaScript and cookies to continue</span></noscript>' +
        '<script>(function(){window._cf_chl_opt={cvId: \'3\',cZone: "www.example.com",cType: \'managed\'};' +
        'var a=document.createElement(\'script\');' +
        'a.src=\'/cdn-cgi/challenge-platform/h/g/orchestrate/chl_page/v1?ray=8a1b2c3d4e5f6789\';' +
        'document.getElementsByTagName(\'head\')[0].appendChild(a);}());</script></body></html>',
    DataDome: '<html><head><title>www.example.com</title></head><body style="margin:0">' +
        '<p id="cmsg">Please enable JS and disable any ad blocker</p>' +
        '<script data-cfasync="false">var dd={\'rt\':\'c\',\'cid\':\'AHrlqAAAAAMAexample\',\'hsh\':\'0123456789ABCDEF\',' +
        '\'t\':\'fe\',\'s\':12345,\'host\':\'geo.captcha-delivery.com\'}</script>' +
        '<script data-cfasync="false" src="https://ct.captcha-delivery.com/c.js"></script></body></html>',
    PerimeterX: '<html lang="en"><head><title>Access to this page has been denied</title></head><body>' +
        '<script>window._pxAppId = \'PXabc123\'; window._pxJsClientSrc = \'/abc123/init.js\';</script>' +
        '<div id="px-captcha"></div><p>Press &amp; Hold to confirm you are a human (and not a bot).</p></body></html>',
    Imperva: '<html style="height:100%"><head><META NAME="ROBOTS" CONTENT="NOINDEX, NOFOLLOW"></head>' +
        '<body style="margin:0px;height:100%"><iframe id="main-iframe" ' +
        'src="/_Incapsula_Resource?CWUDNSAI=24&xinfo=0-0-0&incident_id=0-0&edet=15&mth=GET" frameborder=0 ' +
        'width="100%" height="100%">Request unsuccessful. Incapsula incident ID: 0-0</iframe></body></html>'
};

/** Ordinary page content, with no marker in it, to pad a page to a real size. */
function storefrontFiller(bytes) {
    const block = '<div class="a-section a-spacing-base"><span class="a-size-base a-color-base">' +
        'Free shipping on eligible orders. Shop deals in electronics, home and kitchen.</span></div>\n';
    return block.repeat(Math.ceil(bytes / block.length)).slice(0, bytes);
}

/**
 * AMAZON'S CHECKPOINT, derived from a capture taken 2026-11-18 UTC: a
 * signed-out search answered with GET /checkpoint/continue -> 200 text/html,
 * about 300 KB, its title "Amazon.com" set by script, the form about 148 KB
 * in. Kept verbatim: the opening of the document, and the alert and the form.
 * Dropped: the checkpoint token (from the query string and the hidden input),
 * the delivery location, and an opaque trailing comment. The storefront around
 * the form is generated filler of the captured page's size.
 */
function amazonCheckpointPage() {
    const head = '<!doctype html><html lang="en-us" class="a-no-js"><!-- sp:feature:head-start -->\n' +
        '<head><script>var aPageStart = (new Date()).getTime();</script><meta charset="utf-8"/>\n' +
        '<!-- sp:end-feature:head-start -->\n';
    const checkpoint = '<div id="tmms-challenge" lang="en-US" class="a-container a-padding-double-large">\n' +
        '        <div class="a-box a-alert a-alert-info a-spacing-base">\n' +
        '    <div class="a-box-inner a-alert-container">\n' +
        '        <i class="a-icon a-icon-alert" aria-hidden="true"></i>\n' +
        '        <div class="a-alert-content">\n' +
        '            <h4 role="heading" aria-level="1">Check the box to continue.</h4>\n' +
        '        </div>\n    </div>\n</div>\n\n' +
        '<div class="a-section">\n    <div class="a-box a-color-offset-background">\n' +
        '        <div class="a-box-inner a-padding-extra-large">\n' +
        '            <form method="POST" action="/checkpoint/validate" id="checkpointForm">\n' +
        '                <input type="hidden" name="token" value="">\n' +
        '                <div class="a-row a-spacing-large">\n' +
        '                    <label for="continueCheck" class="a-size-medium a-text-bold">\n' +
        '                        <input type="checkbox" id="continueCheck" name="continueCheck" value="true">\n' +
        '                        <span>I am human</span>\n' +
        '                    </label>\n                </div>\n\n' +
        '                <div class="a-row">\n' +
        '                    <span class="a-button a-button-primary a-span12" id="continueButton" aria-disabled="true">\n' +
        '                        <span class="a-button-inner">\n' +
        '                            <button type="submit" class="a-button-text" disabled>Continue shopping</button>\n' +
        '                        </span>\n                    </span>\n                </div>\n' +
        '            </form>\n        </div>\n    </div>\n</div>\n';
    const tail = '<!-- sp:end-feature:csm:body-close -->\n</div></body></html>\n';
    const before = 148 * 1024 - head.length;
    return head + storefrontFiller(before) + checkpoint +
        storefrontFiller(303 * 1024 - 148 * 1024 - checkpoint.length - tail.length) + tail;
}

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
        body: VENDOR_PAGES.DataDome })).kind, 'blocked',
    'with a known signature a sub-resource counts');
    assert.strictEqual(pacing.classifyResponse(onSite({
        status: 200, mimeType: 'text/html', headers: { 'cf-mitigated': 'challenge' }, body: '<html></html>'
    })).kind, 'challenge');
    // WORDING counts on a refusal: the site has already said no, and it says why.
    const worded = pacing.classifyResponse(onSite({ status: 403, isDocument: true, mimeType: 'text/html',
        body: '<html><body>Our systems have detected unusual traffic from your computer network.</body></html>' }));
    assert.strictEqual(worded.kind, 'blocked');
    assert.match(worded.signature, /unusual traffic/);
});

test('C3: challenge interstitials are recognised by markers only they carry', () => {
    const doc = (body, over) => pacing.classifyResponse(onSite(Object.assign(
        { status: 200, isDocument: true, mimeType: 'text/html', body }, over)));
    for (const [vendor, page] of Object.entries(VENDOR_PAGES)) {
        const s = doc(page);
        assert.ok(s && s.kind === 'challenge', `${vendor}'s interstitial, answering 200, is a challenge: ${JSON.stringify(s)}`);
        assert.strictEqual(pacing.classifyResponse(onSite({ status: 403, isDocument: true, mimeType: 'text/html',
            body: page })).kind, 'blocked', `${vendor}'s interstitial answering 403 is a block`);
    }
    assert.strictEqual(doc('<html><title>Sign in</title><form><input type=password></form>' +
        '<script src="https://www.google.com/recaptcha/api.js?render=abc"></script></html>'), null,
    'reCAPTCHA v3 runs invisibly on ordinary sign-in pages; handing over on it would end every sign-in');
    // Cloudflare's exact title is its own interstitial's, even with the script cut away.
    const titled = doc('<html><head><title>Just a moment...</title></head><body>Checking your browser</body></html>');
    assert.ok(titled && titled.kind === 'challenge', `"Just a moment..." answering 200: ${JSON.stringify(titled)}`);
    // A PAGE THAT LOADED is judged inside a much tighter bound than a refusal.
    const padded = VENDOR_PAGES.Cloudflare.replace('</body>',
        `<div>${'x'.repeat(pacing.INTERSTITIAL_BODY_MAX)}</div></body>`);
    assert.ok(Buffer.byteLength(padded) < pacing.CHALLENGE_BODY_MAX, 'the padded page is inside the refusal bound');
    assert.strictEqual(doc(padded), null, 'a 2xx page above the interstitial bound is a page, whatever it contains');
    assert.strictEqual(doc(padded, { status: 403 }).kind, 'blocked', 'the same page refused is judged on the larger bound');
    assert.strictEqual(doc(VENDOR_PAGES.Cloudflare, { isDocument: false }), null,
        'a 200 is judged as an interstitial only as the main document');
});

test('C6: an ordinary page carrying a vendor\'s tag, or the wording, is not a challenge', () => {
    const doc = (body, over) => pacing.classifyResponse(onSite(Object.assign(
        { url: 'https://www.example.com/', status: 200, isDocument: true, mimeType: 'text/html', body }, over)));
    const ordinary = {
        'Imperva\'s script on a small start page': '<html><head><title>Welcome</title>' +
            '<script src="/_Incapsula_Resource?SWJIYLWA=719d34d31c8e3a6e6fffd425f7e032f3&ns=2"></script></head>' +
            '<body><div id="app"></div></body></html>',
        'DataDome\'s tag and a preconnect to its CAPTCHA host': '<html><head><title>Welcome</title>' +
            '<link rel="preconnect" href="https://geo.captcha-delivery.com">' +
            '<script>window.ddjskey = \'0123456789ABCDEF\';</script>' +
            '<script src="https://js.datadome.co/tags.js" async></script></head><body><div id="app"></div></body></html>',
        'a script from DataDome\'s CAPTCHA host': '<html><script src="https://geo.captcha-delivery.com/captcha/"></script></html>',
        'PerimeterX\'s sensor': '<html><head><title>Shop</title>' +
            '<script src="//client.px-cloud.net/PXabc123/main.min.js" async></script></head><body><main></main></body></html>',
        'Cloudflare\'s bot-detection script': '<html><head><title>Home</title></head><body><script>(function(){' +
            'window.__CF$cv$params={r:\'8a1b2c3d4e5f6789\',t:\'MTcwMDAwMDAwMA==\'};var a=document.createElement(\'script\');' +
            'a.src=\'/cdn-cgi/challenge-platform/h/b/scripts/jsd/1a2b3c4d5e6f/main.js\';' +
            'document.getElementsByTagName(\'head\')[0].appendChild(a);})();</script></body></html>',
        'a sign-up form that asks you to verify you are human': '<html><title>Create account</title><form>' +
            '<label>Please verify you are human</label><div class="cf-turnstile" data-sitekey="0x4AAAAAAA"></div></form></html>',
        'a help page about "Are you a robot?" and unusual traffic': '<html><title>Help</title><h2>Are you a robot?</h2>' +
            '<p>If a page says it detected unusual traffic from your computer network, try again later.</p></html>',
        'a help page about Press & Hold': '<html><title>Help</title><p>If you see Press &amp; Hold, hold the button.</p></html>',
        'a title naming a CAPTCHA': '<html><head><title>How our CAPTCHA works</title></head><body><p>...</p></body></html>',
        'Akamai Bot Manager\'s sensor': '<html><head><title>Flights</title>' +
            '<script type="text/javascript" src="/akam/13/7a3c8f2d" defer></script></head><body><main></main>' +
            '<noscript><img src="/akam/13/pixel_7a3c8f2d?a=dD0x" style="visibility: hidden; position: absolute;" />' +
            '</noscript></body></html>',
        'an Amazon page that says "I am human" and links to /checkpoint': '<html><head><title>Amazon.com</title></head>' +
            '<body><p>Seen "I am human"? <a href="/checkpoint/continue">Read about our checks</a>.</p>' +
            storefrontFiller(40 * 1024) + '</body></html>'
    };
    for (const [what, body] of Object.entries(ordinary)) {
        assert.strictEqual(doc(body), null, `${what} is an ordinary page, not a challenge`);
    }
    // AWS WAF's script check answers 202 with a page whose script fetches a
    // token and reloads: an ordinary browser passes it by itself, and the next
    // page is the real one. Handing over here would hand over on Amazon's home
    // page. (Hand-written; the capture's 202 entry was not kept.)
    const awsWaf = '<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title></title>' +
        '<script>window.awsWafCookieDomainList = [];window.gokuProps = {"key":"AQIDAHjc","iv":"A6we","context":"x"};</script>' +
        '<script src="https://0123456789ab.edge.sdk.awswaf.com/0123456789ab/cdef01234567/challenge.js"></script></head>' +
        '<body><div id="challenge-container"></div><script>AwsWafIntegration.checkForceRefresh().then((forceRefresh) => {' +
        'if (forceRefresh) { AwsWafIntegration.forceRefreshToken().then(() => { window.location.reload(true); }); } else {' +
        'AwsWafIntegration.getToken().then(() => { window.location.reload(true); }); } });</script>' +
        '<noscript><h1>JavaScript is disabled</h1>In order to continue, we need to verify that you\'re not a robot. ' +
        'This requires JavaScript. Enable JavaScript and then reload the page.</noscript></body></html>';
    assert.strictEqual(doc(awsWaf, { url: 'https://www.amazon.com/', status: 202 }), null,
        'AWS WAF\'s script check passes by itself in a browser; it is not a hand-over');
    // The telemetry a checkpoint page sends mentions the checkpoint; it is not one.
    assert.strictEqual(pacing.classifyResponse({ url: 'https://unagi.amazon.com/1/events/com.amazon.csm.csa.prod',
        onSite: true, isDocument: false, status: 200, mimeType: 'application/json', headers: {}, body: '{}' }), null);
});

test('C7: Amazon\'s checkpoint is a challenge, though its form sits deep in a full-size page', () => {
    const page = amazonCheckpointPage();
    assert.ok(Buffer.byteLength(page) > 250 * 1024, 'the captured page is about 300 KB');
    assert.ok(page.indexOf('/checkpoint/validate') > pacing.CHALLENGE_BODY_MAX,
        'and the form is beyond the head of the body the other signatures read');
    const at = (url, body = page) => pacing.classifyResponse({ url, onSite: true, isDocument: true, status: 200,
        mimeType: 'text/html;charset=UTF-8', headers: { 'content-type': 'text/html;charset=UTF-8' },
        body: Buffer.from(body) });
    const s = at('https://www.amazon.com/checkpoint/continue');
    assert.ok(s && s.kind === 'challenge', `the checkpoint must hand over: ${JSON.stringify(s)}`);
    assert.match(s.signature, /Amazon "Check the box to continue" checkpoint/);
    assert.strictEqual(at('https://www.amazon.com/s?k=usb+hub').kind, 'challenge',
        'served in place of the search, the form still says what it is');
    assert.strictEqual(at('https://www.amazon.com/s?k=usb+hub', page.replace('Check the box to continue.', '')
        .replace('<span>I am human</span>', '<span>Continue</span>')), null,
    'a form to /checkpoint/validate with no checkbox wording is not this interstitial');
});

test('C8: a block delivered to an XHR is seen, whatever its content type', () => {
    // HAND-WRITTEN, like VENDOR_PAGES: Akamai's "Access Denied" entity-encodes
    // its reference, and DataDome and PerimeterX answer a refused XHR in JSON.
    const akamai = '<HTML><HEAD>\n<TITLE>Access Denied</TITLE>\n</HEAD><BODY>\n<H1>Access Denied</H1>\n \n' +
        'You don\'t have permission to access "http&#58;&#47;&#47;www&#46;example&#46;com&#47;api&#47;search" ' +
        'on this server.<P>\nReference&#32;&#35;18&#46;2f3c1002&#46;1700000000&#46;1a2b3c4d\n' +
        '<P>https&#58;&#47;&#47;errors&#46;edgesuite&#46;net&#47;18&#46;2f3c1002&#46;1700000000&#46;1a2b3c4d</P>\n' +
        '</BODY>\n</HTML>\n';
    const xhr = (over) => pacing.classifyResponse(onSite(Object.assign({ status: 403 }, over)));
    const ak = xhr({ mimeType: 'text/html', headers: { server: 'AkamaiGHost' }, body: akamai });
    assert.ok(ak && ak.kind === 'blocked', `Akamai's real page, encoded reference and all: ${JSON.stringify(ak)}`);
    assert.match(ak.signature, /Akamai/);
    assert.strictEqual(xhr({ mimeType: 'text/html', body: akamai.replace(/&#32;/, ' ').replace(/&#35;/, '#')
        .replace(/https&#58;.*<\/P>/, '') }).kind, 'blocked', 'and the plain spelling');

    const dd = xhr({ mimeType: 'application/json', headers: { 'x-datadome': 'protected' },
        body: '{"url":"https://geo.captcha-delivery.com/captcha/?initialCid=AHrlqAAAAAMAexample&cid=example&t=fe"}' });
    assert.ok(dd && dd.kind === 'blocked', `DataDome's JSON block: ${JSON.stringify(dd)}`);
    assert.strictEqual(xhr({ mimeType: 'application/json', headers: { 'x-datadome': 'protected' }, body: '' }).kind,
        'blocked', 'x-datadome on a 403 is DataDome refusing, even with no body to read');
    const px = xhr({ mimeType: 'application/json', body: '{"appId":"PXabc123","jsClientSrc":"/abc123/init.js",' +
        '"firstPartyEnabled":true,"vid":"","uuid":"0000","hostUrl":"/abc123/xhr","blockScript":"/abc123/captcha/captcha.js?a=c"}' });
    assert.ok(px && px.kind === 'blocked', `PerimeterX's JSON block: ${JSON.stringify(px)}`);

    // CONTENT TYPE IS NOT WHAT DECIDES; vendor evidence is.
    assert.strictEqual(xhr({ mimeType: 'application/json', headers: { 'cf-ray': 'x', server: 'cloudflare' },
        body: '{"error":"forbidden"}' }), null, 'a CDN\'s headers alone are on every response, an app\'s 403 included');
    assert.strictEqual(pacing.classifyResponse({ url: 'https://collector.tracker.example.net/t', onSite: false,
        isDocument: false, status: 403, mimeType: 'application/json', headers: { 'x-datadome': 'protected' }, body: '' }),
    null, 'a tracker\'s refusal is not the site\'s');
    assert.strictEqual(xhr({ status: 200, mimeType: 'application/json',
        body: '{"url":"https://geo.captcha-delivery.com/captcha/?x"}' }), null, 'a 200 is not a refusal');
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

test('M6: a burst is one episode even when the site asks for no wait at all', () => {
    // Three requests of one page load refused together with Retry-After: 0.
    // The pause they name ends the moment it starts, so "inside the pause" is
    // never true -- and each used to count as a new episode (and a new pause,
    // announced and over at once).
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0.5) });
    const decisions = [];
    for (let i = 0; i < 3; i++) {
        decisions.push(monitor.observe(throttle(0)).action);
        time.advance(100);
    }
    assert.deepStrictEqual(decisions, ['pause', 'none', 'none'], 'one page load\'s burst is one episode');
    assert.strictEqual(monitor.handedOver(), null);
    // After the burst window, the site refusing again IS a new episode.
    time.advance(5000);
    assert.strictEqual(monitor.observe(throttle(0)).action, 'pause');
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

test('M7: a named wait of zero (or a date already past) is a short real pause -- and the third distinct one in ten minutes hands over', () => {
    // Retry-After: 0, or an HTTP-date at or behind this PC's clock, names a
    // wait of 0 ms. That used to be a zero-length "pause", announced and over at
    // once, repeated for ever: the site saying "slow down" over and over and
    // nothing ever escalating.
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now });
    const first = monitor.observe(throttle(0));
    assert.strictEqual(first.action, 'pause');
    assert.strictEqual(first.waitMs, pacing.SHORT_WAIT_MIN_MS, 'a real pause, however short the wait named');
    assert.strictEqual(pacing.SHORT_WAIT_MIN_MS, pacing.PAUSE_EXTEND_TOLERANCE_MS,
        'the floor IS the burst window: one rule, not two');
    time.advance(first.waitMs);
    const second = monitor.observe(throttle(0));
    assert.strictEqual(second.action, 'pause');
    time.advance(second.waitMs);
    const third = monitor.observe(throttle(0));
    assert.strictEqual(third.action, 'hand-over', `the third within the window hands over: ${JSON.stringify(third)}`);
    assert.strictEqual(third.why, 'zero-wait');
    assert.match(pacing.handOverReason(third), /no wait|already over/);

    // Outside the window they do not add up: ten minutes apart is not "again and again".
    const t2 = fakeTime();
    const m2 = pacing.createThrottleMonitor({ now: t2.now });
    assert.strictEqual(m2.observe(throttle(0)).action, 'pause');
    t2.advance(6 * MIN);
    assert.strictEqual(m2.observe(throttle(0)).action, 'pause');
    t2.advance(6 * MIN);   // the first is now twelve minutes old
    assert.strictEqual(m2.observe(throttle(0)).action, 'pause', 'only two inside the last ten minutes');
    t2.advance(2000);
    assert.strictEqual(m2.observe(throttle(0)).action, 'hand-over', 'three inside ten minutes');

    // ONE BURST IS ONE EPISODE, and is counted once: the rest of a page load's
    // refusals land inside the short pause.
    const t3 = fakeTime();
    const m3 = pacing.createThrottleMonitor({ now: t3.now });
    const burst = [];
    for (let i = 0; i < 3; i++) { burst.push(m3.observe(throttle(0)).action); t3.advance(100); }
    assert.deepStrictEqual(burst, ['pause', 'none', 'none']);
    t3.advance(pacing.SHORT_WAIT_MIN_MS);
    assert.strictEqual(m3.observe(throttle(0)).action, 'pause',
        'the second distinct episode -- not the fourth refusal, which would hand over');
    // And a person handing back starts the count again.
    m3.handedBack();
    t3.advance(pacing.SHORT_WAIT_MIN_MS);
    assert.strictEqual(m3.observe(throttle(0)).action, 'pause');
    t3.advance(pacing.SHORT_WAIT_MIN_MS);
    assert.strictEqual(m3.observe(throttle(0)).action, 'pause', 'two since the person handed back');
});

test('M8: time spent honouring a NAMED wait never counts towards the hour', () => {
    // NAMED, THEN NOT: an hour's Retry-After honoured in full, then one 429
    // that names nothing. That is the first minute of backoff, not "throttling
    // for an hour without one page loading".
    const time = fakeTime();
    const monitor = pacing.createThrottleMonitor({ now: time.now, rng: fixed(0) });
    const named = monitor.observe(throttle(60 * MIN));
    assert.strictEqual(named.action, 'pause');
    time.advance(named.waitMs);
    const after = monitor.observe(throttle(null));
    assert.strictEqual(after.action, 'pause', `backoff, not a hand-over: ${JSON.stringify(after)}`);
    assert.strictEqual(after.waitMs, 1 * MIN);

    // NOT, NAMED, NOT: the named hour in the middle is not counted either;
    // only the unnamed backoff adds up to the hour.
    const t2 = fakeTime();
    const m2 = pacing.createThrottleMonitor({ now: t2.now, rng: fixed(0) });
    const unnamed = [];
    let d = m2.observe(throttle(null));
    unnamed.push(d.waitMs / MIN);
    t2.advance(d.waitMs);
    d = m2.observe(throttle(60 * MIN));
    assert.strictEqual(d.action, 'pause');
    t2.advance(d.waitMs);
    for (;;) {
        d = m2.observe(throttle(null));
        if (d.action !== 'pause') break;
        unnamed.push(d.waitMs / MIN);
        t2.advance(d.waitMs);
    }
    assert.deepStrictEqual(unnamed, [1, 2, 4, 8, 15, 15, 15], 'the same schedule as with no named wait in between');
    assert.strictEqual(d.action, 'hand-over');
    assert.strictEqual(d.why, 'persistent');
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
    // No -AI and nobody at the terminal: automation over CDP. Held, not told.
    const external = say({ action: 'pause', waitMs: MIN, until: start + MIN, signal: throttle(60000) }, 'external');
    assert.match(external, /browser holds this capture's requests to the site, whatever drives it over CDP/);
    assert.doesNotMatch(external, /told/);
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
    // A wait over the 10-minute bound is clamped, and the run says so.
    const clamped = run(['--pace', 'nav=3000000000']);
    assert.strictEqual(clamped.status, 0, clamped.stderr);
    assert.match(clamped.stderr, /capture-cdp: .*nav=3000000000.*10 minutes/);
});

test('D3: headed, capture-cdp tells the person browsing what to do; it claims no pause it does not take', () => {
    const { throttleAudience } = require(path.join(__dirname, 'capture-cdp.js'));
    const pause = { action: 'pause', waitMs: 30000, signal: throttle(30000) };
    const headed = throttleAudience({ headless: false });
    const said = pacing.throttleLines(pause, headed.who, headed.say).map(([, t]) => t).join('\n');
    // Headed, the script navigates once and the person drives from there:
    // nothing of the script's is left to pause.
    assert.doesNotMatch(said, /Pausing .* before the next step/, `a pause nothing takes:\n${said}`);
    assert.match(said, /^capture-cdp: /);
    assert.match(said, /Wait at least 30 s/);
    const block = { action: 'hand-over', why: 'blocked', signal: { kind: 'blocked', status: 403,
        url: 'https://www.example.com/', retryAfterMs: null, signature: '403 Imperva' } };
    const handed = pacing.throttleLines(block, headed.who, headed.say).map(([, t]) => t).join('\n');
    assert.doesNotMatch(handed, /Automated browsing has stopped/, 'the person was the one browsing');
    assert.match(handed, /Answer it yourself in the browser if you choose/);
    const headless = throttleAudience({ headless: true });
    const ended = pacing.throttleLines(block, headless.who, headless.say).map(([, t]) => t).join('\n');
    assert.match(ended, /Nobody can take over a headless browser, so this run ends \(exit 3\)/);
});

test('D2: capture-cdp reads the body of any refusal from the site, JSON included', () => {
    const { readsBodyFor } = require(path.join(__dirname, 'capture-cdp.js'));
    const xhr = { isDocument: false, onSite: true };
    assert.strictEqual(readsBodyFor(Object.assign({ status: 403, contentType: 'application/json' }, xhr)), true,
        'an edge refusing an XHR answers in JSON as often as in HTML');
    assert.strictEqual(readsBodyFor(Object.assign({ status: 503, contentType: 'text/html' }, xhr)), true);
    assert.strictEqual(readsBodyFor({ status: 200, contentType: 'text/html', isDocument: true, onSite: true }), true,
        'a page that answered 200 can be an interstitial');
    assert.strictEqual(readsBodyFor(Object.assign({ status: 200, contentType: 'application/json' }, xhr)), false,
        'an ordinary API answer is not read twice');
    assert.strictEqual(readsBodyFor({ status: 403, contentType: 'application/json', isDocument: false, onSite: false }),
        false, 'nor a tracker\'s refusal');
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
