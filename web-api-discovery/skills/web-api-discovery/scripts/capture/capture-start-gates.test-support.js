'use strict';

/**
 * capture-start-gates.test-support.js -- A MACHINE THE START GATES PASS ON, OR
 * FAIL ON PURPOSE. For tests only (#25).
 *
 * `start --validate-only` used to be a dependency-free seam: it resolved paths
 * and exited 0 whatever the machine had. Since #25 it also answers the gates a
 * real start is refused on -- the browser driver, the operator profile and,
 * with `--ai`, the agent -- and exits 1 when one would refuse. So a test that
 * wants exit 0 has to stand on a machine where those gates pass, and a test
 * that wants a particular refusal has to stand on one where exactly that gate
 * fails. Neither can be left to what the machine running the suite happens to
 * have: a developer machine has a global `playwright` and a `claude`, and a CI
 * runner has neither.
 *
 * EVERY LEVER HERE IS ONE THE RECORDER ALREADY READS. NODE_PATH is the fourth
 * place the driver is looked for; APPDATA is where npm keeps its global modules
 * on Windows; PLAYWRIGHT_BROWSERS_PATH is Playwright's own; PATH is where the
 * agent is looked for. Nothing was added to the product to make it testable.
 *
 * Not a test file: the Node runner sweeps `*.test.js`, and this is loaded by
 * the suites that need it.
 */

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

/** The names a `claude` executable goes by, per platform. */
const AGENT_EXECUTABLES = process.platform === 'win32'
    ? ['claude.exe', 'claude.cmd', 'claude.bat', 'claude']
    : ['claude'];

/** What the fixture driver says if anything ever tries to open a browser with it. */
const LAUNCH_ATTEMPTED = 'fixture playwright: a browser launch was attempted';

function ensureDir(...parts) {
    const dir = path.join(...parts);
    fs.mkdirSync(dir, { recursive: true });
    return dir;
}

/**
 * A resolvable package standing in for playwright, under `where/node_modules`.
 *
 * Not the real one: the gate asks WHETHER the module resolves, and a real
 * install would make every answer depend on the machine. Its launch methods
 * throw by name, so a run that got as far as opening a browser says so instead
 * of opening one.
 */
function plantPlaywright(where) {
    const pkg = ensureDir(where, 'node_modules', 'playwright');
    fs.writeFileSync(path.join(pkg, 'package.json'),
        JSON.stringify({ name: 'playwright', version: '0.0.0-fixture', main: 'index.js' }));
    fs.writeFileSync(path.join(pkg, 'index.js'),
        'const refuse = () => { throw new Error(' + JSON.stringify(LAUNCH_ATTEMPTED) + '); };\n' +
        'module.exports = { chromium: { launchPersistentContext: refuse, connectOverCDP: refuse } };\n');
    return where;
}

/** A browsers directory the recorder accepts as holding a Chromium build. */
function plantChromium(where) {
    ensureDir(where, 'chromium-9999');
    return where;
}

/**
 * An operator profile in `dir`. The default is what the recorder's own scaffold
 * writes; pass `profile` to plant one that is incomplete on purpose.
 */
function plantOperatorProfile(dir, profile) {
    const file = path.join(dir, '.har-profile.json');
    fs.writeFileSync(file,
        JSON.stringify(profile || { salt: 'a'.repeat(48), literals: {} }, null, 2) + '\n', 'utf8');
    return file;
}

/**
 * A `claude` that spawn() can start and that answers `--version` with exit 0,
 * which is the question the recorder asks before it will drive anything.
 *
 * On Windows that has to be a real executable -- spawn() without a shell cannot
 * launch a `.cmd` -- so it is node itself under the agent's name.
 */
function plantAgent(where) {
    if (process.platform === 'win32') {
        const exe = path.join(where, 'claude.exe');
        if (!fs.existsSync(exe)) {
            try { fs.linkSync(process.execPath, exe); } catch (e) { void e; fs.copyFileSync(process.execPath, exe); }
        }
    } else {
        const shim = path.join(where, 'claude');
        fs.writeFileSync(shim, '#!/bin/sh\necho 0.0.0-fixture\n', 'utf8');
        fs.chmodSync(shim, 0o755);
    }
    return where;
}

