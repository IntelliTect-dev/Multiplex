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
git clone https://github.com/IntelliTect-Samples/web-api-discovery C:\Git\web-api-discovery
C:\Git\web-api-discovery\Install.ps1
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
`clone.path`, then `web-api-discovery` under `Git`, `source\repos`, `repos` or
`src` off your home folder or any fixed drive. A clone in a conventional place
therefore works even on a machine where you have never run the installer. To
**update**, pull:

```powershell
git -C C:\Git\web-api-discovery pull
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

**In a folder that isn't a git repository**, the recorder first asks whether
to create one (`[Y/n]`). A repository plus the `.gitignore` the recorder writes
keeps raw captures and the scrub's substitution tables, which can reverse the
scrub, out of any commit. If you answer no, it records anyway and warns you.
Don't capture inside Dropbox, OneDrive or any other synced folder: raw
captures carry live session cookies.

The skill's full instructions are in
[`skills/web-api-discovery/SKILL.md`](skills/web-api-discovery/SKILL.md).

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

```powershell
./.github/ci/Invoke-PesterSuite.ps1 -Path ./tests, ./.github/ci/tests, ./Install.Tests.ps1
```

CI also installs .NET 10 and gitleaks, so the generated-project build and the
secret-detection tests run there. Locally they skip when those tools are missing.

## History

This project was split out of
[IntelliSDLC.ai](https://github.com/IntelliTect-Samples/IntelliSDLC.ai) with its
full history. Issue and PR numbers such as `#377` in commit messages, code
comments and design notes from before the split refer to **IntelliSDLC.ai**.
The issues that were still open were transferred here.
