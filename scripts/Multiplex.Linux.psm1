#Requires -Version 7.2
# Deploying Multiplex to a Linux server over SSH (issue #97): the server runs it as two Docker
# containers from deploy/linux -- the server, and a Tailscale device named "multiplex" that serves it
# to the tailnet only. Deploy-MultiplexLinux.ps1 wires these helpers to ssh and scp;
# Deploy-MultiplexLinux.Tests.ps1 tests them against a fake server.
Set-StrictMode -Version Latest

# Everything on the server lives in ~/multiplex: compose.yaml, serve.json, .env (which image runs) and
# multiplex.env (the server's address and passcode hash). Uploads are unpacked in ~/multiplex/incoming.
$script:RemoteRoot = 'multiplex'

function ConvertTo-MultiplexEnvFile {
    <#
    KEY='value' lines with Linux line endings, as Docker Compose reads them. Single quotes make Compose
    take a value literally, so the $ signs in a passcode hash are never expanded as variables. A value
    that cannot be single-quoted (a quote or a line break in it) is refused, not written wrongly.
    #>
    [CmdletBinding()] [OutputType([string])]
    param([Parameter(Mandatory)] [System.Collections.IDictionary] $Values)
    $lines = foreach ($key in $Values.Keys) {
        $value = [string]$Values[$key]
        if ($value -match "['\r\n]") { throw "The value of $key holds a quote or a line break, which an environment file cannot carry." }
        "$key='$value'"
    }
    return ($lines -join "`n") + "`n"
}

function Get-MultiplexLinuxServerEnvironment {
    <#
    The settings that differ per server: its public address and the owner passcode's hash. The rest
    (listening address, data folder, browser, display) is fixed in deploy/linux/Dockerfile.
    #>
    [CmdletBinding()] [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param([Parameter(Mandatory)] [string] $PublicBaseUrl, [Parameter(Mandatory)] [string] $PasscodeHash)
    return [ordered]@{
        Multiplex__PublicBaseUrl      = $PublicBaseUrl.TrimEnd('/')
        Multiplex__Gate__PasscodeHash = $PasscodeHash
    }
}

function ConvertFrom-MultiplexTailscaleStatus {
    <#
    The parts of `tailscale status --json` the deploy needs: the state (Running, NeedsLogin, ...), the
    link that adds the device to the tailnet, the device's DNS name, and whether the tailnet issues
    HTTPS certificates. Output that is not a status (the container not up yet) is state Unknown.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [string] $Json)
    try { $status = $Json | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { $status = $null }
    if ($status -isnot [System.Collections.IDictionary] -or -not $status['BackendState']) {
        return [pscustomobject]@{ State = 'Unknown'; LoginUrl = $null; DnsName = $null; HttpsEnabled = $false; Detail = "$Json".Trim() }
    }
    $self = $status['Self']
    $dnsName = if ($self -is [System.Collections.IDictionary] -and $self['DNSName']) { ([string]$self['DNSName']).TrimEnd('.') }
    return [pscustomobject]@{
        State        = [string]$status['BackendState']
        LoginUrl     = ($status['AuthURL'] ? [string]$status['AuthURL'] : $null)
        DnsName      = ($dnsName ? $dnsName : $null)
        HttpsEnabled = [bool]@($status['CertDomains'] | Where-Object { $_ }).Count
        Detail       = $null
    }
}

function Get-MultiplexLinuxServerProblem {
    <#
    Why the server cannot run Multiplex, or nothing when it can. -Facts is the check step's output:
    arch=<uname -m>, then docker=<version> or dockererr=<message>, then compose=<version> or composeerr=<message>.
    #>
    [CmdletBinding()] [OutputType([string])]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Facts)
    $f = @{}
    foreach ($line in $Facts -split "`r?`n") { if ($line -match '^(\w+)=(.*)$') { $f[$Matches[1]] = $Matches[2].Trim() } }

    if ($f['arch'] -ne 'x86_64') {
        return "The server's processor is $($f['arch'] ?? 'unknown'), but Multiplex needs an x64 (x86_64) server: Google Chrome, which the TripIt sign-in needs, is built for x64 only on Linux."
    }
    if ($f.ContainsKey('dockererr')) {
        if ($f['dockererr'] -match 'permission denied') {
            return "Docker is installed, but this SSH user may not use it. On the server, run: sudo usermod -aG docker `$USER -- then sign out and back in, and run this again. ($($f['dockererr']))"
        }
        if ($f['dockererr'] -match 'not found') {
            return 'Docker is not installed on the server. Install Docker Engine and its Compose plugin (https://docs.docker.com/engine/install/), then run this again.'
        }
        return "Docker on the server did not answer: $($f['dockererr'])"
    }
    if (-not $f.ContainsKey('docker')) { return "The server's check did not report Docker: $Facts" }
    if ($f.ContainsKey('composeerr') -or -not ($f['compose'] -match '^v?(\d+)\.') -or [int]$Matches[1] -lt 2) {
        return "The server's Docker has no Compose v2 plugin. Install the docker-compose-plugin package, then run this again. ($($f['composeerr'] ?? $f['compose']))"
    }
    return $null
}

