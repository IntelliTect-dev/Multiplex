#!/usr/bin/env node
// Behavior tests for scrubbing a percent-encoded payload that lives OUTSIDE a
// URL's query string -- in a path segment or the fragment (issue #22).
//
// Zero-dep, runs with `node pii-url-escape-scrub.test.js`. Exits non-zero on
// first failure.
//
// THE DEFECT THESE PIN, and it is #479's defect in the locations #479
// deliberately scoped out. `detectInUrl` / `replaceInUrl` treated the query as
// form data and handed it to the decoding path; the origin, path and fragment
// stayed on the plain-text scanner. `%22` is an encoded `"`, and its literal
// characters include the digits `22`, so a digit-run detector reading an
// encoded PATH sees those two digits joined to whatever follows:
//
//     /x/%7B%22id%22%3A%22<14-digit id>%22%7D
//                         ^^^^^^^^^^^^^^^^ the scanner sees a 16-DIGIT RUN
//
// Sixteen digits is a credit-card length and the run is Luhn-valid, so the
// check that exists does not stop it. The 16-digit fake is then written over a
// span that includes the `22` INSIDE the escape, and `%22` becomes `%42`:
//
//     {"id":"<id>"}   ->   {"id":B<digits>"}      (no longer JSON)
//
// TWO HALVES, AND THE SECOND IS THE ONE THAT MATTERS. Decoding before
// detection stops the false positive; making replacement escape-AWARE stops
// the write landing inside a `%XX`. A decode without a write guard would
// relocate the corruption rather than remove it, which is why both directions
// are asserted here.
//
// AND THE OPPOSITE DIRECTION IS A REAL RISK. #22's own scope note warns that
// rewriting a path risks changing a URL's MEANING to fix a payload that is
// usually absent, and that a wrong path rewrite is worse than the corruption
// it prevents. So every section that proves a value IS reached is paired with
// one proving an untouched component comes back byte-identical.
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

/** A GET whose whole payload is in the URL. Nothing else carries a value. */
function harWithUrl(url) {
    return {
        log: {
            entries: [{
                request: {
                    method: 'GET',
                    url,
                    headers: [],
                    queryString: [],
                    cookies: []
                },
                response: {
                    status: 200,
                    headers: [],
                    cookies: [],
                    content: { mimeType: 'application/json', text: '{}' }
                }
            }]
        }
    };
}

function urlOf(har) { return har.log.entries[0].request.url; }

function scrubbedUrl(url) {
    const har = harWithUrl(url);
    pii.scrubPii(har);
    return urlOf(har);
}

// The same 14-digit identifier the form-encoded suite uses: preceded by the
// `22` of an encoded quote it forms a Luhn-VALID 16-digit run, which is what
// makes the false positive reachable. The fixture states that rather than
// relying on luck.
const ID_14 = '61593494464534';

function luhnOk(s) {
    let sum = 0, alt = false;
    for (let i = s.length - 1; i >= 0; i--) {
        let d = Number(s[i]);
        if (alt) { d *= 2; if (d > 9) d -= 9; }
        sum += d; alt = !alt;
    }
    return sum % 10 === 0;
}

/** `{"id":"<ID_14>"}`, percent-encoded -- the payload shape #22 reports. */
const PAYLOAD = encodeURIComponent(JSON.stringify({ id: ID_14 }));

console.log('pii-url-escape-scrub');

// --- 0. the fixture really does express the defect -----------------------

check('the fixture premise holds: escape digits + id form a Luhn-valid 16-digit run', () => {
    assert.strictEqual(('22' + ID_14).length, 16, 'the run must be credit-card length');
    assert.ok(luhnOk('22' + ID_14),
        'the run must be Luhn-valid, or the detector would reject it and the test ' +
        'would pass for the wrong reason');
    assert.ok(!luhnOk(ID_14) || ID_14.length !== 16,
        'the id alone must not be a card, or the false positive is not what is being tested');
    assert.ok(PAYLOAD.includes('%22' + ID_14),
        'the encoded payload must place an escape immediately before the id, or the ' +
        'escape-boundary shape this issue is about is not present');
});

// --- 1. the corruption, in each location the query branch did not cover ---

check('a payload in a PATH SEGMENT still decodes to valid JSON after the scrub', () => {
    const after = scrubbedUrl('https://api.example.test/x/' + PAYLOAD + '?ok=1');
    const segment = new URL(after).pathname.split('/')[2];
    assert.doesNotThrow(() => JSON.parse(decodeURIComponent(segment)),
        'the scrub rewrote the path segment into something that no longer parses: ' + after);
});

check('a payload in a path segment survives when the URL has no query at all', () => {
    // The pre-#22 `splitUrl` returned null without a `?`, which sent the WHOLE
    // url to the plain-text scanner. So "no query" was not a lesser case of the
    // one above; it was the only case for a URL shaped like this.
    const after = scrubbedUrl('https://api.example.test/x/' + PAYLOAD);
    const segment = new URL(after).pathname.split('/')[2];
    assert.doesNotThrow(() => JSON.parse(decodeURIComponent(segment)),
        'the scrub corrupted a path payload in a URL with no query: ' + after);
});

