#!/usr/bin/env node
// Behavior tests for issue #25 -- the check-only start (`start --validate-only`)
// also answers whether a real start would be refused. Zero-dep; runs with
// `node capture-check-only.test.js`. Exits non-zero when anything fails.
//
// ## The incident
//
// A caller about to start the recorder where its errors cannot be seen -- a
// detached process, a hidden console, a schedule -- runs `--validate-only`
// first, because that is the one start that launches nothing. It passed, exit
// 0. The real start, in the same folder a moment later, ended at once with
// "No .har-profile.json found at or above <cwd>", in a place nobody was
// looking. The check skipped, by design, every gate the real start runs first.
//
// ## What is pinned here
//
// The check-only start asks the SAME gates the real start does, with the
// arguments the real start would get, and reports instead of refusing:
//
//   browser-driver    the playwright module and, for a browser the recorder
//                     launches itself, its Chromium build
//   operator-profile  a usable .har-profile.json at or above the folder
//   agent             with --ai, a `claude` that can be spawned
//
// FALSIFIERS (fail before #25, pass after):
//   C1   everything there: exit 0, and the JSON says each check passed
//   C2   the module missing: exit 1, the reason named, the session still printed
//   C3   no operator profile: exit 1, in the real start's own words
//   C4   a profile with no salt is not a usable profile
//   C5   every failure is reported in one run, in the order a start asks
//   C6   only the modes that launch Chromium are asked for a Chromium build
//   C7   every mode needs the module
//   C8   --ai also needs an agent, and nothing else does
//   C9   --profile is the BROWSER profile and cannot stand in for the operator's
//   C10  what the check says is what an unattended start says, word for word
//   C11  a start the check passed gets as far as opening the browser
//   C12  it writes nothing, starts nothing but its own questions, and connects
//        to nothing -- in every mode, passing or failing -- and the questions
//        it does ask are the ones the real start asks
//   C13  at a terminal it still asks nothing and installs nothing
//
// GUARD (passed before; it must keep passing):
//   C14  a usage error is still exit 2, with no verdict
//
// Every case drives the REAL recorder in a child process. The claims are about
// an exit code, two streams and what is on disk afterwards, and only running it
// answers those. No case opens a browser or reaches a network: the driver is a
// stand-in whose launch throws by name (capture-start-gates.test-support.js).

'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const gates = require(path.join(__dirname, 'capture-start-gates.test-support.js'));

const captureJs = path.join(__dirname, 'capture-har.js');
const SIDE_EFFECTS_PRELOAD = path.join(__dirname, 'capture-side-effects.test-support.js');

/** os.tmpdir() can hand back a Windows 8.3 short name; the recorder answers in the long form. */
function real(p) {
    try { return fs.realpathSync.native(p); } catch (e) { void e; return fs.realpathSync(p); }
}

const tmpRoot = real(fs.mkdtempSync(path.join(os.tmpdir(), 'capture-check-only-')));

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

function dir(...parts) {
    const d = path.join(tmpRoot, ...parts);
    fs.mkdirSync(d, { recursive: true });
    return d;
}

// ---------------------------------------------------------------------------
// fixtures
// ---------------------------------------------------------------------------

/**
 * One machine, entirely inside `<tmpRoot>/<name>`: the folder the operator is
 * standing in, the things the gates look for (or their absence), and every
 * place a process is in the habit of writing -- the temp directory, the home
 * directory, the per-user data directories.
 *
 * ALL OF IT INSIDE ONE ROOT ON PURPOSE. "The check wrote nothing" is asserted
 * by listing that root before and after, and a listing only sees where it
 * looks. It also keeps a real start, which a few cases run up to its first
 * refusal, from leaving anything on the machine running the suite.
 *
 * `state.profile` is the operator profile: absent when false, the scaffold's
 * own shape by default, or the object given.
 *
 * The one thing outside the root is the agent, which every machine that has one
 * shares (see `gateVars`). It is an executable on PATH and nothing writes there.
 */
