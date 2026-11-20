#Requires -Version 7.2
# Helpers for installing Multiplex on this PC: configuration, the Microsoft sign-in setup
# check, the Claude Desktop bridge entry, the tunnel, and endpoint checks. Install-Multiplex.ps1
# and Start-Multiplex.ps1 use them; Install-Multiplex.Tests.ps1 tests them.
Set-StrictMode -Version Latest

function Get-MultiplexRoot {
    <#
    The install folder: -Root, else %USERPROFILE%\.multiplex. Not AppData: a packaged app (the
    Claude desktop app is one) gets a private copy of AppData, so an install run from its
    terminal would land where the logon task, a separate process, cannot see it.
    #>
    [CmdletBinding()] param([string] $Root)
    if ($Root) { return $Root }
    return Join-Path ([Environment]::GetFolderPath('UserProfile')) '.multiplex'
}

function Test-MultiplexAppDataRedirected {
    <#
    Whether this process's AppData writes are redirected to a packaged app's private copy, as
    they are for anything started from the Claude desktop app (its terminal, its Run button,
    its agents). Probed directly: create a folder in %LOCALAPPDATA% and look for it in a
    package's LocalCache. Windows does not report such a process as packaged, so asking is not
    enough.
    #>
    [CmdletBinding()] [OutputType([bool])] param()
    if (-not $IsWindows) { return $false }
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    $name = "multiplex-probe-$([guid]::NewGuid().ToString('N'))"
    $probe = Join-Path $local $name
    try {
        New-Item -ItemType Directory -Path $probe -WhatIf:$false -Confirm:$false | Out-Null
        # The wildcard must be in -Path itself: -Filter under a wildcard path matches nothing.
        return [bool](Get-ChildItem -Path (Join-Path $local "Packages\*\LocalCache\Local\$name") -Directory -ErrorAction SilentlyContinue)
    }
    finally { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue -WhatIf:$false -Confirm:$false }
}
function Get-MultiplexRootProblem {
    <# Why -Root cannot be used from this process, or nothing when it can. #>
    [CmdletBinding()] [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Root, [bool] $AppDataRedirected = (Test-MultiplexAppDataRedirected))
    if (-not $AppDataRedirected) { return $null }
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Temp\'), [StringComparison]::OrdinalIgnoreCase)) { return $null }
    foreach ($appData in [Environment]::GetFolderPath('LocalApplicationData'), [Environment]::GetFolderPath('ApplicationData')) {
        if ($full.StartsWith($appData.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            return "This PowerShell was started from a packaged app (for example the Claude desktop app), which keeps a private copy of AppData: an install to $Root would land where the Multiplex logon task cannot see it. Use the default folder, another folder outside AppData, or run the installer from an ordinary PowerShell window."
        }
    }
    return $null
}

function Read-MultiplexConfig {
    [CmdletBinding()] param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ Port = 5190; PublicBaseUrl = $null; Tunnel = 'None' }
    }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
}

function Remove-MultiplexRetiredSetting {
    <#
    Drops what an earlier install left behind that nothing uses any more, with one line for each:
    the TripIt account list (#52: each connection uses the TripIt account signed in while it is
    added) and the owner passcode (#80: people sign in with Microsoft), both its hash in -Config
    and the saved passcode.dpapi in -Root. Changes -Config in place; the caller writes it.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Config, [Parameter(Mandatory)] [string] $Root)
    if ($Config.Contains('AllowedAccounts')) {
        $Config.Remove('AllowedAccounts')
        Write-Host 'The TripIt account list in the configuration is no longer used: each connection uses the TripIt account signed in while it is added. Removing the list.'
    }
    if ($Config.Contains('PasscodeHash')) {
        $Config.Remove('PasscodeHash')
        Write-Host 'The owner passcode in the configuration is no longer used: people sign in with Microsoft. Removing it.'
    }
    $savedPasscode = Join-Path $Root 'passcode.dpapi'
    if (Test-Path -LiteralPath $savedPasscode) {
        Write-Host "The saved owner passcode ($savedPasscode) is no longer used: people sign in with Microsoft. Removing it."
        if ($PSCmdlet.ShouldProcess($savedPasscode, 'Remove the saved owner passcode')) { Remove-Item -LiteralPath $savedPasscode -Force }
    }
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
    <#
    The environment variables Start-Multiplex.ps1 sets before launching the server. An account
    list or a passcode hash left in the configuration by an earlier install is not passed on:
    there is none any more. Microsoft sign-in (Multiplex__Identity__*) is neither set nor cleared
    here: the owner sets it for their Windows account and the server inherits it from there.
    #>
    [CmdletBinding()] [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Config, [Parameter(Mandatory)] [string] $Root)
    return [ordered]@{
        ASPNETCORE_ENVIRONMENT   = 'Production'
        # "localhost" makes Kestrel listen on both 127.0.0.1 and ::1; binding only 127.0.0.1 costs
        # every client that resolves localhost to ::1 first a ~2 s connection retry.
        ASPNETCORE_URLS          = "http://localhost:$($Config.Port)"
        Multiplex__DataPath      = Join-Path $Root 'data'
        Multiplex__PublicBaseUrl = ($Config.PublicBaseUrl ?? "http://localhost:$($Config.Port)").TrimEnd('/')
    }
}

