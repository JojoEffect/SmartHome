# scripts\Run-IntegrationTests.ps1 -- the host-side half of the integration suite.
#
# This is the half that decides every verdict the suite reports, and the half with no
# coverage at all until issue #74. Dot-sourcing the script defines its functions and its
# catalog and runs nothing (see the guard in that file), which is what lets these cases
# exercise the real shipped source instead of a copy lifted out of it with the AST.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsDir = Split-Path -Parent $PSScriptRoot
$subject = Join-Path $scriptsDir 'Run-IntegrationTests.ps1'

. $subject

Describe 'The dot-source guard' {
    It 'defines the functions and the catalog without reading the environment' {
        # In a child process, because the interesting claim is about what a fresh
        # dot-source does -- and this file has already done one.
        $probe = Join-Path (New-TestDirectory -Name 'guard') 'probe.ps1'
        Set-TestFileContent -Path $probe -Content @(
            'Set-StrictMode -Version Latest'
            '$ErrorActionPreference = ''Stop'''
            ". '$subject'"
            '$functions = @(Get-Command -CommandType Function | Where-Object { $_.ScriptBlock.File -eq ' + "'$subject'" + ' }).Count'
            'Write-Output ("functions={0}" -f $functions)'
            'Write-Output ("catalog={0}" -f @($testCatalog.Keys).Count)'
            'Write-Output ("repoRootDefined={0}" -f [bool](Get-Variable -Name repoRoot -ErrorAction SilentlyContinue))'
            'Write-Output ("logDirectory=[{0}]" -f $LogDirectory)'
        )

        $host_ = (Get-Process -Id $PID).Path
        $output = & $host_ -NoProfile -ExecutionPolicy Bypass -File $probe
        Assert-Equal -Expected 0 -Actual $LASTEXITCODE -Because 'a dot-source must not fail'

        $report = @{}
        foreach ($line in $output) {
            $parts = $line -split '=', 2
            $report[$parts[0]] = $parts[1]
        }

        Assert-True -Condition ([int]$report['functions'] -ge 25) -Because "the verdict functions must be reachable, got $($report['functions'])"
        Assert-Equal -Expected '5' -Actual $report['catalog']

        # The two that prove nothing below the guard ran: $repoRoot is assigned there, and
        # $LogDirectory is filled in and created there.
        Assert-Equal -Expected 'False' -Actual $report['repoRootDefined']
        Assert-Equal -Expected '[]' -Actual $report['logDirectory']
    }

    It 'has nothing but declarations above it' {
        # The guard only holds while everything above it is a declaration. This is the
        # case that stops an Import-SmartHomeLocalEnv, a New-Item or a Test-Path drifting
        # back to the top of the file, where a dot-source would run it -- which is how
        # this script came to be un-dot-sourceable in the first place.
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

        # The only two non-declarations allowed above the guard, both of which a
        # dot-sourcing caller wants anyway: the strict-mode setting and the Common.ps1
        # dot-source that brings in the helpers these functions are built on.
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

Describe 'Get-CatalogValidationError' {
    $deviceDecided = @('CaptureSeconds')
    $required = @{
        'Invoke-Something' = @('DeviceId', 'SettleSeconds')
    }

    It 'accepts the catalog this script actually ships' {
        # The run's own pre-flight, run at a desk: every entry, not just the default set.
        Assert-Null -Value (Get-CatalogValidationError -Catalog $testCatalog `
                                                       -Tests @($testCatalog.Keys) `
                                                       -DeviceDecidedKeys $deviceDecidedKeys `
                                                       -RequiredCatalogKeys $requiredCatalogKeys)
    }

    It 'names an unknown test and lists the known ones' {
        $error_ = Get-CatalogValidationError -Catalog $testCatalog `
                                             -Tests @('WifiCheck', 'NoSuchCheck') `
                                             -DeviceDecidedKeys $deviceDecidedKeys `
                                             -RequiredCatalogKeys $requiredCatalogKeys

        Assert-Match -Pattern 'NoSuchCheck' -Actual $error_
        Assert-Match -Pattern 'WifiCheck' -Actual $error_ -Because 'the message has to say what the options were'
    }

    It 'rejects an entry that declares no OwnsBroker' {
        # Required of every entry, device-decided ones included: it is read as
        # $settings.OwnsBroker on every path, and leaving it out would default to
        # whichever answer is quietly wrong for the next check that owns the broker.
        $catalog = [ordered]@{ 'Thing' = @{ CaptureSeconds = 30 } }

        Assert-Match -Pattern 'OwnsBroker' `
                     -Actual (Get-CatalogValidationError -Catalog $catalog -Tests @('Thing') -DeviceDecidedKeys $deviceDecided -RequiredCatalogKeys $required)
    }

    It 'rejects a Verdict function this script does not know' {
        $catalog = [ordered]@{ 'Thing' = @{ OwnsBroker = $true; Verdict = 'Invoke-Whatever' } }
        $error_ = Get-CatalogValidationError -Catalog $catalog -Tests @('Thing') -DeviceDecidedKeys $deviceDecided -RequiredCatalogKeys $required

        Assert-Match -Pattern 'Invoke-Whatever' -Actual $error_
        Assert-Match -Pattern 'Invoke-Something' -Actual $error_ -Because 'and say which names are known'
    }

    It 'rejects a host-decided entry missing a setting its verdict function needs' {
        $catalog = [ordered]@{ 'Thing' = @{ OwnsBroker = $true; Verdict = 'Invoke-Something'; DeviceId = 'x' } }

        Assert-Match -Pattern 'SettleSeconds' `
                     -Actual (Get-CatalogValidationError -Catalog $catalog -Tests @('Thing') -DeviceDecidedKeys $deviceDecided -RequiredCatalogKeys $required)
    }

    It 'rejects a device-decided entry with no CaptureSeconds' {
        # A forgotten CaptureSeconds would otherwise become a zero-length capture window
        # -- a test that reports FAIL because nothing was captured.
        $catalog = [ordered]@{ 'Thing' = @{ OwnsBroker = $false } }

        Assert-Match -Pattern 'CaptureSeconds' `
                     -Actual (Get-CatalogValidationError -Catalog $catalog -Tests @('Thing') -DeviceDecidedKeys $deviceDecided -RequiredCatalogKeys $required)
    }

    It 'checks only the tests it was asked about' {
        $catalog = [ordered]@{
            'Good' = @{ OwnsBroker = $false; CaptureSeconds = 30 }
            'Bad'  = @{ OwnsBroker = $false }
        }

        Assert-Null -Value (Get-CatalogValidationError -Catalog $catalog -Tests @('Good') -DeviceDecidedKeys $deviceDecided -RequiredCatalogKeys $required)
        Assert-NotNull -Value (Get-CatalogValidationError -Catalog $catalog -Tests @('Good', 'Bad') -DeviceDecidedKeys $deviceDecided -RequiredCatalogKeys $required)
    }
}

Describe 'The shipped catalog' {
    It 'names only Verdict functions this script defines' {
        # The run checks this too, but only after the catalog validation and only for the
        # tests being run -- and it can only run there, after the definitions. Here it
        # covers every entry, before anything is built or flashed.
        foreach ($testName in $testCatalog.Keys) {
            $entry = $testCatalog[$testName]
            if ($entry.Contains('Verdict')) {
                Assert-NotNull -Value (Get-Command -Name $entry.Verdict -CommandType Function -ErrorAction SilentlyContinue) `
                               -Because "$testName names Verdict $($entry.Verdict)"
            }
        }
    }

    It 'has a required-keys table whose every name is a function too' {
        # The table doubles as the list of names an entry may point at, so a name in it
        # that no longer exists would be accepted by validation and fail at the call.
        foreach ($verdict in $requiredCatalogKeys.Keys) {
            Assert-NotNull -Value (Get-Command -Name $verdict -CommandType Function -ErrorAction SilentlyContinue) -Because $verdict
        }
    }

    It 'gives every broker-owning entry a recovery budget' {
        foreach ($testName in $testCatalog.Keys) {
            $entry = $testCatalog[$testName]
            if ($entry.OwnsBroker) {
                Assert-True -Condition ($entry.Contains('RecoverySeconds') -and $entry.RecoverySeconds -gt 0) -Because $testName
            }
        }
    }
}

Describe 'The conformance lifecycle table' {
    $settings = @{ SettleSeconds = 90; RecoverySeconds = 90; CommandTimeoutSeconds = 30 }

    It 'drives ready -> alert -> ready -> sleeping -> ready with one refused transition' {
        # alert may only return to ready (or disconnect), so alert -> sleeping is the one
        # the convention's own state machine forbids. A device that advertises it as done
        # anyway is the defect that step exists to measure.
        Assert-Equal -Expected 5 -Actual $conformanceLifecycleSteps.Count

        $refused = @($conformanceLifecycleSteps | Where-Object { $_.Refused })
        Assert-Equal -Expected 1 -Actual $refused.Count
        Assert-Equal -Expected 'sleeping' -Actual $refused[0].Command
        Assert-Equal -Expected 'alert' -Actual $refused[0].Expect -Because 'a refused command must leave $state where it was'
    }

    It 'sizes the capture from the table, not from a second spelling of its length' {
        # The defect this replaced: the count was a literal 5 about 420 lines from the
        # table, so a sixth step left the capture one CommandTimeoutSeconds short and took
        # the device-side log away on exactly the slow run worth reading (issue #83).
        # Recomputing the formula would not catch that -- moving the count has to.
        $atFive = Get-ConformanceCaptureSeconds -Settings $settings -LifecycleStepCount 5
        $atSix = Get-ConformanceCaptureSeconds -Settings $settings -LifecycleStepCount 6

        Assert-Equal -Expected $settings.CommandTimeoutSeconds -Actual ($atSix - $atFive)
    }

    It 'defaults to the shipped table' {
        Assert-Equal -Expected (Get-ConformanceCaptureSeconds -Settings $settings -LifecycleStepCount $conformanceLifecycleSteps.Count) `
                     -Actual (Get-ConformanceCaptureSeconds -Settings $settings)
    }

    It 'stays longer than the phases it has to outlive' {
        # It is a ceiling, and a loose one on purpose: nothing waits this window out, and
        # a capture that ends early loses the evidence.
        Assert-True -Condition ((Get-ConformanceCaptureSeconds -Settings $settings) -gt ($settings.SettleSeconds + $settings.RecoverySeconds))
    }
}

Describe 'ConvertFrom-HomieCaptureLine' {
    It 'reads topic, retain flag and payload out of a -F "%t %r %p" line' {
        $parsed = ConvertFrom-HomieCaptureLine -Line 'homie/room-sensor-office/$state 1 ready'

        Assert-Equal -Expected 'homie/room-sensor-office/$state' -Actual $parsed.Topic
        Assert-True -Condition $parsed.Retained
        Assert-Equal -Expected 'ready' -Actual $parsed.Payload
    }

    It 'reads a live message as not retained' {
        # MQTT only sets the retain flag when replaying from the store: a live retained
        # publish arrives with the flag clear, which is why the conformance check reads
        # retained-ness from a fresh subscriber.
        Assert-False -Condition (ConvertFrom-HomieCaptureLine -Line 'homie/d/$state 0 init').Retained
    }

    It 'keeps a payload that contains spaces' {
        Assert-Equal -Expected '12,34,56 and more' -Actual (ConvertFrom-HomieCaptureLine -Line 'homie/d/n/colour 0 12,34,56 and more').Payload
    }

    It 'reads an empty payload as an empty string, not as a non-message' {
        # An empty retained publish is how a Homie attribute is cleared, so this line has
        # to parse rather than be dropped as noise.
        $parsed = ConvertFrom-HomieCaptureLine -Line 'homie/d/$state 1 '

        Assert-NotNull -Value $parsed
        Assert-Equal -Expected '' -Actual $parsed.Payload
    }

    It 'returns $null for a line that is not a message' {
        # mosquitto_sub's stderr shares the capture file, so its diagnostics come through
        # this function too.
        Assert-Null -Value (ConvertFrom-HomieCaptureLine -Line 'Error: Connection refused')
        Assert-Null -Value (ConvertFrom-HomieCaptureLine -Line '')
        Assert-Null -Value (ConvertFrom-HomieCaptureLine -Line 'homie/d/$state 2 ready') -Because 'the retain flag is 0 or 1'
    }

    It 'parses a line built from the format Common.ps1 actually asks for' {
        # The layout is chosen in Get-SmartHomeSubscriberArguments and read here. This is
        # the case that fails if one of them is changed alone.
        $format = @(Get-SmartHomeSubscriberArguments -Port '1883')[-1]
        $line = $format -replace '%t', 'homie/d/$state' -replace '%r', '1' -replace '%p', 'ready'

        $parsed = ConvertFrom-HomieCaptureLine -Line $line
        Assert-NotNull -Value $parsed
        Assert-Equal -Expected 'homie/d/$state' -Actual $parsed.Topic
        Assert-True -Condition $parsed.Retained
        Assert-Equal -Expected 'ready' -Actual $parsed.Payload
    }
}

Describe 'ConvertTo-HomieSnapshot' {
    It 'keeps the last value per topic' {
        $snapshot = ConvertTo-HomieSnapshot -Lines @(
            'homie/d/$state 1 init'
            'homie/d/$state 0 ready'
        )

        Assert-Equal -Expected 'ready' -Actual $snapshot['homie/d/$state'].Payload
    }

    It 'lets a repeat of the same payload add the retain flag it proved' {
        # A QoS-1 publish whose PUBACK is late is retransmitted with DupFlag set, so a
        # live duplicate can arrive after the broker's replayed copy. Overwriting on
        # payload equality threw away the retain flag replay had just proved, and the
        # conformance check reported "not retained" for three attributes that plainly
        # were.
        $snapshot = ConvertTo-HomieSnapshot -Lines @(
            'homie/d/$name 1 Office'
            'homie/d/$name 0 Office'
        )

        Assert-True -Condition $snapshot['homie/d/$name'].Retained
    }

    It 'lets a different payload replace both the value and the flag' {
        # The other half of that rule: a value delivered only live must not inherit the
        # retained-ness of the value it replaced.
        $snapshot = ConvertTo-HomieSnapshot -Lines @(
            'homie/d/n/p 1 on'
            'homie/d/n/p 0 off'
        )

        Assert-Equal -Expected 'off' -Actual $snapshot['homie/d/n/p'].Payload
        Assert-False -Condition $snapshot['homie/d/n/p'].Retained
    }

    It 'treats payloads differing only in case as different' {
        # -ceq, not -eq: a live 'TRUE' merging with a replayed 'true' would inherit a
        # retain flag it never earned.
        $snapshot = ConvertTo-HomieSnapshot -Lines @(
            'homie/d/n/p 1 true'
            'homie/d/n/p 0 TRUE'
        )

        Assert-Equal -Expected 'TRUE' -Actual $snapshot['homie/d/n/p'].Payload
        Assert-False -Condition $snapshot['homie/d/n/p'].Retained
    }

    It 'skips lines that are not messages' {
        $snapshot = ConvertTo-HomieSnapshot -Lines @(
            'Error: Connection refused'
            'homie/d/$state 1 ready'
        )

        Assert-Equal -Expected 1 -Actual $snapshot.Keys.Count
    }

    It 'returns an empty snapshot rather than $null for no lines' {
        Assert-Equal -Expected 0 -Actual (ConvertTo-HomieSnapshot -Lines @()).Keys.Count
    }
}

Describe 'Get-HomieLivePayloads' {
    $lines = @(
        'homie/d/n/p 1 idle'
        'homie/d/n/p 0 running'
        'homie/d/n/other 0 ignored'
        'homie/d/n/p 0 stopped'
    )

    It 'returns what went past the topic, in order' {
        # This is what tells a command that was refused apart from one never delivered:
        # both leave the retained store exactly as it was.
        Assert-ArrayEqual -Expected @('running', 'stopped') -Actual (Get-HomieLivePayloads -Lines $lines -Topic 'homie/d/n/p')
    }

    It 'drops the retained replay, which is from before the window opened' {
        Assert-Equal -Expected 0 -Actual @(Get-HomieLivePayloads -Lines $lines -Topic 'homie/d/n/p' |
            Where-Object { $_ -eq 'idle' }).Count -Because 'the replayed value says nothing about what happened inside the window'
    }

    It 'returns an array even when nothing matched' {
        # The leading comma in the function's return: a zero- or one-element result still
        # has to arrive as an array, or the caller's .Count throws under Set-StrictMode.
        $payloads = Get-HomieLivePayloads -Lines $lines -Topic 'homie/d/n/nothing'

        Assert-NotNull -Value $payloads
        Assert-Equal -Expected 0 -Actual $payloads.Count
    }

    It 'returns an array for a single match' {
        $payloads = Get-HomieLivePayloads -Lines $lines -Topic 'homie/d/n/other'

        Assert-Equal -Expected 1 -Actual $payloads.Count
        Assert-Equal -Expected 'ignored' -Actual $payloads[0]
    }
}

Describe 'The announce witness' {
    # Both functions read the long-running homie/# log through Get-SmartHomeDevEnvPath.
    # Stubbing it here -- in this file's scope, which is where the subject was
    # dot-sourced -- points them at a fixture instead of the real dev environment.
    $script:witnessLog = $null

    function Get-SmartHomeDevEnvPath {
        param([string]$Port, [string]$Kind)
        return $script:witnessLog
    }

    function Set-WitnessLog {
        param([string[]]$Lines)
        $script:witnessLog = Join-Path (New-TestDirectory -Name 'witness') 'homie.log'
        Set-TestFileContent -Path $script:witnessLog -Content $Lines
    }

    It 'Get-SubscriberLogLineCount reads a missing log as 0, not as an error' {
        # -NoBroker skips the long-running subscriber, and a watermark of 0 then means
        # "read the whole file", which is the right answer when there is no file.
        $script:witnessLog = Join-Path (New-TestDirectory -Name 'no-log') 'absent.log'

        Assert-Equal -Expected 0 -Actual (Get-SubscriberLogLineCount -Port '1883')
    }

    It 'Get-SubscriberLogLineCount counts lines, so the reader can -Skip it' {
        Set-WitnessLog -Lines @('a', 'b', 'c')

        Assert-Equal -Expected 3 -Actual (Get-SubscriberLogLineCount -Port '1883')
    }

    It 'witnesses this boot announcing' {
        Set-WitnessLog -Lines @(
            'homie/other/$state 0 ready'
            'homie/d/$state 0 init'
        )

        Assert-True -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 2 -Watermark 1)
    }

    It 'is not satisfied by a previous boot''s init before the watermark' {
        # The case the whole mechanism exists for. The watermark is read in the gap
        # between the flash's hard reset and the new image's first publish, so a line
        # before it cannot have come from the image that was just flashed -- and a
        # retained $state=ready from the previous instance cannot say anything about this
        # one either, which is what this replaced (issue #35).
        Set-WitnessLog -Lines @(
            'homie/d/$state 0 init'
            'homie/d/$state 0 ready'
        )

        Assert-False -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 1 -Watermark 2)
    }

    It 'accepts a retained init as well as a live one' {
        Set-WitnessLog -Lines @('homie/d/$state 1 init')

        Assert-True -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 2 -Watermark 0)
    }

    It 'is not satisfied by init on another topic or another device' {
        # Line-prefix match, not -like "*init*": a payload of 'init' on some other topic,
        # or a device id that merely starts with this one, must not satisfy it.
        Set-WitnessLog -Lines @(
            'homie/d/n/mode 0 init'
            'homie/device-two/$state 0 init'
            'homie/d/$state 0 ready'
        )

        Assert-False -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 1 -Watermark 0)
    }

    It 'is not satisfied by a device id this one is a prefix of' {
        Set-WitnessLog -Lines @('homie/d-two/$state 0 init')

        Assert-False -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 1 -Watermark 0)
    }

    It 'returns false at the deadline rather than throwing on a missing log' {
        $script:witnessLog = Join-Path (New-TestDirectory -Name 'no-log-2') 'absent.log'

        Assert-False -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 1 -Watermark 0)
    }
}
