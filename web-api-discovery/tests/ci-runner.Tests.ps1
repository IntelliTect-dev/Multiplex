#Requires -Version 7.0
#Requires -Modules @{ ModuleName = "Pester"; ModuleVersion = "5.0.0" }

# This repo's CI runs on the self-hosted build runner (issue #146). Unlike a
# GitHub-hosted runner, that one is persistent, shares its host with other
# runners, and its user has no sudo. These tests keep the workflow fit for it:
# each fails if the job goes back to ubuntu-latest, to needing sudo where the
# tool is already there, or to claiming a fixed X display.

BeforeAll {
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
    # The skill lives in a subfolder of Multiplex; GitHub runs only workflows at
    # the repository root, so that is where its CI is.
    $script:CiYml = Get-Content -Raw (Join-Path $script:RepoRoot '..' '.github/workflows/web-api-discovery.yml')

    function script:Get-WorkflowStep {
        # The code of one step, from its `- name:` line to the next step's, with
        # comment lines dropped so a comment that explains why there is no sudo
        # does not read as a sudo call.
        param([string]$Name)

        $steps = @($script:CiYml -split '(?m)^(?=[ \t]*-[ \t]*name:)' |
            Where-Object { $_ -match "^\s*-\s*name:\s*$([regex]::Escape($Name))" })
        $steps.Count | Should -Be 1 -Because "exactly one step's name starts with '$Name'"

        ($steps[0] -split '\r?\n' | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    }

    # `if ! command -v Xvfb ...; then ... fi`: the only place apt may run.
    $script:XvfbGuard = '(?s)if\s+!\s+command\s+-v\s+Xvfb\b.*?\n\s*fi\b'
}

Describe "This repo's CI runs on the self-hosted build runner (issue #146)" {

    It 'runs the Pester job on the self-hosted build runner' {
        $script:CiYml | Should -Match '(?m)^\s*runs-on:\s*\[\s*self-hosted\s*,\s*linux\s*,\s*build\s*\]\s*$'
        $script:CiYml | Should -Not -Match '(?m)^\s*runs-on:\s*ubuntu-' -Because 'no job is left on a GitHub-hosted image'
    }

    It 'installs gitleaks without sudo, onto the PATH of the steps that follow' {
        $step = Get-WorkflowStep 'Install gitleaks'
        $step | Should -Not -Match '\bsudo\b' -Because "the runner's user has no sudo"
        $step | Should -Match 'GITHUB_PATH' -Because 'the Pester suite, a later step, must find it'
    }

    It 'installs Xvfb with apt only where it is missing' {
        $step = Get-WorkflowStep 'Start a virtual display'
        $step | Should -Match $script:XvfbGuard
        [regex]::Match($step, $script:XvfbGuard).Value | Should -Match 'apt-get install -y xvfb' -Because 'an image without Xvfb still gets it'
        ($step -replace $script:XvfbGuard, '') | Should -Not -Match '\bsudo\b' -Because "the runner's user has no sudo"
    }

    It 'takes a free X display instead of a fixed one' {
        # A persistent, shared host can already have :99 taken, by a job of
        # another runner or by the lock a killed job left behind.
        $step = Get-WorkflowStep 'Start a virtual display'
        $step | Should -Match 'Xvfb\s+-displayfd\b'
        $step | Should -Not -Match 'Xvfb\s+:\d+'
        $step | Should -Not -Match 'DISPLAY=:\d+'
    }
}
