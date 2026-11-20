#Requires -Version 7.2
# Helpers for adding Multiplex's services to your Claude and ChatGPT accounts, so they work on
# every device signed in to those accounts, a phone included. Neither has a command line or API
# for that: Claude has a link that opens its add-connector dialog already filled in, ChatGPT only
# its add page. Add-MultiplexConnector.ps1 uses these; Add-MultiplexConnector.Tests.ps1 tests them.
Set-StrictMode -Version Latest

$script:ClaudeAddConnectorPage = 'https://claude.ai/customize/connectors'
$script:ChatGptAddPage = 'https://chatgpt.com/plugins'
# What tab completion learned from each server, and for how long it is trusted.
$script:ServiceCompletionCache = @{}
$script:ServiceCompletionLifetime = [TimeSpan]::FromMinutes(5)

function Resolve-MultiplexConnectorBaseUrl {
    <#
    Multiplex's public address, for Claude and ChatGPT: -BaseUrl, else the one the Multiplex
    installed on this PC recorded for its tunnel. Claude and ChatGPT reach services from their
    own servers, so only an https address on the internet will do; anything else is refused
    with what to pass instead.
    #>
    [CmdletBinding()] [OutputType([string])]
    param(
        [string] $BaseUrl,
        [string] $ConfigPath = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.multiplex\multiplex.config.json'))
    if (-not $BaseUrl) {
        $recorded = if (Test-Path -LiteralPath $ConfigPath) {
            try { (Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop)['PublicBaseUrl'] }
            catch { throw "The Multiplex configuration at $ConfigPath could not be read ($($_.Exception.Message)). Pass -BaseUrl with the public address of the PC that runs Multiplex." }
        }
        if (-not $recorded) {
            throw "This PC has no public address for Multiplex (no install here, or one without a tunnel). " +
                'Pass -BaseUrl with the public address of the PC that runs Multiplex, for example -BaseUrl https://<pc>.<tailnet>.ts.net.'
        }
        $BaseUrl = $recorded
    }
    $uri = $null
    $public = [uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$uri) -and $uri.Scheme -eq 'https' -and
        -not $uri.IsLoopback -and $uri.Host -ne 'localhost'
    if (-not $public) {
        throw "Claude and ChatGPT connect from the internet, so they need Multiplex's public https address; $BaseUrl is not one."
    }
    if ($uri.UserInfo) { throw 'Leave the user name and password out of the address: it is printed, and given to Claude and ChatGPT.' }
    if ($uri.AbsolutePath -ne '/' -or $uri.Query) {
        throw "Give the address of Multiplex itself, like $($uri.GetLeftPart([UriPartial]::Authority)), not $BaseUrl."
    }
    return $uri.GetLeftPart([UriPartial]::Authority)
}

function Get-MultiplexAccountService {
    <#
    The services of the Multiplex at -BaseUrl that can go on an account: those that need
    sign-in, as the server lists them in its own sign-in settings, each with its name and
    address. Open services are left out, because Multiplex refuses them to the internet unless
    told otherwise. Stops when the server's own public address is not -BaseUrl, since sign-in
    from a connector added at -BaseUrl would then fail.
    #>
    [CmdletBinding()] param([Parameter(Mandatory)] [string] $BaseUrl, [int] $TimeoutSec = 15)
    # One session for every request: its connection is reused, so a slow link pays for setting
    # one up (a round trip each for TCP and TLS) only once.
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $settings = "$BaseUrl/.well-known/oauth-authorization-server"
    try { $server = Invoke-RestMethod -Uri $settings -TimeoutSec $TimeoutSec -WebSession $session -ErrorAction Stop }
    catch { throw "Could not read Multiplex's sign-in settings at ${settings}: $($_.Exception.Message) Is Multiplex running and reachable at ${BaseUrl}?" }
    $issuer = "$(Get-JsonProperty $server 'issuer')".TrimEnd('/')
    if (-not $issuer) { throw "The server at $BaseUrl did not give its public address in $settings. Is it Multiplex?" }
    if ($issuer -ne $BaseUrl) {
        throw "The Multiplex at $BaseUrl gives its public address as $issuer, so a connector added at $BaseUrl would fail to sign in. Use -BaseUrl $issuer."
    }
    foreach ($slug in @(Get-JsonProperty $server 'scopes_supported')) {
        if (-not $slug) { continue }
        # A service's name goes into a request path and into commands printed to paste.
        if ("$slug" -notmatch '\A[A-Za-z0-9][A-Za-z0-9_-]*\z') {   # \z: $ would let a trailing line break through
            throw "The Multiplex at $BaseUrl lists a service named '$slug', which is not a valid service name; nothing was opened."
        }
        $address = "$BaseUrl/$slug/mcp"
        $metadata = "$BaseUrl/.well-known/oauth-protected-resource/$slug/mcp"
        try { $resource = Invoke-RestMethod -Uri $metadata -TimeoutSec $TimeoutSec -WebSession $session -ErrorAction Stop }
        catch { throw "Could not read the settings of the $slug service at ${metadata}: $($_.Exception.Message)" }
        $reported = "$(Get-JsonProperty $resource 'resource')"
        if ($reported -and $reported -ne $address) {
            throw "The Multiplex at $BaseUrl gives the address of its $slug service as $reported, not $address, so a connector added at either would fail to sign in."
        }
        $name = "$(Get-JsonProperty $resource 'resource_name')"
        [pscustomobject]@{ Slug = "$slug"; Name = $name ? $name : "$slug"; Address = $address }
    }
}

function Get-MultiplexServiceCompletion {
    <#
    What Tab offers for -Service: the services the Multiplex at -BaseUrl (by default, the one the
    command itself would use) can add to an account, whose short or full name starts with
    -Word, each with its full name and address as the hint. The server is asked briefly, so Tab
    never hangs for long, and its answer is remembered for a few minutes, so only the first Tab
    waits for it. With nothing to offer (no match, no address, no answer), it offers what was
    typed, unchanged, with the reason as the hint: offering nothing would make PowerShell offer
    file names instead.
    #>
    [CmdletBinding()] [OutputType([System.Management.Automation.CompletionResult])]
    param([string] $BaseUrl, [string] $Word, [int] $TimeoutSec = 5)
    $typed = "$Word".Trim("'", '"')
    $offered = 0
    try {
        $BaseUrl = Resolve-MultiplexConnectorBaseUrl -BaseUrl $BaseUrl
        $known = $script:ServiceCompletionCache[$BaseUrl]
        if (-not $known -or ([datetime]::UtcNow - $known.At) -gt $script:ServiceCompletionLifetime) {
            $known = [pscustomobject]@{ At = [datetime]::UtcNow; Services = @(Get-MultiplexAccountService -BaseUrl $BaseUrl -TimeoutSec $TimeoutSec) }
            $script:ServiceCompletionCache[$BaseUrl] = $known
        }
        foreach ($s in $known.Services) {
            if ($s.Slug.StartsWith($typed, [StringComparison]::OrdinalIgnoreCase) -or $s.Name.StartsWith($typed, [StringComparison]::OrdinalIgnoreCase)) {
                [System.Management.Automation.CompletionResult]::new($s.Slug, $s.Slug, 'ParameterValue', "$($s.Name)  $($s.Address)")
                $offered++
            }
        }
        $why = "The Multiplex at $BaseUrl has no service that can be added to an account$($typed ? " starting with '$typed'" : '')."
    }
    catch { $why = $_.Exception.Message }
    if (-not $offered) {
        # A completion must have text; a lone space leaves the line as it was.
        [System.Management.Automation.CompletionResult]::new(($Word ? $Word : ' '), '(no service)', 'ParameterValue', $why)
    }
}

function Get-JsonProperty {
    <# A property of parsed JSON, or nothing when it is missing (strict mode throws on a missing one). #>
    param($Object, [Parameter(Mandatory)] [string] $Name)
    if ($null -eq $Object) { return }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { $property.Value }
}

function Get-MultiplexClaudeConnectorLink {
    <# Claude's own link that opens "Add custom connector" with -Name and -Address filled in. #>
    [CmdletBinding()] [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $Address)
    '{0}?modal=add-custom-connector&connectorName={1}&connectorUrl={2}' -f $script:ClaudeAddConnectorPage,
        [uri]::EscapeDataString($Name), [uri]::EscapeDataString($Address)
}

function Open-MultiplexConnectorPage {
    <# Opens -Url in the default browser. #>
    [CmdletBinding()] param([Parameter(Mandatory)] [string] $Url)
    Start-Process -FilePath $Url
}

function Set-MultiplexConnectorClipboard {
    <# Puts -Text on the clipboard. #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Add-MultiplexConnector asks ShouldProcess before calling it.')]
    [CmdletBinding()] param([Parameter(Mandatory)] [string] $Text)
    Set-Clipboard -Value $Text
}

function Add-MultiplexConnector {
    <#
    Adds the services of the Multiplex at -BaseUrl (by default, the public address of the one
    installed on this PC) to your Claude and ChatGPT accounts, as far
    as a command can: for Claude it opens the add-connector dialog already filled in, one per
    service; for ChatGPT it opens the add page once and, for a single service, copies its
    address. You finish in the browser: confirm, then sign in (the Multiplex passcode, then the
    site). Every link and address is also printed, with the one-line commands for agents that
    only run on a PC. -WhatIf prints all of it and opens nothing.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string] $BaseUrl,
        # $args: the command, the parameter, the word typed, the syntax tree, the parameters typed so far.
        [ArgumentCompleter({ Get-MultiplexServiceCompletion -BaseUrl $args[4]['BaseUrl'] -Word $args[2] })]
        [string[]] $Service,
        [ValidateSet('Claude', 'ChatGPT')] [string[]] $Agent = @('Claude', 'ChatGPT'))
    $BaseUrl = Resolve-MultiplexConnectorBaseUrl -BaseUrl $BaseUrl
    $offered = @(Get-MultiplexAccountService -BaseUrl $BaseUrl)
    if (-not $offered) { throw "The Multiplex at $BaseUrl has no service that can be added to an account (none needs sign-in)." }
    $chosen = $offered
    if ($Service) {
        $picked = foreach ($wanted in $Service) {
            $match = $offered | Where-Object { $_.Slug -eq $wanted -or $_.Name -eq $wanted } | Select-Object -First 1
            if (-not $match) {
                $names = ($offered | ForEach-Object { "$($_.Slug) ($($_.Name))" }) -join ', '
                throw "The Multiplex at $BaseUrl has no service '$wanted' that can be added to an account. It has: $names."
            }
            $match.Slug
        }
        $chosen = @($offered | Where-Object { $_.Slug -in $picked })   # each once, in the server's order
    }

    Write-Host "Adding from the Multiplex at ${BaseUrl}:"
    foreach ($s in $chosen) { Write-Host "  $($s.Name)  $($s.Address)" }

    if ('Claude' -in $Agent) {
        Write-Host ''
        Write-Host 'Claude (one account for the web, Desktop and phone apps):'
        foreach ($s in $chosen) {
            $link = Get-MultiplexClaudeConnectorLink -Name $s.Name -Address $s.Address
            $opened = $PSCmdlet.ShouldProcess($link, "Open Claude's add-connector dialog for $($s.Name)")
            if ($opened) { Open-MultiplexConnectorPage -Url $link }
            Write-Host "  $($s.Name): $($opened ? 'opened' : 'open') this link, check the name and address, click Continue, then sign in:"
            Write-Host "    $link"
        }
    }

    if ('ChatGPT' -in $Agent) {
        Write-Host ''
        Write-Host 'ChatGPT (one account for the web, desktop and phone apps; developer mode must be on):'
        $opened = $PSCmdlet.ShouldProcess($script:ChatGptAddPage, "Open ChatGPT's page for adding a custom MCP server")
        if ($opened) { Open-MultiplexConnectorPage -Url $script:ChatGptAddPage }
        Write-Host "  $($opened ? 'Opened' : 'Open') $script:ChatGptAddPage, click Add, add a custom MCP server with the address, choose OAuth, then sign in."
        if ($chosen.Count -eq 1) {
            $copied = $PSCmdlet.ShouldProcess('clipboard', "Copy the $($chosen[0].Name) address")
            if ($copied) { Set-MultiplexConnectorClipboard -Text $chosen[0].Address }
            Write-Host "  The $($chosen[0].Name) address$($copied ? ' is on the clipboard' : ''): $($chosen[0].Address)"
        }
        else {
            Write-Host '  Add one server per address:'
            foreach ($s in $chosen) { Write-Host "    $($s.Name): $($s.Address)" }
        }
    }

    Write-Host ''
    Write-Host 'Signing in asks for the Multiplex passcode. On the PC that runs Multiplex, show it with: ./scripts/Install-Multiplex.ps1 -ShowPasscode'
    Write-Host 'A service already on your account gets a second copy; remove the extra one in the app''s connector settings.'
    Write-Host ''
    Write-Host 'Agents on a PC (these change that PC only, not your account, so they do not reach your phone):'
    foreach ($s in $chosen) {
        Write-Host "  Codex:       codex mcp add $($s.Slug) --url $($s.Address)"
        Write-Host "  Copilot CLI: copilot mcp add --transport http $($s.Slug) $($s.Address)"
    }
    Write-Host '  Claude Code, signed in with your Claude account, already has that account''s connectors.'
}

Export-ModuleMember -Function *-Multiplex*
