#!/usr/bin/env node
// Behavior tests for mirror.js -- the capture browser, live and private, on the
// owner's phone during a hand-over. Zero-dep; runs with `node mirror.test.js`.
//
// FALSIFIERS (fail before this change, pass after):
//   T1  the address is this PC's Tailscale IPv4, read from `tailscale status
//       --json` at run time -- and when Tailscale is missing or not running,
//       there is no address and the reason says which; an address Tailscale
//       reports that is NOT a tailnet one (a LAN address, loopback) is refused
//       with the reason, never used
//   T2  it never listens on a wildcard, a name, a LAN address or loopback --
//       only on a tailnet address (100.64.0.0/10, fd7a:115c:a1e0::/48), with
//       loopback only through the in-process test switch
//   T3  it listens ONLY on the address it was given
//   T4  the link is SINGLE-USE: without the token every request is a bare
//       404; the first browser to open it gets the one cookie the server
//       issues, and the link is spent -- a second holder gets a one-line
//       "already opened on another device" page (410) and nothing else, and
//       404 for the stream and input; a wrong token is still a bare 404; a
//       cookie the server never issued is nobody; the browser that holds it
//       can reload and reconnect
//   T5  frames: the screencast starts, each frame is acknowledged and reaches
//       the phone; closing stops the screencast, ends the page and stops
//       listening
//   T6  input: taps, press/move/release, scroll, typing (Input.insertText),
//       keys and Back reach the page through CDP, in order; a cross-origin or
//       non-JSON post is refused; the two buttons call back
//   T7  a view that cannot listen leaves nothing running: no screencast, no
//       frame listener, the CDP session let go; malformed HTTP is a bare 404;
//       one browser cannot pile up live streams
//   E1  end to end, against a real browser: a frame of a real page reaches the
//       phone view, and a tap and typing from it land in that page
//   E2  the phone page's own script, in a real browser emulating a phone
//       (touch, mobile viewport): live frames, a touch tap on the picture,
//       typing through its text box, a press-drag-release that moves a
//       slider, and "I'm done, continue"

'use strict';

const assert = require('assert');
const http = require('http');
const os = require('os');
const path = require('path');
const { EventEmitter } = require('events');

const mirror = require(path.join(__dirname, 'mirror.js'));

const queued = [];
function test(name, fn) { queued.push({ name, fn }); }
const skipped = [];

// Tailnet-shaped addresses (Tailscale's 100.64.0.0/10 and fd7a:115c:a1e0::/48)
// that no test ever listens on.
const FAKE_V4 = '100.101.102.103';
const FAKE_V6 = 'fd7a:115c:a1e0::10';

// Every view below plays the phone on THIS PC, so it listens on loopback --
// through the in-process test switch, which nothing in the product reaches.
// T1 and T2 turn it off to prove loopback is refused without it.
mirror.allowLoopbackForTests(true);

/** A CDP session that records what it was asked, and can emit events. */
function fakeCdp(history) {
    const cdp = new EventEmitter();
    cdp.calls = [];
    cdp.detached = false;
    cdp.send = async (method, params) => {
        cdp.calls.push({ method, params });
        if (method === 'Page.getNavigationHistory') return history || { currentIndex: 1, entries: [{ id: 7 }, { id: 8 }] };
        return {};
    };
    cdp.detach = async () => { cdp.detached = true; };
    cdp.frame = (w, h, data = 'AAAA', sessionId = 1) =>
        cdp.emit('Page.screencastFrame', { data, sessionId, metadata: { deviceWidth: w, deviceHeight: h } });
    return cdp;
}

function request(url, opts = {}) {
    return new Promise((resolve, reject) => {
        const u = new URL(url);
        const req = http.request({ host: u.hostname, port: u.port, path: u.pathname + u.search,
            method: opts.method || 'GET', headers: opts.headers || {} }, (res) => {
            let body = '';
            res.setEncoding('utf8');
            res.on('data', (d) => { body += d; });
            res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body }));
        });
        req.on('error', reject);
        if (opts.body) req.write(opts.body);
        req.end();
    });
}

/** Open the page as a browser would: its own cookie. */
async function openPage(view) {
    const r = await request(view.url);
    assert.strictEqual(r.status, 200, 'the link is the page');
    return String(r.headers['set-cookie']).split(';')[0];
}

