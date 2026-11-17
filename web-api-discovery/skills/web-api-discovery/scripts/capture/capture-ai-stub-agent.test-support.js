'use strict';

/**
 * capture-ai-stub-agent.test-support.js -- THE STUB AGENT, for tests only (#98).
 *
 * The issue's acceptance criterion is "a test with a stub agent (a script that
 * drives a local page through the CDP endpoint) proves the hand-off, stop and
 * post-processing". This file is that stub, and the seam that installs it.
 *
 * It has two jobs, and which one it does depends on how it is loaded:
 *
 *   PRELOADED (`node -r`) into the recorder process, it wraps
 *   `child_process.spawn` AND `child_process.spawnSync` -- before capture-har.js
 *   takes its references to them -- so that:
 *
 *     * the one spawn of `claude -p --input-format stream-json ...` becomes a
 *       spawn of THIS FILE, with the same piped stdin the prompt and the
 *       recorder's messages arrive on, and
 *     * the recorder's `claudeOnPath()` probe (`claude --version`, spawned
 *       without a shell) answers YES, because the agent it would run is this
 *       file.
 *
 *   BOTH, FROM ONE PREDICATE, and that is the point rather than a convenience.
 *   The recorder refuses `--ai` before it starts a browser when no agent is
 *   installed (resolveAgent), which is a guard worth keeping and is pinned by its
 *   own case. A stub that replaced the agent but left it UNDISCOVERABLE would hit
 *   that refusal and never run -- which is exactly what happened on a runner with
 *   no `claude` on PATH, while passing on a developer machine that has one. So
 *   the existence and the identity of the agent are stubbed together: there is no
 *   state in which this file is the agent and the recorder cannot see it.
 *
 *   RUN AS A COMMAND, it behaves as the agent, and SPEAKS THE CLI'S STREAM-JSON
 *   the way `claude -p --input-format stream-json --output-format stream-json
 *   --replay-user-messages` does (measured, see agent-stream.js): it reads the
 *   prompt as the first user message on stdin, echoes every message it takes
 *   in (`isReplay`), narrates as assistant text, ends each turn with a
 *   `result`, starts a new turn for a message that arrives while it is idle,
 *   and exits 0 when its stdin closes. In between it finds the session file
 *   named in the prompt, reads the CDP endpoint out of that file, attaches to
 *   the browser and drives the page.
 *
 * WHY IT READS THE PROMPT rather than being handed the endpoint: that is the
 * hand-off under test. If the generated prompt ever stops naming the session
 * file, or names it in a way nothing can parse, a real agent is as stuck as this
 * stub -- and the integration case fails instead of passing on a prompt no agent
 * could have used.
 *
 * It does NOT stop the recording, does NOT close the browser, and nothing here
 * is reachable from the product: the file is loaded only by a test, and `claude`
 * is spawned by the product exactly as it would be.
 */

const fs = require('fs');
const path = require('path');
const childProcess = require('child_process');

const realSpawn = childProcess.spawn;
const realSpawnSync = childProcess.spawnSync;

// The one name both halves of the stub are keyed on. Spelled once so "which
// command is the agent" cannot be answered differently by the two wrappers.
const AGENT = 'claude';

/** Is this the one spawn the recorder makes for the driving agent? */
function isAgentSpawn(command, argv) {
    return String(command) === AGENT && Array.isArray(argv) && argv[0] === '-p';
}

/**
 * Is this the recorder asking whether the agent is installed?
 *
 * `claudeOnPath()` in capture-har.js SPAWNS the agent -- `claude --version`,
 * with no shell, exactly as the drive will -- and reads the exit status and
 * whether the spawn errored. It used to ask `where`/`which`, which said yes to a
 * `claude.cmd` that spawn() cannot launch; the probe now asks the same question
 * the launch does, and so does this stub. Matched on the ARGUMENT too, so any
 * other spawn of `claude` goes through untouched.
 */
function isAgentProbe(command, argv) {
    return String(command) === AGENT && Array.isArray(argv) &&
        argv.length === 1 && argv[0] === '--version';
}

/** What `claude --version` says when the agent is this file. */
function agentProbeResult() {
    const version = 'stub-agent (capture-ai-stub-agent.test-support.js)\n';
    return {
        status: 0, signal: null, pid: 0, error: undefined,
        stdout: version, stderr: '', output: [null, version, '']
    };
}

childProcess.spawn = function spawn(command, args, options) {
    if (isAgentSpawn(command, args)) {
        return realSpawn.call(this, process.execPath, [__filename], options);
    }
    return realSpawn.call(this, command, args, options);
};

/**
 * Is this the recorder asking Tailscale for this PC's address (mirror.js)?
 * Answered from HAR_STUB_TAILSCALE_JSON when a test sets it -- so a test can
 * stand the phone view up on LOOPBACK, and no test ever touches a real tailnet.
 */
function isTailscaleStatus(command, argv) {
    return /tailscale(\.exe)?$/i.test(path.basename(String(command))) && Array.isArray(argv) &&
        argv[0] === 'status' && argv[1] === '--json';
}