check('a payload in the FRAGMENT still decodes to valid JSON after the scrub', () => {
    const after = scrubbedUrl('https://api.example.test/x?ok=1#' + PAYLOAD);
    const frag = after.slice(after.indexOf('#') + 1);
    assert.doesNotThrow(() => JSON.parse(decodeURIComponent(frag)),
        'the scrub corrupted the fragment: ' + after);
});

check('no percent-escape in a path segment is overwritten by a replacement', () => {
    // The escape-awareness half, asserted directly rather than through JSON
    // validity -- a write that lands inside a `%XX` changes the DECODED
    // structure, which is the damage, whether or not the result still parses.
    const before = 'https://api.example.test/x/' + PAYLOAD + '?ok=1';
    const after = scrubbedUrl(before);
    const escapesOf = (s) => (s.match(/%[0-9A-Fa-f]{2}/g) || [])
        .map((e) => decodeURIComponent(e)).join('');
    assert.strictEqual(escapesOf(after), escapesOf(before),
        'a replacement landed inside a %XX sequence and changed the decoded structure');
});

// --- 2. the fix must not switch detection off ----------------------------
//
// Each of these fails BEFORE the fix as well, and for the more serious reason:
// the escape's digits push a real value out of its detector's length range, so
// the value is never found and ships in the clear. The same encoding fault that
// corrupts a benign id also leaks a real one.

// Luhn-valid, Visa range, not a published test number. NOT 4242424242424242:
// the scrubber recognises the canonical test card as already-fake and declines
// to replace it, so that number would pass this test vacuously.
const CARD = '4539578763621486';

check('a genuine card in an encoded path segment is detected and replaced', () => {
    const payload = encodeURIComponent(JSON.stringify({ cardNumber: CARD }));
    const har = harWithUrl('https://api.example.test/pay/' + payload);
    const { substitutions } = pii.scrubPii(har);
    const after = urlOf(har);
    assert.ok(!decodeURIComponent(after).includes(CARD),
        'the card survived the scrub inside a path segment: ' + after);
    assert.ok(substitutions.some((s) => s.type === 'credit-card'),
        'the card must be recorded as a substitution, not merely absent');
});

check('a genuine card in an encoded path segment leaves the payload parseable', () => {
    const payload = encodeURIComponent(JSON.stringify({ cardNumber: CARD }));
    const har = harWithUrl('https://api.example.test/pay/' + payload);
    pii.scrubPii(har);
    const segment = new URL(urlOf(har)).pathname.split('/')[2];
    assert.doesNotThrow(() => JSON.parse(decodeURIComponent(segment)),
        'the replacement that removed the card corrupted the segment: ' + urlOf(har));
});

check('an UNencoded value in a path segment is still reached', () => {
    // The path used to be covered -- badly -- by the plain-text scanner, and a
    // fix that decoded its way out of the false positive could easily have
    // stopped covering it at all. A segment with no escapes decodes to itself,
    // so this is the assertion that the plain reading survived.
    const har = harWithUrl('https://api.example.test/u/someone@example.test/profile');
    pii.scrubPii(har);
    assert.ok(!urlOf(har).includes('someone@example.test'),
        'an email sitting plainly in a path segment was not scrubbed: ' + urlOf(har));
});

check('a value in the FRAGMENT is reached', () => {
    const har = harWithUrl('https://api.example.test/x#contact=someone@example.test');
    pii.scrubPii(har);
    assert.ok(!urlOf(har).includes('someone@example.test'),
        'an email in the fragment was not scrubbed: ' + urlOf(har));
});

// --- 2b. a replacement is encoded ONCE -----------------------------------
//
// Found by independent review, and the assertion above is why it got through:
// "the original string is gone" passes just as happily on a DOUBLE-encoded
// result, because the original is indeed gone -- replaced by a mangling of its
// substitute. These assert the output's meaning, not the absence of an input.
//
// The mechanism. `replaceJsonOrText`'s form branch re-encodes each part it
// changes, which is correct when it is handed raw wire text -- that is exactly
// how the query branch works, and the query is deliberately NOT re-encoded by
// its caller. A component that pre-decodes and then re-encodes the whole
// result encodes those parts a SECOND time: `%40` comes back as `%2540`, which
// decodes to the literal text `%40` rather than to `@`. That is the value's
// meaning destroyed, not the project's accepted over-escaping.

/** The component after `#`, decoded exactly once. */
function fragmentDecodedOnce(url) {
    return decodeURIComponent(url.slice(url.indexOf('#') + 1));
}

