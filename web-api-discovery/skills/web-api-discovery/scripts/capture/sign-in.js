#!/usr/bin/env node
/**
 * sign-in.js -- the recorder signs in, so nobody has to type into the capture
 * browser and no agent ever does (#115).
 *
 * WHERE THE CREDENTIALS COME FROM, in order, resolved the first time a sign-in
 * form is actually seen (a capture that never meets one never touches a vault):
 *
 *   1. Bitwarden (`bw`), unlocked or unlockable -- the item whose saved URIs
 *      match the site. Several matches are a numbered pick; none falls through.
 *   2. A masked prompt in the operator's terminal.
 *   3. Nothing, when there is no terminal to prompt in. The capture then hands
 *      back exactly as it did before this file existed.
 *
 * THE VALUES NEVER LEAVE THIS PROCESS EXCEPT INTO THE PAGE AND THE SCRUB.
 * Nothing here prints a username, password, code or vault session key; the
 * master password and session key reach `bw` through the CHILD's environment
 * only (never argv, which other processes can read, and never this process's
 * own environment, which every later child would inherit); and the values that
 * were filled are handed to the scrub over stdin (see `secrets()`), so they are
 * removed from the HAR under whatever field name the site happened to use.
 *
 * WHAT IS NOT HERE, deliberately: CAPTCHAs, passkeys, push approvals and codes
 * sent by SMS or email. Those are the human's, in the browser, and the
 * recording simply carries on while they do it.
 */

'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

// Hosts and vault ids are the only operator-derived strings that ever reach a
// `bw` command line. Both are validated rather than quoted: a value that does
// not look like a hostname or an id is not one, and refusing it is simpler and
// safer than escaping it for a shell.
const HOST_RE = /^[a-z0-9.-]+$/i;
const ID_RE = /^[0-9a-f-]+$/i;

// The child-only variable `bw unlock --passwordenv` reads the master password
// from. Named for this tool so it can never be mistaken for one the operator set.
const MASTER_ENV = 'BW_HARCAPTURE_MASTER';

// Bitwarden's item type for a login.
const BW_LOGIN_TYPE = 1;

const PASSWORD_SELECTOR = 'input[type=password]:visible';
const USERNAME_SELECTOR = [
    'input[autocomplete~=username]',
    'input[autocomplete~=email]',
    'input[type=email]',
    'input[name*=user i]',
    'input[name*=email i]',
    'input[name=login i]',
    'input[id*=user i]',
    'input[id*=email i]'
].map((s) => `${s}:visible`).join(', ');
// Identifier-first pages (enter your email, then Next). The looser name/id
// matches above are fine beside a password field, where the context is already
// a sign-in form; alone, they match a newsletter box, a contact form, an invite
// field -- and filling one would hand the operator's account name to a
// marketing list. So alone, only the attribute that SAYS "this is an account
// name" counts, and a bare email field counts only on a page whose address says
// it is a sign-in.
const IDENTIFIER_SELECTOR = 'input[autocomplete~=username]:visible';
const EMAIL_SELECTOR = 'input[type=email]:visible';
// Whole PATH SEGMENTS, so `/blog-insights`, `/authors` and `/accounting` are
// not sign-ins. `account` alone is left out: `/account/newsletter` holds an
// email box that is not one.
const SIGN_IN_PATH_RE = /(^|\/)(log-?in|sign-?in|sso|oauth2?|authorize|auth|identifier)(\/|$|[-_.])/i;
const OTP_SELECTOR = [
    'input[autocomplete=one-time-code]',
    'input[name*=otp i]',
    'input[name*=totp i]',
    'input[id*=otp i]',
    'input[id*=totp i]'
].map((s) => `${s}:visible`).join(', ');

function hostOf(uri) {
    try {
        return new URL(uri).hostname.toLowerCase();
    } catch (_) {
        // Bitwarden URIs are often stored without a scheme ("example.com").
        try { return new URL(`https://${uri}`).hostname.toLowerCase(); } catch (__) { return null; }
    }
}

