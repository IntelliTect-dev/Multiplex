#!/usr/bin/env node
// Behavior tests for issue #66: a value enrolled by its FIELD NAME alone is
// never written into a URL's structure.
//
// Zero-dep, runs with `node pii-structure-scrub.test.js`. Exits non-zero on any
// failure.
//
// THE DEFECT. `name` is an exact person-name field, so every `{"name": ...}` in
// a capture -- a tag, an ad slot, an experiment -- enrolled its value as a
// person's name. The replacement pass then wrote a fake name over every
// word-bounded occurrence of that value ANYWHERE in the entry: a hostname
// label, a path segment, a matrix-parameter name, a query-parameter name. A
// fake name holds a space, and a space is a forbidden host code point, so on a
// real signed-out capture 160 of 651 request URLs stopped parsing. Every hit
// was a false positive.
//
// THE RULE PINNED. A person-name, street address, city, region, postal code or
// country has no shape: its detector accepts ANY string at the right field. So
// its own spelling is no evidence anywhere else, and where it appears in
// structure -- a scheme or authority, a bare path segment, a parameter or key
// name -- it is left alone. Payload keeps its reach: the value at its declared
// field, the same value in a query VALUE, in free text, in a JSON value.
//
// No real captured data appears here. Every value is synthetic.

'use strict';

const assert = require('assert');
const path = require('path');
const pii = require(path.join(__dirname, 'pii.js'));

let failures = 0;
function check(name, fn) {
    try {
        fn();
        console.log(`  ok - ${name}`);
    } catch (err) {
        failures++;
        console.error(`  FAIL - ${name}\n    ${err && err.message}`);
    }
}

function entry(url, {
    method = 'GET', headers = [], reqBody, reqMime = 'application/json',
    resBody = '{}', resMime = 'application/json', resHeaders = [], redirectURL = '',
} = {}) {
    return {
        request: {
            method, url, headers, queryString: [], cookies: [],
            ...(reqBody === undefined ? {} : { postData: { mimeType: reqMime, text: reqBody } }),
        },
        response: {
            status: 200, headers: resHeaders, cookies: [], redirectURL,
            content: { mimeType: resMime, text: resBody },
        },
    };
}

function harOf(entries) { return { log: { entries } }; }

/** `new URL(u)`, or null when it throws. */
function parsed(u) { try { return new URL(u); } catch (_) { return null; } }

function paramNames(search) {
    return search.replace(/^\?/, '').split('&').filter(Boolean).map((p) => p.split('=')[0]);
}

// --- the fixture: the four shapes #66 reports, and their neighbours ----------
//
// Each structural word is enrolled exactly as the real capture enrolled it: as
// the value of a `name` field in an ordinary JSON response. The real name sits
// at a declared display-name field and travels on as payload.
const WORD = {
    hostLabel: 'qvx',       // static01.<word>.test           -- a second-level label
    subdomain: 'zeta',      // <word>.et.example.test         -- the first label
    letter: 'g',            // securepubads.<word>.click.test -- a single-letter label
    segment: 'ht',          // /<word>/ixmatch.html           -- a bare path segment
    matrix: 'kw',           // ;<word>=sports                 -- a matrix-parameter name
    query: 'sz',            // ?<word>=300x250                -- a query-parameter name
    key: 'gid',             // {"<word>": ...}                -- a JSON key
};
const PERSON = 'Rosalind Featherstonehaugh';

const CONFIG = JSON.stringify({
    slots: Object.values(WORD).map((w) => ({ name: w })),
    owner: { display_name: PERSON },
    title: `Notes by ${PERSON}`,
});

const VARIABLES = JSON.stringify({ [WORD.key]: '5', input: {}, uri: 'x' });

