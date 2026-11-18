#!/usr/bin/env node
/**
 * pii.js -- shared typed-PII detection + deterministic faker for the
 * web-api-discovery scrub pipeline (issue #46).
 *
 * Exports:
 *   detectPii(har, policy?)     -> [{ type, value, location }]
 *   fakeFor(type, original)     -> string (deterministic, obviously-fake)
 *   scrubPii(har, policy?)      -> { substitutions: [...], retained: [...] }
 *                                  (mutates har)
 *   PII_TYPES                   -> array of supported type strings
 *
 * `policy` is the MERGED scrub policy (`har-policy.loadPolicy`). It is
 * OPTIONAL and absent means the shipped defaults, so no caller breaks; passing
 * it is what makes a consuming project's `piiFields` and `cardIssuers` govern
 * the SCRUB as well as the gate (issue #334), its `classes` too (issue
 * #346), and its `identifierFields` (issue #360): an identity class set to
 * `off` is still DETECTED and still REPORTED -- in `retained`, as counts and
 * never values -- but is not replaced. That is the meaning `off` already
 * carries on the gate side, where a disabled finding is returned marked rather
 * than dropped. An identity value at a declared identifier field is reported
 * the same way, carrying `identifierField: true`, which is the mark the gate
 * puts on the same decision. Secret classes are exempt from the setting
 * entirely and can never reach the identifier rule; see `scrubSettingFor`.
 *
 * Determinism: seed = SHA-256(original).hexSlice(0, 16). Same input always
 * yields same fake. No external dependencies; pure Node stdlib.
 *
 * Substitutions returned by scrubPii contain only { type, originalHash,
 * replacement, locations }. The originalHash is the first 8 hex chars of
 * the SHA-256 -- never the plaintext original -- so the resulting
 * substitution table is safe to commit. (The table filenames themselves are
 * spelled once, in subs-destination.js, and are deliberately not restated
 * here -- see the single-definition check in sanitize-har-importable.test.js.)
 */

'use strict';

const crypto = require('crypto');

// The recorder's key for an unretained-request-body descriptor (#442),
// IMPORTED rather than spelled again. A second copy of a node name is how the
// scrub and the recorder end up disagreeing about which node exists, and the
// disagreement is silent in the direction that ships: the scrub simply never
// visits it. `request-body-descriptor.js` requires nothing, so naming it from
// here cannot make a cycle.
const { DESCRIPTOR_KEY: BODY_DESCRIPTOR_KEY } = require('../capture/request-body-descriptor.js');

const PII_TYPES = [
    'email', 'phone', 'person-name', 'street-address',
    'city', 'region', 'postal-code', 'country',
    'dob', 'ssn', 'credit-card', 'ip-address', 'geo-coordinates',
    'iban', 'mac-address', 'device-id'
];

// --- minimal embedded word lists (no external dependencies) ---
const FIRST_NAMES = [
    'Avery','Blair','Casey','Dakota','Emery','Finley','Gray','Hayden','Ira','Jules',
    'Kai','Lane','Morgan','Nico','Oakley','Parker','Quinn','Reese','Sage','Tatum',
    'Umi','Vesper','Wren','Xen','Yarrow','Zion','Ash','Briar','Cleo','Darcy',
    'Elliot','Frey','Gale','Harper','Indigo','Jess','Kit','Lior','Marin','Niles',
    'Olin','Pax','Rio','Sasha','Tyne','Uriah','Vance','Wynn','Yale','Zephyr'
];
const SURNAMES = [
    'Archer','Brooks','Carter','Doyle','Ellis','Fisher','Grant','Hayes','Irving','Jensen',
    'Kerr','Lowell','Mercer','Nash','Oates','Pratt','Quincy','Reeves','Stone','Tate',
    'Underwood','Vega','Walsh','Xiong','York','Zimmer','Abram','Blake','Crane','Dean',
    'Emerson','Frost','Gibson','Hale','Inman','Jordan','Knox','Lyle','Monroe','Noble',
    'Orton','Paige','Quill','Rhodes','Sterling','Thorne','Underhill','Vail','Wilde','Yates'
];
const STREETS = [
    'Birch','Cedar','Dogwood','Elm','Forest','Garnet','Hawthorn','Ivy','Juniper','Kestrel',
    'Larch','Maple','Nutmeg','Oak','Poplar','Quince','Rowan','Sycamore','Tamarack','Umbra',
    'Vine','Willow','Yarrow','Zephyr','Acacia','Beech','Cypress','Dunwood','Elderberry','Fern',
    'Greenway','Hollow','Iron','Jasper','Knoll','Linden','Mulberry','Northgate','Olive','Pinecrest',
    'Quail','Redwood','Spruce','Thistle','Upland','Violet','Wisteria','Yew','Zinnia','Aspen'
];
const CITIES = [
    'Aldenbrook','Bramblefield','Cinderwood','Dunmoor','Everwillow','Fallowcrest','Glenhaven','Hartshollow','Inglevale','Junipercove',
    'Kettlebrook','Larchmount','Mistford','Northwillow','Oakhurst','Pinecliff','Quietford','Ravenstead','Stonebriar','Thornehaven',
    'Underglen','Vesperton','Wickburn','Yarrowfield','Zephyrport','Ashendell','Briarholt','Cresthollow','Drystwood','Elderfen'
];
const REGIONS = ['ZZ','XA','XB','XC','XD','XE','XF','XG','XH','XI'];

// --- field-name dictionaries (case-insensitive match on JSON key tail) ---
//
// The names are DATA and live in `har-policy.default.json`, so the scrubber and
// both gates read one list and a consuming project can extend it. Each type
// carries `exact` (whole-key names), `tail` (trailing words that denote the
// type) and `qualifiers` -- the words allowed to precede an ambiguous tail, or
// the string `any` where the tail speaks for itself. See `fieldTypeFor`.
// One place recognises a value as a capture, and refuses it when it is not
// one (issue #423).
const harDocument = require('./har-document.js');
const harPolicy = require('./har-policy.js');
// ONE definition of "credit card", consumed rather than copied (issue #334).
// `har-shapes.js` is the gate; it requires an assigned issuer identifier at a
// length that issuer mints before calling a Luhn-valid run a card. This module
// drives a REPLACE, so holding a looser definition here did not leak -- it
// CORRUPTED, rewriting provider object ids into generated fake card numbers in
// committed references. A second copy of the predicate here would only drift
// again, so the predicate is imported and the issuer table is not duplicated.
const { hasAssignedIin, LEAK_PATTERNS, settingFor, isIdentifierShaped } = require('./har-shapes.js');
// ONE traversal of the encoding layers, consumed rather than copied (issue #24).
//
// This module is the THIRD engine. `har-nested.js` gave the gate and the secret
// scrubber one walk, one cap and one byte-preservation rule (#454); the identity
// axis kept a private descent that stopped at the first parsed JSON document, so
// a value whose sensitivity is knowable only from its FIELD NAME was unreachable
// two layers down -- while the shared walk delivered that same value as a whole
// leaf. Measured: the walk reached it at hops 1-7 and this pass acted only at
// hop 1.
//
// Shape evidence was never affected, which is why #24 is the INTERSECTION of
// name-only sensitivity and nesting rather than a general failure to descend:
// the flat text pass finds a shaped value at any depth, because it does not
// need the layer to be decoded to recognise it.
const { transformNested, isNamedLayer, MAX_DEPTH: NESTED_MAX_DEPTH } = require('./har-nested.js');

/**
 * One slot of the gate's own leak table, taken from the gate rather than
 * copied -- for the card slot, its PATTERN and its fake marker.
 *
 * A card predicate has two halves -- the pattern that finds candidates and the
 * check that validates them -- and unifying only the check leaves the other
 * half free to drift. It had. `har-shapes.js` carries lookarounds that keep a
 * digit run which is part of a DECIMAL NUMBER out of the card slot (#292/#293);
 * this module carried a bare 13-19 digit run between word boundaries, on the
 * reasoning that an assigned-IIN check makes those lookarounds redundant
 * because a fractional part begins with digits no issuer owns. That holds only
 * until the run happens to begin with an assigned IIN at a length that issuer
 * mints -- thirteen digits starting at `4` will do it -- at which point the
 * gate passes a price clean and this pass rewrites its integer part into a
 * fake card number. Corruption, in a case the gate already guarded, and
 * reachable anywhere `detectInString` scans: headers, cookies, query
 * parameters, the URL, or a non-JSON body.
 *
 * So the pattern is CONSUMED, not re-typed here with the lookarounds added by
 * hand -- which would only leave a third copy to drift on the next change.
 *
 * Sharing the RegExp object is safe: both sides reach it through
 * `String.prototype.match`, which resets `lastIndex` on a global pattern
 * before it scans, and neither call site is re-entrant. A missing pattern
 * throws at load rather than silently disabling card detection, because an
 * upstream rename must not be able to switch this pass off quietly.
 */
function gateSlot(name) {
    const found = LEAK_PATTERNS.find((p) => p.name === name);
    if (!found) {
        throw new Error(`pii.js: har-shapes.js no longer defines a "${name}" pattern`);
    }
    return found;
}

// The whole card slot -- pattern, and the marker that recognises this
// scrubber's OWN output. Every other detector below skips its fake before
// reporting (`@example.invalid`, the 555 area code, the 9XX SSN prefix, `ZZ00`,
// `06:F0:0D`, `192.0.2.`); the card slot did not, so re-scrubbing an already
// scrubbed capture churned one fake card into a different fake card. The gate
// has always ignored `4242...` for exactly this reason, and taking the test
// from it keeps the two from disagreeing about what a fake is either.
const GATE_CARD = gateSlot('credit-card');

const DEFAULT_PII_POLICY = harPolicy.loadDefaultPolicy();

// The secret axis, taken from the default policy rather than listed again here
// -- adding a secret class upstream must not require a second edit, and two
// lists are how the scrubber and the gate come to disagree.
const SECRET_KINDS = new Set(Object.keys(DEFAULT_PII_POLICY.classes.secret));

// The identity axis, read the same way and from the same place. Which class a
// kind belongs to is ESTABLISHED here rather than assumed from "not a secret":
// a kind the shipped policy names on neither axis has no class, and a decision
// that keys off `class === 'identity'` must not silently claim it.
const IDENTITY_KINDS = new Set(Object.keys(DEFAULT_PII_POLICY.classes.identity));

/**
 * Which class the SHIPPED policy puts a detector kind on, or null.
 *
 * Read from the default document, never from the merged one. A project may set
 * a kind's SETTING; it may not move a kind between axes, and reading the merged
 * classes here would make the axis itself project-controlled -- which is the
 * one lever that could walk a secret kind onto the identity path.
 */
function piiClassFor(type) {
    if (SECRET_KINDS.has(type)) return 'secret';
    if (IDENTITY_KINDS.has(type)) return 'identity';
    return null;
}

/**
 * Is this detection an identity value sitting at a field the policy declares
 * to hold object ids -- the question the GATE already asks?
 *
 * Asked THROUGH `har-shapes.isIdentifierShaped`, not re-derived here. That
 * function states the scope in one place (identity class only, secret never, a
 * resolved key path only) and owns the field-name extraction; a second copy of
 * it in this file is exactly the divergence that let the gate agree a value was
 * an object id while this pass rewrote it into a fake card number.
 *
 * `fieldKey` is the JSON key that DIRECTLY holds the scanned string, and it is
 * `null` for every value that has no structural key: headers, cookies, query
 * parameters, the request URL, a non-JSON body, and the elements of a top-level
 * array. A finding with no resolved path gets no suppression -- reading the
 * enclosing HAR node's name (`request.url`, `response.content`) instead would
 * decline a replacement on no evidence at all.
 */
function atIdentifierField(type, fieldKey, policy) {
    if (typeof fieldKey !== 'string' || fieldKey === '') return false;
    // The floor, restated on this path and not merely inherited. `piiClassFor`
    // already refuses a secret kind, and `isIdentifierShaped` refuses anything
    // that is not identity; a secret kind must fail BOTH, because a caller
    // cannot forget a check it never makes.
    if (SECRET_KINDS.has(type)) return false;
    return isIdentifierShaped({ class: piiClassFor(type) }, fieldKey, policy);
}

/**
 * What the policy says about REPLACING one PII type -- `gate`, `advise` or
 * `off` (issue #346).
 *
 * `off` means DETECT, REPORT, DO NOT ACT. That is not a new semantic invented
 * for the scrubber: it is exactly what `off` already means on the gate, where
 * `findLeaks` still returns a disabled finding carrying `setting: 'off'` and
 * `gating: false`, and `blocksLeak` declines to fail on it. The finding has
 * left the gate, not the report. Mirroring it here is the point -- a second
 * meaning of `off` would be the same drift that produced the card predicate,
 * the fake markers, `piiFields` and `cardIssuers` in turn.
 *
 * `gate` and `advise` both replace. The distinction between them is about
 * whether a SURVIVING value fails the build, which is the gate's question; on
 * this side there is nothing to distinguish, because a replaced value cannot
 * survive either way. Only `off` reaches the scrub as a decision.
 *
 * THE FLOOR, ENFORCED HERE AND NOT ONLY IN THE LOADER. The loader refuses a
 * project file that lowers a secret class, and that is the check operators
 * meet. It is not the check that makes the guarantee true: this function is
 * reachable with a policy object nobody loaded. So a secret kind resolves to
 * `gate` before any lookup happens, on EITHER axis -- a caller cannot forget a
 * check it never makes, and a secret grants access, so it is removed
 * unconditionally. Per-value relief is a waiver, never a class setting.
 *
 * An identity type is read from `classes.identity` alone, so a setting parked
 * on the secret axis loosens nothing. An absent policy, or a type the policy
 * does not name, is `gate` -- `settingFor`'s own default, and the only safe
 * reading: a missing file must never be the thing that quietly stops a scrub.
 */
function scrubSettingFor(policy, type) {
    if (SECRET_KINDS.has(type)) return 'gate';
    return settingFor(policy, 'identity', type);
}

/**
 * The identity classes this policy turns off, sorted.
 *
 * Read from the policy rather than from what a capture happened to contain:
 * disabling a class is a standing decision to publish personal data, and it is
 * true of the run whether or not this particular capture exercised it.
 */
function disabledIdentityClasses(policy) {
    const identity = (policy && policy.classes && policy.classes.identity) || {};
    return Object.keys(identity)
        .filter((kind) => identity[kind] === 'off' && !SECRET_KINDS.has(kind))
        .sort();
}

function compileDictionaries(policy) {
    const source = (policy && policy.piiFields) || DEFAULT_PII_POLICY.piiFields;
    return Object.keys(source).map((type) => {
        const d = source[type];
        return [type, {
            exact: new Set((d.exact || []).map((n) => n.toLowerCase())),
            tail: new Set((d.tail || []).map((n) => n.toLowerCase())),
            qualifiers: d.qualifiers === 'any' ? 'any'
                : new Set((d.qualifiers || []).map((n) => n.toLowerCase())),
        }];
    });
}

