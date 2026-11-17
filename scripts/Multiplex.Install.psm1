#Requires -Version 7.2
# Helpers for installing Multiplex on this PC: configuration, the owner passcode, the
# Claude Desktop bridge entry, the tunnel, and endpoint checks. Install-Multiplex.ps1 and
# Start-Multiplex.ps1 use them; Install-Multiplex.Tests.ps1 tests them.
Set-StrictMode -Version Latest

$script:Pbkdf2Iterations = 210000

function Get-MultiplexRoot {
    [CmdletBinding()] param([string] $Root)
    if ($Root) { return $Root }
    return Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Multiplex'
}

function New-MultiplexPasscode {
    <# A random passcode that is easy to type on a phone: 4 groups of 4 lowercase letters/digits. #>
    [CmdletBinding()] [OutputType([string])] param()
    $alphabet = 'abcdefghjkmnpqrstuvwxyz23456789'   # no l/1/i/o/0
    $groups = foreach ($g in 1..4) {
        -join (1..4 | ForEach-Object { $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)] })
    }
    return $groups -join '-'
}

function ConvertTo-MultiplexPasscodeHash {
    <# Hash format shared with the server: pbkdf2-sha256$<iterations>$<saltBase64>$<hashBase64> #>
    [CmdletBinding()] [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Passcode, [int] $Iterations = $script:Pbkdf2Iterations, [byte[]] $Salt)
    if (-not $Salt) { $Salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16) }
    $hash = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        [System.Text.Encoding]::UTF8.GetBytes($Passcode), $Salt, $Iterations,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    return 'pbkdf2-sha256${0}${1}${2}' -f $Iterations, [Convert]::ToBase64String($Salt), [Convert]::ToBase64String($hash)
}

function Test-MultiplexPasscodeHash {
    [CmdletBinding()] [OutputType([bool])]
    param([Parameter(Mandatory)] [string] $Passcode, [Parameter(Mandatory)] [string] $Hash)
    $parts = $Hash.Split('$')
    if ($parts.Count -ne 4 -or $parts[0] -ne 'pbkdf2-sha256') { return $false }
    $expected = [Convert]::FromBase64String($parts[3])
    $actual = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        [System.Text.Encoding]::UTF8.GetBytes($Passcode), [Convert]::FromBase64String($parts[2]), [int]$parts[1],
        [System.Security.Cryptography.HashAlgorithmName]::SHA256, $expected.Length)
    return [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals($actual, $expected)
}

function Save-MultiplexPasscodeSecret {
    <# Keeps the plaintext passcode for the owner only, encrypted with Windows DPAPI (current user). #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [string] $Passcode, [Parameter(Mandatory)] [string] $Path)
    if ($PSCmdlet.ShouldProcess($Path, 'Save the owner passcode (DPAPI, current user)')) {
        $secure = ConvertTo-SecureString -String $Passcode -AsPlainText -Force
        New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
        Set-Content -LiteralPath $Path -Value (ConvertFrom-SecureString -SecureString $secure) -NoNewline
    }
}

function Read-MultiplexPasscodeSecret {
    [CmdletBinding()] [OutputType([string])] param([Parameter(Mandatory)] [string] $Path)
    $secure = ConvertTo-SecureString -String (Get-Content -LiteralPath $Path -Raw)
    return [System.Net.NetworkCredential]::new('', $secure).Password
}

function Read-MultiplexConfig {
    [CmdletBinding()] param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ Port = 5190; PublicBaseUrl = $null; Tunnel = 'None'; PasscodeHash = $null; AllowedAccounts = [ordered]@{ tripit = @() } }
    }
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if (-not $json.Contains('AllowedAccounts')) { $json.AllowedAccounts = @{ tripit = @() } }
    return $json
}

function Write-MultiplexConfig {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Config, [Parameter(Mandatory)] [string] $Path)
    if ($PSCmdlet.ShouldProcess($Path, 'Write Multiplex configuration')) {
        New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
        $tmp = "$Path.tmp"
        $Config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tmp
        Move-Item -LiteralPath $tmp -Destination $Path -Force
    }
}

function Get-MultiplexServerEnvironment {
    <# The environment variables Start-Multiplex.ps1 sets before launching the server. #>
    [CmdletBinding()] [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Config, [Parameter(Mandatory)] [string] $Root)
    $envVars = [ordered]@{
        ASPNETCORE_ENVIRONMENT        = 'Production'
        # "localhost" makes Kestrel listen on both 127.0.0.1 and ::1; binding only 127.0.0.1 costs
        # every client that resolves localhost to ::1 first a ~2 s connection retry.
        ASPNETCORE_URLS               = "http://localhost:$($Config.Port)"
        Multiplex__DataPath           = Join-Path $Root 'data'
        Multiplex__PublicBaseUrl      = ($Config.PublicBaseUrl ?? "http://localhost:$($Config.Port)").TrimEnd('/')
        Multiplex__Gate__PasscodeHash = $Config.PasscodeHash
    }
    foreach ($site in $Config.AllowedAccounts.Keys) {
        $i = 0
        foreach ($account in @($Config.AllowedAccounts[$site])) {
            $envVars["Multiplex__Sites__${site}__AllowedAccounts__$i"] = $account
            $i++
        }
    }
    return $envVars
}

