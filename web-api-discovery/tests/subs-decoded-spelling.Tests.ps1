#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Wrapper for the zero-dep Node behavior tests pinning issue #67 -- a secret
# replaced in its percent-encoded wire spelling must not survive in its DECODED
# spelling elsewhere in the capture. Pester is the only suite CI runs, so a
# Node test with no wrapper here is a test that never runs on a pull request.
#
#   har/subs-decoded-spelling.test.js   the scrub, run as a command: a decoded
#                                       echo of a form-body, params[] or query
#                                       secret gets the field's own fake; a
#                                       secret with no escapes is unaffected;
#                                       and the survivor guard (exit 5) holds
#                                       the decoded spelling.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
}

Describe 'a secret recorded percent-encoded does not survive decoded (#67)' {
    It 'runs har/subs-decoded-spelling.test.js and all of its behavioral assertions pass' {
        $testJs = Join-Path $script:ScriptsDir 'har/subs-decoded-spelling.test.js'
        Test-Path -LiteralPath $testJs | Should -BeTrue

        $out = & node $testJs 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) { Write-Host ($out -join "`n") }
        $exit | Should -Be 0
        ($out -join "`n") | Should -Match 'All subs-decoded-spelling tests passed'
    }
}