# Microsoft sign-in, which the owner sets for their own Windows account (User scope) and the
# Multiplex task inherits; never written by the installer, never committed. Each with the
# expression the owner runs to supply it: typed at a prompt, so the line runs as shown and the
# value never lands in shell history (the client secret at a masked prompt).
$script:SignInSettings = [ordered]@{
    'Multiplex__Identity__Microsoft__ClientId'     = "(Read-Host 'Application (client) ID')"
    'Multiplex__Identity__Microsoft__ClientSecret' = "(Read-Host -MaskInput 'Client secret')"
    'Multiplex__Identity__AllowedEmails'           = "(Read-Host 'Allowed emails, separated by ;')"
}

function Test-MultiplexEnvironmentVariable {
    <#
    Whether environment variable -Name is set for this process or for the Windows account (User
    scope). Only asks whether it exists: its value is never read.
    #>
    [CmdletBinding()] [OutputType([bool])] param([Parameter(Mandatory)] [string] $Name)
    if (Test-Path -LiteralPath "Env:$Name") { return $true }
    if (-not $IsWindows) { return $false }
    $userEnvironment = Get-Item -LiteralPath 'HKCU:\Environment' -ErrorAction SilentlyContinue
    return [bool]($userEnvironment -and ($userEnvironment.GetValueNames() -contains $Name))
}

function Get-MultiplexSignInSetupWarning {
    <#
    What the owner still has to do before anyone outside this PC can sign in, or nothing once
    Microsoft sign-in is set up: the settings set neither for this process nor for the Windows
    account, the line that sets each for the account, and the restart of the Multiplex task that
    picks them up. Only tests whether each is set; never reads or prints a value.
    #>
    [CmdletBinding()] [OutputType([string])] param()
    $missing = @($script:SignInSettings.Keys | Where-Object { -not (Test-MultiplexEnvironmentVariable -Name $_) })
    if (-not $missing) { return $null }
    $lines = @(
        "Microsoft sign-in is not set up, so nobody outside this PC can sign in until it is: $($missing -join ', ') " +
            "$(if ($missing.Count -eq 1) { 'is' } else { 'are' }) not set. Set each for your Windows account, in PowerShell 7:"
        foreach ($name in $missing) { "  [Environment]::SetEnvironmentVariable('$name', $($script:SignInSettings[$name]), 'User')" }
        'Then run this installer again: it restarts Multiplex, which then picks them up:'
        '  ./scripts/Install-Multiplex.ps1')
    return $lines -join [Environment]::NewLine
}

function Get-MultiplexMicrosoftRedirectUri {
    <# The redirect URI to register for Microsoft sign-in: <public address>/signin-microsoft, or nothing without a public address. #>
    [CmdletBinding()] [OutputType([string])] param([string] $PublicBaseUrl)
    if (-not $PublicBaseUrl) { return $null }
    return "$($PublicBaseUrl.TrimEnd('/'))/signin-microsoft"
}

