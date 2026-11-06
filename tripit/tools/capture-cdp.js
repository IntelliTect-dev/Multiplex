// TripIt HAR capture via CDP attach.
// Strategy:
//   1. Spawn the user's installed Chrome ourselves, with --remote-debugging-port
//      and a dedicated user-data-dir.  No Playwright launch flags, so the
//      browser is fingerprint-identical to a normal Chrome session.
//   2. Attach Playwright via chromium.connectOverCDP.
//   3. Capture CDP network events into HAR using chrome-har.
//   4. Phase 1: user logs in (script polls for /home).
//   5. Phase 2: automated traversal of TripIt pages.
//   6. Phase 3: logout.
// HARs are written to ./har/{login,browse,logout}.har.

const { chromium } = require('playwright');
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const http = require('http');
let harFromMessages; // loaded dynamically (ESM)

// Resolve the repo root (this script lives in tools/) so har/ lands in its conventional
// gitignored location regardless of cwd.
const ROOT = path.resolve(__dirname, '..');
const OUT  = path.join(ROOT, 'har');
fs.mkdirSync(OUT, { recursive: true });

const CHROME_EXE  = process.env.TRIPIT_CHROME_EXE
  || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

// The capture profile, shared with tools/extract-session.js so one browser sign-in serves
// both. Defaults to the web-api-discovery skill's own profile; a PATH is used as given and
// a bare NAME lands beside the other capture profiles.
const PROFILE_DIR = resolveProfileDir();
fs.mkdirSync(PROFILE_DIR, { recursive: true });

function resolveProfileDir() {
  const argIdx = process.argv.indexOf('--profile');
  const explicit = (argIdx !== -1 && process.argv[argIdx + 1])
    || process.env.TRIPIT_CAPTURE_PROFILE
    || process.env.HAR_CAPTURE_PROFILE;
  const root = path.join(
    process.env.LOCALAPPDATA || process.env.XDG_DATA_HOME
      || path.join(require('os').homedir(), '.local', 'share'),
    'har-capture');
  if (!explicit) return path.join(root, 'profile');
  if (path.isAbsolute(explicit) || explicit.includes('/') || explicit.includes('\\')) {
    return path.resolve(explicit);
  }
  return path.join(root, `profile-${explicit.trim().toLowerCase().replace(/[^a-z0-9]/g, '-')}`);
}
const DEBUG_PORT  = 9222;

const BASE          = 'https://www.tripit.com';
const LOGIN_URL     = BASE + '/account/login';
const LOGIN_DONE_RE = /tripit\.com\/(app|home|dashboard|trips|account(?!\/login))/i;

const sleep = ms => new Promise(r => setTimeout(r, ms));

// ── HAR recorder ─────────────────────────────────────────────────────────────
class HarRecorder {
  constructor(name) {
    this.name = name;
    this.events = [];
    this.pages = new Map();      // pageId -> {clientRef}
    this.sessions = new Set();   // CDPSessions to detach later
    this.pageCounter = 0;
  }
  attach(page, browserContext) {
    return this._attachPage(page, browserContext);
  }
  async _attachPage(page) {
    const session = await page.context().newCDPSession(page);
    this.sessions.add(session);
    const pageId = 'page_' + (++this.pageCounter);

    await session.send('Page.enable');
    await session.send('Network.enable');

    const push = (method, params) =>
      this.events.push({ method, params: { ...params, pageref: pageId } });

    // Synthesize Page.frameStartedLoading so chrome-har starts a new page
    push('Page.frameStartedLoading', { frameId: pageId });

    session.on('Page.frameNavigated', e => push('Page.frameNavigated', e));
    session.on('Page.loadEventFired', e => push('Page.loadEventFired', e));
    session.on('Page.domContentEventFired', e => push('Page.domContentEventFired', e));
    session.on('Page.frameStartedLoading', e => push('Page.frameStartedLoading', e));
    session.on('Page.frameAttached', e => push('Page.frameAttached', e));

    session.on('Network.requestWillBeSent', e => push('Network.requestWillBeSent', e));
    session.on('Network.requestWillBeSentExtraInfo', e => push('Network.requestWillBeSentExtraInfo', e));
    session.on('Network.responseReceived', e => push('Network.responseReceived', e));
    session.on('Network.responseReceivedExtraInfo', e => push('Network.responseReceivedExtraInfo', e));
    session.on('Network.dataReceived', e => push('Network.dataReceived', e));
    session.on('Network.loadingFinished', async e => {
      push('Network.loadingFinished', e);
      try {
        const body = await session.send('Network.getResponseBody', { requestId: e.requestId });
        e.response = body;  // attach for chrome-har content
      } catch {}
    });
    session.on('Network.loadingFailed', e => push('Network.loadingFailed', e));
    session.on('Network.resourceChangedPriority', e => push('Network.resourceChangedPriority', e));
    session.on('Network.requestServedFromCache', e => push('Network.requestServedFromCache', e));

    return { session, pageId };
  }
  async save() {
    for (const s of this.sessions) {
      try { await s.detach(); } catch {}
    }
    let har;
    try {
      har = harFromMessages(this.events, { includeTextFromResponseBody: true });
    } catch (e) {
      console.log('  HAR build error:', e.message);
      har = { log: { version: '1.2', creator: { name: 'capture-cdp', version: '1' }, entries: [] } };
    }
    const file = path.join(OUT, this.name + '.har');
    fs.writeFileSync(file, JSON.stringify(har, null, 2));
    console.log('  wrote', file, '(' + (har.log.entries?.length || 0) + ' entries)');
  }
}

