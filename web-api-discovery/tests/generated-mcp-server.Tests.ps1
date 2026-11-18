#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# A generated MCP server lists its tools (issue #17).
#
# THE FAILURE THIS EXISTS TO STOP: the generator emitted an MCP entry point that
# called WithToolsFromAssembly() and no class marked [McpServerToolType], so a
# generated server started and listed NO tools -- and the wrapper's own smoke
# test, which read the source for [Description] attributes, passed anyway.
#
# So nothing here reads generated source to decide whether the server works. It
# generates a wrapper from a fixture, builds it, STARTS the server over stdio the
# way an MCP client does, and asserts on what the server answers.
#
# The expectations are literals, written against the fixture. They do not come
# from the generator, so a generator that changes its mind is caught, not
# agreed with.

BeforeDiscovery {
    $script:NoDotnet = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)

    # tests/fixtures/har/rest-crud.har, endpoint by endpoint: the tool each one
    # becomes and the hints it must declare.
    $script:CrudEndpoints = @(
        @{ Name = 'get_items';                  Request = 'GET /api/items';                ReadOnly = $true;  Destructive = $false; Idempotent = $true }
        @{ Name = 'get_items_by_id';            Request = 'GET /api/items/{id}';           ReadOnly = $true;  Destructive = $false; Idempotent = $true }
        # An additive POST: not read-only, not destructive, and a second call adds a second item.
        # What vouches for it is in the fixture: /api/items/{id} was captured too, so /api/items
        # is a collection. A POST nothing vouches for is destructive.
        @{ Name = 'create_items';               Request = 'POST /api/items';               ReadOnly = $false; Destructive = $false; Idempotent = $false }
        # A POST addressed to an existing item, and naming a removal, is not an additive call.
        @{ Name = 'create_items_by_id_archive'; Request = 'POST /api/items/{id}/archive';  ReadOnly = $false; Destructive = $true;  Idempotent = $false }
        @{ Name = 'update_items_by_id';         Request = 'PUT /api/items/{id}';           ReadOnly = $false; Destructive = $true;  Idempotent = $true }
        @{ Name = 'update_profile';             Request = 'PATCH /api/profile';            ReadOnly = $false; Destructive = $true;  Idempotent = $false }
        @{ Name = 'delete_items_by_id';         Request = 'DELETE /api/items/{id}';        ReadOnly = $false; Destructive = $true;  Idempotent = $true }
    )
}

BeforeAll {
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:GenJs    = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts/codegen/generate-wrapper.js'
    $script:CrudHar  = Join-Path $script:RepoRoot 'tests/fixtures/har/rest-crud.har'
    $script:GqlHar   = Join-Path $script:RepoRoot 'tests/fixtures/har/graphql.har'
    $script:BaseUrl  = 'https://api.example.com'

    function New-GeneratedWrapper {
        <#
            Generates a wrapper and builds its server.

            The project name is new on every run ON PURPOSE. The session store is
            keyed by project name (%LOCALAPPDATA%\<Name>\session.dpapi, or the
            user-secrets folder <Name>), so a name nobody has used cannot have a
            saved session: the server under test starts with none, on a
            developer's machine as surely as in CI, and nothing here has to touch
            a real store to make that so.
        #>
        param([Parameter(Mandatory)][string]$Har, [Parameter(Mandatory)][string]$Prefix)

        $name = $Prefix + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $out = Join-Path ([IO.Path]::GetTempPath()) ("mcpserver-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Force $out | Out-Null
        $generateOutput = & node $script:GenJs `
            --har $Har `
            --out $out `
            --project-name $name `
            --namespace ($Prefix + 'Site') `
            --base-url $script:BaseUrl `
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
            Server         = Join-Path $out "src/$name/bin/Debug/net10.0/$name.dll"
            Smoke          = Join-Path $out "tests/$name.Tests/pester/Mcp.Tests.ps1"
            Stdio          = Join-Path $out "tests/$name.Tests/pester/McpStdio.ps1"
            ToolsSource    = Join-Path $out "src/$name/${name}Tools.Generated.cs"
            GenerateExit   = $generateExit
            GenerateOutput = $generateOutput
            BuildExit      = $buildExit
            BuildOutput    = $buildOutput
        }
    }

    function Invoke-ServerSession {
        # The wrapper's own stdio client, the one its smoke test uses.
        param([Parameter(Mandatory)]$Wrapper, [hashtable[]]$Request = @())

        . $Wrapper.Stdio
        Invoke-McpStdioSession -FilePath dotnet -ArgumentList @($Wrapper.Server) -Request $Request -TimeoutSeconds 90
    }

    function Invoke-EmittedSmoke {
        # Out of process, as agent-e2e runs it: the emitted suite is the
        # wrapper's, and must pass or fail on its own.
        param([Parameter(Mandatory)]$Wrapper)

        $command = "`$r = Invoke-Pester -Path '$($Wrapper.Smoke)' -PassThru -Output None; " +
            "'{0},{1},{2}' -f `$r.PassedCount, `$r.FailedCount, `$r.TotalCount"
        $line = & pwsh -NoProfile -Command $command | Select-Object -Last 1
        $passed, $failed, $total = "$line".Trim() -split ','
        [pscustomobject]@{ Passed = [int]$passed; Failed = [int]$failed; Total = [int]$total }
    }

    function Remove-GeneratedWrapper {
        param($Wrapper)
        if ($Wrapper -and (Test-Path $Wrapper.Out)) {
            Remove-Item -Recurse -Force $Wrapper.Out -ErrorAction SilentlyContinue
        }
    }
}

