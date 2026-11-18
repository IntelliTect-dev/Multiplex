#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Behavior tests for `Remove-HarCapture` -- deleting ONE recording, on purpose
# (issue #102).
#
# THE MAINTAINER DECISION THIS ENCODES, 2026-10-14: **never delete
# automatically**. No age sweep, no count cap, no size cap, no bulk delete.
# Every recording is kept until somebody deletes that one recording on purpose.
# A raw is the one artifact in this pipeline that cannot be regenerated -- a
# browser session a human drove by hand -- and before this command the only
# delete path at all was `Invoke-SanitizeHar -RemoveSource`, which applies only
# after a scrub the gate verified clean.
#
# So the tests below are mostly about what it REFUSES. That is not defensive
# padding: every refusal here is a route by which "delete one recording" would
# have become "delete several", and each one is the feature.
#
#   ONE CAPTURE, NAMED. A wildcard is refused. Two captures are refused. A
#   label that matches more than one capture is refused by naming the matches,
#   never resolved to "the newest" -- guessing which recording the operator
#   meant is the one thing an irreversible command must not do.
#   -WhatIf DELETES NOTHING, and says what it would have done.
#   CONFIRMATION BY DEFAULT. ConfirmImpact is High, so the prompt appears
#   without -Confirm being typed.
#   THE WHOLE SESSION DIRECTORY GOES, substitution tables included: they are
#   keyed by the plaintext a scrub replaced, so a table outliving its capture is
#   a reverse lookup of live credentials with nothing left to justify it.
#   A DIRECTORY THAT IS NOT A CAPTURE IS REFUSED. The command resolves through
#   the shared walk, so "delete this folder" is not something it can be talked
#   into.
#
# ONE PARAMETER, AND NO SECOND ONE. A `host/stamp` label is resolved against the
# capture store the recorder itself would use, found from the working directory
# exactly as `stop`, `status` and `catalogue` find it. That is why the label
# tests below run from inside their fixture rather than naming a store: an
# option to point the command at a different store is an option this issue did
# not ask for, and a delete command is the last place to start accumulating
# them.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
    $script:RemovePs1  = Join-Path $script:ScriptsDir 'capture/Remove-HarCapture.ps1'
    $script:ListPs1    = Join-Path $script:ScriptsDir 'capture/Get-HarCapture.ps1'

    # Every call is -Confirm:$false. The confirmation prompt is the default and
    # is asserted on its own below; a suite that answered it interactively would
    # hang CI.
    function script:New-RemoveFixture {
        param([Parameter(Mandatory)][string]$Name)

        $base = Join-Path $TestDrive $Name
        $root = Join-Path $base '.har-captures'
        $hostDir = Join-Path $root 'app.example.test'

        $makeSession = {
            param($dir, $describe)
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $dir 'raw.har') -Encoding utf8 `
                -Value '{"log":{"version":"1.2","creator":{"name":"fixture","version":"1"},"entries":[]}}'
            Set-Content -LiteralPath (Join-Path $dir 'session.json') -Encoding utf8 -Value (
                [ordered]@{
                    uri        = 'https://app.example.test/'
                    sessionDir = $dir
                    describe   = $describe
                    startedUtc = '2026-01-01T00:00:00.000Z'
                    endedUtc   = '2026-01-01T00:05:00.000Z'
                } | ConvertTo-Json -Depth 6)
            return $dir
        }

        [pscustomobject]@{
            Base     = $base
            Root     = $root
            HostDir  = $hostDir
            First    = & $makeSession (Join-Path $hostDir '2026-01-02-000001') 'the one to delete'
            Second   = & $makeSession (Join-Path $hostDir '2026-01-02-000002') 'the one to keep'
            # Same stamp under a DIFFERENT host. A bare stamp therefore matches
            # two captures, which is the ambiguity the command must refuse
            # rather than resolve.
            Other    = & $makeSession (
                Join-Path $root 'other.example.test' | Join-Path -ChildPath '2026-01-02-000001') 'another site'
        }
    }
}

Describe 'Remove-HarCapture deletes exactly one named capture' {

    It 'deletes the capture it is given, by path, and nothing else' {
        $fx = script:New-RemoveFixture -Name 'bypath'
        & $script:RemovePs1 -Capture $fx.First -Confirm:$false
        Test-Path -LiteralPath $fx.First  | Should -BeFalse -Because 'this is the one that was named'
        Test-Path -LiteralPath $fx.Second | Should -BeTrue -Because 'and nothing else was'
        Test-Path -LiteralPath $fx.Other  | Should -BeTrue
    }

    It 'accepts a host/stamp label, resolved against the store where it is run' {
        # An operator reads `app.example.test/2026-01-02-000001` out of the
        # list; retyping the absolute path from it is friction with no purpose.
        $fx = script:New-RemoveFixture -Name 'bylabel'
        Push-Location $fx.Base
        try { & $script:RemovePs1 -Capture 'app.example.test/2026-01-02-000001' -Confirm:$false }
        finally { Pop-Location }
        Test-Path -LiteralPath $fx.First  | Should -BeFalse
        Test-Path -LiteralPath $fx.Second | Should -BeTrue
        Test-Path -LiteralPath $fx.Other  | Should -BeTrue
    }

    It 'takes one object straight from Get-HarCapture' {
        $fx = script:New-RemoveFixture -Name 'byobject'
        $one = @(& $script:ListPs1 -Path $fx.Root) |
            Where-Object { $_.Site -eq 'app.example.test' -and $_.Stamp -eq '2026-01-02-000002' }
        & $script:RemovePs1 -Capture $one -Confirm:$false
        Test-Path -LiteralPath $fx.Second | Should -BeFalse
        Test-Path -LiteralPath $fx.First  | Should -BeTrue
    }

    It 'takes the whole session directory, substitution tables included' {
        # A table is keyed by the plaintext values a scrub replaced, so
        # `.har-substitutions.json` in particular is a reverse lookup of live
        # credentials. One left behind after its capture is gone has nothing
        # left to justify it.
        $fx = script:New-RemoveFixture -Name 'withsubs'
        Set-Content -LiteralPath (Join-Path $fx.First '.har-substitutions.json') -Encoding utf8 -Value '{}'
        Set-Content -LiteralPath (Join-Path $fx.First 'scrubbed.har') -Encoding utf8 -Value '{}'
        & $script:RemovePs1 -Capture $fx.First -Confirm:$false
        Test-Path -LiteralPath $fx.First | Should -BeFalse
    }
}

Describe 'Remove-HarCapture under -WhatIf' {

    It 'deletes nothing' {
        $fx = script:New-RemoveFixture -Name 'whatif'
        & $script:RemovePs1 -Capture $fx.First -WhatIf
        Test-Path -LiteralPath $fx.First | Should -BeTrue -Because 'a preview changes no state'
        Test-Path -LiteralPath (Join-Path $fx.First 'raw.har') | Should -BeTrue
    }

    It 'names the capture it would have deleted' {
        # A dry run is where the decision gets made, so the preview has to be
        # specific enough to decide on. "Would remove a capture" is not.
        $fx = script:New-RemoveFixture -Name 'whatifsays'
        $out = (& $script:RemovePs1 -Capture $fx.First -WhatIf 6>&1 3>&1 2>&1) | Out-String
        $out | Should -Match ([regex]::Escape('2026-01-02-000001'))
        Test-Path -LiteralPath $fx.First | Should -BeTrue
    }
}

Describe 'Remove-HarCapture and the run a capture belongs to' {

    # Issue #26. Deleting is the one bulk action whose mistake cannot be undone,
    # and "the captures from that hour" is how another session's recording gets
    # deleted in a shared store. There is still no bulk delete: a run is
    # selected from the LIST, and each of its captures is removed on purpose,
    # one at a time, through the same one-capture command.

    BeforeAll {
        function script:New-RunRemoveFixture {
            param([Parameter(Mandatory)][string]$Name)

            $root = Join-Path $TestDrive $Name | Join-Path -ChildPath '.har-captures'
            $hostDir = Join-Path $root 'app.example.test'
            $record = {
                param($stamp, [hashtable]$Identity)
                $dir = Join-Path $hostDir $stamp
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $dir 'raw.har') -Encoding utf8 -Value '{"log":{"entries":[]}}'
                $body = [ordered]@{
                    uri        = 'https://app.example.test/'
                    sessionDir = $dir
                    describe   = 'the same description on every one'
                    startedUtc = '2026-01-01T00:00:00.000Z'
                    endedUtc   = '2026-01-01T00:05:00.000Z'
                }
                foreach ($key in $Identity.Keys) { $body[$key] = $Identity[$key] }
                Set-Content -LiteralPath (Join-Path $dir 'session.json') -Encoding utf8 `
                    -Value ($body | ConvertTo-Json -Depth 6)
                return $dir
            }
            $runA = @{ id = 'aaaaaaaaaaaa'; basis = 'process'; lineage = @('aaaaaaaaaaaa') }
            $runB = @{ id = 'bbbbbbbbbbbb'; basis = 'process'; lineage = @('bbbbbbbbbbbb') }
            [pscustomobject]@{
                Root = $root
                # Interleaved by stamp, as two sessions recording at once are.
                A1   = & $record '2026-04-01-000001' @{ captureId = 'capture-a1'; run = $runA }
                B1   = & $record '2026-04-01-000002' @{ captureId = 'capture-b1'; run = $runB }
                A2   = & $record '2026-04-01-000003' @{ captureId = 'capture-a2'; run = $runA }
                B2   = & $record '2026-04-01-000004' @{ captureId = 'capture-b2'; run = $runB }
                Old  = & $record '2026-04-01-000005' @{}
            }
        }
    }

    It 'removes one run''s captures, each on purpose, and leaves every other capture' {
        $fx = script:New-RunRemoveFixture -Name 'byrun'
        $run = 'aaaaaaaaaaaa'
        @(& $script:ListPs1 -Path $fx.Root) |
            Where-Object { $_.RunId -and $_.RunId -eq $run } |
            ForEach-Object { & $script:RemovePs1 -Capture $_ -Confirm:$false }

        Test-Path -LiteralPath $fx.A1 | Should -BeFalse
        Test-Path -LiteralPath $fx.A2 | Should -BeFalse
        Test-Path -LiteralPath $fx.B1 | Should -BeTrue -Because 'another run made it, in between the two that were removed'
        Test-Path -LiteralPath $fx.B2 | Should -BeTrue
        Test-Path -LiteralPath $fx.Old | Should -BeTrue -Because 'a capture with no run belongs to no selection by run'
    }

    It 'removes nothing when the run asked for is empty' {
        # An unset variable in the filter must not turn into "every capture
        # that has no run", which here is the one recorded before this change.
        $fx = script:New-RunRemoveFixture -Name 'emptyrun'
        $run = $null
        @(& $script:ListPs1 -Path $fx.Root) |
            Where-Object { $_.RunId -and $_.RunId -eq $run } |
            ForEach-Object { & $script:RemovePs1 -Capture $_ -Confirm:$false }

        foreach ($dir in $fx.A1, $fx.B1, $fx.A2, $fx.B2, $fx.Old) {
            Test-Path -LiteralPath $dir | Should -BeTrue
        }
    }

    It 'names the run in what it is about to delete' {
        # The confirmation is where the decision is made. Two captures with one
        # description are told apart there by the run that made them.
        $fx = script:New-RunRemoveFixture -Name 'saysrun'
        $out = (& $script:RemovePs1 -Capture $fx.B1 -WhatIf 6>&1 3>&1 2>&1) | Out-String
        $out | Should -Match 'run bbbbbbbbbbbb'
        $out | Should -Not -Match 'aaaaaaaaaaaa'
        Test-Path -LiteralPath $fx.B1 | Should -BeTrue
    }

    It 'says a capture has no run recorded, rather than leaving it out' {
        $fx = script:New-RunRemoveFixture -Name 'saysnorun'
        $out = (& $script:RemovePs1 -Capture $fx.Old -WhatIf 6>&1 3>&1 2>&1) | Out-String
        $out | Should -Match 'no run recorded'
    }
}

