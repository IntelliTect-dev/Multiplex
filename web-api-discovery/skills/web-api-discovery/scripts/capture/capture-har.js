#!/usr/bin/env node
/**
 * capture-har.js -- record a browser session, scrub it, and catalogue it.
 *
 * One command takes a URL to a scrubbed, catalogued, committable reference
 * set. The operator's entire surface is still a URL:
 *
 *   Invoke-HarCapture https://example.com
 *
 * Two properties drive the design, and neither is negotiable:
 *
 *  - A HUMAN drives it by browsing, and ends it by pressing ENTER.
 *  - An AI drives it over CDP, and ends it with `stop`.
 *
 * Both need the SAME recording, so this script owns the browser context and
 * nothing else does. `playwright open --save-har` cannot serve the second
 * case: it flushes only when a human closes the window, and it offers no
 * persistent profile. A context launched here records every request made in
 * it, including requests driven by a CDP client that attached later --
 * `connectOverCDP(...).contexts()[0]` IS this context, not a new one.
 *
 * ## Two recorders, one artifact
 *
 * Playwright's recordHar buffers the entire session in the driver and
 * serializes it once, during a client-initiated context.close(). That has a
 * sharp edge, measured rather than assumed: when the operator closes the
 * BROWSER WINDOW, Chrome exits, the connection drops before any close call
 * can run, and nothing is written at all.
 *
 * So a second recorder runs alongside it, appending every finished and every
 * FAILED request to a line-delimited log. Both recorders always run and
 * exactly one survives:
 *
 *   clean driver close  -> recordHar wins; the log is deleted
 *   window closed       -> the log is assembled into raw.har
 *
 * Either way `raw.har` exists and is a genuine HAR 1.2 document. recordHar is
 * preferred where both are available because it is more authoritative on
 * serverIPAddress, connection, real header/body sizes, cache and page timings
 * -- not because the assembled one is degraded. It is not: it carries real
 * timings, uncapped bodies, base64 for anything not valid UTF-8, and the
 * failed requests that a `response`-driven recorder drops entirely.
 *
 * ## A browser Playwright launched is a browser a site can refuse
 *
 * The paragraph above is true of a browser this script LAUNCHES. On a
 * protected site, that launch is the end of the recording before it starts:
 * Playwright marks the browser as automated, and bot protection answers the
 * marking rather than the traffic. Measured against car-rental sites (#73):
 *
 *   Hertz          Imperva      403 "Access denied, Error 15" on the FIRST page
 *                               load, before any interaction, browsing by hand
 *   Costco Travel  Akamai       search POST reset (ERR_HTTP2_PROTOCOL_ERROR),
 *                               including with a fresh profile
 *   Avis           DataDome     slider CAPTCHA on the vehicle search, then 403
 *                               even after it was solved
 *   Priceline      PerimeterX   "Press & Hold" as soon as the location field
 *                               was typed into, including with a fresh profile
 *
 * The SAME sites, on the same machine and the same network, all worked in a
 * normally launched Chrome. So there is a second way to obtain a browser:
 * `--browser native` starts `chrome.exe` itself with no automation flags, and
 * `--attach` records in one that is already running. Both then reach it the way
 * the paragraph above says an AI reaches a recording --
 * `connectOverCDP(endpoint).contexts()[0]` -- which is why this mode is
 * possible at all rather than being a second recorder.
 *
 * WHAT CHANGES, and it is only this: recordHar is Playwright's, and Playwright
 * did not launch this browser, so `raw.har` always comes from
 * IncrementalRecorder + assembleFromLog. That artifact is not degraded (see
 * above), and it is no longer reported as a fallback -- exit 5 says "recordHar
 * lost", which is not a fact about a mode where recordHar never ran.
 *
 * WHAT DOES NOT CHANGE: the scrub -> verify -> digest -> catalogue tail, every
 * ending, and the confinement of raw captures below.
 *
 * NO STEALTH MEASURES, deliberately. Nothing here patches
 * `navigator.webdriver`, spoofs a fingerprint, or rotates anything -- a
 * normally launched Chrome needs none of it, which is the entire finding. A
 * site may still challenge; the human answers it.
 *
 * AND NOTHING HERE EVER CLOSES A BROWSER IT DID NOT LAUNCH. `stop` detaches.
 * The operator's window, and every signed-in tab in it, survives the recording
 * -- for the same reason nothing in this file kills a process.
 *
 * ## An agent can drive it, and the prompt is generated (#98)
 *
 * `--ai` hands the browsing to an agent while this process goes on recording.
 * It implies `--browser native`, because a browser Playwright launched is a
 * browser the sites worth driving an agent at refuse (the table above), so an
 * agent driving one would be driving the refusal.
 *
 * `--describe` BECOMES THE GOAL. It is already required (#366), so the switch
 * needs no new input: the prompt is assembled from drive-prompt.md, this run's
 * own facts (the goal, the site, the CDP endpoint, session.json) and
 * discovery-checklist.md, written to the session directory as agent-prompt.md,
 * and handed to `claude -p`.
 *
 * THE PROMPT IS GENERATED BECAUSE MOST OF IT IS SAFETY TEXT. It was hand-written
 * for the five-site car-rental capture this feature comes from, and it is the
 * same every time except the goal: human pace, keep the site's tab in front, NO
 * STEALTH MEASURES, never a password or personal or payment detail, stop before
 * any driver-details or payment page, and stop and hand back on any bot check or
 * sign-in prompt. A prompt somebody retypes in a hurry is a prompt that drops
 * one of those, and the dropping is invisible until an agent has typed a card
 * number into somebody's checkout.
 *
 * THE AGENT IS AN ENDING, NOT A MODE. Its exit joins the same race as the stop
 * sentinel, ENTER and SIGINT, so `Stop-HarRecording`, a keypress and a closed
 * window all still end the recording mid-drive -- a drive is never a loss of
 * control. Everything after the ending is unchanged: scrub -> verify -> digest
 * -> catalogue, exactly as a hand-driven capture.
 *
 * WHAT IT DOES NOT WIDEN: an agent-driven run records only this capture's tabs,
 * like any other native or attached run (#85), and nothing about `--ai` closes a
 * browser -- the agent is told not to, and `stop` still detaches.
 *
 * ## Pacing, throttling, and where the line is
 *
 * Polite pacing and honouring a site's throttling are in; anything whose
 * purpose is hiding automation from bot detection is out (see NO STEALTH
 * MEASURES, above, and pacing.js). So:
 *
 * WHAT THIS PROCESS DOES TO A PAGE IS PACED -- its first navigation and the
 * sign-in fills -- by pacing.js: a randomized, human-scale wait between
 * navigations and between typing steps, one navigation in flight, and a
 * per-minute ceiling. `--pace` sets it; `--pace off` (tests) makes every wait
 * zero. An agent's actions are the agent's, so the same numbers go into its
 * prompt, and the recorder only WATCHES its rate.
 *
 * EVERY RECORDED RESPONSE IS READ FOR THROTTLING -- only the site's own (a
 * main-frame document, or the capture's host), because trackers answer 429 all
 * day. A 429 or a 503 with Retry-After PAUSES (Retry-After, or a backoff with
 * jitter); throttling that persists, throttling through a pause, a small HTML
 * 403 from a CDN, or a challenge page HANDS OVER.
 *
 * A HAND-OVER UNDER --ai STOPS THE AGENT -- the one process this file ever
 * signals, and only for this. Nothing about it ends the browser or the
 * recording: the operator is told what the site did and where, the window is
 * theirs, and the recording goes on until they end it. With nobody at the
 * terminal, nobody can take over, so the recording ends as `handed-over` and is
 * post-processed like any other. By hand, a hand-over is advice -- the browser
 * is already the operator's.
 *
 * WHAT IS NOT HERE YET, deliberately: `-SignInFirst` (#75) and multi-site (#74).
 * Without #75 the agent stops at the first sign-in prompt and hands back, which
 * is what the issue says the behaviour is until then; without #74 one run drives
 * one site. The checklist text belongs to #76 and is parked in
 * discovery-checklist.md so that issue can lift it out without a rewrite.
 *
 * ## Raw captures are confined by construction
 *
 * The raw capture carries live session cookies. It always lands in the fixed,
 * gitignored `.har-captures/` and no option can redirect it.
 *
 * WHICH `.har-captures/`, though, is the question that wording never asked, and
 * it turned out to be the load-bearing one (#367). The root used to resolve
 * against the working directory, so recording from a linked worktree put an
 * unrepeatable capture somewhere `git worktree remove` deletes outright --
 * gitignored, so git reports nothing to lose and no prompt appears. It now
 * anchors to the repository's MAIN working tree, whose lifetime is the clone's,
 * and the resolved path is announced while the recording starts. `--output-path`
 * receives only what has already been scrubbed and verified, so the guard is
 * structural rather than a check somebody has to remember to run. The scrub
 * therefore writes its candidate into the session directory and it is COPIED
 * out only once the gate has passed on it: a file the gate has not judged
 * never exists in a committable directory, and a scrub the gate refuses is
 * renamed to `scrubbed.rejected.har` where it stands, never deleted. One false
 * positive used to destroy the whole capture (#297).
 *
 * NOTHING IN THIS FILE EVER DELETES A RAW, and the rejected-scrub path above is
 * unconditional: a capture the gate refused keeps both its raw and its refused
 * candidate, because that is the case where losing either is unrecoverable.
 *
 * What that rule no longer says is "a raw is never deleted by anything".
 * There are now exactly two ways an operator may ask for one to be removed,
 * and both are asked for one capture at a time:
 *
 *   `har/Invoke-SanitizeHar.ps1 -RemoveSource` -- only when the scrub of that
 *      capture VERIFIED CLEAN, never on a rejection and never on an advisory
 *      verdict, since advisory means "waive or correct, then scrub again" and
 *      scrubbing again needs the raw. Opt-in per run. It takes the
 *      substitution tables with it, because they are keyed by the plaintext
 *      originals (#352).
 *   `capture/Remove-HarCapture.ps1` -- the deliberate delete, for a recording
 *      an operator simply does not want, scrubbed or not. It takes exactly one
 *      capture, refuses a wildcard and refuses more than one, asks before it
 *      acts (ConfirmImpact High), and removes the whole session directory
 *      (#102).
 *
 * WHAT NEITHER OF THEM IS. There is deliberately NO age sweep, no count or
 * size cap, and no bulk delete command: every recording is kept until somebody
 * deletes that one recording on purpose. Adding a delete command is not adding
 * retention (maintainer decision, 2026-10-14).
 *
 * ## Phases
 *
 *   record -> scrub -> verify -> digest     this process, on every ending
 *   catalogue (segment, extract, rows)      an AI, once the digest exists
 *
 * ## The pipeline is RE-ENTERABLE at the catalogue stage
 *
 * `catalogue` runs the digest and delegation phases against a capture that was
 * recorded and scrubbed some other time, so regenerating a catalogue no longer
 * means re-recording a browser session a human drove by hand (#352). Scrubbing
 * alone was already re-enterable through Invoke-SanitizeHar.ps1; this is the
 * other half.
 *
 * It is an ENTRY POINT, not a second pipeline. It asks the same leak gate, runs
 * the same digest, and asks the same decideCatalogueRunner. It does not publish
 * and does not quarantine -- those belong to the scrub stage, and a second copy
 * of them behind a second door is how the invariants in #343 get lost.
 *
 * Usage:
 *   node capture-har.js start --uri <url> --describe <text>
 *                             [--profile <name|path>] [--isolated]
 *                             [--browser playwright|native] [--attach <cdp>]
 *                             [--ai] [--pace off|human|<spec>]
 *                             [--port <n>] [--output-path <dir>]
 *                             [--snapshot-seconds <n>] [--no-wait]
 *                             [--validate-only]
 *   node capture-har.js stop  [--session <dir>] [--min-bytes <n>] [--dir <d>]
 *   node capture-har.js status [--session <dir>] [--dir <d>]
 *   node capture-har.js catalogue [<scrubbed.har | session dir | output dir>]
 *                                 [--session <dir>] [--output-path <dir>]
 *
 *   --profile        a NAME keeps a separate signed-in identity under the
 *                    capture root; a PATH records as an identity another tool
 *                    owns (that tool cannot use it meanwhile -- a persistent
 *                    profile is single-instance).
 *   --isolated       bundled Chromium + an ephemeral profile, for CI and
 *                    throwaway captures. The default is system Chrome on a
 *                    dedicated capture profile, which stays signed in between
 *                    sessions -- bundled Chromium can silently close against
 *                    a profile Chrome created, hence channel:'chrome'.
 *   --browser        `playwright` (default) launches the browser here, which is
 *                    the mode a protected site detects and refuses. `native`
 *                    starts chrome.exe with NO automation flags and attaches
 *                    over CDP -- use it on a site that blocks the default. See
 *                    the measured table above. Exclusive with --isolated.
 *   --attach         record in a Chrome that is ALREADY running with
 *                    --remote-debugging-port, given its HTTP endpoint
 *                    (http://127.0.0.1:9222, 127.0.0.1:9222, or just 9222).
 *                    Implies --browser native, and takes the port and the
 *                    profile from the browser it finds, so --port and
 *                    --profile are refused with it. `stop` DETACHES and leaves
 *                    that browser and its tabs alone.
 *                    With --attach or --browser native, ONLY THIS CAPTURE'S
 *                    TABS are recorded (#85): the tab opened for --uri, windows
 *                    opened from an in-scope tab, and tabs on the --uri's site
 *                    (host minus a leading www., plus subdomains). Every
 *                    request such a tab makes is recorded, whatever its host.
 *                    The debugging port stays open, unauthenticated, until
 *                    that window closes -- close it when done.
 *   --ai             an AGENT does the browsing while this process records.
 *                    Implies --browser native, and so is exclusive with
 *                    --isolated and with --browser playwright. The prompt is
 *                    GENERATED -- goal, endpoint, session file, the discovery
 *                    checklist and the safety rules -- written to the session
 *                    as agent-prompt.md, and run as `claude -p`. Everything the
 *                    agent prints is saved beside the capture as agent-log.txt.
 *                    The recording ends when the agent finishes or hands back;
 *                    ENTER, `stop` and closing the window still end it sooner.
 *                    Needs the `claude` CLI on PATH, which is checked before a
 *                    browser is started.
 *   --pace           how fast this process acts on a page, and how fast an
 *                    --ai agent is told to. Absent or `human`: 1.5-4 s between
 *                    navigations, 0.3-1.2 s between clicks or typing steps, at
 *                    most 12 navigations a minute, each wait drawn at random.
 *                    `nav=MIN-MAX,action=MIN-MAX,per-minute=N` (milliseconds;
 *                    any subset; each wait at most 10 minutes, clamped and said
 *                    so) changes it; `off` or `0` makes every wait zero, for
 *                    tests. A site's Retry-After is honoured either way.
 *   --port           default 9333, and it never has to be specified: a busy
 *                    port falls forward to the next free one.
 *   --describe       REQUIRED (#366). What this recording is for, in the
 *                    operator's own words. Segmenting the session is the
 *                    smaller half of its job: a shared, append-only capture
 *                    store whose directory names are START times is one where
 *                    the description is the ONLY reliable way to tell one
 *                    capture from another -- a time window wide enough to hold
 *                    one session's runs holds other sessions' runs too. It is
 *                    never the source of action names; those come from the
 *                    traffic.
 *   --no-wait        do not read ENTER from stdin (for non-interactive use;
 *                    the session then ends via `stop`, SIGINT, or the window).
 *   --validate-only  resolve and print paths without launching a browser.
 *   --log-level      `normal` (default) says what is being recorded, how to
 *                    end it, and which artifacts it produced. `verbose` adds
 *                    the resolved paths, the profile and the CDP endpoint.
 *                    Accepted on every command; Invoke-HarCapture sets it from
 *                    PowerShell's own -Verbose rather than exposing a second
 *                    switch for the same idea. All chatter goes to STDERR --
 *                    stdout carries only the --validate-only and status JSON.
 *
 * Exit codes:
 *   0 -- recorded, scrubbed and catalogued
 *   1 -- I/O or runtime error
 *   2 -- usage error
 *   3 -- no capture session found to stop
 *   4 -- the capture produced no usable recording
 *   5 -- raw.har was assembled from the incremental log rather than recordHar.
 *        Only reachable in a mode where recordHar could have won: a native or
 *        attached browser has no recordHar at all, so assembling is the design
 *        rather than a fallback, and reporting it would make the normal ending
 *        of that mode look like a degraded one.
 *   6 -- recorded successfully, but scrub or catalogue failed
 *   7 -- recorded, scrubbed and catalogued, but the leak gate reported
 *        ADVISORY findings (identity evidence by shape). Every artifact is
 *        where it should be; the findings need a human verdict.
 *   8 -- sign-in needed: a sign-in form appeared with nobody at the terminal
 *        to start one (#115). The raw is kept and not post-processed. A
 *        person signs in once from a terminal and the saved session carries
 *        the unattended runs after it.
 */

'use strict';

const fs = require('fs');
const net = require('net');
const os = require('os');
const path = require('path');
const readline = require('readline');
const { spawn, spawnSync } = require('child_process');

const harProfile = require(path.join(__dirname, '..', 'har', 'har-profile.js'));
const harCatalogue = require(path.join(__dirname, '..', 'har', 'har-catalogue.js'));
const repoGuard = require(path.join(__dirname, '..', 'lib', 'repo-workflow-guard.js'));
const captureSize = require(path.join(__dirname, '..', 'lib', 'capture-size.js'));
// The ONE walk over a capture store (#386/#387). It USED to be the body of
// `listSessionDirs` right here; it moved out so the batch drivers could reuse
// this exact enumeration -- with its classification of legacy and foreign
// captures intact -- instead of growing a second notion of what a capture is.
const captureStore = require(path.join(__dirname, 'capture-store.js'));
// One place recognises a file as a capture, and refuses it when it is not
// one (issue #423).
const harDocument = require(path.join(__dirname, '..', 'har', 'har-document.js'));
const bodyDescriptor = require(path.join(__dirname, 'request-body-descriptor.js'));
// The ONE gitignore check in this subsystem (#318). It wraps `git check-ignore`
// and already defends against a forged `.gitignore` containing `*` and against
// `core.excludesFile` injection, so nothing here asks the question a second
// way -- a bespoke re-implementation is how the original defect arrived.
const subsDestination = require(path.join(__dirname, '..', 'har', 'subs-destination.js'));
// Reused, not reimplemented: the consumer .gitignore entry list and the
// idempotent append both already exist for the scaffolder (#119).
const { ensureRepoRootGitignoreHasScaffoldEntries } =
    require(path.join(__dirname, '..', 'codegen', 'generate-wrapper.js'));
// Where a module is looked for, and what to say when it is not there (#512).
// A bare `require('playwright')` searches upward from THIS file's directory, so
// the recorder run out of the upstream checkout could not see an install in the
// folder the operator was standing in -- and told them to make one there.
const nodeDep = require(path.join(__dirname, '..', 'lib', 'node-dependency.js'));
// The recorder signs in, from Bitwarden or a masked prompt (#115).
const signIn = require(path.join(__dirname, 'sign-in.js'));
// Human-scale pacing for what this process does to a page, and the reading of
// a site's throttling and blocking in what it records. See "## Pacing" above.
const pacing = require(path.join(__dirname, 'pacing.js'));

const DEFAULT_PORT = 9333;
const DEFAULT_MIN_BYTES = 1024;
const CAPTURES_DIR = '.har-captures';
const DEFAULT_SNAPSHOT_SECONDS = 5;
const STORAGE_STATE_FILENAME = '.har-storage-state.json';
const CATALOGUE_PROMPT = 'catalogue-prompt.md';
const STOP_SENTINEL = 'STOP';
const SESSION_FILE = 'session.json';
// There is deliberately NO `current.json` (#377). A single pointer file at the
// captures ROOT is shared mutable state in a store several agent sessions write
// to at once, so a second capture overwrote it and the first recording became
// unaddressable. The STAMP is the identity, and `resolveSession` already scanned
// the session directories whenever the pointer went stale -- removing it makes
// that existing path the only path.
const RAW_HAR = 'raw.har';
const RECORD_LOG = 'raw.ndjson';
const SCRUBBED_HAR = 'scrubbed.har';
// Where a scrub the leak gate refused goes. Under the session directory, which
// is gitignored, so triage is possible without the committable output path
// ever holding a file that is known to be leaking.
const REJECTED_HAR = 'scrubbed.rejected.har';
const FINDINGS_FILE = 'scrub-findings.json';
// Publication writes here first and then RENAMES onto the real name. The
// prefix is shared by every file the output path receives, so one sweep finds
// them all; the dot keeps them out of a casual listing.
const PUBLISH_TEMP_PREFIX = '.publishing-';
// A temporary older than this was abandoned by a run that died. A live one
// exists for milliseconds, so nothing in flight is ever this old -- which is
// what lets the sweep run without racing a concurrent capture.
const PUBLISH_TEMP_STALE_MS = 24 * 60 * 60 * 1000;
// Windows refuses to rename ONTO a destination another process holds open,
// where POSIX replaces it regardless. The everyday cause is real-time
// antivirus reading the file that was just written, and it clears in
// milliseconds. Six attempts with a doubling backoff wait 465ms in total: long
// enough for a scanner to let go, short enough that a standing fault -- a file
// somebody left open in an editor -- is reported rather than waited on.
const PUBLISH_RENAME_ATTEMPTS = 6;
const PUBLISH_RENAME_BACKOFF_MS = 15;
// EPERM and EBUSY are what Windows raises for a held handle, and only those.
// EACCES is a permission fact about the path: retrying it changes nothing
// except how long the operator waits to be told.
const PUBLISH_RENAME_RETRY_CODES = new Set(['EPERM', 'EBUSY']);
// verify-scrub.js exit 4: identity findings by SHAPE and nothing worse. The
// artifact is verified enough to proceed. Any other non-zero blocks.
const VERIFY_ADVISORY_EXIT = 4;
const DIGEST_FILE = 'digest.json';
const CATALOGUE_FILE = 'catalogue.json';
// The two artifacts an agent-driven capture adds, both inside the session
// directory beside the raw (#98). The PROMPT is written out rather than only
// passed as an argument: it is the instruction set a run was actually driven
// under, including the safety rules, and a capture whose prompt cannot be read
// afterwards is one nobody can audit. The LOG is the agent's own output -- what
// it did, where it stopped and why -- which the issue asks for so catalogue
// segmentation has a record of the actions taken.
const AGENT_PROMPT_FILE = 'agent-prompt.md';
const AGENT_LOG_FILE = 'agent-log.txt';
// The static halves of the generated prompt. Two files rather than one, and the
// split is the point: DISCOVERY_CHECKLIST is the text issue #76 owns and will
// move into SKILL.md, so it lives alone and is read rather than inlined -- #76
// can then relocate it without touching the prompt builder or the rules.
const DRIVE_PROMPT = 'drive-prompt.md';
const DISCOVERY_CHECKLIST = 'discovery-checklist.md';
// The agent the issue names as the default driver. There is deliberately no
// `--agent` option yet: a second driver is a command-line option, which this
// repository treats as an API decision needing approval, and nothing yet needs
// one. The name is carried on the session so the day one arrives, a reader of
// session.json can already tell which drove.
const AGENT_CLAUDE = 'claude';
// THE ONLY TOOLS THE DRIVING AGENT MAY USE WITHOUT ASKING (owner decision, I-2
// on #100). It drives somebody's live, signed-in browser, so the grant is the
// browser-driving tools and nothing that acts on the machine: no Bash, Write or
// Edit. Passed as `--allowedTools`; ONE list, so the argv and the prompt that
// names the tools cannot disagree. The list PRE-APPROVES; what it omits is left
// to the permission mode (auto, #112) -- or, under --ai-tools browser-only,
// removed outright.
//
// Two drivers, matching the order drive-prompt.md prefers them in. The names are
// the ones the tools register under -- `claude-in-chrome` per the Claude Code
// Chrome docs, `playwright` per the Playwright MCP README's own `claude mcp add`
// line -- checked, not guessed.
//
// Left off on purpose, from the extension: javascript_tool (it could patch
// navigator.webdriver, the stealth the rules forbid), file_upload and
// upload_image (they send local files to a site), read_network_requests (it
// reads the live cookies the capture files are kept from the agent for),
// gif_creator and shortcuts_execute (they do more than drive the page), and
// browser_batch (a burst of actions is what the human-pace rule exists to stop).
//
// ON the list, for SELECTION (#117): list_connected_browsers and select_browser.
// The extension can be connected in several Chromes -- the operator's everyday
// one among them -- and the agent must pick the CAPTURE browser by deviceId
// before it touches a page, or it may act, unrecorded, in somebody's real
// session. Not switch_browser: that broadcasts a pairing request to every Chrome
// with the extension and waits for a human to click Connect in one of them.
// WHICH TOOLS THE DRIVING AGENT HAS (#112): `--ai-tools`. `auto` is the default.
const AGENT_TOOLS_BROWSER_ONLY = 'browser-only';
const AGENT_TOOLS_MODES = Object.freeze(['auto', AGENT_TOOLS_BROWSER_ONLY]);
const AGENT_ALLOWED_TOOLS = Object.freeze([
    'mcp__claude-in-chrome__tabs_context_mcp',
    'mcp__claude-in-chrome__tabs_create_mcp',
    'mcp__claude-in-chrome__navigate',
    'mcp__claude-in-chrome__computer',
    'mcp__claude-in-chrome__read_page',
    'mcp__claude-in-chrome__find',
    'mcp__claude-in-chrome__form_input',
    'mcp__claude-in-chrome__get_page_text',
    'mcp__claude-in-chrome__list_connected_browsers',
    'mcp__claude-in-chrome__select_browser',
    'mcp__playwright__browser_navigate',
    'mcp__playwright__browser_click',
    'mcp__playwright__browser_type',
    'mcp__playwright__browser_take_screenshot',
    'mcp__playwright__browser_snapshot'
]);
// The allowlist for one run: without the extension (--no-chrome, review of
// #122) its tools are not pre-approved either, so the argv and the prompt name
// only Playwright MCP's.
function agentAllowedTools(chrome) {
    return chrome ? AGENT_ALLOWED_TOOLS : AGENT_ALLOWED_TOOLS.filter((t) => !t.startsWith('mcp__claude-in-chrome__'));
}
// CLAUDE IN CHROME, AS INSTALLED IN THE CAPTURE PROFILE (#117). The Chrome Web
// Store ID, which is also the folder Chrome installs it under:
// <profile>/<Default|Profile N>/Extensions/<id>/<version>/manifest.json. Checked
// against an installed copy (its manifest names itself "Claude") and the store
// listing, whose URL is keyed by the same ID.
const CLAUDE_EXTENSION_ID = 'fcoeoabgfenejglbffodgkkbkcdhcgfn';
const CLAUDE_EXTENSION_STORE_URL =
    `https://chromewebstore.google.com/detail/claude/${CLAUDE_EXTENSION_ID}`;
// The name the operator gives the capture browser when connecting the extension,
// and the name the agent selects it by. See captureBrowserName.
const CAPTURE_BROWSER_NAME = 'har-capture';
// Where committed references live in a consuming project. Named here only to
// PRINT the promote command -- nothing in this file writes into it.
const REFERENCE_DIR = path.join('docs', 'har-reference');
const POLL_MS = 200;
const STOP_TIMEOUT_MS = 60000;
const LAUNCH_TIMEOUT_MS = 45000;
const CDP_PROBE_MS = 1500;
const PORT_SCAN_LIMIT = 64;
// The two ways a browser can come to exist for a recording (#73). The default
// is unchanged; `native` exists because Playwright's launch is DETECTABLE and
// protected sites refuse it outright -- see the measured table at the top.
const BROWSER_PLAYWRIGHT = 'playwright';
const BROWSER_NATIVE = 'native';
const BROWSER_MODES = [BROWSER_PLAYWRIGHT, BROWSER_NATIVE];
// How long a natively launched Chrome gets to answer on its debugging port.
// Generous, because this is a cold browser start on a fresh profile and the
// alternative to waiting is telling an operator their browser failed when it
// was merely slow. The wait ends EARLY when the process we spawned has already
// exited, which is the failure that actually happens (see launchNativeChrome).
const NATIVE_CDP_TIMEOUT_MS = 30000;
const NATIVE_CDP_POLL_MS = 250;
// A pause longer than this between two requests is the only signal in the
// traffic that says "the human moved on to a different action".
const ACTION_GAP_SECONDS = 5;
// HOW LONG AN AGENT-DRIVEN ENDING WAITS BEFORE TEARING THE RECORDING DOWN (#98).
//
// Measured, not anticipated: a stub agent clicked a button, the fixture server
// logged the request, and raw.har did not contain it. An agent exits the instant
// its last action returns, and `stop()` then ran while the handler for that
// request was still awaiting its response body over CDP. A human takes seconds
// to walk back to the keyboard and press ENTER, which is why no ending before
// this one had the problem -- and the last response of a session is routinely the
// most valuable entry in it.
//
// The GRACE covers what cannot be observed: a request the browser has not yet
// reported as finished. The CAP covers a handler that never completes -- a body
// the browser will never hand over must not hold a capture hostage.
const AGENT_SETTLE_GRACE_MS = 500;
const AGENT_SETTLE_MAX_MS = 15000;
// HOW LONG AN EXITED AGENT'S OUTPUT IS WAITED FOR (#98). Its pipes normally
// drain within milliseconds of 'exit'; this bounds the case where a grandchild
// holds one open, so a stray process cannot keep the recording running.
const AGENT_DRAIN_MAX_MS = 5000;
// HOW LONG `claude --version` MAY TAKE before the agent counts as not usable.
const AGENT_PROBE_TIMEOUT_MS = 30000;

// ---------------------------------------------------------------------------
// Console levels
// ---------------------------------------------------------------------------

/**
 * Two thresholds, matching the only two states PowerShell can be in: with
 * `-Verbose` and without it. There is deliberately no `--quiet`: the caller
 * already has `-InformationAction SilentlyContinue` and a third level would be
 * a second way to say the same thing.
 */
const LOG_LEVELS = { normal: 1, verbose: 2 };

/**
 * The threshold at which each kind of message becomes visible. Warnings and
 * errors sit at 0 so no level can suppress them -- a leak-gate rejection that
 * an operator has to opt in to read is worse than no report at all.
 */
const LINE_LEVELS = { error: 0, warn: 0, info: 1, verbose: 2 };

let currentLevel = LOG_LEVELS.normal;

/**
 * Reject an unrecognized level rather than falling back to `normal`. Silently
 * substituting turns a typo into a working flag that ignores the operator.
 */
function setLogLevel(value) {
    if (!Object.prototype.hasOwnProperty.call(LOG_LEVELS, value)) {
        throw new Error(
            `--log-level must be one of ${Object.keys(LOG_LEVELS).join(', ')} (got '${value}')`);
    }
    currentLevel = LOG_LEVELS[value];
}

/**
 * Filter (level, text) pairs down to what one threshold shows, then join them.
 *
 * Building the console output as data and rendering it separately is what
 * makes "what does an operator see without -Verbose" a question a test can ask
 * without launching a browser or spawning a process.
 */
function renderLines(lines, levelName) {
    const threshold = levelName === undefined ? currentLevel : LOG_LEVELS[levelName];
    return lines
        .filter(([level]) => LINE_LEVELS[level] <= threshold)
        .map(([, text]) => text)
        .join('\n');
}

/**
 * Every human-facing message goes to stderr. stdout carries machine output --
 * the `--validate-only` and `status` JSON -- and prose mixed into it is what
 * makes `Invoke-HarCapture ... | ConvertFrom-Json` fail on a verbose run.
 *
 * `lines` is the only primitive; the per-level helpers are one-line pairs
 * through it, so the threshold is applied in exactly one place.
 */
const log = {
    lines: (pairs) => {
        const text = renderLines(pairs);
        if (text) process.stderr.write(text + '\n');
    },
    info: (text) => log.lines([['info', text]]),
    verbose: (text) => log.lines([['verbose', text]]),
    warn: (text) => log.lines([['warn', text]]),
    error: (text) => log.lines([['error', text]])
};

// The options `start` accepts. Exported so a test can assert that the two
// dropped ones are genuinely gone: silently accepting and ignoring `--dir`
// would leave an operator believing the raw capture had moved.
const START_OPTIONS = [
    'uri', 'profile', 'isolated', 'port', 'output-path', 'describe',
    'snapshot-seconds', 'no-wait', 'validate-only', 'log-level',
    'browser', 'attach', 'ai', 'ai-tools', 'pace'
];

/**
 * Is the driver that owns this session still running?
 *
 * Signal 0 sends nothing: it is the POSIX and Node idiom for "does this pid
 * exist and may I signal it". Nothing here ever terminates a process -- that
 * would discard the recording and, for a browser, could destroy unrelated
 * signed-in windows.
 *
 * Asking about the DRIVER pid rather than the debugging port matters: the
 * browser is a separate child process, and nothing in this design kills it, so
 * a crashed driver can leave a browser still answering on the port. Trusting
 * the port there reports a dead session as live. A port can also be answered
 * by an unrelated browser that happens to hold the same number.
 */