function Test-MultiplexSshTarget {
    <#
    Whether -Target is a plain SSH destination: user@host or an SSH config alias. Anything ssh could
    read as an option (a leading -), or that holds spaces or shell characters, is refused.
    #>
    [CmdletBinding()] [OutputType([bool])]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Target)
    # \A and \z, not ^ and $: $ would also match before a trailing line break. A colon only inside
    # brackets (an IPv6 address): scp reads host:22 as a host and a path.
    return $Target -match '\A(?:[A-Za-z0-9_][A-Za-z0-9._-]*@)?(?:\[[0-9A-Fa-f:.%]+\]|[A-Za-z0-9_][A-Za-z0-9._-]*)\z'
}

function Get-MultiplexRemoteInvocation {
    <#
    How to run -Script on the server: a fixed Command for ssh, and the script as base64 to send on its
    standard input. The script never appears on a command line (where ps and logs would show it), no
    quoting on this PC or the server can change it, and Windows line endings are dropped. base64 -i
    skips the line breaks a pipe adds; bash runs the script only once all of it has arrived, so a
    command in it that reads standard input finds it empty instead of eating the rest of the script.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Script)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Script -replace "`r`n", "`n"))
    return [pscustomobject]@{ Command = 'bash -c "$(base64 -di)"'; Input = [Convert]::ToBase64String($bytes) }
}

function Get-MultiplexTool {
    <#
    The program to run for tar, ssh or scp. On Windows, Windows' own: Git for Windows' GNU tar, if it
    comes first on PATH, reads "C:\..." as a remote host "C", and its scp mishandles drive letters.
    #>
    [CmdletBinding()] [OutputType([string])]
    param([Parameter(Mandatory)] [ValidateSet('tar', 'ssh', 'scp')] [string] $Name)
    if ($IsWindows) {
        $path = if ($Name -eq 'tar') { Join-Path $env:SystemRoot 'System32' 'tar.exe' } else { Join-Path $env:SystemRoot 'System32' 'OpenSSH' "$Name.exe" }
        if (Test-Path -LiteralPath $path) { return $path }
    }
    return $Name
}

function New-MultiplexRemoteFileScript {
    <#
    Bash lines that write -Content to ~/-Path, readable by the SSH user only. The file is written beside
    its destination and moved over it, so a reader never sees half a file.
    #>
    [CmdletBinding()] [OutputType([string])]
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Content)
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Content))
    return @"
# file: $Path
umask 077
printf '%s' '$b64' | base64 -d > "`$HOME/$Path.tmp"
mv -f "`$HOME/$Path.tmp" "`$HOME/$Path"
"@
}

