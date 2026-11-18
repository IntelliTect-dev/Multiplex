'use strict';

/**
 * WHICH RUN MADE A CAPTURE (issue #26).
 *
 * The capture store is shared and append-only, and several sessions record
 * into it at once. Nothing in a capture said which of them made it: directory
 * names are start times, `session.json`'s modified time is when post-processing
 * finished, and the two orderings do not even agree -- so no time window
 * selects one session's captures. `describe` was the only key that survived
 * into the artifact, and a selection built on it over-matched on its first real
 * use (72 files where the run was 69) and could not separate three attempts
 * that shared one description.
 *
 * So a capture records TWO identities, at two grains, because those were two
 * different failures:
 *
 *   captureId   one per recorder invocation. "Which attempt is this?"
 *   run         everything ONE invoking session started. "Which captures are
 *               mine?"
 *
 * `run` GROUPS and `captureId` IDENTIFIES. Anything that acts on a capture
 * acts on one capture.
 *
 * ## What a run is, and why it is not a fixed ancestor
 *
 * No caller supplies anything -- an option to pass a run id is an option, and
 * a session that had to remember to pass it would be the operator discipline
 * the original report asked to stop relying on. The recorder reads its own
 * process ancestry instead.
 *
 * Measured on the machine this was written on:
 *
 *   recorder <- pwsh <- cmd <- claude (one per session) <- claude (shared) <- sihost
 *
 * The direct parent is a new process for every command an agent runs, so
 * "the parent" would make every capture its own run. The top of the chain is
 * shared by every session, so "the root" would make them all one. The session
 * sits in the middle, at no fixed depth -- the same session reaches the
 * recorder through `pwsh <- cmd` from one command tool and through three
 * `bash` processes from another.
 *
 * What does not vary is the SHAPE: command shells, then a program that is not
 * one. So:
 *
 *   1. Walk up through the unbroken chain of command shells above the recorder
 *      to the first process that is not a shell.
 *   2. With a terminal on stdin and at least one shell, the run is the
 *      OUTERMOST of those shells: the terminal's own shell.   basis: terminal
 *   3. Otherwise the run is that first non-shell process: an agent session, a
 *      driving script, a CI job worker.                      basis: process
 *   4. If nothing above the shells could be read, the run is the outermost
 *      shell.                                                basis: shell
 *      And if no ancestor could be read at all there is NO run, with a reason.
 *
 * NO CLOCK IS INVOLVED, deliberately. "Skip an ancestor that started moments
 * ago" reads well and is unstable: a driving script is moments old for its
 * first capture and minutes old for its fifth, so one script would be two runs.
 *
 * ## Which way it is wrong
 *
 * Rule 2 is never coarser than rule 3, and a wrapper this does not recognise
 * becomes its own run. FINER IS THE SAFE DIRECTION: a run that is too small
 * selects too little, and one that is too large hands a bulk action somebody
 * else's captures -- which is the defect. The cases where it is too coarse are
 * known, pinned in capture-run.test.js (R9) and written down in SKILL.md:
 *
 *   - no terminal on stdin, inside an interactive terminal (a background job,
 *     redirected stdin): the run is the terminal APPLICATION, shared by every
 *     tab;
 *   - a scheduler or service that launches through shells only;
 *   - a recorder whose launcher has already exited, on a system that re-homes
 *     orphans under a long-lived process.
 *
 * ## What is recorded, and what is only read
 *
 * A token is 12 hex characters of SHA-256 over a process id and that process's
 * operating-system start time, so an id the system reused is a different run.
 * The record is tokens, a basis and nothing else. Process NAMES are read, to
 * tell a shell from a program, and are not stored; no command line, user name,
 * host name or environment content is read at all.
 *
 * `lineage` -- the token of every ancestor that was read, nearest first -- is
 * kept in `session.json` and deliberately goes no further. It is what makes a
 * wrong anchor recoverable later. It is not carried into the listing, because
 * a containment filter on a high ancestor matches every session on the machine.
 */

const crypto = require('crypto');
const fs = require('fs');
const { spawn } = require('child_process');