function isDriverAlive(session) {
    if (!session || !session.pid) return false;
    try {
        process.kill(session.pid, 0);
        return true;
    } catch (e) {
        // EPERM means it exists but belongs to someone else; only ESRCH means gone.
        return e.code === 'EPERM';
    }
}

/**
 * Ask a debugging port whether a browser is already there. Used to name a
 * running capture in a conflict message rather than to gate the launch --
 * a busy port is no longer fatal, because it falls forward.
 */
async function probeCdp(port) {
    return probeCdpEndpoint(`http://localhost:${port}`);
}

/**
 * The same question asked of an ENDPOINT rather than a port, because an
 * attached browser is named by one (#73) and the recorder never chose its host.
 *
 * `probeCdp` delegates here so there is one place that knows what answering
 * looks like. A non-2xx response is NOT an answer: Chrome serves
 * /json/version from the debugging endpoint and nothing else does, so a 404
 * from something else on that port means "not a browser", not "not ready".
 */
async function probeCdpEndpoint(endpoint) {
    try {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), CDP_PROBE_MS);
        const res = await fetch(`${endpoint}/json/version`, { signal: controller.signal });
        clearTimeout(timer);
        return res.ok ? await res.json() : null;
    } catch (e) {
        return null;   // nothing listening, which is the normal case
    }
}

/**
 * Wait for a browser to open its debugging port.
 *
 * `stillStarting` is the reason this is not a plain sleep-and-poll. The
 * everyday failure of a native launch is that chrome.exe HANDS OFF: point it at
 * a profile some other Chrome already has open and the new process forwards the
 * request to that instance and exits immediately, having applied none of our
 * flags. Nothing ever answers on the port, so a timeout-only wait spends the
 * full 30 seconds and then reports "the browser did not start" about a browser
 * that started and left. Asking whether our own child is still alive turns that
 * into an immediate, accurate failure.
 */
async function waitForCdp(endpoint, timeoutMs, opts = {}) {
    const probe = opts.probe || probeCdpEndpoint;
    const stillStarting = opts.stillStarting || (() => true);
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        const version = await probe(endpoint);
        if (version) return version;
        if (!stillStarting()) return null;
        await sleep(NATIVE_CDP_POLL_MS);
    }
    return null;
}

/**
 * The first free port at or after `start`.
 *
 * A busy port used to be a hard error because it doubled as the "a capture is
 * already running" detector. Those are two different concerns: several
 * captures can coexist happily on different ports, while a persistent profile
 * genuinely is single-instance. Only the second is a conflict, so only the
 * second still fails.
 */
function findFreePort(start, limit = PORT_SCAN_LIMIT) {
    const tryPort = (port) => new Promise((resolve) => {
        const server = net.createServer();
        server.once('error', () => resolve(false));
        server.once('listening', () => server.close(() => resolve(true)));
        server.listen(port, '127.0.0.1');
    });
    return (async () => {
        for (let port = start; port < start + limit; port++) {
            if (await tryPort(port)) return port;
        }
        throw new Error(`no free port in ${start}..${start + limit - 1}`);
    })();
}

function parseArgs(argv) {
    const out = { _: [] };
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--isolated' || a === '--validate-only' || a === '--no-wait' || a === '--ai') {
            out[a.slice(2)] = true;
            continue;
        }
        if (a.startsWith('--')) { out[a.slice(2)] = argv[++i]; continue; }
        out._.push(a);
    }
    return out;
}

function usage(msg) {
    if (msg) process.stderr.write(`capture-har: ${msg}\n`);
    process.stderr.write(
        'usage: node capture-har.js start --uri <url> --describe <text>\n' +
        '                                 [--isolated] [--port <n>] [--output-path <dir>]\n' +
        '                                 [--browser playwright|native] [--attach <cdp>] [--ai]\n' +
        '                                 [--pace off|human|nav=MIN-MAX,action=MIN-MAX,per-minute=N]\n' +
        '       node capture-har.js stop [--session <dir>] [--min-bytes <n>]\n' +
        '       node capture-har.js status\n' +
        '       node capture-har.js catalogue [<scrubbed.har|session dir|output dir>]\n' +
        '                                     [--session <dir>] [--output-path <dir>]\n' +
        '\n' +
        '  --browser native            start chrome.exe with no automation flags and\n' +
        '                              attach over CDP, for a site that refuses a\n' +
        '                              Playwright-launched browser.\n' +
        '  --attach <cdp>              record in a Chrome already running with\n' +
        '                              --remote-debugging-port. `stop` detaches only.\n' +
        '                              With either, only this capture\'s tabs are\n' +
        '                              recorded: the one opened for --uri, windows\n' +
        '                              opened from it, and tabs on the --uri\'s site.\n' +
        '                              Its debugging port stays open until that\n' +
        '                              window closes -- close it when done.\n' +
        '  --ai                        an agent drives the browsing while this process\n' +
        '                              records. Implies --browser native. The prompt is\n' +
        '                              generated and saved beside the capture, and so is\n' +
        '                              the agent\'s step log. The recording ends when the\n' +
        '                              agent finishes or hands back.\n' +
        '  --ai-tools auto|browser-only  with --ai: which tools the agent has. auto\n' +
        '                              (default): auto permission mode, its classifier\n' +
        '                              approves or blocks each action. browser-only:\n' +
        '                              every built-in tool removed (`--tools ""`), only\n' +
        '                              MCP tools remain. Use it when signed into\n' +
        '                              something sensitive, with only the browser MCP\n' +
        '                              configured.\n' +
        '  --log-level normal|verbose  how much the console says (default normal).\n' +
        '                              Invoke-HarCapture sets it from -Verbose.\n');
    return 2;
}

/**
 * Where raw captures live. FIXED, and deliberately not derived from any
 * option: the raw carries live session cookies, and the only reason the old
 * `--dir` existed was to move it -- which is exactly the leak this closes.
 *
 * Still no option redirects the NAME. What changed in #367 is WHICH
 * `.har-captures` -- the question the old wording never asked.
 *
 * It used to resolve against the working directory. Inside a linked worktree
 * that put an unrepeatable capture somewhere DISPOSABLE: `git worktree remove`
 * deletes the tree outright, the capture is gitignored so git reports nothing
 * to lose, and no prompt appears at any point. A 71 MB, 666-entry raw was
 * destroyed exactly that way by a correct, routine cleanup.
 *
 * So the root anchors to the repository's MAIN working tree, whose lifetime is
 * the clone's. Outside a repository the working-directory answer is the only
 * one there is and nothing changes -- which is also how a test harness contains
 * a capture, by choosing where node runs rather than by an override that would
 * reopen the redirect for everyone else.
 */
function capturePlacement(cwd) {
    return repoGuard.resolveCaptureRoot(cwd || process.cwd(), CAPTURES_DIR);
}

/**
 * Every root a capture might be FOUND under, newest convention first.
 *
 * Discovery is not resolution. `stop`, `status` and `catalogue` have to reach
 * captures written before this change -- including a recording still in flight
 * when the tool was updated, whose `current.json` sits in the old place. So
 * they read both roots and never write to, move, or tidy the old one: reading
 * is free, and relocating a raw is the one operation this pipeline will not
 * perform.
 */
function capturesSearchRoots(cwd) {
    const placement = capturePlacement(cwd);
    return placement.relocated ? [placement.root, placement.legacyRoot] : [placement.root];
}

/**
 * The directory name that stands for the captured site.
 *
 * HOST ONLY, for the same reason the digest reduces the URI to its origin: the
 * operator types this URL, and a magic-link, password-reset or signed start URL
 * carries its token in the path or the query. This name becomes a directory
 * sitting next to committable artifacts, so echoing any more of the URL would
 * put a live credential exactly where the design promises only scrubbed
 * artifacts land.
 *
 * The port is joined with `_` rather than `-`: a dash is legal inside a
 * hostname, so it could not be told apart from the host's own characters.
 * Periods are kept for the same reason -- they read as the host does.
 *
 * Throws rather than falling back to a shared name. An unparseable URI that
 * quietly collapsed to one folder would re-introduce the cross-site collision
 * this keying exists to remove.
 */
function uriFolder(uri) {
    let url;
    try {
        url = new URL(uri);
    } catch (e) {
        throw new Error(`cannot derive a capture folder: --uri ${JSON.stringify(uri)} is not a URL`);
    }

    // PARSING IS NOT ENOUGH. `new URL('http://../evil')` succeeds and its
    // hostname is '..'; `file:///x`, `data:...` and `about:blank` succeed with
    // an empty one. Used as a directory name, the first walks the raw capture
    // OUT of .har-captures/ and into the working tree, and the second collapses
    // every hostless capture into a single directory -- the very collision this
    // keying removes. Both are rejected on the host itself, before it is folded
    // into a name, so no later replacement can disguise them.
    const host = url.hostname;
    if (!host || host === '.' || host === '..') {
        throw new Error(
            `cannot derive a capture folder: --uri ${JSON.stringify(uri)} has no usable host ` +
            '(a capture needs a real site, e.g. https://example.com)');
    }

    // An IPv6 literal arrives bracketed (`[::1]`). Dropping the brackets and
    // mapping the separator to `-` -- which is already in the safe set -- keeps
    // distinct addresses distinct; folding every one of `[`, `:` and `]` to `_`
    // made addresses that differ only in their zero-groups converge.
    const bare = host.replace(/^\[|\]$/g, '').replace(/:/g, '-');
    const withPort = url.port ? `${bare}_${url.port}` : bare;
    const folded = withPort.toLowerCase().replace(/[^a-z0-9._-]/g, '_');

    // `con`, `nul`, `lpt1` and friends cannot be directories on Windows. A
    // trailing `_` keeps the name recognisable while making it creatable.
    if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|_|$)/i.test(folded)) return `${folded}_`;
    return folded;
}

/**
 * The two directories a capture writes to, and why they are different.
 *
 * `harPath` is under the fixed captures root, always. `outputPath` receives
 * only artifacts that have already been scrubbed and verified. Nothing an
 * operator passes can move the first, which is what makes the containment
 * structural rather than a rule somebody has to remember.
 *
 * Both are keyed on the captured host, so two captures against different sites
 * never land on top of each other -- `scrubbed.har`, `digest.json` and
 * `catalogue.json` are fixed filenames, and before this the second capture
 * silently overwrote the first.
 *
 * WHERE THE DEFAULT OUTPUT LANDS (#377, revising #300). It was the working
 * directory, then the repository root. Both were wrong for the same reason: the
 * root of whichever work tree the operator happened to be standing in is not a
 * place scrubbed artifacts belong, so a run from a checkout root dropped an
 * untracked `<host>/` directory there and #300's warning was simply stepped
 * past. Anchoring made placement PREDICTABLE, not correct -- its own comment
 * said so.
 *
 * The default is now THE RUN'S OWN SESSION DIRECTORY. That fixes three defects
 * at once and invents no new location:
 *
 *   - it is inside `.har-captures/`, which is gitignored by construction, so
 *     nothing lands untracked in a work tree;
 *   - it carries the STAMP, so a second capture against the same host no longer
 *     silently overwrites the first's `scrubbed.har`/`digest.json`;
 *   - the scrubbed artifact ends up beside the raw it came from, which is the
 *     structural link a committed reference needs back to its source.
 *
 * Nothing is lost by this: promotion into a committable directory is a separate,
 * deliberate step (see promoteReferenceLines), and the session directory is the
 * durable copy either way.
 *
 * An explicit `outputPath` still resolves against the WORKING DIRECTORY, because
 * a relative path the operator typed has to mean what they typed. It is warned
 * about, never refused -- see classifyOutputDestination.
 */
function resolveSessionPaths(opts = {}) {
    const cwd = opts.cwd || process.cwd();
    const placement = opts.capturesRoot ? null : capturePlacement(cwd);
    const root = opts.capturesRoot ? path.resolve(opts.capturesRoot) : placement.root;
    const folder = uriFolder(opts.uri);
    const sessionDir = path.join(root, folder, opts.stamp || stamp(new Date()));
    // The host folder is appended to an EXPLICIT root only. The session
    // directory is already keyed on the host one level up, and appending it
    // again would produce `<host>/<stamp>/<host>/`.
    const outputPath = opts.outputPath
        ? path.join(path.resolve(cwd, opts.outputPath), folder)
        : sessionDir;
    return {
        capturesRoot: root,
        // Null when a caller pinned the root itself (tests do), because there
        // is then nothing about the anchoring for the recorder to announce.
        capturePlacement: placement,
        // Where a session may be FOUND, as opposed to where this one is
        // written. Identical unless #367's anchoring moved the root.
        searchRoots: placement && placement.relocated ? [root, placement.legacyRoot] : [root],
        uriFolder: folder,
        sessionDir,
        harPath: path.join(sessionDir, RAW_HAR),
        recordLog: path.join(sessionDir, RECORD_LOG),
        outputPath,
        // Whether the operator NAMED this destination. The warning below fires
        // only for a deliberate choice; the default can no longer be the hazard.
        outputExplicit: Boolean(opts.outputPath)
    };
}

/**
 * The placement question for an output file, with the one destination that is
 * never the hazard taken out first (#471).
 *
 * ONE function for both `start` and `catalogue`, because they write the same
 * artifacts to the same kind of place and a second answer here is exactly the
 * drift the shared guard exists to prevent. `catalogue` used to ask a
 * location-only question of its own and so told operators to `mv` a file that
 * had never been misplaced.
 *
 * WHY THE CAPTURES ROOT IS EXEMPTED STRUCTURALLY rather than by asking
 * classifyDestination. It is gitignored -- but this tool is what makes it so,
 * via ensureCapturesRootIgnored, and that runs AFTER the guard, which has to
 * fire before anything is recorded. Asking git during the window between the
 * two gets "not ignored" for a directory that is about to be ignored, and the
 * default run is warned about for a hazard that never materialises. That is the
 * exact false warning #471 is about, so the exemption is the containment
 * invariant itself and not a shortcut: nothing an operator passes can move the
 * captures root, so nothing can move output out of the exemption either.
 *
 * The exemption is a FAST PATH, not the authority. `catalogue` can be pointed
 * at a session discovered under the LEGACY root (capturesSearchRoots, when
 * placement.relocated), which the current root will not match; that run falls
 * through to strandingPlacement and gets git's own answer, which is correct
 * either way. So the exemption is exhaustive for `start`, which resolves its
 * own root, and merely an optimisation for `catalogue` -- worth saying, because
 * a reader who assumed it was exhaustive everywhere would be surprised by a
 * legacy-rooted run taking the slow path.
 */
function placementForOutput(outputFile, capturesRoot) {
    if (capturesRoot) {
        const root = path.resolve(capturesRoot);
        const full = path.resolve(outputFile);
        if (full === root || full.startsWith(root + path.sep)) { return null; }
    }
    return repoGuard.strandingPlacement(outputFile);
}

/**
 * Is an EXPLICIT `--output-path` somewhere scrubbed artifacts will show up as
 * untracked files, and what to say about it?
 *
 * WARN, DO NOT REFUSE (#377). Hard-failing was right while the DEFAULT was the
 * hazard -- an operator who never typed a path got the bad location anyway.
 * Now that the default is the gitignored session directory, the only route to a
 * committable-but-unignored destination is a deliberate one, and refusing a
 * deliberate choice is user-hostile: pointing `--output-path` at
 * `docs/har-reference` is a legitimate thing to do, and the test suites do it.
 *
 * The question is asked through `classifyDestination()` and nowhere else. It
 * already wraps `git check-ignore`, and already refuses to trust a forged
 * `.gitignore` containing `*` or an injected `core.excludesFile`. A second
 * gitignore check written here would be a second answer free to disagree.
 *
 * Returns null when there is nothing to say -- outside a work tree (where
 * behaviour is unchanged), when the destination is ignored, and for the
 * default, which is never classified because it cannot be the problem.
 */
function outputDestinationWarning(paths) {
    if (!paths || !paths.outputExplicit) { return null; }
    // Classified on a FILE inside the destination, not on the directory: a
    // consumer's .gitignore covers these artifacts by name at any depth as well
    // as by directory, and asking about the directory would miss that.
    const status = subsDestination.classifyDestination(path.join(paths.outputPath, SCRUBBED_HAR));
    // IGNORED and OUTSIDE_WORK_TREE are the two answers that mean "nothing to
    // say". Everything else is said, UNVERIFIABLE included: git declining to
    // answer is not the same as answering no, and treating the two alike would
    // make the warning disappear in exactly the case where nobody can tell
    // whether it was needed.
    if (status === subsDestination.IGNORED
        || status === subsDestination.OUTSIDE_WORK_TREE) { return null; }
    const why = status === subsDestination.NOT_IGNORED
        ? '  It is inside a git work tree and is not gitignored -- the scrubbed capture, the\n' +
          '  digest and the catalogue will show as untracked files there.'
        : '  git did not answer whether it is gitignored, so nothing here can promise the\n' +
          '  scrubbed capture, the digest and the catalogue will stay out of version control.';
    return [
        `--output-path resolved to ${paths.outputPath}`,
        why,
        '  Proceeding: this is a destination you named. The session directory keeps its own',
        '  copy of everything either way.'
    ].join('\n');
}

// The gitignore entries a capture store needs. A SUBSET of the scaffolder's
// list, and passed to the scaffolder's own idempotent append rather than
// retyped into a second writer.
//
// Deliberately NOT a blanket `*.har`. `docs/har-reference/**/*.har` are the
// committed artifacts of this whole pipeline, and a blanket rule would make a
// NEW reference silently un-committable until somebody remembered `git add -f`.
// By name and by suffix is what the shipped .gitignore already does.
// The two substitution-table names come from subsDestination (already
// required above for the gitignore CHECK), not retyped here -- #387 made this
// the one other spot in this file that named them literally.
const CAPTURE_GITIGNORE_ENTRIES = [
    '.har-captures/',
    '.har-profile.json',
    subsDestination.LEGACY_SUBS_FILENAME,
    subsDestination.PII_SUBS_FILENAME
];

// The block says what it is FOR, and names only what it writes. The scaffolder's
// own header talks about HAR-Original/ and MobileApp-Binaries/, which this block
// does not contain -- a provenance note about files that are not there sends the
// next reader hunting.
const CAPTURE_GITIGNORE_HEADER = [
    '# Added by the HAR recorder (see skills/web-api-discovery/SKILL.md).',
    '# A raw capture carries live session cookies and is never scrubbed; the',
    '# substitution tables are keyed by the plaintext values the scrub replaced.'
];

/**
 * A path under the captures root that only a rule ABOUT THE STORE can ignore.
 *
 * Not `raw.har`: the shipped .gitignore also lists that by name, so probing
 * with it would report a store as protected on the strength of a rule that
 * covers one filename. The probe has to be a name nothing else claims.
 */
function capturesRootProbe(capturesRoot) {
    return path.join(capturesRoot, '.har-captures-ignore-probe');
}

/**
 * Refuse to record into a capture store that version control can see.
 *
 * The raw capture carries live session cookies and runs to hundreds of
 * megabytes. Unignored, it is one `git add -A` from a commit -- and unlike the
 * output side, there is no warn-and-proceed answer that is honest here: what
 * would be committed is unscrubbed.
 *
 * INTERACTIVE: prompt once, naming the resolved absolute path, saying it is in
 * the MAIN checkout (which is not where the operator is standing when they run
 * from a worktree), and saying why. Answering yes appends the rules and re-asks
 * git; the answer that matters is git's, not ours.
 *
 * NON-INTERACTIVE: hard failure. An agent or a CI run has nowhere to prompt,
 * and this is exactly the case worth stopping.
 *
 * OUTSIDE A WORK TREE: nothing to do. There is no repository for `git add` to
 * take the capture into, and behaviour there is unchanged by design.
 */
async function ensureCapturesRootIgnored(placement, opts = {}) {
    const isTty = !!opts.isTty;
    const ask = opts.ask || askLine;
    const capturesRoot = placement && placement.root;
    if (!capturesRoot) { return { ok: true, status: null }; }
    const probe = capturesRootProbe(capturesRoot);
    const status = subsDestination.classifyDestination(probe);
    if (status === subsDestination.IGNORED) { return { ok: true, status }; }

    // OUTSIDE A WORK TREE: leave the rule behind for the repository that does
    // not exist yet (#512).
    //
    // "Not a repository" is usually "not a repository YET". The skill's own
    // generation pipeline runs `git init && git add -A && git commit` in this
    // same folder a few phases later, and an operator starting a new site does
    // it by hand for the same reason. At that moment the salt and a directory
    // of unscrubbed raw captures are both untracked files in the folder being
    // swept, and nobody is watching.
    //
    // WRITTEN, NOT PROMPTED. There is no repository, so no tracked file is
    // being changed and there is nothing to ask permission about; a prompt
    // here would also make every agent-driven capture in a fresh folder
    // refuse. The append is the scaffolder's own idempotent one, so a folder
    // that already has a `.gitignore` keeps every line of it.
    //
    // The file goes beside the store rather than inside it: a rule inside
    // `.har-captures/` cannot exclude `.har-captures/` itself, and the profile
    // it also covers is the store's sibling.
    //
    // THEN OFFER THE REPOSITORY ITSELF. The scrub writes substitution tables --
    // reverse lookups of live credentials -- and refuses to derive their home
    // outside a work tree, because nothing there keeps them out of a commit.
    // So a terminal operator is asked, default yes, and `git init` makes the
    // rule just written take effect. On no, or with no terminal to ask (an AI
    // agent -- the skill tells it to ask the user in the conversation first),
    // the capture proceeds anyway: `unprotected` tells postProcess to place the
    // tables in the session directory by name, and the caller warns once.
    if (status === subsDestination.OUTSIDE_WORK_TREE) {
        const beside = path.dirname(capturesRoot);
        const added = ensureRepoRootGitignoreHasScaffoldEntries(
            beside, CAPTURE_GITIGNORE_ENTRIES, CAPTURE_GITIGNORE_HEADER);
        const ignoreFile = path.join(beside, '.gitignore');
        if (isTty) {
            const answer = await ask(`capture-har: ${beside} is not a git repository.\n` +
                "  Initialize one so the capture's secrets can never be committed? [Y/n] ");
            if (!/^n/i.test((answer || '').trim())) {
                const init = (opts.gitInit || gitInit)(beside);
                const after = init ? subsDestination.classifyDestination(probe) : status;
                if (after === subsDestination.IGNORED) {
                    return { ok: true, status: after, ignoreFile, added, initialized: beside };
                }
                return {
                    ok: false,
                    status: after,
                    message: init
                        ? `capture-har: initialized a repository in ${beside}, but git does not ` +
                          `report the capture store as ignored (${after}). Nothing was recorded.`
                        : `capture-har: git init failed in ${beside}. Nothing was recorded.`
                };
            }
        }
        return { ok: true, status, ignoreFile, added, unprotected: true };
    }

    const anchor = placement.mainWorkingTree || placement.currentWorkingTree;
    // "not ignored" and "git declined to answer" are different facts, and
    // telling an operator the first when the second happened sends them to edit
    // a .gitignore that was never the problem. Both still refuse: an unverified
    // destination is not assumed to be a safe one.
    const unverified = status === subsDestination.UNVERIFIABLE;
    const why = [
        unverified
            ? `capture-har: git did not answer whether the capture store is ignored: ${capturesRoot}`
            : `capture-har: the capture store is not gitignored: ${capturesRoot}`,
        '  A raw capture carries live session cookies and is never scrubbed -- unignored,',
        '  it is one `git add -A` away from being committed.',
        anchor ? `  The store belongs to the MAIN working tree (${anchor}), not to the` : null,
        anchor ? '  worktree you may be standing in.' : null
    ].filter(Boolean).join('\n');

    if (unverified) {
        // There is nothing to prompt about: appending a rule cannot help when
        // the answer could not be read in the first place.
        return {
            ok: false,
            status,
            message: `${why}\n` +
                '  Make git available on PATH and re-run. An unverified destination is not\n' +
                '  assumed to be a safe one.'
        };
    }

    if (!isTty) {
        return {
            ok: false,
            status,
            message: `${why}\n` +
                `  Add ${CAPTURE_GITIGNORE_ENTRIES.map((e) => `'${e}'`).join(', ')} to that\n` +
                "  repository's .gitignore and re-run. This run is refused rather than\n" +
                '  prompted: there is no terminal here to answer, and an unscrubbed capture\n' +
                '  inside a work tree is the one case worth stopping for.'
        };
    }

    const answer = await ask(`${why}\n  Append ${CAPTURE_GITIGNORE_ENTRIES.join(', ')} to ` +
        `${path.join(anchor || capturesRoot, '.gitignore')} now? [y/N] `);
    if (!/^y/i.test((answer || '').trim())) {
        return { ok: false, status, message: 'capture-har: declined -- nothing was recorded.' };
    }
    ensureRepoRootGitignoreHasScaffoldEntries(anchor || capturesRoot, CAPTURE_GITIGNORE_ENTRIES,
        CAPTURE_GITIGNORE_HEADER);
    // Ask git again rather than assuming the append worked. A rule that does
    // not take effect -- a negation later in the file, a nested .gitignore --
    // must not be reported as protection.
    const after = subsDestination.classifyDestination(probe);
    if (after === subsDestination.IGNORED) { return { ok: true, status: after, appended: true }; }
    return {
        ok: false,
        status: after,
        message: 'capture-har: the entries were appended but git still does not report the ' +
            `store as ignored (${after}). Nothing was recorded.`
    };
}

/**
 * The SHORT provider name for a host: `www.facebook.com` -> `facebook`.
 *
 * The reference-naming convention repeats the provider in the filename as well
 * as in the directory, because the directory is invisible the moment the file
 * is opened in an editor tab, attached to an issue, or pasted into a diff.
 */
// Second-level labels that are part of the suffix rather than the name:
// `example.co.uk` must give `example`, not `co`. Deliberately a short list and
// not the public suffix list -- this names a file a human then reads and can
// rename, so the cost of a miss is cosmetic, and a megabyte of suffix data
// vendored into a recorder to improve a default filename is not a trade worth
// making.
const SUFFIX_LABELS = new Set(['co', 'com', 'net', 'org', 'gov', 'edu', 'ac', 'or', 'ne', 'go']);

function providerSlug(host) {
    const labels = String(host || '').split('.').filter(Boolean)
        .filter((l, i) => !(i === 0 && l === 'www'));
    if (!labels.length) { return null; }
    // The registrable label, not the TLD: `app.example.com` -> `example`.
    let index = labels.length >= 2 ? labels.length - 2 : 0;
    if (index > 0 && SUFFIX_LABELS.has(labels[index])) { index -= 1; }
    return slugify(labels[index]) || null;
}

function slugify(value) {
    // Character-for-character the extractor's own slug(), because the name this
    // prints has to be the name the extractor produces.
    return String(value).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
}

// Words that carry no information in an action slug. Trimming them is what
// turns a sentence into a name a human would have written.
const ACTION_STOPWORDS = new Set([
    'a', 'an', 'the', 'and', 'then', 'with', 'of', 'to', 'for', 'in', 'on', 'my',
    'that', 'this', 'it', 'its', 'at', 'by', 'from', 'into'
]);
const ACTION_SLUG_WORDS = 3;

/**
 * A short action slug derived from `--describe`, for the operator to accept or
 * replace.
 *
 * Deriving SILENTLY from a whole sentence yields
 * `facebook-create-a-post-with-an-audience-selection-then-delete-it-2026-09-01.har`
 * -- legal, and not a name anybody would write for a file that gets read in
 * issues and diffs. So the derivation is a DEFAULT, not an answer.
 */
function deriveActionSlug(describe, provider) {
    let text = String(describe || '');
    // `facebook: create a post ...` -- the operator already named the provider,
    // and repeating it would give `facebook-facebook-...`.
    const colon = text.indexOf(':');
    if (colon > 0 && provider && slugify(text.slice(0, colon)).includes(provider)) {
        text = text.slice(colon + 1);
    }
    const words = slugify(text).split('-')
        .filter(Boolean)
        .filter((w) => !ACTION_STOPWORDS.has(w));
    const picked = words.slice(0, ACTION_SLUG_WORDS);
    return picked.length ? picked.join('-') : 'capture';
}

function todayStamp(now) {
    return (now || new Date()).toISOString().slice(0, 10);
}

function uriHostOf(uri) {
    try { return new URL(uri).hostname; } catch (e) { void e; return null; }
}

/**
 * `group.host` minus a trailing `:<port>`, so it slugs the same way
 * `uriHostOf` does.
 *
 * `group.host` comes from `URL#host` (host **plus** port, e.g.
 * `localhost:3000`), because `Endpoints` needs the port to name the
 * endpoint precisely. `uriHostOf` -- what `referenceRelativePath` slugs to
 * pick the `<provider>/` directory -- comes from `URL#hostname` (host
 * only). Slugging `group.host` unstripped would turn every port-bearing
 * host, most commonly a local dev server, into a `Provider` that
 * disagrees with that directory: `localhost:3000` -> `localhost-3000`
 * against a directory named `localhost`. Stripping the port first is what
 * keeps the two in agreement for exactly the hosts that would otherwise
 * differ only by it.
 */
function stripPort(host) {
    return String(host || '').replace(/:\d+$/, '');
}

/**
 * The reference filename this capture's extract would take.
 *
 * `<provider>-<action>-<yyyy-MM-dd>.har`, which is exactly what
 * `har/extract-har-reference.js` writes. This names it; it does not invent a
 * second convention, and it does not write anything.
 *
 * The provider is in the FILENAME as well as the directory it lands in. The
 * directory is invisible the moment the file is opened in an editor tab,
 * attached to an issue, or pasted into a diff, and a reference that cannot
 * say what provider it describes once separated from its folder is a
 * reference nobody can place.
 */
function referenceFileName(session, opts = {}) {
    const provider = providerSlug(uriHostOf(session && session.uri));
    if (!provider) { return null; }
    const action = opts.action || deriveActionSlug(session && session.describe, provider);
    return `${provider}-${action}-${todayStamp(opts.now)}.har`;
}

/**
 * The same reference, as `<provider>/<filename>`.
 *
 * This -- not the bare filename -- is what `HarFile` records, because the
 * field's job is to survive promotion. #379 defines `HarFile` as a path
 * RELATIVE TO the committed `catalogue.json`, which sits at
 * `docs/har-reference/<host>/` while the extract sits one level deeper in
 * `<provider>/`. A bare filename would be ambiguous the moment a host carries
 * two providers, and `verify-har-catalogue.js` resolves the value against the
 * catalogue's own directory, so a bare filename would simply not resolve.
 *
 * Forward slash, always: this is a value written into JSON and read back on
 * every platform, not a path built for the local filesystem.
 */
function referenceRelativePath(session, opts = {}) {
    const provider = providerSlug(uriHostOf(session && session.uri));
    const fileName = referenceFileName(session, opts);
    if (!provider || !fileName) { return null; }
    return `${provider}/${fileName}`;
}

/**
 * The capture profile deliberately lives OUTSIDE the repo and is shared by
 * every capture: signing in once is the whole point, and a per-session
 * profile would ask the operator to re-authenticate on every recording. It is
 * never the operator's daily browser profile -- that one holds live sessions
 * this tool must not disturb.
 */
function captureProfileRoot() {
    const base = process.env.LOCALAPPDATA
        || process.env.XDG_DATA_HOME
        || path.join(os.homedir(), '.local', 'share');
    return path.join(base, 'har-capture');
}

function defaultProfileDir() {
    if (process.env.HAR_CAPTURE_PROFILE) return process.env.HAR_CAPTURE_PROFILE;
    return path.join(captureProfileRoot(), 'profile');
}

/**
 * --profile takes a NAME or a PATH, because the two real needs are different.
 *
 * A name keeps several signed-in identities side by side without the operator
 * having to know or type where profiles live.
 *
 * A path records as an identity some other tool already owns. Projects that
 * key browser profiles off their own concept -- a workspace, an account, a
 * tenant -- compute that directory themselves; this script cannot know the
 * rule, and should not pretend to. Handing it a path is the whole integration.
 */
function resolveProfileDir(value) {
    if (!value) return defaultProfileDir();
    if (path.isAbsolute(value) || value.includes('/') || value.includes('\\')) {
        return path.resolve(value);
    }
    const safe = value.trim().toLowerCase().replace(/[^a-z0-9]/g, '-');
    return path.join(captureProfileRoot(), `profile-${safe}`);
}

/**
 * Find the nearest serialized session, the same upward walk that discovers the
 * operator profile. The capability that `--storage-state` provided survives;
 * the option does not, because a path the operator had to retype on every
 * capture was never carrying information the filesystem could not.
 *
 * An absent storage state is NOT an error -- the default path is a persistent
 * profile that is already signed in.
 */
