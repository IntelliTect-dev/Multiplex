<#
.SYNOPSIS
    Pester 5 tests for the TripIt.PowerShell module.

.DESCRIPTION
    These tests verify the module manifest, cmdlet exports, parameter binding, and
    -WhatIf behavior. Tests that hit the live TripIt API are gated on $env:TRIPIT_COOKIE
    and skipped otherwise.

    Run with: Invoke-Pester -Path tests/TripIt.PowerShell.Tests
#>

BeforeAll {
    $repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
    $moduleDir  = Join-Path $repoRoot 'src/TripIt.PowerShell/bin/Debug/net10.0'
    $manifest   = Join-Path $moduleDir 'TripIt.psd1'

    if (-not (Test-Path $manifest)) {
        throw "Module manifest not found at $manifest. Run 'dotnet build src/TripIt.PowerShell' first."
    }

    Import-Module $manifest -Force
    $script:Module = Get-Module TripIt.PowerShell
}

AfterAll {
    Remove-Module TripIt.PowerShell -Force -ErrorAction SilentlyContinue
}

Describe 'Module manifest' {
    It 'loads successfully' {
        $script:Module | Should -Not -BeNullOrEmpty
    }

    It 'exports the documented cmdlets' {
        $expected = @(
            'Connect-TripIt',
            'Get-TripItProfile',
            'Get-TripItTrip',
            'Get-TripItTravelerProfile',
            'Get-TripItProAlert',
            'Get-TripItPurchasedProduct',
            'Get-TripItAppConfig',
            'Get-TripItGtmData',
            'Invoke-TripItRaw',
            'Sync-TripItSecretToGitHub'
        )
        $exported = $script:Module.ExportedCmdlets.Keys
        foreach ($name in $expected) {
            $exported | Should -Contain $name
        }
    }

    It 'requires PowerShell 7.5+' {
        $script:Module.PowerShellVersion | Should -BeGreaterOrEqual ([Version]'7.5')
    }
}

Describe 'Cmdlet metadata' {
    It '<Name> has a synopsis' -ForEach @(
        @{ Name = 'Connect-TripIt' }
        @{ Name = 'Get-TripItProfile' }
        @{ Name = 'Get-TripItTrip' }
        @{ Name = 'Invoke-TripItRaw' }
        @{ Name = 'Sync-TripItSecretToGitHub' }
    ) {
        $help = Get-Help $Name -ErrorAction SilentlyContinue
        $help | Should -Not -BeNullOrEmpty
    }
}

Describe 'Connect-TripIt' {
    It 'supports -WhatIf' {
        { Connect-TripIt -NoBrowserFallback -WhatIf } | Should -Not -Throw
    }

    It 'requires Password when UserName is supplied' {
        $cmd = Get-Command Connect-TripIt
        $params = $cmd.ParameterSets | Where-Object Name -EQ 'Credentials'
        ($params.Parameters | Where-Object Name -EQ 'Password').IsMandatory | Should -BeTrue
    }
}

Describe 'Invoke-TripItRaw' {
    It 'rejects paths that do not start with /' {
        { Invoke-TripItRaw -Path 'api/v2/get/profile' } | Should -Throw '*must start*'
    }

    It 'has a mandatory Path parameter' {
        (Get-Command Invoke-TripItRaw).Parameters['Path'].Attributes |
            Where-Object { $_ -is [Parameter] -and $_.Mandatory } |
            Should -Not -BeNullOrEmpty
    }
}

Describe 'Sync-TripItSecretToGitHub' {
    It 'requires -Repository' {
        (Get-Command Sync-TripItSecretToGitHub).Parameters['Repository'].Attributes |
            Where-Object { $_ -is [Parameter] -and $_.Mandatory } |
            Should -Not -BeNullOrEmpty
    }

    It 'supports -WhatIf' {
        $cmd = Get-Command Sync-TripItSecretToGitHub
        $cmd.Parameters.ContainsKey('WhatIf') | Should -BeTrue
    }
}

Describe 'Live TripIt API (integration)' -Tag 'Integration' -Skip:(-not $env:TRIPIT_COOKIE) {
    It 'Get-TripItProfile returns an object with DisplayName or ScreenName' {
        $profile = Get-TripItProfile
        $profile | Should -Not -BeNullOrEmpty
        ($profile.DisplayName -or $profile.ScreenName) | Should -BeTrue
    }
}
