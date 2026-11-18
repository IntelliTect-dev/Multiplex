#!/usr/bin/env node
// Behavior tests for the query parameters of a generated wrapper (issue #34):
// what the capture showed for an endpoint, which of it becomes a default, which
// is never baked in, what each parameter is called in C#, and the query string
// that goes on the wire.
//
// These pin the RULES. That a generated client really sends the query is
// tests/generated-query-parameters.Tests.ps1, which builds a wrapper and reads
// the request line a stand-in site received.
//
// Zero-dep, runs with `node query-params.test.js`.
'use strict';

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { dedupePatterns } = require(path.join(__dirname, 'generate-wrapper.js'));
const {
    collectQueryParameters, collectDroppedQueryNames, loadScrubTables, scrubReasonFor, looksLikeCredentialName,
    assignIdentifiers, escapeDataString, wireQuery,
} = require(path.join(__dirname, 'query-params.js'));
// The scrubber's own fakes, produced by the scrubber: recognising them must not
// drift from what it emits.
const sanitize = require(path.join(__dirname, '..', 'har', 'sanitize-har.js'));
const pii = require(path.join(__dirname, '..', 'har', 'pii.js'));
const harBlunt = require(path.join(__dirname, '..', 'har', 'har-blunt.js'));

const STARTED = '2026-11-18T07:29:53.187Z';
const STARTED_MS = Date.parse(STARTED);

function har(url, startedDateTime) {
    const entry = { request: { method: 'GET', url } };
    if (startedDateTime !== undefined) entry.startedDateTime = startedDateTime;
    return { entry };
}

/** The query parameters of ONE endpoint, through the generator's own de-duplication. */
function parametersOf(urls, options) {
    const entries = urls.map((u) => (typeof u === 'string' ? har(u) : har(u.url, u.started)));
    const patterns = dedupePatterns(entries);
    assert.strictEqual(patterns.length, 1, 'the test URLs must be one endpoint: ' +
        JSON.stringify(patterns.map((p) => p.segments)));
    return collectQueryParameters(patterns[0].samples, options);
}

function byWireName(parameters) {
    return Object.fromEntries(parameters.map((p) => [p.wireName, p]));
}

const SITE = 'https://api.example.com/api/search';

// ---- Collected across every call of the endpoint, in first-seen order ----
{
    const parameters = parametersOf([
        SITE + '?limit=11&prefix=w',
        SITE + '?limit=11&prefix=wi&cursor=abc',
        SITE + '?sort=asc&limit=11&prefix=wir',
    ]);
    assert.deepStrictEqual(parameters.map((p) => p.wireName), ['limit', 'prefix', 'cursor', 'sort'],
        'the first call sets the order; what a later call adds comes after');
    for (const p of parameters) assert.strictEqual(p.calls, 3);
    assert.strictEqual(byWireName(parameters).cursor.sentIn, 1);
    console.log('PASS: parameters are collected across all calls of an endpoint, in first-seen order.');
}

// ---- An endpoint with no query has no parameters ----
{
    assert.deepStrictEqual(parametersOf([SITE, SITE]), []);
    assert.deepStrictEqual(collectQueryParameters([{ entry: { request: {} } }]), [],
        'a sample that carries no parsed query is not an error');
    console.log('PASS: an endpoint called without a query has no query parameters.');
}

// ---- Constant: sent as captured. Varying: left to the caller, with examples ----
{
    const q = byWireName(parametersOf([
        SITE + '?limit=11&prefix=w&page=1',
        SITE + '?limit=11&prefix=wi&page=2',
        SITE + '?limit=11&prefix=wir&page=3',
        SITE + '?limit=11&prefix=wire&page=4',
    ]));
    assert.strictEqual(q.limit.kind, 'constant');
    assert.deepStrictEqual(q.limit.defaults, ['11'], 'what every call sent is the default');
    assert.strictEqual(q.limit.repeated, false);

    assert.strictEqual(q.prefix.kind, 'varying');
    assert.strictEqual(q.prefix.defaults, null, 'a varying parameter has no default');
    assert.deepStrictEqual(q.prefix.examples, ['w', 'wi', 'wir'], 'a few observed values, in the order seen');

    // A paging number differs on every call and is NOT a nonce: it is short.
    assert.strictEqual(q.page.kind, 'varying');
    assert.deepStrictEqual(q.page.examples, ['1', '2', '3']);
    console.log('PASS: a constant is the default; a varying parameter has none, and carries examples.');
}

// ---- A parameter some calls left out is varying, not constant ----
{
    const q = byWireName(parametersOf([
        SITE + '?limit=11&filter=new',
        SITE + '?limit=11',
        SITE + '?limit=11&filter=new',
    ]));
    assert.strictEqual(q.filter.kind, 'varying', 'leaving it out was observed to work');
    assert.strictEqual(q.filter.defaults, null);
    assert.strictEqual(q.filter.sentIn, 2);
    console.log('PASS: a parameter absent from some calls is not a constant.');
}

// ---- Repeated parameters keep every value, and are compared call by call ----
{
    const q = byWireName(parametersOf([
        SITE + '?type=KEYWORD&type=WIDGET&tag=a&tag=b',
        SITE + '?type=KEYWORD&type=WIDGET&tag=b',
        SITE + '?type=KEYWORD&type=WIDGET&tag=c&tag=d',
    ]));
    assert.strictEqual(q.type.repeated, true);
    assert.strictEqual(q.type.kind, 'constant', 'the same two values in every call are a constant, not a variation');
    assert.deepStrictEqual(q.type.defaults, ['KEYWORD', 'WIDGET'], 'both values, in the order sent');

    assert.strictEqual(q.tag.repeated, true, 'repeated in some calls is repeated');
    assert.strictEqual(q.tag.kind, 'varying');
    assert.deepStrictEqual(q.tag.examples, ['a', 'b', 'c']);
    console.log('PASS: a=1&a=2 is one repeated parameter, constant when every call repeats it the same way.');
}

