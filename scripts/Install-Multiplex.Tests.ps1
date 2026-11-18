#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'Multiplex.Install.psm1') -Force
}

Describe 'owner passcode' {
    It 'generates phone-friendly passcodes without look-alike characters' {
        $p = New-MultiplexPasscode
        $p | Should -Match '^[a-z2-9]{4}(-[a-z2-9]{4}){3}$'
        $p | Should -Not -Match '[lio01]'
    }

    It 'hashes in the server format and verifies the same passcode only' {
        $hash = ConvertTo-MultiplexPasscodeHash -Passcode 'abcd-efgh-jkmn-pqrs'
        $hash | Should -Match '^pbkdf2-sha256\$210000\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+$'
        Test-MultiplexPasscodeHash -Passcode 'abcd-efgh-jkmn-pqrs' -Hash $hash | Should -BeTrue
        Test-MultiplexPasscodeHash -Passcode 'abcd-efgh-jkmn-pqrt' -Hash $hash | Should -BeFalse
    }

    It 'salts every hash' {
        (ConvertTo-MultiplexPasscodeHash -Passcode 'x') | Should -Not -Be (ConvertTo-MultiplexPasscodeHash -Passcode 'x')
    }

    It 'matches a known PBKDF2-SHA256 vector so the server can verify it' {
        # RFC 7914 section 11 test vector (P="passwd", S="salt", c=1, dkLen=64), first 32 bytes.
        $hash = ConvertTo-MultiplexPasscodeHash -Passcode 'passwd' -Iterations 1 -Salt ([Text.Encoding]::UTF8.GetBytes('salt'))
        $bytes = [Convert]::FromBase64String($hash.Split('$')[3])
        [Convert]::ToHexString($bytes).ToLowerInvariant() | Should -Be '55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc'
    }

    It 'rejects malformed hashes' {
        Test-MultiplexPasscodeHash -Passcode 'x' -Hash 'sha1$1$a$b' | Should -BeFalse
        Test-MultiplexPasscodeHash -Passcode 'x' -Hash 'garbage' | Should -BeFalse
    }

    It 'round-trips the saved secret for the current user only (DPAPI)' -Skip:(-not $IsWindows) {
        $path = Join-Path $TestDrive 'passcode.dpapi'
        Save-MultiplexPasscodeSecret -Passcode 'abcd-efgh-jkmn-pqrs' -Path $path
        (Get-Content -LiteralPath $path -Raw) | Should -Not -Match 'abcd'
        Read-MultiplexPasscodeSecret -Path $path | Should -Be 'abcd-efgh-jkmn-pqrs'
    }
}

Describe 'configuration' {
    It 'defaults sensibly when no file exists' {
        $c = Read-MultiplexConfig -Path (Join-Path $TestDrive 'none.json')
        $c.Port | Should -Be 5190
        $c.Tunnel | Should -Be 'None'
    }

    It 'round-trips and maps to the server environment' {
        $path = Join-Path $TestDrive 'multiplex.config.json'
        $c = Read-MultiplexConfig -Path $path
        $c.PublicBaseUrl = 'https://box.example.ts.net/'
        $c.PasscodeHash = 'pbkdf2-sha256$1$a$b'
        Write-MultiplexConfig -Config $c -Path $path
        $env = Get-MultiplexServerEnvironment -Config (Read-MultiplexConfig -Path $path) -Root 'C:\M'
        $env['Multiplex__PublicBaseUrl'] | Should -Be 'https://box.example.ts.net'
        $env['Multiplex__Gate__PasscodeHash'] | Should -Be 'pbkdf2-sha256$1$a$b'
        $env['ASPNETCORE_URLS'] | Should -Be 'http://localhost:5190'
        $env['Multiplex__DataPath'] | Should -Be (Join-Path 'C:\M' 'data')
    }

    It 'falls back to the local address when there is no public one' {
        $c = Read-MultiplexConfig -Path (Join-Path $TestDrive 'none2.json')
        (Get-MultiplexServerEnvironment -Config $c -Root 'C:\M')['Multiplex__PublicBaseUrl'] | Should -Be 'http://localhost:5190'
    }
}