const URLS = {
    hostLabel: `https://static01.${WORD.hostLabel}.test/assets/app.js`,
    subdomain: `https://${WORD.subdomain}.et.example.test/beacon`,
    letter: `https://securepubads.${WORD.letter}.click.test/gampad/ads`,
    segment: `https://js-sec.example.test/${WORD.segment}/ixmatch.html`,
    matrix: `https://ad.example.test/ddm/activity;${WORD.matrix}=sports;ord=1`,
    query: `https://ad.example.test/pixel?${WORD.query}=300x250&iu=%2F123`,
    flagQuery: `https://ad.example.test/beacon?${WORD.query}=1&debug&who=${encodeURIComponent(PERSON)}`,
    graphqlGet: `https://api.example.test/graphql?operationName=comments&variables=${encodeURIComponent(VARIABLES)}` +
        `&q=${encodeURIComponent(PERSON)}`,
};

function scrubbedFixture() {
    const entries = [
        entry('https://api.example.test/v1/config', { resBody: CONFIG }),
        ...Object.values(URLS).map((u) => entry(u)),
        entry('https://api.example.test/graphql', {
            method: 'POST',
            reqBody: JSON.stringify({ operationName: 'comments', variables: JSON.parse(VARIABLES) }),
        }),
        entry('https://api.example.test/form', {
            method: 'POST', reqMime: 'application/x-www-form-urlencoded',
            reqBody: `${WORD.key}=5&${WORD.query}=2&note=hello`,
        }),
        entry('https://api.example.test/nested', {
            resBody: JSON.stringify({ payload: VARIABLES }),
        }),
        entry(URLS.hostLabel, {
            headers: [
                { name: 'Host', value: `static01.${WORD.hostLabel}.test` },
                { name: ':authority', value: `static01.${WORD.hostLabel}.test` },
                { name: 'Origin', value: `https://${WORD.subdomain}.et.example.test` },
                { name: 'X-Display', value: PERSON },
            ],
        }),
    ];
    const before = JSON.parse(JSON.stringify(entries));
    const har = harOf(entries);
    pii.scrubPii(har);
    return { before, after: har.log.entries };
}

const { before, after } = scrubbedFixture();
const urlAt = (u) => after[before.findIndex((e) => e.request.url === u)].request.url;

console.log('pii-structure-scrub');

// --- 0. the premise: the words really are enrolled as person names -----------

check('premise: `name` is a person-name field, so every word is enrolled', () => {
    assert.strictEqual(pii.fieldTypeFor('name'), 'person-name');
    const slots = JSON.parse(after[0].response.content.text).slots;
    for (const [i, w] of Object.values(WORD).entries()) {
        assert.notStrictEqual(slots[i].name, w,
            `the value at its own declared field must still be replaced (slot ${i})`);
    }
});

// --- 1. every request URL still parses ---------------------------------------

check('every request URL still parses after the scrub', () => {
    const broken = after.filter((e) => parsed(e.request.url) === null).length;
    assert.strictEqual(broken, 0, `${broken} request URL(s) no longer parse`);
});

// --- 2. the authority is structure -------------------------------------------

check('a hostname label, a subdomain and a single-letter label come back byte-identical', () => {
    for (const k of ['hostLabel', 'subdomain', 'letter']) {
        assert.strictEqual(urlAt(URLS[k]), URLS[k], `${k}: the URL was rewritten`);
    }
});

// The text pass reads URLs in running text conservatively and stops at a
// sub-delimiter (`;`, `,`, `'`), where free text usually ends a URL. A request
// URL's authority is known to be one, so it is held as structure whole --
// userinfo included, which RFC 3986 lets carry those characters.
check('a request URL authority is structure whole, userinfo with a sub-delimiter included', () => {
    // `!` before the `@` keeps the userinfo from reading as an email, which is
    // a shaped kind and would be replaced on its own evidence.
    const url = `https://x;${WORD.letter};!@h.example.test/p`;
    assert.ok(parsed(url), 'premise: the userinfo is legal');
    const har = harOf([
        entry('https://api.example.test/v1/config', { resBody: JSON.stringify({ name: WORD.letter }) }),
        entry(url),
    ]);
    pii.scrubPii(har);
    assert.strictEqual(har.log.entries[1].request.url, url);
});

