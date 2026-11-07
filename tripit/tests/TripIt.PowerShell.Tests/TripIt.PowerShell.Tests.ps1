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
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')

    # Find the built module rather than assuming a configuration: CI builds Release while
    # a local `dotnet build` and scripts/Start-TripItShell.ps1 default to Debug. Hardcoding
    # Debug here meant CI could never pass. Honour an explicit choice, else take whichever
    # manifest was built most recently -- that is the one the developer just produced.
    $configurations = if ($env:TRIPIT_TEST_CONFIGURATION) {
        @($env:TRIPIT_TEST_CONFIGURATION)
    } else {
        @('Release', 'Debug')
    }

    $candidates = foreach ($configuration in $configurations) {
        Join-Path $repoRoot "src/TripIt.PowerShell/bin/$configuration/net10.0/TripIt.psd1"
    }

    $manifest = $candidates |
        Where-Object { Test-Path $_ } |
        Sort-Object { (Get-Item $_).LastWriteTimeUtc } -Descending |
        Select-Object -First 1

    if (-not $manifest) {
        throw ("Module manifest not found. Looked at:`n  " + ($candidates -join "`n  ") +
            "`nRun 'dotnet build src/TripIt.PowerShell' first, or set " +
            '$env:TRIPIT_TEST_CONFIGURATION to the configuration you built.')
    }

    # The module's name comes from the MANIFEST filename (TripIt.psd1 -> "TripIt"), not
    # from the assembly (TripIt.PowerShell.dll). Looking up "TripIt.PowerShell" returned
    # $null and silently failed three assertions against an empty module.
    $script:ModuleName = [System.IO.Path]::GetFileNameWithoutExtension($manifest)

    Import-Module $manifest -Force
    $script:Module = Get-Module $script:ModuleName
    if (-not $script:Module) {
        throw "Imported $manifest but no module named '$script:ModuleName' is loaded."
    }
}

AfterAll {
    if ($script:ModuleName) {
        Remove-Module $script:ModuleName -Force -ErrorAction SilentlyContinue
    }
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
            'Get-TripItItinerary',
            'Get-TripItObject',
            'Search-TripItTrip',
            'Find-TripItReference',
            'New-TripItTrip',
            'Set-TripItTrip',
            'Remove-TripItTrip',
            'New-TripItPlan',
            'Set-TripItPlan',
            'Remove-TripItPlan',
            'Move-TripItPlan',
            'Get-TripItPlanSchema',
            'Merge-TripItTrip',
            'Get-TripItParticipant',
            'Add-TripItParticipant',
            'Set-TripItParticipant',
            'Remove-TripItParticipant',
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

Describe 'Read cmdlets' {
    It 'Get-TripItTrip has List and Uuid parameter sets' {
        (Get-Command Get-TripItTrip).ParameterSets.Name | Should -Contain 'Uuid'
        (Get-Command Get-TripItTrip).ParameterSets.Name | Should -Contain 'List'
    }

    It 'Get-TripItObject -Type completes object types' {
        $line = 'Get-TripItObject -Type lod'
        $r = TabExpansion2 -inputScript $line -cursorColumn $line.Length
        $r.CompletionMatches.CompletionText | Should -Contain 'lodging'
    }

    It 'Get-TripItObject rejects an unknown type' {
        { Get-TripItObject -Type spaceship -Uuid 11111111-aaaa-9000-0001-000000000001 } |
            Should -Throw '*Unknown TripIt object type*'
    }

    It 'Find-TripItReference validates Kind' {
        { Find-TripItReference -Kind Planet -Query x } | Should -Throw
    }
}

Describe 'Write cmdlets' {
    It '<Name> supports -WhatIf' -ForEach @(
        @{ Name = 'New-TripItTrip' }, @{ Name = 'Set-TripItTrip' }, @{ Name = 'Remove-TripItTrip' },
        @{ Name = 'New-TripItPlan' }, @{ Name = 'Set-TripItPlan' }, @{ Name = 'Remove-TripItPlan' },
        @{ Name = 'Move-TripItPlan' }
    ) {
        (Get-Command $Name).Parameters.ContainsKey('WhatIf') | Should -BeTrue
    }

    It '<Name> has ConfirmImpact High' -ForEach @(
        @{ Name = 'Remove-TripItTrip' }, @{ Name = 'Remove-TripItPlan' }, @{ Name = 'Merge-TripItTrip' },
        @{ Name = 'Add-TripItParticipant' }, @{ Name = 'Remove-TripItParticipant' }
    ) {
        $type = (Get-Command $Name).ImplementingType
        $attr = $type.GetCustomAttributes([System.Management.Automation.CmdletAttribute], $false) | Select-Object -First 1
        $attr.ConfirmImpact | Should -Be 'High'
    }

    It 'New-TripItTrip -WhatIf makes no request' {
        { New-TripItTrip -Destination 'Nowhere' -StartDate 2030-01-01 -EndDate 2030-01-02 -WhatIf } | Should -Not -Throw
    }

    It 'New-TripItPlan rejects unknown fields before any request' {
        { New-TripItPlan -Type lodging -Fields @{ hotel_name = 'x' } -TripUuid 11111111-aaaa-9000-0001-000000000001 } |
            Should -Throw '*Unknown lodging field*'
    }

    It 'Get-TripItTrip -Traveler accepts All, Mine, Others' {
        (Get-Command Get-TripItTrip).Parameters['Traveler'].Attributes.ValidValues | Should -Be @('All', 'Mine', 'Others')
    }

    It 'New-TripItTrip has -NotTraveling' {
        (Get-Command New-TripItTrip).Parameters.ContainsKey('NotTraveling') | Should -BeTrue
    }

    It 'Get-TripItPlanSchema lists the air segment fields' {
        (Get-TripItPlanSchema air).SegmentFields | Should -Contain 'marketing_flight_number'
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