Describe 'public address' {
    BeforeEach {
        $script:path = Join-Path $TestDrive "public-$([guid]::NewGuid().ToString('N')).json"
        $script:config = Read-MultiplexConfig -Path $script:path
        $script:config.PublicBaseUrl = 'https://old-tunnel.example.ts.net'
        Write-MultiplexConfig -Config $script:config -Path $script:path
    }

    It 'the installer drops an address left from an earlier tunnel when the tunnel is None' {
        $script:config.Tunnel = 'None'
        Clear-MultiplexStalePublicAddress -Config $script:config
        $script:config.PublicBaseUrl | Should -BeNullOrEmpty
        (Get-MultiplexServerEnvironment -Config $script:config -Root 'C:\M')['Multiplex__PublicBaseUrl'] | Should -Be 'http://localhost:5190'
    }

    It 'the installer keeps the address while a tunnel is configured (the start script sets it)' {
        $script:config.Tunnel = 'Tailscale'
        Clear-MultiplexStalePublicAddress -Config $script:config
        $script:config.PublicBaseUrl | Should -Be 'https://old-tunnel.example.ts.net'
    }

    It 'starting without a tunnel clears the recorded address on disk' {
        $script:config.Tunnel = 'None'
        Start-MultiplexTunnel -Config $script:config -ConfigPath $script:path -LogDir $TestDrive | Should -BeNullOrEmpty
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
    }

}

