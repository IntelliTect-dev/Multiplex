'use strict';

/**
 * mirror.js -- the capture browser, live on the owner's phone, during a
 * hand-over.
 *
 * When a site puts up a block or a "prove you're human" check, the capture is
 * handed to a person (capture-har.js). The person may not be at this PC: the
 * owner runs captures from a phone. So a hand-over also starts this: a small
 * web page that shows the capture browser's tab as it is, live, and relays the
 * person's taps, typing, scrolling and Back into it -- so they can answer the
 * check themselves, from wherever they are -- with two buttons: "I'm done,
 * continue" and "End the capture".
 *
 * PRIVATE BY CONSTRUCTION.
 *   - It listens on ONE address: this PC's Tailscale address, read at run time
 *     from `tailscale status --json` (findTailnetAddress). Never a wildcard,
 *     never a LAN interface, never loopback -- ENFORCED, not assumed: an
 *     address outside the tailnet's ranges (100.64.0.0/10, fd7a:115c:a1e0::/48)
 *     is refused by findTailnetAddress and again by startMirror, whatever the
 *     CLI that answered said. Only devices on the owner's own tailnet can even
 *     open a connection. Without Tailscale there is no phone view, and the
 *     hand-over is the PC-only one it always was.
 *   - The link carries a long random token, and is SINGLE-USE. The first
 *     browser to open it is given the one cookie this server issues -- random,
 *     HttpOnly, SameSite=Strict -- and the token is spent: from then on the
 *     page, the live stream and input all need that cookie. Without it (a
 *     second holder of the link: a transcript, a log, a link preview), the
 *     spent link gets a one-line page saying it was already opened on another
 *     device -- so a person reopening it knows why -- and nothing more; the
 *     stream, input and every other path, a wrong or unknown token above all,
 *     are a bare 404. So ONE BROWSER AT A TIME
 *     holds for the whole hand-over, and the cookie is a credential, not a
 *     label. The browser holding it may reload and reconnect.
 *   - `tailscale serve` is deliberately not used: it writes persistent
 *     configuration to the tailnet node. The tailnet is already encrypted end
 *     to end (WireGuard), so the page is plain HTTP on the tailnet address.
 *   - What the person types is relayed and never logged.
 *
 * WHAT IT DOES TO THE BROWSER, and only this: CDP `Page.startScreencast` for
 * the frames, and `Input.*` for the person's input -- the same input a person
 * at this PC would give. It does not resize the page (that would change the
 * captured site's layout on the PC as well); the phone zooms the picture
 * instead. It solves nothing and disguises nothing: the person answers the
 * check (THE BOUNDARY, SKILL.md).
 *
 * THE TRANSPORT is Server-Sent Events down (frames and status) and small JSON
 * POSTs up (input) -- not a WebSocket, because Node has no WebSocket server
 * built in and this skill is zero-dependency. The messages are the ones the
 * Multiplex server's live sign-in mirror uses on a phone: tap, scroll, text,
 * key, back -- plus press / move / release, so a "Press & Hold" or a slider
 * can be answered, and done / end.
 *
 * Zero dependencies. The CDP session is anything with send(method, params)
 * and on/off(event, fn) -- Playwright's CDPSession, or a test's fake.
 */

const crypto = require('crypto');
const http = require('http');
const net = require('net');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const MAX_INPUT_BYTES = 8 * 1024;
const MAX_TEXT_LENGTH = 512;
const KEEPALIVE_MS = 15000;
const FRAME_QUALITY = 60;
const FRAME_MAX_WIDTH = 1280;
const FRAME_MAX_HEIGHT = 1600;
const TAILSCALE_TIMEOUT_MS = 5000;
// How many live streams the one browser holding the view may have open: a
// phone that reconnects keeps its newest few, and the oldest are ended.
const MAX_STREAMS = 3;

