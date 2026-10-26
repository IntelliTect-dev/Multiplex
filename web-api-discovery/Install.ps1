#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Make web-api-discovery available in every folder: the skill to every
    installed AI agent, and its commands to PowerShell -- straight from this
    clone.

.DESCRIPTION
    Links, never copies, so an edit in the clone shows up at once and
    `git pull` is the whole update. Everything points at the clone's MAIN
    working tree, never a linked worktree, which is disposable.

      the skill     ~/.claude/skills/web-api-discovery   Claude Code
                    ~/.agents/skills/web-api-discovery   GitHub Copilot, OpenAI
                                                         Codex and Gemini CLI
                    A folder is linked only when its agent is installed
                    (~/.claude; any of ~/.copilot, ~/.codex, ~/.gemini, ~/.agents).

      the commands  one managed block in your PowerShell profile that finds this
                    clone and imports WebApiDiscovery from it, so
                    Invoke-HarCapture and the other entry points are there in
                    every new terminal.

      the pointer   %LOCALAPPDATA%\web-api-discovery\clone.path -- this
                    machine's clone, which is how the block above finds it.

    WHY THE PROFILE AND NOT A LINK IN THE MODULES FOLDER. The modules folder is
    inside OneDrive-redirected Documents on a roaming setup, and a link there is
    a reparse point OneDrive syncs to other machines as a cloud placeholder that
    can never be hydrated -- so `Import-Module WebApiDiscovery` fails with "the
    cloud file provider is not running" on every machine but the one that made
    it. The profile is a file; it roams correctly.

    WHY THE BLOCK NAMES NO PATHS. The profile roams and a clone does not: it sits
    at C:\Git here and D:\Git there. The block used to list every machine's clone
    and import whichever existed, which meant the roaming file accumulated paths
    that were meaningless on every machine but one -- and a machine whose path was
    not in the list yet got no commands at all, from a profile that looked
    installed.

    So the block is now IDENTICAL on every machine, and the machine-specific fact
    lives in machine-local state. It resolves this clone at load time, in order:

      1. $env:WEBAPIDISCOVERY_CLONE, for a clone somewhere unguessable
      2. the pointer file above, written by THIS script -- the deterministic
         answer on a machine that has run it
      3. web-api-discovery under Git, source\repos, repos or src, off the home
         folder or any fixed drive root -- so a machine that has NOT run this
         script still works when the clone is somewhere conventional

    Nothing in it is specific to a machine, so the same profile serves all of
    them and `git pull` on one does not leave another broken.

    Then it reports what the capture tooling needs and cannot find: Node.js,
    Playwright and Chrome. It installs none of them.

    Twice-runnable: a second run reports every link unchanged. A real folder
    where a link would go is never replaced; a link from an earlier install is
    repointed without touching what it pointed at.

.EXAMPLE
    git clone https://github.com/IntelliTect-Samples/web-api-discovery C:\Git\web-api-discovery
    C:\Git\web-api-discovery\Install.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SkillName = 'web-api-discovery'

# Personal skills folders, and the home-folder entries that mean an agent
# reading each one is installed.
$script:SkillTargets = @(
    [pscustomobject]@{ Folder = '.claude/skills'; AgentHomes = @('.claude') }
    [pscustomobject]@{ Folder = '.agents/skills'; AgentHomes = @('.copilot', '.codex', '.gemini', '.agents') }
)

function Get-MainCheckout {
    <#
        The main working tree of the clone containing $Path. In a linked
        worktree the git dir differs from the common dir, whose parent is the
        main tree; otherwise (a plain clone, or a submodule) --show-toplevel is.
    #>
    param([Parameter(Mandatory)][string]$Path)

    $dirs = @(& git -C $Path rev-parse --path-format=absolute --git-dir --git-common-dir --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0 -or $dirs.Count -ne 3) {
        throw "'$Path' is not inside a git clone of web-api-discovery. Clone it, then run this from the clone."
    }
    $main = if ($dirs[0] -ne $dirs[1]) { Split-Path $dirs[1] -Parent } else { $dirs[2] }
    return [System.IO.Path]::GetFullPath($main)
}

function Get-SkillFolders {
    <# The personal skills folders to link into, for the agents present under $HomeDir. #>
    param([Parameter(Mandatory)][string]$HomeDir)

    foreach ($target in $script:SkillTargets) {
        if ($target.AgentHomes | Where-Object { Test-Path -LiteralPath (Join-Path $HomeDir $_) }) {
            Join-Path $HomeDir $target.Folder
        }
    }
}

