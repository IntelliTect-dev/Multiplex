#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Wrapper for the zero-dep Node behavior tests that pin issue #528 -- a capture
# too large to hold as a single string says so BY NAME, and says it while the
# session can still be saved. Pester is the only suite CI runs, so a Node test
# with no wrapper here is a test that never runs on a pull request.
#
#   lib/capture-size.test.js              the one definition of the ceiling and
#                                         the bands below it: the limit is the
#                                         runtime's own rather than a copied hex
#                                         literal, boundaries belong to the band
#                                         below them, every message names the
#                                         file, its size and the limit, and the
#                                         errors the runtime really raises when a
#                                         string cannot exist are recognised.
#
#   har/sanitize-har-size-limit.test.js   since the scrub streams (#10), size is
#                                         no reason to refuse: a file past the
#                                         old ceiling is refused only for not
#                                         being a HAR, writing nothing and
#                                         leaving the raw untouched.
#
#   har/sanitize-har-stream.test.js       the streamed scrub writes the bytes
#                                         the whole-document scrub wrote, and
#                                         scrubs a capture larger than its heap.
#
#   har/verify-scrub-stream.test.js       the leak gate walks a capture as it
#                                         reads it: same findings as the whole-
#                                         document gate on every fixture, same
#                                         literal counts, larger than its heap.
#
#   capture/capture-size-warning.test.js  the recorder warns once, during
#                                         recording, which is the only moment an
#                                         operator can still stop, split the
#                                         session or narrow the filter.
#
# The scrub half creates its oversized input with `ftruncate`, so a 600 MB file
# exists in milliseconds. Since #10 the scrub READS it to learn it is not a HAR,
# so that half takes seconds rather than milliseconds -- expected, not a
# regression.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
}

Describe 'a capture too large to read whole says so, in time (#528)' {
    It 'runs <Name> and all of its behavioral assertions pass' -ForEach @(
        @{ Name = 'lib/capture-size.test.js';              Expect = 'All capture-size tests passed' }
        @{ Name = 'har/sanitize-har-size-limit.test.js';   Expect = 'All sanitize-har size-limit tests passed' }
        @{ Name = 'har/sanitize-har-stream.test.js';       Expect = 'All sanitize-har-stream tests passed' }
        @{ Name = 'har/verify-scrub-stream.test.js';       Expect = 'All verify-scrub-stream tests passed' }
        @{ Name = 'capture/capture-size-warning.test.js';  Expect = 'All capture size-warning tests passed' }
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
