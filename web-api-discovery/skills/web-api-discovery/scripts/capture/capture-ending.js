'use strict';

/**
 * capture-ending.js -- where the END of a capture has got to, and whether it is
 * still getting anywhere (#27).
 *
 * A recording ends in the recording process: it closes or detaches from the
 * browser, writes `raw.har`, then scrubs, verifies, digests and catalogues.
 * `stop` runs in ANOTHER process and can only wait for that. It used to wait a
 * fixed 60 seconds and then say "post-processing is still running", exit 6 --
 * on a 22 MB capture whose recording process finished normally a little later.
 * The caller could not tell three different things apart:
 *
 *   progressing     the ending registered and the work is getting somewhere
 *   stalled         the ending registered and nothing has moved for a stated time
 *   not-registered  the stop was asked for and the recorder never took it up
 *
 * This file is the one place that tells them apart. `stop` waits on it and
 * `status` asks it once, so the two cannot disagree.
 *
 * WHAT COUNTS AS A SIGN OF PROGRESS, and it is only these two:
 *
 *   1. THE ENDING RECORD in session.json, which the recording process writes
 *      when its ending is settled and again as each phase STARTS (`notePhase`).
 *      Post-processing is synchronous, so a phase is entered only when the one
 *      before it returned.
 *   2. THE FILES POST-PROCESSING ITSELF WRITES, by size and modification time
 *      (`isEndingArtifact`): the raw being written, the scrub's partial output
 *      growing, the scrubbed capture, the findings, the digest, the catalogue.
 *
 * WHAT DOES NOT, because each of these goes on advancing while the work is
 * hung: the record log (`raw.ndjson` grows for as long as the browser makes
 * requests), the agent's own logs (an agent can outlive the recording), the
 * heartbeat of run-with-heartbeat.js (a timer), the recorder's CPU time, and
 * the recorder merely being alive.
 *
 * HOW LONG NOTHING MAY MOVE is not one number. Measured on a 22.9 MB capture,
 * the scrub wrote nothing for its first 53 seconds (it reads the capture once
 * before it writes a byte) and the verify wrote nothing at all for its 12 -- so
 * a healthy capture is silent for stretches that grow with its size, and any
 * fixed window is the old defect again. The allowance is a floor plus a rate
 * per megabyte (`quietLimitMs`), stated in every message and in the JSON.
 *
 * EVERYTHING IS DERIVED FROM WHAT IS ON DISK -- the record's timestamps, the
 * files' modification times, the STOP file's time -- and the clock. So the
 * single-shot `status` gives the same answer `stop` does, and a capture that
 * stalled an hour before anybody asked is reported as stalled at once.
 *
 * WHEN THE ALLOWANCE IS COUNTED FROM is a separate question from when anything
 * last moved, and the two are kept apart. A `stop` whose own machine slept
 * through part of its wait counts the allowance again from when it woke -- its
 * recorder slept too -- but that is NOT a sign of progress and is never
 * reported as one: `lastProgressUtc` is evidence from the list above and
 * nothing else, and an interrupted wait says it was interrupted.
 *
 * Zero dependencies beyond Node. Time, sleeping and "is the recorder running"
 * are injected, so all of it is testable without a clock or a process.
 */

const fs = require('fs');
const path = require('path');

const { formatElapsed } = require(path.join(__dirname, 'run-with-heartbeat.js'));

const SESSION_FILE = 'session.json';
const STOP_SENTINEL = 'STOP';
const MB = 1024 * 1024;