const DEFAULT_DICTIONARIES = compileDictionaries(null);
function piiDictionaries(policy) {
    return policy && policy.piiFields ? compileDictionaries(policy) : DEFAULT_DICTIONARIES;
}

// Retained for the callers that still read it directly; derived from the
// policy so there is one source rather than two that can disagree.
const FIELD = Object.fromEntries(
    DEFAULT_DICTIONARIES.map(([type, d]) => [type, new Set([...d.exact])]));

/**
 * Split a JSON key into its words.
 *
 * `first_name`, `first-name`, `firstName`, `FirstName` and `billing.city` are
 * the same field wearing five conventions, and snake_case is dominant in the
 * APIs this targets -- which is why an exact-match lookup missed almost all of
 * them. A trailing ordinal is dropped: `address_line_1` and `phone_2` are how
 * an API spells repetition, not part of the field's name.
 */
function keyWords(key) {
    if (typeof key !== 'string') return [];
    const words = key
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
        .split(/[^A-Za-z0-9]+/)
        .filter(Boolean)
        .map((w) => w.toLowerCase());
    while (words.length > 1 && /^\d+$/.test(words[words.length - 1])) words.pop();
    // ...and an ordinal fused onto the last word, which is the same repetition
    // spelled without a separator: `addr1`, `addr2`, `address2`, `phone2`.
    // Only the trailing digits go, and only when letters remain, so `sha256`
    // becomes `sha` -- a word no dictionary lists -- rather than anything.
    if (words.length > 0) {
        const fused = /^([a-z]+)\d+$/.exec(words[words.length - 1]);
        if (fused) words[words.length - 1] = fused[1];
    }
    return words;
}

/**
 * Which PII type a JSON key denotes, or null.
 *
 * Three rules, in order of confidence:
 *
 *   1. The whole key, words joined, is a name the policy lists exactly.
 *   2. A trailing run of words joins to a listed `tail` token, and the type
 *      accepts any qualifier before it.
 *   3. Same, but the type qualifies its tail, and the immediately preceding
 *      word is on that type's allowlist.
 *
 * Why rule 3 exists, and why the allowlist runs that way round: this function
 * does not report a value, it REPLACES it. A key ending in the word `name` is
 * overwhelmingly not a person -- `file_name`, `event_name`, `column_name`,
 * `bucket_name` -- and scrubbing those writes `Avery Brooks` over a filename,
 * corrupting the payload the reference exists to document, undetectably, after
 * the fact. `address` is sharper still: `ip_address` and `email_address` are
 * not streets.
 *
 * A DENYLIST of non-person qualifiers would fail open -- every convention
 * nobody anticipated becomes a corrupted field, silently, in the artifact
 * itself. The allowlist fails toward a MISS instead.
 *
 * BE HONEST ABOUT WHAT A MISS COSTS. It is tempting to say the literal pass
 * catches it, and for the operator's own identifiers it does -- but
 * `.har-profile.json` holds *the operator's* account id, display name and
 * email, by design and by enforcement. It knows nothing about the third
 * parties who make up most of the PII in a real capture: a friend list, a
 * search result, an order recipient. `email`, `phone`, `ssn` and `credit-card`
 * have shape detectors that catch those regardless of field name; `person-name`,
 * `street-address`, `city` and `region` DO NOT, because no shape distinguishes
 * a person's name from any other short string.
 *
 * So a missed name field is a real residual risk, not a covered one. The
 * allowlist is still the right trade -- a corrupted reference is undetectable
 * after the fact and destroys the artifact's whole purpose, while a miss is
 * visible to anyone who reads the capture and fixable by adding a qualifier --
 * but it is a trade, not a free lunch, and the qualifier list is meant to grow
 * as real field names turn up.
 */
function fieldTypeFor(key, policy) {
    const words = keyWords(key);
    if (words.length === 0) return null;
    const dictionaries = piiDictionaries(policy);
    const joined = words.join('');

    for (const [type, dict] of dictionaries) {
        if (dict.exact.has(joined)) return type;
    }

    // Longest tail first, so `dateofbirth` wins over a bare `birth` and
    // `postalcode` over `code`.
    for (let start = 0; start < words.length; start++) {
        const tail = words.slice(start).join('');
        for (const [type, dict] of dictionaries) {
            if (!dict.tail.has(tail)) continue;
            if (dict.qualifiers === 'any') return type;
            // No bare-word shortcut here. Returning a match merely because the
            // tail began at word zero skipped the allowlist entirely -- which
            // stayed invisible only because every tail token also happened to
            // be an `exact` name, a coincidence nothing enforces and the merge
            // explicitly allows a project to break. A legitimate bare-word
            // match is rule 1's job.
            if (start > 0 && dict.qualifiers.has(words[start - 1])) return type;
        }
    }
    return null;
}

function fieldType(key) {
    return fieldTypeFor(key, null);
}

// --- regex detectors (context-free) ---
// Email is already handled by sanitize-har.js's legacy pattern, but we also
// detect it here so detectPii() reports it for the substitutions store.
const RE = {
    // RFC 5321 caps the local part at 64 characters and the domain at 255.
    // The bounds are not pedantry: an unbounded `[chars]+@` backtracks
    // quadratically over a long run that never reaches an `@`, and capture
    // bodies are routinely hundreds of KB.
    email:        /[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,255}\.[A-Za-z]{2,}/g,
    // E.164, and the national spellings a US API actually emits. The
    // punctuated forms carry their own evidence -- a run grouped 3-3-4 with
    // separators is a phone number far more often than anything else -- so
    // they are safe to match without a field name.
    //
    // The lookarounds keep the match off a longer digit run: without them
    // `555-123-45678` and the tail of a formatted card would both match.
    phone:        /\+\d{10,15}\b/g,
    phonePunctuated: /(?<![\d-])(?:\+?1[ .-]?)?(?:\(\d{3}\)[ .-]?|\d{3}[ .-])\d{3}[ .-]\d{4}(?![\d-])/g,
    // Ten digits with no punctuation and no context is an order id, a product
    // code, or a timestamp. It is only a phone number when the field says so
    // -- the credit-card lesson, applied before it bites rather than after.
    phoneBare:    /^\+?1?\d{10}$/,
    ssn:          /\b\d{3}-\d{2}-\d{4}\b/g,
    // The GATE's pattern, shared rather than copied -- see `gatePattern`.
    creditDigits: GATE_CARD.re,
    ipv4:         /\b(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}\b/g,
    // Same treatment, same reason: a maximal run of colon-separated hex groups,
    // with the count deciding.
    //
    // This one is PRE-EXISTING. main carves two eight-GROUP matches out of a
    // twenty-group certificate fingerprint and then replaces each with a fake
    // IPv4 sentinel, so the fingerprint comes back as
    // `192.0.2.234:192.0.2.4:AB:CD:EF:01`. The IPv4-looking output is the
    // FAKE, not the match -- worth saying, because a reviewer read the
    // original wording as an IPv4 example misfiled under IPv6. Fixed here
    // because it is the identical defect in the identical value, and shipping
    // only the MAC half would leave the reproduction case still corrupting.
    //
    // ACCEPTED LIMIT, pre-existing and unchanged: `::` compression is not
    // matched, so a compressed IPv6 address is missed. main's exact-seven-colon
    // pattern did not handle it either. A miss, not a corruption.
    ipv6:         /\b(?:[A-Fa-f0-9]{1,4}:)+[A-Fa-f0-9]{1,4}\b/g,
    isoDate:      /^\d{4}-\d{2}-\d{2}$/,
    // An IBAN is two country letters, two check digits, then up to 30
    // alphanumerics -- and a mod-97 checksum over the lot. The arithmetic is
    // what makes this safe to match without a field name: shape alone would be
    // another "Luhn-valid digit run", firing on identifiers an API just mints.
    iban:         /\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b/g,
    // A MAC is a maximal run of EXACTLY six hex pairs. Match the whole run and
    // let the count decide -- do not try to express "six" in the pattern.
    //
    // Two wrong answers were tried first. A bare six-pair pattern carves a MAC
    // out of a TLS thumbprint (which is hex pairs too) and the scrub then
    // destroys real protocol evidence. Anchoring it with a hex-character
    // lookbehind fixes that and silently breaks `device:AA:BB:...`, because
    // `a` through `f` are ordinary letters and the anchor cannot tell the last
    // letter of an English word from the second digit of a hex pair.
    //
    // Matching the run and counting says what a MAC actually is, so neither
    // failure is reachable.
    //
    // ACCEPTED LIMITS -- found by review, not oversights, and both are MISSES
    // rather than corruptions:
    //
    //  1. Mixed separators (`AA:BB-CC:DD:EE:FF`) split into two sub-runs,
    //     neither of which is six pairs, so nothing is reported. No tool emits
    //     a MAC with inconsistent separators.
    //  2. A MAC glued directly to trailing hex with no delimiter
    //     (`...EE:FFcafe1234`) leaves the closing `\b` unable to fire. This is
    //     inherent to any word-bounded maximal-run definition.
    //
    // Both need an input no real capture produces, and both fail toward a miss
    // that the literal and name controls still cover. If either turns up in a
    // real capture, the answer is a proper tokeniser, not another lookaround --
    // the lookaround is what produced the `device:` regression in the first
    // place.
    mac:          /\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2})+\b|\b[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2})+\b/g,
    uuid:         /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/
};

function luhnOk(s) {
    let sum = 0, alt = false;
    for (let i = s.length - 1; i >= 0; i--) {
        let d = s.charCodeAt(i) - 48;
        if (d < 0 || d > 9) return false;
        if (alt) { d *= 2; if (d > 9) d -= 9; }
        sum += d; alt = !alt;
    }
    return sum % 10 === 0;
}

function hashOf(s) {
    return crypto.createHash('sha256').update(String(s)).digest('hex');
}
function hashPrefix(s) { return hashOf(s).slice(0, 8); }
function seed(s) { return hashOf(s); }
function intFromHex(hex, start, len) {
    return parseInt(hex.slice(start, start + len), 16);
}

// --- deterministic faker ---
function fakeFor(type, original) {
    const h = seed(String(original));
    switch (type) {
        case 'email':
            return `user-${h.slice(0, 8)}@example.invalid`;
        case 'phone': {
            const tail = (intFromHex(h, 0, 8) % 10000000).toString().padStart(7, '0');
            return `+1555${tail}`;
        }
        case 'ssn': {
            const a = 900 + (intFromHex(h, 0, 4) % 100);   // 900-999
            const b = (intFromHex(h, 4, 4) % 100).toString().padStart(2, '0');
            const c = (intFromHex(h, 8, 4) % 10000).toString().padStart(4, '0');
            return `${a}-${b}-${c}`;
        }
        case 'iban':
            // ZZ is unassigned, and the check digits are left as 00 so the
            // value fails its own mod-97 -- a fake that validated would be
            // indistinguishable from a real account number.
            return `ZZ00${h.slice(0, 18).toUpperCase()}`;
        case 'mac-address': {
            const octets = [];
            for (let i = 0; i < 3; i++) octets.push(h.slice(i * 2, i * 2 + 2).toUpperCase());
            return `06:F0:0D:${octets.join(':')}`;
        }
        case 'device-id':
            return `DEADBEEF-${h.slice(0, 4)}-4${h.slice(4, 7)}-8${h.slice(7, 10)}-${h.slice(10, 22)}`.toUpperCase();
        case 'credit-card': {
            // Build 15-digit body then compute Luhn check digit -> 16 total.
            let body = '4242';
            for (let i = 0; i < 11; i++) {
                body += ((intFromHex(h, i * 2, 2)) % 10).toString();
            }
            // Compute Luhn check digit for body + '0', then derive correct check.
            let sum = 0, alt = true;
            for (let i = body.length - 1; i >= 0; i--) {
                let d = body.charCodeAt(i) - 48;
                if (alt) { d *= 2; if (d > 9) d -= 9; }
                sum += d; alt = !alt;
            }
            const check = (10 - (sum % 10)) % 10;
            return body + String(check);
        }
        case 'person-name': {
            const f = FIRST_NAMES[intFromHex(h, 0, 4) % FIRST_NAMES.length];
            const l = SURNAMES[intFromHex(h, 4, 4) % SURNAMES.length];
            return `${f} ${l}`;
        }
        case 'street-address': {
            const num = 100 + (intFromHex(h, 0, 4) % 9900);
            const street = STREETS[intFromHex(h, 4, 4) % STREETS.length];
            return `${num} ${street} St`;
        }
        case 'city':
            return CITIES[intFromHex(h, 0, 4) % CITIES.length];
        case 'region':
            return REGIONS[intFromHex(h, 0, 4) % REGIONS.length];
        case 'postal-code':
            return (90000 + (intFromHex(h, 0, 4) % 10000)).toString();
        case 'country':
            return 'ZZ';
        case 'dob': {
            const year = 1950 + (intFromHex(h, 0, 4) % 50);
            const month = 1 + (intFromHex(h, 4, 4) % 12);
            const day = 1 + (intFromHex(h, 8, 4) % 28);
            return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
        }
        case 'ip-address': {
            // 192.0.2.0/24 is RFC 5737 TEST-NET-1, never routable.
            return `192.0.2.${intFromHex(h, 0, 2) % 254 + 1}`;
        }
        case 'geo-coordinates':
            return 0;
        default:
            return `<REDACTED-${h.slice(0, 8)}>`;
    }
}

// --- detection ---
function pushDetection(out, type, value, location, identifierField) {
    const detection = { type, value, location };
    // Present only when true, and never removed from the detection list. The
    // finding stays visible and marked -- exactly what the gate does with one
    // (`blocksLeak` reads the mark rather than the finding being dropped).
    // A loosening that made findings disappear would be an invisible one.
    if (identifierField) detection.identifierField = true;
    out.push(detection);
}

// The scrubber emits `+1555XXXXXXX` and nothing else, so the exemption is that
// EXACT string -- unpunctuated, E.164, no spaces.
//
// Normalising punctuation away before this test looks tidier and is wrong: it
// turns a real `+1 (555) 123-4567` into the fake's shape and exempts it. An
// exemption is a hole by construction, so it must recognise our own output and
// not one character more.
/**
 * ISO 13616 mod-97: move the first four characters to the end, map letters to
 * two-digit numbers, and the whole value modulo 97 must be 1.
 *
 * This is the entire reason `iban` may match without a field name. A shape
 * that fires on `AB12...` and nothing else would be the credit-card mistake
 * again -- the checksum is what turns a shape into evidence.
 */
