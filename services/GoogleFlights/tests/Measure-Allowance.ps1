#Requires -Version 7.5
<#
.SYNOPSIS
    Finds what makes Google Flights refuse this connection's client, and how long a refusal lasts: how close
    together two searches may be, how many may go back to back, and what pace may follow a burst. It runs for
    hours or days, and picks up where it left off when run again. Stop it any time with Ctrl+C.

    Since 2026-11-22 a search is a search page fetched over HTTPS (about 2-3 MB; protocol.md section 2), so this
    measures the search pages. The data-endpoint measurement of 2026-11-20 keeps its own files and is not continued.

.DESCRIPTION
    Issue #101. Three experiments, in order, each changing one thing so its result can be told apart:

      1. Gap. Two searches a set time apart, one pair every 20 minutes. The gap shrinks from 60 seconds to none
         (the second as soon as the first answers). Only the gap changes, so a refusal at the second search means
         the gap was too short.
      2. Burst. Searches back to back (twice the shortest safe gap apart), one burst every 45 minutes, growing from
         3 searches to 24. A refusal at search k means at most k-1 may go back to back.
      3. Pace. The client's own worst case: the safe burst back to back, then a search every T for 6 more, one
         trial every 45 minutes. T shrinks from 5 minutes to 10 seconds. The burst uses up whatever allowance
         Google keeps, so a T faster than Google lets it refill is refused within the 6. The hardest T answered,
         with the safe burst, is what the client's own pacing can be set to.

    Each experiment starts easy and gets harder until the first refusal, then narrows in. Once Google answers
    again and the connection has rested half an hour, it tries the level halfway (geometrically) between the
    hardest one answered and the easiest one refused. It stops when those two are within a third of each other,
    or after four refusals in that experiment. It then repeats the final safe level once to confirm it. If even
    the easiest level is refused, it keeps doubling the gap (up to 10 minutes) or the pace (up to an hour).

    After any refusal it asks again 2, 5, 15 and 30 minutes later, then hourly to every three hours, until Google
    answers. That times the refusal. A check that a person must clear in a browser ("unusual traffic") stops
    everything at once.

    Searches go from one client for the whole run, as the MCP server's does, with its pacing, retries, cache and
    refusal cool-down turned off, so the timing is exactly the experiment's. The same one-way search goes every
    time (SEA-DEN, six weeks out).

    Everything is kept per network, under a short salted hash of the network's name and gateway that does not name
    it, in the repository's gitignored capture store (.har-captures/www.google.com/pace/ in the main checkout):
    the state, so a second run continues where the first stopped; a log of every search; and a report. When an
    experiment finishes, or the run stops, the report is also posted to issue #101 with gh, if gh is signed in.
    Times there are UTC.

    It is built to get refused, which can stop the client on this network for hours, for everyone on it. A refusal
    of a search page may take the form of Google's "unusual traffic" check, which then applies to Google on this
    network, not only to Google Flights. Run it only on a connection you can spare, with the owner's go-ahead. It
    keeps Windows from sleeping while it runs, but not from sleeping when the lid closes. It builds the module into
    a folder of its own, so the branch's own build is not locked while it runs.

.EXAMPLE
    # From an existing Multiplex checkout with the branch in .worktrees/89-google-flights:
    git -C .worktrees/89-google-flights pull --ff-only && ./.worktrees/89-google-flights/services/GoogleFlights/tests/Measure-Allowance.ps1
#>
$ErrorActionPreference = 'Stop'

# The plan. Harder = 1 when a larger level is harder, -1 when a smaller one is. Rest = seconds from one trial's
# start to the next one's. Easiest = how far it goes below the ladder, doubling the gap or pace (halving the
# burst), when even the ladder's easiest level is refused.
$experiments = [ordered]@{
    Gap   = @{ Ladder = 60, 30, 15, 8, 4, 2, 0; Harder = -1; Rest = 20 * 60; Easiest = 600; Unit = ' s apart' }
    Burst = @{ Ladder = 3, 4, 6, 8, 12, 16, 24; Harder = 1; Rest = 45 * 60; Easiest = 2; Unit = ' back to back' }
    Pace  = @{ Ladder = 300, 180, 120, 90, 60, 40, 30, 20, 15, 10; Harder = -1; Rest = 45 * 60; Easiest = 3600; Unit = ' s after the burst' }
}
$maxRefusals = 4  # per experiment, the confirmation included
$precision = 1.3  # stop narrowing when the hardest answered and the easiest refused are this close
$quiet = 30 * 60  # rest after a refusal clears, and between experiments
$probeAfter = 2, 5, 15, 30, 60, 90, 120, 180, 240, 300, 360, 480, 600, 720 # minutes after a refusal; then every 180
$late = 10        # seconds a search may start after its time before the trial no longer counts (the PC slept)
$maxEarly = 3     # refusals before the part a trial tests, after which the experiment gives up
$paced = 6        # searches at the pace, after the burst, in a Pace trial