check('a form-shaped FRAGMENT keeps its structure and its replacement decodes once', () => {
    // The OAuth 2 implicit-flow shape (RFC 6749 is explicit that this fragment
    // is form-urlencoded), so this is a routine capture shape rather than a
    // contrived one.
    const har = harWithUrl(
        'https://api.example.test/cb#access_token=someone@example.test&token_type=Bearer');
    pii.scrubPii(har);
    const frag = urlOf(har).slice(urlOf(har).indexOf('#') + 1);
    const params = new URLSearchParams(frag);
    assert.strictEqual(params.get('token_type'), 'Bearer',
        'the fragment stopped being a parameter list -- its `&` was escaped away: ' + frag);
    const value = params.get('access_token');
    assert.ok(value && !value.includes('%'),
        'the replacement was encoded twice, so it decodes to text containing a literal ' +
        'escape instead of to its own value: ' + value);
    assert.ok(/@example\.invalid$/.test(value),
        'the substituted email did not survive as an email: ' + value);
});

check('a form-shaped PATH SEGMENT replacement decodes once', () => {
    const har = harWithUrl('https://api.example.test/u/contact=someone@example.test');
    pii.scrubPii(har);
    const segment = new URL(urlOf(har)).pathname.split('/')[2];
    const once = decodeURIComponent(segment);
    assert.ok(!once.includes('%'),
        'the replacement was encoded twice: decoding the segment once leaves an escape ' +
        'behind, so its value is text rather than an address: ' + once);
    assert.ok(/^contact=.*@example\.invalid$/.test(once),
        'the segment did not decode back to its own shape: ' + once);
});

check('a literal + inside a value is not turned into a space', () => {
    // `+` is a space in form DATA and a literal `+` in a path. A path segment
    // read as form data loses the distinction, and the damage compounds: the
    // `+` becomes a space, the space re-encodes to `%20`, and the second encode
    // makes it `%2520`.
    const har = harWithUrl('https://api.example.test/u/contact=bob+smith@example.test');
    pii.scrubPii(har);
    const once = decodeURIComponent(new URL(urlOf(har)).pathname.split('/')[2]);
    assert.ok(!/%|\s/.test(once),
        'a literal + was read as a space and/or the result was encoded twice: ' + once);
});

check('a fragment carrying encoded JSON is still decoded before detection', () => {
    // The non-form fragment shape must keep working -- it is the one this
    // stage exists for, and a fix aimed at the form shape must not drop it.
    const har = harWithUrl('https://api.example.test/x#' + PAYLOAD);
    pii.scrubPii(har);
    assert.doesNotThrow(() => JSON.parse(fragmentDecodedOnce(urlOf(har))),
        'the encoded-JSON fragment was corrupted: ' + urlOf(har));
});

// --- 3. and it must not change a URL that carries nothing ----------------
//
// #22's scope note is explicit that this is the risk worth guarding: rewriting
// a path to fix a payload that is usually absent can change the URL's MEANING,
// and a wrong path rewrite is worse than the corruption it prevents.

check('a URL with nothing to scrub is byte-identical', () => {
    const url = 'https://api.example.test/v1/items/42/children?page=2&sort=-created#top';
    assert.strictEqual(scrubbedUrl(url), url,
        'a URL carrying no detectable value was rewritten');
});

check('escape spellings and reserved characters are preserved when nothing changed', () => {
    // `encodeURIComponent(urlDecode(x))` is not the identity: `%7E` decodes to
    // `~`, `:` re-encodes to `%3A`. An unchanged segment must never be
    // round-tripped through the decoder, or every capture gains a normalisation
    // diff the scrub was not asked for.
    const url = 'https://api.example.test/a%7Eb/c:d,e;f/g%2Fh?q=1';
    assert.strictEqual(scrubbedUrl(url), url,
        'an untouched segment was normalised rather than re-emitted verbatim');
});

check('a literal + in a path is not read as a space', () => {
    // `+` means space in form DATA and a literal `+` everywhere else in a URL.
    // Decoding a path with form semantics and re-encoding would emit `%20` --
    // a changed URL. The payload here forces the segment to be rewritten, so
    // the sibling segment is the one under test: it must not drift either way.
    const url = 'https://api.example.test/tag/a+b/' + PAYLOAD;
    const after = scrubbedUrl(url);
    assert.ok(after.includes('/tag/a+b/'),
        'a literal + in a path segment was altered: ' + after);
});

check('a trailing slash and an empty segment survive', () => {
    const url = 'https://api.example.test/a//b/';
    assert.strictEqual(scrubbedUrl(url), url,
        "the URL's own delimiters were not preserved");
});

check('a URL with no path at all survives', () => {
    const url = 'https://api.example.test';
    assert.strictEqual(scrubbedUrl(url), url, 'an origin-only URL was rewritten');
});

