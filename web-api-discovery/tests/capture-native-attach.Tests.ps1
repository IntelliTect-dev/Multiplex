#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Wrapper for the Node behavior tests that pin issue #73 -- recording in a
# browser Playwright did NOT launch. Pester is the only suite CI runs, so a Node
# test with no wrapper here is a test that never runs on a pull request.
#
#   capture-native-attach.test.js  `--browser native` starts chrome.exe with no
#                                  automation flags; `--attach` records in a
#                                  Chrome that is already running; stopping
#                                  DETACHES and leaves that browser and its tabs
#                                  alone; and raw.har comes from the incremental
#                                  log without being reported as a fallback.
#                                  And (#85) only THIS capture's tabs are
#                                  recorded in that borrowed browser -- another
#                                  site's tab, polling in the background, stays
#                                  out of raw.har.
#
# The Node suite's last case starts a REAL Chrome, records a real page load,
# stops through the real `stop` command and then asks the BROWSER whether it is
# still running. That is the only honest shape for it: "stop detaches" is a
# property of Playwright's CDP connection rather than of our teardown branch, so
# a test that inspected our own code would pass whether or not `browser.close()`
# takes Chrome down with it. It skips -- reporting the skip in its success line,
# never silently -- where playwright or Chrome is absent.
#
# The Describe blocks below cover the PowerShell half the Node suite cannot
# reach: the front door's own parameter contract.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
    $script:InvokePs1  = Join-Path $script:ScriptsDir 'capture/Invoke-HarCapture.ps1'
    $script:CaptureJs  = Join-Path $script:ScriptsDir 'capture/capture-har.js'

    function Get-ParamAst {
        param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Name)
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$errors)
        return $ast.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq $Name }
    }
}

Describe 'recording in a browser Playwright did not launch (#73)' {
    It 'runs <Name> and all of its behavioral assertions pass' -ForEach @(
        @{ Name = 'capture/capture-native-attach.test.js'; Expect = 'All capture-native-attach tests passed' }
    ) {
        $testJs = Join-Path $script:ScriptsDir $Name
        Test-Path -LiteralPath $testJs | Should -BeTrue

        & node --check $testJs 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0

        $out = & node $testJs 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) {
            Write-Host ($out -join "`n")
        }
        $exit | Should -Be 0
        ($out -join "`n") | Should -Match $Expect
    }
}