$root = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$common = (git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
$dir = Join-Path (Split-Path $common -Parent) '.har-captures/www.google.com/pace'
# What is measured, in every file name: the data endpoint's files (2026-11-20) are named by the network alone.
$target = 'pages'

function Get-Now { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0 }

# Waits in short steps against the wall clock, so a PC that slept does not stretch the wait.
function Wait-Until([double] $when) {
    while (($left = $when - (Get-Now)) -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Ceiling([Math]::Min($left, 15) * 1000)) }
}

# A short salted hash of the network the default route leaves by: it tells networks apart without naming one.
# $null when there is no route out.
function Get-NetworkId {
    if (-not $IsWindows) { return 'default' }
    try {
        $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
            Sort-Object { $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric } |
            Select-Object -First 1
    }
    catch { return $null }
    if (-not $route) { return $null }
    $name = (Get-NetConnectionProfile -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue).Name
    $bytes = [Text.Encoding]::UTF8.GetBytes("$script:salt|$name|$($route.NextHop)")
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 8).ToLowerInvariant()
}

function Initialize-Client {
    $project = Join-Path $root 'services/GoogleFlights/src/GoogleFlights.PowerShell'
    Write-Host 'Building the module...'
    # A folder of the run's own: a run lasts days, and a loaded module locks the files it was loaded from.
    $out = Join-Path ([IO.Path]::GetTempPath()) "multiplex-measure-allowance/$PID/"
    dotnet build $project --nologo -v quiet "-p:OutDir=$out" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The module did not build: dotnet build $project" }
    Import-Module (Join-Path $out 'GoogleFlights.psd1')
    $options = [Multiplex.Sites.GoogleFlights.GoogleFlightsClientOptions]::new()
    $options.BurstRequests = 1000000          # no pacing: the experiment sets the timing
    $options.MaxWaitingRequests = 0
    $options.RefusalCooldown = [TimeSpan]::Zero # no cool-down: the experiment decides when to ask again
    $options.MaxRefusalCooldown = [TimeSpan]::Zero
    $options.MaxRetries = 0                   # one search, one request
    $options.TotalTimeout = [TimeSpan]::FromMinutes(1)
    $script:client = [Multiplex.Sites.GoogleFlights.GoogleFlightsClient]::new($options, [TimeProvider]::System)
    $script:userAgent = [Multiplex.Sites.GoogleFlights.GoogleFlightsClientOptions]::UserAgent
}

# One search. Returns its outcome: answered, refused, challenge (a person must clear a check), invalid (Google
# called the request invalid) or error (anything else: no connection, a timeout, a server error, gRPC 9).
function Invoke-Search {
    $date = [DateOnly]::FromDateTime([DateTime]::UtcNow.Date.AddDays(42))
    $request = [Multiplex.Sites.GoogleFlights.FlightSearchRequest]::OneWay('SEA', 'DEN', $date, $null)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $result = $script:client.SearchAsync($request, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $count = @($result.AllFlights).Count
        # This route always has flights: a page that carries none is Google withholding them, not an empty route.
        $outcome, $detail = $count -gt 0 ? ('answered', "$count flights") : ('refused', 'the page carried no flights')
    }
    catch {
        $e = $_.Exception
        while ($e -is [Management.Automation.MethodInvocationException] -and $e.InnerException) { $e = $e.InnerException }
        $code = [regex]::Match($e.Message, 'gRPC \d+|HTTP \d+').Value
        if ($e -is [Multiplex.Sites.GoogleFlights.GoogleFlightsThrottledException] -and "$($e.Reason)" -eq 'Challenge') {
            $outcome, $detail = 'challenge', 'unusual traffic'
        }
        elseif ($e -is [Multiplex.Sites.GoogleFlights.GoogleFlightsThrottledException] -and "$($e.Reason)" -in 'Refused', 'TooManyRequests') {
            $outcome, $detail = 'refused', $code
        }
        elseif ($e -is [Multiplex.Sites.GoogleFlights.GoogleFlightsRequestException]) {
            $outcome, $detail = 'invalid', $code
        }
        elseif ($e -is [Multiplex.Sites.GoogleFlights.GoogleFlightsFormatException]) {
            # A page without its flight data, or an unexpected redirect: Google not serving the search.
            $outcome, $detail = 'refused', "no flight data: $($e.Message)"
        }
        else {
            $outcome, $detail = 'error', "$($e.GetType().Name): $($e.Message)"
        }
    }
    @{ Outcome = $outcome; Detail = $detail; Ms = $watch.ElapsedMilliseconds }
}