function ibanChecksumOk(value) {
    const s = String(value).toUpperCase();
    if (s.length < 15 || s.length > 34) return false;
    const rearranged = s.slice(4) + s.slice(0, 4);
    let remainder = 0;
    for (const ch of rearranged) {
        const code = ch.charCodeAt(0);
        let part;
        if (code >= 48 && code <= 57) part = String(code - 48);
        else if (code >= 65 && code <= 90) part = String(code - 55);
        else return false;
        for (const digit of part) remainder = (remainder * 10 + (digit.charCodeAt(0) - 48)) % 97;
    }
    return remainder === 1;
}

// The scrubber's own output, recognised exactly. `ZZ` is an unassigned country
// code and `06:F0:0D` is an arbitrary locally administered MAC prefix -- NOT
// `02:00:00`, which is the textbook one that real virtualisation and sandbox
// tools emit, and which therefore made real addresses look already-scrubbed.
// A fake marker must be a shape the world does not already use, so neither fake can
// be mistaken for a real value -- and neither is re-detected as a leak.
function isFakeIban(v) { return /^ZZ00/i.test(String(v)); }
// A delimited hex run is a MAC only at exactly six pairs, and an IPv6 address
// only at exactly eight groups. Anything longer is a fingerprint, a digest or
// a key -- protocol evidence, not an identifier.
function isMacRun(m) { return String(m).split(/[:-]/).length === 6; }
function isIpv6Run(m) { return String(m).split(":").length === 8; }

function isFakeMac(v) { return /^06[:-]F0[:-]0D[:-]/i.test(String(v)); }
function isFakeDeviceId(v) { return /^DEADBEEF-/i.test(String(v)); }

function isFakePhone(value) {
    return /^\+1555\d{7}$/.test(String(value));
}

/**
 * The context-free shape detectors.
 *
 * `fieldKey` is the JSON key directly holding `str`, or null/absent when there
 * is none -- see `atIdentifierField`. It marks findings, and never suppresses
 * one.
 */
function detectInString(str, entryIndex, loc, out, policy, fieldKey) {
    if (typeof str !== 'string' || str.length === 0) return;
    const atId = (type) => atIdentifierField(type, fieldKey, policy);
    // emails
    (str.match(RE.email) || []).forEach(m => {
        // skip already-fake markers
        if (/@example\.invalid$/i.test(m)) return;
        pushDetection(out, 'email', m, { entryIndex, ...loc }, atId('email'));
    });
    // phones -- E.164 and the punctuated national spellings
    for (const re of [RE.phone, RE.phonePunctuated]) {
        (str.match(re) || []).forEach(m => {
            if (isFakePhone(m)) return;
            pushDetection(out, 'phone', m, { entryIndex, ...loc }, atId('phone'));
        });
    }
    // ssn
    (str.match(RE.ssn) || []).forEach(m => {
        if (/^9\d{2}-/.test(m)) return;
        pushDetection(out, 'ssn', m, { entryIndex, ...loc }, atId('ssn'));
    });
    // credit card -- the GATE's predicate, not a looser local one (issue #334).
    // An assigned issuer identifier at a length that issuer mints, AND Luhn.
    // Bare Luhn accepts ~10% of all digit runs by chance, and since this pass
    // replaces what it finds, that arithmetic accident was being written into
    // references as a fake card number over the top of a real object id.
    (str.match(RE.creditDigits) || []).forEach(m => {
        if (!hasAssignedIin(m, policy) || !luhnOk(m)) return;
        if (GATE_CARD.isFake(m)) return;
        pushDetection(out, 'credit-card', m, { entryIndex, ...loc }, atId('credit-card'));
    });
    // iban -- shape AND checksum, which is what licenses a context-free match
    (str.match(RE.iban) || []).forEach(m => {
        if (!ibanChecksumOk(m) || isFakeIban(m)) return;
        pushDetection(out, 'iban', m, { entryIndex, ...loc }, atId('iban'));
    });
    // mac address
    (str.match(RE.mac) || []).forEach(m => {
        if (!isMacRun(m) || isFakeMac(m)) return;
        pushDetection(out, 'mac-address', m, { entryIndex, ...loc }, atId('mac-address'));
    });
    // ipv4
    (str.match(RE.ipv4) || []).forEach(m => {
        if (/^192\.0\.2\./.test(m)) return; // fake range
        pushDetection(out, 'ip-address', m, { entryIndex, ...loc }, atId('ip-address'));
    });
    // ipv6
    (str.match(RE.ipv6) || []).forEach(m => {
        if (!isIpv6Run(m)) return;
        pushDetection(out, 'ip-address', m, { entryIndex, ...loc }, atId('ip-address'));
    });
}

function detectInValue(value, key, entryIndex, loc, out, policy) {
    const fType = fieldTypeFor(key, policy);
    // Context-driven detections (run first; do not also report as raw email/etc.)
    if (fType && typeof value === 'string' && value.length > 0) {
        if (fType === 'person-name') {
            pushDetection(out, 'person-name', value, { entryIndex, ...loc });
            return true;
        }
        if (fType === 'street-address') {
            pushDetection(out, 'street-address', value, { entryIndex, ...loc });
            return true;
        }
        if (fType === 'city')        { pushDetection(out, 'city',        value, { entryIndex, ...loc }); return true; }
        if (fType === 'region')      { pushDetection(out, 'region',      value, { entryIndex, ...loc }); return true; }
        if (fType === 'postal-code') { pushDetection(out, 'postal-code', value, { entryIndex, ...loc }); return true; }
        if (fType === 'country')     { pushDetection(out, 'country',     value, { entryIndex, ...loc }); return true; }
        if (fType === 'device-id') {
            // A UUID is the most common identifier shape in any API -- request
            // ids, trace ids, idempotency keys. There is no shape evidence
            // here at all, so the FIELD NAME is the only evidence, and a
            // pattern for this deliberately does not exist in har-shapes.js.
            if (RE.uuid.test(value) && !isFakeDeviceId(value)) {
                pushDetection(out, 'device-id', value, { entryIndex, ...loc });
                return true;
            }
            return false;
        }
        if (fType === 'phone') {
            // The field says phone, so a bare run is evidence enough. Anything
            // punctuated is caught context-free by detectInString anyway.
            const digits = value.replace(/[ .()-]/g, '');
            if (RE.phoneBare.test(digits) && !isFakePhone(digits)) {
                pushDetection(out, 'phone', value, { entryIndex, ...loc });
                return true;
            }
            return false;
        }
        if (fType === 'dob' && RE.isoDate.test(value)) {
            pushDetection(out, 'dob', value, { entryIndex, ...loc });
            return true;
        }
    }
    if ((fType === 'geo-lat' || fType === 'geo-lng') && typeof value === 'number') {
        const ok = fType === 'geo-lat' ? (value >= -90 && value <= 90)
                                        : (value >= -180 && value <= 180);
        if (ok && value !== 0) {
            pushDetection(out, 'geo-coordinates', value, { entryIndex, ...loc, axis: fType === 'geo-lat' ? 'lat' : 'lng' });
            return true;
        }
    }
    return false;
}

function walkJsonForDetect(node, key, entryIndex, jsonPath, out, policy, depth) {
    if (node === null || node === undefined) return;
    if (typeof node === 'string') {
        const handled = detectInValue(node, key, entryIndex, { jsonPath }, out, policy);
        if (!handled) detectInString(node, entryIndex, { jsonPath }, out, policy, key);
        // AND THEN ASK WHETHER THE LEAF IS ITSELF A PAYLOAD (issue #24).
        //
        // This is the whole of the identity axis's reach gap. The two calls
        // above are the flat detectors: they see this string as text, so a
        // SHAPED value in it is found at any depth. A value whose sensitivity
        // is knowable only from its FIELD NAME is not -- the name it travels
        // under is a key inside the string, and nothing here parsed the string.
        //
        // Returning without descending is what made a name-only value two hops
        // down invisible while the shared walk delivered it as a whole leaf.
        descendNestedLeaf(node, key, entryIndex, jsonPath, out, policy, depth || 0);
        return;
    }
    if (typeof node === 'number' || typeof node === 'boolean') {
        detectInValue(node, key, entryIndex, { jsonPath }, out, policy);
        return;
    }
    if (Array.isArray(node)) {
        node.forEach((v, i) => walkJsonForDetect(v, key, entryIndex, `${jsonPath}[${i}]`, out, policy, depth));
        return;
    }
    if (typeof node === 'object') {
        for (const k of Object.keys(node)) {
            walkJsonForDetect(node[k], k, entryIndex, jsonPath ? `${jsonPath}.${k}` : k, out, policy, depth);
        }
    }
}

/**
 * Detect inside the encoding layers a single string leaf may still carry.
 *
 * THE TRAVERSAL IS `har-nested.js`'s, not a fourth private one. The gate and
 * the secret scrubber have shared it since #454; this is the identity axis
 * joining them, so all three now agree about WHERE a value can be -- which is
 * the half of agreement that rule parity never covered.
 *
 * READ-ONLY: `visit` returns its input, the mode the gate already runs in, so
 * nothing here can rewrite the document. Replacement is the symmetric call in
 * `replaceNestedLeaf`, and detection and replacement must descend identically
 * or a value is found in one shape and rewritten in another.
 *
 * ACCEPTED LIMIT, pre-existing and disclosed rather than introduced: the shared
 * walk offers STRING leaves only, so a card or a coordinate serialised as a
 * JSON NUMBER inside a nested document is still missed by the name-keyed rules.
 * It was missed entirely before this function existed, so this is a strict
 * improvement; closing it means teaching the shared walk about scalar leaves,
 * which changes all three engines and belongs in its own change.
 */
function descendNestedLeaf(text, key, entryIndex, jsonPath, out, policy, depth) {
    if (typeof text !== 'string' || text.length === 0) return;
    transformNested(text, (node) => {
        const at = node.path || jsonPath;
        // Counted from the array's own growth, so the number means DETECTIONS
        // found below the surface and not merely layers visited. A count of
        // visits would report a figure on every nested capture and say nothing
        // about whether the deeper reach changed any outcome.
        // The LOCATION is the key path and nothing more. `node.depth` is used
        // for the counter below and deliberately NOT written into the finding:
        // a detection's shape is compared field-for-field by several suites,
        // and a new key on every nested finding would be a change to the
        // artifact contract bought for no information a reader needs -- the key
        // path already says how deep the value sat.
        const before = out.length;
        const handled = detectInValue(node.value, node.name, entryIndex,
            { jsonPath: at }, out, policy);
        if (!handled) {
            detectInString(node.value, entryIndex, { jsonPath: at }, out, policy, node.name);
        }
        countDeep(out, node.depth, out.length - before);
        return node.value;
    }, {
        depth,
        name: key,
        path: jsonPath,
        // A walk that stopped early has not looked everywhere, and one that
        // stops early while reporting nothing fails OPEN -- the D3 half of
        // #454. The gate reports its own early stop; so must this.
        onDepthLimit: () => countDepthLimited(out),
    });
}

/**
 * The two run-wide counters, carried ON the detections array.
 *
 * WHY NOT A PARAMETER. `detectPiiInEntry(entry, index, policy, out)` is the
 * entry point the streaming scrub (#10) calls once per entry, and `out` is
 * already the one object both it and the whole-document path thread. A fifth
 * parameter would be a fifth thing a future caller can forget to pass, and a
 * counter nobody passed reads as a clean walk -- the same fail-open shape this
 * change exists to remove. Counting on the array keeps the numbers travelling
 * with the detections they describe.
 *
 * Both are counts. Neither can carry a value.
 */
function countDeep(out, depth, added) {
    if (depth > 1 && added > 0) out.deepDetections = (out.deepDetections || 0) + added;
}
function countDepthLimited(out) {
    out.depthLimited = (out.depthLimited || 0) + 1;
}

/**
 * @param {object} har
 * @param {object} [policy] the MERGED scrub policy, as `har-policy.loadPolicy`
 *   returns it. OPTIONAL: absent means the shipped defaults, which is what
 *   every caller got before issue #334 and what the direct-call test suites
 *   still expect. Supplying it is what makes a consuming project's `piiFields`
 *   and `cardIssuers` reach the SCRUBBER and not only the gate -- before this
 *   parameter existed, they were validated, merged, loaded, and then never
 *   consulted on this side, so the whole project-policy surface was inert here.
 */
function detectPii(har, policy) {
    const out = [];
    // Recognised, not folded (issue #423). Every path here comes through
    // sanitize-har's guarded read, so this cannot fire in practice -- which is
    // the point: the day a new caller forgets the read, the scrub says so
    // instead of reporting a document it never walked as carrying no PII.
    const entries = harDocument.entriesOf(har, 'the capture being scrubbed');
    entries.forEach((entry, entryIndex) => detectPiiInEntry(entry, entryIndex, policy, out));
    return out;
}

/**
 * Detection for ONE entry, appended to `out`. `detectPii` is this over every
 * entry; the streaming scrub (issue #10) calls it one entry at a time, so the
 * two cannot disagree about what an entry holds.
 */
function detectPiiInEntry(entry, entryIndex, policy, out) {
    {
        // headers
        const allHeaders = [
            ...((entry.request && entry.request.headers) || []).map(h => ({ ...h, _ctx: 'request' })),
            ...((entry.response && entry.response.headers) || []).map(h => ({ ...h, _ctx: 'response' }))
        ];
        for (const h of allHeaders) {
            if (!h || typeof h.value !== 'string') continue;
            detectInString(h.value, entryIndex, { headerName: h.name, headerCtx: h._ctx }, out, policy);
        }
        // cookies, both directions. The raw `Cookie` HEADER sweep caught some
        // of this incidentally, and incidentally is not a control: the
        // structured arrays are where a cookie's name and value are actually
        // addressable, and `response.cookies` was never scanned at all.
        //
        // The NAME gates the value exactly as a JSON key does, so
        // `billing_city` in a cookie is classified like `billing_city` in a
        // body. Session handles (`_ga`, `sessionid`) tokenise to words no PII
        // dictionary lists, so they fall through to the shape detectors --
        // which is right: they are secrets by NAME, and har-secrets.js owns
        // them. Running one through the PII faker would write a fake city over
        // a session token.
        const allCookies = [
            ...((entry.request && entry.request.cookies) || []).map(c => ({ ...c, _ctx: 'request' })),
            ...((entry.response && entry.response.cookies) || []).map(c => ({ ...c, _ctx: 'response' })),
        ];
        for (const c of allCookies) {
            if (!c || typeof c.value !== 'string') continue;
            const loc = { cookieName: c.name, cookieCtx: c._ctx };
            const handled = detectInValue(c.value, c.name, entryIndex, loc, out, policy);
            if (!handled) detectInString(c.value, entryIndex, loc, out, policy);
        }

        // query string
        const qs = (entry.request && entry.request.queryString) || [];
        for (const q of qs) {
            if (!q || typeof q.value !== 'string') continue;
            detectInString(q.value, entryIndex, { queryParam: q.name }, out, policy);
        }
        // request URL
        if (entry.request && typeof entry.request.url === 'string') {
            detectInUrl(entry.request.url, entryIndex, out, policy);
        }
        // request body
        if (entry.request && entry.request.postData && typeof entry.request.postData.text === 'string') {
            tryWalkJsonText(entry.request.postData.text, entryIndex, 'request.postData', out, policy);
        }
        // response body
        if (entry.response && entry.response.content && typeof entry.response.content.text === 'string') {
            tryWalkJsonText(entry.response.content.text, entryIndex, 'response.content', out, policy);
        }
        // The unretained-request-body descriptor (#442). A `filename` IS
        // captured data -- `emily-watson-birthday-2019.jpg` carries a person,
        // a date and an occasion -- so the descriptor must be scrubbed like
        // any other captured value and must NOT become a channel that bypasses
        // the scrub. This walk is a SELECTIVE node list, not a generic descent,
        // so a new node is invisible to it until it is named here; the legacy
        // regex pass in `sanitize-har.js` walks the whole document and would
        // have caught an email- or card-shaped filename, but a person's NAME is
        // only ever found by THIS pass, which is context-driven.
        const descriptor = entry.request && entry.request[BODY_DESCRIPTOR_KEY];
        if (descriptor && typeof descriptor === 'object') {
            walkJsonForDetect(descriptor, null, entryIndex,
                `request.${BODY_DESCRIPTOR_KEY}`, out, policy);
        }
    }
    return out;
}

