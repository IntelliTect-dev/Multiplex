#!/usr/bin/env node
// Behavior tests for mirror-fields.js -- the text boxes on screen, so the phone
// view can lay a real one over each (#155). Zero-dep; runs with
// `node mirror-fields.test.js`. Every test drives a real (headless) Chrome:
// what counts as "a text box the person can see" is the browser's answer, not
// ours.
//
// FALSIFIERS (fail before this change, pass after):
//   F1  a sign-in form: the email box and the password box are listed, where
//       they are on screen, as a username and a password; a box nobody can
//       see or type in is not (hidden, display:none, disabled, read-only,
//       scrolled away, covered by another element, a checkbox); a password's
//       value is never read, and another box's value is
//   F2  a box inside a CROSS-ORIGIN frame (its own renderer process) is listed
//       where it is on the page, offset by the frame, clipped to it
//   F3  what a box is for: a one-time code, a sign-in name beside a password,
//       an email box alone (an email, not a sign-in -- unless the page's
//       address says it is a sign-in), a phone number, plain text
//   F4  a box with its label drawn over it (a "floating label") is still one
//       the person can tap
//   F5  A PASSWORD BY ANY OTHER TYPE (review of #155): a password box a
//       "show password" toggle turned into type=text (its autocomplete, or its
//       name, still says password), and a PIN, are passwords -- their values
//       never read; a phone-number box on a sign-in is the sign-in name

'use strict';

const assert = require('assert');
const http = require('http');
const path = require('path');

const fields = require(path.join(__dirname, 'mirror-fields.js'));

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }
const skipped = [];

function integrationRequired(ciValue) {
    if (ciValue === undefined || ciValue === null) return false;
    const value = String(ciValue).trim();
    return !!value && value.toLowerCase() !== 'false';
}

function browserBlocker() {
    const capture = require(path.join(__dirname, 'capture-har.js'));
    const env = Object.assign({}, process.env,
        { NODE_PATH: process.env.HAR_CAPTURE_TEST_NODE_PATH || process.env.NODE_PATH || '' });
    const resolved = capture.resolvePlaywright({ cwd: process.cwd(), env });
    if (!resolved.found) return { why: 'the playwright module is not installed' };
    const chrome = capture.findChromeExecutable();
    if (!chrome.path) return { why: 'Google Chrome is not installed' };
    return { playwright: require(resolved.path), chrome: chrome.path };
}

/** A browser for one test, or null when there is none (skipped, or a failure in CI). */
async function withBrowser(id, fn) {
    const found = browserBlocker();
    if (found.why) {
        if (integrationRequired(process.env.CI)) throw new Error(`${id} cannot run: ${found.why} -- in CI that is a failure`);
        skipped.push(`${id} (${found.why})`);
        return;
    }
    // Headless, in a test only: the product never launches this browser.
    const browser = await found.playwright.chromium.launch({ executablePath: found.chrome, headless: true });
    try { await fn(browser); } finally { await browser.close(); }
}

/** A local site: `routes` maps a path to HTML. Listens on 127.0.0.1. */
async function site(routes) {
    const server = http.createServer((req, res) => {
        const body = routes[new URL(req.url, 'http://x').pathname];
        res.writeHead(body ? 200 : 404, { 'content-type': 'text/html; charset=utf-8' });
        res.end(body || 'not found');
    });
    await new Promise((r) => server.listen(0, '127.0.0.1', r));
    return { server, port: server.address().port };
}

const near = (a, b, what) => assert.ok(Math.abs(a - b) <= 1.5, `${what}: ${a} is not ${b}`);
const byLabel = (list, label) => list.find((f) => f.label === label);

// ---------------------------------------------------------------------------

