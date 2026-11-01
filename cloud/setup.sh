#!/usr/bin/env bash
#
# SmartHome -- bootstrap for a Claude Code cloud environment (Ubuntu 24.04, runs as root).
#
# This is the file to paste into the environment's "Setup script" field (see
# cloud/README.md). It is written to work pasted on its own, with no repository around it,
# and also to be run from a checkout. It is idempotent: a second run finds everything in
# place and changes nothing.
#
# What it installs, and why each one is here (measured on 2026-11-01, not assumed):
#
#   powershell (7.x)   every script in scripts/ is PowerShell, and Run-ScriptTests.ps1 is the
#                      one workflow that can be proven end to end in the cloud
#   dotnet-sdk-8.0     tools/DeviceDebugMonitor is a net8.0 project; also hosts the dotnet
#                      tools below. From the Ubuntu archive: dot.net, aka.ms and
#                      builds.dotnet.microsoft.com are not reachable from a cloud session
#   mosquitto(+clients) broker and mosquitto_sub/pub for ad-hoc MQTT experiments. The
#                      scripts' dev-env workflow (Start-DevEnv.ps1) is Windows-only and does
#                      not use it
#   nanoclr            the nanoFramework virtual device CLI. Only versions >= 1.1.3xx run on
#                      Linux (1.1.122, which CI pins on Windows, crashes at startup here)
#   nanoff (optional)  the flasher. Useless without a device, so off by default
#
# What it deliberately does NOT do:
#
#   * No hook, no secrets, no environment variables: this script cannot read the
#     environment's saved variables or secrets, and nothing here needs them.
#   * It cannot build .nfproj projects. The nanoFramework build components ship only in a
#     Visual Studio VSIX (a GitHub release asset, blocked for cloud sessions) and its task
#     assemblies are .NET Framework 4.7.2. CI on windows-latest is where C# is built.
#
# Knobs: edit the values below before pasting. They are plain assignments on purpose --
# the setup script gets no environment to read them from.

set -euo pipefail

INSTALL_POWERSHELL=1
INSTALL_DOTNET_SDK=1
INSTALL_MOSQUITTO=1
INSTALL_NANOCLR=1
INSTALL_NANOFF=0
NANOFF_VERSION="2.5.131"   # flashes; 2.5.163 deploys files but cannot flash (issue #135)

# Copy the two scripts/*.local.env.template.ps1 files to their git-ignored names when the
# checkout has none, so scripts that dot-source them (Sync-NanoFrameworkRepos.ps1) start.
SEED_LOCAL_ENV=1

# Clone the 14 nanoFramework companion repos beside the checkout. Off by default: it is
# several GB and several minutes, and a session that never touches firmware or library
# behaviour pays for it. Run `pwsh scripts/Sync-NanoFrameworkRepos.ps1` on demand instead
# (CLAUDE.md says to do exactly that before any framework debugging).
SYNC_SIBLINGS=0

# --------------------------------------------------------------------------------------

log() { printf '[smarthome-setup] %s\n' "$*"; }
warn() { printf '[smarthome-setup] WARNING: %s\n' "$*" >&2; }

SECONDS=0
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
  if command -v sudo >/dev/null 2>&1; then SUDO="sudo"; else
    warn "not root and no sudo: package installs will fail"
  fi
fi

export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# --- Locate the checkout, if any ------------------------------------------------------
# Either this file lives in <repo>/cloud/, or the script starts one directory above the
# repository (which is where the environment runs it), or there is none and the repo-level
# steps are skipped.
REPO=""
here=""
if dir="$(dirname "${BASH_SOURCE[0]:-$0}")" && [ -d "$dir" ]; then here="$(cd "$dir" && pwd)"; fi
if [ -n "$here" ] && [ -f "$here/../SmartHome.sln" ]; then
  REPO="$(cd "$here/.." && pwd)"
elif [ -f "./SmartHome/SmartHome.sln" ]; then
  REPO="$(cd ./SmartHome && pwd)"
elif [ -f "./SmartHome.sln" ]; then
  REPO="$(pwd)"
fi
if [ -n "$REPO" ]; then log "checkout: $REPO"; else log "no checkout found; repo-level steps skipped"; fi

# --- Platform guard --------------------------------------------------------------------
# shellcheck source=/dev/null
. /etc/os-release 2>/dev/null || true
if [ "${ID:-}" != "ubuntu" ] || [ "${VERSION_ID:-}" != "24.04" ]; then
  warn "written for Ubuntu 24.04, found ${PRETTY_NAME:-unknown}; package names may differ"
fi

# --- apt packages ----------------------------------------------------------------------
apt_packages=()
need_ms_repo=0

if [ "$INSTALL_POWERSHELL" = 1 ] && ! command -v pwsh >/dev/null 2>&1; then
  apt_packages+=(powershell)
  need_ms_repo=1
fi
if [ "$INSTALL_DOTNET_SDK" = 1 ] && ! command -v dotnet >/dev/null 2>&1; then
  apt_packages+=(dotnet-sdk-8.0)
fi
if [ "$INSTALL_MOSQUITTO" = 1 ] && { ! command -v mosquitto >/dev/null 2>&1 || ! command -v mosquitto_sub >/dev/null 2>&1; }; then
  apt_packages+=(mosquitto mosquitto-clients)
fi

