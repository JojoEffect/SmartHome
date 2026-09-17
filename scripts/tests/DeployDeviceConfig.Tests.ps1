# scripts\Deploy-DeviceConfig.ps1 -- the half that decides what gets refused.
#
# Everything this file exercises is host-side: reading a manifest, deciding it cannot be
# deployed, resolving it against a checkout, and reading nanoff's output afterwards. The
# device half is one nanoff invocation, and there is nothing at a desk that can stand in
# for it.
#
# The output-reading cases are the ones that matter most. nanoff exits 0 when an
# individual file fails to upload (FileDeploymentManager.DeployAsync in the pinned
# nanoFirmwareFlasher checkout returns ExitCodes.OK regardless, and its README says the
# deployment "will continue and an error will be displayed"), so the exit code alone
# cannot tell a successful deployment from an empty one.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsDir = Split-Path -Parent $PSScriptRoot
$subject = Join-Path $scriptsDir 'Deploy-DeviceConfig.ps1'

. $subject

function New-ManifestFixture {
    # A checkout-shaped fixture: a repo root with config\ in it, the payload written, and
    # the manifest text handed back rather than written, so a case can mangle it.
    param(
        [string]$Payload = '{ "DeviceId": "room-sensor-office" }',

        [string]$PayloadName = 'room-sensor.json',

        [string]$Name = 'manifest'
    )

    $root = New-TestDirectory -Name $Name
    Set-TestFileContent -Path (Join-Path $root "config\$PayloadName") -Content $Payload
    return $root
}

$validManifest = @'
{
  "Files": [
    {
      "DestinationFilePath": "I:\\configuration.json",
      "SourceFilePath": "config/room-sensor.json"
    }
  ]
}
'@

Describe 'The dot-source guard' {
    It 'defines the functions without reading the environment' {
        # In a child process: this file has already dot-sourced the subject, so the
        # interesting claim is about what a *fresh* dot-source does.
        $probe = Join-Path (New-TestDirectory -Name 'guard') 'probe.ps1'
        Set-TestFileContent -Path $probe -Content @(
            'Set-StrictMode -Version Latest'
            '$ErrorActionPreference = ''Stop'''
            ". '$subject'"
            '$functions = @(Get-Command -CommandType Function | Where-Object { $_.ScriptBlock.File -eq ' + "'$subject'" + ' }).Count'
            'Write-Output ("functions={0}" -f $functions)'
            'Write-Output ("repoRootDefined={0}" -f [bool](Get-Variable -Name repoRoot -ErrorAction SilentlyContinue))'
        )

        $host_ = (Get-Process -Id $PID).Path
        $output = & $host_ -NoProfile -ExecutionPolicy Bypass -File $probe
        Assert-Equal -Expected 0 -Actual $LASTEXITCODE -Because 'a dot-source must not fail'

        $report = @{}
        foreach ($line in $output) {
            $parts = $line -split '=', 2
            $report[$parts[0]] = $parts[1]
        }

        Assert-Equal -Expected '5' -Actual $report['functions']

        # $repoRoot is assigned below the guard, so its absence is what proves nothing
        # below the guard ran -- including Import-SmartHomeLocalEnv, which would exit.
        Assert-Equal -Expected 'False' -Actual $report['repoRootDefined']
    }
}

