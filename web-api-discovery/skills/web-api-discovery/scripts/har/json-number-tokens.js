'use strict';

/**
 * json-number-tokens.js -- replace inside JSON NUMBERS without breaking JSON
 * (issues #23, #68).
 *
 * Two scrub passes work on text: the literal pass swaps an operator's own
 * identifiers for `<Sentinel>`s, and the substitution sweep swaps originals
 * for fakes. Both are blind to JSON structure, which is right for everything
 * they cover -- prose, headers, HTML, URLs -- and wrong for exactly one case:
 * a value written as a bare JSON NUMBER. `"userID":100012345678` became
 * `"userID":<FacebookUserId>`, and the body no longer parsed. The #21 runner
 * measured it on real captures: 4 and 15 Facebook bodies, one Instagram
 * request.
 *
 * WHY THIS IS STRUCTURAL. The first fix looked at the punctuation around a
 * match (`:` before, `,` after) and quoted the sentinel. It corrupted prose
 * (`Name: 123, Age: 5`) and CSV (`a,123,c`), because punctuation is not
 * structure. Here nothing is decided from punctuation: a text is treated as
 * JSON only if it PARSES as JSON, and then its number tokens are found by a
 * tokenizer that skips strings. Text that does not parse is left for the
 * blind passes, exactly as before.
 *
 * WHAT HAPPENS TO A NUMBER. A number token that contains a needle -- equal to
 * it, or merely containing its digits -- becomes a JSON STRING with the
 * needle replaced: `100012345678` -> `"<FacebookUserId>"`, `91000123456789`
 * -> `"9<FacebookUserId>9"`. The value's type changes from number to string;
 * it is a redaction marker either way, and a document that parses is worth
 * more than one that keeps a type and cannot be read.
 *
 * Runs BEFORE the blind passes, on the parsed entry, so by the time they run
 * there is no bare number left for them to break.
 */

// One JSON number, from the grammar: optional minus, integer part without a
// leading zero, optional fraction, optional exponent.
const NUMBER = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;

// The anti-hijacking prefix some providers put in front of a JSON response.
const XSSI_PREFIX = /^\s*for\s*\(;;\);/;

function parses(text) {
    try { JSON.parse(text); return true; } catch { return false; }
}

function looksLikeJson(text) {
    const t = text.trimStart();
    return t.startsWith('{') || t.startsWith('[');
}

/**
 * Rewrite every number token in `text` -- which must be valid JSON -- that
 * contains a needle. `needles` is a list of `{ needle, replacement }`, longest
 * first. Returns the text unchanged (same string) when nothing matched.
 */
function rewriteNumbers(text, needles) {
    let out = null;
    let last = 0;
    for (let i = 0; i < text.length;) {
        const c = text.charCodeAt(i);
        if (c === 0x22) { // a string: skip it whole, escapes included
            i++;
            while (i < text.length) {
                const d = text.charCodeAt(i);
                if (d === 0x5c) { i += 2; continue; }
                i++;
                if (d === 0x22) break;
            }
            continue;
        }
        if (c === 0x2d || (c >= 0x30 && c <= 0x39)) {
            NUMBER.lastIndex = i;
            const m = NUMBER.exec(text);
            if (!m) { i++; continue; }
            const token = m[0];
            let replaced = token;
            for (const { needle, replacement } of needles) {
                if (replaced.includes(needle)) replaced = replaced.split(needle).join(replacement);
            }
            if (replaced !== token) {
                if (out === null) out = [];
                out.push(text.slice(last, i), JSON.stringify(replaced));
                last = i + token.length;
            }
            i += token.length;
            continue;
        }
        i++;
    }
    if (out === null) return text;
    out.push(text.slice(last));
    return out.join('');
}

/** A JSON text, or a JSON text behind the `for (;;);` prefix, rewritten. */
function rewriteJsonText(text, needles) {
    const prefix = XSSI_PREFIX.exec(text);
    const rest = prefix ? text.slice(prefix[0].length) : text;
    if (looksLikeJson(rest) && parses(rest)) {
        const next = rewriteNumbers(rest, needles);
        return next === rest ? text : (prefix ? prefix[0] : '') + next;
    }
    // Line-delimited JSON: every non-empty line a JSON document.
    if (!prefix && text.includes('\n')) {
        const lines = text.split('\n');
        if (lines.every((l) => !l.trim() || (looksLikeJson(l) && parses(l)))) {
            const next = lines.map((l) => (l.trim() ? rewriteNumbers(l, needles) : l));
            return next.every((l, i) => l === lines[i]) ? text : next.join('\n');
        }
    }
    return text;
}