function Invoke-MultiplexLinuxStep {
    # Runs one remote step; returns its output, or throws with -Failure and the output's last lines.
    param([scriptblock] $Remote, [string] $Step, [string] $Body, [string] $Failure, [switch] $AllowFailure)
    $result = & $Remote "# step: $Step`nset -e`ncd `"`$HOME`"`n$Body"
    if ($result.ExitCode -ne 0 -and -not $AllowFailure) {
        $tail = (@("$($result.Output)" -split "`r?`n") | Select-Object -Last 20) -join "`n"
        throw "$Failure`n$tail"
    }
    return $result
}

function Invoke-MultiplexLinuxDeploy {
    <#
    Deploys Multiplex to a Linux server. Re-running it is safe, and a failure before the last step
    leaves the running server as it was:
      1. checks the server can run it (x64, Docker, Compose v2) -- nothing changes before this passes;
      2. -Publish builds the server on this PC; the build and deploy/linux are uploaded with -Upload;
      3. the server builds the image under -Tag -- a failed build stops here, and the upload is removed;
      4. on a first deploy, the Tailscale device starts (a redeploy leaves the running one alone); a new
         device shows the link that adds it to the tailnet, and the deploy waits (up to -LoginTimeout);
      5. the server's settings are written, and the device and the server are updated together;
      6. waits (up to -HealthTimeout) for the server to answer /healthz, else reports its log;
      7. removes the upload, all but the last three images, and week-old build cache;
      8. checks that Chrome starts, sandboxed, in the server's container.
    -Remote runs a bash script on the server and returns @{ ExitCode; Output }. -Upload copies a local
    file to a path under the SSH user's home. Returns the server's public address.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [scriptblock] $Remote,
        [Parameter(Mandatory)] [scriptblock] $Upload,
        [Parameter(Mandatory)] [scriptblock] $Publish,
        [Parameter(Mandatory)] [string] $PasscodeHash,
        [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9_.-]+$')] [string] $Tag,
        [string] $DeployFolder = (Join-Path $PSScriptRoot '..' 'deploy' 'linux'),
        [TimeSpan] $LoginTimeout = [TimeSpan]::FromMinutes(15),
        [TimeSpan] $HealthTimeout = [TimeSpan]::FromMinutes(2),
        [TimeSpan] $PollInterval = [TimeSpan]::FromSeconds(3))
    $root = $script:RemoteRoot
    $image = "multiplex-server:$Tag"

    Write-Host 'Checking the server...'
    $check = Invoke-MultiplexLinuxStep $Remote 'check' -AllowFailure -Body @'
set +e
echo "arch=$(uname -m)"
if v=$(docker version --format '{{.Server.Version}}' 2>&1); then echo "docker=$v"; else echo "dockererr=$(printf '%s' "$v" | tr '\n' ' ')"; fi
if v=$(docker compose version --short 2>&1); then echo "compose=$v"; else echo "composeerr=$(printf '%s' "$v" | tr '\n' ' ')"; fi
'@
    if ($problem = Get-MultiplexLinuxServerProblem -Facts "$($check.Output)") { throw $problem }

    Write-Host 'Building the server on this PC...'
    $staging = Join-Path ([IO.Path]::GetTempPath()) "multiplex-linux-$([guid]::NewGuid().ToString('N'))"
    try {
        & $Publish (Join-Path $staging 'app')
        Copy-Item -Path (Join-Path $DeployFolder '*') -Destination $staging -Recurse
        $bundle = "$staging.tgz"
        & (Get-MultiplexTool tar) -czf $bundle -C $staging .
        if ($LASTEXITCODE) { throw "tar could not pack $staging (exit $LASTEXITCODE)." }

        Write-Host 'Uploading...'
        Invoke-MultiplexLinuxStep $Remote 'prepare' "mkdir -p '$root'" 'Could not create ~/multiplex on the server.' | Out-Null
        & $Upload $bundle "$root/incoming.tgz"
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "$staging.tgz" -Force -ErrorAction SilentlyContinue
    }

    $cleanup = @"