// ---- Empty values are values ----
{
    const q = byWireName(parametersOf([SITE + '?wc=&limit=1', SITE + '?wc=&limit=1']));
    assert.strictEqual(q.wc.kind, 'constant');
    assert.deepStrictEqual(q.wc.defaults, [''], 'an empty value is sent as captured: wc=');
    console.log('PASS: an empty value is kept, and sent, as an empty value.');
}

// ---- Values are decoded once, the way a server reads them ----
{
    const q = byWireName(parametersOf([
        SITE + '?q=wire+cutter&x=caf%C3%A9&y=50%25+off&z=a%26b%3Dc&q%5B0%5D=first',
    ]));
    assert.deepStrictEqual(q.q.defaults, ['wire cutter'], '+ is a space in a query');
    assert.deepStrictEqual(q.x.defaults, ['café']);
    assert.deepStrictEqual(q.y.defaults, ['50% off']);
    assert.deepStrictEqual(q.z.defaults, ['a&b=c']);
    assert.ok(q['q[0]'], 'a percent-encoded NAME is decoded too: ' + Object.keys(q).join(', '));
    console.log('PASS: captured names and values are percent-decoded, with + read as a space.');
}

// ---- One captured call: every parameter defaults to what was captured ----
{
    const q = byWireName(parametersOf([SITE + '?alias=aps&lop=en_US&empty=&type=A&type=B']));
    for (const name of ['alias', 'lop', 'empty', 'type']) {
        assert.strictEqual(q[name].kind, 'single', name);
        assert.strictEqual(q[name].calls, 1);
    }
    assert.deepStrictEqual(q.alias.defaults, ['aps']);
    assert.deepStrictEqual(q.empty.defaults, ['']);
    assert.deepStrictEqual(q.type.defaults, ['A', 'B']);
    console.log('PASS: with one captured call, every parameter is optional with the captured value as its default.');
}

// ---- Scrubbed: the scrubber's own fakes are recognised, and never a default ----
{
    const salt = 'query-params-test-salt';
    const fakes = {
        // sanitize-har.js fakeFor(): what the key-name and shape passes write.
        'legacy cookie': sanitize.fakeFor('cookie', 'the original', salt),
        'legacy field': sanitize.fakeFor('field', 'the original', salt),
        'legacy jwt': sanitize.fakeFor('jwt', 'the original', salt),
        'legacy hex64': sanitize.fakeFor('hex64', 'the original', salt),
        'legacy hex32': sanitize.fakeFor('hex32', 'the original', salt),
        'legacy email': sanitize.fakeFor('email', 'the original', salt),
        'legacy bearer': sanitize.fakeFor('bearer', 'the original', salt),
        'legacy uuid': sanitize.fakeFor('uuid', 'the original', salt),
        'legacy upload handle': sanitize.fakeFor('upload-handle', 'the original', salt),
        'legacy password envelope': sanitize.fakeFor('pwd-envelope', 'the original', salt),
        'legacy default': sanitize.fakeFor('anything-else', 'the original', salt),
        // pii.js fakeFor(): the typed fakes that carry a marker of their own.
        'pii email': pii.fakeFor('email', 'the original'),
        'pii phone': pii.fakeFor('phone', 'the original'),
        'pii ssn': pii.fakeFor('ssn', 'the original'),
        'pii iban': pii.fakeFor('iban', 'the original'),
        'pii mac': pii.fakeFor('mac-address', 'the original'),
        'pii device id': pii.fakeFor('device-id', 'the original'),
        'pii card': pii.fakeFor('credit-card', 'the original'),
        'pii ip': pii.fakeFor('ip-address', 'the original'),
        'pii default': pii.fakeFor('anything-else', 'the original'),
        // har-blunt.js: the floor.
        'blunted': harBlunt.sentinelFor('jwt', 'the original', salt),
        // The operator's literal -> sentinel map (.har-profile.json).
        'profile sentinel': '<UserEmail>',
        'sign-in sentinel': '<SignInCredential>',
        'sentinel inside a larger value': '/users/<AccountId>/orders',
    };
    for (const [what, fake] of Object.entries(fakes)) {
        assert.ok(typeof fake === 'string' && fake.length > 0, what + ' must be a fake to test with');
        // Constant across three calls: without recognition it would be baked in.
        const url = SITE + '?limit=11&secret=' + encodeURIComponent(fake);
        const q = byWireName(parametersOf([url, url, url]));
        assert.strictEqual(q.secret.kind, 'scrubbed', what + ' must be recognised: ' + fake);
        assert.strictEqual(q.secret.defaults, null, what + ' must never be a default');
        assert.deepStrictEqual(q.secret.examples, [], what + ' must never be quoted as an example');
        assert.strictEqual(q.limit.kind, 'constant', 'and nothing else is touched');
    }
    console.log('PASS: every fake the scrubber emits is recognised in a query value, and never baked in (' +
        Object.keys(fakes).length + ' kinds).');
}

// ---- Scrubbed: one replaced value is enough, also on a single call ----
{
    const single = byWireName(parametersOf([SITE + '?alias=aps&session-id=redacted-3f9a1c0b7e2d4a68']));
    assert.strictEqual(single['session-id'].kind, 'scrubbed', 'scrubbed wins over the single-call default');
    assert.strictEqual(single['session-id'].defaults, null);
    assert.strictEqual(single.alias.kind, 'single');

    const mixed = byWireName(parametersOf([SITE + '?who=alice', SITE + '?who=%3CUserName%3E', SITE + '?who=bob']));
    assert.strictEqual(mixed.who.kind, 'scrubbed', 'a parameter the scrub touched once is one it would touch again');
    assert.deepStrictEqual(mixed.who.examples, []);
    console.log('PASS: scrubbed outranks every other rule.');
}