const POLL_MS = 200;
// How long a running recorder gets to take up a stop. It looks for the STOP
// file five times a second once it is recording, so this is generous: the one
// stretch in which it is not looking is its own start-up, and that ends.
const REGISTER_LIMIT_MS = 60 * 1000;
// The least time nothing may move before it is called a stall. Sixty seconds is
// what `stop` always allowed, and for a small capture it was never the problem.
const QUIET_FLOOR_MS = 60 * 1000;
// And what a larger capture is allowed on top: fifteen seconds for each
// megabyte. Measured, the longest stretch with nothing to see was 2.3 s/MB (the
// scrub's first pass) on a development laptop; a whole scrub was 5 s/MB. The
// margin is deliberately wide. Calling a stall late costs somebody a wait they
// can watch counting up; calling one early reports a good capture as stuck.
const QUIET_MS_PER_MB = 15 * 1000;
// The AI catalogue pass prints nothing until it ends -- fifteen silent minutes
// on one measured capture -- and how long it takes follows the number of
// actions in the capture, not its size.
const AI_CATALOGUE_QUIET_MS = 30 * 60 * 1000;
// How often a wait says where it is: the cadence run-with-heartbeat.js uses.
const REPORT_EVERY_MS = 30 * 1000;
// Two polls this far apart mean the waiting process was not running in between
// -- a laptop asleep, most likely -- and then neither was the recorder. Polls
// are 200 ms apart, so nothing ordinary comes near it.
const SUSPENDED_GAP_MS = 10 * 1000;

const STATE_RECORDING = 'recording';
const STATE_STOP_PENDING = 'stop-pending';
const STATE_NOT_REGISTERED = 'not-registered';
const STATE_PROGRESSING = 'progressing';
const STATE_STALLED = 'stalled';
const STATE_COMPLETE = 'complete';
const STATE_NOT_PROCESSED = 'not-processed';
const STATE_RECORDER_GONE = 'recorder-gone';

// The two states there is something to wait for in.
const WAITING_STATES = new Set([STATE_STOP_PENDING, STATE_PROGRESSING]);

// The phases of an ending, in the order the recorder enters them. The names are
// what session.json and the JSON carry; the recorder takes them from here.
const PHASES = Object.freeze({
    FINALIZING: 'finalizing',
    SCRUB: 'scrub',
    VERIFY: 'verify',
    PUBLISH: 'publish',
    DIGEST: 'digest',
    CATALOGUE: 'catalogue',
    AI_CATALOGUE: 'ai-catalogue',
    COMPLETE: 'complete'
});
// What each phase is, in the words a message uses.
const PHASE_LABELS = {
    [PHASES.FINALIZING]: 'saving the recording',
    [PHASES.SCRUB]: 'scrubbing',
    [PHASES.VERIFY]: 'verifying the scrub',
    [PHASES.PUBLISH]: 'publishing the scrubbed capture',
    [PHASES.DIGEST]: 'building the digest',
    [PHASES.CATALOGUE]: 'writing the catalogue',
    [PHASES.AI_CATALOGUE]: 'the AI catalogue pass'
};

// The files a sign of progress is read from. Exact names and prefixes, never
// "whatever is in the directory" -- see the header for what must stay out.
//
// These repeat names capture-har.js and sanitize-har.js own, the way
// capture-store.js repeats them, rather than importing the recorder into a file
// the recorder imports. A name that drifts costs a sign of progress, not
// correctness: the phase record still moves, and the allowance covers a whole
// phase with nothing else to see.
const SESSION_ARTIFACTS = [
    (name) => name === 'raw.har',
    (name) => name === 'raw.har.annotating',
    (name) => name === 'scrubbed.har',
    (name) => name.startsWith('.scrubbed.har.partial-'),
    (name) => name.startsWith('scrub-findings') && name.endsWith('.json'),
    (name) => name.startsWith('scrubbed.rejected') && name.endsWith('.har')
];
const OUTPUT_ARTIFACTS = [
    (name) => name === 'scrubbed.har',
    (name) => name === 'scrub-findings.json',
    (name) => name === 'digest.json',
    (name) => name === 'catalogue.json',
    (name) => name.startsWith('.publishing-')
];

/** Is `name`, in the session directory or the output path, a file post-processing writes? */
function isEndingArtifact(name, where) {
    return (where === 'output' ? OUTPUT_ARTIFACTS : SESSION_ARTIFACTS).some((matches) => matches(name));
}

// ---------------------------------------------------------------------------
// The recorder's side
// ---------------------------------------------------------------------------