/** `value` (a PATH string) with every directory that holds a `claude` removed. */
function pathWithoutAgent(value) {
    return String(value || '').split(path.delimiter).filter((dir) => {
        if (!dir) return false;
        return !AGENT_EXECUTABLES.some((name) => {
            try { return fs.existsSync(path.join(dir, name)); } catch (e) { void e; return false; }
        });
    }).join(path.delimiter);
}

/**
 * The variables that put a child on a machine in a chosen state.
 *
 * `state` says which of the three things the gates look for are THERE:
 *
 *   driver   the `playwright` module                       (default: there)
 *   browser  a Chromium build in Playwright's browsers dir (default: there)
 *   agent    a `claude` that can be spawned                (default: not there)
 *
 * The fixtures live under `<root>/start-gates`, so every suite keeps them inside
 * its own temporary root and removes them with it. `agent` may also be a
 * directory `plantAgent` already filled: a suite that builds many machines
 * shares one agent between them, because the first run of a new executable is
 * slow on Windows and every machine would pay for it again.
 *
 * ABSENT IS MADE, NOT ASSUMED. With `driver: false` the Windows global npm root
 * is moved into the fixture (APPDATA) and NODE_PATH is blanked, so a machine
 * that has `npm install -g playwright` -- the setup the recorder's own remedy
 * recommends -- still looks like one that has not. Elsewhere npm's global root
 * follows node's own install prefix and cannot be moved; `driverReach` exists
 * so a suite can assert that nothing is reachable there rather than hope. The
 * agent is always pruned from PATH first, so `agent: false` means none.
 */
function gateVars(root, state) {
    const want = Object.assign({ driver: true, browser: true, agent: false }, state || {});
    const home = ensureDir(root, 'start-gates');
    const agentDir = want.agent === true ? plantAgent(ensureDir(home, 'agent')) : want.agent;
    const vars = {
        NODE_PATH: want.driver
            ? path.join(plantPlaywright(ensureDir(home, 'driver')), 'node_modules')
            : '',
        PLAYWRIGHT_BROWSERS_PATH: want.browser
            ? plantChromium(ensureDir(home, 'browsers'))
            : ensureDir(home, 'no-browsers'),
        PATH: (agentDir ? agentDir + path.delimiter : '') + pathWithoutAgent(process.env.PATH)
    };
    if (!want.driver) vars.APPDATA = ensureDir(home, 'no-global-modules');
    return vars;
}

/**
 * This process's environment with `vars` laid over it, for a child.
 *
 * Built key by key so a replaced PATH is the ONLY path-shaped key in the
 * result. Windows spells it `Path`, and an object carrying both leaves which
 * one the child sees up to the platform -- which, for a case whose point is
 * that the agent is unreachable, would be a silent pass.
 */
function childEnv(vars) {
    const over = vars || {};
    const env = {};
    for (const [key, value] of Object.entries(process.env)) {
        if (over.PATH !== undefined && key.toUpperCase() === 'PATH') continue;
        env[key] = value;
    }
    return Object.assign(env, over);
}

/**
 * Where a child in `env`, standing in `cwd`, resolves the driver from -- asked
 * of the production resolver, with `toolDir` as the recorder's own location.
 *
 * The precondition behind every "the driver is absent" case: a suite that
 * cannot make the driver unreachable would pass those cases while testing
 * nothing. Returns `{ found, location, path }`.
 */
function driverReach(env, cwd, toolDir) {
    const nodeDep = path.join(__dirname, '..', 'lib', 'node-dependency.js');
    const script =
        'const r = require(' + JSON.stringify(nodeDep) + ').resolvePlaywright(' +
        '{ toolDir: ' + JSON.stringify(toolDir || __dirname) + ' });' +
        'process.stdout.write(JSON.stringify({ found: r.found, location: r.location, path: r.path }));';
    const r = spawnSync(process.execPath, ['-e', script],
        { cwd, encoding: 'utf8', windowsHide: true, env });
    if (r.status !== 0) {
        throw new Error('the driver resolution probe itself failed (' + r.status + '): ' +
            (r.stderr || r.error));
    }
    return JSON.parse(r.stdout);
}

module.exports = {
    AGENT_EXECUTABLES,
    LAUNCH_ATTEMPTED,
    plantPlaywright,
    plantChromium,
    plantOperatorProfile,
    plantAgent,
    pathWithoutAgent,
    gateVars,
    childEnv,
    driverReach
};
