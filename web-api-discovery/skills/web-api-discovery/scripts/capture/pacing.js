'use strict';

/**
 * pacing.js -- polite pacing, and what to do when a site throttles or blocks.
 *
 * Two jobs, both pure enough to test without a browser:
 *
 *   PACING. Every action THIS TOOLING takes on a page -- the recorder's own
 *   first navigation, the sign-in fills (#115), capture-cdp.js's navigation --
 *   goes through a pacer: a randomized, human-scale wait between navigations
 *   and between clicks or typing steps, one navigation in flight at a time,
 *   and a ceiling on navigations per minute. The same numbers are written into
 *   the -AI prompt, because the agent's actions are the agent's: the recorder
 *   observes them, and cannot slow them down.
 *
 *   THROTTLING. Every response the recorder records is offered to a monitor.
 *   A 429, a 503 that names a Retry-After, a small HTML 403 from a CDN, or a
 *   challenge page is a signal. A throttle PAUSES THE WHOLE CAPTURE -- for
 *   exactly the Retry-After the site names (up to a day), or for a backoff
 *   with jitter when it names none -- and then it carries on by itself. A
 *   block, a challenge, or throttling that goes on for an hour with no page
 *   loading in between HANDS OVER: whatever is driving is told to wait, and
 *   the human -- whose browser it is -- decides. capture-pause.js holds the
 *   browser's requests to the site for the length of a pause, and tells the
 *   agent; nothing here ever ends a process.
 *
 * THE BOUNDARY, stated once here and in SKILL.md. Polite pacing and honouring
 * a site's throttling are in. Anything whose PURPOSE is hiding automation from
 * a site's bot detection is out: no user-agent or fingerprint spoofing, no
 * `navigator.webdriver` or stealth patches, no CAPTCHA solving, no retrying a
 * block until it clears. The wait between actions is there because it is how a
 * person uses a site and what a site serving people is sized for -- not to
 * look like one. When a site says no, this tooling stops and says so.
 *
 * Zero dependencies. Time, randomness and sleeping are injected everywhere so
 * the tests can pin the bounds without waiting on a clock.
 */

// ---------------------------------------------------------------------------
// Pace
// ---------------------------------------------------------------------------

/**
 * The default pace: what a person browsing does. Milliseconds, [min, max].
 * `perMinute` is a ceiling on NAVIGATIONS (top-level page loads) in any rolling
 * minute; 0 means no ceiling.
 */
const DEFAULT_PACE = Object.freeze({
    navigation: Object.freeze([1500, 4000]),
    action: Object.freeze([300, 1200]),
    perMinute: 12
});

/** Every wait zero and no ceiling -- for tests, never a default. */
const PACE_OFF = Object.freeze({
    navigation: Object.freeze([0, 0]),
    action: Object.freeze([0, 0]),
    perMinute: 0,
    off: true
});

const PACE_KEYS = { nav: 'navigation', navigation: 'navigation', action: 'action', 'per-minute': 'perMinute' };

/**
 * The longest single wait a `--pace` range may ask for: 10 minutes. Longer is
 * clamped to it, and the run says so. Without a bound, Node runs a timer above
 * 2^31-1 ms after 1 ms -- a huge wait became no wait at all.
 */
const MAX_PACE_WAIT_MS = 10 * 60 * 1000;

/**
 * Parse a `--pace` value.
 *
 *   (absent) | human     the default pace
 *   off | 0              every wait zero, no ceiling (tests); so is a spec
 *                        that sets every wait and the ceiling to 0
 *   nav=1500-4000,action=300-1200,per-minute=12
 *                        any subset; what is not named keeps its default;
 *                        each wait at most MAX_PACE_WAIT_MS
 *
 * Refused, never guessed: a typo must not quietly become "no pacing".
 *
 * @returns {{pace: object, notes?: string[]} | {error: string}} -- `notes`
 *          are what the caller tells the operator (a clamped wait)
 */
function parsePace(spec) {
    if (spec === undefined || spec === null) return { pace: DEFAULT_PACE };
    const text = String(spec).trim().toLowerCase();
    if (text === '' || text === 'human') return { pace: DEFAULT_PACE };
    if (text === 'off' || text === '0') return { pace: PACE_OFF };
    const pace = { navigation: DEFAULT_PACE.navigation.slice(), action: DEFAULT_PACE.action.slice(),
        perMinute: DEFAULT_PACE.perMinute };
    const notes = [];
    for (const part of text.split(',')) {
        const m = /^\s*([a-z-]+)\s*=\s*(.+?)\s*$/.exec(part);
        const key = m && PACE_KEYS[m[1]];
        if (!key) {
            return { error: `--pace: '${part.trim()}' is not one of off, human, ` +
                'nav=MIN-MAX, action=MIN-MAX, per-minute=N' };
        }
        if (key === 'perMinute') {
            if (!/^\d+$/.test(m[2])) return { error: `--pace: per-minute must be a whole number, not '${m[2]}'` };
            pace.perMinute = Number(m[2]);
            continue;
        }
        const range = /^(\d+)(?:\s*-\s*(\d+))?$/.exec(m[2]);
        if (!range) return { error: `--pace: ${m[1]} must be MIN-MAX milliseconds, not '${m[2]}'` };
        const lo = Number(range[1]);
        const hi = range[2] === undefined ? lo : Number(range[2]);
        if (hi < lo) return { error: `--pace: ${m[1]}=${m[2]} has its maximum below its minimum` };
        if (hi > MAX_PACE_WAIT_MS) {
            notes.push(`--pace ${m[1]}=${m[2]}: a wait is at most 10 minutes (${MAX_PACE_WAIT_MS} ms); ` +
                'longer is clamped to that.');
        }
        pace[key] = [Math.min(lo, MAX_PACE_WAIT_MS), Math.min(hi, MAX_PACE_WAIT_MS)];
    }
    // Every wait zero and no ceiling is `off` by another spelling -- and is
    // marked as test mode the same way.
    if (pace.navigation[1] === 0 && pace.action[1] === 0 && pace.perMinute === 0) return { pace: PACE_OFF };
    return notes.length ? { pace: Object.freeze(pace), notes } : { pace: Object.freeze(pace) };
}

