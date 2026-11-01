# The runner testing itself.
#
# This is not ceremony. A test framework that cannot fail is the exact shape of the
# failure this repository has already shipped: vstest exited 0 while every nanoFramework
# test was silently skipped, and three commits went out on a green run that executed
# nothing. Every assertion below is checked in the direction that matters -- that a false
# claim actually fails -- because the direction that a true claim passes is proved by the
# whole rest of the suite anyway.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-AssertionFails {
    # Runs an assertion that is expected to fail, and returns its message. $null means it
    # did NOT fail, which is what these cases are looking for. Anything that is not an
    # assertion failure is re-thrown, so a genuine error in the runner still reads as one.
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Body
    )

    try {
        & $Body | Out-Null
        return $null
    }
    catch {
        $target = $_.TargetObject
        if ($target -is [System.Collections.IDictionary] -and $target.Contains('SmartHomeAssertion')) {
            return $target['Message']
        }
        throw
    }
}

Describe 'Assert-Equal' {
    It 'fails when the values differ' {
        $message = Test-AssertionFails { Assert-Equal -Expected 'a' -Actual 'b' }
        Assert-NotNull -Value $message -Because 'a false claim must fail'
        Assert-Match -Pattern "expected 'a', got 'b'" -Actual $message
    }

    It 'is case-sensitive by default' {
        # The reason this default is worth a test: ConvertTo-HomieSnapshot merges two
        # captured lines only on a byte-for-byte equal payload (-ceq), so a suite whose
        # own comparison could not tell 'TRUE' from 'true' could not check that at all.
        Assert-NotNull -Value (Test-AssertionFails { Assert-Equal -Expected 'true' -Actual 'TRUE' })
    }

    It 'accepts a case difference under -IgnoreCase' {
        Assert-Null -Value (Test-AssertionFails { Assert-Equal -Expected 'true' -Actual 'TRUE' -IgnoreCase })
    }

    It 'fails when only one side is null' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-Equal -Expected 'a' -Actual $null })
        Assert-NotNull -Value (Test-AssertionFails { Assert-Equal -Expected $null -Actual 'a' })
    }

    It 'passes when both sides are null' {
        Assert-Null -Value (Test-AssertionFails { Assert-Equal -Expected $null -Actual $null })
    }

    It 'prefixes the message with -Because' {
        $message = Test-AssertionFails { Assert-Equal -Expected 1 -Actual 2 -Because 'the count is wrong' }
        Assert-Match -Pattern 'the count is wrong' -Actual $message
    }

    It 'refuses a collection instead of quietly passing on one that contains the value' {
        # The reason this refusal exists rather than a comparison: PowerShell's -eq filters
        # a collection on the left, so @('a','b') -ceq 'a' is @('a') -- truthy -- and this
        # assertion used to PASS for that claim while still failing for
        # -Expected @('a','b') -Actual 'z'. Selectively wrong is worse than wrong.
        $message = Test-AssertionFails { Assert-Equal -Expected @('a', 'b') -Actual 'a' }

        Assert-NotNull -Value $message -Because 'this is the case that used to pass'
        Assert-Match -Pattern 'Assert-ArrayEqual' -Actual $message -Because 'and it has to name the right tool'
    }

    It 'refuses a collection on either side' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-Equal -Expected 'a' -Actual @('a', 'b') })
        Assert-NotNull -Value (Test-AssertionFails { Assert-Equal -Expected @() -Actual @() })
    }

    It 'still compares strings, which are enumerable but not collections' {
        Assert-Null -Value (Test-AssertionFails { Assert-Equal -Expected 'abc' -Actual 'abc' })
    }
}

Describe 'Assert-ArrayEqual' {
    It 'fails on a length mismatch and says both lengths' {
        $message = Test-AssertionFails { Assert-ArrayEqual -Expected @('a', 'b') -Actual @('a') }
        Assert-Match -Pattern 'expected 2 item' -Actual $message
        Assert-Match -Pattern 'got 1 item' -Actual $message
    }

    It 'fails on a content mismatch and says which index' {
        $message = Test-AssertionFails { Assert-ArrayEqual -Expected @('a', 'b') -Actual @('a', 'c') }
        Assert-Match -Pattern 'item 1' -Actual $message
    }

    It 'is case-sensitive' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-ArrayEqual -Expected @('a') -Actual @('A') })
    }

    It 'treats a scalar and a one-element array as equal' {
        Assert-Null -Value (Test-AssertionFails { Assert-ArrayEqual -Expected 'a' -Actual @('a') })
    }

    It 'passes on two empty collections' {
        Assert-Null -Value (Test-AssertionFails { Assert-ArrayEqual -Expected @() -Actual @() })
    }
}

