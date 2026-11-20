#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'Multiplex.Connector.psm1') -Force
    $script:Base = 'https://box.example.ts.net'

    # What a Multiplex at $Base with TripIt and LinkedIn answers; a test may change it before calling.
    function Use-FakeMultiplex([string] $Issuer = $script:Base, [string[]] $Slugs = @('tripit', 'linkedin')) {
        $script:FakeIssuer = $Issuer
        $script:FakeSlugs = $Slugs
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod {
            switch -Regex ($Uri) {
                '/\.well-known/oauth-authorization-server$' { return [pscustomobject]@{ issuer = $script:FakeIssuer; scopes_supported = $script:FakeSlugs } }
                '/\.well-known/oauth-protected-resource/(?<slug>[^/]+)/mcp$' {
                    $slug = $Matches.slug
                    $name = @{ tripit = 'TripIt'; linkedin = 'LinkedIn' }[$slug]
                    return [pscustomobject]@{ resource = "$script:FakeIssuer/$slug/mcp"; resource_name = $name }
                }
                default { throw "Unexpected request: $Uri" }
            }
        }
    }
}

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

    It 'says the server could not be reached, and where' {
        Mock -ModuleName Multiplex.Connector Invoke-RestMethod { throw 'No such host is known.' }
        { Get-MultiplexAccountService -BaseUrl $Base } | Should -Throw "*$Base*No such host is known.*"
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

    It 'says that sign-in asks for the Multiplex passcode, and that an existing connector would be added twice' {
        $said = Add-MultiplexConnector -BaseUrl $Base -Service tripit 6>&1 | Out-String
        $said | Should -BeLike '*passcode*-ShowPasscode*'
        $said | Should -BeLike '*already*second*'
    }

    It 'shows the one-line commands for agents on a PC, and says they do not reach the phone' {
        $said = Add-MultiplexConnector -BaseUrl $Base -Service tripit -Agent Claude 6>&1 | Out-String
        $said | Should -BeLike "*codex mcp add tripit --url $Base/tripit/mcp*"
        $said | Should -BeLike "*copilot mcp add --transport http tripit $Base/tripit/mcp*"
        $said | Should -BeLike '*not*phone*'
    }
}

Describe 'the command' {
    It 'passes what it is given through, so a local address is refused before anything opens' {
        { & (Join-Path $PSScriptRoot 'Add-MultiplexConnector.ps1') -BaseUrl 'http://localhost:5190' -WhatIf 6>$null } |
            Should -Throw '*public https address*'
    }
}
