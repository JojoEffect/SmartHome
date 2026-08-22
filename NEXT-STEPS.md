# Next steps

Work that is known, wanted, and not done yet. Each item says what it is and why it matters, so
it can be picked up cold. Delete an item when it ships — this is a to-do list, not a changelog;
the git history is the changelog.

RoomSensor's real sensor data, the device-agnostic `HomieClientCheck` and topic-id validation
all shipped on 2026-08-21.

## 1. Land this work on `main`

`main` contains three files: `.gitignore`, `LICENSE`, `README.md`. Every line of this project —
`src/`, `scripts/`, `tools/`, `CLAUDE.md` — lives on branches, and `claude/project-cleanup-tests-708e9e`
alone is 29 commits ahead of it. Two consequences: a fresh clone gets an empty repository, and the
`@claude` GitHub workflow cannot fire at all, because GitHub reads workflow files for
`issue_comment` events from the **default branch** and `.github/workflows/claude.yml` is not on it.

Needs a decision rather than code: open a PR from this branch into `main`, merge
`feature/homie-v4` into `main` first and then this on top, or repoint the repository's default
branch. The third is a settings change only the repo owner can make.

## 2. Float values don't round-trip their own payload

A float property set to `21.5` comes back as `21.499999999999999`: nanoFramework's
`double.ToString()`. Not a spec violation — the convention only says a float payload is a number
— but a controller that writes a value and reads back something else is a poor experience, and
`HomieClientCheck` has to compare floats with a tolerance because of it. Worth deciding whether
the library should format float payloads deliberately (round-trip precision, or a fixed number
of decimals) rather than inheriting whatever `ToString()` does.

## 3. IrrigationControl and OvenControl are empty stubs

Both are 20-line "Hello from nanoFramework" projects with no Homie usage. They are the reason
`IHomieClient` and `OnCommand` exist — the first one written will be the real test of whether the
Homie library is genuinely device-agnostic, and the first hardware exercise of the `/set` command
path, which today only unit tests and `HomieClientCheck` cover.

Blocked on a question only the owner can answer: what is physically wired to the board for
irrigation or oven control — a relay module, GPIO pins directly, something over I2C, or nothing
yet? Until that is known the device model (which nodes, which properties, which datatypes) can't
be designed.
