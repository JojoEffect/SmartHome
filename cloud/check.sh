#!/usr/bin/env bash
#
# SmartHome -- is this cloud environment set up the way cloud/setup.sh leaves it?
#
# Read-only. Reports every item in one pass, so a missing piece does not hide the next one
# (the same reason scripts/Test-Setup.ps1 exists for a Windows desk, which this is NOT:
# that script checks a Windows machine for hardware work and cannot run here).
#
#   cloud/check.sh            tools, repository seeding, network reachability, GitHub access
#   cloud/check.sh --tests    the same, then scripts/Run-ScriptTests.ps1 under pwsh
#
# Exit 0 unless a REQUIRED item fails. Items marked "expected" are things that are blocked
# in a cloud session by design; they are listed so nobody tries them, not as problems.

set -uo pipefail

RUN_TESTS=0
if [ "${1:-}" = "--tests" ]; then RUN_TESTS=1; fi

here="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
REPO="$(cd "$here/.." && pwd)"

failures=0

row() { printf '  %-5s %-28s %s\n' "$1" "$2" "$3"; }
ok() { row "OK" "$1" "${2:-}"; }
warn() { row "WARN" "$1" "${2:-}"; }
info() { row "info" "$1" "${2:-}"; }
bad() { row "FAIL" "$1" "${2:-}"; failures=$((failures + 1)); }

section() { printf '\n%s\n' "$1"; }

tool() { # <kind: req|opt> <command> <version-command> <fix>
  local kind="$1" cmd="$2" vcmd="$3" fix="$4" v
  if command -v "$cmd" >/dev/null 2>&1; then
    v="$(eval "$vcmd" 2>/dev/null | head -n 1)"
    ok "$cmd" "${v:-present}"
  elif [ "$kind" = req ]; then
    bad "$cmd" "missing -- $fix"
  else
    warn "$cmd" "missing -- $fix"
  fi
}

reach() { # <kind: req|opt|blocked> <label> <url>
  local kind="$1" label="$2" url="$3" code
  code="$(curl -sS -m 12 -o /dev/null -w '%{http_code}' -L "$url" 2>/dev/null || true)"
  case "$code" in
    2*|3*) ok "$label" "reachable ($code)" ;;
    *)
      case "$kind" in
        req) bad "$label" "not reachable (HTTP ${code:-none}) -- add the host to the network policy" ;;
        opt) warn "$label" "not reachable (HTTP ${code:-none})" ;;
        *) info "$label" "blocked (HTTP ${code:-none}) -- expected on the default policy" ;;
      esac ;;
  esac
}

# shellcheck source=/dev/null
os_name="$( . /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-unknown OS}" )"
printf 'SmartHome cloud environment check -- %s\n' "$os_name"
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  printf '  (CLAUDE_CODE_REMOTE is not "true": this does not look like a cloud session)\n'
fi

section "Tools"
tool req git "git --version" "apt-get install git"
tool req pwsh "pwsh -NoProfile -Command '\$PSVersionTable.PSVersion.ToString()'" "run cloud/setup.sh"
tool req dotnet "dotnet --version" "run cloud/setup.sh"
tool opt mosquitto_sub "dpkg-query -W -f='\${Version}' mosquitto-clients" "run cloud/setup.sh"
tool opt mosquitto "dpkg-query -W -f='\${Version}' mosquitto" "run cloud/setup.sh"
tool opt nanoclr "nanoclr --help | head -n 1" "run cloud/setup.sh (INSTALL_NANOCLR=1)"
tool opt nanoff "nanoff --version" "off by default; INSTALL_NANOFF=1 in cloud/setup.sh"
tool opt gh "gh --version" "preinstalled in cloud sessions"
tool opt shellcheck "shellcheck --version | sed -n 2p" "apt-get install shellcheck (only for editing cloud/*.sh)"

section "Repository ($REPO)"
for f in local.env.ps1 nanoFramework.local.env.ps1; do
  if [ -f "$REPO/scripts/$f" ]; then ok "scripts/$f"; else
    warn "scripts/$f" "missing -- Sync-NanoFrameworkRepos.ps1 stops at its first line without it (cloud/setup.sh seeds it)"
  fi
done
siblings=0
for d in nf-interpreter nanoFramework.m2mqtt Samples CoreLibrary; do
  if [ -d "$REPO/../$d/.git" ]; then siblings=$((siblings + 1)); fi
done
if [ "$siblings" -ge 4 ]; then ok "companion repos" "present beside the checkout"; else
  info "companion repos" "not cloned -- run: pwsh scripts/Sync-NanoFrameworkRepos.ps1 (several GB) before any framework debugging"
fi

section "Network (this session's egress policy)"
reach req "archive.ubuntu.com" "http://archive.ubuntu.com/ubuntu/"
reach req "packages.microsoft.com" "https://packages.microsoft.com/"
reach req "api.nuget.org" "https://api.nuget.org/v3/index.json"
# Git smart-HTTP, which is what git clone uses. The website itself answers 403 here even
# though cloning works, so probing the page would report a false failure.
reach req "github.com (git)" "https://github.com/nanoframework/Samples.git/info/refs?service=git-upload-pack"
reach blocked "builds.dotnet.microsoft.com" "https://builds.dotnet.microsoft.com/"
reach blocked "aka.ms" "https://aka.ms/"
reach blocked "GitHub release assets" "https://github.com/nanoframework/nf-Visual-Studio-extension/releases/latest"

section "GitHub access"
if command -v gh >/dev/null 2>&1; then
  if gh api -X GET rate_limit >/dev/null 2>&1; then ok "gh api (REST)" "works"; else
    warn "gh api (REST)" "failed -- scripts that call gh cannot reach GitHub"
  fi
  if gh issue list --repo JojoEffect/SmartHome --limit 1 >/dev/null 2>&1; then
    ok "gh issue/pr list (GraphQL)" "works"
  else
    info "gh issue/pr list (GraphQL)" "blocked -- expected; the repo scripts read issues/PRs over REST (#166), do the same by hand"
  fi
else
  warn "gh" "not installed"
fi

if [ "$RUN_TESTS" = 1 ]; then
  section "Host-side script tests (pwsh)"
  if command -v pwsh >/dev/null 2>&1; then
    out="$(pwsh -NoProfile -File "$REPO/scripts/Run-ScriptTests.ps1" 2>&1)"
    rc=$?
    printf '%s\n' "$out" | tail -n 4
    if [ "$rc" -eq 0 ]; then ok "Run-ScriptTests.ps1" "exit 0"; else bad "Run-ScriptTests.ps1" "exit $rc"; fi
  else
    bad "Run-ScriptTests.ps1" "pwsh is missing"
  fi
fi

printf '\n'
if [ "$failures" -eq 0 ]; then
  printf 'Result: ready (no required item failed).\n'
  exit 0
fi
printf 'Result: %d required item(s) failed.\n' "$failures"
exit 1