Describe 'the .NET SDK a generated server needs' {
    # Everything below starts a real server, so without the SDK it is skipped --
    # fair on a machine that has none, and a false green in CI, where this is the
    # run anyone relies on. Same rule as GitleaksGuard.ps1.
    It 'is installed whenever CI is set' {
        if ([string]::IsNullOrWhiteSpace($env:CI) -or $env:CI -eq 'false') {
            Set-ItResult -Skipped -Because 'not a CI run'
            return
        }
        Get-Command dotnet -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
    }
}

Describe 'a server generated from a REST capture' -Skip:$script:NoDotnet {
    BeforeAll {
        $script:Crud = New-GeneratedWrapper -Har $script:CrudHar -Prefix 'Crud'
        $script:CrudSession = $null
        $script:CrudSessionError = $null
        if ($script:Crud.BuildExit -eq 0) {
            try {
                $script:CrudSession = Invoke-ServerSession -Wrapper $script:Crud -Request @(
                    @{ method = 'tools/list' }
                    # No session is saved for this project: see New-GeneratedWrapper.
                    @{ method = 'tools/call'; params = @{ name = 'get_items'; arguments = @{} } }
                    @{ method = 'tools/list' }
                )
            }
            catch { $script:CrudSessionError = $_.Exception.Message }
        }
        $script:CrudTools = if ($script:CrudSession) { @($script:CrudSession.Responses[0].tools) } else { @() }
    }
    AfterAll {
        Remove-GeneratedWrapper $script:Crud
    }

    Context 'generating and building it' {
        It 'the generator exits 0' {
            $script:Crud.GenerateExit | Should -Be 0 -Because $script:Crud.GenerateOutput
        }
        It 'the wrapper builds with 0 warnings and 0 errors' {
            $script:Crud.BuildExit | Should -Be 0 -Because $script:Crud.BuildOutput
            $script:Crud.BuildOutput | Should -Match '0 Warning\(s\)'
            $script:Crud.BuildOutput | Should -Match '0 Error\(s\)'
        }
    }

    Context 'tools/list' {
        It 'the server starts over stdio and answers' {
            $script:CrudSessionError | Should -BeNullOrEmpty
            $script:CrudSession | Should -Not -BeNullOrEmpty
        }
        It 'lists one tool for each endpoint of the capture, and nothing else' {
            (@($script:CrudTools | ForEach-Object { $_.name }) | Sort-Object) -join ', ' |
                Should -Be 'create_items, create_items_by_id_archive, delete_items_by_id, get_items, get_items_by_id, update_items_by_id, update_profile'
        }
        It '<Name> carries the hints of <Request>' -ForEach $script:CrudEndpoints {
            $tool = $script:CrudTools | Where-Object { $_.name -eq $Name }
            $tool | Should -Not -BeNullOrEmpty -Because "the server must list $Name"
            $tool.annotations.readOnlyHint    | Should -Be $ReadOnly
            $tool.annotations.destructiveHint | Should -Be $Destructive
            $tool.annotations.idempotentHint  | Should -Be $Idempotent
            # These tools reach a website: its content is not the server's to vouch for.
            $tool.annotations.openWorldHint   | Should -BeTrue
        }
        It '<Name> is described by the request it sends' -ForEach $script:CrudEndpoints {
            $tool = $script:CrudTools | Where-Object { $_.name -eq $Name }
            $tool.description | Should -Match ([regex]::Escape("Sends $Request to $($script:BaseUrl).")) -Because 'the model reads this to choose a tool'
            $tool.description | Should -Not -Match 'TODO|placeholder'
        }
        It 'a description names the fields the capture showed coming back' {
            $one = $script:CrudTools | Where-Object { $_.name -eq 'get_items_by_id' }
            $one.description | Should -Match 'Returns an object with: id, name, price, tags\.'
            $list = $script:CrudTools | Where-Object { $_.name -eq 'get_items' }
            $list.description | Should -Match 'Returns a list of objects with: id, name, price\.'
        }
        It 'a write says what calling it does to the site, and a read does not' {
            ($script:CrudTools | Where-Object { $_.name -eq 'create_items' }).description |
                Should -Match 'Adds to the data on the site; calling it again adds again\.'
            ($script:CrudTools | Where-Object { $_.name -eq 'delete_items_by_id' }).description |
                Should -Match 'Changes or removes data on the site; repeating the same call changes nothing more\.'
            ($script:CrudTools | Where-Object { $_.name -eq 'get_items' }).description |
                Should -Not -Match 'Adds to|hanges? or removes?'
        }
        It 'a POST nothing vouches for is described by its request, and is not said to create or add' {
            # The client names and summarizes every POST "Creates ...". Beside a
            # destructive hint that told the model two different things.
            $archive = ($script:CrudTools | Where-Object { $_.name -eq 'create_items_by_id_archive' }).description
            $archive | Should -Match '^Sends POST /api/items/\{id\}/archive to '
            $archive | Should -Match 'May change or remove data on the site\.$'
            $archive | Should -Not -Match 'Creates|Adds to'
        }
        It 'a path parameter is a required, described argument' {
            $schema = ($script:CrudTools | Where-Object { $_.name -eq 'get_items_by_id' }).inputSchema
            $schema.properties.id.type | Should -Be 'integer'
            $schema.properties.id.description | Should -Be 'The id in /api/items/{id}.'
            @($schema.required) | Should -Contain 'id'
        }
        It 'a request body is an optional, described argument, and only where the method takes one' {
            $create = ($script:CrudTools | Where-Object { $_.name -eq 'create_items' }).inputSchema
            $create.properties.body.description | Should -Match 'JSON request body'
            @($create.PSObject.Properties['required'] | ForEach-Object { $_.Value }) | Should -Not -Contain 'body'
            $delete = ($script:CrudTools | Where-Object { $_.name -eq 'delete_items_by_id' }).inputSchema
            $delete.properties.PSObject.Properties.Name | Should -Not -Contain 'body'
        }
        It 'no tool asks the model for anything but the endpoint''s own arguments' {
            $script:CrudTools.Count | Should -BeGreaterThan 0 -Because 'with no tools listed there would be nothing to check'
            foreach ($tool in $script:CrudTools) {
                # A tool that takes nothing has no properties at all.
                foreach ($argument in @($tool.inputSchema.properties.PSObject.Properties | ForEach-Object { $_.Name })) {
                    $argument | Should -BeIn @('id', 'body') -Because "$($tool.name) must not expose plumbing such as a session or a cancellation token"
                }
            }
        }
    }

    Context 'with no session saved' {
        It 'a tool call answers with an error that says so' {
            $call = $script:CrudSession.Responses[1]
            $call.isError | Should -BeTrue
            $text = (@($call.content) | ForEach-Object { $_.text }) -join "`n"
            $text | Should -Match ([regex]::Escape("No $($script:Crud.Name) session is saved on this machine"))
            $text | Should -Match ([regex]::Escape("$($script:Crud.Name)Authenticator")) -Because 'it says how to get one'
        }
        It 'the error tells the model nothing about where sessions are kept' {
            $text = (@($script:CrudSession.Responses[1].content) | ForEach-Object { $_.text }) -join "`n"
            $text | Should -Not -Match '(?i)session\.dpapi|secrets\.json|AppData|usersecrets|[A-Z]:\\|/home/|/Users/'
        }
        It 'the server goes on answering afterwards' {
            @($script:CrudSession.Responses[2].tools).Count | Should -Be $script:CrudTools.Count
        }
    }

    Context 'the protocol stream' {
        It 'stdout carries one JSON-RPC message per line and nothing else' {
            # One reply to initialize and one to each of the three requests.
            $script:CrudSession.StdoutLines.Count | Should -BeGreaterOrEqual 4
            foreach ($line in $script:CrudSession.StdoutLines) {
                $message = $null
                try { $message = $line | ConvertFrom-Json -ErrorAction Stop } catch { $message = $null }
                $message | Should -Not -BeNullOrEmpty -Because "stdout must carry only protocol messages, and this is not JSON: $line"
                $message.jsonrpc | Should -Be '2.0' -Because "this stdout line is not a JSON-RPC message: $line"
            }
        }
        It 'logging goes to stderr' {
            # The wrapper's own log line for the call above. The store's
            # location belongs here, for the operator, and nowhere on stdout.
            $script:CrudSession.Stderr | Should -Match ([regex]::Escape("No $($script:Crud.Name) session in")) -Because 'the missing session is logged for the operator'
            ($script:CrudSession.StdoutLines -join "`n") | Should -Not -Match ([regex]::Escape("No $($script:Crud.Name) session in"))
        }
        It 'the server exits cleanly when the client closes its input' {
            $script:CrudSession.ExitCode | Should -Be 0
        }
    }

    Context 'a pacing setting that cannot be parsed' {
        It 'stops the server at start, naming the setting' {
            $variable = $script:Crud.Name.ToUpperInvariant() + '_PACING_MAX_ATTEMPTS'
            $failure = $null
            Set-Item -Path "Env:$variable" -Value 'plenty'
            try {
                Invoke-ServerSession -Wrapper $script:Crud -Request @(@{ method = 'tools/list' }) | Out-Null
            }
            catch { $failure = $_.Exception.Message }
            finally { Remove-Item -Path "Env:$variable" -ErrorAction SilentlyContinue }

            $failure | Should -Not -BeNullOrEmpty -Because 'a server with a setting it cannot parse must not start'
            $failure | Should -Match ([regex]::Escape("$variable must be a whole number, not 'plenty'"))
        }
    }

    Context 'the smoke test the wrapper carries' {
        It 'passes against the wrapper as generated' {
            $result = Invoke-EmittedSmoke -Wrapper $script:Crud
            $result.Failed | Should -Be 0
            $result.Passed | Should -BeGreaterOrEqual 6 -Because 'a suite that ran nothing would also report no failures'
        }

        # FALSIFIERS. The emitted smoke test passed for years over a server with
        # no tools. These change the generated wrapper the way a regression in
        # the generator would, and require the smoke test to notice. They run
        # last: each leaves the wrapper broken.
        It 'FALSIFIER: fails when one tool''s hint is wrong' {
            $source = Get-Content -LiteralPath $script:Crud.ToolsSource -Raw
            $wrong = $source.Replace('Name = "delete_items_by_id", ReadOnly = false, Destructive = true',
                'Name = "delete_items_by_id", ReadOnly = true, Destructive = false')
            $wrong | Should -Not -Be $source -Because 'the falsifier must actually change the tool'
            Set-Content -LiteralPath $script:Crud.ToolsSource -Value $wrong -NoNewline

            (Invoke-EmittedSmoke -Wrapper $script:Crud).Failed | Should -BeGreaterThan 0
        }
        It 'FALSIFIER: fails when the generated tool class is gone' {
            Test-Path -LiteralPath $script:Crud.ToolsSource | Should -BeTrue -Because 'there must be a tool class to take away'
            Remove-Item -LiteralPath $script:Crud.ToolsSource -Force

            (Invoke-EmittedSmoke -Wrapper $script:Crud).Failed | Should -BeGreaterThan 0
        }
    }
}

