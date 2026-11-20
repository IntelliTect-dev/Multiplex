#Requires -Version 7.2
<#
.SYNOPSIS
Deploys Multiplex to a Linux server over SSH, reachable from your tailnet only.

.DESCRIPTION
Builds the server on this PC, uploads it, and runs it on the server as two Docker containers
(deploy/linux): the server itself, with Google Chrome and a virtual display for the sign-in
browsers, and a Tailscale device named "multiplex" that serves it to your tailnet over HTTPS at
https://multiplex.<your-tailnet>.ts.net. Nothing is reachable from the internet.

Run it again to update the server. Re-running is safe: until the new image has built and the
Tailscale device is up, the running server is left as it was.

The server needs an x64 processor, Docker with the Compose plugin, and must accept this PC's SSH key
for -Target; the SSH user must be allowed to run docker. This PC needs the .NET SDK, ssh and scp.

The owner passcode is the one this PC already keeps (show it with ./scripts/Install-Multiplex.ps1
-ShowPasscode); if there is none, one is made and kept the same way. Only its hash goes to the server.

On the first deploy the Tailscale device prints a link: open it and sign in to Tailscale to add the
device to your tailnet. The tailnet must issue HTTPS certificates (Tailscale admin console, DNS page).

.PARAMETER Target
The server's SSH destination: user@host (for example me@192.168.1.20) or an alias from your SSH config.
For an IPv6 address, or a port other than 22, use an alias.

.EXAMPLE
./scripts/Deploy-MultiplexLinux.ps1 -Target me@192.168.1.20
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Target)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Multiplex.Install.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Multiplex.Linux.psm1') -Force

if (-not (Test-MultiplexSshTarget $Target)) { throw "'$Target' is not an SSH destination: give user@host or an SSH config alias." }
$sshOptions = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', '-o', 'ServerAliveInterval=30')
$ssh = Get-MultiplexTool ssh
$scp = Get-MultiplexTool scp

$remote = {
    param([string] $Script)
    $call = Get-MultiplexRemoteInvocation -Script $Script
    $output = $call.Input | & $ssh @sshOptions $Target $call.Command 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output | ForEach-Object { "$_" }) -join "`n" }
}
$upload = {
    param([string] $LocalFile, [string] $RemotePath)
    & $scp @sshOptions -q $LocalFile "${Target}:$RemotePath"
    if ($LASTEXITCODE) { throw "scp could not upload to $Target (exit $LASTEXITCODE)." }
}
$repoRoot = Split-Path $PSScriptRoot
$publish = {
    param([string] $Folder)
    & dotnet publish (Join-Path $repoRoot 'src' 'Multiplex.Web' 'Multiplex.Web.csproj') -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -o $Folder --nologo -v quiet
    if ($LASTEXITCODE) { throw "dotnet publish failed (exit $LASTEXITCODE); nothing on the server was changed." }
}

# The server can be reached with this PC's key: ssh asks for nothing (BatchMode), so a missing key fails here.
$probe = & $ssh @sshOptions $Target 'true' 2>&1
if ($LASTEXITCODE -and "$probe" -match 'Host key verification failed') {
    throw @"
This PC does not know $Target's host key yet, so ssh will not trust it. Connect once yourself, check
the key fingerprint it shows, and answer yes; then run this again:
  ssh $Target
"@
}
if ($LASTEXITCODE) {
    throw @"
Could not sign in to $Target with this PC's SSH key: $($probe -join ' ')
This uses Windows' own ssh, which reads keys from ~\.ssh and the Windows ssh-agent service (not Git's).
If your key has a passphrase, load it once: Start-Service ssh-agent; ssh-add
If the server asks for a password, add this PC's key once (it asks for the server's password):
  Get-Content `$HOME\.ssh\id_ed25519.pub | ssh $Target "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
"@
}

$root = Get-MultiplexRoot
$secretPath = Join-Path $root 'passcode.dpapi'
if (Test-Path -LiteralPath $secretPath) {
    $passcode = Read-MultiplexPasscodeSecret -Path $secretPath
    Write-Host "Using the owner passcode this PC keeps (show it with ./scripts/Install-Multiplex.ps1 -ShowPasscode)."
}
else {
    $passcode = New-MultiplexPasscode
    Save-MultiplexPasscodeSecret -Passcode $passcode -Path $secretPath
    Write-Host "A new owner passcode was made and kept on this PC. Show it with: ./scripts/Install-Multiplex.ps1 -ShowPasscode"
}
$hash = ConvertTo-MultiplexPasscodeHash -Passcode $passcode

$commit = if (Get-Command git -ErrorAction SilentlyContinue) { (& git -C $repoRoot rev-parse --short HEAD 2>$null) ?? 'local' } else { 'local' }
$tag = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), $commit
$result = Invoke-MultiplexLinuxDeploy -Remote $remote -Upload $upload -Publish $publish -PasscodeHash $hash -Tag $tag

Write-Host "Checking $($result.BaseUrl) from this PC (the first HTTPS request waits for its certificate)..."
$deadline = [DateTime]::UtcNow.AddMinutes(2)
do {
    $checks = Test-MultiplexEndpoint -BaseUrl $result.BaseUrl
    if (($checks | Where-Object Check -EQ 'health').Passed -or [DateTime]::UtcNow -ge $deadline) { break }
    Start-Sleep -Seconds 5
} while ($true)
$checks | Format-Table -AutoSize | Out-Host

Write-Host @"
Multiplex runs at $($result.BaseUrl) (image $($result.Image)), for devices on your tailnet only.
  TripIt MCP address: $($result.BaseUrl)/tripit/mcp
  Claude Code:        claude mcp add --transport http tripit $($result.BaseUrl)/tripit/mcp
Claude's web, phone and Desktop connectors and ChatGPT call from their own clouds, which are not on
your tailnet, so they cannot reach this address.
"@
if ($checks | Where-Object { -not $_.Passed }) { throw 'Some checks failed; see the table above.' }
