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
//   R1  Retry-After: delay-seconds, an HTTP-date, a date in the past, junk
//   R2  backoff is exponential with equal jitter: in [step/2, step], capped
//   C1  429 and 503+Retry-After are throttling; a bare 503 is not
//   C2  an HTML 403 from a CDN is a block; an application's JSON 403 is not
//   C3  challenge interstitials are recognised by markers only they carry; a
//       sign-in page that merely embeds reCAPTCHA is not
//   C4  only the SITE counts: a tracker's 429 on another host is ignored
//   C6  an ordinary page carrying a vendor's tag or the wording is not a
//       challenge -- nor is AWS WAF's 202 script check
//   C7  Amazon's checkpoint is found deep in a 300 KB page
//   C8  a block delivered to an XHR is seen whatever its content type:
//       Akamai's encoded page, DataDome's and PerimeterX's JSON -- but the
//       x-datadome header alone, on every response of a DataDome site, is not one
//   C9  a page built to make a signature backtrack (an unclosed <form or
//       `var dd={` repeated) is judged in bounded time, not minutes
//   C10 a signature's marker must sit near where it opens (the bound C9 rests on)
//   M1  a throttle pauses for Retry-After, a burst is one episode
//   M2  a throttle that persists hands over
//   M3  throttling through a pause hands over (the agent case)
//   M4  a block or a challenge hands over at once, and only once
//   M5  a Retry-After longer than the cap hands over rather than cutting it short
//   M6  a burst is one episode even with Retry-After: 0
//   L1  the hand-over tells the human the browser is theirs, and that nothing
//       evades -- and a hand-driven run gets advice, not a hand-over
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
    // THE FORM IS WHAT SAYS SO, never the path: any site can have a /checkpoint/.
    assert.strictEqual(at('https://game.example.org/checkpoint/3', '<html><head><title>Level 3</title></head><body>' +
        '<h1>Checkpoint 3</h1><p>Tell the guard "I am human" to pass.</p></body></html>'), null,
    'another site\'s /checkpoint/ page saying "I am human" is its own page');
    const formless = page.replace('<form method="POST" action="/checkpoint/validate" id="checkpointForm">',
        '<div id="checkpointForm">').replace('</form>', '</div>');
    assert.ok(!formless.includes('/checkpoint/validate'), 'the form is gone, its wording kept');
    assert.strictEqual(at('https://www.amazon.com/checkpoint/continue', formless), null,
        'Amazon\'s checkpoint path without the form is not the checkpoint');
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

    const ddBlock = '{"url":"https://geo.captcha-delivery.com/captcha/?initialCid=AHrlqAAAAAMAexample&cid=example&t=fe"}';
    const dd = xhr({ mimeType: 'application/json', headers: { 'x-datadome': 'protected' }, body: ddBlock });
    assert.ok(dd && dd.kind === 'blocked', `DataDome's JSON block: ${JSON.stringify(dd)}`);
    const ddBare = xhr({ mimeType: 'application/json', body: ddBlock });
    assert.ok(ddBare && ddBare.kind === 'blocked', `DataDome's JSON block with no header is still one: ${JSON.stringify(ddBare)}`);
    // X-DATADOME IS ON EVERY RESPONSE of a DataDome site, the ones it lets
    // through included, like cf-ray. A 403 carrying it is the application's
    // unless the body is DataDome's.
    assert.strictEqual(xhr({ mimeType: 'application/json', headers: { 'x-datadome': 'protected' },
        body: '{"error":"sign in required"}' }), null, 'a signed-out account call is the application refusing it');
    assert.strictEqual(xhr({ mimeType: 'application/json', headers: { 'x-datadome': 'protected' }, body: '' }), null,
        'x-datadome with nothing of DataDome\'s in the body says nothing');
    assert.strictEqual(xhr({ url: 'https://www.example.com/img/avatar.png', mimeType: 'image/png',
        headers: { 'x-datadome': 'protected' }, body: Buffer.from([0x89, 0x50, 0x4e, 0x47]) }), null,
    'an image the site refused is not DataDome refusing the visitor');
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