Describe 'Assert-True / Assert-False' {
    It 'Assert-True fails on $false, 0 and an empty string' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-True -Condition $false })
        Assert-NotNull -Value (Test-AssertionFails { Assert-True -Condition 0 })
        Assert-NotNull -Value (Test-AssertionFails { Assert-True -Condition '' })
    }

    It 'Assert-False fails on $true and on a non-empty string' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-False -Condition $true })
        Assert-NotNull -Value (Test-AssertionFails { Assert-False -Condition 'x' })
    }
}

Describe 'Assert-Null / Assert-NotNull' {
    It 'Assert-Null fails on an empty string, which is not null' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-Null -Value '' })
    }

    It 'Assert-Null fails on an empty array, which is also not null' {
        # Worth pinning rather than assuming: an empty array binds to an untyped
        # parameter as an empty System.Object[], not as $null, so "no items" and "no
        # value" are different claims here. A test that means "no items" wants
        # Assert-ArrayEqual -Expected @(), not Assert-Null.
        Assert-NotNull -Value (Test-AssertionFails { Assert-Null -Value @() })
    }

    It 'Assert-NotNull fails on $null' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-NotNull -Value $null })
    }
}

Describe 'Assert-Match' {
    It 'fails when the pattern does not match' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-Match -Pattern 'cat' -Actual 'dog' })
    }

    It 'fails on $null rather than treating it as an empty match' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-Match -Pattern '.*' -Actual $null })
    }

    It 'refuses a collection rather than reporting no match on one that matched' {
        # -match filters a collection too, so @('cat','dog') -notmatch 'cat' is @('dog') --
        # truthy -- and this used to report "no match" for output that plainly matched.
        $message = Test-AssertionFails { Assert-Match -Pattern 'cat' -Actual @('cat', 'dog') }

        Assert-NotNull -Value $message
        Assert-Match -Pattern 'Join it' -Actual $message -Because 'the message has to say what to do instead'
    }
}

Describe 'Assert-Contains' {
    It 'fails when the item is absent' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-Contains -Item 'c' -Collection @('a', 'b') })
    }

    It 'passes when the item is present' {
        Assert-Null -Value (Test-AssertionFails { Assert-Contains -Item 'b' -Collection @('a', 'b') })
    }

    It 'is case-sensitive, like the rest of the runner' {
        # -notcontains is case-insensitive, so this used to accept 'localhost' as present in
        # @('LOCALHOST') -- and the claims this assertion carries in Common.Tests.ps1 are
        # about exact tokens: '127.0.0.1', '%t %r %p', a topic name.
        Assert-NotNull -Value (Test-AssertionFails { Assert-Contains -Item 'localhost' -Collection @('LOCALHOST') })
    }

    It 'accepts a case difference under -IgnoreCase' {
        Assert-Null -Value (Test-AssertionFails { Assert-Contains -Item 'localhost' -Collection @('LOCALHOST') -IgnoreCase })
    }
}

Describe 'Assert-Throws' {
    It 'fails when the body completes without throwing' {
        $message = Test-AssertionFails { Assert-Throws -Body { 1 + 1 } }
        Assert-Match -Pattern 'expected a terminating error' -Actual $message
    }

    It 'fails when the body throws something the pattern does not match' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-Throws -Body { throw 'boom' } -Pattern 'fizz' })
    }

    It 'returns the message when the body throws as expected' {
        $thrown = Assert-Throws -Body { throw 'boom' } -Pattern 'boom'
        Assert-Equal -Expected 'boom' -Actual $thrown
    }
}

