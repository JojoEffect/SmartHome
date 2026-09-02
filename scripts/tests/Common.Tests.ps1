# scripts\Common.ps1 -- the helpers every other script in this repository is built on.
#
# Dot-sourced into this file's own scope rather than relied on from the entry point's, so
# the subject is unambiguous and anything defined here goes away with the file.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Common.ps1')

function New-PackagesConfigFixture {
    # A synthetic checkout with every case Get-SmartHomePackagesConfig has to separate:
    # a real reference, the build-time copies under bin\ and obj\, a linked worktree
    # inside the checkout, and a sibling directory whose name merely starts the same way.
    param([string]$Name = 'checkout')

    $root = New-TestDirectory -Name $Name

    $paths = @(
        'src\common\Homie\packages.config'
        'src\devices\RoomSensor\packages.config'
        'src\devices\RoomSensor\bin\Debug\packages.config'
        'src\devices\RoomSensor\obj\packages.config'
        '.claude\worktrees\issue-99\src\common\Homie\packages.config'
        '.claude\worktrees\issue-99\src\devices\RoomSensor\packages.config'
        '.claude\worktrees-archive\issue-01\src\common\Homie\packages.config'
    )

    foreach ($relative in $paths) {
        Set-TestFileContent -Path (Join-Path $root $relative) -Content @(
            '<?xml version="1.0" encoding="utf-8"?>'
            '<packages>'
            '  <package id="nanoFramework.CoreLibrary" version="1.17.11" targetFramework="netnano1.0" />'
            '</packages>'
        )
    }

    return $root
}

Describe 'Get-SmartHomePackagesConfig' {
    It 'returns this checkout only: no bin\, no obj\, no linked worktree' {
        $root = New-PackagesConfigFixture
        $found = Get-SmartHomePackagesConfig -RepoRoot $root

        Assert-Equal -Expected 3 -Actual $found.Count -Because 'two real references plus the worktrees-archive one'
        Assert-Equal -Expected 0 -Actual @($found | Where-Object { $_.FullName -match '\\(bin|obj)\\' }).Count
        Assert-Equal -Expected 0 -Actual @($found | Where-Object { $_.FullName -match '\\worktrees\\' }).Count
    }

    It 'keeps a sibling whose name merely starts with the excluded one' {
        # The anchor carries a trailing separator on purpose: '.claude\worktrees-archive'
        # is a different folder, and a bare name-prefix test would sweep it up (issue #68).
        $root = New-PackagesConfigFixture
        $found = Get-SmartHomePackagesConfig -RepoRoot $root

        Assert-Equal -Expected 1 -Actual @($found | Where-Object { $_.FullName -match 'worktrees-archive' }).Count
    }

    It 'answers for the checkout it was asked about, not the one it lives in' {
        # These scripts usually run *from* a worktree, whose own paths all contain
        # '\.claude\worktrees\'. Anchoring at $RepoRoot rather than matching the substring
        # anywhere is what keeps a worktree able to see its own files at all.
        $root = New-PackagesConfigFixture
        $worktree = Join-Path $root '.claude\worktrees\issue-99'

        Assert-Equal -Expected 2 -Actual (Get-SmartHomePackagesConfig -RepoRoot $worktree).Count
    }

    It 'finds the same files under a checkout path containing brackets' {
        # '[' and ']' are legal in a Windows directory name and -Path reads them as
        # character-class syntax, so this returned nothing at all before issue #71.
        $root = New-PackagesConfigFixture -Name 'SmartHome [wip]'

        Assert-Equal -Expected 3 -Actual (Get-SmartHomePackagesConfig -RepoRoot $root).Count
    }

    It 'returns something whose .Count is readable when nothing matched' {
        # The leading comma in the function's return is what makes this true: a plain
        # `return @(...)` unrolls on the way out, no match reaches the caller as $null,
        # and $null.Count throws under Set-StrictMode -- which is how Restore-Packages.ps1
        # failed rather than reporting an empty checkout.
        $empty = New-TestDirectory -Name 'no-configs'
        $found = Get-SmartHomePackagesConfig -RepoRoot $empty

        Assert-NotNull -Value $found
        Assert-Equal -Expected 0 -Actual $found.Count
    }

    It 'propagates -ErrorAction to the enumeration' {
        # Test-Setup.ps1 passes -ErrorAction SilentlyContinue because it would rather
        # report a gap than abort on one; Restore-Packages.ps1 keeps the default and
        # still stops loudly. Both behaviours come from this one pass-through.
        $missing = Join-Path (New-TestDirectory -Name 'gone') 'not-a-directory'

        Assert-Throws -Body { Get-SmartHomePackagesConfig -RepoRoot $missing } -Because 'the default must still abort'
        Assert-Equal -Expected 0 -Actual (Get-SmartHomePackagesConfig -RepoRoot $missing -ErrorAction SilentlyContinue).Count
    }
}

