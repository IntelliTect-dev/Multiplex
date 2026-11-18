#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Delete ONE recording, on purpose.

.DESCRIPTION
    NOTHING IN THIS PIPELINE DELETES A RECORDING BY ITSELF, and this command
    does not change that. The maintainer's decision of 2026-10-14 is the whole
    design: every recording is kept until somebody deletes that one recording
    deliberately. No age sweep. No count cap. No size cap. No bulk delete. A
    raw is the one artifact here that cannot be regenerated -- a browser session
    a human drove by hand -- and a 71 MB capture was already destroyed once by a
    routine `git worktree remove` that had nothing to prompt about (#367).

    What was missing was the deliberate half. The only delete path at all was
    `Invoke-SanitizeHar -RemoveSource`, which applies only after a scrub the
    leak gate verified clean, so a recording an operator simply did not want had
    no exit except deleting a gitignored directory by hand.

    EXACTLY ONE CAPTURE, AND EVERY OTHER SHAPE IS REFUSED. A wildcard is refused
    by its spelling, before any resolution, so there is no path on which it
    happens to match one capture and is accepted -- which is how the habit would
    get taught. More than one capture is refused. A store, or a host folder, is
    refused by naming how many captures it holds. A label that matches more than
    one capture is refused by naming the matches: guessing which recording the
    operator meant is the one thing an irreversible command must not do.

    ONE PARAMETER, AND NO SECOND ONE. A `host/stamp` label resolves against the
    store the recorder itself would use, found from the working directory
    exactly as `stop`, `status` and `catalogue` find it. An option to point this
    at some other store is an option nobody asked for, and a delete command is
    the last place to start accumulating them.

    WHAT IT PRINTS is the capture it is about to delete: host, stamp, size, the
    run that made it (#26) -- or that none was recorded -- and
    the description -- the operator's own `-Describe` text, or the first line of
    the `CAPTURE.md` they wrote beside an imported capture. That is the point of
    a confirmation prompt, and it is also worth knowing when writing those: a
    description is echoed to the console. Nothing from inside the capture is --
    no URI, no header, no entry.

    THE WHOLE SESSION DIRECTORY GOES, substitution tables included. They are
    keyed by the plaintext values a scrub replaced, so one left behind after its
    capture is gone is a reverse lookup of live credentials with nothing left to
    justify it.

    IT RESOLVES THROUGH THE SHARED WALK -- `capture-store.js`, the one
    enumeration -- so "delete this folder" is not something it can be talked
    into. A directory that is not a capture is refused as one.

    AND IT MUST LIE IN A CAPTURE STORE. The walk classifies a directory by the
    files in it and anchors to nothing, which is right for a walk and wrong for
    an irreversible recursive delete: an unrelated folder that happens to hold a
    stray `session.json` or `raw.har` satisfied every other test here. So the
    target must sit under a `.har-captures` directory -- the one directory name
    the recorder writes captures into and no option redirects. Found in review.

    WHAT THAT CHECK IS, EXACTLY: the SPELLING of an ancestor directory, not its
    identity. A junction or symlink literally named `.har-captures` satisfies it
    while pointing somewhere else, and deliberately so -- a capture tree reached
    through a redirected drive or a linked folder is an ordinary Windows setup,
    and verifying the segment were a real directory would refuse every delete on
    one. The guard is against a directory that merely looks capture-shaped,
    which is what the review found; it is not a defence against an operator who
    types a path through a link they made, and the confirmation prompt names the
    resolved path for exactly that reason.

.PARAMETER Capture
    The one capture to delete. Any of:

      * its directory path;
      * a `host/stamp` label, as `Get-HarCapture` prints it;
      * a bare `stamp`, for a capture at the store root;
      * one object from `Get-HarCapture`.

    Never a wildcard, and never more than one.

.EXAMPLE
    Remove-HarCapture app.example.com/2026-01-02-143000

    Asks, then deletes that one recording.

.EXAMPLE
    Remove-HarCapture app.example.com/2026-01-02-143000 -WhatIf

    Says what it would delete, and deletes nothing.

.EXAMPLE
    Get-HarCapture | Where-Object Stamp -eq 2026-01-02-143000 | ForEach-Object {
        Remove-HarCapture -Capture $_
    }

    The scripted form. Note the `ForEach-Object`: this command takes one
    capture, so a pipeline of several is something the caller writes out
    deliberately rather than something it accepts quietly.

.EXAMPLE
    Get-HarCapture | Where-Object { $_.RunId -and $_.RunId -eq $run } | ForEach-Object {
        Remove-HarCapture -Capture $_
    }

    One run's recordings, each confirmed. The `$_.RunId -and` guard is not
    decoration: with an empty `$run`, `RunId -eq $run` alone is true for every
    capture that has no run.
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    # [object], not [string]: an object from Get-HarCapture is the idiomatic
    # input, and a typed [string] would silently stringify an ARRAY of captures
    # into one space-joined path that then fails to resolve -- a refusal, but
    # one that never says what was actually wrong.
    [Parameter(Mandatory, Position = 0)]
    [object]$Capture
)

$ErrorActionPreference = 'Stop'

# The deletion is reported, and under -WhatIf the plan is. A module function's
# Write-Information answers to the MODULE's preference rather than the caller's,
# so the default is set here, in the script that emits it -- and only when the
# caller did not say, since binding -InformationAction to 'Continue' on every
# call would make these unsuppressable.
if (-not $PSBoundParameters.ContainsKey('InformationAction')) {
    $InformationPreference = 'Continue'
}

Import-Module (Join-Path $PSScriptRoot '..' 'har' 'HarStoreBatch.psm1') -Force
$captureStoreJs = Join-Path $PSScriptRoot 'capture-store.js'

# ---------------------------------------------------------------------------
# WHICH ONE -- and every way of not being one
# ---------------------------------------------------------------------------

# REFUSED BEFORE ANYTHING IS RESOLVED. A collection reaching here means the
# caller asked for several, and the count is the useful half of saying no.
if ($Capture -isnot [string] -and $Capture -is [System.Collections.IEnumerable]) {
    $count = @($Capture).Count
    Write-Error ("Remove-HarCapture deletes one capture, and $count were named. " +
        'Nothing under .har-captures/ is deleted in bulk -- delete them one at a time, ' +
        'each on purpose.')
    return
}

# What the caller named, as a string: a path, a label, or the Path of a
# Get-HarCapture object.
$spec = if ($Capture -is [string]) { $Capture }
elseif ($Capture.PSObject.Properties['Path']) { [string]$Capture.Path }
elseif ($Capture.PSObject.Properties['FullName']) { [string]$Capture.FullName }
else { [string]$Capture }

if ([string]::IsNullOrWhiteSpace($spec)) {
    Write-Error 'Remove-HarCapture needs one capture: a path, a host/stamp label, or a Get-HarCapture object.'
    return
}

# BY SPELLING, NOT BY WHAT IT MATCHES. A wildcard that resolved to exactly one
# capture and was accepted would teach the habit, and the next one would match
# four. Asked of PowerShell's own wildcard parser rather than a hand-written
# character class, so `[`, `]`, `*` and `?` all count without this file having
# an opinion about which of them matter.
if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($spec)) {
    Write-Error ("Remove-HarCapture will not take a wildcard ($spec). Name the one capture " +
        'to delete -- a path, or the host/stamp Get-HarCapture prints.')
    return
}

