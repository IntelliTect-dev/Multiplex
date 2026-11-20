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
    and ends there, saying so: the token exchange and the MCP calls after it need the code that
    sign-in ends with. A server where Microsoft sign-in is not set up answers HTTP 503, which
    fails the check. Exit code 0 when every step it can run passes.
.NOTES
    -Passcode and $env:MULTIPLEX_PASSCODE were removed in #80: there is no owner passcode any more.
    -ExpectEmail, -ToolName, -ToolArguments, -WriteCheck and -SaveGrantTo were removed in #80 too:
    the steps they drove (a tool call, the write check, refresh) need a person's sign-in.
.EXAMPLE
    ./scripts/Test-MultiplexConnect.ps1 -BaseUrl http://localhost:5190
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [string] $Slug = 'tripit',
    [string] $ClientMetadataUrl)
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
