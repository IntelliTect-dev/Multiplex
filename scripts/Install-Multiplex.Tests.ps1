#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'Multiplex.Install.psm1') -Force
    # A loopback port nothing listens on: tests never use the real server's port, which a live
    # install on the same PC may hold.
    function Get-FreeLoopbackPort {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        try { $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    }
    # The real installer in its own process, into a throwaway folder: -WhatIf skips the publish,
    # the configuration write and removing anything; -NoScheduledTask and -NoStart leave the
    # logon task and any running server alone. It inherits this process's environment.
    function Invoke-Install([string] $Root) {
        $output = & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'Install-Multiplex.ps1') `
            -Root $Root -NoScheduledTask -NoStart -WhatIf *>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    $script:signInNames = 'Multiplex__Identity__Microsoft__ClientId', 'Multiplex__Identity__Microsoft__ClientSecret', 'Multiplex__Identity__AllowedEmails'
    # Sets a variable of this process (and so of the processes it starts), or removes it for $null.
    # Not [Environment]::SetEnvironmentVariable: from PowerShell its $null arrives as '', which
    # leaves the variable set, empty.
    function Set-ProcessVariable([string] $Name, $Value) {
        if ($null -eq $Value) { Remove-Item -LiteralPath "Env:$Name" -ErrorAction SilentlyContinue }
        else { Set-Item -LiteralPath "Env:$Name" -Value $Value }
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
        Write-MultiplexConfig -Config $c -Path $path
        $env = Get-MultiplexServerEnvironment -Config (Read-MultiplexConfig -Path $path) -Root 'C:\M'
        $env['Multiplex__PublicBaseUrl'] | Should -Be 'https://box.example.ts.net'
        $env['ASPNETCORE_URLS'] | Should -Be 'http://localhost:5190'
        $env['Multiplex__DataPath'] | Should -Be (Join-Path 'C:\M' 'data')
    }

    It 'falls back to the local address when there is no public one' {
        $c = Read-MultiplexConfig -Path (Join-Path $TestDrive 'none2.json')
        (Get-MultiplexServerEnvironment -Config $c -Root 'C:\M')['Multiplex__PublicBaseUrl'] | Should -Be 'http://localhost:5190'
    }

    It 'a new configuration has no passcode' {
        (Read-MultiplexConfig -Path (Join-Path $TestDrive 'new-no-passcode.json')).Contains('PasscodeHash') | Should -BeFalse
    }

    It 'passes no passcode to the server, even from a configuration an earlier install left one in' {
        $c = Read-MultiplexConfig -Path (Join-Path $TestDrive 'old-passcode.json')
        $c.PasscodeHash = 'pbkdf2-sha256$1$a$b'
        $vars = Get-MultiplexServerEnvironment -Config $c -Root 'C:\M'
        $vars.Keys | Should -Not -Contain 'Multiplex__Gate__PasscodeHash'
        @($vars.Keys | Where-Object { $_ -match 'Passcode' }) | Should -BeNullOrEmpty
        @($vars.Values | Where-Object { "$_" -match 'pbkdf2' }) | Should -BeNullOrEmpty
    }

    It 'neither sets nor clears Microsoft sign-in, which the owner sets for their Windows account' {
        # Start-Multiplex.ps1 sets every one of these for the server, so a sign-in key here (even
        # an empty one, which clears the variable) would override the owner's own setting.
        $vars = Get-MultiplexServerEnvironment -Config (Read-MultiplexConfig -Path (Join-Path $TestDrive 'none3.json')) -Root 'C:\M'
        @($vars.Keys | Where-Object { $_ -like 'Multiplex__Identity__*' }) | Should -BeNullOrEmpty
    }
}

Describe 'no owner passcode' {
    # Retired in #80: people sign in with Microsoft. An install over an earlier one drops the
    # passcode hash from the configuration and the saved passcode.dpapi, saying so for each.
    BeforeAll {
        function New-EarlierInstall {
            $root = Join-Path $TestDrive "earlier-$([guid]::NewGuid().ToString('N'))"
            $path = Join-Path $root 'multiplex.config.json'
            $c = Read-MultiplexConfig -Path $path
            $c.PasscodeHash = 'pbkdf2-sha256$210000$c2FsdA==$aGFzaA=='
            Write-MultiplexConfig -Config $c -Path $path
            Set-Content -LiteralPath (Join-Path $root 'passcode.dpapi') -Value 'encrypted' -NoNewline
            $root
        }
    }

    It 'the installer has no -ShowPasscode or -NewPasscode parameter, and its help says so' {
        $script = Join-Path $PSScriptRoot 'Install-Multiplex.ps1'
        (Get-Command $script).Parameters.Keys | Should -Not -Contain 'ShowPasscode'
        (Get-Command $script).Parameters.Keys | Should -Not -Contain 'NewPasscode'
        ((Get-Help $script -Full).alertSet.alert.Text -join ' ') |
            Should -Match ([regex]::Escape('-ShowPasscode and -NewPasscode were removed in #80'))
    }

    It 'a fresh install makes no passcode and says nothing about one' {
        $result = Invoke-Install -Root (Join-Path $TestDrive "fresh-$([guid]::NewGuid().ToString('N'))")
        $result.ExitCode | Should -Be 0
        $result.Output | Should -Not -Match '(?i)passcode'
        $result.Output | Should -Match 'Connector address'
    }

    It 'installs over an earlier install with a passcode, and says the passcode is no longer used' {
        $root = New-EarlierInstall
        $result = Invoke-Install -Root $root
        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'The owner passcode in the configuration is no longer used'
        $result.Output | Should -Match ([regex]::Escape("The saved owner passcode ($(Join-Path $root 'passcode.dpapi')) is no longer used"))
        $result.Output | Should -Match 'Connector address'
        Test-Path -LiteralPath (Join-Path $root 'passcode.dpapi') | Should -BeTrue   # -WhatIf
    }

    It 'removes the passcode hash an earlier install left in the configuration' {
        $root = New-EarlierInstall
        $path = Join-Path $root 'multiplex.config.json'
        $c = Read-MultiplexConfig -Path $path
        $said = Remove-MultiplexRetiredSetting -Config $c -Root $root 6>&1 | Out-String
        Write-MultiplexConfig -Config $c -Path $path
        (Read-MultiplexConfig -Path $path).Contains('PasscodeHash') | Should -BeFalse
        Get-Content -LiteralPath $path -Raw | Should -Not -Match 'pbkdf2'
        $said | Should -Match 'owner passcode in the configuration is no longer used'
    }

    It 'deletes the saved passcode.dpapi an earlier install left in the install folder' {
        $root = New-EarlierInstall
        $said = Remove-MultiplexRetiredSetting -Config (Read-MultiplexConfig -Path (Join-Path $root 'multiplex.config.json')) -Root $root 6>&1 | Out-String
        Test-Path -LiteralPath (Join-Path $root 'passcode.dpapi') | Should -BeFalse
        $said | Should -Match 'saved owner passcode .* is no longer used'
    }

    It 'keeps the saved passcode under -WhatIf' {
        $root = New-EarlierInstall
        Remove-MultiplexRetiredSetting -Config (Read-MultiplexConfig -Path (Join-Path $root 'multiplex.config.json')) -Root $root -WhatIf 6>$null
        Test-Path -LiteralPath (Join-Path $root 'passcode.dpapi') | Should -BeTrue
    }

    It 'says nothing when an earlier install left nothing behind' {
        $root = Join-Path $TestDrive "clean-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $root | Out-Null
        $said = Remove-MultiplexRetiredSetting -Config (Read-MultiplexConfig -Path (Join-Path $root 'multiplex.config.json')) -Root $root 6>&1 | Out-String
        $said.Trim() | Should -BeNullOrEmpty
    }
}

Describe 'Microsoft sign-in setup' {
    # The owner sets the client id, the client secret and the allowed emails for their Windows
    # account (User scope); the installer only checks whether each is set, never reads or prints
    # a value, and shows the lines that set the ones missing.
    It 'says nothing once the client id, the client secret and the allowed emails are all set' {
        Mock -ModuleName Multiplex.Install Test-MultiplexEnvironmentVariable { $true }
        Get-MultiplexSignInSetupWarning | Should -BeNullOrEmpty
    }

    It 'says nobody outside this PC can sign in, with the line that sets each setting for the Windows account and the task restart' {
        Mock -ModuleName Multiplex.Install Test-MultiplexEnvironmentVariable { $false }
        $warning = Get-MultiplexSignInSetupWarning
        $warning | Should -Match 'nobody outside this PC can sign in'
        foreach ($name in $script:signInNames) {
            $warning | Should -Match ("(?m)^\s+" + [regex]::Escape("[Environment]::SetEnvironmentVariable('$name', ") + ".*, 'User'\)\r?$")
        }
        $warning | Should -Match ([regex]::Escape('./scripts/Install-Multiplex.ps1'))
        $warning | Should -Not -Match 'Stop-ScheduledTask'
    }

    It 'has the client secret typed at a masked prompt, so it never lands in shell history' {
        Mock -ModuleName Multiplex.Install Test-MultiplexEnvironmentVariable { $false }
        Get-MultiplexSignInSetupWarning |
            Should -Match ([regex]::Escape("[Environment]::SetEnvironmentVariable('Multiplex__Identity__Microsoft__ClientSecret', (Read-Host -MaskInput 'Client secret'), 'User')"))
    }

    It 'shows lines only for the settings that are not set' {
        Mock -ModuleName Multiplex.Install Test-MultiplexEnvironmentVariable { $Name -ne 'Multiplex__Identity__Microsoft__ClientSecret' }
        $warning = Get-MultiplexSignInSetupWarning
        $warning | Should -Match 'Multiplex__Identity__Microsoft__ClientSecret'
        $warning | Should -Not -Match 'Multiplex__Identity__Microsoft__ClientId'
        $warning | Should -Not -Match 'Multiplex__Identity__AllowedEmails'
    }

    It 'shows lines that PowerShell runs as they are' {
        Mock -ModuleName Multiplex.Install Test-MultiplexEnvironmentVariable { $false }
        $commands = @((Get-MultiplexSignInSetupWarning) -split '\r?\n' | Where-Object { $_ -match '^\s+\S' } | ForEach-Object Trim)
        $commands.Count | Should -Be 4
        foreach ($command in $commands) {
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseInput($command, [ref]$null, [ref]$errors) | Out-Null
            $errors.Count | Should -Be 0 -Because $command
        }
    }

    It 'never prints a value: a secret set for this process is found, and not shown' {
        $saved = @{}
        foreach ($name in $script:signInNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
        try {
            $secret = "secret-$([guid]::NewGuid().ToString('N'))"
            Set-ProcessVariable 'Multiplex__Identity__Microsoft__ClientSecret' $secret
            Set-ProcessVariable 'Multiplex__Identity__Microsoft__ClientId' $null
            $set = Test-MultiplexEnvironmentVariable -Name 'Multiplex__Identity__Microsoft__ClientSecret'
            $set | Should -BeOfType [bool]
            $set | Should -BeTrue
            "$(Get-MultiplexSignInSetupWarning)" | Should -Not -Match $secret
        }
        finally { foreach ($name in $script:signInNames) { Set-ProcessVariable $name $saved[$name] } }
    }

    It 'a setting is set when this process has it, and not when nothing has it' {
        $name = "Multiplex__Test__$([guid]::NewGuid().ToString('N'))"
        Test-MultiplexEnvironmentVariable -Name $name | Should -BeFalse
        Set-ProcessVariable $name 'value'
        try { Test-MultiplexEnvironmentVariable -Name $name | Should -BeTrue }
        finally { Set-ProcessVariable $name $null }
    }

    It 'a setting is set when the Windows account has it (User scope), found without reading its value' -Skip:(-not $IsWindows) {
        # Only the names of the account's variables are offered: reading a value would fail here.
        Mock -ModuleName Multiplex.Install Get-Item {
            [pscustomobject]@{} | Add-Member -MemberType ScriptMethod -Name GetValueNames -Value { @('Path', 'Multiplex__Test__SetForTheAccount') } -PassThru
        } -ParameterFilter { $LiteralPath -eq 'HKCU:\Environment' }
        Test-MultiplexEnvironmentVariable -Name 'Multiplex__Test__SetForTheAccount' | Should -BeTrue
        Test-MultiplexEnvironmentVariable -Name 'Multiplex__Test__SetNowhere' | Should -BeFalse
    }

    It 'the redirect URI to register is the public address with /signin-microsoft, and there is none without one' {
        Get-MultiplexMicrosoftRedirectUri -PublicBaseUrl 'https://box.example.ts.net/' | Should -Be 'https://box.example.ts.net/signin-microsoft'
        Get-MultiplexMicrosoftRedirectUri -PublicBaseUrl $null | Should -BeNullOrEmpty
    }

    Context 'the installer' {
        BeforeEach {
            $script:savedSignIn = @{}
            foreach ($name in $script:signInNames) { $script:savedSignIn[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
        }
        AfterEach { foreach ($name in $script:signInNames) { Set-ProcessVariable $name $script:savedSignIn[$name] } }

        It 'prints the redirect URI to register for the public address' {
            $root = Join-Path $TestDrive "public-$([guid]::NewGuid().ToString('N'))"
            $c = Read-MultiplexConfig -Path (Join-Path $root 'multiplex.config.json')
            $c.Tunnel = 'Tailscale'
            $c.PublicBaseUrl = 'https://box.example.ts.net'
            Write-MultiplexConfig -Config $c -Path (Join-Path $root 'multiplex.config.json')
            $result = Invoke-Install -Root $root
            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match ([regex]::Escape('redirect URI (register it for the app, under Authentication > Web): https://box.example.ts.net/signin-microsoft'))
        }

        It 'says nothing about sign-in once all three are set, and prints none of their values' {
            $values = foreach ($name in $script:signInNames) {
                $value = "value-$([guid]::NewGuid().ToString('N'))"
                Set-ProcessVariable $name $value
                $value
            }
            $result = Invoke-Install -Root (Join-Path $TestDrive "set-up-$([guid]::NewGuid().ToString('N'))")
            $result.ExitCode | Should -Be 0
            $result.Output | Should -Not -Match 'Microsoft sign-in is not set up'
            foreach ($value in $values) { $result.Output | Should -Not -Match $value }
        }

        It 'warns, with the line to run, while the client secret is not set' -Skip:($IsWindows -and
            @((Get-Item -LiteralPath 'HKCU:\Environment').GetValueNames()) -contains 'Multiplex__Identity__Microsoft__ClientSecret') {
            Set-ProcessVariable 'Multiplex__Identity__Microsoft__ClientId' 'client-id'
            Set-ProcessVariable 'Multiplex__Identity__AllowedEmails' 'owner@example.com'
            Set-ProcessVariable 'Multiplex__Identity__Microsoft__ClientSecret' $null
            $result = Invoke-Install -Root (Join-Path $TestDrive "no-secret-$([guid]::NewGuid().ToString('N'))")
            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'nobody outside this PC can sign in'
            $result.Output | Should -Match ([regex]::Escape("(Read-Host -MaskInput 'Client secret'), 'User')"))
            $result.Output | Should -Not -Match ([regex]::Escape("SetEnvironmentVariable('Multiplex__Identity__Microsoft__ClientId'"))
            $result.Output | Should -Not -Match 'owner@example\.com'
        }
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
        # Never this PC's own Tailscale adapter, name or ports: a test that needs them says what they show.
        Mock -ModuleName Multiplex.Install Get-TailscaleDnsNameFromAdapter {
            [pscustomobject]@{ DnsName = $null; Problem = 'No network adapter on this PC has a Tailscale (*.ts.net) DNS suffix.' } }
        Mock -ModuleName Multiplex.Install Test-UrlReachesLocalPort { [pscustomobject]@{ Reached = $false; Problem = 'Not checked in this test.' } }
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

    It 'Tailscale owned by another Windows account, with a Funnel set up outside Multiplex reaching the port: uses its name and says it is managed outside Multiplex' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Exit = 1; Err = $script:refused } }
        Mock -ModuleName Multiplex.Install Get-TailscaleDnsNameFromAdapter { [pscustomobject]@{ DnsName = 'box.example.ts.net'; Problem = $null } }
        Mock -ModuleName Multiplex.Install Test-UrlReachesLocalPort { [pscustomobject]@{ Reached = $true; Problem = $null } } `
            -ParameterFilter { $Url -eq 'https://box.example.ts.net/healthz' -and $Port -eq 5190 }
        Start-Tunnel
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -Be 'https://box.example.ts.net'
        $log = Get-TunnelLog
        $log | Should -Match ([regex]::Escape('https://box.example.ts.net already reaches port 5190 on this PC'))
        $log | Should -Match 'managed outside Multiplex'
        $log | Should -Match ([regex]::Escape('PC\Owner'))   # and still why Multiplex cannot manage it itself
        "$script:warned" | Should -BeNullOrEmpty
        Get-Calls | Should -Not -Match '^funnel'   # a Funnel Multiplex does not own is never touched
    }

    It 'Tailscale owned by another Windows account, and its name does not reach the port: serves locally and says why' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Exit = 1; Err = $script:refused } }
        Mock -ModuleName Multiplex.Install Get-TailscaleDnsNameFromAdapter { [pscustomobject]@{ DnsName = 'box.example.ts.net'; Problem = $null } }
        Mock -ModuleName Multiplex.Install Test-UrlReachesLocalPort {
            [pscustomobject]@{ Reached = $false; Problem = 'https://box.example.ts.net/healthz answered HTTP 502, but not from port 5190 on this PC.' } }
        { Start-Tunnel } | Should -Not -Throw
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
        $log = Get-TunnelLog
        $log | Should -Match 'serves on this PC only'
        $log | Should -Match ([regex]::Escape('PC\Owner'))
        $log | Should -Match ([regex]::Escape('answered HTTP 502, but not from port 5190 on this PC'))
        "$script:warned" | Should -Match 'answered HTTP 502'
    }

    It 'Tailscale owned by another Windows account, and no Tailscale name found: serves locally and says why' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Exit = 1; Err = $script:refused } }
        { Start-Tunnel } | Should -Not -Throw
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
        Get-TunnelLog | Should -Match ([regex]::Escape('No network adapter on this PC has a Tailscale (*.ts.net) DNS suffix.'))
        Should -Invoke -ModuleName Multiplex.Install Test-UrlReachesLocalPort -Times 0 -Exactly
    }

    It 'a Tailscale that fails for any other reason never looks for a Funnel outside Multiplex' {
        New-FakeTailscale -Dir $script:fake -Responses @{ status = @{ Exit = 1; Err = 'failed to connect to local tailscaled; it doesn''t appear to be running' } }
        { Start-Tunnel } | Should -Not -Throw
        Get-TunnelLog | Should -Match 'not running on this PC'

        New-FakeTailscale -Dir $script:fake -Responses @{
            status   = @{ Out = $script:running }
            'funnel' = @{ Exit = 1; Err = 'error enabling https feature: permission denied' } }
        { Start-Tunnel } | Should -Not -Throw

        Should -Invoke -ModuleName Multiplex.Install Get-TailscaleDnsNameFromAdapter -Times 0 -Exactly
        Should -Invoke -ModuleName Multiplex.Install Test-UrlReachesLocalPort -Times 0 -Exactly
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
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
            # A separate process, so nothing is mocked there: on a PC with Tailscale it looks for a
            # Funnel set up outside Multiplex for real, so it must never be the real server's port.
            $c.Port = Get-FreeLoopbackPort
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

Describe 'this PC''s Tailscale name, learned without tailscale' -Skip:(-not $IsWindows) {
    # Read from the Tailscale network adapter and MagicDNS, because tailscale itself refuses an
    # account it does not run for. The Windows host name here is not the Tailscale name.
    BeforeEach {
        Mock -ModuleName Multiplex.Install Get-DnsClient {
            [pscustomobject]@{ InterfaceAlias = 'Ethernet'; InterfaceIndex = 11; ConnectionSpecificSuffix = 'localdomain' }
            [pscustomobject]@{ InterfaceAlias = 'Tailscale'; InterfaceIndex = 40; ConnectionSpecificSuffix = 'tail1234.ts.net' }
        }
        Mock -ModuleName Multiplex.Install Get-NetIPAddress { [pscustomobject]@{ IPAddress = '100.101.102.103'; AddressFamily = 'IPv4' } }
        Mock -ModuleName Multiplex.Install Resolve-DnsName {
            [pscustomobject]@{ Name = '103.102.101.100.in-addr.arpa'; Type = 'PTR'; Section = 'Answer'; NameHost = 'renamed-box.tail1234.ts.net.' } }
        function Get-Name { InModuleScope Multiplex.Install { Get-TailscaleDnsNameFromAdapter } }
    }

    It 'asks MagicDNS for the name of the Tailscale adapter''s address' {
        $result = Get-Name
        $result.DnsName | Should -Be 'renamed-box.tail1234.ts.net'
        $result.Problem | Should -BeNullOrEmpty
        Should -Invoke -ModuleName Multiplex.Install Get-NetIPAddress -ParameterFilter { $InterfaceIndex -eq 40 }
        Should -Invoke -ModuleName Multiplex.Install Resolve-DnsName -ParameterFilter {
            $Name -eq '100.101.102.103' -and $Type -eq 'PTR' -and $Server -contains '100.100.100.100' }
    }

    It 'finds no name, and says why, when no adapter has a Tailscale DNS suffix' {
        Mock -ModuleName Multiplex.Install Get-DnsClient { [pscustomobject]@{ InterfaceAlias = 'Ethernet'; InterfaceIndex = 11; ConnectionSpecificSuffix = 'localdomain' } }
        $result = Get-Name
        $result.DnsName | Should -BeNullOrEmpty
        $result.Problem | Should -Match ([regex]::Escape('*.ts.net'))
    }

    It 'finds no name, and says why, when the Tailscale adapter has no IPv4 address' {
        Mock -ModuleName Multiplex.Install Get-NetIPAddress { }
        $result = Get-Name
        $result.DnsName | Should -BeNullOrEmpty
        $result.Problem | Should -Match 'no IPv4 address'
    }

    It 'finds no name, and says why, when MagicDNS does not answer' {
        Mock -ModuleName Multiplex.Install Resolve-DnsName { throw 'This operation returned because the timeout period expired' }
        $result = Get-Name
        $result.DnsName | Should -BeNullOrEmpty
        $result.Problem | Should -Match '100\.101\.102\.103.*timeout period expired'
    }

    It 'refuses a name outside the adapter''s tailnet' {
        Mock -ModuleName Multiplex.Install Resolve-DnsName { [pscustomobject]@{ Type = 'PTR'; Section = 'Answer'; NameHost = 'box.elsewhere.example.' } }
        $result = Get-Name
        $result.DnsName | Should -BeNullOrEmpty
        $result.Problem | Should -Match ([regex]::Escape('box.elsewhere.example'))
        $result.Problem | Should -Match ([regex]::Escape('tail1234.ts.net'))
    }

    It 'never throws' {
        Mock -ModuleName Multiplex.Install Get-DnsClient { throw 'Invalid class' }
        { Get-Name } | Should -Not -Throw
        (Get-Name).Problem | Should -Match 'Invalid class'
    }
}

Describe 'checking that an address reaches a port on this PC' {
    # Start-up runs this before the server listens, so a stand-in answers on the port with a
    # one-time token; only that token counts, not merely HTTP 200.
    BeforeEach {
        $script:port = Get-FreeLoopbackPort
        function Test-Reach([string] $Url, [int] $Port = $script:port, [int] $TimeoutSeconds = 10) {
            InModuleScope Multiplex.Install -Parameters @{ Url = $Url; Port = $Port; TimeoutSeconds = $TimeoutSeconds } {
                Test-UrlReachesLocalPort -Url $Url -Port $Port -TimeoutSeconds $TimeoutSeconds }
        }
        # Another server on another port that answers every request with 200 "Healthy", as any
        # Multiplex does: a second PC on the same tailnet, say.
        function Start-OtherServer {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $job = Start-ThreadJob -ArgumentList $listener {
                param($listener)
                try {
                    $client = $listener.AcceptTcpClient()
                    $stream = $client.GetStream()
                    $reader = [IO.StreamReader]::new($stream)
                    while ($reader.ReadLine()) { }
                    $bytes = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: text/plain`r`nContent-Length: 7`r`nConnection: close`r`n`r`nHealthy")
                    $stream.Write($bytes, 0, $bytes.Length)
                    $client.Dispose()
                }
                finally { $listener.Stop() }
            }
            [pscustomobject]@{ Port = $listener.LocalEndpoint.Port; Job = $job }
        }
    }

    It 'confirms an address whose requests arrive at the port, and leaves the port free for the server at once' {
        $result = Test-Reach -Url "http://127.0.0.1:$script:port/healthz"
        $result.Reached | Should -BeTrue
        $result.Problem | Should -BeNullOrEmpty
        $server = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $script:port)
        { $server.Start() } | Should -Not -Throw
        $server.Stop()
    }

    It 'does not count an address that answers 200 "Healthy" from somewhere else' {
        $other = Start-OtherServer
        try { $result = Test-Reach -Url "http://127.0.0.1:$($other.Port)/healthz" }
        finally { $other.Job | Wait-Job -Timeout 10 | Remove-Job -Force }
        $result.Reached | Should -BeFalse
        $result.Problem | Should -Match ([regex]::Escape("answered HTTP 200, but not from port $script:port on this PC"))
    }

    It 'says so when the address cannot be reached at all' {
        $nothing = Get-FreeLoopbackPort
        $result = Test-Reach -Url "http://127.0.0.1:$nothing/healthz"
        $result.Reached | Should -BeFalse
        $result.Problem | Should -Match ([regex]::Escape("http://127.0.0.1:$nothing/healthz could not be reached"))
    }

    It 'gives up on an address that does not answer, so start-up goes on' {
        $silent = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $silent.Start()   # accepts connections, never answers
        try {
            $elapsed = Measure-Command { $script:result = Test-Reach -Url "http://127.0.0.1:$($silent.LocalEndpoint.Port)/healthz" -TimeoutSeconds 2 }
        }
        finally { $silent.Stop() }
        $elapsed.TotalSeconds | Should -BeLessThan 10
        $script:result.Reached | Should -BeFalse
        $script:result.Problem | Should -Match 'did not answer within 2 s'
    }

    It 'does not check through a port something else already holds' {
        $holder = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $script:port)
        $holder.Start()
        try { $result = Test-Reach -Url "http://127.0.0.1:$script:port/healthz" }
        finally { $holder.Stop() }
        $result.Reached | Should -BeFalse
        $result.Problem | Should -Match "port $script:port on this PC is already in use"
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
    # is added, behind Microsoft sign-in, so the installer neither needs nor asks for an account.
    It 'the installer has no -AllowedAccount parameter, and its help says so' {
        $script = Join-Path $PSScriptRoot 'Install-Multiplex.ps1'
        (Get-Command $script).Parameters.Keys | Should -Not -Contain 'AllowedAccount'
        # An old command line stops at parameter binding; the help is where the owner finds out why.
        ((Get-Help $script -Full).alertSet.alert.Text -join ' ') |
            Should -Match ([regex]::Escape('-AllowedAccount was removed in #52; re-run the installer without it.'))
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

Describe 'connect check' {
    # Test-MultiplexConnect.ps1 against a stand-in that answers as Multiplex does since #80:
    # discovery and registration are open, and /oauth/authorize sends a browser that is not
    # signed in to Microsoft sign-in, or answers 503 while sign-in is not set up.
    BeforeAll {
        function Start-StandIn([ValidateSet('microsoft', 'not-set-up', 'wrong-redirect', 'elsewhere')] [string] $Authorize) {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $base = "http://127.0.0.1:$($listener.LocalEndpoint.Port)"
            $job = Start-ThreadJob -ArgumentList $listener, $base, $Authorize {
                param($listener, $base, $authorize)
                try {
                    while ($true) {
                        $client = $listener.AcceptTcpClient()
                        try {
                            $stream = $client.GetStream()
                            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 4096, $true)
                            $method, $target = $reader.ReadLine().Split(' ')[0, 1]
                            $length = 0
                            while ($line = $reader.ReadLine()) { if ($line -match '^Content-Length:\s*(\d+)') { $length = [int]$Matches[1] } }
                            if ($length) { $null = $reader.ReadBlock([char[]]::new($length), 0, $length) }
                            $path = $target.Split('?')[0]
                            $status, $type, $body, $headers = 404, 'text/plain', 'not found', ''
                            if ($method -eq 'POST' -and $path -eq '/tripit/mcp') {
                                $status, $body = 401, ''
                                $headers = "WWW-Authenticate: Bearer resource_metadata=`"$base/.well-known/oauth-protected-resource/tripit/mcp`"`r`n"
                            }
                            elseif ($path -eq '/.well-known/oauth-protected-resource/tripit/mcp') {
                                $status, $type = 200, 'application/json'
                                $body = @{ resource = "$base/tripit/mcp"; authorization_servers = @($base) } | ConvertTo-Json -Compress
                            }
                            elseif ($path -eq '/.well-known/oauth-authorization-server') {
                                $status, $type = 200, 'application/json'
                                $body = @{
                                    issuer = $base; authorization_endpoint = "$base/oauth/authorize"; token_endpoint = "$base/oauth/token"
                                    registration_endpoint = "$base/oauth/register"; code_challenge_methods_supported = @('S256')
                                } | ConvertTo-Json -Compress
                            }
                            elseif ($method -eq 'POST' -and $path -eq '/oauth/register') {
                                $status, $type, $body = 201, 'application/json', '{"client_id":"stand-in-client"}'
                            }
                            elseif ($path -eq '/oauth/authorize') {
                                $back = [Uri]::EscapeDataString(($authorize -eq 'wrong-redirect') ? 'https://elsewhere.example/signin-microsoft' : "$base/signin-microsoft")
                                switch ($authorize) {
                                    'not-set-up' { $status, $type, $body = 503, 'text/html', '<!doctype html><title>Sign-in is not set up</title><p>Sign-in is not set up on this server.</p>' }
                                    'elsewhere' { $status, $body, $headers = 302, '', "Location: /connect`r`n" }
                                    default {
                                        $status, $body = 302, ''
                                        $headers = "Location: https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=ms-client-id&response_type=code&scope=openid%20email&redirect_uri=$back&state=s`r`n"
                                    }
                                }
                            }
                            elseif ($path -eq '/connect') { $status, $type, $body = 200, 'text/html', '<form method="post"><input name="passcode"></form>' }
                            $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                            $head = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status Stand-in`r`nContent-Type: $type`r`nContent-Length: $($bytes.Length)`r`n${headers}Connection: close`r`n`r`n")
                            $stream.Write($head, 0, $head.Length)
                            $stream.Write($bytes, 0, $bytes.Length)
                        }
                        catch { Write-Verbose "The stand-in could not answer: $($_.Exception.Message)" }
                        finally { $client.Dispose() }
                    }
                }
                catch { Write-Verbose 'The stand-in was stopped.' }
            }
            [pscustomobject]@{ BaseUrl = $base; Listener = $listener; Job = $job }
        }
        function Invoke-Connect([string] $Authorize) {
            $standIn = Start-StandIn -Authorize $Authorize
            try {
                $output = & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'Test-MultiplexConnect.ps1') `
                    -BaseUrl $standIn.BaseUrl *>&1 | Out-String
                [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output; BaseUrl = $standIn.BaseUrl }
            }
            finally {
                $standIn.Listener.Stop()
                $standIn.Job | Wait-Job -Timeout 10 | Out-Null
                $standIn.Job | Remove-Job -Force
            }
        }
    }

    BeforeEach { $script:savedPasscode = $env:MULTIPLEX_PASSCODE }
    AfterEach { $env:MULTIPLEX_PASSCODE = $script:savedPasscode }

    It 'passes once authorize sends the person to Microsoft sign-in, then says the rest needs a person and ends' {
        $env:MULTIPLEX_PASSCODE = $null   # there is no passcode any more
        $result = Invoke-Connect -Authorize microsoft
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match 'PASS  client registration'
        $result.Output | Should -Match 'PASS  authorize sends the person to Microsoft sign-in'
        $result.Output | Should -Match ([regex]::Escape("Microsoft client_id ms-client-id; redirect_uri $($result.BaseUrl)/signin-microsoft"))
        $result.Output | Should -Match 'STOP  the remaining steps need a person'
        $result.Output | Should -Not -Match 'FAIL'
        $result.Output | Should -Not -Match 'PASS  token exchange'
    }

    It 'fails, saying Microsoft sign-in is not set up, when the server answers 503' {
        $result = Invoke-Connect -Authorize not-set-up
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'FAIL  authorize sends the person to Microsoft sign-in -- HTTP 503 .*Microsoft sign-in is not set up'
        $result.Output | Should -Match 'Multiplex__Identity__Microsoft__ClientSecret'
        $result.Output | Should -Not -Match 'STOP'
    }

    It 'fails when Microsoft sign-in is asked to send the person back anywhere but this server''s /signin-microsoft' {
        $result = Invoke-Connect -Authorize wrong-redirect
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match ([regex]::Escape("FAIL  authorize sends the person to Microsoft sign-in -- Microsoft sign-in is asked to send the person back to 'https://elsewhere.example/signin-microsoft'"))
    }

    It 'fails when authorize leads anywhere but Microsoft sign-in' {
        $result = Invoke-Connect -Authorize elsewhere
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match ([regex]::Escape("FAIL  authorize sends the person to Microsoft sign-in -- HTTP 200 at $($result.BaseUrl)/connect, not a redirect to Microsoft sign-in"))
    }

    It 'takes no passcode' {
        $script = Get-Command (Join-Path $PSScriptRoot 'Test-MultiplexConnect.ps1')
        $script.Parameters.Keys | Should -Not -Contain 'Passcode'
        ((Get-Help $script.Source -Full).alertSet.alert.Text -join ' ') | Should -Match ([regex]::Escape('-Passcode and $env:MULTIPLEX_PASSCODE were removed in #80'))
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
