#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the installed Multiplex server, and its public tunnel when one is configured.
.DESCRIPTION
    Installed beside the published server by Install-Multiplex.ps1 and started at logon by the
    "Multiplex" scheduled task. Reads multiplex.config.json, starts the tunnel first when the
    public address depends on it (a Cloudflare quick tunnel gets a new address on every
    start), then runs the server in the foreground with its configuration in environment
    variables. Stopping the server stops the tunnel. Each step, and any failure with the step
    it happened in, is written to logs\start.log: the task runs hidden, so nothing else shows it.
    Where Tailscale runs unattended for another Windows account, it refuses this one; a Funnel
    set up outside Multiplex is then used if a request through this PC's Tailscale name reaches
    the port, and logs\tunnel.log says the Funnel is managed outside Multiplex, or why the server
    serves on this PC only.
#>
[CmdletBinding()]
param([string] $Root = (Split-Path -Parent $PSCommandPath))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$logDir = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$startLog = Join-Path $logDir 'start.log'
function Write-StartLog([string] $Message) { Add-Content -LiteralPath $startLog -Value "$(Get-Date -Format o) $Message" }
$tunnel = $null
$step = 'loading the install module'

try {
    Import-Module (Join-Path $Root 'Multiplex.Install.psm1') -Force
    Limit-MultiplexLog -Path $startLog
    Write-StartLog "Starting Multiplex as $([Environment]::UserDomainName)\$([Environment]::UserName)."

    $step = 'reading the configuration'
    $configPath = Join-Path $Root 'multiplex.config.json'
    $config = Read-MultiplexConfig -Path $configPath

    $step = "starting the tunnel ($($config.Tunnel))"
    Write-StartLog "Step: $step."
    # Clears the recorded public address first, so only a tunnel that came up sets it, or, when
    # Tailscale refuses this account, a Funnel managed outside Multiplex that reaches the port.
    # That check runs here, before the server listens, so a stand-in briefly holds the port.
    $tunnel = Start-MultiplexTunnel -Config $config -ConfigPath $configPath -LogDir $logDir

    $step = 'starting the server'
    Write-StartLog "Step: $step on http://localhost:$($config.Port); public address: $($config.PublicBaseUrl ?? '(none, this PC only)')."
    foreach ($pair in (Get-MultiplexServerEnvironment -Config $config -Root $Root).GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($pair.Key, $pair.Value, 'Process')
    }
    # Run from the app folder: the server reads appsettings.json from its working directory.
    $appDir = Join-Path $Root 'app'
    $serverLog = Join-Path $logDir 'server.log'
    Limit-MultiplexLog -Path $serverLog   # over ~10 MB it becomes server.log.1; one earlier log is kept
    Push-Location $appDir
    try { & (Join-Path $appDir 'Multiplex.Web.exe') *>> $serverLog }
    finally { Pop-Location }
    $code = $LASTEXITCODE
    Write-StartLog "The server stopped with exit code $code; see $serverLog."
    exit $code
}
catch {
    Write-StartLog "Start-up failed while ${step}: $($_.Exception.Message)"
    throw
}
finally {
    if ($tunnel -and -not $tunnel.HasExited) { Stop-Process -Id $tunnel.Id -Force -ErrorAction SilentlyContinue }
}