check('the authority shapes the decomposition does not special-case survive', () => {
    // `splitUrl` finds the origin by looking for the first `/` after `://`,
    // and reads a URL with no `://` as all path. These are the shapes where
    // that reading could plausibly drop or mangle a component. Each is
    // asserted to round-trip, so the tolerant reading stays tolerant.
    for (const url of [
        'https://[::1]:8080/x/y',               // IPv6 literal with a port
        '//api.example.test/a/b',               // protocol-relative
        'api.example.test/a/b',                 // no scheme at all
        'https://api.example.test/x?',          // a `?` with an empty query
        'https://api.example.test/x#',          // a `#` with an empty fragment
        'https://api.example.test/x?a=1#b=2',   // both, both non-empty
    ]) {
        assert.strictEqual(scrubbedUrl(url), url, 'this URL was rewritten: ' + url);
    }
});

// --- 3b. the scrub is idempotent over every URL component ----------------

check('scrubbing an already-scrubbed URL changes nothing further', () => {
    // The pipeline can be re-run on its own output, so a component that
    // re-encodes on every pass would creep: `%40` -> `%2540` -> `%252540`. The
    // re-encode is guarded by "only if something changed", and this is the
    // assertion that the guard actually holds for each component kind rather
    // than only for the one the fixture happens to exercise.
    for (const url of [
        'https://api.example.test/x/' + PAYLOAD + '?ok=1',
        'https://api.example.test/pay/' + encodeURIComponent(JSON.stringify({ cardNumber: CARD })),
        'https://api.example.test/u/someone@example.test/profile',
        'https://api.example.test/cb#access_token=someone@example.test&token_type=Bearer',
        'https://api.example.test/u/contact=bob+smith@example.test',
        'https://api.example.test/graphql?variables=' + PAYLOAD,
        'https://api.example.test/x#' + PAYLOAD,
    ]) {
        const first = scrubbedUrl(url);
        assert.strictEqual(scrubbedUrl(first), first,
            'a second scrub changed the URL again, so the rewrite is not idempotent:\n' +
            '      input ' + url + '\n      pass1 ' + first + '\n      pass2 ' + scrubbedUrl(first));
    }
});

// --- 3c. the four regressions the second reviewer found ------------------
//
// Every one of these is a case `origin/main` handled correctly and the first
// version of this change broke. They are the reason a component may only be
// decoded when decoding is unambiguously safe, and why "nothing was detected"
// has to mean "emit the original bytes".

check('a JSON-shaped path segment with nothing detected is byte-identical', () => {
    // `JSON.stringify(JSON.parse(x))` is not the identity. It drops
    // insignificant whitespace, normalises `1.0` to `1`, resolves `\uXXXX`,
    // and -- the one that is not cosmetic -- ROUNDS AN INTEGER PAST 2^53. A
    // component rebuilt from a parsed document therefore differs from its
    // input even when nothing was scrubbed, and the "re-encode only if
    // changed" guard then fires on a change the scrub did not make.
    for (const body of [
        '{"a": 1}',                      // insignificant whitespace
        '{"n":1.0}',                     // 1.0 -> 1
        '{"id":12345678901234567890}',   // silently rounded to ...567000
        '{"s":"\\u00e9"}',               // escape resolved
    ]) {
        const url = 'https://api.example.test/x/' + encodeURIComponent(body);
        assert.strictEqual(scrubbedUrl(url), url,
            'a segment the scrub did not touch was rewritten: ' + body + ' -> ' +
            decodeURIComponent(scrubbedUrl(url).split('/x/')[1]));
    }
});

check('a component whose decoded text is itself encoded is left alone', () => {
    // ONE DECODE IS NOT ENOUGH, AND HALF A DECODE IS WORSE THAN NONE. A
    // proxied URL in a path segment is encoded twice: decoding once yields
    // text that STILL holds `%XX`, so the plain-text scanner reads those
    // escapes as payload -- the very defect this stage closes, one layer
    // down. `main` was safe here only by accident (two layers put the digit
    // run at 18, out of card range); a single decode brought it back to 16
    // and the write landed inside the inner escape.
    //
    // So a component is decoded only when the result is unambiguously
    // decoded. Otherwise it keeps the plain-text treatment, which is what
    // `main` does. That leaves the multi-layer case open -- deliberately, and
    // tracked -- rather than making it worse.
    const inner = 'https://other.test/x?q=' + encodeURIComponent(JSON.stringify({ id: ID_14 }));
    const url = 'https://api.example.test/go/' + encodeURIComponent(inner);
    const after = scrubbedUrl(url);
    const segment = new URL(after).pathname.split('/')[2];
    const q = /q=([^&]*)/.exec(decodeURIComponent(segment));
    assert.ok(q, 'the inner URL lost its query: ' + after);
    assert.doesNotThrow(() => JSON.parse(decodeURIComponent(q[1])),
        'the inner payload was corrupted one layer down: ' + after);
});

