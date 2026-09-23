#!/usr/bin/env bash
# Run the Macro Deck bridge plugin (self-registering, interactive pairing on first run).
# Pairs once (approve the prompt in Macro Deck, Developer Mode required), then runs headless.
set -euo pipefail

BASE="$(cd "$(dirname "$0")/.." && pwd)"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/macrodeck-auto-folder-switcher/bridge"
mkdir -p "$STATE_DIR"

# .NET lives in ~/.dotnet (dotnet-install.sh) unless the distro's is on PATH already.
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
if [ -x "$DOTNET_ROOT/dotnet" ]; then
  export DOTNET_ROOT
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

DLL="$BASE/macrodeck-bridge/bin/Release/net10.0/MacroDeck.Bridge.dll"
if [ -f "$DLL" ]; then
  exec macrodeck-plugin run --executable "$DLL" --state-directory "$STATE_DIR"
fi
exec macrodeck-plugin run --project "$BASE/macrodeck-bridge" --state-directory "$STATE_DIR"