#!/usr/bin/env node
// Behavior tests for the recorder's dependency preflight and for what it does
// with a capture folder that is not in a repository (issue #512).
//
// Zero-dep, runs with `node capture-preflight.test.js`. Exits non-zero when
// anything fails.
//
// The owner's report is the specification. Starting a brand-new site from an
// empty, non-repo folder, running the recorder out of the upstream checkout:
// the run scaffolded a profile, announced a capture root, and THEN failed on
// the browser dependency with a remedy that could not work. Two defects in one
// transcript, and this file pins both.
//
//  - THE PREFLIGHT COMES FIRST. A missing dependency is the first line, not
//    the last. Nothing is scaffolded, nothing is prompted and no browser is
//    opened before it is satisfied -- because every one of those is work the
//    operator has to undo when the run cannot proceed anyway.
//  - `--validate-only` STAYS dependency-free. It resolves paths and opens
//    nothing, so requiring a browser to run it would break the one command
//    that exists to answer questions without side effects.
//  - A NON-REPO CAPTURE FOLDER IS PROTECTED. The skill's own pipeline runs
//    `git init && git add -A` in that folder later. Without a rule left behind
//    now, that commit takes the salt and the unscrubbed raw captures with it,
//    and nobody is watching when it happens.
//
// The subprocess cases drive the REAL recorder. The ordering claim is about
// what has happened to the filesystem by the time the process exits, and only
// running it can answer that.

'use strict';

