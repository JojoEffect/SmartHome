---
name: smarthome-prioritize
description: Analyse the GitHub backlog, classify it into clusters, then ask a three-question interview about this session and re-rank for it. Use when asked what to work on next, what is most important, to triage or prioritise the backlog, to plan a session, or to pick the highest-leverage issue.
---

# Prioritise the backlog for the session in front of you

The repository's labels (`type:`, `area:`, `status:`) classify an issue but never rank it —
nothing in them decides between eleven `type: task` issues. `Get-BacklogPriorities.ps1` adds
the seven axes that do, and this skill runs it in **two rounds with an interview between
them**. The order matters: the interview asks the user to choose among clusters that are
already on screen with real issue numbers in them, not among abstractions.

## Round 1 — analyse before asking anything

```powershell
.\scripts\Get-BacklogPriorities.ps1
```

Read-only, no session weighting, works in a fresh worktree with no setup. It prints the
clusters and an unweighted ranking.

Do **not** re-derive any of this with `gh issue list`, `gh issue view` in a loop and manual
reasoning in the conversation. That is what the script replaces, and it costs many times the
tokens for a less reproducible answer.

Show the user the clusters. Keep it short — the cluster block is the summary.

## Round 2 — the interview

Ask all three questions in **one** `AskUserQuestion` call. Never ask them before round 1 has
run: the third question is meaningless without the clusters.

| Question | Header | Options |
|---|---|---|
| Is the ESP32 reachable right now? | `Hardware` | `Available` / `Unavailable` / `Unknown` |
| How much time? | `Time` | `Quick` (under an hour) / `HalfDay` / `Deep` (a day or more) |
| Which cluster this session? | `Theme` | the clusters round 1 actually printed, plus `Any` |

Two things to get right:

- **Put the real counts in the theme options.** "Verification trust (5) — #21, #35, #54, #38,
  #36" is a choice the user can make; "Trust" alone is a guess. Round 1 printed those numbers;
  use them.
- **Offer only non-empty clusters.** A theme with nothing in it damps the whole backlog by 0.7
  and ranks nothing.

The hardware question is the one that changes the answer most, because CI here cannot run the
integration suite — roughly two thirds of this backlog is hardware-gated, and asking for it
while the device is on someone's desk in another room wastes the session.

## Round 3 — re-rank and commit to an order

```powershell
.\scripts\Get-BacklogPriorities.ps1 -Hardware Unavailable -TimeBudget Quick -Theme Trust -RankingOnly -Top 8
```

Nothing is re-classified between the rounds; only the multipliers change. `-RankingOnly` skips
the clusters, which the user has already seen and just answered about.

Then **read the bodies of the top three or four** before presenting the order. The
classification is a keyword heuristic and the script says so itself; the top of the list is
exactly where a wrong call is most expensive.

## Correcting the heuristic

When reading a body contradicts an axis, do not argue with it in prose — hand the correction
back so the ranking stays reproducible. Write a JSON file and re-run:

```json
{
  "54": { "Effort": "S", "Note": "one guard around an already-exited subscriber" },
  "26": { "Unblocks": [14, 35], "Risk": "Friction" }
}
```

```powershell
.\scripts\Get-BacklogPriorities.ps1 -Overrides .\overrides.json -RankingOnly
```

Any axis may be set. An overridden axis reports confidence `Override`, and the heuristic's own
value stays in the JSON under `Heuristic` so the two can be compared.

| Axis | Accepted values |
|---|---|
| `Trust`, `EvidenceDebt`, `Blocked` | JSON `true` / `false` — **unquoted** |
| `Where` | `Hardware`, `Desk`, `Either`, `Unknown` |
| `Track` | `Capability`, `Velocity`, `Either`, `Unknown` |
| `Risk` | `SilentWrong`, `LoudFailure`, `Friction`, `Cosmetic` |
| `Effort` | `S`, `M`, `L` |
| `Unblocks`, `DependsOn` | array of issue numbers |
| `Note` | string |

The file must be a JSON **object keyed by issue number** — an array wrapper is rejected rather
than silently ignored — and it is validated strictly: an unknown axis, a value outside the list,
or a quoted `"false"` where a boolean belongs stops the run with every problem listed at once.
That is deliberate. A quoted `"false"` is truthy in PowerShell, so coercing it would turn an
attempt to *clear* a flag into the strongest possible vote *for* it, and a ranking that quietly
dropped half its corrections is exactly the failure this script exists to catch.

`Unblocks` is applied after the dependency graph is derived, so setting it **replaces** the
edges rather than adding to them — that is how a false edge invented by the loose `once #N`
pattern gets removed.

