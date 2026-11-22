#Requires -Version 7.5
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
    The Google Flights PowerShell module. Build first (dotnet build), then:
        Invoke-Pester services/GoogleFlights/tests/GoogleFlights.PowerShell.Tests
    The live part runs only with MULTIPLEX_LIVE_TESTS=1 and spends two requests.
#>

BeforeAll {
    $script:root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
    $script:project = Join-Path $root 'src' 'GoogleFlights.PowerShell'
    $manifest = Get-ChildItem -Path (Join-Path $project 'bin') -Recurse -Filter GoogleFlights.psd1 |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $manifest) { throw "Build the module first: dotnet build $project" }
    Import-Module $manifest.FullName -Force
    $script:docs = [Multiplex.Sites.GoogleFlights.GoogleFlightsDocs]
}

AfterAll {
    Remove-Module GoogleFlights -ErrorAction SilentlyContinue
}

Describe 'The GoogleFlights module' {
    It 'exports exactly the six commands the spec names' {
        (Get-Command -Module GoogleFlights).Name | Sort-Object |
            Should -Be @('Find-Airport', 'Find-Flight', 'Get-Airline', 'Get-FlightBookingOption', 'Get-FlightPriceCalendar', 'Get-FlightReturn')
    }

    It 'has every command take -Verify' {
        Get-Command -Module GoogleFlights | ForEach-Object { $_.Parameters.Keys | Should -Contain 'Verify' -Because $_.Name }
    }

    It 'ships help that is exactly what the shared descriptions generate' {
        $committed = Get-Content -Raw (Join-Path $project 'en-US' ([Multiplex.Sites.GoogleFlights.PowerShell.MamlHelp]::FileName))
        $generated = [Multiplex.Sites.GoogleFlights.PowerShell.MamlHelp]::Generate()
        $committed | Should -BeExactly $generated -Because 'the help file is generated from GoogleFlightsDocs: regenerate it with [Multiplex.Sites.GoogleFlights.PowerShell.MamlHelp]::Generate() after editing a description'
    }

    It 'shows the same description in Get-Help as the MCP tool has' {
        $help = Get-Help Find-Flight -Full
        ($help.description.Text -join '') | Should -BeExactly $docs::SearchFlights
        ($help.parameters.parameter | Where-Object name -eq 'Origin').description.Text | Should -BeExactly $docs::Origin
    }
}

Describe 'Commands without a request to Google' {
    It 'Get-Airline lists an alliance''s members' {
        $members = Get-Airline 'star alliance'
        $members.Code | Should -Contain 'UA'
        $members | ForEach-Object { $_.Alliance | Should -Be 'StarAlliance' }
    }

    It 'Find-Flight reports an invalid airport as an argument error' {
        Find-Flight 'Seattle' DEN 2026-12-15 -ErrorVariable failed -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
        $failed[0].FullyQualifiedErrorId | Should -Match '^InvalidArgument'
        $failed[0].Exception.Message | Should -Match 'three-letter IATA airport code'
    }

    It 'Get-FlightReturn takes a selection from the pipeline by property name' {
        $day = [DateOnly]::FromDateTime((Get-Date).AddDays(30))
        $request = [Multiplex.Sites.GoogleFlights.FlightSearchRequest]::OneWay('SEA', 'DEN', $day, $null).Normalize('USD', 'US')
        $chosen = [Multiplex.Sites.GoogleFlights.ChosenSegment]::new('SEA', $day, 'DEN', 'DL', '1')
        $legs = [System.Collections.Generic.IReadOnlyList[Multiplex.Sites.GoogleFlights.ChosenSegment][]]@(, [Multiplex.Sites.GoogleFlights.ChosenSegment[]]@($chosen))
        $selection = [Multiplex.Sites.GoogleFlights.FlightSelection]::new($request, $legs, 'token')

        # A one-way selection is already complete, so the command refuses it before any request.
        [pscustomobject]@{ Selection = $selection } | Get-FlightReturn -ErrorVariable failed -ErrorAction SilentlyContinue
        $failed[0].Exception.Message | Should -Match 'booking options instead'
    }
}

Describe 'Live, against Google Flights' -Skip:($env:MULTIPLEX_LIVE_TESTS -ne '1') {
    BeforeAll {
        $script:departure = (Get-Date).Date.AddDays(43)
    }

    It 'Find-Airport finds Denver''s airport' {
        (Find-Airport Denver).Code | Should -Contain 'DEN'
    }

    It 'Find-Flight pipes a choice into Get-FlightBookingOption, which gives the booking link instead' {
        $flights = Find-Flight SEA DEN $departure
        $flights | Should -Not -BeNullOrEmpty
        $flights | ForEach-Object { $_.Origin | Should -Be 'SEA'; $_.Destination | Should -Be 'DEN' }
        $flights[0].RetrievedAt | Should -BeOfType [DateTimeOffset]

        # Booking options are not available over HTTPS: an error holding the booking page's link, nothing sent.
        $options = $flights | Select-Object -First 1 | Get-FlightBookingOption -ErrorVariable failed -ErrorAction SilentlyContinue
        $options | Should -BeNullOrEmpty
        $failed[0].Exception | Should -BeOfType ([Multiplex.Sites.GoogleFlights.GoogleFlightsUnavailableException])
        $failed[0].Exception.GoogleFlightsUrl.AbsolutePath | Should -Be '/travel/flights/booking'
    }
}