test('F1: a sign-in form -- the boxes a person can see and type in, where they are, and no password value', async () => {
    await withBrowser('F1', async (browser) => {
        const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
        await page.setContent('<html><body style="margin:0">' +
            '<input aria-label="email" type="email" autocomplete="username" value="pre@example.invalid" ' +
            '  style="position:absolute;left:100px;top:50px;width:300px;height:40px">' +
            '<input aria-label="password" type="password" value="never-read-1" ' +
            '  style="position:absolute;left:100px;top:120px;width:300px;height:40px">' +
            '<input aria-label="newsletter" type="text" value="hello" ' +
            '  style="position:absolute;left:500px;top:50px;width:200px;height:30px">' +
            '<input aria-label="hidden-type" type="hidden" value="x">' +
            '<input aria-label="display-none" style="display:none">' +
            '<input aria-label="invisible" style="position:absolute;left:0;top:300px;visibility:hidden">' +
            '<input aria-label="disabled" disabled style="position:absolute;left:0;top:340px">' +
            '<input aria-label="readonly" readonly style="position:absolute;left:0;top:380px">' +
            '<input aria-label="checkbox" type="checkbox" style="position:absolute;left:0;top:420px">' +
            '<input aria-label="below-the-fold" style="position:absolute;left:0;top:2000px">' +
            '<input aria-label="covered" style="position:absolute;left:500px;top:300px;width:200px;height:30px">' +
            '<div style="position:absolute;left:480px;top:280px;width:260px;height:80px;background:#fff"></div>' +
            '</body></html>');
        const got = await fields.readFields(page);
        assert.deepStrictEqual([got.vw, got.vh], [800, 600], 'measured in the viewport the screencast shows');
        assert.deepStrictEqual(got.fields.map((f) => f.label).sort(), ['email', 'newsletter', 'password'],
            `only the boxes a person can see and type in: ${JSON.stringify(got.fields.map((f) => f.label))}`);
        const email = byLabel(got.fields, 'email');
        near(email.x, 100, 'x'); near(email.y, 50, 'y'); near(email.w, 300, 'w'); near(email.h, 40, 'h');
        assert.strictEqual(email.kind, 'username');
        assert.strictEqual(email.type, 'email');
        assert.strictEqual(email.value, 'pre@example.invalid', 'what the box shows, so the phone shows it too');
        const pw = byLabel(got.fields, 'password');
        assert.strictEqual(pw.kind, 'password');
        assert.strictEqual(pw.length, 'never-read-1'.length, 'whether it holds something, and how much');
        assert.ok(!('value' in pw), 'a password\'s value is never read');
        assert.ok(!JSON.stringify(got).includes('never-read-1'), 'not anywhere');
        assert.strictEqual(byLabel(got.fields, 'newsletter').kind, 'text', 'a box beside a sign-in is not a sign-in name');
    });
});

test('F2: a box inside a cross-origin frame is listed where it is on the page', async () => {
    await withBrowser('F2', async (browser) => {
        // 127.0.0.1 and localhost are different SITES, so the frame gets its
        // own renderer process -- the case a same-document walk would miss.
        const routes = {
            '/inner': '<html><body style="margin:0">' +
                '<input aria-label="inner-pw" type="password" style="position:absolute;left:20px;top:30px;width:150px;height:30px">' +
                '<input aria-label="inner-far" style="position:absolute;left:20px;top:900px;width:150px;height:30px">' +
                '</body></html>'
        };
        const { server, port } = await site(routes);
        routes['/outer'] = '<html><body style="margin:0">' +
            `<iframe src="http://localhost:${port}/inner" style="position:absolute;left:200px;top:100px;` +
            'width:300px;height:200px;border:0"></iframe></body></html>';
        try {
            const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
            await page.goto(`http://127.0.0.1:${port}/outer`);
            await page.waitForFunction(() => document.querySelector('iframe') &&
                document.querySelector('iframe').getBoundingClientRect().width > 0);
            const frame = page.frames().find((f) => f.url().includes('/inner'));
            assert.ok(frame, 'the inner frame loaded');
            await frame.waitForSelector('input');
            const got = await fields.readFields(page);
            const inner = byLabel(got.fields, 'inner-pw');
            assert.ok(inner, `the box in the frame is listed: ${JSON.stringify(got.fields)}`);
            near(inner.x, 220, 'x, offset by the frame'); near(inner.y, 130, 'y, offset by the frame');
            assert.strictEqual(inner.kind, 'password');
            assert.ok(!byLabel(got.fields, 'inner-far'), 'a box outside the frame\'s window is not on screen');
        } finally {
            server.close();
        }
    });
});

