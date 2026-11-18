#Requires -Version 7.2
<#
.SYNOPSIS
    Installs Multiplex on this PC so Claude and ChatGPT (including on a phone) can connect to it.
.DESCRIPTION
    Unattended and idempotent; safe to re-run. It:
      1. publishes the server to %USERPROFILE%\.multiplex\app (not AppData: the Claude desktop
         app keeps a private copy of AppData for everything started from it, which the logon
         task could not see);
      2. writes %USERPROFILE%\.multiplex\multiplex.config.json: port, tunnel, the TripIt accounts
         allowed to connect, and the hash of the owner passcode (generated on first install and
         kept, encrypted for your Windows account only, in passcode.dpapi — see -ShowPasscode);
      3. registers a per-user "Multiplex" scheduled task that starts the server (and tunnel) at
         logon, and starts it now;
      4. checks the endpoint locally and through the public address;
      5. with -ClaudeDesktopBridge, adds a local stdio bridge entry to Claude Desktop's config.
    Adding the connector inside Claude or ChatGPT is a step in their apps; the script prints
    the address to paste.
.EXAMPLE
    ./scripts/Install-Multiplex.ps1 -AllowedAccount you@example.com -Tunnel Tailscale
.EXAMPLE
    ./scripts/Install-Multiplex.ps1 -ShowPasscode
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]] $AllowedAccount,
    [ValidateSet('None', 'Tailscale', 'Cloudflare')] [string] $Tunnel,
    [int] $Port,
    [switch] $ClaudeDesktopBridge,
    [switch] $NoScheduledTask,
    [switch] $NoStart,
    [switch] $ShowPasscode,
    [switch] $NewPasscode,
    [string] $Root)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Multiplex.Install.psm1') -Force

$Root = Get-MultiplexRoot -Root $Root
if ($problem = Get-MultiplexRootProblem -Root $Root) { throw $problem }
$configPath = Join-Path $Root 'multiplex.config.json'
$secretPath = Join-Path $Root 'passcode.dpapi'

if ($ShowPasscode) {
    if (-not (Test-Path -LiteralPath $secretPath)) { throw "No passcode saved at $secretPath. Run the install first." }
    Read-MultiplexPasscodeSecret -Path $secretPath
    return
}

$repo = Split-Path -Parent $PSScriptRoot
$config = Read-MultiplexConfig -Path $configPath
if ($PSBoundParameters.ContainsKey('Port')) { $config.Port = $Port }
if ($PSBoundParameters.ContainsKey('Tunnel')) { $config.Tunnel = $Tunnel }
Clear-MultiplexStalePublicAddress -Config $config   # no tunnel, no public address
# -AllowedAccount replaces the whole allowlist (that is how an account is removed); say what that did.
$previousAccounts = @($config.AllowedAccounts.tripit)
if ($AllowedAccount) { $config.AllowedAccounts.tripit = ConvertTo-MultiplexAccountList -Account $AllowedAccount }
$allowlist = Get-MultiplexAllowlistChange -Previous $previousAccounts -Current @($config.AllowedAccounts.tripit)
Write-Host $allowlist.Summary
if ($allowlist.RemovedNotice) { Write-Warning $allowlist.RemovedNotice }
if (-not @($config.AllowedAccounts.tripit).Count) {
    throw 'No TripIt account is allowed to connect yet. Pass -AllowedAccount <your TripIt email>.'
}

# 1. Publish into a staging folder first: a failed build leaves the running install untouched.
$appDir = Join-Path $Root 'app'
if ($PSCmdlet.ShouldProcess($appDir, 'Publish Multiplex.Web')) {
    $project = Join-Path $repo 'src/Multiplex.Web/Multiplex.Web.csproj'
    Update-MultiplexApp -AppDir $appDir -Publish {
        param($destination)
        dotnet publish $project -c Release -o $destination --nologo -v quiet
        if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE); the installed Multiplex was left as it was." }
    } -StopServer {
        $task = Get-ScheduledTask -TaskName 'Multiplex' -ErrorAction SilentlyContinue
        if ($task -and $task.State -eq 'Running') { Stop-ScheduledTask -TaskName 'Multiplex' }
        Get-Process -Name 'Multiplex.Web' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and (Test-MultiplexPathUnder -Path $_.Path -Folder $appDir) } |
            Stop-Process -Force -PassThru |
            Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    }
    Copy-Item (Join-Path $PSScriptRoot 'Start-Multiplex.ps1'), (Join-Path $PSScriptRoot 'Multiplex.Install.psm1') -Destination $Root -Force
}