Describe 'Tailscale Funnel at start-up' -Skip:(-not $IsWindows) {
    # A fake tailscale.cmd, first on PATH, answers each subcommand from files beside it:
    # <name>.out (stdout), <name>.err (stderr), <name>.code (exit code), <name>.hang (then waits,
    # as `tailscale funnel` does while Funnel still needs enabling). Every call is logged.
    BeforeAll {
        function New-FakeTailscale {
            param([Parameter(Mandatory)] [string] $Dir, [Parameter(Mandatory)] [hashtable] $Responses)
            New-Item -ItemType Directory -Force -Path $Dir | Out-Null
            Set-Content -LiteralPath (Join-Path $Dir 'tailscale.cmd') -Value @(
                '@echo off'
                'setlocal'
                'set "name=%~1"'
                'if /i "%~1 %~2"=="funnel status" set "name=funnel-status"'
                'echo %*>>"%~dp0calls.log"'
                'if exist "%~dp0%name%.out" type "%~dp0%name%.out"'
                'if exist "%~dp0%name%.err" type "%~dp0%name%.err" 1>&2'
                'if exist "%~dp0%name%.hang" ping -n 60 127.0.0.1 >nul'
                'set "code=99"'
                'if exist "%~dp0%name%.code" set /p code=<"%~dp0%name%.code"'
                'exit /b %code%')
            foreach ($name in $Responses.Keys) {
                $r = $Responses[$name]
                Set-Content -LiteralPath (Join-Path $Dir "$name.code") -Value "$($r.Exit ?? 0)" -NoNewline
                if ($r.Contains('Out')) { Set-Content -LiteralPath (Join-Path $Dir "$name.out") -Value $r.Out }
                if ($r.Contains('Err')) { Set-Content -LiteralPath (Join-Path $Dir "$name.err") -Value $r.Err }
                if ($r.Hang) { Set-Content -LiteralPath (Join-Path $Dir "$name.hang") -Value '' }
            }
        }
        $script:refused = 'failed to connect to local tailscaled (which appears to be running as tailscaled.exe, pid 6272). Got error: 401 Unauthorized: Tailscale running in server mode ("PC\\Owner"); connection from "unknown-user-S-1-12-1-1" not allowed'
        $script:running = '{ "BackendState": "Running", "Self": { "DNSName": "box.example.ts.net." } }'
        $script:funnelOn = '{ "AllowFunnel": { "box.example.ts.net:443": true } }'
        $script:savedPath = $env:PATH
    }

    BeforeEach {
        $script:fake = Join-Path $TestDrive "ts-$([guid]::NewGuid().ToString('N'))"
        $script:logDir = Join-Path $script:fake 'logs'
        New-Item -ItemType Directory -Force -Path $script:logDir | Out-Null
        $env:PATH = "$script:fake;$script:savedPath"
        $script:path = Join-Path $script:fake 'multiplex.config.json'
        $script:config = Read-MultiplexConfig -Path $script:path
        $script:config.Tunnel = 'Tailscale'
        $script:config.PublicBaseUrl = 'https://old-tunnel.example.ts.net'   # left from an earlier start
        Write-MultiplexConfig -Config $script:config -Path $script:path
        function Start-Tunnel {
            Start-MultiplexTunnel -Config $script:config -ConfigPath $script:path -LogDir $script:logDir -WarningVariable script:warned -WarningAction SilentlyContinue 6>$null
        }
        function Get-TunnelLog { Get-Content -LiteralPath (Join-Path $script:logDir 'tunnel.log') -Raw -ErrorAction SilentlyContinue }
        function Get-Calls { @(Get-Content -LiteralPath (Join-Path $script:fake 'calls.log') -ErrorAction SilentlyContinue) }
    }

    AfterEach {
        $env:PATH = $script:savedPath
        InModuleScope Multiplex.Install { $script:TailscaleTimeoutSeconds = 20 }
    }

    It 'Tailscale owned by another Windows account: serves locally, and says whose it is and what to do' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Exit = 1; Err = $script:refused } }
        { Start-Tunnel } | Should -Not -Throw
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
        $log = Get-TunnelLog
        $log | Should -Match ([regex]::Escape('runs unattended for the Windows account PC\Owner'))
        $log | Should -Match 'Run unattended'
        "$script:warned" | Should -Match ([regex]::Escape('PC\Owner'))
        Get-Calls | Should -Not -Match '^funnel'   # no Funnel attempt once Tailscale has refused
    }

    It 'a non-zero exit with nothing said: serves locally and reports the exit code' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Exit = 3 } }
        { Start-Tunnel } | Should -Not -Throw
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
        Get-TunnelLog | Should -Match 'tailscale status --json failed with exit code 3'
    }

    It 'a status that prints nothing: serves locally and says so' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Exit = 0 } }
        { Start-Tunnel } | Should -Not -Throw
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
        Get-TunnelLog | Should -Match 'tailscale status --json printed nothing'
    }

    It 'a status that is not JSON, or JSON without this PC''s name: serves locally and says so' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Out = 'Health check: { truncated' } }
        { Start-Tunnel } | Should -Not -Throw
        Get-TunnelLog | Should -Match 'not JSON.*Health check'

        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Out = '[ 1, 2 ]' } }
        { Start-Tunnel } | Should -Not -Throw
        Get-TunnelLog | Should -Match "did not report this PC's DNS name"
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
    }

    It 'Tailscale signed out: serves locally and says to sign in' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Out = '{ "BackendState": "NeedsLogin", "Self": { "DNSName": "" } }' } }
        { Start-Tunnel } | Should -Not -Throw
        Get-TunnelLog | Should -Match 'not signed in'
        Get-Calls | Should -Not -Match '^funnel'
    }

    It 'a refused Funnel: serves locally and reports Tailscale''s own words' {
        New-FakeTailscale -Dir $script:fake -Responses @{
            status   = @{ Out = $script:running }
            'funnel' = @{ Exit = 1; Err = 'error enabling https feature: permission denied' } }
        { Start-Tunnel } | Should -Not -Throw
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
        Get-TunnelLog | Should -Match 'exit code 1.*error enabling https feature: permission denied'
    }

    It 'a Funnel that waits to be enabled is stopped, and its enable link reported' {
        New-FakeTailscale -Dir $script:fake -Responses @{
            status   = @{ Out = $script:running }
            'funnel' = @{ Out = 'Funnel is not enabled on your tailnet. To enable, visit: https://login.tailscale.com/f/funnel?node=abc'; Hang = $true } }
        InModuleScope Multiplex.Install { $script:TailscaleTimeoutSeconds = 3 }   # short only here: a slow PC must not time out the other calls
        $elapsed = Measure-Command { { Start-Tunnel } | Should -Not -Throw }
        $elapsed.TotalSeconds | Should -BeLessThan 20
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
        Get-TunnelLog | Should -Match 'did not finish within 3 s'
        Get-TunnelLog | Should -Match ([regex]::Escape('https://login.tailscale.com/f/funnel?node=abc'))
    }

    It 'a Funnel that reports success but is not open to the internet is not counted as started' {
        New-FakeTailscale -Dir $script:fake -Responses @{
            status          = @{ Out = $script:running }
            'funnel'        = @{ Out = 'Available within your tailnet:' }
            'funnel-status' = @{ Out = '{ "AllowFunnel": { "other.example.ts.net:443": true } }' } }
        { Start-Tunnel } | Should -Not -Throw
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
        Get-TunnelLog | Should -Match ([regex]::Escape('does not show https://box.example.ts.net open to the internet'))
    }

    It 'a confirmed Funnel records its address' {
        New-FakeTailscale -Dir $script:fake -Responses @{
            status          = @{ Out = $script:running }
            'funnel'        = @{ Out = "Available on the internet:`nhttps://box.example.ts.net/" }
            'funnel-status' = @{ Out = $script:funnelOn } }
        Start-Tunnel
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -Be 'https://box.example.ts.net'
        Get-TunnelLog | Should -BeNullOrEmpty
        Get-Calls | Should -Contain 'funnel --bg --yes 5190'
    }

    It 'Tailscale that cannot be run at all is reported, not thrown' {
        $result = InModuleScope Multiplex.Install { Get-TailscaleDnsName -Tailscale (Join-Path $TestDrive 'absent\tailscale.exe') }
        $result.DnsName | Should -BeNullOrEmpty
        $result.Problem | Should -Match 'could not be run'
    }

    Context 'the start script' {
        BeforeEach {
            # A real start: the install module and start script beside a stand-in server that
            # prints this PC's name and exits.
            $script:root = Join-Path $TestDrive "root-$([guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Force -Path (Join-Path $script:root 'app') | Out-Null
            Copy-Item (Join-Path $PSScriptRoot 'Start-Multiplex.ps1'), (Join-Path $PSScriptRoot 'Multiplex.Install.psm1') -Destination $script:root
            Copy-Item (Join-Path $env:SystemRoot 'System32\HOSTNAME.EXE') (Join-Path $script:root 'app\Multiplex.Web.exe')
            $c = Read-MultiplexConfig -Path (Join-Path $script:root 'multiplex.config.json')
            $c.Tunnel = 'Tailscale'
            Write-MultiplexConfig -Config $c -Path (Join-Path $script:root 'multiplex.config.json')
            function Invoke-Start { & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $script:root 'Start-Multiplex.ps1') *>&1 | Out-Null; $LASTEXITCODE }
            function Get-Log([string] $Name) { Get-Content -LiteralPath (Join-Path $script:root "logs\$Name") -Raw -ErrorAction SilentlyContinue }
        }

        It 'starts the server when Tailscale refuses this account, and logs why it serves locally' {
            New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Exit = 1; Err = $script:refused } }
            Invoke-Start | Should -Be 0
            Get-Log 'server.log' | Should -Match ([regex]::Escape([Environment]::MachineName))
            Get-Log 'tunnel.log' | Should -Match ([regex]::Escape('PC\Owner'))
            Get-Log 'start.log' | Should -Match 'starting the server'
        }

        It 'logs the step it reached when start-up fails' {
            Set-Content -LiteralPath (Join-Path $script:root 'multiplex.config.json') -Value '{ not json'
            Invoke-Start | Should -Not -Be 0
            Get-Log 'start.log' | Should -Match 'Start-up failed while reading the configuration'
        }
    }
}