function discoverStorageState(startDir, stopAt) {
    return harProfile.findUpward(STORAGE_STATE_FILENAME, startDir, stopAt);
}

// ---------------------------------------------------------------------------
// A browser Playwright did not launch (#73)
// ---------------------------------------------------------------------------

/**
 * Which browser this run records in, from `--browser` and `--attach`.
 *
 * Resolved in one place and BEFORE anything is written, because the answer
 * decides what the dependency preflight has to insist on: a native or attached
 * Chrome needs the playwright MODULE and not its Chromium build, and demanding
 * a browser download to drive the browser the machine already has would be a
 * refusal with no cause.
 *
 * Every combination below is refused rather than silently resolved. `--isolated`
 * means "bundled Chromium, throwaway profile", which is the exact launch a
 * protected site rejects -- so honouring one flag while the operator typed both
 * would record in the browser they were trying to avoid, and the evidence would
 * be a 403 they then spend an afternoon on.
 */
function resolveBrowserMode(args) {
    // `--ai` MOVES THE DEFAULT (#98), it does not add a third mode.
    //
    // An agent driving a Playwright-launched browser is an agent driving the one
    // launch the protected sites in the table above refuse outright -- which are
    // the sites `--ai` was built for. So the implication is part of the switch
    // rather than something the operator has to remember to type, and the two
    // combinations that contradict it are refused below rather than resolved by
    // precedence. Precedence here would silently record in the browser the
    // operator was avoiding, and the evidence would be a 403 an afternoon later.
    const ai = !!args.ai;
    const asked = args.browser === undefined
        ? (ai ? BROWSER_NATIVE : BROWSER_PLAYWRIGHT)
        : String(args.browser).trim().toLowerCase();
    if (!BROWSER_MODES.includes(asked)) {
        return { error: `--browser takes ${BROWSER_MODES.join(' or ')}, not '${args.browser}'` };
    }
    // Named BEFORE the --isolated/--attach rules below so the message quotes the
    // flags the operator actually typed. Without this, `--ai --isolated` would be
    // refused as "--browser native and --isolated are exclusive" -- true, and
    // about a flag they never wrote.
    if (ai && args.isolated) {
        return {
            error: '--ai and --isolated are exclusive. --isolated launches Playwright\'s bundled' +
                '\n  Chromium, which IS the launch a protected site refuses -- and an agent ' +
                'driving\n  that browser is an agent driving the refusal'
        };
    }
    if (ai && asked !== BROWSER_NATIVE) {
        return {
            error: `--ai and --browser ${asked} are exclusive: --ai implies --browser native.` +
                '\n  Playwright\'s launch is detectable and the sites worth driving an agent at ' +
                'refuse\n  it, so there would be nothing to drive'
        };
    }
    const attaching = args.attach !== undefined;
    if (!attaching) {
        if (asked === BROWSER_NATIVE && args.isolated) {
            return {
                error: '--browser native and --isolated are exclusive. --isolated launches ' +
                    "Playwright's\n  bundled Chromium, which IS the launch a protected site refuses"
            };
        }
        return { browser: asked, attached: false };
    }
    if (args.isolated) {
        return {
            error: '--attach and --isolated are exclusive. --isolated launches a throwaway ' +
                'browser;\n  --attach records in one that is already running'
        };
    }
    if (args.browser !== undefined && asked !== BROWSER_NATIVE) {
        return {
            error: `--attach cannot be combined with --browser ${asked}: an attached browser ` +
                'is not\n  one Playwright launched. --attach already implies --browser native'
        };
    }
    // --port and --profile are not merely redundant here, they are unanswerable:
    // the browser is already running, so its debugging port and its profile are
    // facts to be read rather than choices to be made. Accepting either would
    // mean accepting a value this run cannot honour.
    if (args.port !== undefined) {
        return { error: '--attach and --port are exclusive: the endpoint already names the port' };
    }
    if (args.profile !== undefined) {
        return {
            error: '--attach and --profile are exclusive: the browser you are attaching to is ' +
                'already\n  running on a profile, and this run cannot change it'
        };
    }
    const parsed = parseCdpEndpoint(args.attach);
    if (parsed.error) return { error: parsed.error };
    return {
        browser: BROWSER_NATIVE,
        attached: true,
        endpoint: parsed.endpoint,
        port: parsed.port
    };
}

/**
 * Normalize whatever the operator had in hand into an endpoint and a port.
 *
 * Three spellings, because all three are things people actually have: the URL
 * Chrome prints, a host:port pair, and the bare port they typed after
 * `--remote-debugging-port`.
 *
 * A `ws://` URL is REFUSED with its own message rather than coerced. It is the
 * DevTools socket, not the endpoint `connectOverCDP` wants, and it carries a
 * target id that stops working the moment anything else connects -- so
 * accepting it would produce an intermittent failure a long way from the flag
 * that caused it.
 */
function parseCdpEndpoint(value) {
    const raw = String(value === undefined || value === null ? '' : value).trim();
    if (!raw) {
        return { error: '--attach needs the endpoint, e.g. --attach http://127.0.0.1:9222' };
    }
    if (/^wss?:\/\//i.test(raw)) {
        return {
            error: 'that is the DevTools websocket, not the debugging endpoint. Pass the HTTP ' +
                'one\n  (http://127.0.0.1:9222) -- the ws:// URL names one target and expires'
        };
    }
    if (/^\d+$/.test(raw)) {
        const port = Number(raw);
        if (port < 1 || port > 65535) return { error: `${raw} is not a port` };
        return { endpoint: `http://127.0.0.1:${port}`, port };
    }
    let url;
    try {
        url = new URL(/^https?:\/\//i.test(raw) ? raw : `http://${raw}`);
    } catch (e) {
        void e;
        return { error: `could not read '${raw}' as a CDP endpoint` };
    }
    if (!url.port) {
        return {
            error: `'${raw}' names no port, and Chrome's debugging endpoint always has one ` +
                '(the\n  value you gave --remote-debugging-port)'
        };
    }
    return { endpoint: `${url.protocol}//${url.host}`, port: Number(url.port) };
}

/**
 * Chrome's own installation paths, in the order a machine is likely to have
 * them.
 *
 * NO NEW OPTION AND NO NEW ENVIRONMENT VARIABLE for "where is Chrome", on
 * node-dependency.js's reasoning: when this list cannot answer, the operator
 * already has a better lever than one more path to type -- they start Chrome
 * themselves and pass `--attach`, which is a mode this file supports anyway. So
 * the failure names every path it tried and then names that lever.
 *
 * Chromium is listed last on Linux and nowhere else. It is the same engine and
 * will record, but it is not the browser whose fingerprint the sites in the
 * table above accepted, so it is a last resort rather than an equal.
 */
function chromeCandidates(opts = {}) {
    const env = opts.env || process.env;
    const platform = opts.platform || process.platform;
    const home = opts.homedir === undefined ? os.homedir() : opts.homedir;
    if (platform === 'win32') {
        const suffix = ['Google', 'Chrome', 'Application', 'chrome.exe'];
        return [env['ProgramFiles'], env['ProgramFiles(x86)'], env.LOCALAPPDATA]
            .filter(Boolean)
            .map((root) => path.join(root, ...suffix));
    }
    if (platform === 'darwin') {
        const app = path.join('Google Chrome.app', 'Contents', 'MacOS', 'Google Chrome');
        return [
            path.join('/Applications', app),
            home ? path.join(home, 'Applications', app) : null
        ].filter(Boolean);
    }
    return [
        '/opt/google/chrome/chrome',
        '/usr/bin/google-chrome',
        '/usr/bin/google-chrome-stable',
        '/usr/bin/chromium',
        '/usr/bin/chromium-browser'
    ];
}

/**
 * The first candidate that is actually a file, and the whole list either way.
 *
 * The list comes back on the failure path too, because "not found" is only
 * actionable alongside where it was looked for -- the same contract
 * node-dependency.js holds itself to for the module.
 */
function findChromeExecutable(opts = {}) {
    const candidates = chromeCandidates(opts);
    for (const candidate of candidates) {
        try {
            if (fs.statSync(candidate).isFile()) return { path: candidate, candidates };
        } catch (e) {
            void e;   // absent, which is the ordinary answer for most of the list
        }
    }
    return { path: null, candidates };
}

/**
 * Start Chrome the way a person does, and get out of the way.
 *
 * THE ARGUMENT LIST IS THE FEATURE, so every entry is accounted for:
 *
 *   --user-data-dir            a dedicated profile. Not a nicety: Chrome
 *                              refuses remote debugging on a profile another
 *                              Chrome already has open, and pointing this at
 *                              the operator's daily profile would put a
 *                              recorder inside every session they have running.
 *   --remote-debugging-port    the only reason this process is involved at all.
 *   --no-first-run             a dedicated profile is a FRESH profile, so the
 *                              first-run experience would otherwise stand in
 *                              front of the site on every new one.
 *   --no-default-browser-check same: a modal about switching to a browser the
 *                              operator did not ask to switch to.
 *
 * AND NOTHING ELSE. No --disable-blink-features, no --disable-extensions, no
 * automation switches, no headless. Those are the marks this mode exists to
 * avoid; the two suppressions above are first-run UI, not automation, and
 * `navigator.webdriver` stays false -- which is the property that matters and
 * is asserted in the suite rather than assumed here.
 *
 * DETACHED, AND NEVER WAITED ON. In the Playwright mode this process outlives
 * the browser. Here the browser outlives US, because `stop` detaches and leaves
 * it running -- so tying the two lifetimes together is exactly what must not
 * happen: it would end a recording by taking down a window full of the
 * operator's signed-in tabs.
 */
function launchNativeChrome(options) {
    const { executable, profileDir, port } = options;
    const args = [
        `--user-data-dir=${profileDir}`,
        `--remote-debugging-port=${port}`,
        '--no-first-run',
        '--no-default-browser-check',
        // Opened blank ON PURPOSE. The site is navigated to after the attach, so
        // its first request -- the one carrying the challenge, the cookies and
        // the redirect chain -- is recorded. Handing the URL to the command line
        // would load it while nothing was listening.
        'about:blank'
    ];
    const child = (options.spawn || spawn)(executable, args, {
        detached: true,
        stdio: 'ignore'
    });
    child.unref();
    return { child, args };
}

// Folders a consumer sync client watches. A browser profile is a set of SQLite
// databases Chrome writes continuously, so a sync client copying one mid-write
// corrupts the profile -- and carries live session cookies up to a cloud
// account on the way.
const SYNCED_FOLDER_PATTERNS = [
    { re: /(^|[\\/])OneDrive([\\/ -]|$)/i, name: 'OneDrive' },
    { re: /(^|[\\/])Dropbox([\\/]|$)/i, name: 'Dropbox' },
    { re: /(^|[\\/])Google Drive([\\/]|$)/i, name: 'Google Drive' },
    { re: /(^|[\\/])iCloud ?Drive([\\/]|$)/i, name: 'iCloud Drive' },
    { re: /(^|[\\/])Box( Sync)?([\\/]|$)/i, name: 'Box' }
];

/**
 * Warn, never refuse: the operator named this path.
 *
 * Matched on the PATH rather than by asking a sync client, because there is no
 * portable way to ask one and a name match is the signal an operator can check
 * for themselves. A false positive costs one warning line about a folder that
 * happens to be called Dropbox; a false negative costs a corrupted profile and
 * a cookie jar in somebody's cloud.
 */
function syncedFolderWarning(dir) {
    if (!dir) return null;
    const target = path.resolve(dir);
    const hit = SYNCED_FOLDER_PATTERNS.find((p) => p.re.test(target));
    if (!hit) return null;
    return `the browser profile is inside what looks like a ${hit.name} folder:\n` +
        `  ${target}\n` +
        '  A profile is a set of databases the browser writes continuously. A sync\n' +
        '  client copying one mid-write corrupts it, and carries live session cookies\n' +
        '  to a cloud account on the way. Prefer a local path.';
}

const PROFILE_GITIGNORE_HEADER = [
    '# Added by the HAR recorder (see skills/web-api-discovery/SKILL.md).',
    '# A browser profile holds live session cookies for the account the capture',
    '# was recorded as.'
];

/**
 * A browser profile inside a work tree gets an ignore rule, WRITTEN rather than
 * prompted for and never refused.
 *
 * Both departures from ensureCapturesRootIgnored are deliberate, and both come
 * from the same difference: that path is ours and this one is the operator's.
 *
 * WHY IT DOES NOT REFUSE. The capture store's path is fixed, is where an
 * unscrubbed raw is about to land, and nobody chose it. A profile path only
 * reaches here because somebody TYPED it, and this file has a settled answer
 * for a destination somebody deliberately named -- warn, never refuse (see
 * outputDestinationWarning). Refusing would also arrive too late to help:
 * the directory has to exist before the browser can use it.
 *
 * WHY IT DOES NOT PROMPT. The store prompts because refusing is the
 * alternative. There is no refusal here, so the question would have one
 * meaningful answer -- and appending an ignore rule for a directory this run
 * just created changes nobody's work.
 *
 * git is asked AGAIN afterwards, for the reason the store's guard gives: a rule
 * that does not take effect (a later negation, a nested .gitignore) must never
 * be reported as protection.
 */
function ensureProfileIgnored(profileDir) {
    const probe = path.join(profileDir, '.browser-profile-ignore-probe');
    const status = subsDestination.classifyDestination(probe);
    if (status !== subsDestination.NOT_IGNORED) return { status };
    const anchor = repoGuard.inspectCheckout(profileDir).topLevel;
    if (!anchor) return { status };
    const rel = path.relative(anchor, profileDir).split(path.sep).join('/');
    // The repository root itself is not a profile directory, and `/` is not a
    // rule anybody wants written. Nothing reaches this in practice; it is here
    // so that if something does, it does nothing.
    if (!rel || rel.startsWith('..')) return { status };
    // Rooted with a leading slash so the rule cannot also match a same-named
    // directory elsewhere in the tree, and closed with a trailing one because
    // what is ignored is a directory.
    const entry = `/${rel}/`;
    const added = ensureRepoRootGitignoreHasScaffoldEntries(
        anchor, [entry], PROFILE_GITIGNORE_HEADER);
    const after = subsDestination.classifyDestination(probe);
    return {
        status: after,
        entry,
        added,
        ignoreFile: path.join(anchor, '.gitignore'),
        ineffective: after !== subsDestination.IGNORED
    };
}

const PLAYWRIGHT_MODULE = nodeDep.PLAYWRIGHT;

/**
 * Ask the four locations for the browser driver (#512).
 *
 * The search itself lives in node-dependency.js so this file and capture-cdp.js
 * cannot drift apart; all this adds is THIS file's location as the tool one.
 */
function resolvePlaywright(opts) {
    return nodeDep.resolvePlaywright(Object.assign({ toolDir: __dirname }, opts || {}));
}

/**
 * The remedies for whatever is actually missing, as commands.
 *
 * The module and the browser are SEPARATE facts and get separate commands.
 * `npm install` does not fetch a browser binary, so a message that offered one
 * command for both would be followed, appear to succeed, and fail again on the
 * same line.
 */
function dependencyInstallCommands(moduleMissing, browserMissing) {
    const commands = [];
    if (moduleMissing) commands.push(['npm', ['install', '-g', PLAYWRIGHT_MODULE]]);
    if (browserMissing) commands.push(['npx', [PLAYWRIGHT_MODULE, 'install', 'chromium']]);
    return commands;
}

/**
 * Run an install the operator agreed to. Inherits stdio so they watch it
 * happen: an install that takes a minute behind a silent spinner is
 * indistinguishable from a hang.
 *
 * `started` is carried back because "never launched" and "launched and failed"
 * need different words, and on Windows the first is a real possibility -- Node
 * refuses to spawn npm's batch shim without a shell. Telling an operator their
 * install "ran" when no process ever started sends them to debug the wrong
 * thing entirely.
 */
function runInstall(command, args) {
    return nodeDep.runCommand(command, args, { stdio: 'inherit' });
}

function dependencyMessage(resolved, browserPresent, env) {
    if (!resolved.found) {
        return 'capture-har: ' +
            nodeDep.describeMissing(PLAYWRIGHT_MODULE, resolved, { browser: browserPresent });
    }
    // Named, not merely described. "Playwright's default location" is a phrase
    // the operator then has to go and look up; the path is the answer.
    const where = nodeDep.browsersPath({ env: env || process.env });
    return 'capture-har: the playwright module is installed, but its Chromium build is not.\n' +
        `  ${nodeDep.BROWSER_INSTALL_COMMAND}\n` +
        '  It installs once per machine' + (where ? `, at ${where}` : '') + '.';
}

/**
 * IS THE BROWSER DRIVER HERE -- asked before anything else happens (#512).
 *
 * The owner's transcript is the specification. A run scaffolded a profile,
 * announced a capture root, and THEN reported the missing module, with a
 * remedy that could not work. Everything before the failure was work to undo.
 *
 * So this is the first gate in `start`, ahead of the placement guard, the
 * profile prompt, the port scan and the browser. Nothing is written and
 * nothing is asked until the run is known to be able to finish.
 *
 * INTERACTIVE: offer to install into the machine default, so one install
 * serves every folder afterwards. NON-INTERACTIVE: print the same locations
 * and the same commands and refuse -- an agent has nowhere to answer, which is
 * how every other guard in this file behaves.
 *
 * `requireBrowser: false` asks only about the MODULE, and is what a native or
 * attached run passes (#73). Those modes drive the Chrome this machine already
 * has, so Playwright's bundled Chromium is not a dependency of theirs -- and a
 * gate that demanded a browser download in order to use an installed browser
 * would be exactly the unfollowable advice #512 set out to remove.
 */
async function preflightDependencies(opts = {}) {
    const isTty = !!opts.isTty;
    const ask = opts.ask || askLine;
    const install = opts.install || runInstall;
    const env = opts.env || process.env;
    const requireBrowser = opts.requireBrowser !== false;
    // Both facts, re-read together. Called again after an install, because the
    // only honest way to know whether an install worked is to ask the same
    // question a second time.
    const check = () => ({
        resolved: resolvePlaywright(Object.assign({}, opts, { env })),
        browser: requireBrowser
            ? nodeDep.chromiumPresent({ env, platform: opts.platform, homedir: opts.homedir })
            : true
    });

    let state = check();
    if (state.resolved.found && state.browser) return { ok: true };

    const message = dependencyMessage(state.resolved, state.browser, env);
    const commands = dependencyInstallCommands(!state.resolved.found, !state.browser);

    if (isTty && commands.length > 0) {
        // DEFAULT YES, matching the capture store's own `git init` prompt rather
        // than the refusals. The two shapes in this file are not arbitrary: a
        // prompt that can LOSE something defaults to no, and a prompt that is
        // merely the next step defaults to yes. Installing the driver is the
        // second kind -- an operator who has just been told the recorder cannot
        // run without it, and who is standing at a terminal answering the
        // question, has already decided. Making them type `y` to proceed and
        // ENTER to abandon the run inverts the cheap and the expensive answer:
        // ENTER is what a hurried person presses, and here that meant reading
        // the remedy, agreeing with it, and then being refused anyway.
        const answer = await ask(`${message}\n  Install it now? [Y/n] `);
        if (!/^n/i.test((answer || '').trim())) {
            let neverStarted = null;
            for (const [command, args] of commands) {
                const r = install(command, args) || {};
                // `started === false` means no process was created at all --
                // on Windows, Node refuses to spawn npm's batch shim without a
                // shell. Reporting that as a failed install sends the operator
                // to debug an install that never began.
                if (r.started === false && !neverStarted) { neverStarted = command; }
            }
            // ASK AGAIN rather than trusting the exit code. An install that
            // reports success and produces nothing is the case worth catching:
            // believing it hands the operator a browser launch failure several
            // steps later, with no mention of the install that did not work.
            state = check();
            if (state.resolved.found && state.browser) return { ok: true };
            return {
                ok: false,
                message: dependencyMessage(state.resolved, state.browser, env) + '\n' +
                    (neverStarted
                        ? `  ${neverStarted} could not be started at all -- it is not on PATH, ` +
                          'or this\n  shell cannot launch it. Run the command above yourself. ' +
                          'Nothing was recorded.'
                        // On Windows `started` only tells us the INTERPRETER
                        // started. If npm itself is absent from PATH, cmd.exe
                        // runs, prints "not recognized" and exits 1 -- a
                        // normal exit, indistinguishable here from an install
                        // that ran and did not work. So this wording covers
                        // both rather than asserting the one it cannot know.
                        : '  The install did not produce a usable module -- npm may not be ' +
                          'on PATH, or\n  it did not do what it reported. Run the command ' +
                          'above yourself. Nothing was recorded.')
            };
        }
    }
    return { ok: false, message };
}

function requirePlaywright() {
    const resolved = resolvePlaywright();
    if (!resolved.found) {
        process.stderr.write(
            dependencyMessage(resolved, nodeDep.chromiumPresent({})) + '\n');
        process.exit(1);
    }
    return require(resolved.path);
}

function stamp(d) {
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}` +
        `-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function readJson(file) {
    try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { return null; }
}

function writeJson(file, value) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify(value, null, 2) + '\n', 'utf8');
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * Numeric option with a default, without `||`'s falsy-zero trap: a caller who
 * passes 0 means 0, and silently substituting the default turns an explicit
 * instruction into a surprise.
 */
function numberOr(value, fallback) {
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
}

// ---------------------------------------------------------------------------
// Entry construction
// ---------------------------------------------------------------------------

function toHeaderArray(headers) {
    return Object.entries(headers || {}).map(([name, value]) => ({ name, value: String(value) }));
}

/**
 * Header block size on the wire, near enough to be useful. -1 is the HAR
 * spec's "not available", and reporting it when the headers are right there
 * throws away information a reader of the reference actually uses.
 */
function headerBytes(headers, firstLine) {
    const lines = Object.entries(headers || {})
        .map(([name, value]) => `${name}: ${value}\r\n`).join('');
    return Buffer.byteLength(`${firstLine}\r\n${lines}\r\n`, 'utf8');
}

/**
 * Does this buffer survive a UTF-8 round trip?
 *
 * `body.toString('utf8')` on a PNG or a protobuf silently substitutes U+FFFD
 * for every invalid sequence, which is lossy and irreversible -- the capture
 * looks fine and the payload is destroyed. HAR's answer is content.encoding,
 * so anything that does not round-trip becomes base64 and anything that does
 * stays readable text (a reference nobody can grep is most of a reference
 * wasted).
 */
function isUtf8(buf) {
    return Buffer.compare(Buffer.from(buf.toString('utf8'), 'utf8'), buf) === 0;
}

function bodyContent(buf, mimeType) {
    const content = { size: buf ? buf.length : 0, mimeType: mimeType || '' };
    if (!buf) return content;
    if (isUtf8(buf)) {
        content.text = buf.toString('utf8');
    } else {
        content.text = buf.toString('base64');
        content.encoding = 'base64';
    }
    return content;
}

function queryStringOf(url) {
    try {
        // URLSearchParams decodes percent-encoding for us. A raw query string
        // is not greppable for a field name; the decoded copy is what makes
        // the reference searchable.
        return [...new URL(url).searchParams.entries()].map(([name, value]) => ({ name, value }));
    } catch (e) {
        return [];
    }
}

function cookiesOf(headers) {
    const raw = headers && (headers.cookie || headers.Cookie);
    if (!raw) return [];
    return String(raw).split(';').map((pair) => {
        const i = pair.indexOf('=');
        return i < 0
            ? { name: pair.trim(), value: '' }
            : { name: pair.slice(0, i).trim(), value: pair.slice(i + 1).trim() };
    }).filter((c) => c.name);
}

/**
 * Build one HAR entry from what the recorder observed.
 *
 * Pure by design: the Playwright event handlers are a thin adapter that reads
 * the request/response and hands the values here, so the entry shape can be
 * tested exhaustively without launching a browser -- which is the only way it
 * ever gets tested at all.
 *
 * `response` is null for a FAILED request. Those used to be dropped entirely,
 * because the recorder listened on `response` and a failure never produces
 * one. SKILL.md calls failure paths "frequently the highest-value entries":
 * they establish the platform's real error taxonomy.
 */
function buildEntry(observed) {
    const { request, response, failure, timing, body } = observed;
    // Whether a body was READABLE is different from whether it was EMPTY, and
    // only the caller knows which. Absent the flag, a present body implies it
    // was readable.
    const bodyAvailable = observed.bodyAvailable === undefined
        ? body !== null && body !== undefined
        : observed.bodyAvailable;
    const reqHeaders = request.headers || {};
    const resHeaders = (response && response.headers) || {};
    const postBuf = request.postDataBuffer || null;
    // What we can say about a request body we did NOT keep (#442). null when
    // there is nothing to say -- no body was declared, or the whole of it is
    // in `postBuf` -- so a genuinely bodiless request is untouched.
    const unretained = bodyDescriptor.describeRequestBody({
        headers: reqHeaders,
        postDataBuffer: postBuf,
        postMimeType: request.postMimeType
    });

    const phases = timing
        ? { send: timing.send, wait: timing.wait, receive: timing.receive }
        : { send: -1, wait: -1, receive: -1 };
    const known = [phases.send, phases.wait, phases.receive].filter((n) => typeof n === 'number' && n >= 0);

    const entry = {
        startedDateTime: observed.startedDateTime || new Date().toISOString(),
        time: known.length ? known.reduce((a, b) => a + b, 0) : -1,
        request: {
            method: request.method,
            url: request.url,
            httpVersion: request.httpVersion || 'HTTP/1.1',
            headers: toHeaderArray(reqHeaders),
            queryString: queryStringOf(request.url),
            cookies: cookiesOf(reqHeaders),
            headersSize: headerBytes(reqHeaders, `${request.method} ${request.url}`),
            // -1 is HAR's "not available", and it is what this recorder
            // already reports for a RESPONSE body it could not read. Reporting
            // 0 for a body the request declared and we did not keep is a false
            // statement, and it is the same text a genuinely bodiless GET
            // produces -- which is the whole of #442. 0 still means 0: no
            // descriptor, no body, nothing withheld.
            bodySize: postBuf ? postBuf.length : (unretained ? -1 : 0)
        },
        response: {
            // A failed request has no status. 0 is the established HAR
            // convention for it, and it keeps the document schema-valid
            // instead of inventing a "no response" shape no tool reads.
            status: response ? response.status : 0,
            statusText: response
                ? (response.statusText || '')
                : ((failure && failure.errorText) || 'request failed'),
            httpVersion: (response && response.httpVersion) || 'HTTP/1.1',
            headers: toHeaderArray(resHeaders),
            cookies: [],
            content: bodyContent(body, resHeaders['content-type'] || ''),
            redirectURL: resHeaders.location || '',
            headersSize: response
                ? headerBytes(resHeaders, `HTTP/1.1 ${response.status}`)
                : -1,
            // -1 is HAR's "not available". Reporting 0 for a body we could
            // not read -- a redirect, a stream, one already discarded -- is a
            // claim the recorder cannot support.
            bodySize: body ? body.length : (response && bodyAvailable ? 0 : -1)
        },
        cache: {},
        timings: phases
    };

    if (postBuf) {
        entry.request.postData = Object.assign(
            { mimeType: request.postMimeType || '', params: [] },
            (({ text, encoding }) => (encoding ? { text, encoding } : { text }))(
                bodyContent(postBuf, request.postMimeType))
        );
    }
    if (unretained) entry.request[bodyDescriptor.DESCRIPTOR_KEY] = unretained;
    if (failure && failure.errorText) entry._failure = failure.errorText;
    return entry;
}

// ---------------------------------------------------------------------------
// Incremental recorder
// ---------------------------------------------------------------------------

/**
 * Append-only recorder. Entries are buffered and flushed on an interval;
 * a flush writes ONLY what arrived since the last one, so the cost of a short
 * interval does not grow with the size of the capture.
 */
class IncrementalRecorder {
    /**
     * @param {string} logPath append-only NDJSON log
     * @param {number} intervalMs flush interval
     * @param {{warnAtBytes?: number, onSizeWarning?: (text: string) => void}} [options]
     *   seams for the size warning (issue #528). Not operator-facing: there is
     *   no flag and no environment variable behind them. The threshold has one
     *   correct value, which is computed from the runtime's own string ceiling;
     *   the parameters exist so a test can cross it without producing hundreds
     *   of megabytes, and so the warning's destination is injectable.
     */
    constructor(logPath, intervalMs, options = {}) {
        this.logPath = logPath;
        this.intervalMs = intervalMs;
        this.pending = [];
        this.written = 0;
        this.timer = null;
        // Handlers that have begun and not finished (#98). Maintained by
        // attachRecorder, which is the only writer: reading a request's headers
        // and its response body are awaits, so between "the browser says this
        // request finished" and "the entry exists" the recording holds work
        // nothing else can see. An ending that tears the recorder down inside
        // that window loses the entry and nothing reports it missing.
        // settleRecorder waits on this count.
        this.inFlight = 0;
        // Counted as it is appended rather than stat-ed per flush: the flush
        // interval is short by design and the log is the largest file the
        // session writes. Seeded from what is already there, because the log is
        // append-only and a recorder attached to an existing one inherits its
        // size -- starting the count at zero would put the warning hundreds of
        // megabytes late, which for this warning is the same as never.
        this.bytes = 0;
        try { this.bytes = fs.statSync(logPath).size; } catch { /* a fresh log */ }
        this.warnAtBytes = options.warnAtBytes === undefined
            ? captureSize.WARN_BYTES
            : options.warnAtBytes;
        this.onSizeWarning = options.onSizeWarning || ((text) => log.warn(text));
        this.sizeWarned = false;
    }

    start() {
        this.timer = setInterval(() => this.flush(), this.intervalMs);
        if (this.timer.unref) this.timer.unref();
    }

    add(entry) {
        this.pending.push(entry);
    }

    flush() {
        if (!this.pending.length) return;
        const batch = this.pending;
        this.pending = [];
        try {
            const text = batch.map((e) => JSON.stringify(e)).join('\n') + '\n';
            fs.appendFileSync(this.logPath, text, 'utf8');
            this.written += batch.length;
            this.bytes += Buffer.byteLength(text, 'utf8');
        } catch (e) {
            // A failed flush must never take the recording down with it.
            log.verbose(`capture-har: incremental flush failed: ${e.message}`);
        }
        // CONTAINED, AND SEPARATELY. Two things are true at once and an earlier
        // version of this sacrificed the second for the first.
        //
        // Separately, because a warning that threw must not be reported as a
        // failed flush: those are different facts about a recording in progress
        // and confusing them sends anyone debugging one to the wrong place.
        //
        // Contained, because `flush` runs from an unguarded `setInterval`
        // callback and nothing in this process handles an uncaught exception.
        // An exception escaping here does not fail a flush -- it kills the
        // recorder, orphans the browser and discards everything buffered since
        // the last write. `log.warn` writes to stderr, and a stderr write can
        // throw: on POSIX a pipe whose reader has gone away raises EPIPE
        // synchronously, right here. On Windows those writes are asynchronous,
        // so the same condition arrives as a stream error event that no
        // synchronous catch anywhere can reach -- a property of every log call
        // in this file, not of this one. What is claimed is therefore narrow:
        // a synchronous throw out of the warning cannot cost a capture.
        // SILENT, AND THAT IS THE POINT. Reporting the failure would mean
        // writing to stderr -- the channel that just failed, through the very
        // call that failed, with no containment of its own. Under
        // `--log-level verbose` the recovery line would throw the same EPIPE
        // straight back out of `flush`, into the timer callback, and take the
        // capture down: the identical crash, reached by way of the apology for
        // it. There is nowhere safe to say this, so it is not said.
        try {
            this.announceSizeOnce();
        } catch (e) {
            void e;
        }
    }

    /**
     * Say so, once, while the operator can still do something about it.
     *
     * THE ONLY MOMENT THIS CAN BE SAID USEFULLY. Everything that consumes a
     * capture -- scrub, verify, digest, catalogue -- runs after the browser
     * exits, which is why this command is `Invoke-` rather than `Start-`. So an
     * operator recording past the size the scrub can read finds out when the
     * session is over and the account writes behind it are spent, and the only
     * artifact they still hold is the credential-bearing raw that must not be
     * shared. The recorder is the one component that knows the size while
     * stopping, splitting the session or narrowing the filter is still
     * possible (issue #528).
     *
     * THE NDJSON LOG IS A FAIR PROXY for the HAR assembled from it -- in the
     * session that prompted this, 811 MB of log against an 816 MB HAR -- and it
     * is a slight UNDER-count, which is the safe direction for a warning: it
     * fires marginally early rather than marginally late.
     *
     * Once. The flush interval is short and a session runs for many minutes, so
     * a per-flush warning would scroll away the browsing output it shares a
     * console with, and teach the operator to skip warnings generally.
     */
    announceSizeOnce() {
        if (this.sizeWarned || this.bytes <= this.warnAtBytes) return;
        this.sizeWarned = true;
        const limit = captureSize.formatSize(captureSize.MAX_STRING_LENGTH);
        this.onSizeWarning(
            `capture-har: this recording is already ${captureSize.formatSize(this.bytes)} and ` +
            `is approaching the ${limit} ceiling on a single string. A capture past that size ` +
            'cannot be scrubbed, and the scrub runs only after the browser exits -- so stop ' +
            'now and record the rest as a second session, or narrow what is being captured, ' +
            'rather than finding out when this one is over.');
    }

