#!/usr/bin/env node
// Behavior tests for issues #23 and #68 -- a replacement written over a JSON
// NUMBER must leave valid JSON behind.
//
// Zero-dep, runs with `node json-number-placeholders.test.js`.
//
// Both defects were found on real captures by the #21 runner: bodies that
// parsed before the scrub and not after.
//
//   #23  the literal pass swapped an operator id written as a bare JSON number
//        -- `"userID":100012345678` -- for an unquoted `<FacebookUserId>`.
//   #68  the substitution sweep did the same with a fake, inside a form field's
//        JSON (`variables=`); and the value it swept was a device-pixel-ratio
//        cookie, taken as a token only because it was long.
//
// The fix is STRUCTURAL: where a body (or a form field inside one) is valid
// JSON, its number tokens are found by tokenizing it, and a matching number
// becomes a QUOTED placeholder. Text that is not JSON keeps the plain
// replacement it always had -- the punctuation heuristic that was tried first
// corrupted prose and CSV, and this test pins that it cannot.
//
// All values are synthetic.

'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { makeTempRepo } = require(path.join(__dirname, 'har-test-repo.test-support.js'));

const SANITIZE = path.join(__dirname, 'sanitize-har.js');
const ACCOUNT = '100012345678';
const SPIN = '1700000000123456';
const DPR = '1.3499999046325684';

let passed = 0;
function test(name, fn) {
    try { fn(); passed++; console.log(`ok - ${name}`); } catch (e) {
        console.error(`not ok - ${name}\n${e.stack}`);
        process.exitCode = 1;
    }
}

function entry(url, { method = 'GET', cookies = [], headers = [], postData, resText, mime = 'application/json' }) {
    return {
        startedDateTime: '2026-01-01T00:00:00.000Z', time: 1,
        request: { method, url, httpVersion: 'HTTP/1.1', headers, queryString: [], cookies,
            headersSize: -1, bodySize: 0, ...(postData ? { postData } : {}) },
        response: { status: 200, statusText: 'OK', httpVersion: 'HTTP/1.1', headers: [], cookies: [],
            content: { size: resText.length, mimeType: mime, text: resText },
            redirectURL: '', headersSize: -1, bodySize: -1 },
        cache: {}, timings: { send: 0, wait: 1, receive: 0 },
    };
}

function scrub(entries) {
    const tmp = makeTempRepo('json-numbers-');
    const dir = path.join(tmp, '.har-captures', 'c');
    fs.mkdirSync(dir, { recursive: true });
    const profile = path.join(tmp, '.har-profile.json');
    fs.writeFileSync(profile, JSON.stringify({ salt: 'json-number-salt', literals: { [ACCOUNT]: '<FacebookUserId>' } }));
    const raw = path.join(dir, 'raw.har');
    fs.writeFileSync(raw, JSON.stringify({ log: { version: '1.2', creator: { name: 't', version: '1' }, entries } }));
    const out = path.join(dir, 'scrubbed.har');
    const r = spawnSync(process.execPath, [SANITIZE, '--in', raw, '--out', out, '--profile', profile,
        '--fixed-time', '2026-01-01T00:00:00.000Z'], { encoding: 'utf8' });
    try {
        assert.strictEqual(r.status, 0, r.stderr);
        return JSON.parse(fs.readFileSync(out, 'utf8')).log.entries;
    } finally {
        fs.rmSync(tmp, { recursive: true, force: true });
    }
}

const body = (e) => e.response.content.text;
const formValue = (text, name) => decodeURIComponent(
    text.split('&').find((p) => p.startsWith(`${name}=`)).slice(name.length + 1).replace(/\+/g, ' '));

test('#23: an operator id written as a bare JSON number becomes a QUOTED placeholder', () => {
    const [e] = scrub([entry('https://a.test/me', { resText: `{"userID":${ACCOUNT},"kind":"x"}` })]);
    const parsed = JSON.parse(body(e));
    assert.strictEqual(parsed.userID, '<FacebookUserId>');
    assert.strictEqual(parsed.kind, "x");
});

test('#23: behind a for (;;); prefix, and inside nested arrays', () => {
    const [e] = scrub([entry('https://a.test/ajax', {
        resText: `for (;;);{"payload":{"actors":[${ACCOUNT},2],"ok":true}}`,
    })]);
    assert.ok(body(e).startsWith('for (;;);'), 'the prefix is kept');
    const parsed = JSON.parse(body(e).slice('for (;;);'.length));
    assert.deepStrictEqual(parsed.payload.actors, ['<FacebookUserId>', 2]);
});