/**
 * Is `pageHost` somewhere `credentialHost`'s credential belongs?
 *
 * The same host, or a subdomain of it: an item saved for example.com fills on
 * login.example.com. Never the other way round -- an item saved for
 * login.example.com says nothing about every other example.com host -- and
 * never a host that merely ends in the same characters (badexample.com).
 */
function hostMatches(credentialHost, pageHost) {
    if (!credentialHost || !pageHost) return false;
    const c = credentialHost.toLowerCase();
    const p = pageHost.toLowerCase();
    return p === c || p.endsWith(`.${c}`);
}

function itemHosts(item) {
    const uris = (item && item.login && item.login.uris) || [];
    return uris.map((u) => hostOf(u && u.uri)).filter(Boolean);
}

/** How to get `bw`, said once when it is missing. */
function installHint(platform = process.platform) {
    const install = platform === 'win32'
        ? 'winget install Bitwarden.CLI'
        : platform === 'darwin' ? 'brew install bitwarden-cli' : 'npm install -g @bitwarden/cli';
    return [
        'capture-har: the Bitwarden CLI (bw) is not installed, so the sign-in will be prompted for.',
        `  To sign in from your vault next time:  ${install}`,
        '  then:  bw login'
    ];
}

// Synchronous, inside the process that is recording -- so every call is
// BOUNDED. A `bw` that hangs (a sync over a dead network, a prompt it cannot
// show) would otherwise freeze the recorder with it.
const BW_TIMEOUT_MS = 30000;

/**
 * The `bw` this run will start, as an ABSOLUTE path, or null.
 *
 * Found by walking PATH ourselves, and only its absolute entries, because the
 * platform's own search does not stop there: on Windows both CreateProcess
 * and cmd.exe look in the CURRENT DIRECTORY first, and the recorder's current
 * directory is the operator's project -- which can hold anything somebody
 * downloaded. A `bw.cmd` planted there would be handed the master password
 * and the vault session key. A relative or empty PATH entry is the same hole
 * by another name, so it is skipped too.
 */
function findBw(env = process.env, platform = process.platform) {
    const pathVar = env.PATH || env.Path || '';
    const names = platform === 'win32' ? ['bw.exe', 'bw.cmd'] : ['bw'];
    const sep = platform === 'win32' ? ';' : ':';
    for (const dir of pathVar.split(sep)) {
        const clean = dir.replace(/^"|"$/g, '');
        if (!clean || !path.isAbsolute(clean)) continue;
        for (const name of names) {
            const candidate = path.join(clean, name);
            try {
                if (fs.statSync(candidate).isFile()) return candidate;
            } catch (_) { /* not here */ }
        }
    }
    return null;
}

function defaultRun(args, opts = {}) {
    const exe = findBw(opts.env || process.env);
    if (!exe) {
        const error = new Error('bw not found on PATH');
        error.code = 'ENOENT';
        return { status: null, stdout: '', stderr: '', error };
    }
    const options = {
        encoding: 'utf8',
        env: opts.env,
        // Not the operator's project directory: nothing `bw` loads by
        // relative path should come from there.
        cwd: os.homedir(),
        windowsHide: true,
        timeout: BW_TIMEOUT_MS,
        // Nothing of ours to read, and nothing of the operator's to take: a
        // `bw` that wanted to ask something gets EOF instead of the terminal.
        stdio: ['ignore', 'pipe', 'pipe']
    };
    // npm's bw is a .cmd shim, which Windows starts only through a shell --
    // as one command string, since an args array with `shell` is deprecated.
    // Every argument is a fixed word, a validated host or a validated id
    // (HOST_RE / ID_RE), and the path is quoted, so there is nothing else to
    // escape.
    const r = /\.cmd$/i.test(exe)
        ? spawnSync([`"${exe}"`, ...args].join(' '), Object.assign({}, options, { shell: true }))
        : spawnSync(exe, args, options);
    return { status: r.status, stdout: r.stdout || '', stderr: r.stderr || '', error: r.error };
}