    stop() {
        if (this.timer) clearInterval(this.timer);
        this.flush();
    }
}

/**
 * The site a capture is about: the target's host without a leading `www.`.
 *
 * Deliberately NOT the registrable domain. A suffix list short enough to vendor
 * (see SUFFIX_LABELS) would make `foo.github.io` admit `bar.github.io`, and this
 * decides what gets recorded out of somebody's own browser -- so a miss errs
 * toward recording LESS. The pages a capture actually uses are in scope by the
 * other two rules below, whatever host they are on; this one only reaches tabs
 * the operator opens by hand.
 */
function captureSite(uri) {
    let host;
    try { host = new URL(uri).hostname.toLowerCase(); } catch (e) { void e; return null; }
    if (!host) return null;
    return host.startsWith('www.') ? host.slice(4) : host;
}

function onCaptureSite(site, url) {
    if (!site || !url) return false;
    let host;
    try { host = new URL(url).hostname.toLowerCase(); } catch (e) { void e; return false; }
    return host === site || host.endsWith(`.${site}`);
}

/**
 * Which requests a capture in somebody else's browser records (#85).
 *
 * In a browser this script launched, every request in the context is this
 * capture's: the context did not exist until this run made it. Attached -- and
 * natively launched, where Chrome may restore a profile's tabs -- the context is
 * the OPERATOR'S browser, holding whatever else they are signed into. Recording
 * all of it put other sites' and other accounts' traffic into raw.har, and from
 * there into scrubbed artifacts the scrub only cleans of KNOWN credential shapes.
 *
 * THE RULE. A request is recorded when the PAGE (tab or window) that made it is
 * in scope, and a page is in scope when:
 *
 *   1. this run opened it (the tab `start` navigates to the target), or
 *   2. it was opened FROM an in-scope page -- a popup or sign-in window, on any
 *      host, because it carries the auth exchange; or
 *   3. its top-level document is on the target's site (captureSite) -- a tab
 *      the operator opens on the target by hand.
 *
 * Once in scope, a page STAYS in scope wherever it navigates: an in-tab sign-in
 * leaves the site and comes back, and that round trip is the capture.
 *
 * WHAT IS NEVER CONSULTED: the request's own host. The APIs a capture exists to
 * find usually live on a different host from the page (`api.`, an auth server,
 * a CDN), so filtering requests by origin would discard exactly what is wanted.
 * Scoping is by page, and an in-scope page's requests are all recorded.
 *
 * FAILS CLOSED. A request whose page cannot be resolved -- a frame already gone,
 * a service worker not on the target's site -- is not recorded. Leaking another
 * account's traffic is the failure this exists to prevent; dropping a stray
 * request is not.
 */
function createCaptureScope(targetUri) {
    const site = captureSite(targetUri);
    const inScope = new WeakSet();
    // The opener never changes for a page, so it is asked once per page; whether
    // that opener is in scope CAN change (it may navigate to the target later),
    // so that part is not cached.
    const openers = new WeakMap();
    const MAX_OPENER_DEPTH = 8;

    async function pageAdmitted(page, documentUrl, depth) {
        if (inScope.has(page)) return true;
        let current = documentUrl;
        if (!current) {
            try { current = page.url(); } catch (e) { void e; current = null; }
        }
        if (onCaptureSite(site, current)) { inScope.add(page); return true; }
        if (depth >= MAX_OPENER_DEPTH) return false;
        if (!openers.has(page)) {
            openers.set(page, Promise.resolve()
                .then(() => (typeof page.opener === 'function' ? page.opener() : null))
                .catch(() => null));
        }
        const opener = await openers.get(page);
        if (opener && opener !== page && await pageAdmitted(opener, null, depth + 1)) {
            inScope.add(page);
            return true;
        }
        return false;
    }

    return {
        site,
        /** Rule 1: a page this run opened is in scope from the start. */
        claim(page) { if (page) inScope.add(page); },
        /**
         * Already admitted -- synchronous, and deliberately without the opener
         * walk: the sign-in watcher (#115) asks this of a page that has just
         * loaded, by which time any request it made has been through `admits`.
         */
        owns(page) { return !!page && inScope.has(page); },
        async admits(request) {
            try {
                let frame = null;
                try { frame = request.frame(); } catch (e) { void e; frame = null; }
                if (!frame) {
                    // A service worker's request has no frame. It belongs to the
                    // site that registered the worker, so rule 3 is the only one
                    // that can admit it.
                    const worker = typeof request.serviceWorker === 'function'
                        ? request.serviceWorker() : null;
                    return !!worker && onCaptureSite(site, worker.url());
                }
                const page = frame.page();
                if (!page) return false;
                // A tab's FIRST document request finishes before page.url() has
                // caught up with it, so a top-level navigation is judged by where
                // it is going rather than by where the tab was.
                let documentUrl = null;
                if (request.isNavigationRequest() && frame === page.mainFrame()) {
                    documentUrl = request.url();
                }
                return await pageAdmitted(page, documentUrl, 0);
            } catch (e) {
                void e;
                return false;
            }
        }
    };
}

/**
 * Is this request a tab's own page load -- a navigation of its MAIN frame?
 * The one kind of request whose refusal is the site refusing the visitor
 * wherever it is hosted (pacing.classifyResponse), and the unit an agent's
 * navigation rate is counted in. Anything that cannot be asked is "no".
 */
function isMainFrameDocument(request) {
    try {
        if (!request.isNavigationRequest()) return false;
        const frame = request.frame();
        return !!frame && typeof frame.parentFrame === 'function' && frame.parentFrame() === null;
    } catch (e) {
        void e;
        return false;
    }
}

/**
 * Adapt Playwright events onto buildEntry.
 *
 * `requestfinished` and `requestfailed` rather than `response`, for two
 * reasons: a failed request never emits a response and was therefore lost
 * entirely, and `requestfinished` is the point at which timing information is
 * actually complete.
 *
 * `scope` (createCaptureScope) is given only where the browser is not this
 * run's own -- attached or natively launched -- and a request it does not admit
 * is never read, let alone written. Null records everything in the context,
 * which in a browser this run launched is the same set as "this capture".
 *
 * `opts.onResponse(info)`, when given, is shown every RECORDED response once
 * its entry is written -- url, status, headers, mime type, body and whether it
 * was a main-frame document -- so throttling and blocks are read from exactly
 * what the capture holds (pacing.js). It is told after the entry is added, and
 * guarded, so an observer that throws can never cost the capture an entry.
 */
function attachRecorder(context, recorder, scope, opts = {}) {
    const record = async (request, failure) => {
        // COUNTED FROM THE FIRST LINE TO THE LAST (#98). Everything below is
        // awaits, so an ending that arrives mid-handler -- which is what an agent
        // exiting the instant its last action returns produces -- would take the
        // recorder down while this entry is still being built. The count is what
        // settleRecorder waits on, and it is incremented here rather than inside
        // the try so no path can begin without being counted.
        recorder.inFlight = (recorder.inFlight || 0) + 1;
        // The whole handler is guarded, not just the calls known to be
        // fragile: an unhandled rejection here would crash the driver
        // mid-capture, which is the exact loss this recorder exists to prevent.
        try {
            if (scope && !(await scope.admits(request))) {
                recorder.excluded = (recorder.excluded || 0) + 1;
                return;
            }
            const response = await request.response().catch(() => null);
            let body = null;
            // Tracked separately from `body` because "could not read it" and
            // "it was empty" are different facts, and only this layer knows
            // which one happened.
            let bodyAvailable = false;
            if (response) {
                // Best-effort: a redirect, a streamed response, or one already
                // discarded must not cost the entry itself.
                body = await response.body().then(
                    (b) => { bodyAvailable = true; return b; },
                    () => null);
            }
            const reqHeaders = await request.allHeaders().catch(() => ({}));
            const respHeaders = response ? await response.allHeaders().catch(() => ({})) : null;
            recorder.add(buildEntry({
                request: {
                    method: request.method(),
                    url: request.url(),
                    headers: reqHeaders,
                    httpVersion: 'HTTP/1.1',
                    postDataBuffer: request.postDataBuffer ? request.postDataBuffer() : null,
                    postMimeType: reqHeaders['content-type'] || ''
                },
                response: response ? {
                    status: response.status(),
                    statusText: response.statusText(),
                    headers: respHeaders,
                    httpVersion: 'HTTP/1.1'
                } : null,
                failure,
                timing: request.timing ? request.timing() : null,
                body,
                bodyAvailable
            }));
            if (opts.onResponse && response) {
                try {
                    opts.onResponse({
                        url: request.url(),
                        method: request.method(),
                        status: response.status(),
                        headers: respHeaders || {},
                        mimeType: (respHeaders && respHeaders['content-type']) || '',
                        body,
                        isDocument: isMainFrameDocument(request)
                    });
                } catch (e) {
                    log.verbose(`capture-har: the throttle check skipped a response: ${e.message}`);
                }
            }
        } catch (e) {
            log.verbose(`capture-har: entry skipped: ${e.message}`);
        } finally {
            recorder.inFlight = Math.max(0, (recorder.inFlight || 0) - 1);
        }
    };

    context.on('requestfinished', (request) => { void record(request, null); });
    context.on('requestfailed', (request) => { void record(request, request.failure()); });
    // Our process outlives the browser, so a window close still reaches us --
    // flushing here is what makes the fallback non-lossy.
    context.on('close', () => recorder.stop());
}

/**
 * Assemble the append-only log into raw.har.
 *
 * A truncated final line is expected after an abrupt ending and is dropped
 * rather than failing the assembly -- salvaging the rest is the entire point.
 *
 * There is deliberately no "this artifact is degraded" banner. It is not
 * degraded: it carries real timings, uncapped bodies, and the failed requests
 * recordHar's own output would have. recordHar is preferred where both exist
 * only because it knows things a client-side recorder cannot observe.
 */
function assembleFromLog(logPath, outPath) {
    if (!fs.existsSync(logPath)) return null;
    const { entries, dropped } = readLogEntries(logPath);
    if (!entries.length) return null;
    const har = {
        log: {
            version: '1.2',
            creator: { name: 'capture-har.js', version: '2.0' },
            comment: 'Assembled from the incremental record log.' +
                (dropped ? ` ${dropped} truncated entr${dropped === 1 ? 'y was' : 'ies were'} dropped.` : ''),
            pages: [],
            entries
        }
    };
    writeJson(outPath, har);
    return { entries: entries.length, dropped };
}

/**
 * Read the record log's entries, dropping a truncated final line.
 *
 * Shared with `assembleFromLog` rather than duplicated, because "a truncated
 * last line is expected and is dropped" is a rule the two readers must not
 * disagree about.
 */
function readLogEntries(logPath) {
    if (!logPath || !fs.existsSync(logPath)) return { entries: [], dropped: 0 };
    let text;
    try { text = fs.readFileSync(logPath, 'utf8'); } catch (e) { return { entries: [], dropped: 0 }; }
    const entries = [];
    let dropped = 0;
    for (const line of text.split('\n')) {
        if (!line.trim()) continue;
        try { entries.push(JSON.parse(line)); } catch (e) { dropped++; }
    }
    return { entries, dropped };
}

/**
 * Attach every unretained-request-body descriptor this recorder observed onto
 * whichever `raw.har` exists -- THE ONE CALL SITE THAT SERVES BOTH RECORDERS.
 *
 * A descriptor has to be recorded at capture time because the payload it
 * describes is gone afterwards. But on the `recordHar` path there is no moment
 * in this process at which an entry is being built: the driver buffers the
 * session and serialises it during `context.close()`. Writing the descriptor
 * "while building an entry" therefore only ever runs on the assembled path --
 * which is the path that USUALLY LOSES.
 *
 * What is true on both paths is that THIS RECORDER SAW THE REQUEST: that is
 * how `raw.ndjson` exists at all. Since #377 the log is kept even when
 * `recordHar` wins, so the observation outlives the choice of artifact, and
 * attaching it to `raw.har` after the driver has written it is still "at
 * capture time" in the only sense that matters -- the knowledge has not left
 * the process, and on the `stop` path it has not left the session directory.
 *
 * Failure here is never fatal. An un-annotated `raw.har` is exactly today's
 * artifact; losing the capture over a descriptor would be a far worse trade
 * than the one this fixes.
 */
function annotateUnretainedBodies(harPath, logPath) {
    if (!harPath || !fs.existsSync(harPath)) return null;
    const { entries: logged } = readLogEntries(logPath);
    if (!logged.length) return null;
    const staged = `${harPath}.annotating`;
    try {
        const read = harDocument.readHarDocument(harPath);
        const har = read.document;
        const entries = read.entries;
        const annotated = bodyDescriptor.attachDescriptors(entries, logged);
        if (!annotated) return { annotated: 0 };
        // WRITE BESIDE IT, THEN RENAME. This is the only place in this file
        // that overwrites an ALREADY-EXISTING HAR rather than writing a fresh
        // one, and the file it overwrites is `raw.har` -- the one artifact
        // whose loss is unrecoverable. A bare `writeFileSync` truncates first,
        // so a kill between truncate and completion leaves a corrupt raw where
        // a whole one stood; and this runs at exactly the moments a driver may
        // be dying. `rename` over the same directory is atomic, so the file is
        // either the old one or the new one and never a prefix of either.
        writeJson(staged, har);
        fs.renameSync(staged, harPath);
        return { annotated };
    } catch (e) {
        // The half-written stage is removed rather than left beside the raw,
        // where it would look like a second capture to anything that globs the
        // session directory.
        try { if (fs.existsSync(staged)) fs.unlinkSync(staged); } catch (_) { /* best effort */ }
        log.verbose(`capture-har: could not annotate unretained request bodies: ${e.message}`);
        return null;
    }
}

// ---------------------------------------------------------------------------
// Digest
// ---------------------------------------------------------------------------

/**
 * Collapse the ids out of a path so two calls to one operation group together.
 *
 * Without this, `/posts/1234` and `/posts/9876` read as two different
 * endpoints and the digest degenerates into a list of every request -- which
 * is the thing the digest exists to avoid.
 */
// Collapse identifier-shaped path segments to a template.
//
// The implementation lives in har-catalogue.js and is imported rather than
// kept here, because verify-har-catalogue.js recomputes a committed row's
// `Endpoints` from the reference while `buildCatalogueScaffold` below writes
// them from this digest. Two implementations that agree today would drift, and
// the drift would surface as every scaffolded row failing the guard the moment
// it was committed -- a failure that looks like the gate is broken, so the fix
// people reach for is to weaken the gate.
//
// Re-exported below, so a caller that reasonably expects the digest's own
// templating to come from the digest's own module still gets it.
const pathTemplate = harCatalogue.pathTemplate;

/**
 * Top-level key names of a JSON payload -- the SHAPE, never the values.
 *
 * A digest that embedded bodies would be as large as the capture and would
 * carry the very credentials the containment rule exists to keep out of the
 * output path.
 */
function payloadShape(text) {
    if (!text) return [];
    try {
        const parsed = JSON.parse(text);
        const target = Array.isArray(parsed) ? parsed[0] : parsed;
        return target && typeof target === 'object' ? Object.keys(target) : [];
    } catch (e) {
        return [];
    }
}

/**
 * Scheme and host, discarding path, query and fragment -- the parts that carry
 * tokens. Returns null rather than guessing when the value is not a URL.
 */
function originOf(uri) {
    if (!uri) return null;
    try { return new URL(uri).origin; } catch (e) { return null; }
}

/**
 * A compact description of a capture that an AI can segment from without
 * re-parsing a multi-hundred-megabyte HAR.
 */
function buildDigest(har, meta = {}) {
    // Recognised, not folded (issue #423). `|| []` here was the named symptom:
    // a capture nothing could read produced a digest with no groups, the
    // scaffold produced no rows, and the catalogue guard then blamed the
    // catalogue for a file the READER had failed on. A digest is a claim about
    // traffic, so it refuses to be built from something it cannot identify as
    // a recording.
    const entries = harDocument.entriesOf(har, 'the capture being digested');
    const groups = new Map();
    const gaps = [];

    entries.forEach((entry, index) => {
        let url;
        try { url = new URL(entry.request.url); } catch (e) { return; }
        const template = pathTemplate(url.pathname);
        const status = (entry.response && entry.response.status) || 0;
        // UPPERCASED, and it matters twice.
        //
        // RFC 9110 defines the methods in uppercase and makes them
        // case-sensitive, so `get` and `GET` are one endpoint in every practical
        // sense -- keying on the raw verb split them into two groups and
        // double-counted the endpoint.
        //
        // It also has to agree with har-catalogue.js's `measureReference`, which
        // uppercases: the scaffold writes a row's `Methods` from here, and the
        // guard recomputes them from the reference. A scaffold reading ["get"]
        // against a recomputed ["GET"] fails the row on casing alone -- correct
        // data reported as wrong, and the repair everyone reaches for is to
        // weaken the comparison rather than fix the derivation.
        const method = String(entry.request.method || '').toUpperCase();
        const key = `${url.host}|${method}|${template}|${status}`;

        let group = groups.get(key);
        if (!group) {
            group = {
                host: url.host,
                method,
                pathTemplate: template,
                status,
                count: 0,
                firstIndex: index,
                contentTypes: [],
                requestShape: [],
                responseShape: []
            };
            groups.set(key, group);
        }
        group.count++;

        const mime = (entry.response && entry.response.content && entry.response.content.mimeType) || '';
        if (mime && !group.contentTypes.includes(mime)) group.contentTypes.push(mime);
        for (const k of payloadShape(entry.request.postData && entry.request.postData.text)) {
            if (!group.requestShape.includes(k)) group.requestShape.push(k);
        }
        for (const k of payloadShape(entry.response && entry.response.content && entry.response.content.text)) {
            if (!group.responseShape.includes(k)) group.responseShape.push(k);
        }

        // A pause between two requests is the only evidence in the traffic
        // that the human moved on to a different action.
        if (index > 0) {
            const prev = entries[index - 1];
            const prevEnd = Date.parse(prev.startedDateTime) + (prev.time > 0 ? prev.time : 0);
            const seconds = (Date.parse(entry.startedDateTime) - prevEnd) / 1000;
            if (seconds >= ACTION_GAP_SECONDS) {
                gaps.push({ beforeIndex: index - 1, afterIndex: index, seconds: Math.round(seconds * 100) / 100 });
            }
        }
    });

    return {
        capturedUtc: meta.capturedUtc || new Date().toISOString(),
        // ORIGIN only. The operator types this URL, and a magic-link,
        // password-reset or signed start URL carries its token in the query or
        // the path. The digest is written to the committable output path, so
        // echoing the URL back verbatim would put a live credential exactly
        // where the design promises only scrubbed artifacts land.
        uri: originOf(meta.uri),
        describe: meta.describe || null,
        entryCount: entries.length,
        hosts: [...new Set(entries.map((e) => {
            try { return new URL(e.request.url).host; } catch (x) { return '?'; }
        }))].sort(),
        groups: [...groups.values()],
        gaps
    };
}

/**
 * Provisional catalogue rows, one per observed operation.
 *
 * Every row starts `Observed`: nothing is `Exercised` until an AI has actually
 * segmented the session and extracted a reference HAR for it. Naming and
 * describing the actions is the AI's job -- a name derived mechanically from a
 * URL is exactly the "endpoint is recoverable from the file, what you did to
 * provoke it is not" gap the catalogue exists to close.
 */
function buildCatalogueScaffold(digest, meta = {}) {
    return digest.groups.map((group) => ({
        Action: `${group.method.toLowerCase()}-${group.pathTemplate.split('/').filter(Boolean).join('-') || 'root'}`,
        Description: null,
        // From THIS ROW's own endpoint host -- `group.host`, the same source
        // `Endpoints` below is built from -- never from the capture's start
        // URI. One capture routinely spans several hosts (the site's own API,
        // a CDN, a third party), so deriving every row from the start URI
        // would stamp one provider onto rows that are not that provider: a
        // row asserting something nobody checked, which is exactly the
        // defect #379 exists to prevent. `providerSlug` returns null when the
        // host yields no slug, and that null is left as-is -- it means "a
        // human still has to say", and inventing a value to avoid it would be
        // the same defect. `stripPort` first, so that for a GIVEN host this
        // agrees with `referenceRelativePath` (which slugs `URL#hostname`,
        // no port) on the `<provider>/` directory that host's reference
        // would land in -- differing when the row's host differs from the
        // capture's start host is the point of deriving per row at all.
        Provider: providerSlug(stripPort(group.host)),
        Methods: [group.method],
        Endpoints: [`${group.host}${group.pathTemplate}`],
        EntryCount: group.count,
        Status: 'Observed',
        // The reference this capture WOULD be extracted into (#377 s6), as
        // `<provider>/<filename>` -- the shape #379 defines for this field,
        // relative to the committed catalogue at docs/har-reference/<host>/.
        // Not invented here: `referenceRelativePath` names what
        // har/extract-har-reference.js writes, from the provider, the operator's
        // own --describe and the capture date. Null when there is no session to
        // derive it from, which is every caller that passes no meta.
        //
        // This is the structural link a committed reference needs back to its
        // raw. Its absence is why an audit found 0 of 29 committed references
        // adjudicable: a reference nobody can pair with a capture cannot be
        // re-derived, re-scrubbed, or diffed.
        HarFile: meta.referenceFile || null,
        // NULL, NOT ZERO. These are facts about a reference file, and a scaffold
        // row describes a digest GROUP -- no reference has been extracted yet,
        // so there is nothing to have measured. Writing `0` would state a fact
        // nobody checked, in the exact shape of the defect the structured
        // catalogue exists to prevent: a row asserting something about a file
        // that does not support it. verify-har-catalogue.js recomputes these
        // once the row names a reference, so a null that was never filled in is
        // reported rather than mistaken for a measurement.
        RequestBodies: null,
        RequestBytes: null,
        ResponseBytes: null,
        RequestBodiesAbsent: null,
        Related: [],
        CapturedUtc: meta.capturedUtc || digest.capturedUtc
    }));
}

// ---------------------------------------------------------------------------
// Catalogue delegation
// ---------------------------------------------------------------------------

/**
 * Who runs the catalogue phase.
 *
 * Cataloguing is AI work, and the two ways this command gets launched need
 * different handling:
 *
 *  - An agent already drives the session. It reads the digest itself; shelling
 *    out to a second AI from inside one would be absurd.
 *  - A human ran it interactively. Shell out to the `claude` CLI so the
 *    catalogue completes without the human needing to know it was an AI step.
 *  - Neither. Report the prompt and the command, so the step is visibly
 *    outstanding rather than silently dropped.
 */
function decideCatalogueRunner(ctx = {}) {
    const env = ctx.env || process.env;
    const promptPath = path.join(__dirname, CATALOGUE_PROMPT);
    if (env.CLAUDECODE || env.CLAUDE_CODE_ENTRYPOINT) {
        return { delegatedTo: 'agent', pending: true, promptPath };
    }
    if (ctx.isTty && ctx.claudeOnPath) {
        return { delegatedTo: 'claude-cli', pending: false, promptPath };
    }
    return { delegatedTo: 'none', pending: true, promptPath };
}

/**
 * Is there a `claude` this process can actually RUN?
 *
 * Asked by spawning it, exactly as the drive and the catalogue pass will -- no
 * shell -- rather than by `where`/`which`. Those answer "is there a file by that
 * name", and on Windows the file is often a `claude.cmd` shim, which spawn()
 * without a shell cannot launch (Node refuses .cmd/.bat since CVE-2024-27980).
 * The probe then said yes, `--ai` passed the guard that is supposed to refuse
 * BEFORE a browser is launched, and the run ended `agent-failed` with the
 * browser already up. Spawning `claude --version` makes the probe's answer and
 * the launch's answer the same question.
 */
function claudeOnPath() {
    const probe = spawnSync(AGENT_CLAUDE, ['--version'], {
        encoding: 'utf8', windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        timeout: AGENT_PROBE_TIMEOUT_MS
    });
    return !probe.error && probe.status === 0;
}

// ---------------------------------------------------------------------------
// An agent drives the browser while this process records (#98)
// ---------------------------------------------------------------------------

/**
 * The prompt an agent-driven capture is driven under.
 *
 * GENERATED, NEVER HAND-WRITTEN, and that is the feature. The prompt that drove
 * the five-site car-rental capture this comes from was typed by hand, and it is
 * the same every time except for the goal -- so most of it is text that ought
 * not to be retyped at all. Most of it is also SAFETY TEXT: never a password,
 * never payment data, stop before a driver-details page, hand back on a bot
 * check. A prompt somebody retypes in a hurry is a prompt that drops one of
 * those, and the dropping is invisible until an agent has typed a card number
 * into somebody's checkout.
 *
 * THREE PARTS, assembled here and stored beside the capture:
 *
 *   drive-prompt.md         the standing instructions and the rules
 *   this run's own facts    the goal (-Describe), the site, the CDP endpoint,
 *                           session.json -- the only part that varies
 *   discovery-checklist.md  what to vary so the capture exposes parameters
 *
 * The checklist is a FILE OF ITS OWN because issue #76 owns that text and will
 * move it into SKILL.md; read rather than inlined, that move is a file move
 * instead of a rewrite of this function.
 *
 * SEAM FOR #75 (the sign-in checkpoint). Without it, the rule in
 * drive-prompt.md stands alone: the agent stops at the first sign-in prompt and
 * hands back. With #75 the session grows a `phase`, a human signs in while the
 * recording runs, and `-SignInFirst` flips it -- at which point this function
 * gains a line telling the agent to wait for `phase: "recording"` in the
 * session.json it is already given. Nothing else here changes.
 *
 * SEAM FOR #74 (multi-site). One prompt, one site, one window. Multi-site is one
 * capture per site, so it is one call to this function per site rather than a
 * list inside the prompt -- the rule "keep the site's tab in front" only has a
 * meaning per window.
 */
function buildAgentPrompt(session, opts = {}) {
    const dir = opts.dir || __dirname;
    const read = (name) => fs.readFileSync(path.join(dir, name), 'utf8').trimEnd();
    const sessionFile = path.join(session.sessionDir, SESSION_FILE);
    return [
        read(DRIVE_PROMPT),
        '',
        '## This capture',
        '',
        `- **Goal:** ${session.describe}`,
        `- **Site:** ${session.uri}`,
        `- **Browser CDP endpoint:** ${session.cdpEndpoint}`,
        ...(session.chromeExtension === false
            ? [
                '- **Driver:** this run drives through Playwright MCP on the CDP endpoint above. The',
                '  Claude-in-Chrome extension is not installed in the capture profile, so skip',
                '  "Select the capture browser" and do not use any `mcp__claude-in-chrome__*` tool.'
            ]
            : [
                `- **Capture browser name (Claude in Chrome):** \`${captureBrowserName(session)}\``,
                '  (select the connected browser with exactly this name; see "Select the capture browser")'
            ]),
        // THE SAME NUMBERS the recorder paces its own steps by (pacing.js), so
        // "human pace" in the rules means one thing for the agent and the code.
        `- **Pace:** ${pacing.describePace(session.pace || pacing.DEFAULT_PACE)}`,
        `- **Session file:** ${sessionFile}`,
        '  (everything the recorder resolved for this run, including the endpoint above)',
        `- **Your step log is saved to:** ${session.agentLogPath}`,
        '- **The only tools you may use** (the ones this run pre-approves; use nothing else):',
        ...agentAllowedTools(session.chromeExtension !== false).map((tool) => `  - \`${tool}\``),
        '',
        '## Discovery checklist',
        '',
        read(DISCOVERY_CHECKLIST),
        ''
    ].join('\n');
}

/**
 * Is Claude in Chrome installed in this capture profile?
 *
 * Read from the profile's own Extensions directory -- the folder Chrome creates
 * for an extension installed from the Chrome Web Store, keyed by its store ID --
 * in `Default` and any `Profile N` inside the user-data-dir. A version folder
 * with no manifest.json is a half-finished install and does not count.
 *
 * Read-only, and only a hint: it cannot tell whether the extension is signed in
 * or connected. That is why its answer is a warning, never a refusal.
 */
function chromeExtensionInstalled(profileDir) {
    let entries;
    try { entries = fs.readdirSync(profileDir, { withFileTypes: true }); } catch (e) { void e; return false; }
    return entries.some((entry) => {
        if (!entry.isDirectory() || !/^(Default|Profile \d+)$/.test(entry.name)) return false;
        const extDir = path.join(profileDir, entry.name, 'Extensions', CLAUDE_EXTENSION_ID);
        let versions;
        try { versions = fs.readdirSync(extDir); } catch (e) { void e; return false; }
        return versions.some((v) => fs.existsSync(path.join(extDir, v, 'manifest.json')));
    });
}

/**
 * The name the capture browser goes by in Claude in Chrome, and the one fact the
 * agent selects it by (#117). The extension's list_connected_browsers reports
 * each connected Chrome by deviceId and display name -- nothing about CDP ports
 * or profiles -- so the operator names the capture browser once, when connecting
 * the extension in it, and the run tells the agent which name to select.
 *
 * UNIQUE PER PROFILE (review I3). The default profile is plain `har-capture`;
 * every other profile is `har-capture-<folder slug>-<6 hex>`, the hex a hash of
 * the resolved profile path. The slug alone collided -- `profile-work` and a
 * folder named `work` were both `har-capture-work`, and a folder whose slug came
 * out empty took the default profile's name -- and a collision is the agent
 * driving the wrong browser. The hash is stable, so a profile keeps its name.
 * An attached browser has no profile this run can name, so it is the plain name.
 */
function captureBrowserName(session) {
    const profileDir = session && session.profileDir;
    if (!profileDir) return CAPTURE_BROWSER_NAME;
    const resolved = path.resolve(profileDir);
    const key = (dir) => {
        const r = path.resolve(dir);
        return process.platform === 'win32' ? r.toLowerCase() : r;
    };
    // The EFFECTIVE default only: with HAR_CAPTURE_PROFILE set, that is the
    // default and `<root>/profile` is an ordinary profile with its own name.
    if (key(defaultProfileDir()) === key(resolved)) return CAPTURE_BROWSER_NAME;
    const base = path.basename(resolved).trim().toLowerCase();
    const slug = (base.startsWith('profile-') ? base.slice('profile-'.length) : base)
        .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
    const hash = require('crypto').createHash('sha256').update(key(resolved)).digest('hex').slice(0, 6);
    return [CAPTURE_BROWSER_NAME, slug, hash].filter(Boolean).join('-');
}

/**
 * What an -AI run says about the extension before the browser launches (#117).
 * Null when there is no profile to look in (an attached browser). Never an
 * install: no `--load-extension` (the stealth test forbids it), nothing
 * automated -- the operator installs it once, by hand, in the capture profile.
 */
function chromeExtensionNotice(session) {
    if (!session.profileDir) return null;
    const name = captureBrowserName(session);
    if (chromeExtensionInstalled(session.profileDir)) {
        return {
            installed: true,
            level: 'info',
            message: 'capture-har: Claude in Chrome is installed in the capture profile. The agent ' +
                `selects the connected\n  browser named \`${name}\` -- give it that name when you ` +
                'connect the extension in it.'
        };
    }
    return {
        installed: false,
        level: 'warn',
        message: [
            'capture-har: Claude in Chrome -- the preferred driver for --ai -- is not installed in ' +
                'the capture',
            '  profile, so this run drives through Playwright MCP on the CDP endpoint (the agent is',
            '  not given the extension at all). One-time setup, for the next run:',
            '    1. Open the capture browser on this profile (a run without --ai, --browser native):',
            `       ${session.profileDir}`,
            '    2. In it, install Claude in Chrome from the Chrome Web Store:',
            `       ${CLAUDE_EXTENSION_STORE_URL}`,
            '    3. Sign in to the extension with your Claude account, and connect it under the name',
            `       \`${name}\` -- the agent selects the capture browser by that name. Give that`,
            '       name to the capture browser only.'
        ].join('\n')
    };
}

/**
 * Who, if anyone, drives this capture.
 *
 * A pure decision rather than a branch inside the launch, because the only thing
 * that can make it fail -- no `claude` on PATH -- cannot be produced inside a
 * test without uninstalling the CLI.
 *
 * `--ai` with no agent to run is UNANSWERABLE, so it is a refusal rather than a
 * warning that falls back to a recording nobody is driving: the operator asked
 * for a capture to be driven, and a browser sitting open on a bot-protected site
 * with nothing happening in it is not a lesser version of that.
 */