function Set-ClaudeDesktopBridge {
    <#
    Adds (or updates) a stdio bridge entry for a Multiplex endpoint in Claude Desktop's config,
    keeping every other entry. mcp-remote opens the system browser for the same sign-in page.
    Claude Desktop must be restarted to pick it up. The first change keeps the original file as
    <config>.bak (a later change never overwrites it), and the new file is written beside the
    old one and then moved over it, so a failed write never leaves a half-written config.
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
        $backup = "$ConfigPath.bak"
        if ((Test-Path -LiteralPath $ConfigPath) -and -not (Test-Path -LiteralPath $backup)) {
            Copy-Item -LiteralPath $ConfigPath -Destination $backup
        }
        $full = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($ConfigPath)
        $tmp = "$full.tmp"
        try {
            $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $tmp -ErrorAction Stop
            [IO.File]::Move($tmp, $full, $true)   # one replace: the config is old or new, never partial
        }
        finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
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

$script:TailscaleTimeoutSeconds = 20
# How tailscale words its refusal of every Windows account but the one it runs unattended for.
$script:TailscaleServerModePattern = 'server mode \("([^"]+)"\)'
# Tailscale's own DNS resolver (MagicDNS): the same address on every PC in every tailnet.
$script:MagicDnsServer = '100.100.100.100'

function Invoke-TailscaleCommand {
    <#
    Runs tailscale with -Arguments and says what happened; never throws. ExitCode is $null when
    it could not be run or did not finish within -TimeoutSeconds (it is then stopped): `tailscale
    funnel` prints an enable link and waits indefinitely while Funnel still needs enabling.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Tailscale,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [int] $TimeoutSeconds = $script:TailscaleTimeoutSeconds)
    $result = [pscustomobject]@{
        Command = "tailscale $($Arguments -join ' ')"; ExitCode = $null; TimedOut = $false
        TimeoutSeconds = $TimeoutSeconds; Output = ''; ErrorOutput = ''; RunError = $null
    }
    $start = [System.Diagnostics.ProcessStartInfo]::new($Tailscale)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    try { $process = [System.Diagnostics.Process]::Start($start) }
    catch { $result.RunError = $_.Exception.GetBaseException().Message; return $result }
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($process.WaitForExit($TimeoutSeconds * 1000)) { $process.WaitForExit(); $result.ExitCode = $process.ExitCode }
        else {
            $result.TimedOut = $true
            try { $process.Kill($true) } catch { Write-Verbose "Could not stop $($result.Command): $($_.Exception.Message)" }
        }
        # Whatever it printed before it ended or was stopped (an enable link, an error).
        if ([System.Threading.Tasks.Task]::WaitAll(@($stdout, $stderr), 5000)) {
            $result.Output = $stdout.Result.Trim()
            $result.ErrorOutput = $stderr.Result.Trim()
        }
    }
    finally { $process.Dispose() }
    return $result
}

function Get-TailscaleProblem {
    <# A plain account of a tailscale command that did not do its job, in its own words, and what to do. #>
    [CmdletBinding()] [OutputType([string])] param([Parameter(Mandatory)] [psobject] $Result)
    $said = (@($Result.ErrorOutput, $Result.Output) | Where-Object { $_ }) -join ' '
    $words = if ($said) { " Tailscale said: $said" } else { ' Tailscale said nothing.' }
    if ($Result.RunError) { return "Tailscale could not be run ($($Result.RunError)). Is it installed?" }
    if ($said -match $script:TailscaleServerModePattern) {
        $owner = $Matches[1] -replace '\\\\', '\'
        $me = "$([Environment]::UserDomainName)\$([Environment]::UserName)"
        return "Tailscale on this PC runs unattended for the Windows account $owner and refuses every other " +
            "account, including $me, which runs Multiplex. Either sign in to Windows as $owner, open Tailscale " +
            "and turn off 'Run unattended', then sign in as $me, open Tailscale and turn 'Run unattended' on; " +
            "or install and run Multiplex while signed in as $owner.$words"
    }
    if ($Result.TimedOut) {
        return "$($Result.Command) did not finish within $($Result.TimeoutSeconds) s and was stopped; it waits " +
            "like this when Funnel still needs enabling for this PC in the Tailscale admin console.$words"
    }
    if ($said -match 'not enabled') { return "Funnel is not enabled for this PC in the Tailscale admin console.$words" }
    if ($said -match 'failed to connect to local tailscaled|is Tailscale running') {
        return "Tailscale is not running on this PC; start it and sign in.$words"
    }
    return "$($Result.Command) failed with exit code $($Result.ExitCode).$words"
}

