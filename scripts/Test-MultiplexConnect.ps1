#Requires -Version 7.2

<#
.SYNOPSIS
    Connects to a running Multiplex endpoint as Claude or ChatGPT would, as far as a script can:
    discovery from the 401, client registration, and the PKCE authorization request, which must
    send the person to Microsoft sign-in.
.DESCRIPTION
    For checks of an install. Since #80 every connection starts with the person signing in with
    Microsoft, with an email the owner allows, and a script cannot do that for a person. So the
    check confirms that /oauth/authorize sends a browser that is not signed in to Microsoft
    sign-in, with this server's redirect URI (<public address>/signin-microsoft) and a client id,
    and ends there, saying so. The later steps (token exchange with PKCE, MCP initialize,
    tools/list, the -ToolName call and -ExpectEmail, -WriteCheck, refresh and -SaveGrantTo) need
    the code that sign-in ends with, so they are not reached. A server where Microsoft sign-in
    is not set up answers HTTP 503, which fails the check. Exit code 0 when every step it can
    run passes.
.NOTES
    -Passcode and $env:MULTIPLEX_PASSCODE were removed in #80: there is no owner passcode any more.
.EXAMPLE
    ./scripts/Test-MultiplexConnect.ps1 -BaseUrl http://localhost:5190
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [string] $Slug = 'tripit',
    [string] $ExpectEmail,
    [string] $ToolName = 'tripit_get_profile',
    [hashtable] $ToolArguments = @{},
    [string] $ClientMetadataUrl,
    # Also create a private, dated throwaway trip and delete it through the confirm step.
    # Use only with a test account.
    [switch] $WriteCheck,
    # Saves the final refresh token and endpoints as JSON, to check renewal hours later. Test servers only.
    [string] $SaveGrantTo)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$mcpUrl = "$BaseUrl/$Slug/mcp"
$failures = 0
function Step([string] $name, [scriptblock] $body) {
    try { $r = & $body; Write-Host "PASS  $name"; return $r }
    catch { $script:failures++; Write-Host "FAIL  $name -- $($_.Exception.Message)"; throw }
}
function Base64Url([byte[]] $bytes) { [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
function Resolve-Url([string] $from, [string] $to) { if (-not $to) { return $from }; return [Uri]::new([Uri]$from, $to).AbsoluteUri }

# A loopback "client" that never actually listens: we read the code from the redirect.
$redirectUri = 'http://127.0.0.1:53999/callback'
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()

$challenge = Step 'unauthenticated initialize is 401 with resource metadata' {
    $r = Invoke-WebRequest $mcpUrl -Method Post -ContentType 'application/json' -SkipHttpErrorCheck `
        -Headers @{ Accept = 'application/json, text/event-stream' } `
        -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"connect-test","version":"1"}}}'
    if ($r.StatusCode -ne 401) { throw "HTTP $($r.StatusCode)" }
    $h = "$($r.Headers['WWW-Authenticate'])"
    if ($h -notmatch 'resource_metadata="(?<u>[^"]+)"') { throw "no resource_metadata in '$h'" }
    $Matches['u']
}
$prm = Step 'protected resource metadata names this endpoint' {
    $p = Invoke-RestMethod $challenge
    if ($p.resource -ne $mcpUrl) { throw "resource '$($p.resource)' != '$mcpUrl'" }
    $p
}
$as = Step 'authorization server metadata' {
    $a = Invoke-RestMethod "$(@($prm.authorization_servers)[0])/.well-known/oauth-authorization-server"
    if ('S256' -notin @($a.code_challenge_methods_supported)) { throw 'S256 not supported' }
    $a
}
$clientId = Step 'client registration' {
    if ($ClientMetadataUrl) { return $ClientMetadataUrl }
    $reg = Invoke-RestMethod $as.registration_endpoint -Method Post -ContentType 'application/json' -Body (@{
        client_name = 'Multiplex connect test'; redirect_uris = @($redirectUri)
        grant_types = @('authorization_code', 'refresh_token'); response_types = @('code'); token_endpoint_auth_method = 'none'
    } | ConvertTo-Json)
    $reg.client_id
}

