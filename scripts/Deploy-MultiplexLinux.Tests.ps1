#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
# Tests for deploying Multiplex to a Linux server over SSH (Multiplex.Linux.psm1, Deploy-MultiplexLinux.ps1,
# deploy/linux). The server is faked: each remote script starts with a "# step: <name>" line, and the
# fake answers by step, so these tests run anywhere without SSH or Docker.
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'Multiplex.Linux.psm1') -Force
    $script:DeployFolder = Join-Path $PSScriptRoot '..' 'deploy' 'linux'
    # A device that is up, with HTTPS certificates, and that the tailnet lets use Funnel (the funnel node attribute).
    $script:Running = '{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net.","CapMap":{"funnel":null,"https":null,"https://tailscale.com/cap/funnel-ports?ports=443,8443,10000":null}},"CertDomains":["multiplex.tail1234.ts.net"]}'
    # The same device without the funnel node attribute.
    $script:NoFunnel = '{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net.","CapMap":{"https":null}},"CertDomains":["multiplex.tail1234.ts.net"]}'

    # A fake server. -Status is a list of `tailscale status --json` answers, given in turn (the last repeats).
    # A remote script that writes files carries, per file, a "# file: <path>" line and the content as base64.
    function New-FakeServer {
        param(
            [string] $Arch = 'x86_64',
            [string] $Docker = 'docker=27.3.1',
            [string] $Compose = 'compose=2.29.7',
            [bool] $BuildFails = $false,
            [object[]] $Status = @($script:Running),
            [bool] $Healthy = $true,
            # A redeploy: the Tailscale device already runs (and the server with it).
            [bool] $TailscaleRunning = $false,
            [bool] $BrowserWorks = $true)
        $server = [pscustomobject]@{
            Steps = [System.Collections.Generic.List[string]]::new()
            Scripts = [System.Collections.Generic.List[string]]::new()
            Files = [ordered]@{}
            Uploads = [System.Collections.Generic.List[string]]::new()
            StatusCalls = 0
        }
        $remote = {
            param([string] $Script)
            $step = if ($Script -match '(?m)^# step: (\S+)') { $Matches[1] }
            $server.Steps.Add($step)
            $server.Scripts.Add($Script)
            foreach ($m in [regex]::Matches($Script, "(?m)^# file: (\S+)\s*\r?\n(?:.*\r?\n)*?printf '%s' '([A-Za-z0-9+/=]+)' \| base64 -d")) {
                $server.Files[$m.Groups[1].Value] = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($m.Groups[2].Value))
            }
            switch ($step) {
                'check' { return [pscustomobject]@{ ExitCode = 0; Output = "arch=$Arch`n$Docker`n$Compose" } }
                'build' { return [pscustomobject]@{ ExitCode = ($BuildFails ? 1 : 0); Output = ($BuildFails ? "step 3/7`nERROR: failed to solve: apt-get install exited 100" : 'naming to docker.io/library/multiplex-server done') } }
                'tailscale-status' {
                    $answer = $Status[[Math]::Min($server.StatusCalls, $Status.Count - 1)]
                    $server.StatusCalls++
                    return [pscustomobject]@{ ExitCode = 0; Output = $answer }
                }
                'health' { return [pscustomobject]@{ ExitCode = ($Healthy ? 0 : 1); Output = ($Healthy ? 'Healthy' : 'wget: can''t connect to remote host') } }
                'logs' { return [pscustomobject]@{ ExitCode = 0; Output = 'multiplex-1  | Unhandled exception. System.InvalidOperationException: boom' } }
                'tailscale-running' { return [pscustomobject]@{ ExitCode = 0; Output = ($TailscaleRunning ? '4f2a9c1e7b3d' : '') } }
                'browser-check' {
                    if ($BrowserWorks) { return [pscustomobject]@{ ExitCode = 0; Output = 'Chrome started with a window on :99, sandboxed.' } }
                    return [pscustomobject]@{ ExitCode = 1; Output = '[0101/000000.000:FATAL:zygote_host_impl_linux.cc(128)] No usable sandbox!' }
                }
                default { return [pscustomobject]@{ ExitCode = 0; Output = '' } }
            }
        }.GetNewClosure()
        $upload = { param([string] $LocalFile, [string] $RemotePath) $server.Uploads.Add($RemotePath) }.GetNewClosure()
        $server | Add-Member -NotePropertyName Remote -NotePropertyValue $remote
        $server | Add-Member -NotePropertyName Upload -NotePropertyValue $upload
        return $server
    }

    $script:GoodPublish = { param([string] $Folder) New-Item -ItemType Directory -Force -Path $Folder | Out-Null; Set-Content (Join-Path $Folder 'Multiplex.Web.dll') 'x' }

    # A bash that runs here, to syntax-check the scripts sent to the server: Git for Windows' bash on
    # Windows (System32's bash.exe is WSL, which may have no distribution), else the bash on PATH.
    $script:Bash = @(
        if ($IsWindows) { Join-Path $env:ProgramFiles 'Git' 'bin' 'bash.exe' } else { (Get-Command bash -ErrorAction SilentlyContinue)?.Source }
    ) | Where-Object { $_ -and (Test-Path $_) -and ((& $_ -c 'echo ok' 2>$null) -eq 'ok') } | Select-Object -First 1

    function Invoke-Deploy($Server, [scriptblock] $Publish = $script:GoodPublish) {
        Invoke-MultiplexLinuxDeploy -Remote $Server.Remote -Upload $Server.Upload -Publish $Publish `
            -Tag '20261120-1200-abc1234' -PollInterval ([TimeSpan]::Zero) `
            -LoginTimeout ([TimeSpan]::FromSeconds(1)) -HealthTimeout ([TimeSpan]::FromSeconds(1)) 6>&1
    }
}

