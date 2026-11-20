#Requires -Version 7.2

<#
.SYNOPSIS
    Adds Multiplex's services to your Claude and ChatGPT accounts, so they work on your phone too.
.DESCRIPTION
    Neither Claude nor ChatGPT has a command line or API for adding a connector to an account,
    so this does as much of it as a command can. It reads the services from the running
    Multiplex (those that need sign-in), then:
      - Claude: opens Claude's own "Add custom connector" dialog with each service's name and
        address filled in; you check them and click Continue, then sign in;
      - ChatGPT: opens ChatGPT's add page and, for a single service, copies its address; you
        add a custom MCP server, paste it, choose OAuth, then sign in.
    A connector added this way is on the account, so it appears in the phone apps as well.
    Signing in asks for the Multiplex passcode, then signs in to the site. A service already on
    the account gets a second copy. It also prints the one-line commands for agents that only
    run on a PC (Codex, Copilot CLI); it never runs them.
.PARAMETER BaseUrl
    Multiplex's public https address. Defaults to the one the Multiplex installed on this PC
    recorded for its tunnel.
.PARAMETER Service
    The services to add, by name or short name (for example tripit). Defaults to all of them.
.PARAMETER Agent
    Claude, ChatGPT or both (the default).
.EXAMPLE
    ./scripts/Add-MultiplexConnector.ps1 -BaseUrl https://<pc>.<tailnet>.ts.net -WhatIf
.EXAMPLE
    ./scripts/Add-MultiplexConnector.ps1 -Service linkedin -Agent Claude
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $BaseUrl,
    [string[]] $Service,
    [ValidateSet('Claude', 'ChatGPT')] [string[]] $Agent = @('Claude', 'ChatGPT'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Multiplex.Connector.psm1') -Force

Add-MultiplexConnector -BaseUrl (Resolve-MultiplexConnectorBaseUrl -BaseUrl $BaseUrl) -Service $Service -Agent $Agent -WhatIf:$WhatIfPreference
