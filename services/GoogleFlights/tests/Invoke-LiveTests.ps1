#Requires -Version 7.5
<#
.SYNOPSIS
    Runs every Google Flights live test on this machine's internet connection, stopping at the first failure.

.DESCRIPTION
    About 21 requests to Google Flights, paced by the client, in about 5 minutes. They spend this
    connection's Google Flights allowance, which is small and not yet measured (issue #101), and is shared
    with everyone on the connection, so do not run this in a loop.

    Stages, each only after the one before passed:
      1. Prerequisites: git, the .NET 10 SDK, Pester 5 or later.
      2. Build, then the offline tests (no requests to Google).
      3. One live search (1 request). If Google refuses it, stop: this connection's allowance is spent.
      4. The client's live suite (12 requests).
      5. The MCP tools (5 requests) and the PowerShell module (3 requests), live.

    Every exchange with Google is saved in full under .har-captures/www.google.com/<stamp>-live/ in the
    repository's main checkout (gitignored). If anything was refused, bring those folders back: a refusal is
    evidence for the client's classifier.

.EXAMPLE
    # From an existing Multiplex checkout: the branch in its own worktree, then the run.
    git fetch origin feature/google-flights
    git worktree add .worktrees/89-google-flights feature/google-flights
    ./.worktrees/89-google-flights/services/GoogleFlights/tests/Invoke-LiveTests.ps1
#>
$ErrorActionPreference = 'Stop'
$started = Get-Date
$root = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$client = Join-Path $root 'services/GoogleFlights/tests/GoogleFlights.Tests'
$knownGood = 'FullyQualifiedName~Live.GoogleFlightsLiveTests.A_one_way_search_lists_priced'

function Step([string] $Title, [scriptblock] $Run) {
    Write-Host "`n=== $Title" -ForegroundColor Cyan
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $Run
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STOPPED: '$Title' failed after $([int]$clock.Elapsed.TotalSeconds) s. Later stages did not run." -ForegroundColor Red
        Show-Captures
        exit 1
    }
    Write-Host "passed in $([int]$clock.Elapsed.TotalSeconds) s" -ForegroundColor Green
}

function Show-Captures {
    $common = (git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
    $store = Join-Path (Split-Path $common -Parent) '.har-captures/www.google.com'
    $new = Get-ChildItem $store -Directory -Filter '*-live' -ErrorAction SilentlyContinue | Where-Object CreationTime -ge $started
    if ($new) {
        Write-Host "`nRecorded exchanges with Google (bring these back if anything was refused):"
        $new | ForEach-Object { Write-Host "  $($_.FullName)" }
    }
}

Step 'Prerequisites' {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is not installed.' }
    if (-not ((dotnet --list-sdks) -match '^10\.')) { throw 'The .NET 10 SDK is not installed.' }
    if (-not (Get-Module -ListAvailable Pester | Where-Object Version -ge '5.0')) { throw 'Pester 5 or later is not installed (Install-Module Pester).' }
    Write-Host "repository: $root (branch $(git -C $root branch --show-current))"
    $global:LASTEXITCODE = 0
}

Step 'Build' { dotnet build (Join-Path $root 'Multiplex.slnx') --nologo -v quiet }

Step 'Offline tests (no requests to Google)' {
    dotnet test $client --no-build --nologo
    if ($LASTEXITCODE -eq 0) { dotnet test (Join-Path $root 'tests/Multiplex.Tests') --no-build --nologo --filter 'FullyQualifiedName~GoogleFlights' }
}

$env:MULTIPLEX_LIVE_TESTS = '1'
try {
    Step 'Live: one search first (1 request; refused means stop)' {
        dotnet test $client --no-build --nologo --filter $knownGood
    }
    Step 'Live: the client suite (12 requests, about 3 minutes)' {
        dotnet test $client --no-build --nologo --filter "FullyQualifiedName~Live.GoogleFlightsLiveTests&$($knownGood.Replace('~', '!~'))"
    }
    Step 'Live: the MCP tools (5 requests)' {
        dotnet test (Join-Path $root 'tests/Multiplex.Tests') --no-build --nologo --filter 'FullyQualifiedName~GoogleFlightsMcpLiveTests'
    }
    Step 'Live: the PowerShell module (3 requests)' {
        $result = Invoke-Pester (Join-Path $root 'services/GoogleFlights/tests/GoogleFlights.PowerShell.Tests') -PassThru -Output Detailed
        $global:LASTEXITCODE = [int]($result.FailedCount -gt 0 -or $result.Result -ne 'Passed')
    }
}
finally {
    Remove-Item Env:MULTIPLEX_LIVE_TESTS -ErrorAction SilentlyContinue
}

Write-Host "`nAll live tests passed in $([int]((Get-Date) - $started).TotalMinutes) min." -ForegroundColor Green
Show-Captures
