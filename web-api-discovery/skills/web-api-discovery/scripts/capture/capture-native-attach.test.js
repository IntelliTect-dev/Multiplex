#!/usr/bin/env node
// Behavior tests for issue #73 -- recording in a browser Playwright did NOT
// launch. Zero-dep; runs with `node capture-native-attach.test.js`.
//
// ## The incident
//
// `capture-har.js start` could only record in a browser it launched with
// Playwright, and Playwright's launch is DETECTABLE. On a protected site that
// ends the recording before it starts. Measured against car-rental sites, by
// hand, with nothing automated driving them:
//
//   Hertz          Imperva      403 "Access denied, Error 15" on the FIRST page
//                               load, before any interaction
//   Costco Travel  Akamai       search POST reset (ERR_HTTP2_PROTOCOL_ERROR),
//                               including with a fresh profile
//   Avis           DataDome     slider CAPTCHA, then 403 even once solved
//   Priceline      PerimeterX   "Press & Hold" as soon as the location field
//                               was typed into, including with a fresh profile
//
// The same sites, machine and network all worked in a normally launched Chrome.
// The only way to get a HAR out of THAT browser was a DevTools export by hand,
// and Chrome writes a 0-byte file when exporting a large multi-site log -- so
// the capture was unobtainable rather than merely awkward.
//
// ## Why the integration case is a real browser and a real detach
//
// The property that makes this mode safe -- `stop` DETACHES, and the operator's
// browser and every tab in it survive -- is a property of Playwright's CDP
// connection, not of this file. A test that inspected our own teardown branch
// would pass whether or not `browser.close()` takes Chrome down with it, which
// is the one thing worth knowing. So I1 below starts a real Chrome, records a
// real page load, stops through the real `stop` command, and then asks the
// BROWSER whether it is still there.
//
// It SKIPS, loudly, when playwright or Chrome is absent -- and skipping is
// reported as a skip rather than as a pass, because a suite that goes quiet is
// how #304 shipped. Everything above I1 runs everywhere, including in CI.
//
// IN CI A SKIP IS A FAILURE. These two are the only cases anywhere that attach
// to a real browser, so a runner that skips them has proved nothing about the
// mode they cover -- which is precisely the position three gitleaks-backed tests
// were in when tests/GitleaksGuard.ps1 was written. CI installs the driver and
// provides a display (see .github/workflows/web-api-discovery.yml at the repository root); if either is missing, these
// fail there and skip on a laptop, where requiring both would be a poor trade.
//
// FALSIFIERS (fail before #73, pass after):
//   N1  --browser native records its mode, its endpoint and its profile
//   N2  --attach reads the port and the endpoint off what the operator gave,
//       and records no profile path, because it did not choose one
//   N3  every unanswerable flag combination is refused as a usage error
//   N4  a ws:// URL is refused with its own message, never coerced
//   N5  the launch carries NO automation flags -- the whole point of the mode
//   N6  a native run does not report exit 5: recordHar never ran, so
//       "recordHar lost" is not a fact about it
//   N7  the banner says whose browser it is and what stopping will do
//   N8  the wait for the debugging port gives up as soon as the process we
//       spawned is gone (Chrome's profile HANDOFF, the everyday failure)
//   N9  a profile in a synced folder is warned about
//   N10 a profile inside a work tree gets a rule that git then honours
//   N11 in CI, a missing browser FAILS instead of skipping -- the guard that
//       keeps I1 and I2 from quietly not running on the one machine whose green
//       anybody relies on
//   I1  a natively launched Chrome records a local page, and is STILL RUNNING
//       after `stop` -- with a non-empty raw.har
//   I2  attaching to a browser that is already running records in it, and
//       stopping closes NONE of the tabs it already had -- asserted by target
//       id against the browser's own list, because that is the guarantee an
//       operator has to believe before they will attach to the browser they are
//       signed into
//
// FALSIFIERS for issue #85 (fail before #85, pass after). An attached browser is
// the operator's OWN browser, so "every request in the context" stopped meaning
// "every request in this capture" -- other signed-in tabs landed in raw.har:
//   S1  a page this run opened is in scope, and stays in scope wherever it goes
//       -- including its requests to OTHER sites, which are the API traffic a
//       capture exists to find
//   S2  an unrelated tab on another site is out; the same tab is in once it
//       navigates to the target's site
//   S3  a page opened FROM an in-scope page is in (the OAuth/popup window),
//       on any site; one opened from an unrelated tab is not
//   S4  "the target's site" is the host minus a leading www., and its subdomains
//       -- never a lookalike suffix
//   S5  a request whose page cannot be resolved is refused: the default for a
//       leak is "not recorded"
//   N12 the stop line says the debugging port is still open, and to close the
//       window -- and the banner says which tabs are recorded
//   I3  attached to a browser with an unrelated tab polling another site, that
//       tab's traffic is ABSENT from raw.har while the capture page's own
//       cross-site API call, and a rel=noopener sign-in window it opens on
//       that other site, are PRESENT
//
// GUARDS (passed before this change; they must keep passing):
//   G1  the default mode is untouched -- playwright, recordHar, localhost
//   G2  exit 5 still means what it meant for a Playwright-launched run

'use strict';

const assert = require('assert');
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { execFileSync, spawn, spawnSync } = require('child_process');

const captureJs = path.join(__dirname, 'capture-har.js');

// HEADLESS, in tests only (#88). The production launch is pinned flag-for-flag
// by N5 and stays headful; these two seams add `--headless=new` to the Chrome
// a TEST starts, so no run leaves a window on a developer's desktop.
//
//   headlessSpawn     for a browser this file launches itself, through
//                     launchNativeChrome's spawn seam (I2, I3).
//   HEADLESS_PRELOAD  for the browser the RECORDER launches (I1): preloaded
//                     into that process, it wraps child_process.spawn before
//                     capture-har.js takes its reference to it.
const HEADLESS_PRELOAD = path.join(__dirname, 'capture-native-headless.test-support.js');
const headlessSpawn = (exe, argv, opts) => spawn(exe,
    argv.filter((a) => a !== 'about:blank').concat(['--headless=new', 'about:blank']), opts);
const capture = require(captureJs);
const gates = require(path.join(__dirname, 'capture-start-gates.test-support.js'));

/**
 * `fs.realpathSync` does not expand a Windows 8.3 short name and os.tmpdir()
 * hands one back, while git answers in the long form. Same fixture hazard the
 * durability suite documents, same resolver.
 */
function real(p) {
    try { return fs.realpathSync.native(p); } catch (e) { void e; return fs.realpathSync(p); }
}

const tmpRoot = real(fs.mkdtempSync(path.join(os.tmpdir(), 'capture-native-')));
let seq = 0;
function scratch(name) {
    const dir = path.join(tmpRoot, `${name}-${seq++}`);
    fs.mkdirSync(dir, { recursive: true });
    return real(dir);
}

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }

// Named in the success line, so a run that could not exercise a browser cannot
// be mistaken for one that did. Declared up here because the guard that fills it
// runs long before the runner at the bottom reads it.
const skipped = [];

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------------------
// fixtures
// ---------------------------------------------------------------------------

function git(cwd, args) {
    return execFileSync('git', args, {
        cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true
    }).trim();
}

/**
 * A repository with `.har-captures/` ignored -- the shape a consuming project
 * has, and the one the recorder's own store guard demands before it will record
 * anything at all.
 */
