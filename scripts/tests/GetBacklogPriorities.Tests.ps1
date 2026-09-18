# scripts\Get-BacklogPriorities.ps1 -- the classification, reached without `gh`.
#
# Dot-sourcing the script defines its rules, its axis table and the functions that classify
# one issue, and stops at the guard: fetching the backlog, reading -Overrides and the whole
# report sit below it. So these cases hand it synthetic issues rather than the live backlog,
# and what they pin is how the heuristic *reports* a call -- which calls an axis says are too
# thin to trust unread (#82), and the shape -Json has always carried -- not what today's
# backlog happens to contain. The ranking's arithmetic is proved the other way: by running
# the script against the real backlog before and after a change and comparing the -Json.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsDir = Split-Path -Parent $PSScriptRoot
$subject = Join-Path $scriptsDir 'Get-BacklogPriorities.ps1'

. $subject

function New-TestIssue {
    # An issue as `gh issue list --json` hands it over, carrying only what a case sets.
    param(
        [string]$Title = 'An issue',

        [string]$Body = '',

        [string[]]$Labels = @()
    )

    return [pscustomobject]@{
        number    = 1
        title     = $Title
        body      = $Body
        labels    = @($Labels | ForEach-Object { [pscustomobject]@{ name = $_ } })
        createdAt = '2026-09-01T00:00:00Z'
        updatedAt = '2026-09-01T00:00:00Z'
        comments  = @()
        url       = 'https://github.com/JojoEffect/SmartHome/issues/1'
        state     = 'OPEN'
    }
}

Describe 'The dot-source guard' {
    It 'defines the classification without calling gh' {
        # In a child process: this file has already dot-sourced the subject, so the
        # interesting claim is about what a *fresh* dot-source does. $repoRoot and $issues
        # are both assigned below the guard, so their absence is what proves the fetch
        # never ran -- and on a machine without an authenticated gh, a fetch that did run
        # would have exited 1 before either.
        $probe = Join-Path (New-TestDirectory -Name 'guard') 'probe.ps1'
        Set-TestFileContent -Path $probe -Content @(
            'Set-StrictMode -Version Latest'
            '$ErrorActionPreference = ''Stop'''
            ". '$subject'"
            'Write-Output ("classifies={0}" -f [bool](Get-Command Get-IssueRecord -ErrorAction SilentlyContinue))'
            'Write-Output ("axes={0}" -f @($axes.Keys).Count)'
            'Write-Output ("repoRootDefined={0}" -f [bool](Get-Variable -Name repoRoot -ErrorAction SilentlyContinue))'
            'Write-Output ("issuesDefined={0}" -f [bool](Get-Variable -Name issues -ErrorAction SilentlyContinue))'
        )

        $host_ = (Get-Process -Id $PID).Path
        $output = & $host_ -NoProfile -ExecutionPolicy Bypass -File $probe
        Assert-Equal -Expected 0 -Actual $LASTEXITCODE -Because 'a dot-source must not fail'

        $report = @{}
        foreach ($line in $output) {
            $parts = $line -split '=', 2
            $report[$parts[0]] = $parts[1]
        }

        Assert-Equal -Expected 'True' -Actual $report['classifies']
        Assert-Equal -Expected '5' -Actual $report['axes']
        Assert-Equal -Expected 'False' -Actual $report['repoRootDefined']
        Assert-Equal -Expected 'False' -Actual $report['issuesDefined']
    }

    It 'has nothing but declarations above it' {
        # The guard only holds while everything above it is a declaration. The fetch sat at
        # the top of this script until #82, which is why nothing in it could be tested
        # without a live, authenticated gh; this is the case that keeps it from drifting back.
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($subject, [ref]$null, [ref]$parseErrors)
        Assert-Equal -Expected 0 -Actual @($parseErrors).Count

        $statements = @($ast.EndBlock.Statements)
        $guardIndex = -1
        for ($i = 0; $i -lt $statements.Count; $i++) {
            if ($statements[$i] -is [System.Management.Automation.Language.IfStatementAst] -and
                $statements[$i].Extent.Text -match 'InvocationName') {
                $guardIndex = $i
                break
            }
        }

        Assert-True -Condition ($guardIndex -ge 0) -Because 'the guard itself must still be there'

        # The same two non-declarations Run-IntegrationTests.ps1 allows above its guard, for
        # the same reason: a dot-sourcing caller wants both anyway.
        $allowedAbove = @(
            "^Set-StrictMode -Version Latest$"
            "^\.\s*\(Join-Path \`$PSScriptRoot 'Common\.ps1'\)$"
        )

        foreach ($statement in $statements[0..($guardIndex - 1)]) {
            $text = $statement.Extent.Text
            $isDeclaration =
                $statement -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
                $statement -is [System.Management.Automation.Language.AssignmentStatementAst] -or
                @($allowedAbove | Where-Object { $text -match $_ }).Count -gt 0

            Assert-True -Condition $isDeclaration -Because ("line {0} runs on a dot-source: {1}" -f $statement.Extent.StartLineNumber, ($text -split "`n")[0])
        }
    }
}

