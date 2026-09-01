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
}

Describe 'Assert-Contains' {
    It 'fails when the item is absent' {
        Assert-NotNull -Value (Test-AssertionFails { Assert-Contains -Item 'c' -Collection @('a', 'b') })
    }

    It 'passes when the item is present' {
        Assert-Null -Value (Test-AssertionFails { Assert-Contains -Item 'b' -Collection @('a', 'b') })
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
