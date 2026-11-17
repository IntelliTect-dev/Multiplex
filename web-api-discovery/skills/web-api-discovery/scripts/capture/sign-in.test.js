#!/usr/bin/env node
// Behavior tests for the recorder signing in from Bitwarden or a masked prompt
// (issue #115).
//
// THE PROPERTY UNDER TEST ABOVE ALL OTHERS: no credential value reaches
// anything but the page and the scrub. Every case that resolves credentials
// collects everything said to the operator, and every argv handed to `bw`, and
// asserts the planted values appear in neither. A sign-in that works but
// prints the password once is a failure, not a partial pass.
//
// Bitwarden is a fake `run` -- no vault is ever touched and `bw` need not be
// installed. The browser cases drive a real Playwright Chromium against a
// local sign-in page with test credentials; they SKIP loudly when Playwright
// is absent, and in CI a skip is a failure (the same rule as
// capture-native-attach.test.js).
//
//   B1  hosts: same host and subdomains match; look-alikes and parents do not
//   B2  bw: master password and session key travel by child env, never argv
//   B3  bw: items whose saved URIs do not match the host are dropped
//   B4  bw: an invalid host never reaches a command line
//   R1  unlocked vault, one match -> bitwarden, nothing prompted
//   R2  several matches -> a numbered pick, by item name
//   R3  no match -> masked prompt
//   R4  bw missing -> install hint, then masked prompt
//   R5  locked vault + terminal -> masked master password, unlock, item
//   R6  no terminal -> none, and bw is never run (even unlocked)
//   R7  bw logged out -> prompt
//   R8  blank password at the prompt -> none (the human signs in)
//   R9  a terminal with no reader (--no-wait) -> none, and bw is never run
//   B5  bw is found only in absolute PATH entries, never the current directory
//   T1  the shared terminal: a waiting prompt takes the line; secrets are not echoed
//   S3  no new cookie since the fill -> the saved session is left alone
//   S4  where the temporary file would not be ignored, nothing is written beside it
//   S5  a rotated session cookie (same name, new value) counts as a new session
//   S1  storage state merge keeps other sites and replaces this one
//   H1  the fills are paced, and each wait comes before the check that vets the step
//   H2  a page that moved during a wait is not typed into
//   H3  no pacer, no wait
//   H4  the real pacer waits within the action range, and not at all with --pace off
//   P1  password form: filled, submitted, onSignedIn fires, secrets() reported
//   P2  identifier-first: username, then password on the next page
//   P3  a form on another host is never filled
//   P4  a refused sign-in is not retried
//   P5  no credentials -> nothing filled, the human is told
//   P6  no terminal, first page -> nothing filled, onUnattended fires (sign-in-needed)
//   P7  a newsletter email box and a sign-up form are not sign-ins
//   P8  a page that changes host while the prompt is open is not filled
//   P9  no terminal, mid-run -> a notice; the recording is not ended
//   P10 a login wall drawn by script after load still counts as the first page

'use strict';

const assert = require('assert');
const http = require('http');
const path = require('path');

const signIn = require(path.join(__dirname, 'sign-in.js'));
const capture = require(path.join(__dirname, 'capture-har.js'));

const USER = 'operator@example.test';
const PASSWORD = 'correct-horse-battery-staple-115';
const MASTER = 'master-password-never-printed-115';
const SESSION_KEY = 'bw-session-key-never-printed-115';
const PLANTED = [USER, PASSWORD, MASTER, SESSION_KEY];

const failures = [];
const skipped = [];
// Named when a step takes long, so a slow suite says WHERE rather than just
// being slow -- and so a creeping timeout is visible before it is a failure.
function slow(name, started) {
    const ms = Date.now() - started;
    if (ms > 5000) console.log(`slow: ${name} took ${(ms / 1000).toFixed(1)}s`);
}
const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

function item(id, name, uris, extra = {}) {
    return {
        id, name, type: 1,
        login: Object.assign({ username: USER, password: PASSWORD, uris: uris.map((uri) => ({ uri })) }, extra)
    };
}

/**
 * A stand-in `bw`. `state` is the vault's status; `items` what `list items`
 * returns. Every call is recorded with its argv and env so leaks can be found.
 */
