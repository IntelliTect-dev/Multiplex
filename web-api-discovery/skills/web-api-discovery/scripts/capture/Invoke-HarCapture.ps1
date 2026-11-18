#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Record a browser session, scrub it, and catalogue it -- one command. Or
    re-run the scrub, the catalogue, or both on a capture that already exists.

.DESCRIPTION
    PowerShell front door for capture-har.js. Opens a browser on a dedicated
    capture profile, records every request, then scrubs, verifies, digests and
    catalogues what it recorded.

    The operator's surface is a URL and a sentence saying what the recording
    is for:

        Invoke-HarCapture https://example.com -Describe 'create, then delete, a post'

    Browse, perform the operations worth documenting -- including the failure
    paths, which are frequently the highest-value entries because they
    establish the site's real error taxonomy -- then press ENTER in this
    terminal. Closing the browser window does the same thing; so does
    Stop-HarRecording from another process. Ctrl+C asks whether you meant to
    finish or to cancel.

    HOW MUCH IT SAYS. By default the console names the site, how to end the
    recording, and the artifacts the run produced. `-Verbose` adds the resolved
    paths, the capture profile, the CDP endpoint and the per-phase detail --
    including the recorder's own, because the level is forwarded to it. Nothing
    a failure needs to report is ever gated by the level: a warning, a
    leak-gate rejection and an error are printed at every level.

    `-InformationAction SilentlyContinue` silences the status lines without
    silencing the warnings.

    `Invoke-`, not `Start-`, because the command no longer merely starts
    something: by the time it returns, the capture has been scrubbed and
    verified and the catalogue exists.

    TWO DIRECTORIES, and the difference is the whole safety story:

      .har-captures/   the raw capture. Carries live session cookies, is
                       gitignored, and CANNOT be redirected -- there is no
                       option for it, so an unignored credential-bearing
                       capture is not reachable by any argument.

                       WHICH .har-captures: the one in the repository's MAIN
                       working tree, never a linked worktree's. A worktree is
                       disposable by design and `git worktree remove` deletes a
                       gitignored capture outright with nothing to prompt
                       about; a raw is the one artifact that cannot be
                       regenerated. Outside a repository it is relative to the
                       current directory, as before. The recorder prints the
                       resolved path as it starts.
      -OutputPath      scrubbed, verified artifacts only: the session digest,
                       the catalogue and the scrubbed capture. It DEFAULTS to
                       this run's own session directory, beside the raw.

    So by default there is one directory, stamped with the run, and everything
    lands in it:

      .har-captures/app.example.com/<timestamp>/{raw.har,scrubbed.har,digest.json,catalogue.json}

    That is the fix for three defects at once (#377): nothing untracked appears
    in a work tree, a second capture against the same host no longer overwrites
    the first's scrubbed artifact, and the scrubbed capture sits beside the raw
    it came from. Passing -OutputPath still writes <path>/<host>/ instead, and
    the recorder WARNS -- it does not refuse -- when that destination is inside
    a work tree and not gitignored.

    The host alone, never the full URL: a magic-link or password-reset URL
    carries its token in the path or the query, and a host directory sits next
    to committable artifacts.

    PROMOTING A REFERENCE is a separate, deliberate step, and this command does
    not perform it. A committed reference is a TRIMMED extract of the entries
    that matter (kilobytes), not a whole scrubbed capture (hundreds of
    megabytes), and choosing those entries is a judgement. The run ends by
    printing the ready-to-paste har/extract-har-reference.js command, with the
    provider, the action and the destination filled in.

    RE-RUNNING ONE PHASE. `-Scrub` and `-Catalogue` run on a capture that
    ALREADY EXISTS and record nothing -- no browser, no new session directory:

        Invoke-HarCapture https://example.com -Catalogue
        Invoke-HarCapture https://example.com -Scrub -Catalogue

    They are for the capture you already have: the scrubber improved, a finding
    was waived, the AI cataloguing pass failed. A browser session somebody
    drove by hand is not a thing to repeat for any of those. Without either
    switch the command records, exactly as before.

    The first argument then says WHICH capture instead of which site to open --
    the site's URL, the capture's directory, or its `host/stamp` label -- and
    the run says which one it selected before it does anything. A site recorded
    more than once is refused with the list rather than resolved to the newest;
    see -Uri and -Describe for how to name one. A capture that is not in the
    state a phase needs is refused too: there is nothing to scrub without a raw,
    and a catalogue is only ever derived from a scrubbed, verified capture.

    Both phases read and write the capture's OWN directory under
    .har-captures/, and both are the pipeline's existing re-entry points --
    Invoke-SanitizeHar for the scrub, the recorder's `catalogue` command for
    the catalogue -- so the leak gate, the quarantine of a refused scrub and
    the refusal to replace a catalogue somebody worked on are all exactly what
    they are when those are run directly.

.PARAMETER Uri
    The site to open. Positional, so the parameter name is optional.

    WITH -Scrub OR -Catalogue it names the existing capture to run on, and
    nothing is opened. Any of:

      * the site's URL -- the capture recorded for that site. Only the host is
        used, and only the host is ever printed;
      * the capture's directory, which `Get-HarCapture` reports as Path;
      * its `host/stamp` label, e.g. app.example.com/2026-01-02-143000 --
        `Get-HarCapture`'s Site and Stamp joined by a slash. It is the name
        this command's own refusals list captures by, and the one
        `Remove-HarCapture` takes.

    It has to come to exactly one capture this recorder made. None, several, or
    one somebody imported are each refused, with what was found.

.PARAMETER OutputPath
    Where the scrubbed capture, the digest and the catalogue are written.

    THE DEFAULT is this run's own session directory under .har-captures/ --
    the same stamped directory the raw capture is in (#377). It is gitignored,
    so nothing appears untracked in a work tree, and it is stamped, so a second
    capture against the same host cannot overwrite the first's scrubbed.har.

    It used to be the current directory, and then the repository root. Both
    were wrong the same way: the root of whichever work tree the operator
    happened to be standing in is not a place scrubbed artifacts belong, and a
    run from a checkout root dropped an untracked <host>/ directory there.
    Anchoring made the placement predictable, not correct.

    Passing -OutputPath D:\refs writes D:\refs\app.example.com\ instead: the
    host folder is always appended, and a relative path resolves against the
    current directory, because a path you typed has to mean what you typed.
    When that destination is inside a git work tree and is NOT gitignored the
    recorder warns, names the resolved path, says the artifacts will show as
    untracked, and proceeds -- refusing a destination you deliberately named
    would be user-hostile, and the session directory keeps its own copy either
    way. The raw capture never goes here under any value.

    Recording from the primary checkout on the protected branch is NOT warned
    about on its own (#471). It used to be, from here, back when the default
    output was the work tree root. The default is now the gitignored session
    directory, so where you are standing no longer decides whether anything
    committable is left behind -- only where this parameter points does. The
    recorder makes that call once, with the resolved destination in hand; see
    placementForRun in capture-har.js.

.PARAMETER Describe
    REQUIRED. What this recording is for, in your own words. Omitting it is a
    terminating error, not a prompt: this front door and capture-har.js refuse
    on identical terms, so a person recording by hand and an agent driving the
    same script hit the same wall in the same way. It helps the
    cataloguing step segment the session into actions, but that is the smaller
    half of its job.

    THE CAPTURE STORE IS SHARED AND APPEND-ONLY, and its directory names are
    capture START times while session.json's mtime is when post-processing
    FINISHED -- so with several sessions recording at once neither orders
    reliably, and a time window wide enough to hold your runs holds other
    people's too. The host directory groups by site, not by intent. In that
    setting the description is the only thing that says what a capture was
    FOR, and it is the one part of a capture that cannot be reconstructed
    afterwards: the bytes can be re-captured, what you were doing cannot.

    WHO made a capture is a different question and is not asked of you: every
    capture records its own id and the run that made it (#26), and this
    command says both when the recording ends. `Get-HarCapture` lists them.

    It is never the source of action names -- those come from the traffic.

    WITH -Scrub OR -Catalogue it is not required: the capture was described
    when it was recorded. Typed anyway, it NARROWS the selection to the capture
    recorded with exactly that description -- so the command that made a
    capture, with a phase switch added, names that capture. It renames nothing.

.PARAMETER Profile
    Which signed-in identity to record as.

    A NAME keeps a separate capture profile per identity, so several accounts
    can stay signed in side by side without you knowing where profiles live.

    A PATH records as an identity another tool already owns -- useful when a
    project keys browser profiles off its own concept (a workspace, an account,
    a tenant) and can compute that directory for you. That tool cannot use the
    profile until the recording ends, because a persistent profile is
    single-instance; the recorder says so when you pass one.

    Omit it for the default capture profile.

.PARAMETER Isolated
    Use bundled Chromium with an ephemeral profile instead of system Chrome on
    the persistent capture profile. For CI and throwaway captures. An isolated
    run is not signed in unless a .har-storage-state.json is found at or above
    the working directory, which is discovered automatically.

.PARAMETER Port
    Remote-debugging port the recording context listens on, so an agent can
    attach over CDP and drive the same recording. Default 9333, and it never
    has to be specified: a busy port falls forward to the next free one, and
    the endpoint actually chosen is written to the session so an agent reads
    it rather than assuming.

    Refused with -Attach: that browser is already listening somewhere, and its
    endpoint names the port.

.PARAMETER Browser
    WHO LAUNCHES THE BROWSER.

    `Playwright` (the default) launches it here, which is what every capture
    did before -- and what a site with bot protection detects and refuses.
    Playwright marks the browser as automated, and the protection answers the
    marking rather than the traffic. Measured on car-rental sites: Imperva
    returned 403 "Error 15" on the first page load, Akamai reset the search
    POST, DataDome served a slider CAPTCHA and then 403 even once it was
    solved, and PerimeterX raised "Press & Hold" as soon as a location field
    was typed into -- all while browsing BY HAND, and all on sites that worked
    normally in an ordinary Chrome on the same machine and network.

    `Native` is that ordinary Chrome: the recorder starts chrome.exe itself
    with no automation flags and attaches over CDP. Use it on any site that
    refuses the default. There are no stealth measures and none are wanted --
    a site may still challenge, and you answer it in the window.

    What changes: Playwright's recordHar belongs to a browser Playwright
    launched, so raw.har comes from the incremental recorder instead. That
    artifact is not degraded (real timings, uncapped bodies, the failed
    requests recordHar drops), and the scrub/verify/digest/catalogue tail is
    identical.

    Exclusive with -Isolated, which launches the bundled Chromium -- the very
    launch a protected site rejects.

    The recording is scoped to this capture's tabs, and the debugging port
    stays open after it stops: see -Attach.

.PARAMETER Attach
    Record in a Chrome that is ALREADY RUNNING, given its debugging endpoint:
    `http://127.0.0.1:9222`, `127.0.0.1:9222`, or just `9222`.

    Start that browser with a DEDICATED profile -- Chrome refuses remote
    debugging on a profile another Chrome already has open:

        chrome --user-data-dir=C:\chrome-capture --remote-debugging-port=9222

    Implies -Browser Native. STOPPING DETACHES: the browser, its window and
    every tab in it are left exactly as they are, because this recording
    borrowed a browser it did not open. Nothing here ever closes one.

    A new tab is opened for -Uri rather than navigating one of yours away.

    ONLY THIS CAPTURE'S TABS ARE RECORDED (applies to -Browser Native too). A
    request is recorded when the tab or window that made it is in scope: the
    tab opened for -Uri, any window opened from an in-scope tab (a sign-in
    popup, on any host), and any tab whose page is on -Uri's site -- its host
    without a leading www., plus subdomains. A tab stays in scope wherever it
    navigates, and every request an in-scope tab makes is recorded whatever
    host it goes to. Other tabs in that browser are not recorded.

    RESIDUAL RISK: the debugging port outlives the capture. It has no
    authentication and stays open for as long as that window lives, so any
    local process can attach and read the profile's cookies. Close the window
    when you are done.

    -Port and -Profile are refused with it: the browser is already listening
    on a port and already running on a profile, so neither is this run's to
    choose.

.PARAMETER AI
    AN AGENT DOES THE BROWSING while the recorder records.

        Invoke-HarCapture https://www.avis.com -AI `
            -Describe 'search DCA->IAD one-way, all classes, pay now vs pay later, rate details'

    -Describe BECOMES THE GOAL, so this switch needs no new input of its own: it
    is already mandatory. Everything else is generated.

    IT IMPLIES -Browser Native, and is therefore exclusive with -Isolated and
    with -Browser Playwright. An agent driving a Playwright-launched browser is
    an agent driving the one launch the protected sites above refuse outright,
    which are the sites worth driving an agent at in the first place.

    THE PROMPT IS GENERATED, NOT HAND-WRITTEN, and that is the feature. It is
    assembled from the recorder's own drive-prompt.md, this run's facts (the
    goal, the site, the CDP endpoint, the session file) and
    discovery-checklist.md; written into the session directory as
    `agent-prompt.md`; and run as `claude -p`. Most of that text is SAFETY text
    -- human pace, keep the site's tab in front, no stealth measures, never a
    password or personal or payment detail, stop before any driver-details or
    payment page, and stop and hand back on any bot check, sign-in prompt, or
    page saying the site is throttling or blocking it (see -Pace) -- and
    a prompt somebody retypes per run is a prompt that eventually drops one of
    them.

    THE AGENT IS TOLD, NEVER ENDED. `claude -p` runs with stream-json in and
    out: the prompt is its first message, and its input stays open so the
    recorder can tell it the site asked for a pause, the wait is over, a person
    is taking over, the person is done, or the capture has ended (see -Pace).

    ONLY BROWSER TOOLS ARE PRE-APPROVED. `claude -p` is given an
    `--allowedTools` list of the Claude-in-Chrome page tools and Playwright MCP's
    navigate, click, type, screenshot and snapshot -- no shell, no file writes.
    The list pre-approves rather than removes, so what it leaves out is decided
    by -AITools:

      Auto         (the default) `--permission-mode auto`: Claude Code's
                   classifier approves or blocks each action outside the list,
                   so a stuck agent can still take a reasonable step, while the
                   browser tools never wait on it. The classifier is a
                   judgement, not a wall.
      BrowserOnly  also `--tools ""`: every BUILT-IN tool is removed -- no
                   Read, Grep or Bash -- so no built-in tool can read the
                   capture files. Only MCP tools remain.

    The agent reads untrusted page content (a prompt-injection surface) from a
    folder whose capture files hold LIVE session cookies. Use -AITools BrowserOnly
    whenever the browser is signed into something sensitive -- banking, an admin
    console, your own mail.

    `--tools ""` governs only the built-in set: other MCP servers configured in
    your Claude Code settings (a filesystem or shell server, say) still load, and
    in auto mode the classifier is their only gate. For a sensitive site, keep
    only the browser MCP configured.

    THE STEP LOG is saved beside the capture as `agent-log.txt`: what the agent
    did, what it saw and where it stopped, so the cataloguing step has a record
    of the actions taken. It is also streamed to this terminal as it happens.

    THE RECORDING ENDS when the agent finishes or hands back, and then the usual
    scrub -> verify -> digest -> catalogue tail runs, unchanged. A drive is never
    a loss of control: ENTER here, `Stop-HarRecording` from another process, and
    closing the browser window all still end it sooner.

    It needs the `claude` CLI on PATH. That is checked BEFORE a browser is
    started, so a missing driver costs nothing to clean up.

    THE CLAUDE-IN-CHROME EXTENSION IS THE DEFAULT DRIVER once it is set up,
    once, in the CAPTURE PROFILE -- by hand; nothing installs it and no
    `--load-extension` is passed:

      1. Open the capture browser on its profile (a run without -AI,
         -Browser Native).
      2. In it, install Claude in Chrome from the Chrome Web Store:
         https://chromewebstore.google.com/detail/claude/fcoeoabgfenejglbffodgkkbkcdhcgfn
      3. Sign in to the extension, and connect it under the capture browser
         name the run prints: `har-capture` for the default profile, or
         `har-capture-<folder>-<6 hex>` (a hash of the profile path) for any
         other. Give that name to the capture browser only. The hash comes
         from the path, so moving or renaming a profile changes the name:
         reconnect the extension under the new one.

    Before the browser launches, every -AI run looks for the extension's store
    ID under the profile's Extensions folder. Installed: `claude -p` gets
    `--chrome` and the allowlist includes the extension's
    list_connected_browsers and select_browser. Missing: the run warns, prints
    these steps, and drives through Playwright MCP -- `--no-chrome` (off even
    if your settings enable it by default), no extension tools in the
    allowlist, and a prompt that says Playwright MCP only. With -Attach nothing
    is checked and `--chrome` is passed; the name is the plain `har-capture`,
    so give it only to the browser you attach to -- and an attached run whose
    only connected Chrome is another browser hands back rather than falling
    back to Playwright MCP.

    THE BROWSER-SELECTION RULE, enforced by the prompt, not by code. Before
    any page action the agent lists the connected browsers and selects, with
    select_browser, the one whose name is exactly the capture browser name. If
    it cannot identify it unambiguously it never acts in another browser (that
    could be your everyday Chrome, unrecorded): it stops and hands back.

    NOT YET, and deliberately: a human sign-in checkpoint (`-SignInFirst`, #75)
    and driving several sites in one run (#74). Until #75 the agent stops at the
    first sign-in prompt and hands back; until #74 one run drives one site.

.PARAMETER AITools
    With -AI: which tools the driving agent has.

      Auto         (the default) auto permission mode: its classifier approves
                   or blocks each action.
      BrowserOnly  every built-in tool is removed (`--tools ""`); only MCP
                   tools remain, so keep only the browser MCP configured.

    Forwarded only when typed. Without -AI it is refused, at exit 2: there is no
    agent whose tools it could choose. See -AI for when to use BrowserOnly.

.PARAMETER Pace
    How fast the recorder acts on a page itself (its first navigation, the
    sign-in fills), and how fast an -AI agent is told to. Forwarded as --pace
    only when typed.

      (absent) | human   1.5-4 s between navigations, 0.3-1.2 s between clicks
                         or typing steps, at most 12 navigations a minute, each
                         wait drawn at random -- the default
      nav=MIN-MAX,action=MIN-MAX,per-minute=N
                         milliseconds; any subset, the rest keep their defaults;
                         a wait over 10 minutes is clamped to 10, and said so
      off | 0            every wait zero -- for tests, never for a real site

    Whatever the pace, the recorder reads every response from the site. A 429
    or a 503 with Retry-After PAUSES THE WHOLE CAPTURE -- the agent included,
    with the browser holding this capture's requests to the site -- for exactly
    as long as the site asks (up to a day), or for a backoff of about 1, 2, 4,
    8 then 15 minutes when it names no wait, and then carries on by itself. A
    block, a bot check, or throttling that goes on for an hour with no page
    loading hands the browser to you; the agent is told to wait, never ended.
    This command's output says so as it happens:

      capture-har: Paused until 10:42 -- the site asked us to wait 5 min (HTTP 429)
      capture-har: Resumed after 5 min; the agent has been told to carry on.
      capture-har: Handed over to you: <reason>. Open on your phone: <link>

    The phone link appears only when this PC is on a Tailscale tailnet: it
    shows the capture browser live, privately, on the owner's tailnet, with
    "I'm done, continue" and "End the capture". At this PC, ENTER ends the
    recording and `continue` + ENTER hands back. Nothing retries a block or
    works around a bot check. Each pause is recorded under `pauses` in
    session.json. See "Pacing, throttling, and where the line is" in SKILL.md.

.PARAMETER Scrub
    Scrub an EXISTING capture again, and verify the result. Records nothing.

    The capture's raw is scrubbed into its own directory under a temporary
    name, put in front of the leak gate, and called `scrubbed.har` only once
    the gate has passed it. A scrub the gate rejects is quarantined beside the
    raw as `scrubbed.rejected.har` and replaces nothing -- a `scrubbed.har` an
    earlier run verified stays as it was. The raw is never written to.

    A capture that was already scrubbed is scrubbed AGAIN: that is the request.
    Its digest and catalogue then describe the scrub that was replaced, and the
    run says so; add -Catalogue to rebuild them in the same run.

    Refused when the capture has no raw.har. An ADVISORY verdict (identity
    evidence by shape alone) keeps the artifact and is said in the summary.

    A capture whose recorder SIGNED IN for it is scrubbed with a warning. The
    values it filled were handed to the scrub at recording time and exist
    nowhere else, so this scrub removes them by field name and cannot prove
    them gone -- check the new scrubbed.har before sharing it.

    The exit code is the scrub's own: 0 when a verified scrubbed.har was
    produced, non-zero when it was not.

.PARAMETER Catalogue
    Digest and catalogue an EXISTING, already scrubbed capture. Records
    nothing, and emits the catalogue rows exactly as a recording does.

    The recorder asks the leak gate about the capture's scrubbed.har again
    before it derives anything from it, decides who does the cataloguing as it
    does after a recording, and will not replace a catalogue that already
    carries described actions -- move that file aside to catalogue afresh.

    Refused, by itself, on a capture that was never scrubbed or whose scrub the
    gate rejected. With -Scrub the scrub runs FIRST, whichever order the
    switches are typed in, and the catalogue runs only when the scrub produced
    a verified capture.

    The exit code is the recorder's own, passed on whole: 0, or 7 when the
    catalogue was produced over ADVISORY findings, or the code of whatever it
    refused.

.OUTPUTS
    WebApiDiscovery.HarCapture.CatalogueEntry

    Nothing, for -Scrub without -Catalogue.

.EXAMPLE
    Invoke-HarCapture https://example.com -Describe 'sign in, then sign out'

.EXAMPLE
    Invoke-HarCapture https://example.com -Describe 'browse the catalogue' |
        Where-Object Status -eq Observed

.EXAMPLE
    Invoke-HarCapture https://example.com -Describe 'create, edit and delete a post' |
        ConvertTo-Json -Depth 4

.EXAMPLE
    # A site whose bot protection refuses a Playwright-launched browser: record
    # in an ordinary Chrome instead. Stopping leaves that browser running.
    Invoke-HarCapture https://www.hertz.com -Describe 'search a one-way rental' -Browser Native

.EXAMPLE
    # Record in a Chrome you started yourself, on a dedicated profile:
    #   chrome --user-data-dir=C:\chrome-capture --remote-debugging-port=9222
    Invoke-HarCapture https://www.avis.com -Describe 'vehicle search' -Attach 9222

.EXAMPLE
    # Let an agent do the browsing. -Describe is the goal; the prompt, the
    # discovery checklist and the safety rules are generated.
    Invoke-HarCapture https://www.avis.com -AI `
        -Describe 'search DCA->IAD one-way, all classes, pay now vs pay later, rate details, extras'

.EXAMPLE
    # Signed into something sensitive: the agent gets the browser tools and no
    # built-in tools at all.
    Invoke-HarCapture https://admin.example.com -AI -AITools BrowserOnly `
        -Describe 'list users, open one, view its audit log'

.EXAMPLE
    # The AI cataloguing pass failed, or was never run. Catalogue the capture
    # that is already there -- nothing is recorded.
    Invoke-HarCapture https://example.com -Catalogue

.EXAMPLE
    # The scrubber improved: scrub the existing capture again, then rebuild its
    # catalogue from the new scrub.
    Invoke-HarCapture https://example.com -Scrub -Catalogue

.EXAMPLE
    # The site has been recorded more than once, so say which capture: by the
    # label the refusal lists, or by the description it was recorded with.
    Invoke-HarCapture example.com/2026-01-02-143000 -Scrub
    Invoke-HarCapture https://example.com -Describe 'create, then delete, a post' -Catalogue
#>

[CmdletBinding()]
[OutputType('WebApiDiscovery.HarCapture.CatalogueEntry')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidAssignmentToAutomaticVariable', 'Profile',
    Justification = 'Which signed-in identity to record as is the operator-facing name for this input, and $PROFILE is never read in this script. Renaming the parameter to satisfy the analyzer would make the surface worse to serve a shadowing that has no effect here.')]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Uri,

    [string]$OutputPath,

    # Required (#366), and DELIBERATELY NOT [Parameter(Mandatory)].
    #
    # Mandatory would make PowerShell PROMPT for it when a host is attached and
    # only hard-fail when one is not. That was considered and rejected: it gives
    # a human recording by hand a different failure from an agent driving the
    # same script, and #366 exists because the description is the one part of a
    # capture that cannot be reconstructed afterwards. The moment of refusal has
    # to look the same however you arrived at it -- which is also the only way
    # it can match what capture-har.js does, since the Node side has no prompt
    # to offer. So the check lives in the body, below, and terminates.
    #
    # Whitespace is empty, for the same reason it is on the Node side.
    #
    # Required to RECORD. With -Scrub or -Catalogue there is no recording to
    # describe, and a second reason Mandatory would have been wrong: there it
    # is an optional narrowing of which existing capture is meant.
    [string]$Describe,

    [string]$Profile,

    [switch]$Isolated,

    # NOT defaulted to 9333 here, and the difference is load-bearing: the
    # recorder has to be able to tell "the operator asked for a port" from "the
    # operator said nothing", because -Attach refuses the first and must accept
    # the second. A default on this parameter would make every -Attach run look
    # like a request for port 9333. $PSBoundParameters is the discriminator, and
    # the default lives where it always did -- DEFAULT_PORT in capture-har.js.
    [int]$Port,

    [ValidateSet('Playwright', 'Native')]
    [string]$Browser = 'Playwright',

    [string]$Attach,

    # WHO DRIVES (#98). The recorder owns every rule about it -- the implication
    # to -Browser Native, the exclusivity with -Isolated, and the refusal when no
    # agent is installed -- for the same reason this file forwards -Attach and
    # -Browser without re-stating their exclusivity: one owner decides, and a
    # second copy of the judgement here would be free to disagree with it.
    [switch]$AI,

    # WHICH TOOLS THE DRIVER HAS (#112). Forwarded as --ai-tools only when
    # TYPED, like -Browser, so the recorder -- which owns the rule, as it owns
    # -AI's -- refuses it without --ai.
    [ValidateSet('Auto', 'BrowserOnly')]
    [string]$AITools = 'Auto',

    # HOW FAST. Forwarded as --pace only when typed; the recorder owns the
    # default (human pace) and refuses a value it cannot read, so this file
    # validates nothing about it -- one owner, as for -Browser and -AI.
    [string]$Pace,

    # WHICH PHASES RUN, on a capture that already exists (#24).
    #
    # The owner's shape, and the reason it is two switches rather than one
    # -SkipCapture: "I'm not sure it is skip capture so much as -Catalogue
    # -Scrub, which both imply skip capture because it is already done." Each
    # names what WILL run, so there is no way to type a combination that means
    # "record nothing and do nothing".
    #
    # These two are the whole of what the feature adds. WHICH capture they act
    # on is said with what was already here -- the positional first argument,
    # and -Describe -- because a third parameter to carry a target would be a
    # new option, and those are an owner decision.
    [switch]$Scrub,

    [switch]$Catalogue
)

$ErrorActionPreference = 'Stop'

# Read by VALUE, never by presence in $PSBoundParameters: `-Scrub:$false` means
# off, and a run with both switches off is a recording exactly as it always was.
$reentering = [bool]$Scrub -or [bool]$Catalogue

# THE REFUSAL, before anything else runs and before any host is consulted.
#
# It is a terminating error rather than a prompt so that this front door and
# `capture-har.js` fail IDENTICALLY -- same reason, same example, same
# non-zero exit -- whether a person or an agent invoked it. Nothing here reads
# $Host, $PSCmdlet.MyInvocation, or whether a TTY is attached: a refusal that
# varied by how the script was launched would be a second behaviour to reason
# about, and the one the operator would hit least often is the one that would
# rot.
#
# A RECORDING needs it. -Scrub and -Catalogue record nothing: the capture they
# run on was described when it was made, and that description is on disk.
if (-not $reentering -and [string]::IsNullOrWhiteSpace($Describe)) {
    # The explanation goes to stderr as its own lines, and only then does the
    # terminating error fire. Throwing the whole paragraph instead would hand it
    # to PowerShell's error renderer, which reflows it into one run-on line and
    # takes the `Try:` example -- the actionable half -- with it. Node prints
    # clean lines; so does this.
    [Console]::Error.WriteLine((@(
        'Invoke-HarCapture: refusing to record without -Describe.'
        '  A capture nobody can identify is a capture nobody can use: the store is'
        '  shared and append-only, the directory name is a START time, and several'
        '  sessions record into it at once. The description is the only part of a'
        '  capture that cannot be reconstructed afterwards -- the bytes can be'
        '  re-captured, what you were doing cannot.'
        "  Try: -Describe 'example.com: create a post with two photos, then delete it'"
    ) -join [Environment]::NewLine))
    # EXIT 2, matching capture-har.js's usage-error code exactly.
    #
    # `throw` was the first shape here and it disagreed quietly: an uncaught
    # throw exits 1, so the two doors were both non-zero -- enough for any
    # caller testing truthiness -- while still not saying the same thing. "Fail
    # identically" is the whole reason this is a hard failure rather than a
    # prompt, and a code is part of what a failure says. Refusing an invocation
    # is a usage error at either door, so both answer 2.
    exit 2
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error 'node not found on PATH -- install Node.js >= 18 to record a capture.'
    return
}

$captureJs = Join-Path $PSScriptRoot 'capture-har.js'
if (-not (Test-Path -LiteralPath $captureJs)) {
    Write-Error "capture-har.js not found at $captureJs"
    return
}

# Status goes to the information stream, never to the pipeline: per
# powershell.instructions.md -> Output & Streams, chatter mixed into the output
# is what makes `... | ConvertTo-Json` return prose instead of data. It is also
# why there is no Write-Host here.
#
# The preference is set once rather than pinning `-InformationAction Continue`
# on every call. Pinning made the messages visible but also made them
# unsuppressable: the caller's own -InformationAction, the mechanism the
# convention points them at, did nothing. Honouring an explicit binding gives
# both -- on by default, and off when the caller says so.
if (-not $PSBoundParameters.ContainsKey('InformationAction')) {
    $InformationPreference = 'Continue'
}

# Which folder this capture's artifacts land in. Mirrors uriFolder() in
# capture-har.js -- HOST ONLY (never the path or query, which carry
# magic-link and reset tokens), periods kept, port joined with `_` because a
# dash is legal inside a hostname.
#
# Deliberately duplicated rather than shelled out to node: this script's whole
# observable contract is the arguments it hands the recorder, and a second node
# invocation would sit in the middle of that. The duplication is pinned instead
# -- har-recording.Tests.ps1 asserts this function and uriFolder() agree on a
# table of URIs, so the two cannot drift apart silently.
function Get-HarUriFolder {
    param([Parameter(Mandatory)][string]$Uri)

    $parsed = $null
    if (-not [uri]::TryCreate($Uri, [UriKind]::Absolute, [ref]$parsed)) { return $null }

    # Parsing is not enough: 'http://../evil' parses with a host of '..', and
    # file:/data:/about: URIs parse with none. Both are refused for the same
    # reasons capture-har.js refuses them -- the first would walk the capture
    # out of its directory, the second collapses every hostless capture into
    # one folder.
    $hostName = $parsed.Host
    if (-not $hostName -or $hostName -eq '.' -or $hostName -eq '..') { return $null }

    # .Host, NOT .IdnHost, is the wrong choice here: WHATWG punycodes an
    # international host and .Host does not, so a capture of a non-ASCII site
    # would be WRITTEN to xn--... and its catalogue LOOKED FOR under the
    # unicode name. .IdnHost matches the recorder, and already drops the
    # brackets from an IPv6 literal.
    $bare = $parsed.IdnHost -replace '^\[|\]$', ''

    # WHATWG folds an IPv4-mapped tail into hex groups (`::ffff:1.2.3.4` ->
    # `::ffff:102:304`); .NET keeps it dotted. Match the recorder.
    if ($bare -match '^(.*:)(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$') {
        $hi = ([int]$Matches[2] -shl 8) -bor [int]$Matches[3]
        $lo = ([int]$Matches[4] -shl 8) -bor [int]$Matches[5]
        $bare = '{0}{1:x}:{2:x}' -f $Matches[1], $hi, $lo
    }
    $bare = $bare -replace ':', '-'

    $folder = if ($parsed.IsDefaultPort) { $bare } else { "${bare}_$($parsed.Port)" }
    $folded = $folder.ToLowerInvariant() -replace '[^a-z0-9._-]', '_'

    # Reserved Windows device names cannot be directories.
    if ($folded -match '^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|_|$)') { return "${folded}_" }
    return $folded
}

# ---------------------------------------------------------------------------
# -Scrub / -Catalogue: ONE PHASE, ON A CAPTURE THAT ALREADY EXISTS (#24)
# ---------------------------------------------------------------------------
#
# The functions below are used only by the block that follows them, and that
# block ends in `exit`: a run that typed a phase switch never reaches the
# recording further down, and a recording never enters the block.
#
# WHAT THIS SECTION DOES NOT DO is the reason it is short. It does not scrub,
# gate, promote, quarantine, digest or catalogue. It answers ONE question that
# nothing else could -- which capture -- and then hands that capture's
# directory to the two re-entry points the pipeline already has.

<#
.SYNOPSIS
    Say why a phase run was refused: clean lines on stderr.

.DESCRIPTION
    The same shape as the -Describe refusal above, for the same reason: thrown
    instead, the lines would go through PowerShell's error renderer, which
    reflows them into one run-on sentence and takes the ready-to-paste command
    with it. The caller exits with the refusal's code.
#>
function Write-HarCaptureRefusal {
    param([Parameter(Mandatory)][object]$Refusal)

    [Console]::Error.WriteLine((@($Refusal.Line) -join [Environment]::NewLine))
}

<#
.SYNOPSIS
    Captures, one per line, as an operator can name them back: label, then the
    description they were recorded with.

.DESCRIPTION
    `host/stamp` is HarStoreBatch.psm1's answer to how a capture is named, not
    a second one, and it is exactly what this command accepts as its first
    argument -- so a line of this list can be pasted straight back.

    The description is the operator's own -Describe text, which Get-HarCapture
    and Remove-HarCapture already print. Nothing from inside a capture is: no
    URI the recording started from, no header, no entry.
#>
function Format-HarCaptureChoice {
    param([Parameter(Mandatory)][object[]]$Capture)

    $shown = 10
    $Capture | Select-Object -First $shown | ForEach-Object {
        $described = if ($_.describe) { $_.describe } else { '(no description)' }
        "    $(Get-HarCaptureLabel -Capture $_) -- $described"
    }
    if ($Capture.Count -gt $shown) {
        "    ... and $($Capture.Count - $shown) more -- Get-HarCapture lists them all"
    }
}

<#
.SYNOPSIS
    Every capture the first argument could mean.

.DESCRIPTION
    THREE SPELLINGS, ONE PARAMETER. A phase run has to say which capture, and
    the only things an operator has to say it with are the things the store
    already shows them:

      a URL          the site. Its captures are the ones in that site's folder,
                     derived by the same function a recording derives it with.
      a directory    the capture's own directory -- what Get-HarCapture reports
                     as Path -- or a folder holding captures.
      host/stamp     the label: Get-HarCapture's Site and Stamp joined by a
                     slash, which is how this command's own refusals name a
                     capture and what Remove-HarCapture takes.

    ASKED OF THE SHARED WALK, never of the file system from here. What a
    capture is, and which store this clone records into, each have exactly one
    definition (capture-store.js), and Get-HarCaptureInventory is how every
    PowerShell caller reaches it.

    `Named` is what a message may print about the request: the HOST for a URL,
    never the URL -- a magic-link or reset URL carries its token in the path or
    the query, and this is echoed to a console.
#>
function Find-HarCaptureCandidate {
    param(
        [Parameter(Mandatory)][string]$Spec,
        [Parameter(Mandatory)][string]$CaptureStoreJs
    )

    if (Test-Path -LiteralPath $Spec -PathType Container) {
        $folder = (Resolve-Path -LiteralPath $Spec).ProviderPath
        return [pscustomobject]@{
            Named = $folder
            Found = @(Get-HarCaptureInventory -Path $folder -CaptureStoreJs $CaptureStoreJs)
        }
    }

    $store = @(Get-HarCaptureInventory -CaptureStoreJs $CaptureStoreJs)
    $site = Get-HarUriFolder -Uri $Spec
    if ($site) {
        return [pscustomobject]@{ Named = $site; Found = @($store | Where-Object { $_.host -eq $site }) }
    }

    # A label is compared the way it is printed -- forward slashes -- and
    # reported the way it was typed.
    $label = $Spec.Replace('\', '/').Trim('/')
    return [pscustomobject]@{
        Named = "'$Spec'"
        Found = @($store | Where-Object { (Get-HarCaptureLabel -Capture $_) -eq $label })
    }
}

<#
.SYNOPSIS
    The one capture a phase run is about, or the refusal that says why there
    is not exactly one.

.DESCRIPTION
    NAMED, NEVER PICKED. A site recorded twice has two captures, and "the
    newest" is a guess: captures coexist, several sessions record into one
    store, and the recording further down already refuses to find its own
    session that way. So more than one match is refused by listing them, newest
    first, with the command that names one.

    -Describe NARROWS, when it was typed. The description is how one recording
    of a site is told from another, so the command that made a capture, plus a
    phase switch, names that capture. It is compared whole: a near miss is a
    different capture, not a match.

    Only RECORDER OUTPUT is a candidate -- asked as `recorderOutput`, the field
    the walk answers, never as a list of classes to exclude. A dump with no
    session.json has nothing to say whose traffic it holds.

    Returns `Capture`, or `Code` and `Line` for the refusal. 2 is this door's
    usage error (the invocation named several); 3 is the recorder's own code
    for "no capture to act on".
#>
function Resolve-HarCaptureSelection {
    param(
        [Parameter(Mandatory)][string]$Spec,
        [AllowEmptyString()][string]$Describe,
        # The phase switches as they were typed, for the command a refusal
        # offers back.
        [Parameter(Mandatory)][string]$PhaseSwitch,
        [Parameter(Mandatory)][string]$CaptureStoreJs
    )

    $candidate = Find-HarCaptureCandidate -Spec $Spec -CaptureStoreJs $CaptureStoreJs
    $named = $candidate.Named
    if (-not $candidate.Found.Count) {
        $lines = @(
            "Invoke-HarCapture: no capture found for $named."
            '  -Scrub and -Catalogue run on a capture that already exists. Get-HarCapture lists'
            "  the ones in this clone's store; to record a new one, run this without -Scrub or"
            '  -Catalogue.')
        if (Test-Path -LiteralPath $Spec -PathType Leaf) {
            $lines += "  $Spec is a file: name the capture's directory instead."
        }
        return [pscustomobject]@{ Capture = $null; Code = 3; Line = $lines }
    }

    $ours = @($candidate.Found | Where-Object { $_.recorderOutput } | Sort-Object -Property stamp -Descending)
    if (-not $ours.Count) {
        $declined = $candidate.Found[0]
        return [pscustomobject]@{ Capture = $null; Code = 3; Line = @(
                "Invoke-HarCapture: $(Get-HarCaptureLabel -Capture $declined) was not made by this recorder ($($declined.reason))."
                '  Nothing says whose traffic it holds, so it is never scrubbed or catalogued here --'
                '  the same reason the folder drivers decline it.') }
    }

    $pool = $ours
    if (-not [string]::IsNullOrWhiteSpace($Describe)) {
        $pool = @($ours | Where-Object { ([string]$_.describe).Trim() -eq $Describe.Trim() })
        if (-not $pool.Count) {
            return [pscustomobject]@{ Capture = $null; Code = 3; Line = @(
                    "Invoke-HarCapture: $($ours.Count) capture(s) match $named, but none is described exactly '$($Describe.Trim())'."
                    Format-HarCaptureChoice -Capture $ours
                    '  With -Scrub or -Catalogue, -Describe narrows to the capture recorded with that'
                    '  description. Drop it, or name the capture by its label.') }
        }
    }

    if ($pool.Count -gt 1) {
        return [pscustomobject]@{ Capture = $null; Code = 2; Line = @(
                "Invoke-HarCapture: $($pool.Count) captures match $named -- say which one. Nothing was run."
                Format-HarCaptureChoice -Capture $pool
                '  Name it by its label, or by its directory:'
                "    Invoke-HarCapture $(Get-HarCaptureLabel -Capture $pool[0]) $PhaseSwitch"
                "    Invoke-HarCapture '$($pool[0].dir)' $PhaseSwitch"
                "  -Describe '<text>' also narrows to the capture recorded with exactly that description.") }
    }

    return [pscustomobject]@{ Capture = $pool[0]; Code = 0; Line = @() }
}

<#
.SYNOPSIS
    Why this capture cannot go through the phases that were asked for, or
    nothing when it can.

.DESCRIPTION
    Each phase needs what the one before it produces, and the walk has already
    said what is there.

    The scrub needs the raw. With both switches that is the only question: the
    scrub is what produces the scrubbed capture the catalogue is derived from.

    The catalogue ALONE needs a scrubbed capture that passed the gate. The
    recorder refuses an unscrubbed one too -- it asks the gate again, whatever
    this says -- but it can only say "nothing to catalogue". Here the refusal
    can name the way through, which is this same command with -Scrub added.
#>
function Get-HarCapturePhaseBlocker {
    param(
        [Parameter(Mandatory)][object]$Capture,
        [Parameter(Mandatory)][bool]$Scrubbing
    )

    $label = Get-HarCaptureLabel -Capture $Capture
    if ($Scrubbing) {
        if ($Capture.raw) { return $null }
        return [pscustomobject]@{ Code = 3; Line = @(
                "Invoke-HarCapture: $label has no raw.har, so there is nothing to scrub.") }
    }
    if ($Capture.scrubbed) { return $null }
    if ($Capture.rejected) {
        return [pscustomobject]@{ Code = 3; Line = @(
                "Invoke-HarCapture: $label has no scrubbed.har -- its last scrub was REJECTED by the leak gate"
                '  and quarantined beside the raw. Triage the findings there, then run this with'
                '  -Scrub -Catalogue.') }
    }
    return [pscustomobject]@{ Code = 3; Line = @(
            "Invoke-HarCapture: $label has not been scrubbed -- there is no scrubbed.har to catalogue."
            '  A catalogue is only ever derived from a scrubbed, verified capture. To do both, run'
            '  this with -Scrub -Catalogue.') }
}

<#
.SYNOPSIS
    Did the recorder itself sign in for this capture, with values nothing but
    that recorder ever held?

.DESCRIPTION
    A recording that fills a sign-in form hands the values it filled to the
    scrub and to the leak gate over stdin, so they are removed under whatever
    name the site used and then PROVED absent (#115). They exist only in that
    recorder's memory -- not in session.json, not on disk, by design -- so a
    scrub run afterwards cannot be given them. It still removes them by field
    name; it cannot prove them gone.

    `postProcess` in capture-har.js owns that rule, and says exactly this when
    it scrubs for a recorder that has already exited. This asks the same
    question of the same field so that re-scrubbing through this door is not
    the one place it goes unsaid. `none` -- a form was met and the operator
    signed in by hand -- filled nothing, so there is nothing to be missing.

    session.json is read here because the shared walk does not carry this
    field. Anything unreadable about it answers no: this decides whether to
    add a caution, never whether to scrub.
#>
function Test-HarCaptureSignInValue {
    param([Parameter(Mandatory)][string]$Directory)

    try {
        $session = Get-Content -LiteralPath (Join-Path $Directory 'session.json') -Raw | ConvertFrom-Json
        return [string]$session.signIn.source -in 'bitwarden', 'prompt'
    }
    catch { return $false }
}

if ($reentering) {
    $phases = @(if ($Scrub) { 'Scrub' }; if ($Catalogue) { 'Catalogue' })

    # REFUSED, NOT QUIETLY IGNORED. Every one of these says how to RECORD --
    # which browser, which identity, who drives, how fast, where a new
    # capture's artifacts go. A phase run records nothing, so accepting one
    # would let an operator believe it had done something. A switch is read by
    # value here too: `-AI:$false` asked for nothing.
    $recordingOnly = foreach ($name in 'OutputPath', 'Profile', 'Isolated', 'Port', 'Browser', 'Attach', 'AI', 'AITools', 'Pace') {
        if (-not $PSBoundParameters.ContainsKey($name)) { continue }
        $value = $PSBoundParameters[$name]
        if ($value -is [switch] -and -not $value) { continue }
        "-$name"
    }
    if ($recordingOnly) {
        Write-HarCaptureRefusal -Refusal @{ Line = @(
                "Invoke-HarCapture: $($recordingOnly -join ', ') cannot be combined with -Scrub or -Catalogue."
                '  Those switches run on a capture that already exists and record nothing, so there is'
                '  no browser, no recording and no new output directory for a recording option to apply'
                "  to. The phases read and write the capture's own directory under .har-captures/.") }
        exit 2
    }

    Import-Module (Join-Path $PSScriptRoot '..' 'har' 'HarStoreBatch.psm1') -Force

    $selection = Resolve-HarCaptureSelection -Spec $Uri -Describe $Describe `
        -PhaseSwitch (($phases | ForEach-Object { "-$_" }) -join ' ') `
        -CaptureStoreJs (Join-Path $PSScriptRoot 'capture-store.js')
    if (-not $selection.Capture) {
        Write-HarCaptureRefusal -Refusal $selection
        exit $selection.Code
    }
    $capture = $selection.Capture
    $label = Get-HarCaptureLabel -Capture $capture

    # WHICH ONE, said before anything runs -- and before the state check, so a
    # refusal about a capture's state follows the line that says which capture
    # it is about.
    $described = if ($capture.describe) { $capture.describe } else { '(no description)' }
    Write-Information "Invoke-HarCapture: selected $label -- $described"
    Write-Information "  at $($capture.dir)"

    $blocker = Get-HarCapturePhaseBlocker -Capture $capture -Scrubbing ([bool]$Scrub)
    if ($blocker) {
        Write-HarCaptureRefusal -Refusal $blocker
        exit $blocker.Code
    }

    Write-Information ("  running $(($phases | ForEach-Object { "the $($_.ToLowerInvariant())" }) -join ', then '). " +
        'Nothing is recorded.')

    if ($Scrub) {
        # Said BEFORE the scrub, and on the warning stream: it is about what
        # this scrub cannot check, so it belongs ahead of a summary that will
        # otherwise read as the whole verdict.
        if (Test-HarCaptureSignInValue -Directory $capture.dir) {
            Write-Warning ("$label signed in while it was recorded, and the values it filled were held " +
                'only by the recorder that made it. This scrub removes them by field name and cannot ' +
                'prove them gone, as the scrub at recording time did -- check the new scrubbed.har for ' +
                'them before sharing it.')
        }

        # THE FOLDER FORM OF THE SCRUB WRAPPER, pointed at this one capture.
        #
        # Not its file form (-InputHar raw -OutputHar scrubbed). That one writes
        # the scrub straight to its destination before the gate has judged it,
        # which is why composing it here was refused in the first place (see
        # the note above the recording, below). The folder form scrubs under a
        # temporary name, calls it scrubbed.har only once the gate has passed
        # it, and quarantines a refusal beside the raw -- so promotion,
        # quarantine and the verdict stay where they are implemented, and this
        # file restates none of them.
        #
        # -Force, because the operator asked for THIS capture to be scrubbed
        # again: without it a capture a recording already scrubbed would answer
        # "already scrubbed -- use -Force", naming a switch this door does not
        # have. It replaces the derived scrubbed.har and never touches a raw.
        #
        # -WhatIf:$false, because that wrapper supports ShouldProcess and this
        # command does not: a $WhatIfPreference set in an outer scope would
        # otherwise turn the phase into a run that reports and writes nothing.
        #
        # -InformationAction is handed across because the wrapper turns its own
        # status lines on unless told otherwise; the caller's choice here has to
        # reach it. Out-Host keeps anything it emits off this command's success
        # stream, whose only content is catalogue rows.
        & (Join-Path $PSScriptRoot '..' 'har' 'Invoke-SanitizeHar.ps1') -InputHar $capture.dir -Force -WhatIf:$false -InformationAction $InformationPreference | Out-Host
        $scrubExit = $LASTEXITCODE
        if ($scrubExit -ne 0) {
            # The wrapper's summary, printed just above, says what happened and
            # where a refused scrub was quarantined. Its code is passed on
            # whole; the one thing added is what this run did NOT go on to do.
            Write-Warning ("The scrub did not produce a verified scrubbed.har for $label -- its summary " +
                'above says why. The raw capture is untouched.' +
                $(if ($Catalogue) { ' Nothing was catalogued.' }))
            exit $scrubExit
        }
        if (-not $Catalogue -and ($capture.digest -or $capture.catalogue)) {
            # Nothing else would say it: the digest and catalogue already in
            # that directory describe the scrub this run just replaced.
            Write-Information ('  digest.json and catalogue.json in that directory were derived from the ' +
                'previous scrub. Run this with -Catalogue to rebuild them.')
        }
    }

    if (-not $Catalogue) { exit 0 }

    # THE RECORDER'S OWN `catalogue` COMMAND, the pipeline's second door.
    #
    # --output-path is the capture's own directory, said explicitly. Left out,
    # the recorder reads the output path the capture was RECORDED with -- and
    # for a capture recorded with -OutputPath that is a published copy
    # somewhere else, not the scrubbed.har a -Scrub just wrote here. The phases
    # work on the capture's directory, as the folder drivers do.
    #
    # Everything else is the recorder's: it asks the leak gate again, builds
    # the digest, decides who catalogues, and refuses to replace a catalogue
    # that carries described actions. Its stderr is left alone, so its table,
    # its warnings and its refusals arrive in its own words.
    $catalogueArgs = @('catalogue', $capture.dir, '--output-path', $capture.dir)
    if ($VerbosePreference -ne 'SilentlyContinue') { $catalogueArgs += @('--log-level', 'verbose') }
    Write-Verbose "capture-har.js $($catalogueArgs -join ' ')"
    & node $captureJs @catalogueArgs | Out-Host
    $catalogueExit = $LASTEXITCODE
    Write-Verbose "capture-har.js exited $catalogueExit"

    # The recorder's codes are passed on whole. 0 and 7 both mean a catalogue
    # was produced -- 7 over ADVISORY findings, non-zero so nothing reads the
    # run as clean. Any other code has already been explained by the recorder,
    # and a second, vaguer sentence about it from here would only blur that.
    if ($catalogueExit -notin 0, 7) { exit $catalogueExit }
    if ($catalogueExit -eq 7) {
        Write-Warning ('Catalogued, but the leak gate reported ADVISORY findings. The recorder printed ' +
            'each finding and the path to the full report -- review them, then waive or correct each one.')
    }

    # The DATA, as a recording returns it: typed rows on the success stream, so
    # `... -Catalogue | Where-Object Status -eq Observed` composes the same way.
    $cataloguePath = Join-Path $capture.dir 'catalogue.json'
    if (Test-Path -LiteralPath $cataloguePath) {
        & (Join-Path $PSScriptRoot 'ConvertFrom-HarCatalogue.ps1') -Path $cataloguePath
    }
    else {
        Write-Warning "no catalogue was written to $cataloguePath"
    }
    exit $catalogueExit
}

$uriFolder = Get-HarUriFolder -Uri $Uri
if (-not $uriFolder) {
    Write-Error "cannot derive a capture folder from '$Uri' -- it must be a URL, e.g. https://example.com"
    return
}

# WHERE THE OUTPUT WILL LAND is decided by the RECORDER, not here (#471).
#
# This used to probe the checkout and warn before spawning node, then set
# HARCAPTURE_PLACEMENT_GUARD_RAN so the recorder stayed quiet and the operator
# was told once rather than twice.
#
# The question it was asking stopped being answerable from here. Since #377 the
# default output is the run's own session directory under the gitignored
# captures root, so being on the protected branch no longer implies this run
# will leave anything committable in the work tree -- only an explicitly named
# `-OutputPath` can do that, and whether THAT path is gitignored is a question
# about a destination this script never resolves. The recorder resolves it, so
# the recorder decides; see placementForRun in capture-har.js.
#
# Guessing from here would be a second answer free to disagree with the first,
# which is the failure this guard was extracted into a shared library to avoid.
# The handshake env var goes with it: with one owner there is no second warning
# to suppress.

# WHY THIS STILL HANDS THE WHOLE PIPELINE TO THE RECORDER, and does not call
# Invoke-SanitizeHar.ps1 and Invoke-HarCatalogue.ps1 in turn (#352).
#
# Re-entry was the ask, and re-entry now exists: the scrub stage has been
# re-runnable through Invoke-SanitizeHar.ps1 for some time, and the catalogue
# stage is re-runnable through Invoke-HarCatalogue.ps1 as of this change. An
# operator can enter the pipeline at either one against a capture recorded some
# other time. What was NOT done is rewriting this front door as a sequence of
# those two wrappers, because doing so loses invariants that #343 exists to
# hold, and the issue says to stop rather than lose them:
#
#   1. `capture-har.js start` records AND post-processes in one process. There
#      is no way to ask it for the recording alone, and adding one would be a
#      new command-line option -- which needs approval, not initiative.
#   2. The scrub stage inside postProcess is not "run sanitize-har, then
#      verify-scrub". It writes the candidate into the gitignored session
#      directory, promotes it to the output path by atomic rename ONLY after
#      the gate passes, and quarantines it as scrubbed.rejected.har when the
#      gate refuses. Invoke-SanitizeHar.ps1 does none of that -- it is a
#      general-purpose HAR scrubber -- so a composed front door would have to
#      write an unjudged file straight into the committable output path and
#      then clean up, which is precisely the window #343 closed.
#   3. Promotion and quarantine would then live in PowerShell, and the exit
#      3 / 4 -> 6 / 7 mapping with them. Three of the four invariants restated
#      on this side of the process boundary, drifting from the Node ones.
#
# So the composition is left undone deliberately, and A RECORDING goes on
# delegating whole.
#
# WHAT -Scrub AND -Catalogue CHANGED, AND WHAT THEY DID NOT (#24). They re-enter
# the pipeline for a capture that already exists -- the section above -- and
# they do it through those same two entry points. That is not the composition
# refused here, and the difference is point 2. The scrub wrapper's FOLDER form
# arrived with #386, after this note was written: it scrubs under a temporary
# name, calls the result scrubbed.har only once the gate has passed it, and
# quarantines a refusal. Called that way the wrapper carries those invariants
# itself, so they are still not restated in this file; its FILE form, which
# carries none of them, is still never called from here.
#
# What that leaves short of postProcess's phase A is narrow and known. The
# folder form writes into the capture's own directory rather than publishing to
# the output path a capture was recorded with, and it answers 0 or 1 rather
# than 0, 6 or 7. The honest shape for closing that is still a `scrub` command
# on capture-har.js exposing phase A whole -- another entry point to existing
# code, the same move the `catalogue` command makes -- and it is still new
# command-line surface, which needs approval, not initiative.
$captureArgs = @('start', '--uri', $Uri)
if ($PSBoundParameters.ContainsKey('Port')) { $captureArgs += @('--port', $Port) }
if ($OutputPath) { $captureArgs += @('--output-path', $OutputPath) }
$captureArgs += @('--describe', $Describe)
if ($Profile) { $captureArgs += @('--profile', $Profile) }
if ($Isolated) { $captureArgs += '--isolated' }
# THE RECORDER OWNS THE EXCLUSIVITY RULES, and this script deliberately
# re-states none of them (#73).
#
# -Attach with -Port, with -Profile, with -Isolated, or with -Browser Playwright
# are all unanswerable combinations, and all four are refused -- by
# capture-har.js, with the reason, at exit 2. Checking here as well would be a
# second copy of the same judgement, free to disagree with the first the day one
# of them changes; this file already has that argument on record about the
# placement guard (#471) and settles it the same way. So the flags are forwarded
# and the one owner answers.
#
# -Browser is forwarded only when it was TYPED. `--browser playwright` is refused
# alongside --attach, so forwarding the default would turn `-Attach x` into that
# refusal for an operator who never named a browser. But the test is the
# binding, not "differs from the default": a typed `-Browser Playwright` with
# -AI is a refusal too (#98), and comparing against the default dropped it, so
# the run went ahead in the native browser the operator had just declined.
if ($PSBoundParameters.ContainsKey('Browser')) { $captureArgs += @('--browser', $Browser.ToLowerInvariant()) }
if ($Attach) { $captureArgs += @('--attach', $Attach) }
# Read by VALUE, never by presence in $PSBoundParameters: `-AI:$false` means off,
# not "the switch was mentioned" -- the same hazard -Verbose:$false has below.
if ($AI) { $captureArgs += '--ai' }
if ($PSBoundParameters.ContainsKey('AITools')) {
    $captureArgs += @('--ai-tools', $(if ($AITools -eq 'BrowserOnly') { 'browser-only' } else { 'auto' }))
}
if ($PSBoundParameters.ContainsKey('Pace')) { $captureArgs += @('--pace', $Pace) }
# -Verbose is the only verbosity switch. capture-har.js prints its own console
# output directly -- re-streaming it through PowerShell would buffer the ENTER
# prompt and the Ctrl+C question in the very terminal the operator answers --
# so the level is handed across instead of a second switch being invented.
if ($VerbosePreference -ne 'SilentlyContinue') { $captureArgs += @('--log-level', 'verbose') }

Write-Verbose "capture-har.js $($captureArgs -join ' ')"
Write-Information 'Recording. Browse, then press ENTER in the recorder terminal -- that writes the most complete HAR.'

# STDOUT IS CAPTURED, stderr is not (#377).
#
# The recorder writes one line of JSON to stdout when a run completes, naming
# the session directory and the catalogue it wrote. That handoff exists because
# the default output path now carries the run's STAMP, so this script can no
# longer rebuild it from an anchoring rule -- and the alternative, globbing
# .har-captures/ for the newest session, is what the comment below refuses.
#
# Every human-facing line the recorder emits -- the banner, the ENTER prompt,
# the Ctrl+C question, the warnings, the catalogue table -- goes to stderr, so
# capturing stdout leaves the interactive experience untouched.
$recorderStdout = & node $captureJs @captureArgs
$exit = $LASTEXITCODE
Write-Verbose "capture-har.js exited $exit"

# Exit codes are documented on capture-har.js. 5 means raw.har was assembled
# from the incremental log rather than recordHar -- a full capture either way,
# so it is not a failure. It is also unreachable under -Browser Native and
# -Attach, where there is no recordHar to lose: the recorder does not report a
# design as a fallback, so the advice in that arm cannot be handed to a run that
# could not have taken the other path (#73). 6 means the recording is fine but a post-process
# phase is not, which must not be reported as success. 7 means every artifact
# was produced but the leak gate reported advisory findings -- non-zero so no
# caller reads it as clean, and handled here so the `default` arm below does
# not tell the operator no catalogue exists when one does.
switch ($exit) {
    0 { }
    5 { Write-Information 'Recording ended by closing the window; press ENTER next time for a slightly richer HAR.' }
    6 { Write-Warning 'Recorded successfully, but scrub or catalogue failed. The raw capture is intact.' }
    7 {
        # 7 is not a failure: everything was produced. The leak gate reported
        # identity findings that rest on SHAPE alone, which carries no
        # provenance -- a Luhn-valid digit run is a card, an object id, or
        # nothing. Withholding the catalogue over that is what this issue
        # exists to stop, so the run continues and the operator triages the
        # findings report the recorder just named.
        #
        # It names no location. The report USUALLY sits beside the artifact,
        # but when publishing it fails -- a locked file, a full disk -- it stays
        # in the session directory instead and the recorder says so. An arm
        # that asserted the usual place would send the operator looking for a
        # file that is not there, in exactly the case they most need to read it.
        #
        # And the loop closes without re-recording. Waiving a false positive
        # used to leave the operator with "run the capture again", which for a
        # session a human drove by hand is not a repeat of anything.
        # Invoke-HarCatalogue.ps1 re-enters at the catalogue stage instead
        # (#352); it is named here rather than implemented here.
        Write-Warning ('Recorded and catalogued, but the leak gate reported ADVISORY findings. ' +
            'The recorder printed each finding and the path to the full report -- review ' +
            'them, then waive or correct each one. To rebuild the catalogue afterwards ' +
            'without re-recording, run Invoke-HarCatalogue.ps1.')
    }
    2 {
        # A USAGE ERROR KEEPS ITS CODE, and keeps the recorder's own words.
        #
        # This file already holds the rule, for -Describe: "the front door and
        # the recorder must refuse on identical terms -- same reason, same
        # example, same exit code", and har-capture's exit 2 is that refusal
        # arriving from the other side of the process boundary. The `default`
        # arm below would re-wrap it as a Write-Error and exit 1: a second,
        # vaguer sentence about a refusal that has already explained itself,
        # under a code that says something else.
        #
        # It went unnoticed while exit 2 was unreachable through this script's
        # own parameters. #73 made it reachable -- `-Browser Native -Isolated`
        # and the three other unanswerable -Attach combinations are refused by
        # the recorder, by design, because one owner decides them (see the
        # forwarding block above). Delegating the judgement is only honest if the
        # answer survives the trip back.
        exit 2
    }
    8 {
        # SIGN-IN NEEDED KEEPS ITS CODE (#115), for the same reason 2 does: the
        # caller is usually not a person. An agent started this run with no
        # terminal -- from a phone, a schedule -- and the code is how it tells
        # the person "sign in once" rather than "it broke".
        Write-Warning ('Sign-in needed, and nobody was at a terminal to start one. Run ' +
            'Invoke-HarCapture once from a terminal: Bitwarden (or a masked prompt) signs in ' +
            'and the session is saved, so unattended runs after it go straight through.')
        exit 8
    }
    default {
        Write-Error "capture-har exited $exit -- no catalogue was produced."
        return
    }
}

# The catalogue is read from THIS invocation's own report, and never by
# globbing .har-captures/ for the newest session directory.
#
# That glob looks equivalent and is not: captures coexist happily on different
# ports, so a second capture started in another terminal and finished first
# would be the lexicographically-last directory, and this run would emit a
# different site's catalogue as though it were its own.
#
# So the recorder says which one is its own. The default output path is now the
# run's stamped session directory (#377), which this script cannot compute, and
# the -OutputPath fallback below is only for a run that ended before the
# recorder could report -- a cancel, or a recording nothing was written for.
$cataloguePath = $null
$pauses = $null
$identity = $null
foreach ($line in @($recorderStdout)) {
    if ($line -isnot [string] -or -not $line.TrimStart().StartsWith('{')) { continue }
    try { $report = $line | ConvertFrom-Json } catch { continue }
    if ($report.cataloguePath) { $cataloguePath = $report.cataloguePath }
    if ($report.PSObject.Properties['pauses']) { $pauses = $report.pauses }
    if ($report.PSObject.Properties['captureId']) { $identity = $report }
}

# WHICH CAPTURE THIS WAS, AND WHICH RUN IT BELONGS TO (#26). The store is shared
# and several sessions record into it at once, so a session selects its own
# captures by run -- and it can only do that if it is told its run by its own
# recording. Picking it out of a listing by description or time is the guess the
# run exists to replace. The recorder's line is swallowed above, so it is said
# here. A recorder that reports neither says nothing.
if ($identity -and $identity.captureId) {
    $runText = if ($identity.runId) {
        "in run $($identity.runId). ``Get-HarCapture`` shows the run of every capture in the store."
    }
    else {
        'and no run could be identified for it -- see `run` in session.json for why.'
    }
    Write-Information "This capture is $($identity.captureId), $runText"
}

# THE PAUSES, SUMMED UP (owner request: "communicate that back"). Each pause
# and hand-over was already said live on stderr as it happened -- "Paused
# until 10:42 -- ...", "Resumed after 5 min", "Handed over to you: ..." -- and
# is recorded in session.json under `pauses`. This line is for whoever reads
# only the end: a caller capturing this command, or an AI session.
if ($pauses -and (($pauses.count -gt 0) -or ($pauses.handOvers -gt 0))) {
    $minutes = [Math]::Round(([double]$pauses.totalMs) / 60000, 1)
    Write-Information ("The capture paused $($pauses.count) time(s) and was handed over " +
        "$($pauses.handOvers) time(s), $minutes min in all -- see ``pauses`` in session.json.")
}
if (-not $cataloguePath -and $OutputPath) {
    $cataloguePath = Join-Path (Join-Path $OutputPath $uriFolder) 'catalogue.json'
}
if (-not $cataloguePath) {
    Write-Warning 'the recorder reported no catalogue for this run.'
    return
}
Write-Verbose "catalogue: $cataloguePath"

# NO closing notice here, deliberately (#300). The recorder emits it, because
# the recorder is the process that actually wrote the files and prints it
# in-process. Printing it from here as well would duplicate it; printing it ONLY
# from here would make it depend on this process surviving long enough to reach
# its own epilogue -- and a notice that goes missing exactly when something went
# wrong is not a safety net. The opening warning is the reverse case and is
# owned here, because it is printed before the recorder exists.
if (-not (Test-Path -LiteralPath $cataloguePath)) {
    Write-Warning "no catalogue was written to $cataloguePath"
    return
}

$rows = @(& (Join-Path $PSScriptRoot 'ConvertFrom-HarCatalogue.ps1') -Path $cataloguePath)

# NO SECOND RENDERING OF THE CATALOGUE HERE (#377), deliberately, and for the
# reason this script already gives about the closing notice: the recorder is the
# process that wrote the files, it prints the table in-process, and a table
# printed only from here would depend on this process surviving long enough to
# reach its own epilogue.
#
# So the recorder owns the human-readable table AND the "still a scaffold"
# notice, and this script adds neither. What it still owes its callers is the
# DATA: $rows below is the documented output of this command
# (`Invoke-HarCapture ... | Where-Object Status -eq Observed`), typed objects on
# the success stream rather than a second copy of the text the operator has
# already read.

$rows