const TOKEN_LENGTH = 12;
/** How many ancestors are read. A real chain is five to ten deep. */
const MAX_DEPTH = 16;
/**
 * How long a lookup may take before the capture carries on without a run.
 *
 * Generous on purpose: the lookup is started early and awaited only when
 * `session.json` is first written, by which time a browser has been launched.
 * Measured at about 3 s on a machine running several agent sessions at once.
 */
const DEFAULT_TIMEOUT_MS = 10000;

const BASIS_TERMINAL = 'terminal';
const BASIS_PROCESS = 'process';
const BASIS_SHELL = 'shell';

/**
 * Why a capture has no run. A FIXED VOCABULARY, and that is a privacy property
 * as much as a tidy one: an error message can carry a path with a user name in
 * it, and this record is printed into listings and evidence.
 */
const REASONS = Object.freeze({
    UNSUPPORTED: 'process ancestry is not read on this platform',
    TIMED_OUT: 'reading the process ancestry took too long',
    FAILED: 'the process ancestry could not be read',
    NO_INVOKER: 'no invoking process could be identified'
});

/**
 * Command shells, and only command shells.
 *
 * Kept short on purpose. A wrapper that is NOT listed -- `sudo`, `npm`, a
 * heartbeat runner -- reads as a program, so it becomes its own run: too fine,
 * which is the safe way to be wrong. Listing things that are not shells to
 * make more cases group would trade that for the unsafe direction.
 */
const SHELLS = new Set([
    'pwsh', 'powershell', 'cmd',
    'bash', 'sh', 'zsh', 'dash', 'ash', 'ksh', 'csh', 'tcsh', 'fish', 'nu'
]);

/** A capture id: one per recorder invocation, and nothing derived from anything. */
function newCaptureId() {
    return crypto.randomUUID();
}

/**
 * Is this process name a command shell?
 *
 * Forgiving about spelling because three operating systems spell it three
 * ways: `pwsh.exe`, a full path, and `-bash` -- a login shell, which the
 * kernel reports with a leading dash.
 */
function isShell(name) {
    if (typeof name !== 'string') return false;
    const base = name.split(/[\\/]/).pop().toLowerCase().replace(/\.exe$/, '').replace(/^-/, '');
    return SHELLS.has(base);
}

/** The token for one process: who it is, and nothing about what it is. */
function tokenFor(proc) {
    return crypto.createHash('sha256')
        .update(`${proc.pid}:${proc.start}`)
        .digest('hex')
        .slice(0, TOKEN_LENGTH);
}

function unidentified(reason) {
    return { id: null, reason };
}

/**
 * The run, from a chain of ancestors -- nearest first, the recorder itself not
 * included. See the rule at the top of this file.
 *
 * Pure: it is given the chain and whether stdin is a terminal, and reads
 * nothing. That is what lets every shape that was measured be a test.
 */
function resolveRun(chain, options = {}) {
    if (!Array.isArray(chain) || chain.length === 0) return unidentified(REASONS.NO_INVOKER);

    let shells = 0;
    while (shells < chain.length && isShell(chain[shells].name)) shells++;
    const program = chain[shells];
    const outermostShell = shells > 0 ? chain[shells - 1] : null;

    let anchor;
    let basis;
    if (options.stdinIsTty && outermostShell) {
        anchor = outermostShell;
        basis = BASIS_TERMINAL;
    } else if (program) {
        anchor = program;
        basis = BASIS_PROCESS;
    } else {
        anchor = outermostShell;
        basis = BASIS_SHELL;
    }
    return { id: tokenFor(anchor), basis, lineage: chain.map(tokenFor) };
}

// ---------------------------------------------------------------------------
// Reading the ancestry
// ---------------------------------------------------------------------------

/**
 * One `/proc/<pid>/stat` line, as the three fields this needs.
 *
 * The name is everything between the FIRST `(` and the LAST `)`: the kernel
 * prints it unescaped, and `tmux: server (1)` is a real process name. Splitting
 * on spaces from the left is the well-known way to read the wrong field.
 */
