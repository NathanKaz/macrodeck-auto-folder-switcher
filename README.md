# Macro Deck auto folder switcher

Switch the folder (or profile) your Macro Deck shows to match the desktop window that has focus — on **Wayland**, where Macro Deck 3's built-in focus rules do not work ("Wayland without XWayland is not supported").

## Architecture

```
GNOME Shell extension  ──writes──>  $XDG_RUNTIME_DIR/macrodeck-focus.json
   (active.window.monitor@macrodeck.local)   {app_id, wm_class, wm_class_instance, title, pid}

Python watcher  ──polls + rule-match──>  POST /navigate {"folder":"Main / Blender"}
   focus-watcher/watcher.py                    (or /restore on focus loss)

.NET bridge plugin  ──host.invoke──>  Macro Deck host → deck client(s)
   macrodeck-bridge/ (loopback REST on an ephemeral port,
   URL written to $XDG_RUNTIME_DIR/macrodeck-bridge.url)
```

- GNOME Shell 45+ only (ESM extension), written for GNOME 50.
- The bridge is an official Macro Deck 3 plugin in **self-registering** mode: it pairs once (approve in the desktop app), then runs headless. No fixed port, no stored URLs — the watcher learns the port from the URL file the plugin writes.
- Everything listens on loopback. Set `MACRO_DECK_BRIDGE_TOKEN` on the bridge and `bridge.token` in the config to require an `X-Bridge-Token` header.

## Install

Prereqs: .NET 10 SDK (install into `~/.dotnet` with `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet`), `macrodeck-plugin` CLI (`dotnet tool install --global MacroDeck.Plugin.Cli --version 3.0.0-beta.12`).

```
scripts/install.sh
```

Installer copies everything, `chmod +x` the helper scripts, bakes the .NET toolchain into both systemd units (scoped `env.conf` drop-ins — see Troubleshooting), and runs `systemctl --user daemon-reload` + `enable --now` for both services if a user systemd session is available.

Then, once:

1. **Log out and back in** so the shell extension loads (GNOME only scans new extensions at startup).
2. **Approve the pairing prompt** in Macro Deck (needs Developer Mode on in its settings) — the bridge service is already running, it just waits for this the first time.
3. `python3 .local/share/macrodeck-auto-folder-switcher/focus-watcher/watcher.py --detect`

## Configuration

`~/.config/macrodeck-auto-folder-switcher/config.json` (see `config.json.example`):

- `rules[]` — first match wins. Each has `match` (regex lists for `app_id`, `wm_class`, `wm_class_instance`, `title`) and `folder`/`profile` (by label, e.g. `"Main / Blender"`).
- `return_on_focus_loss: true` — when focus leaves a mapped window, POST `/restore` to return the deck to the folder it showed before.
- `client` — `null`/`"all"`, or a concrete client/device id from `watcher.py --clients`.
- `bridge.auto` — discover the URL from the plugin's port file (default true); `bridge.url` forces it.
- `rules_refresh_ms` — how often the watcher re-reads rules from the bridge (default 15000).

### Rules right in the Macro Deck app

The bridge can be configured without files: in **Macro Deck → Settings → Integrations → Auto Folder Switcher**, each config entry is exactly one rule (application → folder). The application comes from an `Autocomplete` with **hints about running applications** (ids of open windows from `macrodeck-apps.json`, written by the extension; the focus history is the fallback), and the folder comes from a dropdown of the deck's live folder labels.

**How the rule sources work:**

- The `config.json` file is only a **bootstrap for the first run**. It applies while Macro Deck settings have no rules yet.
- As soon as `/rules` returns at least one rule, the watcher remembers it (state file `~/.local/state/.../rules-source.txt`) and from then on works **only** from the Macro Deck rules:
  - delete a rule → the app stops switching the folder;
  - delete **all** rules → nothing switches;
  - bridge temporarily unavailable → the last loaded rules keep working; no fallback to the file.
- Rules polling interval — `rules_refresh_ms` (default 15000 ms).

Diagnostics: `watcher.py --rules` shows the source and effective rules; `GET /rules` — rules from the app settings; `GET /apps` — app ids from the focus history.

## Tooling

```
focus-watcher/watcher.py --detect             # current focus + matched rule + bridge health
focus-watcher/watcher.py --clients|--folders|--profiles
focus-watcher/watcher.py --rules              # effective rules + their source (file / Macro Deck)
focus-watcher/watcher.py --navigate "Main / Blender" [--client <id>]
focus-watcher/watcher.py --back | --restore
focus-watcher/watcher.py --once|--foreground
```

## Development

- Tests: `python3 -m unittest discover -s tests`
- Bridge: `macrodeck-plugin run --project macrodeck-bridge --stub-host` (isolated smoke test; `--stub-host` spins a throwaway test host). Against the real host: `macrodeck-plugin run --project macrodeck-bridge`.
- The bridge intentionally does not set a static listen URL — the Macro Deck supervisor owns it (MDP4002). It reports the real bound address via the port file. Because the port is **ephemeral and changes on every bridge restart**, the watcher re-reads the port file each poll tick and swaps to the new URL automatically (it does not cache it for the process lifetime).

## Troubleshooting (clean install)

Two known problems on a clean system and what usually causes them:

**"macrodeck-bridge.service exited with status=203/EXEC"** — `scripts/start-bridge.sh` is missing its executable bit. The installer fixes this itself (`chmod +x`), but if you copied it manually from a zip archive or a clone with `core.fileMode=false`:
```
chmod +x ~/.local/share/macrodeck-auto-folder-switcher/scripts/*.sh
```

**"dotnet/macrodeck-plugin not found" inside the service** — `systemd --user` does not read `~/.bashrc`, so tools from a local `~/.dotnet` are not visible. The installer writes a scoped drop-in on both units (`~/.config/systemd/user/macrodeck-bridge.service.d/env.conf`, `Environment="PATH=..."`), and `start-bridge.sh` locates the tools on its own even without it. Manual equivalent:
```
systemctl --user set-environment PATH="$HOME/.dotnet:$PATH"
systemctl --user daemon-reload
systemctl --user restart macrodeck-bridge.service
```
To make that survive a user-manager restart (not just the current session), put the PATH in `~/.config/environment.d/...` — but then it applies to every service. A scoped drop-in on our unit is preferable.

**"GET /rules returns 500: This plugin is calling back into the host too quickly"** — the Macro Deck host rejects plugin→host calls that arrive back-to-back. The bridge paces its config reads (`MACRO_DECK_BRIDGE_RULES_PACE_MS`, default 250 ms) and serves cached rules while a refresh is throttled, so a single slow poll is enough. Symptoms of the missing fix: rules silently empty and nothing switches after a fresh bridge start.

## Files

| Path | Purpose |
| --- | --- |
| `focus-watcher/active-window-monitor/` | GNOME Shell extension (uuid `active.window.monitor@macrodeck.local`) |
| `focus-watcher/watcher.py` | poll loop, rule matching, bridge client CLI |
| `focus-watcher/bridge.py` | loopback HTTP client + URL discovery |
| `macrodeck-bridge/` | .NET 10 plugin: `/health /apps /rules /clients /folders /profiles /navigate /restore /back` + built-in settings (config flow) inside Macro Deck |
| `scripts/install.sh`, `start-bridge.sh`, `start-watcher.sh`, `*.service` | one-shot install + systemd user units |