#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Every Node test file must be reachable from CI.
#
# THE FAILURE THIS EXISTS TO STOP IS UNCHANGED, and it is a quiet one: a
# `*.test.js` that is never executed on the pull request passes on the author's
# machine and reports green either way. That is worse than having no test, since
# the suite's own coverage is what a reviewer trusts when deciding a change is
# safe. Same class of defect as #304 -- a suite that never ran reading as a suite
# that passed.
#
# WHAT CHANGED IS THE MECHANISM (#94). Reachability used to mean "some Pester
# wrapper in this directory names this file in its executable text", because CI's
# only job was Pester and a wrapper was the sole way a Node file got run. That
# guard carried a hole it documented at length -- a mention and an invocation
# could not be associated, so an incidental mention in a wrapper that ran
# something else granted FALSE coverage -- and it imposed a rule every
# contributor had to know or silently lose their coverage.
#
# CI now runs every `*.test.js` directly, through
# `scripts/Invoke-NodeTestSuite.ps1`. Reachability is no longer a property of
# what some wrapper happens to mention; it is a property of two things, and those
# are what this suite pins:
#
#   1. THE GATE EXISTS. ci.yml still has a step that invokes the runner. Delete
#      that step and 81 suites stop running while CI stays green -- the same
#      false green as before, one level up.
#   2. THE GATE'S REACH COVERS THE TREE. Every `*.test.js` lives under the root
#      the runner sweeps by default, so none sits outside it.
#
# Plus the runner's own contract, driven against a throwaway tree: a failing file
# fails the run and is named with its output, and a search that matched nothing is
# an error rather than a green run over zero files. A gate that cannot fail is not
# a gate.

BeforeAll {
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
    $script:Runner = Join-Path $script:RepoRoot 'scripts/Invoke-NodeTestSuite.ps1'
    # The skill lives in a subfolder of Multiplex; GitHub runs only workflows at
    # the repository root, so that is where its CI is.
    $script:Workflow = Join-Path $script:RepoRoot '..' '.github/workflows/web-api-discovery.yml'
    # The root the runner sweeps when -Path is not given. A literal here ON
    # PURPOSE: reading the default out of the script under test would make this
    # suite agree with whatever that script says, including a typo, instead of
    # pinning where the tests actually are.
    $script:DefaultRoot = 'skills'

    function script:New-NodeTestTree {
        <#
            A throwaway tree of self-running Node scripts, in the same shape as
            the real ones: exit 0 is a pass, non-zero is a failure.
        #>
        param([hashtable]$Files)

        $root = Join-Path $TestDrive ('nodetree-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        foreach ($name in $Files.Keys) {
            $path = Join-Path $root $name
            $dir = Split-Path $path -Parent
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Set-Content -LiteralPath $path -Value $Files[$name] -Encoding utf8
        }
        return $root
    }

    function script:Invoke-Runner {
        # Out-of-process, because the contract under test is an EXIT CODE. A
        # dot-sourced run would take this Pester process down with its `exit`.
        param([string[]]$Arguments)

        $out = & pwsh -NoProfile -File $script:Runner @Arguments 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }
}

