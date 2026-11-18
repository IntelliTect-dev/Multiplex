#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Behavior tests for the HAR capture pipeline (issues #270, #281):
# capture-har.js and its two PowerShell front doors.
#
# None of these launch a browser. The recorder's browser handling is verified
# by hand against a real site; what is pinned here is everything a regression
# could silently break -- argument contracts, the containment that keeps a
# credential-bearing raw capture out of a committable directory, the object
# surface, and the failure reporting that stops an empty capture being
# analyzed as data.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
    $script:CaptureDir = Join-Path $script:ScriptsDir 'capture'
    $script:CaptureJs  = Join-Path $script:CaptureDir 'capture-har.js'
    $script:CaptureTestJs = Join-Path $script:CaptureDir 'capture-har.test.js'
    $script:InvokePs1  = Join-Path $script:CaptureDir 'Invoke-HarCapture.ps1'
    $script:StopPs1    = Join-Path $script:CaptureDir 'Stop-HarRecording.ps1'
    $script:ConvertPs1 = Join-Path $script:CaptureDir 'ConvertFrom-HarCatalogue.ps1'
    $script:PromptMd   = Join-Path $script:CaptureDir 'catalogue-prompt.md'
    $script:SkillMd    = Join-Path $script:RepoRoot 'skills/web-api-discovery/SKILL.md'

    # The raw capture location is FIXED and no flag can move it. $script:Tmp is
    # outside any repository, where the root still resolves against the WORKING
    # DIRECTORY, so the only way a test contains one is by choosing where node
    # runs -- which is exactly the containment being asserted, exercised rather
    # than bypassed. Inside a repository the root now anchors to the MAIN
    # working tree (#367); capture-durability.Tests.ps1 owns that.
    #
    # Every `start` below passes --describe because #366 made it required. The
    # requirement itself is asserted in capture-durability.test.js, not here --
    # these cases are about paths, ports and profiles.
    #
    # A MACHINE THE CHECK-ONLY START PASSES ON (#25). `start --validate-only` is
    # how those cases ask the recorder what it resolved, and since #25 it exits 1
    # when a real start would be refused -- no browser driver, no operator
    # profile. A CI runner has neither and a developer machine may have both, so
    # a check-only start here always stands on the same machine: a stand-in
    # driver, reached through the variables the recorder already reads, and an
    # operator profile in the folder it runs from. Nothing else is given them --
    # the `stop` cases depend on that folder having NO profile.
    #
    # The stand-ins come from the same fixture module the Node suites use, so
    # "a machine the gates pass on" is defined once.
    $script:GatesRoot = Join-Path ([IO.Path]::GetTempPath()) ("har-rec-gates-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $script:GatesRoot -Force | Out-Null
    $script:PassingGates = & node -e @'
const gates = require(process.argv[1]);
process.stdout.write(JSON.stringify(gates.gateVars(process.argv[2])));
'@ (Join-Path $script:CaptureDir 'capture-start-gates.test-support.js') $script:GatesRoot | ConvertFrom-Json

    function Invoke-CaptureHar {
        param([Parameter(ValueFromRemainingArguments)][string[]]$CaptureArgs)
        Push-Location -LiteralPath $script:Tmp
        # Keep the streams apart: --validate-only writes machine-readable JSON
        # on stdout and human notes on stderr, and a test that merges them
        # cannot assert on either.
        $errFile = [IO.Path]::GetTempFileName()
        $restore = @{}
        try {
            if ($CaptureArgs[0] -eq 'start' -and $CaptureArgs -contains '--validate-only') {
                Set-Content -LiteralPath (Join-Path $script:Tmp '.har-profile.json') -Encoding utf8 `
                    -Value (@{ salt = 'a' * 48; literals = @{} } | ConvertTo-Json)
                foreach ($name in 'NODE_PATH', 'PLAYWRIGHT_BROWSERS_PATH') {
                    $restore[$name] = [Environment]::GetEnvironmentVariable($name)
                    [Environment]::SetEnvironmentVariable($name, $script:PassingGates.$name)
                }
            }
            $out = & node $script:CaptureJs @CaptureArgs 2>$errFile | Out-String
            $code = $LASTEXITCODE
            $err = Get-Content -LiteralPath $errFile -Raw
        }
        finally {
            foreach ($name in $restore.Keys) {
                [Environment]::SetEnvironmentVariable($name, $restore[$name])
            }
            Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue
            Pop-Location
        }
        [pscustomobject]@{
            StdOut   = $out
            StdErr   = $err
            Output   = "$out$err"
            ExitCode = $code
        }
    }

    # Run a PowerShell front door against a STUB node, and report what it
    # forwarded and what it wrote to each stream.
    #
    # The front doors were previously asserted by grepping their own source for
    # '--log-level' and 'VerbosePreference'. That passes just as happily when
    # the condition is inverted, so it pins the spelling rather than the
    # behavior. Standing a fake `node` in front of the real one on PATH is what
    # makes "which arguments did node receive" and "did -InformationAction
    # actually suppress anything" observable without launching a browser.
    #
    # The stub is shaped per platform. On Windows, PATHEXT makes `& node`
    # resolve node.cmd, and the front door's own `Get-Command node` preflight
    # finds it too. On Linux and macOS a .cmd shadows nothing: the lookup wants
    # a file named exactly `node` with the execute bit, and PATH entries are
    # separated by ':' not ';'. With a Windows-only stub the REAL recorder runs
    # and the test dies with "capture-har exited 1" -- undetected for as long as
    # this suite never ran on Linux (issue #308).
    function New-NodeStub {
        param(
            [Parameter(Mandatory)][string]$Directory,
            [Parameter(Mandatory)][string]$ArgsFile
        )

        if ($IsWindows) {
            Set-Content -LiteralPath (Join-Path $Directory 'node.cmd') -Encoding ascii -Value @(
                '@echo off'
                "echo %* > `"$ArgsFile`""
                'exit /b 0'
            )
            return
        }

        $stub = Join-Path $Directory 'node'
        # LF endings and no BOM: the kernel reads the shebang literally, so a CR
        # would make the interpreter '/bin/sh\r', which does not exist.
        $body = "#!/bin/sh`necho `"`$@`" > '$ArgsFile'`nexit 0`n"
        [System.IO.File]::WriteAllText($stub, $body, [System.Text.UTF8Encoding]::new($false))
        & chmod +x $stub

        # A stub that is written but not executable is worse than no stub: the
        # PATH search silently skips it, the REAL recorder runs, and the test
        # fails with the same "capture-har exited 1" that issue #308 was about,
        # pointing at nothing. Fail loudly at the setup step instead.
        if ($LASTEXITCODE -ne 0) {
            throw "chmod +x failed on the stub node (exit $LASTEXITCODE): $stub"
        }
        # UnixMode is the ls -l string, e.g. '-rwxr-xr-x'. Check the OWNER
        # triplet (chars 1-3) specifically: this process runs the stub as the
        # owner, so an x borrowed from the group or other bits would pass a
        # bare -match 'x' while the stub still could not run.
        $mode = (Get-Item -LiteralPath $stub -Force).UnixMode
        if ($mode.Length -lt 4 -or $mode.Substring(1, 3) -notmatch 'x') {
            throw "stub node is not executable by its owner (mode '$mode'): $stub -- " +
            'the real recorder would run instead. Is TMPDIR mounted noexec?'
        }
    }

    function Invoke-FrontDoor {
        param(
            [Parameter(Mandatory)][string]$Script,
            [hashtable]$Arguments = @{}
        )
        $stubDir = Join-Path $script:Tmp ('stub-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
        $argsFile = Join-Path $stubDir 'args.txt'
        New-NodeStub -Directory $stubDir -ArgsFile $argsFile

        # Streams are redirected to FILES, not to -InformationVariable: the
        # common variable parameters collect records even when the matching
        # action is SilentlyContinue, so a test built on them cannot tell
        # "suppressed" from "emitted". What reaches stream 6 can.
        $infoFile = Join-Path $stubDir 'info.txt'
        $warnFile = Join-Path $stubDir 'warn.txt'

        $savedPath = $env:PATH
        $env:PATH = $stubDir + [System.IO.Path]::PathSeparator + $savedPath
        try {
            & $Script @Arguments 6> $infoFile 3> $warnFile 2>$null | Out-Null
        }
        finally {
            $env:PATH = $savedPath
        }

        # Get-Content -Raw returns $null for an empty file, and a stream a
        # command never wrote to is exactly that -- coalesce so the caller can
        # always .Trim() what comes back.
        function Read-Stream([string]$Path) {
            $text = if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw } else { $null }
            if ($null -eq $text) { '' } else { $text }
        }

        [pscustomobject]@{
            Args        = (Read-Stream $argsFile).Trim()
            Information = (Read-Stream $infoFile).Trim()
            Warning     = (Read-Stream $warnFile).Trim()
        }
    }

    # A pid that cannot exist: Windows pids are multiples of 4 well below this,
    # so process.kill(pid, 0) reports ESRCH -- i.e. "that driver is gone".
    $script:DeadPid = 999999

    # A session directory as it looks after the driver was lost: a record log
    # with entries, a session.json, and no raw.har.
    #
    # -Crashed models the case that actually matters and is easy to get wrong:
    # a driver killed MID-RECORDING never gets to write endedUtc, so a fixture
    # that hardcodes endedUtc quietly tests a different, far rarer scenario --
    # "ended cleanly, cleanup failed" -- while reading as though it covered the
    # crash.
    function New-LostSession {
        param(
            [Parameter(Mandatory)][string]$Root,
            [int]$Entries = 2,
            [switch]$Crashed,
            # Sessions resolve newest-last by directory name, so a test that
            # needs to tell "the pointer was followed" from "the newest-on-disk
            # fallback found the same thing" must create two, with different
            # stamps. A single-session fixture cannot distinguish them.
            [string]$Stamp = '2026-01-01-120000',
            [int]$OwnerPid = $script:DeadPid,
            [string]$OutputPath,
            # Sessions live at <capturesRoot>/<host>/<stamp>. The fixture must
            # mirror that: seeded flat, the newest-on-disk fallback finds
            # nothing and every status/stop test resolves no session at all.
            [string]$HostFolder = 'example.com'
        )
        $sessionDir = Join-Path (Join-Path $Root $HostFolder) $Stamp
        New-Item -ItemType Directory -Path $sessionDir -Force | Out-Null
        if (-not $OutputPath) { $OutputPath = Join-Path $Root 'out' }
        $lines = 1..$Entries | ForEach-Object {
            @{
                startedDateTime = "2026-01-01T12:00:0${_}Z"
                time            = 0
                request         = @{ method = 'GET'; url = "https://example.com/api/thing/$_"; headers = @() }
                response        = @{ status = 200; content = @{ text = '{"ok":true}' }; headers = @() }
                cache           = @{}
                timings         = @{}
            } | ConvertTo-Json -Depth 8 -Compress
        }
        Set-Content -LiteralPath (Join-Path $sessionDir 'raw.ndjson') -Value $lines -Encoding utf8
        $session = @{
            uri        = 'https://example.com'
            sessionDir = $sessionDir
            harPath    = Join-Path $sessionDir 'raw.har'
            recordLog  = Join-Path $sessionDir 'raw.ndjson'
            outputPath = $OutputPath
            startedUtc = '2026-01-01T12:00:00Z'
            port       = 49999
            pid        = $OwnerPid
        }
        # A crashed driver leaves NO endedUtc -- that is the whole difficulty.
        if (-not $Crashed) { $session.endedUtc = '2026-01-01T12:05:00Z' }
        $session | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath (Join-Path $sessionDir 'session.json') -Encoding utf8
        return $sessionDir
    }
}