const assert = require('assert');
const { execFileSync, spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const capture = require(path.join(__dirname, 'capture-har.js'));
const subsDestination = require(path.join(__dirname, '..', 'har', 'subs-destination.js'));
const harProfile = require(path.join(__dirname, '..', 'har', 'har-profile.js'));

const captureJs = path.join(__dirname, 'capture-har.js');

/**
 * Does any ancestor of `start` hold a playwright install?
 *
 * Node's upward walk does not stop at a temp directory, and a stray
 * `node_modules` in a home directory is an ordinary thing to find on a
 * developer machine -- this issue was reported from one. A fixture placed
 * underneath such a folder resolves playwright from it, and then every "the
 * module is absent" case below silently tests nothing.
 */
function ancestorHasPlaywright(start) {
    let d = path.resolve(start);
    for (;;) {
        if (fs.existsSync(path.join(d, 'node_modules', 'playwright'))) return true;
        const up = path.dirname(d);
        if (up === d) return false;
        d = up;
    }
}

/**
 * Somewhere to put fixtures that no playwright install sits above.
 *
 * Tried in order of politeness: the system temp directory, then the root of
 * the volume it lives on, then the repository itself. A machine where none of
 * them is usable fails the precondition test loudly rather than running a
 * suite whose absence cases cannot fail.
 */
function cleanRoot() {
    const candidates = [
        os.tmpdir(),
        path.parse(os.tmpdir()).root,
        path.join(__dirname, '..', '..', '..', '..')
    ];
    for (const base of candidates) {
        if (ancestorHasPlaywright(base)) continue;
        try {
            // .native for the same reason repo-workflow-guard.test.js gives: on
            // Windows os.tmpdir() is often the 8.3 short form and git reports
            // the long one.
            return fs.realpathSync.native(
                fs.mkdtempSync(path.join(base, 'capture-preflight-test-')));
        } catch (e) { void e; }
    }
    return null;
}

const tmpRoot = cleanRoot();

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

let failures = 0;
async function run() {
    let ran = 0;
    for (const { name, fn } of queued) {
        ran++;
        try {
            await fn();
        } catch (e) {
            failures++;
            process.stderr.write('FAIL: ' + name + '\n  ' + e.message + '\n');
            if (e.stack) process.stderr.write(e.stack.split('\n').slice(1, 4).join('\n') + '\n');
        }
    }
    return ran;
}

function dir(...parts) {
    const d = path.join(tmpRoot, ...parts);
    fs.mkdirSync(d, { recursive: true });
    return d;
}

/**
 * A resolvable package standing in for playwright, in the folder the operator
 * is standing in. Not the real one: the assertion is about WHERE the recorder
 * looks, and a real install would make every result depend on what the machine
 * happens to have.
 */
function plantPlaywright(where) {
    const pkg = path.join(where, 'node_modules', 'playwright');
    fs.mkdirSync(pkg, { recursive: true });
    fs.writeFileSync(path.join(pkg, 'package.json'),
        JSON.stringify({ name: 'playwright', version: '0.0.0-fixture', main: 'index.js' }));
    fs.writeFileSync(path.join(pkg, 'index.js'), 'module.exports = { chromium: {} };\n');
    return where;
}

/** A browsers directory Playwright would accept, and one it would not. */
function plantChromium(where) {
    fs.mkdirSync(path.join(where, 'chromium-9999'), { recursive: true });
    return where;
}

/**
 * The child's view of the machine, with every location the resolver consults
 * pointed somewhere this test owns.
 *
 * APPDATA and PLAYWRIGHT_BROWSERS_PATH are npm's and Playwright's OWN
 * variables, not switches invented for testing -- the same reason NODE_PATH is
 * the fourth search location rather than a new one.
 */
function childEnv(over) {
    return Object.assign({}, process.env, {
        APPDATA: dir('machine-empty-appdata'),
        NODE_PATH: '',
        PLAYWRIGHT_BROWSERS_PATH: dir('machine-empty-browsers')
    }, over);
}

/**
 * Where playwright resolves from, asked by a process that sees what the
 * subprocess cases' recorder sees: the same `childEnv()`, a fixture folder as
 * its working directory, and this recorder's directory as the tool location.
 *
 * It runs the production resolver -- the one the recorder itself calls,
 * including the `npm root -g` refinement on the way out -- rather than
 * re-deriving any location here. A re-derivation is how this precondition
 * came to check the machine's real global root, which the fixture had already
 * pointed elsewhere (#6): the question is what the CHILD can reach, and only
 * the child's own resolver, in the child's own environment, can answer it.
 *
 * Returns `{ found, location, path, dir, label }`. `env` and `cwd` are
 * overridable so the probe itself can be proved to detect a planted install.
 */
function subprocessReach(env, cwd) {
    const nodeDep = path.join(__dirname, '..', 'lib', 'node-dependency.js');
    const script =
        'const r = require(' + JSON.stringify(nodeDep) + ').resolvePlaywright(' +
        '{ toolDir: ' + JSON.stringify(__dirname) + ' });' +
        'const s = r.searched.filter((l) => l.found)[0] || {};' +
        'process.stdout.write(JSON.stringify({ found: r.found, location: r.location,' +
        ' path: r.path, dir: s.dir || null, label: s.label || null }));';
    const r = spawnSync(process.execPath, ['-e', script], {
        cwd: cwd || dir('precondition-probe'),
        encoding: 'utf8', windowsHide: true, env: env || childEnv()
    });
    if (r.status !== 0) {
        throw new Error('the subprocess resolution probe itself failed (' + r.status + '): ' +
            (r.stderr || r.error));
    }
    return JSON.parse(r.stdout);
}

/**
 * The precondition's failure message: what the CHILD reaches, and from where.
 *
 * Names the location by the resolver's own label and path, so the reader is
 * told which search step answered -- not what the machine has installed. When
 * the answering location is the global root on a platform where the fixture's
 * APPDATA does not move it, that is the obstacle, and it is said.
 */
function describeReach(reach, platform) {
    const host = platform || process.platform;
    let text = 'a subprocess running with the fixture environment (childEnv) still resolves ' +
        'playwright -- from ' + reach.label + ' (' + reach.dir + '), as ' + reach.path +
        '. Every subprocess "the module is absent" case would pass without reaching the ' +
        'branch it names.';
    if (reach.location === 'global' && host !== 'win32') {
        text += ' On ' + host + " npm's global root comes from node's own install prefix " +
            '(or an npm prefix configured outside APPDATA), which the fixture cannot ' +
            'redirect: uninstall the global playwright there, or run this suite with a ' +
            'node and npm configuration whose global root holds none.';
    }
    return text;
}

function runCapture(cwd, argv, env) {
    const r = spawnSync(process.execPath, [captureJs, ...argv], {
        cwd, encoding: 'utf8', windowsHide: true, env: env || childEnv()
    });
    return { status: r.status, stdout: r.stdout || '', stderr: r.stderr || '' };
}

function git(cwd, args) {
    return execFileSync('git', args, { cwd, encoding: 'utf8', windowsHide: true });
}

// Every in-process preflight case below pins the platform to Windows.
//
// Not because the behaviour is Windows-specific, but because npm's default
// global root is derived DIFFERENTLY per platform -- `%APPDATA%\npm\node_modules`
// on Windows, `<prefix>/lib/node_modules` elsewhere. A fixture that sets
// APPDATA and lets the host platform decide controls the machine default on a
// Windows runner and not on a Linux one, where the REAL global root would be
// searched instead: the assertions would then depend on what the runner
// happens to have installed. Pinning makes one fixture mean the same thing
// everywhere. The per-platform derivation itself is pinned separately, on both
// branches, in node-dependency.test.js.
const WIN = 'win32';

// npm is silenced in every in-process case. The refinement spawns a REAL
// `npm root -g` against the machine running the test, and a box that has ever
// run `npm install -g playwright` would then supply the module to cases whose
// whole point is that nothing supplies it. The refinement has its own case,
// with an injected answer, in this file; the real subprocess is exercised in
// node-dependency.test.js where it is the subject rather than a dependency.
const NO_NPM = () => null;

// The suite's preconditions, asserted rather than assumed. Every "the module
// is absent" case below depends on nothing above the fixtures -- or above the
// recorder itself -- holding a playwright install. On a machine where one
// does, those cases would pass while testing nothing, which is the exact
// failure mode this whole issue is about.
test('PRECONDITION: no playwright install sits above the fixtures', () => {
    assert.ok(tmpRoot,
        'No usable fixture location was found: every candidate has a playwright install ' +
        'above it, or could not be written to. The absence cases cannot be trusted here.');
    assert.ok(!ancestorHasPlaywright(tmpRoot), 'fixture root is clean: ' + tmpRoot);
    assert.ok(!ancestorHasPlaywright(__dirname),
        "the recorder's own directory is clean, so the tool location cannot answer either");

    // And whatever the SUBPROCESS cases can reach (#6). Not the machine's real
    // global root: the subprocess cases run under childEnv(), whose fake
    // APPDATA already moves npm's Windows global root into the fixture, so a
    // global playwright on this machine -- the setup the recorder's own
    // remedy recommends -- is invisible to them. Asserting against the live
    // environment refused exactly that machine while testing nothing extra.
    //
    // So the child's own resolver is asked, in the child's own environment.
    // Anything still reachable from there -- a location the fixture cannot
    // redirect on this platform, an ancestor install, a user-level npm prefix
    // -- fails here, by name, instead of hollowing out the cases below.
    const reach = subprocessReach();
    assert.ok(!reach.found, describeReach(reach));

    // AND NODE_PATH, which is the one this precondition used to miss -- found
    // the hard way, by CI exporting it and ten in-process cases turning from
    // "the module is absent" into "the module is here", each reporting a
    // mismatch that said nothing about the cause.
    //
    // It is not enough that the in-process cases pass an `env` without
    // NODE_PATH. `require.resolve(name, { paths: [dir] })` STILL falls back to
    // `Module.globalPaths`, NODE_PATH is in it, and those paths are captured
    // when the process starts -- so no argument to the resolver can suppress
    // them. `childEnv()` protects the subprocess cases because a child gets a
    // fresh process with a blanked variable; nothing protects this one.
    //
    // So it is asserted rather than worked around. A machine whose NODE_PATH
    // holds playwright genuinely cannot run the absence cases, and saying so
    // once is worth more than ten failures that each look like a behaviour
    // change.
    for (const entry of (process.env.NODE_PATH || '').split(path.delimiter)) {
        const dir = entry.trim();
        if (!dir) continue;
        assert.ok(!fs.existsSync(path.join(dir, 'playwright')),
            'NODE_PATH points at a playwright install (' + dir + '), which every case below ' +
            'would resolve through -- Module.globalPaths is consulted even when the resolver ' +
            'is handed explicit paths. Unset NODE_PATH to run this suite.');
    }
});

// The precondition above is only as good as its probe. A probe that could not
// see a planted install would pass on every machine and guard nothing, so it
// is proved to detect one, in each kind of location the child consults.
test('the precondition probe reports a playwright planted above the child, by name', () => {
    const planted = plantPlaywright(dir('probe-planted-ancestor'));
    const reach = subprocessReach(childEnv(), dir('probe-planted-ancestor', 'deep', 'cwd'));
    assert.strictEqual(reach.found, true, 'an ancestor install is reachable from the child');
    assert.strictEqual(reach.location, 'cwd');
    const message = describeReach(reach);
    assert.ok(message.includes(path.join(planted, 'node_modules', 'playwright')),
        'the message names what the child actually resolved\n' + message);
    assert.ok(/fixture environment/.test(message),
        'and says it is the subprocess environment that reaches it\n' + message);
});

test('the precondition probe reports a playwright in a container location the child consults', () => {
    // The location #6 is about. On Windows the child's global root follows
    // APPDATA, so planting there is planting where the child looks; that is
    // also the proof that childEnv()'s fake APPDATA is what keeps a real
    // global install out of reach. Elsewhere the root follows node's own
    // prefix and cannot be planted from a fixture, so NODE_PATH -- the other
    // container location -- stands in for it.
    const container = process.platform === 'win32'
        ? path.join(dir('probe-planted-appdata'), 'npm')
        : dir('probe-planted-nodepath-parent');
    plantPlaywright(container);
    const env = process.platform === 'win32'
        ? childEnv({ APPDATA: dir('probe-planted-appdata') })
        : childEnv({ NODE_PATH: path.join(container, 'node_modules') });
    const reach = subprocessReach(env, dir('probe-planted-global-cwd'));
    assert.strictEqual(reach.found, true, 'a container install is reachable from the child');
    assert.ok(reach.path.startsWith(path.join(container, 'node_modules', 'playwright') + path.sep),
        'and it is the planted copy that answered: ' + reach.path);
    if (process.platform === 'win32') {
        // Only Windows can pin WHICH search step answered. NODE_PATH also
        // sits in the child's Module.globalPaths, so the first upward location
        // (the cwd) already resolves through it and claims the hit -- as CI
        // on Linux showed. The file that resolved is the fact either way.
        assert.strictEqual(reach.location, 'global');
        assert.strictEqual(reach.dir, path.join(container, 'node_modules'));
    }
});

test('where the fixture cannot redirect the global root, the message says so', () => {
    const reach = { found: true, location: 'global', label: "this machine's global npm modules",
        dir: '/usr/local/lib/node_modules', path: '/usr/local/lib/node_modules/playwright/index.js' };
    const elsewhere = describeReach(reach, 'linux');
    assert.ok(/cannot redirect/.test(elsewhere), 'names the real obstacle\n' + elsewhere);
    assert.ok(elsewhere.includes(reach.dir), elsewhere);
    assert.ok(!/cannot redirect/.test(describeReach(reach, 'win32')),
        'on Windows APPDATA does redirect it, so a hit there is something else');
});

// ---------------------------------------------------------------------------
// The preflight, in process
// ---------------------------------------------------------------------------

test('the preflight passes when the module and the browser are both there', async () => {
    const cwd = plantPlaywright(dir('pf-ok'));
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd, toolDir: dir('pf-ok-tool'), isTty: false,
        env: { APPDATA: dir('pf-ok-appdata'), PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-ok-browsers')) }
    });
    assert.strictEqual(r.ok, true, r.message);
});