function makeRepo(name) {
    const dir = scratch(name);
    git(dir, ['init', '--quiet', '-b', 'main']);
    fs.writeFileSync(path.join(dir, '.gitignore'),
        '.har-captures/\n.har-profile.json\n', 'utf8');
    fs.writeFileSync(path.join(dir, 'README.md'), '# fixture\n', 'utf8');
    git(dir, ['add', '-A']);
    git(dir, [
        '-c', 'user.email=fixture@example.invalid', '-c', 'user.name=fixture',
        'commit', '--quiet', '-m', 'init'
    ]);
    return dir;
}

/** What scaffoldProfile writes, so the recorder's profile preflight passes. */
function plantOperatorProfile(dir) {
    const file = path.join(dir, '.har-profile.json');
    fs.writeFileSync(file, JSON.stringify({
        salt: 'a'.repeat(48), literals: {}
    }, null, 2) + '\n', 'utf8');
    return file;
}

// A MACHINE THE CHECK-ONLY START PASSES ON (#25). `--validate-only` exits 1 when
// a real start would be refused for want of the browser driver, so the cases
// that ask it how a mode RESOLVES stand on a machine that has one -- a stand-in,
// reached the way the recorder documents (NODE_PATH), and only for these runs.
// The cases that really launch a browser use `driverEnv()` and the real driver.
const passingGates = gates.childEnv(gates.gateVars(tmpRoot));

/** `start --validate-only`: the recorder's own resolution, not a copy of it. */
function resolveViaRecorder(cwd, args) {
    const r = spawnSync(process.execPath, [
        captureJs, 'start', '--uri', 'https://example.com',
        '--describe', 'native fixture', '--validate-only', ...(args || [])
    ], { cwd, encoding: 'utf8', windowsHide: true, env: passingGates });
    return {
        status: r.status,
        stderr: r.stderr || '',
        session: r.status === 0 ? JSON.parse(r.stdout) : null
    };
}

function runCapture(cwd, argv, opts) {
    const r = spawnSync(process.execPath, [captureJs, ...argv], Object.assign({
        cwd, encoding: 'utf8', windowsHide: true
    }, opts || {}));
    return { status: r.status, stdout: r.stdout || '', stderr: r.stderr || '' };
}

async function cdpAnswers(endpoint) {
    try {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 2000);
        const res = await fetch(`${endpoint}/json/version`, { signal: controller.signal });
        clearTimeout(timer);
        return res.ok;
    } catch (e) { void e; return false; }
}

/**
 * The browser a test launched must be HEADLESS (#88). These cases run on the
 * owner's desktop as well as in CI, and a visible window per run, left behind
 * whenever a run dies, is a cost with nothing bought by it. Asked of the
 * browser itself -- Chrome names itself HeadlessChrome when it is one -- so a
 * test that stops making it headless fails here rather than on someone's
 * screen. The PRODUCTION launch stays headful; N5 pins that.
 */
async function assertHeadless(endpoint, name) {
    const res = await fetch(`${endpoint}/json/version`);
    const v = await res.json();
    const ua = String(v['User-Agent'] || v.Browser || '');
    assert.ok(/Headless/i.test(ua),
        `${name}: the test's browser must be headless (#88), but it reported "${v.Browser}"`);
}

// ---------------------------------------------------------------------------
// N1, N2, G1 -- what each mode resolves to, asked of the recorder itself
// ---------------------------------------------------------------------------

test('N1: --browser native records its mode, its endpoint and a profile it owns', () => {
    const repo = makeRepo('native-resolve');
    plantOperatorProfile(repo);
    const r = resolveViaRecorder(repo, ['--browser', 'native']);
    assert.strictEqual(r.status, 0, r.stderr);

    assert.strictEqual(r.session.mode, 'native-chrome');
    assert.strictEqual(r.session.browser, 'native');
    assert.strictEqual(r.session.attached, false);
    assert.strictEqual(r.session.ownsRecordHar, false,
        'Playwright did not launch this browser, so there is no recordHar to own');
    assert.ok(r.session.profileDir,
        'a browser this run starts needs a dedicated profile -- Chrome refuses remote ' +
        'debugging on one another Chrome already has open');
    // 127.0.0.1 and not `localhost`: Chrome binds the debugging port to the IPv4
    // loopback, and Node's resolver may answer `localhost` with ::1 first. The
    // literal is the only spelling that cannot fail to reach the browser this
    // process just launched.
    assert.ok(/^http:\/\/127\.0\.0\.1:\d+$/.test(r.session.cdpEndpoint),
        `expected an IPv4-literal endpoint, got ${r.session.cdpEndpoint}`);
});

test('N2: --attach reads the port and endpoint it was given, and claims no profile', () => {
    const repo = makeRepo('attach-resolve');
    plantOperatorProfile(repo);
    const r = resolveViaRecorder(repo, ['--attach', '127.0.0.1:9321']);
    assert.strictEqual(r.status, 0, r.stderr);

    assert.strictEqual(r.session.mode, 'attached-chrome');
    assert.strictEqual(r.session.browser, 'native');
    assert.strictEqual(r.session.attached, true);
    assert.strictEqual(r.session.ownsRecordHar, false);
    assert.strictEqual(r.session.cdpEndpoint, 'http://127.0.0.1:9321');
    // NOT the next free port. Falling forward is right for a browser about to be
    // started and exactly wrong for one already listening: the next free port is
    // the one nothing is on.
    assert.strictEqual(r.session.port, 9321,
        'an attached browser\'s port is read, never chosen');
    assert.strictEqual(r.session.profileDir, null,
        'the browser is running on a profile this run did not choose and cannot name -- ' +
        'a guess here would put an unverified path in session.json');
});

test('G1: the default mode is untouched -- playwright, recordHar, localhost', () => {
    const repo = makeRepo('default-resolve');
    plantOperatorProfile(repo);
    const r = resolveViaRecorder(repo);
    assert.strictEqual(r.status, 0, r.stderr);

    assert.strictEqual(r.session.mode, 'chrome-profile');
    assert.strictEqual(r.session.browser, 'playwright');
    assert.strictEqual(r.session.attached, false);
    assert.strictEqual(r.session.ownsRecordHar, true);
    assert.ok(r.session.cdpEndpoint.startsWith('http://localhost:'),
        'agents have been told to expect localhost here; a native run is the one that moved');
});

// ---------------------------------------------------------------------------
// N3, N4 -- the refusals
// ---------------------------------------------------------------------------

test('N3: every unanswerable flag combination is a usage error, not a guess', () => {
    const repo = makeRepo('exclusivity');
    plantOperatorProfile(repo);
    const cases = [
        // --isolated is bundled Chromium on a throwaway profile, which IS the
        // launch a protected site refuses. Honouring one flag while the operator
        // typed both would record in the browser they were avoiding, and the
        // evidence would be a 403 they then spend an afternoon on.
        { args: ['--browser', 'native', '--isolated'], match: /exclusive/i },
        { args: ['--attach', '9222', '--isolated'], match: /exclusive/i },
        // The endpoint already names the port, and the browser already has a
        // profile. Accepting either would be accepting a value this run cannot
        // honour.
        { args: ['--attach', '9222', '--port', '9333'], match: /port/i },
        { args: ['--attach', '9222', '--profile', 'x'], match: /profile/i },
        { args: ['--attach', '9222', '--browser', 'playwright'], match: /not\s+one Playwright/i },
        { args: ['--browser', 'chromium'], match: /--browser takes/i }
    ];
    for (const c of cases) {
        const r = runCapture(repo, [
            'start', '--uri', 'https://example.com', '--describe', 'x', ...c.args
        ]);
        assert.strictEqual(r.status, 2,
            `${c.args.join(' ')} should be a usage error (2), got ${r.status}\n${r.stderr}`);
        assert.ok(c.match.test(r.stderr),
            `${c.args.join(' ')} was refused without saying why: ${r.stderr}`);
    }
});

