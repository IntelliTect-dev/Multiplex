'use strict';

/**
 * sanitize-har-stream.test-support.js -- the capture that tells one pass from two.
 *
 * The streaming scrub (issue #10) reads a capture entry by entry, twice. The
 * whole-document scrub it replaced read it once, and several of its decisions
 * were taken over the WHOLE document before any entry was rewritten. A
 * streaming port that got one of those wrong would still scrub, still pass the
 * gate, and quietly write different bytes. So this capture carries one case
 * for every such decision, each placed so that a single forward pass gets it
 * wrong:
 *
 *   - a card-shaped value at a PLAIN field in entry 0 whose identifier-field
 *     twin sits in a LATER entry (2): the whole-run `declined` set keeps it in
 *     both places; a single pass would rewrite entry 0's copy;
 *   - a key-name secret (`lsd`) first named in entry 1 that also appears
 *     UNNAMED in entry 0: the survivor sweep replaces it only if the full set
 *     of originals is known before entry 0 is written;
 *   - `fb_dtsg`, replaced in its percent-encoded wire spelling and echoed
 *     DECODED in entry 0. The scrub records the decoded spelling beside the
 *     wire one (issue #67), so the sweep replaces the echo with the form
 *     field's own fake -- and, like `lsd`, only if every original is known
 *     before entry 0 is written. (The oracle first froze that echo as a
 *     surviving leak; #67 fixed it and regenerated the oracle, which changed
 *     that one value and nothing else.)
 *   - a 32-hex module hash the gate blocks, which is blunted, so the blunt
 *     record is written into the envelope AFTER `entries`;
 *   - an operator literal in `log.comment` -- an envelope key that sorts after
 *     `entries` -- and in a body;
 *   - `pages` before `entries`, so the envelope has content on both sides.
 *
 * The expected output was produced by the whole-document scrub on `main` at
 * 860e21e and committed under tests/fixtures/har/stream-scrub/. It is the
 * oracle: the streaming scrub must reproduce it byte for byte. All values are
 * synthetic.
 */

const PROFILE = {
    salt: 'stream-scrub-fixture-salt',
    literals: { 'Zelda Quartermain-Operator': '<OperatorName>' },
};

const FIXED_TIME = '2026-01-01T00:00:00.000Z';
const OPERATOR = 'Zelda Quartermain-Operator';
const DTSG = 'NAdQx3kLr9WpT2vYb7Zc:17:1700000000';
const CARD = '4143123412341237';
const HEX32 = 'a1b2c3d4e5f60718293a4b5c6d7e8f90';
const EMAIL = 'zelda.quartermain@example.org';

function luhnFix(digits15) {
    let sum = 0;
    [...digits15].reverse().forEach((d, i) => {
        let n = Number(d);
        if (i % 2 === 0) { n *= 2; if (n > 9) n -= 9; }
        sum += n;
    });
    return digits15 + String((10 - (sum % 10)) % 10);
}

function entry(i, { method = 'GET', url, reqHeaders = [], cookies = [], postData, resText }) {
    return {
        startedDateTime: `2026-01-01T00:00:0${i}.000Z`, time: 1,
        request: {
            method, url, httpVersion: 'HTTP/1.1', headers: reqHeaders,
            queryString: [], cookies, headersSize: -1, bodySize: postData ? postData.text.length : 0,
            ...(postData ? { postData } : {}),
        },
        response: {
            status: 200, statusText: 'OK', httpVersion: 'HTTP/1.1', headers: [], cookies: [],
            content: { size: resText.length, mimeType: 'application/json', text: resText },
            redirectURL: '', headersSize: -1, bodySize: resText.length,
        },
        cache: {}, timings: { send: 0, wait: 1, receive: 0 },
    };
}

function buildCapture() {
    const card = luhnFix(CARD.slice(0, 15));
    const vars = JSON.stringify({ actor_id: '100000000001', caption: `posted by ${OPERATOR}` });
    const form = `fb_dtsg=${encodeURIComponent(DTSG)}&lsd=AbCdEf123456GhIjKlMn&variables=${encodeURIComponent(vars)}`;
    return {
        log: {
            version: '1.2',
            creator: { name: 'stream-scrub-fixture', version: '1' },
            pages: [{ startedDateTime: FIXED_TIME, id: 'page_1', title: 'fixture', pageTimings: {} }],
            entries: [
                entry(0, {
                    url: 'https://www.example.test/profile',
                    cookies: [{ name: 'datr', value: 'Xy7Qp2LmN8vR4tKw9Zb3cD5f' }],
                    reqHeaders: [{ name: 'Cookie', value: 'datr=Xy7Qp2LmN8vR4tKw9Zb3cD5f' }],
                    resText: JSON.stringify({ order_code: card, contact: EMAIL, echo: DTSG, again: 'AbCdEf123456GhIjKlMn', n: 1 }),
                }),
                entry(1, {
                    method: 'POST',
                    url: 'https://www.example.test/api/graphql/',
                    postData: {
                        mimeType: 'application/x-www-form-urlencoded',
                        text: form,
                        params: [
                            { name: 'fb_dtsg', value: encodeURIComponent(DTSG) },
                            { name: 'lsd', value: 'AbCdEf123456GhIjKlMn' },
                            { name: 'variables', value: encodeURIComponent(vars) },
                        ],
                    },
                    resText: `for (;;);{"jsmods":{"require":[["m\\u002F${HEX32}","m",[]]]},"n":1}`,
                }),
                entry(2, {
                    url: 'https://www.example.test/media',
                    resText: JSON.stringify({ media_id: card, owner: OPERATOR }),
                }),
            ],
            comment: `recorded by ${OPERATOR}`,
        },
    };
}

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { makeTempRepo } = require(path.join(__dirname, 'har-test-repo.test-support.js'));

/** Scrub the fixture with the given sanitize-har.js; return its outputs as text. */
function scrubFixture(sanitizePath) {
    const tmp = makeTempRepo('stream-scrub-');
    const dir = path.join(tmp, '.har-captures', 'fixture');
    fs.mkdirSync(dir, { recursive: true });
    const profile = path.join(tmp, '.har-profile.json');
    fs.writeFileSync(profile, JSON.stringify(PROFILE));
    const raw = path.join(dir, 'raw.har');
    fs.writeFileSync(raw, JSON.stringify(buildCapture(), null, 2));
    const out = path.join(dir, 'scrubbed.har');
    const subs = path.join(dir, 'subs.json');
    const piiSubs = path.join(dir, 'pii-subs.json');
    const r = spawnSync(process.execPath, [sanitizePath, '--in', raw, '--out', out, '--subs', subs,
        '--pii-subs', piiSubs, '--profile', profile, '--fixed-time', FIXED_TIME], { encoding: 'utf8' });
    const read = (p) => (fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : null);
    const result = { code: r.status, stdout: r.stdout, stderr: r.stderr,
        scrubbed: read(out), subs: read(subs), piiSubs: read(piiSubs), dir: tmp, raw };
    return result;
}

const GOLDEN_DIR = path.join(__dirname, '..', '..', '..', '..', 'tests', 'fixtures', 'har', 'stream-scrub');

module.exports = { PROFILE, FIXED_TIME, buildCapture, scrubFixture, GOLDEN_DIR };
