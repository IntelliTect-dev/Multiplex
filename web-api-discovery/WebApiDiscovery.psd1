@{
    RootModule        = 'WebApiDiscovery.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '5b0e6f3c-2d7a-4a4e-9a8f-6f1d2c9b7e41'
    Author            = 'IntelliTect'
    CompanyName       = 'IntelliTect'
    Description       = 'Record a web session, scrub it, and catalogue the API it used -- the PowerShell commands of the web-api-discovery skill.'
    PowerShellVersion = '7.0'
    FunctionsToExport = @(
        'Invoke-HarCapture'
        'Get-HarCapture'
        'Remove-HarCapture'
        'Invoke-HarCatalogue'
        'Stop-HarRecording'
        'ConvertFrom-HarCatalogue'
        'Invoke-SanitizeHar'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData       = @{
        PSData = @{
            ProjectUri = 'https://github.com/IntelliTect-Samples/web-api-discovery'
            Tags       = @('HAR', 'API', 'Playwright', 'scrub', 'AgentSkills')
        }
    }
}
