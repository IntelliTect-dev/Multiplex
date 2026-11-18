#Requires -Version 7.0
#Requires -Modules @{ ModuleName = "Pester"; ModuleVersion = "5.0.0" }

# Behavior tests for the xUnit + Pester test scaffolds emitted by
# web-api-discovery codegen (issue #50). Builds on PR #49 (codegen pipeline).

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot "..\") | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot "skills/web-api-discovery/scripts"
    $script:GenJs      = Join-Path $script:ScriptsDir "codegen/generate-wrapper.js"
    $script:TmplDir    = Join-Path $script:RepoRoot "skills/web-api-discovery/csharp/tests"
    $script:RestHar    = Join-Path $script:RepoRoot "tests/fixtures/har/rest-3endpoints.har"

    function New-OutDir {
        $d = Join-Path ([IO.Path]::GetTempPath()) ("wrapgen-tests-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force $d | Out-Null
        return $d
    }

    function Invoke-Gen {
        param([string]$Har, [string]$Out)
        & node $script:GenJs `
            --har $Har `
            --out $Out `
            --project-name "ExampleEx" `
            --namespace "Example" `
            --base-url "https://api.example.com" `
            --auth-model "cookie" `
            --authors "IntelliTect" `
            --description "Example wrapper" `
            --repository-url "https://github.com/example/example" `
            --package-tags "example;api;wrapper" 2>&1 | Out-Null
        return $LASTEXITCODE
    }
}

Describe "tests-templates directory exists" {
    It "lives at the canonical path" {
        Test-Path -LiteralPath $script:TmplDir | Should -BeTrue
    }
    It "contains a manifest.json" {
        Test-Path -LiteralPath (Join-Path $script:TmplDir "manifest.json") | Should -BeTrue
    }
    It "declares the required .tmpl files" {
        $manifest = Get-Content -Raw (Join-Path $script:TmplDir "manifest.json") | ConvertFrom-Json
        $expected = @(
            "Tests.csproj.tmpl",
            "FixtureLoader.cs.tmpl",
            "ClientTests.cs.tmpl",
            "MockHandler.cs.tmpl",
            "PacingHandlerTests.cs.tmpl",
            "ToolSessionTests.cs.tmpl",
            "pester/Mcp.Tests.ps1.tmpl",
            "pester/run-pester.ps1.tmpl"
        ) | Sort-Object
        $actual = @($manifest.templates | ForEach-Object { $_.file } | Sort-Object)
        ($actual -join ",") | Should -Be ($expected -join ",")
    }
}

Describe "Manifest body token parity" {
    It "every {{Token}} in a body is declared in the manifest entry" {
        $manifest = Get-Content -Raw (Join-Path $script:TmplDir "manifest.json") | ConvertFrom-Json
        foreach ($entry in $manifest.templates) {
            $path = Join-Path $script:TmplDir $entry.file
            Test-Path $path | Should -BeTrue -Because "manifest references $($entry.file)"
            $body = Get-Content -Raw $path
            $found = @([regex]::Matches($body, "\{\{([A-Za-z]+)\}\}") |
                ForEach-Object { $_.Groups[1].Value } |
                Sort-Object -Unique) -join ","
            $declared = @($entry.requiredTokens | Sort-Object -Unique) -join ","
            $found | Should -Be $declared -Because "tokens-in-body vs manifest parity for $($entry.file)"
        }
    }
}

Describe "Token substitution leaves no markers" {
    It "every template substitutes cleanly with the canonical token map" {
        $tokens = @{
            ProjectName     = "ExampleEx"
            Namespace       = "Example"
            TestProjectName = "ExampleEx.Tests"
            Facts           = "    // (facts injected by generator)"
            ExpectedMethods = '        "GetExampleAsync"'
        }
        $manifest = Get-Content -Raw (Join-Path $script:TmplDir "manifest.json") | ConvertFrom-Json
        foreach ($entry in $manifest.templates) {
            $body = Get-Content -Raw (Join-Path $script:TmplDir $entry.file)
            $out = $body
            foreach ($k in $tokens.Keys) { $out = $out.Replace("{{$k}}", $tokens[$k]) }
            $out | Should -Not -Match "\{\{" -Because "$($entry.file) has unresolved tokens"
        }
    }
}