// ---- Scrubbed by NAME: a name the scrub policy calls a secret is never baked in ----
{
    // Not scrub-shaped at all: on a capture that was never scrubbed these are live.
    const url = SITE + '?fb_dtsg=NAdQx3kLr9WpT2vY&lsd=AbCdEf12&limit=11';
    const q = byWireName(parametersOf([url, url]));
    assert.strictEqual(q.fb_dtsg.kind, 'scrubbed');
    assert.strictEqual(q.lsd.kind, 'scrubbed');
    assert.strictEqual(q.fb_dtsg.defaults, null);
    assert.strictEqual(scrubReasonFor('lsd', 'AbCdEf12'), 'secret-name');
    assert.strictEqual(scrubReasonFor('limit', '11'), null);
    // Even an empty one: a default of "" would send `csrftoken=` on every call.
    const empty = SITE + '?csrftoken=&limit=11';
    assert.strictEqual(byWireName(parametersOf([empty, empty])).csrftoken.kind, 'scrubbed');
    assert.strictEqual(byWireName(parametersOf([empty])).csrftoken.defaults, null, 'and on a single call');
    console.log('PASS: a parameter whose name is a known secret is never baked in, whatever its value looks like.');
}

// ---- Scrubbed by SHAPE: what the scrub always replaces is never baked in ----
{
    const shaped = {
        uuid: '7c9e6679-7425-40de-944b-e07fc1f90ae7',
        hex32: '0123456789abcdef0123456789abcdef',
        jwt: 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk',
        email: 'someone@example.org',
    };
    for (const [what, value] of Object.entries(shaped)) {
        const url = SITE + '?v=' + encodeURIComponent(value);
        assert.strictEqual(byWireName(parametersOf([url, url])).v.kind, 'scrubbed', what);
    }
    console.log('PASS: a UUID, a long hex run, a JWT or an email in a query is never baked in.');
}

// ---- Nonce: a different long number or opaque token on every call ----
{
    const q = byWireName(parametersOf([
        SITE + '?rshVal=4821937465021&tok=Zk3Qw9Zt5Vb7Xn2M4Pd8R&page=1&offset=0&term=wirelessheadphones',
        SITE + '?rshVal=4821937466377&tok=Jh7Lm2Np4Qr6St8Uv0Wx1&page=2&offset=20&term=mechanicalkeyboards',
        SITE + '?rshVal=4821937467910&tok=Bc5De7Fg9Hi1Jk3Lm5No7&page=3&offset=40&term=noisecancellingbuds',
    ]));
    assert.strictEqual(q.rshVal.kind, 'nonce');
    assert.strictEqual(q.rshVal.defaults, null, 'a nonce is never a default');
    assert.deepStrictEqual(q.rshVal.examples, [], 'and a stale one is not an example');
    assert.strictEqual(q.tok.kind, 'nonce', 'an opaque token that differs on every call');
    // Short numbers that differ on every call are paging, not nonces.
    assert.strictEqual(q.page.kind, 'varying');
    assert.strictEqual(q.offset.kind, 'varying');
    assert.strictEqual(q.term.kind, 'varying', 'a long word is a search term, not a token');
    console.log('PASS: a long digit run or opaque token that differs on every call is a nonce; paging is not.');
}

// ---- Nonce: a timestamp of the call itself, even when captured once ----
{
    const stamp = String(STARTED_MS - 14000);
    const seconds = String(Math.floor(STARTED_MS / 1000));
    const q = byWireName(parametersOf([
        { url: SITE + '?alias=aps&_=' + stamp + '&since=' + seconds + '&order=1794000000000', started: STARTED },
    ]));
    assert.strictEqual(q._.kind, 'nonce', 'a 13-digit value 14 s before the call is its timestamp');
    assert.strictEqual(q._.defaults, null);
    assert.strictEqual(q.since.kind, 'nonce', 'a 10-digit value is one in seconds');
    assert.strictEqual(q.order.kind, 'single', 'a 13-digit number far from the call is an id, and one call is one call');
    assert.deepStrictEqual(q.order.defaults, ['1794000000000']);
    assert.strictEqual(q.alias.kind, 'single');

    // The same on several calls, each stamped with its own time: still a nonce.
    const many = byWireName(parametersOf([0, 700, 1400].map((offset) => ({
        url: SITE + '?limit=11&_=' + (STARTED_MS + offset - 40),
        started: new Date(STARTED_MS + offset).toISOString(),
    }))));
    assert.strictEqual(many._.kind, 'nonce');
    assert.strictEqual(many.limit.kind, 'constant');
    console.log('PASS: a value that is the time of its own call is never baked in, even on an endpoint captured once.');
}

// ---- Nonce: a conventional cache-buster name, when the capture has no times ----
{
    const q = byWireName(parametersOf([SITE + '?alias=aps&_=1794986993187&nocache=0.4821937465021&t=12']));
    assert.strictEqual(q._.kind, 'nonce');
    assert.strictEqual(q.nocache.kind, 'nonce');
    assert.strictEqual(q.t.kind, 'single', 'a short value under a cache-buster name is just a value');
    console.log('PASS: a long number under a cache-buster name is a nonce without any other evidence.');
}