function seconds(ms) {
    const s = ms / 1000;
    return Number.isInteger(s) ? String(s) : s.toFixed(1).replace(/\.0$/, '');
}

/** One line a person (or the prompt) can read. */
function describePace(pace) {
    if (!pace || pace.off) return 'off -- no wait between actions (test mode)';
    const range = ([lo, hi]) => (lo === hi ? `${seconds(lo)} s` : `${seconds(lo)}-${seconds(hi)} s`);
    return `${range(pace.navigation)} between navigations, ${range(pace.action)} between clicks or ` +
        'typing steps' + (pace.perMinute ? `, at most ${pace.perMinute} navigations a minute` : '');
}

/** An integer in [min, max], uniformly. */
function randomBetween(min, max, rng = Math.random) {
    if (max <= min) return min;
    return Math.round(min + rng() * (max - min));
}

const defaultSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * The pacer for actions this tooling takes itself.
 *
 *   navigate(fn)   one navigation in flight at a time: waits for the previous
 *                  one to finish, then for the random gap, the per-minute
 *                  ceiling and any pause, then runs fn
 *   act(fn)        the same for a click or a typing step, on the action range
 *   pauseFor(ms)   a throttle pause: nothing the pacer runs starts before it
 *                  ends. Applied even with the pace off -- honouring a site's
 *                  Retry-After is not pacing, and turning pacing off for a test
 *                  must not turn it off
 *   resumeNow()    the pause is over early -- a person handed the browser back
 *
 * The gap is measured from the END of the previous step, and time already
 * spent since then counts towards it: a person who read the page for ten
 * seconds does not then wait another three.
 */
function createPacer(pace = DEFAULT_PACE, opts = {}) {
    const sleep = opts.sleep || defaultSleep;
    const now = opts.now || Date.now;
    const rng = opts.rng || Math.random;
    const navigations = [];
    let lastNavigationEnd = null;
    let lastActionEnd = null;
    let pausedUntil = 0;
    let navChain = Promise.resolve();
    let actChain = Promise.resolve();
    const waits = [];

    function ceilingWait(t) {
        if (!pace.perMinute) return 0;
        while (navigations.length && navigations[0] <= t - 60000) navigations.shift();
        if (navigations.length < pace.perMinute) return 0;
        return navigations[0] + 60000 - t;
    }

    async function waitBefore(kind) {
        const t = now();
        const range = kind === 'navigation' ? pace.navigation : pace.action;
        const last = kind === 'navigation' ? lastNavigationEnd : lastActionEnd;
        const gap = last === null ? 0 : Math.max(0, randomBetween(range[0], range[1], rng) - (t - last));
        const ceiling = kind === 'navigation' ? ceilingWait(t) : 0;
        const pause = Math.max(0, pausedUntil - t);
        const wait = Math.max(gap, ceiling, pause);
        waits.push({ kind, wait, gap, ceiling, pause });
        if (wait > 0) await sleep(wait);
        // A pause that was EXTENDED while this step slept (the site asked for
        // longer) is waited out too: a step never starts inside a pause.
        while (pausedUntil > now()) await sleep(pausedUntil - now());
    }

    function serialize(kind, fn) {
        const run = async () => {
            await waitBefore(kind);
            if (kind === 'navigation') navigations.push(now());
            try {
                return await fn();
            } finally {
                if (kind === 'navigation') lastNavigationEnd = now();
                lastActionEnd = now();
            }
        };
        const chain = kind === 'navigation' ? navChain : actChain;
        const result = chain.then(run, run);
        const settled = result.then(() => {}, () => {});
        if (kind === 'navigation') navChain = settled; else actChain = settled;
        return result;
    }

    return {
        pace,
        navigate: (fn) => serialize('navigation', fn),
        act: (fn) => serialize('action', fn),
        /** Wait as an action would, with nothing to run: for a step whose effect is already elsewhere. */
        beforeAction: () => serialize('action', () => undefined),
        pauseFor(ms) {
            if (ms > 0) pausedUntil = Math.max(pausedUntil, now() + ms);
            return pausedUntil;
        },
        /**
         * End a pause early. A step already asleep finishes its sleep -- a
         * person handing back is rarely a matter of milliseconds -- but no
         * step waits on the pause from here on.
         */
        resumeNow() { pausedUntil = 0; },
        pausedUntil: () => pausedUntil,
        /** Every wait taken, for tests and the verbose log. */
        waits: () => waits.slice()
    };
}

// ---------------------------------------------------------------------------
// Throttling
// ---------------------------------------------------------------------------

