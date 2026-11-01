# scripts\Common.ps1's GitHub readers -- issues and pull requests over REST, because a
# Claude Code cloud session refuses the GraphQL that `gh issue list` / `gh pr list` use (#166).
#
# `gh` is never called: Invoke-SmartHomeGhApi is the one place it is spelled, and each case
# replaces that function with one that serves REST-shaped payloads.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Common.ps1')

function New-RestIssue {
    param([int]$Number = 1, [string]$State = 'open', [string[]]$Labels = @(), [switch]$AsPullRequest)

    $issue = [ordered]@{
        number     = $Number
        title      = "Issue $Number"
        body       = "Body $Number`nsecond line"
        state      = $State
        labels     = @($Labels | ForEach-Object { [ordered]@{ name = $_; color = 'ffffff' } })
        created_at = '2026-09-01T00:00:00Z'
        updated_at = '2026-09-02T00:00:00Z'
        comments   = 3
        html_url   = "https://github.com/JojoEffect/SmartHome/issues/$Number"
    }
    if ($AsPullRequest) { $issue['pull_request'] = [ordered]@{ url = 'https://api.github.com/x' } }
    return $issue
}

function New-RestPullRequest {
    param([int]$Number = 1, [string]$State = 'closed', $MergedAt = $null, [string]$Branch = 'feature', $HeadRepo = 'JojoEffect/SmartHome')

    return [ordered]@{
        number    = $Number
        state     = $State
        merged_at = $MergedAt
        head      = [ordered]@{ ref = $Branch; repo = $(if ($HeadRepo) { [ordered]@{ full_name = $HeadRepo } } else { $null }) }
        base      = [ordered]@{ ref = 'main'; repo = [ordered]@{ full_name = 'JojoEffect/SmartHome' } }
    }
}

function Set-GhApiPages {
    # Replaces Invoke-SmartHomeGhApi for the calling case. One JSON array per call, in order;
    # the arguments of every call are kept so a case can check what was asked for.
    param([object[]]$Pages = @(), [int]$FailWith = 0, [string]$FailBody = '')

    $script:ghPages = @($Pages | ForEach-Object { , (ConvertTo-Json -InputObject @($_) -Depth 8 -Compress) })
    $script:ghCalls = New-Object System.Collections.Generic.List[object]
    $script:ghFail = $FailWith
    $script:ghFailBody = $FailBody

    function script:Invoke-SmartHomeGhApi {
        param([string[]]$Arguments)
        $script:ghCalls.Add($Arguments)
        if ($script:ghFail -ne 0) {
            return [pscustomobject]@{ ExitCode = $script:ghFail; Output = @($script:ghFailBody) }
        }
        $index = $script:ghCalls.Count - 1
        $body = if ($index -lt $script:ghPages.Count) { $script:ghPages[$index] } else { '[]' }
        return [pscustomobject]@{ ExitCode = 0; Output = @($body) }
    }
}

Describe 'Get-SmartHomeGitHubSlug' {
    It 'reads owner/repo out of every spelling a remote takes' {
        Assert-Equal -Expected 'JojoEffect/SmartHome' -Actual (Get-SmartHomeGitHubSlug -RemoteUrl 'https://github.com/JojoEffect/SmartHome')
        Assert-Equal -Expected 'JojoEffect/SmartHome' -Actual (Get-SmartHomeGitHubSlug -RemoteUrl 'https://github.com/JojoEffect/SmartHome.git')
        Assert-Equal -Expected 'JojoEffect/SmartHome' -Actual (Get-SmartHomeGitHubSlug -RemoteUrl 'git@github.com:JojoEffect/SmartHome.git')
        Assert-Equal -Expected 'JojoEffect/SmartHome' -Actual (Get-SmartHomeGitHubSlug -RemoteUrl 'http://local_proxy@127.0.0.1:41999/git/JojoEffect/SmartHome')
    }

    It 'answers $null for nothing it can read' {
        Assert-Null -Value (Get-SmartHomeGitHubSlug -RemoteUrl '')
        Assert-Null -Value (Get-SmartHomeGitHubSlug -RemoteUrl 'not-a-url')
    }
}