Describe 'a server generated from a GraphQL capture' -Skip:$script:NoDotnet {
    # A GraphQL site has one endpoint and no REST methods, so without a tool for
    # it the server would be back to listing nothing.
    BeforeAll {
        $script:Gql = New-GeneratedWrapper -Har $script:GqlHar -Prefix 'Gql'
        $script:GqlTools = @()
        $script:GqlSessionError = $null
        if ($script:Gql.BuildExit -eq 0) {
            try {
                $script:GqlTools = @((Invoke-ServerSession -Wrapper $script:Gql -Request @(@{ method = 'tools/list' })).Responses[0].tools)
            }
            catch { $script:GqlSessionError = $_.Exception.Message }
        }
    }
    AfterAll {
        Remove-GeneratedWrapper $script:Gql
    }

    It 'builds with 0 warnings and 0 errors' {
        $script:Gql.GenerateExit | Should -Be 0 -Because $script:Gql.GenerateOutput
        $script:Gql.BuildExit | Should -Be 0 -Because $script:Gql.BuildOutput
        $script:Gql.BuildOutput | Should -Match '0 Warning\(s\)'
    }
    It 'lists one graphql tool' {
        $script:GqlSessionError | Should -BeNullOrEmpty
        @($script:GqlTools | ForEach-Object { $_.name }) -join ', ' | Should -Be 'graphql'
    }
    It 'does not call it read-only: the endpoint also takes mutations' {
        $tool = $script:GqlTools | Select-Object -First 1
        $tool.annotations.readOnlyHint    | Should -BeFalse
        $tool.annotations.destructiveHint | Should -BeTrue
        $tool.annotations.idempotentHint  | Should -BeFalse
        $tool.description | Should -Match 'mutation can change or remove data'
    }
    It 'takes the document, and optionally its variables' {
        $schema = ($script:GqlTools | Select-Object -First 1).inputSchema
        $schema.properties.query.type | Should -Be 'string'
        @($schema.required) | Should -Contain 'query'
        $schema.properties.PSObject.Properties.Name | Should -Contain 'variables'
        @($schema.required) | Should -Not -Contain 'variables'
    }
    It 'the smoke test the wrapper carries passes' {
        $result = Invoke-EmittedSmoke -Wrapper $script:Gql
        $result.Failed | Should -Be 0
        $result.Passed | Should -BeGreaterOrEqual 6
    }
}