/**
 * Record on `session` that the ending has reached `phase`.
 *
 * The first call is the acknowledgement: it sets when the ending registered and
 * what ended the recording (`opts.by`), and those never move again. Every call
 * moves the phase and when it started.
 *
 * Only the object is changed. Writing it to session.json is the caller's, so
 * the phases themselves stay free of any knowledge of where a session lives --
 * `stop` runs them too, and `catalogue` runs the last of them on a capture that
 * has no session at all.
 */
function notePhase(session, phase, opts = {}) {
    const at = new Date(opts.now === undefined ? Date.now() : opts.now).toISOString();
    if (!session.ending) session.ending = { by: opts.by || null, registeredUtc: at };
    else if (opts.by && !session.ending.by) session.ending.by = opts.by;
    session.ending.phase = phase;
    session.ending.phaseStartedUtc = at;
    return session.ending;
}

// ---------------------------------------------------------------------------
// What is on disk
// ---------------------------------------------------------------------------

// How long a session.json that is THERE but cannot be read is tried again
// before it is given up on: about a quarter of a second in all.
const READ_ATTEMPTS = 10;
const READ_RETRY_MS = 25;

function sleepSync(ms) {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

/**
 * session.json, or null.
 *
 * Another process writes this file while it is read. The recorder stages its
 * ending writes and renames them, but a rename can briefly lock the file on
 * Windows, and a write whose rename failed falls back to rewriting in place --
 * truncate, then fill. So a file that is there and unreadable is tried again,
 * a little later, for a bounded time: retries with no pause between them all
 * land inside the same instant. One that is not there at all is not waited for.
 */
function readSession(sessionDir) {
    const file = path.join(sessionDir, SESSION_FILE);
    for (let attempt = 1; ; attempt++) {
        try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) {
            if (e.code === 'ENOENT' || attempt >= READ_ATTEMPTS) return null;
            sleepSync(READ_RETRY_MS);
        }
    }
}

function statOrNull(file) {
    try { return fs.statSync(file); } catch (e) { return null; }
}

function artifactsIn(dir, where) {
    let names;
    try { names = fs.readdirSync(dir); } catch (e) { return []; }
    const found = [];
    for (const name of names.sort()) {
        if (!isEndingArtifact(name, where)) continue;
        const stat = statOrNull(path.join(dir, name));
        if (stat && stat.isFile()) found.push({ name: `${where}:${name}`, size: stat.size, mtimeMs: stat.mtimeMs });
    }
    return found;
}

/**
 * Everything the evaluation is derived from, read once.
 *
 *   deps.isRecorderAlive(session)  is the recording process still running?
 *   deps.session                   the last session read, used when the file
 *                                  cannot be read this instant
 */
function readEvidence(sessionDir, deps = {}) {
    let session = readSession(sessionDir) || deps.session || null;
    const recorderAlive = !!(deps.isRecorderAlive && deps.isRecorderAlive(session));
    // A recorder that has exited wrote its last word before it went. Read
    // again, or one that finished between the read above and the question just
    // asked is taken for one that died with the work undone.
    if (!recorderAlive) session = readSession(sessionDir) || session;
    const stop = statOrNull(path.join(sessionDir, STOP_SENTINEL));
    const artifacts = artifactsIn(sessionDir, 'session');
    const outputPath = session && session.outputPath;
    if (outputPath && path.resolve(outputPath) !== path.resolve(sessionDir)) {
        artifacts.push(...artifactsIn(outputPath, 'output'));
    }
    // How much there is to chew through. The record log stands in for the raw
    // until the raw is written: it holds the same entries.
    const sizes = [session && session.harPath, session && session.recordLog]
        .map((file) => (file ? statOrNull(file) : null))
        .map((stat) => (stat ? stat.size : 0));
    const summarized = (session && session.summary && Number(session.summary.bytes)) || 0;
    return {
        session,
        recorderAlive,
        recorderPid: (session && session.pid) || null,
        stopRequestedMs: stop ? stop.mtimeMs : null,
        artifacts,
        captureBytes: Math.max(summarized, ...sizes),
        // What only a WATCHER knows; `waitForEnding` fills these in. The first
        // is evidence -- it SAW something move. The other two are only where
        // to count from, and are never reported as progress.
        observedChangeMs: null,
        watchingSinceMs: null,
        resumedMs: null
    };
}