/**
 * THE LONGEST WAIT A SITE MAY NAME AND STILL BE WAITED OUT. A sanity ceiling,
 * not a cap: anything up to it is waited IN FULL, because "just pause" means
 * pause until the time is up and then carry on. A Retry-After beyond a day is
 * not a pause any more, and a person decides.
 */
const RETRY_AFTER_CEILING_MS = 24 * 60 * 60 * 1000;

/**
 * THE BACKOFF WHEN A SITE THROTTLES AND NAMES NO WAIT: about 1, 2, 4 and 8
 * minutes, then 15 minutes every time after. Each wait takes up to
 * BACKOFF_JITTER of its step off at random, so it is never in lockstep with
 * another client throttled at the same moment, and never longer than the cap.
 */
const BACKOFF_BASE_MS = 60 * 1000;
const BACKOFF_MAX_MS = 15 * 60 * 1000;
const BACKOFF_JITTER = 0.2;

/**
 * HOW LONG A SITE MAY GO ON THROTTLING, NAMING NO WAIT, BEFORE A PERSON
 * DECIDES: an hour without one page of the site loading. Counted from the
 * first throttle after the last page that loaded, so a pause that worked --
 * a page loads afterwards -- starts the count again. A site that NAMES its
 * wait is taken at its word every time (up to RETRY_AFTER_CEILING_MS a wait):
 * this rule is for a site that only ever says no.
 */
const THROTTLE_GIVE_UP_MS = 60 * 60 * 1000;

/**
 * THE BURST WINDOW. A throttle that arrives while a pause is already running,
 * or within this of its start, and names a wait that ends no more than this
 * after the pause does, is the same pause: the spread of one page's requests
 * being refused together, not news.
 */
const PAUSE_EXTEND_TOLERANCE_MS = 2000;

/**
 * A response header, from either shape the recorder sees: Playwright's
 * lower-cased object, or a HAR `[{name, value}]` list.
 */
function headerValue(headers, name) {
    if (!headers) return undefined;
    const want = name.toLowerCase();
    if (Array.isArray(headers)) {
        const h = headers.find((x) => x && String(x.name).toLowerCase() === want);
        return h ? String(h.value) : undefined;
    }
    for (const key of Object.keys(headers)) {
        if (key.toLowerCase() === want) return String(headers[key]);
    }
    return undefined;
}

/**
 * Retry-After, RFC 9110 section 10.2.3: delay-seconds or an HTTP-date.
 *
 * @returns {number|null} milliseconds from `nowMs` (never negative), or null
 *          when the header is absent or is neither form.
 */
function parseRetryAfter(value, nowMs = Date.now()) {
    if (value === undefined || value === null) return null;
    const text = String(value).trim();
    if (!text) return null;
    if (/^\d+$/.test(text)) return Number(text) * 1000;
    // An HTTP-date always names a weekday and GMT; a bare number with a decimal
    // point or a sign is neither form, and Date.parse would accept it.
    if (!/[a-z]/i.test(text)) return null;
    const at = Date.parse(text);
    if (Number.isNaN(at)) return null;
    return Math.max(0, at - nowMs);
}

/**
 * Exponential backoff with jitter: the step doubles from BACKOFF_BASE_MS to
 * BACKOFF_MAX_MS, and up to BACKOFF_JITTER of it is taken off at random. Never
 * zero, so a backoff is always a real wait; never above the cap, so "15 minutes
 * at most" is a promise; and never in lockstep with another client that was
 * throttled at the same moment.
 */
function backoffMs(attempt, opts = {}) {
    const baseMs = opts.baseMs === undefined ? BACKOFF_BASE_MS : opts.baseMs;
    const maxMs = opts.maxMs === undefined ? BACKOFF_MAX_MS : opts.maxMs;
    const jitter = opts.jitter === undefined ? BACKOFF_JITTER : opts.jitter;
    const rng = opts.rng || Math.random;
    const step = Math.min(maxMs, baseMs * 2 ** Math.max(0, attempt - 1));
    return Math.max(1, Math.round(step * (1 - jitter * rng())));
}

// The body of a block or challenge page is small; a real page that merely
// embeds a CAPTCHA widget on a sign-in form is not. Only this much of a body
// is read for the signatures below, and a refusal (403, 503) is judged on them
// only below it.
const CHALLENGE_BODY_MAX = 64 * 1024;

// A page that ANSWERED 2xx or 3xx is judged an interstitial only below this
// much tighter bound. The vendor interstitials are a few kilobytes; an ordinary
// page is usually far more, and a wrong call here hands over a page that loaded.
const INTERSTITIAL_BODY_MAX = 16 * 1024;

// How far into a body a DEEP signature looks (see INTERSTITIALS).
const DEEP_SCAN_MAX = 1024 * 1024;

const marker = (re) => (ctx) => re.test(ctx.text);

// How far before its literal a marker may open: the checkpoint's `<form` before
// its action, DataDome's `var dd={` before its CAPTCHA host.
const FORM_TAG_REACH = 2048;
const DD_OBJECT_REACH = 4096;

/**
 * Does `literal` (lower case) occur in `text` with what `opens` accepts in the
 * `reach` characters before it? `opens(before, next)` gets that stretch,
 * lower-cased, and the character after the literal.
 *
 * FOUND BY ITS LITERAL FIRST. A regex that opens on `<form` and runs to the
 * marker backtracks across the whole page when the page never closes the tag:
 * an unclosed `<form ` repeated to 1 MB did not finish in five minutes, and the
 * scan is synchronous, so it stalled ENTER, the stop command and the recording.
 * Here a page without the literal is one plain search, and each occurrence costs
 * at most `reach`.
 */