function parseProcStat(text) {
    if (typeof text !== 'string') return null;
    const open = text.indexOf('(');
    const close = text.lastIndexOf(')');
    if (open < 0 || close < open) return null;
    // After the name: state ppid pgrp ... -- field 3 onward, so the parent is
    // index 1 and the start time, field 22, is index 19.
    const rest = text.slice(close + 1).trim().split(/\s+/);
    const ppid = Number(rest[1]);
    const startTicks = rest[19];
    if (!Number.isInteger(ppid) || !/^\d+$/.test(startTicks || '')) return null;
    return { ppid, startTicks, name: text.slice(open + 1, close) };
}

/**
 * The ancestors of `pid` on Linux, nearest first, from /proc.
 *
 * STOPS BELOW PROCESS 1. Init adopts whatever is orphaned, so it is an
 * ancestor of everything and the session of nothing.
 *
 * THE BOOT ID IS PART OF THE START TIME. Linux counts a process's start in
 * ticks since boot, so the same pid at the same tick count after a reboot is a
 * different process; without the boot id that cannot be told, and this throws
 * rather than hand back an identity that could be wrong.
 */
function readLinuxChain(pid, options = {}) {
    const readFile = options.readFile || ((file) => fs.readFileSync(file, 'utf8'));
    const bootId = String(readFile('/proc/sys/kernel/random/boot_id')).trim();
    if (!bootId) throw new Error('no boot id');
    const self = parseProcStat(readFile(`/proc/${pid}/stat`));
    if (!self) throw new Error('unreadable stat');

    const chain = [];
    let next = self.ppid;
    while (next > 1 && chain.length < MAX_DEPTH) {
        let stat;
        try { stat = parseProcStat(readFile(`/proc/${next}/stat`)); } catch (e) { void e; stat = null; }
        if (!stat) break;
        chain.push({ pid: next, start: `${bootId}/${stat.startTicks}`, name: stat.name });
        next = stat.ppid;
    }
    return chain;
}

/**
 * What the Windows lookup prints, as a chain: nearest first, the process
 * itself -- the first row -- left out.
 *
 * Rows are `pid|ticks|name`, where ticks is the .NET start time in UTC.
 *
 * A PARENT THAT STARTED AFTER ITS CHILD IS NOT ITS PARENT. Windows keeps the
 * parent's id on a process after the parent exits, and the id can be handed to
 * something unrelated. The only tell is that the newcomer started later than
 * its supposed child, so the chain ends at the first hop where that is true.
 *
 * START TIMES ARE CUT TO THE MILLISECOND before they are hashed. Measured: one
 * Windows interface reports 639305780637208890 for a process and another
 * reports 639305780637208891. Both truncate, to different precisions, so a
 * token over the raw value would give one run two ids depending on who read it.
 *
 * THE SYSTEM'S OWN PROCESSES (ids 0 and 4) are where a chain stops.
 */
function parseWindowsChain(text) {
    const rows = [];
    for (const line of String(text || '').split(/\r?\n/)) {
        const m = /^(\d+)\|(\d+)\|(.*)$/.exec(line.trim());
        if (!m) continue;
        rows.push({ pid: Number(m[1]), ticks: BigInt(m[2]), name: m[3] });
    }
    const chain = [];
    for (let i = 1; i < rows.length && chain.length < MAX_DEPTH; i++) {
        const row = rows[i];
        if (row.pid <= 4 || row.ticks > rows[i - 1].ticks) break;
        chain.push({ pid: row.pid, start: String(row.ticks / 10000n), name: row.name });
    }
    return chain;
}

/**
 * The PowerShell that prints the chain. PowerShell 7, which the front doors
 * already require: its `Parent` property reads the parent the kernel recorded,
 * where Windows PowerShell has none and the WMI route to the same answer took
 * 5-15 s on the machine this was measured on.
 *
 * A process this user may not inspect ends the chain -- reading its start time
 * throws -- and what was printed before it stands.
 */