function resolveAgent(ctx = {}) {
    if (!ctx.ai) return {};
    if (!ctx.claudeOnPath) {
        return {
            error: 'capture-har: --ai drives the recording with the `claude` CLI, and it is not ' +
                'on PATH.\n' +
                '  Install it (https://claude.com/claude-code), or record the session yourself in ' +
                'an\n  ordinary Chrome:\n' +
                '    node capture-har.js start --uri <url> --describe <text> --browser native\n' +
                '  Refused here, before a browser is started, so there is nothing to clean up.'
        };
    }
    return { agent: AGENT_CLAUDE };
}

/**
 * The agent child:
 *   `claude -p <prompt> --permission-mode auto --chrome|--no-chrome [--tools ""] --allowedTools <tools>`.
 *   (`--no-chrome`, and no extension tools in the allowlist, when the capture
 *   profile has no extension -- see agentAllowedTools.)
 *
 * `--chrome` in both modes (#117): "Enable Claude in Chrome integration". The
 * extension is the preferred driver, and a `-p` child without the flag may have
 * none of its tools -- leaving only the Playwright MCP fallback.
 *
 * TWO MODES (owner decision, #112). `--allowedTools` only PRE-APPROVES the
 * browser tools; it removes nothing. Without a mode, a `-p` child would run the
 * read-only built-ins (Read, Grep, Glob) unprompted -- beside raw.ndjson and its
 * live cookies -- and silently deny everything else, such as a Bash step when it
 * gets stuck.
 *
 *   auto          (default) `--permission-mode auto`: the classifier approves
 *                 or blocks each action the list does not cover, and the browser
 *                 tools stay pre-approved so clicking never waits on it.
 *   browser-only  also `--tools ""`: every BUILT-IN tool is removed. MCP tools
 *                 remain -- the browser's, and any other server in the
 *                 operator's own Claude settings, which `--tools` does not
 *                 govern (documented: keep only the browser MCP configured).
 *                 `--strict-mcp-config` is deliberately NOT passed: it would
 *                 drop the browser MCP too. For a site the operator is signed
 *                 into something sensitive on -- the agent reads untrusted page
 *                 content beside live cookies, and the classifier is a
 *                 judgement, not a wall.
 *
 * Never `--dangerously-skip-permissions` or `bypassPermissions`: those approve
 * without judging anything.
 *
 * The empty string is its own argv element, and survives because the child is
 * spawned WITHOUT a shell (a shell would drop the empty word, and `--tools`,
 * variadic, would then swallow the allowlist).
 *
 * `-p` is non-interactive by design. The interactive part of this run is the
 * BROWSING, and it is happening in the browser; a driver that stopped to ask the
 * terminal a question would block behind the recorder's own ENTER prompt, which
 * is still armed because a human may always cut a drive short.
 *
 * DELIBERATELY NOT under run-with-heartbeat.js, which the catalogue pass runs
 * through (#492). That wrapper exists because postProcess is SYNCHRONOUS and
 * cannot tick while `spawnSync` blocks its event loop; the drive is awaited
 * inside an async `start`, which can. And decisively: the wrapper inherits
 * stdio, so the agent's narration would reach the terminal and nowhere else --
 * while the step log beside the capture is one of the artifacts this mode owes
 * the cataloguing step. The narration IS the progress signal here, so there is
 * nothing left for a heartbeat to add.
 */
function agentChildCommand(prompt, opts = {}) {
    // `--allowedTools` LAST, one tool per argument: the CLI declares it
    // `<tools...>`, variadic, so anything after it would be read as a tool name.
    return {
        command: AGENT_CLAUDE,
        args: [
            '-p', prompt,
            '--permission-mode', 'auto',
            // Boolean, so it may sit anywhere before the variadic flags: without
            // it a `-p` child need not load the extension's tools at all (#117).
            // DISABLED when the preflight found the extension missing from the
            // capture profile (review I1): then the only Chrome the extension
            // could reach is some other one, and the run is Playwright MCP's.
            // `--no-chrome`, not merely no flag: Claude Code settings can enable
            // the integration by default, and an omitted flag would leave it on.
            opts.chrome === false ? '--no-chrome' : '--chrome',
            ...(opts.tools === AGENT_TOOLS_BROWSER_ONLY ? ['--tools', ''] : []),
            '--allowedTools', ...agentAllowedTools(opts.chrome !== false)
        ]
    };
}

/**
 * Run the agent against the recording, and keep every word it says.
 *
 * TEED, not captured: each chunk goes to the step log beside the capture AND to
 * this process's stderr. The log is for the cataloguing step, which the issue
 * asks to be able to read what the agent did and where it stopped; the terminal
 * copy is for the operator, who is otherwise watching a browser move on its own
 * with no account of why. stderr rather than stdout because stdout carries this
 * script's one machine-readable line and nothing else.
 *
 * It NEVER REJECTS. A driver that could not be started, or that died, is an
 * ending like any other: the traffic recorded up to that point is real, and
 * discarding it would be the one outcome worse than a short capture. The exit
 * status is reported so `start` can say which happened.
 *
 * IT ENDS ON 'close', NOT ON 'exit'. 'exit' is the process ending; its stdout
 * and stderr can still hold output, delivered AFTER it -- and the output that
 * arrives last is the agent's summary of where it stopped, the part of the
 * step log the cataloguing step most needs. Ending the log on 'exit' lost that
 * text, and a chunk landing between `end()` and the stream finishing threw
 * ERR_STREAM_WRITE_AFTER_END with no listener, killing the recorder before
 * session.json or the HAR were written. 'close' fires once both pipes drained.
 *
 * BUT ONLY SO LONG. A grandchild the agent started can inherit its stdout and
 * hold the pipe open after the agent is gone, and an unconditional wait for
 * 'close' would turn that into a recording that never ends. After 'exit' the
 * drain is given `opts.drainMs`, then the drive ends with what arrived.
 *
 * `opts.control`, when given, receives `stop(why)`: the recorder's way to end
 * the DRIVE -- never the recording -- when the site throttles or blocks it
 * (see "## Pacing" at the top). It ends the child this call spawned, through
 * the handle it holds: never a pid looked up, never a process tree, never the
 * browser. The result then carries `stoppedFor`, which agentExitEnding reads
 * as "not an ending".
 */
function driveWithAgent(session, prompt, opts = {}) {
    const spawnFn = opts.spawn || spawn;
    const drainMs = opts.drainMs === undefined ? AGENT_DRAIN_MAX_MS : opts.drainMs;
    const child = agentChildCommand(prompt, {
        tools: session.agentTools, chrome: session.chromeExtension !== false
    });
    return new Promise((resolve) => {
        let settled = false;
        let stoppedFor = null;
        let stream = null;
        // The log is closed exactly once, and nothing is written after that:
        // `say` checks this rather than trusting the order events arrive in.
        let logEnded = false;
        const finish = (result) => {
            if (settled) return;
            settled = true;
            if (stream && !logEnded) {
                // Resolved once the log is FLUSHED, because `start` ends in
                // process.exit and a write still queued then is a write lost.
                // 'close' rather than the end() callback: it also fires when
                // the stream fails, so the drive cannot hang on a broken log.
                logEnded = true;
                stream.once('close', () => resolve(result));
                stream.end();
            } else {
                resolve(result);
            }
        };
        try {
            stream = fs.createWriteStream(session.agentLogPath, { flags: 'a' });
            // A step log that cannot be written is a lost record, not a lost
            // capture: said once, and the drive goes on without it.
            stream.on('error', (e) => {
                log.warn(`capture-har: the agent step log could not be written: ${e.message}`);
                logEnded = true;
                stream = null;
            });
            stream.write(`capture-har: agent (${session.agent}) started ` +
                `${new Date().toISOString()}\n`);
        } catch (e) {
            log.warn(`capture-har: could not open the agent step log: ${e.message}`);
        }
        const say = (text) => {
            if (stream && !logEnded) stream.write(text);
            process.stderr.write(text);
        };
        let proc;
        try {
            proc = spawnFn(child.command, child.args, {
                cwd: session.sessionDir,
                stdio: ['ignore', 'pipe', 'pipe'],
                windowsHide: true
            });
        } catch (e) {
            say(`capture-har: could not start ${child.command}: ${e.message}\n`);
            finish({ status: 127 });
            return;
        }
        // RECORDED, and signalled in exactly ONE case -- never as an ending.
        // The pid is here so the warning about an agent that outlived the
        // recording can name a process the operator can actually find; the
        // recorder does not end an agent because the RECORDING ended (see
        // agentStillDrivingLines). The one exception is below: the site is
        // throttling or blocking the drive, and continuing would be hammering
        // or evading it.
        session.agentPid = proc.pid === undefined ? null : proc.pid;
        if (opts.control) {
            opts.control.stop = (why) => {
                if (settled || exited || stoppedFor) return false;
                stoppedFor = why || 'stopped';
                say(`capture-har: the recorder stopped the agent ${new Date().toISOString()} ` +
                    `(${stoppedFor}): the site is throttling or blocking this session.\n`);
                try {
                    proc.kill();
                } catch (e) {
                    say(`capture-har: the agent could not be stopped: ${e.message}\n`);
                    return false;
                }
                return true;
            };
        }
        if (proc.stdout) proc.stdout.on('data', say);
        if (proc.stderr) proc.stderr.on('data', say);
        proc.once('error', (e) => {
            say(`capture-har: the agent could not be started: ${e.message}\n`);
            finish({ status: 127 });
        });
        let exited = null;
        let drainTimer = null;
        const ended = () => {
            if (drainTimer) clearTimeout(drainTimer);
            if (settled) return;
            const { code, signal } = exited || {};
            say(`capture-har: agent ended ${new Date().toISOString()}` +
                (signal ? ` (${signal})` : ` (exit ${code})`) + '\n');
            finish(Object.assign({ status: code === null || code === undefined ? 1 : code, signal: signal },
                stoppedFor ? { stoppedFor } : {}));
        };
        proc.once('exit', (code, signal) => {
            exited = { code, signal };
            // Not after 'error' already ended the drive: a timer nothing will
            // clear would hold the event loop open for the whole drain window.
            if (!settled) drainTimer = setTimeout(ended, drainMs);
        });
        proc.once('close', (code, signal) => {
            if (!exited) exited = { code, signal };
            ended();
        });
    });
}

/**
 * What the agent's exit means for the recording (#98).
 *
 * `recordingEndedBy` is the reason the recording has ALREADY ended for, or null
 * while it is still running. An agent that exits after ENTER, the stop command
 * or a closed window ended nothing: it is not an ending, it earns no warning
 * that it is "ending the recording", and it does not get to write its exit code
 * onto a session already recorded as ended by something else. So null.
 *
 * And an agent the RECORDER STOPPED (`result.stoppedFor`, a hand-over: the site
 * was throttling or blocking it) ended nothing either. Its exit is the
 * recorder's own doing, and the recording goes on for the human now driving.
 *
 * @returns {null | {reason: string, warning: string|null}}
 */
function agentExitEnding(result, recordingEndedBy) {
    if (recordingEndedBy) return null;
    if (result.stoppedFor) return null;
    if (result.status === 0) return { reason: 'agent-finished', warning: null };
    return {
        reason: 'agent-failed',
        warning: `capture-har: the agent exited ${result.status} -- ending the recording ` +
            'and post-processing what it did record.'
    };
}

/**
 * May the agent still be started? Asked immediately before it would be.
 *
 * Under --ai the start page loads BEFORE the agent is launched, so the site can
 * refuse -- a block, a bot check -- before there is an agent to stop. Starting
 * one then would put an agent to work on a page that has already said no, and
 * nothing would ever stop it: the hand-over has been made. Likewise a
 * recording that already ended (an unattended sign-in wall, ENTER, a closed
 * window) has nothing left for an agent to do, and anything it did would go
 * unrecorded.
 *
 * @returns {null | Array<[level, text]>} null to start it; otherwise what the
 *          operator is told instead
 */
function agentStartRefusal(session, recordingEndedBy) {
    if (session.handOver) {
        return [['warn', 'capture-har: the agent was not started: the site refused the start page ' +
            `(${session.handOver.signature} on ${session.handOver.url}).\n` +
            '  The browser is yours; nothing will drive it.']];
    }
    if (recordingEndedBy) {
        return [['warn', `capture-har: the agent was not started: the recording had already ended ` +
            `(${recordingEndedBy}).`]];
    }
    return null;
}

/**
 * Is an -AI agent driving the browser right now: launched, and neither
 * finished nor stopped? Before launch the page loads are the recorder's own
 * first navigation -- or, when the start page was refused, a person's -- and
 * neither is the agent's to be warned about or stopped.
 */
function agentDriving({ ai, agentLaunched, session }) {
    return !!ai && !!agentLaunched && session.agentExitCode === undefined && !session.agentStopped;
}

/**
 * Let the recording go quiet before an agent-driven ending tears it down (#98).
 *
 * TWO DIFFERENT WAITS, because two different things are unknown.
 *
 * What CAN be observed is waited for exactly: `recorder.inFlight` is the number
 * of handlers that have begun and not finished, and the wait ends the moment it
 * reaches zero. A fixed sleep would be both slower than that when nothing is
 * pending and shorter than it when something is.
 *
 * What CANNOT be observed gets a bounded GRACE: a request the browser has not yet
 * reported as finished is, from here, indistinguishable from no request at all.
 * Half a second is the measured shape of the defect -- the handler was mid-flight
 * at `stop()`, not minutes away from starting.
 *
 * And the whole thing is CAPPED. A response body the browser will never hand over
 * must not hold a capture hostage: the artifacts are worth more than the one
 * entry, so the cap is reported rather than waited through.
 *
 * @returns {Promise<{drained: boolean, waitedMs: number}>}
 */
async function settleRecorder(recorder, opts = {}) {
    const graceMs = opts.graceMs === undefined ? AGENT_SETTLE_GRACE_MS : opts.graceMs;
    const maxMs = opts.maxMs === undefined ? AGENT_SETTLE_MAX_MS : opts.maxMs;
    const pause = opts.sleep || sleep;
    const started = Date.now();
    if (graceMs > 0) await pause(graceMs);
    while ((recorder.inFlight || 0) > 0 && Date.now() - started < maxMs) {
        await pause(Math.min(POLL_MS, 50));
    }
    return { drained: (recorder.inFlight || 0) === 0, waitedMs: Date.now() - started };
}

/**
 * What the operator is told when the recording ended BEFORE the agent did.
 *
 * The issue describes the agent ending the recording. The other direction is just
 * as reachable -- ENTER, `Stop-HarRecording`, or closing the window, all of which
 * win the same race -- and then an agent is still browsing while nothing is being
 * recorded any more.
 *
 * NOT KILLED. "Nothing in this file kills a process" is older and wider than this
 * feature, and an agent mid-action is a process whose work is somebody's live
 * account. So the operator is told, with the pid, because a warning about a
 * process nobody can find is not actionable. (The recording ending is never a
 * reason to stop an agent. The one thing that is -- the site throttling or
 * blocking the drive while the recording goes on -- is driveWithAgent's
 * `control.stop`, and an agent stopped that way is not "still running".)
 */
function agentStillDrivingLines(session) {
    return [
        ['warn', `capture-har: the ${session.agent} agent is still running (pid ` +
            `${session.agentPid === undefined || session.agentPid === null
                ? 'unknown' : session.agentPid}) and what it ` +
            'does from here is no longer being recorded.\n' +
            '  This run ended the recording first and killed nothing. End the agent yourself ' +
            'if\n  you are done with it.'],
        ['warn', `  Its step log, up to this point: ${session.agentLogPath}`]
    ];
}

/**
 * What the operator is told when an agent takes the wheel.
 *
 * Said at info level and before the first action, because a browser that starts
 * moving on its own is alarming unless it was announced -- and because the two
 * facts an operator needs while watching it are what it will refuse to do and
 * how to take over. ENTER still ends the recording: a drive is never a loss of
 * control.
 */
function agentBannerLines(session) {
    return [
        ['info', `  An agent (${session.agent}) is driving this recording. It works at a human ` +
            'pace, keeps\n' +
            '  this tab in front, enters no passwords, personal details or payment data, and\n' +
            '  stops and hands back at any bot check or sign-in prompt.'],
        ['info', `  Its instructions: ${session.agentPromptPath}\n` +
            `  Its step log:     ${session.agentLogPath}`],
        ['info', '  The recording ends when the agent finishes or hands back. Press ENTER here ' +
            'to\n  end it sooner.'],
        ['info', `  Pace: ${pacing.describePace(session.pace || pacing.DEFAULT_PACE)}.\n` +
            '  If the site throttles or blocks the drive, the agent is stopped and the browser is\n' +
            '  handed to you -- nothing retries a block or works around a bot check.']
    ];
}

// ---------------------------------------------------------------------------
// Post-processing: scrub -> verify -> digest -> catalogue
// ---------------------------------------------------------------------------

// `--literals -` and its stdin document, or nothing when there are no values.
function literalArgs(literals) {
    return literals && literals.length ? ['--literals', '-'] : [];
}
function literalInput(literals) {
    return literals && literals.length ? JSON.stringify(literals) : undefined;
}

// `input` is written to the child's stdin -- the only channel the sign-in
// values (#115) take to the scrub: not argv, not the environment, not a file.
function runNode(script, argv, cwd, input) {
    const result = spawnSync(process.execPath, [script, ...argv], {
        encoding: 'utf8', cwd, windowsHide: true, input
    });
    return {
        ok: result.status === 0,
        status: result.status,
        stdout: result.stdout || '',
        stderr: result.stderr || ''
    };
}

/**
 * Put `candidate` in front of the leak gate and record its verdict on `state`.
 *
 * ONE implementation, because there are now two doors into the pipeline. A
 * full capture arrives here from postProcess with a scrub it just wrote; the
 * `catalogue` command arrives here with a HAR somebody hands it. Both need the
 * same three-way verdict, and a second copy of it in the second door is
 * exactly how "advisory" quietly becomes "rejected" on one path only.
 *
 * Exit 4 is not a rejection. It says the only findings are identity evidence
 * by SHAPE, which carries no provenance -- a Luhn-valid 16-digit run is a
 * card, a trip id, or ~10% of digit runs by chance. Withholding the capture
 * over that is what cost 1413 trip ids their reference; the artifact is kept
 * and the findings are surfaced, so a false positive costs a review step
 * instead of the capture.
 *
 * @returns {{path: string, verified: boolean, advisory: boolean}} state.scrubbed
 */
function askTheGate(candidate, state, run, literals) {
    log.info('capture-har: verifying the scrub ...');
    const verify = (run || runNode)(
        path.join(__dirname, '..', 'har', 'verify-scrub.js'),
        ['--in', candidate, ...literalArgs(literals)], undefined, literalInput(literals));
    const advisory = !verify.ok && verify.status === VERIFY_ADVISORY_EXIT;
    state.scrubbed = { path: candidate, verified: verify.ok, advisory };
    // verify-scrub.js already names itself in what it prints, so a second
    // prefix would read as two tools reporting the same thing.
    const spoke = verify.stderr.trim();
    const said = spoke.startsWith('verify-scrub')
        ? spoke : `verify-scrub: ${spoke || `exit ${verify.status}`}`;
    if (advisory) state.warnings.push(said);
    else if (!verify.ok) state.errors.push(said);
    return state.scrubbed;
}

/**
 * Digest the scrubbed capture at `state.scrubbed.path`, then hand the result to
 * whoever catalogues.
 *
 * Split out of postProcess so the `catalogue` command can run THIS code rather
 * than a copy of it. Nothing about the phase changed in the move: it still
 * derives from the scrubbed capture, still refuses to clobber an existing
 * catalogue, and still asks decideCatalogueRunner -- once, in runCatalogue --
 * who does the segmenting.
 *
 * It does not publish and it does not quarantine. Those belong to the scrub
 * stage, and a second implementation of them behind a second door is the
 * two-engines defect this subsystem has spent a dozen PRs removing.
 */
function catalogueScrubbed(session, state, opts = {}) {
    try {
        log.info('capture-har: building the digest ...');
        const har = harDocument.readHarDocument(state.scrubbed.path).document;
        // capturedUtc is when the RECORDING happened, not when it was
        // processed. A catalogue row dated to the scrub would answer "how old
        // is this evidence of their API" with the wrong number -- and that
        // question is most of why the date is in the filename convention.
        const digest = buildDigest(har, {
            uri: session.uri,
            describe: session.describe,
            capturedUtc: session.startedUtc
        });
        const digestPath = path.join(session.outputPath, DIGEST_FILE);
        writeJson(digestPath, digest);
        state.digest = { path: digestPath };

        const cataloguePath = path.join(session.outputPath, CATALOGUE_FILE);
        // Never clobber an existing catalogue: a re-capture must not erase the
        // actions a previous run's AI phase already named and exercised.
        if (!fs.existsSync(cataloguePath)) {
            writeJson(cataloguePath, buildCatalogueScaffold(digest, {
                referenceFile: referenceRelativePath(session)
            }));
        }
        state.catalogue = Object.assign(
            { path: cataloguePath, actions: [], files: [] },
            runCatalogue(session, digestPath, cataloguePath, state, opts));
        // Described, never performed -- see describeReference. Placed after the
        // catalogue so a rejected scrub, which returns before any of this,
        // cannot reach it: a capture the leak gate refused promotes nothing.
        state.reference = describeReference(session, state);
    } catch (e) {
        state.errors.push(`digest: ${e.message}`);
    }
    return state;
}

/**
 * What promoting this capture into a committable reference would produce.
 *
 * A DESCRIPTION, not an action. Nothing here copies, writes or creates
 * anything, and that is the whole point of the shape:
 *
 *  - A reference is a TRIMMED EXTRACT, not a whole scrubbed capture. Measured
 *    in a consuming project, committed references run 3 KB - 60 KB while the
 *    captures they came from run 277 MB - 1.6 GB. Copying `scrubbed.har` into
 *    `docs/har-reference/` would commit a several-hundred-megabyte file where a
 *    20 KB extract belongs.
 *  - The tool that makes the extract already exists --
 *    `har/extract-har-reference.js` -- and already implements this naming
 *    convention, the no-truncation rule for request bodies, and the decoded
 *    `postData.params[]`. Re-implementing any of that here would be a second
 *    engine.
 *  - That tool selects the API traffic BY DEFAULT (#410) and reports what it
 *    dropped, by category. It used to refuse to run without `--match`, on the
 *    reasoning that choosing which entries matter is a judgement a tool cannot
 *    make -- until the operator, asked for a regex, could not supply one and
 *    named the mechanical distinction instead ("the API calls, not the fonts,
 *    images"). So the run ends by telling the operator exactly what to type,
 *    with the provider, the action and the destination already filled in, and
 *    nothing left to invent. `--match` is still available to narrow within the
 *    API set when a capture is API-heavy.
 *
 * Returns null when there is nothing to suggest -- no host to derive a provider
 * from, or no verified artifact to extract from.
 */
function describeReference(session, state, cwd) {
    if (!state || !state.scrubbed || !state.scrubbed.path) { return null; }
    const host = uriHostOf(session && session.uri);
    const fileName = referenceFileName(session);
    const relativePath = referenceRelativePath(session);
    if (!host || !fileName || !relativePath) { return null; }
    // The CURRENT working tree, which is where the operator is standing and
    // where a committed reference belongs. Never the main working tree: that is
    // the raw capture's anchor, chosen for its lifetime, and it is frequently
    // the protected branch.
    const placement = capturePlacement(cwd || process.cwd());
    const root = placement.currentWorkingTree || path.resolve(cwd || process.cwd());
    // NESTED under the provider: `docs/har-reference/<host>/<provider>/`. The
    // host directory holds the committed `catalogue.json` and its generated
    // `README.md` (#379); each provider under it holds that provider's
    // extracts and its generated `api.json` (#382). A host routinely spans
    // several third-party APIs, so the provider level is what keeps one
    // provider's references, policy and API document together.
    // Derived FROM relativePath rather than recomputing the provider, so the
    // path the run prints and the value it records in `HarFile` cannot drift
    // apart. They are the same fact stated twice; only one of them computes it.
    const dir = path.join(root, REFERENCE_DIR, host, path.dirname(relativePath));
    return {
        fileName,
        relativePath,
        dir,
        path: path.join(root, REFERENCE_DIR, host, ...relativePath.split('/')),
        source: state.scrubbed.path,
        extractor: path.join(__dirname, '..', 'har', 'extract-har-reference.js')
    };
}

// The catalogue table, column for column what HarCapture.Format.ps1xml already
// renders. The two entry points agree because they show the same five columns
// in the same order, and neither shows anything else.
//
// NONE OF THESE FIELDS CAN CARRY A CAPTURED VALUE. `Endpoints` is
// `<host><pathTemplate>` from the digest, which groups on the TEMPLATE -- ids
// are collapsed before they reach here, and the digest is derived from the
// SCRUBBED capture in the first place. `Action` is built from the same
// template. `Methods`, `EntryCount` and `Status` are a verb, a count and an
// enum. `Description` is the operator's own words and is deliberately NOT a
// column: the PowerShell table does not show it either, and parity is what
// stops the two renderings drifting.
const CATALOGUE_COLUMNS = [
    { label: 'Status', width: 10, value: (r) => r.Status },
    { label: 'Action', width: 32, value: (r) => r.Action },
    { label: 'Methods', width: 12, value: (r) => (r.Methods || []).join(',') },
    { label: 'Entries', width: 7, right: true, value: (r) => r.EntryCount },
    { label: 'Endpoints', width: 0, value: (r) => (r.Endpoints || []).join(', ') }
];

function catalogueCell(column, row) {
    const raw = column.value(row);
    const text = raw === null || raw === undefined ? '' : String(raw);
    if (!column.width) { return text; }
    // Padded, never truncated. PowerShell's formatter truncates to the column
    // width; here a long path template would lose its tail, and the tail is the
    // part that distinguishes two endpoints under one prefix.
    return column.right ? text.padStart(column.width) : text.padEnd(column.width);
}

function catalogueTableLines(rows) {
    const list = (rows || []).filter((r) => r && typeof r === 'object');
    if (!list.length) { return []; }
    const header = CATALOGUE_COLUMNS
        .map((c) => (c.width ? (c.right ? c.label.padStart(c.width) : c.label.padEnd(c.width)) : c.label))
        .join(' ').replace(/\s+$/, '');
    const rule = CATALOGUE_COLUMNS
        .map((c) => '-'.repeat(c.width || Math.max(9, ...list.map((r) => catalogueCell(c, r).length))))
        .join(' ');
    const body = list.map((row) =>
        CATALOGUE_COLUMNS.map((c) => catalogueCell(c, row)).join(' ').replace(/\s+$/, ''));
    return [header, rule].concat(body);
}

/**
 * The mechanical phases, run in this process on every ending that is not a
 * cancel. Recording success dominates: a scrub failure is reported and exits
 * 6, but it never reports a good capture as lost, and the raw is always kept.
 *
 * Three verdicts, not two. Clean promotes; ADVISORY (identity evidence by
 * shape) also promotes, with the findings surfaced as a warning and exit 7;
 * anything else quarantines the candidate and stops before the digest. The
 * middle verdict is the whole point of #297 -- a false positive should cost a
 * review step, not the capture.
 */
function postProcess(session, opts = {}) {
    // The runner is injectable so a test can exercise the "sanitized, but the
    // leak gate rejected it" branch without having to find a value that the
    // two real detectors happen to disagree about. What is under test there is
    // this function's decision, not sanitize-har.js's pattern list.
    const run = opts.run || runNode;
    const harDir = path.join(__dirname, '..', 'har');
    const state = { startedUtc: new Date().toISOString(), errors: [], warnings: [] };

    // Phase A -- scrub, then verify. Both reused, never reimplemented.
    //
    // The candidate is written INSIDE the session directory and only promoted
    // to the output path once the gate has passed on it. That ordering is what
    // makes "the output path receives only verified artifacts" true by
    // construction rather than by cleanup: there is no window in which a file
    // the gate has not yet judged sits in a committable directory, and a
    // re-capture whose scrub is rejected cannot have already overwritten the
    // verified artifact a previous run left there.
    const candidate = path.join(session.sessionDir, SCRUBBED_HAR);
    fs.mkdirSync(session.sessionDir, { recursive: true });
    // Each stage is named as it STARTS (#492). Every one of them is silent
    // until it finishes, and after a recording stops they run for minutes --
    // which an operator cannot tell apart from a hang.
    log.info(`capture-har: scrubbing ${describeSize(session.harPath)} ...`);
    const tables = session.unprotectedTables
        ? ['--subs', path.join(session.sessionDir, subsDestination.LEGACY_SUBS_FILENAME),
            '--pii-subs', path.join(session.sessionDir, subsDestination.PII_SUBS_FILENAME)]
        : [];
    // The values the recorder signed in with (#115), handed to BOTH the scrub
    // and the gate: removed under whatever name the site used for them, and
    // then proved absent.
    const literals = opts.literals || [];
    // The values live only in the recorder's memory. A post-process that runs
    // without them -- `stop` after the recorder died -- still scrubs by key
    // name, but cannot prove the filled values gone, and says so rather than
    // letting a pass read as one that checked.
    if (!opts.literals && session.signIn
        && (session.signIn.source === 'bitwarden' || session.signIn.source === 'prompt')) {
        state.warnings.push('sign-in: this capture signed in, but the values it filled were not ' +
            'available to this scrub (the recorder that held them has exited). They were scrubbed ' +
            'by field name only; check the scrubbed capture for them before sharing it.');
    }
    const sanitize = run(path.join(harDir, 'sanitize-har.js'),
        ['--in', session.harPath, '--out', candidate, ...tables, ...literalArgs(literals)],
        undefined, literalInput(literals));
    if (!sanitize.ok) {
        state.errors.push(`sanitize-har: ${sanitize.stderr.trim() || `exit ${sanitize.status}`}`);
        state.scrubbed = { path: null, verified: false, advisory: false };
    } else {
        // The scrub's floor (#511): values the gate would have blocked were
        // blunted rather than the capture refused. Read off the scrub's own
        // stable stdout line -- counts only, never a value -- so the run
        // summary says the artifact was blunted, not merely verified.
        const blunted = /^sanitize-har: blunted: (.+)$/m.exec(sanitize.stdout || '');
        if (blunted) {
            state.blunted = blunted[1];
            state.warnings.push(`sanitize-har: BLUNTED ${blunted[1]} -- values the leak gate ` +
                'would have blocked, replaced by typed sentinels and recorded in the artifact');
        }
        askTheGate(candidate, state, run, literals);
    }

    // Phase B input -- the digest an AI segments from.
    //
    // Derived from the SCRUBBED capture, never the raw one. The digest and the
    // catalogue are written to the output path, which the design promises
    // receives only scrubbed, verified artifacts -- and a URL path segment
    // carrying an operator identifier survives grouping untouched, because
    // path templating collapses ids, not secrets. Scrubbing the input is the
    // control; the template heuristic is not and cannot be.
    //
    // So a scrub that did not VERIFY stops the pipeline here -- gating on
    // "a scrubbed file exists" would not be enough. sanitize-har.js and
    // verify-scrub.js apply deliberately different detectors, and a value the
    // first misses and the second catches is precisely the case the two-stage
    // design exists for. Falling through on it would derive a digest and a
    // catalogue from a capture already known to be leaking, and write all
    // three into the committable output path -- where `git add -A` beats the
    // exit-6 warning that arrives afterwards.
    //
    // Reporting a digest built from an unverified capture is worse than
    // reporting none: it looks safe.
    if (!state.scrubbed || !(state.scrubbed.verified || state.scrubbed.advisory)) {
        // Quarantine, not deletion. The old code UNLINKED the scrubbed file
        // here, so one false positive destroyed the whole capture -- no
        // artifact, no digest, no catalogue, and a missing file to explain it.
        // The reasoning was right and is kept: a file the gate refused must not
        // sit where `git add -A` will take it. What changes is that the
        // invariant is now preserved by WHERE the file goes.
        quarantineRejectedScrub(session, state);
        state.completedUtc = new Date().toISOString();
        return state;
    }
    // Verified (or advisory-only): promote the candidate to the output path.
    // A COPY, because nothing under .har-captures/ is ever taken away -- the
    // session keeps the complete record of what it produced.
    try {
        fs.mkdirSync(session.outputPath, { recursive: true });
        sweepAbandonedTemps(session.outputPath);
        state.scrubbed.path =
            publishFile(state.scrubbed.path, path.join(session.outputPath, SCRUBBED_HAR));
        // A findings report describes ONE run. Left behind by a re-capture
        // that came back clean it would still read as current, which is the
        // same disease as a digest built from an unverified capture: it looks
        // like information. The output path is the committable tree, not the
        // archive -- the session directory keeps every report ever written.
        const publishedReport = path.join(session.outputPath, FINDINGS_FILE);
        const freshReport = path.join(session.sessionDir, FINDINGS_FILE);
        if (!fs.existsSync(freshReport) && isFindingsReport(publishedReport)) {
            try { fs.unlinkSync(publishedReport); } catch { /* leave it */ }
        }
        // A sidecar that will not copy must not fail the capture -- the report
        // is a triage aid, not the gate. But the degradation has to carry a
        // signal. Silently leaving `findings` null ends the run byte-for-byte
        // the way a CLEAN one ends, so neither the operator nor an agent
        // reading the artifacts afterwards can tell "no report was needed"
        // from "advisory findings exist and their report did not make it" --
        // while the front door goes on naming a file that is not there.
        //
        // The report is not lost in that state: it is complete in the session
        // directory. So point at it and say so.
        if (fs.existsSync(freshReport)) {
            try {
                state.scrubbed.findings = publishFile(freshReport, publishedReport);
            } catch (e) {
                state.scrubbed.findings = freshReport;
                state.warnings.push(
                    `capture-har: the findings report could not be published to the output ` +
                    `path (${e.message}). It is complete at ${freshReport}`);
            }
        }
    } catch (e) {
        state.errors.push(`publish: ${e.message}`);
        state.scrubbed.path = null;
        state.completedUtc = new Date().toISOString();
        return state;
    }
    catalogueScrubbed(session, state, opts);

    state.completedUtc = new Date().toISOString();
    return state;
}