test('N4: a ws:// URL is refused with its own message, never coerced', () => {
    // It is the DevTools socket rather than the endpoint connectOverCDP wants,
    // and it names ONE target and expires -- so coercing it would produce an
    // intermittent failure a long way from the flag that caused it.
    const bad = capture.parseCdpEndpoint('ws://127.0.0.1:9222/devtools/browser/abc');
    assert.ok(bad.error, 'a websocket URL must not resolve to an endpoint');
    assert.match(bad.error, /websocket/i);
    assert.ok(!bad.endpoint);

    // And the spellings that ARE accepted, because all three are things people
    // actually have in hand.
    assert.deepStrictEqual(capture.parseCdpEndpoint('9222'),
        { endpoint: 'http://127.0.0.1:9222', port: 9222 });
    assert.deepStrictEqual(capture.parseCdpEndpoint('127.0.0.1:9222'),
        { endpoint: 'http://127.0.0.1:9222', port: 9222 });
    assert.deepStrictEqual(capture.parseCdpEndpoint('http://localhost:9222/'),
        { endpoint: 'http://localhost:9222', port: 9222 });
    assert.ok(capture.parseCdpEndpoint('localhost').error,
        'an endpoint with no port cannot be a debugging endpoint');
});

// ---------------------------------------------------------------------------
// N5 -- THE FALSIFIER for the whole mode
// ---------------------------------------------------------------------------

test('N5: the native launch carries NO automation flags', () => {
    // This is the entire reason the mode exists. If an automation switch ever
    // reappears in this list the feature is gone while every other test in this
    // file still passes, because the recording would keep working -- on the
    // sites that do not check.
    const calls = [];
    const fakeSpawn = (exe, args, opts) => {
        calls.push({ exe, args, opts });
        return { pid: 4242, unref() {}, once() {} };
    };
    const profileDir = path.join(tmpRoot, 'flags-profile');
    const { args } = capture.launchNativeChrome({
        executable: 'C:\\does\\not\\matter\\chrome.exe',
        profileDir,
        port: 9411,
        spawn: fakeSpawn
    });

    assert.strictEqual(calls.length, 1);
    assert.deepStrictEqual(args, [
        `--user-data-dir=${profileDir}`,
        '--remote-debugging-port=9411',
        '--no-first-run',
        '--no-default-browser-check',
        'about:blank'
    ], 'the argument list IS the feature -- every entry is accounted for in ' +
       'launchNativeChrome, and nothing else belongs in it');

    // Stated as a prohibition as well as an equality: the list above would also
    // have to change for a flag to be added, but this is the assertion whose
    // failure says WHY it matters.
    const forbidden = /automation|blink-features|headless|disable-extensions|disable-gpu|remote-allow-origins|load-extension/i;
    for (const a of args) {
        assert.ok(!forbidden.test(a), `${a} is an automation marking -- a site reads it and refuses`);
    }

    // about:blank ON PURPOSE: the site is navigated to after the attach, so its
    // first request -- the challenge, the cookies, the redirect chain -- is
    // recorded. Handing the URL to the command line would load it while nothing
    // was listening.
    assert.strictEqual(args[args.length - 1], 'about:blank');

    // Detached, because the browser outlives this process: `stop` detaches and
    // leaves it running. Tying the lifetimes together would end a recording by
    // taking down a window full of the operator's signed-in tabs.
    assert.strictEqual(calls[0].opts.detached, true);
});

// ---------------------------------------------------------------------------
// N6, G2 -- what exit 5 is allowed to mean
// ---------------------------------------------------------------------------

test('G2/N6: exit 5 reports a LOST recordHar, so it cannot describe a native run', () => {
    // G2 -- unchanged for a Playwright-launched run.
    assert.strictEqual(capture.postProcessExitCode({ errors: [], warnings: [] }, true), 5);
    assert.strictEqual(capture.postProcessExitCode({ errors: [], warnings: [] }, false), 0);
    // An error outranks it, and so does an advisory warning.
    assert.strictEqual(capture.postProcessExitCode({ errors: ['x'], warnings: [] }, true), 6);
    assert.strictEqual(capture.postProcessExitCode({ errors: [], warnings: ['x'] }, true), 7);

    // N6 -- and the caller is what makes a native run exempt: `ownsRecordHar` is
    // false, so `assembled && ownsRecordHar` is false however the assembly went.
    // Without this, the NORMAL ending of every native capture would be a
    // non-zero exit, and Invoke-HarCapture would advise pressing ENTER next time
    // for a richer HAR that cannot exist.
    const repo = makeRepo('exit-code');
    plantOperatorProfile(repo);
    const native = resolveViaRecorder(repo, ['--browser', 'native']);
    assert.strictEqual(native.session.ownsRecordHar, false);
    const attached = resolveViaRecorder(repo, ['--attach', '9222']);
    assert.strictEqual(attached.session.ownsRecordHar, false);
});

// ---------------------------------------------------------------------------
// N7 -- what the operator is told
// ---------------------------------------------------------------------------

test('N7: the banner says whose browser it is and what stopping will do', () => {
    const text = (session) => capture.startBannerLines(session)
        .map(([, line]) => line).join('\n');

    const attached = text({
        uri: 'https://example.com', attached: true, browser: 'native',
        ownsRecordHar: false, cdpEndpoint: 'http://127.0.0.1:9222', port: 9222,
        requestedPort: 9222, profileDir: null, harPath: 'r', outputPath: 'o'
    });
    assert.match(attached, /did not launch/i);
    assert.match(attached, /detach/i, 'an operator must be told their tabs survive');

    const native = text({
        uri: 'https://example.com', attached: false, browser: 'native',
        ownsRecordHar: false, cdpEndpoint: 'http://127.0.0.1:9333', port: 9333,
        requestedPort: 9333, profileDir: 'p', harPath: 'r', outputPath: 'o'
    });
    assert.match(native, /no automation flags/i);
    assert.match(native, /leaves it running/i,
        'every other mode ends with the browser gone, so a window that stays open reads ' +
        'as a leak unless it was announced as the contract');

    // "The most complete HAR" is a claim about recordHar. Saying it where
    // recordHar never runs would promise a richer artifact for pressing a key
    // that cannot produce one.
    assert.ok(!/most complete HAR/i.test(native));
    assert.ok(!/most complete HAR/i.test(attached));
    const playwrightMode = text({
        uri: 'https://example.com', browser: 'playwright', ownsRecordHar: true,
        cdpEndpoint: 'http://localhost:9333', port: 9333, requestedPort: 9333,
        profileDir: 'p', harPath: 'r', outputPath: 'o'
    });
    assert.match(playwrightMode, /most complete HAR/i,
        'and it is still said where it is true');
});

// ---------------------------------------------------------------------------
// N8 -- the failure that actually happens
// ---------------------------------------------------------------------------

test('N8: the wait for the debugging port ends as soon as our own process is gone', async () => {
    // CHROME'S PROFILE HANDOFF. Point chrome.exe at a profile another Chrome
    // already has open and the new process forwards the request to that instance
    // and exits immediately, having applied none of our flags. Nothing ever
    // answers on the port.
    //
    // A timeout-only wait spends the full 30 seconds and then reports "the
    // browser did not start" about a browser that started and left. Asking
    // whether our own child is alive turns that into an immediate, accurate
    // failure -- so the assertion is about ELAPSED TIME, which a
    // timeout-only implementation cannot satisfy.
    let gone = false;
    const started = Date.now();
    const answer = await capture.waitForCdp('http://127.0.0.1:1', 30000, {
        probe: async () => null,
        stillStarting: () => {
            gone = true;          // alive for exactly one probe, then not
            return !gone;
        }
    });
    const elapsed = Date.now() - started;
    assert.strictEqual(answer, null);
    assert.ok(elapsed < 5000,
        `gave up after ${elapsed}ms -- a dead child must not be waited on for the full ` +
        'timeout, which is the difference between an accurate failure and a silent 30s');

    // And it still returns the version document when one arrives.
    const version = await capture.waitForCdp('http://127.0.0.1:1', 5000, {
        probe: async () => ({ Browser: 'Chrome/999' })
    });
    assert.deepStrictEqual(version, { Browser: 'Chrome/999' });
});