// How many nested layers to unwrap -- THE SHARED CAP, not a second one.
//
// This was a private `4` while `har-nested.js` held `8`. Two engines, two
// numbers, one name for the idea, which is the exact hazard that module's own
// header warns about: "Leaving the gate uncapped while the scrubber capped at 3
// is what reintroduces this bug one layer further down; two different caps are
// two different reaches wearing the same name." The identity axis was the
// engine still wearing the name.
//
// Taking the shared value rather than restating it means a change to the cap
// moves all three engines at once. It also RAISES this axis's reach from 4 to
// 8, so values nested deeper than the old private bound are now detected and
// replaced where previously they went to the plain-text scanner. That increase
// is reported on every run rather than left to be discovered in a diff -- see
// `deepDetections` below and the NOTE `sanitize-har.js` prints from it.
const FORM_DEPTH_LIMIT = NESTED_MAX_DEPTH;

/**
 * The `name=value` pairs of an `application/x-www-form-urlencoded` body, or
 * null when the text is not one.
 *
 * DELIBERATELY STRICT, because a false positive here would route a response
 * body through the form path and re-encode it. Every `&`-separated part must
 * carry an `=`, every name must be a bare token, and the whole text must be
 * free of whitespace -- which a minified JS bundle or an HTML document is not,
 * and `&amp;` entities are not (they yield a part with no `=`).
 */
function formEncodedPairs(text) {
    if (typeof text !== 'string' || text.length === 0 || text.length > 8 * 1024 * 1024) return null;
    // Whitespace or a control character. A real form body has neither; a minified
    // JS bundle, an HTML document and pretty-printed JSON all have the first.
    // Written as ESCAPES on purpose -- the literal bytes make the file read as
    // binary to grep and are invisible in review.
    if (/[\s\u0000-\u001f]/.test(text)) return null;
    const parts = text.split('&');
    const pairs = [];
    for (const part of parts) {
        // An EMPTY segment is RECORDED, not rejected and not dropped. A
        // trailing `&` is ordinary serializer output, and rejecting the body
        // over one sent the whole thing back to the plain-text path --
        // reinstating, for those bodies, exactly the defect this branch exists
        // to remove. Carrying it through the rebuild instead of skipping it is
        // what keeps a body whose delimiters are redundant byte-identical when
        // some other parameter changes.
        if (part.length === 0) { pairs.push({ empty: true, name: '', raw: '' }); continue; }
        const eq = part.indexOf('=');
        if (eq <= 0) return null;
        const name = part.slice(0, eq);
        if (!/^[A-Za-z0-9_.\-[\]%]+$/.test(name)) return null;
        pairs.push({ name, raw: part.slice(eq + 1) });
    }
    return pairs.some((x) => !x.empty) ? pairs : null;
}

/**
 * Percent-decode a URL-ish value, LENIENTLY, and never fail.
 *
 * `plusIsSpace` is the whole reason this takes a parameter, and getting it
 * wrong would corrupt a path to fix a payload. `+` means space in
 * `application/x-www-form-urlencoded` DATA -- a body or a query string -- and
 * means a literal `+` everywhere else in a URL. Decoding a path segment with
 * form semantics would turn a legitimate `+` into a space, and re-encoding it
 * would emit `%20`: a changed URL, in the one direction issue #22's own scope
 * note warns about.
 *
 * A MALFORMED ESCAPE DOES NOT DISQUALIFY THE VALUE, and getting this wrong cost
 * two rounds. Returning null and SKIPPING the parameter hid every other secret
 * in it from detection -- a leak the plain-text path did not have. Returning
 * null and processing the RAW text instead put the value back on the exact
 * path this branch exists to remove: the still-encoded `%22` beside it reads as
 * two payload digits again, and the replacement then gets percent-encoded a
 * second time on the way out, so the output is both corrupted AND
 * double-escaped.
 *
 * Decoding what is well-formed and passing through what is not gives detection
 * real delimiters, keeps every byte of the value in scope, and leaves the
 * replacement on decoded text where re-encoding is correct.
 *
 * The one visible effect: a value that BOTH contains a malformed escape AND has
 * something replaced in it comes back with that escape normalised (`%zz` ->
 * `%25zz`). Only a value that was already being rewritten pays it, and a
 * well-formed escape is a better artifact than the malformed one it replaces.
 */
function percentDecode(raw, plusIsSpace, offsets) {
    let out = '';
    // `offsets[k]` is the index in `raw` where decoded character `k` began, so a
    // caller can map a position in the decoded text back to the bytes it came
    // from. An escape run that decodes to several characters records the RUN's
    // start for each of them, which maps a boundary inside the run to the whole
    // run -- the safe direction, since the run is then re-encoded as a unit.
    const mark = (n, at) => { if (offsets) { for (let k = 0; k < n; k++) offsets.push(at); } };
    for (let i = 0; i < raw.length; i++) {
        const c = raw[i];
        if (c === '+') { mark(1, i); out += plusIsSpace ? ' ' : '+'; continue; }
        if (c !== '%') { mark(1, i); out += c; continue; }
        const hex = raw.slice(i + 1, i + 3);
        if (!/^[0-9A-Fa-f]{2}$/.test(hex)) { mark(1, i); out += '%'; continue; }
        // A multi-byte UTF-8 sequence has to decode as a unit, so hand the
        // whole run of escapes to the real decoder and fall back to literals
        // when it is not valid UTF-8.
        let j = i, run = '';
        while (j < raw.length && raw[j] === '%' && /^[0-9A-Fa-f]{2}$/.test(raw.slice(j + 1, j + 3))) {
            run += raw.slice(j, j + 3);
            j += 3;
        }
        let piece;
        try { piece = decodeURIComponent(run); }
        catch (_) { piece = run; }
        mark(piece.length, i);
        out += piece;
        i = j - 1;
    }
    if (offsets) offsets.push(raw.length);
    return out;
}

/** A URL path segment or fragment: `+` is a literal `+`. */
function urlDecode(raw) { return percentDecode(raw, false); }


/**
 * A URL, split into the four places a value can sit: origin, path, query and
 * fragment.
 *
 * WHY THE QUERY BRANCH EXISTS (issue #479, found in review). A query string is
 * percent-encoded form data that happens to live in a URL, and it was going
 * straight to the plain-text scanner -- so every failure this module's form
 * branch removes was still fully reachable through `request.url`. GraphQL over
 * GET puts an entire JSON payload there, which is precisely the shape that
 * turns an escape's digits into part of an adjacent value.
 *
 * WHY IT IS NO LONGER ONLY THE QUERY (issue #22). #479 fixed the two shapes
 * that are form data and DISCLOSED the rest as a known limit: the origin, path
 * and fragment stayed on the plain-text pass. Some REST APIs put a JSON or
 * base64 blob in a path SEGMENT rather than a query, and there the original
 * corruption was fully reachable -- `%22`'s digits completed a 14-digit id into
 * a Luhn-valid card-length run and the 16-digit fake overwrote the escape, so
 * `%22` became `%42` and the segment decoded with a `B` where its delimiting
 * quote had been.
 *
 * The general statement of the defect is not about form data at all:
 *
 *     percent-encoded text scanned as plain text has its escapes read as
 *     payload.
 *
 * So the fix is stated the same way -- decode a percent-encoded run wherever
 * it occurs, and let replacement act on DECODED text only. Escape-awareness
 * then costs nothing and is not a second mechanism: decoded text contains no
 * `%XX` for a write to land inside, and the re-encode happens afterwards over
 * a whole component. A decode WITHOUT that write guard would have relocated
 * the corruption rather than removed it, which is why the two always travel
 * together here.
 *
 * THE ORIGIN IS STILL STRUCTURE, NOT PAYLOAD. A scheme and an authority are
 * not a place a provider hides a JSON document, and a `/` inside the authority
 * would be the path's first delimiter rather than a character of it. So the
 * origin keeps the plain-text treatment and ends at the first `/` after the
 * scheme. A URL with no `://` is read as all path, which is the tolerant
 * reading -- this module never rejects a URL it failed to parse, because
 * refusing one would hide every value in it.
 *
 * ONE COST, STATED. The path is scanned per SEGMENT rather than as one string,
 * so a value that spanned a `/` is no longer matched. That is a tightening: a
 * `/` is a delimiter, and a digit run reaching across one was the same
 * accidental-length reading this fix exists to stop.
 */
function splitUrl(url) {
    const hash = url.indexOf('#');
    const head = hash < 0 ? url : url.slice(0, hash);
    const fragment = hash < 0 ? null : url.slice(hash + 1);
    const q = head.indexOf('?');
    const beforeQuery = q < 0 ? head : head.slice(0, q);
    const query = q < 0 ? null : head.slice(q + 1);

    const scheme = beforeQuery.indexOf('://');
    let origin = '';
    let pathText = beforeQuery;
    if (scheme >= 0) {
        const slash = beforeQuery.indexOf('/', scheme + 3);
        origin = slash < 0 ? beforeQuery : beforeQuery.slice(0, slash);
        pathText = slash < 0 ? '' : beforeQuery.slice(slash);
    } else if (/^[A-Za-z][A-Za-z0-9+.-]*:/.test(beforeQuery)) {
        // A NON-HIERARCHICAL URL HAS NO PATH TO SEGMENT. `mailto:` and
        // `data:` carry a scheme with no `//` authority, so looking for the
        // first `/` after `://` finds nothing and the whole URL reads as one
        // path segment. Re-encoding that segment then escapes the delimiters
        // that give it its meaning -- `mailto:` became `mailto%3A`, and a
        // `data:` URL lost the `,` between its media type and its payload.
        // `data:` requests do appear in browser HARs.
        //
        // Its syntax is scheme-specific, so there is no general decomposition
        // to apply. It keeps the plain-text treatment, which is what `main`
        // did for every URL and is still right for this one.
        origin = beforeQuery;
        pathText = '';
    }
    // `hierarchical`: the origin is a scheme and an authority -- structure --
    // rather than a whole opaque URL (#66).
    return { origin, pathText, query, fragment, hierarchical: scheme >= 0 };
}

/**
 * The path, as its `/`-delimited segments.
 *
 * Detection and replacement BOTH go through this. A path the two split
 * differently is a value found at one index and rewritten at another, which is
 * the twin-divergence failure the rest of this module keeps warning about.
 *
 * An EMPTY segment -- a leading, trailing or doubled `/` -- is a delimiter and
 * not a value. Detection skips it and replacement maps it through untouched:
 * one decision, stated on each side.
 */
function pathSegments(pathText) { return pathText.split('/'); }

/**
 * Is this component `name=value` FORM DATA, or one opaque value?
 *
 * The answer decides which of two treatments a component gets, and the two
 * must not be mixed -- mixing them is what double-encoded a replacement (found
 * by independent review):
 *
 * - **Form data** goes to the form branch raw, exactly as a query does. That
 *   branch decodes and re-encodes each part itself, so the caller must NOT
 *   re-encode its result.
 * - **One opaque value** is decoded here, acted on as decoded text, and
 *   re-encoded here -- so the form branch must be suppressed, or it would
 *   encode a second time.
 *
 * A PATH SEGMENT IS NEVER FORM DATA, whatever it happens to look like. A
 * segment reading `a=b` is a segment whose text contains `=`; `/` is its
 * delimiter and `+` is a literal `+` in it. Reading it as form data would
 * apply `+`-is-space to a path and re-encode the space as `%20`, changing the
 * URL -- the direction #22's scope note warns about.
 *
 * A FRAGMENT GENUINELY CAN BE. RFC 6749's implicit flow returns
 * `#access_token=...&token_type=Bearer` as `application/x-www-form-urlencoded`,
 * so a form-shaped fragment is a routine capture shape and is treated as the
 * parameter list it is -- which also keeps its `&` a delimiter instead of
 * escaping it away.
 */
function isFormComponent(raw, mayBeForm) {
    return mayBeForm && formEncodedPairs(raw) !== null;
}

/**
 * The decoded component, or null when decoding it once is NOT ENOUGH.
 *
 * HALF A DECODE IS WORSE THAN NONE, and this is the guard that says so. The
 * premise "decoded text holds no `%XX` for a write to land inside" is only
 * true when the component carried exactly one layer of encoding. A proxied or
 * redirect URL in a path segment carries two:
 *
 *     /go/https%3A%2F%2Fother.test%2Fx%3Fq%3D%257B%2522id%2522...
 *
 * Decoding once yields `https://other.test/x?q=%7B%22id%22...` -- text that
 * still holds escapes, which the plain-text scanner then reads as payload. The
 * digit run that two layers had pushed to 18 characters (out of card range,
 * and therefore safe on `main` by accident) comes back to 16, and the write
 * lands inside the inner escape. That is this stage's own defect, relocated
 * one layer down instead of removed -- found by independent review.
 *
 * So a component is decoded only when the result is unambiguously decoded.
 * Otherwise it keeps the plain-text treatment, which is what `main` does: the
 * multi-layer case stays open and tracked rather than being made worse.
 *
 * Decoding the layers properly means recursing the COMPONENT treatment -- the
 * inner text is a URL with its own origin, path, query and fragment, so
 * blanket re-encoding would mangle its structure. That is a design, not a
 * tweak, and it belongs in its own change.
 */
function decodedOnceOrNull(raw, plusIsSpace, offsets) {
    const decoded = percentDecode(raw, plusIsSpace === undefined ? false : plusIsSpace, offsets);
    return /%[0-9A-Fa-f]{2}/.test(decoded) ? null : decoded;
}