# Posts a report to the issue. A failure is reported, never fatal: the report is also on disk.
function Publish-Report([string] $path) {
    $remote = git -C $root remote get-url origin
    $repo = [regex]::Match($remote, 'github\.com[:/](.+?)(?:\.git)?$').Groups[1].Value
    if (-not (Get-Command gh -ErrorAction SilentlyContinue) -or -not $repo) {
        Write-Host "Not posted to #101 (gh is not available). The report: $path" -ForegroundColor Yellow
        return
    }
    $url = gh issue comment 101 --repo $repo --body-file $path
    if ($LASTEXITCODE -ne 0) { Write-Host "Could not post to #101. The report: $path" -ForegroundColor Yellow }
    else { Write-Host "Posted the report: $url" }
}

# ---------------------------------------------------------------------------------------------------------------
# Deciding the next trial. Pure: it reads only the trials, so a run picks up exactly where it stopped.

function Get-Key($level, [int] $harder) { $level * $harder }

function Get-Ratio($a, $b) {
    $lo = [Math]::Min($a, $b); $hi = [Math]::Max($a, $b)
    if ($lo -le 0) { [double]::PositiveInfinity } else { $hi / $lo }
}

function Get-Midpoint($a, $b) {
    $mid = if ([Math]::Min($a, $b) -le 0) { ($a + $b) / 2 } else { [Math]::Sqrt($a * $b) }
    [int][Math]::Round($mid, [MidpointRounding]::AwayFromZero)
}

# The next level to try, or that the experiment is done, with the hardest level answered (Safe) and the easiest
# refused (Unsafe). $known is a level already known to be safe from an earlier experiment, or $null.
function Get-Plan([object[]] $ladder, [int] $harder, $easiest, [object[]] $trials, $known) {
    $passed = @($trials | Where-Object { $_.verdict -eq 'pass' } | ForEach-Object { $_.level })
    if ($null -ne $known) { $passed += $known }
    $failed = @($trials | Where-Object { $_.verdict -eq 'fail' } | ForEach-Object { $_.limit })
    $unsafe = $failed | Sort-Object { Get-Key $_ $harder } | Select-Object -First 1
    $safe = $passed | Where-Object { $null -eq $unsafe -or (Get-Key $_ $harder) -lt (Get-Key $unsafe $harder) } |
        Sort-Object { Get-Key $_ $harder } | Select-Object -Last 1

    if (@($trials | Where-Object { $_.verdict -eq 'early' }).Count -ge $maxEarly) {
        return @{ Done = $true; Safe = $safe; Unsafe = $unsafe; Note = "refused $maxEarly times before the part being tested" }
    }
    if ($failed.Count -eq 0) {
        $next = $ladder | Where-Object { $null -eq $safe -or (Get-Key $_ $harder) -gt (Get-Key $safe $harder) } |
            Sort-Object { Get-Key $_ $harder } | Select-Object -First 1
        if ($null -eq $next) { return @{ Done = $true; Safe = $safe; Unsafe = $null; Note = 'never refused' } }
        return @{ Done = $false; Next = $next; Safe = $safe; Unsafe = $null }
    }
    if ($failed.Count -ge $maxRefusals) {
        return @{ Done = $true; Safe = $safe; Unsafe = $unsafe; Note = "stopped after $maxRefusals refusals" }
    }
    if ($null -eq $safe) {
        $next = $ladder | Where-Object { (Get-Key $_ $harder) -lt (Get-Key $unsafe $harder) } |
            Sort-Object { Get-Key $_ $harder } | Select-Object -Last 1
        if ($null -eq $next) {
            $next = $harder -lt 0 ? 2 * $unsafe : [Math]::Floor($unsafe / 2)
            if ((Get-Key $next $harder) -lt (Get-Key $easiest $harder)) { return @{ Done = $true; Safe = $null; Unsafe = $unsafe; Note = 'refused even at the easiest level' } }
        }
        return @{ Done = $false; Next = $next; Safe = $null; Unsafe = $unsafe }
    }
    $mid = Get-Midpoint $safe $unsafe
    if ((Get-Ratio $safe $unsafe) -gt $precision -and
        (Get-Key $mid $harder) -gt (Get-Key $safe $harder) -and (Get-Key $mid $harder) -lt (Get-Key $unsafe $harder)) {
        return @{ Done = $false; Next = $mid; Safe = $safe; Unsafe = $unsafe }
    }
    $answered = @($trials | Where-Object { $_.verdict -eq 'pass' -and $_.level -eq $safe }).Count
    if ($answered -ge 2 -or ($null -ne $known -and $safe -eq $known)) {
        return @{ Done = $true; Safe = $safe; Unsafe = $unsafe; Note = 'narrowed in' }
    }
    @{ Done = $false; Next = $safe; Safe = $safe; Unsafe = $unsafe; Confirming = $true }
}

