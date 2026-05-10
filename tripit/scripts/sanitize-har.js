#!/usr/bin/env node
/**
 * Sanitize HAR files for public publication.
 *
 * Aggressive scrub:
 *   - All cookie values (request/response cookie arrays + Cookie/Set-Cookie headers).
 *   - Sensitive header values (auth, csrf, correlation, server-side identifiers).
 *   - Sensitive URL query-string parameter values.
 *   - All POST request body field values (form or JSON).
 *   - All string leaf values inside JSON response bodies (keeps keys + structure
 *     so the file remains useful as API documentation).
 *   - Non-JSON / non-text response bodies are replaced with a placeholder.
 *   - Final regex sweep redacts UUIDs, emails, phones, JWTs, and long hex tokens
 *     anywhere in the output (URL paths, custom DevTools fields, etc.).
 *
 * Usage:
 *   node scripts/sanitize-har.js <input.har> <output.har>
 *   node scripts/sanitize-har.js --dir <input-dir> <output-dir>
 */

'use strict';

const fs = require('node:fs');
const path = require('node:path');

const SENSITIVE_HEADERS = new Set([
  'cookie', 'set-cookie', 'authorization', 'proxy-authorization',
  'x-csrf-token', 'x-csrf-token-wa', 'x-xsrf-token',
  'it_csrf', 'it_wa_csrf',
  'x-tid', 'x-account-id', 'x-consumer-id', 'x-request-id',
  'x-amz-cf-id', 'x-amz-cf-pop',
  'x-amz-meta-companyid', 'x-amz-meta-source', 'x-amz-meta-templateid',
  'x-amz-meta-url', 'x-amz-meta-version', 'x-amz-version-id',
  'x-amz-replication-status', 'x-amz-server-side-encryption',
  'x-msedge-ref', 'x-served-by', 'x-fb-debug', 'x-aam-tid',
  'x-dtpc', 'x-dtreferer', 'concur-correlationid',
  'cf-ray', 'cdn-uid', 'cdn-requestid',
  'etag', 'if-none-match', 'if-modified-since',
]);

const SENSITIVE_QUERY_PARAMS = new Set([
  'uuid', 'trip_uuid', 'id', 'user', 'username', 'user_id',
  'email', 'account_id', 'account', 'token', 'auth', 'q', 'query',
]);

const CUSTOM_FIELDS_TO_DROP = new Set([
  '_initiator', '_initiator_detail', '_priority', '_resourceType',
  '_webSocketMessages', '_fromCache', '_connectionId',
]);

const UUID_RE    = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;
const EMAIL_RE   = /[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}/g;
const PHONE_RE   = /\b\d{3}[-.\s]\d{3}[-.\s]\d{4}\b/g;
const LONG_HEX_RE= /\b[0-9a-f]{32,}\b/gi;
const JWT_RE     = /eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]+/g;

function placeholder(name) {
  if (!name) return '<STRING>';
  return '<' + String(name).replace(/-/g, '_').toUpperCase() + '>';
}

function scrubHeaders(headers) {
  if (!Array.isArray(headers)) return headers;
  for (const h of headers) {
    if (h && typeof h.name === 'string'
        && SENSITIVE_HEADERS.has(h.name.toLowerCase())) {
      h.value = placeholder(h.name);
    }
  }
  return headers;
}

function scrubCookies(cookies) {
  if (!Array.isArray(cookies)) return cookies;
  for (const c of cookies) {
    if (c) c.value = '<COOKIE_VALUE>';
  }
  return cookies;
}

function scrubUrl(url) {
  if (typeof url !== 'string') return url;
  const qIdx = url.indexOf('?');
  if (qIdx < 0) return url;
  const base = url.slice(0, qIdx);
  let rest = url.slice(qIdx + 1);
  let fragment = '';
  const hashIdx = rest.indexOf('#');
  if (hashIdx >= 0) {
    fragment = rest.slice(hashIdx);
    rest = rest.slice(0, hashIdx);
  }
  const newPairs = rest.split('&').map((pair) => {
    if (!pair) return pair;
    const eq = pair.indexOf('=');
    if (eq < 0) return pair;
    const k = pair.slice(0, eq);
    let kDecoded;
    try { kDecoded = decodeURIComponent(k); } catch { kDecoded = k; }
    if (SENSITIVE_QUERY_PARAMS.has(kDecoded.toLowerCase())) {
      return `${k}=${encodeURIComponent(placeholder(kDecoded))}`;
    }
    return pair;
  });
  return `${base}?${newPairs.join('&')}${fragment}`;
}

function scrubJsonLeaves(value, key) {
  if (value === null || value === undefined) return value;
  if (Array.isArray(value)) {
    return value.map((v) => scrubJsonLeaves(v, key));
  }
  if (typeof value === 'object') {
    const out = {};
    for (const k of Object.keys(value)) {
      out[k] = scrubJsonLeaves(value[k], k);
    }
    return out;
  }
  if (typeof value === 'string') {
    if (value.length === 0) return value;
    return placeholder(key);
  }
  // numbers, booleans — keep
  return value;
}