// A vault item's name is the operator's label -- or a collaborator's, in a
// shared vault -- and it is printed to a terminal. Control characters would
// let it rewrite that terminal.
function printable(text) {
    // eslint-disable-next-line no-control-regex
    return String(text || '').replace(/[\u0000-\u001f\u007f-\u009f]/g, '?');
}

/**
 * The vault, through the `bw` CLI. `run` is injectable so the tests can stand
 * in for Bitwarden without one being installed or a real vault being touched.
 */
function createBitwarden(opts = {}) {
    const run = opts.run || defaultRun;
    const baseEnv = opts.env || process.env;
    let session = baseEnv.BW_SESSION || null;
    const env = (extra) => Object.assign({}, baseEnv, session ? { BW_SESSION: session } : {}, extra || {});

    function json(args) {
        const r = run(args, { env: env() });
        if (r.error || r.status !== 0) return null;
        try { return JSON.parse(r.stdout); } catch (_) { return null; }
    }

    return {
        available() {
            const r = run(['--version'], { env: env() });
            return !r.error && r.status === 0;
        },
        /** 'unauthenticated' | 'locked' | 'unlocked' | null */
        status() {
            const s = json(['status']);
            return s && typeof s.status === 'string' ? s.status : null;
        },
        unlock(masterPassword) {
            const r = run(['unlock', '--raw', '--passwordenv', MASTER_ENV],
                { env: env({ [MASTER_ENV]: masterPassword }) });
            const key = (r.stdout || '').trim();
            if (r.error || r.status !== 0 || !key) return false;
            session = key;
            return true;
        },
        itemsForHost(host) {
            if (!HOST_RE.test(host || '')) return [];
            const items = json(['list', 'items', '--url', `https://${host}`]);
            return (Array.isArray(items) ? items : [])
                .filter((i) => i && i.type === BW_LOGIN_TYPE && i.login && i.login.password)
                .filter((i) => itemHosts(i).some((h) => hostMatches(h, host)));
        },
        totp(id) {
            if (!ID_RE.test(id || '')) return null;
            const r = run(['get', 'totp', id], { env: env() });
            const code = (r.stdout || '').trim();
            return r.error || r.status !== 0 || !code ? null : code;
        }
    };
}

/**
 * Where this run's credentials come from, decided once.
 *
 * `io` is the terminal: `say(lines)`, `ask(question)`, `askSecret(question)`
 * and `isTty`. Nothing printed through it ever carries a value -- the item NAME
 * is printed when there is a choice to make, because that is the operator's
 * own label and the only way to tell two items apart.
 *
 * @returns {Promise<{source: 'bitwarden'|'prompt', username: string, password: string,
 *   hosts: string[], totp: (() => Promise<string|null>)|null} | {source: 'none', reason: string}>}
 */
async function resolveCredentials(host, bw, io) {
    // NOBODY AT THE TERMINAL, NO SIGN-IN -- not even from an unlocked vault.
    // A run with no terminal is one an agent or a scheduler started, and a
    // sign-in with someone's real credentials is something a person starts.
    // The unattended answer is a fast, clear "sign-in needed" (see the
    // recorder's `sign-in-needed` ending), and a person signs in once so the
    // saved session carries the unattended runs that follow.
    if (!io.isTty) {
        return { source: 'none', code: 'no-terminal', reason: 'nobody is at the terminal to start a sign-in' };
    }
    // AND SOMEBODY ANSWERING. A terminal with no reader on it (`--no-wait`,
    // which is for non-interactive use) may have a person nearby, so it does
    // not end the run -- but it does not get a vault read either: whatever
    // wrapped the recorder in a pseudo-terminal is not that person.
    if (io.canPrompt === false) {
        return { source: 'none', code: 'no-prompt', reason: 'this run is not reading the terminal (--no-wait)' };
    }
    if (bw && bw.available()) {
        const fromVault = await fromBitwarden(host, bw, io);
        if (fromVault) return fromVault;
    } else {
        io.say(installHint());
    }
    return fromPrompt(host, io);
}