// ---------------------------------------------------------------------------
// N9, N10 -- a profile is as committable as a capture
// ---------------------------------------------------------------------------

test('N9: a profile in a synced folder is warned about', () => {
    // A profile is a set of databases the browser writes continuously: a sync
    // client copying one mid-write corrupts it, and carries live session cookies
    // to a cloud account on the way.
    for (const dir of [
        path.join('C:', 'Users', 'x', 'OneDrive - Contoso', 'proj', '.chrome-profile'),
        path.join('C:', 'Users', 'x', 'Dropbox', 'proj', '.chrome-profile'),
        path.join('C:', 'Users', 'x', 'OneDrive', 'p')
    ]) {
        const warning = capture.syncedFolderWarning(dir);
        assert.ok(warning, `${dir} should be reported as a synced location`);
        assert.match(warning, /session cookies/i, 'the warning has to say what is at stake');
    }
    assert.strictEqual(
        capture.syncedFolderWarning(path.join('C:', 'Users', 'x', 'proj', '.chrome-profile')),
        null, 'and an ordinary local path is not warned about');
});

test('N10: a profile inside a work tree gets a rule git then honours', () => {
    // Asked of GIT afterwards, not of the file we wrote. A rule that does not
    // take effect -- a later negation, a nested .gitignore -- must never be
    // reported as protection, which is the contract ensureCapturesRootIgnored
    // holds itself to for the capture store.
    const repo = makeRepo('profile-ignore');
    const profileDir = path.join(repo, '.chrome-profile');
    fs.mkdirSync(profileDir, { recursive: true });
    // A FILE, not merely the directory. git tracks files, so an empty directory
    // is invisible to `status` and the fixture would assert nothing -- while the
    // real hazard is exactly this: the cookie database Chrome writes into it.
    fs.writeFileSync(path.join(profileDir, 'Cookies'), 'fixture', 'utf8');

    const before = git(repo, ['status', '--porcelain']);
    assert.match(before, /\.chrome-profile/,
        'fixture: an unignored profile directory must show up as untracked first');

    const result = capture.ensureProfileIgnored(profileDir);
    assert.strictEqual(result.status, 'ignored',
        `git still does not report the profile as ignored: ${JSON.stringify(result)}`);
    assert.deepStrictEqual(result.added, ['/.chrome-profile/']);
    // Rooted so the rule cannot also match a same-named directory elsewhere in
    // the tree; trailing slash because what is ignored is a directory.
    assert.match(fs.readFileSync(path.join(repo, '.gitignore'), 'utf8'), /^\/\.chrome-profile\/$/m);

    const after = git(repo, ['status', '--porcelain']);
    assert.ok(!/\.chrome-profile/.test(after),
        `the profile is still visible to git: ${after}`);

    // Idempotent: the ignore rules this subsystem writes are appended once, and
    // a second capture must not grow the file again.
    const second = capture.ensureProfileIgnored(profileDir);
    assert.strictEqual(second.status, 'ignored');
    assert.ok(!second.added || second.added.length === 0,
        'a rule already in force must not be appended a second time');

    // Outside a work tree there is nothing to write and nothing to claim.
    const loose = scratch('profile-outside');
    assert.strictEqual(capture.ensureProfileIgnored(loose).status, 'outside-work-tree');
});

test('N11: in CI a missing browser is a FAILURE, never a silent skip', () => {
    // The guard that keeps the two cases below honest, pinned on its own --
    // because if it is wrong, they skip on the runner and every other assertion
    // in this file still passes. That is the #304 shape, and tests/GitleaksGuard.ps1
    // exists because it had already happened once here with gitleaks.
    //
    // Fails CLOSED on anything unrecognised: the failure being prevented is a
    // silent skip, so an odd value must require the browser rather than excuse it.
    for (const value of ['true', 'TRUE', '1', '0', 'yes', 'anything']) {
        assert.strictEqual(integrationRequired(value), true,
            `CI=${value} must require the browser`);
    }
    // Not set, empty, or explicitly disabled -- a developer laptop, where a skip
    // is the correct trade.
    for (const value of [undefined, null, '', '   ']) {
        assert.strictEqual(integrationRequired(value), false,
            `CI=${JSON.stringify(value)} must allow a skip`);
    }
    // 'false' opts out so a developer can leave CI set for other tools. The
    // asymmetry with '0' is deliberate and matches the PowerShell guard rather
    // than inventing a second notion of what CI means.
    for (const value of ['false', 'False', ' false ']) {
        assert.strictEqual(integrationRequired(value), false,
            `CI=${value} must allow a skip`);
    }
});

// ---------------------------------------------------------------------------
// S1-S5 -- which pages an attached capture records (#85)
//
// The rule is decided against stand-ins for Playwright's page and request, so
// the cases a real browser cannot be made to produce on demand -- a popup with
// no user gesture, a frame that has already gone -- are pinned too. I3 below is
// the same rule against a real browser.
// ---------------------------------------------------------------------------

function fakePage(url, opener) {
    const page = {
        current: url,
        url() { return this.current; },
        opener: async () => opener || null
    };
    page.frame = { page: () => page };
    page.mainFrame = () => page.frame;
    return page;
}

function fakeRequest(page, url, opts) {
    const o = opts || {};
    return {
        url: () => url,
        isNavigationRequest: () => !!o.navigation,
        frame() {
            if (!page) throw new Error('Service Worker requests do not have an associated frame.');
            return o.subframe ? { page: () => page } : page.frame;
        },
        serviceWorker: () => (o.worker ? { url: () => o.worker } : null)
    };
}

test('S1: the page this run opened is in scope wherever it goes, cross-site calls included', async () => {
    const scope = capture.createCaptureScope('https://www.example.com/start');
    const mine = fakePage('about:blank');
    scope.claim(mine);
    assert.strictEqual(await scope.admits(fakeRequest(mine, 'https://www.example.com/start',
        { navigation: true })), true);
    // THE TRAFFIC A CAPTURE EXISTS FOR. Filtering requests by their own origin
    // would discard exactly this, which is why the rule is about pages.
    mine.current = 'https://www.example.com/start';
    assert.strictEqual(await scope.admits(fakeRequest(mine, 'https://api.example-cdn.net/v1/things')),
        true, 'a cross-site API call made by the capture page must be recorded');
    assert.strictEqual(await scope.admits(fakeRequest(mine, 'https://frames.other.net/x',
        { subframe: true })), true, 'a cross-site frame inside the capture page is part of it');
    // STICKY: an in-tab sign-in round trip leaves the site and comes back.
    mine.current = 'https://login.identity-provider.net/authorize';
    assert.strictEqual(await scope.admits(fakeRequest(mine, 'https://login.identity-provider.net/token')),
        true, 'the capture tab is still the capture tab while it is on the sign-in host');
});

