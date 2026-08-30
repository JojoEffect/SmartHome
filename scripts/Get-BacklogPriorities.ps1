<#
.SYNOPSIS
    Classify the open GitHub backlog on seven axes and rank it, optionally re-weighted
    for the session you are actually about to have.

.DESCRIPTION
    The repository's own labels (type:, area:, status:) classify an issue but do not rank
    it -- nothing in them says which of eleven `type: task` issues to pick up first. This
    script adds the missing axes, scores every open issue, and prints the ranking.

    It runs in two rounds, and the skill (smarthome-prioritize) drives both:

      1. A plain call classifies and ranks the backlog with no knowledge of the session.
         That is the "first analysis" -- the clusters it prints are what the interview
         asks about.
      2. A second call passes -Hardware, -TimeBudget and -Theme, which re-weight the same
         scores for the session at hand. Nothing is re-classified; only the multipliers
         change, and both the base and the final score are kept so the effect of the
         interview is visible rather than hidden in one number.

    CLASSIFICATION IS A KEYWORD HEURISTIC AND IT IS NOT AUTHORITATIVE. Every axis records
    which patterns fired and a confidence (High/Medium/Low/None), because a heuristic that
    hides its reasoning is worse than no heuristic at all. Anything it could not read is
    reported as Unknown rather than guessed into a bucket. Correct it with -Overrides: the
    caller reads the issue bodies, decides, and hands the decisions back, so the ranking
    stays reproducible instead of living in a conversation.

    The seven axes, and what each answers:

      Trust        Can this issue make a check lie -- silently pass, lose its evidence,
                   or mask a real failure? Weighted hardest, because every other verdict
                   in this repo rests on the checks being honest.
      EvidenceDebt Does it name a constant, default or calibration asserted without
                   measurement? A latent wrong answer, not a cosmetic one.
      Where        Hardware (needs the ESP32, probe or broker physically present) or Desk
                   (doable anywhere, CI can verify it). Splits the backlog into two
                   schedulable pools, because CI here cannot run the integration suite.
      Track        Capability (ships device behaviour) or Velocity (speeds the dev loop).
      Risk         What leaving it open costs: SilentWrong > LoudFailure > Friction >
                   Cosmetic. The worst band that fires wins; risks are not summed.
      Effort       S / M / L.
      Unblocks     Which other issues this one is holding up, read from "blocked by #N" /
                   "depends on #N" / "once #N" phrasing in their bodies.

    Read-only. It touches no hardware, opens no port, and deliberately does not read
    scripts\local.env.ps1, so it works in a fresh worktree with no setup. It needs an
    authenticated `gh`.

.PARAMETER Hardware
    Session shape. Available raises hardware-gated issues, Unavailable pushes them down
    to near-zero (they are still listed -- suppressed, not deleted). Unknown (default)
    leaves them alone.

.PARAMETER TimeBudget
    Quick favours small issues, Deep favours large ones, HalfDay (default) is neutral.

.PARAMETER Theme
    Cluster to work in this session: Trust, EvidenceDebt, Capability, Velocity, Hardware,
    or Any (default). The matching cluster is raised and everything else damped; nothing
    is filtered out.

.PARAMETER Overrides
    Path to a JSON file of per-issue corrections, as produced after reading the bodies:

        { "35": { "Trust": true, "Effort": "S", "Note": "single Write-Host guard" },
          "26": { "Where": "Hardware", "Risk": "Friction" } }

    Must be a JSON object keyed by issue number; an array wrapper is rejected rather than
    silently ignored. Accepted axes and values:

      Trust, EvidenceDebt, Blocked  JSON true / false, unquoted
      Where                         Hardware | Desk | Either | Unknown
      Track                         Capability | Velocity | Either | Unknown
      Risk                          SilentWrong | LoudFailure | Friction | Cosmetic
      Effort                        S | M | L
      Unblocks, DependsOn           arrays of issue numbers
      Note                          string

    Validation is strict and reports every problem at once. A quoted "false" is truthy in
    PowerShell, so coercing it would turn an attempt to clear a flag into the strongest
    possible vote for it; an unknown axis or an out-of-list value would silently contribute
    nothing at all. Both stop the run instead.

    Unblocks is applied after the dependency graph is derived, so it REPLACES the derived
    edges rather than adding to them -- that is how a false edge from the loose `once #N`
    pattern gets removed. Blocked is overridable even though it comes from a label: an
    issue can be waiting on something real without anyone having applied it, and only the
    body says so.

    An overridden axis is reported with confidence Override and its heuristic value is kept
    in the JSON for comparison.

.PARAMETER Top
    Show only the first N in the ranked table. The clusters and the JSON always cover
    every issue.

.PARAMETER State
    Which issues to fetch. Default open. `all` is for checking whether a finding is
    already filed, not for ranking.

.PARAMETER Limit
    Maximum issues to fetch. Default 200.

.PARAMETER Handoff
    Print the top N as pointers for spinning each one off into its own session: url, axes,
    the full scoring trail, any override note, and a marker on the hardware-gated ones so
    the confirm-before-device-scripts rule reaches the new session. Blocked and closed rows
    are skipped however high they rank -- a blocked issue names what it is waiting for
    rather than work that can start -- and the skipped ones are listed by name and reason.
    In -Json the selection comes back as a Handoff array of issue numbers.

.PARAMETER RankingOnly
    Skip the cluster listing and print the ranked table alone. For the second round,
    where the clusters have already been shown and discussed.

.PARAMETER Json
    Emit the full classification -- every axis, every signal that fired, both scores --
    as JSON on stdout instead of the human report.

.EXAMPLE
    .\scripts\Get-BacklogPriorities.ps1
    First analysis: classify and rank with no session weighting.