Describe 'Remove-HarCapture refuses anything but one capture' {

    It 'refuses a wildcard' {
        # A wildcard is how "delete one recording" becomes "delete several"
        # without anybody deciding to. It is refused by its SPELLING, before any
        # resolution -- so there is no path on which it expands to one match and
        # is accepted, teaching the habit.
        $fx = script:New-RemoveFixture -Name 'wildcard'
        { & $script:RemovePs1 -Capture (Join-Path $fx.HostDir '2026-01-02-*') -Confirm:$false } |
            Should -Throw -ExpectedMessage '*wildcard*'
        Test-Path -LiteralPath $fx.First  | Should -BeTrue
        Test-Path -LiteralPath $fx.Second | Should -BeTrue
    }

    It 'refuses a wildcard even when it would match exactly one capture' {
        $fx = script:New-RemoveFixture -Name 'wildcardone'
        { & $script:RemovePs1 -Capture (Join-Path $fx.HostDir '2026-01-02-000001*') -Confirm:$false } |
            Should -Throw -ExpectedMessage '*wildcard*'
        Test-Path -LiteralPath $fx.First | Should -BeTrue
    }

    It 'refuses more than one capture' {
        $fx = script:New-RemoveFixture -Name 'twoargs'
        { & $script:RemovePs1 -Capture @($fx.First, $fx.Second) -Confirm:$false } |
            Should -Throw -ExpectedMessage '*one capture*'
        Test-Path -LiteralPath $fx.First  | Should -BeTrue
        Test-Path -LiteralPath $fx.Second | Should -BeTrue
    }

    It 'refuses a whole store, and a whole host folder' {
        # The two paths that hold many captures and look like one argument.
        $fx = script:New-RemoveFixture -Name 'wholestore'
        { & $script:RemovePs1 -Capture $fx.Root -Confirm:$false } | Should -Throw
        { & $script:RemovePs1 -Capture $fx.HostDir -Confirm:$false } | Should -Throw
        Test-Path -LiteralPath $fx.First  | Should -BeTrue
        Test-Path -LiteralPath $fx.Second | Should -BeTrue
        Test-Path -LiteralPath $fx.Other  | Should -BeTrue
    }

    It 'refuses an ambiguous label by naming the matches, never picking one' {
        # The same stamp exists under two hosts. Resolving that to "the newest"
        # would make an irreversible command guess which recording was meant.
        $fx = script:New-RemoveFixture -Name 'ambiguous'
        Push-Location $fx.Base
        try {
            { & $script:RemovePs1 -Capture '2026-01-02-000001' -Confirm:$false } |
                Should -Throw -ExpectedMessage '*app.example.test*'
        }
        finally { Pop-Location }
        Test-Path -LiteralPath $fx.First | Should -BeTrue
        Test-Path -LiteralPath $fx.Other | Should -BeTrue
    }

    It 'says so when nothing matches' {
        $fx = script:New-RemoveFixture -Name 'nomatch'
        Push-Location $fx.Base
        try {
            { & $script:RemovePs1 -Capture 'app.example.test/2029-12-31-000001' -Confirm:$false } |
                Should -Throw -ExpectedMessage '*no capture*'
        }
        finally { Pop-Location }
    }

    It 'refuses a capture-shaped directory that is not inside a capture store' {
        # RAISED IN REVIEW, and the gap was real. The shared walk classifies a
        # directory by the files in it and anchors to nothing -- right for a
        # walk, wrong for the one command that deletes a tree recursively. An
        # unrelated folder holding a leftover session.json satisfied every other
        # check on the way to Remove-Item.
        #
        # Recordings only ever live under `.har-captures`, a name no option
        # redirects, which is what makes it a usable anchor.
        $loose = Join-Path $TestDrive 'outside/my-documents/2026-01-02-000001'
        New-Item -ItemType Directory -Path $loose -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $loose 'raw.har') -Encoding utf8 -Value '{"log":{"entries":[]}}'
        Set-Content -LiteralPath (Join-Path $loose 'session.json') -Encoding utf8 -Value (
            @{ sessionDir = $loose; endedUtc = '2026-01-02T00:05:00.000Z' } | ConvertTo-Json)
        Set-Content -LiteralPath (Join-Path $loose 'something-precious.txt') -Encoding utf8 -Value 'keep me'

        { & $script:RemovePs1 -Capture $loose -Confirm:$false } |
            Should -Throw -ExpectedMessage '*capture store*'
        Test-Path -LiteralPath $loose | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $loose 'something-precious.txt') | Should -BeTrue
    }

    It 'is not fooled by a directory whose name merely starts with the store name' {
        # `.har-captures-archive` is a sibling, not a store. A substring test
        # would have called it one.
        $sneaky = Join-Path $TestDrive 'outside/.har-captures-archive/app.example.test/2026-01-02-000001'
        New-Item -ItemType Directory -Path $sneaky -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $sneaky 'raw.har') -Encoding utf8 -Value '{"log":{"entries":[]}}'
        Set-Content -LiteralPath (Join-Path $sneaky 'session.json') -Encoding utf8 -Value (
            @{ sessionDir = $sneaky; endedUtc = '2026-01-02T00:05:00.000Z' } | ConvertTo-Json)

        { & $script:RemovePs1 -Capture $sneaky -Confirm:$false } |
            Should -Throw -ExpectedMessage '*capture store*'
        Test-Path -LiteralPath $sneaky | Should -BeTrue
    }

    It 'names the store directory the shared walk names' {
        # The containment check spells `.har-captures` as a literal, and
        # `capture-store.js`'s CAPTURES_DIR is the definition. Reading it at
        # runtime would make the check agree with whatever that file says,
        # including a typo; this pins the two to each other instead. Same trade
        # node-test-coverage.Tests.ps1 makes for the runner's default root.
        $walk = Get-Content -LiteralPath (
            Join-Path $script:ScriptsDir 'capture/capture-store.js') -Raw
        $walk | Should -Match "CAPTURES_DIR\s*=\s*'\.har-captures'" -Because @'
Remove-HarCapture refuses to delete anything outside a directory by that exact
name. If the walk renames the store, that check stops matching the real store
and the command refuses every legitimate delete.
'@
        # The literal lives with the guard, and the guard moved to the shared
        # module when `Invoke-HarCapture -Scrub / -Catalogue` needed the same
        # question answered (#24): one definition of "inside a capture store",
        # asked by the command that deletes and by the command that scrubs.
        $guard = Get-Content -LiteralPath (
            Join-Path $script:ScriptsDir 'har/HarStoreBatch.psm1') -Raw
        $guard | Should -Match "CapturesDirName\s*=\s*'\.har-captures'"
        # And this command really does ask that guard, rather than keeping a
        # copy the pin above would no longer be watching.
        $remove = Get-Content -LiteralPath $script:RemovePs1 -Raw
        $remove | Should -Match 'Test-HarCaptureStorePath -Path'
        $remove | Should -Not -Match 'function Test-HarCaptureStorePath'
    }

    It 'refuses a directory that is not a capture' {
        # Resolution goes through the shared walk, so "delete this folder" is
        # not something this command can be talked into.
        $fx = script:New-RemoveFixture -Name 'notacapture'
        $analysis = Join-Path $fx.Root '_analysis'
        New-Item -ItemType Directory -Path $analysis -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $analysis 'notes.md') -Encoding utf8 -Value '# notes'
        { & $script:RemovePs1 -Capture $analysis -Confirm:$false } |
            Should -Throw -ExpectedMessage '*not a capture*'
        Test-Path -LiteralPath $analysis | Should -BeTrue
    }
}

