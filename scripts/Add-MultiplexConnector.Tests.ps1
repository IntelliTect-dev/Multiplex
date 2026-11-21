#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'Multiplex.Connector.psm1') -Force
    $script:Base = 'https://box.example.ts.net'

    # What a Multiplex at $Base with TripIt and LinkedIn answers. -Server replaces its sign-in
    # settings and -Resources the settings of a service, so a test can leave a field out.
    function Use-FakeMultiplex([string] $Issuer = $script:Base, [string[]] $Slugs = @('tripit', 'linkedin'),
        [hashtable] $Server, [hashtable] $Resources = @{}) {
        $global:MultiplexConnectorFakeServer = $Server ?? @{ issuer = $Issuer; scopes_supported = $Slugs }
        $global:MultiplexConnectorFakeResources = $Resources
        $global:MultiplexConnectorFakeSessions = [System.Collections.Generic.List[object]]::new()
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod {
            $global:MultiplexConnectorFakeSessions.Add($WebSession)
            switch -Regex ($Uri) {
                '/\.well-known/oauth-authorization-server$' { return [pscustomobject]$global:MultiplexConnectorFakeServer }
                '/\.well-known/oauth-protected-resource/(?<slug>[^/]+)/mcp$' {
                    $slug = $Matches.slug
                    if ($global:MultiplexConnectorFakeResources.ContainsKey($slug)) { return [pscustomobject]$global:MultiplexConnectorFakeResources[$slug] }
                    $name = @{ tripit = 'TripIt'; linkedin = 'LinkedIn' }[$slug]
                    return [pscustomobject]@{ resource = "$($global:MultiplexConnectorFakeServer['issuer'])/$slug/mcp"; resource_name = $name }
                }
                default { throw "Unexpected request: $Uri" }
            }
        }
    }
}
# Global: the fake answers calls made from inside the command's own script scope too.
AfterAll { Remove-Variable MultiplexConnectorFakeServer, MultiplexConnectorFakeResources, MultiplexConnectorFakeSessions -Scope Global -ErrorAction SilentlyContinue }

Describe 'the public address to give Claude and ChatGPT' {
    It 'uses the address given, without a trailing slash' {
        Resolve-MultiplexConnectorBaseUrl -BaseUrl "$Base/" | Should -Be $Base
    }

    It 'falls back to the public address of the Multiplex installed on this PC' {
        $config = Join-Path $TestDrive 'multiplex.config.json'
        @{ Port = 5190; PublicBaseUrl = 'https://pc.tailnet.ts.net'; Tunnel = 'Tailscale' } | ConvertTo-Json | Set-Content $config
        Resolve-MultiplexConnectorBaseUrl -ConfigPath $config | Should -Be 'https://pc.tailnet.ts.net'
    }

    It 'says what to pass when this PC has no Multiplex install' {
        { Resolve-MultiplexConnectorBaseUrl -ConfigPath (Join-Path $TestDrive 'absent.json') } |
            Should -Throw '*no public address*-BaseUrl*'
    }

    It 'says what to pass when the install here has no tunnel, so no public address' {
        $config = Join-Path $TestDrive 'local-only.json'
        @{ Port = 5190; PublicBaseUrl = $null; Tunnel = 'None' } | ConvertTo-Json | Set-Content $config
        { Resolve-MultiplexConnectorBaseUrl -ConfigPath $config } | Should -Throw '*no public address*-BaseUrl*'
    }

    It 'refuses <address>, which Claude and ChatGPT cannot reach from the internet' -ForEach @(
        @{ address = 'http://localhost:5190' }
        @{ address = 'https://localhost:5190' }
        @{ address = 'https://127.0.0.1' }
        @{ address = 'http://box.example.ts.net' }
        @{ address = 'box.example.ts.net' }
    ) {
        { Resolve-MultiplexConnectorBaseUrl -BaseUrl $address } | Should -Throw '*public https address*'
    }

    It 'refuses a service address in place of the address of Multiplex itself' {
        { Resolve-MultiplexConnectorBaseUrl -BaseUrl "$Base/tripit/mcp" } | Should -Throw '*address of Multiplex itself*'
    }

    It 'refuses an address with a user name and password in it, which would be printed and sent on' {
        { Resolve-MultiplexConnectorBaseUrl -BaseUrl 'https://me:secret@box.example.ts.net' } | Should -Throw '*user name*'
    }

    It 'says what to pass when the configuration on this PC cannot be read' {
        $config = Join-Path $TestDrive 'broken.json'
        Set-Content $config '{ not json'
        { Resolve-MultiplexConnectorBaseUrl -ConfigPath $config } | Should -Throw "*$config*could not be read*-BaseUrl*"
    }
}

