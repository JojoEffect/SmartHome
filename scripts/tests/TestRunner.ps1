<#
.SYNOPSIS
    The Describe/It/Assert-* vocabulary the files in this directory are written in.

.DESCRIPTION
    Dot-sourced by Run-ScriptTests.ps1, which is the only supported way to run it: the
    functions here record into state that entry point owns, and a test file invoked on
    its own has nowhere to record.

    This is a runner this repository owns rather than Pester, and that is a deliberate
    trade rather than an oversight. Pester 5 does *not* ship with Windows -- what ships,
    and what is on this machine, is Pester 3.4.0, whose dialect ("Should Be", no
    Should -Be) is incompatible with anything written for 5. Adopting Pester would mean
    Install-Module on every desk, a pinned install step in CI, a row in Test-Setup.ps1
    and an explicit version guard at every import so PowerShell does not auto-load the
    in-box 3.4.0 instead. The whole point of this suite is that proving a host-side change
    costs nothing, and a prerequisite that has to be installed before the first run is a
    reason not to run it. Test-Setup.ps1 exists in this repository precisely because
    every out-of-repo prerequisite reads as broken code the first time it is missing.

    So: no modules, no network, no device. `.\scripts\Run-ScriptTests.ps1` works on a
    fresh clone with nothing installed, and on the CI runner unchanged.

    What is deliberately not here: mocking, test discovery by attribute, parallelism,
    NUnit/JUnit output. If one of those becomes load-bearing, that is the moment to
    reconsider the trade above -- not before.

.NOTES
    Comparisons here are case-SENSITIVE (Assert-Equal, Assert-ArrayEqual, Assert-Contains).
    That is the stricter default and it is what the subjects want: ConvertTo-HomieSnapshot
    merges on -ceq, so a test that could not tell 'TRUE' from 'true' could not check it.
    Assert-Equal and Assert-Contains take -IgnoreCase where case is genuinely not part of
    the claim. Assert-Match is a regex and follows -match's own case-insensitive default;
    write (?-i) into the pattern where that matters.

    Assert-Equal and Assert-Match refuse a collection operand rather than comparing one.
    PowerShell's -eq and -match FILTER a collection instead of returning a boolean, so
    @('a','b') -ceq 'a' is @('a') -- truthy -- and an Assert-Equal built on it would pass
    whenever the expected collection merely contains the actual value. Assert-ArrayEqual
    is the tool for collections; for Assert-Match, join at the call site.
#>

Set-StrictMode -Version Latest

# Read by Assert-Fail to skip its own frames when it works out which line failed. Set
# here rather than passed around because every assertion needs it.
$SmartHomeTestRunnerPath = $PSCommandPath

# One object, mutated in place. Mutating a property works from any scope that can *read*
# the variable, and reading walks the scope chain -- so a test file invoked with & from
# the entry point records into the same state without any $script:/$global: ceremony.
$SmartHomeTestState = @{
    Total     = 0
    Passed    = 0
    Failed    = 0
    Skipped   = 0
    Group     = ''
    Depth     = 0
    Failures  = @()
    Filter    = $null
    Detailed  = $false
    TempRoot  = $null
    TempCount = 0
}

function Format-TestValue {
    # How a value is spelled in a failure message. Values in this suite are strings,
    # booleans, ints, string arrays and small hashtables; anything else falls through to
    # its own ToString, which is enough to tell two of them apart.
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        $Value,

        [int]$Depth = 0
    )

    if ($null -eq $Value) { return '<null>' }
    if ($Value -is [string]) { return "'$Value'" }
    if ($Value -is [bool]) { return ('$' + $Value.ToString().ToLowerInvariant()) }

    if ($Depth -ge 3) { return '...' }

    if ($Value -is [System.Collections.IDictionary]) {
        $pairs = @($Value.Keys | Sort-Object | ForEach-Object {
            "{0}={1}" -f $_, (Format-TestValue -Value $Value[$_] -Depth ($Depth + 1))
        })
        return '@{' + ($pairs -join '; ') + '}'
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $items = @(@($Value) | ForEach-Object { Format-TestValue -Value $_ -Depth ($Depth + 1) })
        return '@(' + ($items -join ', ') + ')'
    }

    return "$Value"
}