check('Host, :authority and Origin headers keep the hostname', () => {
    const hs = after[after.length - 1].request.headers;
    const byName = Object.fromEntries(hs.map((h) => [h.name, h.value]));
    assert.strictEqual(byName.Host, `static01.${WORD.hostLabel}.test`);
    assert.strictEqual(byName[':authority'], `static01.${WORD.hostLabel}.test`);
    assert.strictEqual(byName.Origin, `https://${WORD.subdomain}.et.example.test`);
});

// --- 3. a bare path segment is not a person's name ----------------------------

check('a bare path segment equal to an enrolled word is left alone', () => {
    assert.strictEqual(urlAt(URLS.segment), URLS.segment);
});

check('a matrix-parameter name is left alone', () => {
    assert.strictEqual(urlAt(URLS.matrix), URLS.matrix);
});

// --- 4. parameter and key NAMES are structure ---------------------------------

check('a query-parameter name is left alone', () => {
    const now = parsed(urlAt(URLS.query));
    assert.ok(now, 'the URL must parse');
    assert.deepStrictEqual(paramNames(now.search), [WORD.query, 'iu']);
    assert.strictEqual(urlAt(URLS.query), URLS.query);
});

check('a query-parameter name beside a flag part is left alone, and its value is still payload', () => {
    const now = parsed(urlAt(URLS.flagQuery));
    assert.ok(now, 'the URL must parse');
    assert.deepStrictEqual([...now.searchParams.keys()], [WORD.query, 'debug', 'who']);
    assert.notStrictEqual(now.searchParams.get('who'), PERSON,
        'a declared name in the VALUE of a flag-part query must still be replaced');
});

check('a form-body parameter name is left alone', () => {
    const form = after.find((e) => e.request.url.endsWith('/form')).request.postData.text;
    assert.deepStrictEqual(paramNames(form), [WORD.key, WORD.query, 'note']);
});

check('JSON keys stay intact in a request body', () => {
    const body = JSON.parse(after.find((e) => e.request.method === 'POST' && e.request.url.endsWith('/graphql'))
        .request.postData.text);
    assert.deepStrictEqual(Object.keys(body.variables), [WORD.key, 'input', 'uri']);
});

check('JSON keys stay intact in a query value', () => {
    const get = parsed(urlAt(URLS.graphqlGet));
    assert.ok(get, 'the GraphQL GET must parse');
    assert.deepStrictEqual(Object.keys(JSON.parse(get.searchParams.get('variables'))), [WORD.key, 'input', 'uri']);
});

// The common GraphQL and analytics shape: a JSON document carried as a STRING
// inside another document. The flat pass reads that string as plain text, so
// it reached the inner document's keys before the nested walk ever parsed it.
check('JSON keys stay intact in a document nested inside a string value', () => {
    const nested = JSON.parse(JSON.parse(after.find((e) => e.request.url.endsWith('/nested'))
        .response.content.text).payload);
    assert.deepStrictEqual(Object.keys(nested), [WORD.key, 'input', 'uri']);
});

// --- 5. payload keeps its reach ----------------------------------------------

check('a real name at a declared field is still replaced, everywhere it is payload', () => {
    const text = JSON.stringify(after);
    assert.ok(!text.includes(PERSON), 'the declared name survived somewhere');
    assert.ok(!text.includes(encodeURIComponent(PERSON)), 'its encoded spelling survived in a query value');
    const get = parsed(urlAt(URLS.graphqlGet));
    assert.notStrictEqual(get.searchParams.get('q'), PERSON, 'the name in a query VALUE must be replaced');
    const hs = after[after.length - 1].request.headers;
    assert.notStrictEqual(hs.find((h) => h.name === 'X-Display').value, PERSON,
        'a header that is not an authority still receives the replacement');
});