Describe 'Format-TestValue' {
    It 'spells null, strings, booleans and arrays distinguishably' {
        Assert-Equal -Expected '<null>' -Actual (Format-TestValue -Value $null)
        Assert-Equal -Expected "'x'" -Actual (Format-TestValue -Value 'x')
        Assert-Equal -Expected '$true' -Actual (Format-TestValue -Value $true)
        Assert-Equal -Expected "@('a', 'b')" -Actual (Format-TestValue -Value @('a', 'b'))
    }

    It 'renders a hashtable with sorted keys, so two of them compare stably' {
        Assert-Equal -Expected "@{Payload='on'; Retained=`$true}" `
                     -Actual (Format-TestValue -Value @{ Retained = $true; Payload = 'on' })
    }

    It 'does not recurse without bound' {
        $deep = @{ a = @{ b = @{ c = @{ d = 'too far' } } } }
        Assert-Match -Pattern '\.\.\.' -Actual (Format-TestValue -Value $deep)
    }
}

Describe 'New-TestDirectory' {
    It 'creates a fresh empty directory' {
        $dir = New-TestDirectory -Name 'runner'
        Assert-True -Condition (Test-Path -LiteralPath $dir)
        Assert-Equal -Expected 0 -Actual @(Get-ChildItem -LiteralPath $dir).Count
    }

    It 'creates a usable directory even when the name contains brackets' {
        # The fixtures for issue #71 need exactly this, so the helper that builds them
        # has to survive it too.
        $dir = New-TestDirectory -Name 'brackets [wip]'
        Set-TestFileContent -Path (Join-Path $dir 'probe.txt') -Content 'hello'
        Assert-Equal -Expected 'hello' -Actual (Get-Content -LiteralPath (Join-Path $dir 'probe.txt') -Raw).Trim()
    }

    It 'hands out a different directory each time' {
        Assert-True -Condition ((New-TestDirectory) -ne (New-TestDirectory))
    }
}

Describe 'Describe / It -Skip' {
    # The runner's way of saying "this case cannot run on this host" without either
    # failing it or letting it count as a pass. A case that never ran must not read as one
    # that did, so the claim worth testing is that a skipped body is NOT executed and the
    # counters say so.

    function Invoke-WithRestoredCounters {
        # The nested groups below record into the same state as the run they are part of.
        # Put the counters back afterwards, or this group would add phantom passes and
        # skips to the totals of the whole suite.
        param([scriptblock]$Body)

        $state = $SmartHomeTestState
        $saved = @{
            Total       = $state.Total
            Passed      = $state.Passed
            Failed      = $state.Failed
            Unsupported = $state.Unsupported
            Reasons     = $state.UnsupportedReason.Clone()
        }
        try {
            & $Body
        }
        finally {
            $state.Total       = $saved.Total
            $state.Passed      = $saved.Passed
            $state.Failed      = $saved.Failed
            $state.Unsupported = $saved.Unsupported
            $state.UnsupportedReason = $saved.Reasons
            $state.SkipReason  = ''
        }
    }

    It 'does not run the body of a skipped It, and counts it as unsupported' {
        Invoke-WithRestoredCounters {
            $script:ran = $false
            $before = $SmartHomeTestState.Unsupported

            It 'inner' -Skip 'no such host' { $script:ran = $true }

            Assert-False -Condition $script:ran -Because 'a skipped case must not execute'
            Assert-Equal -Expected ($before + 1) -Actual $SmartHomeTestState.Unsupported
            Assert-Equal -Expected 1 -Actual $SmartHomeTestState.UnsupportedReason['no such host']
        }
    }

    It 'skips every It inside a skipped Describe, and still runs a later group' {
        Invoke-WithRestoredCounters {
            $script:ranInside = 0
            $totalBefore = $SmartHomeTestState.Total
            $passedBefore = $SmartHomeTestState.Passed

            Describe 'inner group' -Skip 'no such host' {
                It 'one' { $script:ranInside++ }
                It 'two' { $script:ranInside++ }
            } 6>$null

            Assert-Equal -Expected 0 -Actual $script:ranInside
            Assert-Equal -Expected 2 -Actual $SmartHomeTestState.UnsupportedReason['no such host']
            Assert-Equal -Expected $totalBefore -Actual $SmartHomeTestState.Total -Because 'a skipped case is not a run case'
            Assert-Equal -Expected '' -Actual $SmartHomeTestState.SkipReason -Because 'the reason must not leak into the next group'

            Describe 'next group' {
                It 'runs' { $script:ranInside = 10 }
            } 6>$null
            Assert-Equal -Expected 10 -Actual $script:ranInside
            Assert-Equal -Expected ($passedBefore + 1) -Actual $SmartHomeTestState.Passed
        }
    }

    It 'treats an empty reason as "run it"' {
        Invoke-WithRestoredCounters {
            $script:ran = $false
            It 'inner' -Skip '' { $script:ran = $true }
            Assert-True -Condition $script:ran
        }
    }
}

Describe 'Get-WindowsOnlyReason' {
    It 'is empty exactly where the host is Windows, and names the reason elsewhere' {
        $onWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
        $reason = Get-WindowsOnlyReason -Because 'it launches cmd.exe'

        if ($onWindows) {
            Assert-Equal -Expected '' -Actual $reason
        }
        else {
            Assert-Match -Pattern 'needs Windows: it launches cmd\.exe' -Actual $reason
        }
    }
}