# What a trial at this level sends, from what the earlier experiments found. Spacing holds the seconds before each
# search after the first; Tested is the first search that tests the level (a refusal before it is "early").
function Get-Shape([string] $name, $level) {
    $gap = $state.results['Gap']
    $gapSafe = if ($gap -and $null -ne $gap.Safe) { $gap.Safe } else { 30 }
    $burst = $state.results['Burst']
    $burstSafe = if ($burst -and $null -ne $burst.Safe) { $burst.Safe } else { 2 }
    switch ($name) {
        'Gap' { @{ Spacing = @($level); Tested = 2; Text = "2, $level s apart" } }
        'Burst' { @{ Spacing = @(@(2 * $gapSafe) * ($level - 1)); Tested = 2; Text = "$level, $(2 * $gapSafe) s apart" } }
        'Pace' {
            @{ Spacing = @(@(2 * $gapSafe) * ($burstSafe - 1)) + @(@($level) * $paced); Tested = $burstSafe + 1
               Text = "$burstSafe, $(2 * $gapSafe) s apart, then $paced, $level s apart" }
        }
    }
}

function Get-Ladder([string] $name) {
    $ladder = $experiments[$name].Ladder
    if ($name -eq 'Pace') {
        # A steady pace closer than the shortest safe gap would only measure the gap again.
        $gap = $state.results['Gap']
        if ($gap -and $null -ne $gap.Safe) { $ladder = @($ladder | Where-Object { $_ -ge $gap.Safe }) }
    }
    $ladder
}

# ---------------------------------------------------------------------------------------------------------------
# State, log and report, one set per network.

function Get-StatePath([string] $network) { Join-Path $dir "$network-$target.json" }

function Get-State([string] $network) {
    $path = Get-StatePath $network
    if (Test-Path $path) { return Get-Content $path -Raw | ConvertFrom-Json -AsHashtable }
    [ordered]@{
        network = $network; started = Get-Now; userAgent = $script:userAgent
        experiment = 'Gap'; results = [ordered]@{}; trials = @(); searches = @()
        nextAt = 0.0; lastSearch = 0.0; errorsInRow = 0; inTrial = $null
        recovering = $null; recoveries = @(); stopped = $null
    }
}

function Save-State { $state | ConvertTo-Json -Depth 10 | Set-Content (Get-StatePath $state.network) }

function Format-Time([double] $at, [switch] $Local) {
    $t = [DateTimeOffset]::FromUnixTimeMilliseconds([long]($at * 1000))
    if ($Local) { $t.ToLocalTime().ToString('HH:mm:ss') } else { $t.ToString('yyyy-MM-dd HH:mm') + 'Z' }
}

function Format-Span([double] $seconds) {
    if ($seconds -lt 120) { '{0:N0} s' -f $seconds }
    elseif ($seconds -lt 7200) { '{0:N0} min' -f ($seconds / 60) }
    else { '{0:N1} h' -f ($seconds / 3600) }
}

function Format-Level([string] $name, $level) {
    if ($null -eq $level) { return '-' }
    if ($name -eq 'Gap' -and $level -eq 0) { return 'none (as soon as answered)' }
    "$level$($experiments[$name].Unit)"
}