async function fromBitwarden(host, bw, io) {
    let status = bw.status();
    if (status === 'unauthenticated') {
        io.say(['capture-har: bw is installed but not logged in (run: bw login). Prompting instead.']);
        return null;
    }
    if (status === 'locked') {
        const master = await io.askSecret('Bitwarden master password (to unlock the vault): ');
        if (!master || !bw.unlock(master)) {
            io.say(['capture-har: the vault did not unlock. Prompting instead.']);
            return null;
        }
        status = 'unlocked';
    }
    if (status !== 'unlocked') return null;

    const items = bw.itemsForHost(host);
    if (items.length === 0) {
        io.say([`capture-har: no Bitwarden login matches ${host}. Prompting instead.`]);
        return null;
    }
    let item = items[0];
    if (items.length > 1) {
        io.say([`capture-har: ${items.length} Bitwarden logins match ${host}:`,
            ...items.map((it, i) => `  ${i + 1}. ${printable(it.name)}`)]);
        const answer = await io.ask(`Which one? [1-${items.length}] `);
        const n = Number.parseInt((answer || '').trim(), 10);
        if (!(n >= 1 && n <= items.length)) {
            io.say(['capture-har: no item chosen. Prompting instead.']);
            return null;
        }
        item = items[n - 1];
    }
    io.say([`capture-har: signing in to ${host} from Bitwarden.`]);
    return {
        source: 'bitwarden',
        username: item.login.username || '',
        password: item.login.password,
        hosts: itemHosts(item),
        totp: item.login.totp ? async () => bw.totp(item.id) : null
    };
}

async function fromPrompt(host, io) {
    io.say([`capture-har: sign-in needed for ${host}. Enter the credentials to fill in`,
        '  (they are not shown, saved, or logged; leave the password blank to sign in in the browser yourself).']);
    const username = await io.ask('Username: ');
    const password = await io.askSecret('Password: ');
    // null is the terminal declining (it cannot hide what is typed); '' is
    // the operator declining. Both leave the sign-in to the browser.
    if (password === null) return { source: 'none', reason: 'this terminal cannot hide a typed password' };
    if (!password) return { source: 'none', reason: 'the operator chose to sign in in the browser' };
    return {
        source: 'prompt',
        username: username || '',
        password,
        hosts: [host],
        totp: async () => (await io.askSecret('One-time code (blank to enter it in the browser): ')) || null
    };
}

async function firstVisible(page, selector) {
    const loc = page.locator(selector).first();
    try {
        return (await loc.count()) > 0 ? loc : null;
    } catch (_) {
        // A page mid-navigation throws "execution context was destroyed".
        // That is not a form; the next load event will look again.
        return null;
    }
}

// -1 when the page could not be asked (mid-navigation, a destroyed context):
// UNKNOWN, which is not the same answer as "no form here".
async function visibleCount(page, selector) {
    try { return await page.locator(selector).count(); } catch (_) { return -1; }
}

// How long a page that shows no form yet is given to render one. A login wall
// drawn by script -- most identity providers' -- is an empty shell at
// `domcontentloaded`.
const RENDER_GRACE_MS = 1500;

function pathOf(url) {
    try { return new URL(url).pathname; } catch (_) { return ''; }
}