let sharedAgent = null;
function machine(name, state) {
    const want = Object.assign({ profile: true }, state || {});
    if (want.agent === true) {
        sharedAgent = sharedAgent || gates.plantAgent(dir('the-agent'));
        want.agent = sharedAgent;
    }
    const root = dir(name);
    const cwd = dir(name, 'folder');
    const tmp = dir(name, 'tmp');
    const home = dir(name, 'home');
    const data = dir(name, 'data');
    if (want.profile) {
        gates.plantOperatorProfile(cwd, want.profile === true ? undefined : want.profile);
    }
    const env = gates.childEnv(Object.assign({
        TEMP: tmp, TMP: tmp, TMPDIR: tmp,
        HOME: home, USERPROFILE: home,
        LOCALAPPDATA: data, XDG_DATA_HOME: data,
        APPDATA: dir(name, 'appdata')
    }, gates.gateVars(root, want)));
    return { name, root, cwd, env };
}

const START = ['start', '--uri', 'https://example.com', '--describe', 'check-only fixture'];

function runRecorder(m, argv, opts) {
    const o = opts || {};
    const r = spawnSync(process.execPath, [...(o.nodeArgs || []), captureJs, ...argv], {
        cwd: m.cwd, encoding: 'utf8', windowsHide: true, timeout: 120000,
        env: Object.assign({}, m.env, o.env || {}),
        // Nothing can answer a prompt. A run that asked one would wait on a
        // closed stream rather than on a person who is not there.
        stdio: ['ignore', 'pipe', 'pipe']
    });
    return { status: r.status, stdout: r.stdout || '', stderr: r.stderr || '' };
}

/**
 * `--port 0` keeps a run off the port scan, which is not what these cases are
 * about -- except with --attach, where the endpoint names the port and the
 * recorder refuses the two together, and where a case asks for the scan.
 */
function withPort(args, scanPorts) {
    const given = args || [];
    return scanPorts || given.includes('--attach') ? given : ['--port', '0', ...given];
}

/** `start --validate-only` on machine `m`, with its JSON parsed when it printed one. */
function check(m, args, opts) {
    const scan = !!(opts && opts.scanPorts);
    const run = runRecorder(m, [...START, '--validate-only', ...withPort(args, scan)], opts);
    try { run.json = JSON.parse(run.stdout); } catch (e) { void e; run.json = null; }
    return run;
}

/**
 * The same start, for real. Run only where something refuses it before a
 * browser exists: one of the gates, or (C11) the stand-in driver itself.
 */
function realStart(m, args, opts) {
    return runRecorder(m, [...START, ...withPort(args)], opts);
}

/**
 * The verdict a check printed. Asserted, never assumed: before #25 there was
 * none, and a case must fail with that sentence rather than a TypeError.
 */
function verdictOf(run) {
    assert.ok(run.json,
        'stdout must be one JSON document whether the check passed or failed.\n' +
        `exit ${run.status}\n--- stdout ---\n${run.stdout}\n--- stderr ---\n${run.stderr}`);
    const verdict = run.json.validation;
    assert.ok(verdict && Array.isArray(verdict.checks),
        'the JSON must carry the verdict under "validation": { ok, checks: [...] }. ' +
        `It has: ${Object.keys(run.json).join(', ')}`);
    return verdict;
}

function entry(run, name) {
    const found = verdictOf(run).checks.filter((c) => c.name === name);
    assert.strictEqual(found.length, 1,
        `exactly one "${name}" entry expected; the checks are: ` +
        verdictOf(run).checks.map((c) => c.name).join(', '));
    return found[0];
}

function names(run) { return verdictOf(run).checks.map((c) => c.name); }

function assertPasses(run, what) {
    const verdict = verdictOf(run);
    assert.strictEqual(verdict.ok, true, `${what}: the verdict should be a pass.\n${run.stderr}`);
    assert.strictEqual(run.status, 0, `${what}: a pass is exit 0.\n${run.stderr}`);
    for (const c of verdict.checks) {
        assert.strictEqual(c.ok, true, `${what}: ${c.name} should pass (${c.reason})`);
        assert.strictEqual(c.reason, null, `${what}: a passing check has no reason`);
    }
}

function assertFails(run, name, reason, what) {
    const verdict = verdictOf(run);
    assert.strictEqual(verdict.ok, false, `${what}: the overall verdict must be a failure`);
    assert.strictEqual(run.status, 1,
        `${what}: a check a real start would be refused on exits 1 -- the code the real ` +
        `start itself ends with. Got ${run.status}.\n${run.stderr}`);
    const failed = entry(run, name);
    assert.strictEqual(failed.ok, false, `${what}: ${name} must be the check that failed`);
    assert.strictEqual(typeof failed.reason, 'string', `${what}: a failure carries its reason`);
    assert.match(failed.reason, reason, `${what}: the reason names what is missing`);
    return failed;
}

