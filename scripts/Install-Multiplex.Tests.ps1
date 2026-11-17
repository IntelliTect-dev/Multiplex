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
        $c.AllowedAccounts.tripit = @('a@example.com', 'b@example.com')
        Write-MultiplexConfig -Config $c -Path $path
        $env = Get-MultiplexServerEnvironment -Config (Read-MultiplexConfig -Path $path) -Root 'C:\M'
        $env['Multiplex__PublicBaseUrl'] | Should -Be 'https://box.example.ts.net'
        $env['Multiplex__Sites__tripit__AllowedAccounts__0'] | Should -Be 'a@example.com'
        $env['Multiplex__Sites__tripit__AllowedAccounts__1'] | Should -Be 'b@example.com'
        $env['Multiplex__Gate__PasscodeHash'] | Should -Be 'pbkdf2-sha256$1$a$b'
        $env['ASPNETCORE_URLS'] | Should -Be 'http://localhost:5190'
        $env['Multiplex__DataPath'] | Should -Be (Join-Path 'C:\M' 'data')
    }

    It 'falls back to the local address when there is no public one' {
        $c = Read-MultiplexConfig -Path (Join-Path $TestDrive 'none2.json')
        (Get-MultiplexServerEnvironment -Config $c -Root 'C:\M')['Multiplex__PublicBaseUrl'] | Should -Be 'http://localhost:5190'
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

Describe 'allowed accounts' {
    It 'splits one comma- or semicolon-separated value, as pwsh -File passes it' {
        ConvertTo-MultiplexAccountList -Account 'tripit-test@example.com,Owner@Example.com' |
            Should -Be @('owner@example.com', 'tripit-test@example.com')
        ConvertTo-MultiplexAccountList -Account 'a@example.com; b@example.com' |
            Should -Be @('a@example.com', 'b@example.com')
    }

    It 'accepts separate values, trims, lower-cases and removes duplicates and blanks' {
        ConvertTo-MultiplexAccountList -Account ' B@example.com ', 'a@example.com,', 'b@example.com' |
            Should -Be @('a@example.com', 'b@example.com')
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
