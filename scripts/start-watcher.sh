#!/usr/bin/env bash
# Macro Deck auto folder switcher watcher: polls the focus file, applies rules.
# Discovers the bridge URL itself via the bridge plugin's URL file in $XDG_RUNTIME_DIR.
set -euo pipefail

BASE="$(cd "$(dirname "$0")/.." && pwd)"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
exec python3 "$BASE/focus-watcher/watcher.py" --foreground