/** Open the page and start watching -- which is what claims the view. */
async function claim(view) {
    const cookie = await openPage(view);
    const stream = events(view.url + 'events', cookie);
    await stream.next('status');
    return { cookie, stream };
}

/** An SSE reader: resolves events as they arrive. */
function events(url, cookie) {
    const got = [];
    const waiters = [];
    let res = null;
    const req = http.get(url, { headers: { cookie } }, (r) => {
        res = r;
        let buf = '';
        r.setEncoding('utf8');
        r.on('data', (d) => {
            buf += d;
            let i;
            while ((i = buf.indexOf('\n\n')) >= 0) {
                const block = buf.slice(0, i);
                buf = buf.slice(i + 2);
                const ev = /^event: (.+)$/m.exec(block);
                const data = /^data: (.+)$/m.exec(block);
                if (!ev || !data) continue;
                got.push({ event: ev[1], data: JSON.parse(data[1]) });
                for (const w of waiters.slice()) w();
            }
        });
        r.on('end', () => { for (const w of waiters.slice()) w(); });
    });
    req.on('error', () => {});
    return {
        got,
        status: () => res && res.statusCode,
        async next(event, timeoutMs = 10000) {
            const deadline = Date.now() + timeoutMs;
            for (;;) {
                const found = got.find((g) => g.event === event && !g.taken);
                if (found) { found.taken = true; return found.data; }
                if (Date.now() > deadline) throw new Error(`no '${event}' event within ${timeoutMs} ms (got ${got.map((g) => g.event)})`);
                await new Promise((r) => { waiters.push(r); setTimeout(r, 100); });
            }
        },
        close: () => req.destroy()
    };
}

const post = (view, cookie, doc, headers = {}) => request(view.url + 'input', {
    method: 'POST', body: typeof doc === 'string' ? doc : JSON.stringify(doc),
    headers: Object.assign({ cookie, 'content-type': 'application/json',
        origin: `http://127.0.0.1:${view.port}` }, headers)
});

const settle = () => new Promise((r) => setTimeout(r, 50));

// ---------------------------------------------------------------------------

test('T1: the address is this PC\'s Tailscale IPv4, read at run time -- or the reason there is none', () => {
    const status = (doc) => () => ({ status: 0, stdout: JSON.stringify(doc) });
    const running = { BackendState: 'Running', Self: { TailscaleIPs: [FAKE_V6, FAKE_V4] } };
    assert.deepStrictEqual(mirror.findTailnetAddress({ env: {}, run: status(running) }), { address: FAKE_V4 },
        'the IPv4 one, whatever order Tailscale lists them in');
    const asked = [];
    mirror.findTailnetAddress({ env: {}, run: (exe, args) => { asked.push([exe, ...args]); return status(running)(); } });
    assert.deepStrictEqual(asked, [['tailscale', 'status', '--json']]);

    const stopped = mirror.findTailnetAddress({ env: {}, run: status({ BackendState: 'Stopped', Self: {} }) });
    assert.strictEqual(stopped.address, null);
    assert.match(stopped.reason, /not connected .*Stopped/);
    const enoent = () => ({ error: Object.assign(new Error('spawn ENOENT'), { code: 'ENOENT' }) });
    const missing = mirror.findTailnetAddress({ env: {}, platform: 'linux', run: enoent });
    assert.deepStrictEqual(missing, { address: null, reason: 'Tailscale is not installed on this PC' });
    assert.match(mirror.findTailnetAddress({ env: {}, run: () => ({ status: 1, stdout: 'failed to connect' }) }).reason,
        /not running/);
    assert.match(mirror.findTailnetAddress({ env: {}, run: status({ BackendState: 'Running', Self: { TailscaleIPs: [FAKE_V6] } }) }).reason,
        /no IPv4/);

    // ON WINDOWS the CLI is often not on PATH: the install folder is tried next.
    const tried = [];
    const win = mirror.findTailnetAddress({ env: { ProgramFiles: 'C:\\PF' }, platform: 'win32',
        run: (exe) => { tried.push(exe); return exe === 'tailscale' ? enoent() : status(running)(); } });
    assert.strictEqual(win.address, FAKE_V4);
    assert.deepStrictEqual(tried, ['tailscale', path.join('C:\\PF', 'Tailscale', 'tailscale.exe')]);
    // And HAR_CAPTURE_TAILSCALE names it outright (a test points it nowhere).
    assert.deepStrictEqual(mirror.tailscaleCandidates({ HAR_CAPTURE_TAILSCALE: 'X:\\nowhere\\tailscale.exe' }, 'win32'),
        ['X:\\nowhere\\tailscale.exe']);

    // ONLY A TAILNET ADDRESS. Whatever the CLI that answered says -- a
    // stand-in on PATH, a misconfigured one -- a LAN address or loopback is
    // never the phone view's address, and the reason says what was refused.
    mirror.allowLoopbackForTests(false);
    try {
        for (const ip of ['192.168.1.10', '10.0.0.5', '172.16.4.4', '127.0.0.1', '100.63.255.255', '100.128.0.1',
            '0.0.0.0']) {
            const r = mirror.findTailnetAddress({ env: {}, run: status({ BackendState: 'Running', Self: { TailscaleIPs: [ip] } }) });
            assert.strictEqual(r.address, null, `${ip} is not a tailnet address`);
            assert.match(r.reason, new RegExp(`Tailscale reported ${ip.replace(/\./g, '\\.')} for this PC, which is not a tailnet address`));
        }
        assert.strictEqual(mirror.findTailnetAddress({ env: {}, run: status({ BackendState: 'Running',
            Self: { TailscaleIPs: ['100.64.0.1'] } }) }).address, '100.64.0.1', 'the bottom of 100.64.0.0/10');
        assert.strictEqual(mirror.findTailnetAddress({ env: {}, run: status({ BackendState: 'Running',
            Self: { TailscaleIPs: ['100.127.255.254'] } }) }).address, '100.127.255.254', 'and the top');
    } finally {
        mirror.allowLoopbackForTests(true);
    }
    // The in-process test switch lets loopback through -- and nothing else.
    assert.strictEqual(mirror.findTailnetAddress({ env: {}, run: status({ BackendState: 'Running',
        Self: { TailscaleIPs: ['127.0.0.1'] } }) }).address, '127.0.0.1');
    assert.strictEqual(mirror.findTailnetAddress({ env: {}, run: status({ BackendState: 'Running',
        Self: { TailscaleIPs: ['192.168.1.10'] } }) }).address, null, 'a LAN address, even for a test');
});