test('a missing module is refused without prompting when there is no terminal', async () => {
    let asked = 0;
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: dir('pf-nomod'), toolDir: dir('pf-nomod-tool'), isTty: false,
        ask: () => { asked++; return Promise.resolve('y'); },
        env: { APPDATA: dir('pf-nomod-appdata'), PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-nomod-browsers')) }
    });
    assert.strictEqual(r.ok, false);
    assert.strictEqual(asked, 0, 'an agent or a CI run has nowhere to answer a prompt');
    assert.ok(/npm install -g playwright/.test(r.message), r.message);
});

test('the refusal names every folder it searched', async () => {
    const cwd = dir('pf-named-cwd');
    const toolDir = dir('pf-named-tool');
    const appdata = dir('pf-named-appdata');
    const nodePathDir = dir('pf-named-nodepath');
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd, toolDir, isTty: false,
        // npm silenced, so the location named is the one this test set up.
        // The refinement has its own case below.
        npmGlobalRoot: () => null,
        env: {
            APPDATA: appdata,
            NODE_PATH: nodePathDir,
            PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-named-browsers'))
        }
    });
    assert.strictEqual(r.ok, false);
    for (const d of [cwd, toolDir, path.join(appdata, 'npm', 'node_modules'), nodePathDir]) {
        assert.ok(r.message.includes(d), 'names ' + d + '\n--- message ---\n' + r.message);
    }
});