// ---- Substitution tables: fakes that carry no marker ----
{
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'query-params-tables-'));
    try {
        const harPath = path.join(dir, 'scrubbed.har');
        fs.writeFileSync(harPath, '{}');
        const tables = path.join(dir, '.har-captures');
        fs.mkdirSync(tables);
        // Keyed by the plaintext originals. The KEYS must never be consulted.
        fs.writeFileSync(path.join(tables, '.har-substitutions.json'), JSON.stringify({
            'field:token:the-plaintext-original': 'fakevalue-with-no-marker-at-all',
            'bearer:the-plaintext-bearer': 'Bearer 0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c',
        }));
        fs.writeFileSync(path.join(tables, '.substitutions.json'), JSON.stringify({
            version: 1,
            createdAt: '2026-01-01T00:00:00.000Z',
            substitutions: [
                { type: 'city', originalHash: 'f93f27a2', replacement: 'Cresthollow',
                    locations: [{ entryIndex: 0, queryParam: 'city' }] },
                { type: 'country', originalHash: '9b202ecb', replacement: 'ZZ',
                    locations: [{ entryIndex: 0, jsonPath: 'request.url.query.country' }] },
                { type: 'geo-coordinates', originalHash: '00000000', replacement: 0,
                    locations: [{ entryIndex: 0, jsonPath: 'response.content.lat' }] },
                { type: 'person-name', originalHash: '11111111', replacement: 'Marin Okafor',
                    locations: [{ entryIndex: 0, jsonPath: 'response.content.name' }] },
            ],
        }));

        const scrub = loadScrubTables([harPath]);
        assert.strictEqual(scrub.sources.length, 2, 'both tables are found beside the capture');
        assert.deepStrictEqual(scrub.problems, []);

        const url = SITE +
            '?a=fakevalue-with-no-marker-at-all' +              // a legacy fake, by value
            '&b=0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c' +     // a bearer fake without its scheme
            '&city=Cresthollow' +                                // a typed fake, where the table says it was put
            '&country=ZZ' +                                      // short, but the table names this parameter
            '&who=Marin+Okafor' +                                // a typed fake that travelled to another place
            '&page=0' +                                          // equals a short typed fake: NOT scrubbed
            '&lang=ZZ' +                                         // likewise, under a name the table does not give
            '&k=the-plaintext-original';                         // equals a table KEY: keys are never consulted
        const without = byWireName(parametersOf([url, url]));
        for (const name of ['a', 'b', 'city', 'country', 'who', 'page', 'lang', 'k']) {
            assert.strictEqual(without[name].kind, 'constant', name + ' has no marker, so without the tables it is a constant');
        }

        const q = byWireName(parametersOf([url, url], { scrub }));
        for (const name of ['a', 'b', 'city', 'country', 'who']) {
            assert.strictEqual(q[name].kind, 'scrubbed', name + ' is in a substitution table');
            assert.strictEqual(q[name].defaults, null);
        }
        assert.strictEqual(q.page.kind, 'constant', 'a short fake is matched only where the table says it was put');
        assert.strictEqual(q.lang.kind, 'constant');
        assert.strictEqual(q.k.kind, 'constant', 'a table KEY is a plaintext original and is never read');

        // Beside the capture itself works too.
        fs.renameSync(path.join(tables, '.har-substitutions.json'), path.join(dir, '.har-substitutions.json'));
        fs.renameSync(path.join(tables, '.substitutions.json'), path.join(dir, '.substitutions.json'));
        assert.strictEqual(loadScrubTables([harPath]).sources.length, 2);

        // A table that cannot be read is said, and is not an error.
        fs.writeFileSync(path.join(dir, '.har-substitutions.json'), '{ not json');
        const broken = loadScrubTables([harPath]);
        assert.strictEqual(broken.sources.length, 1);
        assert.strictEqual(broken.problems.length, 1);
        assert.ok(broken.problems[0].includes('.har-substitutions.json'), broken.problems[0]);
        assert.ok(!broken.problems[0].includes('not json'), 'the report names the file and never its contents');
    } finally {
        fs.rmSync(dir, { recursive: true, force: true });
    }
    console.log('PASS: the substitution tables beside a capture identify fakes that carry no marker; their keys are never read.');
}

// ---- Where the tables are looked for is reported, found or not ----
{
    // Nothing found is not an error -- and it is not silence either. The
    // caller is told every directory that was searched, so it can say so: a
    // fake that carries no marker is otherwise taken for a constant.
    const lone = path.join(os.tmpdir(), 'no-such-dir-' + process.pid, 'x.har');
    const none = loadScrubTables([lone]);
    assert.deepStrictEqual(none.sources, []);
    assert.deepStrictEqual(none.problems, []);
    assert.strictEqual(none.searched.length, 1, 'one record per capture given');
    assert.strictEqual(none.searched[0].har, path.resolve(lone));
    assert.deepStrictEqual(none.searched[0].found, []);
    assert.deepStrictEqual(none.searched[0].directories, [
        path.join(path.dirname(path.resolve(lone)), '.har-captures'),
        path.dirname(path.resolve(lone)),
    ], 'beside the capture: its .har-captures folder, then the folder itself');

    // The documented standalone layout: the scrub reads samples/har-original/x.har,
    // writes samples/har/x.har, and puts its tables beside its INPUT -- in
    // samples/har-original/.har-captures/. The generator is given the output.
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'query-params-layout-'));
    try {
        const scrubbed = path.join(dir, 'samples', 'har', 'x.har');
        const beside = path.join(dir, 'samples', 'har-original', '.har-captures');
        fs.mkdirSync(path.dirname(scrubbed), { recursive: true });
        fs.mkdirSync(beside, { recursive: true });
        fs.writeFileSync(scrubbed, '{}');
        fs.writeFileSync(path.join(beside, '.har-substitutions.json'), JSON.stringify({ 'field:k:original': 'fakevalue-with-no-marker-at-all' }));
        fs.writeFileSync(path.join(beside, '.substitutions.json'), JSON.stringify({ version: 1, substitutions: [] }));

        const found = loadScrubTables([scrubbed]);
        assert.deepStrictEqual(found.searched[0].directories, [
            path.join(dir, 'samples', 'har', '.har-captures'),
            path.join(dir, 'samples', 'har'),
            beside,
        ], 'and where the scrub puts them when the capture is its conventional output');
        assert.deepStrictEqual(found.searched[0].found, [
            path.join(beside, '.har-substitutions.json'),
            path.join(beside, '.substitutions.json'),
        ]);
        assert.ok(found.values.has('fakevalue-with-no-marker-at-all'), 'and they are read');
    } finally {
        fs.rmSync(dir, { recursive: true, force: true });
    }
    console.log('PASS: the directories searched for the tables are reported, and the scrub\'s own standalone layout is one of them.');
}