function detectInUrlComponent(raw, entryIndex, basePath, out, policy, mayBeForm, plusIsSpace) {
    plusIsSpace = plusIsSpace === true;
    if (isFormComponent(raw, mayBeForm)) {
        tryWalkJsonText(raw, entryIndex, basePath, out, policy, 0, false, plusIsSpace);
        return;
    }
    // The twin of the no-escape guard in `replaceUrlComponent`. The two must
    // agree about which reading a component gets.
    const decoded = /%[0-9A-Fa-f]{2}/.test(raw) ? decodedOnceOrNull(raw, plusIsSpace) : null;
    if (decoded === null) {
        detectInString(raw, entryIndex, { jsonPath: basePath }, out, policy);
        return;
    }
    tryWalkJsonText(decoded, entryIndex, basePath, out, policy, 0, true, plusIsSpace);
}

function detectInUrl(url, entryIndex, out, policy) {
    const parts = splitUrl(url);
    detectInString(parts.origin, entryIndex, { jsonPath: 'request.url' }, out, policy);
    pathSegments(parts.pathText).forEach((segment, i) => {
        if (segment.length === 0) return;
        detectInUrlComponent(segment, entryIndex,
            `request.url.path[${i}]`, out, policy, false);
    });
    if (parts.query) {
        // THE QUERY IS A COMPONENT TOO. It had its own route into the form
        // branch, which covered a `name=value` query and left a BARE encoded
        // payload -- a query with no `=` in it -- on the plain-text scanner, so
        // #22's defect was fully live there. `plusIsSpace: true`: a query string
        // is form data by convention, which is #479's reading and is not a
        // choice the way a fragment's is.
        detectInUrlComponent(parts.query, entryIndex, 'request.url.query', out, policy, true, true);
    }
    if (parts.fragment) {
        detectInUrlComponent(parts.fragment, entryIndex,
            'request.url.fragment', out, policy, true);
    }
}

/**
 * One path segment or fragment, rewritten on its DECODED text.
 *
 * A CHANGED COMPONENT IS RE-ENCODED; AN UNCHANGED ONE IS RE-EMITTED VERBATIM.
 * Same asymmetry and same reason as `replaceJsonOrText`'s form branch --
 * `encodeURIComponent(urlDecode(x))` is not the identity, so round-tripping
 * every segment would rewrite bytes the scrub was never asked to touch. Here it
 * carries a second weight: issue #22's scope note warns that a wrong path
 * rewrite is worse than the corruption it prevents, so a segment carrying no
 * detected value has to come back byte-identical rather than normalised.
 *
 * `encodeURIComponent` over-escapes for a path segment -- `:`, `@`, `,` and
 * `;` are legal `pchar` and come back as escapes. That is meaning-preserving
 * (a segment's `%3A` and `:` denote the same character) and it is the safe
 * direction: it can never introduce a delimiter. Only a segment that actually
 * changed pays it.
 */
/**
 * Percent-encode text for the component it will sit in.
 *
 * `encodeURIComponent` encodes for a QUERY PARAMETER -- it escapes `/` and
 * `?`, which are legal in a path segment's siblings and in a fragment, and
 * which carry STRUCTURE there. Replacing a value inside an SPA hash route
 * `#/users/<email>/edit` therefore produced `#%2Fusers%2F...%2Fedit`, which no
 * hash router matches: the component was re-encoded correctly for somewhere it
 * was not going.
 *
 * So each component gets back the characters that are legal in it. A FRAGMENT
 * keeps `/` and `?` (RFC 3986 `fragment = *( pchar / "/" / "?" )`); a PATH
 * SEGMENT keeps `?` escaped, because a `?` in a segment would start the query,
 * but is otherwise the same; a QUERY keeps both, since it is already past the
 * `?`. Everything else stays escaped -- over-escaping is meaning-preserving
 * and is the safe direction, and only a component that actually changed pays
 * any of it.
 *
 * `&` AND `=` ARE RETURNED ONLY IF THE INPUT NEVER SPELLED THEM ESCAPED, and
 * that condition is the whole of it. Withholding them unconditionally -- on the
 * reasoning that handing them back could let a rewritten opaque component start
 * parsing as form data on a later pass -- destroyed the structure of four real
 * shapes that `origin/main` preserved: a query with a flag part
 * (`?email=…&debug`), a hash-router query, a `name=value` path segment, and a
 * matrix-parameter segment.
 *
 * The reasoning was wrong in the half it did not cover. A component whose raw
 * text ALREADY held `=` or `&` literally was classified opaque *with them
 * present*, and no replacement can repair whatever made it opaque: every fake
 * keeps the character that disqualified the name (`@` for an email, `+` for a
 * phone, `:` for a MAC), and a flag-style part with no `=` at all is never
 * touched. So returning a character the input spelled literally cannot change
 * the classification -- the input already had it.
 *
 * What WOULD change it is handing back a character the input had escaped, so
 * that is the case the condition excludes. The output then carries exactly the
 * literal-or-escaped spelling the input carried, which is what keeps
 * classification -- and therefore idempotence -- stable.
 *
 * A mixed spelling in one component (a literal `/` and a `%2F` together) still
 * resolves to the one answer this rule gives. Fixing that properly means
 * splicing positionally -- carrying an offset map through `percentDecode` and
 * re-encoding only the replaced spans -- which would delete this table
 * outright. Tracked as the end state on #132.
 */
/**
 * Write a rewritten component back, keeping every byte the scrub did not change.
 *
 * WHY NOT `encodeURIComponent` OVER THE WHOLE THING, and why not a table of
 * per-component exceptions either. Both were tried and both respell bytes
 * nobody asked about.
 *
 * `encodeURIComponent` encodes for a QUERY PARAMETER: it escapes `/` and `?`,
 * which are legal in a fragment and carry structure there, so rewriting a value
 * inside an SPA hash route `#/users/<email>/edit` produced `#%2Fusers%2F...`
 * that no hash router matches.
 *
 * A table of characters to hand back per component kind fixed that and then had
 * its own failure, because it can only give ONE answer per character per
 * component -- and a component routinely spells the same character both ways. A
 * hash-route query beside a base64-padded parameter (`token=abc%3D%3D`), or a
 * path with one encoded separator among literal ones (`/a%2Fb/`), made every
 * literal occurrence withhold too: a four-segment route collapsed to one.
 *
 * SO THE ANSWER IS POSITIONAL, NOT A RULE ABOUT CHARACTERS. Detection and
 * replacement happen on decoded text; the write-back then re-emits the INPUT's
 * own bytes everywhere except the span that actually changed, and encodes only
 * that span. A byte the scrub did not touch is therefore spelled exactly as it
 * arrived, whatever its neighbours do -- which is the guarantee the rest of this
 * module already makes for whole parameters, applied inside one.
 *
 * The span is found by common prefix and suffix against the decoded text, so a
 * component with several disjoint replacements re-encodes the stretch BETWEEN
 * the first and last of them as one span. That is a narrower version of the old
 * defect rather than a new one, and the general fix -- carrying the map through
 * replacement so each match is spliced on its own -- is #132.
 */
/**
 * Widen a decoded-text range to the edges of the escape runs it touches.
 *
 * The offset map records a run's START for every character it decodes to, so a
 * boundary landing inside a run resolves to the run's beginning. Slicing there
 * would take the rest of the run with it or leave it behind -- the defect that
 * consumed a `":"` delimiter, left a name's final letter in the artifact, and
 * dropped an `x!` prefix outright. Widening puts the whole run inside the
 * re-encoded span, where it comes back as its own bytes.
 */
function snapToRuns(from, to, offsets, decodedLength) {
    while (from > 0 && offsets[from - 1] === offsets[from]) from--;
    while (to < decodedLength && to > 0 && offsets[to] === offsets[to - 1]) to++;
    return [from, to];
}

/**
 * Write each accepted replacement back on its own.
 *
 * WHY PER SPAN. The whole-span diff below covers everything from the first
 * change to the last, so a component carrying two replaced values had every
 * delimiter BETWEEN them re-encoded -- two query parameters merged into one,
 * three route segments into one. Writing each match back separately leaves the
 * text between them exactly as it arrived, which is the same guarantee the
 * no-escape route gets for free.
 *
 * The characters a snap pulls in are untouched text, so they are carried into
 * the encoded span rather than replaced: `decoded.slice(from, span.from)` and
 * `decoded.slice(span.to, to)` bracket the replacement.
 *
 * Returns null if the spans cannot be mapped, so the caller falls back rather
 * than emitting something it cannot justify.
 */
function writeBackSpans(raw, decoded, spans, offsets) {
    let out = '';
    let copiedTo = 0;
    for (const span of spans) {
        const [from, to] = snapToRuns(span.from, span.to, offsets, decoded.length);
        const rawFrom = offsets[from];
        const rawTo = offsets[to];
        if (rawFrom === undefined || rawTo === undefined || rawFrom < copiedTo) return null;
        const keptBefore = decoded.slice(from, span.from);
        const keptAfter = decoded.slice(span.to, to);
        out += raw.slice(copiedTo, rawFrom) +
            encodeURIComponent(keptBefore + span.replacement + keptAfter);
        copiedTo = rawTo;
    }
    return out + raw.slice(copiedTo);
}

function writeBackChangedSpan(raw, decoded, next, offsets) {
    const max = Math.min(decoded.length, next.length);
    let pre = 0;
    while (pre < max && decoded[pre] === next[pre]) pre++;
    let suf = 0;
    while (suf < max - pre &&
        decoded[decoded.length - 1 - suf] === next[next.length - 1 - suf]) suf++;

    // SNAP EACH BOUNDARY TO ITS RUN'S EDGE BEFORE SLICING. A multi-escape run
    // decodes to several characters that all record the run's START, so a
    // boundary landing inside one resolves to the run's beginning -- and the
    // slice then takes the rest of the run with it, or leaves it behind.
    //
    // A non-ASCII letter IS such a run, so any value beginning or ending with
    // one hit this. Both directions were live and the second is the worse pair:
    // a start boundary inside the preceding `":"` run consumed that delimiter
    // and produced invalid JSON; an end boundary inside the value's own last
    // run left that character behind -- someone's real initial, still there --
    // and because the leftover is still part of a detectable value, every
    // later pass replaced it again with a DIFFERENT fake, so the artifact never
    // settled.
    //
    // Snapping puts the whole run inside the re-encoded span, where it comes
    // back as its own bytes. Found by independent review.
    let end;
    [pre, end] = snapToRuns(pre, decoded.length - suf, offsets, decoded.length);

    const rawStart = offsets[pre];
    const rawEnd = offsets[end];
    // `offsets` is built by `percentDecode` and carries a final sentinel at
    // `raw.length`, so both ends are always addressable.
    if (rawStart === undefined || rawEnd === undefined) return encodeURIComponent(next);
    return raw.slice(0, rawStart) +
        encodeURIComponent(next.slice(pre, next.length - (decoded.length - end))) +
        raw.slice(rawEnd);
}

function replaceUrlComponent(raw, replacements, policy, mayBeForm, plusIsSpace, plainText) {
    // THE TWIN OF `detectInUrlComponent`, branching on the same predicate over
    // the same raw text. The two must agree about which treatment a component
    // gets, or a value is found under one reading and rewritten under the
    // other.
    //
    // `plainText(text, spansOut)` rewrites the component read as PLAIN TEXT --
    // structure-aware for a bare path segment and a refused query (#66), the
    // plain replacement otherwise. A component that parses as a document or as
    // form data takes the branch for that grammar instead, which knows its own
    // names. Which branch a component takes is unchanged; only what acts inside
    // the plain-text one is.
    const text = plainText || ((t, spans) => replaceAll(t, replacements, spans));
    if (isFormComponent(raw, mayBeForm)) {
        // `plusIsSpace: false`, AND THIS IS THE LEAK-SAFE DIRECTION rather
        // than a stylistic preference. Reading `+` as a space made an E.164
        // phone in a form-shaped fragment undetectable -- the pattern needs a
        // literal `+` -- so the number shipped in the clear where `main` had
        // caught it. The mirror case, a conforming producer's space coming
        // back as `%2B`, only respells a value. One direction loses a value
        // and the other does not, which settles it. Found by independent
        // review on issue #22.
        return replaceJsonOrText(raw, replacements, policy, 0, false, plusIsSpace === true);
    }
    // NO ESCAPE, NO DECODE. The decode route exists to stop an escape's
    // characters being read as payload; a component holding no `%XX` has none to
    // misread, so decoding buys nothing and the re-encode on the way out is
    // pure cost -- it respelled `@` as `%40`, and where two values changed it
    // escaped the `=` and `&` between them. For this case `main` was simply
    // right, and this is the cheapest way to keep being right about it.
    if (!/%[0-9A-Fa-f]{2}/.test(raw)) return text(raw);
    const offsets = [];
    const decoded = decodedOnceOrNull(raw, plusIsSpace === true, offsets);
    if (decoded === null) return text(raw);
    const spans = [];
    const next = replaceJsonOrText(decoded, replacements, policy, 0, true,
        plusIsSpace === true, spans, text);
    if (next === decoded) return raw;
    // Per span when the replacement pass could say where it landed; the
    // whole-span diff otherwise (the JSON branch, which reproduces its own
    // delimiters anyway).
    const exact = spans.length > 0 ? writeBackSpans(raw, decoded, spans, offsets) : null;
    return exact !== null ? exact : writeBackChangedSpan(raw, decoded, next, offsets);
}

function replaceInUrl(url, replacements, policy) {
    const parts = splitUrl(url);
    // STRUCTURE TAKES THE STRUCTURE LIST (#66): a scheme and authority, and a
    // bare path segment, never receive a value whose only evidence was a
    // field name somewhere else. A non-hierarchical URL's "origin" is the
    // whole opaque URL -- a `mailto:` address, a `data:` payload -- and stays
    // payload.
    const structure = structureReplacements(replacements);
    const segmentText = (t, spans) => replaceAll(t, structure, spans);
    const pathText = pathSegments(parts.pathText)
        .map((s) => (s.length === 0 ? s : replaceUrlComponent(s, replacements, policy, false, false, segmentText)))
        .join('/');
    let out = replaceAll(parts.origin, parts.hierarchical ? structure : replacements) + pathText;
    // `!== null` rather than a truthiness test, and the detection twin's
    // falsy skip is the same decision: an EMPTY query or fragment has nothing
    // to detect, but its delimiter is still part of the URL and `http://h/x?`
    // must not come back as `http://h/x`.
    if (parts.query !== null) {
        // A query the strict form reader refuses -- a flag part with no `=`,
        // an unencoded URL inside it -- is still a query: its names stay
        // names (#66). The form-shaped query never reaches this; its form
        // branch already knows its names.
        out += '?' + replaceUrlComponent(parts.query, replacements, policy, true, true,
            (t, spans) => replaceParamText(t, replacements, spans));
    }
    if (parts.fragment !== null) {
        out += '#' + replaceUrlComponent(parts.fragment, replacements, policy, true, false);
    }
    return out;
}

