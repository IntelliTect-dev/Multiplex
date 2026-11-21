#Requires -Version 7.5
<#
.SYNOPSIS
    Measures how Google Flights treats this connection's honest-bot client over time: one search every
    10 minutes, 12 at most (2 hours). Stop it any time with Ctrl+C.

.DESCRIPTION
    Issue #101. Google refuses the client per network at times (docs/protocol.md section 2). This records,
    for one connection, which searches are answered and which refused, and when, so what Google allows and
    how long a refusal lasts can be read off the log.

    Each search runs in a fresh PowerShell process: the client's own refusal cool-down would otherwise skip
    the later probes, and the point here is a fixed, slow cadence. The same one-way search (SEA-DEN, six
    weeks out) goes every time, with -Verify so no answer comes from a cache.

    The log is written to the repository's gitignored capture store,
    .har-captures/www.google.com/<stamp>-allowance.csv in the main checkout.

.EXAMPLE
    # From an existing Multiplex checkout with the branch in .worktrees/89-google-flights:
    git -C .worktrees/89-google-flights pull --ff-only && ./.worktrees/89-google-flights/services/GoogleFlights/tests/Measure-Allowance.ps1
#>
$ErrorActionPreference = 'Stop'
$root = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$common = (git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
$store = Join-Path (Split-Path $common -Parent) '.har-captures/www.google.com'
New-Item -ItemType Directory -Force $store | Out-Null
$log = Join-Path $store "$(Get-Date -Format yyyy-MM-dd-HHmmss)-allowance.csv"

$project = Join-Path $root 'services/GoogleFlights/src/GoogleFlights.PowerShell'
Write-Host 'Building the module...'
dotnet build $project --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The module did not build: dotnet build $project" }
$manifest = Get-ChildItem (Join-Path $project 'bin') -Recurse -Filter GoogleFlights.psd1 | Sort-Object LastWriteTime -Descending | Select-Object -First 1

$date = (Get-Date).Date.AddDays(42).ToString('yyyy-MM-dd')
$probe = @"
Import-Module '$($manifest.FullName)'
try {
    `$flights = @(Find-Flight SEA DEN $date -Verify -ErrorAction Stop)
    "answered,`$(`$flights.Count) flights"
} catch {
    if (`$_.Exception -is [Multiplex.Sites.GoogleFlights.GoogleFlightsThrottledException]) { "refused,`$(`$_.Exception.Reason)" }
    else { "error,`$(`$_.Exception.GetType().Name): `$(`$_.Exception.Message -replace ',', ';')" }
}
"@

'time,outcome,detail' | Set-Content $log
Write-Host "One search every 10 minutes, 12 at most. Log: $log`n"
for ($i = 1; $i -le 12; $i++) {
    $at = Get-Date -Format 'HH:mm:ss'
    $result = (pwsh -NoProfile -Command $probe | Select-Object -Last 1)
    "$at,$result" | Add-Content $log
    Write-Host ('{0,2}. {1}  {2}' -f $i, $at, $result) -ForegroundColor ($result -like 'answered*' ? 'Green' : 'Yellow')
    if ($i -lt 12) { Start-Sleep -Seconds 600 }
}
Write-Host "`nDone. Log: $log"