// ---------------------------------------------------------------------------
// The evaluation
// ---------------------------------------------------------------------------

/** How long nothing may move, in `phase`, on a capture of `bytes`, before it is a stall. */
function quietLimitMs(phase, bytes) {
    const scaled = Math.max(QUIET_FLOOR_MS, Math.ceil((bytes || 0) / MB) * QUIET_MS_PER_MB);
    return phase === PHASES.AI_CATALOGUE ? Math.max(scaled, AI_CATALOGUE_QUIET_MS) : scaled;
}

function timeOf(utc) {
    const ms = utc ? Date.parse(utc) : NaN;
    return Number.isFinite(ms) ? ms : null;
}

const utcOf = (ms) => (ms === null || ms === undefined ? null : new Date(ms).toISOString());

/**
 * The latest of `times` that is not ahead of the clock, or null.
 *
 * A TIME IN THE FUTURE IS NEVER USED. A clock set back, or a file on a share
 * with its own idea of the time, would otherwise hold the silence at zero and
 * nothing could stall.
 */
function latest(times, nowMs) {
    const usable = times.filter((ms) => typeof ms === 'number' && Number.isFinite(ms) && ms <= nowMs);
    return usable.length ? Math.max(...usable) : null;
}

/**
 * The state of a capture's ending at `nowMs`.
 *
 * Pure: `evidence` is what `readEvidence` returns, plus what only a watcher
 * knows (`waitForEnding`). The result is what `status` and `stop` print as
 * `ending`:
 *
 *   lastProgressUtc    the last SIGN OF PROGRESS, and only that: a time the
 *                      recorder wrote, a file post-processing wrote, a file the
 *                      watcher saw grow. Null when nothing usable exists.
 *   quietSeconds       how long the allowance has been running, and
 *   quietLimitSeconds  how long it may: stalled (or not-registered) is exactly
 *                      quietSeconds >= quietLimitSeconds.
 *   waitResumedUtc     set when the two differ for a reason that is NOT
 *                      progress -- the watcher itself was interrupted after the
 *                      last sign, and the allowance is counted from there.
 *   asOfUtc            the clock all of it was read against.
 */
function evaluate(evidence, nowMs) {
    const session = evidence.session || {};
    const record = session.ending || null;
    const phase = (record && record.phase) || null;
    const registeredMs = timeOf(record && record.registeredUtc) || timeOf(session.endedUtc);
    const verdict = {
        state: null,
        phase,
        endedBy: (record && record.by) || session.endedBy || null,
        asOfUtc: utcOf(nowMs),
        stopRequestedUtc: utcOf(evidence.stopRequestedMs),
        registeredUtc: utcOf(registeredMs),
        phaseStartedUtc: (record && record.phaseStartedUtc) || null,
        lastProgressUtc: null,
        waitResumedUtc: null,
        quietSeconds: null,
        quietLimitSeconds: null,
        captureBytes: evidence.captureBytes || 0,
        recorderAlive: !!evidence.recorderAlive,
        recorderPid: evidence.recorderPid || null
    };
    const settle = (state) => Object.assign(verdict, { state });
    // WHERE THE ALLOWANCE IS COUNTED FROM: the last thing that really happened
    // (`sinceMs`) -- unless the watcher was itself interrupted after it, or
    // nothing on disk is usable at all. Neither exception is evidence of
    // anything, so neither is written into `lastProgressUtc`.
    const countedFrom = (sinceMs) => {
        const resumedMs = latest([evidence.resumedMs], nowMs);
        if (resumedMs !== null && (sinceMs === null || resumedMs > sinceMs)) {
            verdict.waitResumedUtc = utcOf(resumedMs);
            return resumedMs;
        }
        if (sinceMs !== null) return sinceMs;
        const began = latest([evidence.watchingSinceMs], nowMs);
        // A single look has nothing to count from, and says so rather than
        // inventing a silence.
        return began === null ? nowMs : began;
    };
    const overdue = (fromMs, limitMs) => {
        verdict.quietSeconds = Math.max(0, Math.floor((nowMs - fromMs) / 1000));
        verdict.quietLimitSeconds = Math.round(limitMs / 1000);
        return nowMs - fromMs >= limitMs;
    };

    if (session.postProcess) {
        // Finished, whoever finished it: `stop` runs the phases itself when the
        // recorder died part-way, and the record still names where it died.
        verdict.phase = PHASES.COMPLETE;
        return settle(STATE_COMPLETE);
    }
    if (session.endedUtc && session.cancelled) return settle(STATE_NOT_PROCESSED);
    if (!evidence.recorderAlive) return settle(STATE_RECORDER_GONE);

    if (registeredMs === null) {
        if (evidence.stopRequestedMs === null || evidence.stopRequestedMs === undefined) {
            return settle(STATE_RECORDING);
        }
        const askedMs = latest([evidence.stopRequestedMs], nowMs);
        return settle(overdue(countedFrom(askedMs), REGISTER_LIMIT_MS)
            ? STATE_NOT_REGISTERED : STATE_STOP_PENDING);
    }

    const lastProgressMs = latest([registeredMs, timeOf(verdict.phaseStartedUtc),
        timeOf(session.endedUtc), evidence.observedChangeMs,
        ...(evidence.artifacts || []).map((a) => a.mtimeMs)], nowMs);
    verdict.lastProgressUtc = utcOf(lastProgressMs);
    return settle(overdue(countedFrom(lastProgressMs), quietLimitMs(phase, verdict.captureBytes))
        ? STATE_STALLED : STATE_PROGRESSING);
}

