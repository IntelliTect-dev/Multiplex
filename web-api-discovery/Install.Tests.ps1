#Requires -Modules Pester

<#
    Behavior tests for Install.ps1. Everything runs against temp folders: the
    real home folder, agent skill folders and module path are never touched.
    The script returns early when dot-sourced, so its functions load without
    the install running.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Install.ps1')

    function New-TempDir {
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ('wadi-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir | Out-Null
        return (Resolve-Path $dir).ProviderPath
    }

    function Remove-TempDir([string]$Dir) {
        # Delete links before their targets so nothing is removed through them.
        Get-ChildItem $Dir -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object LinkType -in 'Junction', 'SymbolicLink' | ForEach-Object { $_.Delete() }
        Remove-Item $Dir -Recurse -Force
    }

    function New-Link([string]$Link, [string]$Target) {
        New-Item -ItemType Directory -Path (Split-Path $Link -Parent) -Force | Out-Null
        New-Item -ItemType ($IsWindows ? 'Junction' : 'SymbolicLink') -Path $Link -Target $Target | Out-Null
    }
}

Describe 'Get-MainCheckout' {
    BeforeEach { $script:tmp = New-TempDir }
    AfterEach { Remove-TempDir $tmp }

    It 'resolves a linked worktree to the main checkout, so links survive worktree cleanup' {
        $main = Join-Path $tmp 'main'
        git init -q $main
        git -C $main -c user.email=t@e -c user.name=t -c commit.gpgsign=false commit -q --allow-empty -m init
        git -C $main worktree add -q (Join-Path $main '.worktrees/wt') -b wt 2>$null

        Get-MainCheckout (Join-Path $main '.worktrees/wt') | Should -Be ([System.IO.Path]::GetFullPath($main))
    }

    It 'refuses a folder that is not a clone' {
        { Get-MainCheckout $tmp } | Should -Throw '*not inside a git clone*'
    }
}

Describe 'Get-SkillFolders' {
    BeforeEach { $script:home_ = New-TempDir }
    AfterEach { Remove-TempDir $home_ }

    It 'links nowhere when no agent is installed' {
        @(Get-SkillFolders -HomeDir $home_) | Should -BeNullOrEmpty
    }

    It 'links Claude''s folder and the shared folder when both kinds of agent are installed' {
        foreach ($agent in '.claude', '.codex') { New-Item -ItemType Directory (Join-Path $home_ $agent) | Out-Null }

        Get-SkillFolders -HomeDir $home_ | Should -Be @((Join-Path $home_ '.claude/skills'), (Join-Path $home_ '.agents/skills'))
    }
}