/**
 * Detection over a request or response body.
 *
 * THREE BRANCHES, AND THE MIDDLE ONE IS WHY THIS EXISTS (issue #479).
 *
 * A percent-encoded body used to go straight to the text scanner, which read
 * `%22` as the two digits `22` rather than as a delimiter. Those digits join
 * whatever follows, so an adjacent value is measured at the wrong LENGTH -- and
 * length is how the digit-run detectors decide what a value is. It fails in
 * both directions: two extra digits can complete a 14-digit id into a
 * card-length run (a false positive, whose replacement then overwrites the
 * escape and corrupts the document), or extend a real 16-digit card to 18 and
 * push it out of range entirely (a false negative -- the card survives).
 *
 * Decoding first removes the whole class: the scanner sees the value the
 * operator actually sent, with delimiters as delimiters.
 */
function tryWalkJsonText(text, entryIndex, basePath, out, policy, depth, noForm, plusIsSpace) {
    if (plusIsSpace === undefined) plusIsSpace = true;
    depth = depth || 0;
    let parsed = null;
    try { parsed = JSON.parse(text); } catch (_) { /* not JSON */ }
    if (parsed !== null && typeof parsed === 'object') {
        // `depth` goes with it, so the form layers already unwrapped here and
        // the layers a leaf still carries draw on ONE budget. Restarting the
        // count inside the document would make the effective reach the sum of
        // two caps, which is a third number nobody declared.
        walkJsonForDetect(parsed, null, entryIndex, basePath, out, policy, depth);
        return;
    }
    if (!noForm && depth < FORM_DEPTH_LIMIT) {
        const pairs = formEncodedPairs(text);
        if (pairs) {
            for (const p of pairs) {
                if (p.empty) continue;
                // `plusIsSpace` TRAVELS WITH THE COMPONENT; `noForm` DOES NOT.
                // The two options describe different things, and conflating them
                // cost a leak. `noForm` is a statement about the CALLER's own
                // layer -- "I already decoded this text" -- so it must stop
                // here; a string one level down is raw wire text again.
                // `plusIsSpace` is a statement about the COMPONENT's encoding
                // convention, which holds for every layer inside it: a nested
                // form pair in a fragment is still in a fragment.
                //
                // Letting it fall back to the default split an RFC 5233
                // subaddressed address (`user+tag@host`) at its `+` and replaced
                // only the email-shaped tail, leaving the real local-part
                // prefix in the clear.
                // THE SAME MULTI-LAYER REFUSAL THE OPAQUE ROUTE MAKES. Half a
                // decode is worse than none wherever it happens, and a form
                // branch is not exempt: a fragment is form-routed, so an OAuth
                // `state` carrying an encoded redirect URL arrives here with
                // two layers on it. Decoding once left escapes that the
                // plain-text scanner then read as payload.
                const value = decodedOnceOrNull(p.raw, plusIsSpace);
                if (value === null) {
                    detectInString(p.raw, entryIndex,
                        { jsonPath: basePath + '.' + p.name }, out, policy);
                } else {
                    tryWalkJsonText(value, entryIndex,
                        basePath + '.' + p.name, out, policy, depth + 1, false, plusIsSpace);
                }
                // The NAME is scanned too. The plain-text path made no
                // distinction between the two sides of an `=`, so leaving names
                // out would silently stop detecting a secret used as a key.
                // The path names the PARAMETER as well as the side, or two
                // secrets used as two names in one body report the same
                // location and the findings cannot be told apart.
                detectInString(percentDecode(p.name, plusIsSpace), entryIndex,
                    { jsonPath: basePath + '.' + p.name + '.<name>' }, out, policy);
            }
            return;
        }
    }
    detectInString(text, entryIndex, { jsonPath: basePath }, out, policy);
}

// --- scrubbing: apply faker substitutions in-place on the HAR ---
function scrubPii(har, policy) {
    const plan = planPiiReplacements(detectPii(har, policy), policy);
    const entries = harDocument.entriesOf(har, 'the capture being scrubbed');
    for (const entry of entries) {
        applyReplacementsToEntry(entry, plan.replacements, policy);
    }
    return {
        substitutions: plan.substitutions,
        retained: plan.retained,
        deepDetections: plan.deepDetections,
        depthLimited: plan.depthLimited,
    };
}

/**
 * Every decision the typed-PII pass takes over the WHOLE run, from the whole
 * run's detections, before any entry is rewritten: which values are declined
 * (an identifier-field site anywhere declines a value everywhere), which are
 * replaced, and with what.
 *
 * Split from `scrubPii` so the streaming scrub (issue #10) can detect in one
 * walk and rewrite in a second: this is the step between them, and the only
 * one that has to see everything at once. `replacements` carries the raw
 * values and stays in memory; `substitutions` is the value-free record.
 */
function planPiiReplacements(detections, policy) {
    // The two walk counters travel on the detections array (see `countDeep`),
    // so they are read here and carried into the result whatever else this
    // function decides. Read BEFORE the empty-detections return: a capture
    // nested past the shared cap can legitimately yield zero detections, and
    // that is precisely the run where "I stopped before the bottom" is the only
    // thing worth saying. Dropping it there would make the quietest possible
    // output the least informative one.
    const walk = {
        deepDetections: detections.deepDetections || 0,
        depthLimited: detections.depthLimited || 0,
    };
    if (detections.length === 0) {
        return { replacements: [], substitutions: [], retained: [], ...walk };
    }

    // Split out the detections the policy asked us to leave alone (issue #346).
    // DETECTION is untouched above: a disabled class is still found, and is
    // still reported below, exactly as the gate reports one. What a setting of
    // `off` changes is only whether the value gets rewritten.
    const replaceable = [];
    const retainedDetections = [];
    for (const d of detections) {
        if (scrubSettingFor(policy, d.type) === 'off') {
            retainedDetections.push({ detection: d, reason: 'off' });
        } else {
            replaceable.push(d);
        }
    }

    // --- identifierFields alignment (issue #360) ---------------------------
    //
    // The gate already declines to FAIL a card-shaped identity value sitting
    // at a declared identifier field. Until now this pass still REPLACED it,
    // so the two engines disagreed on the half where a false positive is
    // silent and permanent rather than noisy and reversible.
    //
    // The decision is taken per (type, value) over the WHOLE run and not per
    // site, because the replacement set is keyed on the value: one entry is
    // applied to every occurrence by a blind text pass. So a value seen at an
    // identifier field in one place and a plain field in another has to be
    // decided ONE WAY for the whole run, and the direction is what matters.
    //
    // ANY declared-identifier-field site declines the replacement everywhere.
    //
    // The gate's `findLeaksInHar` resolves the same mixed evidence the OTHER
    // way -- it promotes the finding back and blocks. That is not a
    // contradiction and this must not be "made consistent" with it: the two
    // engines sit on opposite axes and beat 2 gives them opposite safe
    // directions. On the gate, erring toward more findings is noisy, visible
    // and reversible. On the replace path, erring toward more replacements is
    // plausible corruption that nobody can detect afterwards -- and worse here
    // than anywhere, because the replacement is this scrubber's own recognised
    // fake, so no gate will ever report the corrupted id again. Fail toward a
    // MISS on a replace path.
    //
    // What makes the miss safe is the gate, measured rather than assumed:
    // `findLeaksInHar` groups on (kind, fingerprint) and promotes the whole
    // group when ANY site is not an identifier field, order-independently. So a
    // mixed-evidence value left in place FAILS THE RUN LOUDLY. A real card
    // cannot ship silently through this path; it is stopped by the engine whose
    // false positives are cheap. The two compose exactly as designed -- and
    // `pii-identifier-fields-scrub.test.js` asserts that composition directly,
    // because it is now load-bearing rather than incidental.
    const declined = new Set();
    const alsoAtPlainField = new Set();
    for (const d of replaceable) {
        const key = `${d.type}${d.value}`;
        if (d.identifierField) declined.add(key); else alsoAtPlainField.add(key);
    }
    const survivors = [];
    for (const d of replaceable) {
        const key = `${d.type}${d.value}`;
        if (!declined.has(key)) survivors.push(d);
        else retainedDetections.push({
            detection: d,
            reason: 'identifier-field',
            // MIXED EVIDENCE, carried rather than smoothed over. The audit's
            // `verdictFor` calls this shape UNADJUDICABLE and refuses to guess;
            // the scrub cannot refuse -- it either rewrites or does not -- so it
            // takes the safe direction and SAYS which values it took it on.
            mixed: alsoAtPlainField.has(key),
        });
    }
    replaceable.length = 0;
    replaceable.push(...survivors);

    if (replaceable.length === 0) {
        return { replacements: [], substitutions: [], retained: summariseRetained(retainedDetections, new Set()), ...walk };
    }

    // Group by (type, original value) so we record one substitution per unique original.
    const byKey = new Map();
    for (const d of replaceable) {
        const key = `${d.type}\u0001${d.value}`;
        if (!byKey.has(key)) {
            byKey.set(key, {
                type: d.type,
                value: d.value,
                replacement: fakeFor(d.type, d.value),
                originalHash: hashPrefix(d.value),
                locations: []
            });
        }
        // Sanitize location: never include the raw value.
        byKey.get(key).locations.push(d.location);
    }

    // Build replacement set sorted longest-first to avoid partial-overlap issues
    // (e.g. "Alice Marie Johnson" before "Alice").
    const replacements = Array.from(byKey.values()).sort((a, b) => {
        return String(b.value).length - String(a.value).length;
    });

    // Strip raw values out of the returned substitutions (safe-store schema).
    const safe = replacements.map(r => ({
        type: r.type,
        originalHash: r.originalHash,
        replacement: r.replacement,
        locations: r.locations
    }));
    safe.sort((a, b) => {
        if (a.type !== b.type) return a.type < b.type ? -1 : 1;
        return a.originalHash < b.originalHash ? -1 : 1;
    });
    const replacedValues = new Set(replacements.map((r) => String(r.value)));
    return { replacements, substitutions: safe, retained: summariseRetained(retainedDetections, replacedValues), ...walk };
}

/**
 * What this run did NOT replace, per class and reason, and never a value.
 *
 * `replacedValues` is the set of originals the replacement pass DID rewrite.
 * The replacement set is keyed on the value, not on the location, so a string
 * detected as a disabled type in one field and an enabled type in another is
 * rewritten everywhere -- the safe direction, and the only coherent one: the
 * alternative rewrites one occurrence and leaves the identical string beside
 * it. Such a value is therefore NOT retained, and must not be counted as if it
 * were: a report that overstates what shipped is the noise this subsystem
 * measured at 1134 findings and 3 real ones.
 *
 * The finding wears the gate's vocabulary -- `kind`, `class`, `setting` --
 * because it is the same decision, taken on the other axis. Counts only: this
 * is exactly the `occurrences` / `distinct` shape #346 published, which is what
 * a report of identity data may safely carry.
 */
function summariseRetained(entries, replacedValues) {
    const byBucket = new Map();
    for (const { detection: d, reason, mixed } of entries) {
        if (replacedValues.has(String(d.value))) continue;
        const key = `${d.type}${reason}${mixed ? 'mixed' : 'pure'}`;
        if (!byBucket.has(key)) {
            byBucket.set(key, { kind: d.type, reason, mixed: !!mixed, occurrences: 0, values: new Set() });
        }
        const bucket = byBucket.get(key);
        bucket.occurrences += 1;
        bucket.values.add(String(d.value));
    }
    return [...byBucket.values()]
        .map((bucket) => {
            const row = {
                kind: bucket.kind,
                class: 'identity',
                // A field-name downgrade is not a class being switched off. The
                // class is still `gate`; what declined to act is the identifier
                // rule, and the row says which -- reading `setting: 'off'` here
                // would report a standing policy decision nobody made.
                setting: bucket.reason === 'off' ? 'off' : 'gate',
                occurrences: bucket.occurrences,
                distinct: bucket.values.size,
            };
            if (bucket.reason === 'identifier-field') row.identifierField = true;
            // The operator needs to tell the two apart. A PURE row is a value
            // the gate also passes; a MIXED row is one the gate will BLOCK on,
            // and that is the difference between "this run is finished" and
            // "this run is about to fail and here is why".
            if (bucket.mixed) row.mixedEvidence = true;
            return row;
        })
        .sort((a, b) => {
            if (a.kind !== b.kind) return a.kind < b.kind ? -1 : 1;
            if (!!a.identifierField !== !!b.identifierField) {
                return (a.identifierField ? 1 : 0) - (b.identifierField ? 1 : 0);
            }
            return (a.mixedEvidence ? 1 : 0) - (b.mixedEvidence ? 1 : 0);
        });
}

// Headers whose whole value is a scheme and authority -- the request URL's
// origin, written a second time. Structure for the reason `replaceInUrl`'s
// origin is (#66): the Host header must not disagree with the URL it names.
// Other header values keep plain-text treatment; that is not a decision about
// them, only the edge of this one.
const AUTHORITY_HEADERS = new Set(['host', ':authority', 'origin']);

function applyReplacementsToEntry(entry, replacements, policy) {
    // headers
    for (const ctx of ['request', 'response']) {
        const hs = entry[ctx] && entry[ctx].headers;
        if (Array.isArray(hs)) {
            for (const h of hs) {
                if (typeof h.value !== 'string') continue;
                const authority = AUTHORITY_HEADERS.has(String(h.name).toLowerCase());
                h.value = replaceAll(h.value, authority ? structureReplacements(replacements) : replacements);
            }
        }
    }
    // cookies -- the same two arrays detectPii walks. Detection that never
    // reaches the scrub is a report nobody acts on, and the two walks have to
    // cover the same nodes or the gate fails a value the scrubber was never
    // asked to remove.
    for (const ctx of ['request', 'response']) {
        const cs = entry[ctx] && entry[ctx].cookies;
        if (Array.isArray(cs)) {
            for (const c of cs) {
                if (typeof c.value === 'string') c.value = replaceAll(c.value, replacements);
            }
        }
    }
    // query string
    const qs = entry.request && entry.request.queryString;
    if (Array.isArray(qs)) {
        for (const q of qs) {
            if (typeof q.value === 'string') q.value = replaceAll(q.value, replacements);
        }
    }
    // url
    if (entry.request && typeof entry.request.url === 'string') {
        entry.request.url = replaceInUrl(entry.request.url, replacements, policy);
    }
    // bodies (request/response)
    if (entry.request && entry.request.postData && typeof entry.request.postData.text === 'string') {
        entry.request.postData.text = replaceJsonOrText(entry.request.postData.text, replacements, policy);
    }
    if (entry.response && entry.response.content && typeof entry.response.content.text === 'string') {
        entry.response.content.text = replaceJsonOrText(entry.response.content.text, replacements, policy);
    }
    // The unretained-request-body descriptor (#442). Paired with the walk in
    // `detectPii` for the reason stated on the cookie arrays above: detection
    // that never reaches the scrub is a report nobody acts on, and the gate
    // would then fail a value the scrubber was never asked to remove.
    const descriptor = entry.request && entry.request[BODY_DESCRIPTOR_KEY];
    if (descriptor && typeof descriptor === 'object') {
        entry.request[BODY_DESCRIPTOR_KEY] = replaceInJson(descriptor, null, replacements, policy);
    }
}

