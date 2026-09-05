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

Describe 'Test-DeviceConstant' {
    # The pre-flight's stale-constant warning. A compile-time constant in a device
    # project cannot be read from local.env.ps1, so the two drift, and the whole value of
    # this check is that it turns "the test failed on a healthy device" into a warning
    # naming both values. Every one of its three silent paths -- no file, no match, a
    # match that agrees -- looks identical from the console, which is why they are
    # separated here rather than covered by "it warned once".

    function New-ProgramFile {
        param(
            [string[]]$Lines,
            [string]$Directory = 'device-constant'
        )
        $path = Join-Path (New-TestDirectory -Name $Directory) 'Program.cs'
        Set-TestFileContent -Path $path -Content $Lines
        return $path
    }

    function Get-ConstantWarning {
        # 3>&1 turns the warnings into objects this can count and read.
        #
        # Every caller wraps this in @(), and has to: a return unrolls, so one warning
        # comes back as a bare string and none comes back as $null -- and .Count on
        # either is a PropertyNotFoundException under Set-StrictMode -Version Latest,
        # not a 1 and a 0. Wrapping HERE instead would be the ,@() shape issue #88 is
        # about, which member-enumerates the moment a caller pipes it.
        param([string]$ProgramPath, [string]$Pattern, [string]$Expected)

        return (Test-DeviceConstant -Label 'MqttCheck' -ProgramPath $ProgramPath `
                                    -Pattern $Pattern -Expected $Expected `
                                    -What 'the broker address' 3>&1 |
            ForEach-Object { $_.Message })
    }

    $brokerPattern = 'BrokerAddress\s*=\s*"([^"]+)"'

    It 'warns naming both values when the constant has drifted' {
        $program = New-ProgramFile -Lines @(
            'internal sealed class Program {'
            '    private const string BrokerAddress = "192.168.1.99";'
            '}'
        )

        $warnings = @(Get-ConstantWarning -ProgramPath $program -Pattern $brokerPattern -Expected '192.168.1.238')

        Assert-Equal -Expected 1 -Actual $warnings.Count
        # Both values in the message, not just "they differ": which of the two is stale
        # is the reader's call, and it cannot be made without seeing them.
        Assert-Match -Actual $warnings[0] -Pattern ([regex]::Escape('192.168.1.99'))
        Assert-Match -Actual $warnings[0] -Pattern ([regex]::Escape('192.168.1.238'))
        Assert-Match -Actual $warnings[0] -Pattern 'MqttCheck'
        Assert-Match -Actual $warnings[0] -Pattern 'the broker address'
    }

    It 'says nothing when the constant agrees' {
        $program = New-ProgramFile -Lines @('const string BrokerAddress = "192.168.1.238";')

        Assert-Equal -Expected 0 -Actual @(Get-ConstantWarning -ProgramPath $program `
            -Pattern $brokerPattern -Expected '192.168.1.238').Count
    }

    It 'compares the first capture group, not the whole match' {
        # The pattern deliberately matches more than the value -- the surrounding
        # assignment is what makes it unambiguous -- so a comparison against $0 would
        # never agree with anything and the check would warn on every healthy run.
        $program = New-ProgramFile -Lines @('const string BrokerAddress = "10.0.0.1";')

        Assert-Equal -Expected 0 -Actual @(Get-ConstantWarning -ProgramPath $program `
            -Pattern $brokerPattern -Expected '10.0.0.1').Count
    }

    It 'takes the first match when the file holds several' {
        $program = New-ProgramFile -Lines @(
            'const string BrokerAddress = "10.0.0.1";'
            'const string FallbackAddress = "10.0.0.2";'
            '// BrokerAddress = "10.0.0.3"'
        )

        Assert-Equal -Expected 0 -Actual @(Get-ConstantWarning -ProgramPath $program `
            -Pattern $brokerPattern -Expected '10.0.0.1').Count
    }

    It 'says nothing about a project that has no Program.cs' {
        # The path is handed in rather than derived, and a caller can name a test that
        # does not exist. Returning quietly is right; throwing here would abort a
        # pre-flight over a warning.
        $absent = Join-Path (New-TestDirectory -Name 'no-program') 'Program.cs'

        Assert-Equal -Expected 0 -Actual @(Get-ConstantWarning -ProgramPath $absent `
            -Pattern $brokerPattern -Expected '192.168.1.238').Count
    }

    It 'says nothing when the constant is not in the file at all' {
        # Distinct from the case above, and the more dangerous of the two: the file is
        # there and readable, so a renamed constant reads exactly like one that agrees.
        # Pinned so a future "warn when the pattern finds nothing" is a deliberate change.
        $program = New-ProgramFile -Lines @('const string SomethingElse = "192.168.1.99";')

        Assert-Equal -Expected 0 -Actual @(Get-ConstantWarning -ProgramPath $program `
            -Pattern $brokerPattern -Expected '192.168.1.238').Count
    }

    It 'is silent about a drifted constant under a bracketed path -- issue #80' {
        # Pinned as it behaves today, NOT endorsed. Both reads here are -Path, so a '['
        # anywhere in the checkout makes them wildcard patterns matching nothing:
        # Test-Path returns false and the function returns as if the project had no
        # Program.cs. The stale-broker warning then never fires on exactly the machine
        # whose path is unusual -- the #71 defect class, and two of the 24 sites #80
        # counts in this file.
        #
        # #80's sweep must invert this case rather than delete it: the same fixture, one
        # warning instead of none.
        $program = New-ProgramFile -Directory 'device [constant]' -Lines @(
            'const string BrokerAddress = "192.168.1.99";'
        )

        Assert-Equal -Expected 0 -Actual @(Get-ConstantWarning -ProgramPath $program `
            -Pattern $brokerPattern -Expected '192.168.1.238').Count

        # And the file really is there, so the silence above is the path handling and
        # nothing else.
        Assert-True -Condition (Test-Path -LiteralPath $program)
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

    It 'Wait-ForSubscriberLogLine discards whatever -BeforeRead writes' {
        # A PowerShell function returns everything written to its output stream, so a
        # block that emitted one string would prepend it to the result -- and
        # Wait-ForEcho's "$null -ne $hit" would read a matchless timeout as a PASS.
        Set-SubscriberLog -Lines @('noise 0 x')

        $hit = Wait-ForSubscriberLogLine -Port '1883' -TimeoutSeconds 1 -PollMilliseconds 1500 `
            -Predicate { $false } -BeforeRead { 'chatter from the block' }

        Assert-Null -Value $hit
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

Describe 'Invoke-BrokerOutageCheck' {
    # MqttReconnectCheck's verdict. Everything it decides is decided from the subscriber
    # log -- the baseline heartbeat, the counter across the outage, the echo -- and only
    # taking the broker away is not. So the three broker calls are the stubs and nothing
    # else is: Wait-Heartbeat, Wait-ForEcho and Wait-ForSubscriberLogLine all run for
    # real, against a fixture log, through the same Get-SmartHomeDevEnvPath stub the
    # waits' own cases use.
    #
    # The stubs stand in for what the real ones do to that log, not for their signatures
    # alone. Start-DevEnv.ps1 truncates the subscriber log on every start, and this check
    # rests on that: it is the whole reason a phase's heartbeats can only be ones
    # published after that phase's broker came up. So each stub truncates and refills,
    # and the fixture is written in those terms -- what the device publishes once the
    # opening cycle's broker is up, and what it publishes after each outage. A stub that
    # left the log alone would let a case pass on a heartbeat from before the outage it
    # claims to have survived.

    # Names of their own, not $script:subscriberLog / $script:published. Those belong to
    # the subscriber-log waits above, and sharing them would work only while both groups
    # reset first and run in file order -- a passing suite resting on an accident, which
    # is what a review pass caught in the group below this one.
    $script:outageLog = $null
    $script:outageEvents = @()
    $script:outagePublished = @()

    function Get-SmartHomeDevEnvPath {
        param([string]$Port, [string]$Kind)
        return $script:outageLog
    }

    function Set-OutageLog {
        param([string[]]$Lines)
        Set-TestFileContent -Path $script:outageLog -Content $Lines
    }

    function Reset-OutageFixture {
        # -Baseline is what the device has published once the opening cycle's broker is
        # up; -Recovery the same for each outage, one entry per Start-SuiteBroker, the
        # last repeating if there are more outages than entries. -NoEcho models a client
        # that reconnected without replaying its subscriptions.
        param(
            [string[]]$Baseline = @(),
            [string[][]]$Recovery = @(),
            [switch]$NoEcho
        )

        $script:outageLog = Join-Path (New-TestDirectory -Name 'outage-log') 'homie.log'
        $script:outageEvents = @()
        $script:outagePublished = @()
        $script:outageBaseline = $Baseline
        $script:outageRecovery = $Recovery
        $script:outageStarts = 0
        $script:outageEchoes = -not $NoEcho
        $script:outageEchoTopic = 'homie/mqtt-reconnect-check/echo'

        # Deliberately not empty: the case below that proves the opening cycle happened
        # needs something here the check must NOT read. Overwritten by that cycle.
        Set-OutageLog -Lines @('homie/mqtt-reconnect-check/heartbeat 0 heartbeat 400')
    }

    function Restart-SuiteBroker {
        param([string]$Port, [int]$SettleSeconds = 0)
        $script:outageEvents += 'restart'
        Set-OutageLog -Lines $script:outageBaseline
    }

    function Stop-SuiteBroker {
        param([string]$Port)
        $script:outageEvents += 'stop'
        Set-OutageLog -Lines @()
    }

    function Start-SuiteBroker {
        param([string]$Port)
        $script:outageEvents += 'start'
        $index = [math]::Min($script:outageStarts, $script:outageRecovery.Count - 1)
        # @() around the if, because an if hands back its branch's output stream: the
        # "device published nothing" entry is an empty array, which enumerates to nothing
        # and leaves $lines as $null rather than empty. Set-TestFileContent's -Content is
        # mandatory and allows an empty collection but not a null, so that reads as a
        # broken fixture rather than as the silent device it is meant to be.
        $lines = @(if ($index -lt 0) { @() } else { $script:outageRecovery[$index] })
        $script:outageStarts++
        Set-OutageLog -Lines $lines
    }

    function Publish-HomieCommand {
        param([string]$Port, [string]$Topic, [string]$Payload)
        $script:outagePublished += $Payload
        # The device echoing what it was sent, which is what MqttReconnectCheck's app
        # does. Only a replayed subscription can produce this line, so withholding it is
        # the "connected and deaf" case rather than a missing fixture.
        if ($script:outageEchoes) {
            Add-Content -LiteralPath $script:outageLog -Encoding UTF8 `
                        -Value ('{0} 0 {1}' -f $script:outageEchoTopic, $Payload)
        }
    }

    function New-OutageSettings {
        # Seconds are 1 where the shipped catalog says 90: every one of them is a
        # deadline this suite has to sit through whenever a case is a timeout.
        param([int[]]$OutageSeconds = @(0))

        return @{
            HeartbeatTopic        = 'homie/mqtt-reconnect-check/heartbeat'
            SettleSeconds         = 1
            OutageSeconds         = $OutageSeconds
            RecoverySeconds       = 1
            EchoCommandTopic      = 'homie/mqtt-reconnect-check/echo/set'
            EchoTopic             = 'homie/mqtt-reconnect-check/echo'
            CommandTimeoutSeconds = 1
        }
    }

    function Invoke-Outage {
        # 6> $null, because this function narrates every phase with Write-Host and the
        # runner does not swallow stream 6.
        param([hashtable]$Settings)
        return (Invoke-BrokerOutageCheck -Settings $Settings -Port '1883' -LogPath 'unused' 6> $null)
    }

    function New-Heartbeat {
        param($Counter)
        return @(('homie/mqtt-reconnect-check/heartbeat 0 heartbeat {0}' -f $Counter))
    }

    It 'PASSes when the counter climbed across the outage and the echo came back' {
        Reset-OutageFixture -Baseline (New-Heartbeat 4) -Recovery @(, (New-Heartbeat 9))

        $verdict = Invoke-Outage -Settings (New-OutageSettings)

        Assert-Equal -Expected 'PASS' -Actual $verdict.Outcome
        Assert-Match -Actual $verdict.Detail -Pattern 'stayed subscribed'
    }

    It 'cycles the broker before it reads a baseline' {
        # The baseline has to belong to the instance now on the device. Whatever was
        # flashed before it kept publishing on the same topic right through the build and
        # the flash, so a baseline read from the log as found can be a previous app's --
        # and a counter compared against that proves nothing.
        #
        # The fixture starts at 400 and the cycle replaces it with 2. Reading the log as
        # found would make this 400 -> 7 and therefore RESTARTED, so the verdict is the
        # assertion that the read happened after the cycle -- not merely that the cycle
        # was called.
        Reset-OutageFixture -Baseline (New-Heartbeat 2) -Recovery @(, (New-Heartbeat 7))

        $verdict = Invoke-Outage -Settings (New-OutageSettings)

        Assert-Equal -Expected 'restart' -Actual $script:outageEvents[0]
        Assert-Equal -Expected 'PASS' -Actual $verdict.Outcome
    }

    It 'reports RESTARTED when the counter went backwards' {
        # The distinction the whole check exists for: a device that recovered by rebooting
        # publishes again just as reliably as one that reconnected, and only the counter
        # tells them apart.
        Reset-OutageFixture -Baseline (New-Heartbeat 40) -Recovery @(, (New-Heartbeat 1))

        $verdict = Invoke-Outage -Settings (New-OutageSettings)

        Assert-Equal -Expected 'RESTARTED' -Actual $verdict.Outcome
        Assert-Match -Actual $verdict.Detail -Pattern '40 -> 1'
    }

    It 'reports RESTARTED for a counter that merely stayed put' {
        # -le, not -lt. A counter that did not move is no evidence of a reconnect either,
        # and an app that restarted between two reads of the same value would otherwise
        # be reported as having survived the outage.
        Reset-OutageFixture -Baseline (New-Heartbeat 7) -Recovery @(, (New-Heartbeat 7))

        Assert-Equal -Expected 'RESTARTED' -Actual (Invoke-Outage -Settings (New-OutageSettings)).Outcome
    }

    It 'does not call RESTARTED on a heartbeat carrying no counter' {
        # Wait-Heartbeat reports a null counter rather than failing, and in PowerShell
        # $null -le 7 is true -- so without the two null guards a payload format change
        # would turn every run into RESTARTED, blaming the device for a parse.
        Reset-OutageFixture -Baseline @('homie/mqtt-reconnect-check/heartbeat 0 alive') `
                            -Recovery @(, @('homie/mqtt-reconnect-check/heartbeat 0 alive'))

        Assert-Equal -Expected 'PASS' -Actual (Invoke-Outage -Settings (New-OutageSettings)).Outcome
    }

    It 'reports NO-RESULT when the device never reached the broker at all' {
        # Not FAIL: there was nothing to disconnect, so this says nothing about
        # reconnecting. The detail has to carry that, because the two read alike on the
        # summary line.
        Reset-OutageFixture -Baseline @() -Recovery @()

        $verdict = Invoke-Outage -Settings (New-OutageSettings)

        Assert-Equal -Expected 'NO-RESULT' -Actual $verdict.Outcome
        Assert-Match -Actual $verdict.Detail -Pattern 'nothing to disconnect'
        # And it stopped there rather than taking away a broker it had no baseline for.
        Assert-ArrayEqual -Expected @('restart') -Actual $script:outageEvents
    }

    It 'reports FAIL when no heartbeat returned after the broker came back' {
        Reset-OutageFixture -Baseline (New-Heartbeat 3) -Recovery @(, @())

        $verdict = Invoke-Outage -Settings (New-OutageSettings)

        Assert-Equal -Expected 'FAIL' -Actual $verdict.Outcome
        Assert-Match -Actual $verdict.Detail -Pattern 'did not reconnect'
    }

    It 'reports FAIL when heartbeats resumed but the echo never came' {
        # The case the echo exists for. Publishing resumes as soon as the socket is up, so
        # a client that reconnected and replayed no subscriptions looks healthy from the
        # heartbeat alone -- connected and deaf.
        Reset-OutageFixture -Baseline (New-Heartbeat 2) -Recovery @(, (New-Heartbeat 8)) -NoEcho

        $verdict = Invoke-Outage -Settings (New-OutageSettings)

        Assert-Equal -Expected 'FAIL' -Actual $verdict.Outcome
        Assert-Match -Actual $verdict.Detail -Pattern 'without replaying its subscriptions'
        Assert-Match -Actual $verdict.Detail -Pattern 'echo-8'
        # And it did publish: a check reporting FAIL having sent nothing would be
        # measuring its own silence.
        Assert-True -Condition ($script:outagePublished.Count -ge 1)
        Assert-Equal -Expected 'echo-8' -Actual $script:outagePublished[0]
    }

    It 'names the nonce after the counter it last saw' {
        # echo-<counter>, so the payload required back differs on every outage. A fixed
        # nonce would be satisfied by the previous round's echo sitting in the log.
        Reset-OutageFixture -Baseline (New-Heartbeat 2) -Recovery @(, (New-Heartbeat 55))

        Assert-Equal -Expected 'PASS' -Actual (Invoke-Outage -Settings (New-OutageSettings)).Outcome
        Assert-Equal -Expected 'echo-55' -Actual $script:outagePublished[0]
    }

    It 'takes the broker down once per entry in OutageSeconds' {
        Reset-OutageFixture -Baseline (New-Heartbeat 1) `
                            -Recovery @((New-Heartbeat 9), (New-Heartbeat 12))

        $verdict = Invoke-Outage -Settings (New-OutageSettings -OutageSeconds @(0, 0))

        Assert-Equal -Expected 'PASS' -Actual $verdict.Outcome
        Assert-ArrayEqual -Expected @('restart', 'stop', 'start', 'stop', 'start') -Actual $script:outageEvents
        # Both lengths in the detail, so the summary line says what was actually survived.
        Assert-Match -Actual $verdict.Detail -Pattern '0s, 0s'
    }

    It 'carries the counter forward, so the second outage is measured against the first' {
        # $before is reassigned from $latest at the top of every round. Comparing each
        # outage against the original baseline instead would report a device that
        # restarted during the second outage as PASS, as long as its counter had passed
        # the pre-outage one -- 12 is above the baseline of 1 and below the 30 it reached.
        #
        # The two lengths differ (0s, then 1s) so the detail's "1s outage" also proves the
        # label names the outage being run rather than the first one. One second, not the
        # catalog's 20: Invoke-BrokerOutageCheck really does Start-Sleep for it.
        Reset-OutageFixture -Baseline (New-Heartbeat 1) `
                            -Recovery @((New-Heartbeat 30), (New-Heartbeat 12))

        $verdict = Invoke-Outage -Settings (New-OutageSettings -OutageSeconds @(0, 1))

        Assert-Equal -Expected 'RESTARTED' -Actual $verdict.Outcome
        Assert-Match -Actual $verdict.Detail -Pattern '30 -> 12'
        Assert-Match -Actual $verdict.Detail -Pattern '1s outage'
    }

    It 'reports ERROR naming the phase when the broker cannot be cycled' {
        Reset-OutageFixture -Baseline @() -Recovery @()
        function Restart-SuiteBroker {
            param([string]$Port, [int]$SettleSeconds = 0)
            throw 'could not start the broker on port 1883: port in use'
        }

        $verdict = Invoke-Outage -Settings (New-OutageSettings)

        Assert-Equal -Expected 'ERROR' -Actual $verdict.Outcome
        # A host-side fault reported as the device's would be the worst outcome this
        # function can produce, so the phase is part of the detail, not just the message.
        Assert-Match -Actual $verdict.Detail -Pattern 'port in use'
        Assert-Match -Actual $verdict.Detail -Pattern 'before measuring'
    }

    It 'reports ERROR naming the outage it was starting when the stop failed' {
        Reset-OutageFixture -Baseline (New-Heartbeat 1) -Recovery @(, (New-Heartbeat 2))
        function Stop-SuiteBroker {
            param([string]$Port)
            throw 'could not stop the broker on port 1883: access denied'
        }

        $verdict = Invoke-Outage -Settings (New-OutageSettings -OutageSeconds @(3))

        Assert-Equal -Expected 'ERROR' -Actual $verdict.Outcome
        Assert-Match -Actual $verdict.Detail -Pattern 'access denied'
        Assert-Match -Actual $verdict.Detail -Pattern 'starting the 3s outage'
    }

    It 'reports ERROR naming the outage it had just run when the restart failed' {
        Reset-OutageFixture -Baseline (New-Heartbeat 1) -Recovery @(, (New-Heartbeat 2))
        function Start-SuiteBroker {
            param([string]$Port)
            $script:outageEvents += 'start'
            throw 'could not start the broker on port 1883: port still held'
        }

        # 1s rather than the 3s above: this one reaches the Start-Sleep, the stop-failure
        # case throws before it. The two lengths differing is also what shows each label
        # is read from the outage in hand.
        $verdict = Invoke-Outage -Settings (New-OutageSettings -OutageSeconds @(1))

        Assert-Equal -Expected 'ERROR' -Actual $verdict.Outcome
        Assert-Match -Actual $verdict.Detail -Pattern 'port still held'
        Assert-Match -Actual $verdict.Detail -Pattern 'after the 1s outage'
    }
}

Describe 'The snapshot capture window' {
    # Start-HomieCapture, Stop-HomieCapture and the Get-HomieRetainedSnapshot that is the
    # two of them plus a parse. This is the pair issue #84 names first among the
    # uncovered, and the pair a conformance verdict is computed from: what the window
    # caught, and whether it was open when the caller published into it.
    #
    # The subscriber is a scripted cmd.exe script rather than mosquitto_sub, so a case can
    # say what arrives and when. Start-HomieCapture redirects its stdout into the capture
    # file exactly as it does the real one's, so what is exercised is the shipped
    # redirect, the shipped connect wait and the shipped teardown -- no broker, no device,
    # no network. That is the technique #59's throwaway harness used and #102 re-proved
    # against a live broker; this is it in the repository.
    #
    # 'ping -n' is the delay, not 'timeout': timeout needs a console and there is none
    # behind Start-Process -WindowStyle Hidden. 'ping -n <n+1>' is about n seconds.

    $script:captureFile = $null
    $script:fakeSubscriber = $null
    $script:preservedCaptures = @()

    function Get-SmartHomeDevEnvPath {
        param([string]$Port, [string]$Kind)
        return $script:captureFile
    }

    function Get-SmartHomeMosquittoTool {
        param([string]$Name, [string]$Directory)
        return $script:fakeSubscriber
    }

    function Get-SmartHomeSubscriberArgumentString {
        param([string]$Port)
        return ''
    }

    function Save-SnapshotEvidence {
        # The real one copies into $LogDirectory, which a dot-source leaves empty -- it
        # would warn on every window here and say nothing about the subject. Recorded
        # instead, because *that it ran* is a claim worth making: it is in a finally, and
        # the window is the only record of what a verdict was reached on.
        param([hashtable]$Capture)
        $script:preservedCaptures += $Capture.Path
    }

    function Set-FakeSubscriber {
        # -Script is cmd.exe lines. Whatever they echo is what the window catches.
        param([string[]]$Script, [string]$Directory = 'capture')

        $dir = New-TestDirectory -Name $Directory
        $script:captureFile = Join-Path $dir 'snapshot.log'
        $script:fakeSubscriber = Join-Path $dir 'fake-sub.cmd'
        # ASCII and no BOM: cmd.exe reads a UTF-8 BOM as part of the first command.
        Set-Content -LiteralPath $script:fakeSubscriber -Encoding ascii `
                    -Value (@('@echo off') + $Script)
        $script:preservedCaptures = @()
    }

    function Get-DelayLine {
        param([int]$Seconds)
        return ('ping -n {0} 127.0.0.1 > nul' -f ($Seconds + 1))
    }

    function Start-TestCapture {
        # Returns the capture record and whatever was warned, separately: several claims
        # here are about the warning and not about the record.
        param([int]$WaitForConnectSeconds = 0, [int]$ClearTimeoutSeconds = 5)

        $emitted = @(Start-HomieCapture -Port '1883' `
                                        -WaitForConnectSeconds $WaitForConnectSeconds `
                                        -ClearTimeoutSeconds $ClearTimeoutSeconds 3>&1)

        return @{
            Capture  = @($emitted | Where-Object { $_ -is [hashtable] })[0]
            Warnings = @($emitted |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })
        }
    }

    function Stop-TestCapture {
        param([hashtable]$Capture, [int]$SettleSeconds = 0)

        $emitted = @(Stop-HomieCapture -Capture $Capture -SettleSeconds $SettleSeconds 3>&1)

        return @{
            Lines    = @($emitted | Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] })
            Warnings = @($emitted |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })
        }
    }

    function Stop-Leftover {
        # Nothing here may outlive its case: a surviving cmd.exe holds the capture file
        # open and the next case's removal loop then spends its budget on it.
        #
        # Straight to the tree kill rather than through Stop-SmartHomeRecordedProcess,
        # which one case below replaces with a stub that throws. This runs in that case's
        # finally, so going through the stubbed name would make the cleanup fail on
        # exactly the case that most needs it to work. It is also allowed to be blunt:
        # nothing here asserts on it.
        param([hashtable]$Capture)
        if ($Capture) { Stop-SmartHomeProcessTree -ProcessId $Capture.Record.Id }
    }

    It 'hands back a record naming the launcher and the file it redirects into' {
        Set-FakeSubscriber -Script @(Get-DelayLine 10)

        $started = Start-TestCapture
        try {
            Assert-Equal -Expected $script:captureFile -Actual $started.Capture.Path
            Assert-NotNull -Value $started.Capture.Record.Id
            # -Tree, because the real work is in the mosquitto_sub grandchild of the
            # cmd.exe doing the redirect; stopping the launcher alone orphans it.
            Assert-True -Condition $started.Capture.Record.Tree
            # Taken before the connect wait, so the record still carries the name that
            # tells a dead pid from a recycled one -- ProcessName reads back $null once
            # the process has exited, and StartTime alone is a weaker check.
            Assert-Equal -Expected 'cmd' -Actual $started.Capture.Record.Name
        }
        finally { Stop-Leftover -Capture $started.Capture }
    }

    It 'returns without waiting when no connect wait was asked for' {
        # Get-HomieRetainedSnapshot's call. Its settle is a window rather than a
        # measurement of anything published inside it, so it must not pay for a wait.
        Set-FakeSubscriber -Script @(Get-DelayLine 10)

        $elapsed = Measure-Command { $script:started = Start-TestCapture }
        try {
            Assert-True -Condition ($elapsed.TotalSeconds -lt 2) -Because "took $($elapsed.TotalSeconds)s"
            Assert-Equal -Expected 0 -Actual $script:started.Warnings.Count
        }
        finally { Stop-Leftover -Capture $script:started.Capture }
    }

    It 'waits past a diagnostic and returns once a message has actually arrived' {
        # The invariant #59 corrected: the first byte is not the connection being live.
        # The subscriber's stderr shares this file, so its first line can be a diagnostic
        # about a connection that never happened -- and returning on it would report the
        # very thing the wait exists to establish.
        Set-FakeSubscriber -Script @(
            'echo Warning: Unable to set TCP_NODELAY.'
            (Get-DelayLine 2)
            'echo homie/probe/$state 1 ready'
            (Get-DelayLine 10)
        )

        $elapsed = Measure-Command { $script:started = Start-TestCapture -WaitForConnectSeconds 6 }
        try {
            # 1.5, not 2, though the message lands at about 2s: the claim is only that
            # the diagnostic did not end the wait, and that is decided long before then --
            # the diagnostic is written at once, so returning on it costs about 0.2s. The
            # slack is for a loaded machine, the way 11119bf had to add it to two deadline
            # cases that were reading the same kind of margin.
            Assert-True -Condition ($elapsed.TotalSeconds -ge 1.5) -Because "returned after $($elapsed.TotalSeconds)s, so the diagnostic ended the wait"
            Assert-Equal -Expected 0 -Actual $script:started.Warnings.Count
        }
        finally { Stop-Leftover -Capture $script:started.Capture }
    }

    It 'warns quoting the diagnostics when nothing but diagnostics arrived' {
        Set-FakeSubscriber -Script @(
            'echo Error: Connection refused'
            (Get-DelayLine 10)
        )

        $started = Start-TestCapture -WaitForConnectSeconds 1
        try {
            Assert-Equal -Expected 1 -Actual $started.Warnings.Count
            # The reason is in the file and nowhere else, so the warning has to carry it:
            # this is the difference between "the device did not answer" and "we were not
            # listening", and the captured lines alone cannot tell them apart.
            Assert-Match -Actual $started.Warnings[0] -Pattern 'Connection refused'
            Assert-Match -Actual $started.Warnings[0] -Pattern 'did not within 1s'
            Assert-Match -Actual $started.Warnings[0] -Pattern ([regex]::Escape($script:captureFile))
        }
        finally { Stop-Leftover -Capture $started.Capture }
    }

    It 'warns that nothing at all was recorded when the subscriber is silent' {
        Set-FakeSubscriber -Script @(Get-DelayLine 10)

        $started = Start-TestCapture -WaitForConnectSeconds 1
        try {
            Assert-Equal -Expected 1 -Actual $started.Warnings.Count
            Assert-Match -Actual $started.Warnings[0] -Pattern 'recorded nothing at all'
        }
        finally { Stop-Leftover -Capture $started.Capture }
    }

    It 'gives up on a subscriber that exited rather than spending the whole budget' {
        # Against an unreachable broker mosquitto_sub is gone in about 2.4s, so this is
        # the ordinary outcome and not an edge. Without the liveness probe the wait would
        # sit out its budget watching a process that no longer exists.
        Set-FakeSubscriber -Script @('echo Error: Connection refused')

        $elapsed = Measure-Command { $script:started = Start-TestCapture -WaitForConnectSeconds 10 }
        try {
            Assert-True -Condition ($elapsed.TotalSeconds -lt 8) -Because "spent $($elapsed.TotalSeconds)s of a 10s budget on a dead subscriber"
            Assert-Equal -Expected 1 -Actual $script:started.Warnings.Count
            Assert-Match -Actual $script:started.Warnings[0] -Pattern 'exited first'
        }
        finally { Stop-Leftover -Capture $script:started.Capture }
    }

    It 'does not warn about a subscriber that delivered and then exited' {
        # Which path this takes is a race -- the message may already be in the file by
        # the first poll, or the poll may find it empty and the re-read below the liveness
        # probe pick it up. The assertion holds either way, and that is the point: it pins
        # the OUTCOME and cannot pin the mechanism. The case below does that, and exists
        # because a mutation removing the re-read passed this one.
        Set-FakeSubscriber -Script @('echo homie/probe/$state 1 ready')

        $started = Start-TestCapture -WaitForConnectSeconds 10
        try {
            Assert-Equal -Expected 0 -Actual $started.Warnings.Count
        }
        finally { Stop-Leftover -Capture $started.Capture }
    }

    It 'reads once more after finding the subscriber gone, so a last write is not lost' {
        # The re-read after the liveness probe. That probe classifies from a view taken
        # BEFORE it, and the subscriber's last write -- its whole retained replay, or the
        # error explaining why there was none -- can land in that gap. Reporting from the
        # stale view would claim nothing arrived into a window that is in fact full, which
        # is the same false diagnostic this wait exists to stop making.
        #
        # That gap is microseconds wide against a 100ms poll, so a real subscriber lands
        # in it only by luck: the case above was written that way first and a mutation
        # removing the re-read passed it. The probe stub is what makes it deterministic --
        # it writes the line and THEN reports the process gone, which is exactly the
        # ordering being modelled. The subscriber itself stays silent, so the read that
        # precedes the probe is genuinely empty.
        Set-FakeSubscriber -Script @(Get-DelayLine 10)

        $script:probeCalls = 0
        function Get-SmartHomeRecordedProcess {
            param([hashtable]$Record)
            $script:probeCalls++
            Add-Content -LiteralPath $script:captureFile -Encoding UTF8 `
                        -Value 'homie/probe/$state 1 ready'
            return $null
        }

        $started = Start-TestCapture -WaitForConnectSeconds 10
        try {
            # One probe: it broke out on the first pass rather than polling on.
            Assert-Equal -Expected 1 -Actual $script:probeCalls
            # And nothing was warned about, because the window is full -- even though the
            # subscriber was already gone when the wait looked.
            Assert-Equal -Expected 0 -Actual $started.Warnings.Count
        }
        finally { Stop-Leftover -Capture $started.Capture }
    }

    It 'clears a capture file left behind by a previous window' {
        # Not tidiness. The connect wait reads this file to decide whether THIS subscriber
        # is live, and a previous capture's lines satisfy it instantly -- defeating the
        # wait entirely, on a window the caller is about to publish into.
        Set-FakeSubscriber -Script @(Get-DelayLine 10)
        Set-TestFileContent -Path $script:captureFile -Content @('homie/stale/$state 1 ready')

        $started = Start-TestCapture -WaitForConnectSeconds 1
        try {
            # The stale line is gone, and the warning proves the wait was not satisfied by
            # it: with the removal skipped this is a silent return instead.
            Assert-Equal -Expected 1 -Actual $started.Warnings.Count
            Assert-Match -Actual $started.Warnings[0] -Pattern 'recorded nothing at all'
        }
        finally { Stop-Leftover -Capture $started.Capture }
    }

    It 'throws naming the file when a previous subscriber still holds it open' {
        # The branch that must not become a silent overwrite: -ErrorAction
        # SilentlyContinue on the Remove-Item is what makes this reachable at all, so
        # without the deadline test the loop would fall through to a subscriber appending
        # to somebody else's capture.
        Set-FakeSubscriber -Script @(Get-DelayLine 10)
        Set-TestFileContent -Path $script:captureFile -Content @('held open')

        $held = [System.IO.File]::Open($script:captureFile, 'Open', 'Read', 'None')
        try {
            $message = Assert-Throws -Body { Start-HomieCapture -Port '1883' -ClearTimeoutSeconds 1 }
            Assert-Match -Actual $message -Pattern ([regex]::Escape($script:captureFile))
            Assert-Match -Actual $message -Pattern 'still holds it open'
        }
        finally { $held.Dispose() }
    }

    It 'returns the window''s lines in arrival order' {
        Set-FakeSubscriber -Script @(
            'echo homie/probe/$homie 1 4.0.0'
            'echo homie/probe/$state 1 ready'
            'echo homie/probe/sensor/temperature 0 21.5'
            (Get-DelayLine 10)
        )

        $started = Start-TestCapture -WaitForConnectSeconds 5
        $stopped = Stop-TestCapture -Capture $started.Capture -SettleSeconds 1

        Assert-ArrayEqual -Expected @(
            'homie/probe/$homie 1 4.0.0'
            'homie/probe/$state 1 ready'
            'homie/probe/sensor/temperature 0 21.5'
        ) -Actual $stopped.Lines
        Assert-Equal -Expected 0 -Actual $stopped.Warnings.Count
    }

    It 'counts the window it closed, which is what explains a run''s duration' {
        Set-FakeSubscriber -Script @('echo homie/probe/$state 1 ready', (Get-DelayLine 10))

        $before = $script:snapshotsTaken
        $started = Start-TestCapture -WaitForConnectSeconds 5
        Stop-TestCapture -Capture $started.Capture -SettleSeconds 0 | Out-Null

        Assert-Equal -Expected ($before + 1) -Actual $script:snapshotsTaken
    }

    It 'preserves the capture even when closing the window throws' {
        # Save-SnapshotEvidence is in a finally, and that is the point: the window is the
        # only record of what a verdict was computed from, and the next Start-HomieCapture
        # deletes it. #54's throw is the case that made this urgent.
        Set-FakeSubscriber -Script @('echo homie/probe/$state 1 ready', (Get-DelayLine 10))

        $started = Start-TestCapture -WaitForConnectSeconds 5
        try {
            function Stop-SmartHomeRecordedProcess {
                param([hashtable]$Record)
                throw 'taskkill fell over'
            }

            Assert-Throws -Body { Stop-HomieCapture -Capture $started.Capture -SettleSeconds 0 } `
                          -Pattern 'taskkill fell over' | Out-Null
            Assert-ArrayEqual -Expected @($script:captureFile) -Actual $script:preservedCaptures
        }
        finally { Stop-Leftover -Capture $started.Capture }
    }

    It 'warns that a short capture is the host''s fault, quoting the subscriber''s reason' {
        # A subscriber already gone did not observe the whole window, so its capture is
        # short for a host-side reason -- indistinguishable, in the lines alone, from a
        # device that published nothing. Said where the difference is known, rather than
        # left for a caller to misread as evidence about the device.
        Set-FakeSubscriber -Script @('echo Error: Connection refused')

        $started = Start-TestCapture -WaitForConnectSeconds 3
        $stopped = Stop-TestCapture -Capture $started.Capture -SettleSeconds 0

        Assert-Equal -Expected 1 -Actual $stopped.Warnings.Count
        Assert-Match -Actual $stopped.Warnings[0] -Pattern 'says nothing about the device'
        # The reason is already in the lines -- stderr shares this file -- and it is the
        # detail #54's throw discarded by failing before the read.
        Assert-Match -Actual $stopped.Warnings[0] -Pattern 'Connection refused'
        # And it is a warning, not a throw: this runs inside the caller's finally, where a
        # throw replaces the verdict that caller had already reached.
        Assert-ArrayEqual -Expected @('Error: Connection refused') -Actual $stopped.Lines
    }

    It 'says the subscriber recorded no reason when there is nothing to quote' {
        Set-FakeSubscriber -Script @('echo homie/probe/$state 1 ready')

        $started = Start-TestCapture -WaitForConnectSeconds 3
        $stopped = Stop-TestCapture -Capture $started.Capture -SettleSeconds 0

        Assert-Equal -Expected 1 -Actual $stopped.Warnings.Count
        Assert-Match -Actual $stopped.Warnings[0] -Pattern 'it recorded no reason'
        # A message line is not a reason: quoting the retained replay back at the reader
        # would bury the point.
        Assert-Match -Actual $stopped.Warnings[0] -Pattern ([regex]::Escape('recorded no reason'))
    }

    It 'holds the window open for its settle before closing it' {
        # The settle is the window. A caller that publishes inside one gets exactly this
        # long for the response, so a stop that closed immediately would cut off every
        # message the caller is waiting for.
        Set-FakeSubscriber -Script @(
            (Get-DelayLine 1)
            'echo homie/probe/lifecycle 0 alert'
            (Get-DelayLine 10)
        )

        $started = Start-TestCapture
        $stopped = Stop-TestCapture -Capture $started.Capture -SettleSeconds 3

        Assert-ArrayEqual -Expected @('homie/probe/lifecycle 0 alert') -Actual $stopped.Lines
    }

    It 'Get-HomieRetainedSnapshot is the window plus the parse, retain flags intact' {
        # The three compose: open, close, collapse per topic. The flag has to survive --
        # it is the only thing that separates the broker's retained store from a live
        # delivery, and every conformance assertion about retained-ness reads it.
        Set-FakeSubscriber -Script @(
            'echo homie/probe/$state 1 ready'
            'echo homie/probe/sensor/temperature 0 21.5'
            (Get-DelayLine 10)
        )

        # Get-HomieRetainedSnapshot exposes no settle -- it takes Stop-HomieCapture's
        # default, which is read from here up the dynamic scope chain. Shortened because
        # the claim is that the three compose, not how long the window stays open; that
        # is the case above.
        $SnapshotSettleSeconds = 1

        $snapshot = Get-HomieRetainedSnapshot -Port '1883'

        Assert-Equal -Expected 'ready' -Actual $snapshot['homie/probe/$state'].Payload
        Assert-True -Condition $snapshot['homie/probe/$state'].Retained
        Assert-Equal -Expected '21.5' -Actual $snapshot['homie/probe/sensor/temperature'].Payload
        Assert-False -Condition $snapshot['homie/probe/sensor/temperature'].Retained
    }
}

