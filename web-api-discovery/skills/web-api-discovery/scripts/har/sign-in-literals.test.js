#!/usr/bin/env node
// Behavior tests for the sign-in values reaching the scrub and the gate over
// stdin (`--literals -`, issue #115).
//
// The recorder fills a username and password into the site. The key-name scrub
// removes them where the field is called `password`; these values must ALSO go
// where the site called the field something nobody anticipated -- the
// literal-value pass exists for exactly that -- and the gate must prove them
// gone, profile or no profile.
//
//   L1  a password under an unrecognised field name is replaced, and the
//       username wherever it was echoed back
//   L2  the gate flags the same values in a capture that still carries them,
//       even with no operator profile
//   L3  the gate passes a capture that does not carry them
//   L4  a malformed stdin document fails without echoing what it was given
//   L5  short values are dropped by the profile's own minimum
//
// Zero-dep, runs with `node sign-in-literals.test.js`.

'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { initProtectedRepo } = require(path.join(__dirname, 'har-test-repo.test-support.js'));
const harProfile = require(path.join(__dirname, 'har-profile.js'));

const sanitize = path.join(__dirname, 'sanitize-har.js');
const verify = path.join(__dirname, 'verify-scrub.js');
const tmp = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'sign-in-literals-')));

const USER = 'operator@example.test';
const PASSWORD = 'Zq8-unusual-field-pass-115';

const failures = [];
function check(name, fn) {
    try { fn(); } catch (e) { failures.push(`${name}: ${e && e.message ? e.message : e}`); }
}

function run(script, args, cwd, input) {
    const r = spawnSync(process.execPath, [script, ...args], { encoding: 'utf8', cwd, input });
    return { code: r.status, stdout: r.stdout || '', stderr: r.stderr || '' };
}

function project(name, withProfile) {
    const dir = path.join(tmp, name);
    initProtectedRepo(dir);
    fs.mkdirSync(path.join(dir, 'samples', 'har-original'), { recursive: true });
    if (withProfile) {
        fs.writeFileSync(path.join(dir, '.har-profile.json'),
            JSON.stringify({ salt: 'test-salt', literals: {} }));
    }
    return dir;
}

function har(dir, body, responseText) {
    const p = path.join(dir, 'samples', 'har-original', 'capture.har');
    fs.writeFileSync(p, JSON.stringify({
        log: {
            version: '1.2', creator: { name: 'test', version: '1' },
            entries: [{
                startedDateTime: '2026-01-01T00:00:00.000Z', time: 1,
                request: {
                    method: 'POST', url: 'https://example.invalid/u/login', httpVersion: 'HTTP/1.1',
                    headers: [], queryString: [], cookies: [], headersSize: -1, bodySize: body.length,
                    postData: { mimeType: 'application/x-www-form-urlencoded', text: body }
                },
                response: {
                    status: 200, statusText: 'OK', httpVersion: 'HTTP/1.1', headers: [], cookies: [],
                    content: { size: responseText.length, mimeType: 'application/json', text: responseText },
                    redirectURL: '', headersSize: -1, bodySize: responseText.length
                },
                cache: {}, timings: { send: 0, wait: 1, receive: 0 }
            }]
        }
    }, null, 2));
    return p;
}

const STDIN = JSON.stringify([USER, PASSWORD]);
// `xk` is a field name no key list will ever contain.
const BODY = `ident=${encodeURIComponent(USER)}&xk=${encodeURIComponent(PASSWORD)}`;
const ECHO = JSON.stringify({ welcome: USER });

check('L1: an unrecognised field and an echoed username are both replaced', () => {
    const dir = project('l1', true);
    const input = har(dir, BODY, ECHO);
    const out = path.join(dir, 'samples', 'har', 'capture.har');
    const r = run(sanitize, ['--in', input, '--out', out, '--literals', '-'], dir, STDIN);
    assert.strictEqual(r.code, 0, `sanitize failed: ${r.stderr}`);
    const text = fs.readFileSync(out, 'utf8');
    assert.ok(!text.includes(PASSWORD) && !text.includes(encodeURIComponent(PASSWORD)), 'the password survived');
    assert.ok(!text.includes(USER) && !text.includes(encodeURIComponent(USER)), 'the username survived');
    assert.ok(!(r.stdout + r.stderr).includes(PASSWORD), 'the scrub printed the password');
});

check('L2: the gate flags the values with no operator profile', () => {
    const dir = project('l2', false);
    const input = har(dir, BODY, ECHO);
    const r = run(verify, ['--in', input, '--literals', '-'], dir, STDIN);
    assert.notStrictEqual(r.code, 0, 'the gate passed a capture carrying the sign-in values');
    assert.ok(r.stdout.includes(harProfile.SIGN_IN_SENTINEL) || r.stderr.includes(harProfile.SIGN_IN_SENTINEL),
        'the finding names the sentinel');
    assert.ok(!(r.stdout + r.stderr).includes(PASSWORD), 'the gate printed the password');
});

check('L3: the gate passes a capture without them', () => {
    const dir = project('l3', false);
    const input = har(dir, 'ident=someone-else', '{}');
    const r = run(verify, ['--in', input, '--literals', '-'], dir, STDIN);
    assert.strictEqual(r.code, 0, `the gate refused a clean capture: ${r.stdout}${r.stderr}`);
});

check('L4: a malformed stdin document fails without echoing it', () => {
    const dir = project('l4', true);
    const input = har(dir, BODY, ECHO);
    const r = run(verify, ['--in', input, '--literals', '-'], dir, `{"not":"${PASSWORD}"}`);
    assert.strictEqual(r.code, 1);
    assert.ok(/JSON array of strings/.test(r.stderr));
    assert.ok(!r.stderr.includes(PASSWORD));
});

check('L5: short values are dropped by the profile minimum', () => {
    assert.deepStrictEqual(harProfile.parseSessionLiterals('["ab", "abcd", "abcd"]'),
        [{ literal: 'abcd', sentinel: harProfile.SIGN_IN_SENTINEL }]);
});

fs.rmSync(tmp, { recursive: true, force: true });
if (failures.length) {
    console.error(`sign-in-literals.test.js: ${failures.length} failure(s)`);
    failures.forEach((f) => console.error(`  - ${f}`));
    process.exit(1);
}
console.log('sign-in-literals.test.js: all green (5)');
