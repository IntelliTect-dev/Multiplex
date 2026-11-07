#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    List the recordings in a capture store, as objects.

.DESCRIPTION
    "What have I recorded?" had no answer an operator could ask for. The
    inventory already existed -- `capture-store.js`, the one walk over a store,
    driven from PowerShell by `Get-HarCaptureInventory` and used by the batch
    drivers -- and it was exported by nothing. Answering the question meant
    running an internal script by hand and reading raw JSON. Measured in a
    consuming project on 2026-10-14: 122 entries, read exactly that way.

    So this is a THIN wrapper and deliberately nothing more. It does not walk
    the tree; `capture-store.js` stays the one enumeration, the same one
    `resolveSession` uses for `stop`, `status` and `catalogue`. A second walk in
    PowerShell would be free to disagree about what a capture is, and the store
    having two answers to that is the defect this subsystem exists to avoid
    (#387).

    IT EMITS OBJECTS, NEVER TEXT. `ConvertTo-Json`, `Where-Object` and
    `Sort-Object` work on the result, and what a human sees at the console is
    PowerShell's own formatting engine applied to those objects via
    `HarCapture.Format.ps1xml`.

    A MISSING DESCRIPTION IS FLAGGED RATHER THAN BLANK. The object carries
    `$null`, which is the honest datum and what a script tests; the table prints
    `(no description)`, because an empty cell reads as "the column did not
    render" just as readily as "nobody described this capture", and only one of
    those is the operator's problem.

    NOT ONE CAPTURED VALUE IS PRINTED. Site, stamp, size, stage, and a
    description the operator wrote themselves. Nothing here opens a HAR -- the
    walk it calls never does either.

    THE DESCRIPTION IS THAT ONE EXCEPTION, AND IT IS WORTH KNOWING WHEN WRITING
    ONE. It is whatever was typed into `Invoke-HarCapture -Describe`, or the
    first line of the `CAPTURE.md` written beside an imported capture, and this
    command prints every capture's at once -- to a console, into an evidence
    artifact, into an agent's context. It is a label for a recording, so a URL
    pasted into one is a URL on display later. Nothing from INSIDE the capture
    is ever printed: no URI the recorder observed, no header, no entry.

.PARAMETER Path
    The capture store, a host folder inside one, or a single capture directory.
    Pointing at a host folder IS the host filter; the path already says it.

    Omit it for the store this clone records into -- the same root the recorder
    anchors to, which is the repository's MAIN working tree and not the working
    directory (#367).

.OUTPUTS
    WebApiDiscovery.HarCapture.StoreEntry

.EXAMPLE
    Get-HarCapture

    Every recording in this clone's store, newest site first.

.EXAMPLE
    Get-HarCapture | Where-Object { -not $_.Description }

    The recordings nobody described.

.EXAMPLE
    Get-HarCapture -Path .har-captures/app.example.com | Format-List

    One site, in full.

.EXAMPLE
    Get-HarCapture | Where-Object Stage -eq raw | Sort-Object Bytes -Descending

    What has been recorded and never processed, biggest first -- the queue
    `Invoke-SanitizeHar -InputHar .har-captures` exists to drain.
#>

[CmdletBinding()]
[OutputType('WebApiDiscovery.HarCapture.StoreEntry')]
param(
    [Parameter(Position = 0)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'har' 'HarStoreBatch.psm1') -Force

# -PrependPath so a re-run in the same session picks up the current file rather
# than the copy loaded the first time.
$formatFile = Join-Path $PSScriptRoot 'HarCapture.Format.ps1xml'
if (Test-Path -LiteralPath $formatFile) {
    Update-FormatData -PrependPath $formatFile -ErrorAction SilentlyContinue
}

$captureStoreJs = Join-Path $PSScriptRoot 'capture-store.js'

# CHECKED HERE RATHER THAN LEFT TO NODE. The walk exits non-zero for a path that
# does not exist, and what surfaces from a native command's failure is
# "node ended with a non-zero exit code" -- which does not say WHICH path was
# wrong, the only thing the operator needs.
if ($Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Error "no capture store at $Path"
        return
    }
    $inventory = Get-HarCaptureInventory -Path $Path -CaptureStoreJs $captureStoreJs
}
else {
    $inventory = Get-HarCaptureInventory -CaptureStoreJs $captureStoreJs
}

<#
.SYNOPSIS
    How far down the pipeline this capture got, as one word.

.DESCRIPTION
    The stages are strictly sequential -- a digest needs a verified scrub, a
    catalogue needs a digest -- so the furthest one reached says everything the
    four separate booleans do, in one column an operator can scan. The booleans
    are still on the object, and `Format-List` shows every one of them.

    A capture this pipeline did not make has no stage IN it, so it reports its
    class instead. Calling an unattributable dump `raw` would read as "ready to
    scrub", which is the one thing that must never be said about a capture with
    no session to attribute its traffic to.

    REJECTED is terminal and comes first: the leak gate refused that scrub, and
    a quarantined candidate beside the raw is not progress.
#>
function Get-StageWord {
    param([Parameter(Mandatory)][object]$Capture)

    if (-not $Capture.recorderOutput) { return [string]$Capture.captureClass }
    if ($Capture.rejected)  { return 'REJECTED' }
    if ($Capture.catalogue) { return 'catalogued' }
    if ($Capture.digest)    { return 'digested' }
    if ($Capture.scrubbed)  { return 'scrubbed' }
    return 'raw'
}

<#
.SYNOPSIS
    An ISO timestamp as a DateTime, or $null.

.DESCRIPTION
    A DateTime so `Sort-Object StartedUtc` orders recordings by when they were
    made. Parsed as universal because that is what the recorder wrote; a local
    reading would shift every timestamp by the reader's offset.

    $null rather than a throw for anything unparseable. `session.json` is read
    from disk and may have been hand-edited, and one bad timestamp must cost
    that capture its start time and not the whole listing.
#>
function Get-StartedUtc {
    param([object]$Value)

    if (-not $Value) { return $null }
    [datetime]$parsed = [datetime]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor
              [System.Globalization.DateTimeStyles]::AssumeUniversal
    if ([datetime]::TryParse([string]$Value, [cultureinfo]::InvariantCulture, $styles, [ref]$parsed)) {
        return $parsed
    }
    return $null
}

foreach ($capture in $inventory) {
    # WHAT THE RECORDER ALREADY KNEW AND NOBODY READ (#450), in one place an
    # operator looks. A capture whose post-processing failed listed identically
    # to one that simply has not been scrubbed yet, because the only field that
    # could have carried it -- `scrubbed` -- is false either way.
    #
    # The parse failure is kept alongside the stage errors rather than in its
    # own property: they are both "this capture is not what it looks like", and
    # a second column nobody reads is how the first one got missed.
    $errors = [System.Collections.Generic.List[string]]::new()
    if ($capture.summaryParseError) {
        $errors.Add("the recorder could not read back the HAR it wrote: $($capture.summaryParseError)")
    }
    foreach ($e in @($capture.postProcessErrors)) { if ($e) { $errors.Add([string]$e) } }

    [pscustomobject]@{
        PSTypeName     = 'WebApiDiscovery.HarCapture.StoreEntry'
        # Null, not '.har-captures', for a capture with no host layer: printing
        # what the parent directory literally is would name the store as though
        # it were a site.
        Site           = $capture.host
        Stamp          = $capture.stamp
        StartedUtc     = Get-StartedUtc -Value $capture.startedUtc
        Description    = $capture.describe
        Bytes          = $capture.rawBytes
        # Reported, never refused: `assembleFromLog` exists precisely so an
        # interrupted recording still yields a raw.
        Ended          = [bool]$capture.ended
        Stage          = Get-StageWord -Capture $capture
        Scrubbed       = [bool]$capture.scrubbed
        Rejected       = [bool]$capture.rejected
        Digest         = [bool]$capture.digest
        Catalogue      = [bool]$capture.catalogue
        # EXISTENCE ONLY, which is as far as the walk goes and as far as this
        # goes. These tables are keyed by the plaintext values a scrub replaced,
        # so one of them is a reverse lookup of live credentials -- never which
        # of the two, never its path, and never its contents. It is here because
        # an operator deciding to delete a recording needs to know a credential
        # table goes with it.
        Substitutions  = [bool]$capture.substitutions
        Class          = $capture.captureClass
        # The question every caller deciding whether to PROCESS a capture is
        # actually asking, carried from the walk rather than re-derived from the
        # class by each reader.
        RecorderOutput = [bool]$capture.recorderOutput
        # Why a capture was declined, when it was. Null for recorder output.
        Reason         = $capture.reason
        Errors         = $errors.ToArray()
        # What Remove-HarCapture is given. The store is gitignored and the path
        # carries host and stamp only, which is what the batch summary already
        # prints.
        Path           = $capture.dir
    }
}
