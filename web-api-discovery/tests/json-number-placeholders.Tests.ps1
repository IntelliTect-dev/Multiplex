#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Wrapper for the zero-dep Node behavior tests pinning issues #23 and #68 -- a
# scrub replacement that lands on a bare JSON number must leave valid JSON.
# Pester is the only suite CI runs, so a Node test with no wrapper here is a
# test that never runs on a pull request.
#
#   har/json-number-placeholders.test.js   the scrub, run as a command: a bare
#                                          number becomes a QUOTED placeholder
#                                          inside real JSON (bodies, prefixed
#                                          bodies, form fields and params[]);
#                                          text that is not JSON keeps the plain
#                                          replacement; a decimal cookie is not
#                                          a token.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
}

Describe 'a replacement over a JSON number leaves valid JSON (#23, #68)' {
    It 'runs har/json-number-placeholders.test.js and all of its behavioral assertions pass' {
        $testJs = Join-Path $script:ScriptsDir 'har/json-number-placeholders.test.js'
        Test-Path -LiteralPath $testJs | Should -BeTrue

        $out = & node $testJs 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) { Write-Host ($out -join "`n") }
        $exit | Should -Be 0
        ($out -join "`n") | Should -Match 'All json-number-placeholders tests passed'
    }
}