test('the folder named as the global root is the one npm actually reports', () => {
    // The default location is computed to keep the happy path off a
    // subprocess, and a computed guess can be wrong -- an `npm config set
    // prefix` moves it. On the way OUT, where a second is already being spent
    // on an error, npm is asked and the answer replaces the guess. Naming a
    // folder the operator does not use is how a correct message still sends
    // somebody to the wrong place.
    const guessed = dir('refine-appdata');
    const real = path.join(dir('refine-real'), 'node_modules');
    const r = capture.resolvePlaywright({
        cwd: dir('refine-cwd'), toolDir: dir('refine-tool'),
        env: { APPDATA: guessed }, platform: 'win32',
        npmGlobalRoot: () => real
    });
    assert.strictEqual(r.found, false);
    const global = r.searched.filter((s) => s.key === 'global')[0];
    assert.strictEqual(global.dir, real, 'npm had the last word');
});

test('a present module with a missing browser asks for the browser, not the module', async () => {
    // Two different facts. Telling an operator who already has the module to
    // `npm install` it is advice they will follow, watch succeed, and fail
    // again on the same line.
    const cwd = plantPlaywright(dir('pf-nobrowser'));
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd, toolDir: dir('pf-nobrowser-tool'), isTty: false,
        env: {
            APPDATA: dir('pf-nobrowser-appdata'),
            PLAYWRIGHT_BROWSERS_PATH: dir('pf-nobrowser-browsers')
        }
    });
    assert.strictEqual(r.ok, false);
    assert.ok(/npx playwright install chromium/.test(r.message), r.message);
    assert.ok(!/npm install -g playwright/.test(r.message),
        'the module is present -- do not send them to install it\n' + r.message);
});