test('#23: a quoted id is replaced exactly as before', () => {
    const [e] = scrub([entry('https://a.test/me', { resText: `{"userID":"${ACCOUNT}"}` })]);
    assert.strictEqual(body(e), '{"userID":"<FacebookUserId>"}');
});

test('text that is not JSON keeps the plain replacement -- no quotes injected', () => {
    // The shapes that broke the punctuation heuristic (IntelliSDLC.ai#485).
    const [prose, csv] = scrub([
        entry('https://a.test/p', { resText: `Name: ${ACCOUNT}, Age: 5`, mime: 'text/plain' }),
        entry('https://a.test/c', { resText: `a,${ACCOUNT},c`, mime: 'text/csv' }),
    ]);
    assert.strictEqual(body(prose), 'Name: <FacebookUserId>, Age: 5');
    assert.strictEqual(body(csv), 'a,<FacebookUserId>,c');
});

test('a number that merely CONTAINS the id stays valid JSON, with the id hidden', () => {
    const [e] = scrub([entry('https://a.test/me', { resText: `{"big":9${ACCOUNT}9,"ok":1}` })]);
    const parsed = JSON.parse(body(e));
    assert.strictEqual(parsed.big, '9<FacebookUserId>9');
});

test('#68: a swept secret echoed as a bare number inside a form field\'s JSON leaves valid JSON', () => {
    const vars = `{"ts":${SPIN},"scale":2}`;
    const text = `__spin_t=${SPIN}&variables=${encodeURIComponent(vars)}`;
    const [e] = scrub([entry('https://a.test/api/graphql/', {
        method: 'POST',
        postData: { mimeType: 'application/x-www-form-urlencoded', text,
            params: [{ name: '__spin_t', value: SPIN }, { name: 'variables', value: encodeURIComponent(vars) }] },
        resText: '{}',
    })]);
    const pd = e.request.postData;
    const fromText = JSON.parse(formValue(pd.text, 'variables'));
    const fromParams = JSON.parse(decodeURIComponent(pd.params[1].value));
    assert.strictEqual(typeof fromText.ts, 'string', 'the fake is quoted in the text');
    assert.notStrictEqual(fromText.ts, SPIN, 'and the secret is gone');
    assert.deepStrictEqual(fromParams, fromText, 'both copies agree');
    assert.strictEqual(fromText.scale, 2);
});

test('#68: a device-pixel-ratio cookie is not a token just because it is long', () => {
    const [e] = scrub([entry('https://a.test/', {
        cookies: [{ name: 'dpr', value: DPR }],
        headers: [{ name: 'Cookie', value: `dpr=${DPR}` }],
        resText: '{}',
    })]);
    assert.strictEqual(e.request.cookies[0].value, DPR);
    assert.strictEqual(e.request.headers[0].value, `dpr=${DPR}`);
});

test('a long cookie that is not a plain decimal is still scrubbed', () => {
    const [e] = scrub([entry('https://a.test/', {
        cookies: [{ name: 'sb', value: 'Xy7Qp2LmN8vR4tKw9Zb3' }],
        resText: '{}',
    })]);
    assert.notStrictEqual(e.request.cookies[0].value, 'Xy7Qp2LmN8vR4tKw9Zb3');
});

test('#23 in a URL: JSON in a query parameter stays valid, in the url and in queryString[]', () => {
    const vars = `{"id":${ACCOUNT},"ok":true}`;
    const e0 = entry(`https://a.test/graphql?doc=1&variables=${encodeURIComponent(vars)}`, { resText: '{}' });
    e0.request.queryString = [{ name: 'doc', value: '1' }, { name: 'variables', value: encodeURIComponent(vars) }];
    const [e] = scrub([e0]);
    const fromUrl = JSON.parse(decodeURIComponent(e.request.url.split('variables=')[1]));
    assert.strictEqual(fromUrl.id, '<FacebookUserId>');
    const fromQs = JSON.parse(decodeURIComponent(e.request.queryString[1].value));
    assert.deepStrictEqual(fromQs, fromUrl);
    assert.ok(e.request.url.includes('doc=1&'), 'an untouched parameter is left byte for byte');
});

test('a long decimal cookie under any other name is still scrubbed -- only dpr is a setting', () => {
    const [e] = scrub([entry('https://a.test/', {
        cookies: [{ name: 'sess_t', value: '1700000000.123456789' }],
        resText: '{}',
    })]);
    assert.notStrictEqual(e.request.cookies[0].value, '1700000000.123456789');
});

if (process.exitCode) process.exit(process.exitCode);
console.log(`All json-number-placeholders tests passed (${passed})`);