Describe 'start-up report' {
    It 'shows the step start-up reached, the tunnel log and the end of the server log' {
        $logs = Join-Path $TestDrive "report-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $logs | Out-Null
        Set-Content -LiteralPath (Join-Path $logs 'start.log') -Value '2026-11-18T06:12:10 Start-up failed while starting the server: access denied'
        Set-Content -LiteralPath (Join-Path $logs 'tunnel.log') -Value '2026-11-18T06:12:10 Tailscale Funnel is not available: refused'
        Set-Content -LiteralPath (Join-Path $logs 'server.log') -Value (1..30 | ForEach-Object { "server line $_" })
        $report = Get-MultiplexStartReport -LogDir $logs
        $report | Should -Match ([regex]::Escape("Last lines of $(Join-Path $logs 'start.log')"))
        $report | Should -Match 'Start-up failed while starting the server: access denied'
        $report | Should -Match 'Tailscale Funnel is not available: refused'
        $report | Should -Match 'server line 30'
        $report | Should -Not -Match 'server line 20\b'
    }

    It 'says when nothing was logged at all' {
        $logs = Join-Path $TestDrive "empty-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $logs | Out-Null
        Get-MultiplexStartReport -LogDir $logs | Should -Match 'Nothing was logged'
    }
}

Describe 'server log' {
    It 'rotates a log over the limit, keeping one earlier log' {
        $log = Join-Path $TestDrive 'server.log'
        'x' * 2048 | Set-Content -LiteralPath $log -NoNewline
        'older' | Set-Content -LiteralPath "$log.1"
        Limit-MultiplexLog -Path $log -MaxBytes 1KB
        Test-Path -LiteralPath $log | Should -BeFalse
        (Get-Content -LiteralPath "$log.1" -Raw).Length | Should -Be 2048
        @(Get-ChildItem -LiteralPath $TestDrive -Filter 'server.log*').Count | Should -Be 1
    }

    It 'rotates at about 10 MB by default and leaves smaller logs alone' {
        $big = Join-Path $TestDrive 'big.log'
        $small = Join-Path $TestDrive 'small.log'
        $f = [IO.File]::Create($big); $f.SetLength(11MB); $f.Dispose()
        $f = [IO.File]::Create($small); $f.SetLength(9MB); $f.Dispose()
        Limit-MultiplexLog -Path $big
        Limit-MultiplexLog -Path $small
        Test-Path -LiteralPath "$big.1" | Should -BeTrue
        Test-Path -LiteralPath "$small.1" | Should -BeFalse
        (Get-Item -LiteralPath $small).Length | Should -Be 9MB
    }

    It 'does nothing when there is no log yet' {
        { Limit-MultiplexLog -Path (Join-Path $TestDrive 'absent.log') } | Should -Not -Throw
    }

    It 'only warns when the log cannot be rotated, so the server still starts' {
        $log = Join-Path $TestDrive 'locked.log'
        'x' * 2048 | Set-Content -LiteralPath $log -NoNewline
        Mock -ModuleName Multiplex.Install Move-Item { throw 'The process cannot access the file' }
        Limit-MultiplexLog -Path $log -MaxBytes 1KB -WarningVariable warned -WarningAction SilentlyContinue
        "$warned" | Should -Match 'Could not rotate'
        Test-Path -LiteralPath $log | Should -BeTrue
    }
}

