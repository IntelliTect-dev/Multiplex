<#
.SYNOPSIS
    Sanitize HAR files for public publication (PowerShell wrapper around the
    Node.js sanitizer).

.DESCRIPTION
    Thin wrapper that invokes scripts/sanitize-har.js via Node. Accepts the
    same source/destination semantics. Defaults to sanitizing every *.har
    in ./har into ./samples/har (relative to the repository root).

.PARAMETER Source
    Input HAR file or directory. Defaults to <repo>/har.

.PARAMETER Destination
    Output HAR file or directory. Defaults to <repo>/samples/har.

.PARAMETER File
    Treat Source/Destination as a single file pair instead of a directory.

.EXAMPLE
    pwsh scripts/Invoke-SanitizeHar.ps1
    # Sanitizes every *.har in ./har into ./samples/har

.EXAMPLE
    pwsh scripts/Invoke-SanitizeHar.ps1 -Source har\login.har -Destination samples\har\login.har -File
#>

[CmdletBinding()]
param(
    [string] $Source,
    [string] $Destination,
    [switch] $File
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Source)      { $Source      = Join-Path $repoRoot 'har' }
if (-not $Destination) { $Destination = Join-Path $repoRoot 'samples\har' }

$jsScript = Join-Path $PSScriptRoot 'sanitize-har.js'
if (-not (Test-Path -LiteralPath $jsScript)) {
    throw "Sanitizer not found: $jsScript"
}
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw "Node.js is required (https://nodejs.org). 'node' was not found on PATH."
}

$nodeArgs = @($jsScript)
if (-not $File) { $nodeArgs += '--dir' }
$nodeArgs += $Source
$nodeArgs += $Destination

Write-Host "node $($nodeArgs -join ' ')"
& node @nodeArgs
if ($LASTEXITCODE -ne 0) { throw "sanitize-har.js exited with code $LASTEXITCODE" }
