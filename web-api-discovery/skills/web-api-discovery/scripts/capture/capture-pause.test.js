#!/usr/bin/env node
// Behavior tests for capture-pause.js -- the whole capture pauses, the browser
// holds the site's requests, and it carries on by itself. Zero-dep; runs with
// `node capture-pause.test.js`.
//
// Nothing here waits on a clock or a browser: the hold is driven through fake
// routes, and the pause through an injected clock and timer.
//
// FALSIFIERS (fail before this change, pass after):
//   H1  during a pause the site's requests are HELD -- not continued -- and go
//       on, in order, when it ends; nothing reaches the site in between
//   H2  requests already in flight when the pause began are not held: the
//       route is installed AT the pause, and removed after it (interception
//       disables the browser cache, so it is never left in place)
//   H3  a request that is not the site's (a tracker) goes straight through
//   H4  a hand-over or an ending DROPS what is held: aborted, never sent
//   H5  a predicate that throws holds: when in doubt, the site is not sent it
//   P1  a pause ends by itself when its time is up -- the timer resumes it,
//       releases the hold and the pacer, and records how long it lasted
//   P2  a longer wait named during a pause extends it, and the timer moves
//   P3  a hand-over cuts a pause short, drops the hold, and is recorded with
//       who was driving; handing back records who handed back
//   P4  the record never carries the query string, and the summary counts
//       pauses, hand-overs and the time they took

'use strict';

const assert = require('assert');
const path = require('path');

const { createSiteHold, createCapturePause, pauseSummaryText } = require(path.join(__dirname, 'capture-pause.js'));
const pacing = require(path.join(__dirname, 'pacing.js'));

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

const tick = () => new Promise((r) => setImmediate(r));

/** A Playwright-shaped route that records what was done to it. */
function fakeRoute(url, log) {
    const request = { url: () => url };
    return {
        request: () => request,
        continue: async () => { log.push(`continue ${url}`); },
        abort: async (code) => { log.push(`abort ${url} ${code}`); }
    };
}

/** A context whose route/unroute record the handler, so a test can push requests through it. */
function fakeContext() {
    const ctx = { handler: null, installs: 0, uninstalls: 0 };
    ctx.install = async (h) => { ctx.handler = h; ctx.installs++; };
    ctx.uninstall = async (h) => { if (ctx.handler === h) ctx.handler = null; ctx.uninstalls++; };
    /** What the browser does with a new request: routed if a route is installed, else sent. */
    ctx.request = (url, log) => {
        const route = fakeRoute(url, log);
        if (ctx.handler) return ctx.handler(route);
        log.push(`sent ${url}`);
        return Promise.resolve();
    };
    return ctx;
}

const onSite = (url) => /^https:\/\/(www\.)?example\.com\//.test(url);

function fakeTime(start = Date.parse('2026-11-17T10:37:00Z')) {
    let t = start;
    const timers = [];
    return {
        now: () => t,
        advance(ms) {
            t += ms;
            for (const timer of timers.slice()) {
                if (!timer.cleared && timer.at <= t) { timer.cleared = true; timer.fn(); }
            }
        },
        setTimeout: (fn, ms) => { const timer = { fn, at: t + ms, cleared: false }; timers.push(timer); return timer; },
        clearTimeout: (timer) => { if (timer) timer.cleared = true; },
        pending: () => timers.filter((x) => !x.cleared).length
    };
}

const throttle = (retryAfterMs) => ({ kind: 'throttled', status: 429, retryAfterMs,
    url: 'https://www.example.com/api/search?email=someone%40example.com', signature: '429 Too Many Requests' });

// ---------------------------------------------------------------------------
// The hold
// ---------------------------------------------------------------------------

test('H1: during a pause the site\'s requests are held, and go on in order when it ends', async () => {
    const ctx = fakeContext();
    const hold = createSiteHold({ install: ctx.install, uninstall: ctx.uninstall, holds: (r) => onSite(r.url()) });
    const log = [];
    await hold.start();
    const a = ctx.request('https://www.example.com/api/search?q=1', log);
    const b = ctx.request('https://www.example.com/next', log);
    await tick();
    assert.deepStrictEqual(log, [], `nothing may reach the site during the pause: ${log}`);
    assert.strictEqual(hold.heldCount(), 2);
    await hold.release();
    await Promise.all([a, b]);
    assert.deepStrictEqual(log, ['continue https://www.example.com/api/search?q=1', 'continue https://www.example.com/next']);
    assert.strictEqual(hold.heldCount(), 0);
});

