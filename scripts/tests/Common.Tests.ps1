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

        Assert-Equal -Expected 3 -Actual (Get-SmartHomeReferencedPackage -RepoRoot $root).Count `
                     -Because 'the three references in the two configs beside it must still be read'
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

    It 'reads the config list a caller hands it instead of globbing again' {
        # Test-Setup.ps1 needs the file count as well as the references, and one recursive
        # pass over the main checkout is ~2.4s. Proved by handing over *one* of the two
        # in-checkout configs: a function that ignored -Config would still answer 3.
        $root = New-ReferenceFixture -Name 'handed-configs'
        $one = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\common\Homie') -Filter 'packages.config' -File)

        Assert-ArrayEqual -Expected @('nanoFramework.CoreLibrary.1.17.11', 'nanoFramework.M2Mqtt.5.1.221') `
                          -Actual @(Get-SmartHomeReferencedPackage -RepoRoot $root -Config $one | ForEach-Object { $_.Name })
    }

    It 'globs for itself when -Config is omitted, and reads an empty list as empty' {
        # The two halves of the default, so neither can quietly become the other: $null
        # means "go and look", an empty array means "there was nothing".
        $root = New-ReferenceFixture -Name 'config-default'

        Assert-Equal -Expected 3 -Actual (Get-SmartHomeReferencedPackage -RepoRoot $root).Count
        Assert-Equal -Expected 0 -Actual (Get-SmartHomeReferencedPackage -RepoRoot $root -Config @()).Count
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

    It 'resolves from a referenced list a caller hands it, without touching packages.config' {
        # Test-Setup.ps1 passes the list it already read under -ErrorAction SilentlyContinue,
        # so an unreadable subtree costs that preflight one row rather than the whole table.
        # The fixture deliberately has NO packages.config at all: a function that globbed
        # for itself would find no reference and return $null.
        $root = New-AdapterFixture -Restored @('3.0.9', '3.0.80') -Name 'handed-references'
        $handed = @([pscustomobject]@{
            Id      = 'nanoFramework.TestFramework'
            Version = '3.0.9'
            Name    = 'nanoFramework.TestFramework.3.0.9'
            Path    = (Join-Path $root 'packages\nanoFramework.TestFramework.3.0.9')
        })

        Assert-Match -Pattern '(?-i)nanoFramework\.TestFramework\.3\.0\.9\\' `
                     -Actual (Get-NanoFrameworkTestAdapterDir -RepoRoot $root -ReferencedPackage $handed)
    }

    It 'reads a handed-over empty list as "nothing references it", not as "go and look"' {
        $root = New-AdapterFixture -Restored @('3.0.80') -Referenced @('3.0.80') -Name 'handed-nothing'

        Assert-Null -Value (Get-NanoFrameworkTestAdapterDir -RepoRoot $root -ReferencedPackage @() -WarningAction SilentlyContinue)
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

Describe 'Get-SmartHomeSubscriberArgumentString' {
    It 'renders the argument list cmd.exe expects, quoting only what needs it' {
        # The whole string, not a property of it. This rendering used to live character for
        # character in both Start-DevEnv.ps1 and Run-IntegrationTests.ps1 with nothing but a
        # comment reconciling them (issue #85), so what is worth pinning is the exact text
        # both of them now hand to cmd.exe.
        Assert-Equal -Expected '-h 127.0.0.1 -p 1883 -t "homie/#" -F "%t %r %p"' `
                     -Actual (Get-SmartHomeSubscriberArgumentString -Port '1883')
    }

    It 'quotes every value carrying whitespace, a slash or a #, and no others' {
        # Derived from the argument list rather than from the expectation above, so this
        # case still holds if the list grows: it says which values the quoting rule picks,
        # not what today's list happens to contain.
        $rendered = Get-SmartHomeSubscriberArgumentString -Port '1883'

        foreach ($argument in Get-SmartHomeSubscriberArguments -Port '1883') {
            $expected = if ($argument -match '[\s/#]') { '"{0}"' -f $argument } else { $argument }
            Assert-True -Condition ($rendered.Contains($expected)) `
                        -Because "'$argument' should render as '$expected', in '$rendered'"
        }
    }

    It 'keeps the order the argument list is in' {
        # -F and its format string are a pair, and so are -t and the topic. A rendering that
        # sorted or dropped a value would leave mosquitto_sub reading a flag's value as a
        # flag -- and the capture parser depends on that '%t %r %p' reaching it intact.
        $arguments = @(Get-SmartHomeSubscriberArguments -Port '1883')
        $rendered = Get-SmartHomeSubscriberArgumentString -Port '1883'

        $position = -1
        foreach ($argument in $arguments) {
            $next = $rendered.IndexOf($argument, $position + 1)
            Assert-True -Condition ($next -gt $position) -Because "'$argument' must appear after the one before it, in '$rendered'"
            $position = $next
        }
    }

    It 'passes the port through' {
        Assert-Match -Pattern '-p 1884( |$)' -Actual (Get-SmartHomeSubscriberArgumentString -Port '1884')
    }
}

Describe 'Get-SmartHomeMosquittoTool' {
    It 'returns the tool inside the directory it was given' {
        $dir = New-TestDirectory -Name 'mosquitto'
        Set-TestFileContent -Path (Join-Path $dir 'mosquitto_sub.exe') -Content 'not really an executable'

        Assert-Equal -Expected (Join-Path $dir 'mosquitto_sub.exe') `
                     -Actual (Get-SmartHomeMosquittoTool -Name 'mosquitto_sub.exe' -Directory $dir)
    }

    It 'reads SMARTHOME_MOSQUITTO_DIR when no directory is given' {
        $dir = New-TestDirectory -Name 'mosquitto-from-env'
        Set-TestFileContent -Path (Join-Path $dir 'mosquitto.exe') -Content 'not really an executable'

        $previous = [Environment]::GetEnvironmentVariable('SMARTHOME_MOSQUITTO_DIR')
        try {
            [Environment]::SetEnvironmentVariable('SMARTHOME_MOSQUITTO_DIR', $dir)
            Assert-Equal -Expected (Join-Path $dir 'mosquitto.exe') `
                         -Actual (Get-SmartHomeMosquittoTool -Name 'mosquitto.exe')
        }
        finally {
            [Environment]::SetEnvironmentVariable('SMARTHOME_MOSQUITTO_DIR', $previous)
        }
    }

    It 'lets an explicit directory beat the environment' {
        # The seam that makes every case here independent of this machine's local.env.ps1.
        # Without it the cases above would pass or fail on how Mosquitto happens to be
        # installed here, which is the opposite of what a desk suite is for.
        $wanted = New-TestDirectory -Name 'mosquitto-wanted'
        $ignored = New-TestDirectory -Name 'mosquitto-ignored'
        Set-TestFileContent -Path (Join-Path $wanted 'mosquitto.exe') -Content 'not really an executable'
        Set-TestFileContent -Path (Join-Path $ignored 'mosquitto.exe') -Content 'not really an executable'

        $previous = [Environment]::GetEnvironmentVariable('SMARTHOME_MOSQUITTO_DIR')
        try {
            [Environment]::SetEnvironmentVariable('SMARTHOME_MOSQUITTO_DIR', $ignored)
            Assert-Equal -Expected (Join-Path $wanted 'mosquitto.exe') `
                         -Actual (Get-SmartHomeMosquittoTool -Name 'mosquitto.exe' -Directory $wanted)
        }
        finally {
            [Environment]::SetEnvironmentVariable('SMARTHOME_MOSQUITTO_DIR', $previous)
        }
    }

    It 'names the missing path and SMARTHOME_MOSQUITTO_DIR when the tool is not there' {
        # The remediation is the point of the guard. Without it a wrong directory reached
        # the call site as a raw CommandNotFoundException, or -- for the conformance
        # snapshot -- as an empty read reported as a FAIL against the device.
        $dir = New-TestDirectory -Name 'mosquitto-empty'

        $message = Assert-Throws -Body { Get-SmartHomeMosquittoTool -Name 'mosquitto.exe' -Directory $dir }
        Assert-Match -Pattern 'Not found' -Actual $message
        Assert-Match -Pattern 'SMARTHOME_MOSQUITTO_DIR in local\.env\.ps1' -Actual $message
        Assert-Match -Pattern ([regex]::Escape((Join-Path $dir 'mosquitto.exe'))) -Actual $message
    }

    It 'stops the calling script rather than returning a path that is not there' {
        # In a child process because that is the only place the claim can be made: both
        # callers treat the return value as a runnable path, so a missing tool has to end
        # the run, and "ends the run" means a non-zero exit code and nothing after the call.
        #
        # Note which mechanism actually stops it. Common.ps1 sets $ErrorActionPreference to
        # Stop at file scope, so the Write-Error terminates before the exit 1 below it is
        # reached; the exit is what a caller that relaxed the preference would fall back on.
        # Both land on exit code 1, which is what this asserts.
        $probeDir = New-TestDirectory -Name 'mosquitto-exit'
        $probe = Join-Path $probeDir 'probe.ps1'
        $common = Join-Path (Split-Path -Parent $PSScriptRoot) 'Common.ps1'
        Set-TestFileContent -Path $probe -Content @(
            'Set-StrictMode -Version Latest'
            '$ErrorActionPreference = ''Stop'''
            ". '$common'"
            "Get-SmartHomeMosquittoTool -Name 'mosquitto.exe' -Directory '$probeDir' | Out-Null"
            'Write-Output ''REACHED-AFTER'''
        )

        $stdout = Join-Path $probeDir 'out.txt'
        $stderr = Join-Path $probeDir 'err.txt'
        $powershell = (Get-Process -Id $PID).Path
        $process = Start-Process -FilePath $powershell `
                                 -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $probe) `
                                 -NoNewWindow -Wait -PassThru `
                                 -RedirectStandardOutput $stdout -RedirectStandardError $stderr

        Assert-Equal -Expected 1 -Actual $process.ExitCode
        Assert-Equal -Expected '' -Actual ((Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue) -join '') `
                     -Because 'nothing after the guard may run'
        Assert-Match -Pattern 'SMARTHOME_MOSQUITTO_DIR' -Actual ((Get-Content -LiteralPath $stderr) -join ' ')
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

Describe 'Deployment geometry' {
    # The one desk-provable piece of the deployment-area handling. Everything around it
    # -- the erase itself, whether the device answers at all -- needs a device; this is
    # the seam that was split out so at least the parsing can be pinned where CI runs.
    #
    # What it parses is DeviceDebugMonitor's --erase-deployment output. Deploy-ToDevice.ps1
    # compares Start against its own -DeployAddress and refuses the flash when they
    # disagree, so a parse that quietly produced the wrong number would be worse than one
    # that produced none: it would stop a healthy deploy, or wave through the wrong-address
    # flash the check exists to catch.

    It 'reads the start and length out of the monitor line' {
        $geometry = Read-SmartHomeDeploymentGeometry -Output @(
            'Watching for a nanoFramework device on COM3...'
            'Found device: ESP32. Connecting debug engine...'
            'DEPLOYMENT start=0x001E0000 length=1835008'
            'DEPLOYMENT blank-past=143360'
        )

        Assert-NotNull -Value $geometry
        Assert-Equal -Expected 1966080 -Actual $geometry['Start'] -Because '0x1E0000'
        Assert-Equal -Expected 1835008 -Actual $geometry['Length']
    }

    It 'hands back numbers the caller can do arithmetic with' {
        # Deploy-ToDevice.ps1 compares these against [Convert]::ToInt64($DeployAddress, 16)
        # and against a byte count, so a string that merely prints the same would compare
        # unequal and refuse every deploy.
        $geometry = Read-SmartHomeDeploymentGeometry -Output @('DEPLOYMENT start=0x1E0000 length=1835008')

        Assert-True -Condition ($geometry['Start'] -is [long])
        Assert-True -Condition ($geometry['Length'] -is [long])
        Assert-Equal -Expected 3801088 -Actual ($geometry['Start'] + $geometry['Length'])
    }

    It 'takes the address with or without leading zeroes, in either case' {
        foreach ($rendering in '0x1E0000', '0x001e0000', '0X001E0000') {
            $geometry = Read-SmartHomeDeploymentGeometry -Output @("DEPLOYMENT start=$rendering length=1835008")

            Assert-NotNull -Value $geometry -Because $rendering
            Assert-Equal -Expected 1966080 -Actual $geometry['Start'] -Because $rendering
        }
    }

    It 'returns $null when the line is not there' {
        # The monitor prints this before it does anything that can fail, so its absence
        # means the device never answered -- and the caller already has an exit code
        # saying so. Failing here instead would turn one clear failure into two.
        Assert-Null -Value (Read-SmartHomeDeploymentGeometry -Output @(
            'Watching for a nanoFramework device on COM3...'
            'No nanoFramework device found on COM3 after 15s.'
        ))
    }

    It 'returns $null for no output at all' {
        Assert-Null -Value (Read-SmartHomeDeploymentGeometry -Output @())
        Assert-Null -Value (Read-SmartHomeDeploymentGeometry -Output $null)
    }

    It 'ignores a line that only looks like the geometry line' {
        foreach ($bad in @(
            'DEPLOYMENT start=0x1E0000'                       # no length
            'DEPLOYMENT length=1835008'                       # no start
            'DEPLOYMENT start=1E0000 length=1835008'          # no 0x, so not the format
            'DEPLOYMENT start=0xZZZZ length=1835008'          # not hex
            'DEPLOYMENT start=0x1E0000 length=nonsense'
            'DEPLOYMENT start=0x1E0000 length=0'              # a partition of no size
            'the DEPLOYMENT start=0x1E0000 length=1835008'    # prose that quotes the line
        )) {
            Assert-Null -Value (Read-SmartHomeDeploymentGeometry -Output @($bad)) -Because $bad
        }
    }

    It 'takes the first geometry line and ignores the rest' {
        # One call, one device, one partition. A second line would mean the monitor
        # printed something this parser does not understand, and guessing which of two
        # answers is right is worse than taking the one the tool documents itself as
        # printing before anything else.
        $geometry = Read-SmartHomeDeploymentGeometry -Output @(
            'DEPLOYMENT start=0x1E0000 length=1835008'
            'DEPLOYMENT start=0x1B0000 length=409600'
        )

        Assert-Equal -Expected 1966080 -Actual $geometry['Start']
    }

    It 'skips blank lines rather than tripping over them' {
        # dotnet run interleaves its own blank lines with the tool's output.
        $geometry = Read-SmartHomeDeploymentGeometry -Output @('', '   ', $null, 'DEPLOYMENT start=0x1E0000 length=1835008')

        Assert-NotNull -Value $geometry
    }
}

Describe 'Device monitor project' {
    It 'resolves the monitor project inside this checkout' {
        # Not a fixture: the point of the check is that the real project is where the
        # deploy and the debug capture both expect it, in whichever worktree this is.
        $projectPath = Get-SmartHomeDeviceMonitorProject

        Assert-True -Condition (Test-Path -LiteralPath $projectPath)
        Assert-Equal -Expected 'DeviceDebugMonitor.csproj' -Actual (Split-Path -Leaf $projectPath)
        Assert-Equal -Expected (Get-SmartHomeRepoRoot) -Actual (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $projectPath)))
    }
}