Describe 'Get-SmartHomeGhApiFailure' {
    It 'names the HTTP status and message GitHub gave, and does not blame authentication for a 403' {
        $body = '{"message":"GitHub access to this repository is not enabled for this session.","status":"403"}'
        $detail = Get-SmartHomeGhApiFailure -ExitCode 1 -Output @($body)

        Assert-Match -Actual $detail -Pattern 'HTTP 403'
        Assert-Match -Actual $detail -Pattern 'not enabled for this session'
        Assert-False -Condition ($detail -match 'gh auth login') -Because 'the token was fine; the hint sends a cloud session to a command it cannot run'
    }

    It 'gives the authentication hint for a 401, and for what a real rejected token looks like' {
        Assert-Match -Actual (Get-SmartHomeGhApiFailure -ExitCode 1 -Output @('{"message":"Bad credentials","status":"401"}')) -Pattern 'gh auth login'

        # GitHub's real error body has no `status` member; gh puts the status on stderr.
        $real = '{"message":"Bad credentials","documentation_url":"https://docs.github.com/rest"}'
        Assert-Match -Actual (Get-SmartHomeGhApiFailure -ExitCode 1 -Output @($real)) -Pattern 'gh auth login'
        Assert-Match -Actual (Get-SmartHomeGhApiFailure -ExitCode 4 -Output @()) -Pattern 'gh auth login'
    }

    It 'does not give the authentication hint for a policy refusal whose body carries no status' {
        $real = '{"message":"GitHub access to this repository is not enabled for this session.","documentation_url":"https://docs.anthropic.com/x"}'
        $detail = Get-SmartHomeGhApiFailure -ExitCode 1 -Output @($real)

        Assert-Match -Actual $detail -Pattern 'not enabled for this session'
        Assert-False -Condition ($detail -match 'gh auth login')
    }

    It 'still says something when gh printed nothing on stdout' {
        $detail = Get-SmartHomeGhApiFailure -ExitCode 4 -Output @()
        Assert-Match -Actual $detail -Pattern 'exit code 4'
    }
}

Describe 'Get-SmartHomeGitHubIssue' {
    It 'hands back issues in the shape the backlog script reads' {
        Set-GhApiPages -Pages @(, @((New-RestIssue -Number 7 -State 'open' -Labels @('type: bug', 'area: infra'))))

        $result = Get-SmartHomeGitHubIssue -Repo 'JojoEffect/SmartHome'
        Assert-True -Condition $result.Ok
        $issue = @($result.Items)[0]

        Assert-Equal -Expected 7 -Actual $issue.number
        Assert-Equal -Expected 'OPEN' -Actual $issue.state
        Assert-Equal -Expected 'https://github.com/JojoEffect/SmartHome/issues/7' -Actual $issue.url
        Assert-Equal -Expected 3 -Actual $issue.commentCount
        Assert-ArrayEqual -Expected @('type: bug', 'area: infra') -Actual @($issue.labels | ForEach-Object { $_.name })
        Assert-Match -Actual $issue.body -Pattern 'second line'
    }

    It 'drops pull requests, which /issues also returns, and does not count them against the limit' {
        Set-GhApiPages -Pages @(, @(
            (New-RestIssue -Number 1 -AsPullRequest)
            (New-RestIssue -Number 2)
            (New-RestIssue -Number 3 -AsPullRequest)
            (New-RestIssue -Number 4)
        ))

        $result = Get-SmartHomeGitHubIssue -Repo 'o/r' -Limit 2
        Assert-ArrayEqual -Expected @(2, 4) -Actual @($result.Items | ForEach-Object { $_.number })
    }

    It 'asks for the state it was given, from the repository it was given' {
        Set-GhApiPages -Pages @(, @())

        [void](Get-SmartHomeGitHubIssue -Repo 'o/r' -State 'all')
        $arguments = @($script:ghCalls[0])

        Assert-Contains -Collection $arguments -Item 'repos/o/r/issues'
        Assert-Contains -Collection $arguments -Item 'state=all'
        Assert-Contains -Collection $arguments -Item 'GET'
    }

    It 'follows the pages until one comes back short' {
        $first = @(1..100 | ForEach-Object { New-RestIssue -Number $_ })
        $second = @(101..130 | ForEach-Object { New-RestIssue -Number $_ })
        Set-GhApiPages -Pages @(, $first; , $second)

        $result = Get-SmartHomeGitHubIssue -Repo 'o/r'
        Assert-Equal -Expected 130 -Actual @($result.Items).Count
        Assert-Equal -Expected 2 -Actual $script:ghCalls.Count
        Assert-Contains -Collection @($script:ghCalls[1]) -Item 'page=2'
    }

    It 'stops at the limit and does not fetch a page it cannot use' {
        $first = @(1..100 | ForEach-Object { New-RestIssue -Number $_ })
        Set-GhApiPages -Pages @(, $first)

        $result = Get-SmartHomeGitHubIssue -Repo 'o/r' -Limit 100
        Assert-Equal -Expected 100 -Actual @($result.Items).Count
        Assert-Equal -Expected 1 -Actual $script:ghCalls.Count
    }

    It 'returns an empty list, not $null, when there are no issues' {
        Set-GhApiPages -Pages @(, @())

        $result = Get-SmartHomeGitHubIssue -Repo 'o/r'
        Assert-True -Condition $result.Ok
        Assert-Equal -Expected 0 -Actual @($result.Items).Count
    }

    It 'reports the failure instead of an empty backlog' {
        Set-GhApiPages -FailWith 1 -FailBody '{"message":"GitHub GraphQL is not available","status":"403"}'

        $result = Get-SmartHomeGitHubIssue -Repo 'o/r'
        Assert-False -Condition $result.Ok
        Assert-Match -Actual $result.Detail -Pattern 'HTTP 403'
    }
}