Describe 'The axis table' {
    # The only $rules groups no row reads: Risk takes the worst of these bands that fires
    # rather than weighing groups against each other, so it is decided outside the table.
    $riskBands = @('SilentWrong', 'LoudFailure', 'Friction')

    It 'reads every rules group through exactly one axis, apart from the Risk bands' {
        # A group no row names would still be declared and still match, and score nothing --
        # the same silence #82 found one layer up, in a confidence computed and never shown.
        $read = @($axes.Keys | ForEach-Object { $axes[$_]['Signals'] })
        foreach ($group in @($rules | ForEach-Object { $_.Axis } | Sort-Object -Unique)) {
            if ($riskBands -ccontains $group) { continue }
            Assert-Equal -Expected 1 -Actual @($read | Where-Object { $_ -ceq $group }).Count `
                         -Because "rules group '$group' has to be read by exactly one axis"
        }
    }

    It 'names no group that has no rules' {
        # The other direction: a misspelt group reads nothing and reports confidence None for
        # ever, which looks exactly like an issue that says nothing.
        $declared = @($rules | ForEach-Object { $_.Axis })
        foreach ($name in $axes.Keys) {
            foreach ($signal in $axes[$name]['Signals']) {
                Assert-Contains -Item $signal -Collection $declared -Because "axis $name names group '$signal'"
            }
        }
    }

    It 'keeps the Confidence and Signals shape -Json has always carried' {
        # Built from the table now rather than spelled out member by member, so the order is
        # the table's. Consumers of -Json read these by name, and the comparison that proved
        # the refactor behaviour-preserving diffed them as text.
        $record = Get-IssueRecord -Issue (New-TestIssue)

        Assert-ArrayEqual -Expected @('Trust', 'EvidenceDebt', 'Where', 'VerifyNeeds', 'Track', 'Risk', 'Effort') `
                          -Actual @($record.Confidence.PSObject.Properties.Name)
        Assert-ArrayEqual -Expected @('Trust', 'EvidenceDebt', 'Hardware', 'Desk', 'VerifyHardware', 'VerifyCI', 'VerifyNone', 'Capability', 'Velocity', 'Risk', 'Effort') `
                          -Actual @($record.Signals.PSObject.Properties.Name)
    }
}

Describe 'Trust on thin evidence' {
    It 'marks a Trust that one matched phrase set' {
        # The shape of three of the four false positives at the top of a -Theme Trust run on
        # 2026-09-18: one W=3 pattern, here 'silently', earning the flag and the +30 alone.
        $record = Get-IssueRecord -Issue (New-TestIssue -Body 'The retry count is dropped silently.')

        Assert-True -Condition $record.Trust
        Assert-Equal -Expected 'Medium' -Actual $record.Confidence.Trust
        Assert-Contains -Item 'Trust' -Collection @(Get-LowEvidence -Record $record)
    }

    It 'does not mark a Trust two strong patterns agree on' {
        $record = Get-IssueRecord -Issue (New-TestIssue -Body 'It fails silently, and the run reports a false pass.')

        Assert-True -Condition $record.Trust
        Assert-Equal -Expected 'High' -Actual $record.Confidence.Trust
        Assert-False -Condition (@(Get-LowEvidence -Record $record) -contains 'Trust')
    }

    It 'does not mark a Trust that was never set' {
        # 'conformance' alone is verdict machinery at W=2: confidence Low, below the flag.
        # Nothing was claimed, so there is nothing to doubt.
        $record = Get-IssueRecord -Issue (New-TestIssue -Body 'Mentioned in the conformance suite.')

        Assert-False -Condition $record.Trust
        Assert-Equal -Expected 'Low' -Actual $record.Confidence.Trust
        Assert-False -Condition (@(Get-LowEvidence -Record $record) -contains 'Trust')
    }

    It 'drops the mark once an override has decided the axis' {
        # Confidence Override is what Set-OverriddenAxes writes in place of the heuristic's
        # for any axis the caller corrected, and the run reads LowEvidence after it.
        $record = Get-IssueRecord -Issue (New-TestIssue -Body 'The retry count is dropped silently.')
        $record.Confidence.Trust = 'Override'

        Assert-False -Condition (@(Get-LowEvidence -Record $record) -contains 'Trust')
    }
}

Describe 'VerifyNeeds on thin evidence' {
    It 'marks a call read from the body' {
        # The title says nothing about what proving it takes, so the body decides.
        $record = Get-IssueRecord -Issue (New-TestIssue -Title 'Something is off' -Body 'Only a Run-IntegrationTests run shows it.')

        Assert-True -Condition $record.VerifyFromBody
        Assert-Equal -Expected 'Hardware' -Actual $record.VerifyNeeds
        Assert-Equal -Expected 'Low' -Actual $record.Confidence.VerifyNeeds
        Assert-Contains -Item 'VerifyNeeds' -Collection @(Get-LowEvidence -Record $record)
    }

    It 'does not mark a call read from the title' {
        $record = Get-IssueRecord -Issue (New-TestIssue -Title 'The unit test misses a case' -Body 'Only a Run-IntegrationTests run shows it.')

        Assert-False -Condition $record.VerifyFromBody
        Assert-Equal -Expected 'CI' -Actual $record.VerifyNeeds
        Assert-False -Condition (@(Get-LowEvidence -Record $record) -contains 'VerifyNeeds')
    }

    It 'does not mark a body that said nothing either' {
        # No call was made. The row already heads Needs a human call as one with no signal,
        # and listing it again as thin evidence would say less than that does.
        $record = Get-IssueRecord -Issue (New-TestIssue -Title 'Something is off' -Body 'Nobody knows why.')

        Assert-True -Condition $record.VerifyFromBody
        Assert-Equal -Expected 'Unknown' -Actual $record.VerifyNeeds
        Assert-False -Condition (@(Get-LowEvidence -Record $record) -contains 'VerifyNeeds')
    }

    It 'drops the mark once an override has decided the axis' {
        # The run clears VerifyFromBody for an overridden VerifyNeeds before reading
        # LowEvidence, so the marker goes with the value it described.
        $record = Get-IssueRecord -Issue (New-TestIssue -Title 'Something is off' -Body 'Only a Run-IntegrationTests run shows it.')
        $record.VerifyFromBody = $false

        Assert-False -Condition (@(Get-LowEvidence -Record $record) -contains 'VerifyNeeds')
    }
}

Describe 'Both axes at once' {
    It 'lists the marked axes in the order of the table' {
        # The report walks the table for its thin-evidence lines, so the order here is the
        # order an operator reads them in.
        $record = Get-IssueRecord -Issue (New-TestIssue -Title 'Something is off' -Body 'Only a Run-IntegrationTests run shows it, and it fails silently.')

        Assert-ArrayEqual -Expected @('Trust', 'VerifyNeeds') -Actual @(Get-LowEvidence -Record $record)
    }
}