// Leaving a nested document's names to the nested walk is safe only where the
// walk goes. Past the shared depth cap it stops, the flat pass is the only
// reach left, and withholding the field-only kinds there would ship the value.
check('past the nested depth cap, a declared name inside a nested document is still replaced', () => {
    let inner = JSON.stringify({ note: PERSON });
    for (let i = 0; i < 10; i++) inner = JSON.stringify({ p: inner });
    const har = harOf([
        entry('https://api.example.test/v1/me', { resBody: JSON.stringify({ display_name: PERSON }) }),
        entry('https://api.example.test/v1/deep', { resBody: inner }),
    ]);
    pii.scrubPii(har);
    assert.ok(!har.log.entries[1].response.content.text.includes(PERSON),
        'the name survived in a document nested past the walk\'s reach');
});

// --- 6. shape evidence is unchanged -------------------------------------------
//
// Only the kinds a field name ALONE vouches for lose their reach into
// structure. A value with a shape carries its own evidence wherever it sits,
// and its fake keeps that shape, so the URL stays well-formed.

check('a shape-detected value in the host is still replaced, and the URL still parses', () => {
    const url = 'http://203.0.113.9/status';
    const har = harOf([entry(url)]);
    pii.scrubPii(har);
    const now = parsed(har.log.entries[0].request.url);
    assert.ok(now, 'the URL must parse');
    assert.notStrictEqual(now.hostname, '203.0.113.9', 'an IPv4 host is shape evidence and is still replaced');
});

// --- 7. the property: every field-only kind, every adjacent structural shape ---
//
// A generator over the kinds that carry no shape, the words a real `name`,
// `city` or `zip` field holds, and every structural slot in a URL. Each URL
// carries nothing BUT structure, so the property is simple: it comes back
// byte-identical -- while the value at its own field is still replaced, so the
// property cannot pass by enrolling nothing.

const FIELD_FOR = {
    'person-name': 'name',
    'street-address': 'street',
    city: 'city',
    region: 'region',
    'postal-code': 'zip',
    country: 'country',
};
const WORDS = ['g', 'ht', 'nyt', 'cdn-x', 'w2', 'Chrome', 'Mary Jo'];
const hostSafe = (w) => /^[A-Za-z0-9-]+$/.test(w);
const TEMPLATES = [
    { slot: 'first host label', hostOnly: true, make: (w) => `https://${w}.example.test/p` },
    { slot: 'middle host label', hostOnly: true, make: (w) => `https://a.${w}.example.test/p` },
    { slot: 'host label before a port', hostOnly: true, make: (w) => `https://a.${w}:8443/p` },
    { slot: 'path segment', make: (w) => `https://h.example.test/${encodeURIComponent(w)}/x` },
    { slot: 'last path segment', make: (w) => `https://h.example.test/x/${encodeURIComponent(w)}` },
    { slot: 'matrix name', make: (w) => `https://h.example.test/ads;${encodeURIComponent(w)}=1;sz=2` },
    { slot: 'matrix value', make: (w) => `https://h.example.test/ads;k=${encodeURIComponent(w)}` },
    { slot: 'query name', make: (w) => `https://h.example.test/p?${encodeURIComponent(w)}=1&b=2` },
    // A query with a FLAG part -- no `=` -- is refused by the strict form
    // reader and read as one opaque value. Its names are still names; this is
    // the shape 16 real URLs held after the first fix.
    { slot: 'query name beside a flag part', make: (w) => `https://h.example.test/p?${encodeURIComponent(w)}=1&debug` },
    { slot: 'query flag part', make: (w) => `https://h.example.test/p?a=1&${encodeURIComponent(w)}` },
    { slot: 'query name beside a flag, with escapes', make: (w) => `https://h.example.test/p?${encodeURIComponent(w)}=%2Fx&debug` },
    { slot: 'query name beside an unencoded URL', make: (w) => `https://h.example.test/p?${encodeURIComponent(w)}=1&https://o.test/?x` },
    { slot: 'fragment parameter name', make: (w) => `https://h.example.test/p#${encodeURIComponent(w)}=1&b=2` },
];