/**
 * Replacement over a request or response body. Mirrors tryWalkJsonText branch
 * for branch, because detection and replacement disagreeing about what a body
 * IS is how a value gets detected in one shape and rewritten in another.
 *
 * `noForm` SUPPRESSES THE FORM BRANCH AT THIS LEVEL ONLY, and exists because
 * the form branch re-encodes what it changes (below). That is correct when it
 * is handed raw wire text -- how the query branch uses it -- and a DOUBLE
 * encode when it is handed text the caller already decoded and will re-encode
 * itself. `%40` then comes back as `%2540`, which decodes to the literal text
 * `%40` rather than to `@`: the value's meaning destroyed, not merely
 * over-escaped. It is deliberately NOT propagated into the recursion -- a
 * string leaf inside a parsed document IS raw wire text, so the form branch's
 * re-encode is right there. Found by independent review on issue #22.
 *
 * A CHANGED PARAMETER IS RE-ENCODED; AN UNCHANGED ONE IS RE-EMITTED VERBATIM.
 * That asymmetry is load-bearing. `encodeURIComponent(decodeURIComponent(x))`
 * is not the identity -- `%7E` decodes to `~` and re-encodes to `~`, `+`
 * decodes to a space and re-encodes to `%20` -- so round-tripping every
 * parameter would rewrite bytes the scrub was never asked to touch, in a file
 * whose whole purpose is to be a faithful record. Only a parameter that
 * actually changed pays that cost, and only because it had to.
 */
