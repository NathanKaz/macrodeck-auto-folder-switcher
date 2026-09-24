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
- `rules_refresh_ms` — how often the watcher re-reads rules from the bridge (default 5000).

### Правила прямо в приложении Macro Deck

Мост умеет настройку без файлов: в **Macro Deck → Settings → Integrations → Auto Folder Switcher** каждая запись конфигурации — это одно правило (имя приложения → папка). Приложение берётся из `Autocomplete` с **подсказками по запущенным приложениям** (id открытых окон из `macrodeck-apps.json`, который пишет расширение; плюс история фокуса как запасной вариант), папка — выпадающим списком из живых папок девки.

**Как работать с источниками правил:**

- Файл `config.json` — только **заглушка для первого запуска**. Он применяется, пока в настройках Macro Deck ещё ни разу не было ни одного правила.
- Как только в `/rules` появляется хотя бы одно правило, watcher запоминает это (файл состояния `~/.local/state/.../rules-source.txt`) и с этого момента работает **только** по правилам из Macro Deck:
  - удалил правило → приложение перестаёт переключать папку;
  - удалил **все** правила → ничего не переключается;
  - мост временно недоступен → работают последние загруженные правила, отката на файл нет.
- Частота опроса правил — `rules_refresh_ms` (по умолчанию 5000 мс).

Диагностика: `watcher.py --rules` показывает источник и действующие правила; `GET /rules` — правила из настроек приложения; `GET /apps` — app_id из истории фокуса.

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
- The bridge intentionally does not set a static listen URL — the Macro Deck supervisor owns it (MDP4002). It reports the real bound address via the port file.

## Troubleshooting (чистая установка)

Две известные проблемы на свежей системе и что обычно их вызывает:

**«macrodeck-bridge.service завершился с status=203/EXEC»** — у `scripts/start-bridge.sh` не хватает бита исполнения. Installer решает это сам (`chmod +x`), но если ты копировал вручную из zip-архива или клона с `core.fileMode=false`:
```
chmod +x ~/.local/share/macrodeck-auto-folder-switcher/scripts/*.sh
```

**«dotnet/macrodeck-plugin not found» внутри службы** — `systemd --user` не читает `~/.bashrc`, поэтому инструменты из локального `~/.dotnet` не видны. Installer записывает scoped drop-in на оба юнита (`~/.config/systemd/user/macrodeck-bridge.service.d/env.conf`, `Environment="PATH=..."`), а `start-bridge.sh` сам находит инструменты даже без него. Ручной вариант той же фиксы:
```
systemctl --user set-environment PATH="$HOME/.dotnet:$PATH"
systemctl --user daemon-reload
systemctl --user restart macrodeck-bridge.service
```
Чтобы это пережило перезапуск user-менеджера (а не только текущую сессию), положи PATH в `~/.config/environment.d/…` — но тогда он применится ко всем службам. Скаупед drop-in на наш юнит предпочтительнее.

## Files

| Path | Purpose |
| --- | --- |
| `focus-watcher/active-window-monitor/` | GNOME Shell extension (uuid `active.window.monitor@macrodeck.local`) |
| `focus-watcher/watcher.py` | poll loop, rule matching, bridge client CLI |
| `focus-watcher/bridge.py` | loopback HTTP client + URL discovery |
| `macrodeck-bridge/` | .NET 10 plugin: `/health /apps /rules /clients /folders /profiles /navigate /restore /back` + встроенные настройки (config flow) в Macro Deck |
| `scripts/install.sh`, `start-bridge.sh`, `start-watcher.sh`, `*.service` | one-shot install + systemd user units |