function fakeBw(opts = {}) {
    const calls = [];
    let state = opts.state || 'unlocked';
    const run = (args, o) => {
        calls.push({ args, env: o && o.env });
        if (opts.missing) return { status: null, stdout: '', stderr: '', error: new Error('ENOENT') };
        switch (args[0]) {
            case '--version': return { status: 0, stdout: '2026.1.0\n' };
            case 'status': return { status: 0, stdout: JSON.stringify({ status: state }) };
            case 'unlock':
                if (o.env[signIn.MASTER_ENV] === MASTER) {
                    state = 'unlocked';
                    return { status: 0, stdout: SESSION_KEY };
                }
                return { status: 1, stdout: '', stderr: 'Invalid master password.' };
            case 'list': return { status: 0, stdout: JSON.stringify(opts.items || []) };
            case 'get': return { status: 0, stdout: '123456\n' };
            default: return { status: 1, stdout: '' };
        }
    };
    return { calls, bw: signIn.createBitwarden({ run, env: { PATH: 'x' } }) };
}

/** A scripted terminal. `answers` are consumed in order by ask and askSecret alike. */
function fakeIo(answers = [], isTty = true) {
    const said = [];
    const asked = [];
    const next = (q, secret) => { asked.push({ q, secret }); return Promise.resolve(answers.shift()); };
    return {
        said, asked, isTty,
        say: (lines) => said.push(...lines),
        ask: (q) => next(q, false),
        askSecret: (q) => next(q, true)
    };
}

function assertNoLeak(where, text) {
    for (const v of PLANTED) {
        assert.ok(!String(text).includes(v), `${where} contains a planted credential value`);
    }
}

function assertNoLeakInCalls(calls) {
    for (const c of calls) assertNoLeak(`bw argv ${JSON.stringify(c.args[0])}`, c.args.join(' '));
}

// ---------------------------------------------------------------------------

test('B1: hosts -- same host and subdomains match; look-alikes and parents do not', () => {
    assert.ok(signIn.hostMatches('example.com', 'example.com'));
    assert.ok(signIn.hostMatches('example.com', 'login.example.com'));
    assert.ok(!signIn.hostMatches('example.com', 'badexample.com'), 'a suffix that is not a label boundary');
    assert.ok(!signIn.hostMatches('login.example.com', 'example.com'), 'an item for a subdomain is not for its parent');
    assert.ok(!signIn.hostMatches('example.com', 'example.com.evil.test'));
    assert.strictEqual(signIn.hostOf('example.com/login'), 'example.com', 'a scheme-less Bitwarden URI');
});

test('B2: the master password and session key travel by child env, never argv', () => {
    const { calls, bw } = fakeBw({ state: 'locked' });
    assert.ok(bw.unlock(MASTER));
    bw.itemsForHost('example.com');
    assertNoLeakInCalls(calls);
    const unlock = calls.find((c) => c.args[0] === 'unlock');
    assert.strictEqual(unlock.env[signIn.MASTER_ENV], MASTER);
    const list = calls.find((c) => c.args[0] === 'list');
    assert.strictEqual(list.env.BW_SESSION, SESSION_KEY, 'later calls carry the session key');
    assert.ok(!(signIn.MASTER_ENV in list.env), 'the master password is not handed to every later child');
    assert.ok(!(signIn.MASTER_ENV in process.env) && process.env.BW_SESSION !== SESSION_KEY,
        'nothing is put in this process\'s own environment');
});

test('B3: items whose saved URIs do not match the host are dropped', () => {
    const { bw } = fakeBw({
        items: [
            item('a1', 'right', ['https://example.com']),
            item('a2', 'look-alike', ['https://badexample.com']),
            { id: 'a3', name: 'a note', type: 2 }
        ]
    });
    assert.deepStrictEqual(bw.itemsForHost('login.example.com').map((i) => i.name), ['right']);
});

test('B4: an invalid host never reaches a command line', () => {
    const { calls, bw } = fakeBw();
    assert.deepStrictEqual(bw.itemsForHost('example.com & calc'), []);
    assert.ok(!calls.some((c) => c.args[0] === 'list'));
    assert.strictEqual(bw.totp('x; rm -rf /'), null);
});

test('R1: unlocked vault with one match signs in from Bitwarden without prompting', async () => {
    const { calls, bw } = fakeBw({ items: [item('a1', 'Example', ['https://example.com'])] });
    const io = fakeIo();
    const c = await signIn.resolveCredentials('example.com', bw, io);
    assert.strictEqual(c.source, 'bitwarden');
    assert.strictEqual(c.password, PASSWORD);
    assert.strictEqual(io.asked.length, 0);
    assertNoLeak('operator output', io.said.join('\n'));
    assertNoLeakInCalls(calls);
});

test('R2: several matches are a numbered pick by item name', async () => {
    const { bw } = fakeBw({
        items: [
            item('a1', 'Work account', ['https://example.com']),
            item('a2', 'Personal account', ['https://example.com'], { username: 'second@example.test' })
        ]
    });
    const io = fakeIo(['2']);
    const c = await signIn.resolveCredentials('example.com', bw, io);
    assert.strictEqual(c.username, 'second@example.test');
    assert.ok(io.said.some((l) => l.includes('2. Personal account')));
    assert.strictEqual(io.asked[0].secret, false);
    assertNoLeak('operator output', io.said.join('\n'));
});