Describe 'finding the services that can go on an account' {
    It 'lists every service that needs sign-in, with its name and address, as the server reports them' {
        Use-FakeMultiplex
        $services = @(Get-MultiplexAccountService -BaseUrl $Base)
        $services.Slug | Should -Be 'tripit', 'linkedin'
        $services.Name | Should -Be 'TripIt', 'LinkedIn'
        $services | ForEach-Object Address | Should -Be "$Base/tripit/mcp", "$Base/linkedin/mcp"   # an array's .Address is a method
    }

    It 'stops when the server''s own public address differs, because sign-in would fail' {
        Use-FakeMultiplex -Issuer 'https://other.example.ts.net'
        { Get-MultiplexAccountService -BaseUrl $Base } | Should -Throw '*https://other.example.ts.net*-BaseUrl https://other.example.ts.net*'
    }

    It 'asks over one connection, so a slow link pays for setting it up once' {
        Use-FakeMultiplex
        Get-MultiplexAccountService -BaseUrl $Base | Out-Null
        $global:MultiplexConnectorFakeSessions.Count | Should -Be 3
        $global:MultiplexConnectorFakeSessions[0] | Should -Not -BeNullOrEmpty
        @($global:MultiplexConnectorFakeSessions | Where-Object { -not [object]::ReferenceEquals($_, $global:MultiplexConnectorFakeSessions[0]) }).Count | Should -Be 0
    }

    It 'stops when a service''s own settings cannot be read, since the command must not guess' {
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod {
            if ($Uri -like '*oauth-authorization-server') { return [pscustomobject]@{ issuer = 'https://box.example.ts.net'; scopes_supported = @('tripit') } }
            throw 'The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.'
        }
        { Get-MultiplexAccountService -BaseUrl $Base } | Should -Throw '*settings of the tripit service*Timeout*'
    }

    It 'drops bidirectional and other formatting characters from a service''s name, which could disguise it' {
        Use-FakeMultiplex -Slugs tripit -Resources @{ tripit = @{ resource = "$Base/tripit/mcp"; resource_name = "Trip$([char]0x202E)tI" } }
        (Get-MultiplexAccountService -BaseUrl $Base).Name | Should -Not -Match '\p{Cf}'
    }

    It 'says the server could not be reached, and where' {
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod { throw 'No such host is known.' }
        { Get-MultiplexAccountService -BaseUrl $Base } | Should -Throw "*$Base*No such host is known.*"
    }

    It 'uses a service''s short name when the server gives it no name' {
        Use-FakeMultiplex -Slugs tripit -Resources @{ tripit = @{ resource = "$Base/tripit/mcp" } }
        $service = Get-MultiplexAccountService -BaseUrl $Base
        $service.Name | Should -Be 'tripit'
        $service.Address | Should -Be "$Base/tripit/mcp"
    }

    It 'drops control characters from a service''s name, which is printed and shown as a hint' {
        Use-FakeMultiplex -Slugs tripit -Resources @{ tripit = @{ resource = "$Base/tripit/mcp"; resource_name = "Trip`e[31mIt`r`n" } }
        $name = (Get-MultiplexAccountService -BaseUrl $Base).Name
        $name | Should -Not -Match '\p{Cc}'   # not -BeExactly: culture-aware comparison ignores control characters
        $name.Length | Should -Be 'Trip[31mIt'.Length
    }

    It 'stops when the server gives a service an address other than its own' {
        Use-FakeMultiplex -Slugs tripit -Resources @{ tripit = @{ resource = 'https://elsewhere.example/mcp'; resource_name = 'TripIt' } }
        { Get-MultiplexAccountService -BaseUrl $Base } | Should -Throw "*https://elsewhere.example/mcp*$Base/tripit/mcp*"
    }

    It 'stops on a service name like <slug>, which could reach another address or a shell, before asking about it' -ForEach @(
        @{ slug = 'a/../../x' }
        @{ slug = 'x; calc' }
        @{ slug = 'x y' }
        @{ slug = '-x' }
        @{ slug = "tripit`n" }
    ) {
        Use-FakeMultiplex -Slugs $slug
        { Get-MultiplexAccountService -BaseUrl $Base } | Should -Throw "*'$slug'*not a valid service name*"
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -Times 1 -Exactly   # the sign-in settings only
    }

    It 'stops when the server does not give its public address' {
        Use-FakeMultiplex -Server @{ scopes_supported = @('tripit') }
        { Get-MultiplexAccountService -BaseUrl $Base } | Should -Throw "*$Base*did not give its public address*"
    }

    It 'finds nothing when the server lists no services at all' {
        Use-FakeMultiplex -Server @{ issuer = $Base }
        @(Get-MultiplexAccountService -BaseUrl $Base).Count | Should -Be 0
    }
}

