#Requires -Version 7.5
<#
.SYNOPSIS
    Build the TripIt.PowerShell binary module, launch a fresh pwsh session with it
    imported, and stay at an interactive prompt for exploration.

.DESCRIPTION
    Performs `dotnet build` on src/TripIt.PowerShell (unless -SkipBuild is set), then
    spawns a new pwsh process via `pwsh -NoExit -NoProfile` that imports the freshly
    built module, prints the exported cmdlets, and drops you at the prompt.

.PARAMETER Configuration
    Build configuration to use (Debug or Release). Defaults to Debug.

.PARAMETER SkipBuild
    Skip `dotnet build` and import whatever is already in bin/<Configuration>/net10.0.

.EXAMPLE
    pwsh -File scripts/Start-TripItShell.ps1

.EXAMPLE
    pwsh -File scripts/Start-TripItShell.ps1 -Configuration Release -SkipBuild
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectDir = Join-Path $repoRoot 'src/TripIt.PowerShell'
$csproj     = Join-Path $projectDir 'TripIt.PowerShell.csproj'
$manifest   = Join-Path $projectDir "bin/$Configuration/net10.0/TripIt.psd1"

if (-not $SkipBuild) {
    if (-not (Test-Path $csproj)) {
        throw "Project not found: $csproj"
    }
    Write-Host "Building TripIt.PowerShell ($Configuration)..." -ForegroundColor Cyan
    dotnet build $csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $manifest)) {
    throw "Module manifest not found at $manifest. Build first or omit -SkipBuild."
}

Write-Host "Launching pwsh with TripIt.PowerShell loaded..." -ForegroundColor Cyan

$bootstrap = @"
Import-Module '$manifest' -Force
Write-Host ''
Write-Host 'TripIt.PowerShell loaded from:' -ForegroundColor Green
Write-Host '  $manifest' -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Exported cmdlets:' -ForegroundColor Green
Get-Command -Module TripIt.PowerShell | Format-Table Name, CommandType -AutoSize
Write-Host 'Tips:' -ForegroundColor Green
Write-Host '  Get-Help Connect-TripIt -Full' -ForegroundColor DarkGray
Write-Host '  Connect-TripIt; Get-TripItProfile' -ForegroundColor DarkGray
Write-Host ''
"@

pwsh -NoExit -NoLogo -NoProfile -Command $bootstrap