Describe 'Get-DeviceConfigManifestError' {
    It 'accepts a manifest whose file is there and parses' {
        $root = New-ManifestFixture
        Assert-Null -Value (Get-DeviceConfigManifestError -Json $validManifest -Source 'deploy.json' -RepoRoot $root)
    }

    It 'rejects an empty manifest' {
        $root = New-ManifestFixture
        Assert-Match -Actual (Get-DeviceConfigManifestError -Json '' -Source 'deploy.json' -RepoRoot $root) -Pattern 'empty'
    }

    It 'rejects a manifest that is not JSON' {
        $root = New-ManifestFixture
        $error_ = Get-DeviceConfigManifestError -Json '{ "Files": [ ' -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'not valid JSON'
    }

    It 'rejects a manifest with no Files array' {
        $root = New-ManifestFixture
        $error_ = Get-DeviceConfigManifestError -Json '{ "Something": 1 }' -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern "no 'Files' array"
    }

    It 'rejects an empty Files array' {
        $root = New-ManifestFixture
        $error_ = Get-DeviceConfigManifestError -Json '{ "Files": [] }' -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'empty'
    }

    It 'rejects a committed SerialPort, naming local.env.ps1' {
        # The whole reason the resolved copy exists: a COM port is a per-machine value and
        # the manifest is version-controlled.
        $root = New-ManifestFixture
        $json = $validManifest -replace '\{\s*"Files"', '{ "SerialPort": "COM9", "Files"'
        $error_ = Get-DeviceConfigManifestError -Json $json -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'SMARTHOME_COM_PORT'
    }

    It 'rejects a destination that is not a device path' {
        $root = New-ManifestFixture
        $json = $validManifest -replace 'I:\\\\configuration\.json', 'configuration.json'
        $error_ = Get-DeviceConfigManifestError -Json $json -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'rooted on a drive'
    }

    It 'rejects an entry with no SourceFilePath rather than deleting a device file' {
        # nanoff reads an absent source as "delete this from the device". A manifest whose
        # name says deploy should not be able to do that by omission.
        $root = New-ManifestFixture
        $json = @'
{ "Files": [ { "DestinationFilePath": "I:\\configuration.json" } ] }
'@
        $error_ = Get-DeviceConfigManifestError -Json $json -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'does not delete them'
    }

    It 'rejects an absolute SourceFilePath' {
        $root = New-ManifestFixture
        $json = $validManifest -replace 'config/room-sensor\.json', 'C:/somewhere/room-sensor.json'
        $error_ = Get-DeviceConfigManifestError -Json $json -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'relative to the repository root'
    }

    It 'rejects a source file that is not there' {
        $root = New-ManifestFixture -PayloadName 'something-else.json'
        $error_ = Get-DeviceConfigManifestError -Json $validManifest -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'names a file that is not there'
    }

    It 'rejects a payload that does not parse, before it reaches the device' {
        # The case #37's commissioning loop will meet: a value edited at the tank, a comma
        # left behind. Caught at the desk rather than by a device alerting an hour later.
        $root = New-ManifestFixture -Payload '{ "DeviceId": "room-sensor-office", }'
        $error_ = Get-DeviceConfigManifestError -Json $validManifest -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'not valid JSON'
    }

    It 'rejects an empty payload' {
        $root = New-ManifestFixture -Payload ''
        $error_ = Get-DeviceConfigManifestError -Json $validManifest -Source 'deploy.json' -RepoRoot $root
        Assert-Match -Actual $error_ -Pattern 'is empty'
    }

    It 'accepts a repository path containing brackets' {
        # Issue #71's shape: '[' and ']' are legal in a Windows directory name and -Path
        # reads them as character-class syntax, so a checkout at 'SmartHome [wip]' tested
        # every file as absent.
        $root = New-ManifestFixture -Name 'repo [wip]'
        Assert-Null -Value (Get-DeviceConfigManifestError -Json $validManifest -Source 'deploy.json' -RepoRoot $root)
    }

    It 'leaves a non-json payload unparsed' {
        # A certificate or a key is a legitimate thing to deploy, and it is not JSON.
        $root = New-TestDirectory -Name 'blob'
        Set-TestFileContent -Path (Join-Path $root 'config\device.pem') -Content 'not json at all'
        $json = $validManifest -replace 'config/room-sensor\.json', 'config/device.pem'
        Assert-Null -Value (Get-DeviceConfigManifestError -Json $json -Source 'deploy.json' -RepoRoot $root)
    }
}

Describe 'ConvertTo-DeviceConfigDeployment' {
    It 'fills in the port and makes every source absolute' {
        $root = New-ManifestFixture
        $deployment = ConvertTo-DeviceConfigDeployment -Json $validManifest -RepoRoot $root -ComPort 'COM7'

        Assert-Equal -Expected 'COM7' -Actual $deployment['SerialPort']

        $files = @($deployment['Files'])
        Assert-Equal -Expected 1 -Actual $files.Count
        Assert-Equal -Expected 'I:\configuration.json' -Actual $files[0]['DestinationFilePath']
        Assert-Equal -Expected ([System.IO.Path]::GetFullPath((Join-Path $root 'config\room-sensor.json'))) `
                     -Actual $files[0]['SourceFilePath']
    }

    It 'survives round-tripping through ConvertTo-Json, which is what nanoff reads' {
        # -Depth 5 at the call site: at ConvertTo-Json's default of 2 the Files entries
        # come out as type names rather than as objects, and nanoff would deploy nothing.
        $root = New-ManifestFixture
        $deployment = ConvertTo-DeviceConfigDeployment -Json $validManifest -RepoRoot $root -ComPort 'COM7'
        $rendered = $deployment | ConvertTo-Json -Depth 5 | ConvertFrom-Json

        Assert-Equal -Expected 'COM7' -Actual $rendered.SerialPort
        Assert-Equal -Expected 'I:\configuration.json' -Actual @($rendered.Files)[0].DestinationFilePath
        Assert-True -Condition (Test-Path -LiteralPath @($rendered.Files)[0].SourceFilePath) `
                    -Because 'the resolved source must still point at the payload'
    }
}