Describe 'replacing the installed server' {
    BeforeEach {
        $script:root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:app = Join-Path $script:root 'app'
        New-Item -ItemType Directory -Path $script:app -Force | Out-Null
        'v1' | Set-Content -LiteralPath (Join-Path $script:app 'version.txt')
        $script:calls = @{ Stop = 0; VersionWhenStopped = $null }
        $script:stop = {
            $script:calls.Stop++
            $script:calls.VersionWhenStopped = (Get-Content -LiteralPath (Join-Path $script:app 'version.txt') -Raw).Trim()
        }
        $script:publishV2 = { param($to) New-Item -ItemType Directory -Path $to | Out-Null; 'v2' | Set-Content -LiteralPath (Join-Path $to 'version.txt') }
    }

    It 'a failed publish leaves the installed server untouched and never stops it' {
        { Update-MultiplexApp -AppDir $script:app -StopServer $script:stop -Publish {
                param($to) New-Item -ItemType Directory -Path $to | Out-Null; 'half' | Set-Content -LiteralPath (Join-Path $to 'version.txt'); throw 'build failed' } } |
            Should -Throw '*build failed*'
        (Get-Content -LiteralPath (Join-Path $script:app 'version.txt') -Raw).Trim() | Should -Be 'v1'
        $script:calls.Stop | Should -Be 0
        @(Get-ChildItem -LiteralPath $script:root).Name | Should -Be @('app')
    }

    It 'stops the server only after a good publish, then swaps in the new build' {
        Update-MultiplexApp -AppDir $script:app -StopServer $script:stop -Publish $script:publishV2
        $script:calls.Stop | Should -Be 1
        $script:calls.VersionWhenStopped | Should -Be 'v1'
        (Get-Content -LiteralPath (Join-Path $script:app 'version.txt') -Raw).Trim() | Should -Be 'v2'
        @(Get-ChildItem -LiteralPath $script:root).Name | Should -Be @('app')
    }

    It 'can be re-run after an interrupted swap, restoring the last good install if the build fails' {
        Move-Item -LiteralPath $script:app -Destination "$($script:app).old"
        New-Item -ItemType Directory -Path "$($script:app).new" | Out-Null
        { Update-MultiplexApp -AppDir $script:app -StopServer {} -Publish { throw 'build failed' } } | Should -Throw
        (Get-Content -LiteralPath (Join-Path $script:app 'version.txt') -Raw).Trim() | Should -Be 'v1'
        @(Get-ChildItem -LiteralPath $script:root).Name | Should -Be @('app')

        Update-MultiplexApp -AppDir $script:app -StopServer {} -Publish $script:publishV2
        (Get-Content -LiteralPath (Join-Path $script:app 'version.txt') -Raw).Trim() | Should -Be 'v2'
    }

    It 'installs into an empty root on a first install' {
        Remove-Item -LiteralPath $script:app -Recurse -Force
        Update-MultiplexApp -AppDir $script:app -StopServer {} -Publish $script:publishV2
        (Get-Content -LiteralPath (Join-Path $script:app 'version.txt') -Raw).Trim() | Should -Be 'v2'
        @(Get-ChildItem -LiteralPath $script:root).Name | Should -Be @('app')
    }

    It 'changes nothing under -WhatIf' {
        Update-MultiplexApp -AppDir $script:app -StopServer $script:stop -Publish $script:publishV2 -WhatIf
        (Get-Content -LiteralPath (Join-Path $script:app 'version.txt') -Raw).Trim() | Should -Be 'v1'
        $script:calls.Stop | Should -Be 0
    }

    It 'stops only processes inside the app folder, not a sibling such as the staging folder' {
        Test-MultiplexPathUnder -Path 'C:\M\app\Multiplex.Web.exe' -Folder 'C:\M\app' | Should -BeTrue
        Test-MultiplexPathUnder -Path 'C:\M\app\Multiplex.Web.exe' -Folder 'C:\M\app\' | Should -BeTrue
        Test-MultiplexPathUnder -Path 'C:\M\app.new\Multiplex.Web.exe' -Folder 'C:\M\app' | Should -BeFalse
        Test-MultiplexPathUnder -Path 'C:\M\app2\Multiplex.Web.exe' -Folder 'C:\M\app' | Should -BeFalse
    }
}