Describe 'the server''s environment file' {
    It 'passes values literally: single-quoted, so Docker Compose never expands a $ in them' {
        $text = ConvertTo-MultiplexEnvFile ([ordered]@{ Multiplex__PublicBaseUrl = 'https://multiplex.tail1234.ts.net'; Other = 'a$b$c' })
        $text | Should -Be ("Multiplex__PublicBaseUrl='https://multiplex.tail1234.ts.net'`nOther='a`$b`$c'`n")
    }

    It 'uses Linux line endings only' {
        ConvertTo-MultiplexEnvFile ([ordered]@{ A = '1'; B = '2' }) | Should -Not -Match "`r"
    }

    It 'refuses a value it cannot quote, rather than writing a file Compose would misread' {
        { ConvertTo-MultiplexEnvFile @{ A = "it's" } } | Should -Throw '*A*'
        { ConvertTo-MultiplexEnvFile @{ A = "two`nlines" } } | Should -Throw '*A*'
    }

    It 'holds only the public address, without a trailing slash: no passcode (#80), and not the sign-in settings the owner keeps in identity.env' {
        $env = Get-MultiplexLinuxServerEnvironment -PublicBaseUrl 'https://multiplex.tail1234.ts.net/'
        $env.Multiplex__PublicBaseUrl | Should -Be 'https://multiplex.tail1234.ts.net'
        @($env.Keys) | Should -Be @('Multiplex__PublicBaseUrl')
    }
}