// ---- A table that is JSON but not a table is said, and never crashes the run ----
{
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'query-params-shapes-'));
    try {
        const harPath = path.join(dir, 'scrubbed.har');
        fs.writeFileSync(harPath, '{}');
        const legacy = path.join(dir, '.har-substitutions.json');
        const typed = path.join(dir, '.substitutions.json');
        const SECRET = 'the-content-that-must-not-be-echoed';

        const wrongLegacy = [[SECRET], SECRET, 42, null, true];
        for (const shape of wrongLegacy) {
            fs.writeFileSync(legacy, JSON.stringify(shape));
            fs.rmSync(typed, { force: true });
            const scrub = loadScrubTables([harPath]);
            assert.strictEqual(scrub.values.size, 0, 'nothing is read from ' + JSON.stringify(shape));
            assert.strictEqual(scrub.problems.length, 1, 'and it is reported: ' + JSON.stringify(shape));
            assert.ok(scrub.problems[0].includes('.har-substitutions.json'), scrub.problems[0]);
            assert.ok(!scrub.problems[0].includes(SECRET), 'without its content: ' + scrub.problems[0]);
        }
        fs.rmSync(legacy, { force: true });

        const wrongTyped = [
            [SECRET], SECRET, 42, null,
            { substitutions: { replacement: SECRET } },
            { substitutions: 7 },
            { substitutions: SECRET },
        ];
        for (const shape of wrongTyped) {
            fs.writeFileSync(typed, JSON.stringify(shape));
            const scrub = loadScrubTables([harPath]);
            assert.strictEqual(scrub.values.size, 0, 'nothing is read from ' + JSON.stringify(shape));
            assert.strictEqual(scrub.problems.length, 1, 'and it is reported: ' + JSON.stringify(shape));
            assert.ok(scrub.problems[0].includes('.substitutions.json'), scrub.problems[0]);
            assert.ok(!scrub.problems[0].includes(SECRET), 'without its content: ' + scrub.problems[0]);
        }

        // Entries of the wrong shape inside a table of the right one: the good
        // ones are read, the others are counted, and nothing throws.
        fs.writeFileSync(typed, JSON.stringify({ version: 1, substitutions: [
            null, 5, SECRET, [SECRET],
            { type: 'city', replacement: 'Cresthollow', locations: 7 },
            { type: 'city', replacement: 'Marrowgate', locations: [null, 5, { queryParam: 9 }, { jsonPath: {} }] },
            { type: 'city', locations: [] },
        ] }));
        fs.writeFileSync(legacy, JSON.stringify({ 'k:one': 'fakevalue-number-one', 'k:two': 5, 'k:three': null, 'k:four': { nested: SECRET } }));
        const mixed = loadScrubTables([harPath]);
        assert.ok(mixed.values.has('Cresthollow') && mixed.values.has('Marrowgate') && mixed.values.has('fakevalue-number-one'));
        assert.strictEqual(mixed.sources.length, 2);
        assert.strictEqual(mixed.problems.length, 2, 'one line per table: ' + mixed.problems.join(' | '));
        for (const problem of mixed.problems) assert.ok(!problem.includes(SECRET), problem);
        assert.ok(mixed.problems.some((p) => p.includes('.har-substitutions.json') && /\b3 entr/.test(p)), mixed.problems.join(' | '));
        assert.ok(mixed.problems.some((p) => p.includes('.substitutions.json') && /\b5 entr/.test(p)), mixed.problems.join(' | '));
    } finally {
        fs.rmSync(dir, { recursive: true, force: true });
    }
    console.log('PASS: a table of the wrong shape is reported by its shape, never by its content, and never crashes.');
}

// ---- The project's own scrub policy names secrets too ----
{
    // The scrub finds .har-policy.project.json by walking up from the capture it
    // is given. So does this: a name the project added is never baked in, and a
    // default name the project vetoed is no longer treated as a secret.
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'query-params-policy-'));
    try {
        const harPath = path.join(dir, 'captures', 'x.har');
        fs.mkdirSync(path.dirname(harPath), { recursive: true });
        fs.writeFileSync(harPath, '{}');
        const url = SITE + '?tenantCode=acme-eu-7&lsd=AbCdEf12&limit=11';

        const byDefault = byWireName(parametersOf([url, url], { scrub: loadScrubTables([harPath]) }));
        assert.strictEqual(byDefault.tenantCode.kind, 'constant', 'not a secret by the shipped policy');
        assert.strictEqual(byDefault.lsd.kind, 'scrubbed', 'lsd is one of the shipped names');

        fs.writeFileSync(path.join(dir, '.har-policy.project.json'),
            JSON.stringify({ secretFields: ['tenantCode'], notSecretFields: ['lsd'] }));
        const scrub = loadScrubTables([harPath]);
        assert.deepStrictEqual(scrub.problems, []);
        const q = byWireName(parametersOf([url, url], { scrub }));
        assert.strictEqual(q.tenantCode.kind, 'scrubbed', 'a name the project added');
        assert.strictEqual(q.tenantCode.reason, 'secret-name');
        assert.strictEqual(q.tenantCode.defaults, null);
        assert.strictEqual(q.lsd.kind, 'constant', 'a default name the project vetoed is the project\'s call');
        assert.strictEqual(q.limit.kind, 'constant');

        // A project policy that cannot be loaded is said, and the shipped names still apply.
        fs.writeFileSync(path.join(dir, '.har-policy.project.json'), '{ "secretFields": "not-a-list-and-not-echoed" }');
        const broken = loadScrubTables([harPath]);
        assert.strictEqual(broken.problems.length, 1, broken.problems.join(' | '));
        assert.ok(broken.problems[0].includes('.har-policy.project.json'), broken.problems[0]);
        assert.ok(!broken.problems[0].includes('not-a-list-and-not-echoed'), broken.problems[0]);
        assert.strictEqual(byWireName(parametersOf([url, url], { scrub: broken })).lsd.kind, 'scrubbed');
    } finally {
        fs.rmSync(dir, { recursive: true, force: true });
    }
    console.log('PASS: the project\'s own scrub policy, found the way the scrub finds it, decides which names are secrets.');
}