/** What kind of sign-in step `page` is showing: a step, null, or 'unknown'. */
async function detectStep(page) {
    // EXACTLY ONE password field. Two or more is a sign-up or a
    // change-password form, and filling the first and pressing Enter there
    // would be a guess about somebody's account.
    const passwords = await visibleCount(page, PASSWORD_SELECTOR);
    if (passwords < 0) return 'unknown';
    if (passwords > 1) return null;
    if (passwords === 1) return 'password';
    const otp = await visibleCount(page, OTP_SELECTOR);
    if (otp < 0) return 'unknown';
    if (otp > 0) return 'otp';
    const id = await visibleCount(page, IDENTIFIER_SELECTOR);
    if (id < 0) return 'unknown';
    if (id > 0) return 'identifier';
    if (SIGN_IN_PATH_RE.test(pathOf(page.url()))) {
        const email = await visibleCount(page, EMAIL_SELECTOR);
        if (email < 0) return 'unknown';
        if (email > 0) return 'identifier';
    }
    return null;
}

async function identifierField(page) {
    return (await firstVisible(page, IDENTIFIER_SELECTOR))
        || (SIGN_IN_PATH_RE.test(pathOf(page.url())) ? await firstVisible(page, EMAIL_SELECTOR) : null);
}

/**
 * Fill the step `page` is showing and submit it. Returns the step filled, or
 * null when nothing was filled (an OTP with no code, or `stillHere()` said no).
 *
 * `stillHere()` is asked IMMEDIATELY before every fill. Between seeing a form
 * and filling it there can be a vault unlock or a prompt the operator takes a
 * minute over, and a page can navigate anywhere in that time; a locator is
 * re-evaluated against whatever document is current, so without this check a
 * credential could be typed into a host nobody vetted.
 *
 * `pace` (pacing.js's pacer, optional) puts a human-scale wait before every
 * fill and keypress -- one step at a time, the way a person signs in. The wait
 * comes BEFORE `stillHere()`, never between it and the step it vets: a page
 * can move during a wait, and the check has to be about the page the step
 * then lands on. No pacer, no wait -- for the unit tests and older callers.
 */
async function fillStep(page, creds, step, stillHere = () => true, pace = null) {
    const ready = async () => {
        if (pace) await pace.beforeAction();
        return stillHere();
    };
    if (step === 'password') {
        const user = await firstVisible(page, USERNAME_SELECTOR);
        if (user && creds.username && !(await user.inputValue().catch(() => ''))) {
            if (!(await ready())) return null;
            await user.fill(creds.username);
        }
        const pw = await firstVisible(page, PASSWORD_SELECTOR);
        if (!pw || !(await ready())) return null;
        await pw.fill(creds.password);
        // A wait between typing and submitting is one more chance for the page
        // to move, so the submit is vetted like the fill was.
        if (pace && !(await ready())) return null;
        await pw.press('Enter');
        return step;
    }
    if (step === 'otp') {
        if (!creds.totp) return null;
        const code = await creds.totp();
        if (!code) return null;
        const otp = await firstVisible(page, OTP_SELECTOR);
        if (!otp || !(await ready())) return null;
        await otp.fill(code);
        if (pace && !(await ready())) return null;
        await otp.press('Enter');
        return step;
    }
    if (step === 'identifier') {
        if (!creds.username) return null;
        const user = await identifierField(page);
        if (!user || !(await ready())) return null;
        await user.fill(creds.username);
        if (pace && !(await ready())) return null;
        await user.press('Enter');
        return step;
    }
    return null;
}

const LOOPBACK = new Set(['localhost', '127.0.0.1', '[::1]', '::1']);

/**
 * Watch `context` for sign-in forms on the capture's own site and fill them.
 *
 * `opts`:
 *   uri            the capture's start URI; its host is where a prompted
 *                  credential belongs
 *   resolve(host)  -> resolveCredentials(...), called at most once
 *   say(lines)     operator output (never given a value)
 *   inScope(page)  optional: whether this page belongs to the capture
 *   pace           optional: pacing.js's pacer; each fill and keypress waits
 *                  on it (see fillStep). Absent, nothing waits.
 *   onSignedIn({cookiesBefore})  optional: called once, after a filled
 *                  password step has gone; `cookiesBefore` is the set of
 *                  cookie keys present before the fill, for saveSession
 *   onUnattended(host)  optional: the capture's FIRST page is a sign-in, and
 *                  nobody is at the terminal; the recorder ends the run as
 *                  `sign-in-needed`. Met later (a session expiring mid-run),
 *                  it is a notice instead: what was recorded is kept.
 *
 * Returns `{ check(page), secrets(), hosts(), result() }`. `check` is exposed
 * so the caller can look at the first page before anything else happens, and
 * so tests can drive it without waiting on events.
 */