check('property: no field-only kind rewrites any structural slot of a URL', () => {
    const misses = [];
    let cases = 0;
    for (const [kind, field] of Object.entries(FIELD_FOR)) {
        assert.strictEqual(pii.fieldTypeFor(field), kind, `premise: \`${field}\` is a ${kind} field`);
        for (const w of WORDS) {
            for (const t of TEMPLATES) {
                if (t.hostOnly && !hostSafe(w)) continue;
                const url = t.make(w);
                const har = harOf([
                    entry('https://api.example.test/v1/x', { resBody: JSON.stringify({ [field]: w }) }),
                    entry(url),
                ]);
                pii.scrubPii(har);
                cases++;
                const enrolled = JSON.parse(har.log.entries[0].response.content.text)[field] !== w;
                const now = har.log.entries[1].request.url;
                if (!enrolled) misses.push(`${kind}/${t.slot}/${w.length}ch: the value was never enrolled`);
                else if (now !== url) misses.push(`${kind}/${t.slot}/${w.length}ch: ${parsed(now) ? 'rewritten' : 'BROKEN'}`);
            }
        }
    }
    assert.ok(cases > 300, `the generator ran ${cases} cases`);
    assert.deepStrictEqual(misses, [], `${misses.length} case(s) failed:\n      ${misses.slice(0, 12).join('\n      ')}`);
});

// --- 8. a URL is structure wherever it is written ------------------------------
//
// `request.url` is one place a URL lives. The same hosts travel in a Referer, a
// Location, a CORS header, a Link header, a cookie's Domain, an HTML attribute
// and a JSON string -- and the text pass reached every one of them (found by
// independent review). The rule is the URL's, not the field's.

const AD_HOST = `securepubads.${WORD.letter}.doubleclick.test`;
const AD_URL = `https://${AD_HOST}/x?${WORD.letter}=1&${WORD.matrix}=2`;
// A real name, one word, so it can sit unencoded in a header or a URL.
const SURNAME = 'Zorblat';

/** Scrub one carrier entry after an entry that enrolls every word and the name. */
function scrubCarrier(carrier) {
    const har = harOf([
        entry('https://api.example.test/v1/config', {
            resBody: JSON.stringify({
                slots: Object.values(WORD).map((w) => ({ name: w })),
                owner: { display_name: SURNAME },
            }),
        }),
        carrier,
    ]);
    pii.scrubPii(har);
    return har.log.entries[1];
}

const header = (hs, name) => hs.find((h) => h.name === name).value;

check('Referer: a host and a query name come back byte-identical', () => {
    const e = scrubCarrier(entry('https://www.example.test/', { headers: [{ name: 'Referer', value: AD_URL }] }));
    assert.strictEqual(header(e.request.headers, 'Referer'), AD_URL);
});

check('Location and Access-Control-Allow-Origin: the URL and the origin come back byte-identical', () => {
    const origin = `https://${AD_HOST}`;
    const e = scrubCarrier(entry('https://www.example.test/', {
        resHeaders: [{ name: 'Location', value: AD_URL }, { name: 'Access-Control-Allow-Origin', value: origin }],
    }));
    assert.strictEqual(header(e.response.headers, 'Location'), AD_URL);
    assert.strictEqual(header(e.response.headers, 'Access-Control-Allow-Origin'), origin);
});

// A relative Location has no scheme for the URL rule to find; it is a request
// target, read the way `:path` is. Three real Location headers held this shape.
check('a relative Location keeps its path and query names', () => {
    const target = `/${WORD.segment}/r?${WORD.letter}=1&${WORD.matrix}=2`;
    const e = scrubCarrier(entry('https://www.example.test/', { resHeaders: [{ name: 'Location', value: target }] }));
    assert.strictEqual(header(e.response.headers, 'Location'), target);
});

check('Link: a bracketed URL comes back byte-identical', () => {
    const link = `<https://${AD_HOST}/tag.js>; rel=preload`;
    const e = scrubCarrier(entry('https://www.example.test/', { resHeaders: [{ name: 'Link', value: link }] }));
    assert.strictEqual(header(e.response.headers, 'Link'), link);
});