test('S2: an unrelated tab on another site is out -- until it goes to the target site', async () => {
    const scope = capture.createCaptureScope('https://www.example.com/');
    scope.claim(fakePage('https://www.example.com/'));
    const other = fakePage('https://mail.unrelated.org/inbox');
    assert.strictEqual(await scope.admits(fakeRequest(other, 'https://mail.unrelated.org/poll')),
        false, 'another site\'s tab -- another account\'s data -- must not be recorded');
    // Its own first document request decides it, before page.url() has caught up.
    assert.strictEqual(await scope.admits(fakeRequest(other, 'https://www.example.com/account',
        { navigation: true })), true,
        'a tab the operator points at the target site is part of the capture');
    assert.strictEqual(await scope.admits(fakeRequest(other, 'https://api.example-cdn.net/v1/me')),
        true, 'and from then on, so are its cross-site calls');
    // A navigation request to another site from an unrelated tab stays out.
    const third = fakePage('about:blank');
    assert.strictEqual(await scope.admits(fakeRequest(third, 'https://news.unrelated.org/',
        { navigation: true })), false);
});

test('S3: a window opened from an in-scope page is in scope; one from an unrelated tab is not', async () => {
    const scope = capture.createCaptureScope('https://www.example.com/');
    const mine = fakePage('https://www.example.com/');
    scope.claim(mine);
    const popup = fakePage('https://accounts.identity-provider.net/signin', mine);
    assert.strictEqual(await scope.admits(fakeRequest(popup, 'https://accounts.identity-provider.net/signin',
        { navigation: true })), true, 'the sign-in popup carries the auth exchange');
    const grandchild = fakePage('https://consent.identity-provider.net/', popup);
    assert.strictEqual(await scope.admits(fakeRequest(grandchild, 'https://consent.identity-provider.net/ok')),
        true, 'and so does whatever that popup opens');
    const elsewhere = fakePage('https://mail.unrelated.org/');
    const elsewherePopup = fakePage('https://docs.unrelated.org/', elsewhere);
    assert.strictEqual(await scope.admits(fakeRequest(elsewherePopup, 'https://docs.unrelated.org/a')),
        false, 'a window an unrelated tab opened is as unrelated as that tab');
});

test('S4: the target\'s site is its host without www., plus subdomains -- never a lookalike', async () => {
    const scope = capture.createCaptureScope('https://www.example.com:8443/app');
    const admitsTop = async (url) => scope.admits(fakeRequest(fakePage(url), url, { navigation: true }));
    assert.strictEqual(await admitsTop('https://example.com/'), true);
    assert.strictEqual(await admitsTop('https://www.example.com/'), true);
    assert.strictEqual(await admitsTop('https://accounts.example.com/'), true);
    assert.strictEqual(await admitsTop('http://www.example.com:9999/'), true, 'port is not part of a site');
    assert.strictEqual(await admitsTop('https://notexample.com/'), false);
    assert.strictEqual(await admitsTop('https://example.com.evil.net/'), false);
    assert.strictEqual(await admitsTop('https://example.org/'), false);
    // A target that is an address compares literally.
    const local = capture.createCaptureScope('http://127.0.0.1:5000/');
    const page = (url) => fakeRequest(fakePage(url), url, { navigation: true });
    assert.strictEqual(await local.admits(page('http://127.0.0.1:6000/')), true);
    assert.strictEqual(await local.admits(page('http://localhost:5000/')), false);
    assert.strictEqual(await local.admits(page('http://127.0.0.2:5000/')), false);
});

test('S5: a request whose page cannot be resolved is refused, not recorded by default', async () => {
    const scope = capture.createCaptureScope('https://www.example.com/');
    assert.strictEqual(await scope.admits(fakeRequest(null, 'https://mail.unrelated.org/sync')), false,
        'a service worker with no page and no known origin is not this capture\'s');
    assert.strictEqual(await scope.admits(fakeRequest(null, 'https://mail.unrelated.org/sync',
        { worker: 'https://mail.unrelated.org/sw.js' })), false);
    assert.strictEqual(await scope.admits(fakeRequest(null, 'https://www.example.com/api',
        { worker: 'https://www.example.com/sw.js' })), true,
        'the target site\'s own service worker is part of it');
    const broken = { url: () => 'https://x/', isNavigationRequest: () => false,
        frame: () => ({ page: () => { throw new Error('detached'); } }) };
    assert.strictEqual(await scope.admits(broken), false);
});

test('N12: the operator is told which tabs are recorded, and that the port outlives the run', () => {
    const banner = (session) => capture.startBannerLines(session).map(([, l]) => l).join('\n');
    const attached = banner({
        uri: 'https://www.example.com/', attached: true, browser: 'native',
        ownsRecordHar: false, cdpEndpoint: 'http://127.0.0.1:9222', port: 9222,
        requestedPort: 9222, profileDir: null, harPath: 'r', outputPath: 'o'
    });
    assert.match(attached, /only/i);
    assert.match(attached, /example\.com/, 'the banner names the site whose tabs are recorded');
    assert.match(attached, /other tabs/i);

    const ended = capture.detachedLines({ cdpEndpoint: 'http://127.0.0.1:9222' })
        .map(([, l]) => l).join('\n');
    assert.match(ended, /still running at http:\/\/127\.0\.0\.1:9222/);
    assert.match(ended, /debugging port/i);
    assert.match(ended, /close/i, 'the remedy has to be in the reminder, not only in SKILL.md');
});

// ---------------------------------------------------------------------------
// I1 -- a real browser, a real page, a real detach
// ---------------------------------------------------------------------------

/**
 * Is the integration case runnable here? Asked with the RECORDER's own module
 * resolution, so a machine where the recorder would work is never reported as a
 * machine where the test cannot run.
 */
/**
 * Where the driver is, for the two cases that need one -- and for nobody else.
 *
 * CI CANNOT PUT THIS ON NODE_PATH FOR THE WHOLE JOB, which was tried and is
 * measurably wrong. `require.resolve(name, { paths: [dir] })` still falls back
 * to `Module.globalPaths`, and NODE_PATH is in it; those paths are captured when
 * the process starts, so the `env` object a caller hands the resolver cannot
 * suppress them. capture-preflight.test.js runs its preflight cases IN PROCESS,
 * and ten of them turned from "the module is absent" into "the module is here"
 * -- passing while testing nothing, which is the failure that file exists to
 * prevent.
 *
 * So the runner publishes the path under a name nothing else reads, and it
 * reaches the recorder only through the environment of the child THIS file
 * spawns. Per invocation, which is what NODE_PATH is for and what the recorder
 * documents as its fourth location.
 *
 * Falls back to an inherited NODE_PATH so a developer who already points at an
 * install keeps working without setting a second variable.
 */
function driverNodePath() {
    return process.env.HAR_CAPTURE_TEST_NODE_PATH || process.env.NODE_PATH || '';
}

/** The child's environment, with the driver reachable and nothing else changed. */
function driverEnv() {
    return Object.assign({}, process.env, { NODE_PATH: driverNodePath() });
}

function integrationBlocker(cwd) {
    // Asked with the SAME environment the spawned recorder will get, or the
    // answer would be about this process rather than about the run.
    const resolved = capture.resolvePlaywright({ cwd, env: driverEnv() });
    if (!resolved.found) return 'the playwright module is not installed';
    const chrome = capture.findChromeExecutable();
    if (!chrome.path) {
        return 'Google Chrome is not installed -- looked in:\n' +
            chrome.candidates.map((c) => `      ${c}`).join('\n');
    }
    return null;
}

/**
 * Must a missing browser FAIL rather than skip?
 *
 * The same decision, on the same signal, as Test-GitleaksRequired in
 * tests/GitleaksGuard.ps1 -- and for the same reason it was written. Three
 * gitleaks-backed tests skipped themselves on every platform including CI, so
 * the suite reported green while its only secret-detection assertions had never
 * once executed. These two cases are in that position exactly: they are the only
 * place anything attaches to a real browser, and they are the only place the
 * "stop detaches" guarantee is actually observed.
 *
 * A skip stays correct on a developer laptop -- requiring everyone to install a
 * driver to run the suite is a poor trade. In CI it is not, because CI is the
 * run whose green anyone relies on. So it keys off the standard CI variable
 * rather than an option of our own, and a value it does not recognise fails
 * CLOSED (requires the browser), because the failure being prevented is a
 * silent skip.
 */