Describe 'Invoke-HarCapture.ps1 exposes the mode without re-deciding it (#73)' {

    It 'offers -Browser as a closed set of the two modes that exist' {
        # ValidateSet rather than a free string: 'chrome', 'chromium' and 'system'
        # are all things an operator would reasonably type, and each one would
        # otherwise travel to the recorder to be refused there -- after the
        # front door had already accepted it.
        $browser = Get-ParamAst -Path $script:InvokePs1 -Name 'Browser'
        $browser | Should -Not -BeNullOrEmpty

        $set = $browser.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateSet' }
        $set | Should -Not -BeNullOrEmpty -Because 'the modes are a closed set'
        $values = @($set.PositionalArguments.Value)
        $values | Should -Contain 'Playwright'
        $values | Should -Contain 'Native'
        $values.Count | Should -Be 2 -Because 'there are exactly two ways to obtain a browser'
    }

    It 'takes -Attach, and does not make it a switch' {
        # An endpoint, because the browser is already running somewhere and this
        # run cannot guess where. A switch would have to assume a port.
        $attach = Get-ParamAst -Path $script:InvokePs1 -Name 'Attach'
        $attach | Should -Not -BeNullOrEmpty
        $attach.StaticType.Name | Should -Be 'String'
    }

    It 'leaves -Port UNDEFAULTED so -Attach can still refuse it' {
        # THE FALSIFIER for this parameter, and the one thing here that could
        # have been written the lazy way.
        #
        # -Port used to default to 9333 and was forwarded unconditionally. Keep
        # that and every -Attach run arrives at the recorder carrying
        # `--port 9333`, which --attach refuses -- so the refusal would fire for
        # an operator who never typed a port, and the only way to use -Attach
        # from this front door would be not to use this front door.
        #
        # Asserted on the AST rather than by running the script: a run without
        # -Attach is happy either way, which is exactly why the defect would have
        # shipped.
        $port = Get-ParamAst -Path $script:InvokePs1 -Name 'Port'
        $port | Should -Not -BeNullOrEmpty
        $port.DefaultValue | Should -BeNullOrEmpty -Because (
            'a default makes every run look like a request for that port, and -Attach ' +
            'must be able to tell "asked for a port" from "said nothing"')

        # And the forwarding is gated on the binding, not on truthiness: `0` is a
        # port the recorder accepts under --validate-only, and `if ($Port)` would
        # silently drop it.
        $text = Get-Content -LiteralPath $script:InvokePs1 -Raw
        $text | Should -Match ([regex]::Escape("PSBoundParameters.ContainsKey('Port')"))
    }

    It 'does not pass --browser playwright, which --attach refuses, unless it was typed' {
        # Forwarding the default explicitly would turn `-Attach 9222` into a
        # usage error about a browser the operator never named. Gated on the
        # BINDING rather than on the value, because a typed `-Browser Playwright`
        # must still reach the recorder: with -AI it is a refusal (#98), and
        # comparing against the default dropped it silently. The forwarded
        # arguments are asserted in har-recording.Tests.ps1.
        $text = Get-Content -LiteralPath $script:InvokePs1 -Raw
        $text | Should -Match ([regex]::Escape("PSBoundParameters.ContainsKey('Browser')"))
    }

    It 'RE-STATES NONE of the exclusivity rules -- the recorder owns them' {
        # #471's argument, applied to this feature: a second copy of a judgement
        # is free to disagree with the first the day one of them changes. The
        # recorder refuses -Attach with -Port, -Profile, -Isolated and
        # -Browser Playwright, each with its reason and exit 2. This script
        # forwards and stays out of it.
        $text = Get-Content -LiteralPath $script:InvokePs1 -Raw
        $text | Should -Not -Match '(?m)^\s*if\s*\(\s*\$Attach\s+-and\s+\$Isolated'
        $text | Should -Not -Match '(?i)exclusive.*throw'
    }

    It 'refuses the unanswerable combinations at the recorder, with a reason and exit 2' {
        # The delegation above is only acceptable if the refusal actually
        # happens, so it is exercised here rather than asserted structurally --
        # through the front door, which is where an operator meets it.
        $cases = @(
            @{ Args = @('-Browser', 'Native', '-Isolated'); Match = 'exclusive' }
            @{ Args = @('-Attach', '9222', '-Isolated');    Match = 'exclusive' }
            @{ Args = @('-Attach', '9222', '-Port', '9333'); Match = 'port' }
            @{ Args = @('-Attach', '9222', '-Profile', 'x'); Match = 'profile' }
        )
        foreach ($case in $cases) {
            $out = & (Get-Command pwsh).Source -NoProfile -File $script:InvokePs1 `
                -Uri 'https://example.com' -Describe 'exclusivity' @($case.Args) 2>&1
            $exit = $LASTEXITCODE
            $text = $out | Out-String
            $exit | Should -Be 2 -Because (
                "$($case.Args -join ' ') is a usage error, and both doors answer 2: got $exit`n$text")
            $text | Should -Match $case.Match
        }
    }

    It 'documents WHY the mode exists, not merely that it exists' {
        # The measured evidence is the reason anybody would reach for this, and
        # an operator hitting a 403 needs to recognise their own symptom in the
        # help. Named protections, because "some sites block automation" is a
        # symptom nobody can recognise; the help then describes Native as what
        # it is, an ordinary Chrome with no automation flags.
        $help = Get-Help -Name $script:InvokePs1 -Full
        $browserHelp = ($help.parameters.parameter |
            Where-Object name -eq 'Browser' | Out-String)
        $browserHelp | Should -Match '(?i)Imperva'
        $browserHelp | Should -Match '(?i)DataDome'
        $browserHelp | Should -Match '(?i)automat'

        $attachHelp = ($help.parameters.parameter |
            Where-Object name -eq 'Attach' | Out-String)
        # The operator has to be told their tabs survive, or they will not use it
        # on the browser they are signed into -- which is the only browser worth
        # attaching to.
        $attachHelp | Should -Match '(?i)detach'
        $attachHelp | Should -Match '(?i)remote-debugging-port'
    }
}
