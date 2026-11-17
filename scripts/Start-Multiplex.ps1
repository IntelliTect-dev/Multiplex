#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the installed Multiplex server, and its public tunnel when one is configured.
.DESCRIPTION
    Installed beside the published server by Install-Multiplex.ps1 and started at logon by the
    "Multiplex" scheduled task. Reads multiplex.config.json, starts the tunnel first when the
    public address depends on it (a Cloudflare quick tunnel gets a new address on every
    start), then runs the server in the foreground with its configuration in environment
    variables. Stopping the server stops the tunnel.
#>
[CmdletBinding()]
param([string] $Root = (Split-Path -Parent $PSCommandPath))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $Root 'Multiplex.Install.psm1') -Force

$configPath = Join-Path $Root 'multiplex.config.json'
$config = Read-MultiplexConfig -Path $configPath
$logDir = Join-Path $Root 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$tunnel = $null

try {
    switch ($config.Tunnel) {
        'Cloudflare' {
            $log = Join-Path $logDir 'cloudflared.log'
            Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
            $tunnel = Start-Process -FilePath 'cloudflared' -PassThru -WindowStyle Hidden `
                -ArgumentList @('tunnel', '--no-autoupdate', '--url', "http://127.0.0.1:$($config.Port)", '--logfile', $log)
            $deadline = (Get-Date).AddSeconds(60)
            $url = $null
            while (-not $url -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
                if (Test-Path -LiteralPath $log) { $url = Get-QuickTunnelUrl -Lines (Get-Content -LiteralPath $log) }
            }
            if (-not $url) { throw "cloudflared did not report a public address within 60 s; see $log" }
            $config.PublicBaseUrl = $url
            Write-MultiplexConfig -Config $config -Path $configPath
            Write-Host "Public address (changes on every start): $url"
        }
        'Tailscale' {
            $ts = (Get-Command tailscale -ErrorAction SilentlyContinue)?.Source ?? 'C:\Program Files\Tailscale\tailscale.exe'
            # When Funnel is not enabled for the tailnet, `tailscale funnel` prints an enable link and
            # waits indefinitely; never let that keep the server from starting.
            $job = Start-Job { param($exe, $port) & $exe funnel --bg --yes "$port" 2>&1 } -ArgumentList $ts, $config.Port
            $finished = Wait-Job $job -Timeout 20
            $output = (Receive-Job $job) -join "`n"
            Remove-Job $job -Force
            if (-not $finished -or $output -match 'not enabled') {
                Add-Content -LiteralPath (Join-Path $logDir 'tunnel.log') "$(Get-Date -Format o) Tailscale Funnel is not available: $output"
                Write-Warning "Tailscale Funnel is not enabled for this tailnet; serving locally only. $output"
            } else {
                $dns = (& $ts status --json | ConvertFrom-Json).Self.DNSName.TrimEnd('.')
                $config.PublicBaseUrl = "https://$dns"
                Write-MultiplexConfig -Config $config -Path $configPath
                Write-Host "Public address: $($config.PublicBaseUrl)"
            }
        }
    }

    foreach ($pair in (Get-MultiplexServerEnvironment -Config $config -Root $Root).GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($pair.Key, $pair.Value, 'Process')
    }
    # Run from the app folder: the server reads appsettings.json from its working directory.
    $appDir = Join-Path $Root 'app'
    Push-Location $appDir
    try { & (Join-Path $appDir 'Multiplex.Web.exe') *>> (Join-Path $logDir 'server.log') }
    finally { Pop-Location }
    exit $LASTEXITCODE
}
finally {
    if ($tunnel -and -not $tunnel.HasExited) { Stop-Process -Id $tunnel.Id -Force -ErrorAction SilentlyContinue }
}
