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
    Claude and ChatGPT call the services from their own servers on the internet, so the Multiplex
    must be reachable from the internet (Tailscale Funnel or an open incoming port); one that only
    your tailnet can reach will not connect. Signing in asks for the Multiplex passcode, then signs
    in to the site. Claude refuses an address it already has; ChatGPT may add a second copy. It
    also prints the one-line commands for agents that only run on a PC (Codex, Copilot CLI); it
    never runs them.
.PARAMETER BaseUrl
    Multiplex's public https address. Defaults to the one the Multiplex installed on this PC
    recorded for its tunnel.
.PARAMETER Service
    The services to add, by name or short name (for example tripit). Defaults to all of them.
    Tab offers the ones the server has: it asks the address typed after -BaseUrl (or the
    default) over the network, for at most a few seconds, and remembers the answer briefly.
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
    # $args: the command, the parameter, the word typed, the syntax tree, the parameters typed so far.
    [ArgumentCompleter({
        Import-Module (Join-Path $PSScriptRoot 'Multiplex.Connector.psm1')
        Get-MultiplexServiceCompletion -Word $args[2] -CommandAst $args[3] -Bound $args[4] })]
    [string[]] $Service,
    [ValidateSet('Claude', 'ChatGPT')] [string[]] $Agent = @('Claude', 'ChatGPT'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Not -Force: a copy already loaded in this session is used as it is (the tests rely on that).
Import-Module (Join-Path $PSScriptRoot 'Multiplex.Connector.psm1')

Add-MultiplexConnector @PSBoundParameters   # -WhatIf and -Confirm included