/**
 * Is the file at `p` a findings report THIS tool wrote?
 *
 * Clearing a stale report must key on the document, not on the filename. The
 * output path being tool-owned is documented and bannered, which makes it a
 * convention; deleting whatever occupies a name on the strength of a
 * convention is how somebody's hand-written notes disappear.
 */
function isFindingsReport(p) {
    const doc = readJson(p);
    return !!doc && typeof doc === 'object'
        && doc.schemaVersion !== undefined && Array.isArray(doc.findings);
}

/**
 * A name in `dir` that is not taken yet: `base.ext`, then `base.2.ext`, ...
 *
 * Nothing under `.har-captures/` is ever deleted or overwritten BY THIS TOOL,
 * and a second rejection in a session that already holds one is exactly where a
 * careless implementation would overwrite the first -- which is the evidence
 * the operator is still triaging.
 *
 * The qualifier is not a loophole. An operator can now ask for a verified
 * scrub's source to be removed (`Invoke-SanitizeHar.ps1 -RemoveSource`, #352),
 * which is a request, per run, about one raw. It cannot reach a rejected
 * scrub's evidence: that path never verifies, so the removal never runs.
 */
function freeName(dir, base, ext) {
    let candidate = path.join(dir, `${base}${ext}`);
    for (let n = 2; fs.existsSync(candidate); n++) {
        candidate = path.join(dir, `${base}.${n}${ext}`);
    }
    return candidate;
}

/**
 * Put `from` at `to` so that `to` is never seen holding a partial write.
 *
 * The bytes land under a temporary name in the SAME directory and then take
 * the real name with a rename, which either happens or does not -- there is no
 * intermediate state in which the destination exists and is incomplete. A
 * plain copy has one: a run killed part-way through leaves a truncated file
 * under the name that means "verified", and two captures publishing at once
 * interleave into a corrupted one rather than one of them simply winning.
 *
 * The whole invariant here is "a file that exists at the output path has
 * already been judged", so a half-written file under that name is worse than
 * no file at all -- neither a reader nor `git add -A` can tell it apart.
 *
 * The temporary is removed if the rename does not happen, so a failure leaves
 * the previous artifact in place and nothing else behind.
 *
 * The rename is retried on a transient Windows lock -- see renameWithRetry.
 * Atomicity is not free on that platform, and the retry is what stops the
 * guarantee costing availability.
 */
/**
 * Block for `ms` without an event loop turn.
 *
 * postProcess is synchronous by design -- it runs the mechanical phases in the
 * recorder's own process -- so the backoff cannot be a promise without turning
 * the whole call chain async for a wait measured in milliseconds.
 */
