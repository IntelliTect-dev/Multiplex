@{
    RootModule          = 'TripIt.PowerShell.dll'
    ModuleVersion       = '0.2.0'
    GUID                = 'd7e1a0a6-9b4a-4b1b-9a30-5d6e3c2c1d2e'
    Author              = 'Mark Michaelis'
    CompanyName         = 'TripItEx'
    Copyright           = '(c) Mark Michaelis. All rights reserved.'
    Description         = 'PowerShell cmdlets for the TripIt web SPA backend (TripItEx).'
    PowerShellVersion   = '7.5'
    DotNetFrameworkVersion = '10.0'
    CompatiblePSEditions = @('Core')
    CmdletsToExport     = @(
        'Connect-TripIt',
        'Get-TripItProfile',
        'Get-TripItTrip',
        'Get-TripItItinerary',
        'Get-TripItObject',
        'Search-TripItTrip',
        'Find-TripItReference',
        'Get-TripItTravelerProfile',
        'Get-TripItProAlert',
        'Get-TripItPurchasedProduct',
        'Get-TripItAppConfig',
        'Get-TripItGtmData',
        'Invoke-TripItRaw',
        'Sync-TripItSecretToGitHub'
    )
    FunctionsToExport   = @()
    AliasesToExport     = @()
    VariablesToExport   = @()
    PrivateData = @{
        PSData = @{
            Tags         = @('TripIt', 'Travel', 'API')
            ProjectUri   = 'https://github.com/MarkMichaelis/TripItEx'
        }
    }
}