Describe "Codegen integration: emits test project" {
    BeforeAll {
        $script:Out = New-OutDir
        $script:ExitCode = Invoke-Gen -Har $script:RestHar -Out $script:Out
        $script:TestProjDir = Join-Path $script:Out "tests/ExampleEx.Tests"
    }
    AfterAll {
        if (Test-Path $script:Out) { Remove-Item -Recurse -Force $script:Out -ErrorAction SilentlyContinue }
    }
    It "generator exits 0" { $script:ExitCode | Should -Be 0 }
    It "emits the test project directory" {
        Test-Path $script:TestProjDir | Should -BeTrue
    }
    It "emits the test csproj" {
        Test-Path (Join-Path $script:TestProjDir "ExampleEx.Tests.csproj") | Should -BeTrue
    }
    It "emits FixtureLoader.cs and MockHandler.cs" {
        Test-Path (Join-Path $script:TestProjDir "FixtureLoader.cs") | Should -BeTrue
        Test-Path (Join-Path $script:TestProjDir "MockHandler.cs") | Should -BeTrue
    }
    It "emits PacingHandlerTests.cs, so every wrapper tests its own pacing and throttling" {
        $file = Join-Path $script:TestProjDir "PacingHandlerTests.cs"
        Test-Path $file | Should -BeTrue
        $body = Get-Content $file -Raw
        $body | Should -Not -Match '\{\{' -Because 'every token is substituted'
        # The behaviours the handler promises, each pinned by its own fact.
        foreach ($fact in @(
            'Requests_are_spaced_by_the_minimum_plus_bounded_jitter',
            'Only_one_request_is_in_flight_by_default',
            'A_429_waits_out_Retry_After_in_seconds_then_retries',
            'A_503_honours_Retry_After_as_an_HTTP_date',
            'Without_Retry_After_it_backs_off_exponentially_with_jitter',
            'Retries_that_run_out_surface_a_throttled_exception',
            'A_Retry_After_longer_than_the_cap_is_surfaced_not_waited_or_cut_short',
            'The_handlers_own_backoff_is_never_taken_for_a_wait_the_site_asked_for',
            'A_Retry_After_too_large_for_dotnet_to_read_is_a_long_wait_not_no_wait',
            'A_negative_or_garbage_Retry_After_is_treated_as_absent',
            'Disposing_the_client_while_a_request_waits_surfaces_the_cancellation',
            'POST_is_never_retried_even_when_listed',
            'PUT_and_DELETE_are_retried_only_when_configured',
            'A_block_from_the_edge_is_never_retried',
            'A_Cloudflare_challenge_served_as_a_503_is_a_block_and_is_never_retried',
            'A_503_body_is_read_only_up_to_the_challenge_bound',
            'A_403_carrying_x_datadome_is_a_block_whatever_its_content_type',
            'PacingOptions_None_adds_no_wait',
            'The_client_sends_through_the_pacing_handler',
            'The_client_names_itself_and_sends_no_browser_fingerprint',
            'The_User_Agent_is_whatever_the_options_say',
            'A_User_Agent_that_is_not_one_is_refused',
            'A_bring_your_own_HttpClient_reads_only_the_User_Agent_from_the_environment')) {
            $body | Should -Match ([regex]::Escape($fact)) -Because "PacingHandlerTests must keep the fact $fact"
        }
        $body | Should -Match 'ExampleExThrottledException' -Because 'the exception carries the project name'
        $body | Should -Match 'new ExampleExClient\(creds, Unspaced, site\)' -Because 'one fact proves the client is wired through the handler'
    }
    It "emits ToolSessionTests.cs, so every wrapper tests when its MCP server reads the session" {
        $file = Join-Path $script:TestProjDir "ToolSessionTests.cs"
        Test-Path $file | Should -BeTrue
        $body = Get-Content $file -Raw
        $body | Should -Not -Match '\{\{' -Because 'every token is substituted'
        # What the tool session promises, each pinned by its own fact. They run
        # in "Buildable + runnable test project" below.
        foreach ($fact in @(
            'Creating_the_tool_session_reads_no_saved_session',
            'The_first_call_reads_the_saved_session_and_later_calls_reuse_its_client',
            'Without_a_saved_session_a_call_fails_in_words_the_model_can_pass_on',
            'A_session_saved_after_a_failed_call_is_used_without_a_restart',
            'An_expired_session_is_reported_and_the_next_call_reads_the_store_again',
            'A_refusal_from_the_site_reaches_the_caller_and_keeps_the_session',
            'An_answer_that_is_not_the_expected_json_does_not_claim_nothing_happened',
            'Any_other_failure_is_left_for_the_server_to_report_without_its_detail',
            'A_call_after_the_tool_session_is_disposed_is_refused')) {
            $body | Should -Match ([regex]::Escape($fact)) -Because "ToolSessionTests must keep the fact $fact"
        }
        $body | Should -Match 'class ExampleExToolSessionTests' -Because 'the test class carries the project name'
    }
    It "keeps the pacing facts out of the one-[Fact]-per-endpoint ClientTests.* files" {
        Get-ChildItem $script:TestProjDir -Filter "ClientTests.*.cs" | ForEach-Object {
            Get-Content $_.FullName -Raw | Should -Not -Match 'PacingHandler'
        }
    }
    It "emits at least one ClientTests.<Group>.cs" {
        $files = Get-ChildItem $script:TestProjDir -Filter "ClientTests.*.cs"
        $files.Count | Should -BeGreaterOrEqual 1
    }
    It "emits exactly one [Fact] per detected endpoint" {
        $clientCs = Get-Content (Join-Path $script:Out "src/ExampleEx/ExampleExClient.Generated.cs") -Raw
        $methodCount = ([regex]::Matches($clientCs, "public\s+async\s+Task<.*?>\s+\w+Async\s*\(")).Count
        $methodCount | Should -BeGreaterThan 0
        $factCount = 0
        Get-ChildItem $script:TestProjDir -Filter "ClientTests.*.cs" | ForEach-Object {
            $body = Get-Content $_.FullName -Raw
            $factCount += ([regex]::Matches($body, "\[Fact\]\s*[\r\n]+\s*public\s+async")).Count
        }
        $factCount | Should -Be $methodCount
    }
    It "test csproj references the wrapper project" {
        $csproj = Get-Content (Join-Path $script:TestProjDir "ExampleEx.Tests.csproj") -Raw
        $csproj | Should -Match "ProjectReference\s+Include=`"[^`"]*ExampleEx\.csproj"
    }
    It "test csproj pins NuGet versions for xunit + Moq + Test.Sdk" {
        $csproj = Get-Content (Join-Path $script:TestProjDir "ExampleEx.Tests.csproj") -Raw
        $csproj | Should -Match 'PackageReference\s+Include="Microsoft\.NET\.Test\.Sdk"\s+Version="\d'
        $csproj | Should -Match 'PackageReference\s+Include="xunit"\s+Version="\d'
        $csproj | Should -Match 'PackageReference\s+Include="xunit\.runner\.visualstudio"\s+Version="\d'
        $csproj | Should -Match 'PackageReference\s+Include="Moq"\s+Version="\d'
    }
    It "emits Pester scaffolds under tests/pester/" {
        Test-Path (Join-Path $script:TestProjDir "pester/Mcp.Tests.ps1") | Should -BeTrue
        Test-Path (Join-Path $script:TestProjDir "pester/run-pester.ps1") | Should -BeTrue
    }
    It "Mcp.Tests.ps1 references every generated method name" {
        $clientCs = Get-Content (Join-Path $script:Out "src/ExampleEx/ExampleExClient.Generated.cs") -Raw
        $methods = [regex]::Matches($clientCs, "public\s+async\s+Task<.*?>\s+(\w+Async)\s*\(") |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        $methods.Count | Should -BeGreaterThan 0
        $pester = Get-Content (Join-Path $script:TestProjDir "pester/Mcp.Tests.ps1") -Raw
        foreach ($m in $methods) {
            $pester | Should -Match ([regex]::Escape($m)) -Because "Mcp.Tests.ps1 must mention method $m"
        }
    }
}

Describe "Determinism (generated test project)" {
    It "two runs produce byte-identical test-project files" {
        $a = New-OutDir; $b = New-OutDir
        try {
            Invoke-Gen -Har $script:RestHar -Out $a | Out-Null
            Invoke-Gen -Har $script:RestHar -Out $b | Out-Null
            $aTests = Join-Path $a "tests/ExampleEx.Tests"
            $bTests = Join-Path $b "tests/ExampleEx.Tests"
            $filesA = Get-ChildItem $aTests -Recurse -File | Sort-Object FullName
            $filesB = Get-ChildItem $bTests -Recurse -File | Sort-Object FullName
            $filesA.Count | Should -Be $filesB.Count
            for ($i = 0; $i -lt $filesA.Count; $i++) {
                (Get-FileHash -Algorithm SHA256 $filesA[$i].FullName).Hash |
                    Should -Be (Get-FileHash -Algorithm SHA256 $filesB[$i].FullName).Hash
            }
        } finally {
            Remove-Item -Recurse -Force $a -ErrorAction SilentlyContinue
            Remove-Item -Recurse -Force $b -ErrorAction SilentlyContinue
        }
    }
}

Describe "Buildable + runnable test project" {
    BeforeAll {
        $script:DotnetAvailable = $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)
        $script:Out = New-OutDir
        if ($script:DotnetAvailable) {
            Invoke-Gen -Har $script:RestHar -Out $script:Out | Out-Null
        }
    }
    AfterAll {
        if ($script:Out -and (Test-Path $script:Out)) {
            Remove-Item -Recurse -Force $script:Out -ErrorAction SilentlyContinue
        }
    }
    It "dotnet test on the generated test project succeeds" {
        if (-not $script:DotnetAvailable) {
            Set-ItResult -Skipped -Because "dotnet SDK not available"
            return
        }
        $testProj = Join-Path $script:Out "tests/ExampleEx.Tests"
        Push-Location $testProj
        try {
            $out = & dotnet test --nologo -v minimal 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because "dotnet test must succeed. Output:`n$out"
        } finally {
            Pop-Location
        }
    }
}