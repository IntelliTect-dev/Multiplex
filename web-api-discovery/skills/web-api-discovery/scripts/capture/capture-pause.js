'use strict';

/**
 * capture-pause.js -- pausing the WHOLE capture, and handing it to a person.
 *
 * pacing.js decides WHAT a site's signal means (pause for so long, or hand
 * over). This file makes the decision TRUE in the browser and keeps the record:
 *
 *   createSiteHold      while a pause runs, this capture's requests to the
 *                       site WAIT in the browser and go on, one at a time and
 *                       spaced like any other action, when it ends.
 *                       Enforced at the browser, not by trusting whatever is
 *                       driving: an agent told to wait that clicks anyway
 *                       clicks into a request that does not leave.
 *   createCapturePause  one pause or hand-over at a time: when it started,
 *                       until when, why, who was driving, how it ended. The
 *                       timer that ends a pause and carries on by itself.
 *
 * WHAT THE HOLD IS AND IS NOT.
 *   - Requests already IN FLIGHT when a pause begins finish: the hold is
 *     installed at that moment and only sees requests made after it.
 *   - Only the SITE's requests are held -- the capture's host and its
 *     subdomains, and any page load -- the same set pacing.classifyResponse
 *     reads signals from. A tracker on another host is not the site.
 *   - In somebody else's browser (attached or native), only this capture's
 *     tabs (#85): another tab of the operator's is not held.
 *   - It is installed for the pause and REMOVED after it, never left in
 *     place: request interception disables the browser's HTTP cache, and in
 *     native or attached mode the context is the operator's whole browser.
 *   - A HAND-OVER DROPS IT. The person answering a check needs the site; what
 *     automation queued during the pause is aborted rather than sent -- and
 *     aborted BEFORE the route is removed, because removing it continues
 *     whatever is still held.
 *   - Every ending drops it too: a recording that ends mid-pause sends
 *     nothing the site asked to wait for.
 *   - It is BOUNDED: one pause holds at most HOLD_MAX requests, and aborts the
 *     rest, so a page polling through a long wait is not a flood afterwards.
 *
 * THE BOUNDARY (SKILL.md, "Pacing, throttling, and where the line is") is
 * unchanged by any of this: a pause honours what the site asked; nothing here
 * solves a check, retries a block, or disguises anything. And nothing here
 * ends a process -- an agent is TOLD to wait (capture-har.js, driveWithAgent),
 * and the browser enforces it.
 *
 * Zero dependencies. The browser is reached through two injected functions,
 * and time through injected `now`/`setTimeout`, so all of it is testable
 * without a browser or a clock.
 */

const pacing = require('./pacing.js');

/**
 * THE MOST REQUESTS ONE PAUSE HOLDS. A page that polls every few seconds
 * through a fifteen-minute backoff, or a day-long Retry-After, would otherwise
 * queue hundreds or thousands. Past this, a request is ABORTED at once rather
 * than held: the site never sees it either way, and a page that needs it asks
 * again. Held requests are released one at a time, so this also bounds how
 * long that takes.
 */
const HOLD_MAX = 20;

/**
 * The browser-level hold.
 *
 *   opts.install(handler)    route every request through `handler(route)` --
 *                            capture-har.js passes context.route('**', ...)
 *   opts.uninstall(handler)  stop routing -- context.unroute
 *   opts.holds(request)      is this request the site's, from this capture?
 *                            May be async. A predicate that throws HOLDS: when
 *                            in doubt during a pause, the site is not sent it.
 *   opts.space()             the gap before each held request goes on once
 *                            the pause is over -- capture-har.js passes the
 *                            pacer's action gap. Default: none (tests).
 *   opts.max                 how many one pause holds (HOLD_MAX)
 *   opts.onOverflow(max)     the first request past `max` in a pause was
 *                            aborted -- said once a pause, not once a request
 *   opts.onError(e, installing)  the route could not be installed (or
 *                            removed): told, so a pause is never announced as
 *                            held when it is not
 *
 * `route` is Playwright's shape: request(), continue(), abort(code).
 *
 * THE ORDER THE ROUTE GOES IN. Playwright CONTINUES every request still in a
 * handler when that handler's route is removed. So the route is removed only
 * once every handler that met a pause has SETTLED -- its continue or abort
 * delivered to the browser. Removed first, a dropped request would be sent.
 *
 * WHEN A PAUSE ENDS, what it held goes on ONE AT A TIME, `space()` apart, in
 * the order it was asked for -- not all at once onto a site that has just
 * asked for a slowdown. Chosen over aborting the held requests and letting the
 * page ask again: that is no simpler here, and it would turn "pause until the
 * wait is up, then carry on" into "then fail", losing whatever an agent or a
 * page asked for during the pause. A hand-over or an ending during that drain
 * aborts what has not gone yet; a new pause holds it again until that one ends.
 */
