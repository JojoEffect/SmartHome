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

Describe 'Get-IntegrationTestMarkerName' {
    # The expected half of the [ITEST] name. Issue #20: it used to be the catalog key,
    # compared against a TestName const spelled out again in each project's Program.cs,
    # so a rename in one place was reported as a device-level WRONG-TEST verdict 90
    # seconds into a hardware run. Both ends now read one <AssemblyName>.

    function New-ProjectFixture {
        param([string]$TestName, [string]$AssemblyName)

        $root = New-TestDirectory -Name "marker-$TestName"
        $projectPath = Join-Path $root "src\integrationTests\$TestName\$TestName.nfproj"

        Set-TestFileContent -Path $projectPath -Content @(
            '<Project>'
            '  <PropertyGroup>'
            "    <RootNamespace>SmartHome.IntegrationTests.$TestName</RootNamespace>"
            "    <AssemblyName>$AssemblyName</AssemblyName>"
            '  </PropertyGroup>'
            '</Project>'
        )

        return $root
    }

    It 'reads the assembly name off the project, not the folder it is in' {
        # The whole point: the marker name is whatever the built assembly will call
        # itself, which since the SmartHome.* rename is not the project's own file name.
        $root = New-ProjectFixture -TestName 'WifiCheck' -AssemblyName 'SmartHome.IntegrationTests.WifiCheck'

        Assert-Equal -Expected 'SmartHome.IntegrationTests.WifiCheck' `
                     -Actual (Get-IntegrationTestMarkerName -TestName 'WifiCheck' -RepoRoot $root)
    }

    It 'follows the project when its assembly name is nothing like the folder' {
        # A rename that used to need a matching edit in Program.cs and produced a
        # WRONG-TEST when it did not get one. Here it simply changes the answer.
        $root = New-ProjectFixture -TestName 'WifiCheck' -AssemblyName 'Something.Else.Entirely'

        Assert-Equal -Expected 'Something.Else.Entirely' `
                     -Actual (Get-IntegrationTestMarkerName -TestName 'WifiCheck' -RepoRoot $root)
    }

    It 'throws naming the path when the test has no project' {
        $root = New-TestDirectory -Name 'marker-empty'

        $message = Assert-Throws -Body { Get-IntegrationTestMarkerName -TestName 'NoSuchCheck' -RepoRoot $root }
        Assert-Match -Pattern 'NoSuchCheck' -Actual $message
        Assert-Match -Pattern 'nfproj' -Actual $message -Because 'the message has to say which file was looked for'
    }

    It 'resolves for every device-decided entry in the shipped catalog' {
        # Against this checkout, not a fixture. The pre-flight does exactly this before
        # the first flash, so a missing or unreadable project is a desk-speed failure.
        #
        # Asserted against what each project actually declares, not against
        # "SmartHome.IntegrationTests.<key>". Pinning that spelling here would contradict
        # the case above -- the function's whole contract is that the project decides --
        # and would turn a deliberate assembly rename into a red desk suite pointing at
        # the resolution logic instead of at the rename.
        $repoRoot = Split-Path -Parent $scriptsDir

        foreach ($testName in $testCatalog.Keys) {
            if ($testCatalog[$testName].Contains('Verdict')) {
                continue
            }

            $declared = Get-NfProjectAssemblyName -ProjectPath (Join-Path $repoRoot (Get-TestProjectPath -TestName $testName))

            Assert-Equal -Expected $declared `
                         -Actual (Get-IntegrationTestMarkerName -TestName $testName -RepoRoot $repoRoot) `
                         -Because "$testName's marker name comes from its own project"
        }
    }

    It 'keeps every shipped test on the SmartHome.IntegrationTests.<name> convention' {
        # A separate claim from the one above, on purpose: that one is about the function,
        # this one is about the checkout. Failing it means an assembly was renamed, which
        # is legitimate but deliberate -- so the message should say "convention", not
        # "marker name".
        $repoRoot = Split-Path -Parent $scriptsDir

        foreach ($testName in $testCatalog.Keys) {
            Assert-Equal -Expected "SmartHome.IntegrationTests.$testName" `
                         -Actual (Get-NfProjectAssemblyName -ProjectPath (Join-Path $repoRoot (Get-TestProjectPath -TestName $testName))) `
                         -Because "$testName's <AssemblyName> follows the repository's naming rule"
        }
    }
}