if [ "${#apt_packages[@]}" -gt 0 ]; then
  if [ "$need_ms_repo" = 1 ] && [ ! -f /etc/apt/sources.list.d/microsoft-prod.list ] \
     && [ ! -f /etc/apt/sources.list.d/microsoft-prod.sources ]; then
    log "adding the Microsoft package repository (for powershell)"
    tmp="$(mktemp -d)"
    curl -fsSL -m 60 -o "$tmp/ms.deb" \
      "https://packages.microsoft.com/config/ubuntu/${VERSION_ID:-24.04}/packages-microsoft-prod.deb"
    $SUDO dpkg -i "$tmp/ms.deb" >/dev/null
    rm -rf "$tmp"
  fi

  log "apt: ${apt_packages[*]}"
  # A broker that starts itself on :1883 would sit in the way of any test that starts its
  # own. Containers have no use for the service, so refuse to start it at install time.
  printf '#!/bin/sh\nexit 101\n' | $SUDO tee /usr/sbin/policy-rc.d >/dev/null
  $SUDO chmod +x /usr/sbin/policy-rc.d
  trap '$SUDO rm -f /usr/sbin/policy-rc.d' EXIT

  # The package manager's own output is hundreds of lines nobody reads; keep it for the
  # failure case and say nothing otherwise.
  apt_log="$(mktemp)"
  if ! { $SUDO apt-get update -qq && \
         $SUDO apt-get install -y -qq --no-install-recommends "${apt_packages[@]}"; } >"$apt_log" 2>&1; then
    tail -n 40 "$apt_log" >&2
    warn "apt failed (log above); is the host in the network policy?"
    exit 1
  fi
  rm -f "$apt_log"

  $SUDO rm -f /usr/sbin/policy-rc.d
  trap - EXIT
else
  log "apt: nothing to install"
fi

# --- dotnet global tools ---------------------------------------------------------------
# Installed with -g and linked into /usr/local/bin, because ~/.dotnet/tools is on no PATH
# in a cloud session and a PATH edit would not reach every shell.
#
# These tools are conveniences, so a failed install (a flaky NuGet fetch, say) warns and
# returns 1 instead of aborting: under `set -e` a bare failing command would end the whole
# setup, and a fully usable environment (pwsh, dotnet, mosquitto) would be reported as a
# failed one. Callers add `|| true` for the same reason -- `set -e` looks at the call too.
install_dotnet_tool() { # <name> [version]
  local name="$1" version="${2:-}" out
  if [ -x "$HOME/.dotnet/tools/$name" ]; then
    log "dotnet tool $name already installed"
  else
    log "dotnet tool install: $name ${version:+($version)}"
    if [ -n "$version" ]; then
      out="$(dotnet tool install -g "$name" --version "$version" 2>&1)" || {
        warn "could not install $name: $(printf '%s' "$out" | head -n 1)"; return 1; }
    else
      out="$(dotnet tool install -g "$name" 2>&1)" || {
        warn "could not install $name: $(printf '%s' "$out" | head -n 1)"; return 1; }
    fi
  fi
  $SUDO ln -sf "$HOME/.dotnet/tools/$name" "/usr/local/bin/$name" \
    || { warn "could not link $name into /usr/local/bin"; return 1; }
}

if command -v dotnet >/dev/null 2>&1; then
  if [ "$INSTALL_NANOCLR" = 1 ]; then install_dotnet_tool nanoclr || true; fi
  if [ "$INSTALL_NANOFF" = 1 ]; then install_dotnet_tool nanoff "$NANOFF_VERSION" || true; fi
fi

# --- repository-level seeding ----------------------------------------------------------
if [ -n "$REPO" ] && [ "$SEED_LOCAL_ENV" = 1 ]; then
  for pair in "local.env.template.ps1:local.env.ps1" \
              "nanoFramework.local.env.template.ps1:nanoFramework.local.env.ps1"; do
    template="$REPO/scripts/${pair%%:*}"
    target="$REPO/scripts/${pair##*:}"
    if [ -f "$template" ] && [ ! -f "$target" ]; then
      cp "$template" "$target"
      log "seeded scripts/${pair##*:} from its template"
    fi
  done
fi

if [ -n "$REPO" ] && [ "$SYNC_SIBLINGS" = 1 ]; then
  if command -v pwsh >/dev/null 2>&1; then
    log "syncing the nanoFramework companion repos (this takes a while)"
    pwsh -NoProfile -File "$REPO/scripts/Sync-NanoFrameworkRepos.ps1" \
      || warn "the sibling sync failed; run it again by hand when the network allows"
  else
    warn "SYNC_SIBLINGS=1 but pwsh is not installed"
  fi
fi

# --- summary ---------------------------------------------------------------------------
log "done in ${SECONDS}s"
for tool in pwsh dotnet mosquitto_sub nanoclr nanoff; do
  if command -v "$tool" >/dev/null 2>&1; then
    # shellcheck disable=SC2016  # the $ in the pwsh command belongs to PowerShell
    case "$tool" in
      pwsh) v="$(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || true)" ;;
      dotnet) v="$(dotnet --version 2>/dev/null || true)" ;;
      mosquitto_sub) v="$(dpkg-query -W -f='${Version}' mosquitto-clients 2>/dev/null || true)" ;;
      *) v="installed" ;;
    esac
    printf '  %-14s %s\n' "$tool" "$v"
  else
    printf '  %-14s %s\n' "$tool" "-"
  fi
done

# The one thing the rest of the day depends on. A missing pwsh is a failed setup; the
# other tools are conveniences: a failed install has already warned and did not stop the run.
if [ "$INSTALL_POWERSHELL" = 1 ] && ! command -v pwsh >/dev/null 2>&1; then
  warn "pwsh is missing: scripts/Run-ScriptTests.ps1 cannot run"
  exit 1
fi