test('with a terminal it offers to install, and declining prints the commands', async () => {
    const asked = [];
    let installs = 0;
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: dir('pf-decline'), toolDir: dir('pf-decline-tool'), isTty: true,
        ask: (q) => { asked.push(q); return Promise.resolve('n'); },
        install: () => { installs++; return { ok: true }; },
        env: {
            APPDATA: dir('pf-decline-appdata'),
            PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-decline-browsers'))
        }
    });
    assert.strictEqual(asked.length, 1, 'asked once');
    assert.strictEqual(installs, 0, 'declining installs nothing');
    assert.strictEqual(r.ok, false);
    assert.ok(/npm install -g playwright/.test(r.message),
        'and they are left with the command to run themselves\n' + r.message);
});

test('accepting runs the install into the machine default, then re-checks', async () => {
    // The owner's ask: "If the default location is missing, we can point it
    // somewhere" -- answered by installing into npm's global root, after which
    // every folder on the machine works.
    const appdata = dir('pf-accept-appdata');
    const commands = [];
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: dir('pf-accept'), toolDir: dir('pf-accept-tool'), isTty: true,
        ask: () => Promise.resolve('y'),
        install: (cmd, args) => {
            commands.push([cmd].concat(args).join(' '));
            // The install is what makes the module resolvable. Modelled the
            // way a real one would be: it puts the package in npm's global
            // root, and the SAME resolver then has to find it there.
            plantPlaywright(path.join(appdata, 'npm'));
            return { ok: true };
        },
        env: {
            APPDATA: appdata,
            PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-accept-browsers'))
        }
    });
    assert.deepStrictEqual(commands, ['npm install -g playwright']);
    assert.strictEqual(r.ok, true, 'the re-check finds what the install put there: ' + r.message);
});

test('ENTER installs -- the prompt defaults to YES, and says so', async () => {
    // THE DEFAULT IS THE ANSWER MOST PEOPLE GIVE, so it has to be the one they
    // mean. This prompt used to read [y/N]: an operator who had just been told
    // the recorder cannot run without the driver, and who was standing at a
    // terminal reading the remedy, had to type `y` to proceed -- while ENTER,
    // the key a hurried person presses, abandoned the run.
    //
    // That is backwards for this prompt specifically, and the distinction is the
    // one the rest of this file already draws: a prompt that can LOSE something
    // defaults to no (the leak gate, a scrub the gate refused), and a prompt
    // that is merely the next step defaults to yes (the capture store's own
    // `git init`). An install is the second kind. Nothing is lost by saying yes,
    // and the run cannot continue on no.
    //
    // Asserted on BOTH halves, because either alone is a lie: an empty answer
    // has to install, and the prompt has to advertise that it will. A [y/N]
    // prompt that silently installs on ENTER is worse than the defect.
    const appdata = dir('pf-default-yes-appdata');
    const prompts = [];
    const commands = [];
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: dir('pf-default-yes'), toolDir: dir('pf-default-yes-tool'), isTty: true,
        // What readline hands back for a bare ENTER.
        ask: (q) => { prompts.push(q); return Promise.resolve(''); },
        install: (cmd, args) => {
            commands.push([cmd].concat(args).join(' '));
            plantPlaywright(path.join(appdata, 'npm'));
            return { ok: true };
        },
        env: {
            APPDATA: appdata,
            PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-default-yes-browsers'))
        }
    });
    assert.deepStrictEqual(commands, ['npm install -g playwright'],
        'ENTER must install -- it is the answer an operator who agrees with the remedy gives');
    assert.strictEqual(r.ok, true, r.message);
    assert.strictEqual(prompts.length, 1);
    assert.match(prompts[0], /\[Y\/n\]/,
        'the prompt has to advertise the default it actually applies');
    assert.ok(!/\[y\/N\]/.test(prompts[0]),
        'a [y/N] prompt that installs on ENTER is worse than the default it fixed');
});

test('and an explicit `n` still declines, leaving the commands to run by hand', async () => {
    // The other half of the default: "yes unless you say no" must still take no
    // for an answer, and must still print what to run. `!/^n/` is the test, so
    // anything else -- including a typo -- proceeds, which is the correct bias
    // for a prompt whose yes is free and whose no ends the run.
    let installs = 0;
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: dir('pf-still-no'), toolDir: dir('pf-still-no-tool'), isTty: true,
        ask: () => Promise.resolve('no'),
        install: () => { installs++; return { ok: true }; },
        env: {
            APPDATA: dir('pf-still-no-appdata'),
            PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-still-no-browsers'))
        }
    });
    assert.strictEqual(installs, 0, 'declining installs nothing');
    assert.strictEqual(r.ok, false);
    assert.ok(/npm install -g playwright/.test(r.message), r.message);
});

test('the re-check is a real re-resolution, not a trust of the exit code', async () => {
    // An install that reports success and produces nothing is the case worth
    // catching: believing it hands the operator a browser launch failure
    // several steps later, with no mention of the install that did not work.
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: dir('pf-liar'), toolDir: dir('pf-liar-tool'), isTty: true,
        ask: () => Promise.resolve('y'),
        install: () => ({ ok: true }),
        env: {
            APPDATA: dir('pf-liar-appdata'),
            PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-liar-browsers'))
        }
    });
    assert.strictEqual(r.ok, false, 'still missing, so still refused');
});