// ── helpers ──────────────────────────────────────────────────────────────────
function waitForDevtools(port, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve, reject) => {
    const tick = () => {
      const req = http.get({ host: '127.0.0.1', port, path: '/json/version' }, res => {
        let body = '';
        res.on('data', c => body += c);
        res.on('end', () => {
          try {
            const j = JSON.parse(body);
            resolve(j.webSocketDebuggerUrl);
          } catch (e) { retry(); }
        });
      });
      req.on('error', retry);
    };
    const retry = () => {
      if (Date.now() > deadline) return reject(new Error('CDP not ready'));
      setTimeout(tick, 500);
    };
    tick();
  });
}

async function settle(page, ms = 12000) {
  await page.waitForLoadState('networkidle', { timeout: ms }).catch(() => {});
}

async function autoScroll(page) {
  try {
    await page.evaluate(async () => {
      await new Promise(resolve => {
        let total = 0;
        const step = 500;
        const id = setInterval(() => {
          window.scrollBy(0, step);
          total += step;
          if (total >= Math.max(document.body.scrollHeight + 3000, 8000)) {
            clearInterval(id); resolve();
          }
        }, 150);
      });
    });
  } catch {}
}

async function go(page, url, label) {
  try {
    console.log('  →', label || url);
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 30000 });
    await settle(page);
    await autoScroll(page);
    await settle(page, 5000);
    await sleep(500);
  } catch (e) {
    console.log('    skip:', e.message.split('\n')[0]);
  }
}

// ── catalogue ────────────────────────────────────────────────────────────────
const BROWSE_PATHS = [
  ['/app/dashboard','app/dashboard'],
  ['/app/trips','app/trips'],
  ['/app/trips?trips_filter=your_upcoming&page=1','trips upcoming'],
  ['/app/trips?trips_filter=your_past&page=1','trips past'],
  ['/app/trips?trips_filter=your_shared&page=1','trips shared'],
  ['/app/trips?trips_filter=your_private&page=1','trips private'],
  ['/app/unfiled','app/unfiled'],
  ['/app/alerts','app/alerts'],
  ['/app/notifications','app/notifications'],
  ['/app/points','app/points'],
  ['/app/points/programs','app/points/programs'],
  ['/app/points/activity','app/points/activity'],
  ['/app/people','app/people'],
  ['/app/people/contacts','app/people/contacts'],
  ['/app/people/find','app/people/find'],
  ['/app/calendar','app/calendar'],
  ['/app/map','app/map'],
  ['/app/account','app/account'],
  ['/app/account/profile','app/account/profile'],
  ['/app/account/edit-profile','app/account/edit-profile'],
  ['/app/account/preferences','app/account/preferences'],
  ['/app/account/notifications','app/account/notifications'],
  ['/app/account/email','app/account/email'],
  ['/app/account/connections','app/account/connections'],
  ['/app/account/inner-circle','app/account/inner-circle'],
  ['/app/account/travelers','app/account/travelers'],
  ['/app/account/payment','app/account/payment'],
  ['/app/account/subscription','app/account/subscription'],
  ['/app/account/security','app/account/security'],
  ['/app/account/devices','app/account/devices'],
  ['/app/account/privacy','app/account/privacy'],
  ['/app/account/api','app/account/api'],
  ['/app/account/connected-apps','app/account/connected-apps'],
  ['/app/account/forward-email','app/account/forward-email'],
  // Legacy paths (may redirect to /app/* equivalents)
  ['/home','home'],
  ['/dashboard','dashboard'],
  ['/trips','trips'],
  ['/account','account'],
  ['/help','help'],
  ['/pro','pro'],
];