Describe 'the Node suites are reachable from CI' {

    It 'CI invokes the Node test runner' {
        Test-Path -LiteralPath $script:Runner | Should -BeTrue -Because 'the runner is what makes every Node suite reachable'
        $workflow = Get-Content -LiteralPath $script:Workflow -Raw
        $workflow | Should -Match 'Invoke-NodeTestSuite\.ps1' -Because @'
Without that step, 81 Node suites stop running and CI still reports green --
the same false green this suite has always existed to prevent, one level up.
'@
    }

    It 'every *.test.js in the repository sits under the root the runner sweeps' {
        # Exclusion is computed on the path RELATIVE to the repo root, not the
        # full path: this suite itself runs from inside `.worktrees/<branch>/`
        # during development, and matching `.worktrees` anywhere in the full path
        # excluded every file in the repository -- a check that silently passed
        # over nothing, which is the exact defect class this file exists for. The
        # `.Count -gt 0` assertion below is what caught it.
        $all = @(Get-ChildItem -LiteralPath $script:RepoRoot -Recurse -File -Filter '*.test.js' -ErrorAction SilentlyContinue |
            Where-Object {
                $rel = [System.IO.Path]::GetRelativePath($script:RepoRoot, $_.FullName)
                $rel -notmatch '^\.worktrees[\\/]'
            })
        $all.Count | Should -BeGreaterThan 0 -Because 'finding no Node tests at all would make this check vacuous'

        # Containment is decided on the RELATIVE path, not a raw string prefix.
        # `StartsWith('<repo>\skills')` also matches a sibling whose name merely
        # starts with it -- a future `skills-archive/` or `skillset/` would read
        # as inside the sweep while the runner's own
        # `Get-ChildItem (Join-Path $repoRoot 'skills')` never reaches it, so a
        # test in there would go unrun while this guard called it covered. That is
        # the same prefix-matching mistake the `.worktrees` exclusion above made,
        # and precisely the gap this file exists to catch.
        # The trailing separator is the whole point, and it is compared rather
        # than pattern-matched so no regex escaping stands between this check and
        # the answer.
        $sep = [System.IO.Path]::DirectorySeparatorChar
        $searchedRoot = (Join-Path $script:RepoRoot $script:DefaultRoot).TrimEnd($sep, [System.IO.Path]::AltDirectorySeparatorChar) + $sep
        $outside = @($all | Where-Object { -not $_.FullName.StartsWith($searchedRoot, [System.StringComparison]::OrdinalIgnoreCase) })

        ($outside | ForEach-Object { $_.FullName }) | Should -BeNullOrEmpty -Because @"
A Node test outside '$($script:DefaultRoot)/' is never swept by the runner, so it
never runs on the pull request while reporting nothing. Move it under that root,
or extend the runner's default -Path in the same change.
"@
    }
}

Describe 'the Node test runner is a gate that can fail' {

    It 'fails the run, and names the file, when one Node test fails' {
        $tree = script:New-NodeTestTree @{
            'nested/passing.test.js'        = 'process.exit(0);'
            'nested/deeper/failing.test.js' = 'console.log("progress"); console.error("the reason"); process.exit(3);'
        }

        $result = script:Invoke-Runner @('-Path', $tree)

        $result.ExitCode | Should -Be 1 -Because 'a failing Node suite must turn the run red'
        $result.Output | Should -Match 'failing\.test\.js'
        $result.Output | Should -Match 'exit 3' -Because 'the exit code is the first thing someone needs'
        $result.Output | Should -Match 'the reason' -Because "a failing file's stderr is where the cause is"
        $result.Output | Should -Match 'progress' -Because 'and its stdout carries how far it got'
    }

    It 'passes when every Node test passes, and finds them recursively' {
        $tree = script:New-NodeTestTree @{
            'top.test.js'                  = 'process.exit(0);'
            'nested/middle.test.js'        = 'process.exit(0);'
            'nested/deeper/bottom.test.js' = 'process.exit(0);'
        }

        $result = script:Invoke-Runner @('-Path', $tree)

        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match 'Running 3 Node test suite\(s\)' -Because 'all three, the nested ones included'
    }

    It 'refuses a search that matched nothing instead of reporting a green run over zero files' {
        $tree = script:New-NodeTestTree @{ 'notatest.js' = 'process.exit(0);' }

        $result = script:Invoke-Runner @('-Path', $tree)

        $result.ExitCode | Should -Not -Be 0 -Because 'zero files gated is zero assurance, and must not read as success'
        $result.Output | Should -Match 'No \*\.test\.js found'
    }

    It 'refuses a -Filter that matched nothing for the same reason' {
        $tree = script:New-NodeTestTree @{ 'nested/real.test.js' = 'process.exit(0);' }

        $result = script:Invoke-Runner @('-Path', $tree, '-Filter', '*matches-nothing*')

        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'No \*\.test\.js found'
    }
}