$script:ProfileBegin = '# >>> web-api-discovery (managed by Install.ps1) >>>'
$script:ProfileEnd = '# <<< web-api-discovery <<<'

# Where a machine keeps the path to its own clone. The same convention the
# recorder already uses for the capture profile (see captureProfileRoot in
# capture-har.js): local app data on Windows, XDG_DATA_HOME or ~/.local/share
# elsewhere. Local, therefore never roamed, which is the whole point.
$script:PointerLeaf = Join-Path $script:SkillName 'clone.path'

function Get-LocalStateRoot {
    <# The machine-local state directory. Never inside a roaming profile. #>
    if ($env:LOCALAPPDATA) { return $env:LOCALAPPDATA }
    if ($env:XDG_DATA_HOME) { return $env:XDG_DATA_HOME }
    return Join-Path $HOME '.local' 'share'
}

function Set-ClonePointer {
    <#
        Record $ClonePath as THIS machine's clone, so the managed profile block
        can find it without naming it. Returns 'Written' or 'Unchanged'.
    #>
    param([Parameter(Mandatory)][string]$ClonePath)

    $file = Join-Path (Get-LocalStateRoot) $script:PointerLeaf
    $existing = if (Test-Path -LiteralPath $file) {
        (Get-Content -LiteralPath $file -Raw).Trim()
    } else { '' }
    if ($existing -eq $ClonePath) { return 'Unchanged' }
    New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
    Set-Content -LiteralPath $file -Value $ClonePath -Encoding utf8
    return 'Written'
}

function Update-ProfileBlock {
    <#
        $Content with the managed block -- added when absent, replaced when
        present. Pure; everything outside the block is kept.

        THE BLOCK TAKES NO CLONE PATH, deliberately. It is byte-identical on
        every machine, because the profile it goes into roams and a clone does
        not. See the note at the top of this file for the resolution order it
        uses instead, and why the old per-machine list had to go.
    #>
    param(
        [AllowEmptyString()][AllowNull()][string]$Content
    )

    $Content = [string]$Content
    $pattern = '(?s)' + [regex]::Escape($script:ProfileBegin) + '.*?' + [regex]::Escape($script:ProfileEnd)
    $existing = [regex]::Match($Content, $pattern)

    # Scoped in `& { }` and imported with -Global so the block leaves no
    # variables behind in the session. A profile runs in global scope, so
    # anything declared at its top level is there for the rest of the terminal's
    # life -- and `$clone` is a name somebody else's profile line may well want.
    $block = @(
        $script:ProfileBegin
        '# The web-api-discovery commands. IDENTICAL ON EVERY MACHINE: this profile'
        '# roams and a clone does not, so nothing here names any machine''s paths.'
        '# Install.ps1 records this machine''s clone under local app data; the'
        '# conventional roots below cover a machine that has not run it.'
        '& {'
        '    $state = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA }'
        '             elseif ($env:XDG_DATA_HOME) { $env:XDG_DATA_HOME }'
        '             else { Join-Path $HOME ''.local'' ''share'' }'
        "    `$pointer = Join-Path `$state '$($script:PointerLeaf.Replace('\', '/'))'"
        # $HOME needs no guard: it is a read-only automatic variable, so it is
        # always set. Fixed drives only -- probing a removable or optical drive
        # spins it up, and this runs in every new terminal.
        '    $roots = @($HOME) + @([IO.DriveInfo]::GetDrives() |'
        '        Where-Object { $_.DriveType -eq ''Fixed'' } | ForEach-Object { $_.Name })'
        '    $candidates = @('
        '        $env:WEBAPIDISCOVERY_CLONE'
        '        if (Test-Path -LiteralPath $pointer) { Get-Content -LiteralPath $pointer -TotalCount 1 }'
        '        foreach ($root in $roots) {'
        "            foreach (`$dir in 'Git', 'source/repos', 'repos', 'src') {"
        "                Join-Path `$root `$dir '$($script:SkillName)'"
        '            }'
        '        }'
        '    )'
        '    foreach ($candidate in $candidates) {'
        '        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }'
        '        $manifest = Join-Path $candidate.Trim() ''WebApiDiscovery.psd1'''
        '        if (Test-Path -LiteralPath $manifest) { Import-Module $manifest -Global; break }'
        '    }'
        '}'
        $script:ProfileEnd
    ) -join [Environment]::NewLine

    if ($existing.Success) {
        return $Content.Substring(0, $existing.Index) + $block + $Content.Substring($existing.Index + $existing.Length)
    }
    if ($Content -and -not $Content.EndsWith("`n")) { $Content += [Environment]::NewLine }
    $separator = if ($Content) { [Environment]::NewLine } else { '' }
    return $Content + $separator + $block + [Environment]::NewLine
}