Describe 'Get-SmartHomeReferencedPackage' {
    function New-ReferenceFixture {
        # Written as text rather than built with an XmlDocument on purpose: what the
        # subject has to survive is the *files on disk*, empty <packages> and a mangled
        # <package> included, and a document model would refuse to produce some of them.
        param([string]$Name = 'references')

        $root = New-TestDirectory -Name $Name

        Set-TestFileContent -Path (Join-Path $root 'src\common\Homie\packages.config') -Content @(
            '<?xml version="1.0" encoding="utf-8"?>'
            '<packages>'
            '  <package id="nanoFramework.CoreLibrary" version="1.17.11" targetFramework="netnano1.0" />'
            '  <package id="nanoFramework.M2Mqtt" version="5.1.221" targetFramework="netnano1.0" />'
            '</packages>'
        )

        # Same CoreLibrary reference again, plus one of its own: the repository's 14
        # configs carry 130 references to 29 distinct versions, so the overlap is the
        # normal case rather than an edge one.
        Set-TestFileContent -Path (Join-Path $root 'src\devices\RoomSensor\packages.config') -Content @(
            '<?xml version="1.0" encoding="utf-8"?>'
            '<packages>'
            '  <package id="nanoFramework.CoreLibrary" version="1.17.11" targetFramework="netnano1.0" />'
            '  <package id="nanoFramework.Hardware.Esp32" version="1.6.42" targetFramework="netnano1.0" />'
            '</packages>'
        )

        # A linked worktree's reference, which belongs to another checkout entirely.
        Set-TestFileContent -Path (Join-Path $root '.claude\worktrees\issue-99\src\devices\RoomSensor\packages.config') -Content @(
            '<?xml version="1.0" encoding="utf-8"?>'
            '<packages>'
            '  <package id="nanoFramework.Iot.Device.Ads1115" version="1.2.999" targetFramework="netnano1.0" />'
            '</packages>'
        )

        return $root
    }

    It 'returns one record per distinct reference, across every config in the checkout' {
        $referenced = Get-SmartHomeReferencedPackage -RepoRoot (New-ReferenceFixture)

        Assert-ArrayEqual -Expected @('nanoFramework.CoreLibrary.1.17.11', 'nanoFramework.M2Mqtt.5.1.221', 'nanoFramework.Hardware.Esp32.1.6.42') `
                          -Actual @($referenced | ForEach-Object { $_.Name }) `
                          -Because 'CoreLibrary is referenced twice and must appear once'
    }

    It 'splits each reference into Id, Version and the packages\ path it would be restored to' {
        $root = New-ReferenceFixture
        $referenced = Get-SmartHomeReferencedPackage -RepoRoot $root
        $m2mqtt = @($referenced | Where-Object { $_.Id -eq 'nanoFramework.M2Mqtt' })

        Assert-Equal -Expected 1 -Actual $m2mqtt.Count
        Assert-Equal -Expected '5.1.221' -Actual $m2mqtt[0].Version
        Assert-Equal -Expected 'nanoFramework.M2Mqtt.5.1.221' -Actual $m2mqtt[0].Name
        Assert-Equal -Expected (Join-Path $root 'packages\nanoFramework.M2Mqtt.5.1.221') -Actual $m2mqtt[0].Path
    }

    It 'answers for the checkout it was asked about, not for a worktree inside it' {
        # Inherited from Get-SmartHomePackagesConfig rather than re-implemented, which is
        # the reason both the restore and the preflight go through this one function
        # (issue #68 fixed that asymmetry once; issue #79 stopped it recurring).
        $referenced = Get-SmartHomeReferencedPackage -RepoRoot (New-ReferenceFixture)

        Assert-Equal -Expected 0 -Actual @($referenced | Where-Object { $_.Id -eq 'nanoFramework.Iot.Device.Ads1115' }).Count
    }

    It 'reads a packages.config with no <package> children instead of throwing -- issue #78' {
        # $xml.packages.package throws PropertyNotFoundStrict under Set-StrictMode on this
        # file, which aborted the whole restore without naming it. SelectNodes returns an
        # empty list, which is why every reader of these files now goes through here.
        $root = New-ReferenceFixture -Name 'empty-config'
        Set-TestFileContent -Path (Join-Path $root 'src\devices\OvenControl\packages.config') -Content @(
            '<?xml version="1.0" encoding="utf-8"?>'
            '<packages></packages>'
        )

        Assert-Equal -Expected 3 -Actual (Get-SmartHomeReferencedPackage -RepoRoot $root).Count -Because 'the other three configs must still be read'
    }

    It 'warns about a <package> missing an attribute and keeps the rest of the file' {
        # Skipped rather than thrown on, but never silently: a dropped reference is a
        # package that looks restored to the preflight and to the prune alike.
        $root = New-ReferenceFixture -Name 'mangled-config'
        Set-TestFileContent -Path (Join-Path $root 'src\devices\OvenControl\packages.config') -Content @(
            '<?xml version="1.0" encoding="utf-8"?>'
            '<packages>'
            '  <package id="nanoFramework.Logging" targetFramework="netnano1.0" />'
            '  <package id="nanoFramework.System.Text" version="1.2.54" targetFramework="netnano1.0" />'
            '</packages>'
        )

        $WarningPreference = 'SilentlyContinue'
        $referenced = Get-SmartHomeReferencedPackage -RepoRoot $root
        $names = @($referenced | ForEach-Object { $_.Name })

        Assert-Contains -Item 'nanoFramework.System.Text.1.2.54' -Collection $names
        Assert-Equal -Expected 0 -Actual @($names | Where-Object { $_ -like 'nanoFramework.Logging*' }).Count
    }

    It 'returns something whose .Count is readable when nothing is referenced' {
        $found = Get-SmartHomeReferencedPackage -RepoRoot (New-TestDirectory -Name 'no-references')

        Assert-NotNull -Value $found
        Assert-Equal -Expected 0 -Actual $found.Count
    }

    It 'propagates -ErrorAction to the enumeration underneath it' {
        # Restore-Packages.ps1 keeps the default and aborts; Test-Setup.ps1 would rather
        # report a gap than stop. Both come from the pass-through, which reaches
        # Get-SmartHomePackagesConfig through the scope chain rather than by being forwarded.
        $missing = Join-Path (New-TestDirectory -Name 'gone-references') 'not-a-directory'

        Assert-Throws -Body { Get-SmartHomeReferencedPackage -RepoRoot $missing } -Because 'the default must still abort'
        Assert-Equal -Expected 0 -Actual (Get-SmartHomeReferencedPackage -RepoRoot $missing -ErrorAction SilentlyContinue).Count
    }
}