check('an E.164 phone in a form-shaped fragment is still detected', () => {
    // THE LEAK DIRECTION, and the sharpest argument about `+`. Form semantics
    // make `+` a space, and the E.164 pattern needs a literal `+` -- so
    // reading a fragment as form data with `+`-is-space made a phone number
    // undetectable and it shipped in the clear. `main` caught it.
    //
    // This is what settles the question the mirror-image argument could not:
    // one direction loses a value, the other only respells one. A literal `+`
    // it is.
    const PHONE = '+14155551234';   // a real NANP area code, NOT the +1555 faker prefix
    assert.ok(!/^\+1555\d{7}$/.test(PHONE),
        'the probe must sit outside isFakePhone, or this passes vacuously');
    const har = harWithUrl('https://api.example.test/cb#tel=' + PHONE + '&x=1');
    const { substitutions } = pii.scrubPii(har);
    assert.ok(!urlOf(har).includes(PHONE),
        'the phone survived in a form-shaped fragment: ' + urlOf(har));
    assert.ok(substitutions.some((s) => s.type === 'phone'),
        'the phone must be recorded as a substitution, not merely absent');
});

check('a non-hierarchical URL keeps its delimiters', () => {
    // `mailto:` and `data:` have no authority, so a reader that looks for the
    // first `/` after `://` finds nothing and treats the whole URL as a path.
    // Re-encoding a "segment" of one then escapes the delimiters that give it
    // its meaning: `mailto:` became `mailto%3A`, and a `data:` URL lost the
    // `,` separating its media type from its payload. `data:` requests do
    // appear in browser HARs.
    const mailto = pii.scrubPii(harWithUrl('mailto:someone@example.test'));
    void mailto;
    for (const [url, keep] of [
        ['mailto:someone@example.test', 'mailto:'],
        ['data:text/plain,someone@example.test', 'data:text/plain,'],
    ]) {
        const har = harWithUrl(url);
        pii.scrubPii(har);
        assert.ok(urlOf(har).startsWith(keep),
            'the URL lost the delimiters that give it its meaning: ' + urlOf(har));
        assert.ok(!urlOf(har).includes('someone@example.test'),
            'and the value in it must still be scrubbed: ' + urlOf(har));
    }
});

// --- 3d. the third review round -----------------------------------------
//
// Every encode/decode seam in this file has now cost one round of review, which
// is itself the finding: the risky part of reading a URL is not the decoding,
// it is deciding WHICH LAYER each rule belongs to. These four pin that.

check('a nested form layer inherits its component plus-semantics', () => {
    // THE LEAK. `plusIsSpace` describes the COMPONENT's encoding convention,
    // so it holds for every layer inside that component -- but the form
    // branch's recursive call did not pass it on, so depth 2 silently reverted
    // to `+`-is-space inside a fragment that had asked for `+`-literal.
    //
    // An RFC 5233 subaddressed email (`user+tag@host`, the Gmail-style `+tag`)
    // is then split at the `+`: only `tag@example.test` is email-shaped, so
    // only the tail is replaced and the real local-part prefix `user` SURVIVES
    // IN THE CLEAR. `origin/main` replaced the whole address.
    const EMAIL = 'user+tag@example.test';
    const har = harWithUrl('https://app.example.test/cb#state=key1=' + EMAIL + '&other=1');
    pii.scrubPii(har);
    const after = urlOf(har);
    assert.ok(!after.includes(EMAIL), 'the address survived whole: ' + after);
    // The prefix must not be left behind as its own token on either side of a
    // decode -- that is the half-replacement this pins.
    const twice = decodeURIComponent(decodeURIComponent(after));
    assert.ok(!/\buser\s/.test(twice) && !/=user\b(?![-@])/.test(twice),
        "the address was split at its `+` and its local-part prefix was left in the " +
        'clear: ' + twice);
});

check('a form value whose decoded text is itself encoded is left alone', () => {
    // B1. The multi-layer guard was applied to the opaque route only, and a
    // fragment is form-routed -- so an OAuth `state` carrying an encoded
    // redirect URL hit exactly the shape the guard exists to refuse, and the
    // form branch half-decoded it. `main` was safe here by accident.
    const inner = 'https://other.test/x?q=' + encodeURIComponent(JSON.stringify({ id: ID_14 }));
    const har = harWithUrl('https://app.example.test/cb#state=' +
        encodeURIComponent(inner) + '&token_type=Bearer');
    pii.scrubPii(har);
    const frag = urlOf(har).slice(urlOf(har).indexOf('#') + 1);
    const state = new URLSearchParams(frag).get('state');
    assert.ok(state, 'the state parameter is gone: ' + frag);
    const q = /q=([^&]*)/.exec(state);
    assert.ok(q, 'the inner URL lost its query: ' + state);
    assert.doesNotThrow(() => JSON.parse(decodeURIComponent(q[1])),
        'the inner payload was corrupted through the form branch: ' + state);
});