test('R3: no matching item falls through to the masked prompt', async () => {
    const { bw } = fakeBw({ items: [] });
    const io = fakeIo([USER, PASSWORD]);
    const c = await signIn.resolveCredentials('example.com', bw, io);
    assert.strictEqual(c.source, 'prompt');
    assert.deepStrictEqual(io.asked.map((a) => a.secret), [false, true], 'username shown, password masked');
    assertNoLeak('operator output', io.said.join('\n'));
});

test('R4: bw missing prints the install command, then prompts', async () => {
    const { bw } = fakeBw({ missing: true });
    const io = fakeIo([USER, PASSWORD]);
    const c = await signIn.resolveCredentials('example.com', bw, io);
    assert.strictEqual(c.source, 'prompt');
    assert.ok(io.said.some((l) => /install/.test(l) && /bw|Bitwarden/i.test(l)), 'the install command is shown');
    assertNoLeak('operator output', io.said.join('\n'));
});

test('R5: a locked vault asks for the master password, masked, and unlocks', async () => {
    const { calls, bw } = fakeBw({ state: 'locked', items: [item('a1', 'Example', ['example.com'])] });
    const io = fakeIo([MASTER]);
    const c = await signIn.resolveCredentials('example.com', bw, io);
    assert.strictEqual(c.source, 'bitwarden');
    assert.strictEqual(io.asked[0].secret, true);
    assertNoLeak('operator output', io.said.join('\n'));
    assertNoLeakInCalls(calls);
});

test('R6: with nobody at the terminal the vault is never read, even unlocked', async () => {
    const { calls, bw } = fakeBw({ state: 'unlocked', items: [item('a1', 'Example', ['example.com'])] });
    const io = fakeIo([], false);
    const c = await signIn.resolveCredentials('example.com', bw, io);
    assert.strictEqual(c.source, 'none');
    assert.strictEqual(c.code, 'no-terminal');
    assert.strictEqual(io.asked.length, 0);
    assert.strictEqual(calls.length, 0, 'bw was not run at all');
});

test('R7: bw installed but logged out prompts instead', async () => {
    const { bw } = fakeBw({ state: 'unauthenticated' });
    const io = fakeIo([USER, PASSWORD]);
    const c = await signIn.resolveCredentials('example.com', bw, io);
    assert.strictEqual(c.source, 'prompt');
    assert.ok(io.said.some((l) => l.includes('bw login')));
});

test('R8: a blank password at the prompt leaves the sign-in to the human', async () => {
    const { bw } = fakeBw({ missing: true });
    const c = await signIn.resolveCredentials('example.com', bw, fakeIo([USER, '']));
    assert.strictEqual(c.source, 'none');
});

test('R9: a terminal nobody is reading (--no-wait) never reads the vault', async () => {
    const { calls, bw } = fakeBw({ state: 'unlocked', items: [item('a1', 'Example', ['example.com'])] });
    const io = Object.assign(fakeIo([], true), { canPrompt: false });
    const c = await signIn.resolveCredentials('example.com', bw, io);
    assert.strictEqual(c.source, 'none');
    assert.strictEqual(c.code, 'no-prompt', 'not "no-terminal": a person may be there, so the run is not ended');
    assert.strictEqual(calls.length, 0, 'bw was not run at all');
});

test('B5: bw is found only in absolute PATH entries, never the current directory', () => {
    const fs = require('fs');
    const os = require('os');
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'findbw-'));
    const planted = path.join(root, 'project');
    const real = path.join(root, 'tools');
    fs.mkdirSync(planted);
    fs.mkdirSync(real);
    const name = process.platform === 'win32' ? 'bw.cmd' : 'bw';
    fs.writeFileSync(path.join(planted, name), 'planted');
    const sep = process.platform === 'win32' ? ';' : ':';
    const cwd = process.cwd();
    process.chdir(planted);
    try {
        assert.strictEqual(signIn.findBw({ PATH: ['.', 'project', ''].join(sep) }), null,
            'a relative or empty PATH entry reached the planted bw');
        fs.writeFileSync(path.join(real, name), 'real');
        assert.strictEqual(signIn.findBw({ PATH: ['.', real].join(sep) }), path.join(real, name));
    } finally {
        process.chdir(cwd);
        fs.rmSync(root, { recursive: true, force: true });
    }
});