childProcess.spawnSync = function spawnSync(command, args, options) {
    if (isAgentProbe(command, args)) return agentProbeResult();
    if (process.env.HAR_STUB_TAILSCALE_JSON && isTailscaleStatus(command, args)) {
        const stdout = process.env.HAR_STUB_TAILSCALE_JSON;
        return { status: 0, signal: null, pid: 0, error: undefined, stdout, stderr: '', output: [null, stdout, ''] };
    }
    return realSpawnSync.call(this, command, args, options);
};

// ---------------------------------------------------------------------------
// The stub agent itself
// ---------------------------------------------------------------------------

/** The session file the prompt names. The prompt's own line, parsed as given. */
function sessionFileFrom(prompt) {
    const match = /\*\*Session file:\*\*\s*(.+)/.exec(String(prompt || ''));
    return match ? match[1].trim() : null;
}

// ---- the stream-json half: what the CLI does on stdout, and reads on stdin ----

/** One stream-json event on stdout. A recorder that has gone away is no crash. */
function emit(ev) {
    try { process.stdout.write(JSON.stringify(ev) + '\n'); } catch (e) { void e; }
}
const say = (text) => emit({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text }] } });
const step = (text) => say(`STEP: ${text}`);
const endTurn = () => emit({ type: 'result', subtype: 'success', is_error: false });

/**
 * The messages the recorder sends, as they arrive. `turnActive` is the CLI's
 * own distinction: a message that arrives mid-turn is taken into that turn;
 * one that arrives while idle starts a turn of its own (and ends it).
 */
const inbox = [];
let turnActive = false;
let promptResolve;
const promptArrived = new Promise((r) => { promptResolve = r; });
const waiters = [];
function onMessage(text) {
    inbox.push({ text, at: Date.now() });
    for (const w of waiters.splice(0)) w();
    if (inbox.length === 1) { promptResolve(text); return; }
    if (turnActive) return;
    // Idle: a turn of its own. "The capture has ended" gets the stop it asks for.
    if (/The capture has ended/.test(text)) step('told the capture ended; stopping');
    else step(`received: ${text.slice(0, 60)}`);
    endTurn();
}
function readStdin() {
    let buf = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => {
        buf += chunk;
        let i;
        while ((i = buf.indexOf('\n')) >= 0) {
            const line = buf.slice(0, i);
            buf = buf.slice(i + 1);
            let ev = null;
            try { ev = JSON.parse(line); } catch (e) { void e; }
            if (!ev || ev.type !== 'user') continue;
            emit(Object.assign({}, ev, { isReplay: true }));
            const content = ev.message && ev.message.content;
            const text = Array.isArray(content) ? (content.find((c) => c.type === 'text') || {}).text : content;
            onMessage(String(text || ''));
        }
    });
    // STDIN CLOSING IS THE END OF THE SESSION, as it is for the CLI.
    process.stdin.on('end', () => setTimeout(() => process.exit(0), 100));
}
/** Resolves when a message matching `re` has arrived, or after `ms`. */
async function messageMatching(re, ms) {
    const deadline = Date.now() + ms;
    while (Date.now() < deadline) {
        if (inbox.some((m) => re.test(m.text))) return true;
        await new Promise((r) => { waiters.push(r); setTimeout(r, 200); });
    }
    return false;
}