/** Everything under `root`, with enough about each file to notice a change. */
function listing(root) {
    const out = [];
    (function walk(d) {
        const entries = fs.readdirSync(d, { withFileTypes: true })
            .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
        for (const e of entries) {
            const full = path.join(d, e.name);
            const rel = path.relative(root, full);
            if (e.isDirectory()) {
                out.push(rel + path.sep);
                walk(full);
            } else {
                const st = fs.statSync(full);
                out.push(`${rel}  ${st.size} bytes  modified ${st.mtimeMs}`);
            }
        }
    })(root);
    return out;
}

/**
 * Run the check with every spawn, every write and every outbound connection
 * recorded (capture-side-effects.test-support.js), and the machine listed
 * before and after.
 */
function auditedCheck(m, args, opts) {
    const o = opts || {};
    const report = path.join(dir('side-effects'), `${m.name}.json`);
    const before = listing(m.root);
    const run = check(m, args, {
        nodeArgs: ['-r', SIDE_EFFECTS_PRELOAD],
        env: Object.assign({ HAR_TEST_SIDE_EFFECTS: report }, o.env || {}),
        scanPorts: o.scanPorts
    });
    run.before = before;
    run.after = listing(m.root);
    assert.ok(fs.existsSync(report),
        `the side-effect report was not written, so nothing below would be evidence.\n${run.stderr}`);
    run.events = JSON.parse(fs.readFileSync(report, 'utf8'));
    return run;
}

// What a path-resolving run asks git. Every one of these reads.
const READ_ONLY_GIT = new Set(['rev-parse', 'check-ignore', 'symbolic-ref', 'ls-files', 'status']);

/**
 * Is this spawn one of the QUESTIONS the check is allowed to ask?
 *
 * An allow-list, not a list of browsers to look for. "It launched nothing" is
 * only as strong as the list of things it may launch, and a list of forbidden
 * names would pass for any browser nobody thought to write down.
 */