test('C9: a page built to make a signature backtrack is judged in bounded time', () => {
    // The scan is synchronous: a page that stalls it stalls ENTER, the stop
    // command and the recording. Run in a child with a kill timeout, so a
    // stall fails this test rather than hanging the suite.
    const { spawnSync } = require('child_process');
    const child = `
        const pacing = require(process.argv[1]);
        const MB = 1024 * 1024;
        const fill = (unit, bytes, tail = '') => unit.repeat(Math.ceil(bytes / unit.length)).slice(0, bytes - tail.length) + tail;
        const page = { url: 'https://www.example.com/', onSite: true, isDocument: true, status: 200, mimeType: 'text/html', headers: {} };
        // [what, response, the most of its body a signature reads]
        const cases = [
            ['an unclosed <form repeated to 1 MB', { body: fill('<form ', MB) }, MB],
            ['the same, ending in the checkpoint action and its wording',
                { body: fill('<form ', MB, '/checkpoint/validate I am human') }, MB],
            ['an unclosed var dd={ repeated to 64 KB, refused', { status: 403, body: fill('var dd={', 64 * 1024) },
                pacing.CHALLENGE_BODY_MAX],
            ['the same, closed just before the CAPTCHA host',
                { status: 403, body: fill('var dd={', 64 * 1024, '}captcha-delivery.com') }, pacing.CHALLENGE_BODY_MAX]
        ];
        const out = [];
        for (const [what, over, read] of cases) {
            const r = Object.assign({}, page, over);
            const started = Date.now();
            const signal = pacing.classifyResponse(r);
            out.push({ what, ms: Date.now() - started, inside: Buffer.byteLength(r.body) <= read, signal });
        }
        process.stdout.write(JSON.stringify(out));`;
    const run = spawnSync(process.execPath, ['-e', child, path.join(__dirname, 'pacing.js')],
        { encoding: 'utf8', timeout: 20000, windowsHide: true });
    assert.strictEqual(run.status, 0, `the classifier did not finish within 20 s (${run.signal || run.stderr})`);
    for (const { what, ms, inside, signal } of JSON.parse(run.stdout)) {
        assert.ok(inside, `${what}: inside what is read, so the signature runs on all of it`);
        assert.strictEqual(signal, null, `${what} is no interstitial: ${JSON.stringify(signal)}`);
        assert.ok(ms < 2000, `${what} took ${ms} ms`);
    }
});

test('C10: a signature\'s marker must sit near where it opens', () => {
    // What keeps C9 bounded, pinned: the checkpoint form's action within
    // 2 KB of its <form, and DataDome's CAPTCHA host within 4 KB of `var dd={`.
    const doc = (body, over) => pacing.classifyResponse(onSite(Object.assign(
        { url: 'https://www.amazon.com/s?k=usb+hub', status: 200, isDocument: true, mimeType: 'text/html', body }, over)));
    const form = (gap) => '<html><body><h4>Check the box to continue.</h4><form method="POST" ' +
        `data-pad="${'y'.repeat(gap)}" action="/checkpoint/validate" id="checkpointForm">` +
        '<input type="checkbox" id="continueCheck"><span>I am human</span></form></body></html>';
    assert.strictEqual(doc(form(100)).kind, 'challenge', 'a form tag of ordinary length');
    assert.strictEqual(doc(form(3000)), null, 'an action 3 KB into the form tag is past the reach');
    const dd = (gap) => '<html><body><script>var dd={\'rt\':\'c\',\'cid\':\'AHrlqAAAAAMAexample\',' +
        `'pad':'${'y'.repeat(gap)}','host':'geo.captcha-delivery.com'}</script></body></html>`;
    assert.strictEqual(doc(dd(100), { status: 403, isDocument: false }).kind, 'blocked', 'DataDome\'s object of ordinary length');
    assert.strictEqual(doc(dd(5000), { status: 403, isDocument: false }), null,
        'the CAPTCHA host 5 KB into the object is past the reach');
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

test('M6: a burst is one episode even when the site asks for no wait at all', () => {
    // Three requests of one page load refused together with Retry-After: 0.
    // The pause they name ends the moment it starts, so "inside the pause" is
    // never true -- and each used to count as a new episode: pause, pause,
    // hand-over "persistent".
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