# Sends one search, logs it and records it in the state.
function Send-Search([string] $name, $level, [int] $trial, [int] $index, [string] $label) {
    $at = Get-Now
    $since = if ($state.lastSearch -gt 0) { [Math]::Round($at - $state.lastSearch) } else { $null }
    $s = Invoke-Search
    $s.At = $at
    $state.lastSearch = $at
    $state.searches += $at
    if ($s.Outcome -eq 'refused' -and -not $state.recovering) { $state.recovering = @{ refusedAt = $at; probes = @() } }
    $detail = $s.Detail -replace '[,\r\n]', ';'
    "$(Format-Time $at),$($state.network),$name,$level,$trial,$index,$since,$($s.Ms),$($s.Outcome),$detail" | Add-Content (Join-Path $dir "$($state.network)-$target.csv")
    Save-State
    $gapText = if ($null -ne $since) { "+$since s" } else { '' }
    Write-Host ('{0}  {1,-9} {2,-24} {3,9}  {4} ({5}, {6:N1} s)' -f (Format-Time $at -Local), $name, $label, $gapText, $s.Outcome, $s.Detail, ($s.Ms / 1000)) `
        -ForegroundColor ($s.Outcome -eq 'answered' ? 'Green' : 'Yellow')
    $s
}

function Get-Count([double] $before, [double] $window) { @($state.searches | Where-Object { $_ -ge $before - $window -and $_ -lt $before }).Count }

function Write-Report([string] $headline) {
    $path = Join-Path $dir "$($state.network)-$target.md"
    $lines = @(
        "## Measure-Allowance (search pages), network $($state.network): $headline"
        ''
        "Client: ``$($state.userAgent)``, one client for the run, with pacing, retries, cache and cool-down off. Each search fetches the search page over HTTPS. The same one-way search (SEA-DEN, six weeks out) every time. Started $(Format-Time $state.started); $(@($state.searches).Count) searches so far. Times are UTC."
        ''
        '| Experiment | State | Hardest answered | Easiest refused | Refusals | Note |'
        '|---|---|---|---|---|---|'
    )
    foreach ($name in $experiments.Keys) {
        $r = $state.results[$name]
        $count = @($state.trials | Where-Object { $_.experiment -eq $name -and $_.verdict -eq 'fail' }).Count
        $phase = if ($r) { 'done' } elseif ($state.experiment -eq $name) { 'running' } else { 'not started' }
        $lines += "| $name | $phase | $(Format-Level $name ($r ? $r.Safe : $null)) | $(Format-Level $name ($r ? $r.Unsafe : $null)) | $count | $($r ? $r.Note : '') |"
    }
    $lines += '', '### Trials', '', 'Searches: A answered, R refused, E error. "Before" counts the searches in the hour and the day before the trial.', ''
    $lines += '| # | Started | Experiment | Searches sent | Outcomes | Verdict | Before (1 h / 24 h) |', '|---|---|---|---|---|---|---|'
    foreach ($t in $state.trials) {
        $lines += "| $($t.n) | $(Format-Time $t.at) | $($t.experiment) | $($t.shape) | $("$($t.outcomes) $($t.detail)".Trim()) | $($t.verdict) | $($t.lastHour) / $($t.lastDay) |"
    }
    $lines += '', '### Refusals and how long they lasted', ''
    $lines += '| Refused | Last refused probe | Answered again | Lasted at most | Probes |', '|---|---|---|---|---|'
    foreach ($r in $state.recoveries) {
        $lines += "| $(Format-Time $r.refusedAt) | $(Format-Time $r.lastRefused) | $(Format-Time $r.answeredAt) | $(Format-Span ($r.answeredAt - $r.refusedAt)) | $($r.probes) |"
    }
    if ($state.recovering) { $lines += "| $(Format-Time $state.recovering.refusedAt) | | still refused | | $(@($state.recovering.probes).Count) |" }
    $lines += '', 'Verdicts: pass; fail, refused in the part tested; early, refused before it, not counted; error and late (the PC slept), not counted and tried again. Posted by `services/GoogleFlights/tests/Measure-Allowance.ps1`.'
    $lines -join "`n" | Set-Content $path -NoNewline
    $path
}

# ---------------------------------------------------------------------------------------------------------------
# Running trials and timing refusals.

