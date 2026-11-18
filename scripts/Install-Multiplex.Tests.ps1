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

    It 'a Tailscale Funnel that does not come up leaves no public address' {
        Mock -ModuleName Multiplex.Install Invoke-TailscaleFunnel { [pscustomobject]@{ Started = $false; Output = 'Funnel is not enabled on your tailnet' } }
        $script:config.Tunnel = 'Tailscale'
        Start-MultiplexTunnel -Config $script:config -ConfigPath $script:path -LogDir $TestDrive -WarningAction SilentlyContinue
        $script:config.PublicBaseUrl | Should -BeNullOrEmpty
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -BeNullOrEmpty
    }

    It 'a Tailscale Funnel that comes up records its address' {
        Mock -ModuleName Multiplex.Install Invoke-TailscaleFunnel { [pscustomobject]@{ Started = $true; Output = '' } }
        Mock -ModuleName Multiplex.Install Get-TailscaleDnsName { 'box.example.ts.net' }
        $script:config.Tunnel = 'Tailscale'
        Start-MultiplexTunnel -Config $script:config -ConfigPath $script:path -LogDir $TestDrive 6>$null
        (Read-MultiplexConfig -Path $script:path).PublicBaseUrl | Should -Be 'https://box.example.ts.net'
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

    It 'says which accounts a new -AllowedAccount list removes, and lists the result' {
        $change = Get-MultiplexAllowlistChange -Previous 'a@example.com', 'B@example.com' -Current 'b@example.com,c@example.com'
        $change.Allowed | Should -Be @('b@example.com', 'c@example.com')
        $change.Removed | Should -Be @('a@example.com')
        $change.Summary | Should -Be 'TripIt accounts allowed to connect: b@example.com, c@example.com'
        $change.RemovedNotice | Should -Be 'No longer allowed to connect: a@example.com'
    }

    It 'reports no removal when only case or spacing differs' {
        $change = Get-MultiplexAllowlistChange -Previous ' A@Example.com ' -Current 'a@example.com'
        $change.Removed | Should -BeNullOrEmpty
        $change.RemovedNotice | Should -BeNullOrEmpty
        $change.Summary | Should -Be 'TripIt accounts allowed to connect: a@example.com'
    }

    It 'handles a first install with no earlier list' {
        $change = Get-MultiplexAllowlistChange -Previous @() -Current 'a@example.com'
        $change.Removed | Should -BeNullOrEmpty
        $change.Summary | Should -Be 'TripIt accounts allowed to connect: a@example.com'
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
