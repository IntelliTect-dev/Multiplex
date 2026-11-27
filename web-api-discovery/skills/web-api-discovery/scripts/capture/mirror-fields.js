'use strict';

/**
 * mirror-fields.js -- the text boxes on screen, for the phone view (#155).
 *
 * The phone view (mirror.js) is a picture of the capture tab: there is no
 * text box in it to type into. So the recorder tells the phone where the
 * page's own text boxes are, and the phone lays a REAL one over each -- with
 * the same type -- so tapping it opens the phone's own keyboard there, and
 * the phone's password manager can fill it. What is typed is replayed into
 * the box underneath as key presses (mirror.js).
 *
 * WHAT IS LISTED: a text box a person could tap and type in right now -- an
 * <input> of a text type, not disabled or read-only, drawn, inside the window,
 * and not covered by anything but its own label. In every frame of the page,
 * a cross-origin one included (Playwright reaches its renderer), placed where
 * it is on the page and clipped to its frame.
 *
 * WHAT IS NEVER READ: a password's value. Its length is read -- whether the
 * box already holds something decides whether the phone clears it before
 * typing -- and nothing more. Any other box's value is what the box shows on
 * the screen, which the phone already sees in the picture.
 *
 * WHAT EACH BOX IS FOR (`kind`), so the phone's box gets the type and
 * `autocomplete` a password manager looks for:
 *   password   type=password
 *   otp        autocomplete=one-time-code, or a name/id saying otp/totp
 *   username   autocomplete=username; or an email/name box beside a password,
 *              or on a sign-in address -- the same evidence sign-in.js asks for
 *   email      an email box on its own (a newsletter is not a sign-in)
 *   tel, number, text
 *
 * Zero dependencies: `page` is a Playwright Page.
 */

// Whole PATH SEGMENTS, the same rule as sign-in.js: `/blog-insights` is not a
// sign-in, `/signin` and `/ap/signin` are.
const SIGN_IN_PATH_SOURCE = '(^|\\/)(log-?in|sign-?in|sso|oauth2?|authorize|auth|identifier)(\\/|$|[-_.])';
const MAX_VALUE = 512;
const MAX_LABEL = 60;
const FRAME_TIMEOUT_MS = 1500;

/**
 * Runs IN THE PAGE, once per frame. Returns the boxes in this frame's own
 * viewport coordinates, and the viewport's size.
 */
/* istanbul ignore next -- browser-side */
function collectInFrame(args) {
    const TEXT_TYPES = ['', 'text', 'email', 'password', 'tel', 'number', 'search', 'url'];
    const INPUT_MODES = ['text', 'numeric', 'decimal', 'tel', 'email', 'url', 'search'];
    const signInPath = new RegExp(args.signInPath, 'i').test(location.pathname);
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const boxes = [];
    const roots = [document];
    // Open shadow roots: a sign-in widget is often a web component.
    for (let i = 0; i < roots.length; i++) {
        for (const el of roots[i].querySelectorAll('*')) if (el.shadowRoot) roots.push(el.shadowRoot);
    }
    const inputs = [];
    for (const root of roots) for (const el of root.querySelectorAll('input')) inputs.push(el);
    const hasPassword = inputs.some((el) => (el.getAttribute('type') || '').toLowerCase() === 'password');
    const named = (el, re) => re.test(el.name || '') || re.test(el.id || '');
    const tokens = (el) => (el.getAttribute('autocomplete') || '').toLowerCase().split(/\s+/).filter(Boolean);
    const onTop = (el, x, y) => {
        const root = el.getRootNode();
        const hit = (root.elementFromPoint ? root : document).elementFromPoint(x, y);
        if (!hit) return false;
        if (hit === el || el.contains(hit)) return true;
        // Its own label drawn over it (a "floating label") is still the box.
        const label = hit.closest && hit.closest('label');
        return !!(label && label.control === el);
    };
    for (const el of inputs) {
        const type = (el.getAttribute('type') || '').toLowerCase();
        if (!TEXT_TYPES.includes(type) || el.disabled || el.readOnly) continue;
        const r = el.getBoundingClientRect();
        if (r.width < 4 || r.height < 4) continue;
        if (r.right <= 0 || r.bottom <= 0 || r.left >= vw || r.top >= vh) continue;
        const style = getComputedStyle(el);
        if (style.visibility !== 'visible' || Number(style.opacity) < 0.05) continue;
        const cy = Math.min(vh - 1, Math.max(0, r.top + r.height / 2));
        const xs = [r.left + r.width / 2, r.left + Math.min(r.width / 4, 24)]
            .map((x) => Math.min(vw - 1, Math.max(0, x)));
        if (!xs.some((x) => onTop(el, x, cy))) continue;
        const ac = tokens(el);
        let kind;
        if (type === 'password') kind = 'password';
        else if (ac.includes('one-time-code') || named(el, /otp|totp/i)) kind = 'otp';
        else if (ac.includes('username')) kind = 'username';
        else if (type === 'email' || ac.includes('email')) kind = hasPassword || signInPath ? 'username' : 'email';
        else if ((hasPassword || signInPath) && named(el, /user|email|login/i)) kind = 'username';
        else if (type === 'tel') kind = 'tel';
        else if (type === 'number') kind = 'number';
        else kind = 'text';
        const label = (el.getAttribute('aria-label') || el.getAttribute('placeholder') ||
            (el.labels && el.labels[0] && el.labels[0].textContent) || '').replace(/\s+/g, ' ').trim();
        const box = {
            x: r.left, y: r.top, w: r.width, h: r.height,
            kind,
            type: type || 'text',
            length: el.value.length,
            maxLength: el.maxLength > 0 ? Math.min(el.maxLength, args.maxValue) : args.maxValue,
            label: label.slice(0, args.maxLabel)
        };
        if (INPUT_MODES.includes(el.inputMode)) box.inputmode = el.inputMode;
        if (ac.includes('new-password')) box.newPassword = true;
        // NEVER a password's value: only whether it holds something.
        if (kind !== 'password') box.value = el.value.slice(0, args.maxValue);
        boxes.push(box);
    }
    return { vw, vh, boxes };
}

