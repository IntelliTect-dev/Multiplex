// Extract a fresh TripIt session (Cookie + X-CSRF-Token-WA) for the .NET client,
// MCP server and PowerShell module.
//
// Strategy:
//   1. Attach to a Chrome already listening on the CDP port; otherwise spawn one on
//      the capture profile (shared skill profile by default), so the user is already
//      logged in.
//   2. Read the session cookies from the browser CONTEXT -- not from an intercepted
//      request. Chrome attaches the Cookie header after Playwright surfaces a request,
//      so `request.headers()['cookie']` is empty and the httpOnly cookies that actually
//      carry the session (session_id, it_session_id, it_csrf) never appear there at all.
//   3. Observe one /api/v2/* XHR to learn the SPA's own header set, and to cross-check
//      the CSRF token against the it_wa_csrf cookie.
//   4. Save to ~/.tripit-mcp/session.json.
//
// Re-run this any time the client reports a 302/401 (session expired).

const { chromium } = require('playwright');
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const http = require('http');
const os = require('os');

const CHROME_EXE = process.env.TRIPIT_CHROME_EXE
  || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const DEFAULT_DEBUG_PORT = Number(process.env.TRIPIT_CDP_PORT || 9222);
const OUT_FILE = process.env.TRIPIT_SESSION_FILE
  || path.join(os.homedir(), '.tripit-mcp', 'session.json');
const START_URL = 'https://www.tripit.com/app/trips';
const ORIGIN = 'https://www.tripit.com';

// The session cookies that must be present for a capture to be worth saving. Anything
// else in the jar is analytics or bot-management state.
const REQUIRED_COOKIES = ['session_id', 'it_session_id'];

// Header names that are per-session secrets; their VALUES are never logged.
const SECRET_HEADERS = new Set(['cookie', 'x-csrf-token-wa']);

/**
 * The capture profile, in precedence order. A PATH is used as given; a bare NAME lands
 * beside the other capture profiles. Defaults to the shared web-api-discovery profile so
 * one browser sign-in serves every project, matching that skill's own default.
 */
function resolveProfileDir() {
  const argIdx = process.argv.indexOf('--profile');
  const explicit = (argIdx !== -1 && process.argv[argIdx + 1])
    || process.env.TRIPIT_CAPTURE_PROFILE
    || process.env.HAR_CAPTURE_PROFILE;
  const root = path.join(
    process.env.LOCALAPPDATA || process.env.XDG_DATA_HOME
      || path.join(os.homedir(), '.local', 'share'),
    'har-capture');
  if (!explicit) return path.join(root, 'profile');
  if (path.isAbsolute(explicit) || explicit.includes('/') || explicit.includes('\\')) {
    return path.resolve(explicit);
  }
  return path.join(root, `profile-${explicit.trim().toLowerCase().replace(/[^a-z0-9]/g, '-')}`);
}

function isCdpUp(port) {
  return new Promise(resolve => {
    const req = http.get({ host: '127.0.0.1', port, path: '/json/version', timeout: 1000 },
      r => { r.resume(); resolve(true); });
    req.on('error', () => resolve(false));
    req.on('timeout', () => { req.destroy(); resolve(false); });
  });
}

function waitForCdp(port, timeout = 30000) {
  const deadline = Date.now() + timeout;
  return new Promise((resolve, reject) => {
    (function tick() {
      isCdpUp(port).then(up => {
        if (up) return resolve();
        if (Date.now() > deadline) {
          return reject(new Error(`CDP did not come up on port ${port} within ${timeout}ms`));
        }
        setTimeout(tick, 500);
      });
    })();
  });
}

/**
 * Chrome records the port it is actually listening on in <profile>/DevToolsActivePort.
 * Reading it lets us attach to a capture browser that was started on another port (another
 * tool may have launched it with --remote-debugging-port=9333), instead of failing to spawn
 * a second Chrome on a profile that is already locked.
 */
function portFromProfile(profileDir) {
  try {
    const first = fs.readFileSync(path.join(profileDir, 'DevToolsActivePort'), 'utf8').split(/\r?\n/)[0];
    const port = Number(first);
    return Number.isInteger(port) && port > 0 ? port : null;
  } catch {
    return null;
  }
}

// Ports other capture tooling is known to use for the shared profile (the
// web-api-discovery skill launches its browser on 9333).
const FALLBACK_PORTS = [9333];

