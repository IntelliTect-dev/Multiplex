#Requires -Version 7.0
<#
.SYNOPSIS
    Runs every Node test script in the repository with node, and fails if any
    of them does.

.DESCRIPTION
    The Node suites here are zero-dep, self-contained scripts: `node <file>`
    runs one and exits non-zero on the first failure. Nothing uses `node:test`,
    so no test runner is involved and none is needed -- this script is only
    discovery plus a non-zero exit, which is what CI needs to gate them.

    Before this existed, CI's only job was Pester, so the single way a Node test
    reached a pull request was a Pester wrapper in `tests/` that shelled out to
    node and asserted exit 0 (issue #94). That made Pester the runner for Node
    tests, and made "write a wrapper" a rule a contributor had to know or
    silently lose their coverage. Wrappers remain welcome where the PowerShell
    side has something of its own to assert; they are no longer the price of
    being run at all.

    Each file runs in its own process, so a crash takes down that file and not
    the run. Output is shown only for a file that fails -- a passing suite's
    chatter buries the one line that matters.

.PARAMETER Path
    Roots to search for `*.test.js`. Defaults to the skill folder, which is
    where all of them live.

.PARAMETER Filter
    Only run files whose repo-relative path matches this wildcard. For working
    on one area: -Filter '*capture*'.

.PARAMETER TimeoutSeconds
    Per-file limit. A hung test fails its file instead of hanging the run.
    Default 300.

.OUTPUTS
    Nothing on the pipeline. Exit code 0 when every file passed, 1 otherwise.

.EXAMPLE
    ./scripts/Invoke-NodeTestSuite.ps1

.EXAMPLE
    ./scripts/Invoke-NodeTestSuite.ps1 -Filter '*har-policy*'

.NOTES
    No file is currently known to fail on a developer machine while passing in CI.

    `capture/capture-preflight.test.js` used to: its precondition refused any
    machine with a global `playwright` -- the setup the README recommends. It
    now asks what its subprocess cases can actually reach, and passes there
    (issue #6).

    `har/har-large-capture-read.test.js` used to belong on that list: it aborted
    Node 21 with `v8::ToLocalChecked Empty MaybeLocal` (exit 134) where CI's
    Node 20 passed. The cause was its own precondition reading a >512 MB fixture
    into one string; it now establishes that precondition from the file's byte
    size instead and passes on both (issues #86, #95).
#>
[CmdletBinding()]
param(
    [string[]]$Path = @('skills'),
    [string]$Filter,
    [int]$TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    # Not a skip. Every one of these suites needs node, so a missing node means
    # the run proved nothing -- say so and fail rather than reporting success
    # over zero tests, which is the failure mode .github/ci/Invoke-PesterSuite.ps1
    # exists to prevent on the Pester side.
    Write-Error 'node is not on PATH; cannot run the Node test suites.'
    exit 1
}

# @(...) around the loop ON PURPOSE. A `foreach` that yields nothing produces
# PowerShell's "no value" marker, not $null or an empty array, and under
# Set-StrictMode the later `.Count` on it throws PropertyNotFoundException --
# which would crash past the deliberate "no tests found" message below instead of
# printing it. Found in review of #94.
$files = @(foreach ($root in $Path) {
    $abs = if ([System.IO.Path]::IsPathRooted($root)) { $root } else { Join-Path $repoRoot $root }
    if (-not (Test-Path -LiteralPath $abs)) {
        Write-Error "Path '$root' does not exist under $repoRoot."
        exit 1
    }
    Get-ChildItem -LiteralPath $abs -Recurse -File -Filter '*.test.js'
})

# Carry the full path alongside the display name rather than rebuilding it from
# the relative one: a -Path outside the repository (a test's throwaway tree)
# relativizes to `..\..\..`, and round-tripping that is a needless chance to be
# wrong about the only value that gets executed.
$suites = @($files |
    Sort-Object -Property FullName -Unique |
    ForEach-Object {
        [pscustomobject]@{
            Full = $_.FullName
            Rel  = [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
        }
    })

if ($Filter) { $suites = @($suites | Where-Object { $_.Rel -like $Filter }) }

if ($suites.Count -eq 0) {
    # Zero files is a failure for the same reason a missing node is: a gate that
    # matched nothing has gated nothing, and would report green forever.
    Write-Error ("No *.test.js found under " + ($Path -join ', ') + ($Filter ? " matching '$Filter'" : ''))
    exit 1
}

Write-Host "Running $($suites.Count) Node test suite(s) with $($node.Source)" -ForegroundColor Cyan

$failed = [System.Collections.Generic.List[string]]::new()
$sw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($suite in $suites) {
    $rel = $suite.Rel
    $full = $suite.Full
    $stdout = New-TemporaryFile
    $stderr = New-TemporaryFile
    try {
        try {
            $proc = Start-Process -FilePath $node.Source -ArgumentList @($full) `
                -WorkingDirectory $repoRoot -NoNewWindow -PassThru `
                -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        }
        catch {
            # A process that could not be STARTED is this file's failure, not the
            # run's: record it and carry on, so one exhausted handle on a loaded
            # runner does not cost the report for every file after it.
            $failed.Add("$rel (could not start node: $($_.Exception.Message))")
            Write-Host "  ERROR    $rel (could not start node: $($_.Exception.Message))" -ForegroundColor Red
            continue
        }
        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
            try { $proc.Kill($true) } catch { Write-Verbose "Could not kill $rel : $($_.Exception.Message)" }
            $failed.Add("$rel (timed out after ${TimeoutSeconds}s)")
            Write-Host "  TIMEOUT  $rel" -ForegroundColor Red
            continue
        }
        if ($proc.ExitCode -eq 0) {
            Write-Host "  ok       $rel" -ForegroundColor DarkGray
        }
        else {
            $failed.Add("$rel (exit $($proc.ExitCode))")
            Write-Host "  FAIL     $rel (exit $($proc.ExitCode))" -ForegroundColor Red
            # Only a failing file's output is shown, and both streams are: a
            # process that aborts writes its reason to stderr and its progress
            # to stdout, and the last line of each is usually the whole story.
            foreach ($stream in @($stdout, $stderr)) {
                $text = Get-Content -LiteralPath $stream -Raw -ErrorAction SilentlyContinue
                if ($text) { $text.TrimEnd() -split "`r?`n" | ForEach-Object { Write-Host "    | $_" } }
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

$sw.Stop()
$elapsed = [TimeSpan]::FromSeconds([Math]::Round($sw.Elapsed.TotalSeconds))

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host "$($failed.Count) of $($suites.Count) Node suite(s) FAILED in $elapsed" -ForegroundColor Red
    foreach ($f in $failed) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

Write-Host "All $($suites.Count) Node suite(s) passed in $elapsed" -ForegroundColor Green
exit 0
