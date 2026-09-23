#!/usr/bin/env python3
"""Macro Deck auto folder switcher: watch desktop focus and switch deck folders.

The GNOME Shell extension writes the focused window to ``$XDG_RUNTIME_DIR/macrodeck-focus.json``;
this daemon polls that file, matches it against the rules in ``config.json`` and tells the Macro Deck
bridge plugin (a .NET plugin exposing ``/navigate`` etc. on loopback) which folder to show.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from typing import Any, Optional

try:
    from matching import (  # type: ignore
        Config,
        FocusRecord,
        Rule,
        default_config_paths,
        find_rule,
        load_config,
        load_focus_record,
    )
    from bridge import BridgeClient, BridgeError, discover_bridge_url  # type: ignore
except ImportError:  # executed as a script from another cwd
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from matching import (
        Config,
        FocusRecord,
        Rule,
        default_config_paths,
        find_rule,
        load_config,
        load_focus_record,
    )
    from bridge import BridgeClient, BridgeError, discover_bridge_url

IDENTITY_FIELDS = ("app_id", "wm_class", "wm_class_instance", "title", "reason")


def focus_identity(record: Optional[FocusRecord]) -> Optional[dict[str, Any]]:
    if record is None:
        return None
    d = record.as_dict()
    return {k: d.get(k) for k in IDENTITY_FIELDS}


def rule_changed(current: Optional[Rule], next_rule: Optional[Rule]) -> bool:
    if current is None and next_rule is None:
        return False
    if current is None or next_rule is None:
        return True
    if current.name != next_rule.name:
        return True
    if current.folder != next_rule.folder or current.profile != next_rule.profile:
        return True
    if current.client != next_rule.client:
        return True
    if current.return_on_focus_loss != next_rule.return_on_focus_loss:
        return True
    return False


class Watcher:
    def __init__(self, config: Config, client: BridgeClient):
        self.config = config
        self.client = client
        self.applied: Optional[Rule] = None
        self.applied_client: Optional[str] = None
        self.pending_identity: Optional[dict[str, Any]] = None
        self.pending_since: float = 0.0
        self._last_record: Optional[FocusRecord] = None
        self._last_mtime: Optional[float] = None

    def _read_record(self) -> Optional[FocusRecord]:
        """Only read the focus file when it changed on disk."""
        try:
            mtime = os.path.getmtime(self.config.focus_file)
        except OSError:
            self._last_mtime = None
        else:
            if mtime == self._last_mtime:
                return self._last_record
            self._last_mtime = mtime
        self._last_record = load_focus_record(self.config.focus_file)
        return self._last_record

    def tick(self) -> Optional[Rule]:
        """One poll step. Returns the rule that is (or remains) applied, if any."""
        record = self._read_record()
        identity = focus_identity(record)

        if identity != self.pending_identity:
            self.pending_identity = identity
            self.pending_since = time.monotonic()

        debounce = self.config.debounce_ms / 1000.0
        if time.monotonic() - self.pending_since < debounce:
            return self.applied

        return self.evaluate(record)

    def evaluate(self, record: Optional[FocusRecord]) -> Optional[Rule]:
        rule = find_rule(self.config.rules, record)

        if rule is None:
            # Focus is no longer on any mapped window.
            if self.applied is not None and self.applied.return_on_focus_loss:
                self._restore()
            return self.applied

        if not rule.has_target:
            # A rule matched but only says "stay": no folder change, and no restore either.
            return self.applied

        target = rule.client or self.config.default_client
        if (
            self.applied is not None
            and self.applied.name == rule.name
            and self.applied_client == target
        ):
            return self.applied

        try:
            self.client.navigate(
                folder=rule.folder,
                profile=rule.profile,
                client=target,
            )
        except BridgeError as err:
            print(f"watcher: navigate failed: {err}", file=sys.stderr)
            return self.applied

        self.applied = rule
        self.applied_client = target
        print(
            f"watcher: focus={record.as_dict() if record else None} -> rule={rule.name} "
            f"folder={rule.folder} profile={rule.profile} client={target}",
            file=sys.stderr,
        )
        return self.applied

    def _restore(self) -> None:
        if self.applied is None:
            return
        client = self.applied_client
        try:
            self.client.restore(client=client)
            print(
                f"watcher: focus lost -> restored (rule={self.applied.name} client={client})",
                file=sys.stderr,
            )
        except BridgeError as err:
            print(f"watcher: restore failed: {err}", file=sys.stderr)
        self.applied = None
        self.applied_client = None


def resolve_config_path(explicit: Optional[str]) -> Optional[str]:
    if explicit:
        return explicit if os.path.exists(explicit) else None
    for p in default_config_paths():
        if os.path.exists(p):
            return p
    return None


def print_record(config: Config) -> Optional[FocusRecord]:
    record = load_focus_record(config.focus_file)
    print(json.dumps(record.as_dict() if record else None, indent=2))
    return record


def _resolve_bridge(config: Config) -> Optional[str]:
    url = discover_bridge_url(config.bridge_url, config.bridge_auto)
    if url is None:
        print(
            "watcher: bridge URL unknown (no bridge.url in config and no "
            "port file at $XDG_RUNTIME_DIR/macrodeck-bridge.url). "
            "Is the Macro Deck bridge plugin running?",
            file=sys.stderr,
        )
    return url


def run_daemon(config: Config, client: BridgeClient) -> int:
    watcher = Watcher(config, client)
    interval = max(config.poll_interval_ms, 50) / 1000.0
    print(
        f"watcher: watching {config.focus_file} every {interval:.2f}s, "
        f"{len(config.rules)} rule(s), bridge={client.url}",
        file=sys.stderr,
    )
    try:
        while True:
            watcher.tick()
            time.sleep(interval)
    except KeyboardInterrupt:
        print("watcher: stopped", file=sys.stderr)
        return 0


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        prog="watcher.py",
        description="Watch desktop focus and switch Macro Deck folders.",
    )
    parser.add_argument("--config", help="path to config.json")
    parser.add_argument("--detect", action="store_true", help="print current focus + matched rule")
    parser.add_argument("--folders", action="store_true", help="list folders from the bridge")
    parser.add_argument("--profiles", action="store_true", help="list profiles from the bridge")
    parser.add_argument("--clients", action="store_true", help="list connected clients")
    parser.add_argument("--navigate", metavar="FOLDER", help="switch to folder and exit")
    parser.add_argument("--profile", help="with --navigate: switch to profile instead")
    parser.add_argument("--client", help="target client id (default: all clients)")
    parser.add_argument("--back", action="store_true", help="go back one folder and exit")
    parser.add_argument("--restore", action="store_true", help="restore the previous folder")
    parser.add_argument("--once", action="store_true", help="run one evaluation and exit")
    parser.add_argument(
        "--foreground", action="store_true", help="run the polling loop (default action)"
    )
    parser.add_argument("--interval", type=int, help="override poll interval in ms")
    args = parser.parse_args(argv)

    config_path = resolve_config_path(args.config)
    if config_path is None:
        if args.config:
            print(f"watcher: config not found: {args.config}", file=sys.stderr)
        else:
            print(
                "watcher: no config.json found. Copy config.json.example to "
                "~/.config/macrodeck-auto-folder-switcher/config.json and edit it.",
                file=sys.stderr,
            )
        return 2

    config = load_config(config_path)
    if config is None:
        print(f"watcher: failed to read/parse config: {config_path}", file=sys.stderr)
        return 2

    if args.interval:
        config.poll_interval_ms = args.interval

    url = _resolve_bridge(config)
    if url is None:
        return 3
    client = BridgeClient(url, token=config.bridge_token)

    if args.detect:
        record = print_record(config)
        rule = find_rule(config.rules, record)
        print(f"matched_rule: {rule.name if rule else None}")
        print(f"focus_file: {config.focus_file}")
        print(f"bridge_url: {client.url}")
        try:
            print(f"bridge_health: {client.health()}")
        except BridgeError as err:
            print(f"bridge_health: UNREACHABLE ({err})")
            return 3
        return 0

    if args.folders:
        try:
            print(json.dumps(client.folders(), indent=2))
        except BridgeError as err:
            print(f"watcher: {err}", file=sys.stderr)
            return 3
        return 0

    if args.profiles:
        try:
            print(json.dumps(client.profiles(), indent=2))
        except BridgeError as err:
            print(f"watcher: {err}", file=sys.stderr)
            return 3
        return 0

    if args.clients:
        try:
            print(json.dumps(client.clients(), indent=2))
        except BridgeError as err:
            print(f"watcher: {err}", file=sys.stderr)
            return 3
        return 0

    if args.navigate:
        try:
            print(
                json.dumps(
                    client.navigate(
                        folder=args.navigate,
                        profile=args.profile,
                        client=args.client,
                    ),
                    indent=2,
                )
            )
        except BridgeError as err:
            print(f"watcher: {err}", file=sys.stderr)
            return 3
        return 0

    if args.back:
        try:
            print(json.dumps(client.back(client=args.client), indent=2))
        except BridgeError as err:
            print(f"watcher: {err}", file=sys.stderr)
            return 3
        return 0

    if args.restore:
        try:
            print(json.dumps(client.restore(client=args.client), indent=2))
        except BridgeError as err:
            print(f"watcher: {err}", file=sys.stderr)
            return 3
        return 0

    if args.once:
        watcher = Watcher(config, client)
        record = load_focus_record(config.focus_file)
        rule = watcher.evaluate(record)
        print(f"applied: {rule.name if rule else None}")
        return 0

    return run_daemon(config, client)


if __name__ == "__main__":
    sys.exit(main())