Describe 'the Tailscale device''s status' {
    It 'reads a running device''s name, without the trailing dot, and that HTTPS certificates are on' {
        $s = ConvertFrom-MultiplexTailscaleStatus '{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net."},"CertDomains":["multiplex.tail1234.ts.net"]}'
        $s.State | Should -Be 'Running'
        $s.DnsName | Should -Be 'multiplex.tail1234.ts.net'
        $s.HttpsEnabled | Should -BeTrue
    }

    It 'reads the sign-in link of a device that needs adding to the tailnet' {
        $s = ConvertFrom-MultiplexTailscaleStatus '{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/1a2b3c","Self":{"DNSName":""}}'
        $s.State | Should -Be 'NeedsLogin'
        $s.LoginUrl | Should -Be 'https://login.tailscale.com/a/1a2b3c'
    }

    It 'says HTTPS certificates are off when the tailnet offers no certificate domain' {
        (ConvertFrom-MultiplexTailscaleStatus '{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net."},"CertDomains":null}').HttpsEnabled | Should -BeFalse
    }

    It 'reads whether the tailnet lets the device use Funnel: the funnel node attribute, as Tailscale''s own check reads it' {
        (ConvertFrom-MultiplexTailscaleStatus $script:Running).FunnelAllowed | Should -BeTrue
        (ConvertFrom-MultiplexTailscaleStatus $script:NoFunnel).FunnelAllowed | Should -BeFalse
        (ConvertFrom-MultiplexTailscaleStatus '{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net."},"CertDomains":["multiplex.tail1234.ts.net"]}').FunnelAllowed | Should -BeFalse
        (ConvertFrom-MultiplexTailscaleStatus 'service "tailscale" is not running').FunnelAllowed | Should -BeFalse
    }

    It 'lets the device use Funnel only on a port its funnel-ports attribute lists, a port or a first-last range holding 443, as Tailscale''s own check does' {
        function Funnel([string] $Ports) {
            $port = if ($Ports) { ",`"https://tailscale.com/cap/funnel-ports?ports=$Ports`":null" }
            (ConvertFrom-MultiplexTailscaleStatus ('{"BackendState":"Running","Self":{"DNSName":"m.t.ts.net.","CapMap":{"funnel":null,"https":null' + $port + '}},"CertDomains":["m.t.ts.net"]}')).FunnelAllowed
        }
        Funnel '' | Should -BeFalse -Because 'Tailscale refuses Funnel when no funnel-ports attribute is granted'
        Funnel '8443,10000' | Should -BeFalse
        Funnel '4430,8443' | Should -BeFalse
        Funnel '8443,443' | Should -BeTrue
        Funnel '1-1000' | Should -BeTrue
        Funnel '444-1000' | Should -BeFalse
    }

    It 'reports output that is not a status as not ready, without throwing' {
        $s = ConvertFrom-MultiplexTailscaleStatus 'service "tailscale" is not running'
        $s.State | Should -Be 'Unknown'
        $s.Detail | Should -Match 'not running'
    }
}

Describe 'checking the server before anything changes' {
    It 'accepts an x64 server with Docker and Compose v2' {
        Get-MultiplexLinuxServerProblem -Facts "arch=x86_64`ndocker=27.3.1`ncompose=2.29.7" | Should -BeNullOrEmpty
    }

    It 'refuses an ARM server: Google Chrome, which the TripIt sign-in needs, is built for x64 only' {
        Get-MultiplexLinuxServerProblem -Facts "arch=aarch64`ndocker=27.3.1`ncompose=2.29.7" | Should -Match 'aarch64.*x64'
    }

    It 'says how to let the user run Docker when the Docker socket refuses it' {
        $problem = Get-MultiplexLinuxServerProblem -Facts "arch=x86_64`ndockererr=permission denied while trying to connect to the Docker daemon socket`ncompose=2.29.7"
        $problem | Should -Match 'usermod -aG docker'
    }

    It 'says Docker is missing when it is not installed' {
        Get-MultiplexLinuxServerProblem -Facts "arch=x86_64`ndockererr=bash: docker: command not found`ncomposeerr=bash: docker: command not found" | Should -Match 'Docker is not installed'
    }

    It 'asks for Compose 2.24 or newer, which reads an optional env file, rather than failing later on compose.yaml' {
        Get-MultiplexLinuxServerProblem -Facts "arch=x86_64`ndocker=27.3.1`ncompose=2.20.2" | Should -Match '2\.24'
        Get-MultiplexLinuxServerProblem -Facts "arch=x86_64`ndocker=27.3.1`ncompose=v2.23.9" | Should -Match '2\.24'
        Get-MultiplexLinuxServerProblem -Facts "arch=x86_64`ndocker=27.3.1`ncompose=2.24.0" | Should -BeNullOrEmpty
        Get-MultiplexLinuxServerProblem -Facts "arch=x86_64`ndocker=29.9.1`ncompose=5.6.0" | Should -BeNullOrEmpty
    }

    It 'asks for the Compose plugin when only Docker is there' {
        Get-MultiplexLinuxServerProblem -Facts "arch=x86_64`ndocker=27.3.1`ncomposeerr=docker: 'compose' is not a docker command." | Should -Match 'Compose'
    }
}

Describe 'deploying' {
    It 'stops before changing the server when the server cannot run it' {
        $server = New-FakeServer -Arch 'aarch64'
        { Invoke-Deploy $server } | Should -Throw '*x64*'
        $server.Steps | Should -Be @('check')
        $server.Uploads | Should -BeNullOrEmpty
    }

    It 'a failed publish on this PC changes nothing on the server' {
        $server = New-FakeServer
        { Invoke-Deploy $server -Publish { throw 'dotnet publish failed' } } | Should -Throw '*dotnet publish failed*'
        $server.Steps | Should -Be @('check')
        $server.Uploads | Should -BeNullOrEmpty
    }

    It 'a failed image build leaves the running server as it was, removes the upload, and shows the build''s last lines' {
        $server = New-FakeServer -BuildFails $true
        { Invoke-Deploy $server } | Should -Throw '*apt-get install exited 100*'
        $server.Steps | Should -Contain 'cleanup'
        $server.Steps | Should -Not -Contain 'start-tailscale'
        $server.Steps | Should -Not -Contain 'start'
        $server.Files.Keys | Should -Not -Contain 'multiplex/multiplex.env'
        $server.Files.Keys | Should -Not -Contain 'multiplex/.env'
    }

    It 'builds the image on the server under the tag it was given, then starts it with that tag' {
        $server = New-FakeServer
        $result = Invoke-Deploy $server | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['BaseUrl'] }
        $server.Files['multiplex/.env'] | Should -Be "MULTIPLEX_IMAGE='multiplex-server:20261120-1200-abc1234'`n"
        $server.Steps.IndexOf('build') | Should -BeLessThan $server.Steps.IndexOf('start')
        $result.BaseUrl | Should -Be 'https://multiplex.tail1234.ts.net'
    }

    It 'returns only the address and image, whatever the build on this PC prints (a compiler warning, #129)' {
        $server = New-FakeServer
        $noisyPublish = {
            param([string] $Folder)
            & $script:GoodPublish $Folder
            # What dotnet publish writes for a compiler warning, even at -v quiet.
            'D:\src\CircuitBreaker.cs(11,30): warning CS9124: Parameter ''TimeSpan first'' is captured into the state of the enclosing type'
        }
        # No "6>&1" here: this is the function's own output, as Deploy-MultiplexLinux.ps1 receives it.
        $result = Invoke-MultiplexLinuxDeploy -Remote $server.Remote -Upload $server.Upload -Publish $noisyPublish `
            -Tag '20261120-1200-abc1234' -PollInterval ([TimeSpan]::Zero) `
            -LoginTimeout ([TimeSpan]::FromSeconds(1)) -HealthTimeout ([TimeSpan]::FromSeconds(1)) 6>$null
        @($result).Count | Should -Be 1
        $result.BaseUrl | Should -Be 'https://multiplex.tail1234.ts.net'
        $result.Image | Should -Be 'multiplex-server:20261120-1200-abc1234'
    }

    It 'gives the server its tailnet address, in a file only its owner can read, and never writes identity.env' {
        $server = New-FakeServer
        Invoke-Deploy $server | Out-Null
        $server.Files['multiplex/multiplex.env'] | Should -Be ("Multiplex__PublicBaseUrl='https://multiplex.tail1234.ts.net'`n")
        $server.Files.Keys | Should -Not -Contain 'multiplex/identity.env'
    }

    It 'on a first deploy, shows the link that adds the device to the tailnet and waits until it is added' {
        $server = New-FakeServer -Status @(
            '{"BackendState":"NeedsLogin","AuthURL":"","Self":{"DNSName":""}}',
            '{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/1a2b3c","Self":{"DNSName":""}}',
            '{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/1a2b3c","Self":{"DNSName":""}}',
            $script:Running)
        $output = Invoke-Deploy $server
        ($output | Where-Object { "$_" -match 'login\.tailscale\.com/a/1a2b3c' }).Count | Should -Be 1 -Because 'the link is shown once, not on every poll'
        $server.Files['multiplex/multiplex.env'] | Should -Match 'multiplex\.tail1234\.ts\.net'
    }

    It 'stops, with the link, when nobody adds the device in time, and leaves the server unstarted' {
        $server = New-FakeServer -Status @('{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/1a2b3c","Self":{"DNSName":""}}')
        { Invoke-Deploy $server } | Should -Throw '*login.tailscale.com/a/1a2b3c*'
        $server.Steps | Should -Not -Contain 'start'
    }

    It 'stops and says where to turn on HTTPS certificates when the tailnet has them off' {
        $server = New-FakeServer -Status @('{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net."},"CertDomains":null}')
        { Invoke-Deploy $server } | Should -Throw '*HTTPS*'
        $server.Steps | Should -Not -Contain 'start'
    }

    It 'stops and says where to let the device use Funnel when the tailnet does not, rather than serving the tailnet only without a word (#108)' {
        $server = New-FakeServer -Status @($script:NoFunnel)
        $message = try { Invoke-Deploy $server | Out-Null; $null } catch { $_.Exception.Message }
        $message | Should -Match 'Funnel'
        $message | Should -Match 'admin console'
        $message | Should -Match 'not started'
        $server.Steps | Should -Not -Contain 'start'
    }

    It 'recreates the Tailscale device when it starts the server, so a changed serve.json reaches it (#108)' {
        # serve.json reaches the device through a one-file bind mount, whose changes the device's watch on
        # its /config folder does not see, and "up -d" alone leaves a device whose compose settings did not
        # change running.
        $server = New-FakeServer -TailscaleRunning $true
        Invoke-Deploy $server | Out-Null
        $server.Scripts[$server.Steps.IndexOf('start')] | Should -Match 'docker compose up -d --force-recreate\b'
    }

    It 'reports the server''s own log when it does not become healthy' {
        $server = New-FakeServer -Healthy $false
        { Invoke-Deploy $server } | Should -Throw '*InvalidOperationException: boom*'
    }

    It 'removes the upload and old images once the server answers' {
        $server = New-FakeServer
        Invoke-Deploy $server | Out-Null
        $server.Steps | Should -Contain 'cleanup'
        $server.Steps.IndexOf('health') | Should -BeLessThan $server.Steps.IndexOf('cleanup')
    }

    It 'on a first deploy, starts the Tailscale device before asking for its status' {
        $server = New-FakeServer
        Invoke-Deploy $server | Out-Null
        $server.Steps.IndexOf('start-tailscale') | Should -BeGreaterThan $server.Steps.IndexOf('build')
        $server.Steps.IndexOf('start-tailscale') | Should -BeLessThan $server.Steps.IndexOf('tailscale-status')
    }

    It 'on a redeploy, leaves the running Tailscale device, and so the running server, alone until the start step' {
        # Recreating the device would cut the running server off: it lives in the device's network.
        $server = New-FakeServer -TailscaleRunning $true
        Invoke-Deploy $server | Out-Null
        $server.Steps | Should -Not -Contain 'start-tailscale'
        $start = $server.Scripts[$server.Steps.IndexOf('start')]
        $start | Should -Match '(?s)docker compose pull.*docker compose up -d'
    }

    It 'pulls the Tailscale image before rewriting the server''s settings, and goes on with the image it has when the pull fails' {
        $server = New-FakeServer -TailscaleRunning $true
        Invoke-Deploy $server | Out-Null
        $start = $server.Scripts[$server.Steps.IndexOf('start')]
        $start.IndexOf('docker compose pull') | Should -BeLessThan $start.IndexOf('# file:')
        $start | Should -Match 'docker compose pull[^\n]*\|\|'
    }

    It 'a redeploy that stops before the start step (<Why>) never touches the running device or server' -ForEach @(
        @{ Why = 'no HTTPS certificates'; Status = '{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net."},"CertDomains":null}'; Expected = '*HTTPS*' }
        @{ Why = 'no Funnel'; Status = $null; Expected = '*Funnel*' }
    ) {
        $server = New-FakeServer -TailscaleRunning $true -Status @($Status ?? $script:NoFunnel)
        { Invoke-Deploy $server } | Should -Throw $Expected
        foreach ($script in $server.Scripts) {
            $script | Should -Not -Match 'docker compose (up|pull|restart|stop|down)' -Because ($script -split "`n")[0]
        }
    }

    It 'says so when Chrome cannot start in the server''s container, since every site sign-in would fail, and how to go back' {
        $server = New-FakeServer -BrowserWorks $false
        $message = try { Invoke-Deploy $server | Out-Null; $null } catch { $_.Exception.Message }
        $message | Should -Match 'No usable sandbox'
        $message | Should -Match 'MULTIPLEX_IMAGE'
        $server.Steps | Should -Contain 'cleanup'
    }

    It 'checks Chrome in the server''s container, as the server''s own user, with the image''s own check' {
        $server = New-FakeServer
        Invoke-Deploy $server | Out-Null
        $check = $server.Scripts[$server.Steps.IndexOf('browser-check')]
        $check | Should -Match 'docker compose exec -T multiplex sh /check-chrome\.sh'
        $check | Should -Not -Match '(?m)exec\s+(-\S+\s+)*(-u|--user)\b'
        Get-Content (Join-Path $script:DeployFolder 'Dockerfile') -Raw | Should -Match '(?m)^COPY check-chrome\.sh /check-chrome\.sh\s*$'
    }

    It 'starts Chrome as the TripIt sign-in does: with a window on the virtual display, a DevTools port, and a time limit' {
        $check = Get-Content (Join-Path $script:DeployFolder 'check-chrome.sh') -Raw
        $check | Should -Match 'google-chrome-stable'
        $check | Should -Not -Match '--headless'
        $check | Should -Match '--remote-debugging-port='
        $check | Should -Match '/tmp/\.X11-unix/X'
        $check | Should -Match '-lt 60'
    }

    It 'opens a tab, which needs a working renderer, and force-stops Chrome so the time limit holds' {
        $check = Get-Content (Join-Path $script:DeployFolder 'check-chrome.sh') -Raw
        $check | Should -Match '/json/new'
        $check | Should -Match 'curl[^\n]*-m \d+'
        $check | Should -Match 'kill -9'
    }

    It 'writes every file only its owner can read: umask 077 before the write' {
        $script = New-MultiplexRemoteFileScript -Path 'multiplex/multiplex.env' -Content "A='1'`n"
        $lines = $script -split "`n"
        $umask = [array]::FindIndex($lines, [Predicate[string]] { $args[0] -match '^umask 077$' })
        $write = [array]::FindIndex($lines, [Predicate[string]] { $args[0] -match 'base64 -d >' })
        $umask | Should -BeGreaterOrEqual 0
        $umask | Should -BeLessThan $write
    }

    It 'sends only scripts that bash can parse' {
        if (-not $script:Bash) { Set-ItResult -Skipped -Because 'no bash to check with on this PC'; return }
        # A first deploy, a redeploy, and a failed build between them send every kind of script.
        $scripts = foreach ($fake in (New-FakeServer), (New-FakeServer -TailscaleRunning $true), (New-FakeServer -BuildFails $true)) {
            try { Invoke-Deploy $fake | Out-Null } catch { $null = $_ }
            $fake.Scripts
        }
        $scripts.Count | Should -BeGreaterThan 10
        foreach ($script in $scripts) {
            $file = Join-Path $TestDrive 'remote.sh'
            [IO.File]::WriteAllText($file, $script)
            $out = & $script:Bash -n $file 2>&1
            $LASTEXITCODE | Should -Be 0 -Because "$(($script -split "`n")[0]): $out"
        }
    }
}

Describe 'the SSH target' {
    It 'accepts user@host and an SSH config alias' {
        Test-MultiplexSshTarget 'me@192.168.5.10' | Should -BeTrue
        Test-MultiplexSshTarget 'me@server.lan' | Should -BeTrue
        Test-MultiplexSshTarget 'myserver' | Should -BeTrue
    }

    It 'refuses anything ssh would read as an option, or that holds spaces or shell characters' {
        Test-MultiplexSshTarget '-oProxyCommand=calc' | Should -BeFalse
        Test-MultiplexSshTarget 'me@host; rm -rf ~' | Should -BeFalse
        Test-MultiplexSshTarget 'me@-host' | Should -BeFalse
    }

    It 'refuses a trailing line break, which a $-anchored pattern would let through' {
        Test-MultiplexSshTarget "me@host`n" | Should -BeFalse
    }

    It 'refuses a port written after the host, which scp would read as host:path, and a bracketed address, which ssh does not take' {
        Test-MultiplexSshTarget 'me@host:22' | Should -BeFalse
        Test-MultiplexSshTarget 'me@[fe80::1]' | Should -BeFalse
    }
}

Describe 'remote scripts' {
    It 'travel as base64 on standard input, so they never appear on a command line, and no quoting can change them' {
        $call = Get-MultiplexRemoteInvocation -Script "# step: x`necho 'a `"b`" `$c'`r`n"
        $call.Command | Should -Be 'bash -c "$(base64 -di)"'
        $call.Input | Should -Match '^[A-Za-z0-9+/=]+$'
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($call.Input)) | Should -Be "# step: x`necho 'a `"b`" `$c'`n"
    }
}