// ---- A NAME is screened like a value ----
{
    // `url + '?' + Date.now()` puts a timestamp where a name goes, and a scrub
    // rewrites a name as readily as a value. Neither is a parameter: it is not
    // emitted at all, and what was dropped is counted by kind, never named.
    const calls = [0, 700, 1400].map((offset) => ({
        url: SITE + '?limit=11&' + (STARTED_MS + offset - 40) + '&alice%40example.org&debug&0.4821937465' + offset,
        started: new Date(STARTED_MS + offset).toISOString(),
    }));
    assert.deepStrictEqual(parametersOf(calls).map((p) => p.wireName), ['limit', 'debug'],
        'the junk names are gone; a valueless flag is still a parameter');
    const patterns = dedupePatterns(calls.map((c) => har(c.url, c.started)));
    assert.deepStrictEqual(collectDroppedQueryNames(patterns[0].samples), [
        { reason: 'nonce-name', names: 6, calls: 3 },
        { reason: 'secret-shape', names: 1, calls: 3 },
    ], 'three timestamps and three long numbers, one per call each; one email in every call');

    const fakeEmail = sanitize.fakeFor('email', 'someone@corp.example', 'salt');
    const jwt = 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk';
    const dropped = (name, options) => {
        const url = SITE + '?limit=11&' + encodeURIComponent(name);
        const p = dedupePatterns([har(url), har(url)]);
        assert.deepStrictEqual(collectQueryParameters(p[0].samples, options).map((q) => q.wireName), ['limit'], name + ' must not be a parameter');
        const d = collectDroppedQueryNames(p[0].samples, options);
        assert.strictEqual(d.length, 1, name);
        return d[0].reason;
    };
    assert.strictEqual(dropped(fakeEmail), 'scrub-marker', 'a name the scrub rewrote to its fake');
    assert.strictEqual(dropped('<AccountId>'), 'scrub-marker');
    assert.strictEqual(dropped(jwt), 'secret-shape');
    assert.strictEqual(dropped('7c9e6679-7425-40de-944b-e07fc1f90ae7'), 'secret-shape');
    assert.strictEqual(dropped('1794986993147'), 'nonce-name', 'a long digit run, with no call time to compare it with');
    assert.strictEqual(dropped('fakename-with-no-marker', { scrub: { values: new Set(['fakename-with-no-marker']), namedValues: new Set() } }),
        'scrub-table', 'a name the substitution table lists as a replacement');

    // What stays a parameter: a short number, a version, a word, an empty name.
    const kept = parametersOf([SITE + '?2024=a&v2=b&1234567=c&page=1&=d', SITE + '?2024=a&v2=b&1234567=c&page=2&=d']);
    assert.deepStrictEqual(kept.map((p) => p.wireName), ['2024', 'v2', '1234567', 'page', '']);
    console.log('PASS: a timestamp, a scrub fake or a secret shape used as a NAME is not a parameter, and is counted without being named.');
}

// ---- What the leak gate would report is never baked in, scrubbed capture or not ----
{
    // On a capture nobody scrubbed these are the real thing. The gate's own
    // predicate decides -- the pattern WITH its validator -- so a look-alike
    // stays a value.
    const real = {
        'a phone number': '+14255550123',
        'a social security number': '123-45-6789',
        'a card number': '4111111111111111',
        'an IBAN': 'GB82WEST12345698765432',
        'a bearer token': 'Bearer abcdefghijklmnopqrstuvwxyz012345',
        'a MAC address': '00:1A:2B:3C:4D:5E',
        'a password envelope': '#PWD_BROWSER:5:1794986993:AbCdEfGhIjKlMnOp',
    };
    for (const [what, value] of Object.entries(real)) {
        const url = SITE + '?v=' + encodeURIComponent(value);
        const q = byWireName(parametersOf([url, url])).v;
        assert.strictEqual(q.kind, 'scrubbed', what + ' must not be a default: ' + value);
        assert.strictEqual(q.reason, 'secret-shape', what);
        assert.strictEqual(q.defaults, null, what);
        // Varying, it must not be quoted as an example either.
        const other = SITE + '?v=other-' + encodeURIComponent(value);
        assert.deepStrictEqual(byWireName(parametersOf([url, other])).v.examples, [], what + ' must not be an example');
        // And once is enough: an endpoint captured a single time.
        assert.strictEqual(byWireName(parametersOf([url])).v.defaults, null, what + ', on a single call');
    }
    const lookAlikes = {
        'a locale (the raw MAC pattern matches it)': 'de-DE',
        'three hex pairs': '12-34-56',
        'a 13-digit id (the raw card pattern matches it)': '1794000000000',
        'a 16-digit number that fails the card checksum': '4111111111111112',
        'a 16-digit number no issuer mints': '9999999999999995',
        'a date': '2026-11-18',
        'a short hex word': 'deadbeef',
        'an IBAN-shaped code with a wrong checksum': 'GB00WEST12345698765432',
        'the word Bearer': 'Bearer',
        'a local phone number without its +': '4255550123',
    };
    for (const [what, value] of Object.entries(lookAlikes)) {
        const url = SITE + '?v=' + encodeURIComponent(value);
        const q = byWireName(parametersOf([url, url])).v;
        assert.strictEqual(q.kind, 'constant', what + ' is an ordinary value: ' + value);
        assert.deepStrictEqual(q.defaults, [value], what);
    }
    console.log('PASS: a value the leak gate itself would report is never a default or an example (' +
        Object.keys(real).length + ' shapes), and ' + Object.keys(lookAlikes).length + ' look-alikes stay values.');
}

