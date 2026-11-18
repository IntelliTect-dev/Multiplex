#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Behavior tests for the HAR -> C# codegen pipeline (issue #48).

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
    $script:GenJs      = Join-Path $script:ScriptsDir 'codegen/generate-wrapper.js'
    $script:CaptureJs  = Join-Path $script:ScriptsDir 'capture/capture-cdp.js'
    $script:RestHar    = Join-Path $script:RepoRoot 'tests/fixtures/har/rest-3endpoints.har'
    $script:GqlHar     = Join-Path $script:RepoRoot 'tests/fixtures/har/graphql.har'

    function New-OutDir {
        $d = Join-Path ([IO.Path]::GetTempPath()) ("wrapgen-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force $d | Out-Null
        return $d
    }

    function Invoke-Gen {
        param(
            [string[]]$Har,
            [string]$Out,
            [string]$ProjectName = 'ExampleEx',
            [string]$Namespace   = 'Example',
            [string]$BaseUrl     = 'https://api.example.com',
            [string]$Authors     = 'IntelliTect',
            [string]$Description = 'Example wrapper',
            [string]$RepoUrl     = 'https://github.com/example/example',
            [string]$Tags        = 'example;api;wrapper',
            [string]$AuthModel   = 'cookie'
        )
        $harArg = ($Har -join ',')
        $stdout = & node $script:GenJs `
            --har $harArg `
            --out $Out `
            --project-name $ProjectName `
            --namespace $Namespace `
            --base-url $BaseUrl `
            --auth-model $AuthModel `
            --authors $Authors `
            --description $Description `
            --repository-url $RepoUrl `
            --package-tags $Tags 2>&1
        return @{ ExitCode = $LASTEXITCODE; Output = ($stdout -join "`n") }
    }
}

