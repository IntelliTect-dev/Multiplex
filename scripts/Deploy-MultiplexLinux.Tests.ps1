#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
# Tests for deploying Multiplex to a Linux server over SSH (Multiplex.Linux.psm1, Deploy-MultiplexLinux.ps1,
# deploy/linux). The server is faked: each remote script starts with a "# step: <name>" line, and the
# fake answers by step, so these tests run anywhere without SSH or Docker.
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'Multiplex.Linux.psm1') -Force
    $script:DeployFolder = Join-Path $PSScriptRoot '..' 'deploy' 'linux'
    $script:Hash = 'pbkdf2-sha256$210000$AbCdEf+/==$Zz09+/=='

    # A fake server. -Status is a list of `tailscale status --json` answers, given in turn (the last repeats).
    # A remote script that writes files carries, per file, a "# file: <path>" line and the content as base64.
    function New-FakeServer {
        param(
            [string] $Arch = 'x86_64',
            [string] $Docker = 'docker=27.3.1',
            [string] $Compose = 'compose=2.29.7',
            [bool] $BuildFails = $false,
            [object[]] $Status = @('{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net."},"CertDomains":["multiplex.tail1234.ts.net"]}'),
            [bool] $Healthy = $true)
        $server = [pscustomobject]@{
            Steps = [System.Collections.Generic.List[string]]::new()
            Files = [ordered]@{}
            Uploads = [System.Collections.Generic.List[string]]::new()
            StatusCalls = 0
        }
        $remote = {
            param([string] $Script)
            $step = if ($Script -match '(?m)^# step: (\S+)') { $Matches[1] }
            $server.Steps.Add($step)
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
                default { return [pscustomobject]@{ ExitCode = 0; Output = '' } }
            }
        }.GetNewClosure()
        $upload = { param([string] $LocalFile, [string] $RemotePath) $server.Uploads.Add($RemotePath) }.GetNewClosure()
        $server | Add-Member -NotePropertyName Remote -NotePropertyValue $remote
        $server | Add-Member -NotePropertyName Upload -NotePropertyValue $upload
        return $server
    }

    $script:GoodPublish = { param([string] $Folder) New-Item -ItemType Directory -Force -Path $Folder | Out-Null; Set-Content (Join-Path $Folder 'Multiplex.Web.dll') 'x' }

    function Invoke-Deploy($Server, [scriptblock] $Publish = $script:GoodPublish) {
        Invoke-MultiplexLinuxDeploy -Remote $Server.Remote -Upload $Server.Upload -Publish $Publish `
            -PasscodeHash $script:Hash -Tag '20261120-1200-abc1234' -PollInterval ([TimeSpan]::Zero) `
            -LoginTimeout ([TimeSpan]::FromSeconds(1)) -HealthTimeout ([TimeSpan]::FromSeconds(1)) 6>&1
    }
}

Describe 'the server''s environment file' {
    It 'passes the passcode hash and address literally: single-quoted, so Docker Compose never expands the hash''s $ signs' {
        $text = ConvertTo-MultiplexEnvFile ([ordered]@{ Multiplex__PublicBaseUrl = 'https://multiplex.tail1234.ts.net'; Multiplex__Gate__PasscodeHash = $script:Hash })
        $text | Should -Be ("Multiplex__PublicBaseUrl='https://multiplex.tail1234.ts.net'`nMultiplex__Gate__PasscodeHash='$($script:Hash)'`n")
    }

    It 'uses Linux line endings only' {
        ConvertTo-MultiplexEnvFile ([ordered]@{ A = '1'; B = '2' }) | Should -Not -Match "`r"
    }

    It 'refuses a value it cannot quote, rather than writing a file Compose would misread' {
        { ConvertTo-MultiplexEnvFile @{ A = "it's" } } | Should -Throw '*A*'
        { ConvertTo-MultiplexEnvFile @{ A = "two`nlines" } } | Should -Throw '*A*'
    }

    It 'holds the public address without a trailing slash, and the passcode hash' {
        $env = Get-MultiplexLinuxServerEnvironment -PublicBaseUrl 'https://multiplex.tail1234.ts.net/' -PasscodeHash $script:Hash
        $env.Multiplex__PublicBaseUrl | Should -Be 'https://multiplex.tail1234.ts.net'
        $env.Multiplex__Gate__PasscodeHash | Should -Be $script:Hash
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

    It 'gives the server its tailnet address and the passcode hash, in a file only its owner can read' {
        $server = New-FakeServer
        Invoke-Deploy $server | Out-Null
        $server.Files['multiplex/multiplex.env'] | Should -Be ("Multiplex__PublicBaseUrl='https://multiplex.tail1234.ts.net'`nMultiplex__Gate__PasscodeHash='$($script:Hash)'`n")
    }

    It 'on a first deploy, shows the link that adds the device to the tailnet and waits until it is added' {
        $server = New-FakeServer -Status @(
            '{"BackendState":"NeedsLogin","AuthURL":"","Self":{"DNSName":""}}',
            '{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/1a2b3c","Self":{"DNSName":""}}',
            '{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/1a2b3c","Self":{"DNSName":""}}',
            '{"BackendState":"Running","Self":{"DNSName":"multiplex.tail1234.ts.net."},"CertDomains":["multiplex.tail1234.ts.net"]}')
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

    It 'reports the server''s own log when it does not become healthy' {
        $server = New-FakeServer -Healthy $false
        { Invoke-Deploy $server } | Should -Throw '*InvalidOperationException: boom*'
    }

    It 'removes the upload and old images after a good deploy' {
        $server = New-FakeServer
        Invoke-Deploy $server | Out-Null
        $server.Steps[-1] | Should -Be 'cleanup'
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
}

Describe 'remote scripts' {
    It 'are sent as base64, so no quoting on this PC or the server can change them' {
        $command = Get-MultiplexRemoteCommand -Script "# step: x`necho 'a `"b`" `$c'`r`n"
        $command | Should -Match '^echo [A-Za-z0-9+/=]+ \| base64 -d \| bash$'
        $b64 = ($command -split ' ')[1]
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)) | Should -Be "# step: x`necho 'a `"b`" `$c'`n"
    }
}

Describe 'what the Linux host publishes' {
    It 'serves only the tailnet, through Tailscale''s HTTPS proxy to the server on loopback' {
        $serve = Get-Content (Join-Path $script:DeployFolder 'serve.json') -Raw | ConvertFrom-Json -AsHashtable
        $serve.TCP['443'].HTTPS | Should -BeTrue
        $serve.Web['${TS_CERT_DOMAIN}:443'].Handlers['/'].Proxy | Should -Be 'http://127.0.0.1:5190'
        $serve.ContainsKey('AllowFunnel') | Should -BeFalse -Because 'the owner chose tailnet only (#97)'
    }

    It 'never forwards raw TCP: it would drop the forwarding headers, and every caller would look like the host itself (#80)' {
        $serve = Get-Content (Join-Path $script:DeployFolder 'serve.json') -Raw | ConvertFrom-Json -AsHashtable
        foreach ($port in $serve.TCP.Keys) {
            $serve.TCP[$port].ContainsKey('TCPForward') | Should -BeFalse
            $serve.TCP[$port].ContainsKey('TerminateTLS') | Should -BeFalse
        }
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