Write the file as UTF-8. It is read as UTF-8 explicitly, so an em-dash in a `Note` survives.

Put the file in the scratchpad directory, not in the repository — it describes one session's
judgment, not a durable fact about the backlog.

The `Needs a human call` cluster is the script naming its own blind spots: it lists every issue
where at least one axis found no signal at all. Those are the first bodies worth reading.

## The seven axes

Four are specific to this repository, three are generic.

| Axis | Question | Weight |
|---|---|---|
| **Trust** | Can this make a check *lie* — pass silently, lose its evidence, mask a real failure? | +30, the heaviest single term |
| **EvidenceDebt** | Does it name a constant, default or calibration asserted without measurement? | +15 |
| **Where** | `Hardware` (needs the ESP32, probe or broker present) or `Desk` (CI can verify it) | session multiplier, ×0.15 to ×1.25 |
| **Track** | `Capability` (ships device behaviour) or `Velocity` (speeds the dev loop) | theme multiplier only |
| **Risk** | `SilentWrong` > `LoudFailure` > `Friction` > `Cosmetic` | 40 / 25 / 10 / 3 |
| **Effort** | S / M / L | +8 / 0 / −6, then ×1.4 to ×0.5 by time budget |
| **Unblocks** | Which other issues this one is holding up | +12 each, capped at +36 |

**Trust outweighs everything on purpose.** This repo has already shipped a green unit run that
executed nothing (the `NFUnitTest` rename, three commits before anyone noticed) and an
integration check that lost five `/set` commands and left no evidence it had. A backlog item
that lets a check lie is not one bug — it is every verdict downstream of it, silently.

`Risk` takes the worst band that fires rather than summing bands. A crash and a silent wrong
answer are not additive; the worse one is what leaving the issue open actually costs.

`Blocked` (`status: blocked`) applies ×0.35 rather than filtering. A blocked issue still
belongs on screen — its body names what it is waiting for, and that is sometimes the thing
worth doing instead.

## Reading the output

`Prio` is 0–100 against the top of that run, so round 1 and round 3 are comparable even though
the raw weighted scores are not — the session multipliers stack, and one issue that is both
small and on theme comes out several times the raw score of an off-theme one. `-Json` carries
`Score` (raw weighted), `BaseScore` (before any session weighting), and `Why`, an audit trail
of every term that moved it:

```text
risk SilentWrong +40 ; verification trust +30 ; effort L -6 ;
hardware-gated, device available x1.25 ; large, deep session x1.3 ; theme Capability x1.5
```

Flags in the title column: `T` verification trust, `E` evidence debt, `U` unblocks another
issue, `B` blocked.

## What this does not decide

The ranking is an argument, not a verdict. It cannot see: what the user already has half-built
in a branch, what a hardware session is physically set up for right now, or what a wider change
would make cheap to do at the same time. Present the top few with the reasoning and let the
user pick — and if they pick something the ranking put eighth, that is information about the
axes, worth saying out loud rather than overriding quietly.

It also only reads issues. If work is found that is not filed, file it (`CLAUDE.md`, *File what
you find*) — an unfiled finding cannot be prioritised at all.

## Options

| Flag | Effect |
|---|---|
| `-Hardware Available\|Unavailable\|Unknown` | Session shape. Default `Unknown` (no effect) |
| `-TimeBudget Quick\|HalfDay\|Deep` | Default `HalfDay` (no effect) |
| `-Theme Trust\|EvidenceDebt\|Capability\|Velocity\|Hardware\|Any` | Matching cluster ×1.5, everything else ×0.7. Default `Any` |
| `-Overrides <path>` | JSON corrections applied over the heuristic |
| `-RankingOnly` | Skip the clusters, print the table alone |
| `-Top N` | Show the first N rows. Clusters and `-Json` always cover everything |
| `-State open\|closed\|all` | Default `open`. `all` is for checking whether a finding is already filed |
| `-Limit N` | Default 200. A run that hits the cap warns that the ranking is partial |
| `-Json` | Full classification, every signal and both scores, instead of the report |

`-State closed` or `all` scores closed issues exactly like open ones — they are flagged `C`,
greyed, and counted in a warning line, but not excluded. Do not read a ranking from those runs:
#33 and #39 rank in the top five there, and both are settled.

## Exit codes

`0` — ranking produced (including "no issues found"). `1` — `gh` missing or unauthenticated, or
an `-Overrides` file that is absent, not valid JSON, not a JSON object, or contains a value the
scoring does not understand. There is no partial answer: an empty ranking because `gh` failed
would read exactly like an empty backlog, and a silently-ignored override file would read
exactly like a heuristic that happened to agree with you.
