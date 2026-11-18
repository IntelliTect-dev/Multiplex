# web-api-discovery

Record a website session, scrub it of credentials and personal data, and turn
the traffic into a reviewable API reference. Optionally, generate a complete,
buildable .NET wrapper from it: typed client, PowerShell module, MCP server,
tests and secret-scanning gates.

It ships as an **AI agent skill** in the open [Agent Skills](https://agentskills.io)
format, so Claude Code, GitHub Copilot, OpenAI Codex and Gemini CLI can all
use it. It also ships as **PowerShell commands** you can run yourself. It works
in any folder, including an empty one.

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

To re-run one step on a capture you already have, without recording again:

```powershell
Invoke-HarCapture https://example.com -Catalogue               # catalogue it again
Invoke-HarCapture https://example.com -Scrub -Catalogue        # scrub again, then catalogue
Invoke-HarCapture example.com/2026-01-02-143000 -Scrub         # one capture, by its label
```

It says which capture it selected. When the site has been recorded more than
once it lists them and stops, rather than guessing which one you meant. The
scrub uses the operator profile (`.har-profile.json`) the recording itself
scrubbed with, wherever you run it from, and a scrub the leak gate kept with
advisory findings is a warning and exit code 7, never a quiet success. A
capture recorded before the recorder noted that profile is refused if it
already has a scrub (kept or rejected), and otherwise scrubbed with the
profile found beside the capture store, with a warning to check the result
for your own identifiers. `-Catalogue` on its own re-checks with that
store-root profile, which may be weaker than the recording's.

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