Describe 'Get-SmartHomeUnreferencedPackageDir' {
    function New-PackagesDirFixture {
        param([string[]]$Folders = @(), [string[]]$Files = @(), [string]$Name = 'packages-dir')

        $root = New-TestDirectory -Name $Name
        $packagesDir = Join-Path $root 'packages'
        New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null

        foreach ($folder in $Folders) {
            New-Item -ItemType Directory -Force -Path (Join-Path $packagesDir $folder) | Out-Null
        }
        foreach ($file in $Files) {
            Set-TestFileContent -Path (Join-Path $packagesDir $file) -Content 'not a package'
        }

        return $packagesDir
    }

    function New-ReferenceRecord {
        param([string[]]$Name)
        return @($Name | ForEach-Object { [pscustomobject]@{ Id = $_; Version = '1.0.0'; Name = $_; Path = "packages\$_" } })
    }

    It 'returns the folders the referenced set does not account for' {
        # The shape issue #79 measured: an old version left behind beside the current one.
        $packagesDir = New-PackagesDirFixture -Folders @('nanoFramework.M2Mqtt.5.1.146', 'nanoFramework.M2Mqtt.5.1.221', 'nanoFramework.CoreLibrary.1.17.11')
        $unreferenced = Get-SmartHomeUnreferencedPackageDir -PackagesDir $packagesDir `
                            -ReferencedPackage (New-ReferenceRecord -Name @('nanoFramework.M2Mqtt.5.1.221', 'nanoFramework.CoreLibrary.1.17.11'))

        Assert-ArrayEqual -Expected @('nanoFramework.M2Mqtt.5.1.146') -Actual @($unreferenced | ForEach-Object { $_.Name })
    }

    It 'returns nothing when every folder is referenced' {
        $packagesDir = New-PackagesDirFixture -Folders @('nanoFramework.CoreLibrary.1.17.11')
        $unreferenced = Get-SmartHomeUnreferencedPackageDir -PackagesDir $packagesDir `
                            -ReferencedPackage (New-ReferenceRecord -Name @('nanoFramework.CoreLibrary.1.17.11'))

        Assert-Equal -Expected 0 -Actual $unreferenced.Count
    }

    It 'matches folder to reference without regard to case' {
        # The cache spells ids lowercase and packages.config spells them mixed; NTFS does
        # not care either. A case-sensitive complement would call a restored package stale
        # and -Prune would then delete it.
        $packagesDir = New-PackagesDirFixture -Folders @('nanoframework.m2mqtt.5.1.221')
        $unreferenced = Get-SmartHomeUnreferencedPackageDir -PackagesDir $packagesDir `
                            -ReferencedPackage (New-ReferenceRecord -Name @('nanoFramework.M2Mqtt.5.1.221'))

        Assert-Equal -Expected 0 -Actual $unreferenced.Count
    }

    It 'leaves loose files in packages\ alone' {
        $packagesDir = New-PackagesDirFixture -Folders @('nanoFramework.CoreLibrary.1.17.11') -Files @('some.nupkg', 'repositories.config')
        $unreferenced = Get-SmartHomeUnreferencedPackageDir -PackagesDir $packagesDir `
                            -ReferencedPackage (New-ReferenceRecord -Name @('nanoFramework.CoreLibrary.1.17.11'))

        Assert-Equal -Expected 0 -Actual $unreferenced.Count -Because 'a .nupkg is not a stale package version'
    }

    It 'calls every folder unreferenced when the referenced set is empty' {
        # True, and the reason Restore-Packages.ps1 refuses to prune on it: the complement
        # of nothing is the whole folder, and a checkout that references nothing is broken
        # rather than clean. Pinned here so that guard cannot be dropped as redundant.
        $packagesDir = New-PackagesDirFixture -Folders @('nanoFramework.CoreLibrary.1.17.11', 'nanoFramework.M2Mqtt.5.1.221')

        Assert-Equal -Expected 2 -Actual (Get-SmartHomeUnreferencedPackageDir -PackagesDir $packagesDir -ReferencedPackage @()).Count
    }

    It 'reports nothing, rather than throwing, when packages\ is not there at all' {
        $absent = Join-Path (New-TestDirectory -Name 'no-packages-dir') 'packages'
        $unreferenced = Get-SmartHomeUnreferencedPackageDir -PackagesDir $absent -ReferencedPackage (New-ReferenceRecord -Name @('nanoFramework.CoreLibrary.1.17.11'))

        Assert-NotNull -Value $unreferenced
        Assert-Equal -Expected 0 -Actual $unreferenced.Count
    }

    It 'works under a checkout path containing brackets' {
        $packagesDir = New-PackagesDirFixture -Folders @('nanoFramework.M2Mqtt.5.1.146') -Name 'packages [wip]'

        Assert-Equal -Expected 1 -Actual (Get-SmartHomeUnreferencedPackageDir -PackagesDir $packagesDir -ReferencedPackage (New-ReferenceRecord -Name @('nanoFramework.M2Mqtt.5.1.221'))).Count
    }
}