Describe 'Update-ProfileBlock' {
    It 'adds the block to an empty profile' {
        $result = Update-ProfileBlock -Content ''

        $result | Should -Match ([regex]::Escape($script:ProfileBegin))
        $result | Should -Match ([regex]::Escape($script:ProfileEnd))
    }

    It 'keeps the rest of the profile and changes nothing on a second run' {
        $mine = "Set-PSReadLineOption -EditMode Emacs`n"
        $once = Update-ProfileBlock -Content $mine

        $once.StartsWith($mine) | Should -BeTrue
        Update-ProfileBlock -Content $once | Should -BeExactly $once
    }

    It 'NAMES NO MACHINE -- the roaming profile carries no path any one machine has' {
        # THE FALSIFIER for this whole design, and the defect it closes.
        #
        # The block used to list every machine's clone and import whichever
        # existed. A roaming profile then accumulated paths that were meaningless
        # everywhere but one machine -- and this repo's own author hit the other
        # half: a profile that said C:\Git\web-api-discovery on a machine whose
        # clone was at D:\Git\web-api-discovery imported nothing at all, silently,
        # while looking fully installed.
        #
        # So the assertion is on the ABSENCE of machine-shaped text, not on the
        # presence of a mechanism: a drive letter, a home folder, or the path this
        # very test run is executing from must not appear.
        $block = Update-ProfileBlock -Content ''

        $block | Should -Not -Match '[A-Za-z]:\\'
        $block | Should -Not -Match ([regex]::Escape($PSScriptRoot))
        $block | Should -Not -Match ([regex]::Escape($HOME))
        # And it must be byte-identical between runs on different machines, which
        # is the property that makes it safe to roam. The function takes no
        # machine input at all any more, so this is structural.
        (Get-Command Update-ProfileBlock).Parameters.Keys | Should -Not -Contain 'ClonePath'
    }

    It 'resolves the clone from the machine-local pointer file' {
        # The deterministic path, and the one an installed machine takes.
        $tmp = New-TempDir
        try {
            $clone = Join-Path $tmp 'clone'
            New-Item -ItemType Directory -Path $clone | Out-Null
            Set-Content -LiteralPath (Join-Path $clone 'WebApiDiscovery.psd1') -Value "@{ ModuleVersion = '0.0.1' }"

            $local = Join-Path $tmp 'local'
            $pointer = Join-Path $local $script:PointerLeaf
            New-Item -ItemType Directory (Split-Path $pointer -Parent) -Force | Out-Null
            Set-Content -LiteralPath $pointer -Value $clone

            $script:imported = [System.Collections.Generic.List[string]]::new()
            # $Global is touched so the stub's shape is not a lie: the block passes
            # it, and a stub that silently dropped it would hide a change to that.
            function Import-Module { param($Name, [switch]$Global) $null = $Global; $script:imported.Add($Name) }

            $saved = $env:LOCALAPPDATA
            $savedOverride = $env:WEBAPIDISCOVERY_CLONE
            try {
                $env:LOCALAPPDATA = $local
                $env:WEBAPIDISCOVERY_CLONE = ''
                . ([scriptblock]::Create((Update-ProfileBlock -Content '')))
            }
            finally {
                $env:LOCALAPPDATA = $saved
                $env:WEBAPIDISCOVERY_CLONE = $savedOverride
            }

            $script:imported | Should -Be @((Join-Path $clone 'WebApiDiscovery.psd1'))
        }
        finally { Remove-TempDir $tmp }
    }

    It '$env:WEBAPIDISCOVERY_CLONE outranks the pointer file' {
        # For a clone somewhere no convention would guess, on a machine whose
        # pointer says something else -- a second checkout being tested, say.
        $tmp = New-TempDir
        try {
            foreach ($name in 'pointed', 'overridden') {
                New-Item -ItemType Directory -Path (Join-Path $tmp $name) | Out-Null
                Set-Content -LiteralPath (Join-Path $tmp $name 'WebApiDiscovery.psd1') -Value "@{ ModuleVersion = '0.0.1' }"
            }
            $local = Join-Path $tmp 'local'
            $pointer = Join-Path $local $script:PointerLeaf
            New-Item -ItemType Directory (Split-Path $pointer -Parent) -Force | Out-Null
            Set-Content -LiteralPath $pointer -Value (Join-Path $tmp 'pointed')

            $script:imported = [System.Collections.Generic.List[string]]::new()
            # $Global is touched so the stub's shape is not a lie: the block passes
            # it, and a stub that silently dropped it would hide a change to that.
            function Import-Module { param($Name, [switch]$Global) $null = $Global; $script:imported.Add($Name) }

            $saved = $env:LOCALAPPDATA
            $savedOverride = $env:WEBAPIDISCOVERY_CLONE
            try {
                $env:LOCALAPPDATA = $local
                $env:WEBAPIDISCOVERY_CLONE = Join-Path $tmp 'overridden'
                . ([scriptblock]::Create((Update-ProfileBlock -Content '')))
            }
            finally {
                $env:LOCALAPPDATA = $saved
                $env:WEBAPIDISCOVERY_CLONE = $savedOverride
            }

            $script:imported | Should -Be @((Join-Path $tmp 'overridden' 'WebApiDiscovery.psd1'))
        }
        finally { Remove-TempDir $tmp }
    }

    It 'imports -Global and leaves no variables behind in the session' {
        # A profile runs in GLOBAL scope, so anything the block declares at top
        # level lives for the rest of the terminal -- and `$clone`, `$manifest` and
        # `$state` are names somebody else's profile may well want. Hence the
        # `& { }` wrapper, which then makes -Global the only way the import can
        # reach the session at all.
        $block = Update-ProfileBlock -Content ''

        $block | Should -Match ([regex]::Escape('Import-Module $manifest -Global'))

        $script:sawGlobal = $false
        function Import-Module { param($Name, [switch]$Global) $null = $Name; $script:sawGlobal = [bool]$Global }
        $saved = $env:WEBAPIDISCOVERY_CLONE
        try {
            $env:WEBAPIDISCOVERY_CLONE = $PSScriptRoot    # this repo IS a clone
            . ([scriptblock]::Create($block))
        }
        finally { $env:WEBAPIDISCOVERY_CLONE = $saved }

        $script:sawGlobal | Should -BeTrue
        foreach ($leaked in 'state', 'pointer', 'roots', 'candidates', 'candidate', 'manifest') {
            Get-Variable -Name $leaked -Scope 0 -ErrorAction SilentlyContinue |
                Should -BeNullOrEmpty -Because "the block must not leave `$$leaked in the session"
        }
    }

    It 'the block is valid PowerShell' {
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseInput(
            (Update-ProfileBlock -Content ''), [ref]$null, [ref]$errors) | Out-Null
        $errors | Should -BeNullOrEmpty
    }
}