// ---- A NAME that says "credential" is never baked in, whatever the value ----
{
    // Words are split the way the tool emitter splits a path segment: at
    // camelCase boundaries and at anything that is not a letter or a digit.
    // A whole word must match -- so `key` is one and `keyword` is not.
    const credential = ['key', 'api_key', 'apiKey', 'apikey', 'APIKey', 'sid', 'session', 'session-id', 'sessionId', 'sig',
        'signature', 'secret', 'client-secret', 'auth', 'authToken', 'token', 'access_token', 'credential', 'password',
        'X-Amz-Credential', 'X-Amz-Signature', 'X-Amz-Security-Token', 'X-Goog-Signature', 'X-Goog-Credential',
        'Key-Pair-Id', 'AWSAccessKeyId', 'Signature',
        // Run together or abbreviated, so no split finds the word inside.
        'pwd', 'passwd', 'jwt', 'otp', 'accesstoken', 'authtoken', 'sessionid', 'PHPSESSID', 'jsessionid'];
    for (const name of credential) {
        assert.strictEqual(looksLikeCredentialName(name), true, name);
        // A plain short value under such a name: no marker, no shape, nothing else to go on.
        const url = SITE + '?limit=11&' + encodeURIComponent(name) + '=plainvalue7';
        const q = byWireName(parametersOf([url, url]))[name];
        assert.strictEqual(q.kind, 'scrubbed', name + ' must never be baked in');
        assert.strictEqual(q.defaults, null, name);
        assert.deepStrictEqual(q.examples, [], name);
        const once = byWireName(parametersOf([url]))[name];
        assert.strictEqual(once.defaults, null, name + ', on a single call');
        const other = SITE + '?limit=11&' + encodeURIComponent(name) + '=othervalue8';
        assert.deepStrictEqual(byWireName(parametersOf([url, other]))[name].examples, [], name + ' must have no examples');
    }
    // The reason says it was the name: the value gave nothing away.
    assert.strictEqual(byWireName(parametersOf([SITE + '?sig=plainvalue7']))['sig'].reason, 'credential-name');
    // A marker in the value is the better reason when there is one.
    assert.strictEqual(byWireName(parametersOf([SITE + '?sig=redacted-3f9a1c0b7e2d4a68']))['sig'].reason, 'scrub-marker');

    const ordinary = ['keyword', 'keywords', 'fieldKeywords', 'side', 'sidebar', 'author', 'authors', 'tokenizer', 'sessions-per-page',
        'sessions', 'sigma', 'design', 'secretary', 'monkey', 'k', 'q', 'limit', 'credentials-page-size', 'passwords',
        // `code` is left ordinary on purpose: a postal, country or promo code is not a credential.
        'code', 'postalCode', 'promoCode'];
    for (const name of ordinary) {
        assert.strictEqual(looksLikeCredentialName(name), false, name);
        const url = SITE + '?limit=11&' + encodeURIComponent(name) + '=plainvalue7';
        assert.strictEqual(byWireName(parametersOf([url, url]))[name].kind, 'constant', name + ' is an ordinary parameter');
    }
    console.log('PASS: a parameter whose name is a credential word is never baked in (' + credential.length +
        ' names), and ' + ordinary.length + ' names that only contain one are ordinary.');
}

// ---- A value that merely looks like markup is not a scrub replacement ----
{
    // Inside a larger value only the scrubber's own sentinel spellings count.
    // An HTML-like value is a value: it is the default, and reaches the source.
    const markup = ['<summary>&amp;</param>', '<b>bold</b> text', 'a <BR> b', 'x < y > z', '1 <br/> 2', 'if (a<b && c>d)'];
    for (const value of markup) {
        const url = SITE + '?v=' + encodeURIComponent(value);
        const q = byWireName(parametersOf([url, url])).v;
        assert.strictEqual(q.kind, 'constant', 'markup is not a marker: ' + value);
        assert.deepStrictEqual(q.defaults, [value]);
    }
    const sentinels = ['/users/<AccountId>/orders', 'Hello <DisplayName>!', 'x <REDACTED-1a2b3c4d> y', 'x <BLUNTED:jwt:ABCD> y',
        'key=redacted-3f9a1c0b7e2d4a68;', '<SignInCredential> and more'];
    for (const value of sentinels) {
        const url = SITE + '?v=' + encodeURIComponent(value);
        const q = byWireName(parametersOf([url, url])).v;
        assert.strictEqual(q.kind, 'scrubbed', 'a sentinel inside a value is still found: ' + value);
        assert.strictEqual(q.reason, 'scrub-marker', value);
    }
    // A value that is NOTHING but an angle-bracket placeholder is still one,
    // whatever its case: that is the leak gate's own reading of a redaction.
    assert.strictEqual(byWireName(parametersOf([SITE + '?v=%3Cuseremail%3E', SITE + '?v=%3Cuseremail%3E'])).v.kind, 'scrubbed');
    console.log('PASS: markup inside a value is not mistaken for a scrub sentinel; the scrubber\'s own spellings still are.');
}

// ---- Empty in some calls, a timestamp in others: still a nonce ----
{
    const q = byWireName(parametersOf([
        { url: SITE + '?limit=11&_=', started: STARTED },
        { url: SITE + '?limit=11&_=' + (STARTED_MS + 700 - 40), started: new Date(STARTED_MS + 700).toISOString() },
        { url: SITE + '?limit=11&_=' + (STARTED_MS + 1400 - 40), started: new Date(STARTED_MS + 1400).toISOString() },
    ]));
    assert.strictEqual(q._.kind, 'nonce', 'the empty call says nothing about the others');
    assert.deepStrictEqual(q._.examples, [], 'and no timestamp is quoted as an example');
    assert.strictEqual(q._.defaults, null);

    // A neutral name, so neither the cache-buster names nor a long-number rule
    // decides it: only the timestamp in the call that sent one can.
    const stamp = byWireName(parametersOf([
        { url: SITE + '?limit=11&stamp=', started: STARTED },
        { url: SITE + '?limit=11&stamp=' + (STARTED_MS + 700 - 40), started: new Date(STARTED_MS + 700).toISOString() },
    ])).stamp;
    assert.strictEqual(stamp.kind, 'nonce', 'an empty call must not hide the timestamp in the other');
    assert.strictEqual(stamp.reason, 'timestamp');
    assert.deepStrictEqual(stamp.examples, [], 'and the timestamp is not quoted as an example');

    const tokens = byWireName(parametersOf([
        SITE + '?rsh=', SITE + '?rsh=4821937465021', SITE + '?rsh=4821937466377',
    ]));
    assert.strictEqual(tokens.rsh.kind, 'nonce');
    assert.strictEqual(tokens.rsh.reason, 'differs-every-call');
    // Empty every time is just an empty value.
    assert.strictEqual(byWireName(parametersOf([SITE + '?_=', SITE + '?_='])) ._.kind, 'constant');
    console.log('PASS: an empty value in one call does not hide a timestamp or a nonce in the others.');
}