function decode(value) {
    try { return decodeURIComponent(value.replace(/\+/g, ' ')); } catch { return null; }
}

/**
 * A form-encoded body whose field values carry JSON (`variables=...`). A field
 * that changed is re-encoded; one that did not is left byte for byte, because
 * decode-then-encode is not the identity and the body is a record.
 */
function rewriteFormText(text, needles) {
    if (!text.includes('=') || /\s/.test(text.trim())) return text;
    const parts = text.split('&');
    let changed = false;
    const next = parts.map((part) => {
        const eq = part.indexOf('=');
        if (eq <= 0) return part;
        const value = decode(part.slice(eq + 1));
        if (value === null || !looksLikeJson(value)) return part;
        const rewritten = rewriteJsonText(value, needles);
        if (rewritten === value) return part;
        changed = true;
        return `${part.slice(0, eq + 1)}${encodeURIComponent(rewritten)}`;
    });
    return changed ? next.join('&') : text;
}

/** A `params[]` value, which HAR stores still percent-encoded. */
function rewriteParamValue(value, needles) {
    if (looksLikeJson(value)) return rewriteJsonText(value, needles);
    const decoded = decode(value);
    if (decoded === null || !looksLikeJson(decoded)) return value;
    const rewritten = rewriteJsonText(decoded, needles);
    return rewritten === decoded ? value : encodeURIComponent(rewritten);
}

/**
 * A URL whose query parameters carry JSON (GraphQL over GET sends
 * `?variables=...`). Only the query is read; a parameter that changed is
 * re-encoded, the rest of the URL is left byte for byte.
 */
function rewriteUrl(url, needles) {
    const q = url.indexOf('?');
    if (q === -1) return url;
    const hash = url.indexOf('#', q);
    const end = hash === -1 ? url.length : hash;
    const query = url.slice(q + 1, end);
    const next = rewriteFormText(query, needles);
    return next === query ? url : url.slice(0, q + 1) + next + url.slice(end);
}

/**
 * Rewrite, in place, the numbers matching `needles` in one entry: the URL's
 * query and its `queryString[]` twins, the request body (as JSON, or as a form
 * whose fields carry JSON), its `params[]` twins, and the response body
 * unless it is base64.
 *
 * @param {Array<{needle: string, replacement: string}>} needles
 */
function rewriteEntryNumbers(entry, needles) {
    if (!needles || needles.length === 0 || !entry || typeof entry !== 'object') return;
    const req = entry.request;
    if (req && typeof req.url === 'string') req.url = rewriteUrl(req.url, needles);
    if (req && Array.isArray(req.queryString)) {
        for (const p of req.queryString) {
            if (p && typeof p.value === 'string') p.value = rewriteParamValue(p.value, needles);
        }
    }
    const post = req && req.postData;
    if (post && typeof post.text === 'string') {
        const asJson = rewriteJsonText(post.text, needles);
        post.text = asJson !== post.text ? asJson : rewriteFormText(post.text, needles);
    }
    if (post && Array.isArray(post.params)) {
        for (const p of post.params) {
            if (p && typeof p.value === 'string') p.value = rewriteParamValue(p.value, needles);
        }
    }
    const content = entry.response && entry.response.content;
    if (content && typeof content.text === 'string' && content.encoding !== 'base64') {
        content.text = rewriteJsonText(content.text, needles);
    }
}

/**
 * The needles, longest first so a longer needle is replaced before a shorter
 * one it contains. Only a needle that can occur inside a number is kept.
 */
function numericNeedles(pairs) {
    return pairs
        .filter(({ needle }) => /\d/.test(needle) && /^[-+.eE\d]+$/.test(needle))
        .sort((a, b) => b.needle.length - a.needle.length);
}

module.exports = { rewriteNumbers, rewriteJsonText, rewriteEntryNumbers, numericNeedles };