check('a query with no name=value structure is decoded before detection', () => {
    // I3. `splitUrl` names four components and the written rule claims a query
    // is covered, but the query skipped the component treatment -- so a query
    // that is a bare encoded payload rather than `name=value` still went to the
    // plain-text scanner, and #22's defect was fully live there.
    const url = 'https://api.example.test/graphql?' + PAYLOAD;
    const after = scrubbedUrl(url);
    const q = after.slice(after.indexOf('?') + 1);
    assert.doesNotThrow(() => JSON.parse(decodeURIComponent(q)),
        'a bare encoded query payload was corrupted: ' + after);
});

check('a genuine card in a bare encoded query is detected', () => {
    // The leak twin of the check above: the escape extended the card past
    // range, so it was never found.
    const url = 'https://api.example.test/pay?' +
        encodeURIComponent(JSON.stringify({ cardNumber: CARD }));
    const har = harWithUrl(url);
    const { substitutions } = pii.scrubPii(har);
    assert.ok(!decodeURIComponent(urlOf(har)).includes(CARD),
        'the card survived in a bare encoded query: ' + urlOf(har));
    assert.ok(substitutions.some((s) => s.type === 'credit-card'),
        'the card must be recorded as a substitution');
});

check('a rewritten component keeps the delimiters that are legal in it', () => {
    // S6. `encodeURIComponent` escapes `/` and `?`, which are legal in a
    // fragment and give an SPA hash route its structure -- so replacing a value
    // inside `#/users/<email>/edit` turned the route into
    // `#%2Fusers%2F...%2Fedit` and no hash router matches it. A component is
    // re-encoded only when something changed, so this bit exactly the captures
    // that carried PII.
    const har = harWithUrl('https://app.example.test/#/users/someone@example.test/edit');
    pii.scrubPii(har);
    const after = urlOf(har);
    assert.ok(!after.includes('someone@example.test'), 'the email was not scrubbed: ' + after);
    assert.ok(after.includes('#/users/') && after.endsWith('/edit'),
        'the hash route lost the slashes that give it its structure: ' + after);
});

check('a reserved character the input spelled literally comes back literal', () => {
    // The component encoder withheld `=` and `&` on the reasoning that handing
    // them back could let a rewritten OPAQUE component start parsing as form
    // data on a later pass. That reasoning does not survive contact with the
    // cases: a component whose raw text ALREADY held them literally was
    // classified opaque *with them present*, and no replacement can repair the
    // name-rule violation that made it opaque -- every fake keeps the
    // offending character, and a flag-style part like `&debug` is never
    // touched. Withholding them destroyed the structure of four real shapes,
    // and `origin/main` preserved all four.
    //
    // The rule that is both safe and correct: give a reserved character back
    // **iff the raw input never spelled it escaped**. The output then carries
    // exactly the spelling the input had, which is what keeps classification --
    // and therefore idempotence -- stable.
    for (const [url, keep] of [
        ['https://api.example.test/s?email=someone@example.test&debug', '?email=' ],
        ['https://app.example.test/#/profile?email=someone@example.test&tab=2', '&tab=2'],
        ['https://api.example.test/u/contact=someone@example.test', '/contact='],
        ['https://api.example.test/items;id=42;owner=someone@example.test', ';id=42;owner='],
    ]) {
        const after = scrubbedUrl(url);
        assert.ok(!after.includes('someone@example.test'),
            'the email was not scrubbed: ' + after);
        assert.ok(after.includes(keep),
            'a reserved character the input spelled literally came back escaped, so the ' +
            "component lost its structure -- expected to still contain '" + keep + "': " + after);
    }
});

check('a value whose first or last character is escaped is replaced whole', () => {
    // THE OFFSET MAP RECORDS A RUN'S START FOR EVERY CHARACTER IT DECODES TO,
    // so a span boundary landing inside a multi-escape run resolves to the
    // run's beginning -- and slicing there takes the rest of the run with it,
    // or leaves it behind.
    //
    // A non-ASCII letter is a multi-byte UTF-8 escape run, so any value
    // beginning or ending with one hits it. Both directions were live, and the
    // second is the worse pair of faults:
    //
    //   {"first_name":"Élodie"}  ->  {"first_nameAsh Fisher"...   the `":"` run
    //                                was consumed: invalid JSON
    //   {"first_name":"José"}    ->  {"first_name":"Kai Blakeé"}  the original's
    //                                last character SURVIVES, and because the
    //                                leftover is still part of a detectable
    //                                value the next pass replaces it again --
    //                                a different fake every run, forever.
    //
    // So a boundary has to snap to the edges of its run before the slice.
    const cases = [
        [{ first_name: 'Élodie', ok: 1 }, 'Élodie'],
        [{ city: 'Ålesund', ok: 1 }, 'Ålesund'],
        [{ first_name: 'José' }, 'José'],
        [{ street: "12 Rue de l'Été" }, "12 Rue de l'Été"],
    ];
    for (const [obj, original] of cases) {
        const url = 'https://a.test/x/' + encodeURIComponent(JSON.stringify(obj));
        const har = harWithUrl(url);
        pii.scrubPii(har);
        const segment = decodeURIComponent(new URL(urlOf(har)).pathname.split('/')[2]);
        assert.doesNotThrow(() => JSON.parse(segment),
            'the span boundary consumed a delimiter and the payload no longer parses: ' +
            segment);
        // No fragment of the original may be left behind -- the last character
        // of a name is still part of someone's name.
        assert.ok(!segment.includes(original.slice(-1)) || /[A-Za-z0-9]/.test(original.slice(-1)),
            'the original value&apos;s final character survived the replacement: ' + segment);
    }
});

