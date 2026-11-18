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
        param([Parameter(Mandatory)][string]$Path, [string]$Body = '{"ok":true}')

        @{
            log = @{
                version = '1.2'
                creator = @{ name = 'fixture'; version = '1' }
                entries = @(@{
                        startedDateTime = '2026-01-01T00:00:00.000Z'
                        time            = 1
                        request         = @{ method = 'GET'; url = 'https://api.example.test/v1/things'
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
        # -NoRepository is the folder an agent records in before anybody has
        # run `git init`: the recorder writes the .gitignore beside the store
        # and records anyway, and that is the state a later scrub finds.
        param([switch]$NoRepository)

        $base = Join-Path ([IO.Path]::GetTempPath()) ('har-phases-' + [guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $base -Force | Out-Null
        if (-not $NoRepository) { & git -C $base init --quiet 2>&1 | Out-Null }
        Set-Content -LiteralPath (Join-Path $base '.gitignore') -Encoding utf8 -Value @(
            '.har-profile.json', '.har-substitutions.json', '.substitutions.json', '.har-captures/')
        Set-Content -LiteralPath (Join-Path $base '.har-profile.json') -Encoding utf8 -Value (
            @{ salt = 'har-capture-phases-fixture-salt'; literals = @{} } | ConvertTo-Json)
        [pscustomobject]@{ Base = $base; Captures = (Join-Path $base '.har-captures') }
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
            [switch]$UnprotectedTables
        )

        $dir = Join-Path (Join-Path $Store.Captures $Site) $Stamp
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        if ($Foreign) {
            # A mitmproxy dump: a raw, and no session.json to say whose it is.
            Set-Content -LiteralPath (Join-Path $dir 'raw.mitm') -Value 'not-a-har' -Encoding utf8
            return $dir
        }
        if (-not $NoRaw) { Write-FixtureHar -Path (Join-Path $dir 'raw.har') }
        # The start URI carries a token in its path on purpose: it is the shape
        # nothing here may ever echo.
        $session = @{
            uri = 'https://www.example.test/magic/FIXTURE-START-TOKEN'
            describe = $Describe; sessionDir = $dir
            harPath = (Join-Path $dir 'raw.har'); outputPath = $dir
            startedUtc = '2026-01-01T00:00:00.000Z'; endedUtc = '2026-01-01T00:01:00.000Z'
        }
        if ($SignIn) { $session.signIn = @{ source = $SignIn; signedIn = $true } }
        if ($UnprotectedTables) { $session.unprotectedTables = $true }
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
        [pscustomobject]@{ ExitCode = $code; Text = ($out | Out-String) }
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
    function New-StubbedGateDoor {
        param([Parameter(Mandatory)][object]$Store, [Parameter(Mandatory)][int]$Verdict)

        $copy = Join-Path $Store.Base 'stub-scripts'
        Copy-Item -LiteralPath $script:ScriptsDir -Destination $copy -Recurse -Force
        $stub = @"
const fs = require('fs'), path = require('path');
const target = process.argv[process.argv.indexOf('--in') + 1];
// verify-scrub.js writes its findings report beside the file it verified, and
// a refusal is specified to move that report with the artifact.
fs.writeFileSync(path.join(path.dirname(target), 'scrub-findings.json'),
    JSON.stringify({ findings: [], stub: true }));
process.stderr.write('verify-scrub: stubbed verdict $Verdict\n');
process.exit($Verdict);
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

    AfterEach {
        if ($script:Store -and (Test-Path -LiteralPath $script:Store.Base)) {
            Remove-Item -LiteralPath $script:Store.Base -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

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

    AfterEach {
        if ($script:Store -and (Test-Path -LiteralPath $script:Store.Base)) {
            Remove-Item -LiteralPath $script:Store.Base -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'names the capture by label, description and directory before it runs anything' {
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001' -Scrubbed `
            -Describe 'sign in, list the things'

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
            'www.example.test/reset/TYPED-PATH-TOKEN?t=TYPED-QUERY-TOKEN', '-Catalogue')

        $run.ExitCode | Should -Be 3 -Because $run.Text
        $run.Text | Should -Not -Match 'TYPED-PATH-TOKEN|TYPED-QUERY-TOKEN'
        $run.Text | Should -Match 'read as a capture label'
        $run.Text | Should -Match 'https://' -Because 'the likeliest slip is a URL typed without its scheme'
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

    AfterEach {
        if ($script:Store -and (Test-Path -LiteralPath $script:Store.Base)) {
            Remove-Item -LiteralPath $script:Store.Base -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

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

    AfterEach {
        if ($script:Store -and (Test-Path -LiteralPath $script:Store.Base)) {
            Remove-Item -LiteralPath $script:Store.Base -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

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

    It '-Scrub alone answers the scrub wrapper''s own code on an ADVISORY verdict, and says the verdict' {
        # A KNOWN LIMIT, pinned so it cannot change by accident in either
        # direction. The scrub wrapper's folder form answers 0 or 1 and puts
        # the verdict in its summary; turning that into the recorder's 7 here
        # would be this door restating the recorder's exit mapping. So the code
        # is the wrapper's, and the word the operator needs is in the output.
        # -Scrub -Catalogue, above, does answer 7: the recorder asks the gate
        # again.
        $capture = Add-PhaseCapture -Store $script:Store -Stamp '2026-01-02-000001'
        $door = New-StubbedGateDoor -Store $script:Store -Verdict 4

        $run = Invoke-PhaseDoor -Store $script:Store -Script $door -Arguments @(
            'https://www.example.test', '-Scrub')

        $run.ExitCode | Should -Be 0 -Because $run.Text
        Test-Path -LiteralPath (Join-Path $capture 'scrubbed.har') | Should -BeTrue
        $run.Text | Should -Match 'Scrub verdicts: 0 clean, 0 blunted, 1 kept with advisories, 0 refused'
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