rm -rf '$root/incoming' '$root/incoming.tgz'
docker image ls --format '{{.Tag}}' multiplex-server | sort -r | tail -n +4 | sed 's/^/multiplex-server:/' | xargs -r docker image rm >/dev/null 2>&1 || true
docker builder prune -f --filter until=168h >/dev/null 2>&1 || true
"@
    Write-Host "Building image $image on the server (the first build downloads .NET and Chrome)..."
    $build = & $Remote "# step: build`ncd `"`$HOME`"`nrm -rf '$root/incoming' && mkdir -p '$root/incoming' && tar -xzf '$root/incoming.tgz' -C '$root/incoming' && docker build --pull --build-arg REFRESH=$((Get-Date).ToString('yyyy-MM-dd')) -t '$image' '$root/incoming' 2>&1"
    if ($build.ExitCode -ne 0) {
        Invoke-MultiplexLinuxStep $Remote 'cleanup' $cleanup 'Could not remove the upload.' -AllowFailure | Out-Null
        $tail = (@("$($build.Output)" -split "`r?`n") | Select-Object -Last 20) -join "`n"
        throw "The image did not build, so the running server was left as it was.`n$tail"
    }

    # Compose reads both files to load the project, even to start only the Tailscale device. A first
    # deploy has neither yet: give it placeholders, which the start step below replaces. A redeploy
    # keeps the running ones, so the running server is untouched until then.
    $placeholder = Get-MultiplexLinuxServerEnvironment -PublicBaseUrl 'http://localhost:5190' -PasscodeHash $PasscodeHash
    Invoke-MultiplexLinuxStep $Remote 'install-files' -Failure 'Could not put the compose files in ~/multiplex.' -Body @"
cp -f '$root/incoming/compose.yaml' '$root/incoming/serve.json' '$root/'
if [ ! -f '$root/.env' ]; then
$(New-MultiplexRemoteFileScript -Path "$root/.env" -Content (ConvertTo-MultiplexEnvFile ([ordered]@{ MULTIPLEX_IMAGE = $image })))
fi
if [ ! -f '$root/multiplex.env' ]; then
$(New-MultiplexRemoteFileScript -Path "$root/multiplex.env" -Content (ConvertTo-MultiplexEnvFile $placeholder))
fi
"@ | Out-Null

    # Only a first deploy starts the device here. On a redeploy it already runs, and the server lives in
    # its network: recreating it now would cut the running server off until the start step, or for good
    # if a later step stopped the deploy. The start step updates both at once.
    $running = Invoke-MultiplexLinuxStep $Remote 'tailscale-running' "cd '$root' && docker compose ps --status running --quiet tailscale" 'Could not ask Docker what is running.'
    if (-not "$($running.Output)".Trim()) {
        Write-Host 'Starting the Tailscale device...'
        Invoke-MultiplexLinuxStep $Remote 'start-tailscale' "cd '$root' && docker compose pull --quiet tailscale && docker compose up -d tailscale 2>&1" 'The Tailscale device did not start.' | Out-Null
    }

    $deadline = [DateTime]::UtcNow + $LoginTimeout
    $shownLink = $null
    while ($true) {
        $raw = Invoke-MultiplexLinuxStep $Remote 'tailscale-status' "cd '$root' && docker compose exec -T tailscale tailscale status --json 2>&1" -AllowFailure
        $status = ConvertFrom-MultiplexTailscaleStatus "$($raw.Output)"
        if ($status.State -eq 'Running' -and $status.DnsName) { break }
        if ($status.LoginUrl -and $status.LoginUrl -ne $shownLink) {
            Write-Host "Add the 'multiplex' device to your tailnet: open $($status.LoginUrl) and sign in to Tailscale."
            $shownLink = $status.LoginUrl
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            $why = if ($shownLink) { "Nobody added the device to the tailnet in time. Open $shownLink, then run this again." }
                   elseif ($status.State -eq 'NeedsMachineAuth') { 'The device waits for approval: approve it in the Tailscale admin console (Machines), then run this again.' }
                   else { "The Tailscale device did not come up (state $($status.State)). $($status.Detail)" }
            throw "$why The server was not started."
        }
        if ($PollInterval -gt [TimeSpan]::Zero) { Start-Sleep -Milliseconds ([int]$PollInterval.TotalMilliseconds) }
    }
    if (-not $status.HttpsEnabled) {
        throw "Your tailnet does not issue HTTPS certificates, which the server's address needs. Turn on 'HTTPS Certificates' in the Tailscale admin console (DNS page), then run this again. The server was not started."
    }
    $baseUrl = "https://$($status.DnsName)"

    Write-Host "Starting the server at $baseUrl..."
    $settings = Get-MultiplexLinuxServerEnvironment -PublicBaseUrl $baseUrl -PasscodeHash $PasscodeHash
    Invoke-MultiplexLinuxStep $Remote 'start' -Failure 'The server did not start.' -Body @"
