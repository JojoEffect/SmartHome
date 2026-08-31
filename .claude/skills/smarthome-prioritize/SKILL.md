---
name: smarthome-prioritize
description: Analyse the GitHub backlog, classify it into clusters, then ask a three-question interview about this session and re-rank for it. Use when asked what to work on next, what is most important, to triage or prioritise the backlog, to plan a session, or to pick the highest-leverage issue.
---

# Prioritise the backlog for the session in front of you

The repository's labels (`type:`, `area:`, `status:`) classify an issue but never rank it —
nothing in them decides between eleven `type: task` issues. `Get-BacklogPriorities.ps1` adds
the eight axes that do, and this skill runs it in **two rounds with an interview between
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
integration suite — roughly three quarters of this backlog cannot be *verified* without the
device, and asking for it while the device is on someone's desk in another room wastes the
session.

It keys off `VerifyNeeds`, not `Where`, and those two disagree for a whole class of issue here.
Host-side tooling for the integration suite — `Run-IntegrationTests.ps1`, the capture helpers,
the verdict functions — is edited at a desk and can be proved by nothing but a real suite run.
Ranking it as desk work is what issue #57 was about, and it moved those issues further than any
other single axis, because this multiplier is the largest one in the script.

## Round 3 — re-rank and commit to an order

```powershell
.\scripts\Get-BacklogPriorities.ps1 -Hardware Unavailable -TimeBudget Quick -Theme Trust -RankingOnly -Top 8
```

Nothing is re-classified between the rounds; only the multipliers change. `-RankingOnly` skips
the clusters, which the user has already seen and just answered about.

Then **read the bodies of the top three or four** before presenting the order. The
classification is a keyword heuristic and the script says so itself; the top of the list is
exactly where a wrong call is most expensive.

## Round 4 — offer to spin the top few off

Once the order is agreed, offer to spin the top issues off into their own sessions. Ask how
many (3, 4 or 5 is the useful range) rather than picking a number — it is the user's backlog.

```powershell
.\scripts\Get-BacklogPriorities.ps1 -Hardware Available -TimeBudget Quick -Theme Trust -Overrides .\overrides.json -RankingOnly -Handoff 4
```

`-Handoff N` prints the selected issues with their url, axes, full scoring trail, any override
note, and a marker on the ones whose `VerifyNeeds` is `Hardware`. Three kinds of row are never handed out, however
high they rank:

- **blocked** — it names what it is waiting for, not work that can start
- **closed** — settled
- **`status: in-progress`** — a session already has it. Handing it out again is how two
  sessions land on the same issue, and the +10 the scoring gives it would otherwise push it
  *up* the handoff list

Any that ranked above something selected are named with their reason, so the selection is never
quietly different from the ranking. Rows that were never in contention are not listed — a skip
line longer than the selection buries the cases that actually mattered.

Then spawn one background task per issue. The prompts are the whole job here, and the rule that
makes them work is that **a spun-off session starts cold**: it cannot see this ranking, the
interview, the issue bodies you read, or anything else in this conversation. So each prompt must
carry:

- **The issue number and a `gh issue view <n> --comments` instruction.** Comments carry analysis
  that the body does not — including anything this session posted.
- **The substance, restated.** Not "see #54" — what is wrong, the evidence, the files, and what
  closing it would look like. Written so someone who has never seen this backlog can start.
- **Scope boundaries.** If the issue body contains a separable second question, say so
  explicitly and say it is separable. Otherwise the new session widens the diff.
- **Cross-links to related issues**, where they exist. #35, #36 and #54 all touch
  `Stop-HomieCapture`; a session that does not know that will re-derive it or collide with
  another. Say which may already be in progress.
- **The hardware-confirm rule, verbatim, for every hardware-gated issue.** CLAUDE.md requires
  confirming before `Deploy-ToDevice.ps1`, `Run-Tests.ps1` and `Run-IntegrationTests.ps1` even
  when a task obviously calls for them. A fresh session has CLAUDE.md, but stating it in the
  prompt removes the chance of it being read as pre-authorised. Add the reminder that the suite
  leaves its last test flashed, so RoomSensor needs redeploying afterwards.
- **The process rules**: branch per CLAUDE.md, Conventional Commit PR title, and file a GitHub
  issue for anything found outside the change.
- **`cwd` set to the main checkout**, not the current worktree — these are unrelated branches.

Where a hypothesis rather than a fact is being handed over, mark it as one and say what would
confirm or refute it. A spun-off session that treats a guess as a finding wastes its whole run.

## Correcting the heuristic

When reading a body contradicts an axis, do not argue with it in prose — hand the correction
back so the ranking stays reproducible. Write a JSON file and re-run:

```json
{
  "54": { "Effort": "S", "Note": "one guard around an already-exited subscriber" },
  "26": { "Unblocks": [14, 35], "Risk": "Friction" },
  "57": { "VerifyNeeds": "None", "Note": "proved by re-running the ranking script, not the suite" }
}
```

The `57` row is the standing example of the `VerifyNeeds` blind spot: that issue's body quotes
`Run-IntegrationTests.ps1` and `src/integrationTests` throughout, so the fallback reads it as
hardware-verified, while the only thing that proves a change to the ranking script is running
the ranking script.

```powershell
.\scripts\Get-BacklogPriorities.ps1 -Overrides .\overrides.json -RankingOnly
```

Any axis may be set. An overridden axis reports confidence `Override`, and the heuristic's own
value stays in the JSON under `Heuristic` so the two can be compared.

| Axis | Accepted values |
|---|---|
| `Trust`, `EvidenceDebt`, `Blocked` | JSON `true` / `false` — **unquoted** |
| `Where` | `Hardware`, `Desk`, `Either`, `Unknown` |
| `VerifyNeeds` | `Hardware`, `CI`, `None`, `Unknown` |
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

`VerifyNeeds` names a second blind spot through its confidence instead. It scores the **title
and labels** first and falls back to the body only when those say nothing at all — the title
says what an issue *is*, while every body in this repo mentions the device (the Dependabot issue
names ESP32 packages; a Homie library issue walks through the conformance check). Scoring the
body first put 33 of 37 issues in `Hardware`, which is an axis carrying no information. A
body-derived call is therefore reported at confidence `Low` however many patterns fired: on the
37 issues in this repository every title-derived call was right, and every wrong one came from
the body. **`Low` on `VerifyNeeds` means read the issue before trusting the rank.**

## The eight axes

Five are specific to this repository, three are generic.

| Axis | Question | Weight |
|---|---|---|
| **Trust** | Can this make a check *lie* — pass silently, lose its evidence, mask a real failure? | +30, the heaviest single term |
| **EvidenceDebt** | Does it name a constant, default or calibration asserted without measurement? | +15 |
| **Where** | Where the *edit* lands: `Hardware` (device-side code) or `Desk` (host-side source, scripts, docs) | none — descriptive only |
| **VerifyNeeds** | What *proving* it takes: `Hardware` (a real device run), `CI` (the unit suite or a build), `None` (any desk) | session multiplier, ×0.15 to ×1.25 |
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
| `-Handoff N` | Print the top N as spin-off pointers, skipping blocked, closed and in-progress rows |
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
