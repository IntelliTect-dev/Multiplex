#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# A generated client, and its MCP tools, take the query parameters the capture
# showed (issue #34).
#
# THE FAILURE THIS EXISTS TO STOP: the generator emitted every REST call with
# `query: null`, so 17 captured calls to GET /suggestions?prefix=<typed text>
# became a method, and a tool, with no parameters at all. A "search this site"
# tool that cannot be given a search term asks the site for the bare path.
#
# So nothing here decides by reading generated source. It generates a wrapper
# from a fixture, builds it, runs the wrapper's own tests, STARTS the server
# over stdio, asks it for its tools, makes real tool calls against a stand-in
# site on loopback, and reads the request lines that site received.
#
# The expectations are literals, written against tests/fixtures/har/rest-query.har.
# They do not come from the generator, so a generator that changes its mind is
# caught, not agreed with. No request leaves this machine: the wrapper's base
# URL is the stand-in's loopback address.

BeforeDiscovery {
    $script:NoDotnet = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)

    # tests/fixtures/har/rest-query.har, GET /api/search/suggestions, five calls.
    # What tools/list must say about each query parameter. Default = $null means
    # "no default": the parameter is left out unless the caller supplies it.
    $script:SuggestionParameters = @(
        @{ Property = 'limit';          Type = 'string'; Default = '11';      Says = 'All 5 captured calls sent "11"' }
        @{ Property = 'prefix';         Type = 'string'; Default = $null;     Says = 'Examples from the capture, not the only values: "w", "wi", "wire cutter"' }
        @{ Property = 'suggestionType'; Type = 'array';  Default = $null;     Says = 'sent once per value. All 5 captured calls sent "KEYWORD", "WIDGET"' }
        @{ Property = 'pageType';       Type = 'string'; Default = 'Gateway'; Says = 'The "page-type" query parameter' }
        @{ Property = 'page';           Type = 'string'; Default = $null;     Says = 'It varied across the 5 captured calls' }
        @{ Property = 'wc';             Type = 'string'; Default = '';        Says = 'All 5 captured calls sent ""' }
        @{ Property = 'sessionId';      Type = 'string'; Default = $null;     Says = 'The captured value is not baked in' }
        @{ Property = 'underscore';     Type = 'string'; Default = $null;     Says = 'per-request timestamp or nonce' }
        @{ Property = 'lastPrefix';     Type = 'string'; Default = $null;     Says = '(4 of them sent it)' }
    )
}