test('an installer that never started is not described as having run', async () => {
    // On Windows, Node refuses to spawn npm's batch shim without a shell and
    // fails before any process exists -- spawnSync reports that in `error`
    // rather than throwing, so it is easy to swallow. "The install ran and it
    // still is not resolvable" then sends the operator to debug an install
    // that never began, which is this issue's own defect in a new place.
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: dir('pf-nostart'), toolDir: dir('pf-nostart-tool'), isTty: true,
        ask: () => Promise.resolve('y'),
        install: () => ({ ok: false, started: false }),
        env: {
            APPDATA: dir('pf-nostart-appdata'),
            PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-nostart-browsers'))
        }
    });
    assert.strictEqual(r.ok, false);
    assert.ok(/could not be started at all/.test(r.message),
        'says no process was created\n' + r.message);
    assert.ok(!/The install ran/.test(r.message),
        'and does not claim it ran\n' + r.message);
});

test('an installer that ran without producing a module does not blame the install', async () => {
    // The other side of the same distinction -- otherwise the new wording
    // could be produced by always saying "never started", which would be just
    // as misleading in the opposite direction.
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: dir('pf-ranfailed'), toolDir: dir('pf-ranfailed-tool'), isTty: true,
        ask: () => Promise.resolve('y'),
        install: () => ({ ok: false, started: true }),
        env: {
            APPDATA: dir('pf-ranfailed-appdata'),
            PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('pf-ranfailed-browsers'))
        }
    });
    assert.strictEqual(r.ok, false);
    assert.ok(/did not produce a usable module/.test(r.message), r.message);
    assert.ok(!/could not be started at all/.test(r.message), r.message);
    // On Windows `started` only reports that the INTERPRETER started: npm
    // missing from PATH still exits normally through cmd.exe. So this branch
    // must not claim the install ran -- it cannot know that.
    assert.ok(/not be on PATH/.test(r.message),
        'names the possibility it cannot rule out\n' + r.message);
});

test('accepting also fetches the browser when the browser is what is missing', async () => {
    const browsers = dir('pf-browser-accept-browsers');
    const commands = [];
    const r = await capture.preflightDependencies({
        platform: WIN,
        npmGlobalRoot: NO_NPM,
        cwd: plantPlaywright(dir('pf-browser-accept')), toolDir: dir('pf-browser-accept-tool'),
        isTty: true,
        ask: () => Promise.resolve('y'),
        install: (cmd, args) => {
            commands.push([cmd].concat(args).join(' '));
            plantChromium(browsers);
            return { ok: true };
        },
        env: {
            APPDATA: dir('pf-browser-accept-appdata'),
            PLAYWRIGHT_BROWSERS_PATH: browsers
        }
    });
    assert.deepStrictEqual(commands, ['npx playwright install chromium']);
    assert.strictEqual(r.ok, true, r.message);
});

// ---------------------------------------------------------------------------
// The preflight runs BEFORE anything else -- the real recorder
// ---------------------------------------------------------------------------

test('nothing is scaffolded when the dependency is missing', () => {
    // THE OWNER'S TRANSCRIPT, inverted. There, the run prompted, wrote
    // .har-profile.json, announced a capture root, and only then said the
    // module was missing. Here the module is checked first, so the folder is
    // untouched -- which is the whole difference between a failure you can
    // re-run and one you have to clean up after.
    const cwd = dir('order-empty');
    const r = runCapture(cwd, ['start', '--uri', 'https://example.com',
        '--describe', 'preflight fixture']);

    assert.strictEqual(r.status, 1, r.stderr);
    assert.ok(/playwright/.test(r.stderr), 'it is the dependency that is reported\n' + r.stderr);
    assert.ok(!fs.existsSync(path.join(cwd, harProfile.PROFILE_FILENAME)),
        'no profile was scaffolded');
    assert.ok(!fs.existsSync(path.join(cwd, '.har-captures')),
        'no capture store was created');
    assert.ok(!/Scaffold one here now/.test(r.stderr),
        'and no prompt was reached\n' + r.stderr);
});

test('the recorder finds a playwright installed in the operator folder', () => {
    // The acceptance criterion, driven end to end: run the UPSTREAM recorder
    // from a folder that installed its own copy, and the dependency preflight
    // is satisfied. Proven by the run getting past it to the next gate, which
    // is the operator profile -- a different complaint entirely.
    const cwd = plantPlaywright(dir('operator-install'));
    const r = runCapture(cwd, ['start', '--uri', 'https://example.com',
        '--describe', 'preflight fixture'],
    childEnv({ PLAYWRIGHT_BROWSERS_PATH: plantChromium(dir('operator-install-browsers')) }));

    assert.ok(!/the playwright module was not found/.test(r.stderr),
        'the operator folder answered\n' + r.stderr);
    assert.ok(new RegExp(harProfile.PROFILE_FILENAME.replace('.', '\\.')).test(r.stderr),
        'and the run advanced to the profile gate\n' + r.stderr);
});

