#!/usr/bin/env bash
# Run the Macro Deck bridge plugin (self-registering, interactive pairing on first run).
# Pairs once (approve the prompt in Macro Deck, Developer Mode required), then runs headless.
#
# systemd --user services do not inherit ~/.bashrc, so the .NET toolchain is searched
# independently of the environment the shell happened to have on PATH.
set -euo pipefail

BASE="$(cd "$(dirname "$0")/.." && pwd)"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/macrodeck-auto-folder-switcher/bridge"
mkdir -p "$STATE_DIR"

# Resolve dotnet: already on PATH, or in the usual local/dotnet-install locations.
DOTNET_ROOT="${DOTNET_ROOT:-}"
if ! command -v dotnet >/dev/null 2>&1; then
  for cand in "$HOME/.dotnet/dotnet" "$HOME/.local/share/dotnet/dotnet" /usr/lib/dotnet/dotnet /usr/share/dotnet/dotnet; do
    if [ -x "$cand" ]; then
      DOTNET_ROOT="$(dirname "$cand")"
      break
    fi
  done
fi

# Always prepend the standard global-tool dirs so `macrodeck-plugin` resolves even
# when dotnet came from a distro package (tools still default to ~/.dotnet/tools).
export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"
if [ -n "$DOTNET_ROOT" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
  export DOTNET_ROOT
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if ! command -v macrodeck-plugin >/dev/null 2>&1; then
  echo "ERROR: 'macrodeck-plugin' not found. Searched in:" >&2
  echo "${PATH//:/$'\n'}" | sed 's/^/  - /' >&2
  echo "Install it with:  dotnet tool install --global MacroDeck.Plugin.Cli --version 3.0.0-beta.12" >&2
  exit 1
fi

DLL="$BASE/macrodeck-bridge/bin/Release/net10.0/MacroDeck.Bridge.dll"
if [ -f "$DLL" ]; then
  exec macrodeck-plugin run --executable "$DLL" --state-directory "$STATE_DIR"
fi
exec macrodeck-plugin run --project "$BASE/macrodeck-bridge" --state-directory "$STATE_DIR"