Describe 'Set-ClonePointer' {
    It 'writes this machine''s clone where the block looks for it, and is idempotent' {
        $tmp = New-TempDir
        try {
            $saved = $env:LOCALAPPDATA
            try {
                $env:LOCALAPPDATA = $tmp
                Set-ClonePointer -ClonePath 'C:\Git\web-api-discovery' | Should -Be 'Written'
                $file = Join-Path $tmp $script:PointerLeaf
                (Get-Content -LiteralPath $file -Raw).Trim() | Should -Be 'C:\Git\web-api-discovery'

                Set-ClonePointer -ClonePath 'C:\Git\web-api-discovery' | Should -Be 'Unchanged'
                # A clone that MOVED is the case this exists to absorb, so a new
                # path replaces the old one rather than being added beside it --
                # the machine has one clone, which is the whole difference from
                # the roaming list this replaced.
                Set-ClonePointer -ClonePath 'D:\Git\web-api-discovery' | Should -Be 'Written'
                (Get-Content -LiteralPath $file -Raw).Trim() | Should -Be 'D:\Git\web-api-discovery'
            }
            finally { $env:LOCALAPPDATA = $saved }
        }
        finally { Remove-TempDir $tmp }
    }

    It 'keeps the pointer OUT of any roaming location' {
        # The entire point: local app data is per-machine. A pointer that roamed
        # would be the defect again, wearing a different file name.
        $saved = $env:LOCALAPPDATA
        try {
            $env:LOCALAPPDATA = 'C:\Users\someone\AppData\Local'
            Get-LocalStateRoot | Should -Be 'C:\Users\someone\AppData\Local'
        }
        finally { $env:LOCALAPPDATA = $saved }
    }
}

Describe 'Set-Link' {
    BeforeEach {
        $script:tmp = New-TempDir
        $script:target = Join-Path $tmp 'clone/skills/web-api-discovery'
        New-Item -ItemType Directory $target -Force | Out-Null
        Set-Content (Join-Path $target 'SKILL.md') 'skill'
        $script:link = Join-Path $tmp 'agent/skills/web-api-discovery'
    }
    AfterEach { Remove-TempDir $tmp }

    It 'makes the target visible through the link, live' {
        Set-Link -Link $link -Target $target | Should -Be 'Linked'
        Set-Content (Join-Path $target 'SKILL.md') 'edited'

        Get-Content (Join-Path $link 'SKILL.md') | Should -Be 'edited'
    }

    It 'reports unchanged on a second run' {
        Set-Link -Link $link -Target $target | Out-Null

        Set-Link -Link $link -Target $target | Should -Be 'Unchanged'
    }

    It 'never replaces a real folder' {
        New-Item -ItemType Directory $link -Force | Out-Null
        Set-Content (Join-Path $link 'SKILL.md') 'my own'

        Set-Link -Link $link -Target $target | Should -Be 'Skipped'
        Get-Content (Join-Path $link 'SKILL.md') | Should -Be 'my own'
    }

    It 'repoints a link from an earlier install without touching what it pointed at' {
        $old = Join-Path $tmp 'old'
        New-Item -ItemType Directory $old | Out-Null
        Set-Content (Join-Path $old 'SKILL.md') 'old'
        New-Link $link $old

        Set-Link -Link $link -Target $target | Should -Be 'Linked'

        Get-Content (Join-Path $link 'SKILL.md') | Should -Be 'skill'
        Get-Content (Join-Path $old 'SKILL.md') | Should -Be 'old'
    }
}

Describe 'WebApiDiscovery module' {
    It 'exposes each entry point as a command with the script''s own parameters' {
        $module = Import-Module (Join-Path $PSScriptRoot 'WebApiDiscovery.psd1') -Force -PassThru
        try {
            $module.ExportedFunctions.Keys | Sort-Object | Should -Be @(
                'ConvertFrom-HarCatalogue', 'Invoke-HarCapture', 'Invoke-HarCatalogue', 'Invoke-SanitizeHar', 'Stop-HarRecording')
            (Get-Command Invoke-HarCapture).Parameters.Keys | Should -Contain 'Describe'
            (Get-Command Invoke-SanitizeHar).Parameters.Keys | Should -Contain 'WhatIf'
        }
        finally { Remove-Module $module }
    }
}
