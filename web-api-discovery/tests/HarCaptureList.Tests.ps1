#Requires -Version 7.0
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Behavior tests for `Get-HarCapture` -- the operator-facing list (issue #102).
#
# THE FINDING THIS EXISTS FOR. An inventory of the store already existed and was
# not reachable: `capture-store.js`, driven from PowerShell by
# `Get-HarCaptureInventory`, used by the batch drivers and exported by no
# command. To answer "what have I recorded?" an operator had to run an internal
# script by hand and read raw JSON. Measured in a consuming repo on 2026-10-14:
# 122 entries, and the only way to see them was `node capture-store.js | jq`.
#
# WHAT IS PINNED HERE.
#
#   EVERY CLASS IS LISTED. A session, a legacy capture at the store root, and a
#   phone capture somebody imported by hand. Listing two of the three silently
#   is the defect the shared walk exists to prevent, and a command on top of it
#   must not re-introduce it one layer up.
#   EACH CARRIES ITS DESCRIPTION, including the imported one, whose description
#   is in a hand-written CAPTURE.md rather than a session.json.
#   A MISSING DESCRIPTION IS FLAGGED, not blank. "No description" and "I did not
#   render the column" look identical when the cell is empty, and only one of
#   them is the operator's problem.
#   IT IS OBJECTS, NOT TEXT. `ConvertTo-Json`, `Where-Object` and `Sort-Object`
#   work on the result; the console table is PowerShell's formatting engine
#   applied to those objects.
#   IT DOES NOT WALK THE TREE ITSELF. There is one enumeration of a capture
#   store and this is not a second one.
#
# NOT ONE CAPTURED VALUE IS PRINTED. Site, stamp, size, stage and a description
# the operator wrote themselves. Nothing here opens a HAR.

BeforeAll {
    $script:RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..') | Select-Object -ExpandProperty Path
    $script:ScriptsDir = Join-Path $script:RepoRoot 'skills/web-api-discovery/scripts'
    $script:ListPs1    = Join-Path $script:ScriptsDir 'capture/Get-HarCapture.ps1'
    $script:FormatXml  = Join-Path $script:ScriptsDir 'capture/HarCapture.Format.ps1xml'

    # A fixture store built from nothing. Not one byte comes from a real
    # capture: the shapes were measured, the contents are invented.
    function script:New-FixtureStore {
        param([Parameter(Mandatory)][string]$Path)

        $root = Join-Path $Path '.har-captures'
        $hostDir = Join-Path $root 'app.example.test'

        $writeHar = {
            param($p)
            New-Item -ItemType Directory -Path (Split-Path $p -Parent) -Force | Out-Null
            Set-Content -LiteralPath $p -Encoding utf8 -Value (
                @{ log = @{ version = '1.2'; creator = @{ name = 'fixture'; version = '1' }
                        entries = @()
                    }
                } | ConvertTo-Json -Depth 8)
        }

        $session = {
            param($dir, $describe, [switch]$Unended, [switch]$Scrubbed, [switch]$Catalogued)
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            & $writeHar (Join-Path $dir 'raw.har')
            $body = [ordered]@{
                uri        = 'https://app.example.test/'
                sessionDir = $dir
                startedUtc = '2026-01-01T00:00:00.000Z'
            }
            if ($describe) { $body.describe = $describe }
            if (-not $Unended) { $body.endedUtc = '2026-01-01T00:05:00.000Z' }
            Set-Content -LiteralPath (Join-Path $dir 'session.json') -Encoding utf8 `
                -Value ($body | ConvertTo-Json -Depth 6)
            if ($Scrubbed) { & $writeHar (Join-Path $dir 'scrubbed.har') }
            if ($Catalogued) {
                & $writeHar (Join-Path $dir 'scrubbed.har')
                Set-Content -LiteralPath (Join-Path $dir 'digest.json') -Encoding utf8 -Value '{}'
                Set-Content -LiteralPath (Join-Path $dir 'catalogue.json') -Encoding utf8 -Value '[]'
            }
        }

        # A finished, catalogued recorder session.
        & $session (Join-Path $hostDir '2026-01-02-000001') 'checkout, card payment' -Catalogued
        # One the recorder never finished closing -- reported, never refused.
        & $session (Join-Path $hostDir '2026-01-02-000002') 'browsing the catalogue' -Unended
        # One nobody described. The flag is what this fixture is for.
        & $session (Join-Path $hostDir '2026-01-02-000003') $null -Scrubbed
        # A date-stamped session at the store ROOT, from before the host layer.
        & $session (Join-Path $root '2026-01-01-000001') 'the legacy layout' -Scrubbed
        # A phone capture somebody imported by hand: a mitmproxy dump, no
        # session.json, and a description they wrote in CAPTURE.md.
        $phone = Join-Path $root 'phone.example.test' | Join-Path -ChildPath '2026-01-03-000001'
        New-Item -ItemType Directory -Path $phone -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $phone 'raw.mitm') -Encoding utf8 -Value 'not-a-har'
        Set-Content -LiteralPath (Join-Path $phone 'CAPTURE.md') -Encoding utf8 -Value @(
            '# Grocery app checkout, iPhone, mitmproxy', '', 'Tapped through the basket.')

        return $root
    }
}

Describe 'Get-HarCapture lists the store' {

    BeforeAll {
        $script:Store = script:New-FixtureStore -Path (Join-Path $TestDrive 'list')
        $script:Captures = @(& $script:ListPs1 -Path $script:Store)
    }

    It 'lists every capture in the store, of every class' {
        # Named, not counted. "5 captures found" is equally true when the five
        # are the wrong five, so each class is asserted by the stamp that
        # carries it.
        $script:Captures.Count | Should -Be 5 -Because (
            ($script:Captures | ForEach-Object { "$($_.Site)/$($_.Stamp) [$($_.Class)]" }) -join '; ')

        $byStamp = @{}
        foreach ($c in $script:Captures) { $byStamp[$c.Stamp] = $c }

        $byStamp['2026-01-02-000001'].Class | Should -Be 'session' -Because 'a <host>/<stamp> capture'
        $byStamp['2026-01-01-000001'].Class | Should -Be 'legacy' -Because (
            'a date-stamped capture at the store root is the population a host-scoped walk misses')
        $byStamp['2026-01-03-000001'].Class | Should -Be 'imported' -Because (
            'a phone capture with a CAPTURE.md was imported on purpose, not merely unrecognised')
    }

    It 'carries the site, and says so by its absence for a capture at the store root' {
        ($script:Captures | Where-Object Stamp -eq '2026-01-02-000001').Site |
            Should -Be 'app.example.test'
        # NOT '.har-captures', which is what the parent directory literally is:
        # printing that would name the store as though it were a site.
        ($script:Captures | Where-Object Stamp -eq '2026-01-01-000001').Site |
            Should -BeNullOrEmpty -Because 'there is no host layer to read a site from'
    }

    It 'carries each description, the hand-written one included' {
        ($script:Captures | Where-Object Stamp -eq '2026-01-02-000001').Description |
            Should -Be 'checkout, card payment' -Because 'the recorder wrote it into session.json'
        ($script:Captures | Where-Object Stamp -eq '2026-01-01-000001').Description |
            Should -Be 'the legacy layout'
        ($script:Captures | Where-Object Stamp -eq '2026-01-03-000001').Description |
            Should -Be 'Grocery app checkout, iPhone, mitmproxy' -Because (
            'all six foreign entries in the measured store were described phone captures')
    }

    It 'flags a capture nobody described, instead of leaving the cell blank' {
        $undescribed = $script:Captures | Where-Object Stamp -eq '2026-01-02-000003'
        # The OBJECT keeps the honest datum -- there is no description, so the
        # property is null and a script can test it.
        $undescribed.Description | Should -BeNullOrEmpty

        # The TABLE says so out loud. An empty cell reads as "the column did not
        # render" just as readily as "nobody described this capture", and only
        # one of those is the operator's problem.
        $rendered = ($script:Captures | Format-Table | Out-String -Width 200)
        $rendered | Should -Match '\(no description\)'
    }

    It 'reports whether the recording ended cleanly or was cut off' {
        ($script:Captures | Where-Object Stamp -eq '2026-01-02-000001').Ended | Should -BeTrue
        ($script:Captures | Where-Object Stamp -eq '2026-01-02-000002').Ended |
            Should -BeFalse -Because 'the recorder never wrote endedUtc for it'
    }

    It 'reports how far down the pipeline each capture got' {
        $byStamp = @{}
        foreach ($c in $script:Captures) { $byStamp[$c.Stamp] = $c }

        $byStamp['2026-01-02-000001'].Scrubbed  | Should -BeTrue
        $byStamp['2026-01-02-000001'].Digest    | Should -BeTrue
        $byStamp['2026-01-02-000001'].Catalogue | Should -BeTrue
        $byStamp['2026-01-02-000001'].Stage     | Should -Be 'catalogued'

        $byStamp['2026-01-02-000002'].Scrubbed  | Should -BeFalse
        $byStamp['2026-01-02-000002'].Stage     | Should -Be 'raw'
        $byStamp['2026-01-02-000003'].Stage     | Should -Be 'scrubbed'

        # A capture this pipeline did not make has no stage in it, and saying
        # `raw` about one would read as "ready to scrub" -- which is the one
        # thing that must never happen to a dump with no session to attribute
        # its traffic to.
        $byStamp['2026-01-03-000001'].Stage | Should -Be 'imported'
        $byStamp['2026-01-03-000001'].RecorderOutput | Should -BeFalse
        $byStamp['2026-01-02-000001'].RecorderOutput | Should -BeTrue
    }

    It 'carries the size of the raw, and the path the delete command takes' {
        $one = $script:Captures | Where-Object Stamp -eq '2026-01-02-000001'
        $one.Bytes | Should -BeGreaterThan 0
        Test-Path -LiteralPath $one.Path | Should -BeTrue -Because (
            'Remove-HarCapture is given this, so it has to be the capture directory')
    }

    It 'sizes a hand-imported capture too, which is the number you delete on' {
        # The walk reports no PATH for a capture it cannot attribute -- handing
        # one over is handing it to a scrub that could not say whose traffic it
        # scrubbed. The SIZE is a different question: it is a fact about the
        # directory, and it is what an operator reads to decide what to keep. A
        # multi-gigabyte phone dump listed with no size at all is the failure
        # this issue exists to remove.
        ($script:Captures | Where-Object Stamp -eq '2026-01-03-000001').Bytes |
            Should -BeGreaterThan 0
    }

    It 'is objects on the pipeline, not text' {
        # The whole reason the catalogue emits objects too: a caller composes
        # with the result instead of parsing a table back out of a string.
        $script:Captures[0].PSObject.TypeNames |
            Should -Contain 'WebApiDiscovery.HarCapture.StoreEntry'
        $json = $script:Captures | ConvertTo-Json -Depth 5
        { $json | ConvertFrom-Json } | Should -Not -Throw
        @($script:Captures | Where-Object Class -eq 'session').Count | Should -Be 3
    }

    It 'surfaces what the recorder recorded about its own failure' {
        # The inventory already carried this and nothing read it (#450): a
        # capture whose post-processing failed listed identically to one that
        # simply has not been scrubbed yet.
        $dir = Join-Path $script:Store 'app.example.test' | Join-Path -ChildPath '2026-01-04-000001'
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $dir 'raw.har') -Encoding utf8 -Value '{"log":{"entries":[]}}'
        Set-Content -LiteralPath (Join-Path $dir 'session.json') -Encoding utf8 -Value (
            [ordered]@{
                sessionDir  = $dir
                endedUtc    = '2026-01-04T00:05:00.000Z'
                describe    = 'the one that failed'
                postProcess = @{ errors = @('scrub failed: heap out of memory') }
                summary     = @{ parseError = 'Cannot create a string longer than 0x1fffffe8 characters' }
            } | ConvertTo-Json -Depth 6)

        $failed = @(& $script:ListPs1 -Path $dir)[0]
        $failed.Errors.Count | Should -Be 2 -Because 'both the stage error and the parse failure'
        ($failed.Errors -join ' ') | Should -Match 'heap out of memory'
        ($failed.Errors -join ' ') | Should -Match '0x1fffffe8'

        # And a clean capture is quiet, or the assertion above would pass on
        # anything.
        (@(& $script:ListPs1 -Path (Join-Path $script:Store 'app.example.test/2026-01-02-000001'))[0]).Errors |
            Should -BeNullOrEmpty
    }

    It 'does not grow a second walk over the store' {
        # `capture-store.js` is the one enumeration -- the same one
        # `resolveSession` uses for stop, status and catalogue. A command that
        # re-derived "what is a capture" in PowerShell would be free to
        # disagree with it, and the store having two answers to that is the
        # defect this subsystem exists to avoid (#387).
        $text = Get-Content -LiteralPath $script:ListPs1 -Raw
        $text | Should -Match 'Get-HarCaptureInventory'
        $text | Should -Not -Match 'Get-ChildItem' -Because (
            'enumerating the tree here would be the second walk')
    }
}

Describe 'Get-HarCapture tells one run from another' {

    # Issue #26. The store is shared and several sessions record into it at
    # once. Nothing in a capture said which of them made it, so the only ways
    # to pick out "mine" were a time window -- which the sessions interleave
    # across -- and the description, which over-matched on its first real use
    # and could not separate three attempts that all read the same.
    #
    # THE FIXTURE IS THAT CASE: two runs, interleaved by stamp, on one site,
    # with ONE description between them. Plus the three kinds of capture that
    # have no run to show, each for a different reason.

    BeforeAll {
        $script:RunA = 'aaaaaaaaaaaa'
        $script:RunB = 'bbbbbbbbbbbb'
        $script:SameDescription = 'create a step with one video, then delete it'

        $root = Join-Path $TestDrive 'runs/.har-captures'
        $hostDir = Join-Path $root 'app.example.test'
        $record = {
            param($stamp, [hashtable]$Identity)
            $dir = Join-Path $hostDir $stamp
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $dir 'raw.har') -Encoding utf8 -Value '{"log":{"entries":[]}}'
            $body = [ordered]@{
                uri        = 'https://app.example.test/'
                sessionDir = $dir
                describe   = $script:SameDescription
                startedUtc = '2026-03-01T00:00:00.000Z'
                endedUtc   = '2026-03-01T00:05:00.000Z'
            }
            foreach ($key in $Identity.Keys) { $body[$key] = $Identity[$key] }
            Set-Content -LiteralPath (Join-Path $dir 'session.json') -Encoding utf8 `
                -Value ($body | ConvertTo-Json -Depth 6)
        }
        $runA = @{ id = $script:RunA; basis = 'process'; lineage = @('111111111111', $script:RunA) }
        $runB = @{ id = $script:RunB; basis = 'terminal'; lineage = @($script:RunB, '222222222222') }

        & $record '2026-03-01-000001' @{ captureId = 'capture-a1'; run = $runA }
        & $record '2026-03-01-000002' @{ captureId = 'capture-b1'; run = $runB }
        & $record '2026-03-01-000003' @{ captureId = 'capture-a2'; run = $runA }
        & $record '2026-03-01-000004' @{ captureId = 'capture-b2'; run = $runB }
        # Recorded before any capture carried a run.
        & $record '2026-03-01-000005' @{}
        # Recorded since, where the recorder could not read its own ancestry.
        & $record '2026-03-01-000006' @{
            captureId = 'capture-unread'
            run       = @{ id = $null; reason = 'process ancestry is not read on this platform' }
        }
        # And one this recorder did not make.
        $phone = Join-Path $root 'phone.example.test' | Join-Path -ChildPath '2026-03-01-000007'
        New-Item -ItemType Directory -Path $phone -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $phone 'raw.mitm') -Encoding utf8 -Value 'not-a-har'
        Set-Content -LiteralPath (Join-Path $phone 'CAPTURE.md') -Encoding utf8 -Value '# A phone capture'

        $script:RunStore = $root
        $script:RunCaptures = @(& $script:ListPs1 -Path $root)
        $script:RunTable = ($script:RunCaptures | Sort-Object Stamp | Format-Table | Out-String -Width 200)
        $script:RowFor = {
            param($stamp)
            ($script:RunTable -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($stamp) }) -join "`n"
        }
    }

    It 'carries the run and the capture id on every object' {
        $a1 = $script:RunCaptures | Where-Object Stamp -eq '2026-03-01-000001'
        $a1.RunId     | Should -Be $script:RunA
        $a1.RunBasis  | Should -Be 'process'
        $a1.CaptureId | Should -Be 'capture-a1'
        ($script:RunCaptures | Where-Object Stamp -eq '2026-03-01-000002').RunBasis | Should -Be 'terminal'
    }

    It 'selects one run with a pipeline filter, and never returns the other run''s captures' {
        $mine = @($script:RunCaptures | Where-Object { $_.RunId -and $_.RunId -eq $script:RunA })
        ($mine.Stamp | Sort-Object) | Should -Be @('2026-03-01-000001', '2026-03-01-000003')

        $theirs = @($script:RunCaptures | Where-Object { $_.RunId -and $_.RunId -eq $script:RunB })
        ($theirs.Stamp | Sort-Object) | Should -Be @('2026-03-01-000002', '2026-03-01-000004')

        @($mine | Where-Object { $_.Stamp -in $theirs.Stamp }).Count | Should -Be 0
    }

    It 'separates what a time window and the description cannot' {
        # THE STAMPS INTERLEAVE. Any window wide enough to hold run A's two
        # captures also holds one of run B's.
        $ordered = $script:RunCaptures | Where-Object RunId | Sort-Object Stamp
        ($ordered.RunId -join ',') | Should -Be "$script:RunA,$script:RunB,$script:RunA,$script:RunB"

        # AND THEY ALL READ THE SAME, so the description selects every one of
        # them -- along with the two that have no run.
        @($script:RunCaptures | Where-Object Description -eq $script:SameDescription).Count | Should -Be 6

        # Within a run, each attempt is still its own capture.
        $ids = @($script:RunCaptures | Where-Object { $_.RunId -eq $script:RunA }).CaptureId
        ($ids | Sort-Object -Unique).Count | Should -Be 2
    }

    It 'lists a capture recorded before runs existed, and says it has none' {
        $old = $script:RunCaptures | Where-Object Stamp -eq '2026-03-01-000005'
        $old | Should -Not -BeNullOrEmpty -Because 'a capture with no run is still a capture'
        # The OBJECT keeps the honest datum, as it does for a missing
        # description: nothing was recorded, so the properties are null.
        $old.RunId     | Should -BeNullOrEmpty
        $old.CaptureId | Should -BeNullOrEmpty
        $old.RunBasis  | Should -BeNullOrEmpty
        # The TABLE says so, rather than guessing a run from the stamp or
        # leaving a cell that reads as "the column did not render".
        (& $script:RowFor '2026-03-01-000005') | Should -Match '\(none recorded\)'
    }

    It 'says so when the run could not be read, which is not the same as none recorded' {
        $unread = $script:RunCaptures | Where-Object Stamp -eq '2026-03-01-000006'
        $unread.CaptureId | Should -Be 'capture-unread' -Because 'it is still its own capture'
        $unread.RunId     | Should -BeNullOrEmpty
        (& $script:RowFor '2026-03-01-000006') | Should -Match '\(unidentified\)'
        (& $script:RowFor '2026-03-01-000006') | Should -Not -Match 'none recorded'
    }

    It 'claims no run for a capture this recorder did not make' {
        $phone = $script:RunCaptures | Where-Object Stamp -eq '2026-03-01-000007'
        $phone.RunId | Should -BeNullOrEmpty
        # A dash, as the Ended column does: "none recorded" would say a
        # recording of ours is missing something, and it is not ours.
        (& $script:RowFor '2026-03-01-000007') | Should -Match 'imported\s+--\s'
        (& $script:RowFor '2026-03-01-000007') | Should -Not -Match 'none recorded|unidentified'
    }

    It 'prints the whole run id, because a shortened one could only be matched by guessing' {
        # A table that showed eight characters of twelve would teach `-like`,
        # and substring matching is what over-selected in the first place.
        (& $script:RowFor '2026-03-01-000001') | Should -Match "\s$script:RunA\s"
        (& $script:RowFor '2026-03-01-000002') | Should -Match "\s$script:RunB\s"

        # And it still fits beside the description on an ordinary console.
        $narrow = ($script:RunCaptures | Sort-Object Stamp | Format-Table | Out-String -Width 120)
        $narrow | Should -Match "\s$script:RunA\s"
        $narrow | Should -Match 'Description' -Because 'the run column must not push the description off the table'
        $narrow | Should -Match 'create a step'
    }

    It 'shows the run and the capture id in the list view' {
        $list = ($script:RunCaptures | Where-Object Stamp -eq '2026-03-01-000001' | Format-List | Out-String -Width 200)
        $list | Should -Match "RunId\s+:\s+$script:RunA"
        $list | Should -Match 'RunBasis\s+:\s+process'
        $list | Should -Match 'CaptureId\s+:\s+capture-a1'

        $old = ($script:RunCaptures | Where-Object Stamp -eq '2026-03-01-000005' | Format-List | Out-String -Width 200)
        $old | Should -Match 'RunId\s+:\s+\(none recorded\)'
        $old | Should -Match 'CaptureId\s+:\s+\(none recorded\)'
    }

    It 'selects nothing when the run asked for is empty' {
        # THE HAZARD THE DOCUMENTED FILTER GUARDS AGAINST. `RunId -eq $run` with
        # an unset $run is true for every capture that has no run -- every
        # recording made before this change, in one stroke.
        $unset = $null
        @($script:RunCaptures | Where-Object { $_.RunId -eq $unset }).Count |
            Should -BeGreaterThan 0 -Because 'which is why the filter is never written that way'

        @($script:RunCaptures | Where-Object { $_.RunId -and $_.RunId -eq $unset }).Count | Should -Be 0
        @($script:RunCaptures | Where-Object { $_.RunId -and $_.RunId -eq '' }).Count | Should -Be 0
    }
}