test('F3: what each box is for -- one-time code, sign-in name, email, phone, plain text', async () => {
    await withBrowser('F3', async (browser) => {
        const { server, port } = await site({
            '/settings': '<html><body style="margin:0">' +
                '<input aria-label="email-alone" type="email" style="position:absolute;left:0;top:0;width:200px;height:30px">' +
                '<input aria-label="phone" type="tel" style="position:absolute;left:0;top:40px;width:200px;height:30px">' +
                '<input aria-label="code" autocomplete="one-time-code" inputmode="numeric" maxlength="6" ' +
                '  style="position:absolute;left:0;top:80px;width:200px;height:30px">' +
                '<input aria-label="otp-by-name" name="otp" style="position:absolute;left:0;top:120px;width:200px;height:30px">' +
                '<input aria-label="search" type="search" style="position:absolute;left:0;top:160px;width:200px;height:30px">' +
                '</body></html>',
            '/signin': '<html><body style="margin:0">' +
                '<input aria-label="email-on-signin" type="email" style="position:absolute;left:0;top:0;width:200px;height:30px">' +
                '</body></html>',
            '/login-form': '<html><body style="margin:0">' +
                '<input aria-label="login-name" name="login" style="position:absolute;left:0;top:0;width:200px;height:30px">' +
                '<input aria-label="pw" type="password" style="position:absolute;left:0;top:40px;width:200px;height:30px">' +
                '</body></html>'
        });
        try {
            const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
            await page.goto(`http://127.0.0.1:${port}/settings`);
            let got = await fields.readFields(page);
            const kind = (label) => (byLabel(got.fields, label) || {}).kind;
            assert.strictEqual(kind('email-alone'), 'email', 'an email box on its own is not a sign-in');
            assert.strictEqual(kind('phone'), 'tel');
            assert.strictEqual(kind('code'), 'otp');
            assert.strictEqual(byLabel(got.fields, 'code').inputmode, 'numeric');
            assert.strictEqual(byLabel(got.fields, 'code').maxLength, 6);
            assert.strictEqual(kind('otp-by-name'), 'otp');
            assert.strictEqual(kind('search'), 'text');
            await page.goto(`http://127.0.0.1:${port}/signin`);
            got = await fields.readFields(page);
            assert.strictEqual(kind('email-on-signin'), 'username', 'on a sign-in address, the email box is the sign-in name');
            await page.goto(`http://127.0.0.1:${port}/login-form`);
            got = await fields.readFields(page);
            assert.strictEqual(kind('login-name'), 'username', 'a name box beside a password is the sign-in name');
            assert.strictEqual(kind('pw'), 'password');
        } finally {
            server.close();
        }
    });
});

test('F4: a box with its label drawn over it is still one the person can tap', async () => {
    await withBrowser('F4', async (browser) => {
        const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
        await page.setContent('<html><body style="margin:0">' +
            '<input id="u" aria-label="floating" style="position:absolute;left:100px;top:100px;width:300px;height:50px">' +
            '<label for="u" style="position:absolute;left:100px;top:100px;width:300px;height:50px">Email or phone</label>' +
            '</body></html>');
        const got = await fields.readFields(page);
        assert.ok(byLabel(got.fields, 'floating'), `the box under its own label is listed: ${JSON.stringify(got.fields)}`);
    });
});

test('F5: a password shown as text, or a PIN, is still a password; a phone number on a sign-in is the sign-in name', async () => {
    await withBrowser('F5', async (browser) => {
        const { server, port } = await site({
            '/signin': '<html><body style="margin:0">' +
                '<input aria-label="shown-by-autocomplete" type="text" autocomplete="current-password" value="visible-pw-1" ' +
                '  style="position:absolute;left:0;top:0;width:200px;height:30px">' +
                '<input aria-label="shown-by-name" type="text" name="password" value="visible-pw-2" ' +
                '  style="position:absolute;left:0;top:40px;width:200px;height:30px">' +
                '<input aria-label="pin" type="tel" name="pin" value="4321" ' +
                '  style="position:absolute;left:0;top:80px;width:200px;height:30px">' +
                '<input aria-label="phone" type="tel" name="phone" value="5550100" ' +
                '  style="position:absolute;left:0;top:120px;width:200px;height:30px">' +
                '<input aria-label="passport" type="text" name="passport" ' +
                '  style="position:absolute;left:0;top:160px;width:200px;height:30px">' +
                '</body></html>'
        });
        try {
            const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
            await page.goto(`http://127.0.0.1:${port}/signin`);
            const got = await fields.readFields(page);
            const kind = (label) => (byLabel(got.fields, label) || {}).kind;
            assert.strictEqual(kind('shown-by-autocomplete'), 'password');
            assert.strictEqual(kind('shown-by-name'), 'password');
            assert.strictEqual(kind('pin'), 'password');
            for (const v of ['visible-pw-1', 'visible-pw-2', '4321']) {
                assert.ok(!JSON.stringify(got).includes(v), `a password's value is never read: ${v}`);
            }
            assert.strictEqual(kind('phone'), 'username', 'a phone number on a sign-in is the sign-in name');
            assert.strictEqual(kind('passport'), 'text', '"passport" is not a password');
        } finally {
            server.close();
        }
    });
});

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
    if (failures.length) {
        process.stderr.write(`\n${failures.length} of ${queued.length} mirror-fields tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All mirror-fields tests passed (${passed})` + (skipped.length ? ` -- SKIPPED: ${skipped.join(', ')}` : ''));
    process.exit(0);
})();