// ---- C# names: valid, unique, and the wire name untouched ----
{
    const names = (wire, reserved) => assignIdentifiers(wire, reserved || []).map((n) => n.csName);
    assert.deepStrictEqual(names(['limit', 'page-size', 'last_prefix', 'filter.name', 'q[0]', 'SearchAlias', 'URLPath', 'ID']),
        ['limit', 'pageSize', 'lastPrefix', 'filterName', 'q0', 'searchAlias', 'urlPath', 'id']);
    // Nothing usable in the name at all.
    assert.deepStrictEqual(names(['_', '$', '--']), ['underscore', 'param', 'param2']);
    // A leading digit is not an identifier start.
    assert.deepStrictEqual(names(['2fa', '3d-secure']), ['p2fa', 'p3dSecure']);
    // Non-ASCII letters are dropped: an MCP client may refuse them in a property name.
    assert.deepStrictEqual(names(['größe', '日本']), ['grE', 'param']);
    // Keywords are escaped; the tool property name is the keyword itself.
    const keywords = assignIdentifiers(['class', 'namespace', 'await', 'int', 'string'], []);
    assert.deepStrictEqual(keywords.map((n) => n.csName), ['@class', '@namespace', '@await', '@int', '@string']);
    assert.deepStrictEqual(keywords.map((n) => n.toolName), ['class', 'namespace', 'await', 'int', 'string']);
    // What the method already declares, or its body uses, is taken.
    assert.deepStrictEqual(names(['id', 'body', 'ct', 'client', 'raw', 'envelope', 'wireQuery', 'query'], ['id']),
        ['idQuery', 'bodyQuery', 'ctQuery', 'clientQuery', 'rawQuery', 'envelopeQuery', 'wireQueryQuery', 'query']);
    // Two wire names, one identifier: numbered in first-seen order, and compared without case.
    assert.deepStrictEqual(names(['page-size', 'page_size', 'pageSize', 'PAGESIZE', 'pageSize2']),
        ['pageSize', 'pageSize2', 'pageSize3', 'pagesize4', 'pageSize22']);
    // A collision with the suffix itself still resolves.
    assert.deepStrictEqual(names(['id', 'idQuery', 'id-query'], ['id']), ['idQuery', 'idQuery2', 'idQuery3']);
    // Long names are capped, and still unique.
    const long = assignIdentifiers(['x'.repeat(200), 'x'.repeat(201)], []);
    assert.ok(long.every((n) => n.toolName.length <= 64), 'an MCP property name is at most 64 characters');
    assert.notStrictEqual(long[0].csName, long[1].csName);
    // The wire name is carried through exactly.
    assert.deepStrictEqual(assignIdentifiers(['page-size', 'q[0]', '_'], []).map((n) => n.wireName), ['page-size', 'q[0]', '_']);
    for (const n of assignIdentifiers(['page-size', 'q[0]', '_', 'class', '2fa', 'größe', 'x'.repeat(200)], [])) {
        assert.ok(/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(n.toolName), 'a valid tool property name: ' + n.toolName);
        assert.ok(/^@?[A-Za-z_][A-Za-z0-9_]*$/.test(n.csName), 'a valid C# identifier: ' + n.csName);
    }
    console.log('PASS: wire names map to valid, unique C# and tool parameter names, and stay exact on the wire.');
}

// ---- Identifiers reach the collected parameters, with the method's own names reserved ----
{
    const url = SITE + '?id=7&class=a&page-size=20&page_size=20';
    const q = collectQueryParameters(dedupePatterns([har(url), har(url)])[0].samples, { reserved: ['id'] });
    assert.deepStrictEqual(q.map((p) => p.wireName), ['id', 'class', 'page-size', 'page_size']);
    assert.deepStrictEqual(q.map((p) => p.csName), ['idQuery', '@class', 'pageSize', 'pageSize2']);
    assert.deepStrictEqual(q.map((p) => p.toolName), ['idQuery', 'class', 'pageSize', 'pageSize2']);
    console.log('PASS: a query parameter never takes the name of a path parameter.');
}

// ---- The wire: .NET's Uri.EscapeDataString, exactly ----
{
    const expectations = {
        'wire cutter': 'wire%20cutter',
        'a+b': 'a%2Bb',
        'a&b=c': 'a%26b%3Dc',
        'path/to?x#y': 'path%2Fto%3Fx%23y',
        'café': 'caf%C3%A9',
        '日本': '%E6%97%A5%E6%9C%AC',
        '\u{1F600}': '%F0%9F%98%80',
        "!*'()": '%21%2A%27%28%29',
        'A-Z_a.z~0': 'A-Z_a.z~0',
        '50% off': '50%25%20off',
        'q[0]': 'q%5B0%5D',
        '': '',
        'line\nbreak': 'line%0Abreak',
    };
    for (const [value, escaped] of Object.entries(expectations)) {
        assert.strictEqual(escapeDataString(value), escaped, JSON.stringify(value));
    }
    console.log('PASS: values are escaped the way Uri.EscapeDataString escapes them.');
}

// ---- The wire: order kept, null left out, empty sent, repeats repeated ----
{
    assert.strictEqual(wireQuery([]), '');
    assert.strictEqual(wireQuery([['a', null]]), '', 'nothing to send is no query at all');
    assert.strictEqual(wireQuery([['limit', '11'], ['prefix', null], ['wc', ''], ['type', 'A'], ['type', 'B'], ['q[0]', 'x y']]),
        '?limit=11&wc=&type=A&type=B&q%5B0%5D=x%20y');
    console.log('PASS: the query string keeps its order, leaves null out, sends empty values and repeats repeats.');
}

// ---- Deterministic ----
{
    const urls = [
        SITE + '?limit=11&prefix=w&type=A&type=B&_=1794986993147&session-id=redacted-3f9a1c0b7e2d4a68',
        SITE + '?limit=11&prefix=wi&type=A&type=B&_=1794986993847&session-id=redacted-3f9a1c0b7e2d4a68',
    ];
    assert.deepStrictEqual(parametersOf(urls), parametersOf(urls));
    console.log('PASS: the same calls give the same parameters.');
}

console.log('All query-params tests passed.');