Describe 'Get-FileDeploymentFailure' {
    $okLine = 'Deploying file C:\repo\config\room-sensor.json to I:\configuration.json...OK'

    It 'passes a run that deployed every file' {
        Assert-Null -Value (Get-FileDeploymentFailure -Output @($okLine) -ExitCode 0 -ExpectedFileCount 1)
    }

    It 'fails a per-file error even though nanoff exited 0' {
        # The reason this function exists. nanoff prints the error and returns OK.
        $output = @(
            'Deploying file C:\repo\config\room-sensor.json to I:\configuration.json...'
            'Error deploying content file C:\repo\config\room-sensor.json to I:\configuration.json'
        )
        $failure = Get-FileDeploymentFailure -Output $output -ExitCode 0 -ExpectedFileCount 1
        Assert-Match -Actual $failure -Pattern 'could not deploy 1 file'
    }

    It 'names the stale-nanoff cause, because nothing in the output does' {
        # This failure looks identical whether the device is broken, the file is wrong, or
        # the host tool is four months behind the firmware's wire protocol. It was the
        # third, and finding that out took a whole session; the message now says so.
        $output = @('Error deploying content file C:\repo\config\room-sensor.json to I:\configuration.json')
        $failure = Get-FileDeploymentFailure -Output $output -ExitCode 0 -ExpectedFileCount 1

        Assert-Match -Actual $failure -Pattern '2\.5\.163'
        Assert-Match -Actual $failure -Pattern 'dotnet tool update -g nanoff'
    }

    It 'does not blame nanoff for a failure that is not a per-file error' {
        # An exit code or a silent run says something else entirely, and pointing at the
        # tool version there would send the next person down the wrong path.
        foreach ($failure in @(
            (Get-FileDeploymentFailure -Output @() -ExitCode 2 -ExpectedFileCount 1),
            (Get-FileDeploymentFailure -Output @('Connected to nanoDevice') -ExitCode 0 -ExpectedFileCount 1)
        )) {
            Assert-True -Condition ($failure -notmatch '2\.5\.163') `
                        -Because "only a per-file error carries the version hint, got: $failure"
        }
    }

    It 'fails an exception line too' {
        $output = @('Exception deploying content file C:\repo\config\room-sensor.json to I:\configuration.json')
        Assert-Match -Actual (Get-FileDeploymentFailure -Output $output -ExitCode 0 -ExpectedFileCount 1) `
                     -Pattern 'could not deploy'
    }

    It 'fails an exit code even when nothing was printed' {
        Assert-Match -Actual (Get-FileDeploymentFailure -Output @() -ExitCode 2 -ExpectedFileCount 1) `
                     -Pattern 'exited 2'
    }

    It 'fails a silent run that confirmed nothing' {
        # Exit 0, no error line, no OK line: nanoff connected and did not deploy. Without
        # the count this reads as a success.
        Assert-Match -Actual (Get-FileDeploymentFailure -Output @('Connected to nanoDevice') -ExitCode 0 -ExpectedFileCount 1) `
                     -Pattern 'only 0 of 1'
    }

    It 'fails a run that deployed some of the files' {
        $output = @($okLine, 'Deploying file C:\repo\config\other.json to I:\other.json...')
        Assert-Match -Actual (Get-FileDeploymentFailure -Output $output -ExitCode 0 -ExpectedFileCount 2) `
                     -Pattern 'only 1 of 2'
    }

    It 'reads a null output as a failure rather than throwing' {
        Assert-Match -Actual (Get-FileDeploymentFailure -Output $null -ExitCode 0 -ExpectedFileCount 1) `
                     -Pattern 'only 0 of 1'
    }
}