test('S1: storage state merge keeps other sites and replaces this one', () => {
    const existing = {
        cookies: [{ name: 'old', domain: '.example.com' }, { name: 'other', domain: 'other.test' }],
        origins: [{ origin: 'https://other.test', localStorage: [] }]
    };
    const fresh = {
        cookies: [{ name: 'new', domain: 'login.example.com' }, { name: 'tracker', domain: 'ads.test' }],
        origins: [{ origin: 'https://www.example.com', localStorage: [] }, { origin: 'https://ads.test', localStorage: [] }]
    };
    const merged = signIn.mergeStorageState(existing, fresh, ['www.example.com', 'login.example.com']);
    assert.deepStrictEqual(merged.cookies.map((c) => c.name).sort(), ['new', 'other'],
        'the parent-domain cookie is replaced, the sign-in host cookie kept, the tracker dropped');
    assert.deepStrictEqual(merged.origins.map((o) => o.origin).sort(), ['https://other.test', 'https://www.example.com']);

    // Two labels of shop.example.co.uk is co.uk -- which would take in every
    // UK site in the shared profile.
    const uk = signIn.mergeStorageState(null, {
        cookies: [{ name: 'mine', domain: 'shop.example.co.uk' }, { name: 'theirs', domain: '.another.co.uk' }],
        origins: []
    }, ['shop.example.co.uk']);
    assert.deepStrictEqual(uk.cookies.map((c) => c.name), ['mine']);
});

test('S2: the session is saved only where git will not pick it up, and never holds the password', async () => {
    const fs = require('fs');
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sign-in-'));
    const file = path.join(dir, '.har-storage-state.json');
    const context = { storageState: async () => ({ cookies: [{ name: 'sid', value: 'abc', domain: 'example.com' }], origins: [] }) };

    const refused = await signIn.saveSession(context, file, ['example.com'], () => 'not-ignored');
    assert.deepStrictEqual(refused, { saved: false, status: 'not-ignored' });
    assert.ok(!fs.existsSync(file), 'nothing written into a committable place');

    const ok = await signIn.saveSession(context, file, ['example.com'], () => 'ignored');
    assert.strictEqual(ok.saved, true);
    const text = fs.readFileSync(file, 'utf8');
    assert.ok(text.includes('"sid"'));
    assertNoLeak('saved session', text);

    // Nothing new for this site: the file is left alone, so a sign-in that
    // set no cookie cannot replace a good session with an anonymous one.
    const again = await signIn.saveSession(context, file, ['example.com'], () => 'ignored');
    assert.deepStrictEqual(again, { saved: false, status: 'unchanged' });
    assert.deepStrictEqual(fs.readdirSync(dir), ['.har-storage-state.json'], 'no temporary file left behind');
    fs.rmSync(dir, { recursive: true, force: true });
});

test('T1: the shared terminal routes a line to a waiting prompt first, and never echoes a secret', async () => {
    const { EventEmitter } = require('events');
    const echoed = [];
    const rl = Object.assign(new EventEmitter(), { terminal: true, _writeToOutput: (s) => echoed.push(s) });
    const t = capture.createTerminal(rl);
    assert.strictEqual(t.take('stray'), false, 'with no prompt waiting, a line is ENTER');

    const stderr = process.stderr.write;
    process.stderr.write = () => true;
    try {
        const answer = t.askSecret('Password: ');
        rl._writeToOutput(PASSWORD);              // what readline does as keys arrive
        assert.strictEqual(t.take(PASSWORD), true, 'the waiting prompt took the line');
        assert.strictEqual(await answer, PASSWORD);
        rl._writeToOutput('after');
    } finally { process.stderr.write = stderr; }
    assert.deepStrictEqual(echoed, ['after'], 'the secret was not echoed; output resumes afterwards');

    const plain = capture.createTerminal(Object.assign(new EventEmitter(), { terminal: false, _writeToOutput: () => {} }));
    assert.strictEqual(await plain.askSecret('Password: '), null, 'a terminal that cannot mask is not asked');
});

test('S3: with no new cookie since the password went in, the saved session is left alone', async () => {
    const fs = require('fs');
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sign-in-'));
    const file = path.join(dir, '.har-storage-state.json');
    fs.writeFileSync(file, JSON.stringify({ cookies: [{ name: 'sid', value: 'good', domain: 'example.com', path: '/' }], origins: [] }));
    // A consent page: the cookie it already had, unchanged; nothing new.
    const context = { storageState: async () => ({ cookies: [{ name: 'trk', value: 'v1', domain: 'example.com', path: '/' }], origins: [] }) };
    const before = new Map([['example.com|/|trk', 'v1']]);
    const r = await signIn.saveSession(context, file, ['example.com'], () => 'ignored', { cookiesBefore: before });
    assert.deepStrictEqual(r, { saved: false, status: 'no-new-session' });
    assert.ok(fs.readFileSync(file, 'utf8').includes('"good"'), 'the working session was replaced');
    fs.rmSync(dir, { recursive: true, force: true });
});