check('Set-Cookie: Domain, Path and the cookie name are structure; the value is payload', () => {
    const setCookie = `${WORD.letter}=${SURNAME}; Domain=${WORD.letter}.doubleclick.test; Path=/${WORD.segment}; Secure`;
    const e = scrubCarrier(entry('https://www.example.test/', { resHeaders: [{ name: 'Set-Cookie', value: setCookie }] }));
    const now = header(e.response.headers, 'Set-Cookie');
    assert.ok(now.includes(`; Domain=${WORD.letter}.doubleclick.test; Path=/${WORD.segment}; Secure`),
        'the Domain and Path attributes must come back byte-identical');
    assert.ok(now.startsWith(`${WORD.letter}=`), 'the cookie NAME must come back byte-identical');
    assert.ok(!now.includes(SURNAME), 'a declared name in the cookie VALUE must still be replaced');
});

check('Cookie: cookie names are structure', () => {
    const cookie = `${WORD.letter}=1; ${WORD.matrix}=2`;
    const e = scrubCarrier(entry('https://www.example.test/', { headers: [{ name: 'Cookie', value: cookie }] }));
    assert.strictEqual(header(e.request.headers, 'Cookie'), cookie);
});

check(':path: a request target keeps its path segments and query names', () => {
    const target = `/${WORD.segment}/ads?${WORD.query}=1&debug`;
    const e = scrubCarrier(entry(`https://www.example.test${target}`, { headers: [{ name: ':path', value: target }] }));
    assert.strictEqual(header(e.request.headers, ':path'), target);
});

check('an HTML body: absolute, protocol-relative and JSON-escaped URLs come back byte-identical', () => {
    // ...and one with its colon percent-encoded and its slashes JSON-escaped,
    // a shape two real HTML pages held.
    const html = `<script src="${AD_URL}"></script>` +
        `<img src="//${AD_HOST}/p.gif">` +
        `<script>var u="${AD_URL.replace(/\//g, '\\/')}";</script>` +
        `<script>var v="${AD_URL.replace(':', '%3A').replace(/\//g, '\\/')}";</script>`;
    const e = scrubCarrier(entry('https://www.example.test/', { resBody: html, resMime: 'text/html' }));
    assert.strictEqual(e.response.content.text, html);
});

check('a JSON body: a URL in a string value comes back byte-identical', () => {
    const body = JSON.stringify({ u: AD_URL, next: `//${AD_HOST}/a` });
    const e = scrubCarrier(entry('https://www.example.test/', { resBody: body }));
    assert.strictEqual(e.response.content.text, body);
});

check('redirectURL: never touched', () => {
    const e = scrubCarrier(entry('https://www.example.test/', { redirectURL: AD_URL }));
    assert.strictEqual(e.response.redirectURL, AD_URL);
});

// --- 9. a fake written into a URL is a URL's text ---------------------------
//
// Payload keeps its reach, but a fake holds a space and a URL does not. So a
// fake that lands inside a URL is percent-encoded -- in `request.url` and in a
// URL written anywhere else.

const noRawSpaceIn = (u) => !/\s/.test(u);