test('--validate-only still answers without any browser dependency', () => {
    // It resolves paths and opens nothing. Gating it on a browser install
    // would break the one command that exists to answer questions without
    // side effects -- and several suites use it for exactly that.
    const cwd = dir('validate-only');
    fs.writeFileSync(path.join(cwd, harProfile.PROFILE_FILENAME),
        JSON.stringify({ salt: 'a'.repeat(48), literals: {} }), 'utf8');
    const r = runCapture(cwd, ['start', '--uri', 'https://example.com',
        '--describe', 'preflight fixture', '--port', '0', '--validate-only']);

    assert.strictEqual(r.status, 0, r.stderr);
    assert.ok(!/the playwright module was not found/.test(r.stderr), r.stderr);
    assert.ok(JSON.parse(r.stdout).sessionDir, 'it still reports the resolved session');
});

// ---------------------------------------------------------------------------
// A capture folder that is not a repository
// ---------------------------------------------------------------------------

test('a non-repo capture folder gets the ignore rules left behind', async () => {
    const root = dir('nonrepo-plain');
    const capturesRoot = path.join(root, '.har-captures');
    const r = await capture.ensureCapturesRootIgnored(
        { root: capturesRoot, currentWorkingTree: null, mainWorkingTree: null },
        { isTty: false });

    assert.strictEqual(r.ok, true, 'there is no repository, so nothing is refused');
    const ignore = path.join(root, '.gitignore');
    assert.ok(fs.existsSync(ignore), 'the rule is written beside the store');
    const text = fs.readFileSync(ignore, 'utf8');
    for (const entry of ['.har-captures/', '.har-profile.json']) {
        assert.ok(text.split(/\r?\n/).map((l) => l.trim()).includes(entry),
            'covers ' + entry + '\n' + text);
    }
});

test('a later git init cannot sweep the salt or the raw captures in', async () => {
    // THE FALSIFIER, and the only assertion here that could not be satisfied
    // by writing a file with the right words in it. The skill's own pipeline
    // runs `git init && git add -A` in this folder later; what matters is what
    // git then stages, so git is the one asked.
    const root = dir('nonrepo-sweep');
    fs.mkdirSync(path.join(root, '.har-captures', 'app.example.com'), { recursive: true });
    fs.writeFileSync(path.join(root, '.har-captures', 'app.example.com', 'raw.har'),
        '{"log":{"version":"1.2","entries":[]}}', 'utf8');
    fs.writeFileSync(path.join(root, harProfile.PROFILE_FILENAME),
        JSON.stringify({ salt: 'b'.repeat(48), literals: {} }), 'utf8');
    fs.writeFileSync(path.join(root, 'README.md'), '# fixture\n', 'utf8');

    await capture.ensureCapturesRootIgnored(
        { root: path.join(root, '.har-captures'), currentWorkingTree: null, mainWorkingTree: null },
        { isTty: false });

    git(root, ['init', '--quiet', '-b', 'main']);
    git(root, ['add', '-A']);
    const staged = git(root, ['diff', '--cached', '--name-only'])
        .split(/\r?\n/).map((l) => l.trim()).filter(Boolean);

    assert.ok(staged.includes('README.md'), 'ordinary files still stage: ' + staged.join(', '));
    assert.ok(!staged.some((f) => f.startsWith('.har-captures/')),
        'the unscrubbed raw stayed out: ' + staged.join(', '));
    assert.ok(!staged.includes(harProfile.PROFILE_FILENAME),
        'the salt stayed out: ' + staged.join(', '));
});

test('writing the rules is idempotent', async () => {
    const root = dir('nonrepo-idempotent');
    const placement = {
        root: path.join(root, '.har-captures'), currentWorkingTree: null, mainWorkingTree: null
    };
    await capture.ensureCapturesRootIgnored(placement, { isTty: false });
    const first = fs.readFileSync(path.join(root, '.gitignore'), 'utf8');
    await capture.ensureCapturesRootIgnored(placement, { isTty: false });
    assert.strictEqual(fs.readFileSync(path.join(root, '.gitignore'), 'utf8'), first,
        'a second recording appends nothing');
});