Describe 'Claude''s prefilled add-connector link' {
    It 'opens the add-custom-connector dialog with the name and address filled in' {
        Get-MultiplexClaudeConnectorLink -Name 'TripIt' -Address "$Base/tripit/mcp" |
            Should -BeExactly 'https://claude.ai/customize/connectors?modal=add-custom-connector&connectorName=TripIt&connectorUrl=https%3A%2F%2Fbox.example.ts.net%2Ftripit%2Fmcp'
    }

    It 'keeps a name with spaces or an ampersand in one piece' {
        $link = Get-MultiplexClaudeConnectorLink -Name 'Trips & Notes' -Address "$Base/notes/mcp"
        $query = [System.Web.HttpUtility]::ParseQueryString(([uri]$link).Query)
        $query['connectorName'] | Should -BeExactly 'Trips & Notes'
        $query['connectorUrl'] | Should -BeExactly "$Base/notes/mcp"
        $query['modal'] | Should -BeExactly 'add-custom-connector'
    }
}

Describe 'adding the services to your accounts' {
    BeforeEach {
        Use-FakeMultiplex
        Mock -ModuleName Multiplex.Connector Open-MultiplexConnectorPage { }
        Mock -ModuleName Multiplex.Connector Set-MultiplexConnectorClipboard { }
    }

    It 'opens Claude''s prefilled dialog for each service and ChatGPT''s add page once' {
        Add-MultiplexConnector -BaseUrl $Base 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 3 -Exactly
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 1 -Exactly -ParameterFilter { $Url -like '*connectorName=TripIt&*tripit%2Fmcp' }
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 1 -Exactly -ParameterFilter { $Url -like '*connectorName=LinkedIn&*linkedin%2Fmcp' }
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 1 -Exactly -ParameterFilter { $Url -eq 'https://chatgpt.com/plugins' }
    }

    It 'adds only the service asked for, by its name or its short name, and copies its address for ChatGPT' {
        Add-MultiplexConnector -BaseUrl $Base -Service 'linkedin' 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 1 -Exactly -ParameterFilter { $Url -like '*claude.ai*' }
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 0 -Exactly -ParameterFilter { $Url -like '*tripit*' }
        Should -Invoke -ModuleName Multiplex.Connector Set-MultiplexConnectorClipboard -Times 1 -Exactly -ParameterFilter { $Text -eq "$Base/linkedin/mcp" }

        Add-MultiplexConnector -BaseUrl $Base -Service 'TripIt' -Agent Claude 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 1 -Exactly -ParameterFilter { $Url -like '*connectorName=TripIt&*' }
    }

    It 'adds a service named twice, in any case, once' {
        Add-MultiplexConnector -BaseUrl $Base -Service 'TRIPIT', 'tripit', 'TripIt' -Agent Claude 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 1 -Exactly
    }

    It 'leaves the clipboard alone when there are several addresses to paste' {
        Add-MultiplexConnector -BaseUrl $Base 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Set-MultiplexConnectorClipboard -Times 0 -Exactly
    }

    It 'opens only Claude''s pages when only Claude is asked for' {
        Add-MultiplexConnector -BaseUrl $Base -Agent Claude 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 2 -Exactly
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 0 -Exactly -ParameterFilter { $Url -like '*chatgpt.com*' }
        Should -Invoke -ModuleName Multiplex.Connector Set-MultiplexConnectorClipboard -Times 0 -Exactly
    }

    It 'opens only ChatGPT''s page when only ChatGPT is asked for' {
        Add-MultiplexConnector -BaseUrl $Base -Agent ChatGPT -Service tripit 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 1 -Exactly -ParameterFilter { $Url -eq 'https://chatgpt.com/plugins' }
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 0 -Exactly -ParameterFilter { $Url -like '*claude.ai*' }
    }

    It 'stops before opening anything when a service is not one the server offers, and names the ones it does' {
        { Add-MultiplexConnector -BaseUrl $Base -Service 'time' 6>$null } | Should -Throw "*'time'*tripit (TripIt)*linkedin (LinkedIn)*"
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 0 -Exactly
    }

    It 'stops when the server has no service that can go on an account' {
        Use-FakeMultiplex -Slugs @()
        { Add-MultiplexConnector -BaseUrl $Base 6>$null } | Should -Throw '*no service*'
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 0 -Exactly
    }

    It 'with -WhatIf opens nothing and copies nothing, but still shows every link and address' {
        $said = Add-MultiplexConnector -BaseUrl $Base -Service tripit -WhatIf 6>&1 | Out-String
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 0 -Exactly
        Should -Invoke -ModuleName Multiplex.Connector Set-MultiplexConnectorClipboard -Times 0 -Exactly
        $said | Should -BeLike '*https://claude.ai/customize/connectors?modal=add-custom-connector&connectorName=TripIt&connectorUrl=https%3A%2F%2Fbox.example.ts.net%2Ftripit%2Fmcp*'
        $said | Should -BeLike "*$Base/tripit/mcp*"
        $said | Should -BeLike '*https://chatgpt.com/plugins*'
    }

    It 'says that sign-in asks for the Multiplex passcode' {
        $said = Add-MultiplexConnector -BaseUrl $Base -Service tripit 6>&1 | Out-String
        $said | Should -BeLike '*passcode*-ShowPasscode*'
    }

    It 'says, before the steps for <Agent>, that they reach the address from the internet: Funnel or an open incoming port' -ForEach @(
        @{ Agent = @('Claude'); Headings = @('Claude (') }
        @{ Agent = @('ChatGPT'); Headings = @('ChatGPT (') }
        @{ Agent = @('Claude', 'ChatGPT'); Headings = @('Claude (', 'ChatGPT (') }
    ) {
        $said = Add-MultiplexConnector -BaseUrl $Base -Service tripit -Agent $Agent -WhatIf 6>&1 | Out-String
        $note = $said.IndexOf('reachable from the internet')
        $note | Should -BeGreaterOrEqual 0
        foreach ($heading in $Headings) {
            $at = $said.IndexOf("`n$heading")   # the heading at the start of its line
            $at | Should -BeGreaterThan $note
        }
        $said | Should -BeLike '*their own servers*'
        $said | Should -BeLike '*Tailscale Funnel*'
        $said | Should -BeLike '*open incoming port*'
        $said | Should -BeLike '*only your tailnet*'
    }

    It 'says Claude refuses an address it already has, and that ChatGPT may add a second copy' {
        $claude = Add-MultiplexConnector -BaseUrl $Base -Service tripit -Agent Claude -WhatIf 6>&1 | Out-String
        $claude | Should -BeLike '*Claude already has*Cancel*'
        $claude | Should -Not -BeLike '*second copy*'

        $chatGpt = Add-MultiplexConnector -BaseUrl $Base -Service tripit -Agent ChatGPT -WhatIf 6>&1 | Out-String
        $chatGpt | Should -BeLike '*ChatGPT may add a second copy*'
        $chatGpt | Should -Not -BeLike '*Cancel*'
    }

    It 'shows the one-line commands for agents on a PC, and says they do not reach the phone' {
        $said = Add-MultiplexConnector -BaseUrl $Base -Service tripit -Agent Claude 6>&1 | Out-String
        $said | Should -BeLike "*codex mcp add tripit --url $Base/tripit/mcp*"
        $said | Should -BeLike "*copilot mcp add --transport http tripit $Base/tripit/mcp*"
        $said | Should -BeLike '*not*phone*'
    }
}