function Test-IsTestCollection {
    # Everything PowerShell's comparison operators treat as a collection rather than as a
    # single value -- which is the distinction that matters to the assertions below, and
    # which excludes strings even though they are IEnumerable.
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        $Value
    )

    return ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string]))
}

function Assert-Fail {
    # Every assertion below ends here. The thrown payload is a hashtable rather than a
    # message string so It can tell an assertion apart from the subject blowing up, and
    # so it can report the *test's* line: an ErrorRecord caught from a throw inside this
    # file points at this file, which is never the interesting location.
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    # Found by skipping every frame belonging to this file rather than by a fixed depth,
    # so an assertion built on another assertion still reports the test's own line.
    $frame = @(Get-PSCallStack |
        Where-Object { $_.ScriptName -and $_.ScriptName -ne $SmartHomeTestRunnerPath }) |
        Select-Object -First 1

    $scriptName = '<unknown>'
    $line = 0
    if ($frame) {
        $scriptName = $frame.ScriptName
        $line = $frame.ScriptLineNumber
    }

    throw @{
        SmartHomeAssertion = $true
        Message            = $Message
        ScriptName         = $scriptName
        Line               = $line
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        $Expected,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        $Actual,

        [switch]$IgnoreCase,

        [string]$Because
    )

    # Refused rather than handled, because PowerShell's -eq FILTERS a collection on the
    # left instead of comparing it: @('a','b') -ceq 'a' returns @('a'), which is truthy,
    # so this assertion would silently pass whenever the expected collection merely
    # *contains* the actual value -- and still fail when it does not, which is what makes
    # it look like it works. Assert-ArrayEqual is the tool for collections.
    foreach ($operand in @(@{ Name = 'Expected'; Value = $Expected }, @{ Name = 'Actual'; Value = $Actual })) {
        if (Test-IsTestCollection -Value $operand['Value']) {
            Assert-Fail -Message ("Assert-Equal compares single values, and -{0} is a {1}. Use Assert-ArrayEqual for collections." -f `
                $operand['Name'], $operand['Value'].GetType().Name)
        }
    }

    if ($null -eq $Expected) {
        $same = ($null -eq $Actual)
    }
    elseif ($null -eq $Actual) {
        $same = $false
    }
    elseif ($IgnoreCase) {
        $same = ($Expected -eq $Actual)
    }
    else {
        $same = ($Expected -ceq $Actual)
    }

    if (-not $same) {
        $detail = "expected {0}, got {1}" -f (Format-TestValue -Value $Expected), (Format-TestValue -Value $Actual)
        if ($Because) { $detail = "$Because -- $detail" }
        Assert-Fail -Message $detail
    }
}

function Assert-ArrayEqual {
    # Element-by-element, so a length mismatch and a content mismatch read differently.
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyCollection()]
        $Expected,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyCollection()]
        $Actual,

        [string]$Because
    )

    $expectedItems = @($Expected)
    $actualItems = @($Actual)

    $prefix = ''
    if ($Because) { $prefix = "$Because -- " }

    if ($expectedItems.Count -ne $actualItems.Count) {
        Assert-Fail -Message ("{0}expected {1} item(s) {2}, got {3} item(s) {4}" -f `
            $prefix, $expectedItems.Count, (Format-TestValue -Value $expectedItems),
            $actualItems.Count, (Format-TestValue -Value $actualItems))
    }

    for ($i = 0; $i -lt $expectedItems.Count; $i++) {
        if ($expectedItems[$i] -cne $actualItems[$i]) {
            Assert-Fail -Message ("{0}item {1}: expected {2}, got {3} (full: {4})" -f `
                $prefix, $i, (Format-TestValue -Value $expectedItems[$i]),
                (Format-TestValue -Value $actualItems[$i]), (Format-TestValue -Value $actualItems))
        }
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Condition,

        [string]$Because
    )

    if (-not $Condition) {
        $detail = "expected a true value, got {0}" -f (Format-TestValue -Value $Condition)
        if ($Because) { $detail = "$Because -- $detail" }
        Assert-Fail -Message $detail
    }
}

function Assert-False {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Condition,

        [string]$Because
    )

    if ($Condition) {
        $detail = "expected a false value, got {0}" -f (Format-TestValue -Value $Condition)
        if ($Because) { $detail = "$Because -- $detail" }
        Assert-Fail -Message $detail
    }
}

function Assert-Null {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        $Value,

        [string]$Because
    )

    if ($null -ne $Value) {
        $detail = "expected <null>, got {0}" -f (Format-TestValue -Value $Value)
        if ($Because) { $detail = "$Because -- $detail" }
        Assert-Fail -Message $detail
    }
}

function Assert-NotNull {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        $Value,

        [string]$Because
    )

    if ($null -eq $Value) {
        $detail = 'expected a value, got <null>'
        if ($Because) { $detail = "$Because -- $detail" }
        Assert-Fail -Message $detail
    }
}

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        $Actual,

        [string]$Because
    )

    # Same trap as Assert-Equal, in the other direction: -match on a collection filters it,
    # so @('cat','dog') -notmatch 'cat' returns @('dog') -- truthy -- and this would report
    # no match on output that plainly matched. Refused rather than joined, so the test says
    # what it means: join the lines at the call site if the whole output is the claim.
    if (Test-IsTestCollection -Value $Actual) {
        Assert-Fail -Message ('Assert-Match compares one value, and -Actual is a {0}. Join it with -join, or pick the element the claim is about.' -f $Actual.GetType().Name)
    }

    if ($null -eq $Actual -or $Actual -notmatch $Pattern) {
        $detail = "expected a match for /{0}/, got {1}" -f $Pattern, (Format-TestValue -Value $Actual)
        if ($Because) { $detail = "$Because -- $detail" }
        Assert-Fail -Message $detail
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        $Item,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyCollection()]
        $Collection,

        [switch]$IgnoreCase,

        [string]$Because
    )

    # -cnotcontains, so this keeps the case-sensitive default the rest of the runner has.
    # -notcontains would let 'localhost' be found in @('LOCALHOST'), which is exactly the
    # kind of difference the claims using this assertion are about: topic names, payloads,
    # and the '%t %r %p' format token.
    $absent = if ($IgnoreCase) { @($Collection) -notcontains $Item } else { @($Collection) -cnotcontains $Item }

    if ($absent) {
        $detail = "expected {0} among {1}" -f (Format-TestValue -Value $Item), (Format-TestValue -Value $Collection)
        if ($Because) { $detail = "$Because -- $detail" }
        Assert-Fail -Message $detail
    }
}

function Assert-Throws {
    # Asserts the body raises a terminating error, optionally one whose message matches.
    # Returns the message, so a caller can assert more about it than one pattern.
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Body,

        [string]$Pattern,

        [string]$Because
    )

    $prefix = ''
    if ($Because) { $prefix = "$Because -- " }

    try {
        & $Body | Out-Null
    }
    catch {
        $message = $_.Exception.Message
        if ($Pattern -and $message -notmatch $Pattern) {
            Assert-Fail -Message ("{0}threw, but the message did not match /{1}/: '{2}'" -f $prefix, $Pattern, $message)
        }
        return $message
    }

    Assert-Fail -Message ($prefix + 'expected a terminating error, but the body completed')
}

function Describe {
    # A group. Nesting is allowed and shows as 'outer / inner'; an error escaping the body
    # itself (rather than an It) is recorded as a failure of the group instead of ending
    # the file, so one broken group does not hide every test after it.
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock]$Body
    )

    $state = $SmartHomeTestState
    $previousGroup = $state.Group
    $state.Group = if ($previousGroup) { "$previousGroup / $Name" } else { $Name }
    $state.Depth++

    $passedBefore = $state.Passed
    $failedBefore = $state.Failed

    try {
        & $Body | Out-Null
    }
    catch {
        Add-TestFailure -Name '<group body>' -Record $_
        $state.Failed++
        $state.Total++
    }
    finally {
        $passed = $state.Passed - $passedBefore
        $failed = $state.Failed - $failedBefore
        $indent = '  ' * ($state.Depth - 1)
        $label = "{0}{1}" -f $indent, $Name
        $counts = if ($failed -gt 0) { "{0} passed, {1} FAILED" -f $passed, $failed } else { "{0} passed" -f $passed }
        $colour = if ($failed -gt 0) { 'Red' } else { 'DarkGray' }
        Write-Host ("  {0,-62} {1}" -f $label, $counts) -ForegroundColor $colour

        $state.Depth--
        $state.Group = $previousGroup
    }
}

function Add-TestFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [System.Management.Automation.ErrorRecord]$Record
    )

    $state = $SmartHomeTestState
    $full = if ($state.Group) { "{0} :: {1}" -f $state.Group, $Name } else { $Name }

    $target = $Record.TargetObject
    if ($target -is [System.Collections.IDictionary] -and $target.Contains('SmartHomeAssertion')) {
        $message = $target['Message']
        $where = "{0}:{1}" -f (Split-Path -Leaf $target['ScriptName']), $target['Line']
    }
    else {
        # Not an assertion: the subject itself threw, which is a result too -- report the
        # exception type as well, because "the string is null" and "expected 3, got 4"
        # want different next steps.
        $message = "{0}: {1}" -f $Record.Exception.GetType().Name, $Record.Exception.Message
        $where = '<unknown>'
        $info = $Record.InvocationInfo
        if ($info -and $info.ScriptName) {
            $where = "{0}:{1}" -f (Split-Path -Leaf $info.ScriptName), $info.ScriptLineNumber
        }
    }

    $state.Failures += @{
        Name    = $full
        Message = $message
        Where   = $where
    }
}

function It {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock]$Body
    )

    $state = $SmartHomeTestState
    $full = if ($state.Group) { "{0} :: {1}" -f $state.Group, $Name } else { $Name }

    if ($state.Filter -and ($full -notlike $state.Filter)) {
        $state.Skipped++
        return
    }

    $state.Total++

    try {
        & $Body | Out-Null
        $state.Passed++
        if ($state.Detailed) {
            Write-Host ("      ok   {0}" -f $Name) -ForegroundColor DarkGray
        }
    }
    catch {
        $state.Failed++
        Add-TestFailure -Name $Name -Record $_
        if ($state.Detailed) {
            Write-Host ("      FAIL {0}" -f $Name) -ForegroundColor Red
        }
    }
}

function New-TestDirectory {
    # A fresh empty directory under this run's temp root, removed with it at the end.
    # -Name is appended so a fixture can be recognised in a debugger; it may contain
    # anything legal in a Windows directory name, brackets included -- which is the point
    # for the tests that reproduce issue #71.
    param(
        [string]$Name = 'fixture'
    )

    $state = $SmartHomeTestState
    if (-not $state.TempRoot) {
        Assert-Fail -Message 'New-TestDirectory was called before the run had a temp root; run tests through Run-ScriptTests.ps1.'
    }

    $state.TempCount++
    $path = Join-Path $state.TempRoot ("{0:d3}-{1}" -f $state.TempCount, $Name)
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Set-TestFileContent {
    # Write-a-file helper, on -LiteralPath throughout so a fixture under a bracketed
    # directory can be built at all.
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        $Content
    )

    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8
}
