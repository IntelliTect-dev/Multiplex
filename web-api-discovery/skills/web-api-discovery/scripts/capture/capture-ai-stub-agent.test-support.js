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
 *     * the one spawn of `claude -p <prompt>` becomes a spawn of THIS FILE with
 *       that same prompt, and
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
 *   RUN AS A COMMAND with the prompt as its only argument, it behaves as the
 *   agent: it reads the prompt the way an agent would, finds the session file
 *   named in it, reads the CDP endpoint out of that file, attaches to the
 *   browser, drives the page, narrates what it did, and exits 0.
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
        return realSpawn.call(this, process.execPath, [__filename, args[1]], options);
    }
    return realSpawn.call(this, command, args, options);
};

childProcess.spawnSync = function spawnSync(command, args, options) {
    if (isAgentProbe(command, args)) return agentProbeResult();
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

async function driveTheRecording(prompt) {
    const step = (text) => process.stdout.write(`STEP: ${text}\n`);
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

        // THE AGENT THAT DOES NOT NOTICE IT IS BEING THROTTLED, on request: it
        // clicks a button the fixture answers with 429 every few seconds, the
        // way an agent the recorder cannot slow down would keep on going. The
        // recorder must see the site refusing and stop it -- so this loop
        // ending on its own is the failure, and the case asserts it did not.
        if (process.env.HAR_STUB_AGENT_MODE === 'throttled') {
            await target.waitForSelector('#limited', { timeout: 30000 });
            for (let i = 1; i <= 10; i++) {
                const res = await Promise.all([
                    target.waitForResponse((r) => r.url().includes('/api/limited'), { timeout: 30000 }),
                    target.click('#limited')
                ]).then(([r]) => r);
                step(`clicked #limited (${i}); the site answered ${res.status()}`);
                await new Promise((r) => setTimeout(r, 3000));
            }
            process.stdout.write('SUMMARY: clicked #limited ten times and was never stopped.\n');
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
        // passes it nothing but the prompt.
        const linger = Number(process.env.HAR_STUB_AGENT_LINGER_MS || 0);
        if (linger > 0) {
            step('lingering');
            await new Promise((r) => setTimeout(r, linger));
        }
        // AND IT EXITS AT ONCE, which is the point. An agent stops the moment its
        // last action returns -- it does not wait, the way a human walking back to
        // the keyboard to press ENTER does. Sleeping here would hide the race the
        // recorder has to handle, and the final response is routinely the most
        // valuable entry in a capture.
    } finally {
        // DETACH. The stub is a driver, not an owner: closing the browser is
        // precisely what neither an agent nor the recorder may do.
        await browser.close().catch(() => {});
    }
    process.stdout.write('SUMMARY: searched once, stopped before any details page.\n');
    return 0;
}

module.exports = { isAgentSpawn, isAgentProbe, agentProbeResult, sessionFileFrom };

if (require.main === module) {
    driveTheRecording(process.argv[2]).then(
        (code) => process.exit(code),
        (e) => {
            process.stderr.write(`stub-agent: ${e && e.stack ? e.stack : e}\n`);
            process.exit(1);
        });
}