Describe 'Get-NanoFrameworkTestAdapterDir' {
    function New-AdapterFixture {
        # -Restored is what is on disk under packages\; -Referenced is what the checkout's
        # own packages.config declares. Separating them is the whole point of issue #79:
        # before it, only the first mattered.
        param(
            [string[]]$Restored = @(),
            [string[]]$Referenced = @(),
            [string]$Name = 'adapter'
        )

        $root = New-TestDirectory -Name $Name

        foreach ($version in $Restored) {
            Set-TestFileContent -Path (Join-Path $root "packages\nanoFramework.TestFramework.$version\build\nanoFramework.TestAdapter.dll") -Content 'not a real dll'
        }

        if ($Referenced.Count -gt 0) {
            Set-TestFileContent -Path (Join-Path $root 'src\tests\Unit\packages.config') -Content @(
                '<?xml version="1.0" encoding="utf-8"?>'
                '<packages>'
                @($Referenced | ForEach-Object { "  <package id=""nanoFramework.TestFramework"" version=""$_"" targetFramework=""netnano1.0"" />" })
                '</packages>'
            )
        }

        return $root
    }

    It 'returns $null when the referenced version has not been restored' {
        $root = New-AdapterFixture -Referenced @('3.0.80')
        Assert-Null -Value (Get-NanoFrameworkTestAdapterDir -RepoRoot $root -WarningAction SilentlyContinue)
    }

    It 'returns $null when nothing in the checkout references the test framework' {
        # Not "pick whatever is there anyway": there is no version to demand, and
        # Run-Tests.ps1 has nothing to run either.
        $root = New-AdapterFixture -Restored @('3.0.80')
        Assert-Null -Value (Get-NanoFrameworkTestAdapterDir -RepoRoot $root -WarningAction SilentlyContinue)
    }

    It 'returns the directory holding the adapter, not the file' {
        $root = New-AdapterFixture -Restored @('3.0.80') -Referenced @('3.0.80')
        $dir = Get-NanoFrameworkTestAdapterDir -RepoRoot $root

        Assert-NotNull -Value $dir
        Assert-True -Condition (Test-Path -LiteralPath (Join-Path $dir 'nanoFramework.TestAdapter.dll'))
    }

    It 'finds the adapter under a checkout path containing brackets' {
        $root = New-AdapterFixture -Restored @('3.0.80') -Referenced @('3.0.80') -Name 'adapter [wip]'
        Assert-NotNull -Value (Get-NanoFrameworkTestAdapterDir -RepoRoot $root)
    }

    It 'takes the referenced version over the one that sorts highest -- issue #79' {
        # The case this issue exists for. Nothing prunes packages\, so a version bump
        # leaves the previous folder behind, and '3.0.9' sorts above '3.0.80' as text --
        # so the old descending-path-sort resolver ran the unit tests against an adapter
        # this checkout does not reference. CLAUDE.md records what that class of mismatch
        # costs: a green vstest run that executed nothing, unnoticed for three commits.
        $root = New-AdapterFixture -Restored @('3.0.9', '3.0.80') -Referenced @('3.0.80')

        Assert-Match -Pattern '(?-i)nanoFramework\.TestFramework\.3\.0\.80\\' -Actual (Get-NanoFrameworkTestAdapterDir -RepoRoot $root)
    }

    It 'refuses to guess when the checkout references two test framework versions' {
        # A real disagreement between projects, and picking one silently is the failure
        # this issue is about. Restoring both does not make it resolvable.
        $root = New-AdapterFixture -Restored @('3.0.9', '3.0.80') -Referenced @('3.0.9', '3.0.80')

        Assert-Null -Value (Get-NanoFrameworkTestAdapterDir -RepoRoot $root -WarningAction SilentlyContinue)
    }

    It 'ignores a restored version nothing references, rather than falling back to it' {
        # The fallback that would make this function useful again is exactly the one that
        # makes it lie: 3.0.9 is present and is the only adapter on disk, and it is still
        # not the adapter this checkout asked for.
        $root = New-AdapterFixture -Restored @('3.0.9') -Referenced @('3.0.80')

        Assert-Null -Value (Get-NanoFrameworkTestAdapterDir -RepoRoot $root -WarningAction SilentlyContinue)
    }
}