AfterAll {
    if ($script:GatesRoot -and (Test-Path -LiteralPath $script:GatesRoot)) {
        Remove-Item -LiteralPath $script:GatesRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe 'capture-har.js behavior suite' {
    # The pure functions -- entry construction, log assembly, digest,
    # containment, catalogue delegation -- are pinned by the zero-dep Node
    # suite beside the script, following the convention the rest of the
    # toolkit's JS uses.

    It 'test file exists at the canonical path' {
        Test-Path -LiteralPath $script:CaptureTestJs | Should -BeTrue
    }

    It 'all behavioral assertions pass' {
        $out = & node $script:CaptureTestJs 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) { Write-Information ($out -join "`n") -InformationAction Continue }
        $exit | Should -Be 0
        ($out -join "`n") | Should -Match 'All capture-har tests passed'
    }
}

Describe 'capture-har.js' {

    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ("har-rec-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:Tmp -Force | Out-Null
    }

    AfterEach {
        if ($script:Tmp -and (Test-Path -LiteralPath $script:Tmp)) {
            Remove-Item -LiteralPath $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'exists at the canonical path' {
        Test-Path -LiteralPath $script:CaptureJs | Should -BeTrue
    }

    It 'is valid JavaScript (parses without syntax errors)' {
        & node --check $script:CaptureJs 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
    }

    It 'resolves a session without launching a browser under --validate-only' {
        $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --validate-only
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'no browser launched'
        $r.Output | Should -Match 'cdpEndpoint'
    }

    It 'rejects a start with no URI' {
        $r = Invoke-CaptureHar start
        $r.ExitCode | Should -Be 2
    }

    It 'accepts --log-level on start, and rejects a value that is not a level' {
        $ok = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --log-level verbose --validate-only
        $ok.ExitCode | Should -Be 0
        $bad = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --log-level loud --validate-only
        $bad.ExitCode | Should -Be 2
        $bad.Output | Should -Match '(?i)log-level'
    }

    It 'keeps stdout machine-readable at every level' {
        # The banner used to be on stdout. Levelling it up without moving it
        # would make `Invoke-HarCapture ... | ConvertFrom-Json` fail on exactly
        # the verbose run an operator reaches for when something is wrong.
        foreach ($level in 'normal', 'verbose') {
            $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' `
                --log-level $level --validate-only
            $r.ExitCode | Should -Be 0
            { $r.StdOut | ConvertFrom-Json } | Should -Not -Throw `
                -Because "stdout must be pure JSON at --log-level $level"
        }
    }

    It 'reports a failed phase without -Verbose, and the raw path only with it' {
        # The level must never gate a failure. This fixture has no
        # .har-profile.json, so the scrub cannot run -- an operator who never
        # types -Verbose still has to be told that, while the resolved raw
        # path stays a diagnostic.
        $sessionDir = New-LostSession -Root $script:Tmp
        $quiet = Invoke-CaptureHar stop --session $sessionDir --dir $script:Tmp
        $quiet.Output | Should -Match 'ERROR:'
        $quiet.Output | Should -Not -Match 'raw:\s+\S'

        $sessionDir2 = New-LostSession -Root $script:Tmp -Stamp '2026-01-01-130000'
        $loud = Invoke-CaptureHar stop --session $sessionDir2 --dir $script:Tmp --log-level verbose
        $loud.Output | Should -Match 'raw:\s+\S'
        $loud.Output | Should -Match 'ERROR:'
    }

    It 'refuses --dir on start rather than silently ignoring it' {
        # --dir was how a raw capture got redirected out of the gitignored
        # tree. Accepting and ignoring it would leave the operator believing
        # the raw had moved -- worse than rejecting it.
        $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --dir $script:Tmp --validate-only
        $r.ExitCode | Should -Be 2
        $r.Output | Should -Match '(?i)confined to \.har-captures'
    }

    It 'refuses --storage-state, which is now auto-discovered' {
        $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' `
            --storage-state (Join-Path $script:Tmp 'state.json') --validate-only
        $r.ExitCode | Should -Be 2
        $r.Output | Should -Match '(?i)discovered automatically'
    }

    It 'writes the raw capture under the captures root, never under --output-path' {
        # The leak .gitignore documented in its own comment, closed by
        # construction rather than by a check.
        $outputPath = Join-Path $script:Tmp 'refs'
        $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' `
            --output-path $outputPath --validate-only
        $r.ExitCode | Should -Be 0
        $session = $r.StdOut | ConvertFrom-Json
        $session.harPath | Should -BeLike (Join-Path $script:Tmp '.har-captures*')
        $session.harPath | Should -Not -BeLike "$outputPath*"
    }

    It 'keys both output roots on the captured host' {
        # scrubbed.har, digest.json and catalogue.json are fixed filenames, so
        # before this a second capture silently overwrote the first.
        $outputPath = Join-Path $script:Tmp 'refs'
        $r = Invoke-CaptureHar start --uri 'https://app.example.com/login' --describe 'pester fixture' `
            --output-path $outputPath --validate-only
        $r.ExitCode | Should -Be 0
        $session = $r.StdOut | ConvertFrom-Json
        $session.harPath | Should -BeLike '*.har-captures*app.example.com*raw.har'
        $session.outputPath | Should -Be (Join-Path $outputPath 'app.example.com')
    }

    It 'names the folder from the host alone, never the rest of the URL' {
        # A magic-link or password-reset URL carries its token in the path or
        # the query, and the output path is the committable directory.
        $r = Invoke-CaptureHar start --uri 'https://app.example.com/reset/PATHTOK?t=QUERYTOK' --describe 'pester fixture' `
            --validate-only
        $r.ExitCode | Should -Be 0
        $session = $r.StdOut | ConvertFrom-Json
        # The output path is the run's own stamped session directory now (#377),
        # so the host is a SEGMENT of it rather than its last element.
        $session.outputPath | Should -BeLike '*app.example.com*'
        $session.outputPath | Should -Be $session.sessionDir
        $session.outputPath | Should -Not -Match 'PATHTOK|QUERYTOK'
        $session.harPath | Should -Not -Match 'PATHTOK|QUERYTOK'
    }

    It 'renders a port with an underscore, since a dash is legal in a hostname' {
        $r = Invoke-CaptureHar start --uri 'https://localhost:5001/' --describe 'pester fixture' --validate-only
        $r.ExitCode | Should -Be 0
        ($r.StdOut | ConvertFrom-Json).outputPath | Should -BeLike '*localhost_5001*'
    }

    It 'falls forward to a free port instead of failing on a busy one' {
        # A busy port used to be a hard error because it doubled as the
        # "a capture is already running" detector. Those are different
        # concerns; only a profile in use is a real conflict.
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $busy = $listener.LocalEndpoint.Port
        try {
            $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --port $busy --validate-only
            $r.ExitCode | Should -Be 0
            $session = $r.StdOut | ConvertFrom-Json
            $session.port | Should -Not -Be $busy
            $session.requestedPort | Should -Be $busy
            $session.cdpEndpoint | Should -Match ":$($session.port)$" `
                -Because 'an agent reads the endpoint rather than assuming 9333'
        }
        finally { $listener.Stop() }
    }

    It 'reports that there is nothing to stop rather than inventing a session' {
        $r = Invoke-CaptureHar stop --dir $script:Tmp
        $r.ExitCode | Should -Be 3
        $r.Output | Should -Match 'no capture session found'
    }

    It 'never terminates a process' {
        # Killing a browser discards the recording AND can destroy unrelated
        # signed-in windows on the developer's machine.
        #
        # The invariant is "terminates nothing", not "never names an API called
        # kill": `process.kill(pid, 0)` sends no signal and is the standard way
        # to ask whether a pid is alive. So ban the terminating forms, and
        # require every process.kill call to pass signal 0.
        $source = Get-Content -LiteralPath $script:CaptureJs -Raw
        $source | Should -Not -Match 'SIGKILL|SIGTERM|taskkill|pkill|\.destroy\(\)'

        $killCalls = [regex]::Matches($source, 'process\.kill\(([^)]*)\)')
        foreach ($call in $killCalls) {
            $call.Groups[1].Value | Should -Match ',\s*0\s*$' -Because 'a liveness probe must send signal 0, never a terminating signal'
        }
    }

    It 'applies no HAR glob -- the capture is unfiltered' {
        (Get-Content -LiteralPath $script:CaptureJs -Raw) | Should -Not -Match 'save-har-glob|urlFilter'
    }
}

Describe 'capture-har.js --profile' {

    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ("har-rec-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:Tmp -Force | Out-Null
    }

    AfterEach {
        if ($script:Tmp -and (Test-Path -LiteralPath $script:Tmp)) {
            Remove-Item -LiteralPath $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'gives a bare name its own capture profile' {
        $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --profile 'work' --validate-only
        $r.ExitCode | Should -Be 0
        ($r.StdOut | ConvertFrom-Json).profileDir | Should -Match 'har-capture[\\/]profile-work$'
    }

    It 'folds an awkward name to a safe directory name' {
        $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --profile 'My Account!' --validate-only
        ($r.StdOut | ConvertFrom-Json).profileDir | Should -Match 'profile-my-account-$'
    }

    It 'records into a path another tool owns, verbatim' {
        # The integration point for a project that keys profiles off its own
        # concept: it computes the directory, we record as that identity.
        $external = Join-Path $script:Tmp 'someapp/profile-alias-1a2b3c4d'
        $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --profile $external --validate-only
        $r.ExitCode | Should -Be 0
        ($r.StdOut | ConvertFrom-Json).profileDir | Should -Be ([IO.Path]::GetFullPath($external))
    }

    It 'defaults to the shared capture profile when none is named' {
        $r = Invoke-CaptureHar start --uri 'https://example.com' --describe 'pester fixture' --validate-only
        ($r.StdOut | ConvertFrom-Json).profileDir | Should -Match 'har-capture[\\/]profile$'
    }
}

Describe 'capture-har.js assembling a lost recording' {

    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ("har-rec-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:Tmp -Force | Out-Null
    }

    AfterEach {
        if ($script:Tmp -and (Test-Path -LiteralPath $script:Tmp)) {
            Remove-Item -LiteralPath $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'assembles raw.har itself when the driver was lost' {
        # Exit 5 no longer means "degraded artifact" -- it means "assembled
        # from the incremental log rather than recordHar". The file is a
        # genuine HAR either way, which is what makes closing the window safe.
        $sessionDir = New-LostSession -Root $script:Tmp -Entries 3
        $r = Invoke-CaptureHar stop --session $sessionDir --dir $script:Tmp

        $r.ExitCode | Should -BeIn @(5, 6)
        $rawHar = Join-Path $sessionDir 'raw.har'
        Test-Path -LiteralPath $rawHar | Should -BeTrue

        $har = Get-Content -LiteralPath $rawHar -Raw | ConvertFrom-Json
        $har.log.version | Should -Be '1.2'
        $har.log.entries.Count | Should -Be 3
        $har.log.entries[0].request.url | Should -Match 'example\.com'
    }

    It 'does not write a separate raw.snapshot.har any more' {
        # Two artifacts invited someone to analyze the weaker one. There is
        # now exactly one: raw.har.
        $sessionDir = New-LostSession -Root $script:Tmp
        Invoke-CaptureHar stop --session $sessionDir --dir $script:Tmp | Out-Null
        Test-Path -LiteralPath (Join-Path $sessionDir 'raw.snapshot.har') | Should -BeFalse
    }

    It 'does not label the assembled capture a degraded recovery artifact' {
        $sessionDir = New-LostSession -Root $script:Tmp
        Invoke-CaptureHar stop --session $sessionDir --dir $script:Tmp | Out-Null
        $text = Get-Content -LiteralPath (Join-Path $sessionDir 'raw.har') -Raw
        $text | Should -Not -Match 'RECOVERY ARTIFACT'
    }

    It 'honours an explicit --min-bytes 0 instead of substituting the default' {
        # `Number(x) || DEFAULT` swallows a deliberate 0, turning "accept
        # whatever was captured" into the 1024-byte floor without a word.
        $sessionDir = New-LostSession -Root $script:Tmp
        Set-Content -LiteralPath (Join-Path $sessionDir 'raw.har') `
            -Value '{"log":{"version":"1.2","entries":[]}}' -Encoding utf8

        $r = Invoke-CaptureHar stop --session $sessionDir --dir $script:Tmp --min-bytes 0
        $r.Output | Should -Not -Match 'failed capture'
    }

    It 'gives up quickly on a session whose recorder crashed mid-recording' {
        # A dead driver never answers the sentinel. Waiting the full 60s timeout
        # for it is indistinguishable from a healthy but slow stop.
        $sessionDir = New-LostSession -Root $script:Tmp -Crashed

        $elapsed = Measure-Command { $script:R = Invoke-CaptureHar stop --session $sessionDir --dir $script:Tmp }
        $elapsed.TotalSeconds | Should -BeLessThan 30
        $script:R.Output | Should -Match 'no longer running'
    }

    It 'ignores a leftover current.json entirely -- the pointer is gone (#377)' {
        # These two tests used to assert POINTER semantics: that a stale
        # `current.json` was abandoned, and that a live one was followed. The
        # pointer is gone -- it was shared mutable state at the root of a store
        # several agent sessions record into at once, and `resolveSession`
        # already fell back to scanning whenever it went stale.
        #
        # Left as they were, both would still have passed, for reasons that have
        # nothing to do with what they named: one because the newest-on-disk
        # answer happened to match, the other because of the live/dead filter.
        # That is a test wearing a green tick, so the assertion is rewritten to
        # the fact that is actually true now -- a pointer file, however
        # tempting, decides nothing.
        $crashed = New-LostSession -Root $script:Tmp -Crashed -Stamp '2020-01-01-120000'
        $newer = New-LostSession -Root $script:Tmp -Stamp '2030-01-01-120000'
        # The pointer names the OLD, crashed session. If anything still read it,
        # this is the answer we would get.
        @{ sessionDir = $crashed } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $script:Tmp 'current.json') -Encoding utf8

        $r = Invoke-CaptureHar status --dir $script:Tmp
        $r.ExitCode | Should -Be 0
        $status = $r.StdOut | ConvertFrom-Json
        $status.sessionDir | Should -Be $newer `
            -Because 'the stamp is the identity; a file at the store root is not consulted'
        $status.recording | Should -BeFalse `
            -Because 'a crashed recorder must never be reported as still recording'
    }

    It 'a LIVE capture wins over a newer finished one, with no pointer to help' {
        # The mirror, and the property that actually has to hold once the
        # pointer is gone: liveness is judged by the driver pid, so the
        # recording the operator is sitting in front of is the one `stop`
        # reaches even when a newer session has already ended.
        #
        # The pointer here names the NEWER, finished session -- the wrong
        # answer -- so this fails if anything starts reading it again.
        $live = New-LostSession -Root $script:Tmp -Crashed -Stamp '2020-01-01-120000' -OwnerPid $PID
        $finished = New-LostSession -Root $script:Tmp -Stamp '2030-01-01-120000'
        @{ sessionDir = $finished } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $script:Tmp 'current.json') -Encoding utf8

        $status = (Invoke-CaptureHar status --dir $script:Tmp).StdOut | ConvertFrom-Json
        $status.sessionDir | Should -Be $live `
            -Because 'the running recording is the one a stop must reach'
        $status.recording | Should -BeTrue
    }

    It 'reports a failure when neither recorder produced anything' {
        $sessionDir = New-LostSession -Root $script:Tmp
        Remove-Item -LiteralPath (Join-Path $sessionDir 'raw.ndjson') -Force

        $r = Invoke-CaptureHar stop --session $sessionDir --dir $script:Tmp
        $r.ExitCode | Should -Be 4
        $r.Output | Should -Match 'lost entirely'
    }

    It 'treats a trivially small capture as failed rather than as data' {
        $sessionDir = New-LostSession -Root $script:Tmp
        Set-Content -LiteralPath (Join-Path $sessionDir 'raw.har') -Value '{}' -Encoding utf8

        $r = Invoke-CaptureHar stop --session $sessionDir --dir $script:Tmp --min-bytes 1024
        $r.ExitCode | Should -Be 4
        $r.Output | Should -Match 'failed capture'
    }

}

Describe 'raw captures are gitignored' {
    # A raw capture carries live session cookies. These assert what git will
    # ACTUALLY do, because the natural-looking pattern `*.raw.har` does not
    # match the file the recorder writes (`raw.har`), and the .har-captures/
    # directory rule hides that until someone looks closely.

    It 'ignores the capture the recorder writes, wherever it is written' {
        foreach ($p in @(
                '.har-captures/app.example.com/2026-01-01-120000/raw.har',
                '.har-captures/2026-01-01-120000/raw.har',
                'somewhere/else/raw.har',
                'somewhere/else/raw.ndjson')) {
            & git -C $script:RepoRoot check-ignore -q -- $p
            $LASTEXITCODE | Should -Be 0 -Because "$p holds live credentials and must never be committable"
        }
    }

    It 'ignores the operator profile and any serialized session' {
        foreach ($p in @('.har-profile.json', '.har-storage-state.json', 'anywhere/creds.storage-state.json')) {
            & git -C $script:RepoRoot check-ignore -q -- $p
            $LASTEXITCODE | Should -Be 0 -Because "$p holds real account identifiers or a live session"
        }
    }

    It 'does not ignore a scrubbed reference bound for the catalogue' {
        # The output path is now a host-named folder in the working directory,
        # so the scrubbed artifacts must stay visible to git there too.
        foreach ($p in @(
                'docs/har-reference/example.com/example.com-login-2026-01-01.har',
                'app.example.com/scrubbed.har',
                'app.example.com/catalogue.json',
                'app.example.com/acme/acme-login-2026-01-01.har')) {
            & git -C $script:RepoRoot check-ignore -q -- $p
            $LASTEXITCODE | Should -Not -Be 0 -Because "$p is a scrubbed artifact and must be committable"
        }
    }
}

Describe 'Invoke-HarCapture.ps1' {

    # Invoke-FrontDoor stubs node under $script:Tmp. This block used to inherit
    # whatever path the previous Describe left there -- already deleted, and
    # null when a filtered run skips that Describe -- so it gets its own.
    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ("har-fd-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:Tmp -Force | Out-Null
    }

    AfterEach {
        if ($script:Tmp -and (Test-Path -LiteralPath $script:Tmp)) {
            Remove-Item -LiteralPath $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'exists at the canonical path' {
        Test-Path -LiteralPath $script:InvokePs1 | Should -BeTrue
    }

    It 'replaced Start-HarRecording outright -- no alias, no shim' {
        Test-Path -LiteralPath (Join-Path $script:CaptureDir 'Start-HarRecording.ps1') | Should -BeFalse
    }

    It 'parses without syntax errors' {
        foreach ($f in @($script:InvokePs1, $script:StopPs1, $script:ConvertPs1)) {
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$errors)
            $errors | Should -BeNullOrEmpty -Because "$f must parse"
        }
    }

    It 'exposes exactly the thirteen documented parameters' {
        # Was six, down from eight: -StorageState is auto-discovered and
        # -ValidateOnly stays on capture-har.js where it is the test seam.
        #
        # Back to eight with #73, and the two additions are WHO LAUNCHES THE
        # BROWSER -- which is not a preference this front door could infer.
        # Playwright's launch is a test-automation launch and some sites refuse
        # it outright (Imperva, Akamai, DataDome and PerimeterX, measured), so
        # `-Browser Native` records in an ordinary Chrome and `-Attach` records
        # in one that is already running. Neither could be folded into an
        # existing parameter: -Profile names an identity rather than a launch
        # mechanism, and -Isolated is the opposite end of the same axis (it is
        # refused alongside both).
        #
        # NINE with #98, and the addition is WHO DRIVES -- `-AI` hands the
        # browsing to an agent while the recorder goes on recording. The issue
        # names the switch and the design is its Proposal section; it is the one
        # thing no existing parameter can say. -Browser and -Attach answer whose
        # BROWSER it is, which is a different question and already decided by the
        # time this one is asked: `-AI` implies -Browser Native, because an agent
        # driving a Playwright-launched browser would be driving the launch the
        # protected sites refuse. It takes no value, because -Describe -- already
        # mandatory -- is the goal, and there is deliberately no `-Agent` yet:
        # a second driver would be a second option, and nothing needs one.
        #
        # TEN with #112, owner-approved there: -AITools says WHICH TOOLS the agent
        # has. Auto (the default) runs it in auto permission mode, whose
        # classifier approves or blocks each action; BrowserOnly removes every
        # built-in tool (`--tools ""`) so only the browser tools remain, for a
        # site signed into something sensitive. It only means something with
        # -AI, and is refused without it.
        #
        # ELEVEN with the pacing and throttling change, at the owner's request
        # ("configurable, with a flag to set them to 0 for tests"): -Pace says
        # HOW FAST the recorder acts on a page and an agent is told to -- a range
        # per kind of action plus a per-minute ceiling, which no existing
        # parameter can express. `-Pace off` is the test switch the request
        # names. Forwarded only when typed; the recorder owns the default and
        # refuses what it cannot read.
        #
        # THIRTEEN with #24, and both additions are the owner's own spelling:
        # -Scrub and -Catalogue say WHICH PHASES RUN on a capture that already
        # exists ("-Catalogue -Scrub, which both imply skip capture because it
        # is already done"). Two switches rather than one -SkipCapture because
        # each names what WILL run, so no combination means "record nothing and
        # do nothing". They are the whole of what that feature adds: which
        # capture they act on is said with the positional first argument and
        # -Describe, which were already here, precisely so that no THIRD
        # parameter had to arrive to carry a target. The behaviour is pinned in
        # HarCapturePhases.Tests.ps1.
        #
        # This assertion is the gate the "Adding Command-Line Options" rule
        # relies on, so it is DELIBERATELY still an equality over the whole list:
        # the next parameter has to arrive through this test and the argument for
        # it has to be written down here.
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:InvokePs1, [ref]$null, [ref]$errors)
        $names = $ast.ParamBlock.Parameters |
            ForEach-Object { $_.Name.VariablePath.UserPath } |
            Sort-Object
        $names | Should -Be @(
            'AI', 'AITools', 'Attach', 'Browser', 'Catalogue', 'Describe', 'Isolated', 'OutputPath', 'Pace',
            'Port', 'Profile', 'Scrub', 'Uri')
    }

    It 'treats -Scrub:$false and -Catalogue:$false as off: the run is still a recording' {
        # Neither switch typed is today's behaviour, unchanged -- and a switch
        # typed as $false is neither switch typed. Same hazard as -AI:$false
        # and -Verbose:$false: read by presence in $PSBoundParameters, this
        # would look for an existing capture and record nothing.
        #
        # Asserted on the arguments node actually receives: `start` is the
        # recorder's record command, and a phase run never sends it.
        (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; Scrub = $false; Catalogue = $false }).Args |
            Should -Match 'start --uri https://example\.com'
    }

    It 'forwards -Pace to the recorder only when it was typed' {
        # The recorder owns the default pace and refuses a value it cannot
        # read, so the front door forwards exactly what was typed -- and nothing
        # when it was not, which is how the default stays the recorder's.
        $plain = (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture' }).Args
        $plain | Should -Match 'start --uri' -Because 'an args file node never wrote would satisfy the negative below for the wrong reason'
        $plain | Should -Not -Match '--pace'
        (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; Pace = 'off' }).Args |
            Should -Match '--pace off'
        (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; Pace = 'nav=2000-6000,per-minute=6' }).Args |
            Should -Match '--pace nav=2000-6000,per-minute=6'
    }

    It 'says how often the capture paused, from the recorder''s machine-readable line' {
        # Owner request: a pause is "communicated back" to the PowerShell side.
        # Each one is said live on stderr by the recorder; this pins the closing
        # summary the front door adds from the `pauses` field of the one JSON
        # line -- for a caller (an AI session) that reads only the end.
        $stubDir = Join-Path $script:Tmp ('pause-stub-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
        $json = '{"capture":"complete","sessionDir":"x","outputPath":"x","cataloguePath":null,' +
            '"pauses":{"count":2,"handOvers":1,"totalMs":420000}}'
        if ($IsWindows) {
            Set-Content -LiteralPath (Join-Path $stubDir 'node.cmd') -Encoding ascii -Value @(
                '@echo off', "echo $json", 'exit /b 0')
        }
        else {
            $stub = Join-Path $stubDir 'node'
            [System.IO.File]::WriteAllText($stub, "#!/bin/sh`necho '$json'`nexit 0`n", [System.Text.UTF8Encoding]::new($false))
            & chmod +x $stub
        }
        $infoFile = Join-Path $stubDir 'info.txt'
        $savedPath = $env:PATH
        $env:PATH = $stubDir + [System.IO.Path]::PathSeparator + $savedPath
        try {
            & $script:InvokePs1 -Uri 'https://example.com' -Describe 'pester fixture' 6> $infoFile 3> $null 2> $null | Out-Null
        }
        finally {
            $env:PATH = $savedPath
        }
        $info = Get-Content -LiteralPath $infoFile -Raw
        $info | Should -Match 'The capture paused 2 time\(s\) and was handed over 1 time\(s\), 7 min in all'
        $info | Should -Match 'pauses. in session\.json'
    }

    It 'says which capture this was and which run it belongs to: <Name>' -ForEach @(
        @{
            Name   = 'both known'
            Json   = '{"capture":"complete","sessionDir":"x","captureId":"0f6b3a52-9a0e-4c1c-8d64-0c1d2f4a7b11","runId":"3f9c2a71d0b4","outputPath":"x","cataloguePath":null}'
            Expect = @('0f6b3a52-9a0e-4c1c-8d64-0c1d2f4a7b11', 'run 3f9c2a71d0b4')
            Never  = @('no run')
        }
        @{
            Name   = 'the run could not be read'
            Json   = '{"capture":"complete","sessionDir":"x","captureId":"0f6b3a52-9a0e-4c1c-8d64-0c1d2f4a7b11","runId":null,"outputPath":"x","cataloguePath":null}'
            Expect = @('0f6b3a52-9a0e-4c1c-8d64-0c1d2f4a7b11', 'no run could be identified')
            Never  = @('run 3f9c')
        }
        @{
            Name   = 'a recorder that reports neither'
            Json   = '{"capture":"complete","sessionDir":"x","outputPath":"x","cataloguePath":null}'
            Expect = @()
            Never  = @('This capture is', 'no run')
        }
    ) {
        # Issue #26. A session selects its own captures by run, so it has to be
        # able to LEARN its run -- from its own recording, not by picking a row
        # out of a shared listing by description or time, which is the guess
        # the run exists to replace. The recorder prints both ids on its one
        # machine-readable line; that line is swallowed here, so this is where
        # they have to be said.
        $stubDir = Join-Path $script:Tmp ('run-stub-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
        if ($IsWindows) {
            Set-Content -LiteralPath (Join-Path $stubDir 'node.cmd') -Encoding ascii -Value @(
                '@echo off', "echo $Json", 'exit /b 0')
        }
        else {
            $stub = Join-Path $stubDir 'node'
            [System.IO.File]::WriteAllText($stub, "#!/bin/sh`necho '$Json'`nexit 0`n", [System.Text.UTF8Encoding]::new($false))
            & chmod +x $stub
        }
        $infoFile = Join-Path $stubDir 'info.txt'
        $savedPath = $env:PATH
        $env:PATH = $stubDir + [System.IO.Path]::PathSeparator + $savedPath
        try {
            & $script:InvokePs1 -Uri 'https://example.com' -Describe 'pester fixture' 6> $infoFile 3> $null 2> $null | Out-Null
        }
        finally {
            $env:PATH = $savedPath
        }
        $info = [string](Get-Content -LiteralPath $infoFile -Raw)
        foreach ($text in $Expect) { $info | Should -Match ([regex]::Escape($text)) }
        foreach ($text in $Never) { $info | Should -Not -Match ([regex]::Escape($text)) }
    }

    It 'forwards -AI to the recorder, and nothing when it is absent' {
        # The front door forwards and the recorder decides -- the rule this file
        # already holds for -Attach and -Browser. So what is pinned here is only
        # that the switch REACHES the recorder: the implication to native, the
        # exclusivity with -Isolated and the refusal when no agent is installed
        # are capture-har.js's, asserted against it in
        # scripts/capture/capture-ai-drive.test.js.
        #
        # Asserted on the arguments node actually receives, not on the source
        # text: a grep for 'ai' passes just as happily when the condition is
        # inverted.
        $plain = (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture' }).Args
        $plain | Should -Match 'start --uri' -Because 'an args file node never wrote would satisfy the negative below for the wrong reason'
        $plain | Should -Not -Match '--ai'

        (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; AI = $true }).Args |
            Should -Match '--ai'
    }

    It 'treats -AI:$false as off, not as "the switch was mentioned"' {
        # Same hazard as -Verbose:$false, and the same fix: a switch is read by
        # its VALUE, never by whether it appears in $PSBoundParameters.
        (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; AI = $false }).Args |
            Should -Not -Match '--ai'
    }

    It 'refuses -AI with an explicitly typed -Browser Playwright, at exit 2' {
        # FOUND IN REVIEW. -Browser was forwarded only when it differed from its
        # default, so `-AI -Browser Playwright` reached the recorder as a bare
        # `--ai` and was silently driven in the native browser -- while the help
        # for -AI and SKILL.md both say the combination is refused. The operator
        # typed both; honouring one of them is a guess.
        #
        # Run through the real front door and the real recorder, because "the
        # refusal happens" is a property of the two together. PATH has every
        # `claude` removed so that, if the refusal is ever lost again, the run
        # stops at "no agent installed" (exit 1) rather than driving a browser.
        $agentNames = @('claude.exe', 'claude.cmd', 'claude.bat', 'claude')
        $kept = foreach ($dir in ($env:PATH -split [System.IO.Path]::PathSeparator)) {
            if (-not $dir) { continue }
            $holdsAgent = $false
            foreach ($name in $agentNames) {
                if (Test-Path -LiteralPath (Join-Path $dir $name)) { $holdsAgent = $true }
            }
            if (-not $holdsAgent) { $dir }
        }
        $pwsh = (Get-Command pwsh).Source
        $savedPath = $env:PATH
        $env:PATH = $kept -join [System.IO.Path]::PathSeparator
        Push-Location -LiteralPath $TestDrive
        try {
            $out = & $pwsh -NoProfile -File $script:InvokePs1 -Uri 'https://example.com' `
                -Describe 'pester fixture' -AI -Browser Playwright 2>&1
            $exit = $LASTEXITCODE
        }
        finally {
            Pop-Location
            $env:PATH = $savedPath
        }
        $text = $out | Out-String
        $exit | Should -Be 2 -Because "-AI with -Browser Playwright is a usage error: got $exit`n$text"
        $text | Should -Match '(?i)--ai and --browser playwright'
    }

    It 'forwards -AITools to the recorder only when it was typed (#112)' {
        # Same rule as -Browser: the front door forwards what the operator TYPED
        # and the recorder decides -- so a typed -AITools without -AI reaches the
        # recorder and is refused there, while the default forwards nothing.
        $browserOnly = (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; AI = $true; AITools = 'BrowserOnly' }).Args
        $browserOnly | Should -Match '--ai(\s|$)' -Because 'an args file node never wrote would satisfy the negative below for the wrong reason'
        $browserOnly | Should -Match '--ai-tools browser-only'

        (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; AI = $true; AITools = 'Auto' }).Args |
            Should -Match '--ai-tools auto'
        (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; AI = $true }).Args |
            Should -Not -Match '--ai-tools'
    }

    It 'accepts only Auto and BrowserOnly for -AITools (#112)' {
        { & $script:InvokePs1 -Uri 'https://example.com' -Describe 'pester fixture' -AI -AITools None } |
            Should -Throw -ErrorId 'ParameterArgumentValidationError,Invoke-HarCapture.ps1'
    }

    It 'refuses -AITools without -AI, at exit 2, naming both (#112)' {
        # Which agent's tools? With no agent there is nothing to limit, and an
        # operator who typed it believes something is. Run through the real front
        # door and the real recorder, because the refusal is a property of the
        # two together. $TestDrive holds no .har-profile.json, so were the refusal
        # ever lost the run would stop at the profile preflight (exit 1) rather
        # than launch a browser.
        $pwsh = (Get-Command pwsh).Source
        Push-Location -LiteralPath $TestDrive
        try {
            $out = & $pwsh -NoProfile -File $script:InvokePs1 -Uri 'https://example.com' `
                -Describe 'pester fixture' -AITools BrowserOnly 2>&1
            $exit = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
        $text = $out | Out-String
        $exit | Should -Be 2 -Because "-AITools without -AI is a usage error: got $exit`n$text"
        $text | Should -Match '(?i)--ai-tools needs --ai'
    }

    It 'forwards -Browser only when it was typed, so -Attach alone is not refused' {
        # The other half of the same fix. Forwarding the DEFAULT would turn
        # `-Attach 9222` into a refusal about a browser nobody named (--attach
        # refuses --browser playwright), so the test is whether the operator
        # TYPED it, not whether it differs from the default.
        $attachOnly = (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; Attach = '9222' }).Args
        $attachOnly | Should -Match '--attach 9222' -Because 'an args file node never wrote would satisfy the negative below for the wrong reason'
        $attachOnly | Should -Not -Match '--browser'

        (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{
            Uri = 'https://example.com'; Describe = 'pester fixture'; Browser = 'Playwright' }).Args |
            Should -Match '--browser playwright'
    }

    It 'takes the URI positionally, so the parameter name is optional' {
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:InvokePs1, [ref]$null, [ref]$errors)
        $uri = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Uri' }
        $uri | Should -Not -BeNullOrEmpty
        $attr = $uri.Attributes | Where-Object { $_.TypeName.Name -eq 'Parameter' }
        ($attr.NamedArguments | Where-Object { $_.ArgumentName -eq 'Position' }) | Should -Not -BeNullOrEmpty
    }

    It 'reads the catalogue from its own -OutputPath, not the newest session on disk' {
        # Captures now coexist on different ports, so "newest directory under
        # .har-captures" is not this run's session: a second capture started in
        # another terminal and finished first would win the sort, and this
        # invocation would emit a different site's catalogue as its own.
        # Resolving from -OutputPath is the fix, and it means the script has no
        # reason to look under the captures root at all.
        $text = Get-Content -LiteralPath $script:InvokePs1 -Raw
        $text | Should -Not -Match "Select-Object\s+-Last\s+1" `
            -Because 'picking the last session directory is the race this fixes'
        $text | Should -Match 'catalogue\.json'
    }

    It 'wires -Verbose to the recorder instead of adding a seventh parameter' {
        # -Verbose is free with [CmdletBinding()]. A -LogLevel switch would be
        # a second way to say the same thing, and the eight-parameter assertion
        # above is what stops one appearing.
        #
        # Asserted on the arguments node actually RECEIVES, not on the source
        # text: a grep for 'VerbosePreference' passes just as happily when the
        # condition is inverted.
        $plain = (Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{ Uri = 'https://example.com'; Describe = 'pester fixture' }).Args
        $plain | Should -Match 'start --uri' -Because 'an args file node never wrote would satisfy the negative below for the wrong reason'
        $plain | Should -Not -Match '--log-level'

        (Invoke-FrontDoor -Script $script:InvokePs1 `
            -Arguments @{ Uri = 'https://example.com'; Describe = 'pester fixture'; Verbose = $true }).Args |
            Should -Match '--log-level verbose'
    }

    It 'treats -Verbose:$false as off, not as "the switch was mentioned"' {
        # $PSBoundParameters.ContainsKey('Verbose') is true for -Verbose:$false
        # too. Reading the preference rather than the binding is what keeps an
        # explicit opt-out from turning the level up.
        $off = (Invoke-FrontDoor -Script $script:InvokePs1 `
            -Arguments @{ Uri = 'https://example.com'; Describe = 'pester fixture'; Verbose = $false }).Args
        $off | Should -Match 'start --uri'
        $off | Should -Not -Match '--log-level'
    }

    It 'lets a caller take over the information stream with -InformationAction' {
        # `-InformationAction Continue` pinned on every call site meant the
        # common parameter did nothing: the stream was uncontrollable by the
        # very mechanism the convention points callers at.
        #
        # `Ignore` is the assertable end of that, not `SilentlyContinue`:
        # Write-Information writes the record to stream 6 either way and
        # SilentlyContinue only stops the HOST rendering it, so a redirect
        # cannot see the difference. Only Ignore drops the record -- and only
        # if the script stopped overriding the caller.
        $loud = Invoke-FrontDoor -Script $script:InvokePs1 -Arguments @{ Uri = 'https://example.com'; Describe = 'pester fixture' }
        $loud.Information | Should -Match 'press ENTER' `
            -Because 'status is on by default -- that is what the pinning bought'

        $quiet = Invoke-FrontDoor -Script $script:InvokePs1 `
            -Arguments @{ Uri = 'https://example.com'; Describe = 'pester fixture'; InformationAction = 'Ignore' }
        $quiet.Information | Should -BeNullOrEmpty
    }

    It 'keeps warnings audible when the status lines are silenced' {
        # Silencing chatter must not silence the report that the run produced
        # no catalogue.
        $quiet = Invoke-FrontDoor -Script $script:InvokePs1 `
            -Arguments @{ Uri = 'https://example.com'; Describe = 'pester fixture'; InformationAction = 'Ignore' }
        $quiet.Warning | Should -Match 'no catalogue'
    }

    It 'derives the capture folder exactly as the recorder does' {
        # The rule lives in two runtimes: uriFolder() in capture-har.js decides
        # where artifacts are WRITTEN, Get-HarUriFolder here decides where the
        # catalogue is LOOKED FOR. If they ever disagree the front door reports
        # "no catalogue" for a capture that succeeded, so the duplication is
        # pinned rather than trusted.
        #
        # Dot-sourcing is not possible -- the script has a mandatory parameter
        # and would run -- so the function is lifted out of the source text and
        # re-defined here. That still pins the shipped implementation: an edit
        # to it changes what this test executes.
        $text = Get-Content -LiteralPath $script:InvokePs1 -Raw
        $start = $text.IndexOf('function Get-HarUriFolder')
        $start | Should -BeGreaterThan -1 -Because 'the front door must derive the folder itself'
        $end = $text.IndexOf("`n}", $start)
        . ([scriptblock]::Create($text.Substring($start, $end - $start + 2)))

        foreach ($u in @(
                'https://example.com',
                'https://app.example.com/reset/PATHTOK?t=QUERYTOK',
                'https://localhost:5001/',
                'https://my-app.example.com/',
                'HTTPS://APP.Example.COM/',
                'http://example.com:8080/x',
                'http://[::1]:8080/',
                # Two host-normalisation engines, assumed equivalent. .NET does
                # not punycode `.Host` (only `.IdnHost` does) and renders an
                # IPv4-mapped IPv6 tail dotted where WHATWG folds it to hex --
                # so an international site would have had its capture written
                # to one folder and its catalogue looked for in another.
                'http://xn--fiq228c.example/x',
                'http://中文.example/x',
                'http://[::ffff:1.2.3.4]/x',
                'http://example.com./x',
                'http://user:pass@example.com/x')) {
            $fromNode = & node -e 'process.stdout.write(require(require("path").resolve(process.argv[1])).uriFolder(process.argv[2]))' $script:CaptureJs $u
            $LASTEXITCODE | Should -Be 0 -Because "the recorder must accept $u"
            Get-HarUriFolder -Uri $u | Should -Be $fromNode -Because "the two implementations must agree on $u"
        }

        # Where the two engines cannot be reconciled, the front door must fail
        # CLOSED rather than guess. .NET will not parse a percent-encoded
        # hostname at all, while WHATWG decodes and punycodes it -- so this
        # errors out before recording instead of writing the capture to one
        # folder and looking for the catalogue in another.
        Get-HarUriFolder -Uri 'http://%E4%B8%AD%E6%96%87.example/x' | Should -BeNullOrEmpty `
            -Because 'an unreconcilable host must be refused up front, not guessed at'

        # The refusals matter as much as the agreements: 'http://../evil'
        # parses with a host of '..' and would walk the capture out of its own
        # directory, and file:/data:/about: parse with no host at all.
        foreach ($bad in @(
                'not-a-url',
                'http://../evil',
                'http://./x',
                'file:///etc/passwd',
                'data:text/plain,x',
                'about:blank')) {
            Get-HarUriFolder -Uri $bad | Should -BeNullOrEmpty `
                -Because "$bad must not yield a capture folder"

            & node -e 'try { require(require("path").resolve(process.argv[1])).uriFolder(process.argv[2]); process.exit(0) } catch (e) { process.exit(9) }' $script:CaptureJs $bad
            $LASTEXITCODE | Should -Be 9 -Because "the recorder must refuse $bad too"
        }
    }

    It 'forwards the level from Stop-HarRecording too' {
        $plain = (Invoke-FrontDoor -Script $script:StopPs1 -Arguments @{}).Args
        $plain | Should -Match 'stop --min-bytes'
        $plain | Should -Not -Match '--log-level'
        (Invoke-FrontDoor -Script $script:StopPs1 -Arguments @{ Verbose = $true }).Args |
            Should -Match '--log-level verbose'
    }

    It 'keeps status out of the pipeline -- no Write-Host anywhere in capture/' {
        # PSAvoidUsingWriteHost: Write-Host cannot be captured, redirected or
        # suppressed, and a ConvertTo-Json of the result must be pure data.
        $analyzer = Get-Module -ListAvailable -Name PSScriptAnalyzer
        if (-not $analyzer) { Set-ItResult -Skipped -Because 'PSScriptAnalyzer is not installed' ; return }
        Import-Module PSScriptAnalyzer -ErrorAction Stop
        $findings = Invoke-ScriptAnalyzer -Path $script:CaptureDir -Recurse `
            -IncludeRule PSAvoidUsingWriteHost
        $findings | Should -BeNullOrEmpty -Because ($findings | Out-String)
    }

    It 'is clean under the full default rule set' {
        $analyzer = Get-Module -ListAvailable -Name PSScriptAnalyzer
        if (-not $analyzer) { Set-ItResult -Skipped -Because 'PSScriptAnalyzer is not installed' ; return }
        Import-Module PSScriptAnalyzer -ErrorAction Stop
        $findings = Invoke-ScriptAnalyzer -Path $script:CaptureDir -Recurse -Severity Error, Warning
        $findings | Should -BeNullOrEmpty -Because ($findings | Out-String)
    }
}

Describe 'The catalogue object surface' {

    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ("har-cat-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:Tmp -Force | Out-Null
        $script:CataloguePath = Join-Path $script:Tmp 'catalogue.json'
        @(
            @{ Action = 'composer-story-create'; Description = 'Created a post'; Methods = @('POST')
               Endpoints = @('api.example.com/v1/posts'); EntryCount = 3; Status = 'Exercised'
               HarFile = 'example.com/example.com-composer-story-create-2026-01-01.har'
               CapturedUtc = '2026-01-01T12:00:00Z' }
            @{ Action = 'get-v1-notifications'; Description = $null; Methods = @('GET')
               Endpoints = @('api.example.com/v1/notifications'); EntryCount = 1; Status = 'Observed'
               HarFile = $null; CapturedUtc = '2026-01-01T12:00:00Z' }
        ) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $script:CataloguePath -Encoding utf8
    }

    AfterEach {
        if ($script:Tmp -and (Test-Path -LiteralPath $script:Tmp)) {
            Remove-Item -LiteralPath $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'emits one typed object per catalogue entry' {
        $rows = & $script:ConvertPs1 -Path $script:CataloguePath
        $rows.Count | Should -Be 2
        foreach ($row in $rows) {
            $row.PSObject.TypeNames | Should -Contain 'WebApiDiscovery.HarCapture.CatalogueEntry'
        }
    }

    It 'carries every documented property' {
        $row = (& $script:ConvertPs1 -Path $script:CataloguePath)[0]
        foreach ($name in @('Action', 'Description', 'Methods', 'Endpoints',
                            'EntryCount', 'Status', 'HarFile', 'CapturedUtc')) {
            $row.PSObject.Properties.Name | Should -Contain $name
        }
    }

    It 'survives ConvertTo-Json as pure data, with no status text in the pipeline' {
        # Per powershell.instructions.md -> Output & Streams: status goes to
        # Write-Information so the pipeline carries objects and nothing else.
        $json = & $script:ConvertPs1 -Path $script:CataloguePath | ConvertTo-Json -Depth 4
        { $json | ConvertFrom-Json } | Should -Not -Throw
        $parsed = $json | ConvertFrom-Json
        $parsed.Count | Should -Be 2
        $parsed[0].Action | Should -Be 'composer-story-create'
    }

    It 'survives ConvertTo-Csv' {
        $csv = & $script:ConvertPs1 -Path $script:CataloguePath | ConvertTo-Csv -NoTypeInformation
        $csv[0] | Should -Match 'Action'
    }

    It 'filters on Status, so Observed rows are addressable' {
        $observed = & $script:ConvertPs1 -Path $script:CataloguePath | Where-Object Status -eq 'Observed'
        $observed.Count | Should -Be 1
        $observed.Action | Should -Be 'get-v1-notifications'
    }

    It 'renders through PowerShell formatting rather than hand-rolled text' {
        $formatXml = Join-Path $script:CaptureDir 'HarCapture.Format.ps1xml'
        Test-Path -LiteralPath $formatXml | Should -BeTrue
        [xml]$doc = Get-Content -LiteralPath $formatXml -Raw
        $doc.Configuration.ViewDefinitions.View.ViewSelectedBy.TypeName |
            Should -Contain 'WebApiDiscovery.HarCapture.CatalogueEntry'
    }
}

Describe 'The catalogue phase is never silently dropped' {

    It 'ships the prompt as a file, so the shell-out and an agent read the same thing' {
        Test-Path -LiteralPath $script:PromptMd | Should -BeTrue
        $text = Get-Content -LiteralPath $script:PromptMd -Raw
        $text | Should -Match 'digest\.json'
        $text | Should -Match 'extract-har-reference\.js'
        $text | Should -Match 'verify-har-reference\.js'
        $text | Should -Match '(?i)Observed, not exercised'
    }

    It 'SKILL.md points an agent at that same prompt' {
        $skill = Get-Content -LiteralPath $script:SkillMd -Raw
        $skill | Should -Match 'catalogue-prompt\.md'
    }
}

Describe 'An AI can be told to drive the recording (#98)' {

    It 'SKILL.md has a section saying how, and what the agent will refuse to do' {
        # The issue asks for this section by name. It is asserted rather than
        # trusted because the switch is unusable without it: an operator who does
        # not know the prompt is generated has no reason to believe the rules are
        # in it, and the rules are the reason the switch is safe to use at all.
        $skill = Get-Content -LiteralPath $script:SkillMd -Raw
        $skill | Should -Match '(?i)Let an AI drive'
        $skill | Should -Match '-AI'
        $skill | Should -Match 'drive-prompt\.md'
        $skill | Should -Match 'discovery-checklist\.md'
        # The three facts an operator has to know before they use it: what it
        # will not do, where the record of what it did lands, and that they can
        # still stop it.
        $skill | Should -Match '(?i)no stealth'
        $skill | Should -Match 'agent-log\.txt'
        $skill | Should -Match '(?i)hands? back'
    }

    It 'documents the one-time extension setup and the browser-selection rule (#117)' {
        # The extension is the default driver only once it is installed, signed in
        # and connected under the capture browser name in the CAPTURE profile --
        # a step no run automates. And the rule that keeps the agent out of the
        # operator's everyday Chrome has to be one the operator can read, not only
        # one the agent is told. Both places an operator looks: SKILL.md and the
        # -AI help.
        $help = (Get-Help -Name $script:InvokePs1 -Parameter AI | Out-String)
        foreach ($doc in @(@{ Name = 'SKILL.md'; Text = (Get-Content -LiteralPath $script:SkillMd -Raw) },
                           @{ Name = '-AI help'; Text = $help })) {
            $doc.Text | Should -Match 'chromewebstore\.google\.com/detail/\S*fcoeoabgfenejglbffodgkkbkcdhcgfn' -Because "$($doc.Name) says where to install it"
            $doc.Text | Should -Match '(?i)capture\s+profile' -Because "$($doc.Name) says which profile"
            $doc.Text | Should -Match '(?i)sign\s+in\W+to\s+the\s+extension' -Because "$($doc.Name) says to sign in to the extension"
            $doc.Text | Should -Match '`har-capture`' -Because "$($doc.Name) names the default capture browser name"
            $doc.Text | Should -Match '(?i)capture\s+browser\s+only' -Because "$($doc.Name) says the name is for the capture browser alone"
            $doc.Text | Should -Match 'select_browser' -Because "$($doc.Name) states the selection rule"
            $doc.Text | Should -Match '(?i)enforced\s+by\s+the\s+prompt' -Because "$($doc.Name) says the rule is the prompt's, not code"
            $doc.Text | Should -Match '(?i)hands? back' -Because "$($doc.Name) says what happens when it cannot select"
            $doc.Text | Should -Match '--chrome' -Because "$($doc.Name) says the child is given the extension"
            $doc.Text | Should -Match '--no-chrome' -Because "$($doc.Name) says a run without the extension disables it"
            $doc.Text | Should -Match '(?i)(mov|renam)\w*[\s\S]{0,200}reconnect' -Because "$($doc.Name) says moving a profile changes its name"
            $doc.Text | Should -Match '(?i)attach[\s\S]{0,400}hands?\s+back\s+rather\s+than' -Because "$($doc.Name) says an attached run hands back rather than falling back"
            $doc.Text | Should -Match '(?i)drives?\s+through\s+Playwright\s+MCP' -Because "$($doc.Name) says a run without the extension uses Playwright MCP"
        }
        (Get-Content -LiteralPath $script:SkillMd -Raw) | Should -Match '(?i)default\s+driver\s+once\s+it\s+is\s+set\s+up' -Because 'it is the default only after the one-time setup'
    }

    It 'ships the driving prompt and the checklist as files an agent can read' {
        # Same reasoning as the catalogue prompt above: a prompt that lives in a
        # string literal cannot be reviewed, and this one is mostly safety rules.
        # The checklist is separate because issue #76 owns that text and will lift
        # it into SKILL.md -- a file move rather than a rewrite.
        $driveMd = Join-Path (Split-Path -Parent $script:PromptMd) 'drive-prompt.md'
        $checklistMd = Join-Path (Split-Path -Parent $script:PromptMd) 'discovery-checklist.md'
        Test-Path -LiteralPath $driveMd | Should -BeTrue
        Test-Path -LiteralPath $checklistMd | Should -BeTrue
        $drive = Get-Content -LiteralPath $driveMd -Raw
        $drive | Should -Match '(?i)no stealth measures'
        $drive | Should -Match '(?i)never enter a password'
        $drive | Should -Match '(?i)payment page'
        $drive | Should -Match '(?i)bot check'
        (Get-Content -LiteralPath $checklistMd -Raw) | Should -Match '(?i)error cases'
    }

    It 'recommends a browser setup for what it is, never for getting past a named bot-protection vendor (#42)' {
        # The line is: polite, human-pace recording that honours throttling is in;
        # hiding automation from bot detection is out. A setup recommended because
        # it "passed" Imperva or DataDome reads as the second thing, so the skill,
        # the agent's prompt and the recorder describe the native launch as what
        # it is -- an ordinary browser a person can watch and take over, with no
        # automation flags -- and any challenge as the person's to answer.
        # Naming a vendor to RECOGNISE its block page and hand over stays.
        $captureDir = Split-Path -Parent $script:PromptMd
        $docs = @(
            @{ Name = 'SKILL.md'; Text = (Get-Content -LiteralPath $script:SkillMd -Raw) }
            @{ Name = 'drive-prompt.md'; Text = (Get-Content -LiteralPath (Join-Path $captureDir 'drive-prompt.md') -Raw) }
            @{ Name = 'capture-har.js'; Text = (Get-Content -LiteralPath (Join-Path $captureDir 'capture-har.js') -Raw) }
            @{ Name = 'Invoke-HarCapture.ps1'; Text = (Get-Content -LiteralPath $script:InvokePs1 -Raw) }
        )
        $vendor = '(Imperva|Incapsula|Akamai|DataDome|PerimeterX|HUMAN Security|Cloudflare|Kasada)'
        $passed = '(passed|got past|gets past|get past|was not challenged|setup that was)'
        foreach ($doc in $docs) {
            $doc.Text | Should -Not -Match "(?is)$vendor.{0,200}?\b$passed\b|\b$passed\b.{0,200}?$vendor" -Because (
                "$($doc.Name) must not recommend a setup because it got past a named bot-protection vendor")
            $doc.Text | Should -Not -Match '(?i)protection\s+answers\s+the\s+marking|marks\s+(that\s+)?(this\s+mode|the\s+native\s+launch)\s+exists?\s+to\s+avoid|exists\s+to\s+avoid\)|with\s+bot\s+protection\s+sees' -Because (
                "$($doc.Name) must describe the native launch as an ordinary browser, not as avoiding what bot protection answers")
        }
        (Get-Content -LiteralPath $script:SkillMd -Raw) | Should -Match '(?i)ordinary\s+Chrome\s+a\s+person\s+would\s+(have\s+)?use' -Because 'the native launch is framed as the Chrome a person would use anyway'
    }
}

Describe 'Stop-HarRecording.ps1' {

    It 'exists at the canonical path' {
        Test-Path -LiteralPath $script:StopPs1 | Should -BeTrue
    }

    It 'documents itself as the automation path' {
        (Get-Content -LiteralPath $script:StopPs1 -Raw) | Should -Match 'AI driving the session|automation'
    }

    It 'says it waits for post-processing' {
        # Reporting a capture as done while the scrub is still running names
        # files that do not exist yet.
        (Get-Content -LiteralPath $script:StopPs1 -Raw) | Should -Match '(?i)post-process'
    }

    It 'no longer tells the reader that closing the window loses the capture' {
        # Both recorders now run and exactly one survives, so a window close
        # yields a full HAR. The old warning is false.
        #
        # \s+ between words, not literal spaces: the sentence wraps across
        # lines in the docstring, so a literal-space pattern silently never
        # matches and the guard is dead.
        $stop = Get-Content -LiteralPath $script:StopPs1 -Raw
        $stop | Should -Not -Match '(?i)no\s+HAR\s+is\s+written\s+at\s+all'
    }
}

Describe 'Stop-HarRecording.ps1 says what state the ending is in (#27)' {
    # It used to wait a fixed 60 seconds and then exit 6, "post-processing is
    # still running" -- on a 22 MB capture whose recording process finished
    # normally a little later. These run the REAL front door, as its own
    # process, against a session directory in each state.
    #
    # No clock is injected and none needs to be: the state is derived from what
    # is on disk, so a fixture dated in the past is in that state the moment the
    # command looks. (The clock IS injected in capture-ending.test.js, where the
    # "takes five minutes and succeeds" case lives.)

    BeforeAll {
        $script:RecorderJs = Join-Path $script:CaptureDir 'capture-ending-recorder.test-support.js'

        # ALWAYS with the session and the captures directory named, and from
        # outside any repository. A bare stop resolves the newest live capture
        # in the store this clone records into and asks it to end -- which on a
        # developer's machine may be a recording somebody is in the middle of.
        function Invoke-StopHarRecording {
            param(
                [Parameter(Mandatory)][string]$Session,
                [Parameter(Mandatory)][string]$CapturesDirectory
            )
            $errFile = [IO.Path]::GetTempFileName()
            Push-Location -LiteralPath $CapturesDirectory
            try {
                $out = & pwsh -NoProfile -File $script:StopPs1 -Session $Session `
                    -CapturesDirectory $CapturesDirectory -MinimumBytes 0 2>$errFile | Out-String
                $code = $LASTEXITCODE
                $err = Get-Content -LiteralPath $errFile -Raw
            }
            finally {
                Pop-Location
                Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue
            }
            $json = ($out -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1)
            [pscustomobject]@{
                StdOut   = $out
                StdErr   = "$err"
                ExitCode = $code
                State    = if ($json) { $json | ConvertFrom-Json } else { $null }
            }
        }

        # A session a RUNNING recorder owns. The owner is this Pester process:
        # alive for as long as the test runs, which is all `stop` asks of it.
        function New-LiveSession {
            param(
                [Parameter(Mandatory)][string]$Root,
                [hashtable]$Extra = @{}
            )
            $sessionDir = Join-Path (Join-Path $Root 'example.com') '2026-01-01-120000'
            New-Item -ItemType Directory -Path $sessionDir -Force | Out-Null
            $entry = @{
                startedDateTime = '2026-01-01T12:00:01Z'
                time            = 0
                request         = @{ method = 'GET'; url = 'https://example.com/api/thing/1'; headers = @() }
                response        = @{ status = 200; content = @{ text = '{"ok":true}' }; headers = @() }
                cache           = @{}
                timings         = @{}
            } | ConvertTo-Json -Depth 8 -Compress
            Set-Content -LiteralPath (Join-Path $sessionDir 'raw.ndjson') -Value $entry -Encoding utf8
            $session = @{
                uri        = 'https://example.com'
                sessionDir = $sessionDir
                harPath    = Join-Path $sessionDir 'raw.har'
                recordLog  = Join-Path $sessionDir 'raw.ndjson'
                outputPath = $sessionDir
                startedUtc = '2026-01-01T12:00:00Z'
                pid        = $PID
            }
            foreach ($key in $Extra.Keys) { $session[$key] = $Extra[$key] }
            $session | ConvertTo-Json -Depth 8 |
                Set-Content -LiteralPath (Join-Path $sessionDir 'session.json') -Encoding utf8
            return $sessionDir
        }

        function Get-UtcString([datetime]$When) { $When.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }
    }

    BeforeEach {
        $script:Tmp = Join-Path ([IO.Path]::GetTempPath()) ("har-end-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $script:Tmp -Force | Out-Null
    }

    AfterEach {
        if ($script:Tmp -and (Test-Path -LiteralPath $script:Tmp)) {
            Remove-Item -LiteralPath $script:Tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'reports stalled post-processing as a stall -- its own state, exit 1 not 6 -- and takes nothing over' {
        $then = (Get-Date).AddHours(-2)
        $sessionDir = New-LiveSession -Root $script:Tmp -Extra @{
            endedUtc = Get-UtcString $then
            endedBy  = 'stop-command'
            summary  = @{ exists = $true; bytes = 2048 }
            ending   = @{
                by = 'stop-command'; registeredUtc = Get-UtcString $then
                phase = 'scrub'; phaseStartedUtc = Get-UtcString $then
            }
        }
        $rawHar = Join-Path $sessionDir 'raw.har'
        Set-Content -LiteralPath $rawHar -Encoding utf8 -Value (
            '{"log":{"version":"1.2","creator":{"name":"x","version":"1"},"pages":[],"entries":[]}}')
        (Get-Item -LiteralPath $rawHar).LastWriteTimeUtc = $then.ToUniversalTime()

        $r = Invoke-StopHarRecording -Session $sessionDir -CapturesDirectory $script:Tmp

        $r.ExitCode | Should -Be 1 -Because (
            "6 says the scrub FAILED and 0 says it finished; a stall is neither. stderr:`n$($r.StdErr)")
        $r.State | Should -Not -BeNullOrEmpty -Because "the state must be on stdout as JSON, got: $($r.StdOut)"
        $r.State.stop | Should -Be 'stalled'
        $r.State.exitCode | Should -Be 1
        $r.State.ending.phase | Should -Be 'scrub'
        $r.State.ending.quietSeconds | Should -BeGreaterThan 7000
        $r.State.ending.quietLimitSeconds | Should -Be 60
        $r.StdErr | Should -Match 'STALLED'
        $r.StdErr | Should -Not -Match 'NEVER REGISTERED'

        # The recorder is running and still owns the work.
        Test-Path -LiteralPath (Join-Path $sessionDir 'scrubbed.har') | Should -BeFalse
        (Get-Content -LiteralPath (Join-Path $sessionDir 'session.json') -Raw | ConvertFrom-Json).postProcess |
            Should -BeNullOrEmpty -Because 'stop must not write a result for a recorder that is still running'

        # And `status` gives the same answer, from the same evaluation.
        $status = (Invoke-CaptureHar status --session $sessionDir --dir $script:Tmp).StdOut | ConvertFrom-Json
        $status.ending.state | Should -Be 'stalled'
        $status.ending.phase | Should -Be 'scrub'
    }

    It 'reports a stop that never registered as exactly that, and assembles nothing under the live recorder' {
        $sessionDir = New-LiveSession -Root $script:Tmp
        # Asked for ten minutes ago, and the recorder has still not taken it up.
        $stopFile = Join-Path $sessionDir 'STOP'
        Set-Content -LiteralPath $stopFile -Value '' -NoNewline
        (Get-Item -LiteralPath $stopFile).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddMinutes(-10)

        $r = Invoke-StopHarRecording -Session $sessionDir -CapturesDirectory $script:Tmp

        $r.ExitCode | Should -Be 1 -Because "stderr:`n$($r.StdErr)"
        $r.State.stop | Should -Be 'not-registered'
        $r.State.ending.registeredUtc | Should -BeNullOrEmpty
        $r.State.ending.quietLimitSeconds | Should -Be 60
        $r.StdErr | Should -Match 'NEVER REGISTERED'
        $r.StdErr | Should -Not -Match 'STALLED'

        # The recording is still going: its raw must not be assembled from the
        # log by another process, which is what the fixed timer fell through to.
        Test-Path -LiteralPath (Join-Path $sessionDir 'raw.har') | Should -BeFalse
        Test-Path -LiteralPath $stopFile | Should -BeTrue -Because 'the request stays in place'

        $status = (Invoke-CaptureHar status --session $sessionDir --dir $script:Tmp).StdOut | ConvertFrom-Json
        $status.ending.state | Should -Be 'not-registered'
        $status.recording | Should -BeTrue
    }

    It 'waits for a recording process that is still working, and reports its success' {
        # A real, separate process stands in for the recorder: it takes up the
        # stop, acknowledges it, and spends four seconds "scrubbing" before it
        # finishes. Four seconds, not four minutes -- the long case is pinned
        # with an injected clock -- but enough that a stop which did not wait
        # would return before there was a result to report.
        $sessionDir = New-LiveSession -Root $script:Tmp
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = (Get-Command node).Source
        foreach ($a in @($script:RecorderJs, $sessionDir, '{"scrubMs":4000,"lingerMs":1500}')) {
            [void]$psi.ArgumentList.Add($a)
        }
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $recorder = [System.Diagnostics.Process]::Start($psi)
        try {
            $recorder.StandardOutput.ReadLine() | Should -Match '^ready \d+$' -Because (
                'the stand-in recorder must own the session before it is asked to stop')

            $elapsed = Measure-Command {
                $script:R = Invoke-StopHarRecording -Session $sessionDir -CapturesDirectory $script:Tmp
            }

            $script:R.ExitCode | Should -Be 0 -Because "stderr:`n$($script:R.StdErr)"
            $script:R.State.stop | Should -Be 'complete'
            $script:R.State.ending.endedBy | Should -Be 'stop-command'
            $elapsed.TotalSeconds | Should -BeGreaterThan 3.5 -Because 'it has to have waited for the scrub'
            Test-Path -LiteralPath (Join-Path $sessionDir 'scrubbed.har') | Should -BeTrue
        }
        finally {
            if (-not $recorder.WaitForExit(15000)) { $recorder.Kill($true) }
            $recorder.Dispose()
        }
    }
}

Describe 'The repository no longer references the deleted command' {

    It 'has zero references to Start-HarRecording outside git history' {
        # 19 across 9 files at the time of writing (issue #281). A stale
        # reference sends a reader to a command that does not exist.
        Push-Location $script:RepoRoot
        try {
            # Excluded: docs/designs holds historical plans, and this file
            # necessarily names the command it asserts is gone.
            $hits = & git grep -n -I 'Start-HarRecording' -- . `
                ':!docs/designs/*' ':!tests/har-recording.Tests.ps1' 2>$null
        }
        finally { Pop-Location }
        $hits | Should -BeNullOrEmpty -Because ($hits -join "`n")
    }

    It 'SKILL.md documents the one-command capture' {
        $skill = Get-Content -LiteralPath $script:SkillMd -Raw
        $skill | Should -Match 'Invoke-HarCapture'
        $skill | Should -Match '(?i)catalogue'
    }
}
