#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Behavior tests for issue #92 -- the web-api-discovery skill must always
# prompt for mobile-app discovery, and must ask before initializing git in a
# folder that is not yet a repository.

BeforeAll {
    $script:RepoRoot  = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:SkillPath = Join-Path $script:RepoRoot 'skills/web-api-discovery/SKILL.md'
    $script:SkillText = Get-Content -LiteralPath $script:SkillPath -Raw
}

Describe 'web-api-discovery SKILL.md mandatory prompts (issue #92)' {

    Context 'Hard Gate' {
        It 'requires confirmation of mobile-app inclusion before mutating the filesystem' {
            # The Hard Gate must explicitly call out the mobile-app decision so
            # the agent cannot silently skip Phase 1.5.
            $script:SkillText | Should -Match '(?i)mobile app .*(y(es)?|n(o)?)'
        }

    }

    Context 'Phase 1.5 -- Mobile App Discovery' {
        It 'is marked as a required prompt, not optional' {
            # The header is the load-bearing signal that the agent must always ask.
            $script:SkillText | Should -Match 'Phase 1\.5 -- Mobile App Discovery \(required prompt\)'
        }

        It 'is no longer described as opt-in / optional in its first paragraph' {
            # Grab the Phase 1.5 section up to the next "### Phase" header.
            if ($script:SkillText -match '(?s)### Phase 1\.5[^\n]*\n(.*?)(?=\n### Phase )') {
                $section = $Matches[1]
                $section | Should -Not -Match '(?i)\bopt-in\b'
                $section | Should -Not -Match '(?i)This phase is \*\*optional\*\*'
            } else {
                throw "Phase 1.5 section not found in SKILL.md"
            }
        }

        It 'instructs the agent to ask the user explicitly' {
            $script:SkillText | Should -Match '(?i)ask the user'
            $script:SkillText | Should -Match '\[y/N\]'
        }
    }

    Context 'Git init in a folder that is not a repository' {
        It 'asks the user before running git init' {
            $script:SkillText | Should -Match '(?i)not a git repository'
            $script:SkillText | Should -Match '(?i)git init'
            $script:SkillText | Should -Match '\[Y/n\]'
        }

        It 'no longer offers to seed the project with IntelliSDLC.ai' {
            $script:SkillText | Should -Not -Match 'Pull-SDLC\.ai\.ps1'
            $script:SkillText | Should -Not -Match '(?i)sdlc\.ai'' remote|remote (called |named )?[`'']?sdlc\.ai'
        }
    }

    Context 'Backwards compatibility' {
        It 'still lists Phases 1 through 11' {
            1..11 | ForEach-Object {
                $script:SkillText | Should -Match "Phase $_ --"
            }
        }
    }
}