async function driveTheRecording(prompt) {
    const sessionFile = sessionFileFrom(prompt);
    if (!sessionFile) {
        process.stderr.write('stub-agent: the prompt does not name a session file\n');
        return 2;
    }
    const session = JSON.parse(fs.readFileSync(sessionFile, 'utf8'));
    step(`read ${path.basename(sessionFile)}; endpoint ${session.cdpEndpoint}`);

    // THE RECORDER'S OWN MODULE RESOLUTION, not a bare require. A bare
    // `require('playwright')` searches upward from THIS file and never reaches
    // npm's global root -- which is where the install usually is, and the exact
    // defect #512 fixed for the recorder. Asking the recorder where the driver is
    // means the stub runs wherever the recorder runs, including off the NODE_PATH
    // CI publishes to the child it spawns.
    const recorder = require(path.join(__dirname, 'capture-har.js'));
    const resolved = recorder.resolvePlaywright({ cwd: process.cwd(), env: process.env });
    if (!resolved.found) {
        process.stderr.write('stub-agent: no playwright install to drive with\n');
        return 4;
    }
    const playwright = require(resolved.path);
    const browser = await playwright.chromium.connectOverCDP(session.cdpEndpoint);
    try {
        const context = browser.contexts()[0];
        const target = (context.pages() || []).find((p) => p.url().startsWith(session.uri)) ||
            context.pages()[0];
        if (!target) {
            process.stderr.write('stub-agent: the capture has no page to drive\n');
            return 3;
        }
        step(`attached to ${target.url()}`);

        // THE AGENT THAT DOES NOT WAIT WHEN TOLD TO, on request: the site
        // answers its first click with 429 and Retry-After, and it clicks
        // again at once. The recorder must hold that second click IN THE
        // BROWSER until the pause is over -- so this measures how long the
        // site took to see it, and says whether it was told to wait.
        if (process.env.HAR_STUB_AGENT_MODE === 'throttled') {
            await target.waitForSelector('#limited', { timeout: 30000 });
            const first = await Promise.all([
                target.waitForResponse((r) => r.url().includes('/api/limited'), { timeout: 30000 }),
                target.click('#limited')
            ]).then(([r]) => r);
            step(`clicked #limited; the site answered ${first.status()}`);
            // Clicks again the moment it is told to wait -- the agent that does
            // not -- plus a beat, so the click races the pause, not its start.
            await messageMatching(/asked us to slow down/, 15000);
            await new Promise((r) => setTimeout(r, 300));
            const clicked = Date.now();
            const second = await Promise.all([
                target.waitForResponse((r) => r.url().includes('/api/limited'), { timeout: 60000 }),
                target.click('#limited')
            ]).then(([r]) => r);
            step(`clicked #limited again at once; the site answered ${second.status()} after ` +
                `${Date.now() - clicked} ms`);
            step(inbox.some((m) => /asked us to slow down/.test(m.text))
                ? 'the recorder told me to wait' : 'nobody told me to wait');
            say('SUMMARY: clicked #limited twice; the second click waited out the pause.');
            return 0;
        }

        // THE AGENT THAT MEETS A BOT CHECK, on request: it opens a page the
        // fixture serves as a "Just a moment..." interstitial, and then does
        // what it was told -- waits for the recorder.
        if (process.env.HAR_STUB_AGENT_MODE === 'challenge') {
            await target.goto(new URL('/challenge', session.uri).href, { waitUntil: 'domcontentloaded' });
            step('opened /challenge');
            const told = await messageMatching(/A person is taking over/, 30000);
            step(told ? 'the recorder said a person is taking over; waiting' : 'nobody said anything');
            // Waits as told: until the person hands back, or the capture ends.
            await messageMatching(/The person has finished|The capture has ended/, 120000);
            if (inbox.some((m) => /The person has finished/.test(m.text))) {
                step('the person has finished; re-checked the page and carried on');
            }
            say('SUMMARY: met a bot check and waited for the person.');
            return 0;
        }

        // ONE ACTION, at a human pace, in the tab the recorder opened -- the
        // shape the rules in drive-prompt.md demand, performed rather than
        // asserted. The click is what produces traffic no other part of this run
        // could have produced, which is what makes the recording prove a drive
        // happened.
        await target.waitForSelector('#search', { timeout: 30000 });
        await new Promise((r) => setTimeout(r, 300));
        await Promise.all([
            target.waitForResponse((res) => res.url().includes('/api/search'), { timeout: 30000 }),
            target.click('#search')
        ]);
        step('clicked #search and the search response arrived');
        step('no bot check and no sign-in prompt appeared');
        // STILL DRIVING, on request: the case where the recording is ended by
        // somebody else (the stop command, ENTER) while the agent is mid-drive.
        // Read from the environment because the recorder spawns this file and
        // passes it nothing but its messages. It lingers until it is told the
        // capture ended -- the recorder's message -- or the time runs out.
        const linger = Number(process.env.HAR_STUB_AGENT_LINGER_MS || 0);
        if (linger > 0) {
            step('lingering');
            const told = await messageMatching(/The capture has ended/, linger);
            if (told) step('told the capture ended; stopping');
        }
        // AND IT ENDS ITS TURN AT ONCE, which is the point. An agent stops the
        // moment its last action returns -- it does not wait, the way a human
        // walking back to the keyboard to press ENTER does. Sleeping here would
        // hide the race the recorder has to handle, and the final response is
        // routinely the most valuable entry in a capture.
    } finally {
        // DETACH. The stub is a driver, not an owner: closing the browser is
        // precisely what neither an agent nor the recorder may do.
        await browser.close().catch(() => {});
    }
    say('SUMMARY: searched once, stopped before any details page.');
    return 0;
}

module.exports = { isAgentSpawn, isAgentProbe, agentProbeResult, sessionFileFrom };

if (require.main === module) {
    // A recorder that has gone away closes the pipe: that is the end, not a crash.
    process.stdout.on('error', () => process.exit(0));
    readStdin();
    emit({ type: 'system', subtype: 'init', model: 'stub-agent' });
    promptArrived.then(async (prompt) => {
        turnActive = true;
        let code;
        try {
            code = await driveTheRecording(prompt);
        } catch (e) {
            process.stderr.write(`stub-agent: ${e && e.stack ? e.stack : e}\n`);
            code = 1;
        }
        turnActive = false;
        if (code !== 0) {
            emit({ type: 'result', subtype: 'error_during_execution', is_error: true });
            process.exit(code);
        }
        // The turn ends; the session stays open until stdin closes.
        endTurn();
    });
}