# Waits until the PC has a way out, and moves to that network's state when it is not the current one. Returns
# $true when the network changed.
function Sync-Network {
    while (-not ($id = Get-NetworkId)) {
        Write-Host "$(Format-Time (Get-Now) -Local)  No network. Checking again in a minute."
        Wait-Until ((Get-Now) + 60)
    }
    if ($state -and $id -eq $state.network) { return $false }
    if ($state) { Save-State; Write-Host "`nThe network changed (now $id). Its own measurement continues; the other keeps its place." -ForegroundColor Cyan }
    $script:state = Get-State $id
    Save-State
    return $true
}

function Stop-Run([string] $reason, [string] $headline) {
    $state.stopped = $reason
    Save-State
    Write-Host "`n$reason" -ForegroundColor Yellow
    Publish-Report (Write-Report $headline)
}

# Asks again on the probe schedule until Google answers. Returns early when the network changes.
function Invoke-Recovery {
    $r = $state.recovering
    while ($true) {
        $done = @($r.probes).Count
        $after = if ($done -lt $probeAfter.Count) { $probeAfter[$done] } else { $probeAfter[-1] + 180 * ($done - $probeAfter.Count + 1) }
        $at = $r.refusedAt + 60 * $after
        if ($at -gt (Get-Now)) { Write-Host "Refused. Asking again at $(Format-Time $at -Local), $after minutes after the refusal." }
        Wait-Until $at
        if (Sync-Network) { return }
        $s = Send-Search 'recovery' $after 0 ($done + 1) "probe $($done + 1), $after min after"
        switch ($s.Outcome) {
            'challenge' { Stop-Run 'Google asks for a check that a person must clear in a browser. Stopped: nothing more is sent from this network.' 'stopped at a check'; return }
            'invalid' { Stop-Run "Google called the search invalid ($($s.Detail)): the client or Google has changed. Stopped." 'stopped: invalid search'; return }
            'error' { Wait-Until ((Get-Now) + 300); continue } # not a probe: no answer either way
            'refused' { $r.probes += $s.At; Save-State }
            'answered' {
                $state.recoveries += @{ refusedAt = $r.refusedAt; lastRefused = (@($r.probes) + $r.refusedAt | Measure-Object -Maximum).Maximum; answeredAt = $s.At; probes = $done + 1 }
                $state.recovering = $null
                $state.nextAt = [Math]::Max($state.nextAt, $s.At + $quiet)
                Save-State
                Write-Host "Answered again $(Format-Span ($s.At - $r.refusedAt)) after the refusal. Resting $(Format-Span $quiet)." -ForegroundColor Cyan
                return
            }
        }
    }
}

function Invoke-Trial([string] $name, $level, [bool] $confirming) {
    $shape = Get-Shape $name $level
    $start = [Math]::Max($state.nextAt, $state.lastSearch + $shape.Spacing[0])
    if ($start -gt (Get-Now) + 5) {
        Write-Host "Next: $name $(Format-Level $name $level)$($confirming ? ' (confirming)' : ''), searches $($shape.Text), at $(Format-Time $start -Local)."
    }
    Wait-Until $start
    if (Sync-Network) { return }

    $n = @($state.trials).Count + 1
    $first = Get-Now
    $state.inTrial = $n
    $trial = [ordered]@{
        n = $n; experiment = $name; level = $level; shape = $shape.Text; at = $first; outcomes = ''; detail = ''
        verdict = 'pass'; limit = $null; lastHour = Get-Count $first 3600; lastDay = Get-Count $first 86400
    }
    $count = $shape.Spacing.Count + 1
    $wasLate = $false
    for ($i = 1; $i -le $count; $i++) {
        if ($i -gt 1) {
            $due = $previous.At + $shape.Spacing[$i - 2]
            Wait-Until $due
            if ((Get-Now) - [Math]::Max($due, $previous.At + $previous.Ms / 1000) -gt $late) { $wasLate = $true }
        }
        $previous = Send-Search $name $level $n $i "trial $n search $i/$count"
        $trial.outcomes += @{ answered = 'A'; refused = 'R'; challenge = 'R'; invalid = 'E'; error = 'E' }[$previous.Outcome]
        if ($previous.Outcome -ne 'answered') { $trial.detail = $previous.Detail; break }
    }
    $o = $previous.Outcome
    $trial.verdict = switch ($true) {
        ($o -in 'challenge', 'invalid') { 'stopped'; break }
        ($o -eq 'error') { 'error'; break }
        ($o -eq 'refused' -and $wasLate) { 'late'; break }
        ($o -eq 'refused' -and $i -lt $shape.Tested) { 'early'; break }
        ($o -eq 'refused') { 'fail'; break }
        $wasLate { 'late'; break }
        default { 'pass' }
    }
    # A burst refused at search k says k may not go back to back; the other experiments refuse the level itself.
    if ($trial.verdict -eq 'fail') { $trial.limit = $name -eq 'Burst' ? $i : $level }
    $state.trials += $trial
    $state.inTrial = $null
    $state.nextAt = $first + $experiments[$name].Rest
    $state.errorsInRow = $o -eq 'error' ? $state.errorsInRow + 1 : 0
    if ($o -eq 'error') { $state.nextAt = [Math]::Max($state.nextAt, (Get-Now) + 60 * [Math]::Min(60, 5 * [Math]::Pow(2, $state.errorsInRow - 1))) }
    Save-State
    switch ($o) {
        'challenge' { Stop-Run 'Google asks for a check that a person must clear in a browser. Stopped: nothing more is sent from this network.' 'stopped at a check' }
        'invalid' { Stop-Run "Google called the search invalid ($($previous.Detail)): the client or Google has changed. Stopped." 'stopped: invalid search' }
    }
}