/** One reading of a session directory: the evidence, and what it adds up to. */
function look(sessionDir, deps = {}) {
    const evidence = readEvidence(sessionDir, deps);
    return { evidence, session: evidence.session, verdict: evaluate(evidence, (deps.now || Date.now)()) };
}

// ---------------------------------------------------------------------------
// The wait
// ---------------------------------------------------------------------------

/** What has to differ between two polls for something to have moved. */
function progressKey(evidence) {
    const session = evidence.session || {};
    const record = session.ending || {};
    return JSON.stringify([record.phase, record.phaseStartedUtc, session.endedUtc,
        evidence.artifacts.map((a) => [a.name, a.size, a.mtimeMs])]);
}

/**
 * Wait for as long as there is something to wait for, and return the state it
 * ended in: complete, not-processed, recorder-gone, stalled or not-registered.
 *
 * There is no overall limit. The wait ends when the work does, or when nothing
 * has moved for the allowance -- and a capture cannot go on showing progress
 * for ever without finishing.
 *
 * It watches as well as reads, and a watcher knows two things a single look
 * cannot. They are kept apart, because only the first is evidence:
 *
 *   - A file that GREW since the last poll, even where its modification time
 *     did not move -- which is what some filesystems do with a file still open
 *     for writing. That is a sign of progress like any other.
 *   - That the watcher itself was not running. A laptop that slept through ten
 *     minutes of this wait has a recorder that slept through them too; by the
 *     clock alone that is ten minutes of silence, and a healthy capture would be
 *     told it had stalled the moment the lid opened. The ALLOWANCE is counted
 *     again from the first poll after such a gap -- and nothing more: it is not
 *     a sign of progress, `lastProgressUtc` does not move, and the verdict says
 *     the wait was interrupted (`waitResumedUtc`).
 *
 *   deps.now, deps.sleep         the clock, injected for tests
 *   deps.isRecorderAlive         asked on every poll: a recorder that exits is
 *                                not waited on
 *   deps.onWaiting(verdict, why) called when the state or phase changes
 *                                ('change'), and every thirty seconds of
 *                                waiting whether or not it has ('interval')
 */
