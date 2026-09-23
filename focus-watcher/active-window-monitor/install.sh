#!/usr/bin/env bash
set -euo pipefail

UUID="active.window.monitor@macrodeck.local"
SRC="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/.local/share/gnome-shell/extensions/$UUID"

echo "Installing extension to $DEST"
mkdir -p "$DEST"
for f in metadata.json extension.js; do
  cp "$SRC/$f" "$DEST/$f"
done

if ! command -v gnome-extensions >/dev/null 2>&1; then
  echo "gnome-extensions not found; enable with: gnome-extensions enable $UUID"
fi

echo "Enabling extension..."
gnome-extensions install --force "$SRC" >/dev/null 2>&1 || gnome-extensions enable "$UUID" >/dev/null 2>&1 || true
gnome-extensions enable "$UUID" >/dev/null 2>&1 && echo "enabled: $UUID" || echo "could not enable (run: gnome-extensions enable $UUID)"
gnome-extensions list --enabled | grep -Fq "$UUID" && echo "OK: extension is enabled" || echo "WARN: extension not yet enabled (may need a shell restart / check gnome-extensions app)"