.EXAMPLE
    .\scripts\Get-BacklogPriorities.ps1 -Hardware Unavailable -TimeBudget Quick -Theme Trust
    Re-rank for a desk session with an hour, working on verification trust.

.EXAMPLE
    .\scripts\Get-BacklogPriorities.ps1 -Json -Overrides .\overrides.json
    Full data, with the caller's judgment applied over the heuristic.
#>

[CmdletBinding()]
param(
    [ValidateSet('Available', 'Unavailable', 'Unknown')]
    [string]$Hardware = 'Unknown',

    [ValidateSet('Quick', 'HalfDay', 'Deep')]
    [string]$TimeBudget = 'HalfDay',

    [ValidateSet('Any', 'Trust', 'EvidenceDebt', 'Capability', 'Velocity', 'Hardware')]
    [string]$Theme = 'Any',

    [string]$Overrides = '',

    [int]$Top = 0,

    [ValidateSet('open', 'closed', 'all')]
    [string]$State = 'open',

    [int]$Limit = 200,

    [ValidateRange(0, 20)]
    [int]$Handoff = 0,

    [switch]$RankingOnly,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Common.ps1')

# Deliberately no Import-SmartHomeLocalEnv: this script needs no COM port, no broker and
# no restored packages, so it must not inherit the one thing that would make it fail in a
# fresh worktree.

$repoRoot = Get-SmartHomeRepoRoot

# --------------------------------------------------------------------- fetch ----

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "gh is not on PATH. The backlog is GitHub issues, so there is nothing to rank without it. Install the GitHub CLI, then run: gh auth login"
    exit 1
}