check('request.url: a fake written into a fragment or a query value is percent-encoded', () => {
    const url = `https://www.example.test/p?a=1&debug&who=${SURNAME}#${SURNAME}`;
    const e = scrubCarrier(entry(url));
    const now = e.request.url;
    assert.ok(!now.includes(SURNAME), 'the declared name must be replaced in the query value and the fragment');
    assert.ok(noRawSpaceIn(now), 'no raw space may land in a URL');
    assert.ok(parsed(now), 'the URL must parse');
    assert.ok(/who=[^&#]*%20/.test(now) && /#[^#]*%20/.test(now), 'the fakes are percent-encoded where they landed');
});

// A form value the scrub refuses to decode -- it carries a second layer of
// encoding -- is rewritten on its raw bytes. That route wrote a raw space into
// one real request URL and would into a form body.
check('request.url: a fake in a doubly-encoded form value is percent-encoded', () => {
    const url = `https://www.example.test/p?a=1&v=x-${SURNAME}%2520y`;
    const e = scrubCarrier(entry(url, {
        method: 'POST', reqMime: 'application/x-www-form-urlencoded', reqBody: `a=1&v=x-${SURNAME}%2520y`,
    }));
    for (const now of [e.request.url, e.request.postData.text]) {
        assert.ok(!now.includes(SURNAME), 'the declared name must be replaced');
        assert.ok(noRawSpaceIn(now), 'no raw space may land in a URL or a form body');
    }
    assert.ok(parsed(e.request.url), 'the URL must parse');
});

check('a URL in a header or a JSON string: the fake in a query value is percent-encoded', () => {
    const payloadUrl = `https://news.example.test/s?q=${SURNAME}`;
    const e = scrubCarrier(entry('https://www.example.test/', {
        headers: [{ name: 'Referer', value: payloadUrl }],
        resBody: JSON.stringify({ u: payloadUrl }),
    }));
    const referer = header(e.request.headers, 'Referer');
    const inBody = JSON.parse(e.response.content.text).u;
    for (const now of [referer, inBody]) {
        assert.ok(!now.includes(SURNAME), 'the declared name in a query VALUE must still be replaced');
        assert.ok(noRawSpaceIn(now), 'no raw space may land in a URL');
        assert.strictEqual(new URL(now).host, 'news.example.test');
    }
});

check('a declared name in free text is still replaced, as text', () => {
    const e = scrubCarrier(entry('https://www.example.test/', {
        resBody: `<p>By ${SURNAME}, see https://${AD_HOST}/</p>`, resMime: 'text/html',
    }));
    assert.ok(!e.response.content.text.includes(SURNAME));
    assert.ok(e.response.content.text.includes(`https://${AD_HOST}/`));
});

// --- 10. the property, beyond request.url ------------------------------------
//
// The same generator, with each URL written into the carriers a capture holds:
// a request header, a response header, a plain-text body, an HTML attribute
// and a JSON string. Every carrier comes back byte-identical.

check('property: no field-only kind rewrites a URL written in a header or a body', () => {
    const misses = [];
    let cases = 0;
    for (const [kind, field] of Object.entries(FIELD_FOR)) {
        for (const w of WORDS) {
            for (const t of TEMPLATES) {
                if (t.hostOnly && !hostSafe(w)) continue;
                const url = t.make(w);
                const carriers = {
                    referer: url,
                    location: url,
                    text: `see ${url} now`,
                    html: `<a href="${url}">x</a>`,
                    json: JSON.stringify({ u: url }),
                };
                const har = harOf([
                    entry('https://api.example.test/v1/x', { resBody: JSON.stringify({ [field]: w }) }),
                    entry('https://www.example.test/', {
                        method: 'POST', reqMime: 'text/plain', reqBody: carriers.text,
                        headers: [{ name: 'Referer', value: carriers.referer }],
                        resHeaders: [{ name: 'Location', value: carriers.location }],
                        resBody: carriers.html, resMime: 'text/html',
                    }),
                    entry('https://www.example.test/j', { resBody: carriers.json }),
                ]);
                pii.scrubPii(har);
                cases++;
                const [enrol, a, b] = har.log.entries;
                if (JSON.parse(enrol.response.content.text)[field] === w) {
                    misses.push(`${kind}/${t.slot}/${w.length}ch: the value was never enrolled`);
                    continue;
                }
                const now = {
                    referer: header(a.request.headers, 'Referer'),
                    location: header(a.response.headers, 'Location'),
                    text: a.request.postData.text,
                    html: a.response.content.text,
                    json: b.response.content.text,
                };
                for (const k of Object.keys(carriers)) {
                    if (now[k] !== carriers[k]) misses.push(`${kind}/${t.slot}/${w.length}ch in ${k}: rewritten`);
                }
            }
        }
    }
    assert.ok(cases > 300, `the generator ran ${cases} cases`);
    assert.deepStrictEqual(misses, [], `${misses.length} case(s) failed:\n      ${misses.slice(0, 12).join('\n      ')}`);
});

if (failures) {
    console.error(`pii-structure-scrub: ${failures} failure(s)`);
    process.exit(1);
}
console.log('All pii-structure-scrub tests passed');