// The only keys the phone may press, as CDP describes them.
const KEYS = Object.freeze({
    Enter: { key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' },
    Backspace: { key: 'Backspace', code: 'Backspace', windowsVirtualKeyCode: 8 },
    Tab: { key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 },
    Escape: { key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 },
    Delete: { key: 'Delete', code: 'Delete', windowsVirtualKeyCode: 46 },
    ArrowLeft: { key: 'ArrowLeft', code: 'ArrowLeft', windowsVirtualKeyCode: 37 },
    ArrowUp: { key: 'ArrowUp', code: 'ArrowUp', windowsVirtualKeyCode: 38 },
    ArrowRight: { key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 },
    ArrowDown: { key: 'ArrowDown', code: 'ArrowDown', windowsVirtualKeyCode: 40 }
});

// ---------------------------------------------------------------------------
// Where: this PC's Tailscale address
// ---------------------------------------------------------------------------

// The addresses Tailscale gives a node: 100.64.0.0/10 (IPv4, the CGNAT range)
// and fd7a:115c:a1e0::/48 (IPv6). Nothing outside them is a tailnet address.
const TAILNET = new net.BlockList();
TAILNET.addSubnet('100.64.0.0', 10, 'ipv4');
TAILNET.addSubnet('fd7a:115c:a1e0::', 48, 'ipv6');
const LOOPBACK = new net.BlockList();
LOOPBACK.addSubnet('127.0.0.0', 8, 'ipv4');
LOOPBACK.addAddress('::1', 'ipv6');

function familyOf(ip) {
    const v = net.isIP(String(ip || ''));
    return v === 4 ? 'ipv4' : v === 6 ? 'ipv6' : null;
}

/** Is this one of the addresses Tailscale gives a node? */
function isTailnetAddress(ip) {
    const family = familyOf(ip);
    return !!family && TAILNET.check(String(ip), family);
}

// TEST-ONLY: a test plays the phone on THIS PC, so its phone view must listen
// on loopback. Switched on in-process by test code only -- mirror.test.js, and
// the test preload that stands in for the tailscale CLI -- and reached by no
// flag, no argument and no environment variable of the product.
let loopbackForTests = false;
function allowLoopbackForTests(on = true) { loopbackForTests = !!on; }

/** May the phone view listen here? A tailnet address; loopback only for a test. */
function listenable(ip) {
    const family = familyOf(ip);
    if (!family) return false;
    return isTailnetAddress(ip) || (loopbackForTests && LOOPBACK.check(String(ip), family));
}

/**
 * Where the tailscale CLI may be, in the order tried. HAR_CAPTURE_TAILSCALE
 * names it outright -- a hook for tests, which point it at a CLI that is not
 * there so no test touches the PC's real tailnet. Whatever answers, only a
 * tailnet address is ever used (findTailnetAddress).
 */
function tailscaleCandidates(env = process.env, platform = process.platform) {
    if (env.HAR_CAPTURE_TAILSCALE) return [env.HAR_CAPTURE_TAILSCALE];
    const list = ['tailscale'];
    if (platform === 'win32') {
        list.push(path.join(env.ProgramFiles || 'C:\\Program Files', 'Tailscale', 'tailscale.exe'));
    } else if (platform === 'darwin') {
        list.push('/Applications/Tailscale.app/Contents/MacOS/Tailscale');
    }
    return list;
}

/**
 * This PC's Tailscale IPv4 address, or why there is none.
 *
 * Read from `tailscale status --json` at run time -- never configured, never
 * written down -- and only when Tailscale says it is Running. `Self.TailscaleIPs`
 * is the address the tailnet routes to this PC; the IPv4 one is taken because
 * every phone resolves it.
 *
 * ONLY A TAILNET ADDRESS IS EVER RETURNED. Whatever answered -- the real CLI,
 * a stand-in on PATH, a misconfigured one -- an address outside 100.64.0.0/10
 * (a LAN address, loopback, a wildcard) is refused, and the reason names it.
 *
 * `opts.env` (HAR_CAPTURE_TAILSCALE, a test hook, names the CLI outright),
 * `opts.run(exe, args)` and `opts.platform` are injectable for tests.
 *
 * @returns {{address: string} | {address: null, reason: string}}
 */
function findTailnetAddress(opts = {}) {
    const env = opts.env || process.env;
    const run = opts.run || ((exe, args) => spawnSync(exe, args, {
        encoding: 'utf8', windowsHide: true, timeout: TAILSCALE_TIMEOUT_MS
    }));
    let result = null;
    for (const exe of tailscaleCandidates(env, opts.platform || process.platform)) {
        let r;
        try { r = run(exe, ['status', '--json']); } catch (e) { r = { error: e }; }
        if (r && r.error && r.error.code === 'ENOENT') continue;
        result = r;
        break;
    }
    if (!result) return { address: null, reason: 'Tailscale is not installed on this PC' };
    if (result.error) return { address: null, reason: `Tailscale could not be asked (${result.error.code || result.error.message})` };
    let doc;
    try { doc = JSON.parse(String(result.stdout || '')); } catch (e) {
        void e;
        return { address: null, reason: 'Tailscale is not running on this PC' };
    }
    if (doc.BackendState !== 'Running') {
        return { address: null, reason: `Tailscale is not connected on this PC (it says ${doc.BackendState || 'nothing'})` };
    }
    const ips = (doc.Self && Array.isArray(doc.Self.TailscaleIPs)) ? doc.Self.TailscaleIPs : [];
    const v4s = ips.map(String).filter((ip) => net.isIPv4(ip));
    if (!v4s.length) return { address: null, reason: 'Tailscale has no IPv4 address for this PC' };
    const v4 = v4s.find(listenable);
    if (!v4) {
        return { address: null, reason: `Tailscale reported ${v4s[0]} for this PC, which is not a tailnet address ` +
            '(100.64.0.0/10), and the phone view listens only on the tailnet' };
    }
    return { address: v4 };
}

/**
 * Only a tailnet address -- never a wildcard, a name, a LAN address or
 * loopback (that one for a test only). Checked by range, so no spelling of
 * "every interface" ('0.0.0.0', '::', '0:0:0:0:0:0:0:0') gets through.
 */
function assertBindable(host) {
    if (!listenable(host)) {
        throw new Error('the phone view listens only on this PC\'s Tailscale address (100.64.0.0/10 or ' +
            `fd7a:115c:a1e0::/48), not '${host}'`);
    }
}

// ---------------------------------------------------------------------------
// Input: the phone's messages, into CDP
// ---------------------------------------------------------------------------

function fraction(v) {
    return typeof v === 'number' && Number.isFinite(v) && v >= 0 && v <= 1 ? v : null;
}

function delta(v) {
    return typeof v === 'number' && Number.isFinite(v) ? Math.max(-2, Math.min(2, v)) : 0;
}

/**
 * One message from the phone, validated, or null. Anything unexpected is
 * dropped silently: the page is ours, so a malformed message is noise.
 */
function parseInput(doc) {
    if (!doc || typeof doc !== 'object' || typeof doc.t !== 'string') return null;
    const x = fraction(doc.x);
    const y = fraction(doc.y);
    switch (doc.t) {
        case 'tap': case 'down': case 'move': case 'up':
            return x === null || y === null ? null : { t: doc.t, x, y };
        case 'scroll':
            return x === null || y === null ? null : { t: 'scroll', x, y, dx: delta(doc.dx), dy: delta(doc.dy) };
        case 'text':
            return typeof doc.v === 'string' && doc.v.length > 0 && doc.v.length <= MAX_TEXT_LENGTH
                ? { t: 'text', v: doc.v } : null;
        case 'key':
            return typeof doc.k === 'string' && Object.prototype.hasOwnProperty.call(KEYS, doc.k)
                ? { t: 'key', k: doc.k } : null;
        case 'back': case 'done': case 'end':
            return { t: doc.t };
        default:
            return null;
    }
}

/**
 * Replay one input into the page over CDP. `size` is the CSS size of the
 * frame the phone tapped on, so a fraction of it is a point in the page.
 */
async function relayInput(cdp, input, size) {
    const at = () => ({ x: input.x * size.w, y: input.y * size.h });
    const mouse = (type, extra) => cdp.send('Input.dispatchMouseEvent', Object.assign({ type }, at(), extra));
    switch (input.t) {
        case 'tap':
            await mouse('mouseMoved', { button: 'none' });
            await mouse('mousePressed', { button: 'left', buttons: 1, clickCount: 1 });
            await mouse('mouseReleased', { button: 'left', buttons: 0, clickCount: 1 });
            return;
        case 'down':
            await mouse('mouseMoved', { button: 'none' });
            await mouse('mousePressed', { button: 'left', buttons: 1, clickCount: 1 });
            return;
        case 'move':
            await mouse('mouseMoved', { button: 'left', buttons: 1 });
            return;
        case 'up':
            await mouse('mouseReleased', { button: 'left', buttons: 0, clickCount: 1 });
            return;
        case 'scroll':
            await mouse('mouseWheel', { deltaX: input.dx * size.w, deltaY: input.dy * size.h });
            return;
        case 'text':
            // Into whatever has focus, as a phone keyboard would.
            await cdp.send('Input.insertText', { text: input.v });
            return;
        case 'key': {
            const k = KEYS[input.k];
            await cdp.send('Input.dispatchKeyEvent', Object.assign({ type: k.text ? 'keyDown' : 'rawKeyDown' }, k));
            await cdp.send('Input.dispatchKeyEvent', Object.assign({ type: 'keyUp' }, k, { text: undefined }));
            return;
        }
        case 'back': {
            const history = await cdp.send('Page.getNavigationHistory');
            const entry = history && history.entries && history.entries[history.currentIndex - 1];
            if (entry) await cdp.send('Page.navigateToHistoryEntry', { entryId: entry.id });
            return;
        }
        default:
    }
}

// ---------------------------------------------------------------------------
// The server
// ---------------------------------------------------------------------------

function newSecret() {
    return crypto.randomBytes(32).toString('base64url');
}

function sameSecret(a, b) {
    const x = Buffer.from(String(a || ''));
    const y = Buffer.from(String(b || ''));
    return x.length === y.length && x.length > 0 && crypto.timingSafeEqual(x, y);
}

function cookieValue(req, name) {
    const header = req.headers.cookie || '';
    for (const part of header.split(';')) {
        const i = part.indexOf('=');
        if (i > 0 && part.slice(0, i).trim() === name) return part.slice(i + 1).trim();
    }
    return null;
}

function notFound(res) {
    res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' });
    res.end('Not found');
}

const SPENT_TEXT = 'This link was already opened on another device. Use that device, or the terminal on the PC.';

/**
 * The GENUINE link, already spent, opened by a browser that does not hold it.
 * Said, so the person reopening it knows why it does not work -- and nothing
 * more: no script, no link, nothing about the capture. A wrong or unknown
 * token is never told even this; it gets the bare 404.
 */
function spentPage(res) {
    res.writeHead(410, {
        'content-type': 'text/html; charset=utf-8',
        'cache-control': 'no-store',
        'x-frame-options': 'DENY',
        'x-content-type-options': 'nosniff',
        'referrer-policy': 'no-referrer',
        'content-security-policy': "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
    });
    res.end('<!doctype html><html lang="en"><head><meta charset="utf-8">' +
        '<meta name="viewport" content="width=device-width, initial-scale=1"><title>Link already used</title>' +
        `</head><body><p>${SPENT_TEXT}</p></body></html>`);
}

/**
 * Start the phone view.
 *
 *   host     the address to listen on (findTailnetAddress); never a wildcard
 *   port     0 (any free port) unless a test pins one
 *   cdp      a CDP session attached to the tab to show
 *   status   {state, text} for the status line
 *   onDone   the person tapped "I'm done, continue"
 *   onEnd    the person tapped "End the capture"
 *
 * @returns {Promise<{url, token, port, host, setStatus, close, clients}>}
 */
async function startMirror(opts) {
    const host = opts.host;
    const cdp = opts.cdp;
    try {
        assertBindable(host);
    } catch (e) {
        // It owns the CDP session it was handed, started or not.
        if (cdp && typeof cdp.detach === 'function') await Promise.resolve(cdp.detach()).catch(() => {});
        throw e;
    }
    const token = opts.token || newSecret();
    const prefix = `/m/${token}/`;
    // THE ONE COOKIE. Null until the first browser opens the link; from then
    // on, the link is spent and this is the only way in.
    let claim = null;
    let status = Object.assign({ state: 'handed-over', text: 'The capture is handed over to you.' }, opts.status || {});
    let lastFrame = null;
    let size = { w: 0, h: 0 };
    let closed = false;
    const clients = new Set();
    let inputChain = Promise.resolve();

    const sse = (client, event, data) => {
        if (client.res.writableEnded) return true;
        return client.res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
    };
    const broadcast = (event, data) => { for (const c of clients) sse(c, event, data); };
    const sendFrame = (client) => {
        if (!lastFrame || client.busy) return;
        if (!sse(client, 'frame', lastFrame)) {
            client.busy = true;
            client.res.once('drain', () => { client.busy = false; if (client.stale) { client.stale = false; sendFrame(client); } });
        }
    };

    const holder = (req) => !!claim && sameSecret(cookieValue(req, 'hm'), claim);

    const server = http.createServer((req, res) => {
        let url;
        try { url = new URL(req.url, 'http://mirror.invalid'); } catch (e) { void e; notFound(res); return; }
        const p = url.pathname;
        if (closed || !p.startsWith('/m/') || !sameSecret(p.slice(3, 3 + token.length), token) ||
            p.slice(3 + token.length, 4 + token.length) !== '/') {
            notFound(res);
            return;
        }
        const rest = p.slice(prefix.length);
        if (req.method === 'GET' && rest === '') {
            // SINGLE-USE. The first browser to open the link is given the one
            // cookie, and the link is spent; the browser holding it may reload.
            if (!claim) {
                claim = newSecret();
                res.setHeader('set-cookie', `hm=${claim}; Path=${prefix}; HttpOnly; SameSite=Strict`);
            } else if (!holder(req)) {
                spentPage(res);
                return;
            }
            const nonce = crypto.randomBytes(16).toString('base64');
            res.writeHead(200, {
                'content-type': 'text/html; charset=utf-8',
                'cache-control': 'no-store',
                'x-frame-options': 'DENY',
                'x-content-type-options': 'nosniff',
                'referrer-policy': 'no-referrer',
                'content-security-policy': `default-src 'none'; script-src 'nonce-${nonce}'; ` +
                    `style-src 'nonce-${nonce}'; img-src data:; connect-src 'self'; base-uri 'none'; ` +
                    "form-action 'none'; frame-ancestors 'none'"
            });
            res.end(phonePage(nonce));
            return;
        }
        // Everything else is the browser holding the cookie, or nobody.
        if (!holder(req)) { notFound(res); return; }
        if (req.method === 'GET' && rest === 'events') {
            res.writeHead(200, {
                'content-type': 'text/event-stream; charset=utf-8',
                'cache-control': 'no-store',
                'x-content-type-options': 'nosniff',
                connection: 'keep-alive'
            });
            const client = { res, busy: false, stale: false };
            clients.add(client);
            // A phone that reconnects keeps its newest few streams; the oldest go.
            for (const old of clients) {
                if (clients.size <= MAX_STREAMS) break;
                clients.delete(old);
                try { old.res.end(); } catch (e) { void e; }
            }
            const ping = setInterval(() => { if (!res.writableEnded) res.write(': ping\n\n'); }, KEEPALIVE_MS);
            if (typeof ping.unref === 'function') ping.unref();
            req.on('close', () => { clearInterval(ping); clients.delete(client); });
            sse(client, 'status', status);
            sendFrame(client);
            return;
        }
        if (req.method === 'POST' && rest === 'input') {
            // Only from the page itself: a browser always sends Origin on a
            // POST, and it must be this server.
            const origin = req.headers.origin;
            if (origin && origin !== `http://${req.headers.host}`) { notFound(res); return; }
            if (!/^application\/json\b/i.test(String(req.headers['content-type'] || ''))) { notFound(res); return; }
            let body = '';
            let tooBig = false;
            req.setEncoding('utf8');
            req.on('data', (chunk) => {
                if (tooBig) return;
                body += chunk;
                if (Buffer.byteLength(body) > MAX_INPUT_BYTES) { tooBig = true; body = ''; }
            });
            req.on('end', () => {
                if (tooBig) { res.writeHead(413, { 'cache-control': 'no-store' }); res.end(); return; }
                let doc = null;
                try { doc = JSON.parse(body); } catch (e) { void e; }
                const input = parseInput(doc);
                res.writeHead(input ? 204 : 400, { 'cache-control': 'no-store' });
                res.end();
                if (!input || closed) return;
                if (input.t === 'done') { if (opts.onDone) opts.onDone(); return; }
                if (input.t === 'end') { if (opts.onEnd) opts.onEnd(); return; }
                if (input.t !== 'back' && input.t !== 'text' && input.t !== 'key' && !(size.w > 0)) return;
                // IN ORDER, one at a time: a press must land before its release.
                // Never logged: a typed value may be anything.
                inputChain = inputChain.then(() => relayInput(cdp, input, size)).catch(() => {});
            });
            return;
        }
        notFound(res);
    });
    // Not HTTP at all: the same bare 404 as anything else, not Node's 400.
    server.on('clientError', (e, socket) => {
        if (socket.writable) {
            socket.end('HTTP/1.1 404 Not Found\r\ncontent-type: text/plain; charset=utf-8\r\n' +
                'content-length: 9\r\nconnection: close\r\n\r\nNot found');
        } else {
            socket.destroy();
        }
    });

    // LISTENING FIRST, then the screencast: a view that cannot listen (the
    // port taken, the address gone) must leave nothing running in the browser.
    const detach = () => (typeof cdp.detach === 'function'
        ? Promise.resolve(cdp.detach()).catch(() => {}) : Promise.resolve());
    try {
        await new Promise((resolve, reject) => {
            server.once('error', reject);
            server.listen(opts.port || 0, host, () => { server.off('error', reject); resolve(); });
        });
    } catch (e) {
        await detach();
        throw e;
    }

    const onFrame = (params) => {
        if (!params) return;
        void Promise.resolve(cdp.send('Page.screencastFrameAck', { sessionId: params.sessionId })).catch(() => {});
        if (closed) return;
        const meta = params.metadata || {};
        if (meta.deviceWidth > 0 && meta.deviceHeight > 0) size = { w: meta.deviceWidth, h: meta.deviceHeight };
        lastFrame = { d: params.data, w: size.w, h: size.h };
        for (const c of clients) { if (c.busy) c.stale = true; else sendFrame(c); }
    };
    try {
        // The tab on top, so the browser renders it and the screencast has frames.
        await Promise.resolve(cdp.send('Page.bringToFront')).catch(() => {});
        cdp.on('Page.screencastFrame', onFrame);
        await cdp.send('Page.startScreencast', {
            format: 'jpeg', quality: FRAME_QUALITY, maxWidth: FRAME_MAX_WIDTH, maxHeight: FRAME_MAX_HEIGHT, everyNthFrame: 1
        });
    } catch (e) {
        closed = true;
        cdp.off('Page.screencastFrame', onFrame);
        await Promise.resolve(cdp.send('Page.stopScreencast')).catch(() => {});
        await detach();
        await new Promise((resolve) => server.close(() => resolve()));
        throw e;
    }
    const address = server.address();
    const port = address.port;
    const hostPart = net.isIPv6(host) ? `[${host}]` : host;

    return {
        url: `http://${hostPart}:${port}${prefix}`,
        token,
        port,
        host: address.address,
        clients: () => clients.size,
        setStatus(next) {
            status = Object.assign({}, status, next);
            broadcast('status', status);
        },
        /** Stop the screencast, tell the page, and stop listening. Safe to call twice. */
        async close(text) {
            if (closed) return;
            closed = true;
            broadcast('end', { text: text || 'This hand-over is over. You can close this page.' });
            for (const c of clients) { try { c.res.end(); } catch (e) { void e; } }
            clients.clear();
            cdp.off('Page.screencastFrame', onFrame);
            await Promise.resolve(cdp.send('Page.stopScreencast')).catch(() => {});
            if (typeof cdp.detach === 'function') await Promise.resolve(cdp.detach()).catch(() => {});
            await new Promise((resolve) => {
                server.close(() => resolve());
                if (typeof server.closeAllConnections === 'function') server.closeAllConnections();
            });
        }
    };
}

/** What the phone's status line says during a hand-over. */
function handOverStatus(record) {
    return {
        state: 'handed-over',
        text: `Handed over to you: ${record.reason}. The capture is paused for you. Answer it yourself ` +
            'if you choose to, then tap "I\'m done, continue" -- or end the capture.'
    };
}

// ---------------------------------------------------------------------------
// The page
// ---------------------------------------------------------------------------

let pageTemplate = null;
function phonePage(nonce) {
    if (!pageTemplate) pageTemplate = fs.readFileSync(path.join(__dirname, 'mirror-page.html'), 'utf8');
    return pageTemplate.replace(/__NONCE__/g, nonce);
}

module.exports = {
    findTailnetAddress,
    tailscaleCandidates,
    isTailnetAddress,
    allowLoopbackForTests,
    startMirror,
    handOverStatus,
    parseInput,
    relayInput,
    KEYS,
    MAX_INPUT_BYTES,
    MAX_STREAMS
};