test('S5: a session cookie the sign-in ROTATED (same name, new value) is a new session', async () => {
    const fs = require('fs');
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sign-in-'));
    const file = path.join(dir, '.har-storage-state.json');
    const context = { storageState: async () => ({ cookies: [{ name: 'PHPSESSID', value: 'authenticated', domain: 'example.com', path: '/' }], origins: [] }) };
    const before = new Map([['example.com|/|PHPSESSID', 'anonymous']]);
    const r = await signIn.saveSession(context, file, ['example.com'], () => 'ignored', { cookiesBefore: before });
    assert.strictEqual(r.saved, true, 'the most common real sign-in was not saved');
    fs.rmSync(dir, { recursive: true, force: true });
});

test('S4: where the temporary file would not be ignored, the session is written directly', async () => {
    const fs = require('fs');
    const os = require('os');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sign-in-'));
    const file = path.join(dir, '.har-storage-state.json');
    const context = { storageState: async () => ({ cookies: [{ name: 'sid', value: 'x', domain: 'example.com', path: '/' }], origins: [] }) };
    // Only the exact name is ignored -- the shape of a typical .gitignore entry.
    const classify = (p) => (p === file ? 'ignored' : 'not-ignored');
    const r = await signIn.saveSession(context, file, ['example.com'], classify);
    assert.strictEqual(r.saved, true);
    assert.deepStrictEqual(fs.readdirSync(dir), ['.har-storage-state.json'], 'a cookie-bearing file outside the ignore rule');
    fs.rmSync(dir, { recursive: true, force: true });
});

// ---------------------------------------------------------------------------
// Pacing (pacing.js): the recorder signs in one step at a time, at a human
// pace, and the wait never sits between a check and the step it vetted.
// ---------------------------------------------------------------------------

// A fake page whose locators record what was done to them, in order -- enough
// of Playwright's surface for fillStep and nothing more.
function recordingPage(events, fields) {
    return {
        url: () => 'https://www.example.com/login',
        locator(selector) {
            const name = Object.keys(fields).find((k) => selector.includes(fields[k]));
            const loc = {
                first: () => loc,
                count: async () => (name ? 1 : 0),
                inputValue: async () => '',
                fill: async () => { events.push(`fill ${name}`); },
                press: async (key) => { events.push(`press ${key} in ${name}`); }
            };
            return loc;
        }
    };
}

test('H1: sign-in steps are paced, and each wait comes BEFORE the check that vets the step', async () => {
    const events = [];
    const page = recordingPage(events, { user: 'autocomplete~=username', password: 'type=password' });
    const pace = { beforeAction: async () => { events.push('pause'); } };
    const stillHere = () => { events.push('stillHere'); return true; };
    const done = await signIn.fillStep(page, { username: USER, password: PASSWORD }, 'password', stillHere, pace);
    assert.strictEqual(done, 'password');
    assert.deepStrictEqual(events, [
        'pause', 'stillHere', 'fill user',
        'pause', 'stillHere', 'fill password',
        'pause', 'stillHere', 'press Enter in password'
    ], 'a wait between stillHere() and the step it vetted would let the page move under it');
});

test('H2: a page that moved during the wait is not typed into', async () => {
    const events = [];
    const page = recordingPage(events, { user: 'autocomplete~=username', password: 'type=password' });
    let here = true;
    const pace = { beforeAction: async () => { events.push('pause'); here = false; } };
    const done = await signIn.fillStep(page, { username: USER, password: PASSWORD }, 'password', () => here, pace);
    assert.strictEqual(done, null);
    assert.ok(!events.some((e) => e.startsWith('fill') || e.startsWith('press')),
        `nothing may be typed once the page has moved: ${events.join(', ')}`);
});

test('H3: with no pacer, fillStep waits for nothing (the unit cases and older callers)', async () => {
    const events = [];
    const page = recordingPage(events, { user: 'autocomplete~=username' });
    const done = await signIn.fillStep(page, { username: USER }, 'identifier', () => true);
    assert.strictEqual(done, 'identifier');
    assert.deepStrictEqual(events, ['fill user', 'press Enter in user']);
});

