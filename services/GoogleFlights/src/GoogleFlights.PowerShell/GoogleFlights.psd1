@{
    RootModule             = 'Multiplex.Sites.GoogleFlights.PowerShell.dll'
    ModuleVersion          = '0.1.0'
    GUID                   = '3f0d6d8e-6a2b-4d6c-9a87-5b1f2b8f6c41'
    Author                 = 'Mark Michaelis'
    CompanyName            = 'IntelliTect'
    Copyright              = '(c) Mark Michaelis. All rights reserved.'
    Description            = 'Google Flights from PowerShell, over Google Flights'' own web endpoints (direct HTTP, no browser). Read-only: nothing is booked.'
    PowerShellVersion      = '7.5'
    DotNetFrameworkVersion = '10.0'
    CompatiblePSEditions   = @('Core')
    FormatsToProcess       = @('GoogleFlights.Format.ps1xml')
    CmdletsToExport        = @(
        'Find-Flight',
        'Get-FlightReturn',
        'Get-FlightBookingOption',
        'Get-FlightPriceCalendar',
        'Find-Airport',
        'Get-Airline'
    )
    FunctionsToExport      = @()
    AliasesToExport        = @()
    VariablesToExport      = @()
    PrivateData            = @{
        PSData = @{
            Tags       = @('GoogleFlights', 'Flights', 'Travel', 'Multiplex')
            ProjectUri = 'https://github.com/IntelliTect-dev/Multiplex/tree/main/services/GoogleFlights'
        }
    }
}