function createSignInWatcher(context, opts) {
    const startHost = hostOf(opts.uri);
    let credsPromise = null;
    let creds = null;
    let gaveUp = false;
    let passwordFilled = false;
    let otpDeclined = false;
    let signedIn = false;
    let noTerminal = false;
    // Whether the capture has shown anything but a sign-in. Until it has, a
    // sign-in with nobody present means the run can record nothing useful.
    let sawOtherPage = false;
    const attempted = new Set();
    const filledHosts = new Set();
    let cookiesBefore = null;
    let busy = Promise.resolve();

    function allowedHost(host) {
        const hosts = creds ? creds.hosts : [startHost];
        return hosts.some((h) => hostMatches(h, host)) || hostMatches(startHost, host);
    }

    // Plain http only on this machine: a password typed into an http page
    // crosses the network in the clear, and `hostOf` alone cannot tell
    // https://example.com from http://example.com.
    function allowedUrl(url) {
        let u;
        try { u = new URL(url); } catch (_) { return false; }
        if (u.protocol !== 'https:' && !(u.protocol === 'http:' && LOOPBACK.has(u.hostname))) return false;
        return allowedHost(u.hostname.toLowerCase());
    }

    async function checkNow(page) {
        if (gaveUp || signedIn || (page.isClosed && page.isClosed())) return;
        if (opts.inScope && !opts.inScope(page)) return;
        if (!hostOf(page.url()) || page.url() === 'about:blank') return;
        let step = await detectStep(page);
        // A page with no form YET is given its load and a moment to draw one
        // -- but only where the answer decides something: whether the run has
        // seen anything but a sign-in, and whether a filled sign-in is done.
        if (!step && (!sawOtherPage || (passwordFilled && !signedIn))) {
            // Bounded tightly: checks are serialized, and a page that never
            // reaches `load` (a long poll, a stream) would hold every other
            // tab's check behind it.
            await page.waitForLoadState('load', { timeout: 3000 }).catch(() => {});
            await page.waitForTimeout(RENDER_GRACE_MS).catch(() => {});
            step = await detectStep(page);
        }
        // Unknown is not "no form": the next load event looks again.
        if (step === 'unknown') return;
        const host = hostOf(page.url());
        if (!host) return;
        if (!step || (step === 'otp' && otpDeclined)) {
            if (!step) sawOtherPage = true;
            // POSITIVE evidence only: a password step this run filled, and
            // now a page with no sign-in step on it. A redirect interstitial
            // or a 2FA page is not "signed in", and saving then would replace
            // a good session with an anonymous one.
            if (!step && passwordFilled && !signedIn) {
                signedIn = true;
                if (opts.onSignedIn) await opts.onSignedIn({ cookiesBefore });
            }
            return;
        }
        // The same step on the same host twice means the last fill was refused
        // (a wrong password, a lockout warning). Keyed by HOST, not URL: a
        // refused form usually comes back at the URL it posted to, which is
        // not the one it was filled on. Filling it again would only hammer the
        // site; the human takes it from here.
        const key = `${step} ${host}`;
        if (attempted.has(key)) {
            opts.say(['capture-har: that sign-in step came back -- finish signing in in the browser.']);
            gaveUp = true;
            return;
        }
        if (!credsPromise) credsPromise = opts.resolve(startHost);
        const resolved = await credsPromise;
        if (resolved.source === 'none') {
            gaveUp = true;
            noTerminal = resolved.code === 'no-terminal';
            if (noTerminal && !sawOtherPage && opts.onUnattended) {
                opts.onUnattended(host);
                return;
            }
            opts.say([`capture-har: not signing in automatically (${resolved.reason}).`,
                '  Sign in in the browser; the recording carries on.']);
            return;
        }
        creds = resolved;
        // Checked AFTER resolving, because a Bitwarden item can widen where its
        // credential belongs beyond the start host (its own saved URIs) -- and
        // checked against the page's CURRENT address, which may have moved on
        // while the vault was unlocked or the prompt answered.
        if (!allowedUrl(page.url())) return;
        // What the site had already set before the password went in, so a
        // save can insist on something NEW -- a session cookie -- rather than
        // trusting that a page without a form means "signed in".
        if (step === 'password' && !cookiesBefore && context) {
            // Key -> value, in memory only: a sign-in very often ROTATES the
            // anonymous session cookie the first page view set (same name,
            // new value) rather than adding one.
            cookiesBefore = new Map((await context.cookies().catch(() => [])).map((c) => [cookieKey(c), c.value]));
        }
        const done = await fillStep(page, creds, step, () => allowedUrl(page.url()), opts.pace || null);
        if (!done) {
            if (step === 'otp') otpDeclined = true;
            return;
        }
        // Marked only once something was really filled, so a step this run
        // could not fill is not later mistaken for one the site refused.
        attempted.add(key);
        filledHosts.add(host);
        if (step === 'password') passwordFilled = true;
    }

    // One check at a time. Two load events racing would otherwise resolve the
    // vault twice, or fill one form from two coroutines.
    function check(page) {
        busy = busy.then(() => checkNow(page)).catch((e) => {
            // A message, never the error object: Playwright errors can quote
            // the arguments of the call that failed, and one of those was a fill.
            opts.say([`capture-har: automatic sign-in stopped (${String(e && e.message).split('\n')[0]}).`,
                '  Sign in in the browser; the recording carries on.']);
            gaveUp = true;
        });
        return busy;
    }

    function watch(page) {
        page.on('load', () => { check(page); });
    }
    if (context) {
        context.pages().forEach(watch);
        context.on('page', watch);
    }

    return {
        check,
        /** Every value filled into a page, for the scrub. Codes are left out: they are dead in 30s and 6 digits long. */
        secrets() {
            return creds ? [creds.username, creds.password].filter((v) => v && v.length >= 4) : [];
        },
        /** The hosts whose session is this capture's: the start host and every host a form was filled on. */
        hosts() {
            return [startHost, ...filledHosts].filter(Boolean);
        },
        /** For session.json: where the credentials came from, never what they were. */
        result() {
            const r = { source: creds ? creds.source : (gaveUp ? 'none' : null), signedIn };
            if (noTerminal) r.needed = true;
            return r;
        }
    };
}