test('H4: the real pacer spaces the steps by the action range, and --pace off by nothing', async () => {
    const pacing = require(path.join(__dirname, 'pacing.js'));
    for (const [pace, expectWait] of [[pacing.DEFAULT_PACE, true], [pacing.PACE_OFF, false]]) {
        let t = 0;
        const slept = [];
        const pacer = pacing.createPacer(pace, {
            now: () => t, sleep: async (ms) => { slept.push(ms); t += ms; }, rng: () => 0.5
        });
        const page = recordingPage([], { user: 'autocomplete~=username', password: 'type=password' });
        await signIn.fillStep(page, { username: USER, password: PASSWORD }, 'password', () => true, pacer);
        if (expectWait) {
            assert.strictEqual(slept.length, 2, `a wait before the password and before Enter, got ${slept}`);
            for (const ms of slept) assert.ok(ms >= 300 && ms <= 1200, `${ms} outside 300-1200`);
        } else {
            assert.deepStrictEqual(slept, [], '--pace off types without waiting');
        }
    }
});

// ---------------------------------------------------------------------------
// Browser cases
// ---------------------------------------------------------------------------

const PAGES = {
    '/login': `<form method="post" action="/session">
        <input name="email" type="email" autocomplete="username">
        <input name="pw" type="password">
        <button>Sign in</button></form>`,
    '/signin/identifier': `<form method="post" action="/id-next">
        <input name="loginfmt" type="email"><button>Next</button></form>`,
    '/news': `<p>Latest offers</p><form method="post" action="/subscribe">
        <input name="email" type="email"><button>Subscribe</button></form>`,
    '/register': `<form method="post" action="/subscribe">
        <input name="email" type="email" autocomplete="username">
        <input name="pw" type="password"><input name="pw2" type="password"><button>Create</button></form>`,
    '/plain': '<p>no form here</p>',
    '/blog-insights': `<p>An article</p><form method="post" action="/subscribe">
        <input name="email" type="email"><button>Subscribe</button></form>`,
    '/spa': `<div id="app"></div><script>setTimeout(() => {
        document.getElementById('app').innerHTML =
            '<form method="post" action="/session"><input name="email" type="email" autocomplete="username">' +
            '<input name="pw" type="password"><button>Sign in</button></form>';
    }, 400);</script>`
};

function startSite() {
    const posts = [];
    const server = http.createServer((req, res) => {
        let body = '';
        req.on('data', (d) => { body += d; });
        req.on('end', () => {
            const send = (html, status = 200, headers = {}) => {
                res.writeHead(status, Object.assign({ 'content-type': 'text/html' }, headers));
                res.end(`<!doctype html><html><body>${html}</body></html>`);
            };
            const form = new URLSearchParams(body);
            if (req.method === 'POST') posts.push({ path: req.url, form });
            if (req.url === '/session') {
                const ok = form.get('pw') === PASSWORD && form.get('email') === USER;
                return ok
                    ? send('', 303, { location: '/home', 'set-cookie': 'sid=signed-in; Path=/' })
                    : send(PAGES['/login']);
            }
            if (req.url === '/id-next') {
                return send(`<form method="post" action="/session">
                    <input type="hidden" name="email" value="${form.get('loginfmt')}">
                    <input name="pw" type="password"><button>Sign in</button></form>`);
            }
            if (req.url === '/home') return send('<h1>Welcome</h1>');
            send(PAGES[req.url] || 'not found', PAGES[req.url] ? 200 : 404);
        });
    });
    return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({ server, posts, port: server.address().port })));
}

// Where CI puts the driver: a NODE_PATH published under its own name and
// installed nowhere global (see ci.yml), the same lookup
// capture-native-attach.test.js makes. An inherited NODE_PATH also works.
function loadPlaywright() {
    const nodePath = process.env.HAR_CAPTURE_TEST_NODE_PATH || process.env.NODE_PATH || '';
    const env = Object.assign({}, process.env, { NODE_PATH: nodePath });
    const resolved = capture.resolvePlaywright({ cwd: process.cwd(), env });
    if (!resolved.found) return null;
    try { return require(resolved.path); } catch (_) { return null; }
}

// The machine's Chrome when there is one, Playwright's own Chromium when not.
// CI downloads no Chromium (PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD) and has Chrome;
// a laptop may have either.
function launchBrowser(pw) {
    const chrome = capture.findChromeExecutable();
    return chrome && chrome.path
        ? pw.chromium.launch({ executablePath: chrome.path })
        : pw.chromium.launch();
}