function windowsChainScript(pid) {
    return [
        "$ErrorActionPreference = 'Stop'",
        'try {',
        `  $p = Get-Process -Id ${Number(pid)}`,
        `  for ($i = 0; $i -le ${MAX_DEPTH} -and $p; $i++) {`,
        "    '{0}|{1}|{2}' -f $p.Id, $p.StartTime.ToUniversalTime().Ticks, $p.ProcessName",
        '    $p = $p.Parent',
        '  }',
        '} catch { }'
    ].join('\n');
}

/**
 * The ancestors of `pid` on Windows. One `pwsh`, because Node can read its own
 * parent's id and nothing else about any other process.
 *
 * STDIN IS NOT INHERITED. The recorder reads ENTER from it to end a capture,
 * and a child holding the same console handle is a second reader of that key.
 */
function readWindowsChain(pid, options = {}) {
    const timeoutMs = options.timeoutMs || DEFAULT_TIMEOUT_MS;
    return new Promise((resolve, reject) => {
        let child;
        try {
            child = spawn('pwsh', ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', windowsChainScript(pid)], {
                stdio: ['ignore', 'pipe', 'ignore'],
                windowsHide: true,
                // Node ends it when the time is up, so a lookup that hangs
                // cannot outlive the recording or hold the process open.
                timeout: timeoutMs
            });
        } catch (e) { reject(e); return; }
        let out = '';
        child.stdout.on('data', (d) => { out += d; });
        child.on('error', reject);
        child.on('close', (code, signal) => {
            if (signal) { reject(Object.assign(new Error('timed out'), { timedOut: true })); return; }
            // Not even the process itself was printed: the lookup did not run,
            // which is a different fact from "it ran and found no parent".
            if (!/^\d+\|\d+\|/m.test(out)) { reject(new Error('nothing was read')); return; }
            resolve(parseWindowsChain(out));
        });
    });
}

/** The reader for a platform, or null where ancestry is not read. */
function chainReaderFor(platform) {
    if (platform === 'linux') return async (pid) => readLinuxChain(pid);
    if (platform === 'win32') return (pid, options) => readWindowsChain(pid, options);
    return null;
}

/**
 * Start finding out which run this process belongs to.
 *
 * ALWAYS RESOLVES, NEVER REJECTS, and never takes longer than the timeout. A
 * capture is a browser session somebody is about to drive by hand; it is not
 * delayed or failed because its run could not be read. It records that it
 * could not, and why, and carries on.
 *
 * Called early and awaited late -- see where `capture-har.js` uses it.
 */
function beginRunIdentity(options = {}) {
    const pid = options.pid || process.pid;
    const stdinIsTty = options.stdinIsTty !== undefined ? !!options.stdinIsTty : !!process.stdin.isTTY;
    const timeoutMs = options.timeoutMs || DEFAULT_TIMEOUT_MS;
    const readChain = options.readChain || chainReaderFor(options.platform || process.platform);
    if (!readChain) return Promise.resolve(unidentified(REASONS.UNSUPPORTED));

    // NOT unref'd. The race below always settles within the timeout and clears
    // this, so it holds nothing open for long -- and an unref'd timer lets a
    // process whose only pending work is this lookup exit with the question
    // unanswered, silently and with status 0.
    let timer;
    const timedOut = new Promise((resolve) => {
        timer = setTimeout(() => resolve(unidentified(REASONS.TIMED_OUT)), timeoutMs);
    });
    const read = Promise.resolve()
        .then(() => readChain(pid, { timeoutMs }))
        .then((chain) => resolveRun(chain, { stdinIsTty }))
        .catch((e) => unidentified(e && e.timedOut ? REASONS.TIMED_OUT : REASONS.FAILED));
    return Promise.race([read, timedOut]).finally(() => clearTimeout(timer));
}

module.exports = {
    newCaptureId,
    beginRunIdentity,
    resolveRun,
    isShell,
    tokenFor,
    parseProcStat,
    readLinuxChain,
    parseWindowsChain,
    REASONS,
    BASIS_TERMINAL,
    BASIS_PROCESS,
    BASIS_SHELL
};