Describe 'the tools this PC runs' {
    It 'uses Windows'' own tar, ssh and scp, which read C:\ paths as paths (Git''s GNU tar reads C: as a host)' -Skip:(-not $IsWindows) {
        Get-MultiplexTool tar | Should -Be (Join-Path $env:SystemRoot 'System32' 'tar.exe')
        Get-MultiplexTool ssh | Should -Be (Join-Path $env:SystemRoot 'System32' 'OpenSSH' 'ssh.exe')
        Get-MultiplexTool scp | Should -Be (Join-Path $env:SystemRoot 'System32' 'OpenSSH' 'scp.exe')
    }

    It 'falls back to the command on PATH elsewhere' -Skip:($IsWindows) {
        Get-MultiplexTool tar | Should -Be 'tar'
    }
}

Describe 'what the Linux host publishes' {
    It 'serves HTTPS on 443 only, through Tailscale''s HTTPS proxy to the server on loopback' {
        $serve = Get-Content (Join-Path $script:DeployFolder 'serve.json') -Raw | ConvertFrom-Json -AsHashtable
        @($serve.TCP.Keys) | Should -Be @('443')
        $serve.TCP['443'].HTTPS | Should -BeTrue
        @($serve.Web.Keys) | Should -Be @('${TS_CERT_DOMAIN}:443')
        $serve.Web['${TS_CERT_DOMAIN}:443'].Handlers['/'].Proxy | Should -Be 'http://127.0.0.1:5190'
    }

    It 'opens 443, and only 443, to the internet through Funnel: Claude''s and ChatGPT''s connectors call from their own clouds, off the tailnet (#108)' {
        $serve = Get-Content (Join-Path $script:DeployFolder 'serve.json') -Raw | ConvertFrom-Json -AsHashtable
        $serve.ContainsKey('AllowFunnel') | Should -BeTrue -Because 'the owner opened the Linux host to the internet (#108)'
        @($serve.AllowFunnel.Keys) | Should -Be @('${TS_CERT_DOMAIN}:443')
        $serve.AllowFunnel['${TS_CERT_DOMAIN}:443'] | Should -BeTrue
    }

    It 'never forwards raw TCP: it would drop the forwarding headers, and every caller would look like the host itself (#80)' {
        $serve = Get-Content (Join-Path $script:DeployFolder 'serve.json') -Raw | ConvertFrom-Json -AsHashtable
        foreach ($port in $serve.TCP.Keys) {
            $serve.TCP[$port].ContainsKey('TCPForward') | Should -BeFalse
            $serve.TCP[$port].ContainsKey('TerminateTLS') | Should -BeFalse
        }
    }

    It 'gives the owner one sign-in link that lasts longer than the deploy waits, not a new one every minute' {
        # The Tailscale container gives up on an unfinished sign-in after TS_BOOT_TIMEOUT (60 s by default)
        # and restarts with a new link; a link opened after that adds a dead device that takes the name.
        $compose = Get-Content (Join-Path $script:DeployFolder 'compose.yaml') -Raw
        $found = [regex]::Match($compose, '(?m)^\s+TS_BOOT_TIMEOUT:\s*"?(\d+)m"?\s*$')
        $found.Success | Should -BeTrue -Because 'compose.yaml must set TS_BOOT_TIMEOUT in minutes'
        [int]$found.Groups[1].Value | Should -BeGreaterThan 15 -Because 'Invoke-MultiplexLinuxDeploy waits 15 minutes for the sign-in'
    }

    It 'signs a device in once: a restart of a signed-in device never asks for a new sign-in' {
        Get-Content (Join-Path $script:DeployFolder 'compose.yaml') -Raw | Should -Match '(?m)^\s+TS_AUTH_ONCE:\s*"true"\s*$'
    }

    It 'lets Chrome''s sandbox make its namespaces, through a system-call filter on the server''s container only' {
        # Docker's default filter allows clone/unshare/setns with namespace flags only with CAP_SYS_ADMIN,
        # so Chrome's sandbox failed: "setuid sandbox: failed to move to new namespaces" (first real deploy).
        $compose = Get-Content (Join-Path $script:DeployFolder 'compose.yaml') -Raw
        $compose | Should -Match '(?m)^\s+security_opt:\s*\r?\n\s+-\s*seccomp=chrome-seccomp\.json\s*$'
        $compose | Should -Not -Match 'seccomp[=:]unconfined'
        $compose | Should -Not -Match 'SYS_ADMIN'
    }

    It 'keeps Docker''s default filter and adds only the namespace calls: everything unlisted is still refused' {
        $profile = Get-Content (Join-Path $script:DeployFolder 'chrome-seccomp.json') -Raw | ConvertFrom-Json -AsHashtable
        $profile.defaultAction | Should -Be 'SCMP_ACT_ERRNO'
        $unconditional = @($profile.syscalls | Where-Object { $_.action -eq 'SCMP_ACT_ALLOW' -and -not $_.includes -and -not $_.args -and -not $_.excludes })
        $added = @($unconditional | Where-Object { @($_.names) -contains 'unshare' })
        $added.Count | Should -Be 1
        @($added[0].names | Sort-Object) | Should -Be @('clone', 'setns', 'unshare')
        foreach ($gated in 'mount', 'bpf', 'kexec_load', 'init_module', 'ptrace', 'clone3', 'umount2', 'pivot_root') {
            @($unconditional | Where-Object { @($_.names) -contains $gated }).Count | Should -Be 0 -Because "$gated stays gated as in Docker's default"
        }
    }

    It 'copies the filter beside compose.yaml on the server, where Compose reads it from' {
        $server = New-FakeServer
        Invoke-Deploy $server | Out-Null
        $server.Scripts[$server.Steps.IndexOf('install-files')] | Should -Match 'chrome-seccomp\.json'
    }

    It 'reads the owner''s own identity.env when there is one, and starts without it (#80)' {
        Get-Content (Join-Path $script:DeployFolder 'compose.yaml') -Raw |
            Should -Match '(?m)^\s+-\s*path:\s*identity\.env\s*\r?\n\s+required:\s*false\s*$'
    }

    It 'never writes, reads or uploads identity.env: it holds the owner''s secrets, which stay on the server' {
        $server = New-FakeServer
        Invoke-Deploy $server | Out-Null
        foreach ($script in $server.Scripts) { $script | Should -Not -Match 'identity\.env' }
        $server.Files.Keys | Should -Not -Contain 'multiplex/identity.env'
        $server.Uploads | Should -Not -Match 'identity'
    }

    It 'runs the server inside the Tailscale device''s network, so Serve reaches it on loopback' {
        Get-Content (Join-Path $script:DeployFolder 'compose.yaml') -Raw | Should -Match '(?m)^\s+network_mode:\s*service:tailscale\s*$'
    }

    It 'gives the server an absolute data folder, on a volume that outlives the container' {
        Get-Content (Join-Path $script:DeployFolder 'Dockerfile') -Raw | Should -Match '(?m)Multiplex__DataPath=/data\b'
        Get-Content (Join-Path $script:DeployFolder 'compose.yaml') -Raw | Should -Match '(?m)^\s+-\s*multiplex-data:/data\s*$'
    }

    It 'listens on loopback only, and runs as a user other than root' {
        $dockerfile = Get-Content (Join-Path $script:DeployFolder 'Dockerfile') -Raw
        $dockerfile | Should -Match 'ASPNETCORE_URLS=http://localhost:5190'
        $dockerfile | Should -Match '(?m)^USER app\s*$'
    }

    It 'keeps the sign-in browser''s sandbox: nothing turns it off' {
        foreach ($file in Get-ChildItem $script:DeployFolder -File) {
            Get-Content $file.FullName -Raw | Should -Not -Match 'NoSandbox|no-sandbox' -Because $file.Name
        }
    }

    It 'ships shell scripts that parse' {
        if (-not $script:Bash) { Set-ItResult -Skipped -Because 'no bash to check with on this PC'; return }
        foreach ($file in Get-ChildItem $script:DeployFolder -Filter '*.sh') {
            $out = & $script:Bash -n $file.FullName 2>&1
            $LASTEXITCODE | Should -Be 0 -Because "$($file.Name): $out"
        }
    }

    It 'stores deploy files with Linux line endings, so the shell script runs' {
        foreach ($file in Get-ChildItem $script:DeployFolder -File) {
            [IO.File]::ReadAllText($file.FullName) | Should -Not -Match "`r" -Because $file.Name
        }
    }
}

Describe 'Deploy-MultiplexLinux.ps1' {
    It 'takes one parameter, the SSH target' {
        $command = Get-Command (Join-Path $PSScriptRoot 'Deploy-MultiplexLinux.ps1')
        $own = $command.Parameters.Keys | Where-Object { $_ -notin [System.Management.Automation.PSCmdlet]::CommonParameters }
        $own | Should -Be @('Target')
    }
}