/**
 * Merge this capture's cookies and storage into `existing` storage state,
 * replacing whatever was there for the same hosts and keeping every other
 * site's. Only these hosts are taken from `fresh`: the capture profile is
 * shared by every capture, and the file is not a dump of it.
 *
 * "Ours" is a domain that is one of `hosts`, a subdomain of one, or a PARENT
 * of one (a cookie set for `.example.com` by `www.example.com`). Never a guess
 * at the registrable domain: two labels of `shop.example.co.uk` is `co.uk`,
 * and that would take in every UK site in the profile. A parent can never be
 * a public suffix, because a browser refuses a cookie set on one.
 */
function mergeStorageState(existing, fresh, hosts) {
    const list = (Array.isArray(hosts) ? hosts : [hosts]).filter(Boolean).map((h) => h.toLowerCase());
    const ours = (domain) => {
        const d = String(domain || '').replace(/^\./, '').toLowerCase();
        return !!d && list.some((h) => hostMatches(h, d) || hostMatches(d, h));
    };
    const originHost = (o) => hostOf(o && o.origin);
    const base = existing && typeof existing === 'object' ? existing : {};
    return {
        cookies: [
            ...(base.cookies || []).filter((c) => !ours(c.domain)),
            ...((fresh && fresh.cookies) || []).filter((c) => ours(c.domain))
        ],
        origins: [
            ...(base.origins || []).filter((o) => !ours(originHost(o))),
            ...((fresh && fresh.origins) || []).filter((o) => ours(originHost(o)))
        ]
    };
}

