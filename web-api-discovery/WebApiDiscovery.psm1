# The PowerShell front doors of the web-api-discovery skill, as commands.
#
# The entry points stay scripts inside the skill folder -- the one copy every AI
# agent reads -- and each command here is generated from its script: the same
# parameters, the same [CmdletBinding()] (so -WhatIf and -Verbose flow through),
# and its comment-based help (forwarded to the script). Nothing is duplicated, so a `git pull` of the
# clone updates the commands too.
#
# The module sits at the repository root so that a module-path link to the clone
# (Install.ps1) resolves the skill folder through the link.

Set-StrictMode -Version Latest

$script:SkillRoot = Join-Path $PSScriptRoot 'skills' 'web-api-discovery'

$script:EntryPoints = [ordered]@{
    'Invoke-HarCapture'        = 'scripts/capture/Invoke-HarCapture.ps1'
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