# 2. Passcode and configuration
if ($NewPasscode -or -not $config.PasscodeHash -or -not (Test-Path -LiteralPath $secretPath)) {
    $passcode = New-MultiplexPasscode
    Save-MultiplexPasscodeSecret -Passcode $passcode -Path $secretPath
    $config.PasscodeHash = ConvertTo-MultiplexPasscodeHash -Passcode $passcode
    Remove-Variable passcode
    Write-Host "A new owner passcode was generated. Show it with: ./scripts/Install-Multiplex.ps1 -ShowPasscode"
}
Write-MultiplexConfig -Config $config -Path $configPath

# 3. Start at logon, and now
$pwsh = (Get-Command pwsh).Source
$startScript = Join-Path $Root 'Start-Multiplex.ps1'
if (-not $NoScheduledTask -and $PSCmdlet.ShouldProcess('Multiplex', 'Register per-user logon task')) {
    $action = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -WindowStyle Hidden -File `"$startScript`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName 'Multiplex' -Action $action -Trigger $trigger -Settings $settings `
        -Principal $principal -Description 'Multiplex MCP server (and tunnel) for Claude and ChatGPT' -Force | Out-Null
}
if (-not $NoStart -and $PSCmdlet.ShouldProcess('Multiplex', 'Start server')) {
    if ($NoScheduledTask) { Start-Process $pwsh -ArgumentList '-NoProfile', '-File', $startScript -WindowStyle Hidden }
    else { Start-ScheduledTask -TaskName 'Multiplex' }

    # 4. Verify
    $local = "http://localhost:$($config.Port)"
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Seconds 2
        $up = try { (Invoke-WebRequest "$local/healthz" -TimeoutSec 3 -SkipHttpErrorCheck).StatusCode -eq 200 } catch { $false }
        $config = Read-MultiplexConfig -Path $configPath   # Start-Multiplex records the tunnel's address
    } until ($up -or (Get-Date) -gt $deadline)
    if (-not $up) {
        $taskInfo = if (-not $NoScheduledTask) {
            $info = Get-ScheduledTaskInfo -TaskName 'Multiplex' -ErrorAction SilentlyContinue
            if ($info) { "The Multiplex task last ran at $($info.LastRunTime) with result $($info.LastTaskResult)." }
        }
        throw (@("The server did not answer on $local within 90 s.", $taskInfo, (Get-MultiplexStartReport -LogDir (Join-Path $Root 'logs'))) |
            Where-Object { $_ }) -join [Environment]::NewLine
    }

    $public = $config.PublicBaseUrl
    $checks = @(Test-MultiplexEndpoint -BaseUrl $local -ExpectedBaseUrl ($public ?? $local))
    if ($public) { $checks += Test-MultiplexEndpoint -BaseUrl $public }
    $checks | Format-Table -AutoSize | Out-Host
    if ($checks | Where-Object { -not $_.Passed }) { Write-Warning 'Some checks failed; see above.' }
}

# 5. Optional local bridge for Claude Desktop on this PC
if ($ClaudeDesktopBridge) {
    $desktopConfig = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
    if (Set-ClaudeDesktopBridge -ConfigPath $desktopConfig -Name 'tripit' -ServerUrl "http://localhost:$($config.Port)/tripit/mcp") {
        Write-Host 'Claude Desktop entry added; quit and reopen Claude Desktop to load it.'
    }
}

$connector = "$(($config.PublicBaseUrl ?? "http://localhost:$($config.Port)").TrimEnd('/'))/tripit/mcp"
Write-Host ''
Write-Host "Connector address: $connector"
Write-Host '  Claude (web, phone, Desktop): Settings > Connectors > Add custom connector > paste the address.'
Write-Host '  ChatGPT: chatgpt.com/plugins > + > Add custom MCP server > paste the address, OAuth.'
Write-Host '  Claude Code: claude mcp add --transport http --scope user tripit <address>'
if ($config.Tunnel -eq 'Cloudflare') { Write-Warning 'A Cloudflare quick tunnel gets a new address on every start; connectors must be re-added after a restart. Prefer -Tunnel Tailscale.' }