Describe 'Get-NfProjectAssemblyName' {
    It 'reads <AssemblyName>, which since the SmartHome.* rename is not the file name' {
        $project = Join-Path (New-TestDirectory -Name 'nfproj') 'RoomSensor.nfproj'
        Set-TestFileContent -Path $project -Content @(
            '<Project>'
            '  <PropertyGroup>'
            '    <AssemblyName>SmartHome.Devices.RoomSensor</AssemblyName>'
            '  </PropertyGroup>'
            '</Project>'
        )

        Assert-Equal -Expected 'SmartHome.Devices.RoomSensor' -Actual (Get-NfProjectAssemblyName -ProjectPath $project)
    }

    It 'tolerates whitespace inside the element' {
        $project = Join-Path (New-TestDirectory -Name 'nfproj-spaced') 'A.nfproj'
        Set-TestFileContent -Path $project -Content '<AssemblyName>  SmartHome.Devices.A  </AssemblyName>'

        Assert-Equal -Expected 'SmartHome.Devices.A' -Actual (Get-NfProjectAssemblyName -ProjectPath $project)
    }

    It 'falls back to the file name when the element is absent' {
        $project = Join-Path (New-TestDirectory -Name 'nfproj-bare') 'Legacy.nfproj'
        Set-TestFileContent -Path $project -Content '<Project />'

        Assert-Equal -Expected 'Legacy' -Actual (Get-NfProjectAssemblyName -ProjectPath $project)
    }

    It 'still fails on a project path containing brackets -- issue #80' {
        # Not a claim that this is right. This function reads its project with
        # Get-Content -Path, which is the same wildcard trap #71 fixed in the two globs
        # above, on ~70 further call sites across the other scripts. Pinned so the fix for
        # #80 has to come past this case rather than round it.
        #
        # No -Pattern, for two reasons. PowerShell's messages are localised on this
        # machine the way MSBuild's are, so matching English text would pass here and fail
        # nowhere useful. And the failure is not the ItemNotFound one might expect: -Raw
        # is a FileSystem *dynamic* parameter, resolved from the provider of the resolved
        # path, so a -Path whose wildcard matches nothing fails at parameter binding
        # ("no parameter found matching Raw") before any file is opened.
        $bracketed = Join-Path (New-TestDirectory -Name 'nfproj [wip]') 'A.nfproj'
        Set-TestFileContent -Path $bracketed -Content '<AssemblyName>SmartHome.Devices.A</AssemblyName>'

        Assert-Throws -Body { Get-NfProjectAssemblyName -ProjectPath $bracketed }

        # Same content, same file name, no brackets: so the bracket is the whole
        # difference, and this is not a fixture that was never written.
        $plain = Join-Path (New-TestDirectory -Name 'nfproj-plain') 'A.nfproj'
        Set-TestFileContent -Path $plain -Content '<AssemblyName>SmartHome.Devices.A</AssemblyName>'
        Assert-Equal -Expected 'SmartHome.Devices.A' -Actual (Get-NfProjectAssemblyName -ProjectPath $plain)
    }
}