function ConvertFrom-TailscaleJson {
    <# A tailscale command's JSON as a dictionary, or a reason it is not one. Never throws. #>
    [CmdletBinding()] param([Parameter(Mandatory)] [psobject] $Result)
    if (-not $Result.Output) { return [pscustomobject]@{ Value = $null; Problem = "$($Result.Command) printed nothing." } }
    try { $value = $Result.Output | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch {
        $start = $Result.Output.Substring(0, [Math]::Min(200, $Result.Output.Length))
        return [pscustomobject]@{ Value = $null; Problem = "$($Result.Command) printed something that is not JSON: $start" }
    }
    [pscustomobject]@{ Value = $value; Problem = $null }
}

function Get-DictionaryValue {
    <# The value at -Path in nested dictionaries (parsed JSON), or $null where any step is missing. #>
    [CmdletBinding()] param($Dictionary, [Parameter(Mandatory)] [string[]] $Path)
    $value = $Dictionary
    foreach ($key in $Path) {
        if ($value -isnot [System.Collections.IDictionary] -or -not $value.Contains($key)) { return $null }
        $value = $value[$key]
    }
    return $value
}

function Get-TailscaleDnsName {
    <#
    This PC's Tailscale DNS name (without the trailing dot) when Tailscale is connected, else
    Problem says why, including when Tailscale refuses this Windows account; Refused is true
    then (it runs unattended for another account). Never throws.
    #>
    [CmdletBinding()] param([Parameter(Mandatory)] [string] $Tailscale)
    $run = Invoke-TailscaleCommand -Tailscale $Tailscale -Arguments 'status', '--json'
    if ($run.ExitCode -ne 0) {
        return [pscustomobject]@{
            DnsName = $null; Problem = Get-TailscaleProblem -Result $run
            Refused = "$($run.ErrorOutput) $($run.Output)" -match $script:TailscaleServerModePattern
        }
    }
    $json = ConvertFrom-TailscaleJson -Result $run
    if ($json.Problem) { return [pscustomobject]@{ DnsName = $null; Problem = $json.Problem; Refused = $false } }
    $state = Get-DictionaryValue $json.Value 'BackendState'
    $problem = switch ($state) {
        'NeedsLogin' { 'Tailscale is not signed in on this PC; open Tailscale and sign in.' }
        'NeedsMachineAuth' { 'This PC is waiting for approval in the Tailscale admin console.' }
        'Stopped' { 'Tailscale is turned off on this PC; open Tailscale and connect.' }
    }
    if (-not $problem -and $state -and $state -ne 'Running') { $problem = "Tailscale is not connected on this PC (state $state)." }
    $name = "$(Get-DictionaryValue $json.Value 'Self', 'DNSName')".TrimEnd('.')
    if (-not $problem -and -not $name) { $problem = "Tailscale did not report this PC's DNS name (is MagicDNS on for the tailnet?)." }
    [pscustomobject]@{ DnsName = ($problem ? $null : $name); Problem = $problem; Refused = $false }
}

function Invoke-TailscaleFunnel {
    <#
    Opens -Port to the internet with `tailscale funnel --bg` and confirms it: Started only when the
    command exits 0 and `tailscale funnel status --json` shows https on -DnsName open to the
    internet. Otherwise Problem says why, in Tailscale's own words. Never throws.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Tailscale, [Parameter(Mandatory)] [int] $Port, [Parameter(Mandatory)] [string] $DnsName)
    $run = Invoke-TailscaleCommand -Tailscale $Tailscale -Arguments 'funnel', '--bg', '--yes', "$Port"
    if ($run.ExitCode -ne 0) { return [pscustomobject]@{ Started = $false; Problem = Get-TailscaleProblem -Result $run } }
    $status = Invoke-TailscaleCommand -Tailscale $Tailscale -Arguments 'funnel', 'status', '--json'
    if ($status.ExitCode -ne 0) {
        return [pscustomobject]@{ Started = $false; Problem = "Funnel could not be confirmed. $(Get-TailscaleProblem -Result $status)" }
    }
    $json = ConvertFrom-TailscaleJson -Result $status
    $open = Get-DictionaryValue $json.Value 'AllowFunnel'
    $hostPort = "${DnsName}:443"   # Tailscale keys Funnel by "<name>:<port>"
    $confirmed = $open -is [System.Collections.IDictionary] -and
        @($open.Keys | Where-Object { $_ -eq $hostPort -and $open[$_] -eq $true }).Count -gt 0
    if (-not $confirmed) {
        $said = if ($run.Output) { " Tailscale said: $($run.Output)" } else { '' }
        return [pscustomobject]@{
            Started = $false
            Problem = "$($run.Command) exited 0, but $($status.Command) does not show https://$DnsName open to the internet ($($json.Problem ?? $status.Output)).$said"
        }
    }
    [pscustomobject]@{ Started = $true; Problem = $null }
}

function Get-TailscaleDnsNameFromAdapter {
    <#
    This PC's Tailscale DNS name, learned without tailscale itself, which refuses every Windows
    account but the one it runs unattended for. The Tailscale network adapter carries the
    tailnet as its DNS suffix (<tailnet>.ts.net), and MagicDNS, asked which name has that
    adapter's address, answers with this PC's name in the tailnet. That is Tailscale's own name
    for the PC; <host name>.<tailnet> is not always it, because a PC renamed in the Tailscale
    admin console, or given "-1" when its name was taken, keeps its Windows host name. MagicDNS
    is asked directly: Windows answers that question for its own addresses from the host name.
    Else Problem says why there is no name. Never throws.
    #>
    [CmdletBinding()] param()
    function New-Result([string] $DnsName, [string] $Problem) { [pscustomobject]@{ DnsName = $DnsName ? $DnsName : $null; Problem = $Problem ? $Problem : $null } }
    try {
        $adapter = Get-DnsClient -ErrorAction Stop | Where-Object { "$($_.ConnectionSpecificSuffix)" -like '*.ts.net' } | Select-Object -First 1
        if (-not $adapter) {
            return New-Result -Problem 'No network adapter on this PC has a Tailscale (*.ts.net) DNS suffix, so its Tailscale name is unknown (is Tailscale connected, with MagicDNS on for the tailnet?).'
        }
        $tailnet = "$($adapter.ConnectionSpecificSuffix)".TrimEnd('.')
        $address = Get-NetIPAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty IPAddress
        if (-not $address) { return New-Result -Problem "The Tailscale network adapter ($($adapter.InterfaceAlias)) has no IPv4 address, so this PC's Tailscale name is unknown." }
        try {
            $record = Resolve-DnsName -Name $address -Type PTR -Server $script:MagicDnsServer -DnsOnly -QuickTimeout -ErrorAction Stop |
                Where-Object { $_.Type -eq 'PTR' } | Select-Object -First 1
        }
        catch { return New-Result -Problem "MagicDNS ($script:MagicDnsServer) did not name this PC's Tailscale address ${address}: $($_.Exception.Message)" }
        $name = if ($record) { "$($record.NameHost)".TrimEnd('.') }
        if (-not $name) { return New-Result -Problem "MagicDNS ($script:MagicDnsServer) has no name for this PC's Tailscale address $address." }
        if (-not $name.EndsWith(".$tailnet", [StringComparison]::OrdinalIgnoreCase)) {
            return New-Result -Problem "MagicDNS names this PC's Tailscale address $address $name, which is not in the tailnet $tailnet of the Tailscale network adapter."
        }
        return New-Result -DnsName $name
    }
    catch { return New-Result -Problem "This PC's Tailscale name could not be read from its network adapter: $($_.Exception.Message)" }
}

function Test-UrlReachesLocalPort {
    <#
    Whether a request to -Url arrives at -Port on this PC. Start-up asks this before the server
    listens, so a stand-in listens on 127.0.0.1:<Port> (where `tailscale funnel <port>` sends
    requests) and answers every request with a one-time token; only a response carrying that
    token counts. Requesting /healthz and expecting 200 "Healthy" would prove less: nothing
    answers the port yet (Funnel would say 502), and "Healthy" is what any ASP.NET Core health
    check says, Multiplex on another PC in the same tailnet included. Reached, else Problem says
    why not. Gives up after -TimeoutSeconds, never throws, and leaves the port free.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Url, [Parameter(Mandatory)] [int] $Port, [int] $TimeoutSeconds = 10)
    function New-Result([string] $Problem) { [pscustomobject]@{ Reached = -not $Problem; Problem = $Problem ? $Problem : $null } }
    $token = [guid]::NewGuid().ToString('N')
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $listener.Start() }
    catch {
        $why = $_.Exception.GetBaseException()
        $why = ($why -is [System.Net.Sockets.SocketException] -and $why.SocketErrorCode -eq 'AddressAlreadyInUse') ?
            "port $Port on this PC is already in use" : "port $Port on this PC could not be opened ($($why.Message))"
        return New-Result "Could not check whether $Url reaches port $Port on this PC: $why."
    }
    # Straight to the name, never through a web proxy: the request is meant to take Tailscale's path.
    $http = [System.Net.Http.HttpClient]::new([System.Net.Http.SocketsHttpHandler]@{ UseProxy = $false })
    $http.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    try {
        try {
            $request = $http.GetAsync($Url)   # completes once the whole answer is read, or on the timeout
            $accept = $listener.AcceptTcpClientAsync()
            while (-not $request.IsCompleted) {
                if (-not $accept.Wait(100)) { continue }
                $client = $accept.Result
                try {
                    $client.ReceiveTimeout = 5000
                    $client.SendTimeout = 5000
                    $stream = $client.GetStream()
                    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
                    while ($reader.ReadLine()) { }   # the request line and headers, up to the blank line
                    $answer = [System.Text.Encoding]::ASCII.GetBytes(
                        "HTTP/1.1 200 OK`r`nContent-Type: text/plain`r`nContent-Length: $($token.Length)`r`nConnection: close`r`n`r`n$token")
                    $stream.Write($answer, 0, $answer.Length)
                }
                catch { Write-Verbose "The stand-in on port $Port could not answer a request: $($_.Exception.Message)" }
                finally { $client.Dispose() }
                $accept = $listener.AcceptTcpClientAsync()
            }
        }
        finally { $listener.Stop() }
        if ($request.IsFaulted) { return New-Result "$Url could not be reached: $($request.Exception.GetBaseException().Message)" }
        if ($request.IsCanceled) { return New-Result "$Url did not answer within $TimeoutSeconds s." }
        $response = $request.Result
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$response.StatusCode -eq 200 -and $body -eq $token) { return New-Result }
        return New-Result "$Url answered HTTP $([int]$response.StatusCode), but not from port $Port on this PC."
    }
    catch { return New-Result "Could not check whether $Url reaches port $Port on this PC: $($_.Exception.GetBaseException().Message)" }
    finally { $http.Dispose() }
}