function scrubPostData(post) {
  if (!post || typeof post !== 'object') return post;
  const mime = String(post.mimeType || '').toLowerCase();

  if (Array.isArray(post.params)) {
    for (const p of post.params) {
      if (p && 'value' in p) p.value = placeholder(p.name || 'value');
    }
  }

  if (typeof post.text !== 'string') return post;

  if (mime.includes('json')) {
    try {
      const parsed = JSON.parse(post.text);
      post.text = JSON.stringify(scrubJsonLeaves(parsed));
      return post;
    } catch { /* fall through */ }
  }

  if (mime.includes('x-www-form-urlencoded')) {
    try {
      const out = post.text.split('&').map((pair) => {
        if (!pair) return pair;
        const eq = pair.indexOf('=');
        if (eq < 0) return `${pair}=`;
        const k = pair.slice(0, eq);
        let kDecoded;
        try { kDecoded = decodeURIComponent(k); } catch { kDecoded = k; }
        return `${k}=${encodeURIComponent(placeholder(kDecoded))}`;
      });
      post.text = out.join('&');
      return post;
    } catch { /* fall through */ }
  }

  post.text = '<POST_BODY_REDACTED>';
  return post;
}

function scrubResponseContent(content) {
  if (!content || typeof content !== 'object') return content;
  if (typeof content.text !== 'string') return content;
  const mime = String(content.mimeType || '').toLowerCase();

  if (mime.includes('json')) {
    try {
      const parsed = JSON.parse(content.text);
      content.text = JSON.stringify(scrubJsonLeaves(parsed));
      return content;
    } catch { /* fall through */ }
  }

  content.text = '<RESPONSE_BODY_REDACTED>';
  delete content.encoding;
  return content;
}

function dropCustomFields(obj) {
  if (obj === null || obj === undefined) return obj;
  if (Array.isArray(obj)) return obj.map(dropCustomFields);
  if (typeof obj !== 'object') return obj;
  const out = {};
  for (const k of Object.keys(obj)) {
    if (CUSTOM_FIELDS_TO_DROP.has(k)) continue;
    out[k] = dropCustomFields(obj[k]);
  }
  return out;
}

function sanitizeEntry(entry) {
  entry = dropCustomFields(entry);

  const req = entry.request;
  if (req) {
    if (typeof req.url === 'string') req.url = scrubUrl(req.url);
    if (req.headers) req.headers = scrubHeaders(req.headers);
    if (req.cookies) req.cookies = scrubCookies(req.cookies);
    if (Array.isArray(req.queryString)) {
      for (const q of req.queryString) {
        if (q && typeof q.name === 'string'
            && SENSITIVE_QUERY_PARAMS.has(q.name.toLowerCase())) {
          q.value = placeholder(q.name);
        }
      }
    }
    if (req.postData) req.postData = scrubPostData(req.postData);
  }

  const res = entry.response;
  if (res) {
    if (res.headers) res.headers = scrubHeaders(res.headers);
    if (res.cookies) res.cookies = scrubCookies(res.cookies);
    if (typeof res.redirectURL === 'string' && res.redirectURL) {
      res.redirectURL = scrubUrl(res.redirectURL);
    }
    if (res.content) res.content = scrubResponseContent(res.content);
  }

  return entry;
}

function sanitizeHar(inPath, outPath) {
  const har = JSON.parse(fs.readFileSync(inPath, 'utf8'));
  if (!har.log) throw new Error(`Invalid HAR (missing 'log'): ${inPath}`);

  if ('pages' in har.log) har.log.pages = [];
  if (Array.isArray(har.log.entries)) {
    har.log.entries = har.log.entries.map(sanitizeEntry);
  }

  let text = JSON.stringify(har, null, 2);

  // Catch-all regex sweep — defends against secrets in URL path segments,
  // custom HAR fields, embedded stack traces, or anywhere structural scrub
  // missed.
  text = text.replace(JWT_RE,     '<JWT>');
  text = text.replace(UUID_RE,    '<UUID>');
  text = text.replace(EMAIL_RE,   '<EMAIL>');
  text = text.replace(PHONE_RE,   '<PHONE>');
  text = text.replace(LONG_HEX_RE,'<HEX>');

  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  fs.writeFileSync(outPath, text, 'utf8');
}

function main(argv) {
  const args = argv.slice(2);
  let dirMode = false;
  if (args[0] === '--dir') { dirMode = true; args.shift(); }
  if (args.length !== 2) {
    console.error('usage: node scripts/sanitize-har.js [--dir] <src> <dst>');
    process.exit(2);
  }
  const [src, dst] = args;

  if (dirMode) {
    fs.mkdirSync(dst, { recursive: true });
    const files = fs.readdirSync(src)
      .filter((n) => n.toLowerCase().endsWith('.har'))
      .sort();
    for (const name of files) {
      const inPath  = path.join(src, name);
      const outPath = path.join(dst, name);
      console.error(`sanitizing ${inPath} -> ${outPath}`);
      sanitizeHar(inPath, outPath);
    }
  } else {
    sanitizeHar(src, dst);
  }
}

main(process.argv);