async function browserCases(pw) {
    const site = await startSite();
    const browser = await launchBrowser(pw);
    // `localhost` is the capture's site; 127.0.0.1 is the SAME server under a
    // host the credential does not belong to (P3).
    const base = `http://localhost:${site.port}`;
    const other = `http://127.0.0.1:${site.port}`;
    const prompted = () => Promise.resolve({ source: 'prompt', username: USER, password: PASSWORD, hosts: ['localhost'], totp: null });

    // A per-case limit, so a hang is a named failure rather than a suite that
    // never reports -- which in CI reads as a timeout of the whole job.
    async function run(name, fn) {
        const context = await browser.newContext();
        let timer;
        const limit = new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error('timed out after 20s')), 20000);
        });
        const began = Date.now();
        try { await Promise.race([fn(context), limit]); slow(name, began); } catch (e) {
            failures.push(`${name}: ${e && e.message ? e.message : e}`);
        } finally {
            clearTimeout(timer);
            await context.close().catch(() => {});
        }
    }

    // Each load is checked explicitly AND the watcher's own load listener may
    // fire; checks are serialized, so the second finds nothing new to do.
    async function settle(page, watcher) {
        await page.waitForLoadState('load');
        await watcher.check(page);
    }

    await run('P1: a password form is filled, submitted, and the sign-in is noticed', async (context) => {
        const said = [];
        let signedIn = 0;
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, {
            uri: `${base}/login`, resolve: prompted, say: (l) => said.push(...l), onSignedIn: () => { signedIn++; }
        });
        await page.goto(`${base}/login`);
        await w.check(page);
        await page.waitForURL(/\/home$/);
        await settle(page, w);
        assert.strictEqual(signedIn, 1, 'onSignedIn fires once the form is gone');
        assert.ok(site.posts.some((p) => p.path === '/session' && p.form.get('pw') === PASSWORD));
        assert.deepStrictEqual(w.secrets().sort(), [PASSWORD, USER].sort());
        assert.deepStrictEqual(w.result(), { source: 'prompt', signedIn: true });
        assertNoLeak('operator output', said.join('\n'));
    });

    await run('P2: identifier-first -- the username, then the password on the next page', async (context) => {
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, { uri: `${base}/signin/identifier`, resolve: prompted, say: () => {} });
        const before = site.posts.length;
        await page.goto(`${base}/signin/identifier`);
        await w.check(page);
        // Not waiting on /id-next: the watcher's own load listener can carry
        // the flow through both steps before a wait for the middle one starts.
        await page.waitForURL(/\/home$/);
        const posts = site.posts.slice(before);
        assert.deepStrictEqual(posts.map((p) => p.path), ['/id-next', '/session']);
        assert.strictEqual(posts[0].form.get('loginfmt'), USER);
    });

    await run('P3: a form on a host the credential does not belong to is never filled', async (context) => {
        const before = site.posts.length;
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, { uri: `${base}/login`, resolve: prompted, say: () => {} });
        await page.goto(`${other}/login`);
        await w.check(page);
        await page.waitForTimeout(300);
        assert.strictEqual(await page.locator('input[type=password]').inputValue(), '');
        assert.strictEqual(site.posts.length, before, 'nothing was submitted');
    });

    await run('P4: a refused sign-in is not retried', async (context) => {
        const said = [];
        const wrong = () => Promise.resolve({ source: 'prompt', username: USER, password: 'wrong-password', hosts: ['localhost'], totp: null });
        const before = site.posts.length;
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, { uri: `${base}/login`, resolve: wrong, say: (l) => said.push(...l) });
        await page.goto(`${base}/login`);
        await w.check(page);
        await page.waitForURL(/\/session$/);
        await settle(page, w);
        await settle(page, w);
        assert.strictEqual(site.posts.length - before, 1, 'one attempt, then hand back');
        assert.ok(said.some((l) => /in the browser/.test(l)));
    });

    await run('P5: with no credentials nothing is filled and the human is told', async (context) => {
        const said = [];
        const none = () => Promise.resolve({ source: 'none', reason: 'no terminal to prompt in and no unlocked Bitwarden vault' });
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, { uri: `${base}/login`, resolve: none, say: (l) => said.push(...l) });
        await page.goto(`${base}/login`);
        await w.check(page);
        assert.strictEqual(await page.locator('input[type=password]').inputValue(), '');
        assert.ok(said.some((l) => /Sign in in the browser/.test(l)));
        assert.deepStrictEqual(w.secrets(), []);
    });

    await run('P6: a sign-in form with nobody at the terminal is reported, not filled', async (context) => {
        const unattended = [];
        const bw = fakeBw({ items: [item('a1', 'Example', ['localhost'])] });
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, {
            uri: `${base}/login`,
            resolve: (host) => signIn.resolveCredentials(host, bw.bw, fakeIo([], false)),
            say: () => {},
            onUnattended: (host) => unattended.push(host)
        });
        await page.goto(`${base}/login`);
        await w.check(page);
        assert.deepStrictEqual(unattended, ['localhost']);
        assert.strictEqual(await page.locator('input[type=password]').inputValue(), '');
        assert.deepStrictEqual(w.result(), { source: 'none', signedIn: false, needed: true });
    });

    await run('P7: a newsletter email box and a sign-up form are not sign-ins', async (context) => {
        let resolved = 0;
        let unattended = 0;
        const before = site.posts.length;
        const w = signIn.createSignInWatcher(context, {
            uri: `${base}/news`, say: () => {},
            resolve: () => { resolved++; return prompted(); },
            onUnattended: () => { unattended++; }
        });
        for (const p of ['/news', '/register', '/blog-insights']) {
            const page = await context.newPage();
            await page.goto(`${base}${p}`);
            await w.check(page);
            assert.strictEqual(await page.locator('input[type=email]').inputValue(), '', `${p}: the email box was filled`);
        }
        assert.strictEqual(resolved, 0, 'no vault or prompt was consulted');
        assert.strictEqual(unattended, 0, 'an unattended run would have ended over a newsletter box');
        assert.strictEqual(site.posts.length, before);
    });

    await run('P8: a page that moves to another host while the prompt is open is not filled', async (context) => {
        const before = site.posts.length;
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, {
            uri: `${base}/login`, say: () => {},
            // The operator is still typing when the page navigates away.
            resolve: async () => {
                await page.goto(`${other}/login`);
                return prompted();
            }
        });
        await page.goto(`${base}/login`);
        await w.check(page);
        assert.ok(page.url().startsWith(other));
        assert.strictEqual(await page.locator('input[type=password]').inputValue(), '');
        assert.strictEqual(site.posts.length, before, 'nothing was submitted');
    });

    await run('P9: a sign-in met mid-run with nobody present is a notice, not the end of the run', async (context) => {
        const said = [];
        let unattended = 0;
        const bw = fakeBw({ items: [] });
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, {
            uri: `${base}/plain`, say: (l) => said.push(...l),
            resolve: (host) => signIn.resolveCredentials(host, bw.bw, fakeIo([], false)),
            onUnattended: () => { unattended++; }
        });
        await page.goto(`${base}/plain`);
        await w.check(page);
        await page.goto(`${base}/login`);      // the session expired
        await w.check(page);
        assert.strictEqual(unattended, 0, 'what was already recorded would have been thrown away');
        assert.ok(said.some((l) => /Sign in in the browser/.test(l)));
        assert.strictEqual(w.result().needed, true);
    });

    await run('P10: a login wall drawn by script after load still counts as the first page', async (context) => {
        let unattended = 0;
        const bw = fakeBw({ items: [] });
        const page = await context.newPage();
        const w = signIn.createSignInWatcher(context, {
            uri: `${base}/spa`, say: () => {},
            resolve: (host) => signIn.resolveCredentials(host, bw.bw, fakeIo([], false)),
            onUnattended: () => { unattended++; }
        });
        await page.goto(`${base}/spa`, { waitUntil: 'domcontentloaded' });
        await w.check(page);
        assert.strictEqual(unattended, 1, 'the empty shell was taken for a page that is not a sign-in');
    });

    // Bounded: on some machines Chromium's shutdown alone takes minutes
    // (measured: 148s), which is nothing this suite tests. The process exits
    // explicitly below, taking a slow browser down with it.
    await Promise.race([browser.close(), new Promise((r) => setTimeout(r, 5000))]);
    site.server.close();
}