function cookieKeys(state, hosts) {
    const picked = mergeStorageState(null, state, hosts).cookies;
    return picked.map((c) => `${c.domain}|${c.path}|${c.name}|${c.value}`).sort().join('\n');
}

/** A cookie's identity: domain, path and name. */
function cookieKey(c) {
    return `${String(c.domain || '').replace(/^\./, '').toLowerCase()}|${c.path}|${c.name}`;
}

/**
 * Save the signed-in session for `hosts` to `filePath`, merged with whatever
 * other sites the file already holds.
 *
 * ONLY WHERE GIT WILL NOT PICK IT UP. The file holds live session cookies, so
 * it is written when `classify(filePath)` says it is gitignored or outside any
 * work tree, and not otherwise -- a session that is not saved costs one more
 * sign-in; one that is committed costs the account. `classify` is
 * subs-destination's, the one gitignore check this subsystem has.
 *
 * ONLY WHEN SOMETHING CHANGED, and atomically: a sign-in that set no new
 * cookie is not evidence of a session, and replacing the file's copy with it
 * could swap a working session for an anonymous one. The write goes to a
 * temporary file renamed into place, so a crash cannot leave half of the only
 * copy.
 *
 * Never the password: storage state has no field for one, and nothing here
 * adds one.
 */
async function saveSession(context, filePath, hosts, classify, opts = {}) {
    const ignoredOrOutside = (s) => s === 'ignored' || s === 'outside-work-tree';
    const status = classify(filePath);
    if (!ignoredOrOutside(status)) return { saved: false, status };
    let existing = null;
    try { existing = JSON.parse(fs.readFileSync(filePath, 'utf8')); } catch (_) { /* first save */ }
    const fresh = await context.storageState();
    // EVIDENCE OF A SESSION: a cookie on these hosts that is new, or whose
    // value changed, since the password went in. A consent page or a redirect
    // interstitial changes nothing, and saving it would replace a working
    // session. (A tracker that re-issues itself also counts; that errs toward
    // saving, which the gitignore check above already makes safe.)
    if (opts.cookiesBefore) {
        const gained = mergeStorageState(null, fresh, hosts).cookies
            .some((c) => opts.cookiesBefore.get(cookieKey(c)) !== c.value);
        if (!gained) return { saved: false, status: 'no-new-session' };
    }
    if (existing && cookieKeys(existing, hosts) === cookieKeys(fresh, hosts)) {
        return { saved: false, status: 'unchanged' };
    }
    const text = JSON.stringify(mergeStorageState(existing, fresh, hosts), null, 2) + '\n';
    // The temporary file holds the same live cookies, so it gets the same
    // protection: written only where git ignores it too, and never left
    // behind. Where it would not be ignored, the write is direct instead.
    const temp = `${filePath}.${process.pid}.tmp`;
    if (!ignoredOrOutside(classify(temp))) {
        fs.writeFileSync(filePath, text, { mode: 0o600 });
        return { saved: true, status };
    }
    try {
        fs.writeFileSync(temp, text, { mode: 0o600 });
        fs.renameSync(temp, filePath);
    } finally {
        try { fs.unlinkSync(temp); } catch (_) { /* renamed away, as it should be */ }
    }
    return { saved: true, status };
}

module.exports = {
    MASTER_ENV,
    hostOf,
    hostMatches,
    installHint,
    findBw,
    createBitwarden,
    resolveCredentials,
    detectStep,
    fillStep,
    createSignInWatcher,
    mergeStorageState,
    saveSession
};