$(New-MultiplexRemoteFileScript -Path "$root/multiplex.env" -Content (ConvertTo-MultiplexEnvFile $settings))
$(New-MultiplexRemoteFileScript -Path "$root/.env" -Content (ConvertTo-MultiplexEnvFile ([ordered]@{ MULTIPLEX_IMAGE = $image })))
cd '$root' && docker compose pull --quiet tailscale && docker compose up -d 2>&1
"@ | Out-Null

    $deadline = [DateTime]::UtcNow + $HealthTimeout
    while ($true) {
        # The Tailscale container shares the server's network, so its wget reaches the server on loopback.
        $health = Invoke-MultiplexLinuxStep $Remote 'health' "cd '$root' && docker compose exec -T tailscale wget -qO- -T 5 http://127.0.0.1:5190/healthz 2>&1" -AllowFailure
        if ($health.ExitCode -eq 0) { break }
        if ([DateTime]::UtcNow -ge $deadline) {
            $logs = Invoke-MultiplexLinuxStep $Remote 'logs' "cd '$root' && docker compose logs --no-color --tail 40 multiplex 2>&1" -AllowFailure
            throw "The server did not answer /healthz within $([int]$HealthTimeout.TotalSeconds) s. Its log:`n$($logs.Output)"
        }
        if ($PollInterval -gt [TimeSpan]::Zero) { Start-Sleep -Milliseconds ([int]$PollInterval.TotalMilliseconds) }
    }

    Invoke-MultiplexLinuxStep $Remote 'cleanup' $cleanup 'Could not remove the upload.' -AllowFailure | Out-Null

    # The sign-in browsers run Chrome with its sandbox on, as the server's own user. Docker's default
    # seccomp profile can refuse the namespaces that sandbox needs; then every site sign-in would fail,
    # so try it now (headless: the sandbox is the same) rather than at the first sign-in.
    Write-Host 'Checking that Chrome starts in the server''s container...'
    $browser = Invoke-MultiplexLinuxStep $Remote 'browser-check' -AllowFailure -Body (@'
cd '{0}' && docker compose exec -T multiplex sh -c 'd=$(mktemp -d) && google-chrome-stable --headless=new --user-data-dir="$d" --dump-dom about:blank; s=$?; rm -rf "$d"; exit $s' 2>&1
'@ -f $root)
    if ($browser.ExitCode -ne 0 -or "$($browser.Output)" -notmatch '<body') {
        $tail = (@("$($browser.Output)" -split "`r?`n") | Select-Object -Last 15) -join "`n"
        throw "The server runs at $baseUrl, but Chrome cannot start in its container, so every site sign-in would fail. Chrome said:`n$tail"
    }
    return [pscustomobject]@{ BaseUrl = $baseUrl; Image = $image }
}

Export-ModuleMember -Function ConvertTo-MultiplexEnvFile, Get-MultiplexLinuxServerEnvironment, ConvertFrom-MultiplexTailscaleStatus,
    Get-MultiplexLinuxServerProblem, Test-MultiplexSshTarget, Get-MultiplexRemoteInvocation, Get-MultiplexTool, New-MultiplexRemoteFileScript, Invoke-MultiplexLinuxDeploy