$verifier = Base64Url ([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$codeChallenge = Base64Url ([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($verifier)))
$state = Base64Url ([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
$query = @(@{
    response_type = 'code'; client_id = $clientId; redirect_uri = $redirectUri; state = $state
    code_challenge = $codeChallenge; code_challenge_method = 'S256'; resource = $mcpUrl; scope = $Slug
}.GetEnumerator() | ForEach-Object { "$($_.Key)=$([Uri]::EscapeDataString($_.Value))" }) -join '&'
$authorizeUrl = "$($as.authorization_endpoint)?$query"

$signIn = Step 'authorize sends the person to Microsoft sign-in' {
    # A browser that is not signed in and does not follow redirects, so each hop can be inspected.
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = [System.Net.CookieContainer]::new()
    $browser = [System.Net.Http.HttpClient]::new($handler)
    $browser.Timeout = [TimeSpan]::FromSeconds(30)
    $expectedRedirect = "$("$($as.issuer)".TrimEnd('/'))/signin-microsoft"
    $url = $authorizeUrl
    try {
        for ($hop = 0; $hop -lt 5; $hop++) {
            $response = $browser.GetAsync($url).GetAwaiter().GetResult()
            $status = [int]$response.StatusCode
            if ($status -eq 503) {
                throw ("HTTP 503 at ${url}: the server says Microsoft sign-in is not set up. Set Multiplex__Identity__Microsoft__ClientId, " +
                    'Multiplex__Identity__Microsoft__ClientSecret and Multiplex__Identity__AllowedEmails for the Windows account that runs ' +
                    'Multiplex (the installer shows how), then run the installer again')
            }
            if ($status -notin 301, 302, 303, 307, 308) { throw "HTTP $status at $url, not a redirect to Microsoft sign-in" }
            $next = Resolve-Url $url "$($response.Headers.Location)"
            if (([Uri]$next).Authority -eq ([Uri]$url).Authority) { $url = $next; continue }   # a hop on this server
            if (-not $next.StartsWith('https://login.microsoftonline.com/', [StringComparison]::OrdinalIgnoreCase)) {
                throw "redirected to $next, not to Microsoft sign-in"
            }
            $q = [System.Web.HttpUtility]::ParseQueryString(([Uri]$next).Query)
            if ($q['redirect_uri'] -ne $expectedRedirect) {
                throw "Microsoft sign-in is asked to send the person back to '$($q['redirect_uri'])', not to '$expectedRedirect'"
            }
            if (-not $q['client_id']) { throw 'the redirect to Microsoft sign-in names no client_id' }
            return [pscustomobject]@{ ClientId = $q['client_id']; RedirectUri = $q['redirect_uri'] }
        }
        throw 'too many hops'
    }
    finally { $browser.Dispose() }
}
Write-Host "      Microsoft client_id $($signIn.ClientId); redirect_uri $($signIn.RedirectUri)"
Write-Host ('STOP  the remaining steps need a person: signing in with Microsoft, with an allowed email, ends with the code ' +
    'that the token exchange, initialize, tools/list, the tool call and refresh need. This script cannot sign in for a ' +
    'person, so the check ends here.')
exit 0

# Not reached since #80 (above): they need the code a person's Microsoft sign-in ends with.
$code = $null
$tokens = Step 'token exchange with PKCE' {
    Invoke-RestMethod $as.token_endpoint -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type = 'authorization_code'; code = $code; redirect_uri = $redirectUri; client_id = $clientId
        code_verifier = $verifier; resource = $mcpUrl }
}

$headers = @{ Authorization = "Bearer $($tokens.access_token)"; Accept = 'application/json, text/event-stream' }
function Invoke-Mcp([string] $method, [hashtable] $params, [int] $id) {
    $r = Invoke-WebRequest $mcpUrl -Method Post -ContentType 'application/json' -Headers $headers -SkipHttpErrorCheck `
        -Body (@{ jsonrpc = '2.0'; id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10)
    if ($r.StatusCode -ne 200) { throw "HTTP $($r.StatusCode)" }
    $text = "$($r.Content)"
    if ($text -match '(?m)^data:\s*(?<j>\{.*\})\s*$') { $text = $Matches['j'] }
    $json = $text | ConvertFrom-Json
    if ($json.PSObject.Properties['error']) { throw "JSON-RPC error: $($json.error.message)" }
    $json.result
}
Step 'initialize with the token' { Invoke-Mcp 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'connect-test'; version = '1' } } 1 | Out-Null }
$tools = Step 'tools/list' {
    $t = (Invoke-Mcp 'tools/list' @{} 2).tools
    if (-not ($t | Where-Object name -eq $ToolName)) { throw "no $ToolName among $(@($t).Count) tools" }
    $t
}
Write-Host "      $(@($tools).Count) tools"
Step "tools/call $ToolName" {
    $res = Invoke-Mcp 'tools/call' @{ name = $ToolName; arguments = $ToolArguments } 3
    if ($res.PSObject.Properties['isError'] -and $res.isError) { throw "tool error: $(@($res.content)[0].text)" }
    $text = (@($res.content) | Where-Object type -eq 'text' | ForEach-Object text) -join "`n"
    if ($ExpectEmail -and $text -notmatch [regex]::Escape($ExpectEmail)) { throw 'the expected account email is not in the result' }
} | Out-Null

if ($WriteCheck) {
    # Writes to the signed-in account: one private, dated throwaway trip, deleted again.
    function Invoke-Tool([string] $name, [hashtable] $arguments) {
        $res = Invoke-Mcp 'tools/call' @{ name = $name; arguments = $arguments } (Get-Random -Minimum 100 -Maximum 100000)
        $text = (@($res.content) | Where-Object type -eq 'text' | ForEach-Object text) -join "`n"
        [pscustomobject]@{ IsError = [bool]($res.PSObject.Properties['isError'] -and $res.isError); Text = $text }
    }
    $start = (Get-Date).AddDays(400).ToString('yyyy-MM-dd')
    $tripName = "ZZ Multiplex connect test $(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $tripUuid = $null
    try {
        $tripUuid = Step 'write: create a private throwaway trip' {
            $r = Invoke-Tool 'tripit_create_trip' @{ destination = 'Reykjavik, Iceland'; startDate = $start; endDate = (Get-Date).AddDays(402).ToString('yyyy-MM-dd'); name = $tripName }
            if ($r.IsError) { throw $r.Text }
            if ($r.Text -notmatch '"uuid"\s*:\s*"(?<u>[0-9a-fA-F-]{36})"') { throw "no trip uuid in: $($r.Text.Substring(0, [Math]::Min(200, $r.Text.Length)))" }
            $Matches['u']
        }
        $confirm = Step 'write: delete without confirmation only previews' {
            $r = Invoke-Tool 'tripit_delete_trip' @{ tripUuid = $tripUuid; confirmName = $tripName }
            if ($r.Text -notmatch '"?confirm_token"?\s*[:=]\s*"?(?<t>[A-Za-z0-9_\-]{20,})') { throw "no confirm_token in: $($r.Text.Substring(0, [Math]::Min(300, $r.Text.Length)))" }
            $token = $Matches['t']
            $still = Invoke-Tool 'tripit_get_trip' @{ tripUuid = $tripUuid }
            if ($still.IsError) { throw 'the trip is gone after a preview' }
            $token
        }
        Step 'write: a token for other arguments is refused' {
            $r = Invoke-Tool 'tripit_delete_trip' @{ tripUuid = $tripUuid; confirmName = "$tripName (changed)"; confirm_token = $confirm }
            if (-not $r.IsError) { throw 'a confirm token was accepted for different arguments' }
        } | Out-Null
        Step 'write: delete with the confirm token' {
            $r = Invoke-Tool 'tripit_delete_trip' @{ tripUuid = $tripUuid; confirmName = $tripName; confirm_token = $confirm }
            if ($r.IsError) { throw $r.Text }
            $gone = Invoke-Tool 'tripit_get_trip' @{ tripUuid = $tripUuid }
            if (-not $gone.IsError) { throw 'the trip still exists after a confirmed delete' }
            $script:tripUuid = $null
        } | Out-Null
    }
    finally {
        if ($tripUuid) { Write-Host "NOTE  throwaway trip '$tripName' ($tripUuid) may still exist on the account; delete it by hand." }
    }
}

Step 'refresh token rotation' {
    $new = Invoke-RestMethod $as.token_endpoint -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type = 'refresh_token'; refresh_token = $tokens.refresh_token; client_id = $clientId; resource = $mcpUrl }
    if ($new.refresh_token -eq $tokens.refresh_token) { throw 'refresh token was not rotated' }
    if ($SaveGrantTo) {
        # For a later renewal check: the live refresh token and where to use it. Test servers only.
        # The reuse check below is skipped: reusing a spent refresh token revokes the whole grant
        # (as OAuth 2.1 requires), which would revoke the token being saved.
        @{ client_id = $clientId; refresh_token = $new.refresh_token; token_endpoint = $as.token_endpoint; mcp_url = $mcpUrl } |
            ConvertTo-Json | Set-Content -LiteralPath $SaveGrantTo
        return
    }
    $reuse = Invoke-WebRequest $as.token_endpoint -Method Post -ContentType 'application/x-www-form-urlencoded' -SkipHttpErrorCheck -Body @{
        grant_type = 'refresh_token'; refresh_token = $tokens.refresh_token; client_id = $clientId; resource = $mcpUrl }
    if ($reuse.StatusCode -ne 400) { throw "reusing the old refresh token gave HTTP $($reuse.StatusCode), expected 400" }
    $after = Invoke-WebRequest $as.token_endpoint -Method Post -ContentType 'application/x-www-form-urlencoded' -SkipHttpErrorCheck -Body @{
        grant_type = 'refresh_token'; refresh_token = $new.refresh_token; client_id = $clientId; resource = $mcpUrl }
    if ($after.StatusCode -ne 400) { throw "after a reuse, the newest refresh token still worked (HTTP $($after.StatusCode)); the grant should be revoked" }
} | Out-Null

if ($failures) { exit 1 }
Write-Host 'All connect steps passed.'