Describe 'install folder' {
    It 'defaults to a folder in the user profile, outside AppData' {
        $root = Get-MultiplexRoot
        $root | Should -Be (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.multiplex')
    }

    It 'refuses an AppData folder when AppData is redirected, as it is from the Claude desktop app' {
        $local = [Environment]::GetFolderPath('LocalApplicationData')
        Get-MultiplexRootProblem -Root (Join-Path $local 'Multiplex') -AppDataRedirected $true | Should -Match 'private copy'
        Get-MultiplexRootProblem -Root (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Multiplex') -AppDataRedirected $true | Should -Not -BeNullOrEmpty
    }

    It 'allows AppData when it is not redirected, and Temp or the user profile always' {
        $local = [Environment]::GetFolderPath('LocalApplicationData')
        Get-MultiplexRootProblem -Root (Join-Path $local 'Multiplex') -AppDataRedirected $false | Should -BeNullOrEmpty
        Get-MultiplexRootProblem -Root (Join-Path $local 'Temp\x') -AppDataRedirected $true | Should -BeNullOrEmpty
        Get-MultiplexRootProblem -Root (Get-MultiplexRoot) -AppDataRedirected $true | Should -BeNullOrEmpty
    }

    It 'probes AppData redirection without leaving anything behind' -Skip:(-not $IsWindows) {
        $before = @(Get-ChildItem ([Environment]::GetFolderPath('LocalApplicationData')) -Filter 'multiplex-probe-*' -Directory).Count
        (Test-MultiplexAppDataRedirected) | Should -BeOfType [bool]
        @(Get-ChildItem ([Environment]::GetFolderPath('LocalApplicationData')) -Filter 'multiplex-probe-*' -Directory).Count | Should -Be $before
    }
}

Describe 'no TripIt account list' {
    # Each connection added in Claude or ChatGPT is bound to the TripIt account signed in while it
    # is added, behind the passcode, so the installer neither needs nor asks for an account.
    BeforeAll {
        # The real installer in its own process, into a throwaway folder: -WhatIf skips the publish,
        # the passcode file and the configuration write; -NoScheduledTask and -NoStart leave the
        # logon task and any running server alone.
        function Invoke-Install([string] $Root) {
            $output = & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'Install-Multiplex.ps1') `
                -Root $Root -NoScheduledTask -NoStart -WhatIf *>&1 | Out-String
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
        }
    }

    It 'the installer has no -AllowedAccount parameter' {
        (Get-Command (Join-Path $PSScriptRoot 'Install-Multiplex.ps1')).Parameters.Keys | Should -Not -Contain 'AllowedAccount'
    }

    It 'installs without being given a TripIt account' {
        $result = Invoke-Install -Root (Join-Path $TestDrive "fresh-$([guid]::NewGuid().ToString('N'))")
        $result.Output | Should -Not -Match 'AllowedAccount'
        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'Connector address'
    }

    It 'still installs over a configuration that has an account list, and says the list is no longer used' {
        $root = Join-Path $TestDrive "old-$([guid]::NewGuid().ToString('N'))"
        $path = Join-Path $root 'multiplex.config.json'
        $old = Read-MultiplexConfig -Path $path
        $old.AllowedAccounts = [ordered]@{ tripit = @('owner@example.com') }
        Write-MultiplexConfig -Config $old -Path $path

        $result = Invoke-Install -Root $root

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'TripIt account list.*no longer used'
        $result.Output | Should -Match 'Connector address'
    }

    It 'never passes an old account list to the server' {
        $c = Read-MultiplexConfig -Path (Join-Path $TestDrive 'old-list.json')
        $c.AllowedAccounts = [ordered]@{ tripit = @('a@example.com', 'b@example.com') }
        $env = Get-MultiplexServerEnvironment -Config $c -Root 'C:\M'
        @($env.Keys | Where-Object { $_ -match 'AllowedAccounts' }) | Should -BeNullOrEmpty
        $env['ASPNETCORE_URLS'] | Should -Be 'http://localhost:5190'
    }

    It 'a new configuration has no account list' {
        (Read-MultiplexConfig -Path (Join-Path $TestDrive 'new.json')).Contains('AllowedAccounts') | Should -BeFalse
    }
}

Describe 'Claude Desktop bridge' {
    It 'adds an entry while keeping other servers and settings, and backs up the file' {
        $path = Join-Path $TestDrive 'claude_desktop_config.json'
        '{ "mcpServers": { "other": { "command": "x" } }, "globalShortcut": "Ctrl+Space" }' | Set-Content $path
        Set-ClaudeDesktopBridge -ConfigPath $path -Name 'tripit' -ServerUrl 'http://localhost:5190/tripit/mcp' | Should -BeTrue
        $json = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        $json.mcpServers.other.command | Should -Be 'x'
        $json.globalShortcut | Should -Be 'Ctrl+Space'
        $json.mcpServers.tripit.command | Should -Be 'npx'
        $json.mcpServers.tripit.args | Should -Be @('-y', 'mcp-remote@0.16.6', 'http://localhost:5190/tripit/mcp', '--auth-timeout', '600')
        Test-Path "$path.bak" | Should -BeTrue
    }

    It 'is idempotent' {
        $path = Join-Path $TestDrive 'idem.json'
        Set-ClaudeDesktopBridge -ConfigPath $path -Name 'tripit' -ServerUrl 'http://localhost:5190/tripit/mcp' | Should -BeTrue
        $before = Get-Content $path -Raw
        Set-ClaudeDesktopBridge -ConfigPath $path -Name 'tripit' -ServerUrl 'http://localhost:5190/tripit/mcp' | Should -BeFalse
        Get-Content $path -Raw | Should -Be $before
    }

    It 'changes nothing under -WhatIf' {
        $path = Join-Path $TestDrive 'whatif.json'
        Set-ClaudeDesktopBridge -ConfigPath $path -Name 'tripit' -ServerUrl 'http://localhost:5190/tripit/mcp' -WhatIf | Out-Null
        Test-Path $path | Should -BeFalse

        $existing = Join-Path $TestDrive 'whatif-existing.json'
        '{ "mcpServers": {} }' | Set-Content $existing
        Set-ClaudeDesktopBridge -ConfigPath $existing -Name 'tripit' -ServerUrl 'http://localhost:5190/tripit/mcp' -WhatIf | Out-Null
        Get-Content $existing -Raw | Should -Be "{ `"mcpServers`": {} }$([Environment]::NewLine)"
        Test-Path "$existing.bak" | Should -BeFalse
        Test-Path "$existing.tmp" | Should -BeFalse
    }

    It 'keeps the first backup: a later change never overwrites it' {
        $path = Join-Path $TestDrive 'twice.json'
        $original = '{ "mcpServers": { "other": { "command": "x" } } }'
        $original | Set-Content $path
        Set-ClaudeDesktopBridge -ConfigPath $path -Name 'tripit' -ServerUrl 'http://localhost:5190/tripit/mcp' | Should -BeTrue
        Set-ClaudeDesktopBridge -ConfigPath $path -Name 'tripit' -ServerUrl 'http://localhost:5191/tripit/mcp' | Should -BeTrue
        (Get-Content "$path.bak" -Raw).Trim() | Should -Be $original
        (Get-Content $path -Raw | ConvertFrom-Json -AsHashtable).mcpServers.tripit.args[2] | Should -Be 'http://localhost:5191/tripit/mcp'
    }

    It 'never leaves a half-written config when the write fails' {
        $path = Join-Path $TestDrive 'failing.json'
        $original = '{ "mcpServers": { "other": { "command": "x" } } }'
        $original | Set-Content $path
        # A write that dies half way (disk full, the app holding the file, a crash).
        Mock -ModuleName Multiplex.Install Set-Content { [IO.File]::WriteAllText($LiteralPath, '{ "mcpServers": { "oth'); throw 'disk full' }
        { Set-ClaudeDesktopBridge -ConfigPath $path -Name 'tripit' -ServerUrl 'http://localhost:5190/tripit/mcp' } | Should -Throw '*disk full*'
        (Get-Content $path -Raw).Trim() | Should -Be $original
        Test-Path "$path.tmp" | Should -BeFalse
    }
}

Describe 'connect check passcode' {
    BeforeEach { $script:savedPasscode = $env:MULTIPLEX_PASSCODE }
    AfterEach { $env:MULTIPLEX_PASSCODE = $script:savedPasscode }

    It 'reads the passcode from MULTIPLEX_PASSCODE when -Passcode is absent' {
        $env:MULTIPLEX_PASSCODE = 'from-env-0000'
        Resolve-MultiplexPasscode | Should -Be 'from-env-0000'
    }

    It 'prefers an explicit -Passcode' {
        $env:MULTIPLEX_PASSCODE = 'from-env-0000'
        Resolve-MultiplexPasscode -Passcode 'given-1111' | Should -Be 'given-1111'
    }

    It 'explains how to supply it when neither is set' {
        $env:MULTIPLEX_PASSCODE = $null
        { Resolve-MultiplexPasscode } | Should -Throw '*MULTIPLEX_PASSCODE*'
    }

    It 'Test-MultiplexConnect.ps1 no longer requires the passcode on its command line' {
        $script = Get-Command (Join-Path $PSScriptRoot 'Test-MultiplexConnect.ps1')
        $script.Parameters['Passcode'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } |
            Should -BeNullOrEmpty
    }
}

Describe 'quick tunnel address' {
    It 'finds the trycloudflare address in cloudflared output' {
        $lines = @(
            '2026-11-17T08:00:00Z INF Requesting new quick Tunnel on trycloudflare.com...',
            '2026-11-17T08:00:01Z INF |  https://bright-otter-sings-loud.trycloudflare.com                    |')
        Get-QuickTunnelUrl -Lines $lines | Should -Be 'https://bright-otter-sings-loud.trycloudflare.com'
    }

    It 'returns nothing until the address appears' {
        Get-QuickTunnelUrl -Lines @('starting') | Should -BeNullOrEmpty
    }
}