test('T2: it listens only on a tailnet address -- never a wildcard, a name, a LAN address or loopback', async () => {
    mirror.allowLoopbackForTests(false);
    try {
        for (const host of ['0.0.0.0', '::', '0:0:0:0:0:0:0:0', '', 'localhost', undefined, '192.168.1.10',
            '10.0.0.5', '127.0.0.1', '::1', '2001:db8::10']) {
            await assert.rejects(mirror.startMirror({ host, cdp: fakeCdp() }),
                /listens only on this PC's Tailscale address/, `'${host}' must be refused`);
        }
    } finally {
        mirror.allowLoopbackForTests(true);
    }
    for (const host of ['0.0.0.0', '::', '192.168.1.10']) {
        await assert.rejects(mirror.startMirror({ host, cdp: fakeCdp() }),
            /listens only on this PC's Tailscale address/, `'${host}' is refused even with the test switch on`);
    }
    assert.ok(mirror.isTailnetAddress(FAKE_V4) && mirror.isTailnetAddress(FAKE_V6), 'both tailnet ranges count');
});

test('T3: it listens ONLY on the address it was given', async () => {
    const view = await mirror.startMirror({ host: '127.0.0.1', cdp: fakeCdp() });
    try {
        assert.strictEqual(view.host, '127.0.0.1');
        assert.ok(view.url.startsWith(`http://127.0.0.1:${view.port}/m/`), view.url);
        // Another of this machine's own addresses, same port: nothing there.
        const other = Object.values(os.networkInterfaces()).flat()
            .find((i) => i && i.family === 'IPv4' && !i.internal);
        if (!other) {
            skipped.push('T3 other-interface check (this machine has only loopback)');
            return;
        }
        const err = await request(`http://${other.address}:${view.port}/`).then(() => null, (e) => e);
        assert.ok(err && /ECONNREFUSED|ETIMEDOUT|EHOSTUNREACH/.test(err.code || err.message),
            `the view answered on ${other.address}, which it was never given: ${err}`);
    } finally {
        await view.close();
    }
});

/** Resolves with the HTTP status of an SSE request, without waiting for it to end. */
function streamStatus(url, cookie) {
    return new Promise((resolve, reject) => {
        const req = http.get(url, { headers: cookie ? { cookie } : {} }, (res) => {
            resolve(res.statusCode);
            req.destroy();
        });
        req.on('error', (e) => { if (e.code !== 'ECONNRESET') reject(e); });
    });
}

test('T4: the link is single-use -- the first browser holds the one cookie; a second holder is told it was spent', async () => {
    const view = await mirror.startMirror({ host: '127.0.0.1', cdp: fakeCdp() });
    try {
        assert.ok(view.token.length >= 43, 'a long random token (32 bytes)');
        const origin = `http://127.0.0.1:${view.port}`;
        const wrong = view.token.slice(0, -1) + (view.token.endsWith('A') ? 'B' : 'A');
        for (const p of ['/', '/m/', `/m/${wrong}/`, `/m/${view.token}`, `/m/${view.token}x/`, '/favicon.ico',
            `/m/${view.token}/events`, `/m/${view.token}/input`]) {
            const r = await request(origin + p);
            assert.strictEqual(r.status, 404, `${p.replace(view.token, '<token>')} must be 404, got ${r.status}`);
            assert.strictEqual(r.body, 'Not found', 'and say nothing more');
        }
        // The stream before anyone opened the page: nobody holds it yet.
        assert.strictEqual(await streamStatus(view.url + 'events', null), 404);

        // THE FIRST BROWSER TO OPEN THE LINK gets the one cookie -- a credential
        // the server issued, HttpOnly and SameSite=Strict -- and the link is spent.
        const first = await request(view.url);
        assert.strictEqual(first.status, 200);
        const setCookie = String(first.headers['set-cookie']);
        assert.match(setCookie, /HttpOnly/);
        assert.match(setCookie, /SameSite=Strict/);
        assert.match(first.headers['content-security-policy'], /default-src 'none'/);
        assert.match(first.body, /I'm done, continue/);
        assert.match(first.body, /End the capture/);
        const a = setCookie.split(';')[0];

        // A SECOND HOLDER OF THE LINK -- a transcript, a log, scrollback -- gets
        // nothing: not the page, not a cookie, not the stream, not input. The
        // GENUINE link, spent, says so -- and nothing beyond that -- so a person
        // who reopens it knows why it does not work.
        const second = await request(view.url);
        assert.strictEqual(second.status, 410, 'the link is spent');
        assert.match(second.body,
            /This link was already opened on another device\. Use that device, or the terminal on the PC\./);
        assert.doesNotMatch(second.body, /<script|<form|<a\b|<img|href=|src=/i, 'a page that reveals nothing beyond itself');
        assert.ok(!second.body.includes(view.token), 'not even the token it was asked with');
        assert.match(second.headers['content-security-policy'], /default-src 'none'/);
        assert.strictEqual(second.headers['cache-control'], 'no-store');
        assert.ok(!second.headers['set-cookie'], 'and no cookie');
        // A WRONG or unknown token is still a bare 404, spent link or not.
        for (const p of [`/m/${wrong}/`, '/m/', `/m/${view.token}x/`]) {
            const r = await request(origin + p);
            assert.strictEqual(r.status, 404, `${p.replace(view.token, '<token>')} after the link was spent`);
            assert.strictEqual(r.body, 'Not found');
        }
        // A cookie this server never issued is nobody, whatever its shape.
        const forged = `hm=${'A'.repeat(43)}`;
        assert.strictEqual(await streamStatus(view.url + 'events', forged), 404, 'a forged cookie is nobody');
        assert.strictEqual(await streamStatus(view.url + 'events', 'hm=guess'), 404);
        assert.strictEqual((await post(view, forged, { t: 'text', v: 'x' })).status, 404, 'nor can it type');
        assert.strictEqual((await request(view.url, { headers: { cookie: forged } })).status, 410,
            'a forged cookie on the page gets the spent page, never the view');

        // THE BROWSER THAT HOLDS IT can reload the page and reconnect the
        // stream -- a phone back from another app -- and it alone gives input.
        const again = await request(view.url, { headers: { cookie: a } });
        assert.strictEqual(again.status, 200, 'a reload in the same browser');
        assert.ok(!again.headers['set-cookie'], 'keeps the cookie it has');
        const watch = events(view.url + 'events', a);
        await watch.next('status');
        watch.close();
        await settle();
        const rewatch = events(view.url + 'events', a);
        await rewatch.next('status');
        assert.strictEqual((await post(view, a, { t: 'done' })).status, 204, 'the holder can');
        rewatch.close();
        // Even with the holder's stream closed, the link stays spent.
        await settle();
        assert.strictEqual((await request(view.url)).status, 410);
        assert.strictEqual(await streamStatus(view.url + 'events', forged), 404);
    } finally {
        await view.close();
    }
});

test('T5: frames reach the phone; closing stops the screencast, ends the page and stops listening', async () => {
    const cdp = fakeCdp();
    const view = await mirror.startMirror({ host: '127.0.0.1', cdp,
        status: { state: 'handed-over', text: 'Handed over to you: a check.' } });
    const start = cdp.calls.find((c) => c.method === 'Page.startScreencast');
    assert.ok(start, 'the screencast starts with the view');
    assert.strictEqual(start.params.format, 'jpeg');
    const { cookie, stream } = await claim(view);
    try {
        assert.deepStrictEqual(stream.got[0].data, { state: 'handed-over', text: 'Handed over to you: a check.' });
        cdp.frame(800, 600, '/9j/AAAA', 41);
        assert.deepStrictEqual(await stream.next('frame'), { d: '/9j/AAAA', w: 800, h: 600 });
        await settle();
        assert.ok(cdp.calls.some((c) => c.method === 'Page.screencastFrameAck' && c.params.sessionId === 41),
            'every frame is acknowledged, or Chrome stops sending them');
        view.setStatus({ text: 'still yours' });
        assert.strictEqual((await stream.next('status')).text, 'still yours');
        // The same browser reconnecting (a phone back from another app) is
        // sent the last frame at once.
        const late = events(view.url + 'events', cookie);
        assert.strictEqual((await late.next('frame')).d, '/9j/AAAA');
        late.close();
        assert.ok(cdp.calls.some((c) => c.method === 'Page.bringToFront'), 'the tab is brought on top, so it renders');

        await view.close('The capture carries on. You can close this page.');
        assert.strictEqual((await stream.next('end')).text, 'The capture carries on. You can close this page.');
        assert.ok(cdp.calls.some((c) => c.method === 'Page.stopScreencast'), 'the screencast stops');
        assert.ok(cdp.detached, 'the CDP session is let go');
        const after = await request(view.url).then(() => null, (e) => e);
        assert.ok(after, 'nothing listens once it is closed');
        await view.close();   // twice is harmless
    } finally {
        stream.close();
        await view.close();
    }
});

test('T6: taps, drags, scrolls, typing, keys and Back reach the page in order; the buttons call back', async () => {
    const cdp = fakeCdp();
    const calls = { done: 0, end: 0 };
    const view = await mirror.startMirror({ host: '127.0.0.1', cdp,
        onDone: () => { calls.done++; }, onEnd: () => { calls.end++; } });
    let watching = null;
    try {
        const claimed = await claim(view);
        const cookie = claimed.cookie;
        watching = claimed.stream;
        // Before any frame there is no size to scale a tap by: ignored.
        assert.strictEqual((await post(view, cookie, { t: 'tap', x: 0.5, y: 0.5 })).status, 204);
        await settle();
        assert.ok(!cdp.calls.some((c) => c.method === 'Input.dispatchMouseEvent'));
        cdp.frame(800, 600);
        const mark = cdp.calls.length;
        for (const doc of [
            { t: 'tap', x: 0.5, y: 0.25 },
            { t: 'down', x: 0.1, y: 0.5 }, { t: 'move', x: 0.6, y: 0.5 }, { t: 'up', x: 0.6, y: 0.5 },
            { t: 'scroll', x: 0.5, y: 0.5, dx: 0, dy: 0.5 },
            { t: 'text', v: 'hello' },
            { t: 'key', k: 'Enter' },
            { t: 'back' }
        ]) assert.strictEqual((await post(view, cookie, doc)).status, 204, JSON.stringify(doc));
        await settle();
        const sent = cdp.calls.slice(mark).filter((c) => c.method !== 'Page.screencastFrameAck');
        const brief = sent.map((c) => c.params && c.params.type
            ? `${c.method.split('.')[1]}:${c.params.type}@${c.params.x},${c.params.y}` : c.method.split('.')[1]);
        assert.deepStrictEqual(brief.slice(0, 7), [
            'dispatchMouseEvent:mouseMoved@400,150', 'dispatchMouseEvent:mousePressed@400,150',
            'dispatchMouseEvent:mouseReleased@400,150',
            'dispatchMouseEvent:mouseMoved@80,300', 'dispatchMouseEvent:mousePressed@80,300',
            'dispatchMouseEvent:mouseMoved@480,300', 'dispatchMouseEvent:mouseReleased@480,300'
        ], 'a tap, then a press-drag-release -- what a "Press & Hold" or a slider needs');
        const wheel = sent.find((c) => c.params && c.params.type === 'mouseWheel');
        assert.strictEqual(wheel.params.deltaY, 300, 'half a frame down is 300 CSS px');
        assert.deepStrictEqual(sent.find((c) => c.method === 'Input.insertText').params, { text: 'hello' });
        const keys = sent.filter((c) => c.method === 'Input.dispatchKeyEvent');
        assert.deepStrictEqual(keys.map((k) => k.params.type), ['keyDown', 'keyUp']);
        assert.strictEqual(keys[0].params.text, '\r', 'Enter submits a form, as a keyboard would');
        assert.deepStrictEqual(sent.find((c) => c.method === 'Page.navigateToHistoryEntry').params, { entryId: 7 },
            'Back is the previous history entry');

        // Refused: from another origin, not JSON, a key outside the list, too big.
        const before = cdp.calls.length;
        assert.strictEqual((await post(view, cookie, { t: 'text', v: 'x' }, { origin: 'http://evil.example' })).status, 404);
        assert.strictEqual((await post(view, cookie, { t: 'text', v: 'x' }, { 'content-type': 'text/plain' })).status, 404);
        assert.strictEqual((await post(view, cookie, { t: 'key', k: 'F12' })).status, 400);
        assert.strictEqual((await post(view, cookie, { t: 'text', v: 'x'.repeat(10000) })).status, 413);
        assert.strictEqual((await post(view, 'hm=guess', { t: 'text', v: 'x' })).status, 404, 'not without the claim');
        await settle();
        assert.strictEqual(cdp.calls.slice(before).filter((c) => c.method.startsWith('Input.')).length, 0);

        assert.strictEqual((await post(view, cookie, { t: 'done' })).status, 204);
        assert.strictEqual((await post(view, cookie, { t: 'end' })).status, 204);
        assert.deepStrictEqual(calls, { done: 1, end: 1 });
    } finally {
        if (watching) watching.close();
        await view.close();
    }
});

test('T7: a view that cannot listen leaves nothing running; malformed HTTP is a 404; streams do not pile up', async () => {
    // A port already taken: the view cannot start, and starts nothing.
    const blocker = http.createServer(() => {});
    await new Promise((r) => blocker.listen(0, '127.0.0.1', r));
    const cdp = fakeCdp();
    try {
        await assert.rejects(mirror.startMirror({ host: '127.0.0.1', port: blocker.address().port, cdp }), /EADDRINUSE/);
        assert.ok(!cdp.calls.some((c) => c.method === 'Page.startScreencast') ||
            cdp.calls.some((c) => c.method === 'Page.stopScreencast'), 'no screencast is left running');
        assert.strictEqual(cdp.listenerCount('Page.screencastFrame'), 0, 'no frame listener is left attached');
        assert.ok(cdp.detached, 'the CDP session is let go');
    } finally {
        blocker.close();
    }

    const view = await mirror.startMirror({ host: '127.0.0.1', cdp: fakeCdp() });
    try {
        // Not HTTP at all: the same bare 404 as everything else, not Node's 400.
        const raw = await new Promise((resolve, reject) => {
            const socket = require('net').connect(view.port, '127.0.0.1', () => socket.write('NOT HTTP\r\n\r\n'));
            let got = '';
            socket.setEncoding('utf8');
            socket.on('data', (d) => { got += d; });
            socket.on('end', () => resolve(got));
            socket.on('error', reject);
        });
        assert.match(raw, /^HTTP\/1\.1 404 /, raw);

        // ONE BROWSER, NOT A PILE OF STREAMS: the holder reconnecting again and
        // again keeps a few live streams at most -- the oldest go.
        const { cookie, stream } = await claim(view);
        const more = [];
        for (let i = 0; i < 6; i++) {
            const s = events(view.url + 'events', cookie);
            await s.next('status');
            more.push(s);
        }
        await settle();
        assert.ok(view.clients() <= mirror.MAX_STREAMS, `${view.clients()} live streams for one browser`);
        assert.strictEqual(view.clients(), mirror.MAX_STREAMS);
        for (const s of [stream, ...more]) s.close();
    } finally {
        await view.close();
    }
});

// ---------------------------------------------------------------------------
// E1 -- a real browser
// ---------------------------------------------------------------------------

function integrationRequired(ciValue) {
    if (ciValue === undefined || ciValue === null) return false;
    const value = String(ciValue).trim();
    return !!value && value.toLowerCase() !== 'false';
}

function browserBlocker() {
    const capture = require(path.join(__dirname, 'capture-har.js'));
    const env = Object.assign({}, process.env,
        { NODE_PATH: process.env.HAR_CAPTURE_TEST_NODE_PATH || process.env.NODE_PATH || '' });
    const resolved = capture.resolvePlaywright({ cwd: process.cwd(), env });
    if (!resolved.found) return { why: 'the playwright module is not installed' };
    const chrome = capture.findChromeExecutable();
    if (!chrome.path) return { why: 'Google Chrome is not installed' };
    return { playwright: require(resolved.path), chrome: chrome.path };
}

test('E1: a real page reaches the phone view, and a tap and typing from it land in that page', async () => {
    const found = browserBlocker();
    if (found.why) {
        if (integrationRequired(process.env.CI)) throw new Error(`E1 cannot run: ${found.why} -- in CI that is a failure`);
        skipped.push(`E1 (${found.why})`);
        return;
    }
    // Headless, in a test only: the product never launches this browser.
    const browser = await found.playwright.chromium.launch({ executablePath: found.chrome, headless: true });
    let view = null;
    try {
        const page = await browser.newPage({ viewport: { width: 400, height: 300 } });
        await page.setContent('<html><body style="margin:0">' +
            '<button id="b" style="position:absolute;left:0;top:0;width:200px;height:100px" ' +
            'onclick="document.title=\'tapped\'">tap me</button>' +
            '<input id="i" style="position:absolute;left:200px;top:150px;width:200px;height:50px">' +
            '</body></html>');
        const cdp = await page.context().newCDPSession(page);
        view = await mirror.startMirror({ host: '127.0.0.1', cdp });
        const { cookie, stream } = await claim(view);
        try {
            const frame = await stream.next('frame', 15000);
            assert.ok(frame.d.startsWith('/9j/'), 'a JPEG of the live page');
            assert.deepStrictEqual([frame.w, frame.h], [400, 300], 'sized in CSS pixels, so a tap lands where it was aimed');
            // The button fills the top-left quarter: tap its middle.
            await post(view, cookie, { t: 'tap', x: 0.25, y: 1 / 6 });
            await page.waitForFunction(() => document.title === 'tapped', null, { timeout: 10000 });
            // Tap the field, then type into it -- Input.insertText, as a phone keyboard.
            await post(view, cookie, { t: 'tap', x: 0.75, y: 175 / 300 });
            await post(view, cookie, { t: 'text', v: 'typed on a phone' });
            await page.waitForFunction(() => document.getElementById('i').value === 'typed on a phone', null,
                { timeout: 10000 });
        } finally {
            stream.close();
        }
    } finally {
        if (view) await view.close();
        await browser.close();
    }
});

test('E2: the phone page itself, in a real browser: it shows the live page, and its taps, typing and button work', async () => {
    // E1 plays the phone over HTTP. This runs the PAGE'S OWN SCRIPT -- the
    // EventSource, the pointer handling, the text box, "I'm done, continue" --
    // in a real browser at a phone's size, against a real mirrored page.
    const found = browserBlocker();
    if (found.why) {
        if (integrationRequired(process.env.CI)) throw new Error(`E2 cannot run: ${found.why} -- in CI that is a failure`);
        skipped.push(`E2 (${found.why})`);
        return;
    }
    const browser = await found.playwright.chromium.launch({ executablePath: found.chrome, headless: true });
    let view = null;
    try {
        const site = await browser.newPage({ viewport: { width: 400, height: 300 } });
        await site.setContent('<html><body style="margin:0">' +
            '<button id="b" style="position:absolute;left:0;top:0;width:200px;height:100px" ' +
            'onclick="document.title=\'tapped\'">tap me</button>' +
            '<input id="i" style="position:absolute;left:200px;top:150px;width:200px;height:50px">' +
            '<input id="r" type="range" min="0" max="100" value="0" ' +
            'style="position:absolute;left:10px;top:240px;width:380px;height:40px;margin:0">' +
            '</body></html>');
        let done = 0;
        view = await mirror.startMirror({ host: '127.0.0.1', cdp: await site.context().newCDPSession(site),
            status: { state: 'handed-over', text: 'Handed over to you: a check.' }, onDone: () => { done++; } });
        // The phone: its own context (its own cookies), at a phone's width.
        const phoneContext = await browser.newContext({ viewport: { width: 390, height: 800 }, hasTouch: true, isMobile: true });
        const phone = await phoneContext.newPage();
        await phone.goto(view.url);
        await phone.waitForFunction(() => document.getElementById('status').textContent.includes('Handed over to you'),
            null, { timeout: 15000 });
        await phone.waitForFunction(() => (document.getElementById('screen').src || '').startsWith('data:image/jpeg'),
            null, { timeout: 15000 });
        const box = await phone.locator('#screen').boundingBox();
        // A TOUCH tap (a touch screen, a mobile viewport) on the picture of the
        // button lands on the button.
        await phone.touchscreen.tap(box.x + box.width * 0.25, box.y + box.height / 6);
        await site.waitForFunction(() => document.title === 'tapped', null, { timeout: 10000 });
        // Tap the picture of the field, type in the phone's text box, Send.
        await phone.touchscreen.tap(box.x + box.width * 0.75, box.y + box.height * (175 / 300));
        await phone.fill('#txt', 'from the phone page');
        await phone.click('#send');
        await site.waitForFunction(() => document.getElementById('i').value === 'from the phone page', null,
            { timeout: 10000 });
        // PRESS & DRAG mode: a press, a drag and a release -- what a slider
        // check needs -- move the page's slider from one end to the other.
        await phone.click('#mode');
        assert.strictEqual(await phone.textContent('#mode'), 'Press & drag');
        const y = box.y + box.height * (260 / 300);
        await phone.mouse.move(box.x + box.width * (14 / 400), y);
        await phone.mouse.down();
        for (let i = 1; i <= 8; i++) {
            await phone.mouse.move(box.x + box.width * ((14 + i * 47) / 400), y);
            await new Promise((r) => setTimeout(r, 60));
        }
        await phone.mouse.up();
        await site.waitForFunction(() => Number(document.getElementById('r').value) > 80, null, { timeout: 10000 });
        // A BUTTON WHOSE MESSAGE DID NOT GET THERE says so, and can be tapped
        // again -- not "Handing the browser back..." for ever.
        await phone.route('**/input', (r) => r.fulfill({ status: 500, body: '' }));
        await phone.click('#done');
        await phone.waitForFunction(() => document.getElementById('status').textContent.includes('did not reach the capture'),
            null, { timeout: 10000 });
        assert.strictEqual(await phone.isDisabled('#done'), false, 'the button can be tapped again');
        assert.strictEqual(done, 0);
        await phone.unroute('**/input');
        await phone.click('#done');
        const deadline = Date.now() + 10000;
        while (!done && Date.now() < deadline) await settle();
        assert.strictEqual(done, 1, '"I\'m done, continue" reached the recorder');
        await view.close('Handed back.');
        await phone.waitForFunction(() => !document.getElementById('over').hidden, null, { timeout: 10000 });
        assert.match(await phone.textContent('#over-msg'), /Handed back/);
    } finally {
        if (view) await view.close();
        await browser.close();
    }
});

(async () => {
    let passed = 0;
    const failures = [];
    for (const { name, fn } of queued) {
        try { await fn(); passed++; } catch (e) {
            failures.push(name);
            process.stderr.write(`FAIL: ${name}\n  ${e.message}\n`);
            if (e.stack) process.stderr.write(e.stack.split('\n').slice(1, 4).join('\n') + '\n');
        }
    }
    if (failures.length) {
        process.stderr.write(`\n${failures.length} of ${queued.length} mirror tests FAILED:\n` +
            failures.map((f) => `  - ${f}\n`).join(''));
        process.exit(1);
    }
    console.log(`All mirror tests passed (${passed})` + (skipped.length ? ` -- SKIPPED: ${skipped.join(', ')}` : ''));
    process.exit(0);
})();