Describe 'Get-SmartHomeGitHubPullRequest' {
    It 'maps REST state onto the three states gh pr list used to report' {
        Set-GhApiPages -Pages @(, @(
            (New-RestPullRequest -Number 1 -State 'open' -Branch 'a')
            (New-RestPullRequest -Number 2 -State 'closed' -MergedAt '2026-09-01T00:00:00Z' -Branch 'b')
            (New-RestPullRequest -Number 3 -State 'closed' -Branch 'c')
        ))

        $prs = @((Get-SmartHomeGitHubPullRequest -Repo 'o/r').Items)
        Assert-ArrayEqual -Expected @('OPEN', 'MERGED', 'CLOSED') -Actual @($prs | ForEach-Object { $_.state })
        Assert-ArrayEqual -Expected @('a', 'b', 'c') -Actual @($prs | ForEach-Object { $_.headRefName })
    }

    It 'marks a fork, and a fork whose repository was deleted, as cross-repository' {
        Set-GhApiPages -Pages @(, @(
            (New-RestPullRequest -Number 1 -Branch 'own')
            (New-RestPullRequest -Number 2 -Branch 'fork' -HeadRepo 'someone/SmartHome')
            (New-RestPullRequest -Number 3 -Branch 'gone' -HeadRepo $null)
        ))

        $prs = @((Get-SmartHomeGitHubPullRequest -Repo 'o/r').Items)
        Assert-ArrayEqual -Expected @($false, $true, $true) -Actual @($prs | ForEach-Object { $_.isCrossRepository })
    }

    It 'asks for open and closed pull requests alike' {
        Set-GhApiPages -Pages @(, @())

        [void](Get-SmartHomeGitHubPullRequest -Repo 'o/r')
        Assert-Contains -Collection @($script:ghCalls[0]) -Item 'repos/o/r/pulls'
        Assert-Contains -Collection @($script:ghCalls[0]) -Item 'state=all'
    }

    It 'reports the failure with what GitHub said' {
        Set-GhApiPages -FailWith 1 -FailBody '{"message":"Not Found","status":"404"}'

        $result = Get-SmartHomeGitHubPullRequest -Repo 'o/r'
        Assert-False -Condition $result.Ok
        Assert-Match -Actual $result.Detail -Pattern 'Not Found'
    }
}