Describe 'Wait-ForRetainedValue' {
    # Polls fresh snapshots until a topic reaches a value. Its previous defect was #35:
    # it read a retained $state=ready left by a PREVIOUS boot as proof this one had
    # announced, and the host then published five non-retained /set commands the device
    # was not yet subscribed to, which the broker dropped with no trace.
    #
    # Get-HomieRetainedSnapshot is the stub here, and it is a queue: what the caller sees
    # differs from round to round, which is the whole subject. Publish-HomieCommand is
    # recorded rather than performed.

    $script:snapshotQueue = @()
    $script:snapshotReads = 0
    $script:retainPublished = @()

    function Get-HomieRetainedSnapshot {
        param([string]$Port)

        $index = [math]::Min($script:snapshotReads, $script:snapshotQueue.Count - 1)
        $script:snapshotReads++
        if ($index -lt 0) { return @{} }
        return $script:snapshotQueue[$index]
    }

    function Publish-HomieCommand {
        param([string]$Port, [string]$Topic, [string]$Payload)
        # Recorded with the read count, so a case can assert the publish happened at the
        # TOP of the round rather than merely at some point during it.
        $script:retainPublished += ('{0}={1}@{2}' -f $Topic, $Payload, $script:snapshotReads)
    }

    function Reset-SnapshotQueue {
        param([hashtable[]]$Snapshots)
        $script:snapshotQueue = $Snapshots
        $script:snapshotReads = 0
        $script:retainPublished = @()
    }

    function New-Snapshot {
        param([string]$Topic, [string]$Payload, [bool]$Retained)
        return @{ $Topic = @{ Payload = $Payload; Retained = $Retained } }
    }

    It 'returns Ok with the snapshot it matched on' {
        # The snapshot comes back because the caller needs the one the match was made in,
        # not a fresher one: a later window is a different set of retained values.
        Reset-SnapshotQueue -Snapshots @((New-Snapshot -Topic 'homie/x/$state' -Payload 'ready' -Retained $true))

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/$state' -Expected 'ready' -TimeoutSeconds 5

        Assert-True -Condition $result.Ok
        Assert-Equal -Expected 'ready' -Actual $result.Seen
        Assert-Equal -Expected 'ready' -Actual $result.Snapshot['homie/x/$state'].Payload
    }

    It 'refuses a live delivery of the right value by default' {
        # The #35 half that is still true: a subscriber connecting mid-announce receives
        # the rest of it LIVE, retain flag clear, and accepting that hands the caller a
        # snapshot in which most topics look unretained. It also means the announce was
        # still in flight, which is exactly what the caller is waiting to be over.
        Reset-SnapshotQueue -Snapshots @((New-Snapshot -Topic 'homie/x/$state' -Payload 'ready' -Retained $false))

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/$state' -Expected 'ready' -TimeoutSeconds 1

        Assert-False -Condition $result.Ok
        # Seen still reports the payload, so the caller's message says "not retained"
        # rather than "never appeared".
        Assert-Equal -Expected 'ready' -Actual $result.Seen
    }

    It 'accepts a live delivery when the retain flag is not the claim' {
        # -RequireRetained $false is for callers reading only Ok/Seen. Waiting for a
        # REPLAYED value there costs a whole extra window whenever the device publishes it
        # just after the subscriber connected -- a coin flip for a value written within
        # milliseconds of a command.
        Reset-SnapshotQueue -Snapshots @((New-Snapshot -Topic 'homie/x/lifecycle' -Payload 'alert' -Retained $false))

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/lifecycle' -Expected 'alert' `
                                        -TimeoutSeconds 5 -RequireRetained $false

        Assert-True -Condition $result.Ok
    }

    It 'keeps polling until the value arrives' {
        Reset-SnapshotQueue -Snapshots @(
            @{}
            (New-Snapshot -Topic 'homie/x/$state' -Payload 'init' -Retained $true)
            (New-Snapshot -Topic 'homie/x/$state' -Payload 'ready' -Retained $true)
        )

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/$state' -Expected 'ready' -TimeoutSeconds 5

        Assert-True -Condition $result.Ok
        Assert-Equal -Expected 3 -Actual $script:snapshotReads
    }

    It 'reports the last payload it saw rather than the one it wanted' {
        Reset-SnapshotQueue -Snapshots @((New-Snapshot -Topic 'homie/x/$state' -Payload 'init' -Retained $true))

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/$state' -Expected 'ready' -TimeoutSeconds 1

        Assert-False -Condition $result.Ok
        Assert-Equal -Expected 'init' -Actual $result.Seen
    }

    It 'reports <nothing> for a topic the store never held' {
        # Distinct from a wrong payload, and the caller's message says which: "the device
        # never published this" and "it published something else" have different next
        # steps.
        Reset-SnapshotQueue -Snapshots @(@{})

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/$state' -Expected 'ready' -TimeoutSeconds 1

        Assert-False -Condition $result.Ok
        Assert-Equal -Expected '<nothing>' -Actual $result.Seen
        Assert-NotNull -Value $result.Snapshot
    }

    It 'republishes at the top of every round when given a command' {
        # A /set is non-retained, so one that arrives while the device is not subscribed
        # is dropped and no amount of further polling can recover it. Re-sending each
        # round is what closes that window; publishing once and then only observing is
        # the #35 failure.
        Reset-SnapshotQueue -Snapshots @(
            @{}
            @{}
            (New-Snapshot -Topic 'homie/x/lifecycle' -Payload 'alert' -Retained $true)
        )

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/lifecycle' -Expected 'alert' `
                                        -TimeoutSeconds 5 `
                                        -RepublishTopic 'homie/x/lifecycle/set' -RepublishPayload 'alert'

        Assert-True -Condition $result.Ok
        # Three publishes, each recorded before its round's read: @0, @1, @2 rather than
        # @1, @2, @3. A publish after the snapshot would be measuring the round before it.
        Assert-ArrayEqual -Expected @(
            'homie/x/lifecycle/set=alert@0'
            'homie/x/lifecycle/set=alert@1'
            'homie/x/lifecycle/set=alert@2'
        ) -Actual $script:retainPublished
    }

    It 'publishes nothing when no command was given' {
        # The announce and the re-announce are things the device does by itself. A
        # controller publishing into that wait would be changing what it is measuring.
        Reset-SnapshotQueue -Snapshots @((New-Snapshot -Topic 'homie/x/$state' -Payload 'ready' -Retained $true))

        Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/$state' -Expected 'ready' -TimeoutSeconds 5 | Out-Null

        Assert-Equal -Expected 0 -Actual $script:retainPublished.Count
    }

    It 'returns the last snapshot it read when the deadline passes' {
        # Not an empty one: the caller reports what it saw, and a wait that timed out
        # having read three windows should hand back the third rather than nothing.
        Reset-SnapshotQueue -Snapshots @((New-Snapshot -Topic 'homie/x/$state' -Payload 'init' -Retained $true))

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/$state' -Expected 'ready' -TimeoutSeconds 1

        Assert-False -Condition $result.Ok
        Assert-Equal -Expected 'init' -Actual $result.Snapshot['homie/x/$state'].Payload
    }

    It 'accepts a payload in the wrong case -- issue #93' {
        # Pinned as it behaves today, NOT endorsed. The comparison is -eq, which is
        # case-insensitive, so a device announcing $state = READY is read as having
        # announced 'ready' and the conformance run proceeds on it. The Homie
        # vocabularies are lowercase, so this can only ever accept something it should
        # reject -- the passing-while-lying shape #34 and #36 were.
        #
        # A third site beyond the two #93's body lists, and not reached by either of its
        # fixes: the snapshot's comparer decides the topic KEY, and this reads .Payload
        # off the entry that key found. Recorded on the issue with the measurement.
        # Closing #93 must invert this case rather than delete it.
        Reset-SnapshotQueue -Snapshots @((New-Snapshot -Topic 'homie/x/$state' -Payload 'READY' -Retained $true))

        $result = Wait-ForRetainedValue -Port '1883' -Topic 'homie/x/$state' -Expected 'ready' -TimeoutSeconds 1

        Assert-True -Condition $result.Ok -Because 'today READY satisfies a wait for ready'
        Assert-Equal -Expected 'READY' -Actual $result.Seen
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

Describe 'Invoke-CommandRetryRounds' {
    # The retry loop behind Measure-HomieConformance's /set round trip and its
    # out-of-format round. Both of those publish into a live broker and read a real
    # device's answer back, so none of the looping could be asserted at a desk while it
    # sat inside them, twice.
    #
    # The blocks stand in for the broker rather than a stub of Publish-HomieCommand:
    # what these cases are about is the loop's contract with the four blocks it is
    # handed, and the blocks the shipped call sites pass are the only part that touches
    # mosquitto.
    $script:retryPublished = @()
    $script:retryObserved = @()
    $script:retryContexts = @()
    $script:retryRound = 0

    function Reset-Recorders {
        $script:retryPublished = @()
        $script:retryObserved = @()
        $script:retryContexts = @()
        $script:retryRound = 0
    }

    It 'publishes every item once and returns nothing pending when all settle' {
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @('a', 'b', 'c') -TimeoutSeconds 5 `
            -Publish { param($item) $script:retryPublished += $item } `
            -Observe { return 'settled' } `
            -IsSettled { param($item, $observation) return $true }

        Assert-ArrayEqual -Expected @('a', 'b', 'c') -Actual $script:retryPublished
        Assert-ArrayEqual -Expected @() -Actual $result.Pending
        Assert-Equal -Expected 1 -Actual $result.Rounds
    }

    It 'observes once per round, not once per item' {
        # The /set round's whole cost argument: five properties are checked against one
        # fresh-subscriber window instead of costing five of them.
        Reset-Recorders

        Invoke-CommandRetryRounds -Items @('a', 'b', 'c', 'd', 'e') -TimeoutSeconds 5 `
            -Publish { param($item) } `
            -Observe { $script:retryObserved += 'window'; return 'settled' } `
            -IsSettled { param($item, $observation) return $true } | Out-Null

        Assert-ArrayEqual -Expected @('window') -Actual $script:retryObserved
    }

    It 'republishes only the items that are still pending' {
        # 'a' comes back at once, 'b' only from the second observation on, so the second
        # round must carry 'b' alone. Re-sending a settled command is harmless on the
        # device -- a /set is idempotent -- but it is the pending list, not idempotence,
        # that keeps a healthy run to one round.
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @('a', 'b') -TimeoutSeconds 5 `
            -Publish { param($item) $script:retryPublished += $item } `
            -Observe { $script:retryRound++; return $script:retryRound } `
            -IsSettled { param($item, $observation) return ($item -eq 'a' -or $observation -ge 2) }

        Assert-ArrayEqual -Expected @('a', 'b', 'b') -Actual $script:retryPublished
        Assert-Equal -Expected 2 -Actual $result.Rounds
        Assert-ArrayEqual -Expected @() -Actual $result.Pending
    }

    It 'returns the round count, which is the tell issue #35 is read from' {
        # Measure-HomieConformance warns when this is greater than 1. Nothing else in the
        # run says a command went missing, so a count that stopped being reported would
        # take that warning with it silently.
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
            -Publish { param($item) } `
            -Observe { $script:retryRound++; return $script:retryRound } `
            -IsSettled { param($item, $observation) return ($observation -ge 3) }

        Assert-Equal -Expected 3 -Actual $result.Rounds
    }

    It 'returns what never settled rather than looping past the deadline' {
        # -Observe sleeps because the shipped loop has no sleep of its own: a round costs
        # a 3s snapshot or a capture window, and that is the whole of its pacing. A block
        # that returned instantly would spin this case as fast as the CPU allows.
        #
        # 200ms against a 1s deadline, so the assertion below has five rounds of slack. A
        # sleep sized to give exactly two would make this case fail on a loaded machine
        # for reasons that have nothing to do with the loop.
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @('a', 'b') -TimeoutSeconds 1 `
            -Publish { param($item) $script:retryPublished += $item } `
            -Observe { Start-Sleep -Milliseconds 200; return 'nothing' } `
            -IsSettled { param($item, $observation) return $false }

        Assert-ArrayEqual -Expected @('a', 'b') -Actual $result.Pending
        Assert-True -Condition ($result.Rounds -ge 2) -Because 'the deadline has to allow more than one round'
        Assert-Equal -Expected ($result.Rounds * 2) -Actual $script:retryPublished.Count
    }

    It 'opens the window with -BeforePublish before anything is published' {
        # The out-of-format round's shape. Both payloads have to go past inside the
        # window, so a window opened around the observation instead would miss the very
        # ordering that step measures.
        Reset-Recorders

        Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
            -BeforePublish { $script:retryPublished += 'window-open'; return 'capture-1' } `
            -Publish { param($item) $script:retryPublished += $item } `
            -Observe { param($context) $script:retryContexts += $context; return 'settled' } `
            -IsSettled { param($item, $observation) return $true } | Out-Null

        Assert-ArrayEqual -Expected @('window-open', 'a') -Actual $script:retryPublished
        Assert-ArrayEqual -Expected @('capture-1') -Actual $script:retryContexts
    }

    It 'observes what the publishes did, not a window that closed before them' {
        # The ordering the whole helper turns on, and the one every other case here is
        # blind to: their -Observe blocks return a constant or a round counter, so an
        # -Observe hoisted above the publish loop satisfies all of them. This one wires
        # the two together -- what -Publish put in the store is exactly what -Observe
        # hands back -- so observing first leaves an empty store and nothing settles.
        #
        # On the /set round that inversion takes the snapshot before the commands go out,
        # and at the shipped CommandTimeoutSeconds it reports all five properties as
        # never echoed against a device that applied every one of them.
        #
        # -Observe returns a COPY of the store, not the store. Handing back the live
        # object defeats the case: the publishes mutate it before the predicate reads it,
        # so an -Observe hoisted above them still sees everything and the mutation
        # survives. Get-HomieRetainedSnapshot has this property for real -- it builds a
        # fresh object out of a closed capture window -- and the case has to model it.
        Reset-Recorders
        $store = @{}

        $result = Invoke-CommandRetryRounds -Items @('a', 'b') -TimeoutSeconds 5 `
            -Publish { param($item) $store[$item] = 'echoed' } `
            -Observe { return ,@($store.Keys) } `
            -IsSettled { param($item, $observation) return ([array]::IndexOf($observation, $item) -ge 0) }

        Assert-Equal -Expected 1 -Actual $result.Rounds -Because 'a round that publishes then observes settles first time'
        Assert-ArrayEqual -Expected @() -Actual $result.Pending
    }

    It 'opens a window every round, not just the first' {
        # A window opened once and reused would have rounds 2+ of the out-of-format round
        # calling Stop-HomieCapture on an already-closed capture and reading round 1''s
        # stale lines as fresh evidence -- and rounds 2+ are the lost-command path the
        # retry exists for in the first place.
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
            -BeforePublish { $script:retryRound++; return ("window-{0}" -f $script:retryRound) } `
            -Publish { param($item) } `
            -Observe { param($context) $script:retryContexts += $context; return $context } `
            -IsSettled { param($item, $observation) return ($observation -eq 'window-3') }

        Assert-Equal -Expected 3 -Actual $result.Rounds
        Assert-ArrayEqual -Expected @('window-1', 'window-2', 'window-3') -Actual $script:retryContexts `
                          -Because 'each round must observe through the window that round opened'
    }

    It 'opens one window for the whole round, not one per item' {
        # One window holds every item''s traffic, the same way one -Observe covers them
        # all. A window per item would leave the out-of-format round opening five and
        # closing only the last, orphaning four mosquitto_sub processes on the shared
        # capture path -- the corruption the lifecycle step''s own comment describes.
        Reset-Recorders

        Invoke-CommandRetryRounds -Items @('a', 'b', 'c', 'd', 'e') -TimeoutSeconds 5 `
            -BeforePublish { $script:retryContexts += 'opened'; return 'window' } `
            -Publish { param($item) } `
            -Observe { return 'settled' } `
            -IsSettled { param($item, $observation) return $true } | Out-Null

        Assert-ArrayEqual -Expected @('opened') -Actual $script:retryContexts
    }

    It 'throws when -BeforePublish writes more than the handle it returns' {
        # Sharper than the -IsSettled guard below: what this block returns is the handle
        # to a window it has just opened, so an unbindable $context throws with that
        # window still open. Named here rather than left to Stop-HomieCapture''s
        # parameter binder, whose message says nothing about where the extra value
        # came from.
        $message = Assert-Throws -Pattern 'exactly one value' -Body {
            Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
                -BeforePublish {
                    'chatter from the window'
                    return 'the-handle'
                } `
                -Publish { param($item) } `
                -Observe { return 'settled' } `
                -IsSettled { param($item, $observation) return $true }
        }

        Assert-Match -Pattern 'chatter from the window' -Actual $message
    }

    It 'hands -Observe nothing when there is no window to open' {
        # The /set round passes no -BeforePublish: its snapshot is taken after the
        # publishes, so there is no context to carry.
        #
        # Recorded into a hashtable rather than a variable, because dynamic scope only
        # goes one way: a block *reads* its caller's variables up the chain, but an
        # assignment inside it creates a local that dies with the block. That is why both
        # shipped -IsSettled blocks record into $lastSeen / $seenPayloads by index rather
        # than assigning to a plain variable, and this case is written the same way it
        # would have to be if it were one of them.
        Reset-Recorders
        $seen = @{}

        Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
            -Publish { param($item) } `
            -Observe { param($context) $seen['called'] = $true; $seen['context'] = $context; return 'settled' } `
            -IsSettled { param($item, $observation) return $true } | Out-Null

        Assert-True -Condition $seen['called'] -Because '-Observe has to run even with no window opened'
        Assert-Null -Value $seen['context']
    }

    It 'discards whatever -Publish writes' {
        # The lesson the sibling polling helper's -BeforeRead had to learn (#97): a
        # function returns everything written to its output stream, not just what it
        # returns. A publish block that emitted a line would prepend it to this
        # function's own result, and the caller's $result.Pending would then be read off
        # a string.
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
            -Publish { param($item) 'chatter from the publish block' } `
            -Observe { return 'settled' } `
            -IsSettled { param($item, $observation) return $true }

        Assert-Equal -Expected 1 -Actual @($result).Count
        Assert-ArrayEqual -Expected @() -Actual $result.Pending
    }

    It 'stops retrying an item whose predicate settled on evidence of failure' {
        # The out-of-format round's semantics, and the one place the two shipped
        # predicates genuinely differ: it settles as soon as EITHER payload was seen,
        # because seeing the forbidden one is the defect being measured. A loop that
        # retried that away would replace a recorded failure with a clean window and
        # report PASS. Settled is settled -- the loop does not second-guess it.
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @('bad-landed') -TimeoutSeconds 5 `
            -Publish { param($item) $script:retryPublished += $item } `
            -Observe { return @('the forbidden payload') } `
            -IsSettled {
                param($item, $observation)
                return ([array]::IndexOf($observation, 'the forbidden payload') -ge 0 -or
                        [array]::IndexOf($observation, 'the valid payload') -ge 0)
            }

        Assert-Equal -Expected 1 -Actual $result.Rounds
        Assert-ArrayEqual -Expected @('bad-landed') -Actual $script:retryPublished
    }

    It 'retries an item whose predicate saw neither payload, which is a lost command' {
        # The other half of the same predicate, and the reason it is a retry loop at all.
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @('lost') -TimeoutSeconds 1 `
            -Publish { param($item) $script:retryPublished += $item } `
            -Observe { Start-Sleep -Milliseconds 200; return @('unrelated traffic') } `
            -IsSettled {
                param($item, $observation)
                return ([array]::IndexOf($observation, 'the forbidden payload') -ge 0 -or
                        [array]::IndexOf($observation, 'the valid payload') -ge 0)
            }

        Assert-ArrayEqual -Expected @('lost') -Actual $result.Pending
        Assert-True -Condition ($script:retryPublished.Count -ge 2) -Because 'a lost command has to be re-sent'
    }

    It 'throws rather than settling an item on a predicate that also wrote to its output stream' {
        # Without the guard this is silent and wrong in the worst direction: the verdict
        # is @('chatter', $false), a non-empty array and therefore true, so the item
        # settles having been measured as failed. That is the false-PASS shape this file
        # has produced twice (#34, #36).
        $message = Assert-Throws -Pattern 'exactly one value' -Body {
            Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
                -Publish { param($item) } `
                -Observe { return 'observation' } `
                -IsSettled {
                    param($item, $observation)
                    'chatter'
                    return $false
                }
        }

        Assert-Match -Pattern 'chatter' -Actual $message `
                     -Because 'the message has to show what the block emitted, or it cannot be found'
    }

    It 'throws when -IsSettled returns nothing at all' {
        Assert-Throws -Pattern 'exactly one value' -Body {
            Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
                -Publish { param($item) } `
                -Observe { return 'observation' } `
                -IsSettled { param($item, $observation) }
        } | Out-Null
    }

    It 'reads its blocks'' variables from the caller' {
        # How both call sites record what they saw: the predicate assigns into a
        # hashtable declared beside it in Measure-HomieConformance, which is read after
        # the rounds are over. The blocks are evaluated inside this function, so that
        # only works up the dynamic scope chain.
        Reset-Recorders
        $lastSeen = @{}
        $expected = 'echo'

        Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
            -Publish { param($item) } `
            -Observe { return 'echo' } `
            -IsSettled {
                param($item, $observation)
                $lastSeen[$item] = $observation
                return ($observation -eq $expected)
            } | Out-Null

        Assert-Equal -Expected 'echo' -Actual $lastSeen['a']
    }

    It 'lets a block call a function defined where the block was written' {
        # .GetNewClosure() would break this, and the shipped -Publish blocks both call
        # Publish-HomieCommand. It was the first design for the sibling polling helper
        # and three cases caught it before it shipped (#97), so it is pinned here too.
        Reset-Recorders

        function Send-TestCommand { param($Item) $script:retryPublished += ("sent:{0}" -f $Item) }

        Invoke-CommandRetryRounds -Items @('a') -TimeoutSeconds 5 `
            -Publish { param($item) Send-TestCommand -Item $item } `
            -Observe { return 'settled' } `
            -IsSettled { param($item, $observation) return $true } | Out-Null

        Assert-ArrayEqual -Expected @('sent:a') -Actual $script:retryPublished
    }

    It 'runs no round at all for an empty item list' {
        Reset-Recorders

        $result = Invoke-CommandRetryRounds -Items @() -TimeoutSeconds 5 `
            -Publish { param($item) $script:retryPublished += $item } `
            -Observe { $script:retryObserved += 'window'; return 'settled' } `
            -IsSettled { param($item, $observation) return $true }

        Assert-Equal -Expected 0 -Actual $result.Rounds
        Assert-ArrayEqual -Expected @() -Actual $script:retryPublished
        Assert-ArrayEqual -Expected @() -Actual $script:retryObserved
    }
}