Describe 'generate-wrapper.js exists' {
    It 'lives at the canonical path' {
        Test-Path -LiteralPath $script:GenJs | Should -BeTrue
    }
    It 'parses without syntax errors' {
        & node --check $script:GenJs 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
}

Describe 'har-codegen end-to-end (REST)' {
    BeforeAll {
        $script:RestOut = New-OutDir
        $script:RestRes = Invoke-Gen -Har @($script:RestHar) -Out $script:RestOut
    }
    AfterAll {
        if (Test-Path $script:RestOut) { Remove-Item -Recurse -Force $script:RestOut }
    }
    It 'exits 0' { $script:RestRes.ExitCode | Should -Be 0 }
    It 'emits Client.Generated.cs' {
        Test-Path (Join-Path $script:RestOut 'src/ExampleEx/ExampleExClient.Generated.cs') | Should -BeTrue
    }
    It 'emits Models.Generated.cs' {
        Test-Path (Join-Path $script:RestOut 'src/ExampleEx/Models.Generated.cs') | Should -BeTrue
    }
    It 'emits the .csproj' {
        Test-Path (Join-Path $script:RestOut 'src/ExampleEx/ExampleEx.csproj') | Should -BeTrue
    }
    It 'emits the hand-written Client.cs (template substituted)' {
        Test-Path (Join-Path $script:RestOut 'src/ExampleEx/Client.cs') | Should -BeTrue
    }
    It 'method names match URL paths' {
        $code = Get-Content (Join-Path $script:RestOut 'src/ExampleEx/ExampleExClient.Generated.cs') -Raw
        $code | Should -Match 'GetMeAsync'
        $code | Should -Match 'GetUsers.*Async'
    }
}

Describe 'pacing + throttling handler in the generated client' {
    # Every generated client is polite by construction: the handler is emitted,
    # the client's own constructor puts it in front of the network, the MCP
    # server builds the client with it, and the project's tests pin it. The
    # build and `dotnet test` of this output are asserted in agent-e2e and
    # tests-templates; these pin that the pieces are present and connected.
    BeforeAll {
        $script:PaceOut = New-OutDir
        $script:PaceRes = Invoke-Gen -Har @($script:RestHar) -Out $script:PaceOut
        $script:ProjDir = Join-Path $script:PaceOut 'src/ExampleEx'
    }
    AfterAll {
        if (Test-Path $script:PaceOut) { Remove-Item -Recurse -Force $script:PaceOut }
    }
    It 'emits PacingHandler.cs with its options and exceptions, tokens substituted' {
        $script:PaceRes.ExitCode | Should -Be 0
        $file = Join-Path $script:ProjDir 'PacingHandler.cs'
        Test-Path $file | Should -BeTrue
        $code = Get-Content $file -Raw
        $code | Should -Match 'public sealed class PacingHandler : DelegatingHandler'
        $code | Should -Match 'public sealed class PacingOptions'
        $code | Should -Match 'class ExampleExThrottledException : HttpRequestException'
        $code | Should -Match 'class ExampleExBlockedException : HttpRequestException'
        $code | Should -Match 'namespace Example;'
        $code | Should -Not -Match '\{\{'
    }
    It 'retries only idempotent methods, defaulting to GET and HEAD' {
        $code = Get-Content (Join-Path $script:ProjDir 'PacingHandler.cs') -Raw
        $code | Should -Match 'IdempotentMethods\s*=\s*new HashSet<HttpMethod>\s*\{\s*HttpMethod\.Get, HttpMethod\.Head, HttpMethod\.Options, HttpMethod\.Trace, HttpMethod\.Put, HttpMethod\.Delete,'
        $code | Should -Match 'RetryMethods \{ get; init; \} = new HashSet<HttpMethod> \{ HttpMethod\.Get, HttpMethod\.Head \}'
        $code | Should -Match 'PacingOptions\.IdempotentMethods\.Contains\(request\.Method\) && _options\.RetryMethods\.Contains\(request\.Method\)' -Because 'a POST listed in RetryMethods must still never be retried'
    }
    It 'the client builds its pipeline through the handler, and does not time out the polite waits' {
        $client = Get-Content (Join-Path $script:ProjDir 'Client.cs') -Raw
        $client | Should -Match 'new HttpClient\(new PacingHandler\(pacing, _log\) \{ InnerHandler = network \}\)'
        $client | Should -Match 'Timeout = Timeout\.InfiniteTimeSpan'
        $client | Should -Match ': this\(session, PacingOptions\.FromEnvironment\(\), innerHandler: null, log\)' -Because 'the everyday constructor is paced too'
        $client | Should -Not -Match 'Timeout = TimeSpan\.FromSeconds\(60\)' -Because 'a whole-request timeout would cancel a 60 s Retry-After wait'
    }
    It 'the MCP server registers the options and builds the client with them' {
        $mcp = Get-Content (Join-Path $script:ProjDir 'McpProgram.cs') -Raw
        $mcp | Should -Match 'var pacing = PacingOptions\.FromEnvironment\(\);\s+builder\.Services\.AddSingleton\(pacing\);' -Because 'read before the host is built (agent-e2e Stage 7b runs it)'
        $mcp | Should -Match 'sp\.GetRequiredService<PacingOptions>\(\)'
    }
    It 'the generated README tells a consumer how the client paces and how to tune it' {
        $readme = Get-Content (Join-Path $script:PaceOut 'README.md') -Raw
        $readme | Should -Match 'Retry-After'
        $readme | Should -Match 'EXAMPLEEX_PACING_MIN_SPACING_MS'
        $readme | Should -Match '(?i)never retried'
    }
}

Describe 'the generated client says what it is' {
    # The boundary in SKILL.md ("Pacing, throttling, and where the line is"):
    # a generated client names itself and sends only the headers the API needs.
    # Nothing it sends is there to pass for a browser. The generated project's
    # own PacingHandlerTests check the headers on a real request; these check
    # the emitted source and README. Only src/ is scanned: the generated tests
    # name the forbidden strings in order to assert they are absent.
    BeforeAll {
        $script:IdOut = New-OutDir
        $script:IdRes = Invoke-Gen -Har @($script:RestHar) -Out $script:IdOut
        $script:IdSources = @(Get-ChildItem (Join-Path $script:IdOut 'src') -Recurse -File -Filter '*.cs' |
            ForEach-Object { [pscustomobject]@{ Name = $_.Name; Body = (Get-Content $_.FullName -Raw) } })
    }
    AfterAll {
        if (Test-Path $script:IdOut) { Remove-Item -Recurse -Force $script:IdOut }
    }
    It 'emits client source to check' {
        $script:IdRes.ExitCode | Should -Be 0 -Because $script:IdRes.Output
        $script:IdSources.Name | Should -Contain 'Client.cs'
    }
    It 'sends no browser User-Agent' {
        foreach ($f in $script:IdSources) {
            $f.Body | Should -Not -Match 'Mozilla/\d|AppleWebKit|Chrome/\d|Safari/\d|Gecko\)' -Because "$($f.Name) must not carry a browser's User-Agent"
        }
    }
    It 'sends no browser fingerprint headers' {
        foreach ($f in $script:IdSources) {
            $f.Body | Should -Not -Match '(?i)sec-ch-ua|sec-fetch-|upgrade-insecure-requests' -Because "$($f.Name) must not send browser fingerprint headers"
        }
    }
    It 'the README says what the client calls itself and how to name your own deployment' {
        $readme = Get-Content (Join-Path $script:IdOut 'README.md') -Raw
        $readme | Should -Match 'ExampleEx/<version>'
        $readme | Should -Match 'EXAMPLEEX_PACING_USER_AGENT'
    }
}

Describe 'endpoint-dedup' {
    It 'collapses /users/{int} into a single method with id param' {
        $out = New-OutDir
        try {
            $r = Invoke-Gen -Har @($script:RestHar) -Out $out
            $r.ExitCode | Should -Be 0
            $code = Get-Content (Join-Path $out 'src/ExampleEx/ExampleExClient.Generated.cs') -Raw
            # exactly one GetUsers<id>Async method (the /users/{id} endpoint)
            $matches = [regex]::Matches($code, 'public\s+async\s+Task<.*?>\s+GetUsersByIdAsync\s*\(')
            $matches.Count | Should -Be 1
            # signature carries an int id
            $code | Should -Match 'GetUsersByIdAsync\s*\(\s*int\s+id'
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'shape-merging' {
    It 'merges /me captures into one record with nullable optional fields' {
        $out = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $out | Out-Null
            $models = Get-Content (Join-Path $out 'src/ExampleEx/Models.Generated.cs') -Raw
            # required fields (present in both): Id, Name, Email -> non-nullable string
            $models | Should -Match 'public\s+string\s+Id\s*\{\s*get'
            $models | Should -Match 'public\s+string\s+Email\s*\{\s*get'
            # optional field (only present in second capture): Avatar -> nullable
            $models | Should -Match 'public\s+string\?\s+Avatar\s*\{\s*get'
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'graphql-detector' {
    It 'emits a GraphQLAsync method for graphql endpoints' {
        $out = New-OutDir
        try {
            $r = Invoke-Gen -Har @($script:GqlHar) -Out $out -ProjectName 'GqlEx' -Namespace 'Gql'
            $r.ExitCode | Should -Be 0
            $code = Get-Content (Join-Path $out 'src/GqlEx/GqlExClient.Generated.cs') -Raw
            $code | Should -Match 'GraphQLAsync<'
            # should NOT have generated a REST-style PostGraphqlAsync per individual entry
            $code | Should -Not -Match 'PostGraphqlAsync'
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'partial-class-codegen' {
    It 'declares the generated class as partial' {
        $out = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $out | Out-Null
            $code = Get-Content (Join-Path $out 'src/ExampleEx/ExampleExClient.Generated.cs') -Raw
            $code | Should -Match 'partial\s+class\s+ExampleExClient'
            # And the hand-written Client.cs (template substituted) also declares partial
            $client = Get-Content (Join-Path $out 'src/ExampleEx/Client.cs') -Raw
            $client | Should -Match 'partial\s+class\s+ExampleExClient'
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'mcp-descriptions' {
    It 'emits [Description] attributes on each generated method' {
        $out = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $out | Out-Null
            $code = Get-Content (Join-Path $out 'src/ExampleEx/ExampleExClient.Generated.cs') -Raw
            $code | Should -Match '\[Description\("[^"]+"\)\]'
            # mentions the path noun
            $code | Should -Match '\[Description\("[^"]*[Uu]sers[^"]*"\)\]'
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'auto-generated header' {
    It 'has do-not-edit banner + source HAR sha-256' {
        $out = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $out | Out-Null
            $code = Get-Content (Join-Path $out 'src/ExampleEx/ExampleExClient.Generated.cs') -Raw
            $code | Should -Match '<auto-generated'
            $code | Should -Match 'do not edit'
            $code | Should -Match 'source HAR sha-256:\s*[0-9a-f]{64}'
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'test-fixtures emitted' {
    It 'writes one JSON per detected endpoint into tests/fixtures/' {
        $out = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $out | Out-Null
            $fixDir = Join-Path $out 'tests/fixtures'
            Test-Path $fixDir | Should -BeTrue
            $files = Get-ChildItem $fixDir -Filter '*.json'
            $files.Count | Should -BeGreaterOrEqual 3
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'readme-recipes' {
    It 'emits a README.md with >=3 fenced csharp code blocks' {
        $out = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $out | Out-Null
            $readme = Get-Content (Join-Path $out 'README.md') -Raw
            $fences = [regex]::Matches($readme, '(?ms)^```csharp\b.*?^```')
            $fences.Count | Should -BeGreaterOrEqual 3
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'polite-crawl note' {
    It 'README contains rate-limit guidance' {
        $out = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $out | Out-Null
            $readme = Get-Content (Join-Path $out 'README.md') -Raw
            $readme | Should -Match '(?i)1\s*req(uest)?/sec|rate.?limit|robots\.txt'
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'anti-bot challenge warning (issue #66)' {
    BeforeAll {
        function New-AkamaiHarFixture {
            param(
                [Parameter(Mandatory)][string] $Path,
                [switch] $IncludeAkamai
            )
            $reqHeaders = @(
                @{ name = 'Accept'; value = 'application/json' }
            )
            $respHeaders = @(
                @{ name = 'Content-Type'; value = 'application/json' }
            )
            if ($IncludeAkamai) {
                $reqHeaders += @{ name = 'Cookie'; value = '_abck=opaque; session_id=abc' }
                $respHeaders += @{ name = 'Set-Cookie'; value = 'bm_sz=opaque; Path=/; HttpOnly' }
            } else {
                $reqHeaders += @{ name = 'Cookie'; value = 'session_id=abc' }
                $respHeaders += @{ name = 'Set-Cookie'; value = 'session_id=abc; Path=/; HttpOnly' }
            }
            $har = @{
                log = @{
                    entries = @(
                        @{
                            startedDateTime = '2026-01-01T00:00:00Z'
                            request  = @{
                                method = 'GET'
                                url    = 'https://api.example.com/v1/widgets'
                                httpVersion = 'HTTP/1.1'
                                cookies = @()
                                headers = $reqHeaders
                                queryString = @()
                                headersSize = -1
                                bodySize = 0
                            }
                            response = @{
                                status      = 200
                                statusText  = 'OK'
                                httpVersion = 'HTTP/1.1'
                                cookies     = @()
                                headers     = $respHeaders
                                content     = @{ size = 2; mimeType = 'application/json'; text = '{}' }
                                redirectURL = ''
                                headersSize = -1
                                bodySize    = 2
                            }
                            cache    = @{}
                            timings  = @{ send = 0; wait = 1; receive = 0 }
                            time     = 1
                        }
                    )
                }
            }
            Set-Content -Encoding utf8 -LiteralPath $Path -Value ($har | ConvertTo-Json -Depth 12)
        }
    }

    Context 'when the capture shows Akamai bot management' {
        # The section tells the truth and stops there: the site may refuse this
        # client, a refusal is the client's blocked exception, and the remedy is
        # a person. It never advises a way past the refusal -- no warm-up request
        # to collect the bot manager's cookies, no sending the call again
        # (SKILL.md, "Pacing, throttling, and where the line is").
        BeforeAll {
            $script:BotOut = New-OutDir
            $script:BotHar = Join-Path ([IO.Path]::GetTempPath()) ("akamai-" + [guid]::NewGuid() + ".har")
            New-AkamaiHarFixture -Path $script:BotHar -IncludeAkamai
            Invoke-Gen -Har @($script:BotHar) -Out $script:BotOut | Out-Null
            $readme = Get-Content (Join-Path $script:BotOut 'README.md') -Raw
            $script:BotSection = [regex]::Match($readme, '(?ms)^## Anti-bot challenge warning[ \t]*$.*?(?=^## |\z)').Value
        }
        AfterAll {
            if (Test-Path $script:BotOut) { Remove-Item -Recurse -Force $script:BotOut }
            if (Test-Path $script:BotHar) { Remove-Item -Force $script:BotHar }
        }

        It 'README includes the warning section, naming the cookies it found' {
            $script:BotSection | Should -Not -BeNullOrEmpty
            $script:BotSection | Should -Match '_abck'
            $script:BotSection | Should -Match 'bm_sz'
        }
        It 'says a refused call fails with the client''s blocked exception' {
            $script:BotSection | Should -Match 'ExampleExBlockedException'
        }
        It 'gives the remedy as a fresh session from a real, signed-in browser, or a person' {
            $script:BotSection | Should -Match '(?i)fresh session from a real, signed-in browser'
            $script:BotSection | Should -Match '(?i)\bor have a person\b'
        }
        It 'advises no warm-up request and no retry' {
            $script:BotSection | Should -Not -BeNullOrEmpty -Because 'an empty section would pass this vacuously'
            $script:BotSection | Should -Not -Match '(?i)warm[- ]?up|retr(y|ies|ied)|landing page|\bseed'
        }
    }

    It 'README does NOT include the warning section when no Akamai cookies are present' {
        $out = New-OutDir
        $har = Join-Path ([IO.Path]::GetTempPath()) ("akamai-" + [guid]::NewGuid() + ".har")
        try {
            New-AkamaiHarFixture -Path $har
            Invoke-Gen -Har @($har) -Out $out | Out-Null
            $readme = Get-Content (Join-Path $out 'README.md') -Raw
            $readme | Should -Not -Match '(?i)Anti-bot challenge warning'
        } finally {
            if (Test-Path $out) { Remove-Item -Recurse -Force $out }
            if (Test-Path $har) { Remove-Item -Force $har }
        }
    }
}

Describe 'nuget-metadata' {
    It '.csproj contains required Package* elements' {
        $out = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $out | Out-Null
            $csproj = Get-Content (Join-Path $out 'src/ExampleEx/ExampleEx.csproj') -Raw
            $csproj | Should -Match '<PackageId>ExampleEx</PackageId>'
            $csproj | Should -Match '<Description>Example wrapper</Description>'
            $csproj | Should -Match '<Authors>IntelliTect</Authors>'
            $csproj | Should -Match '<RepositoryUrl>https://github\.com/example/example</RepositoryUrl>'
            $csproj | Should -Match '<PackageTags>example;api;wrapper</PackageTags>'
            $csproj | Should -Match '<TargetFramework>net10\.0</TargetFramework>'
        } finally { Remove-Item -Recurse -Force $out }
    }
}

Describe 'MCP SDK version (issue #17)' {
    # The generated server and the host that Multiplex runs are on one SDK
    # version line. The host's csproj is the source of truth: read from it, not
    # restated here, so raising the host's version fails this until the
    # generator follows.
    BeforeAll {
        $script:SdkOut = New-OutDir
        Invoke-Gen -Har @($script:RestHar) -Out $script:SdkOut | Out-Null
        $script:SdkCsproj = Get-Content (Join-Path $script:SdkOut 'src/ExampleEx/ExampleEx.csproj') -Raw
        $hostCsproj = Join-Path $script:RepoRoot '..' 'src/Multiplex.Web/Multiplex.Web.csproj'
        $script:HostCsprojFound = Test-Path -LiteralPath $hostCsproj
        $script:HostMcpVersion = if ($script:HostCsprojFound) {
            [regex]::Match((Get-Content -LiteralPath $hostCsproj -Raw),
                'PackageReference\s+Include="ModelContextProtocol\.AspNetCore"\s+Version="([^"]+)"').Groups[1].Value
        }
    }
    AfterAll {
        if (Test-Path $script:SdkOut) { Remove-Item -Recurse -Force $script:SdkOut }
    }
    It 'the host declares an MCP SDK version to match' {
        $script:HostCsprojFound | Should -BeTrue -Because 'the host csproj is where the version line is set'
        $script:HostMcpVersion | Should -Match '^\d+\.\d+\.\d+' -Because 'a version that could not be read would match anything'
    }
    It 'the generated csproj pins ModelContextProtocol to the host''s version' {
        $generated = [regex]::Match($script:SdkCsproj,
            'PackageReference\s+Include="ModelContextProtocol"\s+Version="([^"]+)"').Groups[1].Value
        $generated | Should -Be $script:HostMcpVersion
    }
    It 'references the package a stdio server needs, not the HTTP transport' {
        $script:SdkCsproj | Should -Match 'Include="ModelContextProtocol"\s'
        $script:SdkCsproj | Should -Not -Match 'ModelContextProtocol\.AspNetCore'
    }
}

Describe 'top-level solution file (issue #65)' {
    BeforeAll {
        $script:SlnOut = New-OutDir
        Invoke-Gen -Har @($script:RestHar) -Out $script:SlnOut | Out-Null
        $script:SlnxPath = Join-Path $script:SlnOut 'ExampleEx.slnx'
    }
    AfterAll {
        if (Test-Path $script:SlnOut) { Remove-Item -Recurse -Force $script:SlnOut }
    }
    It 'emits ExampleEx.slnx at the wrapper root' {
        Test-Path -LiteralPath $script:SlnxPath | Should -BeTrue
    }
    It 'references the client csproj' {
        $body = Get-Content -LiteralPath $script:SlnxPath -Raw
        $body | Should -Match 'src/ExampleEx/ExampleEx\.csproj'
    }
    It 'references the tests csproj' {
        $body = Get-Content -LiteralPath $script:SlnxPath -Raw
        $body | Should -Match 'tests/ExampleEx\.Tests/ExampleEx\.Tests\.csproj'
    }
    It 'uses POSIX-style forward slashes for cross-platform determinism' {
        $body = Get-Content -LiteralPath $script:SlnxPath -Raw
        $body | Should -Not -Match '\\'
    }
    It 'is wrapped in a <Solution> root element (slnx schema)' {
        $body = Get-Content -LiteralPath $script:SlnxPath -Raw
        $body | Should -Match '<Solution>'
        $body | Should -Match '</Solution>'
    }
}

Describe 'storage-state-flag (capture-cdp --validate-only)' {
    It 'exits 0 when storage-state file exists' {
        $tmpState = Join-Path ([IO.Path]::GetTempPath()) ("ss-" + [guid]::NewGuid() + ".json")
        Set-Content -LiteralPath $tmpState -Value '{"cookies":[],"origins":[]}' -Encoding utf8
        try {
            & node $script:CaptureJs --validate-only --storage-state $tmpState --url https://x --out x.har 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
        } finally { Remove-Item -LiteralPath $tmpState -ErrorAction SilentlyContinue }
    }
    It 'exits non-zero when storage-state file is missing' {
        $missing = Join-Path ([IO.Path]::GetTempPath()) ("ss-missing-" + [guid]::NewGuid() + ".json")
        & node $script:CaptureJs --validate-only --storage-state $missing --url https://x --out x.har 2>&1 | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }
}

Describe 'determinism' {
    It 'produces byte-identical output on repeat runs' {
        $a = New-OutDir; $b = New-OutDir
        try {
            Invoke-Gen -Har @($script:RestHar) -Out $a | Out-Null
            Invoke-Gen -Har @($script:RestHar) -Out $b | Out-Null
            $filesA = Get-ChildItem $a -Recurse -File | Sort-Object FullName
            $filesB = Get-ChildItem $b -Recurse -File | Sort-Object FullName
            $filesA.Count | Should -Be $filesB.Count
            for ($i = 0; $i -lt $filesA.Count; $i++) {
                $relA = $filesA[$i].FullName.Substring($a.Length)
                $relB = $filesB[$i].FullName.Substring($b.Length)
                $relA | Should -Be $relB
                $hashA = (Get-FileHash -Algorithm SHA256 $filesA[$i].FullName).Hash
                $hashB = (Get-FileHash -Algorithm SHA256 $filesB[$i].FullName).Hash
                $hashA | Should -Be $hashB
            }
        } finally {
            Remove-Item -Recurse -Force $a
            Remove-Item -Recurse -Force $b
        }
    }
}

Describe 'verb-passthrough (issue #100)' {
    BeforeAll {
        $script:PostHar = Join-Path $script:RepoRoot 'tests/fixtures/har/rest-post-and-get.har'
        $script:PostOut = New-OutDir
        $script:PostRes = Invoke-Gen -Har @($script:PostHar) -Out $script:PostOut
    }
    AfterAll {
        if (Test-Path $script:PostOut) { Remove-Item -Recurse -Force $script:PostOut }
    }

    It 'codegen exits 0 on the POST/GET fixture' {
        $script:PostRes.ExitCode | Should -Be 0 -Because $script:PostRes.Output
    }

    It 'generated POST endpoint passes HttpMethod.Post to SendRawAsync' {
        $code = Get-Content (Join-Path $script:PostOut 'src/ExampleEx/ExampleExClient.Generated.cs') -Raw
        $code | Should -Match 'CreateWidgetsCreateAsync'
        # The generated POST wrapper must thread the verb through to SendRawAsync.
        $code | Should -Match 'CreateWidgetsCreateAsync[\s\S]*?SendRawAsync\([^)]*HttpMethod\.Post'
    }

    It 'generated POST endpoint accepts an optional body parameter' {
        $code = Get-Content (Join-Path $script:PostOut 'src/ExampleEx/ExampleExClient.Generated.cs') -Raw
        $code | Should -Match 'CreateWidgetsCreateAsync\(\s*string\?\s+body\s*=\s*null'
    }

    It 'generated GET endpoint passes HttpMethod.Get to SendRawAsync' {
        $code = Get-Content (Join-Path $script:PostOut 'src/ExampleEx/ExampleExClient.Generated.cs') -Raw
        $code | Should -Match 'GetWidgetsListAsync[\s\S]*?SendRawAsync\([^)]*HttpMethod\.Get'
    }
}

Describe 'Client.cs.tmpl SendRawAsync verb support (issue #100)' {
    BeforeAll {
        $tmpl = Join-Path $script:RepoRoot 'skills/web-api-discovery/csharp/Client.cs.tmpl'
        $script:ClientTmpl = Get-Content -Raw $tmpl
    }

    It 'SendRawAsync signature includes an HttpMethod parameter' {
        $script:ClientTmpl | Should -Match 'SendRawAsync\([\s\S]*?HttpMethod\??\s+method'
    }

    It 'SendRawAsync signature accepts an optional JSON body' {
        $script:ClientTmpl | Should -Match 'SendRawAsync\([\s\S]*?string\?\s+jsonBody'
    }

    It 'HttpRequestMessage uses the supplied method, not a hard-coded GET' {
        $script:ClientTmpl | Should -Not -Match 'new HttpRequestMessage\(HttpMethod\.Get,'
        $script:ClientTmpl | Should -Match 'new HttpRequestMessage\(method,'
    }
}
