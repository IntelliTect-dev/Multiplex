#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Behavior tests for issue #135 -- an MCP server built from a discovered site
# must carry that site's name, address and icon, or clients such as ChatGPT
# show a placeholder icon for it. The skill must tell the agent to find the
# icon during discovery and to declare it when the server is built.

BeforeAll {
    $script:RepoRoot  = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:SkillPath = Join-Path $script:RepoRoot 'skills/web-api-discovery/SKILL.md'
    $script:SkillText = Get-Content -LiteralPath $script:SkillPath -Raw

    function Get-Section([string] $HeaderPattern) {
        if ($script:SkillText -match "(?s)$HeaderPattern[^\n]*\n(.*?)(?=\n#{2,3} )") { return $Matches[1] }
        throw "Section '$HeaderPattern' not found in SKILL.md"
    }
}

Describe 'web-api-discovery SKILL.md: the site''s icon (issue #135)' {

    It 'Phase 1 finds the site''s icon from what the site itself declares' {
        $discover = Get-Section '### Phase 1 -- Discover'
        $discover | Should -Match '(?i)icon'
        $discover | Should -Match 'apple-touch-icon'
        $discover | Should -Match '(?i)manifest'
        $discover | Should -Match '(?i)square PNG'
    }

    It 'Phase 1 fits the icon to what ChatGPT''s upload takes: 256x256, at most 10 KB (issue #138)' {
        $discover = Get-Section '### Phase 1 -- Discover'
        $discover | Should -Match '256x256'
        $discover | Should -Match '10 KB'
    }

    It 'says ChatGPT takes an app''s icon from its Add form, not from serverInfo, and how to change one (issue #138)' {
        $script:SkillText | Should -Match 'ChatGPT does not take the icon from `serverInfo`'
        $script:SkillText | Should -Match '(?s)"Add custom MCP server" form uploads an icon'
        $script:SkillText | Should -Match '(?s)deleting the app and adding it again'
        $script:SkillText | Should -Match '(?s)README\s+tells the person to upload the same PNG'
    }

    It 'Phase 1 rasterizes an SVG-only icon and records where the icon came from' {
        $discover = Get-Section '### Phase 1 -- Discover'
        $discover | Should -Match '(?i)SVG'
        $discover | Should -Match '(?i)(record|note) (the URL|where)'
    }

    It 'the generated MCP server declares title, websiteUrl and icons in serverInfo' {
        $script:SkillText | Should -Match '(?s)\*\*The server says what site it wraps\.\*\*.*?serverInfo'
        $script:SkillText | Should -Match '`title`'
        $script:SkillText | Should -Match '`websiteUrl`'
        $script:SkillText | Should -Match '`icons`'
    }

    It 'says where the icon is served from: a data: URI over stdio, the server''s own origin over HTTP' {
        $script:SkillText | Should -Match '(?is)stdio.{0,200}`data:`\s+URI'
        $script:SkillText | Should -Match '(?i)own origin'
        $script:SkillText | Should -Match '(?i)without (sign-in|credentials)'
    }

    It 'lists shipping a server without its site''s icon as an anti-pattern' {
        $antiPatterns = Get-Section '## Anti-patterns'
        $antiPatterns | Should -Match '(?is)\*\*Do not\*\* ship an MCP server[^*]*icon'
    }
}
