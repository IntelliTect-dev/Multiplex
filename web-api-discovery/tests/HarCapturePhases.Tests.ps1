#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Behavior tests for `Invoke-HarCapture -Scrub` and `-Catalogue` (issue #24,
# carried over from IntelliTect-Samples/web-api-discovery#48).
#
# THE FINDING THIS EXISTS FOR. `Invoke-HarCapture` ran record -> scrub ->
# catalogue or nothing. A capture already on disk -- one whose AI pass failed,
# one whose scrub a newer scrubber would do better -- could only be reprocessed
# by leaving the front door for two other scripts, or by recording again a
# browser session somebody drove by hand.
#
# WHAT IS PINNED HERE.
#
#   EACH COMBINATION. -Scrub alone, -Catalogue alone, both (scrub FIRST), and
#   neither -- which must stay a recording.
#   NOTHING IS RECORDED. No new session directory appears, and the raw is byte
#   for byte what it was.
#   WHICH CAPTURE IS SAID. Label and directory, on the information stream.
#   AN AMBIGUOUS SELECTION IS REFUSED, never resolved to "the newest". A URL
#   whose site has two captures names neither.
#   A CAPTURE IN THE WRONG STATE IS REFUSED. -Catalogue on a capture nobody
#   scrubbed, on one whose scrub the leak gate REJECTED; -Scrub on one with no
#   raw; either on a capture this recorder did not make.
#   THE GATE IS NOT SOFTENED. A refused re-scrub is quarantined and nothing is
#   catalogued from it.
#   A SCRUB THAT CANNOT MATCH THE ONE IT WOULD REPLACE DOES NOT REPLACE IT. A
#   capture the recorder signed in for was scrubbed with values nothing else
#   holds; scrubbing it again is refused while that scrubbed.har is there.
#   NOTHING TYPED IS ECHOED BACK unless it was read as what it is: a URL gives
#   its host, and an argument that could not be read gives nothing.
#   THE EXISTING RE-ENTRY POINTS DO THE WORK. Asserted by what they leave on
#   disk -- the quarantine name, the digest -- not by reading the source.
#
# None of these launch a browser or reach a website. The store is a temporary
# git repository built from nothing; not one byte comes from a real capture.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
    $script:InvokePs1  = Join-Path $script:ScriptsDir 'capture/Invoke-HarCapture.ps1'
    $script:Pwsh       = (Get-Command pwsh).Source

    # The cataloguing AI is decided by the recorder from the environment. Inside
    # an agent session these two are set, and the recorder would then report the
    # catalogue as delegated to that agent. Blanked so every run here answers
    # the same way wherever the suite is started from -- the same thing
    # har-recatalogue.Tests.ps1 does.
    $script:SavedClaudeCode = $env:CLAUDECODE
    $script:SavedEntryPoint = $env:CLAUDE_CODE_ENTRYPOINT
    $env:CLAUDECODE = ''
    $env:CLAUDE_CODE_ENTRYPOINT = ''

    function Write-FixtureHar {
        param(
            [Parameter(Mandatory)][string]$Path,
            [string]$Body = '{"ok":true}',
            [string]$Url = 'https://api.example.test/v1/things'
        )

        @{
            log = @{
                version = '1.2'
                creator = @{ name = 'fixture'; version = '1' }
                entries = @(@{
                        startedDateTime = '2026-01-01T00:00:00.000Z'
                        time            = 1
                        request         = @{ method = 'GET'; url = $Url
                            httpVersion = 'HTTP/1.1'; headers = @(); queryString = @(); cookies = @()
                            headersSize = -1; bodySize = 0
                        }
                        response        = @{ status = 200; statusText = 'OK'; httpVersion = 'HTTP/1.1'
                            headers = @(); cookies = @()
                            content = @{ size = $Body.Length; mimeType = 'application/json'; text = $Body }
                            redirectURL = ''; headersSize = -1; bodySize = $Body.Length
                        }
                        cache           = @{}
                        timings         = @{ send = 0; wait = 1; receive = 0 }
                    })
            }
        } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
    }

    # An empty store in a temporary git repository.
    #
    # `git init` and the .gitignore entries are not decoration: the scrub
    # refuses to write a substitution table anywhere git will not confirm is
    # ignored (#318), and the store is found from the working directory through
    # the repository it sits in -- so a bare temp directory is neither somewhere
    # the scrub runs nor somewhere a label resolves.
    function New-PhaseStore {
        param(
            # The folder an agent records in before anybody has run `git init`:
            # the recorder writes the .gitignore beside the store and records
            # anyway, and that is the state a later scrub finds.
            [switch]$NoRepository,
            # The operator's own identifiers and what each becomes, as
            # .har-profile.json holds them: literal -> <Sentinel>.
            [hashtable]$Literals = @{},
            # Spliced into the folder name, for a path with something awkward
            # in it.
            [string]$NameSuffix = ''
        )

        $base = Join-Path ([IO.Path]::GetTempPath()) ('har-phases-' + [guid]::NewGuid().ToString('n') + $NameSuffix)
        New-Item -ItemType Directory -Path $base -Force | Out-Null
        if (-not $NoRepository) { & git -C $base init --quiet 2>&1 | Out-Null }
        Set-Content -LiteralPath (Join-Path $base '.gitignore') -Encoding utf8 -Value @(
            '.har-profile.json', '.har-substitutions.json', '.substitutions.json', '.har-captures/')
        Set-Content -LiteralPath (Join-Path $base '.har-profile.json') -Encoding utf8 -Value (
            @{ salt = 'har-capture-phases-fixture-salt'; literals = $Literals } | ConvertTo-Json)
        [pscustomobject]@{ Base = $base; Captures = (Join-Path $base '.har-captures') }
    }

    function Remove-PhaseStore {
        param([object]$Store)

        if ($Store -and (Test-Path -LiteralPath $Store.Base)) {
            Remove-Item -LiteralPath $Store.Base -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # One capture, in whichever state a test needs. Returns its directory.
    function Add-PhaseCapture {
        param(
            [Parameter(Mandatory)][object]$Store,
            [string]$Site = 'www.example.test',
            [Parameter(Mandatory)][string]$Stamp,
            [string]$Describe = 'fixture capture',
            [switch]$Scrubbed,
            [switch]$Catalogued,
            [switch]$DescribedCatalogue,
            [switch]$Rejected,
            [switch]$NoRaw,
            [switch]$Foreign,
            # How the recorder signed in, as it records it in session.json:
            # 'bitwarden' or 'prompt' when IT filled the form, 'none' when a
            # form was met and the operator signed in by hand.
            [string]$SignIn,
            # What the recorder writes when it recorded outside a repository
            # and placed the scrub's substitution tables itself.
            [switch]$UnprotectedTables,
            # The response body the raw capture carries.
            [string]$Body = '{"ok":true}',
            # The recorder's process, as session.json records it. A session a
            # recorder is still working on has a live one and no postProcess.
            [int]$RecorderId,
            # When the recording started. The recorder's process is older than
            # this; a process that began AFTER it only inherited the number.
            [string]$StartedUtc = '2026-01-01T00:00:00.000Z',
            # The recorder never wrote endedUtc: still recording, or cut off.
            [switch]$NotEnded,
            # The recorder finished its own scrub and catalogue, and said so.
            [switch]$PostProcessed,
            # Where the recording published its scrubbed artifacts, when that
            # was somewhere other than the capture's own directory.
            [string]$OutputPath,
            # The operator profile the recording scrubbed with, as the recorder
            # records it (#24 N1). By default the store's own, which is where a
            # recording started in the store's folder finds it.
            [string]$ScrubProfilePath,
            # A capture recorded before the recorder said which profile it
            # used: session.json has no scrubProfilePath at all.
            [switch]$NoProfileRecord
        )

        $dir = Join-Path (Join-Path $Store.Captures $Site) $Stamp
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        if ($Foreign) {
            # A mitmproxy dump: a raw, and no session.json to say whose it is.
            Set-Content -LiteralPath (Join-Path $dir 'raw.mitm') -Value 'not-a-har' -Encoding utf8
            return $dir
        }
        if (-not $NoRaw) { Write-FixtureHar -Path (Join-Path $dir 'raw.har') -Body $Body }
        # The start URI carries a token in its path on purpose: it is the shape
        # nothing here may ever echo.
        $session = @{
            uri = 'https://www.example.test/magic/FIXTURE-START-TOKEN'
            describe = $Describe; sessionDir = $dir
            harPath = (Join-Path $dir 'raw.har')
            outputPath = $(if ($OutputPath) { $OutputPath } else { $dir })
            startedUtc = $StartedUtc
        }
        if (-not $NotEnded) { $session.endedUtc = '2026-01-01T00:01:00.000Z' }
        if ($RecorderId) { $session.pid = $RecorderId }
        if ($PostProcessed) {
            $session.postProcess = @{ completedUtc = '2026-01-01T00:02:00.000Z'; errors = @(); warnings = @() }
        }
        if ($SignIn) { $session.signIn = @{ source = $SignIn; signedIn = $true } }
        if ($UnprotectedTables) { $session.unprotectedTables = $true }
        if (-not $NoProfileRecord) {
            $session.scrubProfilePath = if ($ScrubProfilePath) { $ScrubProfilePath }
            else { Join-Path $Store.Base '.har-profile.json' }
        }
        $session | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dir 'session.json') -Encoding utf8
        if ($Scrubbed) { Write-FixtureHar -Path (Join-Path $dir 'scrubbed.har') }
        if ($Rejected) { Write-FixtureHar -Path (Join-Path $dir 'scrubbed.rejected.har') }
        if ($Catalogued) { Set-Content -LiteralPath (Join-Path $dir 'catalogue.json') -Value '[]' -Encoding utf8 }
        if ($DescribedCatalogue) {
            # A catalogue somebody WORKED on: a Description is the one thing the
            # AI pass writes that the scaffold never can.
            Set-Content -LiteralPath (Join-Path $dir 'catalogue.json') -Encoding utf8 -Value (
                ConvertTo-Json -Depth 6 -InputObject @(@{
                        Action = 'things-list'; Description = 'Listed the things'; Methods = @('GET')
                        Endpoints = @('api.example.test/v1/things'); EntryCount = 1; Status = 'Exercised'
                    }))
        }
        return $dir
    }

    # The front door in a CHILD process, so the exit code is the real one and
    # the refusal lines -- written to the process's stderr, like the -Describe
    # refusal -- are capturable at all.
    #
    # It runs FROM the store's own folder -- where an operator stands, and how
    # a URL or a label finds its store. A test about being somewhere else says
    # so by passing the store it is standing in and naming the capture by path.
    function Invoke-PhaseDoor {
        param(
            [Parameter(Mandatory)][object]$Store,
            [Parameter(Mandatory)][string[]]$Arguments,
            [string]$Script = $script:InvokePs1
        )

        Push-Location -LiteralPath $Store.Base
        try {
            $out = & $script:Pwsh -NoProfile -File $Script @Arguments 2>&1
            $code = $LASTEXITCODE
        }
        finally { Pop-Location }
        # A child pwsh colours its WARNING lines when the host says it may
        # (hosted CI on Linux, Git Bash): strip the escape codes so a match on
        # '^WARNING:' reads what the operator reads, not the terminal's markup.
        $text = ($out | Out-String) -replace "`e\[[0-9;?]*[A-Za-z]", ''
        [pscustomobject]@{ ExitCode = $code; Text = $text }
    }

    # The front door IN this process, for what a child process flattens to
    # text: the objects on the success stream, and each stream on its own.
    function Invoke-PhaseDoorInProcess {
        param(
            [Parameter(Mandatory)][object]$Store,
            [Parameter(Mandatory)][hashtable]$Arguments
        )

        $streams = Join-Path ([IO.Path]::GetTempPath()) ('har-phases-streams-' + [guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $streams -Force | Out-Null
        $infoFile = Join-Path $streams 'info.txt'
        $warnFile = Join-Path $streams 'warn.txt'
        Push-Location -LiteralPath $Store.Base
        try {
            $rows = @(& $script:InvokePs1 @Arguments 6> $infoFile 3> $warnFile)
            $code = $LASTEXITCODE
        }
        finally { Pop-Location }
        $read = { param($p) $t = if (Test-Path -LiteralPath $p) { Get-Content -LiteralPath $p -Raw }; if ($null -eq $t) { '' } else { $t } }
        $result = [pscustomobject]@{
            ExitCode    = $code
            Rows        = $rows
            Information = (& $read $infoFile)
            Warning     = (& $read $warnFile)
        }
        Remove-Item -LiteralPath $streams -Recurse -Force -WhatIf:$false -ErrorAction SilentlyContinue
        return $result
    }

    # Everything a recording would have changed: the set of directories in the
    # store, and the bytes of every raw and every session.json in it.
    function Get-StoreFingerprint {
        param([Parameter(Mandatory)][object]$Store)

        $dirs = @(Get-ChildItem -LiteralPath $Store.Captures -Recurse -Directory -Force |
                ForEach-Object { $_.FullName.Substring($Store.Captures.Length) } | Sort-Object)
        $bytes = @(Get-ChildItem -LiteralPath $Store.Captures -Recurse -File -Force |
                Where-Object { $_.Name -in 'raw.har', 'raw.mitm', 'session.json' } | Sort-Object FullName |
                ForEach-Object { "$($_.FullName.Substring($Store.Captures.Length)) $((Get-FileHash -LiteralPath $_.FullName).Hash)" })
        return (@($dirs) + @($bytes)) -join "`n"
    }

    # A copy of the scripts tree whose leak gate returns a chosen verdict.
    #
    # A rejection cannot be reached with a payload -- the scrub replaces what is
    # planted for it, which is the point of the scrub. So the VERDICT is
    # stubbed, the technique HarBatchStore.Tests.ps1 and
    # capture-quarantine.test.js already use, while the front door, the scrub,
    # the quarantine and the recorder under test stay real.
    #
    # THE GATE IS ASKED TWICE, AND THE TWO ASKERS GET THEIR OWN ANSWER. The
    # scrub wrapper asks about the candidate it just wrote, under its temporary
    # name; the recorder's `catalogue` asks again about scrubbed.har. One
    # answer for both could not tell "the catalogue ran on what the scrub just
    # produced" from "the catalogue ran on whatever was lying there" -- the
    # mutant that dropped the scrub's exit code survived exactly that way. So
    # the stub keys on the file it is handed. -RecorderVerdict defaults to the
    # wrapper's, which is what a real gate would say of the same bytes.
    function New-StubbedGateDoor {
        param(
            [Parameter(Mandatory)][object]$Store,
            [Parameter(Mandatory)][int]$Verdict,
            [int]$RecorderVerdict = -1
        )

        if ($RecorderVerdict -lt 0) { $RecorderVerdict = $Verdict }
        $copy = Join-Path $Store.Base 'stub-scripts'
        Copy-Item -LiteralPath $script:ScriptsDir -Destination $copy -Recurse -Force
        $stub = @"
const fs = require('fs'), path = require('path');
const target = process.argv[process.argv.indexOf('--in') + 1];
const candidate = path.basename(target) === '.scrubbing-scrubbed.har';
const code = candidate ? $Verdict : $RecorderVerdict;
// verify-scrub.js writes its findings report beside the file it verified on a
// run that is not clean -- with the verdict in it -- and writes none on a clean
// one. A refusal is specified to move that report with the artifact.
if (code !== 0) {
    fs.writeFileSync(path.join(path.dirname(target), 'scrub-findings.json'), JSON.stringify({
        schemaVersion: 1, generatedUtc: new Date().toISOString(), target: path.basename(target),
        verdict: code === 4 ? 'advisory' : 'gating', findings: [], stub: true }));
}
process.stderr.write('verify-scrub: stubbed verdict ' + code + '\n');
process.exit(code);
"@
        Set-Content -LiteralPath (Join-Path $copy 'har/verify-scrub.js') -Value $stub -Encoding utf8
        return (Join-Path $copy 'capture/Invoke-HarCapture.ps1')
    }
}

AfterAll {
    $env:CLAUDECODE = $script:SavedClaudeCode
    $env:CLAUDE_CODE_ENTRYPOINT = $script:SavedEntryPoint
}

Describe 'Invoke-HarCapture -Scrub and -Catalogue run one phase on a capture that exists' {

    BeforeEach { $script:Store = New-PhaseStore }

    AfterEach { Remove-PhaseStore -Store $script:Store }

    It '-Scrub scrubs and verifies the capture, and stops there' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue `
            -Because 'the scrub is the phase that was asked for'
        Test-Path -LiteralPath (Join-Path $capture 'digest.json') | Should -BeFalse `
            -Because '-Scrub alone does not catalogue'
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse
        # "And its verification": the scrub wrapper says what the gate decided.
        $run.Text | Should -Match 'Scrub verdicts: 1 clean'
    }

    It '-Scrub scrubs again a capture that was already scrubbed' {
        # The reason to ask: the scrubber improved, or a finding was waived. A
        # scrub that answered "already done" would make the switch a no-op on
        # every capture a recording had finished.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Catalogued
        Set-Content -LiteralPath (Join-Path $capture 'scrubbed.har') -Value 'STALE-SCRUB' -Encoding utf8

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        (Get-Content -LiteralPath (Join-Path $capture 'scrubbed.har') -Raw) | Should -Not -Match 'STALE-SCRUB'
        $run.Text | Should -Not -Match 'already scrubbed'
        # What it leaves stale is said, because nothing else would say it.
        $run.Text | Should -Match 'derived from the previous scrub'
    }

    It '-Catalogue digests and catalogues a scrubbed capture, and emits its rows' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Catalogue = $true }

        $run.ExitCode | Should -Be 0
        Test-Path -LiteralPath (Join-Path $capture 'digest.json') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
        # The documented output of this command, so `... -Catalogue |
        # Where-Object Status -eq Observed` composes exactly as a recording does.
        $run.Rows.Count | Should -BeGreaterThan 0
        $run.Rows[0].PSObject.TypeNames | Should -Contain 'WebApiDiscovery.HarCapture.CatalogueEntry'
        $run.Rows[0].Status | Should -Be 'Observed'
    }

    It '-Scrub alone puts nothing on the success stream' {
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' | Out-Null

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Scrub = $true }

        $run.ExitCode | Should -Be 0
        $run.Rows.Count | Should -Be 0 -Because 'no catalogue was asked for, and status is not data'
        $run.Warning | Should -BeNullOrEmpty -Because 'a clean scrub of an ordinary capture has nothing to warn about'
    }

    It '-Scrub says so when the capture signed in with values only its recorder held' {
        # The recorder hands the values it signed in with to the scrub and to
        # the gate, and they live nowhere else -- not in session.json, not on
        # disk. A scrub run afterwards removes them by field name only and
        # cannot prove them gone, so the scrubbed.har it writes is not checked
        # the way the one it replaces was. Silence about that is the defect.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -SignIn 'bitwarden'

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Scrub = $true }

        $run.Warning | Should -Match 'www\.example\.test/2026-01-02-000001 signed in'
        $run.Warning | Should -Match 'by field name'
        # Said, not refused: a capture that signed in is still worth scrubbing
        # again, and refusing would put the switch out of reach of most of them.
        $run.ExitCode | Should -Be 0
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue
    }

    It '-Scrub does not say it of a capture whose operator signed in by hand' {
        # Nothing was filled by the recorder, so there are no held values for
        # this scrub to be missing.
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -SignIn 'none' | Out-Null

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Scrub = $true }

        $run.ExitCode | Should -Be 0
        $run.Warning | Should -Not -Match 'signed in'
    }

    It '-Scrub treats a sign-in source it has never heard of as a sign-in' {
        # The recorder owns the list of ways it signs in. A copy of that list
        # here would go quiet the day a third way is added, which is the wrong
        # direction to fail in: anything that is not "nobody filled anything"
        # is treated as values this scrub cannot be given.
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -SignIn 'a-source-added-later' | Out-Null

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Scrub = $true }

        $run.Warning | Should -Match 'signed in'
    }

    It '-Scrub will not replace a scrubbed.har that was checked against the sign-in values' {
        # FOUND IN REVIEW. Warning and going ahead was right for a capture with
        # nothing scrubbed yet, and wrong here: the scrubbed.har a recording
        # leaves behind was scrubbed WITH the values the recorder filled and
        # the gate proved them absent. Those values are gone for good, so a
        # re-scrub can only produce a weaker artifact -- and scrubbing "again"
        # overwrites the stronger one, with nothing able to put it back.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -SignIn 'prompt'
        $before = (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'www\.example\.test/2026-01-02-000001 signed in'
        # The way through is said, and it is a deliberate act, not a switch.
        $run.Text | Should -Match 'move scrubbed\.har aside'
        (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash | Should -Be $before
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse -Because 'a refusal runs nothing'
    }

    It '-Scrub will not replace a scrubbed.har when session.json cannot say whether the recorder signed in' {
        # FOUND IN REVIEW, and the same mistake one level down. session.json is
        # where the sign-in is recorded; a torn or hand-edited one answered
        # "did not sign in", so the refusal above could be switched off by
        # damage to the one file it reads. Not knowing is not the same as no.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed
        Set-Content -LiteralPath (Join-Path $capture 'session.json') -Value '{ "describe": "a torn wri' -Encoding utf8
        $before = (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'session\.json that cannot be read'
        $run.Text | Should -Match 'move scrubbed\.har aside'
        (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash | Should -Be $before
    }

    It '-Scrub still scrubs a capture with an unreadable session.json when there is nothing to replace' {
        # The same terms as a known sign-in: with no scrubbed.har there is no
        # better artifact to lose, so the scrub runs -- and says what it could
        # not find out.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        Set-Content -LiteralPath (Join-Path $capture 'session.json') -Value '{ "describe": "a torn wri' -Encoding utf8

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Scrub = $true }

        $run.ExitCode | Should -Be 0
        $run.Warning | Should -Match 'session\.json that cannot be read'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue
    }

    It '-Scrub is not held up by a caller''s lowered $ConfirmPreference' {
        # The other half of the $WhatIfPreference case. The scrub wrapper asks
        # ShouldProcess per capture; a session that set $ConfirmPreference to
        # Low would have it PROMPT, and a run with nobody at a terminal -- an
        # agent, which is who runs this -- fails on a prompt it cannot answer.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        $door = $script:InvokePs1.Replace("'", "''")
        $command = "`$ConfirmPreference = 'Low'; & '$door' https://www.example.test -Scrub; exit `$LASTEXITCODE"

        Push-Location -LiteralPath $script:Store.Base
        try {
            $out = & $script:Pwsh -NoProfile -NonInteractive -Command $command 2>&1
            $exit = $LASTEXITCODE
        }
        finally { Pop-Location }

        $exit | Should -Be 0 -Because ($out | Out-String)
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue
    }

    It '-Scrub -Catalogue says what is left stale when the scrub ran and the catalogue was refused' {
        # The scrub has already replaced scrubbed.har by the time the recorder
        # declines to replace a catalogue somebody worked on. Its refusal says
        # "nothing was written", which is true of the catalogue and silent
        # about the fact that the catalogue now describes a scrub that is gone.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -DescribedCatalogue
        Set-Content -LiteralPath (Join-Path $capture 'scrubbed.har') -Value 'STALE-SCRUB' -Encoding utf8

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Be 2 -Because $run.Text
        (Get-Content -LiteralPath (Join-Path $capture 'scrubbed.har') -Raw) | Should -Not -Match 'STALE-SCRUB' `
            -Because 'the scrub phase did run'
        $run.Text | Should -Match 'derived from the previous scrub'
    }

    It '-Scrub on a capture recorded outside a repository says how to make it scrubbable' {
        # FOUND IN REVIEW. Recording in a plain folder is supported: the
        # recorder writes a .gitignore beside the store and places the scrub's
        # substitution tables itself. The scrub re-entry point will not -- it
        # writes them only where git confirms they are ignored -- and what it
        # says to do about that ("pass --subs") cannot be done through this
        # door. So the run fails, as it must, and says what CAN be done.
        $plain = New-PhaseStore -NoRepository
        try {
            $capture = Add-PhaseCapture -Store $plain -Stamp '2026-01-02-000001' -UnprotectedTables

            $run = Invoke-PhaseDoor -Store $plain -Arguments @('https://www.example.test', '-Scrub', '-Catalogue')

            $run.ExitCode | Should -Not -Be 0 -Because $run.Text
            Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeFalse
            Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse
            $run.Text | Should -Match 'recorded outside a git repository'
            $run.Text | Should -Match 'git init'
        }
        finally { Remove-Item -LiteralPath $plain.Base -Recurse -Force -ErrorAction SilentlyContinue }
    }

    It '-Catalogue says so when the capture also holds a scrub the gate rejected' {
        # A verified scrubbed.har and a quarantined one side by side: a
        # re-scrub was refused, or an earlier refusal was since corrected. The
        # catalogue is derived from the verified one either way -- and an
        # operator who has just watched a scrub be REJECTED should not have to
        # work out which file a catalogue that then succeeds came from.
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -Rejected | Out-Null

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Catalogue = $true }

        $run.ExitCode | Should -Be 0
        $run.Information | Should -Match 'REJECTED'
        $run.Information | Should -Match 'derived from scrubbed\.har'
    }

    It 'runs the same way as the module command, where StrictMode is in force' {
        # The WebApiDiscovery module generates Invoke-HarCapture from this
        # script and sets StrictMode Latest, which reaches the script through
        # the command. A property read that only works without it would pass
        # every test that calls the script by path and fail for the operator.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        $manifest = (Join-Path $script:RepoRoot 'WebApiDiscovery.psd1').Replace("'", "''")
        $command = "Import-Module '$manifest'; Invoke-HarCapture https://www.example.test -Scrub -Catalogue | Out-Null; exit `$LASTEXITCODE"

        Push-Location -LiteralPath $script:Store.Base
        try {
            $out = & $script:Pwsh -NoProfile -Command $command 2>&1
            $exit = $LASTEXITCODE
        }
        finally { Pop-Location }

        $exit | Should -Be 0 -Because ($out | Out-String)
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }

    It '-Scrub -Catalogue scrubs first, then catalogues what it scrubbed' {
        # A raw-only capture: there is no scrubbed.har for the catalogue to be
        # derived from unless the scrub ran first.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        foreach ($artifact in 'scrubbed.har', 'digest.json', 'catalogue.json') {
            Test-Path -LiteralPath (Join-Path $capture $artifact) | Should -BeTrue -Because "$artifact is what the two phases produce"
        }
    }

    It 'the order the switches are typed in does not change the order the phases run in' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test', '-Catalogue', '-Scrub')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }

    It 'records nothing: no new capture, and the raw is untouched' {
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' | Out-Null
        Add-PhaseCapture -Store $script:Store -Site 'www.other.test' -Stamp '2026-01-03-000001' | Out-Null
        $before = Get-StoreFingerprint -Store $script:Store

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Get-StoreFingerprint -Store $script:Store | Should -Be $before `
            -Because 'a phase run adds no session directory and rewrites no raw and no session.json'
        $run.Text | Should -Not -Match 'press ENTER' -Because 'there is no recording to end'
        $run.Text | Should -Match 'Nothing is recorded'
        # The other site's capture was not this run's business.
        Test-Path -LiteralPath (Join-Path $script:Store.Captures 'www.other.test/2026-01-03-000001/scrubbed.har') |
            Should -BeFalse
    }

    It 'does not need -Describe: the capture already has one' {
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed | Out-Null

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        $run.Text | Should -Not -Match 'refusing to record without -Describe'
    }

    It 'still scrubs under an ambient $WhatIfPreference' {
        # The scrub wrapper supports ShouldProcess; this front door does not
        # and rejects -WhatIf. So a preference set in a profile or an outer
        # scope would otherwise reach the wrapper and turn -Scrub into a run
        # that reports and writes nothing -- while looking like success.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'

        # The preference lives in a scope of its own: set in this block it
        # would reach the fixture's own cleanup and leave the store behind.
        Push-Location -LiteralPath $script:Store.Base
        try {
            & {
                $WhatIfPreference = $true
                & $script:InvokePs1 -Uri 'https://www.example.test' -Scrub -InformationAction Ignore | Out-Null
            }
            $exit = $LASTEXITCODE
        }
        finally { Pop-Location }

        $exit | Should -Be 0
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue -Because (
            'a dry-run preference the operator never asked this command for must not ' +
            'silently reduce the scrub to nothing')
    }
}

Describe 'it says which capture it selected' {

    BeforeEach { $script:Store = New-PhaseStore }

    AfterEach { Remove-PhaseStore -Store $script:Store }

    It 'names the capture by label, description and directory before it runs anything' {
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed `
            -Describe 'sign in, list the things' | Out-Null

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Catalogue = $true }

        $run.Information | Should -Match 'selected www\.example\.test/2026-01-02-000001'
        $run.Information | Should -Match 'sign in, list the things'
        # The directory, matched on its tail: the head of a temp path has two
        # spellings on Windows (8.3 and long), and git reports the long one.
        $run.Information | Should -Match '(?m)^\s+at .*\.har-captures[\\/]www\.example\.test[\\/]2026-01-02-000001\s*$'
    }

    It 'lets the caller silence the status lines, and still runs' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Catalogue = $true; InformationAction = 'Ignore' }

        $run.Information | Should -BeNullOrEmpty
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }

    It 'never echoes the rest of a URL, typed or recorded' {
        # The host names a capture. A magic-link or reset URL carries its token
        # in the path or the query -- both the one typed here and the one the
        # recording started from.
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed | Out-Null

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test/reset/TYPED-PATH-TOKEN?t=TYPED-QUERY-TOKEN', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        $run.Text | Should -Not -Match 'TYPED-PATH-TOKEN|TYPED-QUERY-TOKEN|FIXTURE-START-TOKEN'
        # And the host that IS permitted really is printed, or the assertion
        # above would pass on silence.
        $run.Text | Should -Match 'www\.example\.test/2026-01-02-000001'
    }

    It 'does not echo a first argument it could not read as a URL, a directory or a label' {
        # FOUND IN REVIEW. A URL pasted without its scheme is not a URL to the
        # parser, so it falls through to being looked up as a label -- and a
        # "no capture found for '<what you typed>'" then prints the reset token
        # it carried. What was typed is on the operator's own command line; the
        # refusal says how it was READ instead, which is the part they cannot
        # see.
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed | Out-Null

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'www.example.test/reset/TYPED-PATH-TOKEN', '-Catalogue')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Not -Match 'TYPED-PATH-TOKEN'
        $run.Text | Should -Match 'read as a capture label'
        $run.Text | Should -Match 'https://' -Because 'the likeliest slip is a URL typed without its scheme'
    }

    It 'refuses a wildcard by its spelling, and does not echo that either' {
        # Remove-HarCapture's rule, for the same reason: a pattern that happened
        # to match one capture and was accepted would teach the habit, and the
        # next one would match four. A capture is NAMED. A URL is exempt -- `?`
        # is how a query string starts -- but only a URL with its scheme is a
        # URL: pasted without one, its `?t=` makes it a pattern, and it is
        # refused here before anything is looked up.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'

        $pattern = Invoke-PhaseDoor -Store $script:Store -Arguments @('www.example.test/2026-*', '-Scrub')
        $pattern.ExitCode | Should -Be 2 -Because $pattern.Text
        $pattern.Text | Should -Match 'wildcard'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeFalse `
            -Because 'a pattern that matches exactly one capture is still a pattern'

        $pasted = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'www.example.test/reset/TYPED-PATH-TOKEN?t=TYPED-QUERY-TOKEN', '-Scrub')
        $pasted.ExitCode | Should -Be 2 -Because $pasted.Text
        $pasted.Text | Should -Not -Match 'TYPED-PATH-TOKEN|TYPED-QUERY-TOKEN'
        $pasted.Text | Should -Match 'https://'

        # And the query string of a real URL is not a wildcard.
        $url = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test/x?y=1', '-Scrub')
        $url.ExitCode | Should -Be 0 -Because $url.Text
    }

    It 'takes the capture''s own directory in place of a URL' {
        $first = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed
        $second = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000002' -Scrubbed

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($second, '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $second 'catalogue.json') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $first 'catalogue.json') | Should -BeFalse `
            -Because 'only the capture that was named is touched'
    }

    It 'takes the host/stamp label the refusal prints' {
        $first = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed
        $second = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000002' -Scrubbed

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'www.example.test/2026-01-02-000001', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $first 'catalogue.json') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $second 'catalogue.json') | Should -BeFalse
    }

    It 'narrows to the capture recorded with the -Describe it is given' {
        # The command that made a capture, plus -Catalogue, names that capture:
        # the description is how one recording of a site is told from another.
        $first = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -Describe 'browse the catalogue'
        $second = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000002' -Scrubbed -Describe 'create a post'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test', '-Describe', 'create a post', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $second 'catalogue.json') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $first 'catalogue.json') | Should -BeFalse
    }
}