# Never pipe a native tool through 2>&1 in Windows PowerShell 5.1: the redirect wraps
# ordinary stderr in a NativeCommandError and reports a healthy call as a failure. Let
# stderr reach the console and judge by the exit code.
Push-Location $repoRoot
try {
    $raw = & gh issue list --state $State --limit $Limit --json number,title,body,labels,createdAt,updatedAt,comments,url,state
    $ghExit = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($ghExit -ne 0) {
    Write-Error "gh issue list failed with exit code $ghExit. Not authenticated? Run: gh auth login (scripts\Test-Setup.ps1 reports this too)."
    exit 1
}

# ForEach-Object, not just @(): ConvertFrom-Json in Windows PowerShell returns a single
# object rather than a one-element array when the payload has one element.
$issues = @($raw | ConvertFrom-Json | ForEach-Object { $_ })

# A cap that silently trims the backlog would make a partial ranking read as a complete
# one -- the same "no silent caps" rule the rest of this repo holds its scripts to.
if ($issues.Count -ge $Limit) {
    Write-Warning "Fetched $($issues.Count) issues, which is the -Limit. There are probably more, and anything beyond the cap is missing from this ranking. Re-run with a larger -Limit."
}

if ($issues.Count -eq 0) {
    if ($Json) {
        [pscustomobject]@{
            Session = [pscustomobject]@{ Hardware = $Hardware; TimeBudget = $TimeBudget; Theme = $Theme; State = $State; Overrides = $Overrides; Count = 0 }
            Issues  = @()
        } | ConvertTo-Json -Depth 8
    }
    else {
        Write-Warning "No $State issues found. Nothing to rank."
    }
    exit 0
}

# ----------------------------------------------------- classification rules ----
#
# Data, not code, so the report can cite exactly which pattern fired for which axis and
# a wrong call is a one-line fix rather than an archaeology exercise. W is how much a hit
# counts toward that axis's verdict: two W=2 hits outweigh one W=3.

$rules = @(
    # --- Trust: the check itself can lie -------------------------------------
    @{ Axis = 'Trust'; W = 3; P = 'silent(ly)?|no evidence|left no evidence'; Why = 'silent or evidence-free failure' }
    @{ Axis = 'Trust'; W = 3; P = 'mask(s|ing|ed)?\s+(the\s+)?real|swallow|hides?\s+the'; Why = 'masks the real failure' }
    @{ Axis = 'Trust'; W = 3; P = 'false\s+(pass|green|positive|negative)|green\s+run|reported\s+.{0,20}skipped'; Why = 'can report a false pass' }
    @{ Axis = 'Trust'; W = 2; P = 'flak(y|iness)|intermittent|\d+\s+run\s+of\s+\d+|non-?deterministic'; Why = 'unreliable verdict' }
    @{ Axis = 'Trust'; W = 2; P = 'conformance|verdict|WRONG-TEST|drift'; Why = 'verdict machinery' }
    @{ Axis = 'Trust'; W = 1; P = 'unfixed|gaps?\s+left|not\s+covered|no\s+.{0,20}coverage'; Why = 'known coverage gap' }

    # --- EvidenceDebt: asserted, never measured ------------------------------
    @{ Axis = 'EvidenceDebt'; W = 3; P = 'never\s+measured|copied\s+from\s+a\s+comment|without\s+evidence|unverified'; Why = 'value never measured' }
    @{ Axis = 'EvidenceDebt'; W = 3; P = 'assumption|assumed'; Why = 'stated as an assumption' }
    @{ Axis = 'EvidenceDebt'; W = 2; P = 'calibrat'; Why = 'calibration constant' }
    @{ Axis = 'EvidenceDebt'; W = 2; P = 'magic\s+number|arbitrary|guess(ed|work)?'; Why = 'unjustified constant' }
    @{ Axis = 'EvidenceDebt'; W = 1; P = 'hard-?coded|default\s+is'; Why = 'hardcoded default' }

    # --- Where: Hardware ------------------------------------------------------
    @{ Axis = 'Hardware'; W = 3; P = 'ESP32|COM\s*port|flash(ed|ing)?|physical|on-?device|probe|BMP280|ADS1115|I2C|solder|wiring'; Why = 'needs the device' }
    @{ Axis = 'Hardware'; W = 3; P = 'commission|real\s+hardware|self-?hosted\s+runner'; Why = 'needs the physical setup' }
    @{ Axis = 'Hardware'; W = 2; P = 'integration\s+(suite|test)|deploy(ment)?\s+(partition|padding)|broker\s+outage'; Why = 'exercised only on hardware' }
    @{ Axis = 'Hardware'; W = 1; P = 'sensor|driver|firmware'; Why = 'device-side' }

    # --- Where: Desk ----------------------------------------------------------
    @{ Axis = 'Desk'; W = 3; P = 'unit\s+test|\bCI\b|workflow|nanoclr|virtual\s+device'; Why = 'CI-verifiable' }
    @{ Axis = 'Desk'; W = 2; P = 'refactor|dispatch|rename|README|CONTRIBUTING|CLAUDE\.md|documentation|\bdocs?\b'; Why = 'source or doc change only' }
    @{ Axis = 'Desk'; W = 1; P = 'script|\.ps1|helper|parse|regex'; Why = 'host-side tooling' }

    # --- Track: Capability ----------------------------------------------------
    @{ Axis = 'Capability'; W = 3; P = 'IrrigationControl|OvenControl|RoomSensor|RainwaterCistern|new\s+device'; Why = 'device application' }
    @{ Axis = 'Capability'; W = 2; P = 'stub|not\s+implemented|\bempty\b|Homie|actuator|relay|\bMCP\b|Skills\s+Discovery'; Why = 'shippable behaviour' }

    # --- Track: Velocity ------------------------------------------------------
    @{ Axis = 'Velocity'; W = 2; P = 'script|test\s+(adapter|harness|runner)|build|tooling|\bCI\b|runner|capture|monitor'; Why = 'dev-loop tooling' }
    @{ Axis = 'Velocity'; W = 1; P = 'refactor|cleanup|duplicat|maintainab'; Why = 'maintenance' }

    # --- Risk -----------------------------------------------------------------
    @{ Axis = 'SilentWrong'; W = 3; P = 'silent(ly)?|false\s+(pass|green)|no\s+evidence|mask(s|ing|ed)?\s+(the\s+)?real|\bstale\b|\bdrift\b|wrong\s+(value|test|answer)'; Why = 'wrong answer, no warning' }
    @{ Axis = 'SilentWrong'; W = 2; P = 'assumption|assumed|never\s+measured|unverified'; Why = 'unproven premise' }
    @{ Axis = 'LoudFailure'; W = 3; P = 'throws?|crash(es|ed)?|exception|fails?\s+with|cannot\s+\w+|broken|refuses?'; Why = 'fails visibly' }
    @{ Axis = 'Friction'; W = 2; P = 're-?evaluat|every\s+(run|capture|call)|slow(er)?|overhead|by\s+hand|manual|noise|repeats?'; Why = 'costs time on every run' }
)

# Effort is judged separately: it is a size question, not a signal-strength one.
$effortLarge = 'stub|not\s+implemented|self-?hosted\s+runner|commission|new\s+(device|driver|project)|whole\s+suite|rewrite|redesign'
$effortSmall = 'rename|spelling|typo|guard|wrap|one-?line|single\s+(line|call|file)|message|comment'

# Dependency phrasing. Group 'n' is the issue this one waits on, so N unblocks this one.
$dependsPatterns = @(
    'blocked\s+by\s+#(?<n>\d+)'
    'depends\s+on\s+#(?<n>\d+)'
    'waiting\s+(on|for)\s+#(?<n>\d+)'
    'once\s+#(?<n>\d+)'
    'after\s+#(?<n>\d+)\s+(lands|ships|is\s+done|closes)'
    'requires\s+#(?<n>\d+)'
)

function Get-AxisHits {
    param(
        [Parameter(Mandatory = $true)][string]$Axis,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )

    $hits = @()
    foreach ($rule in $rules) {
        if ($rule.Axis -ne $Axis) { continue }
        if ($Text -match $rule.P) {
            $hits += [pscustomobject]@{ Weight = $rule.W; Why = $rule.Why }
        }
    }
    return , $hits
}

function Get-HitScore {
    param($Hits)

    $total = 0
    foreach ($hit in @($Hits)) { $total += $hit.Weight }
    return $total
}

function Get-Confidence {
    param([int]$Score)

    if ($Score -ge 5) { return 'High' }
    if ($Score -ge 3) { return 'Medium' }
    if ($Score -ge 1) { return 'Low' }
    return 'None'
}

# ----------------------------------------------------------------- overrides ----

# What an override may say, and what a valid value looks like. An override is the caller's
# judgment overruling the heuristic, so a malformed one must stop the run rather than be
# skipped: a ranking that silently ignored half its corrections is exactly the kind of
# quietly-wrong answer this script exists to surface.
$overrideSchema = [ordered]@{
    Trust        = 'Boolean'
    EvidenceDebt = 'Boolean'
    Blocked      = 'Boolean'
    Where        = @('Hardware', 'Desk', 'Either', 'Unknown')
    Track        = @('Capability', 'Velocity', 'Either', 'Unknown')
    Risk         = @('SilentWrong', 'LoudFailure', 'Friction', 'Cosmetic')
    Effort       = @('S', 'M', 'L')
    Unblocks     = 'IntArray'
    DependsOn    = 'IntArray'
    Note         = 'String'
}

$overrideMap = @{}
if ($Overrides) {
    if (-not (Test-Path -LiteralPath $Overrides)) {
        Write-Error "Overrides file not found: $Overrides"
        exit 1
    }
    try {
        # -Encoding UTF8 is not optional. Windows PowerShell 5.1 decodes a BOM-less file as
        # the system ANSI codepage, so a Note containing an em-dash -- which issue titles in
        # this backlog do -- comes back as mojibake with no error anywhere.
        $overrideRaw = Get-Content -LiteralPath $Overrides -Raw -Encoding UTF8
        $overrideJson = $overrideRaw | ConvertFrom-Json
    }
    catch {
        Write-Error "Overrides file is not valid JSON: $Overrides -- $($_.Exception.Message)"
        exit 1
    }

    # A JSON array or scalar parses cleanly and then exposes properties like Length and
    # Count, none of which match an issue number -- so without this check every override is
    # dropped while the report still announces the file as loaded.
    if ($overrideJson -isnot [System.Management.Automation.PSCustomObject]) {
        Write-Error "Overrides file must be a JSON object keyed by issue number, e.g. { `"35`": { `"Effort`": `"S`" } }. Found $($overrideJson.GetType().Name) in: $Overrides"
        exit 1
    }

    # Collect every problem before failing, the way Test-Setup.ps1 does: aborting on the
    # first one hides the rest and turns one edit into several round trips.
    $overrideProblems = @()

    foreach ($property in $overrideJson.PSObject.Properties) {
        $issueKey = [string]$property.Name
        $entry = $property.Value

        if ($issueKey -notmatch '^\d+$') {
            $overrideProblems += "key '$issueKey' is not an issue number"
            continue
        }
        if ($null -eq $entry -or $entry -isnot [System.Management.Automation.PSCustomObject]) {
            $overrideProblems += "#$issueKey must map to an object of axis corrections"
            continue
        }

        $clean = @{}
        foreach ($axisProperty in $entry.PSObject.Properties) {
            $axis = [string]$axisProperty.Name
            $value = $axisProperty.Value

            $canonicalAxis = @($overrideSchema.Keys) | Where-Object { $_ -eq $axis } | Select-Object -First 1
            if (-not $canonicalAxis) {
                $overrideProblems += "#$issueKey has unknown axis '$axis' (expected: $(@($overrideSchema.Keys) -join ', '))"
                continue
            }

            $expected = $overrideSchema[$canonicalAxis]

            if ($expected -isnot [string]) {
                # An allowed-value list. Normalise to the canonical spelling so the JSON
                # reports what the scoring actually used, not the caller's casing.
                # Checked before the switch, not inside it: `switch` enumerates a collection
                # scrutinee, so switching on the list itself ran the branch once per allowed
                # value and reported the same problem three or four times over.
                $canonicalValue = @($expected) | Where-Object { $_ -eq [string]$value } | Select-Object -First 1
                if (-not $canonicalValue) {
                    $overrideProblems += "#$issueKey $canonicalAxis is '$value'; expected one of: $(@($expected) -join ', ')"
                }
                else { $clean[$canonicalAxis] = $canonicalValue }
                continue
            }

            switch ($expected) {
                'Boolean' {
                    # Deliberately not [bool]$value: in PowerShell the *string* "false" is
                    # truthy, so coercing would turn an attempt to clear a flag into the
                    # strongest possible vote for it. Demand a real JSON boolean.
                    if ($value -isnot [bool]) {
                        $overrideProblems += "#$issueKey $canonicalAxis must be JSON true or false, not $(if ($null -eq $value) { 'null' } else { "'$value'" })"
                    }
                    else { $clean[$canonicalAxis] = $value }
                }
                'String' {
                    if ($null -eq $value) { $overrideProblems += "#$issueKey $canonicalAxis must be a string" }
                    else { $clean[$canonicalAxis] = [string]$value }
                }
                'IntArray' {
                    $numbers = @()
                    $bad = $false
                    foreach ($item in @($value)) {
                        $parsed = 0
                        if (-not [int]::TryParse([string]$item, [ref]$parsed)) {
                            $overrideProblems += "#$issueKey $canonicalAxis contains '$item', which is not an issue number"
                            $bad = $true
                            break
                        }
                        $numbers += $parsed
                    }
                    if (-not $bad) { $clean[$canonicalAxis] = @($numbers | Sort-Object -Unique) }
                }
            }
        }

        $overrideMap[$issueKey] = $clean
    }

    if ($overrideProblems.Count -gt 0) {
        Write-Error ("Overrides file has $($overrideProblems.Count) problem(s): $Overrides`n  - " + ($overrideProblems -join "`n  - "))
        exit 1
    }
}

function Get-OverrideValue {
    param([string]$Number, [string]$Key)

    if (-not $overrideMap.ContainsKey($Number)) { return $null }
    $entry = $overrideMap[$Number]
    if ($null -eq $entry -or -not $entry.ContainsKey($Key)) { return $null }
    return $entry[$Key]
}

function Set-OverriddenAxes {
    # Applied in two passes around the dependency-edge computation: DependsOn has to land
    # before the edges are derived from it, and Unblocks has to land after, or the edge pass
    # would append a heuristic edge back onto a list the caller had deliberately emptied.
    param(
        [Parameter(Mandatory = $true)]$Records,
        [Parameter(Mandatory = $true)][string[]]$Axes
    )

    foreach ($record in $Records) {
        $key = [string]$record.Number
        if (-not $overrideMap.ContainsKey($key)) { continue }

        foreach ($axis in $Axes) {
            $value = Get-OverrideValue -Number $key -Key $axis
            if ($null -eq $value) { continue }

            if ($axis -eq 'Note') { $record.Note = [string]$value; continue }

            $record.$axis = $value
            $record.Overridden = @(@($record.Overridden) + $axis | Sort-Object -Unique)
            if ($record.Confidence.PSObject.Properties.Name -contains $axis) {
                $record.Confidence.$axis = 'Override'
            }
        }
    }
}

# ------------------------------------------------------------------ classify ----

$records = @()

foreach ($issue in $issues) {
    $body = if ($null -eq $issue.body) { '' } else { [string]$issue.body }
    $labels = @($issue.labels | ForEach-Object { $_.name })
    $text = "$($issue.title)`n$body`n$($labels -join ' ')"

    $trustHits = Get-AxisHits -Axis 'Trust' -Text $text
    $debtHits = Get-AxisHits -Axis 'EvidenceDebt' -Text $text
    $hardwareHits = Get-AxisHits -Axis 'Hardware' -Text $text
    $deskHits = Get-AxisHits -Axis 'Desk' -Text $text
    $capabilityHits = Get-AxisHits -Axis 'Capability' -Text $text
    $velocityHits = Get-AxisHits -Axis 'Velocity' -Text $text

    $trustScore = Get-HitScore $trustHits
    $debtScore = Get-HitScore $debtHits
    $hardwareScore = Get-HitScore $hardwareHits
    $deskScore = Get-HitScore $deskHits
    $capabilityScore = Get-HitScore $capabilityHits
    $velocityScore = Get-HitScore $velocityHits

    # Labels are evidence too, and stronger than prose: someone chose them deliberately.
    if ($labels -contains 'area: sensor') { $hardwareScore += 3; $capabilityScore += 2 }
    if ($labels -contains 'area: homie') { $capabilityScore += 2 }
    if ($labels -contains 'area: infra') { $velocityScore += 3 }
    if ($labels -contains 'type: feature') { $capabilityScore += 3 }

    $trust = ($trustScore -ge 3)
    $debt = ($debtScore -ge 3)

    $where = 'Unknown'
    if ($hardwareScore -gt $deskScore) { $where = 'Hardware' }
    elseif ($deskScore -gt $hardwareScore) { $where = 'Desk' }
    elseif ($hardwareScore -gt 0) { $where = 'Either' }

    $track = 'Unknown'
    if ($capabilityScore -gt $velocityScore) { $track = 'Capability' }
    elseif ($velocityScore -gt $capabilityScore) { $track = 'Velocity' }
    elseif ($capabilityScore -gt 0) { $track = 'Either' }

    # Risk: the worst band that fires, not a sum. A crash and a silent wrong answer are
    # not additive -- the worse one is what leaving the issue open actually costs.
    $silentHits = Get-AxisHits -Axis 'SilentWrong' -Text $text
    $loudHits = Get-AxisHits -Axis 'LoudFailure' -Text $text
    $frictionHits = Get-AxisHits -Axis 'Friction' -Text $text
    $risk = 'Cosmetic'
    $riskHits = @()
    if ((Get-HitScore $frictionHits) -ge 2) { $risk = 'Friction'; $riskHits = $frictionHits }
    if ((Get-HitScore $loudHits) -ge 3) { $risk = 'LoudFailure'; $riskHits = $loudHits }
    if ((Get-HitScore $silentHits) -ge 3) { $risk = 'SilentWrong'; $riskHits = $silentHits }

    $effort = 'M'
    $effortWhy = 'no size signal; defaulted'
    if ($text -match $effortLarge) { $effort = 'L'; $effortWhy = 'large-work phrasing' }
    elseif ($body.Length -gt 2600) { $effort = 'L'; $effortWhy = "long body ($($body.Length) chars)" }
    elseif (($text -match $effortSmall) -and $body.Length -lt 900) { $effort = 'S'; $effortWhy = 'small-work phrasing, short body' }
    elseif ($body.Length -lt 700) { $effort = 'S'; $effortWhy = "short body ($($body.Length) chars)" }
    if ($labels -contains 'type: feature') { $effort = 'L'; $effortWhy = 'labelled type: feature' }

    $effortConfidence = 'Medium'
    if ($effortWhy -like 'no size signal*') { $effortConfidence = 'None' }
    elseif ($effortWhy -like '*body (*') { $effortConfidence = 'Low' }

    $dependsOn = @()
    foreach ($pattern in $dependsPatterns) {
        foreach ($match in ([regex]::Matches($text, $pattern, 'IgnoreCase'))) {
            $dependsOn += [int]$match.Groups['n'].Value
        }
    }
    $dependsOn = @($dependsOn | Sort-Object -Unique)

    $records += [pscustomobject]@{
        Number       = [int]$issue.number
        Title        = [string]$issue.title
        Url          = [string]$issue.url
        State        = [string]$issue.state
        Labels       = $labels
        Blocked      = ($labels -contains 'status: blocked')
        InProgress   = ($labels -contains 'status: in-progress')
        AgeDays      = [int]((Get-Date) - [datetime]$issue.createdAt).TotalDays
        Comments     = @($issue.comments).Count
        Trust        = $trust
        EvidenceDebt = $debt
        Where        = $where
        Track        = $track
        Risk         = $risk
        Effort       = $effort
        DependsOn    = $dependsOn
        Unblocks     = @()
        Heuristic    = [pscustomobject]@{
            Trust = $trust; EvidenceDebt = $debt; Where = $where
            Track = $track; Risk = $risk; Effort = $effort
        }
        Overridden   = @()
        Confidence   = [pscustomobject]@{
            Trust        = Get-Confidence $trustScore
            EvidenceDebt = Get-Confidence $debtScore
            Where        = Get-Confidence ([Math]::Max($hardwareScore, $deskScore))
            Track        = Get-Confidence ([Math]::Max($capabilityScore, $velocityScore))
            Risk         = Get-Confidence (Get-HitScore $riskHits)
            Effort       = $effortConfidence
        }
        Signals      = [pscustomobject]@{
            Trust        = @($trustHits | ForEach-Object { $_.Why })
            EvidenceDebt = @($debtHits | ForEach-Object { $_.Why })
            Hardware     = @($hardwareHits | ForEach-Object { $_.Why })
            Desk         = @($deskHits | ForEach-Object { $_.Why })
            Capability   = @($capabilityHits | ForEach-Object { $_.Why })
            Velocity     = @($velocityHits | ForEach-Object { $_.Why })
            Risk         = @($riskHits | ForEach-Object { $_.Why })
            Effort       = $effortWhy
        }
        Note         = ''
        BaseScore    = 0.0
        Score        = 0.0
        Why          = @()
    }
}

# ------------------------------------------------------------ apply overrides ----

# Blocked is overridable even though it comes from a label: an issue can be waiting on
# something real without anyone having relabelled it, and the body is what says so.
Set-OverriddenAxes -Records $records -Axes @('Trust', 'EvidenceDebt', 'Where', 'Track', 'Risk', 'Effort', 'Blocked', 'DependsOn', 'Note')

# ---------------------------------------------------------- dependency edges ----

$byNumber = @{}
foreach ($record in $records) { $byNumber[$record.Number] = $record }

foreach ($record in $records) {
    foreach ($blocker in @($record.DependsOn)) {
        if ($byNumber.ContainsKey($blocker) -and $blocker -ne $record.Number) {
            $blockerRecord = $byNumber[$blocker]
            $blockerRecord.Unblocks = @(@($blockerRecord.Unblocks) + $record.Number | Sort-Object -Unique)
        }
    }
}

# Unblocks last, so an explicit override *replaces* the derived edges rather than being
# appended to. The `once #N` pattern is loose enough to invent an edge out of incidental
# prose, and each edge is worth +12 -- the caller has to be able to take one away, not only
# add one.
Set-OverriddenAxes -Records $records -Axes @('Unblocks')

# ------------------------------------------------------------------- scoring ----

$riskPoints = @{ SilentWrong = 40; LoudFailure = 25; Friction = 10; Cosmetic = 3 }
$effortPoints = @{ S = 8; M = 0; L = -6 }

foreach ($record in $records) {
    $why = @()
    $score = 0.0

    if ($riskPoints.ContainsKey($record.Risk)) {
        $score += $riskPoints[$record.Risk]
        $why += "risk $($record.Risk) +$($riskPoints[$record.Risk])"
    }
    else {
        $why += "risk $($record.Risk) unrecognised, +0"
    }

    if ($record.Trust) { $score += 30; $why += 'verification trust +30' }
    if ($record.EvidenceDebt) { $score += 15; $why += 'evidence debt +15' }

    $unblockCount = @($record.Unblocks).Count
    if ($unblockCount -gt 0) {
        $bonus = [Math]::Min(36, 12 * $unblockCount)
        $score += $bonus
        $why += "unblocks $(@($record.Unblocks) -join ', ') +$bonus"
    }

    if ($effortPoints.ContainsKey($record.Effort)) {
        $score += $effortPoints[$record.Effort]
        $why += "effort $($record.Effort) $('{0:+#;-#;+0}' -f $effortPoints[$record.Effort])"
    }
    else {
        $why += "effort $($record.Effort) unrecognised, +0"
    }

    if ($record.InProgress) { $score += 10; $why += 'already in progress +10' }

    # Blocked belongs to the issue, not to the session, so it is part of the base score.
    # Reporting it as session weighting made a run with no flags at all print "no session
    # weighting applied" while blocked issues had already been scaled.
    if ($record.Blocked) { $score *= 0.35; $why += 'blocked x0.35' }

    # Floor at 1 before the session multipliers. A Cosmetic/L issue sums to -3, and every
    # weighting below is a multiplier -- so a negative score would be *raised* toward zero
    # by any damping factor and outrank work that scored honestly low. 1 is the bottom of
    # the scale; below it the arithmetic stops meaning anything.
    if ($score -lt 1) { $score = 1.0; $why += 'floored to 1 (nothing scored positive)' }

    $record.BaseScore = [Math]::Round($score, 1)

    # ---- session weighting: multiplicative, so the base score stays readable ----

    if ($record.Where -eq 'Hardware') {
        if ($Hardware -eq 'Unavailable') { $score *= 0.15; $why += 'hardware-gated, device unavailable x0.15' }
        elseif ($Hardware -eq 'Available') { $score *= 1.25; $why += 'hardware-gated, device available x1.25' }
    }
    elseif ($record.Where -eq 'Desk' -and $Hardware -eq 'Available') {
        $score *= 0.9
        $why += 'desk work while the device is free x0.9'
    }

    if ($TimeBudget -eq 'Quick') {
        if ($record.Effort -eq 'S') { $score *= 1.4; $why += 'small, quick session x1.4' }
        elseif ($record.Effort -eq 'L') { $score *= 0.5; $why += 'large, quick session x0.5' }
    }
    elseif ($TimeBudget -eq 'Deep') {
        if ($record.Effort -eq 'L') { $score *= 1.3; $why += 'large, deep session x1.3' }
        elseif ($record.Effort -eq 'S') { $score *= 0.85; $why += 'small, deep session x0.85' }
    }

    if ($Theme -ne 'Any') {
        $matchesTheme = $false
        switch ($Theme) {
            'Trust' { $matchesTheme = $record.Trust }
            'EvidenceDebt' { $matchesTheme = $record.EvidenceDebt }
            'Capability' { $matchesTheme = ($record.Track -eq 'Capability') }
            'Velocity' { $matchesTheme = ($record.Track -eq 'Velocity') }
            'Hardware' { $matchesTheme = ($record.Where -eq 'Hardware') }
        }
        if ($matchesTheme) { $score *= 1.5; $why += "theme $Theme x1.5" }
        else { $score *= 0.7; $why += "off theme $Theme x0.7" }
    }

    $record.Score = [Math]::Round($score, 1)
    $record.Why = $why
}

$ranked = @($records | Sort-Object -Property @{ Expression = 'Score'; Descending = $true }, @{ Expression = 'Number'; Descending = $false })

# Normalise the reported priority to 0-100 against the top of this run. The raw weighted
# score is not comparable between runs -- the session multipliers stack, so one issue that
# is small AND on theme comes out at 164 next to an off-theme 48, and the column reads as
# broken rather than as an ordering. Relative keeps both rounds on one scale; Score and
# BaseScore stay in the JSON for anyone who wants the arithmetic.
$topScore = 0.0
foreach ($record in $ranked) { if ($record.Score -gt $topScore) { $topScore = $record.Score } }

$rankPosition = 0
foreach ($record in $ranked) {
    $rankPosition++
    # Floored at 1: an issue that is on the list has some priority, and a column of 0s
    # reads as "these do not matter" rather than "these are far below the top".
    $relative = 1
    if ($topScore -gt 0) { $relative = [Math]::Max(1, [Math]::Round(100 * $record.Score / $topScore, 0)) }
    Add-Member -InputObject $record -NotePropertyName 'Rank' -NotePropertyValue $rankPosition -Force
    Add-Member -InputObject $record -NotePropertyName 'Relative' -NotePropertyValue $relative -Force
}

# -------------------------------------------------------------------- handoff ----
#
# The top N as self-contained briefs, for spinning each one off into its own session. The
# selection is not simply "the first N rows": a blocked issue names what it is waiting for
# rather than work that can start, and a closed one is settled. Both are skipped, and the
# count skipped is reported -- a handoff list that quietly dropped two entries would read
# as "these are the top five" when it is not.

$handoffSet = @()
$handoffSkipped = @()
if ($Handoff -gt 0) {
    $handoffSkipped = @($ranked | Where-Object { $_.Blocked -or $_.State -ne 'OPEN' })
    $handoffSet = @($ranked |
        Where-Object { -not $_.Blocked -and $_.State -eq 'OPEN' } |
        Select-Object -First $Handoff)
}

# -------------------------------------------------------------------- output ----

if ($Json) {
    [pscustomobject]@{
        Session = [pscustomobject]@{
            Hardware   = $Hardware
            TimeBudget = $TimeBudget
            Theme      = $Theme
            State      = $State
            Overrides  = $Overrides
            Count      = $ranked.Count
        }
        Handoff = @($handoffSet | ForEach-Object { $_.Number })
        Issues  = $ranked
    } | ConvertTo-Json -Depth 8
    exit 0
}

function Write-Cluster {
    param([string]$Title, $Items, [string]$Color, [string]$Meaning)

    Write-Host ''
    Write-Host ("  {0} ({1})" -f $Title, @($Items).Count) -ForegroundColor $Color
    Write-Host ("    {0}" -f $Meaning) -ForegroundColor DarkGray
    if (@($Items).Count -eq 0) {
        Write-Host '    (none)' -ForegroundColor DarkGray
        return
    }
    foreach ($item in @($Items)) {
        $title = $item.Title
        if ($title.Length -gt 68) { $title = $title.Substring(0, 65) + '...' }
        Write-Host ("    #{0,-4} {1}" -f $item.Number, $title)
    }
}

$weighted = ($Hardware -ne 'Unknown' -or $TimeBudget -ne 'HalfDay' -or $Theme -ne 'Any')

Write-Host ''
Write-Host "Backlog priorities - $($ranked.Count) $State issue(s)" -ForegroundColor Cyan
if ($State -ne 'open') {
    $closedCount = @($ranked | Where-Object { $_.State -ne 'OPEN' }).Count
    Write-Host "$closedCount of these are closed and are scored like any other row - flagged C, but not excluded. -State $State is for checking what is already filed, not for choosing work." -ForegroundColor Yellow
}
if ($weighted) {
    Write-Host "Session weighting: Hardware=$Hardware  TimeBudget=$TimeBudget  Theme=$Theme" -ForegroundColor Cyan
}
else {
    Write-Host 'No session weighting applied - this is the first analysis.' -ForegroundColor DarkGray
}
if ($Overrides) { Write-Host "Overrides: $Overrides" -ForegroundColor DarkGray }

if (-not $RankingOnly) {

Write-Host ''
Write-Host 'CLUSTERS' -ForegroundColor White
Write-Cluster -Title 'Verification trust' -Color Red -Items @($ranked | Where-Object { $_.Trust }) `
    -Meaning 'a check here can pass while lying; every other verdict rests on these'
Write-Cluster -Title 'Evidence debt' -Color Yellow -Items @($ranked | Where-Object { $_.EvidenceDebt -and -not $_.Trust }) `
    -Meaning 'a constant or default asserted without measurement'
Write-Cluster -Title 'Capability' -Color Green -Items @($ranked | Where-Object { $_.Track -eq 'Capability' }) `
    -Meaning 'ships device behaviour'
Write-Cluster -Title 'Velocity' -Color Blue -Items @($ranked | Where-Object { $_.Track -eq 'Velocity' }) `
    -Meaning 'speeds the dev loop'
Write-Cluster -Title 'Hardware-gated' -Color Magenta -Items @($ranked | Where-Object { $_.Where -eq 'Hardware' }) `
    -Meaning 'needs the ESP32, probe or broker present; CI cannot verify these'
Write-Cluster -Title 'Blocked' -Color DarkGray -Items @($ranked | Where-Object { $_.Blocked }) `
    -Meaning 'status: blocked - the body names what it is waiting for'

$unclassified = @($ranked | Where-Object { $_.Where -eq 'Unknown' -or $_.Track -eq 'Unknown' -or $_.Confidence.Effort -eq 'None' })
if ($unclassified.Count -gt 0) {
    Write-Cluster -Title 'Needs a human call' -Color DarkYellow -Items $unclassified `
        -Meaning 'no signal on at least one axis - read the body and correct it with -Overrides'
}

}  # -RankingOnly

Write-Host ''
Write-Host 'RANKING' -ForegroundColor White
Write-Host ''
Write-Host ('  {0,-4} {1,-6} {2,-7} {3,-9} {4,-12} {5,-6} {6,-11} {7}' -f 'Rank', 'Issue', 'Prio', 'Where', 'Risk', 'Effort', 'Track', 'Title')
Write-Host ('  ' + ('-' * 112)) -ForegroundColor DarkGray

$shown = if ($Top -gt 0) { @($ranked | Select-Object -First $Top) } else { $ranked }
$rank = 0
foreach ($record in $shown) {
    $rank++
    $title = $record.Title
    if ($title.Length -gt 44) { $title = $title.Substring(0, 41) + '...' }

    $flags = ''
    if ($record.Trust) { $flags += 'T' }
    if ($record.EvidenceDebt) { $flags += 'E' }
    if (@($record.Unblocks).Count -gt 0) { $flags += 'U' }
    if ($record.Blocked) { $flags += 'B' }
    # A closed issue scores exactly like an open one, so without a marker -State all
    # interleaves settled work with actionable work and reads as a recommendation.
    if ($record.State -ne 'OPEN') { $flags += 'C' }
    if ($flags) { $title = "[$flags] $title" }

    $color = 'Gray'
    if ($record.EvidenceDebt) { $color = 'Yellow' }
    if ($record.Trust) { $color = 'Red' }
    if ($record.Blocked) { $color = 'DarkGray' }
    if ($record.State -ne 'OPEN') { $color = 'DarkGray' }

    Write-Host ('  {0,-4} #{1,-5} {2,-7} {3,-9} {4,-12} {5,-6} {6,-11} {7}' -f `
            $rank, $record.Number, $record.Relative, $record.Where, $record.Risk, $record.Effort, $record.Track, $title) -ForegroundColor $color
}

if ($Top -gt 0 -and $ranked.Count -gt $Top) {
    Write-Host ("  ... {0} more (drop -Top, or use -Json, for all)" -f ($ranked.Count - $Top)) -ForegroundColor DarkGray
}

if ($Handoff -gt 0) {
    Write-Host ''
    Write-Host "HANDOFF - top $(@($handoffSet).Count) to spin off" -ForegroundColor White
    if (@($handoffSkipped).Count -gt 0) {
        Write-Host ("  Skipped $(@($handoffSkipped).Count) row(s) that cannot be started, wherever they ranked: {0}" -f `
            ((@($handoffSkipped) | ForEach-Object { "#$($_.Number) $(if ($_.Blocked) { 'blocked' } else { 'closed' })" }) -join ', ')) -ForegroundColor DarkGray
    }

    foreach ($record in $handoffSet) {
        Write-Host ''
        Write-Host ("  #{0} - {1}" -f $record.Number, $record.Title) -ForegroundColor Cyan
        Write-Host ("    {0}" -f $record.Url) -ForegroundColor DarkGray
        Write-Host ("    prio {0}  {1}  risk {2}  effort {3}  {4}" -f `
                $record.Relative, $record.Where, $record.Risk, $record.Effort, $record.Track)
        Write-Host ("    why: {0}" -f (@($record.Why) -join ' ; ')) -ForegroundColor DarkGray

        if (@($record.Overridden).Count -gt 0) {
            Write-Host ("    overridden: {0}" -f (@($record.Overridden) -join ', ')) -ForegroundColor DarkGray
        }
        if ($record.Note) {
            Write-Host ("    note: {0}" -f $record.Note) -ForegroundColor DarkGray
        }
        if (@($record.Unblocks).Count -gt 0) {
            Write-Host ("    unblocks: {0}" -f ((@($record.Unblocks) | ForEach-Object { "#$_" }) -join ', ')) -ForegroundColor DarkGray
        }
        if ($record.Where -eq 'Hardware') {
            Write-Host '    HARDWARE: the handoff prompt must carry the confirm-before-device-scripts rule.' -ForegroundColor Yellow
        }
    }

    Write-Host ''
    Write-Host '  These are pointers, not briefs. Read each body (gh issue view <n> --comments)' -ForegroundColor DarkGray
    Write-Host '  before writing a handoff prompt - a spun-off session starts cold and cannot' -ForegroundColor DarkGray
    Write-Host '  see this ranking or this conversation.' -ForegroundColor DarkGray
}

Write-Host ''
$flagLegend = '  Flags: T verification trust, E evidence debt, U unblocks another issue, B blocked'
if ($State -ne 'open') { $flagLegend += ', C closed' }
Write-Host $flagLegend -ForegroundColor DarkGray
Write-Host '  Prio is 0-100 against the top of this run, so the two rounds share one scale.' -ForegroundColor DarkGray
if ($weighted) {
    Write-Host '  It is session-weighted; the raw and unweighted values are Score and BaseScore in -Json.' -ForegroundColor DarkGray
}
Write-Host ''
Write-Host '  Classification is a keyword heuristic. Read the bodies of the top few before' -ForegroundColor DarkGray
Write-Host '  committing to an order, and correct it with -Overrides rather than by hand.' -ForegroundColor DarkGray
Write-Host ''

exit 0
