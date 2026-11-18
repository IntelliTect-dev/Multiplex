#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    End a HAR recording started by Invoke-HarCapture.

.DESCRIPTION
    PowerShell front door for `capture-har.js stop`.

    A HUMAN does not normally need this: they end the recording by pressing
    ENTER in the console Invoke-HarCapture is waiting on, or by closing the
    browser window. Both reach the same post-processing.

    This script exists for the case where nobody is holding that console --
    an AI driving the session over CDP from another process -- and as the
    recovery path when the console or window is out of reach.

    It does NOT post-process. It writes a STOP sentinel; the recording process
    polls for it, wins the ending race, and runs scrub, verify and digest in
    the process that owns the browser. Doing the work in two places would mean
    two answers to "was this capture scrubbed".

    So this script WAITS -- first for the recording to end, then for
    post-processing to finish -- and reports both the raw capture and the
    scrubbed artifacts. Reporting a capture as done while the scrub is still
    running would name files that do not exist yet.

    It waits for as long as the work is getting somewhere, not for a fixed
    time: a large capture takes minutes to scrub, and a wait that gave up at 60
    seconds reported a good capture as a failed one. While it waits it says
    where the recording process has got to, about every 30 seconds.

    When it returns with the recording process STILL RUNNING, it says which of
    two things happened, and neither of them is a verdict on the capture:

      STALLED          the stop registered, and post-processing has shown no
                       sign of progress for longer than a capture of that size
                       is allowed (60 seconds, or 15 seconds per megabyte; 30
                       minutes during the AI catalogue pass). The message
                       states both times.
      NEVER REGISTERED the recording process has not taken up the stop within
                       60 seconds. The recording is still going, and the
                       request stays in place.

    Both exit 1 and leave everything as it is -- the recording process still
    owns the raw capture and the scrub. Run this again to wait again.

    It never kills a browser process: killing one discards the entire
    recording, and a developer's machine may hold real signed-in windows whose
    loss has nothing to do with this capture.

    Under -Browser Native and -Attach it does not even CLOSE one. The recording
    process detaches from the browser and leaves it, its window and every tab in
    it exactly as they are, because that browser was borrowed rather than
    created -- so a window still standing after this returns is the contract and
    not a leak. Close it yourself, or keep it and record the next session in the
    same signed-in profile.

    It is idempotent. Against a window the operator already closed, the work is
    already done, so it verifies and reports rather than failing.

    A capture that produced no HAR, or one below -MinimumBytes, is reported as
    a failure -- silently handing back an empty file sends you off to analyze a
    recording that never existed.

.PARAMETER Session
    The capture session directory to stop. Defaults to the running capture, or
    the most recent one on disk.

.PARAMETER MinimumBytes
    Size below which the capture is treated as failed. Default 1024.

.PARAMETER CapturesDirectory
    Where captures live. Default .har-captures, under which each capture sits
    at <host>/<timestamp>/. This resolves an EXISTING session; it cannot move
    where a new capture is written.

.PARAMETER ValidateOnly
    Resolve and print what would be stopped, without stopping it.

.OUTPUTS
    One line of JSON, for whatever is driving this: the state it ended in, the
    session directory, the exit code, and the detail behind the state.

        { "stop": "complete", "sessionDir": "...", "exitCode": 0, "ending": { ... } }

    "stop" is one of: complete, not-processed (the capture was cancelled; the
    raw is kept), stalled, not-registered. "ending" carries the phase, when the
    stop registered, the last sign of progress, how long nothing has moved
    (quietSeconds) and how long it is allowed to (quietLimitSeconds).
    `node capture-har.js status` reports the same "ending" at any time,
    including "progressing" while post-processing is still under way.

    Everything a person reads is on the error stream, as it always was.

.NOTES
    Exit codes are capture-har.js's own. The ones this command returns:

      0  stopped, scrubbed and catalogued (or cancelled: the raw is kept)
      1  returned with the recording process still running -- the stop never
         registered, or post-processing stalled -- or a runtime error. The JSON
         says which.
      3  no capture session found to stop
      4  the capture produced no usable recording
      5  the raw was assembled from the incremental log
      6  recorded, but the scrub or the catalogue FAILED. Never returned for
         post-processing that is merely still running.
      7  scrubbed and catalogued, with advisory findings to review

.EXAMPLE
    Stop-HarRecording

.EXAMPLE
    $state = Stop-HarRecording | ConvertFrom-Json
    if ($state.stop -eq 'stalled') { "nothing has moved for $($state.ending.quietSeconds)s" }

    Read the state rather than the exit code alone: 1 covers both early returns.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Session,

    [int]$MinimumBytes = 1024,

    [string]$CapturesDirectory,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error 'node not found on PATH -- install Node.js >= 18 to use this script.'
    return
}

$captureJs = Join-Path $PSScriptRoot 'capture-har.js'
if (-not (Test-Path -LiteralPath $captureJs)) {
    Write-Error "capture-har.js not found at $captureJs"
    return
}

$captureArgs = @('stop', '--min-bytes', $MinimumBytes)
if ($Session) { $captureArgs += @('--session', $Session) }
if ($CapturesDirectory) { $captureArgs += @('--dir', $CapturesDirectory) }
if ($ValidateOnly) { $captureArgs += '--validate-only' }
# One verbosity switch, forwarded rather than duplicated -- see Invoke-HarCapture.ps1.
if ($VerbosePreference -ne 'SilentlyContinue') { $captureArgs += @('--log-level', 'verbose') }

Write-Verbose "capture-har.js $($captureArgs -join ' ')"

& node $captureJs @captureArgs
exit $LASTEXITCODE
