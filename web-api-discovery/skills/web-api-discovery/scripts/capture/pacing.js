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
 *   challenge page is a signal. A throttle PAUSES (Retry-After when the site
 *   names one, else exponential backoff with jitter); a throttle that persists,
 *   a block, or a challenge HANDS OVER: whatever is driving stops, and the
 *   human -- whose browser it is, and which is on screen -- decides.
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
        pausedUntil: () => pausedUntil,
        /** Every wait taken, for tests and the verbose log. */
        waits: () => waits.slice()
    };
}

// ---------------------------------------------------------------------------
// Throttling
// ---------------------------------------------------------------------------

/** The longest Retry-After waited out inline. Longer, and a person decides. */
const DEFAULT_RETRY_AFTER_CAP_MS = 60000;

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
 * Exponential backoff with "equal" jitter: half the step, plus a random part of
 * the other half. Never zero, so a backoff is always a real wait, and never in
 * lockstep with another client that was throttled at the same moment.
 */
function backoffMs(attempt, opts = {}) {
    const baseMs = opts.baseMs === undefined ? 2000 : opts.baseMs;
    const maxMs = opts.maxMs === undefined ? DEFAULT_RETRY_AFTER_CAP_MS : opts.maxMs;
    const rng = opts.rng || Math.random;
    const step = Math.min(maxMs, baseMs * 2 ** Math.max(0, attempt - 1));
    return Math.round(step / 2 + rng() * (step / 2));
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
    { name: 'Cloudflare challenge', test: marker(
        /window\._cf_chl_opt\b|[?&]__cf_chl_(?:f_tk|jschl_tk__)=|\/cdn-cgi\/challenge-platform\/(?:h\/[a-z]\/)?orchestrate\//i) },
    { name: 'Cloudflare block', test: marker(
        /<title>\s*attention required! \| cloudflare\s*<\/title>|\bid\s*=\s*["']?cf-error-details\b/i) },
    // The block page hands its parameters to the CAPTCHA in a `dd` object.
    { name: 'DataDome CAPTCHA', test: marker(/\bvar\s+dd\s*=\s*\{[^}]*captcha-delivery\.com/i) },
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
    // continue", a checkbox labelled "I am human".
    { name: 'Amazon "Check the box to continue" checkpoint', deep: true, test: (ctx) => {
        const page = ctx.deep();
        const form = /<form\b[^>]*\baction\s*=\s*["']?(?:https?:\/\/[^/"'\s>]+)?\/checkpoint\/validate\b/i.test(page);
        return (form || /^\/checkpoint\//i.test(ctx.path)) && /check the box to continue|\bi am human\b/i.test(page);
    } }
];

// What a bot-protection edge answers a refused XHR or fetch with, in place of
// its HTML page. Judged on refusals only, whatever the content type.
const BLOCK_RESPONSES = [
    // {"url":"https://geo.captcha-delivery.com/captcha/?initialCid=..."}
    { name: 'DataDome block', test: marker(/"url"\s*:\s*"https?:\/\/geo\.captcha-delivery\.com\/(?:captcha|interstitial)\//i) },
    // {"appId":"PX...","blockScript":"/.../captcha.js?...", ...}
    { name: 'PerimeterX block', test: (ctx) => /"appId"\s*:\s*"PX\w+"/i.test(ctx.text) && /"blockScript"\s*:/i.test(ctx.text) }
];

// Headers only a bot-protection vendor's own refusal carries. Unlike a CDN's
// cf-ray or x-amz-cf-id, which are on every response of a site behind it, one
// of these on a 403 is the vendor refusing -- whatever the content type.
const BOT_VENDOR_HEADERS = ['x-datadome'];

// WORDING an ordinary page can use too: a sign-up form's "verify you are
// human", a help page's "are you a robot?", a title naming a CAPTCHA game, an
// app shell titled "Just a moment...". It is never a challenge on its own. It
// counts only on a refusal -- an HTML 403 or 503 -- where the site has already
// said no and the wording says why.
const REFUSAL_WORDING = [
    { name: 'a "Just a moment..." page', re: /<title>\s*just a moment\.\.\.\s*<\/title>/i },
    { name: 'a "Press & Hold" page', re: /press (?:&amp;|&) hold/i },
    { name: 'an "unusual traffic" page', re: /unusual traffic from your (?:computer|network)|are you a robot\?/i },
    { name: 'a "verify you are human" page', re: /verify (?:that )?you are (?:a )?human/i },
    { name: 'a CAPTCHA page', re: /<title>[^<]*captcha[^<]*<\/title>/i }
];

// Headers only a CDN or bot-protection edge sets. A 403 carrying one, in HTML,
// is the edge refusing the visitor -- not the application refusing a request.
const CDN_HEADERS = ['cf-ray', 'x-iinfo', 'x-cdn', 'x-datadome', 'x-akamai-transformed', 'akamai-grn',
    'x-amz-cf-id', 'x-sucuri-id', 'x-px'];
const CDN_SERVERS = /cloudflare|akamai|incapsula|imperva|datadome|sucuri|cloudfront|ddos-guard/i;

function bodyText(body, max = CHALLENGE_BODY_MAX) {
    if (body === undefined || body === null) return '';
    if (Buffer.isBuffer(body)) return body.subarray(0, max).toString('utf8');
    return String(body).slice(0, max);
}

/**
 * What a signature is tested against: the head of the body, the URL's path,
 * and more of the body for a deep signature. Each is decoded only when a
 * signature asks for it -- most responses never are.
 */
function signatureContext(r) {
    let text = null;
    let deep = null;
    let path = '';
    try { path = new URL(r.url).pathname; } catch (e) { void e; }
    return {
        get text() { return text === null ? (text = bodyText(r.body)) : text; },
        path,
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
        const vendor = status === 403 && BOT_VENDOR_HEADERS.find((h) => headerValue(headers, h) !== undefined);
        if (vendor) return signal('blocked', `403 from bot protection (${vendor})`);
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
 * Decide what each signal means for whatever is driving.
 *
 *   { action: 'pause', waitMs }   a throttle: wait out Retry-After (or a
 *                                  backoff with jitter), then carry on
 *   { action: 'hand-over', why }  stop driving; the human decides
 *   { action: 'none' }            part of a throttle already being handled
 *
 * WHEN A THROTTLE BECOMES A HAND-OVER:
 *   - it persists: `maxEpisodes` throttle episodes inside `windowMs`;
 *   - the driver kept going through a pause: another throttle after the
 *     opening burst of an episode but before its pause ended (an agent the
 *     recorder cannot slow down looks exactly like this);
 *   - the site asks for longer than `retryAfterCapMs`.
 * A block or a challenge hands over at once. Nothing here ever retries them.
 *
 * Several requests on one page load can be throttled together. Signals within
 * `burstMs` of an episode's first are that same episode, not persistence.
 */
function createThrottleMonitor(opts = {}) {
    const now = opts.now || Date.now;
    const rng = opts.rng || Math.random;
    const maxEpisodes = opts.maxEpisodes || 3;
    const windowMs = opts.windowMs || 10 * 60000;
    const burstMs = opts.burstMs === undefined ? 2000 : opts.burstMs;
    const capMs = opts.retryAfterCapMs === undefined ? DEFAULT_RETRY_AFTER_CAP_MS : opts.retryAfterCapMs;
    const backoff = { baseMs: opts.backoffBaseMs, maxMs: capMs, rng };
    const episodes = [];
    let current = null;
    let handedOver = null;
    const signals = [];

    function handOver(signal, why) {
        handedOver = { signal, why, at: now() };
        return { action: 'hand-over', why, signal };
    }

    function observe(signal) {
        if (!signal) return null;
        signals.push(signal);
        if (handedOver) return { action: 'none', signal };
        if (signal.kind === 'blocked' || signal.kind === 'challenge') return handOver(signal, signal.kind);
        const t = now();
        // THE BURST FIRST, the pause second: a burst is one episode whether or
        // not its pause is still running. A Retry-After of 0 names a pause that
        // is over the moment it starts, and the rest of that page load's
        // refusals are still the same burst.
        if (current && t - current.startedAt <= burstMs) {
            if (signal.retryAfterMs !== null && signal.retryAfterMs > capMs) {
                return handOver(signal, 'retry-after-too-long');
            }
            // The same burst naming a LONGER wait than the episode's: the
            // longer one is what the site wants, and it is news. One that
            // only differs by the burst's own spread is not.
            if (signal.retryAfterMs !== null && t + signal.retryAfterMs > current.pausedUntil + burstMs) {
                current.pausedUntil = t + signal.retryAfterMs;
                return { action: 'pause', waitMs: signal.retryAfterMs, signal, episode: episodes.length };
            }
            return { action: 'none', signal };
        }
        if (current && t < current.pausedUntil) return handOver(signal, 'ignored-pause');
        while (episodes.length && episodes[0] <= t - windowMs) episodes.shift();
        episodes.push(t);
        if (episodes.length >= maxEpisodes) return handOver(signal, 'persistent');
        if (signal.retryAfterMs !== null && signal.retryAfterMs > capMs) {
            return handOver(signal, 'retry-after-too-long');
        }
        const waitMs = signal.retryAfterMs !== null ? signal.retryAfterMs : backoffMs(episodes.length, backoff);
        current = { startedAt: t, pausedUntil: t + waitMs };
        return { action: 'pause', waitMs, signal, episode: episodes.length };
    }

    return {
        observe,
        handedOver: () => handedOver,
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

function describeWait(ms) {
    if (ms === null || ms === undefined) return 'an unspecified time';
    return ms >= 1000 ? `${seconds(Math.round(ms / 100) * 100)} s` : `${ms} ms`;
}

const WHY = {
    challenge: 'the site put up a bot check',
    blocked: 'the site refused the visitor',
    persistent: 'the site kept throttling after repeated pauses',
    'ignored-pause': 'the site went on throttling while the drive should have been paused',
    'retry-after-too-long': 'the site asked for a longer wait than is waited out automatically'
};

/**
 * What the operator is told. `who` is what was driving:
 *
 *   'agent'   an -AI agent: the recorder cannot slow it down, so a pause is a
 *             warning, and a hand-over means the agent has been stopped
 *   'agent-unstarted'  an -AI run whose agent had not been launched yet (the
 *             start page itself refused): it never will be
 *   'script'  this tooling's own navigation: a pause is taken, and a
 *             hand-over means it stopped
 *   'you'     a person browsing by hand, who gets advice rather than a
 *             hand-over -- it is already their browser
 *
 * `opts.tool` names the speaker (default capture-har); `opts.finish` says how
 * the person ends the recording once they have taken over; `opts.noHuman`,
 * when there is nobody to hand the browser to (a headless run), replaces the
 * hand-over with what happens instead.
 *
 * @returns {Array<[level, text]>} in the recorder's renderLines shape
 */
function throttleLines(decision, who = 'you', opts = {}) {
    const tool = opts.tool || 'capture-har';
    const finish = opts.finish || 'press ENTER here (or close the window) to finish';
    const s = decision.signal;
    const at = `${s.signature} on ${whereOf(s.url)}`;
    if (decision.action === 'pause') {
        const wait = describeWait(decision.waitMs);
        const named = s.retryAfterMs !== null ? 'the wait the site named' : 'a backoff with jitter';
        const head = `${tool}: the site is throttling this session (${at}).\n`;
        if (who === 'you') return [['warn', `${head}  Wait at least ${wait} (${named}) before your next action.`]];
        if (who === 'agent') {
            return [['warn', `${head}  It asked for ${wait} (${named}); the agent is told to stop and hand back when a\n` +
                '  page says so, and the recorder cannot slow it down. If the site throttles again\n' +
                '  before then, or keeps throttling, the agent is stopped and the browser is yours.']];
        }
        return [['warn', `${head}  Pausing ${wait} (${named}) before the next step.`]];
    }
    if (decision.action !== 'hand-over') return [];
    const why = WHY[decision.why] || 'the site is throttling or blocking this session';
    const wait = s.retryAfterMs !== null ? ` It asked for ${describeWait(s.retryAfterMs)} before the next request.` : '';
    const head = `${tool}: ${why} (${at}).${wait}`;
    const evade = 'Nothing here retries it, solves it or works around it.';
    if (who === 'you') {
        return [['warn', `${head}\n  ${evade} Answer it yourself in the browser if you choose\n` +
            '  to, or end the recording; what was recorded so far is kept.']];
    }
    const stopped = {
        agent: 'The agent has been stopped',
        'agent-unstarted': 'The agent was not started, and will not be'
    }[who] || 'Automated browsing has stopped';
    if (opts.noHuman) {
        return [['warn', `${head}\n  ${stopped}. ${evade}\n  ${opts.noHuman} What was recorded is kept.`]];
    }
    return [['warn', `${head}\n` +
        `  ${stopped}. ${evade}\n` +
        '  THE BROWSER IS YOURS: it is still open, and still recording. Take over there --\n' +
        '  answer the check yourself if you choose to, or wait and carry on by hand -- then\n' +
        `  ${finish}. What was recorded is kept.`]];
}

module.exports = {
    DEFAULT_PACE,
    PACE_OFF,
    MAX_PACE_WAIT_MS,
    DEFAULT_RETRY_AFTER_CAP_MS,
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
    createThrottleMonitor,
    createRateWatch,
    throttleLines,
    whereOf
};
