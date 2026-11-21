#Requires -Version 7.5
<#
.SYNOPSIS
    Finds how fast Google Flights answers this connection's honest-bot client: the same search at a shrinking
    interval, from 5 minutes down to 10 seconds, until the first refusal, then how long that refusal lasts.
    Stop it any time with Ctrl+C.

.DESCRIPTION
    Issue #101. One search every 10 minutes was answered for hours from one network, and requests seconds apart
    came before refusals (docs/protocol.md section 2, M1 and G1/G2). This finds where between the two Google
    starts refusing.

    The schedule, start to start: a first search, then 3 more 5 minutes apart, then 5 each at 2 minutes,
    1 minute, 30 seconds and 10 seconds: 24 searches in about 33 minutes.

    At the first refusal it stops speeding up and asks again every 10 minutes, for at most an hour, until it is
    answered: that times the refusal. A check that a person must clear in a browser ("unusual traffic") stops it
    at once. If the first search is refused, the connection is still refused from before, and only the timing
    runs.

    It is built to end in a refusal, which can stop the client on this connection for hours, for everyone on it.
    Run it only on a connection you can spare, with the owner's go-ahead.

    Each search runs in a fresh PowerShell process, so the client's own pacing and refusal cool-down never delay
    or skip one: the spacing is exactly the schedule's. The same one-way search (SEA-DEN, six weeks out) goes
    every time, with -Verify so no answer comes from a cache.

    The log is written to the repository's gitignored capture store,
    .har-captures/www.google.com/<stamp>-pace.csv in the main checkout: each search's start time, the seconds
    since the one before, and what Google did.

.EXAMPLE
    # From an existing Multiplex checkout with the branch in .worktrees/89-google-flights:
    git -C .worktrees/89-google-flights pull --ff-only && ./.worktrees/89-google-flights/services/GoogleFlights/tests/Measure-Allowance.ps1
#>
$ErrorActionPreference = 'Stop'

$schedule = @(
    @{ Count = 3; Every = [TimeSpan]::FromMinutes(5) }
    @{ Count = 5; Every = [TimeSpan]::FromMinutes(2) }
    @{ Count = 5; Every = [TimeSpan]::FromMinutes(1) }
    @{ Count = 5; Every = [TimeSpan]::FromSeconds(30) }
    @{ Count = 5; Every = [TimeSpan]::FromSeconds(10) }
)
$recheck = [TimeSpan]::FromMinutes(10)
$rechecks = 6 # an hour

$root = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$common = (git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
$store = Join-Path (Split-Path $common -Parent) '.har-captures/www.google.com'
New-Item -ItemType Directory -Force $store | Out-Null
$log = Join-Path $store "$(Get-Date -Format yyyy-MM-dd-HHmmss)-pace.csv"

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
    `$e = `$_.Exception
    if (`$e -is [Multiplex.Sites.GoogleFlights.GoogleFlightsThrottledException]) {
        "refused,`$(`$e.Reason) `$([regex]::Match(`$e.Message, 'gRPC \d+|HTTP \d+').Value)".TrimEnd()
    }
    else { "error,`$(`$e.GetType().Name): `$(`$e.Message -replace ',', ';')" }
}
"@

$script:last = $null
$script:count = 0

# Runs one search in a fresh process, logs it and returns its outcome ("answered,...", "refused,...", "error,...").
function Invoke-Probe {
    $at = Get-Date
    $since = $script:last ? [int][Math]::Round(($at - $script:last).TotalSeconds) : $null
    $script:last = $at
    $script:count++
    $result = pwsh -NoProfile -Command $probe | Select-Object -Last 1
    if (-not $result) { $result = 'error,the search printed nothing' }
    "$($at.ToString('HH:mm:ss')),$since,$result" | Add-Content $log
    $gap = $null -eq $since ? '' : "+$since s"
    Write-Host ('{0,2}. {1} {2,7}  {3}' -f $script:count, $at.ToString('HH:mm:ss'), $gap, $result) `
        -ForegroundColor ($result -like 'answered*' ? 'Green' : 'Yellow')
    $result
}

function Wait-Until([datetime] $when) {
    $wait = $when - (Get-Date)
    if ($wait -gt [TimeSpan]::Zero) { Start-Sleep -Duration $wait }
}

function Stop-AtChallenge([string] $result) {
    if ($result -like 'refused,Challenge*') {
        Write-Host "`nGoogle asks for a check that a person must clear in a browser. Stopped; nothing more is sent. Log: $log"
        exit
    }
}

'time,seconds_since_last,outcome,detail' | Set-Content $log
Write-Host 'A first search, then 3 more 5 minutes apart, then 5 each at 2 minutes, 1 minute, 30 seconds and 10 seconds.'
Write-Host "At the first refusal: one search every 10 minutes, at most an hour, to time it. Log: $log`n"

$result = Invoke-Probe
Stop-AtChallenge $result
if ($result -like 'refused*') {
    Write-Host "`nThe first search was refused: this connection is still refused from before. Timing the refusal instead."
}
else {
    :pace foreach ($step in $schedule) {
        for ($i = 0; $i -lt $step.Count; $i++) {
            Wait-Until ($script:last + $step.Every)
            $result = Invoke-Probe
            if ($result -like 'refused*') { break pace }
        }
    }
    if ($result -notlike 'refused*') {
        Write-Host "`nNever refused, down to searches 10 seconds apart. Log: $log"
        exit
    }
    Stop-AtChallenge $result
    Write-Host "`nRefused. One search every 10 minutes, at most an hour, until it is answered again."
}

$refusedAt = $script:last
for ($i = 0; $i -lt $rechecks; $i++) {
    Wait-Until ($script:last + $recheck)
    $result = Invoke-Probe
    Stop-AtChallenge $result
    if ($result -like 'answered*') {
        Write-Host ("`nAnswered again {0:N0} minutes after the refusal. Log: {1}" -f ($script:last - $refusedAt).TotalMinutes, $log)
        exit
    }
}
Write-Host "`nStill refused an hour after the refusal. Log: $log"