BeforeAll {
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:GenJs    = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts/codegen/generate-wrapper.js'
    $script:QueryHar = Join-Path $script:RepoRoot 'tests/fixtures/har/rest-query.har'
    $script:HostileHar = Join-Path $script:RepoRoot 'tests/fixtures/har/rest-query-hostile.har'
    $script:StandIn  = Join-Path $PSScriptRoot 'stand-in-site.js'

    function Start-StandInSite {
        <#
            Starts tests/stand-in-site.js and returns once it is listening.
            Its stdin is a pipe this process holds, so if this process dies the
            stand-in sees end-of-input and exits: nothing is left listening.
        #>
        param([Parameter(Mandatory)][string]$Body)

        # The stand-in takes no arguments: it serves response.json from, and
        # logs requests.log to, its working directory. So it gets its own.
        $dir = Join-Path ([IO.Path]::GetTempPath()) ("standin-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force $dir | Out-Null
        $log = Join-Path $dir 'requests.log'
        Set-Content -LiteralPath (Join-Path $dir 'response.json') -Value $Body -NoNewline -Encoding utf8NoBOM

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new('node')
        $startInfo.ArgumentList.Add($script:StandIn)
        $startInfo.WorkingDirectory = $dir
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $process = [System.Diagnostics.Process]::Start($startInfo)

        $lineTask = $process.StandardOutput.ReadLineAsync()
        if (-not $lineTask.Wait(30000) -or "$($lineTask.Result)" -notmatch '^LISTENING (\d+)$') {
            if (-not $process.HasExited) { $process.Kill($true) }
            throw "The stand-in site did not start listening. It wrote: $($lineTask.Result)"
        }
        [pscustomobject]@{
            Process = $process
            Dir     = $dir
            Log     = $log
            BaseUrl = "http://127.0.0.1:$($Matches[1])"
        }
    }

    function Stop-StandInSite {
        param($Site)
        if (-not $Site) { return }
        # This process, by the handle it was started with -- never by name.
        try { $Site.Process.StandardInput.Close() } catch { Write-Verbose "stdin already closed: $($_.Exception.Message)" }
        if (-not $Site.Process.WaitForExit(5000)) {
            try { $Site.Process.Kill($true) } catch { Write-Verbose "Could not stop the stand-in: $($_.Exception.Message)" }
        }
        $Site.Process.Dispose()
        Remove-Item -Recurse -Force $Site.Dir -ErrorAction SilentlyContinue
    }

    function Get-RequestLine {
        # The request lines the stand-in has received so far, oldest first.
        param([Parameter(Mandatory)]$Site)
        if (-not (Test-Path -LiteralPath $Site.Log)) { return @() }
        @(Get-Content -LiteralPath $Site.Log | Where-Object { $_ })
    }

    function New-GeneratedWrapper {
        <#
            Generates a wrapper and builds its server. The project name is new on
            every run, so its session store -- which is keyed by project name --
            cannot already hold anything (see generated-mcp-server.Tests.ps1).
        #>
        param([Parameter(Mandatory)][string]$Har, [Parameter(Mandatory)][string]$Prefix, [Parameter(Mandatory)][string]$BaseUrl)

        $name = $Prefix + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $out = Join-Path ([IO.Path]::GetTempPath()) ("querywrapper-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force $out | Out-Null
        $generateOutput = & node $script:GenJs `
            --har $Har `
            --out $out `
            --project-name $name `
            --namespace ($Prefix + 'Site') `
            --base-url $BaseUrl `
            --auth-model cookie 2>&1 | Out-String
        $generateExit = $LASTEXITCODE

        $project = Join-Path $out "src/$name/$name.csproj"
        $buildOutput = ''
        $buildExit = $null
        if ($generateExit -eq 0) {
            $buildOutput = & dotnet build $project -c Debug --nologo -v:q 2>&1 | Out-String
            $buildExit = $LASTEXITCODE
        }

        [pscustomobject]@{
            Name           = $name
            Out            = $out
            Project        = $project
            TestProject    = Join-Path $out "tests/$name.Tests/$name.Tests.csproj"
            Server         = Join-Path $out "src/$name/bin/Debug/net10.0/$name.dll"
            Smoke          = Join-Path $out "tests/$name.Tests/pester/Mcp.Tests.ps1"
            Stdio          = Join-Path $out "tests/$name.Tests/pester/McpStdio.ps1"
            ClientSource   = Join-Path $out "src/$name/${name}Client.Generated.cs"
            ToolsSource    = Join-Path $out "src/$name/${name}Tools.Generated.cs"
            Readme         = Join-Path $out 'README.md'
            GenerateExit   = $generateExit
            GenerateOutput = $generateOutput
            BuildExit      = $buildExit
            BuildOutput    = $buildOutput
        }
    }

    function Invoke-EmittedUnitTest {
        # The wrapper's own xUnit project, as a consumer runs it.
        param([Parameter(Mandatory)]$Wrapper, [string]$Filter)

        $arguments = @('test', $Wrapper.TestProject, '--nologo', '-v:q')
        if ($Filter) { $arguments += @('--filter', $Filter) }
        $output = & dotnet @arguments 2>&1 | Out-String
        $exit = $LASTEXITCODE
        $counts = [regex]::Match($output, 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')
        [pscustomobject]@{
            ExitCode = $exit
            Output   = $output
            Failed   = if ($counts.Success) { [int]$counts.Groups[1].Value } else { $null }
            Passed   = if ($counts.Success) { [int]$counts.Groups[2].Value } else { $null }
            Total    = if ($counts.Success) { [int]$counts.Groups[4].Value } else { $null }
        }
    }

    function Invoke-ServerSession {
        # The wrapper's own stdio client, the one its smoke test uses.
        param([Parameter(Mandatory)]$Wrapper, [hashtable[]]$Request = @())

        . $Wrapper.Stdio
        Invoke-McpStdioSession -FilePath dotnet -ArgumentList @($Wrapper.Server) -Request $Request -TimeoutSeconds 120
    }

    function Invoke-EmittedSmoke {
        # Out of process: the emitted suite is the wrapper's, and must pass or
        # fail on its own.
        param([Parameter(Mandatory)]$Wrapper)

        $command = "`$r = Invoke-Pester -Path '$($Wrapper.Smoke)' -PassThru -Output None; " +
            "'{0},{1},{2}' -f `$r.PassedCount, `$r.FailedCount, `$r.TotalCount"
        $line = & pwsh -NoProfile -Command $command | Select-Object -Last 1
        $passed, $failed, $total = "$line".Trim() -split ','
        [pscustomobject]@{ Passed = [int]$passed; Failed = [int]$failed; Total = [int]$total }
    }

    function Save-ThrowawaySession {
        <#
            Saves a session for the throwaway project, where its server will
            look: a real tools/call needs one, and the server never signs in.

            This writes what the generated stores read -- DpapiSessionStore on
            Windows (%LOCALAPPDATA%\<Name>\session.dpapi, the credentials as
            JSON, protected for the current user) and UserSecretsSessionStore
            elsewhere (<user secrets>/<Name>/secrets.json). If either format
            changes, the call below answers "No ... session is saved" and this
            suite says so. The folder is named after a project that exists only
            for this run, holds a made-up cookie, and is removed afterwards.
        #>
        param([Parameter(Mandatory)][string]$Name)

        if ($IsWindows) {
            $dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $Name
            New-Item -ItemType Directory -Force $dir | Out-Null
            $json = '{"Cookie":"standin=1","CsrfToken":""}'
            $protected = [System.Security.Cryptography.ProtectedData]::Protect(
                [Text.Encoding]::UTF8.GetBytes($json), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
            [IO.File]::WriteAllBytes((Join-Path $dir 'session.dpapi'), $protected)
            return $dir
        }
        $root = if ($env:APPDATA) { Join-Path $env:APPDATA 'Microsoft/UserSecrets' }
                else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.microsoft/usersecrets' }
        $dir = Join-Path $root $Name
        New-Item -ItemType Directory -Force $dir | Out-Null
        @{ "${Name}:Cookie" = 'standin=1'; "${Name}:CsrfToken" = '' } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $dir 'secrets.json') -Encoding utf8NoBOM
        return $dir
    }

    function Get-SchemaProperty {
        param($Tool, [string]$Name)
        $properties = $Tool.inputSchema.PSObject.Properties['properties']
        if (-not $properties) { return $null }
        $property = $properties.Value.PSObject.Properties | Where-Object { $_.Name -ceq $Name }
        if ($property) { $property.Value } else { $null }
    }

    function Get-SchemaPropertyName {
        param($Tool)
        $properties = $Tool.inputSchema.PSObject.Properties['properties']
        if (-not $properties) { return @() }
        @($properties.Value.PSObject.Properties | ForEach-Object { $_.Name })
    }

    function Get-SchemaRequired {
        param($Tool)
        $required = $Tool.inputSchema.PSObject.Properties['required']
        if ($required) { @($required.Value) } else { @() }
    }
}

Describe 'the .NET SDK a generated server needs' {
    # Everything below builds and starts a real server, so without the SDK it is
    # skipped -- fair on a machine that has none, and a false green in CI.
    It 'is installed whenever CI is set' {
        if ([string]::IsNullOrWhiteSpace($env:CI) -or $env:CI -eq 'false') {
            Set-ItResult -Skipped -Because 'not a CI run'
            return
        }
        Get-Command dotnet -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
    }
}

Describe 'a wrapper generated from a capture with query strings' -Skip:$script:NoDotnet {
    BeforeAll {
        # What the stand-in answers with: the captured response of the endpoint
        # the calls below are made to.
        $script:Site = Start-StandInSite -Body '{"suggestions":[{"value":"wire cutter","type":"KEYWORD"}],"prefix":"wire cutter"}'
        # The base URL is baked in at generation: the stand-in is listening first.
        $script:Query = New-GeneratedWrapper -Har $script:QueryHar -Prefix 'Qry' -BaseUrl $script:Site.BaseUrl

        $script:UnitTests = $null
        $script:QueryFacts = $null
        $script:Session = $null
        $script:SessionError = $null
        $script:SessionStore = $null
        if ($script:Query.BuildExit -eq 0) {
            $script:UnitTests = Invoke-EmittedUnitTest -Wrapper $script:Query
            $script:QueryFacts = Invoke-EmittedUnitTest -Wrapper $script:Query -Filter 'FullyQualifiedName~QueryStringTests'

            $script:SessionStore = Save-ThrowawaySession -Name $script:Query.Name
            try {
                $script:Session = Invoke-ServerSession -Wrapper $script:Query -Request @(
                    @{ method = 'tools/list' }
                    # 1. A search term and a page: the varying parameters.
                    @{ method = 'tools/call'; params = @{ name = 'get_search_suggestions'; arguments = @{ prefix = 'wire cutter & co'; page = '2' } } }
                    # 2. Nothing supplied: only what every captured call sent.
                    @{ method = 'tools/call'; params = @{ name = 'get_search_suggestions'; arguments = @{} } }
                    # 3. A constant overridden, one left out with null, a repeated
                    #    one replaced, and a value for one that is never baked in.
                    @{ method = 'tools/call'; params = @{ name = 'get_search_suggestions'; arguments = @{
                        limit = '3'; pageType = $null; suggestionType = @('A', 'B c'); sessionId = 'fresh/1'; prefix = "caf$([char]0xE9)" } } }
                    # 4. An endpoint captured once, with nothing supplied.
                    @{ method = 'tools/call'; params = @{ name = 'get_catalog_config'; arguments = @{} } }
                    # 5. A path parameter beside query parameters whose names needed changing.
                    @{ method = 'tools/call'; params = @{ name = 'get_items_by_id_reviews'; arguments = @{
                        id = 42; idQuery = 'r-7'; class = 'any'; q0 = 'slow charger'; query = 'battery' } } }
                )
            }
            catch { $script:SessionError = $_.Exception.Message }
        }
        $script:Tools = if ($script:Session) { @($script:Session.Responses[0].tools) } else { @() }
        $script:Wire = Get-RequestLine -Site $script:Site
        $script:Suggestions = $script:Tools | Where-Object { $_.name -eq 'get_search_suggestions' }
    }
    AfterAll {
        Stop-StandInSite $script:Site
        if ($script:SessionStore -and (Test-Path -LiteralPath $script:SessionStore)) {
            Remove-Item -Recurse -Force -LiteralPath $script:SessionStore -ErrorAction SilentlyContinue
        }
        if ($script:Query -and (Test-Path $script:Query.Out)) {
            Remove-Item -Recurse -Force $script:Query.Out -ErrorAction SilentlyContinue
        }
    }

    Context 'generating and building it' {
        It 'the generator exits 0' {
            $script:Query.GenerateExit | Should -Be 0 -Because $script:Query.GenerateOutput
        }
        It 'the wrapper builds with 0 warnings and 0 errors' {
            $script:Query.BuildExit | Should -Be 0 -Because $script:Query.BuildOutput
            $script:Query.BuildOutput | Should -Match '0 Warning\(s\)'
            $script:Query.BuildOutput | Should -Match '0 Error\(s\)'
        }
    }

    Context 'the tests the wrapper carries' {
        It 'all pass' {
            $script:UnitTests.ExitCode | Should -Be 0 -Because $script:UnitTests.Output
            $script:UnitTests.Failed | Should -Be 0
        }
        It 'sixteen of them assert the exact query string on the wire: four for each endpoint that takes one' {
            $script:QueryFacts.ExitCode | Should -Be 0 -Because $script:QueryFacts.Output
            $script:QueryFacts.Total | Should -Be 16
            $script:QueryFacts.Passed | Should -Be 16
        }
    }

    Context 'tools/list' {
        It 'the server starts over stdio and answers' {
            $script:SessionError | Should -BeNullOrEmpty
            $script:Session | Should -Not -BeNullOrEmpty
        }
        It 'the search tool takes the query parameters of its endpoint, in the order the capture showed them' {
            (Get-SchemaPropertyName $script:Suggestions) -join ', ' |
                Should -BeExactly 'limit, prefix, suggestionType, pageType, page, wc, sessionId, underscore, lastPrefix'
        }
        It 'none of them is required' {
            Get-SchemaRequired $script:Suggestions | Should -BeNullOrEmpty
        }
        It '<Property> is a <Type>, described by what the capture showed' -ForEach $script:SuggestionParameters {
            $schema = Get-SchemaProperty $script:Suggestions $Property
            $schema | Should -Not -BeNullOrEmpty -Because "get_search_suggestions must take $Property"
            @($schema.type) | Should -Contain $Type
            $schema.description | Should -Match ([regex]::Escape($Says))
        }
        It '<Property> defaults to what every captured call sent, or to nothing' -ForEach $script:SuggestionParameters {
            $schema = Get-SchemaProperty $script:Suggestions $Property
            if ($null -eq $Default) {
                $schema.default | Should -BeNullOrEmpty -Because "$Property has no default: it is left out unless supplied"
                $schema.default | Should -Not -Be '' -Because 'an empty string would be sent as an empty value'
            }
            else {
                $schema.default | Should -BeExactly $Default
            }
        }
        It 'a repeated parameter is a list of strings' {
            $schema = Get-SchemaProperty $script:Suggestions 'suggestionType'
            @($schema.items.type) | Should -Contain 'string'
        }
        It 'a scrubbed value and a timestamp from the capture are in no description and no default' {
            $text = $script:Session.Responses[0] | ConvertTo-Json -Depth 32
            $text | Should -Not -Match 'redacted-3f9a1c0b7e2d4a68'
            $text | Should -Not -Match '17949869\d{5}'
            $text | Should -Not -Match '7c9e6679-7425-40de-944b-e07fc1f90ae7'
        }
        It 'a query name that is not a C# identifier, or that the method already uses, is still a tool parameter' {
            $reviews = $script:Tools | Where-Object { $_.name -eq 'get_items_by_id_reviews' }
            (Get-SchemaPropertyName $reviews) -join ', ' |
                Should -BeExactly 'id, idQuery, class, q0, filterName, clientQuery, pageSize, pageSize2, query, ctQuery'
            (Get-SchemaProperty $reviews 'id').type | Should -Be 'integer' -Because 'the path parameter keeps its name and its type'
            Get-SchemaRequired $reviews | Should -Be @('id') -Because 'only the path parameter is required'
            (Get-SchemaProperty $reviews 'idQuery').description | Should -Match ([regex]::Escape('The "id" query parameter'))
            (Get-SchemaProperty $reviews 'class').default | Should -BeExactly 'verified'
            (Get-SchemaProperty $reviews 'q0').description | Should -Match ([regex]::Escape('The "q[0]" query parameter'))
            (Get-SchemaProperty $reviews 'pageSize').description | Should -Match ([regex]::Escape('The "page_size" query parameter'))
            (Get-SchemaProperty $reviews 'pageSize2').description | Should -Match ([regex]::Escape('The "page-size" query parameter'))
        }
        It 'an endpoint captured once defaults every parameter to what it sent, except a scrubbed one and a timestamp' {
            $config = $script:Tools | Where-Object { $_.name -eq 'get_catalog_config' }
            (Get-SchemaPropertyName $config) -join ', ' | Should -BeExactly 'alias, lop, visitor, requestId, underscore'
            (Get-SchemaProperty $config 'alias').default | Should -BeExactly 'aps'
            (Get-SchemaProperty $config 'alias').description | Should -Match 'The one captured call sent "aps"'
            (Get-SchemaProperty $config 'lop').default | Should -BeExactly 'en_US'
            (Get-SchemaProperty $config 'visitor').default | Should -BeNullOrEmpty
            (Get-SchemaProperty $config 'visitor').description | Should -Match 'not baked in'
            (Get-SchemaProperty $config 'underscore').default | Should -BeNullOrEmpty
        }
        It 'a POST takes its body and its query parameters' {
            $search = $script:Tools | Where-Object { $_.name -eq 'create_items_search' }
            (Get-SchemaPropertyName $search) -join ', ' | Should -BeExactly 'body, dryRun, sort'
            (Get-SchemaProperty $search 'dryRun').default | Should -BeExactly 'true'
        }
        It 'an endpoint captured with no query takes nothing' {
            $me = $script:Tools | Where-Object { $_.name -eq 'get_me' }
            $me | Should -Not -BeNullOrEmpty
            Get-SchemaPropertyName $me | Should -BeNullOrEmpty
        }
        It 'no tool asks the model for plumbing' {
            $script:Tools.Count | Should -Be 5
            foreach ($tool in $script:Tools) {
                foreach ($argument in (Get-SchemaPropertyName $tool)) {
                    $argument | Should -Not -BeIn @('ct', 'session', 'client', 'wireQuery', 'cancellationToken')
                }
            }
        }
    }

    Context 'tools/call: what reaches the site' {
        It 'every call was answered, and none with an error' {
            $script:Session.Responses.Count | Should -Be 6
            foreach ($call in $script:Session.Responses[1..5]) {
                $text = (@($call.content) | ForEach-Object { $_.text }) -join "`n"
                $call.PSObject.Properties['isError'] | Where-Object { $_.Value } | Should -BeNullOrEmpty -Because $text
            }
            $script:Wire.Count | Should -Be 5 -Because 'each tool call is one request to the site'
        }
        It 'a search term and a page are sent, encoded, beside what every captured call sent' {
            $script:Wire[0] | Should -BeExactly ('GET /api/search/suggestions?limit=11&prefix=wire%20cutter%20%26%20co' +
                '&suggestion-type=KEYWORD&suggestion-type=WIDGET&page-type=Gateway&page=2&wc=')
        }
        It 'the answer the site gave comes back from the tool' {
            $text = (@($script:Session.Responses[1].content) | ForEach-Object { $_.text }) -join "`n"
            $text | Should -Match 'wire cutter'
        }
        It 'with nothing supplied, only the constants are sent: no search term, no session id, no timestamp' {
            $script:Wire[1] | Should -BeExactly 'GET /api/search/suggestions?limit=11&suggestion-type=KEYWORD&suggestion-type=WIDGET&page-type=Gateway&wc='
        }
        It 'a constant can be overridden or left out, a repeated one replaced, and a never-baked-in one supplied' {
            $script:Wire[2] | Should -BeExactly ('GET /api/search/suggestions?limit=3&prefix=caf%C3%A9' +
                '&suggestion-type=A&suggestion-type=B%20c&wc=&session-id=fresh%2F1')
        }
        It 'an endpoint captured once sends what it sent, without its scrubbed value or its timestamp' {
            $script:Wire[3] | Should -BeExactly 'GET /api/catalog/config?alias=aps&lop=en_US&request-id=K3QW9ZT5VB7XN2M4PD8R'
        }
        It 'renamed parameters keep their exact names on the wire, beside the path parameter' {
            $script:Wire[4] | Should -BeExactly ('GET /api/items/42/reviews?id=r-7&class=any&q%5B0%5D=slow%20charger' +
                '&filter.name=stars&client=web&page_size=20&page-size=20&query=battery&ct=1')
        }
    }

    Context 'the README' {
        It 'lists the query parameters and says which are never baked in' {
            $readme = Get-Content -LiteralPath $script:Query.Readme -Raw
            $readme | Should -Match '### Query parameters'
            $readme | Should -Match ([regex]::Escape('| `get_search_suggestions` | `sessionId` | `session-id` | 5 of 5 | **left out, never baked in**: the scrub replaced the captured value |'))
            $readme | Should -Match ([regex]::Escape('| `get_search_suggestions` | `underscore` | `_` | 5 of 5 | **left out, never baked in**: a per-request timestamp or nonce |'))
            $readme | Should -Match ([regex]::Escape('| `get_search_suggestions` | `limit` | `limit` | 5 of 5 | sends `"11"` (every captured call did) |'))
            $readme | Should -Not -Match 'redacted-3f9a1c0b7e2d4a68'
        }
    }

    Context 'the smoke test the wrapper carries' {
        It 'passes against the wrapper as generated' {
            $result = Invoke-EmittedSmoke -Wrapper $script:Query
            $result.Failed | Should -Be 0
            $result.Passed | Should -BeGreaterOrEqual 7 -Because 'a suite that ran nothing would also report no failures'
        }

        # FALSIFIERS. Each changes the generated wrapper the way a regression in
        # the generator would, requires the wrapper's own tests to notice, and
        # puts the source back. They run last all the same.
        It 'FALSIFIER: the smoke test fails when a tool loses a query parameter' {
            $source = Get-Content -LiteralPath $script:Query.ToolsSource -Raw
            $without = [regex]::Replace($source, '(?m)^\s*\[Description\("The \\"prefix\\" query parameter[^\r\n]*\r?\n', '')
            $without = $without.Replace('GetSearchSuggestionsAsync(limit, prefix, suggestionType', 'GetSearchSuggestionsAsync(limit, null, suggestionType')
            $without | Should -Not -Be $source -Because 'the falsifier must actually change the tool'
            $without | Should -Not -Match 'string\? prefix = null' -Because 'the parameter must really be gone'
            try {
                Set-Content -LiteralPath $script:Query.ToolsSource -Value $without -NoNewline
                (Invoke-EmittedSmoke -Wrapper $script:Query).Failed | Should -BeGreaterThan 0
            }
            finally {
                Set-Content -LiteralPath $script:Query.ToolsSource -Value $source -NoNewline
            }
        }
        It 'FALSIFIER: the unit tests fail when the client stops sending its query' {
            # What the generator emitted for every endpoint before issue #34.
            $source = Get-Content -LiteralPath $script:Query.ClientSource -Raw
            $reverted = $source.Replace('query: wireQuery, ct: ct)', 'query: null, ct: ct)')
            $reverted | Should -Not -Be $source -Because 'the falsifier must actually change the client'
            try {
                Set-Content -LiteralPath $script:Query.ClientSource -Value $reverted -NoNewline
                $result = Invoke-EmittedUnitTest -Wrapper $script:Query -Filter 'FullyQualifiedName~QueryStringTests'
                $result.ExitCode | Should -Not -Be 0
                $result.Failed | Should -Be 16 -Because "every query fact asserts a query string, and none is sent now. Output:`n$($result.Output)"
            }
            finally {
                Set-Content -LiteralPath $script:Query.ClientSource -Value $source -NoNewline
            }
        }
    }
}

Describe 'a capture whose query names and values are hostile to C# source' -Skip:$script:NoDotnet {
    # tests/fixtures/har/rest-query-hostile.har: one query string carrying, as
    # values, a quote, backslashes, CR LF, NUL, a tab, the three other characters
    # C# ends a line at (U+0085, U+2028, U+2029), braces, comment and XML
    # delimiters and an emoji; and, as names, C# keywords, names the generated
    # methods and tests already use, an empty name, names with nothing usable in
    # them and two 120-character names that differ in the 121st. One endpoint's
    # PATH is spelled the way .NET rewrites (%c3%a9 becomes %C3%A9, %7E becomes
    # ~): the query facts compare the query string, not the path.
    #
    # A capture is text a website wrote. Every one of these is written into a
    # string literal, an attribute, a `///` comment and a test, and none may
    # change what the compiler sees: it must build, and the wrapper's own query
    # facts must find every name and value on the wire exactly as captured.
    BeforeAll {
        $script:Hostile = New-GeneratedWrapper -Har $script:HostileHar -Prefix 'Odd' -BaseUrl 'https://api.example.com'
        $script:HostileTests = $null
        if ($script:Hostile.BuildExit -eq 0) {
            $script:HostileTests = Invoke-EmittedUnitTest -Wrapper $script:Hostile -Filter 'FullyQualifiedName~QueryStringTests'
        }
    }
    AfterAll {
        if ($script:Hostile -and (Test-Path $script:Hostile.Out)) {
            Remove-Item -Recurse -Force $script:Hostile.Out -ErrorAction SilentlyContinue
        }
    }

    It 'the generator exits 0' {
        $script:Hostile.GenerateExit | Should -Be 0 -Because $script:Hostile.GenerateOutput
    }
    It 'the wrapper builds with 0 warnings and 0 errors' {
        $script:Hostile.BuildExit | Should -Be 0 -Because $script:Hostile.BuildOutput
        $script:Hostile.BuildOutput | Should -Match '0 Warning\(s\)'
        $script:Hostile.BuildOutput | Should -Match '0 Error\(s\)'
    }
    It 'its query facts pass: every name and value reaches the wire as captured' {
        $script:HostileTests.ExitCode | Should -Be 0 -Because $script:HostileTests.Output
        $script:HostileTests.Total | Should -Be 12 -Because 'four facts for each of the three endpoints'
        $script:HostileTests.Passed | Should -Be 12
    }
    It 'no generated source line is ended by a character the capture supplied' {
        # CR, U+0085, U+2028 and U+2029 end a line in C#. The generator writes
        # LF only, so any of these in its output came from the capture.
        $lineEnds = [regex]::new("[`r$([char]0x85)$([char]0x2028)$([char]0x2029)]")
        $sources = @(Get-ChildItem -LiteralPath (Join-Path $script:Hostile.Out 'src') -Recurse -File -Filter '*.Generated.cs') +
            @(Get-ChildItem -LiteralPath (Split-Path $script:Hostile.TestProject -Parent) -File -Filter 'QueryStringTests.cs')
        $sources.Count | Should -Be 4 -Because 'the client, the models, the tools and the query facts'
        foreach ($file in $sources) {
            $lineEnds.IsMatch([IO.File]::ReadAllText($file.FullName)) | Should -BeFalse -Because "$($file.Name) must contain no line end but LF"
        }
    }
}