function isAQuestion(spawned) {
    const base = path.basename(spawned.command).toLowerCase().replace(/\.exe$/, '');
    const line = [spawned.command, ...spawned.args].join(' ');
    if (base === 'git') {
        const sub = spawned.args.filter((a) => !a.startsWith('-'))[0];
        return READ_ONLY_GIT.has(sub);
    }
    // "Is there an agent I can run" -- the same probe the real start makes.
    if (base === 'claude') return spawned.args.join(' ') === '--version';
    // "Where does npm keep its global modules" -- only on the way out of a
    // missing module. Through the command interpreter on Windows.
    return /(^|[\\/ "])npm root -g"?$/.test(line);
}

function assertNoSideEffects(run, what) {
    assert.deepStrictEqual(run.after, run.before,
        `${what}: the check-only start must leave the machine exactly as it found it`);

    const writes = run.events.filter((e) => e.kind === 'fs');
    assert.deepStrictEqual(writes, [],
        `${what}: the check-only start must not create, change or remove anything`);

    const connections = run.events.filter((e) => e.kind === 'connect');
    assert.deepStrictEqual(connections, [],
        `${what}: the check-only start must not open a connection to anything`);

    const launched = run.events.filter((e) => e.kind === 'spawn' && !isAQuestion(e));
    assert.deepStrictEqual(launched, [],
        `${what}: the check-only start may ask git, npm and the agent a question, ` +
        'and must start nothing else');

    assert.ok(!run.stderr.includes(gates.LAUNCH_ATTEMPTED) && !run.stdout.includes(gates.LAUNCH_ATTEMPTED),
        `${what}: the driver was asked to open a browser`);
}

// ---------------------------------------------------------------------------
// PRECONDITIONS -- asserted rather than assumed. A suite that could not make
// the driver or the agent unreachable would pass every "it is missing" case
// below while testing nothing, on exactly the machines that have them.
// ---------------------------------------------------------------------------

test('PRECONDITION: the fixture makes the driver unreachable, and reachable', () => {
    const absent = machine('pre-driver-absent', { driver: false });
    const reach = gates.driverReach(absent.env, absent.cwd, __dirname);
    assert.ok(!reach.found,
        'a child on a machine with no driver still resolves playwright -- from "' +
        reach.location + '": ' + reach.path + '. Every "the module is missing" case would ' +
        'pass without reaching the branch it names. (An install above the fixtures or the ' +
        "recorder, or in a global npm root the fixture cannot move, is what does this.)");

    const present = machine('pre-driver-present', {});
    const there = gates.driverReach(present.env, present.cwd, __dirname);
    assert.strictEqual(there.found, true, 'and the planted driver is the one a child finds');
    assert.ok(there.path.startsWith(present.root + path.sep),
        'it must be the stand-in, never a real install: ' + there.path);
});

test('PRECONDITION: the fixture makes the agent unreachable, and reachable', () => {
    const probe = (m) => {
        const r = spawnSync(process.execPath, ['-e',
            `process.stdout.write(String(require(${JSON.stringify(captureJs)}).claudeOnPath()))`
        ], { cwd: m.cwd, encoding: 'utf8', windowsHide: true, timeout: 60000, env: m.env });
        assert.strictEqual(r.status, 0, `the agent probe itself failed:\n${r.stderr}`);
        return r.stdout.trim();
    };
    assert.strictEqual(probe(machine('pre-agent-absent', { agent: false })), 'false',
        'with the agent pruned from PATH, the recorder must find none');
    assert.strictEqual(probe(machine('pre-agent-present', { agent: true })), 'true',
        'and the planted agent must be one the recorder can spawn');
});

// ---------------------------------------------------------------------------
// C1 -- C5: the verdict
// ---------------------------------------------------------------------------

// The keys the check-only JSON carried before it carried a verdict. A consumer
// reading any of them must find it where it was.
const SESSION_KEYS = [
    'uri', 'describe', 'sessionDir', 'harPath', 'recordLog', 'outputPath', 'placement',
    'profileDir', 'externalProfile', 'storageState', 'mode', 'browser', 'attached',
    'ownsRecordHar', 'cdpEndpoint', 'port', 'requestedPort', 'snapshotSeconds', 'pace'
];

test('C1: with the driver, its browser and the operator profile all there, the check passes and says so', () => {
    const run = check(machine('c1-all-there', {}));
    assertPasses(run, 'everything present');
    assert.deepStrictEqual(names(run), ['browser-driver', 'operator-profile'],
        'one entry per gate a real start would ask -- and no agent entry when no agent was asked for');
    assert.match(run.stderr, /--validate-only OK \(no browser launched\)/,
        'the passing line a caller may already be matching is unchanged');

    // ADDITIVE. The verdict is one new key; nothing that was there moved.
    for (const key of SESSION_KEYS) {
        assert.ok(Object.hasOwn(run.json, key), `the session still carries "${key}"`);
    }
    assert.strictEqual(run.json.uri, 'https://example.com');
    assert.strictEqual(run.json.mode, 'chrome-profile');
    assert.ok(path.isAbsolute(run.json.sessionDir), 'and still resolves the session');
});

test('C2: a missing driver module fails the check, names the reason, and still prints the session', () => {
    const m = machine('c2-no-module', { driver: false });
    const run = check(m);
    const failed = assertFails(run, 'browser-driver', /playwright module was not found/,
        'the module is missing');
    assert.match(failed.reason, /npm install -g playwright/,
        'the reason carries the remedy, exactly as the real start prints it');
    assert.strictEqual(entry(run, 'operator-profile').ok, true,
        'the profile is there, and a failure elsewhere must not hide that');

    // THE SESSION IS STILL THE ANSWER TO THE OTHER QUESTION. Suites and callers
    // ask this flag where a capture would land; a missing driver does not make
    // that unanswerable.
    assert.ok(path.isAbsolute(run.json.sessionDir), 'the resolved session is printed either way');

    // Prose on stderr, data on stdout -- on a failure most of all, because that
    // is the run somebody pipes into a parser to find out what went wrong.
    assert.ok(!/capture-har:/.test(run.stdout), 'stdout carries the JSON and nothing else');
    assert.match(run.stderr, /capture-har: the playwright module was not found/,
        'the reason is also said to whoever is reading the terminal');
    assert.match(run.stderr, /--validate-only FAILED \(no browser launched\)/);
    assert.ok(!/--validate-only OK/.test(run.stderr), 'and nothing claims the check passed');
});

test('C3: a folder with no operator profile fails the check, in the real start\'s own words', () => {
    // THE OWNER'S INCIDENT. The check passed here, exit 0, and the start that
    // followed ended with exactly this sentence where nobody could read it.
    const m = machine('c3-no-profile', { profile: false });
    const run = check(m);
    const failed = assertFails(run, 'operator-profile', /No \.har-profile\.json found at or above/,
        'no operator profile');
    assert.ok(failed.reason.includes(m.cwd),
        'the reason names the folder that was searched: ' + failed.reason);
    assert.strictEqual(entry(run, 'browser-driver').ok, true, 'the driver is there');
    assert.ok(!fs.existsSync(path.join(m.cwd, '.har-profile.json')),
        'and the check did not scaffold one to make itself pass');
});

test('C4: a profile with no salt is not a usable profile', () => {
    const run = check(machine('c4-no-salt', { profile: { literals: {} } }));
    assertFails(run, 'operator-profile', /no "salt"/, 'a profile without a salt');
});

test('C5: every failure is reported in one run, in the order a real start asks', () => {
    // A real start stops at its first refusal, so finding two problems takes
    // two runs. The check is the one place both can be said at once.
    const run = check(machine('c5-both-missing', { driver: false, profile: false }));
    assertFails(run, 'browser-driver', /playwright module was not found/, 'both missing');
    assertFails(run, 'operator-profile', /No \.har-profile\.json found/, 'both missing');
    assert.deepStrictEqual(names(run), ['browser-driver', 'operator-profile'],
        'in the order the real start asks, so the first failure is the one it would end on');
    const failed = verdictOf(run).checks.filter((c) => !c.ok);
    assert.strictEqual(failed.length, 2, 'one reason per failure');
    assert.notStrictEqual(failed[0].reason, failed[1].reason, 'each its own');
});

// ---------------------------------------------------------------------------
// C6 -- C9: the answer follows the arguments
// ---------------------------------------------------------------------------

test('C6: only the modes that launch Chromium themselves are asked for a Chromium build', () => {
    // A native or attached run drives the Chrome the machine already has, and an
    // agent-driven run is a native one. Demanding a browser download to use an
    // installed browser is the unfollowable advice #512 removed from the start;
    // the check must not put it back.
    const noBuild = { browser: false, agent: true };

    for (const args of [[], ['--isolated']]) {
        const run = check(machine('c6-needs-build-' + (args[0] || 'default').replace(/\W/g, ''), noBuild), args);
        const failed = assertFails(run, 'browser-driver', /npx playwright install chromium/,
            `${args.join(' ') || 'the default mode'} with no Chromium build`);
        assert.ok(!/npm install -g playwright/.test(failed.reason),
            'the module IS there -- the reason must not send anyone to install it: ' + failed.reason);
    }

    for (const args of [['--browser', 'native'], ['--attach', '9222'], ['--ai']]) {
        const run = check(machine('c6-module-only-' + args[0].replace(/\W/g, ''), noBuild), args);
        assertPasses(run, `${args.join(' ')} with the module and no Chromium build`);
    }
    // (With the build there, the two modes that wanted it pass: C1 and C11.)
});

test('C7: every mode needs the module', () => {
    for (const args of [['--browser', 'native'], ['--attach', '9222'], ['--ai']]) {
        const run = check(machine('c7-no-module-' + args[0].replace(/\W/g, ''),
            { driver: false, agent: true }), args);
        const failed = assertFails(run, 'browser-driver', /playwright module was not found/,
            `${args.join(' ')} with no module`);
        assert.ok(!/npx playwright install chromium/.test(failed.reason),
            'and is not told to fetch a browser this mode never launches: ' + failed.reason);
    }
});

test('C8: --ai also needs an agent that can be spawned, and nothing else does', () => {
    const driven = check(machine('c8-agent-there', { agent: true }), ['--ai']);
    assertPasses(driven, '--ai with an agent on PATH');
    assert.deepStrictEqual(names(driven), ['agent', 'browser-driver', 'operator-profile'],
        'the agent is asked first, as the real start asks it');

    const undriven = check(machine('c8-agent-missing', { agent: false }), ['--ai']);
    const failed = assertFails(undriven, 'agent', /`claude` CLI, and it is not\s+on PATH/,
        '--ai with no agent');
    assert.strictEqual(entry(undriven, 'browser-driver').ok, true);
    assert.strictEqual(entry(undriven, 'operator-profile').ok, true);
    assert.ok(failed.reason.length > 0);

    // WITHOUT --ai NOBODY IS ASKED. A machine with no agent records by hand
    // perfectly well, and a check that reported a missing agent there would be
    // failing a start that works.
    const byHand = check(machine('c8-by-hand', { agent: false }));
    assertPasses(byHand, 'a hand-driven start on a machine with no agent');
    assert.ok(!names(byHand).includes('agent'), 'no agent entry when no agent was asked for');
});

test('C9: --profile names the BROWSER profile and cannot stand in for the operator profile', () => {
    // Two different things share the word. `--profile` chooses which signed-in
    // browser identity records; the operator profile is found by walking up
    // from the working directory, and no option moves it -- in the real start
    // or here.
    const elsewhere = dir('c9-profile-elsewhere');
    gates.plantOperatorProfile(elsewhere);

    const missing = check(machine('c9-no-operator-profile', { profile: false }), ['--profile', elsewhere]);
    assertFails(missing, 'operator-profile', /No \.har-profile\.json found/,
        '--profile pointing at a folder that holds an operator profile');

    const named = check(machine('c9-named-browser-profile', {}), ['--profile', 'work']);
    assertPasses(named, '--profile work');
    assert.match(named.json.profileDir, /profile-work$/, 'and it still chooses the browser profile');
});

// ---------------------------------------------------------------------------
// C10, C11: "it validated" means "it will start"
// ---------------------------------------------------------------------------

test('C10: what the check says is what an unattended start says, word for word', () => {
    // The whole value of the check is that its answer is the start's answer. So
    // each refusal is produced twice on one machine -- asked, then for real --
    // and the reason in the JSON has to be the sentence the real start ended on.
    // The real start comes second and is refused at a gate, before it has
    // written anything, so the order costs the check nothing.
    const scenarios = [
        { what: 'the module missing', state: { driver: false }, args: [], gate: 'browser-driver' },
        { what: 'the Chromium build missing', state: { browser: false }, args: [], gate: 'browser-driver' },
        { what: 'no operator profile', state: { profile: false }, args: [], gate: 'operator-profile' },
        { what: 'no agent', state: { agent: false }, args: ['--ai'], gate: 'agent' }
    ];
    for (const s of scenarios) {
        const m = machine('c10-' + s.what.replace(/\W+/g, '-'), s.state);
        const asked = check(m, s.args);
        const failed = entry(asked, s.gate);
        assert.strictEqual(failed.ok, false, `${s.what}: the check reports ${s.gate}`);

        const started = realStart(m, s.args);
        assert.strictEqual(started.status, 1, `${s.what}: the real start is refused\n${started.stderr}`);
        assert.strictEqual(asked.status, started.status,
            `${s.what}: the check exits with the code the start exits with`);
        assert.ok(started.stderr.includes('capture-har: ' + failed.reason),
            `${s.what}: the real start must have said the check's reason.\n` +
            `--- the check's reason ---\n${failed.reason}\n--- the real start said ---\n${started.stderr}`);
    }
});

test('C11: a start the check passed gets as far as opening the browser', () => {
    // The other half of "it validated means it will start". A real launch needs
    // a real browser, so the stand-in driver refuses BY NAME when asked to open
    // one -- and a start that reaches that refusal has been through every gate.
    const m = machine('c11-validated', {});
    assertPasses(check(m, ['--isolated']), 'the check');

    const started = realStart(m, ['--isolated', '--no-wait']);
    assert.ok(started.stderr.includes(gates.LAUNCH_ATTEMPTED),
        'the real start must reach the browser launch, having been refused by no gate.\n' + started.stderr);
    assert.ok(!/playwright module was not found|No \.har-profile\.json found|Chromium build is not/
        .test(started.stderr), 'and none of the gates the check answered refused it\n' + started.stderr);
});

// ---------------------------------------------------------------------------
// C12, C13: side-effect free
// ---------------------------------------------------------------------------

/** Did this run start a child whose command line matches `pattern`? */
function asked(run, pattern) {
    return run.events.some((e) => e.kind === 'spawn' && pattern.test([e.command, ...e.args].join(' ')));
}

test('C12: the check writes nothing, starts nothing but its own questions, and connects to nothing', () => {
    // A real start in these folders WOULD write: a .gitignore beside a capture
    // store outside any repository, a session directory, a throwaway browser
    // profile for --isolated, an npm debug log when it asks where the global
    // modules are. The check answers the same questions and leaves none of it.
    const modes = [
        // The one mode run WITHOUT `--port 0`, so the port scan is covered too.
        { what: 'the default mode', state: {}, args: [], scanPorts: true },
        { what: '--isolated', state: {}, args: ['--isolated'] },
        { what: '--browser native on a named profile', state: {}, args: ['--browser', 'native', '--profile', 'work'] },
        { what: '--attach', state: {}, args: ['--attach', '9222'] },
        { what: '--ai', state: { agent: true }, args: ['--ai'], asks: [/claude(\.exe)? --version$/] },
        { what: 'an explicit --output-path', state: {}, args: ['--output-path', 'reference'] },
        { what: 'a failing check: no module, no profile, no agent',
            state: { driver: false, profile: false, agent: false }, args: ['--ai'],
            asks: [/claude(\.exe)? --version$/, /npm root -g/] }
    ];
    for (const mode of modes) {
        const m = machine('c12-' + mode.what.replace(/\W+/g, '-'), mode.state);
        const run = auditedCheck(m, mode.args, { scanPorts: mode.scanPorts });
        verdictOf(run);
        assertNoSideEffects(run, mode.what);

        // AN ALLOW-LIST WOULD PASS A CHECK THAT ASKED NOTHING AT ALL. So the
        // questions are pinned where they are the point: the agent is asked
        // whether it can be spawned, and a missing module still asks npm where
        // its global modules are -- a machine with a moved npm prefix must not
        // fail a check for a start that would have found the module.
        for (const question of mode.asks || []) {
            assert.ok(asked(run, question), `${mode.what}: expected the check to ask ${question}; ` +
                'it ran: ' + JSON.stringify(run.events.filter((e) => e.kind === 'spawn')));
        }
    }
});
test('C13: at a terminal it still asks nothing and installs nothing', () => {
    // At a terminal the REAL start offers to install the driver ([Y/n], and
    // ENTER installs) and to scaffold a profile. The check is the unattended
    // answer whoever runs it: it must never prompt, because the caller it
    // exists for has nowhere to answer, and it must never install or scaffold,
    // because a check that changed the machine could not be trusted to describe it.
    const m = machine('c13-terminal', { driver: false, profile: false });
    const run = auditedCheck(m, [], { env: { HAR_TEST_STDIN_IS_TTY: '1' } });

    assertFails(run, 'browser-driver', /playwright module was not found/, 'at a terminal');
    assertFails(run, 'operator-profile', /No \.har-profile\.json found/, 'at a terminal');
    assert.ok(!/\[Y\/n\]|\[y\/N\]/.test(run.stderr), 'no question was asked\n' + run.stderr);
    assert.ok(!asked(run, /\b(npm install|npx)\b/), 'and no install was started');
    assertNoSideEffects(run, 'at a terminal');
    assert.ok(!fs.existsSync(path.join(m.cwd, '.har-profile.json')), 'no profile was scaffolded');
});

// ---------------------------------------------------------------------------
// C14: a usage error is still a usage error
// ---------------------------------------------------------------------------

test('C14: a usage error is still exit 2, with no verdict', () => {
    // The verdict answers "would this start be refused by a gate". A command
    // line the recorder cannot read was never a start, and exit 2 already says
    // so -- on a machine where every gate would also have failed.
    const m = machine('c14-usage', { driver: false, profile: false });
    for (const args of [['--browser', 'firefox'], ['--ai', '--isolated'], ['--no-such-option', 'x']]) {
        const run = check(m, args);
        assert.strictEqual(run.status, 2, `${args.join(' ')} is a usage error\n${run.stderr}`);
        assert.strictEqual(run.stdout, '', 'and prints no JSON to mistake for a verdict');
    }
});

// ---------------------------------------------------------------------------

(async () => {
    let failures = 0;
    for (const { name, fn } of queued) {
        try {
            await fn();
        } catch (e) {
            failures++;
            process.stderr.write('FAIL: ' + name + '\n  ' + e.message + '\n');
            if (e.stack) process.stderr.write(e.stack.split('\n').slice(1, 4).join('\n') + '\n');
        }
    }
    if (failures) {
        process.stderr.write('\n' + failures + ' of ' + queued.length + ' capture-check-only tests FAILED\n');
        process.exitCode = 1;
    } else {
        process.stdout.write('All capture-check-only tests passed (' + queued.length + ')\n');
    }
    try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (err) { void err; }
})();