Describe 'Get-SmartHomeDevEnvPath' {
    It 'gives every kind its own file, all under temp and all prefixed' {
        $kinds = @('State', 'Config', 'BrokerLog', 'SubscriberLog', 'SubscriberErrorLog', 'Snapshot')
        $paths = @($kinds | ForEach-Object { Get-SmartHomeDevEnvPath -Port '1883' -Kind $_ })

        Assert-Equal -Expected $kinds.Count -Actual @($paths | Sort-Object -Unique).Count -Because 'two kinds sharing a file would have them overwrite each other'
        Assert-Equal -Expected 0 -Actual @($paths | Where-Object { (Split-Path -Leaf $_) -notlike 'smarthome-*' }).Count
    }

    It 'keys the files by port, so two ports can be up at once' {
        Assert-True -Condition ((Get-SmartHomeDevEnvPath -Port '1883' -Kind State) -ne (Get-SmartHomeDevEnvPath -Port '1884' -Kind State))
    }

    It 'rejects a kind that is not in the vocabulary' {
        # The orphan scan recognises leftovers by these same names, so a kind minted at a
        # call site would be invisible to it.
        Assert-Throws -Body { Get-SmartHomeDevEnvPath -Port '1883' -Kind 'Whatever' }
    }
}

Describe 'Get-SmartHomeSubscriberArguments' {
    It 'asks for the retain flag, which -v cannot print' {
        $arguments = Get-SmartHomeSubscriberArguments -Port '1883'

        Assert-Contains -Item '-F' -Collection $arguments
        Assert-Contains -Item '%t %r %p' -Collection $arguments
    }

    It 'dials 127.0.0.1 rather than localhost' {
        # Measured, not stylistic: localhost resolves to ::1 first, the broker binds
        # 0.0.0.0, and every client then waits out an IPv6 connect timeout -- 2.03s
        # against 0.02s on the first message.
        Assert-Contains -Item '127.0.0.1' -Collection (Get-SmartHomeSubscriberArguments -Port '1883')
        Assert-Equal -Expected 0 -Actual @(Get-SmartHomeSubscriberArguments -Port '1883' | Where-Object { $_ -eq 'localhost' }).Count
    }

    It 'passes the port through' {
        Assert-Contains -Item '1884' -Collection (Get-SmartHomeSubscriberArguments -Port '1884')
    }
}

Describe 'ConvertTo-SmartHomeHashtable' {
    It 'turns a PSCustomObject into a hashtable whose missing keys read as $null' {
        # The whole reason it exists: ConvertFrom-Json hands back PSCustomObjects, and
        # under Set-StrictMode reading a property that is not there throws.
        $converted = ConvertTo-SmartHomeHashtable -InputObject ('{"Label":"broker","Id":42}' | ConvertFrom-Json)

        Assert-Equal -Expected 'broker' -Actual $converted['Label']
        Assert-Equal -Expected 42 -Actual $converted['Id']
        Assert-Null -Value $converted['NotThere']
    }

    It 'converts nested objects and arrays of them' {
        $json = '{"Processes":[{"Label":"broker","Tree":true},{"Label":"subscriber","Tree":false}]}'
        $converted = ConvertTo-SmartHomeHashtable -InputObject ($json | ConvertFrom-Json)

        Assert-Equal -Expected 2 -Actual @($converted['Processes']).Count
        Assert-Equal -Expected 'subscriber' -Actual $converted['Processes'][1]['Label']
        Assert-Null -Value $converted['Processes'][0]['Missing']
    }

    It 'passes $null, scalars and an existing hashtable straight through' {
        Assert-Null -Value (ConvertTo-SmartHomeHashtable -InputObject $null)
        Assert-Equal -Expected 'x' -Actual (ConvertTo-SmartHomeHashtable -InputObject 'x')

        $already = @{ Label = 'broker' }
        Assert-Equal -Expected 'broker' -Actual (ConvertTo-SmartHomeHashtable -InputObject $already)['Label']
    }
}