function integrationRequired(ciValue) {
    if (ciValue === undefined || ciValue === null) return false;
    const value = String(ciValue).trim();
    if (!value) return false;
    // GitHub Actions sets CI=true. An explicit 'false' opts out so a developer
    // can keep CI set for other tools; CI=0 still counts, matching the
    // PowerShell guard's deliberate asymmetry rather than inventing a second
    // notion of what CI means.
    return value.toLowerCase() !== 'false';
}

/**
 * Skip, or fail, and say which -- never go quiet.
 *
 * Returns true when the caller should return early. Throws in CI.
 */
function blockedOrSkipped(name, blocker) {
    if (!blocker) return false;
    if (integrationRequired(process.env.CI)) {
        throw new Error(
            `${name} cannot run: ${blocker}\n  In CI this is a FAILURE and not a skip -- ` +
            'these are the only cases that attach to a real browser, so a run that skips\n  ' +
            'them is a run that proved nothing about the mode they cover. Install the driver ' +
            'and a browser on the runner (see .github/workflows/web-api-discovery.yml at the repository root), or set CI=false.');
    }
    process.stderr.write(`  SKIP ${name} -- ${blocker}\n`);
    skipped.push(`${name} (${blocker.split('\n')[0]})`);
    return true;
}

test('I1: a native capture records a local page and LEAVES THE BROWSER RUNNING', async () => {
    const repo = makeRepo('native-e2e');
    plantOperatorProfile(repo);

    // A skip that reads as a skip -- and in CI, not a skip at all. A suite that
    // goes quiet when its only end-to-end case cannot run is how a green tick
    // comes to mean nothing.
    if (blockedOrSkipped('I1', integrationBlocker(repo))) return;

    // The page is served locally: this test must not depend on a network, and a
    // real site would make the assertion about somebody else's uptime.
    const hits = [];
    const server = http.createServer((req, res) => {
        hits.push(req.url);
        if (req.url === '/api/widgets') {
            res.writeHead(200, { 'content-type': 'application/json' });
            res.end(JSON.stringify({ widgets: [{ id: 1, name: 'fixture' }] }));
            return;
        }
        res.writeHead(200, { 'content-type': 'text/html' });
        res.end('<html><body>native capture fixture' +
            '<script>fetch("/api/widgets")</script></body></html>');
    });
    await new Promise((r) => server.listen(0, '127.0.0.1', r));
    const uri = `http://127.0.0.1:${server.address().port}/`;

    const profileDir = path.join(repo, '.chrome-profile');
    let recorder = null;
    let browserPid = null;
    let endpoint = null;
    try {
        recorder = spawn(process.execPath, [
            '-r', HEADLESS_PRELOAD, captureJs, 'start',
            '--uri', uri,
            '--describe', 'native mode: load a local fixture page',
            '--browser', 'native',
            '--profile', profileDir,
            // NO --port. A busy default falls forward to the next free one, so
            // choosing a port here would test our own arithmetic instead of the
            // recorder's -- and the endpoint is read back off session.json,
            // which is what an agent is told to do.
            // Flushed every second so the log is on disk before `stop` arrives.
            '--snapshot-seconds', '1',
            // No TTY here, so there is no ENTER to read; `stop` is the ending.
            '--no-wait'
        ], { cwd: repo, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: driverEnv() });
        let recorderErr = '';
        recorder.stderr.on('data', (d) => { recorderErr += d; });
        recorder.stdout.on('data', () => {});

        // Wait for the session file the recorder writes once it has attached.
        const deadline = Date.now() + 90000;
        let sessionDir = null;
        let session = null;
        while (Date.now() < deadline && !session) {
            const found = findSessions(path.join(repo, '.har-captures'));
            if (found.length) {
                sessionDir = found[0];
                const doc = readJson(path.join(sessionDir, 'session.json'));
                if (doc && doc.startedUtc) session = doc;
            }
            if (!session) {
                assert.ok(recorder.exitCode === null,
                    `the recorder exited (${recorder.exitCode}) before it recorded anything:\n` +
                    recorderErr);
                await sleep(250);
            }
        }
        assert.ok(session, `no capture session appeared under ${repo}\n${recorderErr}`);
        endpoint = session.cdpEndpoint;
        browserPid = session.browserPid;

        assert.strictEqual(session.mode, 'native-chrome');
        assert.ok(browserPid, 'the launched browser\'s pid is recorded so this claim is checkable');
        assert.ok(await cdpAnswers(endpoint),
            `nothing answered as a browser at ${endpoint}\n${recorderErr}`);
        await assertHeadless(endpoint, 'I1');

        // Wait for the fixture's own traffic to reach the log. The page load and
        // its fetch are the recording; without them the assertion below would
        // pass on an empty HAR.
        const logPath = path.join(sessionDir, 'raw.ndjson');
        const trafficDeadline = Date.now() + 60000;
        while (Date.now() < trafficDeadline && !hits.includes('/api/widgets')) {
            await sleep(250);
        }
        assert.ok(hits.includes('/'), `the browser never loaded the page: ${JSON.stringify(hits)}`);

        // END IT THE WAY AN AGENT DOES -- a separate `stop` process, which is the
        // path that has to leave the browser alone.
        const stopped = runCapture(repo, ['stop', '--session', sessionDir], { env: driverEnv() });
        assert.notStrictEqual(stopped.status, 5,
            'exit 5 says recordHar lost, which cannot be true of a browser Playwright ' +
            `never launched:\n${stopped.stderr}`);

        await waitFor(() => recorder.exitCode !== null, 60000);

        // THE ARTIFACT.
        const harPath = path.join(sessionDir, 'raw.har');
        assert.ok(fs.existsSync(harPath), `no raw.har at ${harPath}\n${recorderErr}`);
        const bytes = fs.statSync(harPath).size;
        assert.ok(bytes > 0, 'raw.har is empty -- the 0-byte DevTools export, reproduced');
        const har = readJson(harPath);
        assert.strictEqual(har.log.version, '1.2', 'raw.har must be a genuine HAR 1.2 document');
        assert.ok(har.log.entries.length > 0, 'raw.har has no entries');
        assert.ok(har.log.entries.some((e) => e.request.url.includes('/api/widgets')),
            'the XHR the fixture page made is missing -- attaching over CDP has to see ' +
            'everything the context does, not just top-level navigations');
        assert.ok(fs.existsSync(logPath),
            'raw.har in this mode is assembled from the incremental log, which is kept');

        // THE FALSIFIER. `browser.close()` on a CDP connection must DISCONNECT,
        // and this is the assertion that would fail if it ever closed Chrome
        // instead -- taking the operator's window and every signed-in tab in it
        // down as the price of ending a recording.
        assert.ok(await cdpAnswers(endpoint),
            `the browser at ${endpoint} was closed by stopping the recording. ` +
            '`stop` must DETACH: the operator may have a dozen signed-in tabs in ' +
            'that window, and nothing in the recorder closes what it did not open.');
        assert.ok(pidAlive(browserPid),
            `the browser process ${browserPid} is gone after stopping the recording`);

        const finalSession = readJson(path.join(sessionDir, 'session.json'));
        assert.strictEqual(finalSession.assembledFromLog, true,
            'there is no recordHar in this mode, so the log is the only source');
        assert.strictEqual(finalSession.endedBy, 'stop-command');
    } finally {
        server.close();
        // The test cleans up what the recorder deliberately does not. Killing a
        // browser is the operator's business, and the recorder never does it --
        // which is precisely why this teardown is here and not there.
        if (browserPid) killTree(browserPid);
        if (recorder && recorder.exitCode === null) recorder.kill();
    }
});