function createSiteHold(opts) {
    const install = opts.install;
    const uninstall = opts.uninstall;
    const holds = opts.holds || (() => true);
    const space = opts.space || (() => undefined);
    const max = opts.max === undefined ? HOLD_MAX : opts.max;
    let gate = null;
    let installed = false;
    let routing = Promise.resolve();
    let releasing = Promise.resolve();
    // Bumped by every drop: a request that met a pause before a drop is
    // aborted, however far it got.
    let drops = 0;
    const held = new Set();
    // Every handler call that met a pause and has not settled (see THE ORDER).
    const handling = new Set();
    let total = 0;
    let failures = 0;
    let overflowed = 0;

    const pass = (route) => Promise.resolve().then(() => route.continue()).catch(() => {});
    const abort = (route) => Promise.resolve().then(() => route.abort('blockedbyclient')).catch(() => {});

    /** One held request's turn in the drain, `space()` after the one before. */
    function queueRelease(entry) {
        releasing = releasing.then(async () => {
            if (entry.epoch !== drops) { entry.decide('drop'); return; }
            try { await space(); } catch (e) { void e; }
            if (entry.epoch !== drops) { entry.decide('drop'); return; }
            // A NEW PAUSE began while this one waited its turn: held again.
            if (gate) { gate.entries.push(entry); return; }
            entry.decide('release');
        });
    }

    async function judge(route, g) {
        const epoch = drops;
        let mine = true;
        try { mine = !!(await holds(route.request())); } catch (e) { void e; mine = true; }
        if (!mine) return pass(route);
        // A hand-over or an ending came while this was being judged.
        if (epoch !== drops) return abort(route);
        if (held.size >= max) {
            overflowed++;
            if (g.overflowed++ === 0 && opts.onOverflow) opts.onOverflow(max);
            return abort(route);
        }
        const entry = { epoch };
        const outcome = new Promise((resolve) => { entry.decide = resolve; });
        held.add(entry);
        total++;
        // The pause this request met may have ended while it was judged.
        if (g.over) queueRelease(entry); else g.entries.push(entry);
        const decided = await outcome;
        held.delete(entry);
        return decided === 'drop' ? abort(route) : pass(route);
    }

    function handler(route) {
        // The gate as it was when the request ARRIVED.
        const g = gate;
        if (!g) return pass(route);
        const p = judge(route, g);
        handling.add(p);
        const forget = () => { handling.delete(p); };
        p.then(forget, forget);
        return p;
    }

    function setRouting(on) {
        routing = routing.then(async () => {
            // Only for a pause still running when this step comes round.
            if (on && !installed && gate) { installed = true; await install(handler); }
            if (!on && installed) {
                while (handling.size) await Promise.allSettled([...handling]);
                if (gate) return;   // a new pause began meanwhile, and keeps the route
                installed = false;
                await uninstall(handler);
            }
        }).catch((e) => {
            // SAID, never swallowed: a pause announced as held that is not.
            if (on) { installed = false; failures++; }
            if (opts.onError) opts.onError(e, on);
        });
        return routing;
    }

    function end(outcome) {
        if (outcome === 'drop') {
            drops++;
            for (const entry of held) entry.decide('drop');
        }
        const g = gate;
        if (g) {
            gate = null;
            g.over = true;
            if (outcome === 'release') for (const entry of g.entries) queueRelease(entry);
            g.entries = [];
        }
        return setRouting(false);
    }

    return {
        /** Hold the site's requests from now until release() or drop(). */
        start() {
            if (!gate) gate = { entries: [], over: false, overflowed: 0 };
            return setRouting(true);
        },
        /** The pause is over: what was held goes on, one at a time, `space()` apart. */
        release: () => end('release'),
        /** A person is taking over, or the recording ended: what is held is aborted. */
        drop: () => end('drop'),
        active: () => !!gate,
        heldCount: () => held.size,
        totalHeld: () => total,
        /** How many requests were aborted because a pause already held `max`. */
        overflowed: () => overflowed,
        /** How many times the hold could not be installed (opts.onError was told each time). */
        failures: () => failures,
        /** Resolves once what was held has gone on or been aborted, and the route is in place or removed. */
        settled: () => routing
    };
}

/**
 * One pause or hand-over at a time, and the record of every one.
 *
 *   opts.hold     a createSiteHold (or null: nothing to hold, e.g. in a test)
 *   opts.pacer    pacing.js's pacer, whose own steps wait out a pause
 *   opts.now, opts.setTimeout, opts.clearTimeout   injected time
 *   opts.onTimerResume(record)   a pause ran out and the capture carried on
 *
 * A RECORD, as written to session.json under `pauses`:
 *   kind        'pause' | 'hand-over'
 *   reason      what the site did, in words; `status`, `url` (origin and path
 *               only -- never the query), `signature`
 *   driver      who was driving: agent | agent-unmessaged | agent-unstarted |
 *               script
 *   startedUtc, untilUtc (a pause), endedUtc, pausedMs
 *   waitMs, named   (a pause) how long, and whether the site named it
 *   resumedBy   timer | phone | terminal | hand-over (a pause a hand-over
 *               cut short) | recording-ended
 *   outcome     continued | ended
 */
