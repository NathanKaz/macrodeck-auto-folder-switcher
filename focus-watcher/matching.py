"""Focus-record parsing and rule matching for the Macro Deck auto folder switcher.

Pure, dependency-free logic (stdlib only) so it can be unit-tested.
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from typing import Any, Optional

MATCH_FIELDS = ("app_id", "wm_class", "wm_class_instance", "title")


@dataclass
class FocusRecord:
    version: int
    app_id: Optional[str]
    wm_class: Optional[str]
    wm_class_instance: Optional[str]
    title: Optional[str]
    pid: Optional[int]
    time: Optional[float]
    reason: Optional[str] = None

    @classmethod
    def from_dict(cls, d: Optional[dict]) -> Optional["FocusRecord"]:
        if not d:
            return None
        try:
            return cls(
                version=int(d.get("version", 1)),
                app_id=_nullable_str(d.get("app_id")),
                wm_class=_nullable_str(d.get("wm_class")),
                wm_class_instance=_nullable_str(d.get("wm_class_instance")),
                title=_nullable_str(d.get("title")),
                pid=_nullable_int(d.get("pid")),
                time=_nullable_float(d.get("time")),
                reason=_nullable_str(d.get("reason")),
            )
        except (TypeError, ValueError):
            return None

    def as_dict(self) -> dict[str, Any]:
        return {
            "app_id": self.app_id,
            "wm_class": self.wm_class,
            "wm_class_instance": self.wm_class_instance,
            "title": self.title,
            "pid": self.pid,
            "time": self.time,
            "reason": self.reason,
        }


def _nullable_str(v: Any) -> Optional[str]:
    if v is None:
        return None
    return str(v) if str(v) else None


def _nullable_int(v: Any) -> Optional[int]:
    if v is None or v == "":
        return None
    return int(v)


def _nullable_float(v: Any) -> Optional[float]:
    if v is None or v == "":
        return None
    return float(v)


def load_focus_record(path: str) -> Optional[FocusRecord]:
    """Read the current focus record from the GNOME Shell extension's file."""
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return FocusRecord.from_dict(json.load(fh))
    except (OSError, json.JSONDecodeError, ValueError):
        return None


@dataclass
class Rule:
    name: str
    folder: Optional[str]
    profile: Optional[str]
    return_on_focus_loss: bool
    client: Optional[str]
    patterns: dict[str, list[re.Pattern]] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, d: dict) -> "Rule":
        match = d.get("match") or {}
        patterns: dict[str, list[re.Pattern]] = {}
        for field_name in MATCH_FIELDS:
            exprs = match.get(field_name) or []
            if isinstance(exprs, str):
                exprs = [exprs]
            try:
                patterns[field_name] = [re.compile(e) for e in exprs]
            except re.error as err:
                raise ValueError(
                    f"rule '{d.get('name', '<unnamed>')}': bad regex for '{field_name}': {err}"
                )
        return cls(
            name=str(d.get("name") or "<unnamed>"),
            folder=(d.get("folder") or None),
            profile=(d.get("profile") or None),
            return_on_focus_loss=bool(d.get("return_on_focus_loss", False)),
            client=(d.get("client") or None),
            patterns=patterns,
        )

    def matches(self, record: FocusRecord) -> bool:
        for field_name, patterns in self.patterns.items():
            if not patterns:
                continue
            value = getattr(record, field_name)
            value = value if value is not None else ""
            if not any(p.search(value) for p in patterns):
                return False
        return True

    @property
    def has_target(self) -> bool:
        return bool(self.folder or self.profile)


@dataclass
class Config:
    bridge_url: Optional[str]
    bridge_auto: bool
    bridge_token: Optional[str]
    focus_file: str
    poll_interval_ms: int
    debounce_ms: int
    default_client: Optional[str]
    rules: list[Rule]
    source: str = ""
    rules_refresh_ms: int = 5000

    @classmethod
    def from_dict(cls, d: dict, source: str = "") -> "Config":
        bridge = d.get("bridge") or {}
        rules = [_rule_safe(r) for r in (d.get("rules") or [])]
        rules = [r for r in rules if r is not None]
        return cls(
            bridge_url=bridge.get("url") or None,
            bridge_auto=bool(bridge.get("auto", True)),
            bridge_token=bridge.get("token") or None,
            focus_file=str(d.get("focus_file") or ""),
            poll_interval_ms=int(d.get("poll_interval_ms", 250)),
            debounce_ms=int(d.get("debounce_ms", 300)),
            default_client=(d.get("client") or None),
            rules=rules,
            source=source,
            rules_refresh_ms=int(d.get("rules_refresh_ms", 5000)),
        )


def _rule_safe(r: Any) -> Optional[Rule]:
    if not isinstance(r, dict):
        return None
    try:
        return Rule.from_dict(r)
    except ValueError:
        return None


def rules_from_bridge_payload(payload: Any) -> list[Rule]:
    """Build exact-app-id rules from the bridge ``GET /rules`` payload.

    A rule configured in the Macro Deck app names an application by the exact string the
    extension records, so the match is an anchored whole-string compare (never a substring).
    Empty/invalid entries are dropped.
    """
    raw = payload.get("rules") if isinstance(payload, dict) else None
    if not isinstance(raw, list):
        return []

    result: list[Rule] = []
    for d in raw:
        if not isinstance(d, dict):
            continue
        app = (d.get("match") or {}).get("app_id")
        if not isinstance(app, str) or not app:
            continue
        result.append(
            Rule(
                name=str(d.get("name") or app),
                folder=(d.get("folder") or None),
                profile=(d.get("profile") or None),
                return_on_focus_loss=bool(d.get("return_on_focus_loss", False)),
                client=(d.get("client") or None),
                patterns={"app_id": [re.compile("^" + re.escape(app) + "$")]},
            )
        )
    return result


def load_config(path: str) -> Optional[Config]:
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return Config.from_dict(json.load(fh), source=path)
    except (OSError, json.JSONDecodeError, ValueError, TypeError):
        return None


def find_rule(rules: list[Rule], record: Optional[FocusRecord]) -> Optional[Rule]:
    """First rule (in config order) whose match block is satisfied. No focus -> no rule."""
    if record is None:
        return None
    for rule in rules:
        if rule.matches(record):
            return rule
    return None


def default_config_paths() -> list[str]:
    return [
        os.path.join(os.getcwd(), "config.json"),
        os.path.expanduser("~/.config/macrodeck-auto-folder-switcher/config.json"),
    ]