check('a multi-character escape run keeps the bytes outside the replaced value', () => {
    // THE SAME ROOT CAUSE, AND THE WORST SYMPTOM: dropped bytes, not respelled
    // ones. `%78%21%73%6F%6D%65%6F%6E%65` is nine consecutive escapes, so it is
    // ONE run that decodes to `x!someone`. Only `someone@example.test` is a
    // detected value, so `x!` is on the untouched side of the boundary -- but
    // the boundary resolved to the run's start, which put `x!` in neither the
    // preserved prefix nor the replaced span. It vanished from the output.
    //
    // Found by independent review. Worth its own check because it is the only
    // defect in this suite where the scrub DESTROYED data rather than
    // respelling or missing it.
    const url = 'https://app.example.test/x/%78%21%73%6F%6D%65%6F%6E%65@example.test';
    const after = scrubbedUrl(url);
    assert.ok(!after.includes('someone@example.test'), 'the email survived: ' + after);
    assert.ok(decodeURIComponent(new URL(after).pathname).includes('x!'),
        'bytes outside the replaced value were dropped from the output: ' + after);
    // And this shape IS idempotent, because the email fake is recognised as
    // already-fake. (A person-NAME is not: a fake name is still name-shaped and
    // its field still says `first_name`, so it is re-faked every pass -- which
    // `origin/main` does identically, so it is this module's own pre-existing
    // behaviour and not something to pin here.)
    assert.strictEqual(scrubbedUrl(after), after,
        'a second scrub changed it again: ' + scrubbedUrl(after));
});

check('two replaced values in one component keep the delimiters between them', () => {
    // The prefix/suffix span covered everything from the FIRST change to the
    // LAST, so a component carrying an escape anywhere plus two replaced values
    // had every delimiter between them re-encoded -- two query parameters
    // merging into one, three route segments into one. `origin/main` preserved
    // each, so this was a regression and not a disclosed cost.
    //
    // The fix is to write each accepted match back on its own, which the
    // replacement pass already knows how to arbitrate; it just was not saying
    // where it landed.
    for (const [url, keep] of [
        ['https://a.test/s?email=a@x.test&debug&cc=b@y.test&t=abc%3D%3D', '&debug&cc='],
        ['https://a.test/x#/u/a@x.test/cc/b@y.test?t=abc%3D%3D', '/cc/'],
        ['https://a.test/x/a=a@x.test;b=b@y.test;sig=YWJj%3D', ';b='],
    ]) {
        const after = scrubbedUrl(url);
        assert.ok(!after.includes('a@x.test') && !after.includes('b@y.test'),
            'a value survived: ' + after);
        assert.ok(after.includes(keep),
            'the delimiters between two replaced values were escaped away, merging them -- ' +
            "expected to still contain '" + keep + "': " + after);
    }
});

check('a component with no escapes at all is rewritten as plain text', () => {
    // THE GUARD THAT SHOULD HAVE BEEN FIRST. The decode route exists to stop an
    // escape's characters being read as payload. A component holding no `%XX`
    // has no escape to misread, so decoding it buys nothing and the re-encode
    // on the way out is pure cost -- it respelled `@` as `%40`, and in a
    // component with two replacements it escaped the `=` and `&` between them.
    // `origin/main` left all of these alone, and for this case `main` was
    // simply right.
    for (const url of [
        'https://a.test/x#/route/plain/someone@example.test',
        'https://a.test/x#a=1&contact=someone@example.test',
        'https://a.test/u/someone@example.test/profile',
        'https://a.test/s?contact=someone@example.test&debug',
    ]) {
        const after = scrubbedUrl(url);
        assert.ok(!after.includes('someone@example.test'), 'not scrubbed: ' + after);
        assert.ok(!/%[0-9A-Fa-f]{2}/.test(after),
            'a component carrying no escapes came back with one, so the scrub respelled ' +
            'bytes it had no reason to touch: ' + after);
    }
});