Describe 'Dev-env state' {
    # A port no dev environment would ever be started on, so these cases cannot collide
    # with a broker the developer has running. The files land in the real temp directory,
    # because that is where the functions under test put them, and are cleared again here.
    $statePort = '65999'

    It 'reports nothing running when no state file exists' {
        Clear-SmartHomeDevEnvState -Port $statePort
        Assert-Null -Value (Get-SmartHomeDevEnvState -Port $statePort)
    }

    It 'round-trips a recorded process list' {
        Save-SmartHomeDevEnvState -Port $statePort -State @{
            Port      = $statePort
            Processes = @(
                @{ Label = 'broker'; Id = 111; Name = 'mosquitto'; StartTime = '2026-09-01T10:00:00.0000000Z'; Tree = $false }
                @{ Label = 'subscriber'; Id = 222; Name = 'cmd'; StartTime = '2026-09-01T10:00:01.0000000Z'; Tree = $true }
            )
        }

        try {
            $state = Get-SmartHomeDevEnvState -Port $statePort

            Assert-NotNull -Value $state
            Assert-Equal -Expected 2 -Actual @($state['Processes']).Count
            Assert-Equal -Expected 'mosquitto' -Actual $state['Processes'][0]['Name']
            Assert-True -Condition $state['Processes'][1]['Tree']
            Assert-Null -Value $state['NotRecorded'] -Because 'a missing key must read as $null, not throw'
        }
        finally {
            Clear-SmartHomeDevEnvState -Port $statePort
        }
    }

    It 'drops an unreadable state file rather than wedging the teardown' {
        # Stop-DevEnv.ps1 has to stay callable unconditionally, so a truncated file
        # (Ctrl+C mid-write, a reboot) reports "nothing running" and removes itself.
        $stateFile = Get-SmartHomeDevEnvPath -Port $statePort -Kind State
        Set-TestFileContent -Path $stateFile -Content '{"Processes":[{"Label":'

        $WarningPreference = 'SilentlyContinue'
        try {
            Assert-Null -Value (Get-SmartHomeDevEnvState -Port $statePort)
            Assert-False -Condition (Test-Path -LiteralPath $stateFile) -Because 'the corrupt file must not be read again on the next call'
        }
        finally {
            Clear-SmartHomeDevEnvState -Port $statePort
        }
    }

    It 'New-SmartHomeProcessRecord records name and start time next to the pid' {
        # Windows recycles pids: a state file left by a crash can name a pid that now
        # belongs to something else, and stopping that is worse than leaking.
        $record = New-SmartHomeProcessRecord -Label 'broker' -Process (Get-Process -Id $PID) -Tree

        Assert-Equal -Expected 'broker' -Actual $record['Label']
        Assert-Equal -Expected $PID -Actual $record['Id']
        Assert-NotNull -Value $record['Name']
        Assert-Match -Pattern '^\d{4}-\d{2}-\d{2}T' -Actual $record['StartTime'] -Because 'round-trip format, so it compares exactly'
        Assert-True -Condition $record['Tree']
    }
}