/** Where a child frame's content starts on the page, and the box it is clipped to. */
async function frameWindow(frame) {
    const el = await frame.frameElement();
    try {
        const box = await el.boundingBox();
        if (!box) return null;
        const inset = await el.evaluate((e) => {
            const s = getComputedStyle(e);
            return { l: e.clientLeft + parseFloat(s.paddingLeft || '0'), t: e.clientTop + parseFloat(s.paddingTop || '0') };
        });
        return { x: box.x + inset.l, y: box.y + inset.t, right: box.x + box.width, bottom: box.y + box.height };
    } finally {
        await el.dispose().catch(() => {});
    }
}

function withTimeout(promise, ms) {
    let timer = null;
    return Promise.race([
        promise,
        new Promise((resolve) => { timer = setTimeout(() => resolve(null), ms); })
    ]).finally(() => clearTimeout(timer));
}

/**
 * The text boxes on screen in `page`, in the main frame's viewport (CSS
 * pixels, the size the screencast reports), and that viewport's size.
 *
 * @returns {Promise<{vw: number, vh: number, fields: Array<object>}>}
 */
async function readFields(page) {
    const args = { signInPath: SIGN_IN_PATH_SOURCE, maxValue: MAX_VALUE, maxLabel: MAX_LABEL };
    const main = page.mainFrame();
    const top = await main.evaluate(collectInFrame, args);
    const fields = top.boxes.slice();
    for (const frame of page.frames()) {
        if (frame === main || frame.isDetached()) continue;
        // A frame that does not answer (navigating, hung) is left out this
        // time; the next read looks again.
        const read = await withTimeout((async () => {
            const win = await frameWindow(frame);
            if (!win) return null;
            const inner = await frame.evaluate(collectInFrame, args);
            return { win, inner };
        })().catch(() => null), FRAME_TIMEOUT_MS);
        if (!read) continue;
        const { win, inner } = read;
        const right = Math.min(win.right, top.vw);
        const bottom = Math.min(win.bottom, top.vh);
        for (const b of inner.boxes) {
            const x = win.x + b.x;
            const y = win.y + b.y;
            // Clipped to the frame's window on the page: a box scrolled out
            // of its frame is not on screen, whatever its own frame thinks.
            const cx = x + b.w / 2;
            const cy = y + b.h / 2;
            if (cx < win.x || cy < win.y || cx >= right || cy >= bottom) continue;
            fields.push(Object.assign({}, b, { x, y }));
        }
    }
    return { vw: top.vw, vh: top.vh, fields };
}

module.exports = { readFields, SIGN_IN_PATH_SOURCE, MAX_VALUE };