test('H2: the hold exists only for the pause -- in-flight requests finish, and the route is removed after', async () => {
    const ctx = fakeContext();
    const hold = createSiteHold({ install: ctx.install, uninstall: ctx.uninstall, holds: (r) => onSite(r.url()) });
    const log = [];
    await ctx.request('https://www.example.com/before', log);
    assert.strictEqual(ctx.installs, 0, 'no interception before a pause: the browser cache stays on');
    assert.deepStrictEqual(log, ['sent https://www.example.com/before']);
    await hold.start();
    assert.strictEqual(ctx.installs, 1);
    await hold.release();
    await hold.settled();
    assert.strictEqual(ctx.uninstalls, 1, 'removed when the pause ends -- never left in place');
    await ctx.request('https://www.example.com/after', log);
    assert.strictEqual(log[log.length - 1], 'sent https://www.example.com/after');
    // A second pause installs it again.
    await hold.start();
    assert.strictEqual(ctx.installs, 2);
});

test('H3: a request that is not the site\'s goes straight through', async () => {
    const ctx = fakeContext();
    const hold = createSiteHold({ install: ctx.install, uninstall: ctx.uninstall, holds: (r) => onSite(r.url()) });
    const log = [];
    await hold.start();
    await ctx.request('https://collector.tracker.example.net/t', log);
    assert.deepStrictEqual(log, ['continue https://collector.tracker.example.net/t']);
});

test('H4: a hand-over or an ending drops what is held -- aborted, never sent', async () => {
    const ctx = fakeContext();
    const hold = createSiteHold({ install: ctx.install, uninstall: ctx.uninstall, holds: () => true });
    const log = [];
    await hold.start();
    const p = ctx.request('https://www.example.com/queued-by-the-agent', log);
    await tick();
    await hold.drop();
    await p;
    assert.deepStrictEqual(log, ['abort https://www.example.com/queued-by-the-agent blockedbyclient']);
    assert.strictEqual(hold.active(), false);
});

test('H5: a predicate that throws holds the request', async () => {
    const ctx = fakeContext();
    const hold = createSiteHold({ install: ctx.install, uninstall: ctx.uninstall,
        holds: () => { throw new Error('frame detached'); } });
    const log = [];
    await hold.start();
    const p = ctx.request('https://www.example.com/x', log);
    await tick();
    assert.deepStrictEqual(log, [], 'when in doubt during a pause, the site is not sent it');
    await hold.release();
    await p;
    assert.deepStrictEqual(log, ['continue https://www.example.com/x']);
});

// ---------------------------------------------------------------------------
// The pause
// ---------------------------------------------------------------------------

function rig() {
    const time = fakeTime();
    const ctx = fakeContext();
    const hold = createSiteHold({ install: ctx.install, uninstall: ctx.uninstall, holds: (r) => onSite(r.url()) });
    const pacer = pacing.createPacer(pacing.PACE_OFF, { now: time.now, sleep: async (ms) => time.advance(ms) });
    const resumed = [];
    const pause = createCapturePause({ hold, pacer, now: time.now, setTimeout: time.setTimeout,
        clearTimeout: time.clearTimeout, onTimerResume: (r) => resumed.push(r) });
    const monitor = pacing.createThrottleMonitor({ now: time.now });
    return { time, ctx, hold, pacer, pause, monitor, resumed };
}

test('P1: a pause ends by itself when its time is up, and the capture carries on', async () => {
    const { time, ctx, hold, pacer, pause, monitor, resumed } = rig();
    const decision = monitor.observe(throttle(5 * 60000));
    const record = pause.pause(decision, 'agent');
    await hold.settled();
    assert.strictEqual(record.kind, 'pause');
    assert.strictEqual(record.waitMs, 5 * 60000);
    assert.strictEqual(record.untilUtc, '2026-11-17T10:42:00.000Z');
    assert.strictEqual(record.driver, 'agent');
    assert.ok(pacer.pausedUntil() > time.now(), 'the recorder\'s own steps wait too');
    const log = [];
    const held = ctx.request('https://www.example.com/agent-click', log);
    time.advance(4 * 60000);
    await tick();
    assert.deepStrictEqual(log, [], 'four minutes in, the site has still been sent nothing');
    assert.strictEqual(resumed.length, 0);
    time.advance(60000);
    await held;
    assert.deepStrictEqual(log, ['continue https://www.example.com/agent-click'], 'released when the time was up');
    assert.strictEqual(resumed.length, 1, 'the timer resumed it -- nobody had to');
    assert.strictEqual(resumed[0].resumedBy, 'timer');
    assert.strictEqual(resumed[0].outcome, 'continued');
    assert.strictEqual(resumed[0].pausedMs, 5 * 60000);
    assert.strictEqual(pause.active(), false);
    assert.strictEqual(pacer.pausedUntil(), 0);
});