(async () => {
    for (const t of tests) {
        const started = Date.now();
        try { await t.fn(); } catch (e) { failures.push(`${t.name}: ${e && e.message ? e.message : e}`); }
        slow(t.name, started);
    }
    let started = Date.now();
    const pw = loadPlaywright();
    slow('loading playwright', started);
    started = Date.now();
    let browserBlocker = pw ? null : 'the playwright module is not installed';
    if (pw) {
        try { await browserCases(pw); } catch (e) {
            if (/Executable doesn't exist|browserType\.launch/i.test(String(e && e.message))) {
                browserBlocker = 'neither Chrome nor Playwright\'s Chromium is installed';
            } else {
                failures.push(`browser cases: ${e && e.message ? e.message : e}`);
            }
        }
    }
    if (browserBlocker) {
        if (process.env.CI) failures.push(`P1-P10 could not run in CI: ${browserBlocker}`);
        else skipped.push(`P1-P10 SKIPPED: ${browserBlocker}`);
    }

    skipped.forEach((s) => console.log(s));
    if (failures.length) {
        console.error(`sign-in.test.js: ${failures.length} failure(s)`);
        failures.forEach((f) => console.error(`  - ${f}`));
        process.exit(1);
    }
    console.log(`sign-in.test.js: all green (${tests.length} unit${skipped.length ? ', browser cases skipped' : ' + 10 browser'})`);
    process.exit(0);
})();