Describe 'the command' {
    BeforeEach {
        Use-FakeMultiplex
        Mock -ModuleName Multiplex.Connector Open-MultiplexConnectorPage { }
        Mock -ModuleName Multiplex.Connector Set-MultiplexConnectorClipboard { }
        $script:Command = Join-Path $PSScriptRoot 'Add-MultiplexConnector.ps1'
    }

    It 'opens the pages for what it is given' {
        & $Command -BaseUrl $Base -Service tripit -Agent Claude 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 1 -Exactly -ParameterFilter { $Url -like '*connectorName=TripIt&*' }
    }

    It 'with -WhatIf opens nothing and copies nothing' {
        & $Command -BaseUrl $Base -Service tripit -WhatIf 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Open-MultiplexConnectorPage -Times 0 -Exactly
        Should -Invoke -ModuleName Multiplex.Connector Set-MultiplexConnectorClipboard -Times 0 -Exactly
    }

    It 'passes what it is given through, so a local address is refused before anything opens' {
        { & (Join-Path $PSScriptRoot 'Add-MultiplexConnector.ps1') -BaseUrl 'http://localhost:5190' -WhatIf 6>$null } |
            Should -Throw '*public https address*'
    }
}


Describe 'tab completion for -Service' {
    BeforeAll {
        # What Tab does at the end of -Line, as PowerShell's own completion works it out.
        function Get-CompletionResult([string] $Line) { TabExpansion2 -inputScript $Line -cursorColumn $Line.Length }
        function Get-Completion([string] $Line) { (Get-CompletionResult $Line).CompletionMatches }
        function Get-Remembered { InModuleScope Multiplex.Connector { @($script:ServiceCompletionCache.Values) } }
        $script:Command = Join-Path $PSScriptRoot 'Add-MultiplexConnector.ps1'
    }
    BeforeEach {
        Use-FakeMultiplex
        InModuleScope Multiplex.Connector { $script:ServiceCompletionCache.Clear() }
    }

    It 'offers the services the server has, each with its full name and address as the hint' {
        $offered = Get-Completion "& '$Command' -BaseUrl $Base -Service "
        $offered.CompletionText | Should -Be 'tripit', 'linkedin'
        $offered[0].ToolTip | Should -BeLike "*TripIt*$Base/tripit/mcp*"
        $offered[1].ToolTip | Should -BeLike "*LinkedIn*$Base/linkedin/mcp*"
    }

    It 'offers only the services that start with what has been typed, by short or full name, in any case, quoted or not' {
        (Get-Completion "& '$Command' -BaseUrl $Base -Service li").CompletionText | Should -Be 'linkedin'
        (Get-Completion "& '$Command' -BaseUrl $Base -Service TRIP").CompletionText | Should -Be 'tripit'
        (Get-Completion "& '$Command' -BaseUrl $Base -Service 'tr").CompletionText | Should -Be 'tripit'
    }

    It 'uses an address given by position or by a shortened -BaseUrl' {
        (Get-Completion "& '$Command' $Base -Service ").CompletionText | Should -Be 'tripit', 'linkedin'
        (Get-Completion "& '$Command' -B $Base -Service ").CompletionText | Should -Be 'tripit', 'linkedin'
    }

    It 'leaves out the services already listed before a comma' {
        (Get-Completion "& '$Command' -BaseUrl $Base -Service tripit,").CompletionText | Should -Be 'linkedin'
        (Get-Completion "& '$Command' -BaseUrl $Base -Service tripit,li").CompletionText | Should -Be 'linkedin'
        $offered = @(Get-Completion "& '$Command' -BaseUrl $Base -Service tripit,linkedin,")
        $offered.CompletionText | Should -Be ' '
        $offered[0].ToolTip | Should -BeLike '*already listed*'
    }

    It 'with no match, leaves what was typed exactly as it was, with the reason as the hint, instead of offering file names' {
        $line = "& '$Command' -BaseUrl $Base -Service zz"
        $result = Get-CompletionResult $line
        @($result.CompletionMatches).CompletionText | Should -Be 'zz'
        $result.ReplacementIndex | Should -Be ($line.Length - 2)
        $result.ReplacementLength | Should -Be 2
        $result.CompletionMatches[0].ToolTip | Should -BeLike "*$Base*no service*'zz'*"
    }

    It 'treats wildcard characters typed as plain text' {
        $offered = @(Get-Completion "& '$Command' -BaseUrl $Base -Service *")
        $offered.CompletionText | Should -Be '*'
        $offered[0].ToolTip | Should -BeLike "*no service*'[*]'*"
    }

    It 'asks the address that the command will use when -BaseUrl is not typed' {
        Mock -ModuleName Multiplex.Connector Resolve-MultiplexConnectorBaseUrl { 'https://box.example.ts.net' } -ParameterFilter { -not $BaseUrl }
        (Get-Completion "& '$Command' -Service ").CompletionText | Should -Be 'tripit', 'linkedin'
    }

    It 'when the server cannot be reached, adds only a space where nothing was typed, with the reason as the hint' {
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod { throw 'No such host is known.' }
        $line = "& '$Command' -BaseUrl $Base -Service "
        $result = Get-CompletionResult $line
        @($result.CompletionMatches).CompletionText | Should -Be ' '
        $result.ReplacementIndex | Should -Be $line.Length
        $result.ReplacementLength | Should -Be 0
        $result.CompletionMatches[0].ToolTip | Should -BeLike "*$Base*No such host is known.*"
    }

    It 'when there is no address to ask, says to type -BaseUrl' {
        $offered = @(Get-Completion "& '$Command' -BaseUrl http://localhost:5190 -Service tr")
        $offered.CompletionText | Should -Be 'tr'
        $offered[0].ToolTip | Should -BeLike '*public https address*'
    }

    It 'when -BaseUrl is a variable, says Tab needs it typed out, rather than that none was given' {
        $offered = @(Get-Completion "& '$Command' -BaseUrl `$env:MULTIPLEX_NO_SUCH_VARIABLE -Service ")
        $offered[0].ToolTip | Should -BeLike '*typed out*'
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -Times 0 -Exactly
    }

    It 'asks each request briefly, so a single slow request cannot hold Tab for long' {
        Get-Completion "& '$Command' -BaseUrl $Base -Service " | Out-Null
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -ParameterFilter { $TimeoutSec -gt 5 } -Times 0 -Exactly
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -Times 3 -Exactly
    }

    It 'on a slow server still offers every service in time, naming those it had no time to ask about by their short names' {
        Use-FakeMultiplex -Slugs 'a1', 'a2', 'a3', 'a4', 'a5', 'a6'
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod {
            Start-Sleep -Milliseconds 400
            if ($Uri -like '*oauth-authorization-server') { return [pscustomobject]$global:MultiplexConnectorFakeServer }
            $slug = ($Uri -split '/')[-2]
            [pscustomobject]@{ resource = "https://box.example.ts.net/$slug/mcp"; resource_name = "Name of $slug" }
        }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $offered = @(InModuleScope Multiplex.Connector { Get-MultiplexServiceCompletion -Word '' -Bound @{ BaseUrl = 'https://box.example.ts.net' } -TimeoutSec 1 })
        $clock.Elapsed.TotalSeconds | Should -BeLessThan 2
        $offered.CompletionText | Should -Be 'a1', 'a2', 'a3', 'a4', 'a5', 'a6'
        $offered[0].ToolTip | Should -BeLike 'Name of a1*'
        $offered[-1].ToolTip | Should -BeLike 'a6*https://box.example.ts.net/a6/mcp*'
        ((Get-Remembered)[0].Until - [datetime]::UtcNow).TotalSeconds | Should -BeLessOrEqual 30   # so the names are filled in soon
    }

    It 'still offers every service when asking about one of them fails' {
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod {
            if ($Uri -like '*oauth-authorization-server') { return [pscustomobject]$global:MultiplexConnectorFakeServer }
            throw 'The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing.'
        }
        (Get-Completion "& '$Command' -BaseUrl $Base -Service ").CompletionText | Should -Be 'tripit', 'linkedin'
    }

    It 'gives up, with the reason, when the server''s list itself does not come in time' {
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod { throw 'The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing.' }
        (Get-Completion "& '$Command' -BaseUrl $Base -Service ")[0].ToolTip | Should -BeLike '*sign-in settings*Timeout*'
    }

    It 'remembers the services for a few minutes, so the next Tab answers at once' {
        Get-Completion "& '$Command' -BaseUrl $Base -Service " | Out-Null
        (Get-Completion "& '$Command' -BaseUrl $Base -Service li").CompletionText | Should -Be 'linkedin'
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -Times 3 -Exactly
        ((Get-Remembered)[0].Until - [datetime]::UtcNow).TotalMinutes | Should -BeGreaterThan 1
    }

    It 'asks the server again once what it remembers has expired' {
        Get-Completion "& '$Command' -BaseUrl $Base -Service " | Out-Null
        InModuleScope Multiplex.Connector { foreach ($entry in $script:ServiceCompletionCache.Values) { $entry.Until = [datetime]::UtcNow.AddSeconds(-1) } }
        Get-Completion "& '$Command' -BaseUrl $Base -Service " | Out-Null
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -Times 6 -Exactly
    }

    It 'remembers a server that could not be reached only briefly, so repeated Tabs answer at once' {
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod { throw 'No such host is known.' }
        Get-Completion "& '$Command' -BaseUrl $Base -Service " | Out-Null
        (Get-Completion "& '$Command' -BaseUrl $Base -Service ")[0].ToolTip | Should -BeLike '*No such host is known.*'
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -Times 1 -Exactly
        ((Get-Remembered)[0].Until - [datetime]::UtcNow).TotalSeconds | Should -BeLessOrEqual 30
    }

    It 'remembers a server with no services only briefly, since it may be starting' {
        Use-FakeMultiplex -Slugs @()
        Get-Completion "& '$Command' -BaseUrl $Base -Service " | Out-Null
        ((Get-Remembered)[0].Until - [datetime]::UtcNow).TotalSeconds | Should -BeLessOrEqual 30
    }

    It 'remembers each address apart' {
        Get-Completion "& '$Command' -BaseUrl $Base -Service " | Out-Null
        Use-FakeMultiplex -Issuer 'https://other.example.ts.net' -Slugs tripit
        (Get-Completion "& '$Command' -BaseUrl https://other.example.ts.net -Service ").CompletionText | Should -Be 'tripit'
        (Get-Completion "& '$Command' -BaseUrl $Base -Service ").CompletionText | Should -Be 'tripit', 'linkedin'
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -Times 5 -Exactly
    }

    It 'leaves the command itself asking the server afresh' {
        Get-Completion "& '$Command' -BaseUrl $Base -Service " | Out-Null
        Add-MultiplexConnector -BaseUrl $Base -Service tripit -WhatIf 6>$null
        Should -Invoke -ModuleName Multiplex.Connector Invoke-RestMethod -Times 6 -Exactly
    }

    It 'completes the same way when the module''s own command is used' {
        (Get-Completion "Add-MultiplexConnector -BaseUrl $Base -Service link").CompletionText | Should -Be 'linkedin'
    }

    It 'loads what it needs by itself in a new PowerShell, where nothing is loaded yet' {
        $line = "& '$Command' -BaseUrl http://localhost:5190 -Service "
        $hint = pwsh -NoProfile -NonInteractive -Command "(TabExpansion2 -inputScript `"$line`" -cursorColumn $($line.Length)).CompletionMatches[0].ToolTip"
        "$hint" | Should -BeLike '*public https address*'
    }
}