Describe 'Get-HarCapture over an empty or absent store' {

    It 'returns nothing for a store with no captures in it, rather than failing' {
        $empty = Join-Path $TestDrive 'empty/.har-captures'
        New-Item -ItemType Directory -Path $empty -Force | Out-Null
        $result = @(& $script:ListPs1 -Path $empty)
        $result.Count | Should -Be 0
    }

    It 'says so when the path does not exist' {
        { & $script:ListPs1 -Path (Join-Path $TestDrive 'nope') -ErrorAction Stop } |
            Should -Throw -ExpectedMessage '*nope*'
    }
}

Describe 'HarCapture.Format.ps1xml renders the store entry' {

    BeforeAll {
        $script:Xml = [xml](Get-Content -LiteralPath $script:FormatXml -Raw)
    }

    It 'is well-formed XML that PowerShell will load' {
        # A malformed format file fails silently: Update-FormatData is called
        # with -ErrorAction SilentlyContinue, so the console falls back to the
        # default rendering and nobody notices.
        { [xml](Get-Content -LiteralPath $script:FormatXml -Raw) } | Should -Not -Throw
    }

    It 'has a view selected by the store-entry type' {
        $selectors = $script:Xml.SelectNodes('//ViewSelectedBy/TypeName') |
            ForEach-Object { $_.InnerText }
        $selectors | Should -Contain 'WebApiDiscovery.HarCapture.StoreEntry'
    }

    It 'puts <Needle> in the default table' -ForEach @(
        @{ Needle = 'Site' }
        @{ Needle = 'Stamp' }
        @{ Needle = 'Size' }
        @{ Needle = 'Ended' }
        @{ Needle = 'Stage' }
        @{ Needle = 'Run' }
        @{ Needle = 'Description' }
    ) {
        # The issue's list, column by column: site, start time, description,
        # size, ended-or-cut-off, and how far it got.
        $labels = $script:Xml.SelectNodes('//TableColumnHeader/Label') |
            ForEach-Object { $_.InnerText }
        $labels | Should -Contain $Needle
    }
}
