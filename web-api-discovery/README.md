# web-api-discovery

Record a website session, scrub it of credentials and personal data, and turn
the traffic into a reviewable API reference. Optionally, generate a complete,
buildable .NET wrapper from it: typed client, PowerShell module, MCP server,
tests and secret-scanning gates.

It ships as an **AI agent skill** in the open [Agent Skills](https://agentskills.io)
format, so Claude Code, GitHub Copilot, OpenAI Codex and Gemini CLI can all
use it. It also ships as **PowerShell commands** you can run yourself. It works
in any folder, including an empty one.

## What it does

Many sites have an API behind their pages but publish no description of it.
The skill works that API out from the site's own traffic, in two stages. The
first always runs and is a complete result on its own. The second is optional,
for when you want a client.

![The two stages of the web-api-discovery skill. Stage 1 always runs: a target URL and a one-line purpose go through Discover, Record the session, Scrub and verify, and Catalogue, and produce an API reference. Recording and scrubbing happen inside a private capture store that is never committed. Stage 2 is optional: Confirm scope, Classify auth, Group endpoints, Generate code, and Tests and security gates produce a buildable .NET project with a typed C# client, a PowerShell module and an MCP server.](docs/web-api-discovery-overview.png)

### Stage 1: capture → API reference (always runs)

You provide the site's URL and one sentence saying what the recording is for
(for example, "sign in, create a post, then delete it"). That is all the skill
needs to start recording: no project name, namespace or tracking issue.

1. **Discover.** The skill checks that the site responds and looks for its
   sign-in provider (Google, Microsoft or Facebook, for example).
2. **Record.** Chrome opens and, signed in as a dedicated test account (never
   your own), you do the things worth documenting. Alternatively, with `-AI`,
   an agent browses for you and is told to keep a human pace (1.5 to 4 seconds
   between page loads, at most 12 a minute).
3. **Scrub and verify.** Tokens, cookies and personal data are replaced with
   placeholders. A separate verification pass then checks the result, and the
   capture goes no further unless it passes.
4. **Catalogue.** An AI splits the session into the actions a person took,
   names each one and says what it shows (the part a recording cannot say about
   itself). It also fills in each action's methods, endpoints and request
   bodies, and the tooling recounts those from the recording and rejects any
   row that disagrees.

The result is a scrubbed API reference you can review and commit. The raw
recording, however, carries live session cookies, so it stays in a private
capture store that git ignores. Only scrubbed, verified data leaves it.

### Stage 2: generate a .NET wrapper (optional)

Before generating anything, the skill confirms the project name, namespace and
output folder; the auth model (or leaves it to the detector); whether the site
has a mobile app worth including; and a GitHub issue that describes the scope.
Then it:

1. **Classifies auth** from the traffic: cookie, cookie plus CSRF token, bearer
   token, SSO (Google, Microsoft or Facebook) or OAuth with PKCE.
2. **Groups endpoints** by method and path template, merges the response shapes
   it saw, and works out which query parameters are constants and which a
   caller chooses.
3. **Generates code**: a .NET solution with a typed C# client, a PowerShell
   module and an MCP server, plus sign-in and a README. Re-running it updates
   only the generated files, so code you add alongside them survives.
4. **Adds tests and security gates**: unit, functional and Pester tests, plus
   gitleaks secret scanning in a pre-commit hook and in CI.

Finally, it initializes the project as its own git repository.

**Throughout**, the skill requires you to record as a dedicated test account.
The scrub is a safeguard, not a guarantee, so a test account limits the worst
case to disclosing fake data. The skill uses no stealth measures and solves no
CAPTCHAs; when a site blocks or challenges, it hands the browser to you. It
honours the site's throttling (see
[Pacing and throttling](#pacing-and-throttling)) and never invents an endpoint
the recording didn't show. To keep secrets out of commits, raw captures are
git-ignored, and the generated project runs gitleaks before each commit (once
you switch its hook on) and in CI.

The diagram is also available as an editable PowerPoint slide:
[`docs/web-api-discovery-overview.pptx`](docs/web-api-discovery-overview.pptx).

## Install

Requirements: PowerShell 7, git, Node.js 20+, Playwright
(`npm install -g playwright`), and Google Chrome.

```powershell
git clone https://github.com/IntelliTect-dev/Multiplex C:\Git\Multiplex
C:\Git\Multiplex\web-api-discovery\Install.ps1
```

The installer links, and never copies, from your clone:

| Where | What it enables |
|---|---|
| `~/.claude/skills/web-api-discovery` | the skill in Claude Code |
| `~/.agents/skills/web-api-discovery` | the skill in GitHub Copilot, OpenAI Codex and Gemini CLI |
| a managed block in your PowerShell profile | `Invoke-HarCapture` and the other commands, in every new terminal |
| `%LOCALAPPDATA%\web-api-discovery\clone.path` | where this machine's clone is |

It links only for agents that are installed, and it reports any missing
prerequisites. It is safe to run again.

**The profile block is the same on every machine.** Your profile usually roams
(through OneDrive, say) and your clones do not — they sit at `C:\Git` on one
machine and `D:\Git` on another — so the block names no paths at all. It finds
the clone at load time from `$env:WEBAPIDISCOVERY_CLONE`, then this machine's
`clone.path`, then `Multiplex/web-api-discovery` or `web-api-discovery` under `Git`, `source\repos`, `repos` or
`src` off your home folder or any fixed drive. A clone in a conventional place
therefore works even on a machine where you have never run the installer. To
**update**, pull:

```powershell
git -C C:\Git\Multiplex pull
```

## Use

Ask your agent to "record the network traffic of example.com" or "discover the
API behind example.com", and the skill takes it from there. Or run it yourself:

```powershell
mkdir C:\Captures\example; cd C:\Captures\example
Invoke-HarCapture https://example.com -Describe 'sign in, create a post, then delete it'
```

A browser opens. Do the things worth documenting, then press ENTER. The
capture is scrubbed and verified, and a catalogue of the endpoints it saw is
printed.

To see what you have recorded, and to remove one recording:

```powershell
Get-HarCapture                                                 # the whole store
Remove-HarCapture example.com/2026-01-02-143000 -WhatIf        # say it, do nothing
Remove-HarCapture example.com/2026-01-02-143000                # ask, then delete
```

Nothing deletes a recording on its own -- there is no age sweep, no size cap
and no bulk delete. A raw capture is a browser session somebody drove by hand,
so every one is kept until you remove that one deliberately.

The store is shared, so each recording also says **which run made it**: the
`Run` column, the same for everything one terminal or one agent session
recorded and different for anyone else's. `Invoke-HarCapture` tells you your
run when a recording ends, and
`Get-HarCapture | Where-Object { $_.RunId -and $_.RunId -eq $run }` lists it.

**In a folder that isn't a git repository**, the recorder first asks whether
to create one (`[Y/n]`). A repository plus the `.gitignore` the recorder writes
keeps raw captures and the scrub's substitution tables, which can reverse the
scrub, out of any commit. If you answer no, it records anyway and warns you.
Don't capture inside Dropbox, OneDrive or any other synced folder: raw
captures carry live session cookies.

## Pacing and throttling

**Where the line is.** Polite pacing and honouring a site's throttling are in.
Anything whose purpose is hiding automation from a site's bot detection is out:
no user-agent or fingerprint spoofing, no `navigator.webdriver` or stealth
patches, no CAPTCHA solving. When a site blocks or challenges, the tooling
stops and hands the browser -- which is on screen -- to you, rather than
working around it.

- **Human-scale pacing is the default.** What the recorder does to a page
  itself (its first navigation, the sign-in fills) waits a random 1.5–4 s
  between navigations and 0.3–1.2 s between clicks or typing steps, one
  navigation at a time, at most 12 a minute. An `-AI` agent is given the same
  numbers in its prompt. `-Pace 'nav=2000-6000,action=500-1500,per-minute=6'`
  changes them; `-Pace off` makes them zero, for tests.
- **Throttling is honoured.** The recorder reads every response from the site.
  A `429`, or a `503` with `Retry-After`, pauses the recorder's own steps for
  what the site asked (or backs off with jitter); a running `-AI` agent cannot
  be slowed, so it is warned about instead. A block (the page refused with a
  small HTML `403` from a CDN), a bot check, or throttling that persists stops
  an `-AI` agent (or keeps it from starting), says what the site did and where,
  and leaves the still-recording browser to you; press ENTER when done.
- **Generated clients** carry a `PacingHandler`: one request at a time, about a
  second apart, `Retry-After` honoured, retries only for `GET`/`HEAD` (never
  `POST`), a clear exception when retries run out, and a block never retried.
  They name themselves in their `User-Agent` (`<Name>/<version>`, which you can
  change) and send no browser fingerprint headers.

The skill's full instructions are in
[`skills/web-api-discovery/SKILL.md`](skills/web-api-discovery/SKILL.md) --
see **Pacing, throttling, and where the line is** there for the details.

## Layout

```
skills/web-api-discovery/   the skill: SKILL.md plus all its tooling
  scripts/                  capture, scrub, catalogue and code generation (Node + PowerShell)
  csharp/  secret-gate/     templates for the generated wrapper project
WebApiDiscovery.psd1/.psm1  the PowerShell module (commands generated from the scripts)
Install.ps1                 links the skill and the module from this clone
tests/                      Pester suites; they also run the Node tests
docs/                       the overview diagram, as a picture and as editable PowerPoint
docs/designs/               design notes from the skill's history
```

## Development

Run from this folder (`web-api-discovery/` in Multiplex). CI is
[`.github/workflows/web-api-discovery.yml`](../.github/workflows/web-api-discovery.yml)
at the repository root, and runs only when this folder or that workflow changes.

```powershell
./.github/ci/Invoke-PesterSuite.ps1 -Path ./tests, ./.github/ci/tests, ./Install.Tests.ps1
```

CI also installs .NET 10 and gitleaks, so the generated-project build and the
secret-detection tests run there. Locally they skip when those tools are missing.

## History

This skill moved into [Multiplex](https://github.com/IntelliTect-dev/Multiplex),
the MCP server whose endpoints it feeds, from
[IntelliTect-Samples/web-api-discovery](https://github.com/IntelliTect-Samples/web-api-discovery).
The move carries a condensed history: 13 feature increments, each recording
the range of source commits it stands for. The full commit-by-commit history
stays in that repository.

This project was split out of
[IntelliSDLC.ai](https://github.com/IntelliTect-Samples/IntelliSDLC.ai) with its
full history. Issue and PR numbers such as `#377` in commit messages, code
comments and design notes from before the split refer to **IntelliSDLC.ai**.
The issues that were still open were transferred here.