function Find-TailscaleFunnelOutsideMultiplex {
    <#
    For when Tailscale refuses this Windows account, so Multiplex can neither start a Funnel nor
    ask about one: the Tailscale name of a Funnel set up outside Multiplex that already reaches
    -Port on this PC, else Problem says why there is none. The check goes through the name over
    the tailnet, so it cannot tell a Funnel (open to the internet) from a Serve (tailnet only);
    seeing that needs the tailscale command this account is refused. Never throws.
    #>
    [CmdletBinding()] param([Parameter(Mandatory)] [int] $Port)
    $self = Get-TailscaleDnsNameFromAdapter
    $problem = $self.Problem
    if (-not $problem) {
        $check = Test-UrlReachesLocalPort -Url "https://$($self.DnsName)/healthz" -Port $Port
        if (-not $check.Reached) { $problem = $check.Problem }
    }
    if ($problem) { return [pscustomobject]@{ DnsName = $null; Problem = "No Funnel set up outside Multiplex was found for port $Port either: $problem" } }
    [pscustomobject]@{ DnsName = $self.DnsName; Problem = $null }
}

function Start-MultiplexTunnel {
    <#
    Starts the configured tunnel and records its public address in the config. The recorded
    address is cleared (and the config written) first, so only a tunnel that actually came up
    sets it: a stale address would point every OAuth discovery document at a dead host. When
    Tailscale refuses this Windows account, a Funnel set up outside Multiplex is used if it
    already reaches the port, and logs\tunnel.log says so. Returns the tunnel process when this
    function owns one (Cloudflare), else nothing.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Config,
        [Parameter(Mandatory)] [string] $ConfigPath,
        [Parameter(Mandatory)] [string] $LogDir)
    if ($Config.PublicBaseUrl) {
        $Config.PublicBaseUrl = $null
        Write-MultiplexConfig -Config $Config -Path $ConfigPath
    }
    switch ($Config.Tunnel) {
        'Cloudflare' {
            $log = Join-Path $LogDir 'cloudflared.log'
            Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
            $tunnel = Start-Process -FilePath 'cloudflared' -PassThru -WindowStyle Hidden `
                -ArgumentList @('tunnel', '--no-autoupdate', '--url', "http://127.0.0.1:$($Config.Port)", '--logfile', $log)
            $deadline = (Get-Date).AddSeconds(60)
            $url = $null
            while (-not $url -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
                if (Test-Path -LiteralPath $log) { $url = Get-QuickTunnelUrl -Lines (Get-Content -LiteralPath $log) }
            }
            if (-not $url) {
                Stop-Process -Id $tunnel.Id -Force -ErrorAction SilentlyContinue
                throw "cloudflared did not report a public address within 60 s; see $log"
            }
            $Config.PublicBaseUrl = $url
            Write-MultiplexConfig -Config $Config -Path $ConfigPath
            Write-Host "Public address (changes on every start): $url"
            return $tunnel
        }
        'Tailscale' {
            # Never let Tailscale keep the server from starting: whatever goes wrong is logged and
            # said, and the server serves on this PC only.
            $tunnelLog = Join-Path $LogDir 'tunnel.log'
            $problem = $null
            $outsideNote = $null
            try {
                $ts = (Get-Command tailscale -ErrorAction SilentlyContinue)?.Source ?? 'C:\Program Files\Tailscale\tailscale.exe'
                $self = Get-TailscaleDnsName -Tailscale $ts
                if ($self.Refused) {
                    # Tailscale runs unattended for another Windows account and refuses this one,
                    # so Multiplex can neither start a Funnel nor ask about one. A Funnel set up
                    # outside Multiplex (for example as LocalSystem, which Tailscale always admits)
                    # may already serve the port: use its name once a request through it is seen
                    # to arrive here, and say the Funnel is not Multiplex's (#57).
                    $outside = Find-TailscaleFunnelOutsideMultiplex -Port $Config.Port
                    $dnsName = $outside.DnsName
                    if ($dnsName) {
                        $outsideNote = "Tailscale refuses this Windows account, but https://$dnsName already reaches port $($Config.Port) " +
                            'on this PC, so Multiplex uses it as its public address. That Funnel is managed outside Multiplex: ' +
                            'Multiplex neither starts nor stops it, and cannot see whether it is open to the internet or only to ' +
                            "the tailnet. $($self.Problem)"
                    }
                    else { $problem = "$($self.Problem) $($outside.Problem)" }
                }
                else {
                    $dnsName = $self.DnsName
                    $problem = $self.Problem ?? (Invoke-TailscaleFunnel -Tailscale $ts -Port $Config.Port -DnsName $dnsName).Problem
                }
            }
            catch { $problem = "Starting Tailscale Funnel failed unexpectedly: $($_.Exception.Message)" }
            if ($problem) {
                Add-Content -LiteralPath $tunnelLog "$(Get-Date -Format o) Tailscale Funnel is not available, so Multiplex serves on this PC only. $problem"
                Write-Warning "Tailscale Funnel is not available; serving on this PC only. $problem"
                return
            }
            $Config.PublicBaseUrl = "https://$dnsName"
            Write-MultiplexConfig -Config $Config -Path $ConfigPath
            if ($outsideNote) {
                Add-Content -LiteralPath $tunnelLog "$(Get-Date -Format o) $outsideNote"
                Write-Host "Public address: $($Config.PublicBaseUrl) (a Tailscale Funnel managed outside Multiplex)"
            }
            else { Write-Host "Public address: $($Config.PublicBaseUrl)" }
        }
    }
}

function Get-MultiplexStartReport {
    <#
    What the logs say about a start, for the installer to show when the server does not answer:
    the end of the start log (the step start-up reached and how it ended), the tunnel log, and
    the end of the server log. Says so when nothing was logged at all.
    #>
    [CmdletBinding()] [OutputType([string])]
    param([Parameter(Mandatory)] [string] $LogDir, [int] $Tail = 10)
    $sections = foreach ($name in 'start.log', 'tunnel.log', 'server.log') {
        $path = Join-Path $LogDir $name
        $lines = @(Get-Content -LiteralPath $path -Tail $Tail -ErrorAction SilentlyContinue)
        if ($lines) { "Last lines of ${path}:"; $lines | ForEach-Object { "  $_" } }   # may include earlier starts
    }
    if (-not $sections) { return "Nothing was logged in ${LogDir}: start-up ended before it could write its log." }
    return $sections -join [Environment]::NewLine
}

function Clear-MultiplexStalePublicAddress {
    <#
    The installer's half of keeping the public address honest: with no tunnel there is no public
    address, so a value left from an earlier tunnel is dropped.
    #>
    [CmdletBinding()] param([Parameter(Mandatory)] [System.Collections.IDictionary] $Config)
    if ($Config.Tunnel -eq 'None' -and $Config.PublicBaseUrl) { $Config.PublicBaseUrl = $null }
}

function Limit-MultiplexLog {
    <#
    Rotates a log at server start: when it is larger than -MaxBytes it becomes <log>.1 (replacing
    the previous one), so at most one earlier log is kept. A failure only warns: rotating must
    never keep the server from starting.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path, [long] $MaxBytes = 10MB)
    try {
        $item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
        if (-not $item -or $item.Length -le $MaxBytes) { return }
        Move-Item -LiteralPath $Path -Destination "$Path.1" -Force -ErrorAction Stop
    }
    catch { Write-Warning "Could not rotate $Path ($($_.Exception.Message)); it keeps growing for now." }
}

function Test-MultiplexPathUnder {
    <# Whether -Path is inside -Folder (not merely a sibling whose name starts the same, like app.new). #>
    [CmdletBinding()] [OutputType([bool])]
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Folder)
    $Path.StartsWith($Folder.TrimEnd('\', '/') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Update-MultiplexApp {
    <#
    Replaces the published server in -AppDir without leaving it broken. -Publish (given the
    staging folder) builds beside it first, so a failed build changes nothing and leaves the
    running server alone; only then does -StopServer run and the folders swap. An earlier run
    that was interrupted mid-swap is repaired first, so re-running is always safe.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $AppDir,
        [Parameter(Mandatory)] [scriptblock] $Publish,
        [scriptblock] $StopServer = {})
    $AppDir = $AppDir.TrimEnd('\', '/')
    $staging = "$AppDir.new"
    $previous = "$AppDir.old"
    if (-not $PSCmdlet.ShouldProcess($AppDir, 'Publish Multiplex.Web and replace the installed server')) { return }

    if (-not (Test-Path -LiteralPath $AppDir) -and (Test-Path -LiteralPath $previous)) {
        Move-Item -LiteralPath $previous -Destination $AppDir -ErrorAction Stop   # an interrupted swap: restore the last good install
    }
    foreach ($leftover in $staging, $previous) {
        if (Test-Path -LiteralPath $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force -ErrorAction Stop }
    }

    try { & $Publish $staging }
    catch {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
    if (-not (Test-Path -LiteralPath $staging)) { throw "Publishing produced no $staging; the installed server was left as it was." }

    & $StopServer
    if (Test-Path -LiteralPath $AppDir) {
        # A stopped process can hold its files for a moment after it exits.
        for ($attempt = 1; ; $attempt++) {
            try { Move-Item -LiteralPath $AppDir -Destination $previous -ErrorAction Stop; break }
            catch { if ($attempt -ge 10) { throw }; Start-Sleep -Milliseconds 500 }
        }
    }
    try { Move-Item -LiteralPath $staging -Destination $AppDir -ErrorAction Stop }
    catch {
        if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $AppDir -ErrorAction Stop }
        throw
    }
    Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction SilentlyContinue
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

Export-ModuleMember -Function *-Multiplex*, Set-ClaudeDesktopBridge, Get-QuickTunnelUrl
