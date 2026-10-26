#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Behavior tests for gate/scrubber PARITY (issue #42).
#
# Issue #59 removed the CAUSE of the two engines disagreeing -- one merged policy
# now sources the gate and both verifiers. Nothing FAILED when they stopped
# agreeing, and #42 was opened about that absence, after they were caught
# disagreeing in both directions within hours: once failing open (#378, a live
# login capture blessed) and once failing closed (#395, a capture refused with no
# remedy). A guard covering one direction would have caught one and missed the
# other, so the Node file asserts three, each derived from the live tables.
#
# Delegates to the zero-dep Node script `har-scrub-gate-parity.test.js` and
# asserts exit code 0. `node --test <dir>` is deliberately not used: it fails on
# Node 26, so the file is named explicitly.
#
# The assertions below are the ones that survive the Node file being deleted,
# renamed or quietly turned into a no-op -- the pairing CI cannot be allowed to
# forget. Everything behavioral lives in the Node file.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
    $script:TestJs     = Join-Path $script:ScriptsDir 'har/har-scrub-gate-parity.test.js'
    $script:TableJs    = Join-Path $script:ScriptsDir 'har/har-parity-exceptions.js'
}

Describe 'har gate/scrubber parity guard' {
    It 'test file exists at the canonical path' {
        Test-Path -LiteralPath $script:TestJs | Should -BeTrue
    }

    It 'the exception table exists at the canonical path' {
        # The table is the deliverable, not a helper: without it every asymmetry
        # is indistinguishable from an oversight, which is the cost #42 exists to
        # remove. A missing table must fail loudly rather than skip the suite.
        Test-Path -LiteralPath $script:TableJs | Should -BeTrue
    }

    It 'both files parse without syntax errors' {
        foreach ($file in @($script:TestJs, $script:TableJs)) {
            & node --check $file 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0 -Because "$file must parse"
        }
    }

    It 'the guard reads every engine, not only the two that speak in kinds' {
        # The finding that made this assertion exist: the first draft compared the
        # shape table against the PII kind list and never read har-secrets.js --
        # the FIELD-NAME engine, whose class is the one behind #378, the
        # fails-open incident this guard cites. A kind-table comparison cannot see
        # an engine whose vocabulary is names, so its absence was invisible.
        #
        # Pinned here, in the file a reviewer reads, because deleting the require
        # would silently shrink coverage back to two engines while every remaining
        # assertion still passed.
        $text = Get-Content -LiteralPath $script:TestJs -Raw
        foreach ($engine in @('sanitize-har.js', 'pii.js', 'har-shapes.js', 'har-secrets.js', 'har-policy.js')) {
            $text | Should -BeLike "*$engine*" -Because "the guard must read $engine"
        }
        # And the completeness direction that does not depend on knowing how many
        # engines there are.
        $text | Should -BeLike '*declared-not-implemented*'
    }

    It 'every exception declares a disposition the guard accepts' {
        # Asserted here as well as in the Node file because a row is DATA: a
        # typo'd disposition would make the row excuse nothing while looking
        # exactly like a row that does, and this is the file a reviewer reads.
        $text = Get-Content -LiteralPath $script:TableJs -Raw
        foreach ($d in ([regex]::Matches($text, "disposition:\s*'([^']+)'") | ForEach-Object { $_.Groups[1].Value })) {
            $d | Should -BeIn @('by-design', 'tracked-by')
        }
    }

    It 'every tracked-by exception names an issue' {
        # A gap nobody owns is how #42 became necessary. `tracked-by` without a
        # number is the same thing wearing a label.
        $text = Get-Content -LiteralPath $script:TableJs -Raw
        foreach ($m in [regex]::Matches($text, "disposition:\s*'tracked-by',\s*\r?\n?\s*trackedBy:\s*'([^']*)'")) {
            $m.Groups[1].Value | Should -Match '^#\d+$'
        }
        # And the count of `tracked-by` dispositions must equal the count of
        # `trackedBy` fields, so a row cannot claim to be tracked while carrying
        # no field at all for the regex above to find.
        ([regex]::Matches($text, "disposition:\s*'tracked-by'")).Count |
            Should -Be ([regex]::Matches($text, 'trackedBy:').Count)
    }

    It 'neither file carries a detected value' {
        # #42's own constraint: kinds and class names only. This guard reads
        # pattern TABLES, never captures, so the only route for a value is a
        # sample someone adds later. A long base64 or hex run is the shape that
        # would arrive by copy-paste from a real capture.
        foreach ($file in @($script:TestJs, $script:TableJs)) {
            $text = Get-Content -LiteralPath $file -Raw
            $text | Should -Not -Match '[A-Za-z0-9+/=_-]{60,}'
        }
    }

    It 'all behavioral assertions pass' {
        $out = & node $script:TestJs 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) {
            Write-Host ($out -join "`n")
        }
        $exit | Should -Be 0
        ($out -join "`n") | Should -Match 'All har-scrub-gate-parity tests passed'
    }
}