Describe 'Get-DeviceMarkerVerdict' {
    $expected = 'SmartHome.IntegrationTests.WifiCheck'

    It 'reads PASS and the detail after the colon' {
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @(
            'Program started'
            "[ITEST] $expected PASS: connected to the configured WiFi network"
        ) -ExpectedName $expected -CaptureSeconds 75

        Assert-Equal -Expected 'PASS' -Actual $verdict.Outcome
        Assert-Equal -Expected 'connected to the configured WiFi network' -Actual $verdict.Detail
    }

    It 'reads FAIL and its reason' {
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @("[ITEST] $expected FAIL: no network") `
                                           -ExpectedName $expected -CaptureSeconds 75

        Assert-Equal -Expected 'FAIL' -Actual $verdict.Outcome
        Assert-Equal -Expected 'no network' -Actual $verdict.Detail
    }

    It 'falls back to the whole line when a PASS carries no detail' {
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @("[ITEST] $expected PASS") `
                                           -ExpectedName $expected -CaptureSeconds 75

        Assert-Equal -Expected 'PASS' -Actual $verdict.Outcome
        Assert-Equal -Expected "[ITEST] $expected PASS" -Actual $verdict.Detail
    }

    It 'takes the first marker, so a later one cannot overturn the verdict' {
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @(
            "[ITEST] $expected FAIL: first"
            "[ITEST] $expected PASS: second"
        ) -ExpectedName $expected -CaptureSeconds 75

        Assert-Equal -Expected 'FAIL' -Actual $verdict.Outcome
        Assert-Equal -Expected 'first' -Actual $verdict.Detail
    }

    It 'reports NO-RESULT with the window when nothing marked' {
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @('Program started', 'still booting') `
                                           -ExpectedName $expected -CaptureSeconds 45

        Assert-Equal -Expected 'NO-RESULT' -Actual $verdict.Outcome
        Assert-Match -Pattern '45s' -Actual $verdict.Detail
    }

    It 'reports NO-RESULT for a capture that produced nothing at all' {
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @() -ExpectedName $expected -CaptureSeconds 45

        Assert-Equal -Expected 'NO-RESULT' -Actual $verdict.Outcome
    }

    It 'reports WRONG-TEST naming both sides when another test is on the device' {
        # The one outcome a healthy run never produces, which is why it is worth having a
        # case: the only way to reach it now is a flash that did not take.
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @('[ITEST] SmartHome.IntegrationTests.MqttCheck PASS: round-trip') `
                                           -ExpectedName $expected -CaptureSeconds 75

        Assert-Equal -Expected 'WRONG-TEST' -Actual $verdict.Outcome
        Assert-Match -Pattern 'MqttCheck' -Actual $verdict.Detail
        Assert-Match -Pattern ([regex]::Escape($expected)) -Actual $verdict.Detail -Because 'the message has to say what was expected too'
    }

    It 'does not accept a name the expected one is merely a prefix of' {
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @("[ITEST] ${expected}2 PASS: nearly") `
                                           -ExpectedName $expected -CaptureSeconds 75

        Assert-Equal -Expected 'WRONG-TEST' -Actual $verdict.Outcome
    }

    It 'finds a marker that is not at the start of its line' {
        # The capture is a raw debug stream, so a marker can land after whatever the
        # device wrote before it without a newline in between. Every case above already
        # covers the dotted name; this one covers the leading text, which is the only
        # thing here the anchorless pattern is doing.
        $verdict = Get-DeviceMarkerVerdict -CapturedLines @("noise [ITEST] $expected PASS: ok") `
                                           -ExpectedName $expected -CaptureSeconds 75

        Assert-Equal -Expected 'PASS' -Actual $verdict.Outcome
        Assert-Equal -Expected 'ok' -Actual $verdict.Detail
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

Describe 'The subscriber-log waits' {
    # Wait-Heartbeat, Wait-ForEcho and Wait-ForAnnounceWitnessed are one polling helper --
    # Wait-ForSubscriberLogLine -- plus a predicate each, so one stub carries all of them
    # and Get-SubscriberLogLineCount too. All of them read the long-running homie/# log
    # through Get-SmartHomeDevEnvPath; stubbing it here -- in this file's scope, which is
    # where the subject was dot-sourced -- points them at a fixture instead of the real
    # dev environment.
    $script:subscriberLog = $null

    function Get-SmartHomeDevEnvPath {
        param([string]$Port, [string]$Kind)
        return $script:subscriberLog
    }

    function Set-SubscriberLog {
        # -Directory so a case can put the log somewhere awkward on purpose; the default
        # is an ordinary name.
        param(
            [string[]]$Lines,
            [string]$Directory = 'subscriber-log'
        )
        $script:subscriberLog = Join-Path (New-TestDirectory -Name $Directory) 'homie.log'
        Set-TestFileContent -Path $script:subscriberLog -Content $Lines
    }

    function Add-SubscriberLogLine {
        # The device answering mid-wait. -LiteralPath so this works for the bracketed
        # fixture too.
        param([string]$Line)
        Add-Content -LiteralPath $script:subscriberLog -Value $Line -Encoding UTF8
    }

    It 'Get-SubscriberLogLineCount reads a missing log as 0, not as an error' {
        # -NoBroker skips the long-running subscriber, and a watermark of 0 then means
        # "read the whole file", which is the right answer when there is no file.
        $script:subscriberLog = Join-Path (New-TestDirectory -Name 'no-log') 'absent.log'

        Assert-Equal -Expected 0 -Actual (Get-SubscriberLogLineCount -Port '1883')
    }

    It 'Get-SubscriberLogLineCount counts lines, so the reader can -Skip it' {
        Set-SubscriberLog -Lines @('a', 'b', 'c')

        Assert-Equal -Expected 3 -Actual (Get-SubscriberLogLineCount -Port '1883')
    }

    It 'witnesses this boot announcing' {
        Set-SubscriberLog -Lines @(
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
        Set-SubscriberLog -Lines @(
            'homie/d/$state 0 init'
            'homie/d/$state 0 ready'
        )

        Assert-False -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 1 -Watermark 2)
    }

    It 'accepts a retained init as well as a live one' {
        Set-SubscriberLog -Lines @('homie/d/$state 1 init')

        Assert-True -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 2 -Watermark 0)
    }

    It 'is not satisfied by init on another topic or another device' {
        # Line-prefix match, not -like "*init*": a payload of 'init' on some other topic,
        # or a device id that merely starts with this one, must not satisfy it.
        Set-SubscriberLog -Lines @(
            'homie/d/n/mode 0 init'
            'homie/device-two/$state 0 init'
            'homie/d/$state 0 ready'
        )

        Assert-False -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 1 -Watermark 0)
    }

    It 'is not satisfied by a device id this one is a prefix of' {
        Set-SubscriberLog -Lines @('homie/d-two/$state 0 init')

        Assert-False -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 1 -Watermark 0)
    }

    It 'returns false at the deadline rather than throwing on a missing log' {
        $script:subscriberLog = Join-Path (New-TestDirectory -Name 'no-log-2') 'absent.log'

        Assert-False -Condition (Wait-ForAnnounceWitnessed -Port '1883' -DeviceId 'd' -TimeoutSeconds 1 -Watermark 0)
    }

    # --- Wait-ForSubscriberLogLine: the polling the three waits share -----------------

    It 'Wait-ForSubscriberLogLine returns the matching line itself, not a verdict' {
        # Wait-Heartbeat needs the line, not a bool: the counter it compares across the
        # outage is parsed out of the payload.
        Set-SubscriberLog -Lines @('a 0 one', 'b 0 two')

        Assert-Equal -Expected 'b 0 two' `
                     -Actual (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 2 -Predicate { $_ -like 'b *' })
    }

    It 'Wait-ForSubscriberLogLine returns the first match, not the last' {
        Set-SubscriberLog -Lines @('a 0 first', 'a 0 second')

        Assert-Equal -Expected 'a 0 first' `
                     -Actual (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 2 -Predicate { $_ -like 'a *' })
    }

    It 'Wait-ForSubscriberLogLine returns $null at the deadline rather than throwing on a missing log' {
        $script:subscriberLog = Join-Path (New-TestDirectory -Name 'no-log-3') 'absent.log'

        Assert-Null -Value (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 1 -Predicate { $true })
    }

    It 'Wait-ForSubscriberLogLine skips past the watermark' {
        Set-SubscriberLog -Lines @('a 0 before', 'b 0 after')

        Assert-Null -Value (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 1 -Skip 1 -Predicate { $_ -like 'a *' })
        Assert-Equal -Expected 'b 0 after' `
                     -Actual (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 2 -Skip 1 -Predicate { $_ -like 'b *' })
    }

    It 'Wait-ForSubscriberLogLine reads the log by literal path' {
        # Issue #71's defect class, and the reason this helper is on -LiteralPath: two of
        # the three waits were on -Path, where a '[' anywhere in the temp directory makes
        # the log a wildcard pattern matching nothing -- a device that answered, reported
        # as one that never did.
        Set-SubscriberLog -Directory 'bracket[1]dir' -Lines @('a 0 one')

        Assert-Equal -Expected 'a 0 one' `
                     -Actual (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 2 -Predicate { $_ -like 'a *' })
    }

    It 'Wait-ForSubscriberLogLine runs -BeforeRead on every pass, ahead of the read' {
        # Both halves of that in one case. The block writes the match on its *second*
        # call, so a helper that read before running it would need a third pass to see
        # the line -- the count separates the two orderings.
        Set-SubscriberLog -Lines @('noise 0 x')
        $script:beforeReadCalls = 0

        $hit = Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 10 -PollMilliseconds 50 `
            -Predicate { $_ -like 'answer *' } `
            -BeforeRead {
                $script:beforeReadCalls++
                if ($script:beforeReadCalls -eq 2) { Add-SubscriberLogLine -Line 'answer 0 here' }
            }

        Assert-Equal -Expected 'answer 0 here' -Actual $hit
        Assert-Equal -Expected 2 -Actual $script:beforeReadCalls -Because 'the read must come after the block, on the same pass'
    }

    It 'Wait-ForSubscriberLogLine polls at -PollMilliseconds' {
        # Asserted as passes-within-a-window rather than as elapsed time: a sleep longer
        # than the whole timeout leaves exactly one pass, whatever the machine is doing.
        Set-SubscriberLog -Lines @('noise 0 x')
        $script:pollCalls = 0

        Assert-Null -Value (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 1 -PollMilliseconds 1500 `
            -Predicate { $false } -BeforeRead { $script:pollCalls++ })
        Assert-Equal -Expected 1 -Actual $script:pollCalls
    }

    It 'Wait-ForSubscriberLogLine reads a predicate''s variables from the caller' {
        # How the three waits pass $Topic, $Payload and $DeviceId: the block is written
        # in the wait and evaluated inside the helper, so it resolves them up the dynamic
        # scope chain. This case is that chain, one function deeper than the shipped ones.
        Set-SubscriberLog -Lines @('a 0 one', 'b 0 two')

        function Invoke-WithLocal {
            param([string]$Wanted)
            return (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 2 -Predicate { $_ -like "$Wanted *" })
        }

        Assert-Equal -Expected 'b 0 two' -Actual (Invoke-WithLocal -Wanted 'b')
    }

    It 'Wait-ForSubscriberLogLine also takes a predicate that carries its own closure' {
        # The escape hatch for a block built somewhere the dynamic chain does not reach.
        # It is not what the shipped call sites do, and deliberately: .GetNewClosure()
        # binds the block into a new module scope with its own function table, and a
        # -BeforeRead closed that way cannot resolve Publish-HomieCommand at all.
        Set-SubscriberLog -Lines @('a 0 one', 'b 0 two')

        $predicate = & {
            $wanted = 'b'
            return { $_ -like "$wanted *" }.GetNewClosure()
        }

        Assert-Equal -Expected 'b 0 two' `
                     -Actual (Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 2 -Predicate $predicate)
    }

    # --- Wait-Heartbeat ---------------------------------------------------------------

    It 'Wait-Heartbeat returns the line and the counter that separates a reconnect from a restart' {
        Set-SubscriberLog -Lines @(
            'homie/other/heartbeat 0 heartbeat 99'
            'homie/mqtt-reconnect-check/heartbeat 0 heartbeat 12'
        )

        $hit = Wait-Heartbeat -Topic 'homie/mqtt-reconnect-check/heartbeat' -TimeoutSeconds 2 -Port '1883'

        Assert-Equal -Expected 'homie/mqtt-reconnect-check/heartbeat 0 heartbeat 12' -Actual $hit.Line
        Assert-Equal -Expected 12 -Actual $hit.Counter
        Assert-True -Condition ($hit.Counter -is [int]) -Because 'Invoke-BrokerOutageCheck compares it with -le'
    }

    It 'Wait-Heartbeat reports a null counter rather than failing on a payload without one' {
        # The counter is optional to the match: a heartbeat that arrived is evidence the
        # device published, and Invoke-BrokerOutageCheck null-guards before comparing.
        Set-SubscriberLog -Lines @('homie/x/heartbeat 0 alive')

        $hit = Wait-Heartbeat -Topic 'homie/x/heartbeat' -TimeoutSeconds 2 -Port '1883'

        Assert-Equal -Expected 'homie/x/heartbeat 0 alive' -Actual $hit.Line
        Assert-Null -Value $hit.Counter
    }

    It 'Wait-Heartbeat returns $null when the topic never appears' {
        Set-SubscriberLog -Lines @('homie/other/heartbeat 0 heartbeat 1')

        Assert-Null -Value (Wait-Heartbeat -Topic 'homie/x/heartbeat' -TimeoutSeconds 1 -Port '1883')
    }

    # --- Wait-ForEcho -----------------------------------------------------------------

    It 'Wait-ForEcho republishes the command every round until the echo comes back' {
        # The republish is the whole point of this wait: the device's publish loop
        # resumes before its subscriptions are replayed, so a single QoS-0 command into
        # that window is dropped and a healthy device would be reported FAIL.
        Set-SubscriberLog -Lines @('homie/x/heartbeat 0 heartbeat 1')
        $script:published = @()

        function Publish-HomieCommand {
            param([string]$Port, [string]$Topic, [string]$Payload)
            $script:published += "$Topic=$Payload"
            if ($script:published.Count -eq 3) { Add-SubscriberLogLine -Line "homie/x/echo 0 $Payload" }
        }

        Assert-True -Condition (Wait-ForEcho -Topic 'homie/x/echo' -Payload 'echo-7' -TimeoutSeconds 20 `
                                             -Port '1883' -CommandTopic 'homie/x/echo/set')
        Assert-Equal -Expected 3 -Actual $script:published.Count -Because 'it must keep re-sending, not send once and wait'
        Assert-Equal -Expected 'homie/x/echo/set=echo-7' -Actual $script:published[0]
    }

    It 'Wait-ForEcho returns false when the echo never comes, having published at least once' {
        Set-SubscriberLog -Lines @('homie/x/heartbeat 0 heartbeat 1')
        $script:published = @()

        function Publish-HomieCommand {
            param([string]$Port, [string]$Topic, [string]$Payload)
            $script:published += "$Topic=$Payload"
        }

        Assert-False -Condition (Wait-ForEcho -Topic 'homie/x/echo' -Payload 'echo-7' -TimeoutSeconds 1 `
                                              -Port '1883' -CommandTopic 'homie/x/echo/set')
        Assert-True -Condition ($script:published.Count -ge 1)
    }

    It 'Wait-ForEcho requires the topic and the payload, not either' {
        # A different device echoing the same nonce, and this device echoing a different
        # one, both have to fail: the pair is what proves *this* subscription replayed.
        Set-SubscriberLog -Lines @(
            'homie/other/echo 0 echo-7'
            'homie/x/echo 0 echo-6'
        )
        function Publish-HomieCommand { param([string]$Port, [string]$Topic, [string]$Payload) }

        Assert-False -Condition (Wait-ForEcho -Topic 'homie/x/echo' -Payload 'echo-7' -TimeoutSeconds 1 `
                                              -Port '1883' -CommandTopic 'homie/x/echo/set')
    }
}

Describe 'Get-AttributeFailure' {
    # The conformance check's attribute assertion. It was a nested function closing over
    # Measure-HomieConformance's $snapshot until #84, so none of this could be asserted
    # without a broker, a device and a 90s suite run -- in the verdict function with two
    # prior defects that passed while lying (#34, #36).
    #
    # Snapshots are built with the real ConvertTo-HomieSnapshot rather than by hand, so a
    # change to the entry shape breaks these cases instead of leaving them asserting
    # against a shape the capture no longer produces.

    It 'reports nothing for a retained attribute carrying the expected payload' {
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$homie 1 4')

        Assert-ArrayEqual -Expected @() -Actual @(Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$homie' -Expected '4')
    }

    It 'reports a topic the store never held' {
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$name 1 Office')

        Assert-ArrayEqual -Expected @('missing: homie/d/$homie') `
                          -Actual @(Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$homie' -Expected '4')
    }

    It 'asserts nothing further about a missing topic' {
        # The early return, and it is a crash guard rather than a tidier of messages:
        # `Set-StrictMode -Version Latest` is on, so $Snapshot[$absent].Retained throws
        # PropertyNotFoundException rather than reading as $null. Folding the return away
        # to shorten the function would take a device missing one attribute and turn it
        # into a conformance run that dies with no verdict at all.
        $failures = @(Get-AttributeFailure -Snapshot (ConvertTo-HomieSnapshot -Lines @()) -Topic 'homie/d/$homie' -Expected '4')

        Assert-ArrayEqual -Expected @('missing: homie/d/$homie') -Actual $failures
    }

    It 'reports an attribute the broker did not replay as retained' {
        # Homie requires every attribute retained. The flag is only set on a replay from
        # the store, so a live-only delivery is exactly what a non-retained attribute
        # looks like here.
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$homie 0 4')

        Assert-ArrayEqual -Expected @('not retained: homie/d/$homie') `
                          -Actual @(Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$homie' -Expected '4')
    }

    It 'reports the payload it saw and the one it wanted' {
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$homie 1 3')

        Assert-ArrayEqual -Expected @("homie/d/`$homie is '3', expected '4'") `
                          -Actual @(Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$homie' -Expected '4')
    }

    It 'reports both faults of one attribute rather than the first' {
        # One run reports everything that is wrong: an attribute can be non-retained and
        # carry the wrong value, and stopping at the flag would hide the value until the
        # next 90s hardware run.
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$homie 0 3')

        Assert-ArrayEqual -Expected @('not retained: homie/d/$homie', "homie/d/`$homie is '3', expected '4'") `
                          -Actual @(Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$homie' -Expected '4')
    }

    It 'skips the payload comparison for -AnyValue' {
        # $name and $type are the device's to choose, so only presence and the retain
        # flag are the convention's business.
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$name 1 whatever the device likes')

        Assert-ArrayEqual -Expected @() -Actual @(Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$name' -AnyValue)
    }

    It 'still asserts the retain flag for -AnyValue' {
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$name 0 Office')

        Assert-ArrayEqual -Expected @('not retained: homie/d/$name') `
                          -Actual @(Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$name' -AnyValue)
    }

    It 'still reports a missing topic for -AnyValue' {
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$homie 1 4')

        Assert-ArrayEqual -Expected @('missing: homie/d/$name') `
                          -Actual @(Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$name' -AnyValue)
    }

    It 'reads the snapshot it was passed, not one the caller happens to have in scope' {
        # The case #84 is about. Until then the function was nested inside
        # Measure-HomieConformance and closed over a $snapshot the /set round-trip
        # reassigns further down that same scope: every call happened before the
        # reassignment, so the assertions were right by position rather than by
        # construction.
        #
        # The local $snapshot below is the trap, and it deliberately holds the topic that
        # would make this case pass. PowerShell variable names are case-insensitive, so a
        # $snapshot written inside the function binds to the -Snapshot parameter and this
        # case cannot be fooled that way -- but a parameter renamed while the body still
        # says $Snapshot resolves to a caller's variable instead, silently, and that is
        # what the trap catches. Checked by deleting the parameter outright: this is the
        # case that then fails.
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$homie 1 4')
        $other = ConvertTo-HomieSnapshot -Lines @('homie/d/$name 1 Office')

        Assert-ArrayEqual -Expected @('missing: homie/d/$homie') `
                          -Actual @(Get-AttributeFailure -Snapshot $other -Topic 'homie/d/$homie' -Expected '4')
    }

    It 'appends nothing to a caller collecting a clean attribute with +=' {
        # How every call site uses it. An empty array unrolls to nothing, so a clean
        # attribute must not grow the failure list -- a single $null slipping in would
        # be counted as a conformance failure with no message.
        $snapshot = ConvertTo-HomieSnapshot -Lines @('homie/d/$homie 1 4')
        $collected = @()
        $collected += Get-AttributeFailure -Snapshot $snapshot -Topic 'homie/d/$homie' -Expected '4'

        Assert-Equal -Expected 0 -Actual $collected.Count
    }
}