const SEGMENT_PATHS = [
  '/air','/air/new','/lodging','/lodging/new','/car','/car/new','/rail','/rail/new',
  '/transport','/transport/new','/activity','/activity/new','/note','/note/new',
  '/restaurant','/restaurant/new','/cruise','/cruise/new',
];

const API_PROBES = [
  '/web/v2/list/trip?include_objects=true&past=false&page_size=20',
  '/web/v2/list/trip?include_objects=true&past=true&page_size=20',
  '/web/v2/list/object?objectTypes=all',
  '/web/v2/list/object?objectTypes=AirObject',
  '/web/v2/list/object?objectTypes=LodgingObject',
  '/web/v2/list/object?objectTypes=CarObject',
  '/web/v2/list/object?objectTypes=RailObject',
  '/web/v2/list/object?objectTypes=TransportObject',
  '/web/v2/list/object?objectTypes=ActivityObject',
  '/web/v2/list/object?objectTypes=NoteObject',
  '/web/v2/list/object?objectTypes=MapObject',
  '/web/v2/list/object?objectTypes=RestaurantObject',
  '/web/v2/list/object?objectTypes=CruiseObject',
  '/web/v2/list/points_program',
  '/web/v2/get/profile',
  '/web/v2/get/profile?include_traveler_info=true',
  '/web/v2/list/alerts',
  '/web/v2/list/notification',
  '/web/v2/list/contact',
  '/web/v2/list/traveler',
  '/web/v2/list/connected_app',
  '/web/v2/get/subscription',
  '/web/v2/list/forward_email_address',
  '/web/v2/list/device',
  '/web/v2/list/calendar_subscription',
  '/web/v2/get/preferences',
  '/api/v1/list/trip/format/json',
  '/api/v1/list/trip/format/json?past=true',
  '/api/v1/list/object/format/json',
  '/api/v1/list/points_program/format/json',
  '/api/v1/get/profile/format/json',
  '/api/v1/list/request/format/json',
  '/api/v1/list/connection/format/json',
  '/graphql',
  '/typeahead/search?q=lon',
  '/web/v2/search?q=lon',
  '/web/v2/search/autocomplete?q=lon',
];

async function fetchProbes(page, paths) {
  for (const ep of paths) {
    try {
      await page.evaluate(async u => {
        try { await fetch(u, { credentials: 'include', headers: { Accept: 'application/json,*/*' } }); } catch {}
      }, BASE + ep);
      await sleep(250);
    } catch {}
  }
}

// ── main ─────────────────────────────────────────────────────────────────────
async function isCdpUp(port) {
  return new Promise(resolve => {
    const req = http.get({ host: '127.0.0.1', port, path: '/json/version', timeout: 1000 },
      res => { res.resume(); resolve(true); });
    req.on('error', () => resolve(false));
    req.on('timeout', () => { req.destroy(); resolve(false); });
  });
}

