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

# Re-assert exec bits: they survive git cleanly (100755) but can be stripped by
# zip extraction or `core.fileMode=false` clones. systemd ExecStart needs them.
chmod +x "$REPO"/scripts/*.sh "$REPO"/focus-watcher/active-window-monitor/install.sh \
        "$INSTALL_DIR"/scripts/*.sh \
        "$INSTALL_DIR"/focus-watcher/active-window-monitor/install.sh

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

# systemd --user does not source ~/.bashrc, so the .NET toolchain would be
# invisible inside the services. Scope an explicit PATH into our own units
# (a drop-in, not the global user environment); persist it so it survives
# user-manager restarts, unlike `systemctl --user set-environment`.
augment_path() {
  local p entry out=()
  for p in "$HOME/.dotnet" "$HOME/.dotnet/tools" "$HOME/.local/bin" $PATH; do
    entry=1
    for prev in "${out[@]:-}"; do
      [ "$prev" = "$p" ] && entry=0 && break
    done
    [ -n "$p" ] && [ "$entry" = 1 ] && out+=("$p")
  done
  local IFS=':'
  printf '%s' "${out[*]}"
}
AUGMENTED_PATH="$(augment_path)"
# Escape for systemd's Environment= double-quoted value (backslash, quote, dollar).
escaped="${AUGMENTED_PATH//\\/\\\\}"
escaped="${escaped//\"/\\\"}"
escaped="${escaped//\$/\\\$}"
for unit in macrodeck-bridge macrodeck-focus-watcher; do
  drop_in="$SYSTEMD_DIR/$unit.service.d/env.conf"
  mkdir -p "$(dirname "$drop_in")"
  printf '[Service]\nEnvironment="PATH=%s"\n' "$escaped" > "$drop_in"
done
systemctl --user daemon-reload 2>/dev/null || echo "    (systemd not reachable here; run 'systemctl --user daemon-reload' after logging into a desktop session)"

echo "    enabling + starting services"
if systemctl --user enable --now macrodeck-bridge.service macrodeck-focus-watcher.service >/dev/null 2>&1; then
  echo "    started: systemctl --user is-active macrodeck-bridge macrodeck-focus-watcher"
else
  echo "    WARN: could not enable/start services here (no systemd --user session?) - later run:"
  echo "       systemctl --user enable --now macrodeck-bridge macrodeck-focus-watcher"
fi
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

Once (only if this is the first install):
  1. Log out and back in so the shell extension loads.
  2. If the bridge prompt is not auto-approved yet, open Macro Deck (Developer Mode on)
     and approve the pairing request.
  3. Check:  curl $(cat $XDG_RUNTIME_DIR/macrodeck-bridge.url)/clients
     Manual: python3 "$INSTALL_DIR/focus-watcher/watcher.py" --detect
EOF