function precededBy(text, literal, reach, opens) {
    const lower = text.toLowerCase();
    for (let at = lower.indexOf(literal); at !== -1; at = lower.indexOf(literal, at + literal.length)) {
        if (opens(lower.slice(Math.max(0, at - reach), at), lower.charAt(at + literal.length))) return true;
    }
    return false;
}

// A <form> tag still open -- no `>` since its `<form` -- whose action is the
// checkpoint's validator. The attribute is `action` itself, after whitespace:
// a hyphen is a word boundary, so `\baction` would take data-action= too.
const checkpointForm = (page) => precededBy(page, '/checkpoint/validate', FORM_TAG_REACH, (before, next) => {
    const open = before.lastIndexOf('<form');
    return !/\w/.test(next) && open !== -1 &&
        /^<form\b[^>]*\saction\s*=\s*["']?(?:https?:\/\/[^/"'\s>]+)?$/.test(before.slice(open));
});

// DataDome's `dd` object, still open -- no `}` since its `{` -- naming its CAPTCHA host.
const ddObject = (text) => precededBy(text, 'captcha-delivery.com', DD_OBJECT_REACH,
    (before) => /\bvar\s+dd\s*=\s*\{/.test(before.slice(before.lastIndexOf('}') + 1)));

// Signatures of bot-protection INTERSTITIALS: pages that stand in for the one
// asked for. Each tests for a marker ONLY the interstitial carries -- never the
// vendor's script, a preconnect to its host, or wording an ordinary page can
// use. Those all sit on ordinary pages of a protected site: reCAPTCHA v3 runs
// invisibly on sign-in pages, an Imperva site loads /_Incapsula_Resource
// scripts, a Cloudflare site serves /cdn-cgi/challenge-platform/ scripts on
// every page, and a DataDome site may preconnect to captcha-delivery.com.
// Matching any of them handed over on a page that had loaded.
//
// A DEEP signature is read up to DEEP_SCAN_MAX and is exempt from the size
// bounds: its marker is a form that posts to the vendor's own validator, and the
// page around it is a full-size one with that form far down.
//
// FIXTURES (pacing.test.js): the vendor shapes are hand-written, minimal, from
// what each interstitial is known to contain -- there is no captured vendor page
// to test against. Amazon's checkpoint is the exception: it is derived from a
// capture.
const INTERSTITIALS = [
    // Its exact title counts too: unlike the challenge-platform scripts a
    // Cloudflare site serves on every page, it names the interstitial itself.
    { name: 'Cloudflare challenge', test: marker(new RegExp('<title>\\s*just a moment\\.\\.\\.\\s*</title>|' +
        'window\\._cf_chl_opt\\b|[?&]__cf_chl_(?:f_tk|jschl_tk__)=|' +
        '/cdn-cgi/challenge-platform/(?:h/[a-z]/)?orchestrate/', 'i')) },
    { name: 'Cloudflare block', test: marker(
        /<title>\s*attention required! \| cloudflare\s*<\/title>|\bid\s*=\s*["']?cf-error-details\b/i) },
    // The block page hands its parameters to the CAPTCHA in a `dd` object.
    { name: 'DataDome CAPTCHA', test: (ctx) => ddObject(ctx.text) },
    { name: 'PerimeterX "Press & Hold"', test: marker(/\bid\s*=\s*["']?px-captcha\b/i) },
    // The block page is an iframe onto /_Incapsula_Resource?CWUDNSAI=..., with
    // the incident ID as its fallback text.
    { name: 'Imperva', test: marker(/incapsula incident id|_incapsula_resource\?[^"'\s>]*\bcwudnsai=/i) },
    // Akamai's page ENTITY-ENCODES its reference -- `Reference&#32;&#35;18&#46;...`
    // -- and links it on errors.edgesuite.net; either spelling counts.
    { name: 'Akamai "Access Denied"', test: marker(new RegExp('<title>\\s*access denied\\s*</title>[\\s\\S]*' +
        '(?:reference(?:\\s|&#32;|&nbsp;)*(?:#|&#35;)\\s*\\d|errors(?:\\.|&#46;)edgesuite(?:\\.|&#46;)net)', 'i')) },
    // Amazon answers a signed-out visitor it doubts with a full storefront page
    // whose middle is a form to /checkpoint/validate: "Check the box to
    // continue", a checkbox labelled "I am human". The FORM is what says so,
    // never the URL: any site can have a /checkpoint/ path.
    { name: 'Amazon "Check the box to continue" checkpoint', deep: true, test: (ctx) => {
        const page = ctx.deep();
        return checkpointForm(page) && /check the box to continue|\bi am human\b/i.test(page);
    } }
];

// What a bot-protection edge answers a refused XHR or fetch with, in place of
// its HTML page. Judged on refusals only, whatever the content type. The BODY
// decides, never a vendor's header: DataDome's x-datadome is on every response
// of a site behind it, the ones it lets through included.
const BLOCK_RESPONSES = [
    // {"url":"https://geo.captcha-delivery.com/captcha/?initialCid=..."}
    { name: 'DataDome block', test: marker(/"url"\s*:\s*"https?:\/\/geo\.captcha-delivery\.com\/(?:captcha|interstitial)\//i) },
    // {"appId":"PX...","blockScript":"/.../captcha.js?...", ...}
    { name: 'PerimeterX block', test: (ctx) => /"appId"\s*:\s*"PX\w+"/i.test(ctx.text) && /"blockScript"\s*:/i.test(ctx.text) }
];

// WORDING an ordinary page can use too: a sign-up form's "verify you are
// human", a help page's "are you a robot?", a title naming a CAPTCHA game. It
// is never a challenge on its own. It counts only on a refusal -- an HTML 403
// or 503 -- where the site has already said no and the wording says why.
const REFUSAL_WORDING = [
    { name: 'a "Press & Hold" page', re: /press (?:&amp;|&) hold/i },
    { name: 'an "unusual traffic" page', re: /unusual traffic from your (?:computer|network)|are you a robot\?/i },
    { name: 'a "verify you are human" page', re: /verify (?:that )?you are (?:a )?human/i },
    { name: 'a CAPTCHA page', re: /<title>[^<]*captcha[^<]*<\/title>/i }
];

// Headers only a CDN or bot-protection edge sets -- and sets on EVERY response
// of a site behind it: cf-ray, x-amz-cf-id and DataDome's x-datadome alike are
// on the pages it serves and the ones it lets through, not only its refusals.
// So one is weak evidence (see classifyResponse).
const CDN_HEADERS = ['cf-ray', 'x-iinfo', 'x-cdn', 'x-datadome', 'x-akamai-transformed', 'akamai-grn',
    'x-amz-cf-id', 'x-sucuri-id', 'x-px'];
const CDN_SERVERS = /cloudflare|akamai|incapsula|imperva|datadome|sucuri|cloudfront|ddos-guard/i;

function bodyText(body, max = CHALLENGE_BODY_MAX) {
    if (body === undefined || body === null) return '';
    if (Buffer.isBuffer(body)) return body.subarray(0, max).toString('utf8');
    return String(body).slice(0, max);
}

/**
 * What a signature is tested against: the head of the body, and more of the
 * body for a deep signature. Each is decoded only when a signature asks for it
 * -- most responses never are.
 */
function signatureContext(r) {
    let text = null;
    let deep = null;
    return {
        get text() { return text === null ? (text = bodyText(r.body)) : text; },
        deep: () => (deep === null ? (deep = bodyText(r.body, DEEP_SCAN_MAX)) : deep)
    };
}

function bodySize(body) {
    if (body === undefined || body === null) return 0;
    return Buffer.isBuffer(body) ? body.length : Buffer.byteLength(String(body));
}

/**
 * Is this response the site throttling or refusing the visitor?
 *
 * `r` = { url, status, headers, mimeType?, body?, isDocument, onSite }
 *   isDocument  a top-level (main-frame) document
 *   onSite      the request's host is the capture's site (or a subdomain)
 *
 * ONLY THE SITE COUNTS. An in-scope page talks to trackers and ad networks
 * that answer 403, 429 and 503 all day; none of that is the site asking this
 * visitor to slow down. A signal is a main-frame document, or a request to
 * the site's own host.
 *
 * @returns {null | {kind: 'throttled'|'blocked'|'challenge', status, url,
 *           retryAfterMs: number|null, signature: string}}
 */
function classifyResponse(r, nowMs = Date.now()) {
    if (!r || !(r.isDocument || r.onSite)) return null;
    const status = Number(r.status) || 0;
    const headers = r.headers || {};
    const retryAfterMs = parseRetryAfter(headerValue(headers, 'retry-after'), nowMs);
    const signal = (kind, signature) => ({ kind, status, url: r.url, retryAfterMs, signature });

    if (status === 429) return signal('throttled', '429 Too Many Requests');
    if (status === 503 && retryAfterMs !== null) return signal('throttled', '503 with Retry-After');

    const mitigated = headerValue(headers, 'cf-mitigated');
    if (mitigated && /challenge/i.test(mitigated)) return signal('challenge', 'Cloudflare challenge (cf-mitigated)');

    const mime = String(r.mimeType || headerValue(headers, 'content-type') || '').toLowerCase();
    const html = mime.includes('html') || (!mime && r.isDocument);
    const ctx = signatureContext(r);
    const size = bodySize(r.body);
    const small = size <= CHALLENGE_BODY_MAX;
    const interstitial = (bound) => INTERSTITIALS.find((s) => (s.deep || size <= bound) && s.test(ctx));

    if (status === 403 || status === 503) {
        // A KNOWN BLOCK IS ONE WHATEVER ITS CONTENT TYPE. An edge refusing an
        // XHR or a fetch answers it with its JSON, or its HTML page, while the
        // page on screen stays up -- and an -AI agent's API calls are exactly
        // what gets refused that way.
        const known = interstitial(CHALLENGE_BODY_MAX) ||
            (small ? BLOCK_RESPONSES.find((s) => s.test(ctx)) : null) ||
            (html && small ? REFUSAL_WORDING.find((s) => s.re.test(ctx.text)) : null);
        if (known) return signal('blocked', `${status} ${known.name}`);
        if (!html) return null;
        // EDGE HEADERS ALONE are weak evidence: a CDN-fronted site puts cf-ray
        // or x-amz-cf-id on EVERY response, its own error pages included, and
        // the checklist asks for error cases. So without a known signature only
        // the page itself refused with a small HTML 403 counts -- never a
        // sub-resource, and never a 503, which without Retry-After or a
        // signature is an outage rather than a refusal.
        const server = headerValue(headers, 'server') || '';
        const fromEdge = CDN_SERVERS.test(server) || CDN_HEADERS.some((h) => headerValue(headers, h) !== undefined);
        if (status === 403 && r.isDocument && fromEdge && small) {
            return signal('blocked', `403 from the CDN (${server || 'edge headers'})`);
        }
        return null;
    }
    // A page that answered 2xx or 3xx but IS an interstitial: judged on
    // main-frame documents only, where it replaces the page that was asked for,
    // and only on markers and inside the tighter bound -- an AWS WAF script
    // check that answers 202 and passes by itself in any browser is neither.
    if (!html || !r.isDocument || status < 200 || status >= 400) return null;
    const known = interstitial(INTERSTITIAL_BODY_MAX);
    return known ? signal('challenge', known.name) : null;
}

/**
 * Is this response a page of the site that LOADED -- the progress that ends a
 * run of throttling? A main-frame document that answered 2xx or 3xx and is not
 * itself a block or a challenge. A sub-resource that succeeds is not progress:
 * a site metering its API can go on serving its images and analytics.
 */
function isPageLoad(r, nowMs = Date.now()) {
    if (!r || !r.isDocument) return false;
    const status = Number(r.status) || 0;
    return status >= 200 && status < 400 && classifyResponse(r, nowMs) === null;
}

/**
 * Decide what each signal means for whatever is driving.
 *
 *   { action: 'pause', waitMs, until, named }
 *                       a throttle: the WHOLE capture waits -- for exactly the
 *                       Retry-After the site named (`named`), or for a backoff
 *                       with jitter -- and then carries on by itself.
 *                       `extended` when it lengthens a pause already running
 *   { action: 'hand-over', why }
 *                       a person decides; whatever drives is told to wait
 *   { action: 'none' }  part of a pause already running, or after a
 *                       hand-over nobody has handed back yet
 *
 * WHEN A THROTTLE BECOMES A HAND-OVER -- only these, and never sooner:
 *   - the site names a wait longer than RETRY_AFTER_CEILING_MS (a day);
 *   - the site names NO wait, and has gone on throttling for
 *     THROTTLE_GIVE_UP_MS (an hour) with no page loading in between
 *     (`progress()` is told of every page that loads).
 * A block or a challenge hands over at once. Nothing here ever retries them.
 *
 * A THROTTLE DURING A PAUSE IS THE SAME PAUSE. The browser holds the site's
 * requests while a pause runs (capture-pause.js), so what still arrives then
 * was already in flight when it began -- one page's requests refused together.
 * It lengthens the pause if it names a longer wait, and is otherwise nothing.
 * (This used to hand over as "the driver ignored the pause". With the hold, a
 * driver cannot reach the site during a pause, so that is no longer evidence
 * of anything but timing.)
 *
 * `handedBack()` re-arms the monitor after a person hands the browser back: a
 * second bot check later in the same recording is a second hand-over.
 */
function createThrottleMonitor(opts = {}) {
    const now = opts.now || Date.now;
    const rng = opts.rng || Math.random;
    const ceilingMs = opts.retryAfterCeilingMs === undefined ? RETRY_AFTER_CEILING_MS : opts.retryAfterCeilingMs;
    const giveUpMs = opts.giveUpMs === undefined ? THROTTLE_GIVE_UP_MS : opts.giveUpMs;
    const toleranceMs = opts.extendToleranceMs === undefined ? PAUSE_EXTEND_TOLERANCE_MS : opts.extendToleranceMs;
    const backoff = { baseMs: opts.backoffBaseMs, maxMs: opts.backoffMaxMs, jitter: opts.backoffJitter, rng };
    let episodes = 0;
    let attempt = 0;
    let throttledSince = null;
    let current = null;
    let handedOver = null;
    const signals = [];

    function handOver(signal, why) {
        handedOver = { signal, why, at: now() };
        current = null;
        return { action: 'hand-over', why, signal };
    }

    function pause(signal, t, waitMs, extended) {
        current = { startedAt: extended ? current.startedAt : t, pausedUntil: t + waitMs };
        return { action: 'pause', waitMs, until: t + waitMs, named: signal.retryAfterMs !== null, signal,
            episode: episodes, ...(extended ? { extended: true } : {}) };
    }

    function observe(signal) {
        if (!signal) return null;
        signals.push(signal);
        if (handedOver) return { action: 'none', signal };
        if (signal.kind === 'blocked' || signal.kind === 'challenge') return handOver(signal, signal.kind);
        const t = now();
        const named = signal.retryAfterMs !== null;
        if (named && signal.retryAfterMs > ceilingMs) return handOver(signal, 'retry-after-too-long');
        // THE SAME PAUSE: one that is still running, or -- the burst rule --
        // one that began within the burst window. A burst is one episode
        // whether or not its pause is still running: a Retry-After of 0 names a
        // pause that is over the moment it starts, and the rest of that page
        // load's refusals are still the same burst.
        if (current && (t < current.pausedUntil || t - current.startedAt <= toleranceMs)) {
            // A longer named wait is what the site wants now.
            if (named && t + signal.retryAfterMs > current.pausedUntil + toleranceMs) {
                return pause(signal, t, signal.retryAfterMs, true);
            }
            return { action: 'none', signal };
        }
        if (throttledSince === null) throttledSince = t;
        episodes++;
        if (named) return pause(signal, t, signal.retryAfterMs, false);
        if (t - throttledSince >= giveUpMs) return handOver(signal, 'persistent');
        attempt++;
        return pause(signal, t, backoffMs(attempt, backoff), false);
    }

    return {
        observe,
        /** A page of the site loaded: whatever throttling there was is over. */
        progress() {
            throttledSince = null;
            attempt = 0;
        },
        /** A person handed the browser back after a hand-over: watch again. */
        handedBack() {
            handedOver = null;
            current = null;
            throttledSince = null;
            attempt = 0;
        },
        /** The pause ended early (the recording is ending, or a person took over). */
        endPause() { current = null; },
        handedOver: () => handedOver,
        throttledSince: () => throttledSince,
        signals: () => signals.slice()
    };
}

/**
 * A ceiling the recorder can only WATCH: navigations an agent makes. Returns
 * true the first time the rolling minute goes over `perMinute`, so the
 * operator hears it once rather than on every page after.
 */
function createRateWatch(perMinute, opts = {}) {
    const now = opts.now || Date.now;
    const seen = [];
    let warned = false;
    return {
        note() {
            if (!perMinute) return false;
            const t = now();
            seen.push(t);
            while (seen.length && seen[0] <= t - 60000) seen.shift();
            if (seen.length > perMinute && !warned) { warned = true; return true; }
            return false;
        },
        count: () => seen.length
    };
}

/**
 * Where a signal came from, as the operator is told it: origin and path. The
 * query string is where sites put search terms, emails and tokens, and the
 * message only has to say WHERE the site refused.
 */
function whereOf(url) {
    try {
        const u = new URL(url);
        return u.origin + u.pathname;
    } catch (e) {
        void e;
        return String(url || '').split('?')[0];
    }
}

/**
 * A length of time as a person says it: "45 s", "5 min", "4 min 30 s",
 * "1 h 5 min". Never milliseconds -- nobody waits 52341 of anything.
 */
function describeDuration(ms) {
    if (ms === null || ms === undefined) return 'an unspecified time';
    const totalS = Math.max(0, Math.round(ms / 1000));
    if (totalS < 60) return `${totalS} s`;
    const totalMin = Math.floor(totalS / 60);
    if (totalMin < 60) {
        const s = totalS % 60;
        return s ? `${totalMin} min ${s} s` : `${totalMin} min`;
    }
    const totalMinRounded = Math.round(totalS / 60);
    const h = Math.floor(totalMinRounded / 60);
    const m = totalMinRounded % 60;
    return m ? `${h} h ${m} min` : `${h} h`;
}

/**
 * The local wall-clock time a pause ends at, as the operator reads a clock:
 * "10:42", or "Nov 18 10:42" when it is not today. `nowMs` is when it is said.
 */
function formatClock(ms, nowMs = Date.now()) {
    const d = new Date(ms);
    const hm = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    if (d.toDateString() === new Date(nowMs).toDateString()) return hm;
    return `${d.toLocaleString('en-US', { month: 'short' })} ${d.getDate()} ${hm}`;
}

const WHY = {
    challenge: 'the site put up a bot check (a "prove you\'re human" page)',
    blocked: 'the site refused the visitor',
    persistent: 'the site has gone on throttling for an hour without one page loading',
    'retry-after-too-long': 'the site asked for a wait of more than a day'
};

/** Why a hand-over happened, in a phrase. */
function handOverReason(decision) {
    return WHY[decision.why] || 'the site is throttling or blocking this session';
}

/** "HTTP 429", or the signature when the status says nothing (a 200 challenge). */
function httpOf(signal) {
    return signal.status && signal.status !== 200 ? `HTTP ${signal.status}` : signal.signature;
}

/**
 * What the operator is told. `who` is what was driving:
 *
 *   'agent'   an -AI agent the recorder can message: on a pause the browser
 *             holds the site's requests and the agent is told to wait; on a
 *             hand-over it is told a person is taking over
 *   'agent-unmessaged'  an agent the recorder cannot message: the browser
 *             still holds the site's requests, and that is all
 *   'agent-unstarted'  an -AI run whose agent had not been launched yet (the
 *             start page itself was refused or throttled)
 *   'external'  no -AI, and nobody at the terminal: automation driving the
 *             browser over CDP. The browser holds; nothing can be told
 *   'script'  this tooling's own navigation: the next step waits
 *   'you'     a person browsing by hand, who gets advice rather than a pause
 *             or a hand-over -- it is already their browser
 *
 * `opts.tool` names the speaker (default capture-har); `opts.clock(ms)` and
 * `opts.now` render the end of a pause (injected so tests are deterministic);
 * `opts.link` is the private phone link for a hand-over, `opts.noLink` why
 * there is none; `opts.finish` says how the person ends or hands back once
 * they have taken over; `opts.noHuman`, when there is nobody to hand the
 * browser to (a headless run), replaces the hand-over with what happens;
 * `opts.noNextStep`, when the script has no step left for a pause to hold,
 * replaces the pause line with what the site asked for and what happens
 * instead -- no pause is claimed; `opts.noHold` for a tool that pauses only
 * its own next step and does not hold the browser (capture-cdp.js), so the
 * line claims no more than it does.
 *
 * @returns {Array<[level, text]>} in the recorder's renderLines shape
 */
function throttleLines(decision, who = 'you', opts = {}) {
    const tool = opts.tool || 'capture-har';
    const nowMs = opts.now === undefined ? Date.now() : opts.now;
    const clock = opts.clock || ((ms) => formatClock(ms, nowMs));
    const finish = opts.finish || 'press ENTER here (or close the window) to end the recording';
    const s = decision.signal;
    const at = `${s.signature} on ${whereOf(s.url)}`;
    if (decision.action === 'pause') {
        const wait = describeDuration(decision.waitMs);
        const until = clock(decision.until === undefined ? nowMs + decision.waitMs : decision.until);
        if (who === 'you') {
            const named = s.retryAfterMs !== null ? 'the wait the site named' : 'a backoff with jitter';
            return [['warn', `${tool}: the site is throttling this session (${at}).\n` +
                `  Wait at least ${wait} (${named}, until ${until}) before your next action.`]];
        }
        if (opts.noNextStep) {
            const asked = s.retryAfterMs !== null
                ? `The site asked for a wait of ${describeDuration(s.retryAfterMs)}` : 'The site named no wait';
            return [['warn', `${tool}: the site is throttling this session (${at}).\n  ${asked}; ${opts.noNextStep}`]];
        }
        const head = decision.extended ? `${tool}: Pause extended until ${until}` : `${tool}: Paused until ${until}`;
        const why = s.retryAfterMs !== null
            ? `the site asked us to wait ${wait} (${httpOf(s)})`
            : `the site is throttling (${httpOf(s)}) and named no wait, so backing off ${wait}`;
        const holds = opts.noHold ? 'the next step waits' : {
            agent: 'the browser holds this capture\'s requests to the site, and the agent has been told to wait',
            'agent-unmessaged': 'the browser holds this capture\'s requests to the site (this agent cannot be messaged)',
            'agent-unstarted': 'the browser holds this capture\'s requests to the site, and the agent starts after it',
            external: 'the browser holds this capture\'s requests to the site, whatever drives it over CDP'
        }[who] || 'the browser holds this capture\'s requests to the site, and the next step waits';
        return [['warn', `${head} -- ${why}\n` +
            `  ${at}. Nothing reaches the site until then:\n  ${holds}. It carries on by itself when the time is up.`]];
    }
    if (decision.action !== 'hand-over') return [];
    const why = handOverReason(decision);
    const wait = s.retryAfterMs !== null ? ` It asked for ${describeDuration(s.retryAfterMs)} before the next request.` : '';
    const evade = 'Nothing here retries it, solves it or works around it.';
    if (who === 'you') {
        return [['warn', `${tool}: ${why} (${at}).${wait}\n  ${evade} Answer it yourself in the browser if you choose\n` +
            '  to, or end the recording; what was recorded so far is kept.']];
    }
    const told = {
        agent: 'The agent has been told to wait',
        'agent-unmessaged': 'This agent cannot be messaged; its instructions tell it to stop at a check like this',
        'agent-unstarted': 'The agent has not been started, and will not be unless you hand back',
        external: 'Whatever drives this browser over CDP cannot be told; it should stop at a check like this'
    }[who] || 'Automated browsing has stopped';
    if (opts.noHuman) {
        return [['warn', `${tool}: ${why} (${at}).${wait}\n  ${told}. ${evade}\n  ${opts.noHuman} What was recorded is kept.`]];
    }
    const phone = opts.link ? ` Open on your phone: ${opts.link}` : '';
    const noLink = !opts.link && opts.noLink ? `\n  ${opts.noLink}` : '';
    return [['warn', `${tool}: Handed over to you: ${why}.${phone}\n` +
        `  ${at}.${wait}${noLink}\n` +
        `  ${told}. ${evade}\n` +
        '  THE BROWSER IS YOURS: it is still open, and still recording. Take over there' +
        (opts.link ? ' or on\n  your phone' : '') + ' -- answer the check yourself if you choose to -- then\n' +
        `  ${finish}. What was recorded is kept.`]];
}

/**
 * What the operator is told when a pause or a hand-over ends and the capture
 * carries on. `record` is the pause as capture-pause.js records it.
 */
function resumeLines(record, opts = {}) {
    const tool = opts.tool || 'capture-har';
    const after = describeDuration(record.pausedMs);
    const by = {
        phone: ' -- you handed the browser back on your phone',
        terminal: ' -- you handed the browser back at the terminal'
    }[record.resumedBy] || '';
    const next = {
        agent: '; the agent has been told to carry on',
        'agent-unmessaged': '; the browser no longer holds the site\'s requests',
        external: '; the browser no longer holds the site\'s requests',
        'agent-unstarted': '; the agent starts now'
    }[record.driver] || '';
    return [['warn', `${tool}: Resumed after ${after}${by}${next}.`]];
}

module.exports = {
    DEFAULT_PACE,
    PACE_OFF,
    MAX_PACE_WAIT_MS,
    RETRY_AFTER_CEILING_MS,
    BACKOFF_BASE_MS,
    BACKOFF_MAX_MS,
    BACKOFF_JITTER,
    THROTTLE_GIVE_UP_MS,
    PAUSE_EXTEND_TOLERANCE_MS,
    CHALLENGE_BODY_MAX,
    INTERSTITIAL_BODY_MAX,
    parsePace,
    describePace,
    randomBetween,
    createPacer,
    headerValue,
    parseRetryAfter,
    backoffMs,
    classifyResponse,
    isPageLoad,
    createThrottleMonitor,
    createRateWatch,
    throttleLines,
    resumeLines,
    handOverReason,
    describeDuration,
    formatClock,
    whereOf
};
