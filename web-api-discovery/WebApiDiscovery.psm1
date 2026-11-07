# The PowerShell front doors of the web-api-discovery skill, as commands.
#
# The entry points stay scripts inside the skill folder -- the one copy every AI
# agent reads -- and each command here is generated from its script: the same
# parameters, the script's own [CmdletBinding()], and its comment-based help
# (forwarded to the script). Nothing is duplicated, so a `git pull` of the
# clone updates the commands too.
#
# Because the binding is COPIED rather than restated, a ShouldProcess-gated
# parameter is on a command exactly when its script declares it: -WhatIf and
# -Confirm reach Invoke-SanitizeHar and Remove-HarCapture, the two that declare
# SupportsShouldProcess, and none of the other five. Declaring it on a script is
# the whole of adding -WhatIf to the command. The rest of the common parameters
# are not gated that way -- -Verbose, -Debug and -ErrorAction ride along with
# [CmdletBinding()] itself, so they are on all seven.
#
# The module sits at the repository root because that is where the managed
# profile block Install.ps1 writes expects the manifest -- it probes
# <clone>/WebApiDiscovery.psd1 -- and because the skill folder below resolves
# relative to this file. A link in the modules folder is DELIBERATELY not how
# this is installed; Install.ps1 explains why (a reparse point there roams as a
# placeholder that cannot be hydrated on any other machine).

Set-StrictMode -Version Latest

$script:SkillRoot = Join-Path $PSScriptRoot 'skills' 'web-api-discovery'

$script:EntryPoints = [ordered]@{
    'Invoke-HarCapture'        = 'scripts/capture/Invoke-HarCapture.ps1'
    'Get-HarCapture'           = 'scripts/capture/Get-HarCapture.ps1'
    'Remove-HarCapture'        = 'scripts/capture/Remove-HarCapture.ps1'
    'Invoke-HarCatalogue'      = 'scripts/capture/Invoke-HarCatalogue.ps1'
    'Stop-HarRecording'        = 'scripts/capture/Stop-HarRecording.ps1'
    'ConvertFrom-HarCatalogue' = 'scripts/capture/ConvertFrom-HarCatalogue.ps1'
    'Invoke-SanitizeHar'       = 'scripts/har/Invoke-SanitizeHar.ps1'
}

foreach ($name in $script:EntryPoints.Keys) {
    $path = Join-Path $script:SkillRoot $script:EntryPoints[$name]
    $command = Get-Command -Name $path -CommandType ExternalScript
    $metadata = [System.Management.Automation.CommandMetadata]::new($command)

    $binding = [System.Management.Automation.ProxyCommand]::GetCmdletBindingAttribute($metadata)
    $params = [System.Management.Automation.ProxyCommand]::GetParamBlock($metadata)
    $quoted = $path.Replace("'", "''")

    $definition = @"
<#
.FORWARDHELPTARGETNAME $path
.FORWARDHELPCATEGORY ExternalScript
#>
$binding
param($params)
& '$quoted' @PSBoundParameters
"@
    Set-Item -Path "function:script:$name" -Value ([scriptblock]::Create($definition))
}

Export-ModuleMember -Function @($script:EntryPoints.Keys)
