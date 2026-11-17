#Requires -Version 7.2
<#
.SYNOPSIS
    Connects to a running Multiplex endpoint exactly as Claude or ChatGPT would, end to end,
    and calls a tool: discovery from the 401, client registration, PKCE authorization through
    the connect pages, token exchange, MCP initialize, tools/list, a tool call, refresh.
.DESCRIPTION
    For unattended checks of an install. The connect pages need the owner passcode and an
    account the server already holds a signed-in session for (the "Continue as" choice);
    this script never signs in to the site itself. Exit code 0 when every step passes.
.EXAMPLE
    ./scripts/Test-MultiplexConnect.ps1 -BaseUrl http://localhost:5190 -Passcode (./scripts/Install-Multiplex.ps1 -ShowPasscode) -ExpectEmail test@example.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [string] $Slug = 'tripit',
    [Parameter(Mandatory)] [string] $Passcode,
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
function Get-Forms([string] $html) {
    foreach ($m in [regex]::Matches($html, '(?is)<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>')) {
        $action = [regex]::Match($m.Groups['attrs'].Value, '(?i)action\s*=\s*"(?<a>[^"]*)"').Groups['a'].Value
        $fields = [ordered]@{}
        foreach ($i in [regex]::Matches($m.Groups['body'].Value, '(?is)<(input|button)\b[^>]*>')) {
            $tag = $i.Value
            $name = [regex]::Match($tag, '(?i)\bname\s*=\s*"(?<n>[^"]*)"').Groups['n'].Value
            if (-not $name) { continue }
            $value = [System.Net.WebUtility]::HtmlDecode([regex]::Match($tag, '(?i)\bvalue\s*=\s*"(?<v>[^"]*)"').Groups['v'].Value)
            if ($tag -match '(?i)type\s*=\s*"(hidden|text|password)"' -or $tag -notmatch '(?i)^<button') { $fields[$name] = $value }
        }
        [pscustomobject]@{ Action = [System.Net.WebUtility]::HtmlDecode($action); Fields = $fields; Html = $m.Value }
    }
}
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

$code = Step 'authorize through the connect pages (passcode, then continue as the signed-in account)' {
    # A browser-like client that does not follow redirects, so each hop can be inspected.
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = [System.Net.CookieContainer]::new()
    $browser = [System.Net.Http.HttpClient]::new($handler)
    $browser.Timeout = [TimeSpan]::FromSeconds(90)   # "Continue as" re-checks the TripIt session
    $url = $authorizeUrl
    $form = $null
    for ($hop = 0; $hop -lt 12; $hop++) {
        $request = [System.Net.Http.HttpRequestMessage]::new(($form ? [System.Net.Http.HttpMethod]::Post : [System.Net.Http.HttpMethod]::Get), $url)
        if ($form) {
            $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
            foreach ($k in $form.Keys) { $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($k, [string]$form[$k])) }
            $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
        }
        $response = $browser.SendAsync($request).GetAwaiter().GetResult()
        $r = [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Location   = $response.Headers.Location
            Content    = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        $form = $null
        if ($r.StatusCode -in 301, 302, 303, 307) {
            $next = Resolve-Url $url "$($r.Location)"
            if ($next.StartsWith($redirectUri)) {
                $q = [System.Web.HttpUtility]::ParseQueryString(([Uri]$next).Query)
                if ($q['error']) { throw "authorization error: $($q['error']) $($q['error_description'])" }
                if ($q['state'] -ne $state) { throw 'state mismatch' }
                if ($as.PSObject.Properties['authorization_response_iss_parameter_supported'] -and $as.authorization_response_iss_parameter_supported -and $q['iss'] -ne $as.issuer) { throw "iss '$($q['iss'])' != '$($as.issuer)'" }
                return $q['code']
            }
            $url = $next; continue
        }
        $html = "$($r.Content)"
        # A page that hands the browser back via script (e.g. after the live page completes).
        if ($html -match [regex]::Escape($redirectUri) + '[^"''<\s]*') { $url = [System.Net.WebUtility]::HtmlDecode($Matches[0]); continue }
        $forms = @(Get-Forms $html)
        $gate = $forms | Where-Object { $_.Fields.Keys -match '(?i)passcode' } | Select-Object -First 1
        $continue = $forms | Where-Object { $_.Html -match '(?i)continue as' } | Select-Object -First 1
        $picked = $gate ?? $continue
        if (-not $picked) { throw "HTTP $($r.StatusCode) at $url with no passcode or 'Continue as' form" }
        if ($gate) { $key = @($gate.Fields.Keys | Where-Object { $_ -match '(?i)passcode' })[0]; $gate.Fields[$key] = $Passcode }
        if ($continue -and -not $gate) {
            # Include the clicked button's name/value when the form identifies the account that way.
            foreach ($b in [regex]::Matches($continue.Html, '(?is)<button\b[^>]*\bname\s*=\s*"(?<n>[^"]+)"[^>]*\bvalue\s*=\s*"(?<v>[^"]*)"')) { $continue.Fields[$b.Groups['n'].Value] = [System.Net.WebUtility]::HtmlDecode($b.Groups['v'].Value); break }
        }
        $url = Resolve-Url $url $picked.Action
        $form = $picked.Fields
    }
    throw 'too many hops'
}
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
    $reuse = Invoke-WebRequest $as.token_endpoint -Method Post -ContentType 'application/x-www-form-urlencoded' -SkipHttpErrorCheck -Body @{
        grant_type = 'refresh_token'; refresh_token = $tokens.refresh_token; client_id = $clientId; resource = $mcpUrl }
    if ($reuse.StatusCode -ne 400) { throw "reusing the old refresh token gave HTTP $($reuse.StatusCode), expected 400" }
    if ($SaveGrantTo) {
        # For a later renewal check: the live refresh token and where to use it. Test servers only.
        @{ client_id = $clientId; refresh_token = $new.refresh_token; token_endpoint = $as.token_endpoint; mcp_url = $mcpUrl } |
            ConvertTo-Json | Set-Content -LiteralPath $SaveGrantTo
    }
} | Out-Null

if ($failures) { exit 1 }
Write-Host 'All connect steps passed.'
