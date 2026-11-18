#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Wrapper for the zero-dep Node behavior tests pinning issue #21 -- the runner
# that holds the post-capture pipeline to five mechanical checks over pinned
# raw captures. Pester is the only suite CI runs, so a Node test with no
# wrapper here is a test that never runs on a pull request.
#
#   pipeline-check.test.js   the runner, run as a command against synthetic
#                            captures: the five checks, the SHA-256 pin, the
#                            regression ratchet, and that no captured value
#                            reaches the console or the run record.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
}

Describe 'the pipeline runner over pinned captures' {
    It 'runs har/pipeline-check.test.js and all of its behavioral assertions pass' {
        $testJs = Join-Path $script:ScriptsDir 'har/pipeline-check.test.js'
        Test-Path -LiteralPath $testJs | Should -BeTrue

        $out = & node $testJs 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) { Write-Host ($out -join "`n") }
        $exit | Should -Be 0
        ($out -join "`n") | Should -Match 'All pipeline-check tests passed'
    }
}
