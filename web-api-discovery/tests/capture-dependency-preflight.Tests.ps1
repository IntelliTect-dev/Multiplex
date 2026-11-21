#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Wrapper for the zero-dep Node behavior tests that pin issue #512 -- the
# recorder finds its browser driver WHERE THE OPERATOR IS, and a capture folder
# that is not yet a repository is protected against the `git init` that comes
# later. Pester is the only suite CI runs, so a Node test with no wrapper here
# is a test that never runs on a pull request.
#
#   lib/node-dependency.test.js       the four searched locations, their order,
#                                     the two kinds of location, and a failure
#                                     message that names every folder it asked.
#
#   capture/capture-preflight.test.js the preflight runs before anything is
#                                     scaffolded, prompted or opened;
#                                     --validate-only reports a missing
#                                     dependency rather than passing (#25);
#                                     and a non-repo capture folder gets the
#                                     ignore rules left behind for it.
#
# The Node suites build REAL node_modules trees and run a REAL `git init &&
# git add -A`. Both are the only honest shape: the defect IS Node's resolution
# semantics, and the protection's whole claim is about what git stages -- a
# test that inspected a resolved string, or grepped the .gitignore for the
# right words, would have passed before the fix and after it.
#
# The Describe block below covers the half the Node suites cannot reach: that
# the recorder still declares no new command-line option for any of this.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
    $script:CaptureJs  = Join-Path $script:ScriptsDir 'capture/capture-har.js'
}

Describe 'the recorder resolves its dependencies where the operator stands (#512)' {
    It 'runs <Name> and all of its behavioral assertions pass' -ForEach @(
        @{ Name = 'lib/node-dependency.test.js';        Expect = 'All node-dependency tests passed' }
        @{ Name = 'capture/capture-preflight.test.js';  Expect = 'All capture-preflight tests passed' }
    ) {
        $testJs = Join-Path $script:ScriptsDir $Name
        Test-Path -LiteralPath $testJs | Should -BeTrue

        & node --check $testJs 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0

        $out = & node $testJs 2>&1
        $exit = $LASTEXITCODE
        if ($exit -ne 0) {
            Write-Host ($out -join "`n")
        }
        $exit | Should -Be 0
        ($out -join "`n") | Should -Match $Expect
    }
}

Describe 'no new option was introduced to configure any of this (#512)' {

    It 'accepts exactly the start options it accepted before -- no new lever' {
        # The prompt-first rule. Every lever this feature uses already existed:
        # the working directory, the script's own location, npm's global root,
        # and NODE_PATH. Adding a switch would have been the easy answer and
        # the wrong one.
        #
        # Asserted against the recorder's OWN option list, read out of the
        # running module rather than grepped for flag-shaped strings in the
        # source. A grep would trip over ordinary words like 'install' that now
        # appear as npm arguments, and -- worse -- would keep passing if a new
        # option were spelled in a way the pattern did not anticipate. The list
        # is what actually decides what `start` accepts.
        $json = & node -e @'
const m = require(process.argv[1]);
process.stdout.write(JSON.stringify(m.START_OPTIONS || null));
'@ $script:CaptureJs
        $LASTEXITCODE | Should -Be 0

        $options = $json | ConvertFrom-Json
        $options | Should -Not -BeNullOrEmpty -Because 'the option list is exported so it can be pinned'
        # --browser and --attach joined the list in #73, which is a different
        # question from the one this Describe asks. #512 was "where is the
        # dependency", and the answer had to use mechanisms that already existed
        # -- so it adds no option, and that is still pinned: none of the four
        # search locations is reachable from this list.
        #
        # #73 is "WHOSE BROWSER", which no existing lever can express. Playwright's
        # launch is a test-automation launch, and recording in the ordinary Chrome
        # a person would use means recording in a browser this script did not
        # launch. The issue names both flags, and the argument for them is written
        # up on capture-har.js and in SKILL.md rather than here.
        #
        # `ai` joined the list in #98, which is a third question again: WHO
        # DRIVES. The recorder could only record, so having an AI drive a capture
        # meant somebody opening an agent session and hand-writing a long prompt
        # -- the goal, the endpoint, what to vary, what never to do, when to stop
        # -- which is the same every time except the goal, and most of which is
        # safety text. No existing lever can express it: -Describe is the goal and
        # is already required, --browser says whose BROWSER it is (and `--ai`
        # implies native rather than adding a mode), and --attach names one that
        # is already running. The issue names the switch, and the argument for it
        # is written up on capture-har.js and in SKILL.md rather than here.
        #
        # It takes no value and brought no companion: there is deliberately no
        # `--agent`, because a second driver would be a second option and nothing
        # needs one yet.
        #
        # `ai-tools` joined in #112, owner-approved on that issue: WHICH TOOLS the
        # driver has. `auto` (the default) runs it in auto permission mode, whose
        # classifier approves or blocks each action; `browser-only` adds
        # `--tools ""` so every built-in tool is removed and only the browser
        # tools remain, for a site the operator is signed into something
        # sensitive on. No existing lever can say it, and it is refused without --ai.
        #
        #
        # `pace` joined with the pacing and throttling change, at the owner's
        # request ("configurable, with a flag to set them to 0 for tests"): HOW
        # FAST the recorder acts on a page and an agent is told to. No existing
        # lever can say it -- the pace is a range per kind of action plus a
        # ceiling, not a mode of any flag above -- and `off` is the test switch
        # the request names. Unreadable values are refused, never defaulted.
        #
        # THE EQUALITY IS KEPT DELIBERATELY. Loosening it to a subset check would
        # end the prompt-first gate for every future option, which is the whole
        # value of this assertion -- the next one has to come through here.
        ($options | Sort-Object) -join ',' | Should -Be (
            (@('ai', 'ai-tools', 'attach', 'browser', 'describe', 'isolated', 'log-level', 'no-wait',
               'output-path', 'pace', 'port', 'profile', 'snapshot-seconds', 'uri', 'validate-only') |
             Sort-Object) -join ',') -Because (
            'issue #512 adds no command-line option; the two #73 added, the ones #98 and #112 added, ' +
            'and the pacing change''s --pace are the ones those changes name. Changing this list is a ' +
            'deliberate API decision that the Adding Command-Line Options rule says to prompt for')
    }

    It 'rejects an unknown option rather than ignoring it' {
        # The guarantee above is only worth something if an option the recorder
        # does not know is refused loudly. --validate-only is used so the check
        # needs no browser and no profile.
        $out = & node $script:CaptureJs start --uri 'https://example.com' `
            --describe 'option contract fixture' --playwright-path 'C:\nowhere' --validate-only 2>&1
        $LASTEXITCODE | Should -Be 2
        ($out | Out-String) | Should -Match '(?i)does not accept'
    }
}