# ---------------------------------------------------------------------------------------------------------------
# The run.

New-Item -ItemType Directory -Force $dir | Out-Null
$saltPath = Join-Path $dir 'salt.txt'
if (-not (Test-Path $saltPath)) { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)) | Set-Content $saltPath }
$script:salt = (Get-Content $saltPath -Raw).Trim()
$script:state = $null

if ($IsWindows) {
    try {
        Add-Type -Namespace MeasureAllowance -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);'
        [void][MeasureAllowance.Power]::SetThreadExecutionState(0x80000001u) # ES_CONTINUOUS | ES_SYSTEM_REQUIRED, while this runs
    }
    catch { Write-Host 'Could not keep the PC awake; keep it from sleeping yourself.' -ForegroundColor Yellow }
}

Initialize-Client
[void](Sync-Network)
if ($state.stopped) {
    Write-Host "This network's measurement has stopped: $($state.stopped)`nTo start it over, delete $(Get-StatePath $state.network)."
    return
}
if ($state.inTrial) {
    # The last run stopped in the middle of a trial: its searches went, but say nothing about the level. Rest
    # before the next, so they do not run into it.
    $state.nextAt = [Math]::Max($state.nextAt, $state.lastSearch + [Math]::Max($quiet, $experiments[$state.experiment].Rest))
    $state.inTrial = $null
    Save-State
    Write-Host "The last run stopped in the middle of trial $(@($state.trials).Count + 1); that trial is not counted." -ForegroundColor Yellow
}
Write-Host "Network $($state.network). State, log and report: $dir"
Write-Host 'Gap, then Burst, then Pace; each from easy to hard until refused, then narrowing in. Ctrl+C stops; running it again continues.'

while ($true) {
    if ($state.stopped) { break }
    if ($state.recovering) { Invoke-Recovery; continue }
    $name = $state.experiment
    if (-not $name) {
        Stop-Run 'All three experiments are done.' 'done'
        break
    }
    # A pair at the safe gap already showed that 2 may go back to back.
    $known = ($name -eq 'Burst' -and $state.results['Gap'] -and $null -ne $state.results['Gap'].Safe) ? 2 : $null
    $plan = Get-Plan (Get-Ladder $name) $experiments[$name].Harder $experiments[$name].Easiest @($state.trials | Where-Object { $_.experiment -eq $name }) $known
    if ($plan.Done) {
        $state.results[$name] = @{ Safe = $plan.Safe; Unsafe = $plan.Unsafe; Note = $plan.Note }
        $names = @($experiments.Keys)
        $following = $names.IndexOf($name) + 1
        $state.experiment = $following -lt $names.Count ? $names[$following] : $null
        if ($state.experiment) { $state.nextAt = [Math]::Max($state.nextAt, $state.lastSearch + [Math]::Max($quiet, $experiments[$state.experiment].Rest)) }
        Save-State
        Write-Host "`n$name done: hardest answered $(Format-Level $name $plan.Safe), easiest refused $(Format-Level $name $plan.Unsafe) ($($plan.Note))." -ForegroundColor Cyan
        Publish-Report (Write-Report "$name done")
        continue
    }
    Invoke-Trial $name $plan.Next ([bool]$plan.Confirming)
}