check('an untouched part of a component keeps its own spelling', () => {
    // THE TABLE COULD ONLY GIVE ONE ANSWER PER CHARACTER PER COMPONENT, and a
    // component routinely spells the same character both ways: a hash-route
    // query beside a base64-padded parameter (`token=abc%3D%3D`), a path with
    // one encoded separator among literal ones (`/a%2Fb/`). Deciding per
    // character class meant one escaped sibling withheld the literal spelling
    // everywhere else in the component -- so a route collapsed to a single
    // segment. `origin/main` preserved all of these.
    //
    // The answer is not a better table. It is to re-emit the bytes the input
    // gave us everywhere except the span that actually changed.
    for (const [url, keep] of [
        ['https://app.example.test/#/profile?email=someone@example.test&token=abc%3D%3D',
            '#/profile?email='],
        ['https://app.example.test/#/profile?email=someone@example.test&token=abc%3D%3D',
            '&token=abc%3D%3D'],
        ['https://api.example.test/s?email=someone@example.test&debug&t=a%3Db',
            '&debug&t=a%3Db'],
        ['https://app.example.test/#/files/a%2Fb/someone@example.test',
            '#/files/a%2Fb/'],
    ]) {
        const after = scrubbedUrl(url);
        assert.ok(!after.includes('someone@example.test'), 'not scrubbed: ' + after);
        assert.ok(after.includes(keep),
            'a part of the component the scrub never touched was respelled -- expected ' +
            "to still contain '" + keep + "': " + after);
    }
});

check('a reserved character the input escaped stays escaped', () => {
    // The other half of the same rule, and the direction that keeps
    // classification stable: a component that spelled `=` as `%3D` must not
    // have it handed back, or a rewritten opaque component could begin parsing
    // as form data on the next pass.
    const url = 'https://api.example.test/t/' +
        encodeURIComponent('k=v') + '/' + encodeURIComponent('someone@example.test');
    const after = scrubbedUrl(url);
    assert.ok(!after.includes('someone@example.test'), 'not scrubbed: ' + after);
    assert.ok(after.includes('k%3Dv'),
        'an escaped `=` was handed back as a literal, which can change how the ' +
        'component is classified on a later pass: ' + after);
});

check('a doubly-encoded form NAME is still reached, at the cost of a respelling', () => {
    // THIS PINS THE LESSER OF TWO DEFECTS, deliberately, and the test exists
    // because the better-looking fix leaks.
    //
    // The name half is decoded BLINDLY -- not through the multi-layer refusal
    // the value half uses. Review flagged that asymmetry, and giving the name
    // half the same refusal was tried: it leaks. Refusing a doubly-encoded name
    // leaves its raw bytes to `replaceAll`, and the enrolled needles cover a
    // value and its SINGLE-encoded spelling, so a twice-encoded name matches
    // neither and the address survives in the clear. Measured, not reasoned:
    // the attempted fix emitted `someone%2540example.test` untouched.
    //
    // The blind decode reaches it, because one decode is all the needle needs.
    // The cost is that the matching needle's replacement is already wire-form
    // and gets encoded again, so this half can come back one level deeper than
    // its own value half. A respelling beats a leak. The real fix is the nested
    // re-encode contract on #133.
    const EMAIL = 'someone@example.test';
    const har = harWithUrl('https://app.example.test/x#contact=' + EMAIL +
        '&' + encodeURIComponent(encodeURIComponent(EMAIL)) + '=1');
    pii.scrubPii(har);
    const after = urlOf(har);
    // The assertion that matters: the address is gone at EVERY decode depth.
    let probe = after;
    for (let i = 0; i < 3; i++) {
        assert.ok(!probe.includes(EMAIL),
            'the address survived at decode depth ' + i + ': ' + probe);
        probe = decodeURIComponent(probe);
    }
});

// --- 4. #479 did not regress --------------------------------------------

check('a query payload is still decoded before detection (#479)', () => {
    const url = 'https://api.example.test/graphql?variables=' + PAYLOAD;
    const after = scrubbedUrl(url);
    const q = /variables=([^&#]*)/.exec(after);
    assert.ok(q, 'the query parameter is missing after the scrub');
    assert.doesNotThrow(() => JSON.parse(decodeURIComponent(q[1])),
        'the query payload was corrupted: ' + after);
});

check('a space-bearing query value still round-trips with form semantics (#479)', () => {
    // The query keeps `+`-is-space, and this is the test that the decoder
    // parameter was threaded the right way round: if the query were given path
    // semantics, a detected value beside a `+` would come back with the `+`
    // intact where a space was meant, or vice versa.
    const url = 'https://api.example.test/s?q=a+b&contact=' +
        encodeURIComponent('someone@example.test');
    const har = harWithUrl(url);
    pii.scrubPii(har);
    const after = urlOf(har);
    assert.ok(!after.includes('someone@example.test'),
        'the email in the query was not scrubbed: ' + after);
    assert.ok(/[?&]q=a(\+|%20)b(&|$)/.test(after),
        'the untouched query value lost its space spelling: ' + after);
});

if (failures > 0) {
    console.error(`\npii-url-escape-scrub: ${failures} failure(s)`);
    process.exit(1);
}
console.log('pii-url-escape-scrub: all checks passed');