(async () => {
  const profileDir = resolveProfileDir();
  let DEBUG_PORT = DEFAULT_DEBUG_PORT;
  let attached = await isCdpUp(DEBUG_PORT);
  if (!attached && !process.env.TRIPIT_CDP_PORT) {
    for (const candidate of [portFromProfile(profileDir), ...FALLBACK_PORTS]) {
      if (candidate && candidate !== DEBUG_PORT && await isCdpUp(candidate)) {
        DEBUG_PORT = candidate;
        attached = true;
        break;
      }
    }
  }

  if (!attached) {
    console.log(`Spawning Chrome on ${profileDir} with --remote-debugging-port=${DEBUG_PORT}`);
    fs.mkdirSync(profileDir, { recursive: true });
    const child = spawn(CHROME_EXE, [
      `--remote-debugging-port=${DEBUG_PORT}`,
      `--user-data-dir=${profileDir}`,
      '--no-first-run', '--no-default-browser-check',
      START_URL,
    ], { detached: true, stdio: 'ignore' });
    child.unref();
    await waitForCdp(DEBUG_PORT);
  } else {
    console.log(`Attaching to Chrome already on port ${DEBUG_PORT}`);
  }

  const browser = await chromium.connectOverCDP(`http://127.0.0.1:${DEBUG_PORT}`);
  const ctx = browser.contexts()[0];
  if (!ctx) throw new Error('CDP endpoint exposed no browser context.');

  // Observe the SPA's own API traffic: it teaches us the real header set to replay, and
  // lets the CSRF token be cross-checked against the cookie jar.
  let observed = null;
  const onRequest = req => {
    if (observed) return;
    if (!/^https:\/\/www\.tripit\.com\/api\/v2\//.test(req.url())) return;
    observed = { url: req.url().split('?')[0], headers: req.headers() };
  };
  ctx.on('request', onRequest);

  let page = ctx.pages().find(p => /tripit\.com\/app/.test(p.url()));
  if (page) {
    await page.bringToFront().catch(() => {});
    await page.reload({ waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
  } else {
    page = await ctx.newPage();
    await page.goto(START_URL, { waitUntil: 'domcontentloaded', timeout: 60000 }).catch(() => {});
  }

  const deadline = Date.now() + 25000;
  while (!observed && Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 250));
  }
  ctx.off('request', onRequest);

  // The cookie jar -- not the intercepted headers -- is the source of truth.
  const jar = await ctx.cookies(ORIGIN);
  const missing = REQUIRED_COOKIES.filter(n => !jar.some(c => c.name === n));
  if (missing.length) {
    console.error(
      `Not signed in: ${ORIGIN} is missing cookie(s) ${missing.join(', ')}.\n` +
      `Sign in to TripIt in the Chrome window on ${profileDir}, then re-run.`);
    process.exit(1);
  }

  const cookieHeader = jar.map(c => `${c.name}=${c.value}`).join('; ');

  // The SPA sends it_wa_csrf's value as X-CSRF-Token-WA. Prefer the observed header when
  // we have one; fall back to the cookie. Disagreement means the page is mid-rotation.
  const cookieCsrf = jar.find(c => c.name === 'it_wa_csrf')?.value || null;
  const headerCsrf = observed?.headers['x-csrf-token-wa'] || null;
  const csrf = headerCsrf || cookieCsrf;
  if (!csrf) {
    console.error('No CSRF token found (neither an X-CSRF-Token-WA header nor an it_wa_csrf cookie).');
    process.exit(1);
  }
  if (headerCsrf && cookieCsrf && headerCsrf !== cookieCsrf) {
    console.warn('WARNING: X-CSRF-Token-WA header and it_wa_csrf cookie disagree; using the header.');
  }

  // The SPA's non-secret headers, so the client can replay what the site actually expects
  // rather than a hand-maintained guess. Secret-bearing headers are excluded by name.
  const spaHeaders = {};
  for (const [k, v] of Object.entries(observed?.headers || {})) {
    if (!SECRET_HEADERS.has(k.toLowerCase())) spaHeaders[k] = v;
  }

  fs.mkdirSync(path.dirname(OUT_FILE), { recursive: true });
  fs.writeFileSync(OUT_FILE, JSON.stringify({
    cookie: cookieHeader,
    csrf_token: csrf,
    spa_headers: spaHeaders,
    captured_from: observed?.url || START_URL,
    captured_at: new Date().toISOString(),
    profile_dir: profileDir,
  }, null, 2));

  console.log('Wrote', OUT_FILE);
  console.log('  cookies       :', jar.length, `(${REQUIRED_COOKIES.join(', ')} present)`);
  console.log('  cookie length :', cookieHeader.length);
  console.log('  csrf source   :', headerCsrf ? 'X-CSRF-Token-WA header' : 'it_wa_csrf cookie');
  console.log('  spa headers   :', Object.keys(spaHeaders).sort().join(', ') || '(none observed)');

  // Detaching from CDP must not close the user's browser.
  await browser.close().catch(() => {});
})().catch(err => { console.error(err); process.exit(1); });