test('an existing .gitignore keeps its own content', async () => {
    // Append-only. The folder may already be somebody's, and a recorder that
    // rewrote their file would be a worse problem than the one it is fixing.
    const root = dir('nonrepo-existing');
    fs.writeFileSync(path.join(root, '.gitignore'), 'node_modules/\n*.log\n', 'utf8');
    await capture.ensureCapturesRootIgnored(
        { root: path.join(root, '.har-captures'), currentWorkingTree: null, mainWorkingTree: null },
        { isTty: false });

    const lines = fs.readFileSync(path.join(root, '.gitignore'), 'utf8')
        .split(/\r?\n/).map((l) => l.trim());
    assert.ok(lines.includes('node_modules/'), 'their rules survive');
    assert.ok(lines.includes('*.log'), 'all of them');
    assert.ok(lines.includes('.har-captures/'), 'and ours are added');
});

test('outside a repository, a terminal operator is offered git init, and yes makes the store ignored', async () => {
    // The scrub's substitution tables are reverse lookups of live credentials,
    // and it will not derive their home outside a work tree. So the one
    // question worth asking is whether to make this folder a repository --
    // default yes -- and the .gitignore just written then takes effect.
    const prompts = [];
    const root = dir('nonrepo-yes');
    const r = await capture.ensureCapturesRootIgnored(
        { root: path.join(root, '.har-captures'), currentWorkingTree: null, mainWorkingTree: null },
        { isTty: true, ask: (q) => { prompts.push(q); return Promise.resolve(''); } });
    assert.strictEqual(prompts.length, 1, 'asked exactly once');
    assert.match(prompts[0], /not a git repository/);
    assert.match(prompts[0], /\[Y\/n\]/);
    assert.strictEqual(r.ok, true);
    assert.strictEqual(r.initialized, root);
    assert.ok(fs.existsSync(path.join(root, '.git')), 'a repository was created');
    assert.strictEqual(r.status, subsDestination.IGNORED, 'and git now reports the store ignored');
    assert.ok(!r.unprotected);
});

test('outside a repository, no leaves the folder alone and proceeds unprotected', async () => {
    const root = dir('nonrepo-no');
    const r = await capture.ensureCapturesRootIgnored(
        { root: path.join(root, '.har-captures'), currentWorkingTree: null, mainWorkingTree: null },
        { isTty: true, ask: () => Promise.resolve('n') });
    assert.strictEqual(r.ok, true, 'declining git is not declining the capture');
    assert.strictEqual(r.unprotected, true);
    assert.ok(!fs.existsSync(path.join(root, '.git')), 'no repository was created');
    assert.ok(fs.existsSync(path.join(root, '.gitignore')), 'the rule is still left for a later repository');
});

test('outside a repository with no terminal, it neither prompts nor refuses', async () => {
    // An AI agent driving the capture has nowhere to answer. The skill tells it
    // to ask the user in the conversation first; here it simply proceeds.
    let asked = 0;
    const root = dir('nonrepo-quiet');
    const r = await capture.ensureCapturesRootIgnored(
        { root: path.join(root, '.har-captures'), currentWorkingTree: null, mainWorkingTree: null },
        { isTty: false, ask: () => { asked++; return Promise.resolve('y'); } });
    assert.strictEqual(asked, 0);
    assert.strictEqual(r.ok, true);
    assert.strictEqual(r.unprotected, true);
    assert.ok(!fs.existsSync(path.join(root, '.git')));
});

test('a failed git init refuses before anything is recorded', async () => {
    const root = dir('nonrepo-initfails');
    const r = await capture.ensureCapturesRootIgnored(
        { root: path.join(root, '.har-captures'), currentWorkingTree: null, mainWorkingTree: null },
        { isTty: true, ask: () => Promise.resolve('y'), gitInit: () => false });
    assert.strictEqual(r.ok, false);
    assert.match(r.message, /git init failed/);
});

test('a folder inside a repository is left to the existing guard', async () => {
    // The in-repo path is unchanged: git is asked, and an unignored store is
    // still refused or prompted. Writing a .gitignore into somebody's
    // repository without asking is exactly what that guard exists to avoid.
    const repo = dir('inrepo');
    git(repo, ['init', '--quiet', '-b', 'main']);
    const capturesRoot = path.join(repo, '.har-captures');
    fs.mkdirSync(capturesRoot, { recursive: true });

    const r = await capture.ensureCapturesRootIgnored(
        { root: capturesRoot, currentWorkingTree: repo, mainWorkingTree: repo },
        { isTty: false });

    assert.strictEqual(r.ok, false, 'an unignored store inside a repo is still refused');
    assert.ok(!fs.existsSync(path.join(repo, '.gitignore')),
        'and nothing was written into the repository unasked');
});

// ---------------------------------------------------------------------------

run().then((ran) => {
    if (failures) {
        process.stderr.write('\n' + failures + ' of ' + ran + ' capture-preflight tests FAILED\n');
        process.exitCode = 1;
    } else {
        process.stdout.write('All capture-preflight tests passed (' + ran + ')\n');
    }
    try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (err) { void err; }
});