async function waitForEnding(sessionDir, deps = {}) {
    const now = deps.now || Date.now;
    const sleep = deps.sleep || ((ms) => new Promise((resolve) => setTimeout(resolve, ms)));
    const watchingSinceMs = now();
    let session = deps.session || null;
    let lastKey = null;
    let lastPollMs = null;
    let observedChangeMs = null;
    let resumedMs = null;
    let said = null;
    let intervalSaidMs = watchingSinceMs;
    for (;;) {
        const evidence = readEvidence(sessionDir, { isRecorderAlive: deps.isRecorderAlive, session });
        session = evidence.session;
        const at = now();
        const key = progressKey(evidence);
        if (lastKey !== null && key !== lastKey) observedChangeMs = at;
        if (lastPollMs !== null && at - lastPollMs > SUSPENDED_GAP_MS) resumedMs = at;
        lastKey = key;
        lastPollMs = at;
        Object.assign(evidence, { observedChangeMs, watchingSinceMs, resumedMs });
        const verdict = evaluate(evidence, at);
        if (!WAITING_STATES.has(verdict.state)) return { verdict, session, evidence };
        if (deps.onWaiting) {
            const where = `${verdict.state}/${verdict.phase}`;
            if (where !== said) {
                said = where;
                deps.onWaiting(verdict, 'change');
            }
            // Its own clock, not "since anything was last said": phases that
            // change faster than the interval would otherwise starve it.
            if (at - intervalSaidMs >= REPORT_EVERY_MS) {
                intervalSaidMs = at;
                deps.onWaiting(verdict, 'interval');
            }
        }
        await sleep(POLL_MS);
    }
}

// ---------------------------------------------------------------------------
// What is said
// ---------------------------------------------------------------------------

const seconds = (n) => formatElapsed((n || 0) * 1000);

function megabytes(bytes) {
    return bytes >= MB ? `${(bytes / MB).toFixed(1)} MB` : `${(bytes / 1024).toFixed(1)} KB`;
}

function phaseLabel(verdict) {
    return PHASE_LABELS[verdict.phase] || 'post-processing';
}

/** How long before the verdict was reached `utc` was -- "45s", "4m30s" -- or null. */
function ago(verdict, utc) {
    const then = timeOf(utc);
    const asOf = timeOf(verdict.asOfUtc);
    return then === null || asOf === null || then > asOf ? null : seconds(Math.floor((asOf - then) / 1000));
}

// Said wherever the allowance was counted from somewhere other than the last
// sign of progress, so nobody reads the shorter time as something having moved.
const INTERRUPTED = 'this wait was interrupted (the machine slept?)';

/** One line for a wait that is still going. */
function waitingLine(verdict) {
    const counted = seconds(verdict.quietSeconds);
    const limit = seconds(verdict.quietLimitSeconds);
    if (verdict.state === STATE_STOP_PENDING) {
        return `capture-har: stop requested -- waiting for the recording process (pid ${verdict.recorderPid}) ` +
            `to take it up (${counted} so far${verdict.waitResumedUtc ? `, counted from when ${INTERRUPTED} and resumed` : ''})`;
    }
    const head = `capture-har: the stop registered and post-processing is progressing -- ${phaseLabel(verdict)}; `;
    const since = ago(verdict, verdict.lastProgressUtc);
    if (verdict.waitResumedUtc) {
        return `${head}${since ? `last sign of progress ${since} ago, but ` : ''}${INTERRUPTED} and resumed ` +
            `${counted} ago: a stall is called ${limit} after that. Still waiting.`;
    }
    if (!since) {
        return `${head}no time on disk is usable against this clock, so the silence is counted from when ` +
            `this wait began ${counted} ago (a stall is called at ${limit}). Still waiting.`;
    }
    return `${head}last sign of progress ${since} ago (a stall is called at ${limit}). Still waiting.`;
}