function replaceJsonOrText(text, replacements, policy, depth, noForm, plusIsSpace, spansOut, plainText) {
    if (plusIsSpace === undefined) plusIsSpace = true;
    depth = depth || 0;
    let parsed = null;
    try { parsed = JSON.parse(text); } catch (_) { /* not JSON */ }
    if (parsed !== null && typeof parsed === 'object') {
        // `depth` threaded for the reason stated on the detection twin: one
        // budget, not two added together.
        const rebuilt = replaceInJson(parsed, null, replacements, policy, depth);
        const next = JSON.stringify(rebuilt);
        // NOTHING CHANGED MEANS THE ORIGINAL BYTES, not a re-serialization of
        // them. `JSON.stringify(JSON.parse(x))` is not the identity: it drops
        // insignificant whitespace, normalises `1.0` to `1`, resolves `\uXXXX`
        // -- and ROUNDS AN INTEGER PAST 2^53, which is not cosmetic. An id of
        // `12345678901234567890` came back `12345678901234567000`.
        //
        // Returning the rebuilt text unconditionally also lies to every caller
        // that reads "different bytes" as "something was scrubbed". The URL
        // component path does exactly that to decide whether to re-encode, so
        // a document that merely round-tripped was re-encoded as though the
        // scrub had rewritten it (found by independent review on issue #22).
        return next === JSON.stringify(parsed) ? text : next;
    }
    if (!noForm && depth < FORM_DEPTH_LIMIT) {
        const pairs = formEncodedPairs(text);
        if (pairs) {
            let changed = false;
            const rebuilt = pairs.map((p) => {
                // An EMPTY segment is re-emitted as itself, so a body's
                // redundant delimiters survive a run that changed something
                // else in it. The byte-identical promise covers the whole body,
                // not only the parameters that carry values.
                if (p.empty) return '';
                // The multi-layer refusal, mirroring the detection twin. A
                // refused value is rewritten as PLAIN TEXT on its raw bytes, so
                // it is neither half-decoded nor re-encoded.
                const decodedValue = decodedOnceOrNull(p.raw, plusIsSpace);
                const value = decodedValue === null ? p.raw : decodedValue;
                // `plusIsSpace` threaded for the reason stated on the detection
                // twin: it belongs to the component, not to this layer.
                const nextValue = decodedValue === null
                    ? replaceAll(p.raw, replacements)
                    : replaceJsonOrText(value, replacements, policy,
                        depth + 1, false, plusIsSpace);

                // THE NAME HALF IS DECODED BLINDLY, AND THAT IS DELIBERATE --
                // giving it the value half's multi-layer refusal was tried and
                // LEAKS. Refusing a doubly-encoded name leaves its raw bytes to
                // `replaceAll`, and the enrolled needles cover a value and its
                // SINGLE-encoded spelling, so a twice-encoded name matches
                // neither and the address survives in the clear. The blind
                // decode reaches it, because one decode is all the needle needs.
                //
                // The cost of reaching it is a respelling: the needle that
                // matches is the encoded one, whose replacement is already
                // wire-form, and the wrap below then encodes it again -- so this
                // half can come back one level deeper than its own value half.
                // Raised by independent review as the asymmetry it is; the
                // respelling is the lesser of the two, and the real fix is the
                // nested re-encode contract on #133 rather than a guard here.
                //
                // A NAME IS STRUCTURE (#66), so the field-only kinds stay off
                // it: a query or form parameter named like an enrolled `name`
                // value is a parameter, not a person. The shaped kinds still
                // reach it -- that is the secret-used-as-a-key case above.
                const name = percentDecode(p.name, plusIsSpace);
                const nextName = replaceAll(name, structureReplacements(replacements));

                const valueChanged = nextValue !== value;
                const nameChanged = nextName !== name;
                if (!valueChanged && !nameChanged) return p.name + '=' + p.raw;
                changed = true;
                // Only the half that changed is re-encoded; the other half is
                // re-emitted exactly as it arrived. A half the multi-layer guard
                // refused was rewritten on its RAW bytes, so it is already in
                // wire form and must not be encoded again -- the same rule on
                // both sides of the `=`.
                // A half that ARRIVED unencoded goes back unencoded, provided
                // the replacement carries nothing that has to be escaped to
                // stay one `name=value` half. Same reasoning as the opaque
                // route's no-escape guard: re-encoding a half that held no
                // escape respells bytes the scrub had no reason to touch, and
                // `main` left them alone. The fakes contain none of these
                // characters, so the fast path is the normal one.
                const mustEscape = /[&=+%# ]/;
                const encodeHalf = (refused, rawHalf, next) => {
                    if (refused === null) return next;            // rewritten on raw bytes already
                    if (!/%[0-9A-Fa-f]{2}/.test(rawHalf) && !mustEscape.test(next)) return next;
                    return encodeURIComponent(next);
                };
                const valueOut = !valueChanged ? p.raw : encodeHalf(decodedValue, p.raw, nextValue);
                const nameOut = !nameChanged ? p.name : encodeHalf(undefined, p.name, nextName);
                return nameOut + '=' + valueOut;
            });
            return changed ? rebuilt.join('&') : text;
        }
    }
    // Only the plain-text branch reports spans. The JSON branch rebuilds a
    // document and the form branch re-encodes its own halves, so neither has
    // spans in the caller's coordinate space -- and the caller falls back to
    // the whole-span diff, which is harmless there because those branches
    // reproduce their own delimiters.
    //
    // `plainText` is how THIS text is rewritten when read as plain text, and
    // like `noForm` it is a statement about the caller's own layer: a bare URL
    // path segment and a refused query know which of their characters are
    // structure (#66). It is not threaded into the branches above, whose
    // leaves are values again.
    return plainText ? plainText(text, spansOut) : replaceAll(text, replacements, spansOut);
}

function replaceInJson(node, parentKey, replacements, policy, depth) {
    if (node === null || node === undefined) return node;
    if (typeof node === 'string') {
        // Honor field-typed replacements: if the parent key declares a context
        // type, swap the entire value out for the matching fake.
        const fType = fieldTypeFor(parentKey, policy);
        if (fType && fType !== 'geo-lat' && fType !== 'geo-lng') {
            // Find a matching replacement by exact original value, since detection
            // already enrolled this exact string.
            const target = indexFor(replacements).byTypeValue.get(`${pickContextType(fType)}${node}`);
            if (target) return target.replacement;
        }
        // Flat first, then the layers -- the order `sanitize-har.js:scrubString`
        // already uses. By the time the nested pass runs, every spelling the
        // flat pass could match is already a fake, so the descent only reaches
        // what the flat pass structurally could not: a value whose FIELD NAME
        // lives inside an encoded layer. Detection's twin is
        // `descendNestedLeaf`, and the two must descend identically or a value
        // is found in one shape and rewritten in another. The flat pass leaves
        // a nested document's own names alone (#66): see `replaceLeafText`.
        return replaceNestedLeaf(replaceLeafText(node, replacements, depth || 0), parentKey,
            replacements, policy, depth || 0);
    }
    if (typeof node === 'number') {
        const fType = fieldTypeFor(parentKey, policy);
        // A coordinate is the one type replaced by FIELD NAME alone, without
        // consulting the replacement set -- a number has no string form to
        // enrol. That makes it the one path a class setting could not reach by
        // being applied to the replacement set, so it is asked here directly
        // (issue #346). Miss this and `geo-coordinates: off` reads as honoured
        // while 37,422 coordinates are still zeroed.
        if ((fType === 'geo-lat' || fType === 'geo-lng')
            && scrubSettingFor(policy, 'geo-coordinates') !== 'off') {
            return 0;
        }
        return node;
    }
    if (Array.isArray(node)) {
        return node.map(v => replaceInJson(v, parentKey, replacements, policy, depth));
    }
    if (typeof node === 'object') {
        const out = {};
        for (const k of Object.keys(node)) {
            out[k] = replaceInJson(node[k], k, replacements, policy, depth);
        }
        return out;
    }
    return node;
}

/**
 * Rewrite inside the encoding layers a single string leaf may still carry.
 *
 * The replacement twin of `descendNestedLeaf`, over the same shared traversal,
 * so the two reach equally. A value detected at a depth the replacement pass
 * could not reach is the shape the gate then reports as an unremovable secret
 * -- #454's fail-CLOSED half, which is an operability dead end rather than a
 * leak but is no more acceptable.
 *
 * BYTE PRESERVATION IS THE SHARED WALK'S, not restated here. `transformNested`
 * returns the ORIGINAL bytes of any subtree nothing changed, which is what
 * keeps a committed reference from churning on a re-scrub that altered nothing
 * in it.
 */
function replaceNestedLeaf(text, parentKey, replacements, policy, depth) {
    if (typeof text !== 'string' || text.length === 0) return text;
    return transformNested(text, (node) => {
        // A field-typed replacement is keyed on (type, exact value), which is
        // how a name-only kind -- a person's name, a city -- is rewritten at
        // all: it has no shape for the flat pass to match on. Reaching it here
        // is the whole of #24.
        const fType = fieldTypeFor(node.name, policy);
        if (fType && fType !== 'geo-lat' && fType !== 'geo-lng') {
            const target = indexFor(replacements).byTypeValue
                .get(`${pickContextType(fType)}${node.value}`);
            if (target) return target.replacement;
        }
        // The walk descends into this value next, at `node.depth` -- so a
        // document nested one layer further keeps its names too (#66).
        return replaceLeafText(node.value, replacements, node.depth);
        // `path` below is threaded even though this callback never reads it.
        // The twin passes it, and two functions documented as twins diverging
        // in their options bag is how the next person extending one to use
        // `path` ships a half-wired change.
    }, { depth, name: parentKey, path: null });
}

function pickContextType(fType) {
    // map fieldType labels to detection types
    if (fType === 'geo-lat' || fType === 'geo-lng') return 'geo-coordinates';
    return fType; // 'person-name', 'street-address', 'city', 'region', 'postal-code', 'country', 'dob'
}

// --- single-pass replacement index (issue #326) -------------------------
//
// The replacement pass used to loop the whole replacement list for EVERY
// string it touched, building an escaped pattern and a fresh RegExp each time
// and scanning the string once per value. Distinct detected values grow with
// body size, so the two factors multiplied and the pass went quadratic: on a
// real capture a single 6.3 MB JSON list response took over 45 s while the
// other 314 entries together took ~10 s, and a 27.8 MB capture did not finish
// in 420 s. A profile put ~67% of self time in `replaceAll` and a further ~21%
// in `escapeRe` -- the escape ran once per (value, string) pair.
//
// The values are literals, not patterns, so a literal matcher does the whole
// job in ONE walk of each string regardless of how many values there are. The
// index below is a trie over every needle, built once per replacement list and
// memoised on that list, so the per-string cost is O(string) instead of
// O(string x values).
//
// PRIORITY IS INSERTION ORDER, and reproducing it exactly is the whole
// difficulty. `scrubPii` sorts the replacement list longest value first, and
// the sequential loop turned that into a guarantee: a longer value was
// replaced EVERYWHERE before a shorter one was even considered.
//
// Preferring the earliest-enrolled needle among those starting at the current
// character is NOT that guarantee, and the difference leaks PII. Two values can
// overlap in two ways:
//
//   NESTED      "Alice Marie Johnson" and "Alice" share a start position. A
//               left-to-right scanner picks the longer one there and is right.
//   STAGGERED   "Ann Marie" and "Marie Louise Johnson" inside
//               "contacted Ann Marie Louise Johnson yesterday" share characters
//               at DIFFERENT start positions, and the SHORTER one starts first.
//               A left-to-right scanner matches "Ann Marie", consumes the
//               "Marie" the longer value needed, and never scans its tail --
//               so "Louise Johnson", real detected PII, ships in the clear and
//               the substitutions table records a fake that is nowhere in the
//               body.
//
// So arbitration cannot be per start position. This collects EVERY match in one
// walk, then resolves them in priority order: each match claims its span unless
// a higher-priority match already holds part of it. A longer value therefore
// wins wherever it occurs no matter what starts earlier, which is exactly what
// the priority-ordered global replace did.
//
// What is deliberately NOT reproduced is that loop scanning its own output.
// Because each pass ran over text already carrying earlier fakes, a value
// occurring inside an emitted fake overwrote part of it, leaving a string that
// was neither an original nor the recorded replacement. Resolving against the
// ORIGINAL text drops that, which is the one intended behaviour change here.

// The trie is keyed by UTF-16 code UNIT, matching `` and keeping surrogate
// pairs consistent with the rest of the file. Codes rather than one-character
// strings: `text[i]` allocates a string per character, and this walks every
// character of every body.
function isWordCode(code) {
    // NaN (index past either end of the string) fails every comparison, which
    // is what `` wants at a boundary of the text.
    return (code >= 48 && code <= 57)
        || (code >= 97 && code <= 122)
        || (code >= 65 && code <= 90)
        || code === 95;
}

function newNode() {
    return { next: new Map(), hit: null };
}

function addNeedle(root, needle, replacement, boundary, priority) {
    if (!needle) return;
    let node = root;
    for (let k = 0; k < needle.length; k++) {
        const ch = needle.charCodeAt(k);
        let child = node.next.get(ch);
        if (!child) { child = newNode(); node.next.set(ch, child); }
        node = child;
    }
    // First enrolment wins: the same literal can be detected under two types,
    // and the sequential loop applied whichever came first in the list.
    if (node.hit === null) {
        node.hit = { length: needle.length, replacement, boundary, priority };
    }
}

// Keyed on the replacement list itself, which holds because `scrubPii` finishes
// building that array before any replacing starts and never touches it again.
// That is a convention, not something the type enforces: anyone who later
// appends to a live replacement list must invalidate this entry, or the new
// values will silently go unscrubbed.
const INDEX_CACHE = new WeakMap();

function indexFor(replacements) {
    let idx = INDEX_CACHE.get(replacements);
    if (idx) return idx;

    const root = newNode();
    const byTypeValue = new Map();
    let priority = 0;

    for (const r of replacements) {
        const v = String(r.value);
        const typeKey = `${r.type}${v}`;
        if (!byTypeValue.has(typeKey)) byTypeValue.set(typeKey, r);
        if (v.length === 0) continue;

        // ONLY A STRING GETS A NEEDLE (issue #529).
        //
        // `replaceInJson` already states the rule for the other direction: a
        // coordinate "is the one type replaced by FIELD NAME alone, without
        // consulting the replacement set -- a number has no string form to
        // enrol". Enrolling one here contradicted that, and the contradiction
        // was not academic. A detected coordinate is a NUMBER, so `String(5)`
        // became the needle `5`, matched EVERY `5` in EVERY string in the
        // document, and replaced it with the coordinate fake `0`. The
        // `boundary` guard below cannot catch this: it only fires for a purely
        // alphabetic value.
        //
        // The observed cost on one capture: `/v4/` became `/v0/`, content
        // hashes and image dimensions were rewritten into fiction, and -- the
        // reason it surfaced at all -- the `-1` needle ate the hyphen out of
        // the scrubber's OWN `redacted-1...` sentinel, so the gate read the
        // redaction as a live credential and quarantined a clean capture.
        //
        // NOT scoped to `geo-coordinates`, because the type is not what makes
        // it wrong: a float coordinate corrupts text the same way (`34.05`
        // inside a price or a version string), just less visibly. The
        // discriminating fact is that the detected value has no text spelling
        // to look for.
        //
        // This costs no reach. A non-string value is replaced by the number
        // branch of `replaceInJson`, never by a text match, and a coordinate
        // spelled as a STRING is not detected as one at all -- the geo branch
        // of `detectInValue` requires `typeof value === 'number'`. What is lost
        // is an accident: a numeric `47.6205` in one body also scrubbed the
        // text `?lat=47.6205` in an unrelated URL, which was the wrong engine
        // papering over a detection gap.
        if (typeof r.value !== 'string') continue;

        // Context-typed values (person-name etc.) may have very short literals
        // (e.g. "Alice") that risk false-positive matches inside unrelated
        // text, so a purely alphabetic value only matches on word boundaries.
        const boundary = /^[A-Za-z]+( [A-Za-z]+)*$/.test(v);
        addNeedle(root, v, r.replacement, boundary, priority++);

        // The same value can appear percent-encoded in the very same entry:
        // detection reads the decoded `queryString` pair, while the `url`
        // carries `phone=%2B1...`. Replacing only the raw spelling leaves the
        // encoded copy readable -- one value, several spellings, which is the
        // failure literal-value scrubbing exists to close (see har-literals.js).
        const encoded = encodeURIComponent(v);
        if (encoded !== v) {
            addNeedle(root, encoded, encodeURIComponent(r.replacement), false, priority++);
        }
    }

    // Direct dispatch for the ASCII first character, so the overwhelmingly
    // common case -- a character that begins no needle at all -- costs an array
    // index rather than a Map lookup.
    const rootAscii = new Array(128);
    for (const [code, node] of root.next) {
        if (code < 128) rootAscii[code] = node;
    }

    idx = { root, rootAscii, byTypeValue, empty: root.next.size === 0 };
    INDEX_CACHE.set(replacements, idx);
    return idx;
}

// --- structure is not payload (issue #66) --------------------------------
//
// The kinds a FIELD NAME alone vouches for. Their detectors (`detectInValue`)
// accept ANY non-empty string at the right field and check nothing about the
// value itself, so the value's own spelling is no evidence of anything. That
// is what the blind text pass got wrong: `name` is an exact person-name field,
// a real capture holds `{"name": "g"}` for an ad slot, and the pass then wrote
// a fake name over every word-bounded `g` in the entry -- the `g` in
// `securepubads.g.doubleclick.net` included. A fake name holds a space, a space
// cannot sit in a host, and 160 of 651 request URLs stopped parsing. The
// capture was signed out; every hit was a false positive.
//
// So where these values meet STRUCTURE -- a URL's scheme and authority, a bare
// path segment, a parameter name, the keys of a JSON document carried as a
// string -- they are left alone. A replace path fails toward a miss. PAYLOAD
// keeps its reach: the value at its own field, and the same value in a query
// value, a header, a cookie or free text, exactly as before.
//
// NOT IN THE SET, deliberately: kinds with a SHAPE. An email, a card, an IBAN,
// a MAC or an IP address carries its own evidence wherever it sits, and its
// fake keeps that shape, so a URL holding one stays well-formed. `phone`,
// `dob` and `device-id` are matched by field AND shape (a digit run, an ISO
// date, a UUID) -- shaped enough that the same literal elsewhere is the same
// datum and no fake of theirs breaks a URL. `geo-coordinates` is a number and
// never enters the text pass at all (#529).
//
// THE PATH SEGMENT DECISION, and its cost. A bare segment has no name, so a
// field-only value there has no evidence but its spelling -- on the capture
// above, every one of 55 rewritten paths was a false positive. A segment that
// DECODES to a JSON document keeps the field-typed treatment, because its keys
// are evidence. The residual is stated rather than hidden: a real username a
// REST path carries (`/users/<name>`) is no longer replaced there. The
// operator's own identifiers are the literal pass's job and it still runs over
// URLs; a third party's name in a path is the miss this rule accepts.
const FIELD_ONLY_KINDS = new Set([
    'person-name', 'street-address', 'city', 'region', 'postal-code', 'country',
]);

const STRUCTURE_CACHE = new WeakMap();

/**
 * The replacements that may act on STRUCTURE: every entry but the field-only
 * kinds, in the same longest-first order. Memoised on the list for the reason
 * `indexFor` is, so `indexFor` can then memoise the result in turn.
 *
 * Returns the list ITSELF when it holds no field-only kind, so a caller can
 * tell "nothing to withhold" by identity and skip work.
 */
function structureReplacements(replacements) {
    let structure = STRUCTURE_CACHE.get(replacements);
    if (structure) return structure;
    structure = replacements.some((r) => FIELD_ONLY_KINDS.has(r.type))
        ? replacements.filter((r) => !FIELD_ONLY_KINDS.has(r.type))
        : replacements;
    STRUCTURE_CACHE.set(replacements, structure);
    return structure;
}

/**
 * Plain text that is `&`-separated `name=value` parts -- a URL query the strict
 * form reader (`formEncodedPairs`) refused -- rewritten with each part's NAME
 * as structure and its VALUE as payload (#66).
 *
 * The form reader refuses a whole query over one part it cannot read: a flag
 * with no `=` (`&debug`), or an unencoded URL. That refusal is right for
 * BODIES, where it keeps a script or a page off the form path, and the query
 * then fell to one blind pass that renamed its parameters -- 16 real URLs after
 * the authority fix. A query's grammar is not in doubt, though, so its parts
 * are read here without the form reader's other demands. A part with no `=` is
 * a flag, which is a name. Only the list changes per piece: the shaped kinds
 * still reach every character, and no shaped value holds a `&` or `=` for the
 * split to cut. On the decoded route an escaped `%26` inside a value reads as a
 * delimiter here, so a field-only value after it is withheld -- a miss, the
 * direction a replace path takes.
 *
 * Spans are reported in `text`'s coordinates, for the per-span write-back.
 */
function replaceParamText(text, replacements, spansOut) {
    const structure = structureReplacements(replacements);
    if (structure === replacements) return replaceAll(text, replacements, spansOut);
    let out = '';
    let at = 0;
    const piece = (s, list) => {
        const local = spansOut ? [] : undefined;
        out += replaceAll(s, list, local);
        if (local) {
            for (const span of local) {
                spansOut.push({ from: span.from + at, to: span.to + at, replacement: span.replacement });
            }
        }
        at += s.length;
    };
    const delimiter = (c) => { out += c; at += 1; };
    text.split('&').forEach((part, k) => {
        if (k > 0) delimiter('&');
        const eq = part.indexOf('=');
        if (eq < 0) { piece(part, structure); return; }
        piece(part.slice(0, eq), structure);
        delimiter('=');
        piece(part.slice(eq + 1), replacements);
    });
    return out === text ? text : out;
}

/**
 * The flat text pass over ONE string leaf of a parsed document, before the
 * nested walk (`replaceNestedLeaf`) looks inside it.
 *
 * A leaf that is itself a JSON document or a form body has NAMES of its own --
 * a GraphQL `variables` string, an analytics `payload`. The flat pass reads it
 * as plain text, so it reached those names before the nested walk ever parsed
 * them, and a field-only value equal to a key renamed the key. For such a leaf
 * the field-only kinds are left to the nested walk, which visits every VALUE
 * inside it with the full list and leaves its names alone; the flat pass keeps
 * the shaped kinds. Same reach on values, none on names.
 *
 * Only where the walk will actually descend: at the shared depth cap the walk
 * stops, and the flat pass is all that is left, so it keeps the full list.
 * Asked only when a field-only needle changed something, so a leaf nothing
 * matched pays nothing extra for the question. The one value this gives up is a
 * nested form parameter the walk itself cannot decode (a malformed escape),
 * which it passes over whole -- a miss, on a replace path, in an input no
 * capture has been seen to carry.
 *
 * `walkDepth` is the depth `transformNested` will be called at for this leaf.
 */
function replaceLeafText(text, replacements, walkDepth) {
    const all = replaceAll(text, replacements);
    if (all === text) return text;
    const structure = structureReplacements(replacements);
    if (structure === replacements || walkDepth >= NESTED_MAX_DEPTH) return all;
    const shaped = replaceAll(text, structure);
    if (shaped === all) return all;
    return isNamedLayer(text) ? shaped : all;
}

// One walk of the text, recording EVERY needle that matches anywhere -- nested
// and overlapping matches included. Arbitration happens afterwards, because a
// match cannot be judged against a competitor that has not been found yet.
//
// Matches are held as two parallel arrays rather than objects. This runs over
// every character of every body, and a per-match object is an allocation the
// garbage collector then has to chase; the `hit` records already live in the
// trie, so a reference plus a start offset is the whole match.
function collectMatches(text, idx, outStarts, outHits) {
    const root = idx.root;
    const rootAscii = idx.rootAscii;
    const n = text.length;
    let count = 0;
    for (let i = 0; i < n; i++) {
        const code = text.charCodeAt(i);
        let node = code < 128 ? rootAscii[code] : root.next.get(code);
        if (node === undefined) continue;
        let j = i;
        for (;;) {
            const hit = node.hit;
            if (hit !== null
                && (!hit.boundary
                    || (!isWordCode(text.charCodeAt(i - 1))
                        && !isWordCode(text.charCodeAt(i + hit.length))))) {
                outStarts[count] = i;
                outHits[count] = hit;
                count++;
            }
            j++;
            if (j >= n) break;
            const child = node.next.get(text.charCodeAt(j));
            if (child === undefined) break;
            node = child;
        }
    }
    return count;
}

// Scratch buffers reused across calls. `replaceAll` runs once per string leaf
// and never re-enters itself, so a single set is safe, and reusing them keeps
// a body of many small leaves from allocating two typed arrays per leaf.
let CLAIMED = new Uint8Array(0);
let ACCEPTED = new Uint8Array(0);

function scratch(buf, need) {
    if (buf.length < need) return new Uint8Array(need < 1024 ? 1024 : need * 2);
    buf.fill(0, 0, need);
    return buf;
}

function replaceAll(text, replacements, spansOut) {
    const idx = indexFor(replacements);
    if (idx.empty || text.length === 0) return text;

    const starts = [];
    const hits = [];
    const count = collectMatches(text, idx, starts, hits);
    if (count === 0) return text;

    // Resolve highest priority first; ties (the same needle occurring more than
    // once) left to right, the order the global replace visited them in. The
    // order array is sorted, not the match arrays, so the matches stay in
    // ascending start order for the emit pass below and need no second sort.
    const order = new Array(count);
    for (let k = 0; k < count; k++) order[k] = k;
    order.sort((a, b) => (hits[a].priority - hits[b].priority) || (starts[a] - starts[b]));

    CLAIMED = scratch(CLAIMED, text.length);
    ACCEPTED = scratch(ACCEPTED, count);
    const claimed = CLAIMED;
    const accepted = ACCEPTED;

    // Each match takes its span unless a higher-priority one already holds part
    // of it -- so a longer value wins wherever it occurs, whatever starts first.
    for (let k = 0; k < count; k++) {
        const m = order[k];
        const from = starts[m];
        const to = from + hits[m].length;
        let free = true;
        for (let q = from; q < to; q++) {
            if (claimed[q]) { free = false; break; }
        }
        if (!free) continue;
        for (let q = from; q < to; q++) claimed[q] = 1;
        accepted[m] = 1;
    }

    let out = '';
    let copiedTo = 0;   // everything before this index is already in `out`
    for (let k = 0; k < count; k++) {
        if (!accepted[k]) continue;
        const from = starts[k];
        out += text.slice(copiedTo, from) + hits[k].replacement;
        copiedTo = from + hits[k].length;
        // WHERE each accepted match landed, for a caller that has to write the
        // result back into differently-encoded bytes. Arbitration already
        // happened above, so these spans are ascending and non-overlapping --
        // the invariant the per-span write-back relies on.
        if (spansOut) {
            spansOut.push({ from, to: from + hits[k].length, replacement: hits[k].replacement });
        }
    }
    return copiedTo === 0 ? text : out + text.slice(copiedTo);
}

module.exports = {
    PII_TYPES,
    FIELD,
    fieldType,
    fieldTypeFor,
    keyWords,
    detectPii,
    detectPiiInEntry,
    planPiiReplacements,
    applyReplacementsToEntry,
    fakeFor,
    scrubPii,
    scrubSettingFor,
    disabledIdentityClasses,
    hashPrefix,
    luhnOk
};