Describe 'Deploy state' {
    # The record Deploy-ToDevice.ps1 pads from. Its contract is "the number of bytes from
    # the deploy address the next flash must overwrite", and every rejection below costs
    # one full-size deploy -- the behaviour the script had before the record existed --
    # so the safe direction is $null. A synthetic port, for the reason above.
    $comPort = 'COM-TEST-74'

    function Save-RawDeployState {
        param([hashtable]$State)
        $State | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Get-SmartHomeDeployStatePath -ComPort $comPort) -Encoding utf8
    }

    It 'builds a path that cannot escape the temp directory' {
        # The port comes from local.env.ps1, so something like \\.\COM10 would otherwise
        # point the record somewhere else entirely.
        $path = Get-SmartHomeDeployStatePath -ComPort '\\.\COM10'

        # '\\.\COM10' scrubs to '__._COM10': both backslashes and the leading one before
        # COM10 become underscores, and the dot survives because it is legal in a name.
        Assert-Equal -Expected ([System.IO.Path]::GetTempPath().TrimEnd('\')) -Actual (Split-Path -Parent $path)
        Assert-Equal -Expected 'smarthome-deploy-__._COM10.json' -Actual (Split-Path -Leaf $path)
    }

    It 'keeps * unscrubbed, because the all-ports glob is built from this one rule' {
        Assert-Match -Pattern '\*' -Actual (Get-SmartHomeDeployStatePath -ComPort '*')
    }

    It 'round-trips a saved record' {
        Save-SmartHomeDeployState -ComPort $comPort -DeployAddress '0x1B000' -StaleBytes 409600 -Image 'RoomSensor.bin'

        try {
            $state = Get-SmartHomeDeployState -ComPort $comPort

            Assert-NotNull -Value $state
            Assert-Equal -Expected '0x1B000' -Actual $state['DeployAddress']
            Assert-Equal -Expected 409600 -Actual $state['StaleBytes']
            Assert-Equal -Expected 'RoomSensor.bin' -Actual $state['Image']
        }
        finally {
            Clear-SmartHomeDeployState -ComPort $comPort
        }
    }

    It 'returns $null when there is no record' {
        Clear-SmartHomeDeployState -ComPort $comPort
        Assert-Null -Value (Get-SmartHomeDeployState -ComPort $comPort)
    }

    It 'rejects a record from another schema version' {
        Save-RawDeployState -State @{ Version = 99; ComPort = $comPort; DeployAddress = '0x1B000'; StaleBytes = 4096 }
        try { Assert-Null -Value (Get-SmartHomeDeployState -ComPort $comPort) }
        finally { Clear-SmartHomeDeployState -ComPort $comPort }
    }

    It 'rejects a record written for a different port' {
        Save-RawDeployState -State @{ Version = 1; ComPort = 'COM9'; DeployAddress = '0x1B000'; StaleBytes = 4096 }
        try { Assert-Null -Value (Get-SmartHomeDeployState -ComPort $comPort) }
        finally { Clear-SmartHomeDeployState -ComPort $comPort }
    }

    It 'rejects a record with no deploy address' {
        # The record only vouches for a footprint at the address it was written for.
        Save-RawDeployState -State @{ Version = 1; ComPort = $comPort; DeployAddress = '   '; StaleBytes = 4096 }
        try { Assert-Null -Value (Get-SmartHomeDeployState -ComPort $comPort) }
        finally { Clear-SmartHomeDeployState -ComPort $comPort }
    }

    It 'rejects a negative or unparseable StaleBytes' {
        foreach ($bad in @(-1, 'lots')) {
            Save-RawDeployState -State @{ Version = 1; ComPort = $comPort; DeployAddress = '0x1B000'; StaleBytes = $bad }
            try { Assert-Null -Value (Get-SmartHomeDeployState -ComPort $comPort) -Because "StaleBytes = $bad" }
            finally { Clear-SmartHomeDeployState -ComPort $comPort }
        }
    }

    It 'hands back StaleBytes as an int the caller can do arithmetic with' {
        # ConvertFrom-Json returns Int32, Int64 or Double depending on the literal, and
        # the padding decision multiplies and rounds this.
        Save-RawDeployState -State @{ Version = 1; ComPort = $comPort; DeployAddress = '0x1B000'; StaleBytes = 4096.0 }

        try {
            $state = Get-SmartHomeDeployState -ComPort $comPort

            Assert-NotNull -Value $state
            Assert-True -Condition ($state['StaleBytes'] -is [int])
            Assert-Equal -Expected 8192 -Actual ($state['StaleBytes'] * 2)
        }
        finally {
            Clear-SmartHomeDeployState -ComPort $comPort
        }
    }

    It 'ignores an unreadable record instead of throwing' {
        Set-TestFileContent -Path (Get-SmartHomeDeployStatePath -ComPort $comPort) -Content '{ not json'

        $WarningPreference = 'SilentlyContinue'
        try { Assert-Null -Value (Get-SmartHomeDeployState -ComPort $comPort) }
        finally { Clear-SmartHomeDeployState -ComPort $comPort }
    }

    It 'clearing a record that is not there is a no-op, not an error' {
        Clear-SmartHomeDeployState -ComPort $comPort
        Clear-SmartHomeDeployState -ComPort $comPort
        Assert-Null -Value (Get-SmartHomeDeployState -ComPort $comPort)
    }

    # Not covered here: Clear-SmartHomeDeployState with no -ComPort. It globs every
    # record in the real temp directory, which on a developer's machine includes the one
    # for the device actually attached -- clearing it would silently cost that machine's
    # next deploy its tight pad. The directory is hardcoded inside the function, so there
    # is no way to point that branch at a fixture without changing it.
}