function sleepSync(ms) {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

/**
 * `fs.renameSync`, retried while the destination is momentarily held.
 *
 * Bounded on purpose. Retrying until a lock clears would hang a capture on a
 * file left open in an editor, which is a worse failure than the one being
 * avoided: an error can at least be read. And the retry is narrow -- every
 * attempt is still a rename, so nothing here weakens the property that the
 * destination is never seen holding a partial write.
 */
function renameWithRetry(from, to) {
    for (let attempt = 1; ; attempt++) {
        try {
            fs.renameSync(from, to);
            return;
        } catch (e) {
            if (attempt >= PUBLISH_RENAME_ATTEMPTS
                || !PUBLISH_RENAME_RETRY_CODES.has(e.code)) throw e;
            sleepSync(PUBLISH_RENAME_BACKOFF_MS * 2 ** (attempt - 1));
        }
    }
}

function publishFile(from, to) {
    // The default output path IS the session directory now (#377), so the
    // candidate is frequently already at its destination. Copying a file onto
    // itself through a temporary is not a no-op -- it is a window in which the
    // only copy exists under a name nothing recognises -- so the identity case
    // is answered before any bytes move.
    if (path.resolve(from) === path.resolve(to)) { return to; }
    const temp = path.join(path.dirname(to),
        `${PUBLISH_TEMP_PREFIX}${process.pid}-${Date.now().toString(36)}-` +
        `${Math.random().toString(36).slice(2, 8)}`);
    fs.copyFileSync(from, temp);
    try {
        renameWithRetry(temp, to);
    } catch (e) {
        try { fs.unlinkSync(temp); } catch { /* nothing more to do */ }
        throw e;
    }
    return to;
}

/**
 * Remove publication temporaries abandoned by a run that died.
 *
 * Age is the discriminator, not the prefix alone. A blind sweep would delete
 * the in-flight temporary of a CONCURRENT capture and reintroduce exactly the
 * corruption `publishFile` exists to prevent -- from the other side.
 */
function sweepAbandonedTemps(dir, now = Date.now()) {
    let names;
    try { names = fs.readdirSync(dir); } catch { return; }
    for (const name of names) {
        if (!name.startsWith(PUBLISH_TEMP_PREFIX)) continue;
        const full = path.join(dir, name);
        try {
            if (now - fs.statSync(full).mtimeMs < PUBLISH_TEMP_STALE_MS) continue;
            fs.unlinkSync(full);
        } catch { /* another run may have just taken it; that is fine */ }
    }
}

/**
 * Move a scrub the leak gate refused out of the way, keeping every byte.
 *
 * The candidate already lives in the session directory, so this is a rename
 * within one volume: it cannot half-succeed, and it cannot leave the artifact
 * in a committable directory. The findings report verify-scrub.js wrote beside
 * it moves with it under a matching name, because a report separated from the
 * artifact it describes is a report nobody can act on.
 */
function quarantineRejectedScrub(session, state) {
    if (!state.scrubbed || !state.scrubbed.path) return;
    const source = state.scrubbed.path;
    state.scrubbed.path = null;
    if (!fs.existsSync(source)) return;

    const stem = REJECTED_HAR.slice(0, -path.extname(REJECTED_HAR).length);
    const dest = freeName(session.sessionDir, stem, path.extname(REJECTED_HAR));
    // The suffix that made the artifact's name unique, so the report keeps the
    // same one and the pair stays legible in a directory listing.
    const suffix = path.basename(dest, path.extname(REJECTED_HAR)).slice(stem.length);
    try {
        fs.renameSync(source, dest);
    } catch (e) {
        state.errors.push(`quarantine: ${e.message}`);
        return;
    }
    state.scrubbed.quarantined = dest;
    state.scrubbed.rejected = true;

    const report = path.join(session.sessionDir, FINDINGS_FILE);
    if (fs.existsSync(report) && suffix) {
        const reportStem = FINDINGS_FILE.slice(0, -path.extname(FINDINGS_FILE).length);
        const moved = path.join(session.sessionDir,
            `${reportStem}${suffix}${path.extname(FINDINGS_FILE)}`);
        try { fs.renameSync(report, moved); state.scrubbed.findings = moved; } catch { /* keep */ }
    } else if (fs.existsSync(report)) {
        state.scrubbed.findings = report;
    }
}

/**
 * How the AI catalogue child is launched: `claude -p <prompt>`, run UNDER
 * run-with-heartbeat.js (#492).
 *
 * The child is ~95% of the wall time after a recording stops, and under `-p`
 * it prints nothing at all -- measured at fifteen silent minutes on one real
 * capture. postProcess is synchronous, so it cannot keep a timer of its own
 * while `spawnSync` blocks; the wrapper is a second process that can. The argv
 * after the wrapper is exactly what the child was always given.
 */
function catalogueChildCommand(prompt) {
    return {
        command: process.execPath,
        args: [path.join(__dirname, 'run-with-heartbeat.js'), 'claude', '-p', prompt]
    };
}

/** "67 MB" / "12.4 KB" -- the scale of what a stage is about to chew through. */
function describeSize(filePath) {
    let bytes;
    try { bytes = fs.statSync(filePath).size; } catch { return 'the raw capture'; }
    return bytes >= 1024 * 1024
        ? `${Math.round(bytes / (1024 * 1024))} MB`
        : `${(bytes / 1024).toFixed(1)} KB`;
}

/**
 * `opts.runnerContext` and `opts.spawn` exist for one reason: the claude-cli
 * branch is otherwise unreachable from a test (it needs a TTY and `claude` on
 * PATH), and "the AI child runs under the heartbeat wrapper" is a property of
 * THIS function, not of the helper that builds the argv (#492).
 */
function runCatalogue(session, digestPath, cataloguePath, state, opts = {}) {
    const spawn = opts.spawn || spawnSync;
    // The probe SPAWNS `claude --version` (see claudeOnPath), so it is asked only
    // when its answer can change the decision: interactive, and not already
    // inside an agent. Elsewhere it would cost a process launch on every
    // post-process for nothing.
    const isTty = !!process.stdin.isTTY;
    const insideAgent = !!(process.env.CLAUDECODE || process.env.CLAUDE_CODE_ENTRYPOINT);
    const decision = decideCatalogueRunner(opts.runnerContext || {
        env: process.env,
        isTty,
        claudeOnPath: isTty && !insideAgent && claudeOnPath()
    });
    const result = { delegatedTo: decision.delegatedTo, pending: decision.pending };

    if (decision.delegatedTo === 'claude-cli') {
        const prompt =
            `${fs.readFileSync(decision.promptPath, 'utf8')}\n\n` +
            `Digest: ${digestPath}\nCatalogue: ${cataloguePath}\n` +
            `Raw capture: ${session.harPath}\nOutput path: ${session.outputPath}\n` +
            (session.describe ? `Operator intent: ${session.describe}\n` : '') +
            // WHERE THE ACTIONS ARE WRITTEN DOWN, when an agent drove (#98). The
            // step log is kept so segmentation has a record of what was done and
            // where it stopped -- which is only true if the step that segments is
            // told the file exists. Absent for a hand-driven capture, because a
            // path to a file that is not there is worse than silence.
            (session.agentLogPath ? `Agent step log: ${session.agentLogPath}\n` : '');
        log.info('capture-har: cataloguing (AI pass -- typically several minutes) ...');
        const child = catalogueChildCommand(prompt);
        const run = spawn(child.command, child.args, {
            encoding: 'utf8', cwd: session.outputPath, stdio: 'inherit', windowsHide: true
        });
        if (run.status !== 0) {
            state.errors.push(`catalogue: claude exited ${run.status}`);
            result.pending = true;
        }
    } else if (decision.delegatedTo === 'none') {
        result.skippedReason =
            `no AI runner available -- catalogue with the prompt at ${decision.promptPath}`;
    }

    const rows = readJson(cataloguePath) || [];
    result.actions = rows.filter((r) => r.Status === 'Exercised').map((r) => r.Action);
    result.files = rows.map((r) => r.HarFile).filter(Boolean);
    return result;
}

// ---------------------------------------------------------------------------
// Summaries
// ---------------------------------------------------------------------------

function summarize(harPath) {
    if (!harPath || !fs.existsSync(harPath)) return { exists: false, bytes: 0 };
    const bytes = fs.statSync(harPath).size;
    const summary = { exists: true, bytes, path: harPath };
    try {
        // A file that is not a capture lands in `parseError` below with a
        // message saying so, rather than being summarised as a capture that
        // recorded nothing (issue #423).
        //
        // Walked, never held (#450). This runs on the RAW at capture time, and
        // the raw is the largest file the pipeline ever sees; a count and a
        // set of hosts are all the summary keeps, so that is all it holds. The
        // fields are assigned only after the walk completes, so a refusal
        // mid-walk leaves a parseError and no partial count beside it.
        let count = 0;
        const hosts = new Set();
        for (const e of harDocument.iterateHarEntries(harPath)) {
            count++;
            try { hosts.add(new URL(e.request.url).host); } catch (x) { hosts.add('?'); }
        }
        summary.entries = count;
        summary.hosts = [...hosts].sort();
    } catch (e) {
        summary.parseError = e.message;
    }
    return summary;
}

/**
 * What the console says once a recording is under way.
 *
 * The split is by what the operator needs in order to browse versus what they
 * need in order to debug. The site being recorded and how to end the recording
 * are the first; every resolved path, the profile and the debugging endpoint
 * are the second.
 *
 * The external-profile caveat is deliberately NOT a diagnostic: it says some
 * other tool is locked out for the duration, which the operator has to know
 * before they start rather than after they wonder why.
 */
function startBannerLines(session) {
    const lines = [['info', `\ncapture-har: recording ${session.uri}`]];
    if (session.externalProfile) {
        lines.push(['info',
            '  This profile belongs to another tool, which cannot use it\n' +
            '  until the recording ends.']);
    }
    // Absent when attached, where the browser is running on a profile this run
    // did not choose. Printing `null` would answer the question wrongly rather
    // than declining to answer it.
    if (session.profileDir) lines.push(['verbose', `  profile:  ${session.profileDir}`]);
    if (session.storageState) lines.push(['verbose', `  session:  ${session.storageState}`]);
    // Still verbose: the capture ROOT is announced unconditionally before this
    // banner (#367), and that is the durability-relevant half. The per-session
    // path under it stays a diagnostic, as the rest of these do.
    lines.push(['verbose', `  raw:      ${session.harPath}  (never committed)`]);
    lines.push(['verbose', `  output:   ${session.outputPath}  (scrubbed artifacts only)`]);
    lines.push(['verbose', `  cdp:      ${session.cdpEndpoint}` +
        (session.port !== session.requestedPort ? `  (${session.requestedPort} was busy)` : '')]);
    // WHOSE BROWSER THIS IS, at info level, because it changes what the operator
    // should do when the recording ends (#73). A window that stays open looks
    // like a leak unless it was announced as the contract.
    if (session.attached) {
        lines.push(['info',
            '  Attached to a browser this recorder did not launch. Ending the recording\n' +
            '  DETACHES: the browser, its window and its tabs are left exactly as they are.']);
    } else if (session.browser === BROWSER_NATIVE) {
        lines.push(['info',
            '  Recording in a natively launched Chrome -- no automation flags, so a site\n' +
            '  with bot protection sees an ordinary browser. Ending the recording detaches\n' +
            '  and leaves it running; close the window yourself when you are done with it.']);
    }
    // WHICH TABS, said where the operator can still act on it (#85). Not in
    // the Playwright mode, where the browser is this run's own and holds
    // nothing else.
    if (session.attached || session.browser === BROWSER_NATIVE) {
        const site = captureSite(session.uri) || session.uri;
        lines.push(['info',
            `  Recording ONLY this capture's tab, windows opened from it, and tabs on ${site}.
` +
            '  Other tabs in this browser are not recorded.']);
    }
    // "The most complete HAR" is a claim about recordHar, which only exists in a
    // browser Playwright launched. Saying it in a mode where recordHar never
    // runs would promise a richer artifact for pressing a key that cannot
    // produce one.
    lines.push(['info', session.ownsRecordHar === false
        ? '\n  Browse, then press ENTER here to end the recording.\n'
        : '\n  Browse, then press ENTER here -- that writes the most complete HAR.\n']);
    return lines;
}

/**
 * What the console says when a recording in a browser it did not close ends.
 *
 * Two facts, both about the window still sitting there. It is the CONTRACT,
 * not a failed cleanup (#73) -- every other mode ends with the browser gone, and
 * an operator who closes it on that assumption loses the signed-in profile.
 * And its debugging port is STILL OPEN (#85): `--remote-debugging-port` has no
 * authentication, so for as long as that window lives any local process can
 * attach to it and read the profile's cookies. That is a residual risk this
 * mode carries by design, so the remedy is said every time rather than only in
 * SKILL.md.
 */
function detachedLines(session) {
    return [
        ['info', `  the browser is still running at ${session.cdpEndpoint} -- ` +
            'this run detached and closed nothing.'],
        ['info', '  Its debugging port stays open, unauthenticated, until that window closes -- ' +
            'close it when you are done.']
    ];
}

/**
 * What the console says about the artifacts, once the phases have run.
 *
 * `scrubbed` and `catalogue` stay visible without -Verbose because they are
 * what the operator acts on next; a default run that ended without naming them
 * would send them hunting. `raw` and `digest` are a diagnostic and an
 * intermediate.
 *
 * A rejected scrub says so at warn level, which no threshold suppresses. Going
 * quiet about it would read as "no scrub was attempted" -- a different and far
 * less alarming story than "one was attempted and the leak gate refused it".
 */
/**
 * Render the findings report for the console: one triageable line each.
 *
 * "credit-card, 1413 occurrences" with no location is the report that taught
 * operators to ignore this gate. A kind, a class, an entry index, a key path,
 * a count and a fingerprint is one pass of triage instead -- and the escape is
 * named, because a warning nobody can act on is a warning nobody reads.
 *
 * The report holds no values (verify-scrub.js writes it that way), so nothing
 * here can print one. Everything below is a name, a location or a hash.
 */
function findingSummaryLines(reportPath) {
    const doc = readJson(reportPath);
    if (!doc || !Array.isArray(doc.findings)) return [];
    const shown = doc.findings.filter((f) => f.disposition !== 'reported');
    const lines = shown.slice(0, 10).map((f) => {
        // A finding inside a percent-encoded parameter has no JSON key path of
        // its own -- the enclosing field name is the location, and printing
        // nothing there would leave the operator with a fingerprint and no
        // idea where to look.
        const at = f.keyPath || (f.enclosing ? `(inside encoded ${f.enclosing})` : '');
        const where = f.entryIndex === undefined
            ? '' : ` at entry ${f.entryIndex}${at ? ` ${at}` : ''}`;
        const count = f.count > 1 ? `, x${f.count}` : '';
        return `${f.disposition === 'gating' ? '-' : '!'} ${f.kind}` +
            `${f.class ? ` [${f.class}]` : ''}${where}` +
            ` (fingerprint ${f.fingerprint}${count})`;
    });
    if (shown.length > lines.length) {
        lines.push(`... ${shown.length - lines.length} more in ${path.basename(reportPath)}`);
    }
    if (doc.suggestedPolicyFragment) {
        lines.push(`To accept an identity finding, waive its fingerprint in ` +
            `.har-policy.project.json -- the report carries a paste-ready fragment.`);
    }
    return lines;
}

/**
 * Is the catalogue at `p` still an untouched scaffold?
 *
 * Not a second rule: `describedRowCount` is the one implementation of "has the
 * AI pass happened", already used by the re-catalogue refusal.
 */
function isScaffoldOnly(p) {
    const rows = readJson(p);
    return Array.isArray(rows) && rows.length > 0 && describedRowCount(p) === 0;
}

/**
 * The promote step: the exact command that turns this capture into a committed
 * reference, and the catalogue row that reference will want.
 *
 * It PRINTS and stops. See describeReference for why nothing is copied, and
 * `#379` for the catalogue row this only suggests -- writing a structured
 * committed catalogue is that issue, not this one.
 */
function referenceNoticeLines(reference, pp, describe) {
    if (!reference) { return []; }
    const rows = readJson((pp && pp.catalogue && pp.catalogue.path) || null) || [];
    const described = rows.find((r) => r && r.Description) || rows[0] || null;
    const lines = [
        ['info', `  reference: ${reference.path}`],
        ['info', '             not written yet -- a reference is a TRIMMED extract of the entries'],
        ['info', '             that matter, not the whole scrubbed capture. To write it:'],
        ['info', `               node ${reference.extractor} \\`],
        ['info', `                 --in ${reference.source} \\`],
        ['info', `                 --out ${reference.path}`],
        ['info', '             That command is complete as written: the extractor keeps the API'],
        ['info', '             calls by default and reports, by category, what it dropped. Add'],
        ['info', "             --match to narrow WITHIN that set."]
    ];
    if (described) {
        lines.push(['info', '             Suggested catalogue row (printed only -- see #379):']);
        // The operator's own --describe stands in until the AI pass writes a
        // per-row description. An empty cell in a suggested row is a row
        // nobody can paste.
        lines.push(['info', `               | ${described.Action} ` +
            `| ${described.Description || describe || ''} | ${reference.relativePath} |`]);
    }
    return lines;
}

function postProcessLines(session) {
    const pp = session.postProcess || {};
    const lines = [['verbose', `  raw:       ${session.harPath}  (unscrubbed -- never commit it)`]];
    if (pp.scrubbed && pp.scrubbed.path) {
        lines.push(['info', `  scrubbed:  ${pp.scrubbed.path}` +
            `  (${pp.scrubbed.advisory ? 'kept -- advisory findings, see below' : 'verified'}` +
            `${pp.blunted ? `; BLUNTED ${pp.blunted}` : ''})`]);
    } else if (pp.scrubbed && pp.scrubbed.quarantined) {
        // Where it went, not that it is gone. The predecessor of this line
        // said "deleted", which left the operator with an exit code, a missing
        // file and nothing to triage.
        lines.push(['warn',
            `  scrubbed:  REJECTED by the leak gate -- quarantined, not destroyed:\n` +
            `             ${pp.scrubbed.quarantined}`]);
    } else if (pp.scrubbed && pp.scrubbed.rejected) {
        lines.push(['warn',
            '  scrubbed:  REJECTED by the leak gate; the raw capture is kept']);
    }
    if (pp.scrubbed && pp.scrubbed.findings) {
        lines.push(['warn', `  findings:  ${pp.scrubbed.findings}`]);
        for (const line of findingSummaryLines(pp.scrubbed.findings)) {
            lines.push(['warn', `             ${line}`]);
        }
    }
    if (pp.digest) lines.push(['verbose', `  digest:    ${pp.digest.path}`]);
    if (pp.catalogue) {
        lines.push(['info', `  catalogue: ${pp.catalogue.path}  (${pp.catalogue.delegatedTo})`]);
        if (pp.catalogue.skippedReason) lines.push(['warn', `             ${pp.catalogue.skippedReason}`]);
        // THE CATALOGUE ITSELF, not just its path (#377 s5b).
        //
        // A path is not a result. `CLAUDE.md` asks a run to show the actual
        // result "so the user sees the change worked without re-running it",
        // and this run's result is what was captured. The rendering existed
        // already, but only on the PowerShell front door -- and the node path
        // is the one agents use.
        //
        // Printed from the RECORDER on #300's precedent: this is the process
        // that wrote the files, so the print is in-process and unconditional. A
        // front door killed between spawning us and reaching its own epilogue
        // cannot take it away, and a notice that goes missing exactly when
        // something went wrong is not a safety net.
        for (const line of catalogueTableLines(readJson(pp.catalogue.path))) {
            lines.push(['info', `  ${line}`]);
        }
        // Keyed on Description and NOT on Status, for the reason
        // Invoke-HarCapture.ps1 already gives: a real AI pass may legitimately
        // conclude that every group was Observed and none Exercised, so Status
        // cannot tell a finished catalogue from an untouched scaffold.
        // Describing a row is the one thing the AI does that the scaffold never
        // can.
        if (isScaffoldOnly(pp.catalogue.path)) {
            lines.push(['info',
                '  Every row is still Observed -- the catalogue needs its AI pass. See ' +
                path.join(__dirname, CATALOGUE_PROMPT)]);
        }
    }
    for (const line of referenceNoticeLines(pp.reference, pp, session.describe)) { lines.push(line); }
    for (const err of pp.errors || []) lines.push(['error', `  ERROR:     ${err}`]);
    for (const warn of pp.warnings || []) {
        for (const line of `${warn}`.split('\n')) lines.push(['warn', `  WARNING:   ${line}`]);
    }

    // The other half of what makes warn-and-proceed safe rather than merely
    // deferred (#300). Having declined to discard anything, the run owes the
    // operator the exact paths it wrote and one command to move them. `warn`,
    // not `info`, so it survives -InformationAction SilentlyContinue -- the
    // whole failure mode being fixed is a mess nobody was told about.
    const relocate = repoGuard.relocationNotice(session.placement, [session.outputPath]);
    if (relocate) {
        for (const line of relocate.split('\n')) lines.push(['warn', `  ${line}`]);
    }
    return lines;
}

function describeSummary(summary) {
    if (!summary.exists) return 'no HAR was written';
    const kb = (summary.bytes / 1024).toFixed(1);
    if (summary.parseError) return `${kb} KB, unparseable (${summary.parseError})`;
    const hosts = summary.hosts.length <= 4
        ? summary.hosts.join(', ')
        : `${summary.hosts.slice(0, 4).join(', ')} +${summary.hosts.length - 4} more`;
    return `${summary.entries} entries, ${kb} KB, hosts: ${hosts}`;
}

// ---------------------------------------------------------------------------
// start
// ---------------------------------------------------------------------------

/**
 * Fail before the browser launches when the operator profile is missing.
 *
 * #255 forbids defaulting the literal map -- the values are the operator's own
 * account identifiers. Discovering that after a browsing session is spent
 * costs the whole session, so the check moved to preflight where it costs
 * seconds.
 */
function preflightProfile(isTty) {
    try {
        harProfile.loadProfile({});
        return { ok: true };
    } catch (e) {
        if (!(e instanceof harProfile.ProfileError)) throw e;
        if (isTty) {
            return { ok: false, scaffold: true, message: e.message };
        }
        return { ok: false, scaffold: false, message: e.message };
    }
}

function scaffoldProfile(targetDir) {
    const file = path.join(targetDir, harProfile.PROFILE_FILENAME);
    writeJson(file, { salt: require('crypto').randomBytes(24).toString('hex'), literals: {} });
    return file;
}

/**
 * Ask the operator one line.
 *
 * The question goes to STDERR, with everything else human-facing. stdout is
 * machine output -- and since #377 a completed `start` ends by writing one JSON
 * line there for its front door to read, so a prompt on stdout would be a
 * question mixed into a data stream that a caller is capturing rather than
 * showing.
 */
function gitInit(dir) {
    const r = spawnSync('git', ['init', '--quiet'], { cwd: dir, windowsHide: true, stdio: 'ignore' });
    return !r.error && r.status === 0;
}

async function askLine(question) {
    const rl = readline.createInterface({ input: process.stdin, output: process.stderr });
    try {
        return await new Promise((resolve) => rl.question(question, resolve));
    } finally {
        rl.close();
    }
}

/**
 * The recording's terminal, for the sign-in prompts (#115), over the ONE
 * readline the recording already reads ENTER from.
 *
 * One reader, because two on the same stdin each receive half the keystrokes.
 * The recording's line handler offers every line to `take` first: a pending
 * prompt consumes it, and only an unclaimed line is ENTER.
 *
 * A SECRET IS ONLY ASKED FOR WHERE IT CAN BE HIDDEN. readline in terminal mode
 * echoes through `_writeToOutput`, which is muted for the answer. Without
 * terminal mode the console echoes the keystrokes itself and nothing here can
 * stop it, so the question is not asked and the answer is "none" -- the
 * operator signs in in the browser instead.
 */
function createTerminal(rl, present = !!rl) {
    let pending = null;
    let muted = false;
    // `_writeToOutput` is readline's own, not public API. If a Node release
    // renames it there is nothing to mute -- and then no secret is asked for,
    // rather than one asked for and echoed.
    const canMute = !!rl && typeof rl._writeToOutput === 'function';
    if (canMute) {
        const write = rl._writeToOutput.bind(rl);
        rl._writeToOutput = (text) => { if (!muted) write(text); };
    }
    function question(text, secret) {
        if (!rl || (secret && !(rl.terminal && canMute))) return Promise.resolve(null);
        return new Promise((resolve) => {
            pending = (line) => {
                if (secret) process.stderr.write('\n');
                muted = false;
                resolve(line);
            };
            process.stderr.write(text);
            muted = secret;
        });
    }
    return {
        // PRESENCE and PROMPTING are separate answers. `--no-wait` at a real
        // terminal may have a person there, so it is not "unattended" (no
        // exit 8) -- but with no reader it is not prompted, and so never
        // reads the vault (sign-in.js resolveCredentials).
        isTty: !!present,
        canPrompt: !!rl,
        say: (lines) => lines.forEach((l) => process.stderr.write(`${l}\n`)),
        ask: (text) => question(text, false),
        askSecret: (text) => question(text, true),
        take(line) {
            if (!pending) return false;
            const p = pending;
            pending = null;
            p(line);
            return true;
        }
    };
}

/** An unattended run met a sign-in form (#115). */
function signInNeededLines(host) {
    return [
        ['warn', `capture-har: ${host} needs a sign-in, and nobody is at this terminal to start one.`],
        ['warn', '  Ending now rather than recording a login page. Sign in once from a terminal'],
        ['warn', '  (Bitwarden or the prompt fills it); the saved session lets unattended runs through.']
    ];
}

async function start(args) {
    if (!args.uri) return usage('start requires --uri');
    for (const key of Object.keys(args)) {
        if (key === '_') continue;
        if (!START_OPTIONS.includes(key)) {
            return usage(`start does not accept --${key}` +
                (key === 'dir'
                    ? '. Raw captures are confined to .har-captures/ by design;\n' +
                      '  --output-path receives the scrubbed artifacts instead.'
                    : key === 'storage-state'
                        ? `. A ${STORAGE_STATE_FILENAME} at or above the working directory is\n` +
                          '  discovered automatically.'
                        : ''));
        }
    }

    // #366 -- A CAPTURE NOBODY CAN IDENTIFY IS A CAPTURE NOBODY CAN USE.
    //
    // Checked here, before anything is recorded, for the same reason the
    // placement guard is: refusing a recording costs seconds now and a whole
    // browsing session later. It is deliberately AFTER the option check above,
    // so `--dir` still gets told why it was dropped rather than being answered
    // with a complaint about a different flag.
    //
    // Not a warning. `describe` was already visibly null in session.json and
    // nobody noticed across 82 captures -- a warning is what this effectively
    // was. Whitespace is empty: `--describe "   "` identifies nothing.
    if (typeof args.describe !== 'string' || !args.describe.trim()) {
        process.stderr.write(
            'capture-har: refusing to record without --describe.\n' +
            '  A capture nobody can identify is a capture nobody can use: the store is\n' +
            '  shared and append-only, the directory name is a START time, and several\n' +
            '  sessions record into it at once. The description is the only part of a\n' +
            '  capture that cannot be reconstructed afterwards -- the bytes can be\n' +
            '  re-captured, what you were doing cannot.\n' +
            '  Try: --describe "example.com: create a post with two photos, then delete it"\n');
        return 2;
    }

    // WHICH BROWSER, AND WHOSE (#73). Resolved here, above every gate, because
    // the answer changes what the next one has to insist on: a native or
    // attached run needs the playwright module and not its Chromium build. A
    // usage error also has to arrive before a prompt or an install offer --
    // being asked to install a browser and then told the flags were wrong is
    // the shape of failure #512 exists to prevent.
    const browserMode = resolveBrowserMode(args);
    // WHICH AGENT'S TOOLS (#112)? With no agent there is nothing to limit, and an
    // operator who typed it believes something is. Refused, not ignored -- and so
    // is a value that names no mode, which must not quietly fall back to auto.
    //
    // PRESENCE, not the value: a trailing `--ai-tools` with nothing after it is
    // parsed as undefined, which is indistinguishable from "not typed" by value,
    // and would have run in auto for an operator who meant browser-only.
    if (Object.hasOwn(args, 'ai-tools')) {
        if (args['ai-tools'] === undefined) {
            return usage(`--ai-tools needs a value: ${AGENT_TOOLS_MODES.join(' or ')}`);
        }
        if (!args.ai) {
            return usage('--ai-tools needs --ai: it chooses the driving agent\'s tools, ' +
                'and\n  without --ai no agent is driving');
        }
        if (!AGENT_TOOLS_MODES.includes(args['ai-tools'])) {
            return usage(`--ai-tools takes ${AGENT_TOOLS_MODES.join(' or ')}, not '${args['ai-tools']}'`);
        }
    }
    if (browserMode.error) return usage(browserMode.error);
    // HOW FAST (pacing.js). Presence, not the value, for the same reason as
    // --ai-tools: a trailing `--pace` is undefined, which must not quietly mean
    // the default. A value it cannot read is refused rather than guessed -- a
    // typo must never become "no pacing".
    if (Object.hasOwn(args, 'pace') && args.pace === undefined) {
        return usage('--pace needs a value: off, human, or nav=MIN-MAX,action=MIN-MAX,per-minute=N');
    }
    const paced = pacing.parsePace(args.pace);
    if (paced.error) return usage(paced.error);
    for (const note of paced.notes || []) log.warn('capture-har: ' + note);
    const pace = paced.pace;
    const attached = !!browserMode.attached;
    const nativeBrowser = browserMode.browser === BROWSER_NATIVE;
    // WHO DRIVES (#98). Resolved here, beside WHICH BROWSER, because the two
    // answers are one decision: an agent drives over the same CDP endpoint the
    // native mode exists to provide.
    const ai = !!args.ai;
    // recordHar is Playwright's, and Playwright launched neither of the browsers
    // above. Carried as a fact on the session rather than re-derived from the
    // mode string, because `stop` runs in another process and has to reach the
    // same answer -- and an older session.json, which has no such field, must
    // keep the behaviour it was recorded under.
    const ownsRecordHar = !nativeBrowser;

    const isTty = !!process.stdin.isTTY;

    // CAN THIS RUN FINISH AT ALL -- asked before anything else (#512).
    //
    // Above even the placement guard, because this is the only gate whose
    // answer makes every later one pointless. The owner's report is the shape
    // being prevented: a profile scaffolded, a capture root announced, and
    // then a missing module -- everything before the failure being work to
    // undo, and the printed remedy being one that could not work.
    //
    // NOT under --validate-only. That flag resolves paths and opens nothing,
    // and several suites use it to ask the recorder questions on machines
    // where no browser is installed; gating it on a browser would break the
    // one command that exists to have no side effects.
    // AND CAN ANYTHING DRIVE IT (#98) -- asked above the dependency preflight for
    // the same reason the preflight itself sits above everything else: this is
    // the other gate whose answer makes the rest pointless. An operator left with
    // a Chrome open, a profile warmed up and no driver has to clean up after a run
    // that could never have worked -- and on the protected sites this mode exists
    // for, that browser was the expensive part.
    //
    // NOT under --validate-only, exactly as the preflight is not: that flag
    // resolves paths and starts nothing, and suites use it to ask the recorder
    // questions on machines where neither a browser nor a CLI is installed.
    if (ai && !args['validate-only']) {
        const agent = resolveAgent({ ai, claudeOnPath: claudeOnPath() });
        if (agent.error) {
            process.stderr.write(agent.error + '\n');
            return 1;
        }
    }

    if (!args['validate-only']) {
        const deps = await preflightDependencies({ isTty, requireBrowser: ownsRecordHar });
        if (!deps.ok) {
            process.stderr.write(deps.message + '\n');
            return 1;
        }
    }

    // WHERE THE OUTPUT WILL LAND, checked here and nowhere later (#300, #471).
    //
    // This sits at the very top of `start`, ahead of the profile preflight, the
    // port scan and the browser, because the guard is only safe while nothing
    // has been recorded yet. Warn a second in and cancelling costs the operator
    // nothing; warn after a capture and the choice becomes "discard the
    // recording you just spent minutes producing", which is a worse outcome
    // than the misplacement it would prevent. So: advisory, never fatal, and
    // never moved downstream.
    //
    // The paths are resolved HERE, above the preflight they used to sit below,
    // purely so the guard can consult them without moving downstream of the
    // recording. resolveSessionPaths computes strings and writes nothing, so
    // hoisting it changes no behaviour.
    //
    // THIS PROCESS IS THE ONLY OWNER of the decision (#471). The PowerShell
    // front door used to run this same check first and mark the environment so
    // the operator was told once, not twice; it no longer checks at all. The
    // question is now "will this run write committable output into a work
    // tree", and only the process that resolved the destination can answer it.
    // A front door that guessed would be a second answer free to disagree.
    const paths = resolveSessionPaths({ uri: args.uri, outputPath: args['output-path'] });
    const placement = placementForOutput(
        path.join(paths.outputPath, SCRUBBED_HAR), paths.capturesRoot);
    if (placement) {
        process.stderr.write('capture-har: ' +
            repoGuard.guardMessage(placement).split('\n').join('\n' + '  ') + '\n');
    }

    // Preflight FIRST -- before the port scan and long before a browser.
    const preflight = preflightProfile(isTty);
    if (!preflight.ok && !args['validate-only']) {
        if (preflight.scaffold) {
            const answer = await askLine(
                `capture-har: no ${harProfile.PROFILE_FILENAME} found.\n` +
                `  ${preflight.message}\n` +
                '  Scaffold one here now? [y/N] ');
            if (!/^y/i.test((answer || '').trim())) {
                process.stderr.write('capture-har: cannot record without an operator profile.\n');
                return 1;
            }
            const created = scaffoldProfile(process.cwd());
            log.info(
                `capture-har: wrote ${created}. Add your own identifiers to "literals"\n` +
                '  before the scrub can redact them.');
        } else {
            process.stderr.write(`capture-har: ${preflight.message}\n`);
            return 1;
        }
    }

    // WHERE THE BYTES ARE GOING, said on the way IN (#367).
    //
    // session.json recorded the destroyed capture's path perfectly; nobody read
    // it until the directory was gone. A path announced while the recording is
    // starting is a path the operator can still question, so this is printed at
    // info level, before the browser, and not gated on -Verbose.
    const rootNotice = repoGuard.captureRootNotice(paths.capturePlacement);
    if (rootNotice) log.info('capture-har: ' + rootNotice);
    // AN ATTACHED BROWSER'S PORT IS READ, NEVER CHOSEN. Falling forward to a
    // free port is right for a browser we are about to start and is exactly
    // wrong for one already listening: the next free port is the one nothing is
    // on. So the scan is skipped and the endpoint's own port stands.
    const requestedPort = attached ? browserMode.port : numberOr(args.port, DEFAULT_PORT);
    const port = attached || (args['validate-only'] && requestedPort === 0)
        ? requestedPort
        : await findFreePort(requestedPort);
    const snapshotSeconds = numberOr(args['snapshot-seconds'], DEFAULT_SNAPSHOT_SECONDS);
    const snapshotMs = snapshotSeconds * 1000;
    const isolated = !!args.isolated;
    // Null when attached, and null is the honest value: the browser is running
    // on a profile this run did not choose and cannot name. Guessing one would
    // put a path in session.json that nothing verified.
    const profileDir = attached
        ? null
        : isolated
            ? fs.mkdtempSync(path.join(os.tmpdir(), 'har-capture-'))
            : resolveProfileDir(args.profile);
    const externalProfile = !!profileDir && !isolated && !profileDir.startsWith(captureProfileRoot());
    const storageState = isolated ? discoverStorageState(process.cwd()) : null;

    const session = {
        uri: args.uri,
        describe: args.describe.trim(),
        sessionDir: paths.sessionDir,
        harPath: paths.harPath,
        recordLog: paths.recordLog,
        outputPath: paths.outputPath,
        // Carried on the session so the CLOSING notice can name what was
        // written even though the detection happened before the browser opened.
        // Null unless the guard fired, so nothing downstream has to re-probe.
        //
        // THE RECORDER OWNS BOTH NOTICES since #471, having previously owned
        // only the closing one -- the front door printed the opening warning
        // and marked HARCAPTURE_PLACEMENT_GUARD_RAN so this process stayed
        // quiet. That split could not survive the guard needing to know the
        // resolved destination, which only this process computes.
        //
        // What the split was protecting still holds, and is why the closing
        // notice is printed here rather than delegated back: it is emitted
        // after work has happened, by the process that actually wrote the
        // files, in-process and unconditionally. A front door killed between
        // spawning us and reaching its own epilogue -- a closed terminal, a
        // hard Ctrl+C, an agent that dies mid-session -- cannot take the notice
        // with it. A notice that only arrives when nothing went wrong is not a
        // safety net.
        placement,
        profileDir,
        externalProfile,
        storageState,
        mode: attached
            ? 'attached-chrome'
            : nativeBrowser
                ? 'native-chrome'
                : isolated ? 'isolated-chromium' : 'chrome-profile',
        // WHICH BROWSER, AND WHOSE -- both recorded, because `status` and any
        // agent reading this file need to know not to close it (#73).
        browser: browserMode.browser,
        attached,
        ownsRecordHar,
        // 127.0.0.1 rather than `localhost` for a browser we start, and the
        // difference is not cosmetic: Chrome binds its debugging port to the
        // IPv4 loopback, while Node's resolver may answer `localhost` with ::1
        // first -- so the literal is the one spelling that cannot fail to
        // connect to the browser this process just launched. The Playwright
        // mode keeps `localhost`, which is what it has always printed and what
        // agents have been told to expect.
        cdpEndpoint: attached
            ? browserMode.endpoint
            : nativeBrowser ? `http://127.0.0.1:${port}` : `http://localhost:${port}`,
        port,
        requestedPort,
        snapshotSeconds: snapshotMs / 1000,
        // The pace this run's own steps were taken at, and an agent was told
        // to keep -- on every session, because every run signs in at it.
        pace
    };

    // ONLY WHEN ONE WAS ASKED FOR (#98). A hand-driven capture's session.json
    // says nothing about an agent rather than saying `ai: false, agent: null`:
    // the fields are the record of a driver, and inventing empty ones for every
    // capture ever recorded would make "was this driven by an agent?" a question
    // about a value's truthiness rather than about a key's presence.
    if (ai) {
        session.ai = true;
        session.agent = AGENT_CLAUDE;
        session.agentPromptPath = path.join(paths.sessionDir, AGENT_PROMPT_FILE);
        session.agentLogPath = path.join(paths.sessionDir, AGENT_LOG_FILE);
        // Only when browser-only, like `ai` itself: auto is the default (#112).
        if (args['ai-tools'] === AGENT_TOOLS_BROWSER_ONLY) session.agentTools = AGENT_TOOLS_BROWSER_ONLY;
        // IS THE PREFERRED DRIVER THERE (#117) -- asked before the browser
        // launches, and before --validate-only returns because it only reads.
        // A warning, never a refusal: Playwright MCP is the fallback.
        const extension = chromeExtensionNotice(session);
        if (extension) {
            // CARRIED, so the drive and the prompt act on the same answer the
            // warning gave (review I1): absent means no --chrome and a prompt
            // that says Playwright MCP. Unknown (attached) is left unset.
            session.chromeExtension = extension.installed;
            log[extension.level](extension.message);
        }
    }

    if (args['validate-only']) {
        // stdout stays pure JSON so a caller can parse it; the human note goes
        // to stderr rather than corrupting that.
        process.stdout.write(JSON.stringify(session, null, 2) + '\n');
        log.info('capture-har: --validate-only OK (no browser launched)');
        return 0;
    }

    // THE CAPTURE STORE MUST BE GITIGNORED BEFORE ANYTHING IS RECORDED (#377).
    //
    // Placed here for the reason repo-workflow-guard.js states as its own
    // invariant: a guard that fires before any work costs a cancelled operator
    // seconds, while the same guard downstream would be deciding whether to
    // discard a browsing session somebody spent minutes on. It sits after the
    // --validate-only return so that flag stays what it says it is -- path
    // resolution, no side effects, no prompt.
    const ignoreGuard = await ensureCapturesRootIgnored(paths.capturePlacement, { isTty });
    if (!ignoreGuard.ok) {
        process.stderr.write(ignoreGuard.message + '\n');
        return 1;
    }
    // Said out loud when it happened. A file this tool wrote into a folder the
    // operator owns is theirs to know about -- and the line is also the answer
    // to "do I need a repository for this?", delivered where the question
    // actually comes up.
    if (ignoreGuard.initialized) {
        log.info(`capture-har: initialized a git repository in ${ignoreGuard.initialized}; ` +
            `${ignoreGuard.ignoreFile} keeps the capture store and your operator profile out of it.`);
    } else if (ignoreGuard.added && ignoreGuard.added.length > 0) {
        log.info('capture-har: this folder is not a git repository. Wrote ' +
            `${ignoreGuard.ignoreFile}\n` +
            '  so a later `git init` cannot commit the capture store or your ' +
            'operator profile.');
    }
    if (ignoreGuard.unprotected) {
        session.unprotectedTables = true;
        log.warn('capture-har: proceeding without a git repository. The scrub\'s substitution ' +
            'tables -- reverse lookups of the values it replaced -- will be written into this ' +
            "capture's session folder, and nothing stops them being committed if this folder " +
            'becomes a repository without the .gitignore written here.');
    }

    // And the other half: an EXPLICIT --output-path into a committable
    // directory is warned about, never refused. See outputDestinationWarning.
    const outputWarning = outputDestinationWarning(paths);
    if (outputWarning) { log.warn('capture-har: ' + outputWarning); }

    // AND THE BROWSER PROFILE, when this run owns one (#73). A profile holds
    // live session cookies for the account being recorded, so a project-local
    // one -- `--profile .\.chrome-profile`, the shape a project that keys
    // browsers off its own concept hands us -- is as committable as a raw
    // capture and gets the same treatment the store gets: an ignore rule,
    // written. Unlike the store it is never refused and never prompted for; see
    // ensureProfileIgnored for why those two differ.
    //
    // Not gated on the browser mode, because the hazard is not either mode's:
    // `--profile` has always accepted a path, and a path inside a work tree has
    // always been as exposed there. The default profile lives outside any
    // repository, so this is a no-op for it.
    if (profileDir) {
        const synced = syncedFolderWarning(profileDir);
        if (synced) log.warn('capture-har: ' + synced);
        const profileIgnore = ensureProfileIgnored(profileDir);
        if (profileIgnore.added && profileIgnore.added.length > 0) {
            log.info(`capture-har: added ${profileIgnore.entry} to ${profileIgnore.ignoreFile} -- ` +
                'a browser profile holds live session cookies.');
        }
        if (profileIgnore.ineffective) {
            log.warn('capture-har: the browser profile is NOT gitignored and the rule written ' +
                `for it did not take effect (${profileIgnore.status}):\n  ${profileDir}\n` +
                '  It holds live session cookies for the account you record as. Add a rule ' +
                'that\n  does, or record to a profile outside this repository.');
        }
    }

    // A busy port is no longer a conflict -- we moved. A persistent profile
    // genuinely is single-instance, though, and that stays a hard error naming
    // what holds it.
    //
    // ASKED ONLY WHEN THIS RUN OWNS A PROFILE. An attached session records no
    // profile path, so two of them would compare equal (null against null) and
    // the second would be refused as a conflict with the first -- while in fact
    // they are two different browsers, each one already running, neither of them
    // single-instance with respect to the other.
    const conflict = profileDir ? await findProfileConflict(profileDir, paths.searchRoots) : null;
    if (conflict) {
        process.stderr.write(
            `capture-har: the capture profile is already recording.\n` +
            `  profile: ${profileDir}\n` +
            `  session: ${conflict.sessionDir}\n` +
            `  cdp:     ${conflict.cdpEndpoint}\n` +
            '  End it first (node capture-har.js stop), or record a throwaway\n' +
            '  identity alongside it with --isolated.\n');
        return 1;
    }

    fs.mkdirSync(paths.sessionDir, { recursive: true });
    const playwright = requirePlaywright();

    let context;
    // The CDP side, when there is one. Null in the Playwright mode, and the
    // teardown reads THIS rather than the mode -- so "is this browser mine to
    // close?" has one answer, held by the thing that would know.
    let cdpBrowser = null;

    if (nativeBrowser) {
        if (!attached) {
            const chrome = findChromeExecutable();
            if (!chrome.path) {
                process.stderr.write(
                    'capture-har: --browser native needs Google Chrome, and it is not at any of\n' +
                    '  the places it installs:\n' +
                    chrome.candidates.map((c) => `    ${c}\n`).join('') +
                    '  Install Chrome, or start one yourself and record in it:\n' +
                    '    chrome --user-data-dir=<a dedicated profile> --remote-debugging-port=9222\n' +
                    '    node capture-har.js start --attach http://127.0.0.1:9222 ...\n');
                return 1;
            }
            // Before the launch, because Chrome creates it silently and a
            // --user-data-dir it cannot write to fails with nothing to read.
            fs.mkdirSync(profileDir, { recursive: true });
            log.verbose(`capture-har: launching ${chrome.path}`);
            let launched;
            try {
                launched = launchNativeChrome({
                    executable: chrome.path, profileDir, port
                });
            } catch (e) {
                process.stderr.write(
                    `capture-har: could not start ${chrome.path}: ${e.message}\n`);
                return 1;
            }
            // RECORDED, NEVER SIGNALLED. Nothing in this file kills a process,
            // and a browser holding the operator's signed-in tabs is the last
            // one that would start. The pid is here so the claim this mode makes
            // -- "the browser is still running" -- is one a reader of
            // session.json, or a test, can actually check.
            session.browserPid = launched.child.pid || null;
            // A spawn that fails asynchronously arrives as an `error` event, and
            // an unhandled one on a child process is thrown. Both handlers exist
            // to answer `stillStarting` below -- and to keep a browser that
            // refused to start from taking this process down with it.
            let gone = false;
            launched.child.once('exit', () => { gone = true; });
            launched.child.once('error', () => { gone = true; });
            const version = await waitForCdp(session.cdpEndpoint, NATIVE_CDP_TIMEOUT_MS,
                { stillStarting: () => !gone });
            if (!version) {
                process.stderr.write(
                    'capture-har: Chrome started but never opened its debugging port at\n' +
                    `  ${session.cdpEndpoint}\n` +
                    (gone
                        ? '  The process exited immediately, which is what Chrome does when the\n' +
                          '  profile is ALREADY OPEN in another window: the new process hands the\n' +
                          '  request to that one and leaves, applying none of our flags.\n' +
                          `    profile: ${profileDir}\n` +
                          // THE LIKELIEST CAUSE IS THIS TOOL'S OWN CONTRACT, so it
                          // is named first. A native recording DETACHES and leaves
                          // its browser running, by design -- so the second capture
                          // of a session arrives to find the first one's window
                          // still holding the profile. An operator who is not told
                          // that reads this as a broken feature, and the remedy they
                          // reach for (close it) is the one that throws away the
                          // signed-in state the mode exists to keep.
                          '  If that window is the one a previous recording left running -- which\n' +
                          '  this mode does deliberately -- record in it rather than starting a\n' +
                          '  second browser. Its endpoint is the `cdpEndpoint` in that capture\'s\n' +
                          '  session.json:\n' +
                          '    node capture-har.js status\n' +
                          '    node capture-har.js start --attach <that endpoint> ...\n' +
                          '  Otherwise close the window, or record on a different --profile.\n'
                        : `  It is still running, and did not answer within ` +
                          `${NATIVE_CDP_TIMEOUT_MS / 1000}s. Nothing was\n` +
                          '  recorded; the browser was left alone.\n'));
                return 1;
            }
            log.verbose(`capture-har: attached to ${version.Browser || 'the browser'}`);
        } else {
            // A browser that is not there is worth its own message: attaching
            // would otherwise fail inside Playwright, naming a websocket the
            // operator never typed.
            const version = await probeCdpEndpoint(session.cdpEndpoint);
            if (!version) {
                process.stderr.write(
                    `capture-har: nothing is answering as a browser at ${session.cdpEndpoint}\n` +
                    '  Start Chrome with a debugging port and a DEDICATED profile -- it refuses\n' +
                    '  remote debugging on a profile another Chrome already has open:\n' +
                    '    chrome --user-data-dir=<a dedicated profile> --remote-debugging-port=' +
                    `${port}\n`);
                return 1;
            }
            log.verbose(`capture-har: attaching to ${version.Browser || 'the browser'}`);
        }

        try {
            cdpBrowser = await playwright.chromium.connectOverCDP(session.cdpEndpoint);
        } catch (e) {
            process.stderr.write(
                `capture-har: could not attach to ${session.cdpEndpoint}: ${e.message}\n`);
            return 1;
        }
        // THE DEFAULT CONTEXT IS THE RECORDING, and there is no second one to
        // fall back to. `newContext()` over CDP does work -- measured against
        // Chrome 154, it yields a second context -- but that context is
        // incognito: a different cookie jar, with none of the profile's
        // sign-ins, which is the opposite of what a signed-in capture needs.
        //
        // And the default context is the OPERATOR'S, holding every other tab
        // they have open. So in this branch the recorder is given a scope
        // (createCaptureScope) and records only this capture's pages (#85).
        context = cdpBrowser.contexts()[0];
        if (!context) {
            process.stderr.write(
                `capture-har: attached to ${session.cdpEndpoint}, but it has no browser context\n` +
                '  to record. Open a window in that browser and try again.\n');
            await cdpBrowser.close().catch(() => {});
            return 1;
        }
    } else {
        // THE ONLY BRANCH THAT CAN ASK FOR recordHar, which is why these options
        // are built here rather than above the split: recordHar is a
        // launch-time option on a context Playwright creates, and a browser
        // Playwright did not launch has no way to accept one.
        const options = {
            headless: false,
            timeout: LAUNCH_TIMEOUT_MS,
            recordHar: { path: paths.harPath, mode: 'full', content: 'embed' },
            args: [`--remote-debugging-port=${port}`]
        };
        // Bundled Chromium can silently close against a Chrome-created profile,
        // so the default path pins the system Chrome channel.
        if (!isolated) options.channel = 'chrome';
        if (storageState) options.storageState = storageState;

        try {
            context = await playwright.chromium.launchPersistentContext(profileDir, options);
        } catch (e) {
            // The profile can also be held by a browser that no session file
            // knows about. Launch then stalls rather than erroring, so a timeout
            // here means "in use" far more often than it means "broken".
            if (/Timeout/i.test(e.message)) {
                process.stderr.write(
                    `capture-har: the browser did not start within ${LAUNCH_TIMEOUT_MS / 1000}s.\n` +
                    `  The capture profile is almost certainly open in another window:\n` +
                    `    ${profileDir}\n` +
                    '  Close that window (do not kill the process -- other windows may share it),\n' +
                    '  or record in a throwaway profile with --isolated.\n');
                return 1;
            }
            throw e;
        }
    }

    const recorder = new IncrementalRecorder(paths.recordLog, snapshotMs);
    // Only where the browser is not this run's own: see createCaptureScope.
    const scope = cdpBrowser ? createCaptureScope(args.uri) : null;

    // Armed BEFORE the first navigation. A window closed while `goto` is still
    // in flight emits `close` on an emitter with no listeners, and Node drops
    // it -- after which no ending can ever fire and the driver waits forever,
    // holding the port and reporting `recording: true`.
    let reason = null;
    const settle = (r) => { if (!reason) reason = r; };
    // Set once the terminal is known (below): whether a hand-over has a human
    // to hand to, or has to end the recording instead.
    let humanAtTerminal = false;

    // PACING AND THROTTLING (pacing.js; "## Pacing" at the top). The pacer
    // times what THIS process does to a page; the monitor reads every recorded
    // response from the site; the rate watch only WATCHES an agent, whose
    // actions are its own.
    const pacer = pacing.createPacer(pace);
    const throttleMonitor = pacing.createThrottleMonitor();
    const agentRate = ai ? pacing.createRateWatch(pace.perMinute) : null;
    const agentControl = {};
    // Whether the agent child has been launched. Under --ai the start page
    // loads BEFORE it is, and a refusal there must stop it from ever starting
    // (agentStartRefusal) rather than "stop" an agent that does not exist yet.
    let agentLaunched = false;
    const site = captureSite(args.uri);
    const writeSession = () => writeJson(path.join(paths.sessionDir, SESSION_FILE), session);
    const nobodyToTakeOver = () => {
        log.warn('capture-har: nobody is at this terminal to take over, so the recording ' +
            'ends here and what it holds is post-processed.');
        settle('handed-over');
    };
    const onThrottle = (decision) => {
        if (!decision || decision.action === 'none' || reason) return;
        const s = decision.signal;
        const record = {
            at: new Date().toISOString(), kind: s.kind, status: s.status, url: pacing.whereOf(s.url),
            signature: s.signature, retryAfterMs: s.retryAfterMs, action: decision.action
        };
        if (decision.action === 'pause') record.waitMs = decision.waitMs;
        if (decision.why) record.why = decision.why;
        (session.throttle = session.throttle || []).push(record);
        // Who is driving decides what a signal MEANS. An agent running is
        // stopped on a hand-over; one not launched yet never will be; anything
        // else is a person's browser, and gets advice.
        const driving = agentDriving({ ai, agentLaunched, session });
        const agentPending = ai && !agentLaunched;
        if (decision.action === 'pause') {
            pacer.pauseFor(decision.waitMs);
            log.lines(pacing.throttleLines(decision, driving ? 'agent' : agentPending ? 'script' : 'you'));
        } else if (driving) {
            session.handOver = Object.assign({}, record, { by: 'agent-stopped' });
            session.agentStopped = 'hand-over';
            if (agentControl.stop) agentControl.stop('hand-over');
            log.lines(pacing.throttleLines(decision, 'agent'));
            if (!humanAtTerminal) nobodyToTakeOver();
        } else if (agentPending) {
            session.handOver = Object.assign({}, record, { by: 'agent-not-started' });
            log.lines(pacing.throttleLines(decision, 'agent-unstarted'));
            if (!humanAtTerminal) nobodyToTakeOver();
        } else {
            session.handOver = Object.assign({}, record, { by: 'advice' });
            log.lines(pacing.throttleLines(decision, 'you'));
        }
        writeSession();
    };
    attachRecorder(context, recorder, scope, {
        onResponse: (info) => {
            if (agentRate && info.isDocument && agentDriving({ ai, agentLaunched, session }) && agentRate.note()) {
                log.warn(`capture-har: the agent is navigating faster than the pace allows (more than ` +
                    `${pace.perMinute} pages in a minute).\n  It is told to keep to the pace; the ` +
                    'recorder can watch it, not slow it down.');
            }
            onThrottle(throttleMonitor.observe(pacing.classifyResponse(
                Object.assign({}, info, { onSite: onCaptureSite(site, info.url) }))));
        }
    });
    recorder.start();
    context.on('close', () => settle(nativeBrowser ? 'browser-closed' : 'window-closed'));
    // THE DISCONNECT IS AN ENDING (#73). Over CDP, a browser the operator quits
    // reaches us as a dropped connection rather than as a context close, and a
    // recording with no ending waits forever holding the session open.
    //
    // Our OWN detach fires this too, at the end of a normal run -- harmlessly,
    // because `settle` keeps the first reason and by then there is one. That is
    // the same first-one-wins rule the other endings rely on, not an exception
    // made for this event.
    if (cdpBrowser) cdpBrowser.on('disconnected', () => settle('browser-closed'));

    // THE TERMINAL IS SHARED (#115): ENTER ends the recording, and the sign-in
    // prompts read from the same stdin. Created BEFORE the first navigation,
    // because the start page is the most likely place to meet a sign-in form.
    let rl = null;
    let enterPressed = null;
    if (!args['no-wait'] && isTty) {
        // STDERR, with every other human-facing line: a username echoed to
        // stdout would land in the stream the front door captures.
        rl = readline.createInterface({ input: process.stdin, output: process.stderr });
        // In terminal mode readline takes Ctrl+C itself and raises it on `rl`,
        // not on the process -- which would silently disarm the Ctrl+C ending
        // below. Passed on, so Ctrl+C means what it always meant.
        // Before the Ctrl+C ending is armed (a prompt on the start page) there
        // is no process listener to hand it to, so it settles directly.
        rl.on('SIGINT', () => {
            if (process.listenerCount('SIGINT')) process.emit('SIGINT');
            else settle('ctrl-c');
        });
    }
    const terminal = createTerminal(rl, isTty);
    humanAtTerminal = !!rl;
    if (rl) {
        enterPressed = new Promise((resolve) => rl.on('line', (line) => {
            if (terminal.take(line)) return;
            settle('enter');
            resolve();
        }));
    }
    const signInWatcher = signIn.createSignInWatcher(context, {
        uri: args.uri,
        inScope: scope ? (p) => scope.owns(p) : null,
        // One step at a time, at the action pace -- and after a throttle,
        // not before the site's Retry-After has passed.
        pace: pacer,
        resolve: (host) => signIn.resolveCredentials(host, signIn.createBitwarden(), terminal),
        say: (lines) => log.lines(lines.map((l) => ['info', l])),
        onUnattended: (host) => {
            log.lines(signInNeededLines(host));
            settle('sign-in-needed');
        },
        onSignedIn: async ({ cookiesBefore } = {}) => {
            // On the session AS IT HAPPENS, not only at the end: a `stop` that
            // post-processes after this driver died must still be able to see
            // that a sign-in was filled (see postProcess).
            session.signIn = signInWatcher.result();
            writeJson(path.join(paths.sessionDir, SESSION_FILE), session);
            // Not from a browser this run borrowed: its cookies are the
            // operator's whole browsing session, not this capture's.
            if (attached) return;
            // Read back by a later `--isolated` run (discoverStorageState);
            // the default profile keeps its own sign-in regardless.
            const file = storageState || path.join(process.cwd(), STORAGE_STATE_FILENAME);
            const saved = await signIn.saveSession(context, file, signInWatcher.hosts(),
                subsDestination.classifyDestination, { cookiesBefore });
            const why = {
                unchanged: 'the saved session was already current',
                'no-new-session': 'the site set no new cookie, so there is no session to save -- ' +
                    'the existing saved session is left as it was'
            }[saved.status] || `${file} would not be gitignored`;
            log.info(saved.saved
                ? `capture-har: signed in -- session saved to ${file}`
                : `capture-har: signed in -- session NOT saved: ${why}`);
        }
    });

    session.pid = process.pid;
    session.startedUtc = new Date().toISOString();
    writeJson(path.join(paths.sessionDir, SESSION_FILE), session);

    // A NEW TAB IN SOMEBODY ELSE'S BROWSER, and the existing one everywhere
    // else. Attached, `pages()[0]` is whatever the operator happens to have
    // open -- navigating it away would discard work this recording has no claim
    // on. A browser this run launched has exactly one blank tab, which is there
    // to be used.
    const page = attached
        ? await context.newPage()
        : context.pages()[0] || await context.newPage();
    // Rule 1 of the scope, claimed BEFORE the first navigation so the target's
    // own document is recorded.
    if (scope) scope.claim(page);
    // Through the pacer like every navigation this process makes. It is the
    // first, so it does not wait -- the pacer exists for the ones after it.
    await pacer.navigate(() => page.goto(args.uri, { waitUntil: 'domcontentloaded' })).catch((e) => {
        log.warn(`capture-har: initial navigation failed (${e.message}); recording anyway`);
    });
    // Not awaited: a prompt here must not hold back the banner or the endings.
    // A `load` the watcher missed during `goto` is covered by looking now.
    signInWatcher.check(page);

    log.lines(startBannerLines(session));

    // Whichever ending arrives first wins. (The window-closed ending is
    // already armed, above.)
    const sentinel = path.join(paths.sessionDir, STOP_SENTINEL);
    const endings = [];
    endings.push((async () => {
        while (!reason && !fs.existsSync(sentinel)) await sleep(POLL_MS);
        settle('stop-command');
    })());

    // The unattended sign-in ending (#115) needs no entry of its own: the
    // watcher settles it, and the stop-sentinel poll above ends on any reason.
    if (enterPressed) endings.push(enterPressed);
    endings.push(new Promise((resolve) => {
        process.once('SIGINT', () => { settle('ctrl-c'); resolve(); });
    }));

    // THE AGENT IS AN ENDING, and it is only ONE of them (#98).
    //
    // Pushed into the same race as the stop sentinel, ENTER and SIGINT, which is
    // what keeps a drive from being a loss of control: `Stop-HarRecording` still
    // ends it from another process, ENTER still ends it from here, and closing
    // the window still ends it. Whichever arrives first wins, as it always did.
    //
    // Launched AFTER the banner and after session.json exists, because the prompt
    // hands the agent that file's path and an agent that read it a moment early
    // would find no `startedUtc` and no endpoint.
    //
    // NOT AT ALL when the site already refused (agentStartRefusal). And not
    // before a throttle on the start page has run out: the agent cannot be
    // slowed once it is going, so its first step is the one the recorder can
    // still hold back. ENTER, the stop command and a closed window all still
    // end the wait.
    if (ai) {
        const prompt = buildAgentPrompt(session);
        fs.writeFileSync(session.agentPromptPath, prompt, 'utf8');
        if (!reason && !session.handOver && pacer.pausedUntil() > Date.now()) {
            log.info('capture-har: the site asked for a pause on the start page; the agent starts ' +
                'when it is over.');
            while (!reason && !session.handOver && Date.now() < pacer.pausedUntil()) await sleep(POLL_MS);
        }
    }
    const refusal = ai ? agentStartRefusal(session, reason) : null;
    if (refusal) {
        session.agentStarted = false;
        writeJson(path.join(paths.sessionDir, SESSION_FILE), session);
        log.lines(refusal);
    } else if (ai) {
        agentLaunched = true;
        const prompt = fs.readFileSync(session.agentPromptPath, 'utf8');
        log.lines(agentBannerLines(session));
        endings.push(driveWithAgent(session, prompt, { control: agentControl }).then((result) => {
            // AN AGENT THE RECORDER STOPPED ENDED NOTHING EITHER (a hand-over:
            // the site was throttling or blocking it). The recording is the
            // human's now, and goes on until THEY end it -- so this promise must
            // not resolve the race. It waits for whatever ending does.
            if (result.stoppedFor) {
                session.agentExitCode = result.status;
                return (async () => { while (!reason) await sleep(POLL_MS); })();
            }
            // AN AGENT THAT OUTLIVED THE RECORDING ENDED NOTHING. When ENTER, the
            // stop command or a closed window got there first, the operator was
            // already told the agent is still running (agentStillDrivingLines),
            // and the session is already recorded as ended by that.
            const ending = agentExitEnding(result, reason);
            if (!ending) return;
            session.agentExitCode = result.status;
            // A DRIVER THAT FAILED IS STILL AN ENDING. The traffic recorded up to
            // that point is real and cannot be re-recorded, so the run post-
            // processes it exactly as a clean finish does; what changes is that
            // the operator is told, and that the reason on the session says which
            // happened rather than claiming the agent was done.
            if (ending.warning) {
                log.warn(`${ending.warning}\n  Its step log: ${session.agentLogPath}`);
            }
            settle(ending.reason);
        }));
    }

    await Promise.race(endings);
    if (rl) rl.close();

    // Ctrl+C is ambiguous in a way ENTER is not: it can mean "I'm done" or "I
    // hit the wrong key". Cancel keeps the raw and skips post-processing, so a
    // mis-keyed Ctrl+C is recoverable. Without a TTY there is nobody to ask,
    // and SIGINT means what it conventionally means: cancel.
    let cancelled = false;
    if (reason === 'ctrl-c') {
        if (isTty) {
            const answer = await askLine(
                '\ncapture-har: Ctrl+C -- [f]inish and post-process, or [c]ancel? [f/c] ');
            cancelled = /^c/i.test((answer || '').trim());
        } else {
            cancelled = true;
        }
    }

    // AN AGENT'S LAST ACTION IS STILL BEING RECORDED WHEN THE AGENT IS GONE (#98).
    //
    // Only for an agent ending, because only an agent ends a recording in the same
    // millisecond as its last action. ENTER, a stop command and a closed window all
    // arrive after a human has moved, and a closed window has no browser left to
    // wait for -- waiting there would buy nothing and delay every capture.
    // AND THE OTHER DIRECTION: this run ended first, and the agent did not. Said
    // before the recorder is torn down, because from here on what the agent does
    // is not being recorded -- and nothing killed it (see agentStillDrivingLines).
    if (agentLaunched && session.agentExitCode === undefined && !session.agentStopped) {
        log.lines(agentStillDrivingLines(session));
    }

    // A hand-over with nobody at the terminal ends the recording the instant
    // the site refused -- so the refusal itself may still be in flight.
    if (reason === 'agent-finished' || reason === 'agent-failed' || reason === 'handed-over') {
        const settled = await settleRecorder(recorder);
        if (!settled.drained) {
            log.warn('capture-har: some requests were still being recorded when the agent ' +
                `finished, and did not complete within ${AGENT_SETTLE_MAX_MS / 1000}s. The ` +
                'capture may be missing its last entry or two.');
        }
    }

    recorder.stop();
    if (cdpBrowser) {
        // DETACH. NEVER CLOSE.
        //
        // `browser.close()` on a CDP connection disconnects and leaves the
        // browser, its window and every one of its tabs exactly as they are --
        // measured against Chrome 154, not inferred from the method's name. It
        // is the only teardown this branch performs: `context.close()` would
        // close the operator's tabs, and signalling the pid in session.browserPid
        // would take down a window this recording borrowed. Nothing in this file
        // ends a session by destroying something it did not make.
        //
        // A browser that is already gone -- the operator quit it, which is one
        // of the endings above -- makes this a no-op that can still reject, so
        // it is caught and said quietly. There is nothing left to fix by then.
        await cdpBrowser.close().catch((e) => {
            log.verbose(`capture-har: detaching reported ${e.message}`);
        });
    } else if (reason !== 'window-closed') {
        await context.close().catch((e) => {
            process.stderr.write(`capture-har: closing the context failed: ${e.message}\n`);
        });
    }

    // Playwright writes the HAR during close; give a slow flush a moment
    // before judging the capture missing. Skipped where there is no recordHar
    // to wait for: five seconds spent watching for a file that cannot appear
    // would be the normal ending of every native run.
    if (ownsRecordHar) {
        for (let i = 0; i < 25 && !fs.existsSync(paths.harPath); i++) await sleep(POLL_MS);
    }

    let assembled = null;
    if (!fs.existsSync(paths.harPath)) {
        assembled = assembleFromLog(paths.recordLog, paths.harPath);
    }
    // Unconditional, and BEFORE post-processing reads the raw (#442). On the
    // assembled path every entry already carries its descriptor and this is a
    // no-op; on the recordHar path it is the only place the descriptors can be
    // attached at all.
    annotateUnretainedBodies(paths.harPath, paths.recordLog);
    // THE RECORD LOG IS KEPT EVEN WHEN recordHar WON (#377).
    //
    // It used to be deleted here, on the reasoning that keeping both invites
    // somebody to analyze the wrong one. That reasoning is real but it is
    // outweighed: the log is the only witness to what the recorder SAW before
    // assembly, so it is the recovery path if a `raw.har` is ever found
    // malformed -- and a raw is the one artifact that cannot be re-made.
    //
    // Nothing under `.har-captures/` is discarded. The confusion risk is
    // answered by naming: `raw.har` is the capture, `raw.ndjson` is the log,
    // and both sit in a directory nothing but this tool writes to. Store
    // growth is real and is a separate concern deserving its own issue, not a
    // side effect of a placement fix.
    const summary = summarize(paths.harPath);

    // endedUtc is written FIRST, preserving the stop contract, and
    // postProcess is written after -- so `stop` can tell "still
    // post-processing" from "done" rather than guessing.
    session.endedUtc = new Date().toISOString();
    session.endedBy = reason;
    session.cancelled = cancelled;
    session.assembledFromLog = !!assembled;
    session.summary = summary;
    // Only when a sign-in form was met (#115), and only where the credentials
    // came from -- the same keys-only-when-relevant rule as `ai` above.
    const signInResult = signInWatcher.result();
    if (signInResult.source) session.signIn = signInResult;
    writeJson(path.join(paths.sessionDir, SESSION_FILE), session);

    if (!summary.exists) {
        process.stderr.write(
            `capture-har: nothing was recorded for ${paths.sessionDir}.\n` +
            '  Neither recorder produced a HAR: the recording was lost entirely.\n');
        return 4;
    }

    if (cancelled) {
        log.info(
            `\ncapture-har: cancelled -- ${describeSummary(summary)}\n` +
            `  raw kept, not post-processed: ${paths.harPath}\n` +
            '  Re-run post-processing by stopping this session again.');
        return 0;
    }

    // A login page is not a capture of the API (#115). The raw is kept, as a
    // cancel keeps it, and the exit code is its own so whatever started this
    // run unattended -- an agent, a phone -- can say "sign in" rather than
    // "failed".
    if (reason === 'sign-in-needed') {
        log.info(
            `\ncapture-har: ended -- sign-in needed. ${describeSummary(summary)}\n` +
            `  raw kept, not post-processed: ${paths.harPath}`);
        return 8;
    }

    log.info(`\ncapture-har: stopped (${reason}) -- ${describeSummary(summary)}`);
    // Said out loud, because the operator is about to wonder. Every other mode
    // ends with the browser gone, so a window still sitting there looks like
    // something that failed to clean up rather than the contract (#73) -- and an
    // operator who closes it by hand on that assumption loses the signed-in
    // profile they would otherwise have recorded the next session in.
    if (cdpBrowser && reason !== 'browser-closed') log.lines(detachedLines(session));
    if (recorder.excluded) {
        log.info(`  ${recorder.excluded} request(s) from outside this capture's tabs were not ` +
            'recorded -- see "Only this capture\'s tabs are recorded" in SKILL.md.');
    }
    session.postProcess = postProcess(session, { literals: signInWatcher.secrets() });
    writeJson(path.join(paths.sessionDir, SESSION_FILE), session);
    reportPostProcess(session);

    // ONE line of machine output, on stdout, naming what this run produced.
    //
    // It exists because the DEFAULT output path now carries a stamp (#377), so
    // a front door can no longer compute it: `Invoke-HarCapture.ps1` used to
    // rebuild the path from the same anchoring rule, and the alternative --
    // globbing .har-captures/ for the newest session -- is the very thing that
    // file's own comment refuses, since a concurrent capture against another
    // site finishing first would hand this run somebody else's catalogue.
    //
    // stdout, because that is where this script's machine output already goes
    // (--validate-only and `status`); every human-facing line is on stderr, so
    // a caller capturing this stream still sees the recording live.
    process.stdout.write(JSON.stringify({
        capture: 'complete',
        sessionDir: paths.sessionDir,
        outputPath: session.outputPath,
        cataloguePath: (session.postProcess.catalogue && session.postProcess.catalogue.path) || null
    }) + '\n');

    // `assembled` is only NEWS where recordHar could have won. See
    // postProcessExitCode, and the exit-code table at the top of this file.
    return postProcessExitCode(session.postProcess, assembled && ownsRecordHar);
}

/**
 * The process exit code the post-process phases earned.
 *
 * 7 exists so an advisory finding is neither a lie nor a failure. Returning 0
 * would tell every wrapper and CI step that a capture possibly carrying a real
 * card is clean; returning 6 would say "scrub or catalogue failed" about a run
 * that produced both. An error outranks a warning, and both outrank the
 * assembled-from-log signal, which is informational.
 *
 * `assembled` MEANS "recordHar could have written this and did not" -- so the
 * callers pass false in a mode where recordHar never ran at all (#73). The
 * alternative was teaching this function about browser modes, which would make
 * every caller's answer depend on a second fact it does not hold; the fact it
 * does hold is whether the signal is news.
 */
function postProcessExitCode(pp, assembled) {
    if (pp.errors && pp.errors.length) return 6;
    if (pp.warnings && pp.warnings.length) return 7;
    return assembled ? 5 : 0;
}

function reportPostProcess(session) {
    log.lines(postProcessLines(session));
}

/**
 * Every session under the captures root, as `{ dir, stamp }`.
 *
 * Sessions live at `<root>/<host>/<stamp>`, two levels down. Both callers used
 * to read the root's direct children, and both would silently find nothing at
 * this depth: the conflict scan would stop guarding the single-instance
 * profile, and `stop`/`status` would report no session at all.
 *
 * The stamp is returned alongside the directory because it, not the path, is
 * the ordering key -- the host sorts first in a joined path and would decide
 * which capture counts as newest.
 *
 * Accepts SEVERAL roots since #367 moved where new captures are written: an
 * older capture still under a working-directory root has to remain findable,
 * and a union is how it is found without anything being moved.
 *
 * THE BODY LIVES IN `capture-store.js` NOW (#386/#387). It moved rather than
 * being copied, because the batch drivers need this same enumeration and a
 * second walk beside it would give the store two notions of what a capture is.
 * The contract here is unchanged; what the move added is that a LEGACY session
 * directory sitting at the captures root -- one written before the host layer
 * existed -- is now found too. It always satisfied every test `stop` and
 * `status` apply; the walk simply started one level below it.
 */
function listSessionDirs(root) {
    return captureStore.listSessionDirs(root);
}

/**
 * Is a live capture already recording against this profile?
 *
 * The profile, not the port, is the real single-instance resource. Scanning
 * the sessions for a live driver on the same profile names the offender,
 * which "port 9333 is busy" never could -- that port may belong to anything.
 */
async function findProfileConflict(profileDir, root) {
    for (const { dir, captureClass } of listSessionDirs(root)) {
        if (!canBeLive(captureClass)) continue;
        const session = readJson(path.join(dir, SESSION_FILE));
        if (!session || session.endedUtc) continue;
        if (session.profileDir !== profileDir) continue;
        if (isDriverAlive(session)) return session;
    }
    return null;
}

/**
 * May a capture of this class be a recording that is happening RIGHT NOW?
 *
 * Only the current layout. A LIVE recorder writes `<host>/<stamp>`, so a
 * session directory at the captures root predates every capture this tool
 * writes and cannot be one in flight.
 *
 * WHY THIS IS A GUARD AND NOT A DETAIL. `isDriverAlive` is a bare
 * `process.kill(pid, 0)`: it cannot tell a running recorder from an unrelated
 * process that inherited the pid. That was harmless while legacy directories
 * were invisible here. Making them findable (#386) put old, never-closed
 * sessions into the same pool, where a reused pid would let one shadow the
 * recording the operator is sitting in front of, or refuse a new capture as a
 * profile conflict. Findable was the fix; live was never part of it.
 */
function canBeLive(captureClass) {
    return captureClass !== captureStore.CLASS_LEGACY;
}

// ---------------------------------------------------------------------------
// stop / status
// ---------------------------------------------------------------------------

/**
 * Resolve which capture to act on: an explicit --session, the newest LIVE one,
 * or failing that the newest on disk. The last case is what makes "catalogue
 * that capture" work after a session already ended.
 *
 * THERE IS NO POINTER FILE ANY MORE (#377). `current.json` sat at the captures
 * ROOT and held one `sessionDir`, in a store several agent sessions record into
 * at once -- so a second capture overwrote it and the first recording became
 * unaddressable. It was also unnecessary: this function ALREADY scanned the
 * session directories whenever the pointer went stale, which it did every time
 * a driver was killed before its own cleanup. Removing the pointer makes that
 * existing fallback the only path, and deletes a piece of shared mutable state
 * rather than adding a lock around it.
 *
 * Identity needs no pointer: the STAMP is the identity. Liveness is
 * `session.json` without `endedUtc` plus `isDriverAlive()` -- exactly the test
 * the pointer had to pass before it was trusted at all.
 *
 * With several captures live, `stop` without `--session` takes the newest live
 * one. That is deterministic, and strictly better than the old answer, which
 * was "whichever run wrote the shared file last".
 */
function resolveSession(args) {
    if (args.session) return path.resolve(args.session);
    const roots = args.dir ? [path.resolve(args.dir)] : capturesSearchRoots();
    const candidates = listSessionDirs(roots);
    if (!candidates.length) return null;
    // Sort by STAMP, not by the joined path. The host comes first in the path,
    // so a path sort would answer with the alphabetically-last host rather than
    // the most recent capture.
    candidates.sort((a, b) => (a.stamp < b.stamp ? -1 : a.stamp > b.stamp ? 1 : 0));
    // A LIVE capture outranks a newer dead one. Without this, a session that
    // ended seconds ago would shadow the recording the operator is actually
    // sitting in front of -- which is the one `stop` means.
    const live = candidates.filter(({ dir, captureClass }) => {
        if (!canBeLive(captureClass)) return false;
        const session = readJson(path.join(dir, SESSION_FILE));
        return session && !session.endedUtc && isDriverAlive(session);
    });
    const pool = live.length ? live : candidates;
    return pool[pool.length - 1].dir;
}

async function stop(args) {
    const sessionDir = resolveSession(args);
    if (!sessionDir) {
        process.stderr.write('capture-har: no capture session found to stop\n');
        return 3;
    }
    const sessionFile = path.join(sessionDir, SESSION_FILE);
    let session = readJson(sessionFile);
    if (!session) {
        process.stderr.write(`capture-har: ${sessionDir} has no ${SESSION_FILE}\n`);
        return 3;
    }
    const minBytes = numberOr(args['min-bytes'], DEFAULT_MIN_BYTES);

    if (args['validate-only']) {
        process.stdout.write(JSON.stringify({ sessionDir, harPath: session.harPath, minBytes }, null, 2) + '\n');
        return 0;
    }

    // Idempotent: a session that already ended has flushed whatever it had and
    // retired its driver, so there is nothing to signal -- just report.
    let driverLost = false;
    if (!session.endedUtc) {
        // A driver that already died will never answer the sentinel, and
        // waiting the full timeout for it looks identical to a healthy stop
        // that is merely slow. Ask about the driver process, not the debugging
        // port: a crashed driver can leave an orphaned browser answering that
        // port, and believing it would reinstate the silent 60-second hang.
        if (!isDriverAlive(session)) {
            driverLost = true;
            process.stderr.write(
                `capture-har: the recorder for ${sessionDir} is no longer running.\n` +
                '  Assembling the incremental log instead.\n');
        } else {
            fs.writeFileSync(path.join(sessionDir, STOP_SENTINEL), '', 'utf8');
            // Wait for endedUtc, then keep waiting for post-processing --
            // reporting "done" while the scrub is still running would hand the
            // caller a catalogue that does not exist yet.
            const deadline = Date.now() + STOP_TIMEOUT_MS;
            while (Date.now() < deadline) {
                const updated = readJson(sessionFile);
                if (updated && updated.endedUtc) { session = updated; break; }
                await sleep(POLL_MS);
            }
        }
    }

    let summary = summarize(session.harPath);
    let assembled = false;
    if (!summary.exists) {
        // The driver never got to write one -- assemble the log itself, which
        // is the case where the driver died rather than closed.
        assembled = !!assembleFromLog(session.recordLog, session.harPath);
        summary = summarize(session.harPath);
    }

    if (!summary.exists) {
        process.stderr.write(
            `capture-har: nothing was recorded for ${sessionDir}.\n` +
            '  Neither recorder produced a HAR: the recording was lost entirely.\n');
        return 4;
    }
    if (summary.bytes < minBytes) {
        process.stderr.write(
            `capture-har: ${summary.path} is only ${summary.bytes} bytes ` +
            `(minimum ${minBytes}) -- treat this as a failed capture, not data.\n`);
        return 4;
    }

    // The driver owns post-processing whenever it is alive, so `stop` waits
    // rather than racing it. When the driver is gone, nothing else will ever
    // run those phases, so `stop` runs them itself.
    session = readJson(sessionFile) || session;
    if (!session.postProcess && !session.cancelled) {
        // Wait only while something is actually going to do the work. A driver
        // that is gone will never write postProcess, so waiting the full
        // timeout for it is a minute of silence followed by the same answer --
        // run the phases here instead. `driverLost` alone is not the test: a
        // driver that died AFTER writing endedUtc never sets it.
        if (driverLost || !isDriverAlive(session)) {
            // `stop` runs in a DIFFERENT PROCESS from the recorder and holds no
            // in-memory index -- which is exactly why the descriptors are read
            // back from `raw.ndjson` rather than carried in a variable (#442).
            // Only in THIS branch: while the driver is alive it owns the raw,
            // and two processes rewriting one file is not an improvement over
            // an un-annotated one. Idempotent, so a raw the driver already
            // annotated is left alone.
            annotateUnretainedBodies(session.harPath, session.recordLog);
            session.postProcess = postProcess(session);
            writeJson(sessionFile, session);
        } else {
            const deadline = Date.now() + STOP_TIMEOUT_MS;
            while (Date.now() < deadline) {
                const updated = readJson(sessionFile);
                if (updated && updated.postProcess) { session = updated; break; }
                await sleep(POLL_MS);
            }
        }
    }

    log.info(`capture-har: ${describeSummary(summary)}`);
    if (session.postProcess) {
        reportPostProcess(session);
    } else if (session.cancelled) {
        log.info(`  raw kept, not post-processed: ${session.harPath}`);
    } else {
        log.warn(
            `  raw:       ${session.harPath}\n` +
            '  post-processing is still running in the recording process --\n' +
            '  the scrubbed artifacts and the catalogue are not ready yet.');
        return 6;
    }

    // `ownsRecordHar !== false` rather than a truthiness test: a session.json
    // written before #73 has no such field, and the mode it recorded under is
    // the one where assembling IS news. Absent has to keep meaning true.
    return postProcessExitCode(session.postProcess,
        (assembled || session.assembledFromLog) && session.ownsRecordHar !== false);
}

// ---------------------------------------------------------------------------
// catalogue -- the pipeline's second door
// ---------------------------------------------------------------------------

/**
 * What the `catalogue` command was pointed at, and where its output goes.
 *
 * Three shapes, because an operator re-entering the pipeline has three things
 * to hand and none of them is reliably the same one:
 *
 *   a FILE            the scrubbed HAR itself. Output lands beside it.
 *   a SESSION dir     one holding session.json. Its own outputPath is used,
 *                     and its uri/describe/startedUtc are carried through --
 *                     the provenance a re-catalogue would otherwise lose.
 *   any other dir     an output path that already holds a scrubbed.har.
 *
 * With none of them, `resolveSession` answers -- the same resolution `stop`
 * and `status` use, whose whole point is that it still works after a session
 * has ended.
 */
function resolveCatalogueTarget(args) {
    const given = args._ && args._.length ? path.resolve(args._[0]) : null;
    if (given && !fs.existsSync(given)) {
        return { error: `${given} does not exist`, code: 3 };
    }
    if (given && fs.statSync(given).isFile()) {
        return catalogueTarget(given, args, null);
    }
    const dir = given || resolveSession(args);
    if (!dir) return { error: 'no capture session found to catalogue', code: 3 };
    const session = readJson(path.join(dir, SESSION_FILE));
    const outputPath = args['output-path']
        ? path.resolve(args['output-path'])
        : (session && session.outputPath) || dir;
    return catalogueTarget(path.join(outputPath, SCRUBBED_HAR), args, session);
}

function catalogueTarget(scrubbedPath, args, session) {
    const outputPath = args['output-path']
        ? path.resolve(args['output-path'])
        : path.dirname(scrubbedPath);
    if (!fs.existsSync(scrubbedPath)) {
        // Said, not guessed at. The likeliest reason a session has no scrubbed
        // HAR in its output path is that the gate refused the scrub and #343
        // quarantined it -- in which case the file to look at is the rejected
        // one, and re-cataloguing is not what the operator needs next.
        const rejected = session && session.sessionDir
            && path.join(session.sessionDir, REJECTED_HAR);
        return {
            code: 3,
            error: rejected && fs.existsSync(rejected)
                ? `${scrubbedPath} does not exist -- this capture's scrub was REJECTED by the ` +
                  `leak gate and quarantined at ${rejected}. There is nothing verified to ` +
                  'catalogue: triage the findings, re-scrub, and catalogue then.'
                : `${scrubbedPath} does not exist -- nothing to catalogue.`
        };
    }
    // Provenance the digest would otherwise lose on a second pass. The
    // previous digest is the fallback because capturedUtc means WHEN THE
    // RECORDING HAPPENED: re-cataloguing must not re-date a capture to the day
    // somebody regenerated its catalogue.
    const previous = readJson(path.join(outputPath, DIGEST_FILE)) || {};
    return {
        scrubbedPath,
        outputPath,
        session: {
            uri: (session && session.uri) || previous.uri || null,
            describe: (session && session.describe) || previous.describe || null,
            startedUtc: (session && session.startedUtc) || previous.capturedUtc || null,
            harPath: (session && session.harPath) || null,
            sessionDir: (session && session.sessionDir) || null,
            outputPath
        }
    };
}

/**
 * How many rows in the catalogue at `p` carry a description.
 *
 * The discriminator between "a scaffold nobody has worked on" and "a catalogue
 * with something in it to lose". Keyed on Description and not on Status, and
 * for the reason Invoke-HarCapture.ps1 already gives: a real AI pass may
 * legitimately conclude that every group was Observed and none Exercised, so
 * Status cannot tell the two apart. Describing an action is the one thing the
 * AI does that the scaffold never can.
 *
 * A file that is not a catalogue at all counts as no work -- the refusal below
 * exists to protect somebody's segmentation, not to bounce off anything that
 * occupies the name.
 */
function describedRowCount(p) {
    const rows = readJson(p);
    if (!Array.isArray(rows)) return 0;
    return rows.filter((r) => r && typeof r === 'object' && r.Description).length;
}

/**
 * Catalogue a capture that was recorded and scrubbed some other time.
 *
 * This is an ENTRY POINT to the phases postProcess already runs, and nothing
 * more. It re-decides nothing: the gate verdict comes from `askTheGate`, the
 * digest and the delegation from `catalogueScrubbed`, and the exit code from
 * `postProcessExitCode` -- the same three functions a full capture goes
 * through, so an advisory verdict still exits 7 here and a refusal still
 * exits 6.
 *
 * It asks the gate again rather than trusting the file's location, because
 * "the digest and catalogue derive from a capture that PASSED the gate" is a
 * property of the pipeline and not of the capture path. The new door makes it
 * possible for the first time to point the catalogue phase at a HAR nobody
 * scrubbed, so the door has to hold the property itself.
 *
 * It never publishes and never quarantines. A scrub is judged and promoted by
 * the scrub stage; re-implementing that here would put a second copy of #343's
 * invariants behind a second door.
 */
function catalogueCommand(args) {
    const target = resolveCatalogueTarget(args);
    if (target.error) {
        process.stderr.write(`capture-har: ${target.error}\n`);
        return target.code || 2;
    }

    // WHAT HAPPENS WHEN THE CATALOGUE STAGE RUNS TWICE: it refuses, when there
    // is work in the catalogue to lose.
    //
    // The segmentation is an AI reading catalogue-prompt.md, so two runs over
    // the same digest can legitimately disagree -- this is not a deterministic
    // recomputation, and `catalogue.json` is a fixed name. Silently replacing a
    // catalogue a human reviewed (or corrected by hand) with a fresh,
    // differently-grouped one is the more expensive of the two mistakes, and it
    // is invisible after the fact.
    //
    // But refusing on EXISTENCE alone would block the case this command exists
    // for. The advisory loop is: capture, waive a false positive, catalogue
    // again -- and by then a scaffold is already sitting there. So the test is
    // whether the catalogue carries WORK, using the same discriminator
    // Invoke-HarCapture.ps1 already uses to tell a finished catalogue from a
    // provisional one: a Description is the one thing the AI writes that the
    // scaffold never can. A scaffold is regenerated without comment; anything
    // described is left exactly as it is.
    //
    // Deliberately NOT behind an override option. An option would let a caller
    // ASK for the destructive branch, which is precisely the request that
    // should cost a moment's thought; moving the file you want replaced is an
    // unambiguous statement of the same intent, and is already possible.
    const cataloguePath = path.join(target.outputPath, CATALOGUE_FILE);
    const described = describedRowCount(cataloguePath);
    if (described) {
        process.stderr.write(
            `capture-har: ${cataloguePath} already carries ${described} described ` +
            'action(s), so nothing was written.\n' +
            '  Cataloguing is an AI pass, not a recomputation: a second run over the same\n' +
            '  digest may group the session differently, and replacing a catalogue somebody\n' +
            '  reviewed or corrected would discard that silently.\n' +
            '  To catalogue afresh, move the existing file aside (or catalogue into another\n' +
            '  --output-path) and run this again.\n');
        return 2;
    }

    const state = { startedUtc: new Date().toISOString(), errors: [], warnings: [] };
    const verdict = askTheGate(target.scrubbedPath, state);
    if (verdict.verified || verdict.advisory) {
        const findings = path.join(target.outputPath, FINDINGS_FILE);
        if (isFindingsReport(findings)) state.scrubbed.findings = findings;
        fs.mkdirSync(target.outputPath, { recursive: true });
        catalogueScrubbed(target.session, state);
    } else {
        // No path is claimed, because claiming one would make the summary read
        // as though a verified artifact is sitting there.
        state.scrubbed = { path: null, verified: false, advisory: false };
        state.errors.push(
            `capture-har: ${target.scrubbedPath} did not pass the leak gate, so no digest and ` +
            'no catalogue were derived from it. Nothing was written. Re-scrub it, or waive ' +
            'the findings, and catalogue again.');
    }
    state.completedUtc = new Date().toISOString();

    // The shared placement guard, not a second opinion about where output may
    // land: this command writes into the same committable directory a capture
    // does, so it owes the same notice.
    //
    // It asks about THE FILE IT WILL WRITE, not merely about the checkout it
    // was started from (#471). Asking about location alone is what it used to
    // do, and it was wrong in the same way capture was: the default output here
    // is the capture's own session directory under the gitignored captures
    // root, so a default run from the protected branch was told to `mv` an
    // artifact that was never misplaced.
    const placement = placementForOutput(cataloguePath, capturePlacement(process.cwd()).root);
    log.lines(postProcessLines(Object.assign({}, target.session, {
        placement,
        postProcess: state
    })));
    return postProcessExitCode(state, false);
}

function status(args) {
    const roots = args.dir ? [path.resolve(args.dir)] : capturesSearchRoots();
    const sessionDir = resolveSession(args);
    if (!sessionDir) {
        log.info(`capture-har: no captures under ${roots.join(' or ')}`);
        return 0;
    }
    const session = readJson(path.join(sessionDir, SESSION_FILE)) || {};
    process.stdout.write(JSON.stringify({
        sessionDir,
        uri: session.uri,
        mode: session.mode,
        // Reported so an agent reading this does not close a browser that is not
        // the recorder's to close (#73).
        browser: session.browser || null,
        attached: !!session.attached,
        cdpEndpoint: session.cdpEndpoint,
        outputPath: session.outputPath,
        // A crashed driver never writes endedUtc, so its absence does not mean
        // "still recording" -- only a live driver does. Reporting a dead
        // session as recording sends the operator to wait for a HAR that
        // nothing will ever write.
        recording: !session.endedUtc && isDriverAlive(session),
        endedBy: session.endedBy,
        driverLost: !session.endedUtc && !isDriverAlive(session),
        postProcess: session.postProcess || null,
        summary: session.summary || summarize(session.harPath)
    }, null, 2) + '\n');
    return 0;
}

async function main() {
    const argv = process.argv.slice(2);
    const command = argv[0];
    const args = parseArgs(argv.slice(1));
    // Applied before anything else runs, and to every command: the level has
    // to be in force before the first message, and `stop` reports the same
    // artifacts `start` does.
    if (args['log-level'] !== undefined) {
        try {
            setLogLevel(args['log-level']);
        } catch (e) {
            return usage(e.message);
        }
    }
    switch (command) {
        case 'start': return await start(args);
        case 'stop': return await stop(args);
        case 'status': return status(args);
        case 'catalogue': return catalogueCommand(args);
        default: return usage(command ? `unknown command '${command}'` : 'a command is required');
    }
}

module.exports = {
    START_OPTIONS,
    LOG_LEVELS,
    setLogLevel,
    renderLines,
    startBannerLines,
    detachedLines,
    createCaptureScope,
    postProcessLines,
    buildEntry,
    assembleFromLog,
    annotateUnretainedBodies,
    summarize,
    readLogEntries,
    resolveSessionPaths,
    uriFolder,
    resolveSession,
    findProfileConflict,
    findFreePort,
    resolveBrowserMode,
    parseCdpEndpoint,
    chromeCandidates,
    findChromeExecutable,
    launchNativeChrome,
    waitForCdp,
    probeCdpEndpoint,
    syncedFolderWarning,
    ensureProfileIgnored,
    BROWSER_PLAYWRIGHT,
    BROWSER_NATIVE,
    BROWSER_MODES,
    discoverStorageState,
    createTerminal,
    pathTemplate,
    buildDigest,
    buildCatalogueScaffold,
    decideCatalogueRunner,
    catalogueChildCommand,
    buildAgentPrompt,
    agentChildCommand,
    captureBrowserName,
    captureProfileRoot,
    chromeExtensionInstalled,
    chromeExtensionNotice,
    CLAUDE_EXTENSION_ID,
    agentBannerLines,
    agentStillDrivingLines,
    agentExitEnding,
    agentStartRefusal,
    agentDriving,
    claudeOnPath,
    driveWithAgent,
    settleRecorder,
    resolveAgent,
    postProcess,
    runCatalogue,
    catalogueCommand,
    postProcessExitCode,
    publishFile,
    sweepAbandonedTemps,
    PUBLISH_TEMP_PREFIX,
    PUBLISH_RENAME_ATTEMPTS,
    findingSummaryLines,
    catalogueTableLines,
    isScaffoldOnly,
    referenceNoticeLines,
    describeReference,
    referenceFileName,
    referenceRelativePath,
    deriveActionSlug,
    providerSlug,
    outputDestinationWarning,
    placementForOutput,
    ensureCapturesRootIgnored,
    START_OPTIONS,
    preflightDependencies,
    resolvePlaywright,
    dependencyInstallCommands,
    capturesRootProbe,
    CAPTURE_GITIGNORE_ENTRIES,
    REFERENCE_DIR,
    runNode,
    IncrementalRecorder
};

// Only run as a command when invoked as one: requiring this module from a test
// must not launch a capture or print a usage error.
if (require.main === module) {
    main().then((code) => process.exit(code)).catch((err) => {
        process.stderr.write('capture-har: ' + (err && err.stack ? err.stack : err) + '\n');
        process.exit(1);
    });
}