(async () => {
  ({ harFromMessages } = await import('chrome-har'));

  const alreadyRunning = await isCdpUp(DEBUG_PORT);
  if (alreadyRunning) {
    console.log('Attaching to existing Chrome on port ' + DEBUG_PORT);
  } else {
    console.log('Spawning Chrome with --remote-debugging-port=' + DEBUG_PORT);
    const chromeArgs = [
      '--remote-debugging-port=' + DEBUG_PORT,
      '--user-data-dir=' + PROFILE_DIR,
      '--no-first-run',
      '--no-default-browser-check',
      '--start-maximized',
      'about:blank',
    ];
    const child = spawn(CHROME_EXE, chromeArgs, { detached: true, stdio: 'ignore' });
    child.unref();
  }

  const wsUrl = await waitForDevtools(DEBUG_PORT);
  console.log('CDP ready:', wsUrl);

  const browser = await chromium.connectOverCDP('http://127.0.0.1:' + DEBUG_PORT);
  const ctx = browser.contexts()[0];

  // ── Phase 1: LOGIN ──────────────────────────────────────────────────────
  console.log('\n╔══════════════════════════════════════════════════╗');
  console.log('║  Phase 1 — Please log in to TripIt               ║');
  console.log('║  (the just-opened Chrome window)                 ║');
  console.log('╚══════════════════════════════════════════════════╝\n');

  const recLogin = new HarRecorder('login');
  let page = ctx.pages().find(p => /tripit\.com/.test(p.url())) || ctx.pages()[0] || await ctx.newPage();
  await recLogin.attach(page);
  if (!/tripit\.com/.test(page.url())) {
    await page.goto(LOGIN_URL, { waitUntil: 'domcontentloaded' }).catch(() => {});
  }

  const loginDeadline = Date.now() + 20 * 60 * 1000;
  while (Date.now() < loginDeadline) {
    const url = page.url();
    if (LOGIN_DONE_RE.test(url) && !/\/account\/login/i.test(url)) {
      const cookies = await ctx.cookies();
      if (cookies.some(c => /tripit_session|tpt_|sso|auth/i.test(c.name))) break;
      if (/\/(home|dashboard|trips)/.test(url)) break;
    }
    await sleep(1500);
  }
  console.log('✓ Login detected:', page.url());
  await settle(page, 15000);
  await sleep(2000);
  await recLogin.save();

  // ── Phase 2: AUTOMATED BROWSE ───────────────────────────────────────────
  console.log('\n╔══════════════════════════════════════════════════╗');
  console.log('║  Phase 2 — Automated traversal                   ║');
  console.log('╚══════════════════════════════════════════════════╝\n');

  const recBrowse = new HarRecorder('browse');
  await recBrowse.attach(page);

  for (const [p, label] of BROWSE_PATHS) {
    await go(page, BASE + p, label);
  }

  // Click into individual trip pages
  console.log('\n  [trip pages]');
  try {
    await go(page, BASE + '/trips', 'trips (collect)');
    const tripLinks = await page.locator(
      'a[href*="/trip/show/id/"], a[href*="/trips/trip/"]'
    ).evaluateAll(els => [...new Set(els.map(e => e.href))].slice(0, 8));
    for (const link of tripLinks) {
      await go(page, link, 'trip detail');
      const segLinks = await page.locator(
        'a[href*="/air/"], a[href*="/lodging/"], a[href*="/car/"], a[href*="/rail/"], ' +
        'a[href*="/activity/"], a[href*="/segment/"]'
      ).evaluateAll(els => [...new Set(els.map(e => e.href))].slice(0, 4));
      for (const sl of segLinks) await go(page, sl, 'segment');
      await go(page, link, 'trip return');
    }
  } catch (e) { console.log('  trip-crawl err:', e.message.split('\n')[0]); }

  console.log('\n  [segment pages]');
  for (const p of SEGMENT_PATHS) await go(page, BASE + p, 'seg' + p);

  console.log('\n  [people detail]');
  try {
    await go(page, BASE + '/people', 'people');
    const links = await page.locator('a[href*="/profile/"]').evaluateAll(
      els => [...new Set(els.map(e => e.href))].slice(0, 3));
    for (const l of links) await go(page, l, 'profile');
  } catch {}

  console.log('\n  [points detail]');
  try {
    await go(page, BASE + '/points/programs', 'points programs');
    const links = await page.locator('a[href*="/points/"]').evaluateAll(
      els => [...new Set(els.map(e => e.href))].slice(0, 4));
    for (const l of links) await go(page, l, 'points detail');
  } catch {}

  console.log('\n  [API probes]');
  await go(page, BASE + '/home', 'base for fetch');
  await fetchProbes(page, API_PROBES);
  await settle(page, 8000);

  await recBrowse.save();

  // ── Phase 3: LOGOUT ─────────────────────────────────────────────────────
  console.log('\n╔══════════════════════════════════════════════════╗');
  console.log('║  Phase 3 — Logout                                ║');
  console.log('╚══════════════════════════════════════════════════╝\n');

  const recLogout = new HarRecorder('logout');
  await recLogout.attach(page);
  try {
    await go(page, BASE + '/account/logout', 'logout endpoint');
    const btn = page.locator('a[href*="logout"], button:has-text("Sign out"), button:has-text("Log out")').first();
    if (await btn.isVisible({ timeout: 3000 }).catch(() => false)) {
      await btn.click(); await settle(page, 8000);
    }
    await sleep(2000);
  } catch (e) { console.log('  logout err:', e.message.split('\n')[0]); }
  await recLogout.save();

  await browser.close().catch(() => {});
  console.log('\n✔ Done. HAR files in', OUT);
})().catch(err => { console.error(err); process.exit(1); });