# How a capture is NAMED in output is HarStoreBatch.psm1's answer, not a second
# one: `host/stamp`, or the stamp alone when the layout has no host layer. The
# batch summary already got that wrong once by keying on the capture's class
# instead of on whether a host exists, and printed `/2020-01-01-mitmdump` into
# the one output an operator reads to decide what to triage. A copy of the rule
# here would be a copy of that bug waiting to be reintroduced.
#
# WHETHER A PATH IS INSIDE A CAPTURE STORE is HarStoreBatch.psm1's answer too
# (Test-HarCaptureStorePath), and for the same reason. It was written here, for
# this command; `Invoke-HarCapture -Scrub / -Catalogue` then needed the same
# question answered, and a second copy of the guard that stands between a
# stray folder and a recursive delete is not something to have two of. The
# store's directory name, and the note on what the check is and is not, moved
# with it.

$resolved = $null

if (Test-Path -LiteralPath $spec -PathType Container) {
    # A PATH. Asked of the shared walk rather than tested here: whether a
    # directory is a capture has exactly one definition, and it is not in this
    # file.
    $full = (Resolve-Path -LiteralPath $spec).ProviderPath.TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $under = @(Get-HarCaptureInventory -Path $full -CaptureStoreJs $captureStoreJs)
    $itself = @($under | Where-Object {
            $_.dir.TrimEnd([IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar) -eq $full
        })

    if ($itself.Count -eq 1) {
        # IN A STORE, not merely capture-shaped. `describeCaptureDir` decides by
        # the files present and anchors to nothing, so a folder somewhere else
        # entirely with a leftover session.json in it passed every other check on
        # the way here -- and the next step deletes a tree recursively. The store
        # directory name is fixed and no option redirects it, which is exactly
        # what makes it a usable anchor.
        if (-not (Test-HarCaptureStorePath -Path $full)) {
            Write-Error ("$spec is not inside a capture store. Recordings live under a " +
                '.har-captures directory, and Remove-HarCapture deletes ' +
                'nothing outside one.')
            return
        }
        $resolved = $itself[0]
    }
    elseif ($under.Count -gt 0) {
        # A store, or a host folder: it HOLDS captures rather than being one.
        # Named by count, because "that is not a capture" would be unhelpfully
        # true of a directory the operator correctly believes is full of them.
        Write-Error ("$spec holds $($under.Count) capture(s) and is not one itself. " +
            'Remove-HarCapture deletes one recording, named: ' +
            (($under | ForEach-Object { Get-HarCaptureLabel -Capture $_ } | Select-Object -First 3) -join ', ') +
            $(if ($under.Count -gt 3) { ', ...' }) + '.')
        return
    }
    else {
        Write-Error ("$spec is not a capture. A capture is a directory holding a session.json, " +
            'or a raw somebody imported, at the captures root or one level under a host folder.')
        return
    }
}
else {
    # A LABEL, resolved against the store this clone records into -- the same
    # root the recorder anchors to.
    $wanted = $spec.Replace([IO.Path]::DirectorySeparatorChar, '/').Trim('/')
    $all = @(Get-HarCaptureInventory -CaptureStoreJs $captureStoreJs)
    $matched = @($all | Where-Object {
            (Get-HarCaptureLabel -Capture $_) -eq $wanted -or [string]$_.stamp -eq $wanted
        })

    if ($matched.Count -eq 0) {
        Write-Error ("no capture matches '$spec' in this clone's store. " +
            'Run Get-HarCapture to see what is there.')
        return
    }
    if ($matched.Count -gt 1) {
        # NAMED, NEVER PICKED. A bare stamp exists under more than one host when
        # two sites were recorded in the same second; resolving that to "the
        # newest" would make an irreversible command guess.
        Write-Error ("'$spec' matches $($matched.Count) captures: " +
            (($matched | ForEach-Object { Get-HarCaptureLabel -Capture $_ }) -join ', ') +
            '. Name the one to delete.')
        return
    }
    $resolved = $matched[0]
}

# ---------------------------------------------------------------------------
# THE DELETION
# ---------------------------------------------------------------------------

$label = Get-HarCaptureLabel -Capture $resolved
$target = $resolved.dir
$described = if ($resolved.describe) { $resolved.describe } else { 'no description' }
# EXACT, not rounded. This number is read while deciding to destroy something
# that cannot be remade, and "0.0 MB" is the wrong thing to say about a capture
# worth keeping. Get-HarCapture's table rounds because it is scanned; a
# confirmation prompt is read.
$size = if ($resolved.rawBytes) { '{0:N0} bytes' -f $resolved.rawBytes } else { 'no raw' }
# WHOSE RECORDING THIS IS (#26). Two captures in a shared store can carry the
# same description and stamps a second apart, and be two sessions' work; the
# run is what tells them apart at the moment it matters. A capture recorded
# before runs existed says so -- an absent clause would read as "nothing to
# check". A capture this recorder did not make has no run to speak of.
$run = if (-not $resolved.recorderOutput) { $null }
elseif ($resolved.runId) { "run $($resolved.runId)" }
elseif ($resolved.captureId) { 'run unidentified' }
else { 'no run recorded' }
$what = (@($described, $size, $run) | Where-Object { $_ }) -join ', '

# The ShouldProcess line names the capture AND what it is, because the
# confirmation prompt is where the decision actually gets made and "Remove a
# capture?" is not something anybody can answer. ConfirmImpact is High, so this
# prompt appears without -Confirm being typed.
$operation = "Delete this recording permanently -- $what"
if (-not $PSCmdlet.ShouldProcess($target, $operation)) {
    # Said in the script's own voice as well, so a dry run reads as a plan
    # rather than only as PowerShell's stock preview line.
    Write-Information "Remove-HarCapture: nothing was deleted. $label would be removed ($what)."
    return
}

Remove-Item -LiteralPath $target -Recurse -Force
Write-Information "Remove-HarCapture: deleted $label ($what)."