function createCapturePause(opts = {}) {
    const now = opts.now || Date.now;
    const setTimer = opts.setTimeout || setTimeout;
    const clearTimer = opts.clearTimeout || clearTimeout;
    const hold = opts.hold || null;
    const pacer = opts.pacer || null;
    const onTimerResume = opts.onTimerResume || (() => {});
    const records = [];
    let current = null;
    let timer = null;

    const iso = (ms) => new Date(ms).toISOString();

    function base(kind, signal, driver) {
        return {
            kind,
            signature: signal.signature,
            status: signal.status,
            url: pacing.whereOf(signal.url),
            driver,
            startedUtc: iso(now())
        };
    }

    function close(resumedBy, outcome) {
        if (!current) return null;
        if (timer) { clearTimer(timer); timer = null; }
        const record = current;
        current = null;
        const t = now();
        record.endedUtc = iso(t);
        record.pausedMs = Math.max(0, t - Date.parse(record.startedUtc));
        record.resumedBy = resumedBy;
        record.outcome = outcome;
        return record;
    }

    function schedule(untilMs) {
        if (timer) clearTimer(timer);
        timer = setTimer(() => {
            timer = null;
            const record = resume('timer');
            if (record) onTimerResume(record);
        }, Math.max(0, untilMs - now()));
        if (timer && typeof timer.unref === 'function') timer.unref();
    }

    /**
     * Pause the capture for a throttle decision from pacing.js. A pause
     * already running is extended (decision.extended); a hand-over running
     * is left alone -- the person is driving.
     */
    function pause(decision, driver) {
        const until = decision.until === undefined ? now() + decision.waitMs : decision.until;
        if (current && current.kind === 'hand-over') return null;
        if (current) {
            if (until > Date.parse(current.untilUtc)) {
                current.untilUtc = iso(until);
                current.waitMs = until - Date.parse(current.startedUtc);
                if (pacer) pacer.pauseFor(until - now());
                schedule(until);
            }
            return current;
        }
        const record = Object.assign(base('pause', decision.signal, driver), {
            reason: decision.named || decision.signal.retryAfterMs !== null
                ? `the site asked for a wait (${decision.signal.signature})`
                : `the site is throttling and named no wait (${decision.signal.signature}); backing off`,
            named: decision.signal.retryAfterMs !== null,
            waitMs: decision.waitMs,
            untilUtc: iso(until)
        });
        records.push(record);
        current = record;
        if (hold) hold.start();
        if (pacer) pacer.pauseFor(until - now());
        schedule(until);
        return record;
    }

    /** Hand the capture to a person. Ends any pause first: the person needs the site. */
    function handOver(decision, driver) {
        if (current && current.kind === 'hand-over') return current;
        close('hand-over', 'continued');
        if (hold) hold.drop();
        if (pacer) pacer.resumeNow();
        const record = Object.assign(base('hand-over', decision.signal, driver), {
            reason: pacing.handOverReason(decision),
            why: decision.why
        });
        records.push(record);
        current = record;
        return record;
    }

    /** The pause ran out, or the person handed back: carry on. */
    function resume(by) {
        if (!current) return null;
        const wasPause = current.kind === 'pause';
        const record = close(by, 'continued');
        if (wasPause && hold) hold.release();
        if (pacer) pacer.resumeNow();
        return record;
    }

    /** The recording is ending (or the person chose to end it). Nothing held is sent. */
    function end(by) {
        const record = close(by, 'ended');
        if (hold) hold.drop();
        return record;
    }

    return {
        pause,
        handOver,
        resume,
        end,
        active: () => !!current,
        current: () => current,
        records: () => records.slice(),
        /** For the machine-readable line and the closing summary. */
        summary() {
            const pauses = records.filter((r) => r.kind === 'pause');
            const handOvers = records.filter((r) => r.kind === 'hand-over');
            const total = records.reduce((sum, r) => sum + (r.pausedMs || 0), 0);
            return { count: pauses.length, handOvers: handOvers.length, totalMs: total };
        }
    };
}

/** The closing summary line, or null when nothing paused. */
function pauseSummaryText(summary) {
    if (!summary || (!summary.count && !summary.handOvers)) return null;
    const parts = [];
    if (summary.count) parts.push(`paused ${summary.count === 1 ? 'once' : `${summary.count} times`}`);
    if (summary.handOvers) parts.push(`handed over ${summary.handOvers === 1 ? 'once' : `${summary.handOvers} times`}`);
    return `${parts.join(' and ')} (${pacing.describeDuration(summary.totalMs)} in all)`;
}

module.exports = {
    HOLD_MAX,
    createSiteHold,
    createCapturePause,
    pauseSummaryText
};