Describe 'it refuses rather than guesses' {

    BeforeEach { $script:Store = New-PhaseStore }

    AfterEach { Remove-PhaseStore -Store $script:Store }

    It 'refuses a site with several captures by naming them, never picking the newest' {
        $first = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -Describe 'browse the catalogue'
        $second = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000002' -Scrubbed -Describe 'create a post'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 2 -Because $run.Text
        $run.Text | Should -Match '2 captures match www\.example\.test'
        $run.Text | Should -Match 'www\.example\.test/2026-01-02-000001 -- browse the catalogue'
        $run.Text | Should -Match 'www\.example\.test/2026-01-02-000002 -- create a post'
        # How to say which one, ready to paste: by label, and by directory.
        $run.Text | Should -Match 'Invoke-HarCapture www\.example\.test/2026-01-02-000002 -Catalogue'
        $run.Text | Should -Match "Invoke-HarCapture '[^']*\.har-captures[\\/]www\.example\.test[\\/]2026-01-02-000002' -Catalogue"
        foreach ($capture in $first, $second) {
            Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse `
                -Because 'a refusal runs nothing'
        }
    }

    It 'refuses a folder that holds several captures' {
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' | Out-Null
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000002' | Out-Null

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            (Join-Path $script:Store.Captures 'www.example.test'), '-Scrub')

        $run.ExitCode | Should -Be 2 -Because $run.Text
        $run.Text | Should -Match '2 captures match'
        Test-Path -LiteralPath (Join-Path $script:Store.Captures 'www.example.test/2026-01-02-000001/scrubbed.har') |
            Should -BeFalse
    }

    It 'refuses when no capture exists for the site' {
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' | Out-Null

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.nowhere.test', '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'no capture found for www\.nowhere\.test'
        # A capture that does not exist is not quietly recorded instead.
        Test-Path -LiteralPath (Join-Path $script:Store.Captures 'www.nowhere.test') | Should -BeFalse
    }

    It 'refuses when -Describe matches none of the site''s captures, and lists them' {
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -Describe 'browse the catalogue' | Out-Null

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test', '-Describe', 'something else', '-Catalogue')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match "none is described 'something else'"
        $run.Text | Should -Match 'www\.example\.test/2026-01-02-000001 -- browse the catalogue'
    }

    It 'refuses -Catalogue on a capture that was never scrubbed' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'www\.example\.test/2026-01-02-000001 has not been scrubbed'
        $run.Text | Should -Match '-Scrub -Catalogue' -Because 'the refusal names the way through'
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse `
            -Because 'a catalogue derived from a capture nobody scrubbed is worse than none: it looks safe'
        Test-Path -LiteralPath (Join-Path $capture 'digest.json') | Should -BeFalse
    }

    It 'refuses -Catalogue on a capture whose scrub the leak gate rejected' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Rejected

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'REJECTED by the leak gate'
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse
    }

    It 'refuses to re-scrub a signed-in capture whose scrub the gate REJECTED' {
        # FOUND IN REVIEW. The gate that rejected that scrub had the values the
        # recorder signed in with, and may have rejected it precisely because
        # one of them survived. A scrub run now has no such values: its gate
        # cannot see what the first one saw, so it can pass -- and call
        # scrubbed.har -- the very thing that was refused, and a catalogue is
        # then derived from it. The quarantined file blocks exactly as a
        # checked scrubbed.har does, and for the same reason.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Rejected -SignIn 'bitwarden'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'www\.example\.test/2026-01-02-000001 signed in'
        $run.Text | Should -Match 'REJECTED'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeFalse `
            -Because 'nothing the first gate refused may come out under the verified name'
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.rejected.har') | Should -BeTrue
    }

    It 'refuses it too when session.json cannot say whether the recorder signed in' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Rejected
        Set-Content -LiteralPath (Join-Path $capture 'session.json') -Value '{ "describe": "a torn wri' -Encoding utf8

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'session\.json that cannot be read'
        $run.Text | Should -Match 'REJECTED'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeFalse
    }

    It 'still re-scrubs a rejected capture nobody signed in for' {
        # The reason to ask for a scrub again: the scrubber was fixed, or a
        # finding was waived. With no sign-in there are no values the new gate
        # is missing, so this is the ordinary case and it runs.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Rejected

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue
    }

    It 'offers a paste-back command that survives an apostrophe in the path' {
        # The refusal prints the command that names one capture by directory,
        # in single quotes. A folder called O'Brien closed the quote halfway
        # through the path.
        $awkward = New-PhaseStore -NameSuffix "-o'brien"
        try {
            Add-PhaseCapture -Store $awkward -Stamp '2026-01-02-000001' | Out-Null
            Add-PhaseCapture -Store $awkward -Stamp '2026-01-02-000002' | Out-Null

            $run = Invoke-PhaseDoor -Store $awkward -Arguments @('https://www.example.test', '-Scrub')

            $run.ExitCode | Should -Be 2 -Because $run.Text
            $run.Text | Should -Match "Invoke-HarCapture '[^\r\n]*-o''brien[^\r\n]*2026-01-02-000002' -Scrub"
        }
        finally { Remove-PhaseStore -Store $awkward }
    }

    It 'does not offer a directory this command would then refuse as a pattern' {
        # A store under a folder with a bracket in its name: pasted back, that
        # path is a wildcard by its spelling and is refused. The label is
        # offered alone, and the label works there.
        $awkward = New-PhaseStore -NameSuffix '-[v2]'
        try {
            Add-PhaseCapture -Store $awkward -Stamp '2026-01-02-000001' | Out-Null
            Add-PhaseCapture -Store $awkward -Stamp '2026-01-02-000002' | Out-Null

            $run = Invoke-PhaseDoor -Store $awkward -Arguments @('https://www.example.test', '-Scrub')

            $run.ExitCode | Should -Be 2 -Because $run.Text
            $run.Text | Should -Match 'Invoke-HarCapture www\.example\.test/2026-01-02-000002 -Scrub'
            $run.Text | Should -Not -Match "Invoke-HarCapture '"
        }
        finally { Remove-PhaseStore -Store $awkward }
    }

    It 'refuses -Scrub on a capture with no raw' {
        Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -NoRaw | Out-Null

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'has no raw\.har'
    }

    It 'refuses a capture this recorder did not make' {
        # No session.json, so nothing can say whose traffic it is -- the same
        # reason the folder drivers decline it.
        $dump = Add-PhaseCapture -Store $script:Store -Site 'www.other.test' -Stamp '2026-01-03-000001' -Foreign

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($dump, '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'www\.other\.test/2026-01-03-000001 was not made by this recorder'
        Test-Path -LiteralPath (Join-Path $dump 'scrubbed.har') | Should -BeFalse
    }

    It 'refuses <Name> with a phase switch, at exit 2' -ForEach @(
        @{ Name = '-AI'; Extra = @('-AI') }
        @{ Name = '-OutputPath'; Extra = @('-OutputPath', 'somewhere') }
        @{ Name = '-Browser'; Extra = @('-Browser', 'Native') }
        @{ Name = '-Pace'; Extra = @('-Pace', 'off') }
        @{ Name = '-Isolated'; Extra = @('-Isolated') }
        @{ Name = '-Profile'; Extra = @('-Profile', 'work') }
        @{ Name = '-Port'; Extra = @('-Port', '9444') }
        @{ Name = '-Attach'; Extra = @('-Attach', '9222') }
        @{ Name = '-AITools'; Extra = @('-AITools', 'BrowserOnly') }
    ) {
        # Each of these says how to RECORD. Accepted and ignored, it would let
        # an operator believe it had done something to a run that records
        # nothing.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments (
            @('https://www.example.test', '-Catalogue') + $Extra)

        $run.ExitCode | Should -Be 2 -Because $run.Text
        $run.Text | Should -Match ([regex]::Escape($Name) + ' cannot be combined with -Scrub or -Catalogue')
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse
    }

    It 'does not refuse a recording switch that was typed as $false' {
        # `-AI:$false` asked for nothing. Read by presence in $PSBoundParameters
        # it would be refused as though it had -- the hazard every switch on
        # this command is read by value to avoid.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Catalogue = $true; AI = $false; Isolated = $false }

        $run.ExitCode | Should -Be 0
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }

    It 'carries through the recorder''s refusal to replace a catalogue somebody worked on' {
        # The recorder owns that rule and this door does not restate it: what is
        # pinned is that its exit code and its catalogue both survive.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -DescribedCatalogue
        $before = Get-Content -LiteralPath (Join-Path $capture 'catalogue.json') -Raw

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 2 -Because $run.Text
        $run.Text | Should -Match 'already carries 1 described'
        Get-Content -LiteralPath (Join-Path $capture 'catalogue.json') -Raw | Should -Be $before
    }
}

Describe 'the leak gate is not softened by re-entering' {

    BeforeEach { $script:Store = New-PhaseStore }

    AfterEach { Remove-PhaseStore -Store $script:Store }

    It 'quarantines a scrub the gate rejects, and catalogues nothing from it' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        $door = New-StubbedGateDoor -Store $script:Store -Verdict 3

        $run = Invoke-PhaseDoor -Store $script:Store -Script $door -Arguments @(
            'https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Not -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeFalse `
            -Because 'a scrub the gate refused is never published under the verified name'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.rejected.har') | Should -BeTrue `
            -Because 'every byte is kept for triage'
        Test-Path -LiteralPath (Join-Path $capture 'digest.json') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse `
            -Because 'the catalogue phase runs only after a scrub that passed'
        $run.Text | Should -Match 'leak gate REJECTED'
        $run.Text | Should -Match 'Nothing was catalogued'
    }

    It 'keeps the scrubbed capture a previous run verified when a re-scrub is rejected' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed
        $before = (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash
        $door = New-StubbedGateDoor -Store $script:Store -Verdict 3

        $run = Invoke-PhaseDoor -Store $script:Store -Script $door -Arguments @(
            'https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Not -Be 0 -Because $run.Text
        (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash | Should -Be $before `
            -Because 'a refused candidate replaces nothing'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.rejected.har') | Should -BeTrue
    }

    It 'keeps the artifact on an ADVISORY verdict, says so, and still catalogues' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        $door = New-StubbedGateDoor -Store $script:Store -Verdict 4

        $run = Invoke-PhaseDoor -Store $script:Store -Script $door -Arguments @(
            'https://www.example.test', '-Scrub', '-Catalogue')

        # 7 is the recorder's own code for "produced, with advisory findings",
        # passed through whole: non-zero so nothing reads the run as clean.
        $run.ExitCode | Should -Be 7 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
        $run.Text | Should -Match 'ADVISORY'
    }

    It 'does not blame a missing repository for a refusal that has another cause' {
        # FOUND IN REVIEW. "Recorded outside a git repository" is written into
        # session.json when the capture is made and never changes. The folder
        # can become a repository afterwards -- that is the remedy this very
        # command gives -- and from then on a failure is about something else.
        # Said on the flag alone, the hint would send an operator whose scrub
        # the gate REJECTED to run `git init` instead of to the findings.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -UnprotectedTables
        $door = New-StubbedGateDoor -Store $script:Store -Verdict 3

        $run = Invoke-PhaseDoor -Store $script:Store -Script $door -Arguments @(
            'https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Not -Be 0 -Because $run.Text
        $run.Text | Should -Match 'leak gate REJECTED' -Because 'that is what actually happened'
        $run.Text | Should -Not -Match 'recorded outside a git repository'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.rejected.har') | Should -BeTrue
    }

    It 'does not catalogue an older scrubbed.har when the scrub that was asked for is rejected' {
        # THE CASE THE FIRST VERSION OF THIS SUITE DID NOT PIN. With no older
        # scrubbed.har, a front door that ignored the scrub's failure and went
        # on to the catalogue was still stopped -- by the recorder, which found
        # nothing to catalogue -- so removing the check changed no test. With
        # one there, the recorder has a file it would happily pass: the stale
        # one. Only this door knows the scrub it just asked for did not produce
        # it. The gate here refuses the wrapper's candidate and would pass the
        # recorder's re-ask, which is what makes the difference observable.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed
        $before = (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash
        $door = New-StubbedGateDoor -Store $script:Store -Verdict 3 -RecorderVerdict 0

        $run = Invoke-PhaseDoor -Store $script:Store -Script $door -Arguments @(
            'https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Not -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse `
            -Because 'a catalogue of the older scrub is not what -Scrub -Catalogue was asked for'
        Test-Path -LiteralPath (Join-Path $capture 'digest.json') | Should -BeFalse
        (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash | Should -Be $before
        $run.Text | Should -Match 'Nothing was catalogued'
    }

    It 'works in the capture''s own directory when the recording published somewhere else' {
        # A capture recorded with -OutputPath published a COPY of its scrubbed
        # artifacts there and kept its own beside the raw. Asked for a
        # catalogue with no output path, the recorder reads the published copy
        # -- which a -Scrub just made stale. So the phases name the capture's
        # directory explicitly, and nothing here may land in, or be read from,
        # the other place.
        $published = Join-Path $script:Store.Base 'published/www.example.test'
        New-Item -ItemType Directory -Path $published -Force | Out-Null
        Write-FixtureHar -Path (Join-Path $published 'scrubbed.har') -Url 'https://api.example.test/v1/stale-published-copy'
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -OutputPath $published

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        $catalogue = Get-Content -LiteralPath (Join-Path $capture 'catalogue.json') -Raw
        $catalogue | Should -Match 'v1/things' -Because 'it is derived from the scrub this run just wrote'
        $catalogue | Should -Not -Match 'stale-published-copy'
        foreach ($artifact in 'digest.json', 'catalogue.json') {
            Test-Path -LiteralPath (Join-Path $published $artifact) | Should -BeFalse `
                -Because 'the phases work in the capture''s own directory and do not republish'
        }
    }

    It '-Scrub alone answers 7 on an ADVISORY verdict, and warns even with the status lines silenced' {
        # FOUND IN REVIEW. The scrub wrapper answers 0 for an artifact it kept,
        # and says ADVISORY only in a summary on the information stream -- so
        # `-Scrub -InformationAction SilentlyContinue` replaced a clean
        # scrubbed.har with an advisory one, exited 0, and said nothing at all.
        # The verdict is read from the findings report the gate itself writes
        # for a run that is not clean, never from the summary's wording; it is
        # said on the WARNING stream, which silencing status does not reach;
        # and the code is the recorder's own for "produced, with advisory
        # findings", the one -Scrub -Catalogue already answers.
        #
        # THE REAL SCRUB AND THE REAL GATE, not a stub: what is read here is a
        # file another script writes, so the test has to be about the file that
        # script really writes. A bare 16-digit number that passes the card
        # checksum is identity evidence by SHAPE alone -- a card, an order id,
        # or chance -- which is exactly what the gate reports and does not
        # block on, and the scrub has no rule to take it out.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Body '{"n":4539578763621486}'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test', '-Scrub', '-InformationAction', 'Ignore')

        $run.ExitCode | Should -Be 7 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue -Because 'an advisory verdict keeps the artifact'
        $run.Text | Should -Match '(?m)^WARNING: .*ADVISORY'
        $run.Text | Should -Not -Match 'Scrub verdicts' -Because 'the status lines really were silenced, or the line above proves nothing'
        # The contract this door reads, pinned on the gate's own output: the
        # report beside the capture, and the verdict in it as data.
        $report = Get-Content -LiteralPath (Join-Path $capture 'scrub-findings.json') -Raw | ConvertFrom-Json
        $report.verdict | Should -Be 'advisory'
    }

    It '-Scrub -Catalogue answers 7 on that verdict too, from the recorder' {
        # The same capture through both phases, still with the real gate: the
        # recorder asks it again and answers its own code.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Body '{"n":4539578763621486}'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub', '-Catalogue')

        $run.ExitCode | Should -Be 7 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }

    It '-Scrub is not misled by a findings report an earlier run left behind' {
        # The gate writes its report only for a run that is not clean, and
        # nothing removes the one a previous run wrote. Read by existence, an
        # old advisory report would turn every later clean scrub into exit 7.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        Set-Content -LiteralPath (Join-Path $capture 'scrub-findings.json') -Encoding utf8 -Value (
            @{ schemaVersion = 1; verdict = 'advisory'; findings = @() } | ConvertTo-Json)

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{
            Uri = 'https://www.example.test'; Scrub = $true }

        $run.ExitCode | Should -Be 0
        $run.Warning | Should -BeNullOrEmpty -Because 'this run came back clean'
    }

    It '-Scrub warns when values were BLUNTED, even with the status lines silenced' {
        # The other verdict that is not clean and still exits 0 from the
        # wrapper: values the gate would have blocked were replaced by typed
        # sentinels, so the artifact came out with less in it than a clean one.
        # THE REAL GATE, not a stub -- the planted value is one the scrub has
        # no rule for where it sits (a 32-hex run visible only once a JSON
        # escape is decoded), the shape HarBatchStore.Tests.ps1 uses for #511.
        $planted = 'a1b2c3d4e5f60718293a4b5c6d7e8f90'
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        $raw = Join-Path $capture 'raw.har'
        # Spliced in as JSON TEXT: ConvertTo-Json would decode the `/` escape
        # while writing, which removes exactly the spelling this needs.
        $bodyJson = '"for (;;);{\"jsmods\":{\"require\":[[\"m\\u002F' + $planted + '\",1]]}}"'
        (Get-Content -LiteralPath $raw -Raw).Replace('"{\"ok\":true}"', $bodyJson) |
            Set-Content -LiteralPath $raw -Encoding utf8 -NoNewline
        (Get-Content -LiteralPath $raw -Raw) | Should -Match ([regex]::Escape('\\u002F' + $planted))

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @(
            'https://www.example.test', '-Scrub', '-InformationAction', 'Ignore')

        (Get-Content -LiteralPath (Join-Path $capture 'scrubbed.har') -Raw) | Should -Not -Match $planted
        $run.Text | Should -Match '(?m)^WARNING: .*1 blunted'
        $run.Text | Should -Not -Match $planted -Because 'the warning never carries a value'
    }

    It 'is not thrown off its exit code by a caller that treats native exit codes as errors' {
        # $PSNativeCommandUseErrorActionPreference is the caller's, and with it
        # on, the recorder answering 7 -- "produced, with advisory findings" --
        # became a terminating error: exit 1, no rows, and a catalogue that was
        # in fact written. Every code this door passes on is a NON-ZERO THAT
        # MEANS SOMETHING, so the script turns the preference off for itself.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed
        $door = (New-StubbedGateDoor -Store $script:Store -Verdict 4).Replace("'", "''")
        $command = "`$PSNativeCommandUseErrorActionPreference = `$true; & '$door' https://www.example.test -Catalogue | Out-Null; exit `$LASTEXITCODE"

        Push-Location -LiteralPath $script:Store.Base
        try {
            $out = & $script:Pwsh -NoProfile -Command $command 2>&1
            $exit = $LASTEXITCODE
        }
        finally { Pop-Location }

        $exit | Should -Be 7 -Because ($out | Out-String)
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }
}

Describe 'the phases run where the capture lives, not where the command was typed' {

    BeforeEach { $script:Store = New-PhaseStore }

    AfterEach { Remove-PhaseStore -Store $script:Store }

    It '-Scrub uses the operator profile beside the capture, from whatever folder it is run in' {
        # FOUND IN REVIEW, AND THE WORST OF IT. The scrub and the gate find
        # .har-profile.json -- the operator's own identifiers, and the salt
        # every pseudonym is derived from -- by walking up from the WORKING
        # DIRECTORY. Naming a capture by its directory made "from anywhere" a
        # documented way to run this, so a re-scrub typed in another repository
        # used THAT repository's profile: the operator's account handle went
        # back into scrubbed.har, the gate (same wrong profile) passed it, and
        # the run said "1 clean" and exited 0.
        $literal = 'zqx-operator-handle-7731'
        $owner = New-PhaseStore -Literals @{ $literal = '<AccountHandle>' }
        try {
            $capture = Add-PhaseCapture -Store $owner -Stamp '2026-01-02-000001' -Body ('{"note":"owner ' + $literal + '"}')
            (Get-Content -LiteralPath (Join-Path $capture 'raw.har') -Raw) | Should -Match $literal `
                -Because 'the raw has to carry the identifier, or nothing below is being tested'

            # Typed in the OTHER repository, whose profile maps nothing.
            $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($capture, '-Scrub')

            $run.ExitCode | Should -Be 0 -Because $run.Text
            $scrubbed = Get-Content -LiteralPath (Join-Path $capture 'scrubbed.har') -Raw
            $scrubbed | Should -Not -Match $literal -Because 'the profile that applies is the one beside the capture'
            $scrubbed | Should -Match '<AccountHandle>'
        }
        finally { Remove-PhaseStore -Store $owner }
    }

    It '-Catalogue asks the leak gate with the profile beside the capture too' {
        # The recorder asks the gate again before it derives anything, and the
        # gate's literal check is only as good as the profile it is handed. A
        # scrubbed.har that still carries the operator's identifier is refused
        # by the capture's own profile and waved through by anybody else's.
        $literal = 'zqx-operator-handle-7731'
        $owner = New-PhaseStore -Literals @{ $literal = '<AccountHandle>' }
        try {
            $capture = Add-PhaseCapture -Store $owner -Stamp '2026-01-02-000001'
            Write-FixtureHar -Path (Join-Path $capture 'scrubbed.har') -Body ('{"note":"owner ' + $literal + '"}')

            $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($capture, '-Catalogue')

            $run.ExitCode | Should -Be 6 -Because $run.Text
            Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse `
                -Because 'no catalogue is derived from a capture the gate refuses'
        }
        finally { Remove-PhaseStore -Store $owner }
    }

    It 'leaves the caller where they were, whether the phase worked or not' {
        # The phases change directory to run. A command that returned the
        # operator somewhere else -- on a failure most of all -- would have the
        # NEXT thing they typed act on the wrong folder.
        $good = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        $bad = Add-PhaseCapture -Store $script:Store -Site 'www.other.test' -Stamp '2026-01-03-000001'
        Set-Content -LiteralPath (Join-Path $bad 'raw.har') -Value 'NOT-A-HAR' -Encoding utf8
        $elsewhere = Join-Path $script:Store.Base 'somewhere/else'
        New-Item -ItemType Directory -Path $elsewhere -Force | Out-Null

        Push-Location -LiteralPath $elsewhere
        try {
            & $script:InvokePs1 -Uri $good -Scrub -Catalogue -InformationAction Ignore | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Get-Location).ProviderPath | Should -Be (Get-Item -LiteralPath $elsewhere).FullName

            & $script:InvokePs1 -Uri $bad -Scrub -InformationAction Ignore 3> $null | Out-Null
            $LASTEXITCODE | Should -Not -Be 0 -Because 'that raw is not a capture at all'
            (Get-Location).ProviderPath | Should -Be (Get-Item -LiteralPath $elsewhere).FullName
        }
        finally { Pop-Location }
    }

    It 'runs from a location that is not a folder at all (#24 N2)' {
        # Its module was imported by a path with `..` in it, which PowerShell
        # resolved against the CURRENT location: from Env:\ or a registry drive
        # that is no folder, and the run ended at exit 1 before selecting
        # anything.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'

        Push-Location -LiteralPath 'Env:\'
        try {
            & $script:InvokePs1 -Uri $capture -Scrub -InformationAction Ignore 3> $null | Out-Null
            $code = $LASTEXITCODE
        }
        finally { Pop-Location }

        $code | Should -Be 0
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue
    }

    It 'does not say it is running from the store root when the caller is already there (#24 N3)' {
        # The comparison was of two spellings, not two folders: the store root
        # as the store walk names it, and the caller's location as it was
        # typed -- an 8.3 short name on Windows, a trailing separator, a
        # different letter case, a link to the folder. Stood in the store's own
        # folder reached through a link (a junction on Windows, which needs no
        # privilege), with the capture named by its real path.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        $link = Join-Path ([IO.Path]::GetTempPath()) ('har-phases-link-' + [guid]::NewGuid().ToString('n'))
        $kind = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
        New-Item -ItemType $kind -Path $link -Target $script:Store.Base | Out-Null
        try {
            $run = Invoke-PhaseDoor -Store ([pscustomobject]@{ Base = $link }) -Arguments @($capture, '-Scrub')
        }
        finally { [IO.Directory]::Delete($link) }

        $run.ExitCode | Should -Be 0 -Because $run.Text
        $run.Text | Should -Not -Match '(?m)^\s*from \S.* -- ' -Because 'the caller is already in the folder the phases run from'
    }

    It 'refuses a capture-shaped directory that is not inside a capture store' {
        # Remove-HarCapture's guard, shared rather than copied. The walk decides
        # what a capture is by the files in a directory and anchors to nothing;
        # the phases have to anchor, because the folder that holds
        # .har-captures is where they run and where the profile is found. A
        # stray session.json and raw.har somewhere else have neither.
        $loose = Join-Path $script:Store.Base 'my-documents/2026-01-02-000001'
        New-Item -ItemType Directory -Path $loose -Force | Out-Null
        Write-FixtureHar -Path (Join-Path $loose 'raw.har')
        Set-Content -LiteralPath (Join-Path $loose 'session.json') -Encoding utf8 -Value (
            @{ sessionDir = $loose; endedUtc = '2026-01-02T00:05:00.000Z' } | ConvertTo-Json)

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($loose, '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'not inside a capture store'
        Test-Path -LiteralPath (Join-Path $loose 'scrubbed.har') | Should -BeFalse
    }
}

Describe 'a re-scrub uses the operator profile the recording scrubbed with (#24 N1)' {

    BeforeEach {
        # The store root's profile maps NOTHING; a subfolder the recording was
        # started in has its own, which maps the operator's account id. The
        # recording scrubbed with the subfolder's, because a scrub finds the
        # profile from its working directory -- and the store is anchored at the
        # root, so a re-scrub from there found the root's.
        $script:Store = New-PhaseStore
        $script:Literal = 'acct-zqx-55190-operator'
        $script:Sub = Join-Path $script:Store.Base 'team-a'
        New-Item -ItemType Directory -Path $script:Sub -Force | Out-Null
        $script:SubProfile = Join-Path $script:Sub '.har-profile.json'
        Set-Content -LiteralPath $script:SubProfile -Encoding utf8 -Value (
            @{ salt = 'har-capture-phases-fixture-salt'; literals = @{ $script:Literal = '<AccountId>' } } | ConvertTo-Json)
    }

    AfterEach { Remove-PhaseStore -Store $script:Store }

    It 'does not put back an identifier the recording''s profile replaced (the reviewer''s reproduction)' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' `
            -Body ('{"account":"' + $script:Literal + '"}') -ScrubProfilePath $script:SubProfile
        # What the recording's own scrub left: the id replaced by its sentinel.
        Write-FixtureHar -Path (Join-Path $capture 'scrubbed.har') -Body '{"account":"<AccountId>"}'
        (Get-Content -LiteralPath (Join-Path $capture 'raw.har') -Raw) | Should -Match $script:Literal `
            -Because 'the raw has to carry the id, or nothing below is being tested'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($capture, '-Scrub')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        $scrubbed = Get-Content -LiteralPath (Join-Path $capture 'scrubbed.har') -Raw
        $scrubbed | Should -Not -Match $script:Literal -Because 'the re-scrub has to use the profile the recording used'
        $scrubbed | Should -Match '<AccountId>'
        $run.Text | Should -Match ([regex]::Escape($script:SubProfile)) `
            -Because 'it says which operator profile it scrubbed with'
    }

    It 'refuses when the profile the recording used no longer exists, and replaces nothing' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed `
            -Body ('{"account":"' + $script:Literal + '"}') -ScrubProfilePath $script:SubProfile
        $before = (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash
        # -Force: on Linux a dot-file is hidden, and Remove-Item refuses a hidden item without it.
        Remove-Item -LiteralPath $script:SubProfile -Force

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($capture, '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'no longer exists'
        # Said as the thing to DO, with the path in it (#24 S2): the operator
        # reading this needs the remedy, not an inference from the diagnosis.
        $run.Text | Should -Match ('copy the profile back to\s+' + [regex]::Escape($script:SubProfile))
        $run.Text | Should -Match 'worktree' -Because 'a recording made in a removed worktree is the usual cause'
        (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash | Should -Be $before
    }

    It 'refuses to replace the scrub of a capture that does not record its profile' {
        # Recorded before the recorder said which profile it used: nothing says
        # the store root's profile is the one that made the scrubbed.har there.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -NoProfileRecord
        $before = (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($capture, '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'does not record which operator profile'
        (Get-FileHash -LiteralPath (Join-Path $capture 'scrubbed.har')).Hash | Should -Be $before
    }

    It 'still scrubs such a capture when there is no scrub to replace, and says which profile it used' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -NoProfileRecord

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($capture, '-Scrub')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue
        $run.Text | Should -Match 'does not record which operator profile'
    }

    It 'refuses to scrub such a capture over a quarantined, rejected scrub (#24 I1, X2)' {
        # The quarantined scrub was gated with a profile nothing names -- maybe
        # the subfolder's, which maps the id. Re-scrubbed with the store root's
        # (which maps nothing) the id survives, that gate (same profile) passes
        # it, and the rejection is overridden by a "clean" scrubbed.har.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Rejected -NoProfileRecord `
            -Body ('{"account":"' + $script:Literal + '"}')

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($capture, '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'does not record which operator profile'
        $run.Text | Should -Match 'scrubbed\.rejected' -Because 'it says what to move aside to go on'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeFalse
    }

    It 'warns, naming the profile, when it scrubs such a capture for the first time (#24 I1, X1)' {
        # Nothing says the store root's profile is the recording's. That is
        # something to check, not a status line -- so it is on the WARNING
        # stream, which silencing the status lines does not reach.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -NoProfileRecord `
            -Body ('{"account":"' + $script:Literal + '"}')

        $run = Invoke-PhaseDoorInProcess -Store $script:Store -Arguments @{ Uri = $capture; Scrub = $true }

        $run.ExitCode | Should -Be 0 -Because ($run.Information + $run.Warning)
        $run.Warning | Should -Match ([regex]::Escape((Join-Path $script:Store.Base '.har-profile.json')))
        $run.Warning | Should -Match 'check the result for your own identifiers'
    }

    It 'scrubs such a capture with the store root''s profile, not the caller''s folder''s (#24 S5)' {
        # The store root's profile maps the id; the folder the command is typed
        # in has one that maps nothing. With no recorded path the scrub finds
        # its profile from its working directory, so it has to RUN from the
        # store root.
        $owner = New-PhaseStore -Literals @{ $script:Literal = '<AccountId>' }
        try {
            $capture = Add-PhaseCapture -Store $owner -Stamp '2026-01-02-000001' -NoProfileRecord `
                -Body ('{"account":"' + $script:Literal + '"}')

            $run = Invoke-PhaseDoor -Store $script:Store -Arguments @($capture, '-Scrub')

            $run.ExitCode | Should -Be 0 -Because $run.Text
            $scrubbed = Get-Content -LiteralPath (Join-Path $capture 'scrubbed.har') -Raw
            $scrubbed | Should -Not -Match $script:Literal -Because 'the profile that applies is the store root''s'
            $scrubbed | Should -Match '<AccountId>'
        }
        finally { Remove-PhaseStore -Store $owner }
    }
}

Describe 'a capture its recorder is still working on is left alone' {

    BeforeEach { $script:Store = New-PhaseStore }

    AfterEach { Remove-PhaseStore -Store $script:Store }

    # A recorder writes its process id and start time into session.json as it
    # begins, `endedUtc` when the recording stops, and `postProcess` only when
    # its own scrub and catalogue -- the AI pass included -- have finished. In
    # between, it is writing the very files a phase run would write.
    #
    # THE RECORDER HERE IS THIS TEST PROCESS: alive for as long as the run
    # under test, and older than a `startedUtc` of now, as a real recorder is
    # older than the session it opens.

    It 'refuses -Scrub while the recording is still going' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -NotEnded `
            -RecorderId $PID -StartedUtc ([DateTime]::UtcNow.ToString('o'))

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'still in the hands of the recorder'
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeFalse
    }

    It 'refuses -Catalogue while the recorder is still post-processing' {
        # Ended, scrubbed, and the recorder's own catalogue pass still running:
        # the window that lasts minutes, and the one a -Catalogue is most
        # likely to be typed in.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed `
            -RecorderId $PID -StartedUtc ([DateTime]::UtcNow.ToString('o'))

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Match 'still in the hands of the recorder'
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeFalse
    }

    It 'runs on a capture whose recorder was killed before it finished' {
        # THE CASE THE FEATURE EXISTS FOR, and the reason the refusal cannot be
        # "no postProcess in session.json": a recorder killed during its AI
        # pass leaves exactly that, for good. Its process is gone, so nothing
        # is being raced.
        $gone = Start-Process -FilePath $script:Pwsh -ArgumentList '-NoProfile', '-Command', 'exit' -PassThru
        $gone.WaitForExit()
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed `
            -RecorderId $gone.Id -StartedUtc ([DateTime]::UtcNow.ToString('o'))

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }

    It 'is not stopped by an unrelated process that inherited the recorder''s process id' {
        # A process id is reused. The recorder's own liveness test cannot tell
        # (it says so), and here that would refuse a capture for as long as
        # some stranger happened to be running. A recorder is older than the
        # session it opened; a process that started AFTER the recording did is
        # not that recorder.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed `
            -RecorderId $PID -StartedUtc '2026-01-01T00:00:00.000Z'

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }

    It 'runs on a capture whose recorder finished, even while that process is still there' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed -PostProcessed `
            -RecorderId $PID -StartedUtc ([DateTime]::UtcNow.ToString('o'))

        $run = Invoke-PhaseDoor -Store $script:Store -Arguments @('https://www.example.test', '-Catalogue')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'catalogue.json') | Should -BeTrue
    }
}

Describe 'the approved command-line surface, and nothing more' {

    It 'Invoke-HarCapture.ps1 gained exactly two parameters, both switches' {
        # -Scrub and -Catalogue are the two the owner named. Which capture they
        # act on is said with parameters that already existed -- the positional
        # first argument and -Describe -- so nothing else had to arrive.
        $command = Get-Command $script:InvokePs1
        $own = $command.Parameters.Keys |
            Where-Object { $_ -notin [System.Management.Automation.PSCmdlet]::CommonParameters -and
                           $_ -notin [System.Management.Automation.PSCmdlet]::OptionalCommonParameters }
        ($own | Sort-Object) -join ',' | Should -Be (
            'AI,AITools,Attach,Browser,Catalogue,Describe,Isolated,OutputPath,Pace,Port,Profile,Scrub,Uri')
        $command.Parameters['Scrub'].SwitchParameter | Should -BeTrue
        $command.Parameters['Catalogue'].SwitchParameter | Should -BeTrue
    }

    It 'adds no command to the recorder' {
        # The phases re-enter through what already exists. A `scrub` command on
        # capture-har.js is the shape #48 points at, and it is new command-line
        # surface: an owner decision, not something a front-door change brings
        # in with it. (That the scrub and catalogue wrappers and the store walk
        # gained no option either is pinned where their surface already is, in
        # HarBatchStore.Tests.ps1.)
        $recorder = Get-Content -LiteralPath (Join-Path $script:ScriptsDir 'capture/capture-har.js') -Raw
        $recorder | Should -Not -Match "case 'scrub'"
        $commands = [regex]::Matches($recorder, "(?m)^\s*case '([a-z-]+)': return ") |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object
        ($commands -join ',') | Should -Be 'catalogue,start,status,stop'
    }
}