/** The lines for a wait that ended with post-processing stalled. */
function stalledLines(verdict, session) {
    const saved = session && session.endedUtc;
    const since = ago(verdict, verdict.lastProgressUtc);
    // With no usable time on disk there is no "since anything moved" to state,
    // only how long the allowance ran -- and from where it was counted.
    const counted = `this wait ${verdict.waitResumedUtc ? 'resumed' : 'began'} ${seconds(verdict.quietSeconds)} ago`;
    const lines = [
        `capture-har: post-processing has STALLED -- no sign of progress ${since ? `for ${since}` : `since ${counted}`}.`,
        `  The stop registered (${verdict.endedBy || 'ending'}, ${verdict.registeredUtc}); the last phase entered ` +
            `was ${phaseLabel(verdict)}.`,
        `  A capture of ${megabytes(verdict.captureBytes)} is allowed ${seconds(verdict.quietLimitSeconds)} ` +
            'between signs of progress.'
    ];
    if (!since) {
        lines.push('  No time on disk is usable against this clock (it may have been set back).');
    }
    if (verdict.waitResumedUtc) {
        lines.push(`  The allowance was counted from ${verdict.waitResumedUtc}: ${INTERRUPTED} and resumed then.`);
    }
    if (verdict.phase === PHASES.AI_CATALOGUE) {
        lines.push('  The AI catalogue pass prints nothing until it ends, so this may be a very long pass ' +
            'rather than a hung one;',
        '  the scrubbed capture and the digest are already complete.');
    }
    if (verdict.phase === PHASES.FINALIZING && verdict.endedBy === 'ctrl-c') {
        // The one ending that waits on a person before it goes any further.
        lines.push('  It was ended with Ctrl+C, so its own terminal may be asking whether to finish or cancel.');
    }
    lines.push(
        `  The recording process (pid ${verdict.recorderPid}) is still running and still owns the work, so ` +
            'nothing was taken over here.',
        saved
            ? `  raw:       ${session.harPath}  (saved and kept)`
            : '  The recording itself is still being saved; nothing was assembled here.',
        '  Look at that process. `status` reports this same state at any time. Running stop again waits',
        '  only if something has moved since; if nothing has, it says this again at once.');
    return lines;
}

/** The lines for a stop the recorder never took up. */
function notRegisteredLines(verdict) {
    const asked = ago(verdict, verdict.stopRequestedUtc) || seconds(verdict.quietSeconds);
    return [
        `capture-har: the stop NEVER REGISTERED -- the recording process (pid ${verdict.recorderPid}) is ` +
            `running, and has not taken up the stop asked for ${asked} ago.`,
        ...(verdict.waitResumedUtc
            ? [`  It was given ${seconds(verdict.quietLimitSeconds)} from ${verdict.waitResumedUtc}: ${INTERRUPTED} and resumed then.`]
            : []),
        '  Nothing was stopped, assembled or post-processed: the recording is still going.',
        '  The request -- the STOP file in the session directory -- is left in place, so the recording ends',
        '  as soon as that process next looks for it. `status` shows whether it has. Running stop again waits',
        '  only once it has; until then it says this again at once.',
        '  To end the recording another way, press ENTER at its own terminal or close its browser window.'
    ];
}

/**
 * What to say when a wait ended with the recording process still running and
 * its work unfinished -- or null when it did not: the ending is finished, or
 * the recorder has exited and `stop` has work of its own to do.
 */
function unfinishedLines(verdict, session) {
    if (verdict.state === STATE_STALLED) return stalledLines(verdict, session);
    if (verdict.state === STATE_NOT_REGISTERED) return notRegisteredLines(verdict);
    if (verdict.state === STATE_RECORDING) {
        // Only reachable if something removed the STOP file `stop` had written.
        return [
            `capture-har: the stop request is gone -- the STOP file was removed before the recording process ` +
                `(pid ${verdict.recorderPid}) took it up.`,
            '  Nothing was stopped: the recording is still going. Run stop again to ask again.'
        ];
    }
    return null;
}

module.exports = {
    notePhase,
    readSession,
    readEvidence,
    evaluate,
    look,
    waitForEnding,
    quietLimitMs,
    isEndingArtifact,
    waitingLine,
    unfinishedLines,
    WAITING_STATES,
    PHASES,
    STATE_RECORDING,
    STATE_STOP_PENDING,
    STATE_NOT_REGISTERED,
    STATE_PROGRESSING,
    STATE_STALLED,
    STATE_COMPLETE,
    STATE_NOT_PROCESSED,
    STATE_RECORDER_GONE,
    REGISTER_LIMIT_MS,
    QUIET_FLOOR_MS,
    QUIET_MS_PER_MB,
    AI_CATALOGUE_QUIET_MS,
    REPORT_EVERY_MS,
    POLL_MS
};