function Set-Link {
    <#
        Make $Link point at $Target. Returns 'Linked', 'Unchanged', or
        'Skipped' (a real folder is there -- never replaced).
    #>
    param(
        [Parameter(Mandatory)][string]$Link,
        [Parameter(Mandatory)][string]$Target
    )

    $Target = [System.IO.Path]::GetFullPath($Target)
    $item = Get-Item -LiteralPath $Link -Force -ErrorAction SilentlyContinue
    if ($item) {
        if (-not $item.LinkType) { return 'Skipped' }
        if ([System.IO.Path]::TrimEndingDirectorySeparator($item.Target) -eq
            [System.IO.Path]::TrimEndingDirectorySeparator($Target)) {
            return 'Unchanged'
        }
        # Removing a junction or symlink removes the link, not its target.
        $item.Delete()
    }
    New-Item -ItemType Directory -Path (Split-Path $Link -Parent) -Force | Out-Null
    New-Item -ItemType ($IsWindows ? 'Junction' : 'SymbolicLink') -Path $Link -Target $Target | Out-Null
    return 'Linked'
}

function Get-PrerequisiteWarnings {
    <# What the capture tooling needs and cannot find. Reports; installs nothing. #>

    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        'Node.js is not on PATH -- the recorder is a Node script. Install Node.js LTS.'
        return
    }
    $globalRoot = if (Get-Command npm -ErrorAction SilentlyContinue) { & npm root -g 2>$null }
    $inGlobal = $globalRoot -and (Test-Path -LiteralPath (Join-Path $globalRoot 'playwright'))
    $inNodePath = $env:NODE_PATH -and ($env:NODE_PATH -split [regex]::Escape([System.IO.Path]::PathSeparator) |
        Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'playwright')) })
    if (-not ($inGlobal -or $inNodePath)) {
        'Playwright is not installed globally -- run: npm install -g playwright   (a project with its own copy also works, from that folder)'
    }
    if ($IsWindows) {
        $chrome = @(
            "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
            "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
            "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
        if (-not $chrome) { 'Google Chrome was not found -- the recorder drives your installed Chrome by default.' }
    }
}

# Dot-sourced (by the tests): load the functions, run nothing.
if ($MyInvocation.InvocationName -eq '.') { return }

$root = Get-MainCheckout $PSScriptRoot
$skill = Join-Path $root 'skills' $script:SkillName

$folders = @(Get-SkillFolders -HomeDir $HOME)
if (-not $folders) {
    Write-Warning 'No AI agent found in your home folder (looked for .claude, .copilot, .codex, .gemini, .agents). The skill was not linked.'
}
foreach ($folder in $folders) {
    $link = Join-Path $folder $script:SkillName
    Write-Information "skill   $(Set-Link -Link $link -Target $skill) -- $link" -InformationAction Continue
}

# THE POINTER FIRST, so the block can never be in place while the thing it reads
# is missing. Written every run rather than only on a change of path: it is this
# machine's answer, and a clone that moved is the case it exists to absorb.
$pointerFile = Join-Path (Get-LocalStateRoot) $script:PointerLeaf
Write-Information "clone   $(Set-ClonePointer -ClonePath $root) -- $pointerFile -> $root" -InformationAction Continue

$profilePath = $PROFILE.CurrentUserAllHosts
$current = if (Test-Path -LiteralPath $profilePath) { Get-Content -LiteralPath $profilePath -Raw } else { '' }
$updated = Update-ProfileBlock -Content $current
if ($updated -ne $current) {
    New-Item -ItemType Directory -Path (Split-Path $profilePath -Parent) -Force | Out-Null
    Set-Content -LiteralPath $profilePath -Value $updated -NoNewline -Encoding utf8
    Write-Information "module  Added -- $profilePath finds this machine's clone (new terminals)" -InformationAction Continue
}
else {
    Write-Information "module  Unchanged -- $profilePath" -InformationAction Continue
}

foreach ($warning in Get-PrerequisiteWarnings) { Write-Warning $warning }