test('I2: attaching records in the operator\'s browser and closes none of its tabs', async () => {
    const repo = makeRepo('attach-e2e');
    plantOperatorProfile(repo);

    if (blockedOrSkipped('I2', integrationBlocker(repo))) return;

    const hits = [];
    const server = http.createServer((req, res) => {
        hits.push(req.url);
        res.writeHead(200, { 'content-type': 'text/html' });
        res.end('<html><body>attached fixture</body></html>');
    });
    await new Promise((r) => server.listen(0, '127.0.0.1', r));
    const uri = `http://127.0.0.1:${server.address().port}/`;

    // THE OPERATOR'S BROWSER, started the way the help tells them to -- through
    // the recorder's own launcher, because the test is playing the person who
    // already has a browser open, not reimplementing how one is started.
    const profileDir = path.join(repo, '.chrome-profile');
    fs.mkdirSync(profileDir, { recursive: true });
    const port = await capture.findFreePort(9500);
    const endpoint = `http://127.0.0.1:${port}`;
    const { child } = capture.launchNativeChrome({
        executable: capture.findChromeExecutable().path,
        profileDir,
        port,
        spawn: headlessSpawn
    });
    let recorder = null;
    try {
        let gone = false;
        child.once('exit', () => { gone = true; });
        child.once('error', () => { gone = true; });
        const version = await capture.waitForCdp(endpoint, 60000, { stillStarting: () => !gone });
        assert.ok(version, `the fixture browser never opened ${endpoint}`);
        await assertHeadless(endpoint, 'I2');

        // What the operator already had open. Every one of these has to be there
        // afterwards.
        const before = await pageTargets(endpoint);
        assert.ok(before.length > 0, 'fixture: the browser must have at least one tab');

        recorder = spawn(process.execPath, [
            captureJs, 'start',
            '--uri', uri,
            '--describe', 'attached mode: record in a browser already running',
            '--attach', String(port),
            '--snapshot-seconds', '1',
            '--no-wait'
        ], { cwd: repo, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: driverEnv() });
        let recorderErr = '';
        recorder.stderr.on('data', (d) => { recorderErr += d; });
        recorder.stdout.on('data', () => {});

        const deadline = Date.now() + 90000;
        let sessionDir = null;
        let session = null;
        while (Date.now() < deadline && !session) {
            const found = findSessions(path.join(repo, '.har-captures'));
            if (found.length) {
                sessionDir = found[0];
                const doc = readJson(path.join(sessionDir, 'session.json'));
                if (doc && doc.startedUtc) session = doc;
            }
            if (!session) {
                assert.ok(recorder.exitCode === null,
                    `the recorder exited (${recorder.exitCode}) before recording:\n${recorderErr}`);
                await sleep(250);
            }
        }
        assert.ok(session, `no capture session appeared\n${recorderErr}`);
        assert.strictEqual(session.mode, 'attached-chrome');
        assert.strictEqual(session.cdpEndpoint, endpoint);
        assert.strictEqual(session.profileDir, null);
        assert.ok(!session.browserPid,
            'this run started no browser, so it has no browser pid to record');

        await waitFor(() => hits.includes('/'), 60000);
        assert.ok(hits.includes('/'), `the browser never loaded the page: ${JSON.stringify(hits)}`);

        const stopped = runCapture(repo, ['stop', '--session', sessionDir], { env: driverEnv() });
        assert.notStrictEqual(stopped.status, 5, stopped.stderr);
        await waitFor(() => recorder.exitCode !== null, 60000);

        const har = readJson(path.join(sessionDir, 'raw.har'));
        assert.ok(har && har.log.entries.length > 0,
            `nothing was recorded in the attached browser\n${recorderErr}`);
        assert.ok(har.log.entries.some((e) => e.request.url === uri),
            'the page the recording was started for is missing from the HAR');

        // THE GUARANTEE. Not "the browser is up" -- that is I1's assertion -- but
        // that not one of the operator's own tabs was taken with the recording.
        // `stop` detaches, so a tab that existed before must still exist after,
        // by id.
        assert.ok(await cdpAnswers(endpoint),
            'the operator\'s browser was closed by stopping the recording');
        const after = await pageTargets(endpoint);
        const lost = before.filter((t) => !after.some((a) => a.id === t.id));
        assert.deepStrictEqual(lost, [],
            'stopping the recording closed tabs the operator already had open: ' +
            JSON.stringify(lost.map((t) => t.url)));
    } finally {
        server.close();
        if (recorder && recorder.exitCode === null) recorder.kill();
        if (child.pid) killTree(child.pid);
    }
});