function Set-ClaudeDesktopBridge {
    <#
    Adds (or updates) a stdio bridge entry for a Multiplex endpoint in Claude Desktop's config,
    keeping every other entry. mcp-remote opens the system browser for the same sign-in page.
    Claude Desktop must be restarted to pick it up.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $ConfigPath,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $ServerUrl,
        [string] $McpRemoteVersion = '0.16.6',
        [int] $AuthTimeoutSeconds = 600)
    $config = if (Test-Path -LiteralPath $ConfigPath) {
        Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
    } else { [ordered]@{} }
    if ($null -eq $config) { $config = [ordered]@{} }
    if (-not $config.Contains('mcpServers') -or $null -eq $config.mcpServers) { $config.mcpServers = [ordered]@{} }
    $entry = [ordered]@{
        command = 'npx'
        args    = @('-y', "mcp-remote@$McpRemoteVersion", $ServerUrl, '--auth-timeout', "$AuthTimeoutSeconds")
    }
    $existing = $config.mcpServers[$Name]
    if ($existing -and (($existing | ConvertTo-Json -Depth 5 -Compress) -eq ($entry | ConvertTo-Json -Depth 5 -Compress))) {
        Write-Verbose "Claude Desktop entry '$Name' is already up to date."
        return $false
    }
    $config.mcpServers[$Name] = $entry
    if ($PSCmdlet.ShouldProcess($ConfigPath, "Add Claude Desktop MCP entry '$Name'")) {
        New-Item -ItemType Directory -Force -Path (Split-Path $ConfigPath) | Out-Null
        if (Test-Path -LiteralPath $ConfigPath) { Copy-Item -LiteralPath $ConfigPath -Destination "$ConfigPath.bak" -Force }
        $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ConfigPath
    }
    return $true
}

function Get-QuickTunnelUrl {
    <# Extracts the https://*.trycloudflare.com address from cloudflared's log lines. #>
    [CmdletBinding()] param([string[]] $Lines)
    foreach ($line in $Lines) {
        if ($line -match '(https://[a-z0-9-]+\.trycloudflare\.com)') { return $Matches[1] }
    }
    return $null
}

function Test-MultiplexEndpoint {
    <#
    Checks, without signing in, that an authenticated MCP endpoint is set up the way Claude and
    ChatGPT need: discovery documents agree with each other and an unauthenticated call gets
    HTTP 401 pointing at the resource metadata. Returns one result object per check.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $BaseUrl, [string] $Slug = 'tripit', [string] $ExpectedBaseUrl)
    $BaseUrl = $BaseUrl.TrimEnd('/')
    $expected = ($ExpectedBaseUrl ? $ExpectedBaseUrl : $BaseUrl).TrimEnd('/')
    $resource = "$expected/$Slug/mcp"
    $results = [System.Collections.Generic.List[object]]::new()
    function Add-Result($name, $ok, $detail) { $results.Add([pscustomobject]@{ Check = $name; Passed = [bool]$ok; Detail = $detail }) }

    try {
        $health = Invoke-WebRequest "$BaseUrl/healthz" -SkipHttpErrorCheck -TimeoutSec 10
        Add-Result 'health' ($health.StatusCode -eq 200) "HTTP $($health.StatusCode)"
    } catch { Add-Result 'health' $false $_.Exception.Message }

    foreach ($prmPath in "/.well-known/oauth-protected-resource/$Slug/mcp", '/.well-known/oauth-protected-resource') {
        try {
            $prm = Invoke-RestMethod "$BaseUrl$prmPath" -TimeoutSec 10
            Add-Result "resource metadata $prmPath" ($prm.resource -eq $resource -and @($prm.authorization_servers)[0] -eq $expected) "resource=$($prm.resource) as=$(@($prm.authorization_servers)[0])"
        } catch { Add-Result "resource metadata $prmPath" $false $_.Exception.Message }
    }

    try {
        $as = Invoke-RestMethod "$BaseUrl/.well-known/oauth-authorization-server" -TimeoutSec 10
        $ok = $as.issuer -eq $expected -and 'S256' -in @($as.code_challenge_methods_supported) -and $as.registration_endpoint
        Add-Result 'authorization server metadata' $ok "issuer=$($as.issuer)"
    } catch { Add-Result 'authorization server metadata' $false $_.Exception.Message }

    try {
        $body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"install-check","version":"1"}}}'
        $r = Invoke-WebRequest "$BaseUrl/$Slug/mcp" -Method Post -Body $body -ContentType 'application/json' `
            -Headers @{ Accept = 'application/json, text/event-stream' } -SkipHttpErrorCheck -TimeoutSec 10
        $challenge = "$($r.Headers['WWW-Authenticate'])"
        $ok = $r.StatusCode -eq 401 -and $challenge -match [regex]::Escape("resource_metadata=`"$expected/.well-known/oauth-protected-resource/$Slug/mcp`"")
        Add-Result 'unauthenticated initialize gets 401' $ok "HTTP $($r.StatusCode); $challenge"
    } catch { Add-Result 'unauthenticated initialize gets 401' $false $_.Exception.Message }

    return $results
}

Export-ModuleMember -Function *-Multiplex*, Set-ClaudeDesktopBridge, Get-QuickTunnelUrl, ConvertTo-MultiplexPasscodeHash