Describe 'Remove-HarCapture asks before it deletes' {

    It 'declares High impact, so the prompt appears without -Confirm' {
        # The prompt is the feature. Asserted on the declaration rather than by
        # driving an interactive host: a suite that answered a real prompt would
        # hang CI, and the attribute is what PowerShell actually acts on.
        $cmd = Get-Command -Name $script:RemovePs1 -CommandType ExternalScript
        $binding = $cmd.ScriptBlock.Ast.ParamBlock.Attributes |
            Where-Object { $_.TypeName.Name -eq 'CmdletBinding' }
        $named = @($binding.NamedArguments | ForEach-Object {
                "$($_.ArgumentName)=$($_.Argument.Extent.Text)" })
        ($named -join ';') | Should -Match 'SupportsShouldProcess' -Because (
            'without it there is no -WhatIf and no confirmation at all')
        ($named -join ';') | Should -Match "ConfirmImpact='High'" -Because (
            'a recording cannot be regenerated, so the prompt must not need -Confirm to appear')
    }

    It 'deletes when the operation is confirmed, so the refusals above are not vacuous' {
        # The ablation. Every assertion in this file that a capture SURVIVED
        # would pass against a command that never deleted anything at all.
        $fx = script:New-RemoveFixture -Name 'ablation'
        & $script:RemovePs1 -Capture $fx.First -Confirm:$false
        Test-Path -LiteralPath $fx.First | Should -BeFalse
    }
}

Describe 'the no-delete comments name the command that deletes' {

    It '<File> names Remove-HarCapture' -ForEach @(
        @{ File = 'skills/web-api-discovery/scripts/capture/capture-har.js' }
        @{ File = 'skills/web-api-discovery/scripts/har/Invoke-SanitizeHar.ps1' }
    ) {
        # Both files carry a written rule about what may delete a raw, and both
        # said `-RemoveSource` was the only way. A rule that is out of date is
        # worse than no rule: the next person reads it and believes it.
        $text = Get-Content -LiteralPath (Join-Path $script:RepoRoot $File) -Raw
        $text | Should -Match 'Remove-HarCapture'
    }

    It 'still says there is no sweep and no bulk delete' {
        # The decision that did NOT change. Adding a delete command is not
        # adding retention.
        $text = Get-Content -LiteralPath (
            Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts/capture/capture-har.js') -Raw
        $text | Should -Match '(?i)no age sweep'
        $text | Should -Match '(?i)bulk delete'
    }
}