test('I3: attached, an unrelated tab on another site is NOT recorded; the capture page\'s API call is', async () => {
    const repo = makeRepo('attach-scope-e2e');
    plantOperatorProfile(repo);

    if (blockedOrSkipped('I3', integrationBlocker(repo))) return;

    // TWO SITES, told apart by HOSTNAME -- a site ignores the port, so two
    // servers on 127.0.0.1 would be one site. The capture's target is
    // 127.0.0.1; the other server is reached as `localhost`, which is both the
    // unrelated tab's site AND the host the capture page calls its API on --
    // so the assertions below tell the two apart by PATH, and "recorded" can
    // only mean "recorded because of the page that made it".
    const targetHits = [];
    const target = http.createServer((req, res) => {
        targetHits.push(req.url);
        res.writeHead(200, { 'content-type': 'text/html' });
        // The sign-in link opens a NEW WINDOW on another site with
        // rel=noopener -- the shape a popup login takes, and the one where
        // the window cannot reach back to its opener.
        res.end('<html><body>capture target' +
            `<a id="signin" target="_blank" rel="noopener" href="http://localhost:${otherPort()}/signin">sign in</a>` +
            `<script>fetch("http://localhost:${otherPort()}/api/widgets")</script></body></html>`);
    });
    const otherHits = [];
    const other = http.createServer((req, res) => {
        otherHits.push({ url: req.url, at: Date.now() });
        if (req.url.startsWith('/api/widgets')) {
            res.writeHead(200, {
                'content-type': 'application/json', 'access-control-allow-origin': '*'
            });
            res.end(JSON.stringify({ widgets: [{ id: 1 }] }));
            return;
        }
        if (req.url.startsWith('/other/poll')) {
            res.writeHead(200, { 'content-type': 'application/json' });
            res.end(JSON.stringify({ unread: 3, account: 'someone-else@unrelated.example' }));
            return;
        }
        if (req.url.startsWith('/signin')) {
            // The popup's own page: no polling, so anything under /other/ in
            // the HAR can only have come from the unrelated tab.
            res.writeHead(200, { 'content-type': 'text/html' });
            res.end('<html><body>sign-in window</body></html>');
            return;
        }
        // Another signed-in site, polling in the background -- the tab nobody
        // touched that #85 found in raw.har.
        res.writeHead(200, { 'content-type': 'text/html' });
        res.end('<html><body>unrelated inbox<script>' +
            'setInterval(() => fetch("/other/poll?t=" + Date.now()), 300)' +
            '</script></body></html>');
    });
    await new Promise((r) => target.listen(0, '127.0.0.1', r));
    await new Promise((r) => other.listen(0, '127.0.0.1', r));
    function otherPort() { return other.address().port; }
    const uri = `http://127.0.0.1:${target.address().port}/`;
    const otherUrl = `http://localhost:${otherPort()}/inbox`;

    const profileDir = path.join(repo, '.chrome-profile');
    fs.mkdirSync(profileDir, { recursive: true });
    const port = await capture.findFreePort(9600);
    const endpoint = `http://127.0.0.1:${port}`;
    // Headless through the launcher's spawn seam -- see headlessSpawn (#88).
    const { child } = capture.launchNativeChrome({
        executable: capture.findChromeExecutable().path, profileDir, port,
        spawn: headlessSpawn
    });
    let recorder = null;
    try {
        let gone = false;
        child.once('exit', () => { gone = true; });
        child.once('error', () => { gone = true; });
        assert.ok(await capture.waitForCdp(endpoint, 60000, { stillStarting: () => !gone }),
            `the fixture browser never opened ${endpoint}`);
        await assertHeadless(endpoint, 'I3');

        // The operator's other tab, open BEFORE the recording starts, opened
        // through Chrome's own endpoint -- no automation in this process.
        const opened = await fetch(`${endpoint}/json/new?${otherUrl}`, { method: 'PUT' });
        assert.ok(opened.ok, `could not open the unrelated tab: ${opened.status}`);
        assert.ok(await waitFor(() => otherHits.some((h) => h.url.startsWith('/other/poll')), 30000),
            'fixture: the unrelated tab never started polling');

        recorder = spawn(process.execPath, [
            captureJs, 'start',
            '--uri', uri,
            '--describe', 'attached mode: other tabs are not this capture',
            '--attach', String(port),
            '--snapshot-seconds', '1',
            '--no-wait'
        ], { cwd: repo, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: driverEnv() });
        let recorderErr = '';
        recorder.stderr.on('data', (d) => { recorderErr += d; });
        recorder.stdout.on('data', () => {});

        const deadline = Date.now() + 90000;
        let sessionDir = null;
        let session = null;
        while (Date.now() < deadline && !session) {
            const found = findSessions(path.join(repo, '.har-captures'));
            if (found.length) {
                sessionDir = found[0];
                const doc = readJson(path.join(sessionDir, 'session.json'));
                if (doc && doc.startedUtc) session = doc;
            }
            if (!session) {
                assert.ok(recorder.exitCode === null,
                    `the recorder exited (${recorder.exitCode}) before recording:\n${recorderErr}`);
                await sleep(250);
            }
        }
        assert.ok(session, `no capture session appeared\n${recorderErr}`);

        // The capture page's own API call, and SEVERAL polls from the other tab
        // made while the recording was live -- otherwise "absent" would be true
        // of a recorder that simply started late.
        assert.ok(await waitFor(() => otherHits.some((h) => h.url.startsWith('/api/widgets')), 60000),
            `the capture page never called its API\n${recorderErr}`);
        const startedAt = Date.parse(session.startedUtc);
        assert.ok(await waitFor(() => otherHits.filter((h) =>
            h.url.startsWith('/other/poll') && h.at > startedAt).length >= 5, 30000),
            'fixture: the unrelated tab stopped polling during the recording');

        // A SIGN-IN WINDOW, opened by a real click in the capture's tab -- the
        // way an agent drives a recording, over its own CDP connection. It is
        // on the unrelated tab's site, so only "opened from an in-scope page"
        // can admit it.
        const driver = require(capture.resolvePlaywright({ cwd: repo, env: driverEnv() }).path);
        const agent = await driver.chromium.connectOverCDP(endpoint);
        try {
            const capturePage = agent.contexts()[0].pages().find((p) => p.url() === uri);
            assert.ok(capturePage, 'fixture: the recording tab is not open');
            await capturePage.bringToFront();
            await capturePage.click('#signin', { noWaitAfter: true, timeout: 15000 });
            assert.ok(await waitFor(() => otherHits.some((h) => h.url.startsWith('/signin')), 30000),
                'fixture: the sign-in window never loaded');
        } finally {
            // Detach, never close: this is the operator's browser in the story.
            await agent.close().catch(() => {});
        }
        // Two flushes, so whatever the recorder accepted is on disk.
        await sleep(2500);

        const stopped = runCapture(repo, ['stop', '--session', sessionDir], { env: driverEnv() });
        assert.notStrictEqual(stopped.status, 5, stopped.stderr);
        await waitFor(() => recorder.exitCode !== null, 60000);

        const har = readJson(path.join(sessionDir, 'raw.har'));
        assert.ok(har && har.log.entries.length > 0, `nothing was recorded\n${recorderErr}`);
        const urls = har.log.entries.map((e) => e.request.url);
        assert.ok(urls.includes(uri), 'the capture page itself is missing from the HAR');
        assert.ok(urls.some((u) => u.includes('/api/widgets')),
            'the capture page\'s CROSS-SITE API call is missing -- scoping by page must keep ' +
            'every request the capture page makes, whatever host it goes to');
        assert.ok(urls.some((u) => u.includes('/signin')),
            'the sign-in window the capture page opened is missing -- a window opened from ' +
            'an in-scope page carries the auth exchange, whatever host it is on');
        const leaked = urls.filter((u) => u.includes('/other/') || u.includes('/inbox'));
        assert.deepStrictEqual(leaked, [],
            'another tab\'s traffic -- another site, another account -- was recorded into ' +
            'this capture (#85)');
    } finally {
        target.close();
        other.close();
        if (recorder && recorder.exitCode === null) recorder.kill();
        if (child.pid) killTree(child.pid);
    }
});

// ---------------------------------------------------------------------------
// helpers used by I1, I2 and I3
// ---------------------------------------------------------------------------

/** The browser's own list of open tabs, by id. */
async function pageTargets(endpoint) {
    try {
        const res = await fetch(`${endpoint}/json/list`);
        if (!res.ok) return [];
        const all = await res.json();
        return all.filter((t) => t.type === 'page').map((t) => ({ id: t.id, url: t.url }));
    } catch (e) { void e; return []; }
}

function readJson(file) {
    try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { void e; return null; }
}

/** `<root>/<host>/<stamp>/` -- the layout a live recorder writes. */
function findSessions(root) {
    const out = [];
    let hosts = [];
    try { hosts = fs.readdirSync(root); } catch (e) { void e; return out; }
    for (const host of hosts) {
        const hostDir = path.join(root, host);
        let stamps = [];
        try { stamps = fs.readdirSync(hostDir); } catch (e) { void e; continue; }
        for (const s of stamps) {
            if (fs.existsSync(path.join(hostDir, s, 'session.json'))) {
                out.push(path.join(hostDir, s));
            }
        }
    }
    return out.sort();
}

async function waitFor(predicate, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (predicate()) return true;
        await sleep(200);
    }
    return false;
}

function pidAlive(pid) {
    try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}

function killTree(pid) {
    try {
        if (process.platform === 'win32') {
            spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'],
                { stdio: 'ignore', windowsHide: true });
        } else {
            process.kill(-pid, 'SIGKILL');
        }
    } catch (e) { void e; }
    try { process.kill(pid, 'SIGKILL'); } catch (e) { void e; }
}

// ---------------------------------------------------------------------------

// Every case runs even after one fails, on the durability suite's reasoning:
// ablation has to show WHICH assertions notice a broken line, and a fail-fast
// runner answers "at least one" every time.
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
    try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (e) { void e; }
    if (failures.length) {
        process.stderr.write(
            `\n${failures.length} of ${queued.length} capture-native-attach tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    // The skip is named in the success line, so a run that could not exercise
    // the browser cannot be mistaken for one that did.
    console.log(`All capture-native-attach tests passed (${passed})` +
        (skipped.length ? ` -- SKIPPED: ${skipped.join(', ')}` : ''));
})();
