#!/usr/bin/env bash
# Install macrodeck-auto-folder-switcher for this user.
#
#   * copies the Python watcher, the bridge plugin and the GNOME shell extension
#   * builds the bridge in Release when a .NET SDK is found
#   * installs systemd --user units (available via enable --now)
#   * copies config.json.example to ~/.config/.../config.json if absent
#   * installs the GNOME shell extension (needs a session restart before it loads)
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
INSTALL_DIR="${INSTALL_DIR:-$HOME/.local/share/macrodeck-auto-folder-switcher}"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/macrodeck-auto-folder-switcher"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/macrodeck-auto-folder-switcher"
SYSTEMD_DIR="$HOME/.config/systemd/user"
UUID="active.window.monitor@macrodeck.local"

echo "==> Installing to $INSTALL_DIR"
mkdir -p "$INSTALL_DIR" "$CONFIG_DIR" "$STATE_DIR" "$SYSTEMD_DIR"
cp -r "$REPO/focus-watcher" "$REPO/macrodeck-bridge" "$REPO/scripts" "$REPO/Directory.Packages.props" "$INSTALL_DIR/"

if [ -d "$HOME/.dotnet/sdk" ] && command -v dotnet >/dev/null 2>&1; then
  echo "==> Building bridge (Release)"
  (cd "$INSTALL_DIR" && dotnet build macrodeck-bridge/MacroDeck.Bridge.csproj -c Release)
else
  echo "==> SKIP bridge build: no .NET SDK found (install .NET 10 into ~/.dotnet first)"
fi

echo "==> Config"
if [ ! -f "$CONFIG_DIR/config.json" ]; then
  cp "$REPO/config.json.example" "$CONFIG_DIR/config.json"
  echo "    created $CONFIG_DIR/config.json (edit match rules and folder names)"
else
  echo "    keeping existing $CONFIG_DIR/config.json"
fi

echo "==> Systemd user units"
install -m 0644 "$REPO/scripts/macrodeck-bridge.service" "$SYSTEMD_DIR/"
install -m 0644 "$REPO/scripts/macrodeck-focus-watcher.service" "$SYSTEMD_DIR/"
echo "    enable with:  systemctl --user enable --now macrodeck-bridge macrodeck-focus-watcher"
echo "    first start must pair the plugin - approve the prompt in Macro Deck (Developer Mode on)"

echo "==> GNOME shell extension"
if command -v gnome-extensions >/dev/null 2>&1; then
  cp -r "$REPO/focus-watcher/active-window-monitor" "$HOME/.local/share/gnome-shell/extensions/$UUID"
  gnome-extensions enable "$UUID" 2>/dev/null || true
  echo "    extension already in place at $HOME/.local/share/gnome-shell/extensions/$UUID"
else
  echo "    gnome-extensions not found - copy focus-watcher/active-window-monitor to"
  echo "    $HOME/.local/share/gnome-shell/extensions/$UUID"
fi

cat <<'EOF'

Done.

Next steps (once):
  1. Log out and back in so the shell extension loads.
  2. systemctl --user enable --now macrodeck-bridge
     -> approve the pairing prompt in Macro Deck (Developer Mode on).
  3. systemctl --user enable --now macrodeck-focus-watcher
  4. Check:  python3 "$INSTALL_DIR/focus-watcher/active-window-monitor/install.sh"   (already done)
     Check:  macrodeck bridge:  curl $(cat $XDG_RUNTIME_DIR/macrodeck-bridge.url)/clients
     Manual: python3 "$INSTALL_DIR/focus-watcher/watcher.py" --detect
EOF