test('P2: a longer wait named during a pause extends it, and the timer moves with it', async () => {
    const { time, pause, monitor, resumed } = rig();
    pause.pause(monitor.observe(throttle(60000)), 'agent');
    time.advance(30000);
    const longer = monitor.observe(throttle(120000));
    assert.strictEqual(longer.extended, true);
    pause.pause(longer, 'agent');
    time.advance(40000);   // past the first end
    assert.strictEqual(resumed.length, 0, 'the pause is not over at its first end');
    assert.strictEqual(pause.current().untilUtc, '2026-11-17T10:39:30.000Z');
    time.advance(80000);
    assert.strictEqual(resumed.length, 1);
    assert.strictEqual(resumed[0].pausedMs, 150000);
    assert.strictEqual(pause.records().length, 1, 'one pause, extended -- not two');
});

test('P3: a hand-over cuts a pause short and drops the hold; handing back records who did', async () => {
    const { time, ctx, hold, pause, monitor, resumed } = rig();
    pause.pause(monitor.observe(throttle(10 * 60000)), 'agent');
    await hold.settled();
    const log = [];
    const queued = ctx.request('https://www.example.com/agent-click', log);
    time.advance(60000);
    const challenge = { kind: 'challenge', status: 200, url: 'https://www.example.com/search', retryAfterMs: null,
        signature: 'PerimeterX "Press & Hold"' };
    const decision = monitor.observe(challenge);
    assert.strictEqual(decision.action, 'hand-over', 'a challenge hands over even mid-pause');
    const record = pause.handOver(decision, 'agent');
    await queued;
    assert.deepStrictEqual(log, ['abort https://www.example.com/agent-click blockedbyclient'],
        'what automation queued during the pause is not sent once a person has the browser');
    assert.strictEqual(record.kind, 'hand-over');
    assert.match(record.reason, /bot check/);
    const [cut] = pause.records();
    assert.strictEqual(cut.resumedBy, 'hand-over');
    time.advance(20 * 60000);
    assert.strictEqual(resumed.length, 0, 'a hand-over has no timer: it waits for the person');
    const back = pause.resume('phone');
    assert.strictEqual(back.resumedBy, 'phone');
    assert.strictEqual(back.outcome, 'continued');
    assert.strictEqual(back.pausedMs, 20 * 60000);
    // And ending instead: recorded as ended.
    pause.handOver(monitor.observe(challenge) || decision, 'agent');
    const ended = pause.end('terminal');
    assert.strictEqual(ended.outcome, 'ended');
});

test('P4: no query string in the record, and the summary counts pauses, hand-overs and time', () => {
    const { time, pause, monitor } = rig();
    pause.pause(monitor.observe(throttle(60000)), 'script');
    time.advance(60000);
    const [record] = pause.records();
    assert.strictEqual(record.url, 'https://www.example.com/api/search');
    assert.doesNotMatch(JSON.stringify(record), /email/, 'the query is where sites put emails and search terms');
    pause.handOver({ action: 'hand-over', why: 'blocked', signal: { kind: 'blocked', status: 403,
        url: 'https://www.example.com/', retryAfterMs: null, signature: '403 Imperva' } }, 'agent');
    time.advance(120000);
    pause.end('recording-ended');
    const summary = pause.summary();
    assert.deepStrictEqual(summary, { count: 1, handOvers: 1, totalMs: 180000 });
    assert.strictEqual(pauseSummaryText(summary), 'paused once and handed over once (3 min in all)');
    assert.strictEqual(pauseSummaryText({ count: 0, handOvers: 0, totalMs: 0 }), null);
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
        process.stderr.write(`\n${failures.length} of ${queued.length} capture-pause tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All capture-pause tests passed (${passed})`);
})();
