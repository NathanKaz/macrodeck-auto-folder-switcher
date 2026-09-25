"""Unit tests for focus rule matching. Run with: python3 -m unittest discover -s tests"""

import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "focus-watcher"))

from matching import (
    Config,
    FocusRecord,
    Rule,
    find_rule,
    load_config,
    load_focus_record,
    rules_from_bridge_payload,
    select_rule_source,
)


def record(**over) -> FocusRecord:
    base = dict(
        version=1,
        app_id="org.example.App.desktop",
        wm_class="App",
        wm_class_instance="app",
        title="My Window",
        pid=1234,
        time=1.0,
    )
    base.update(over)
    return FocusRecord.from_dict(base)


class FocusRecordTest(unittest.TestCase):
    def test_from_dict(self):
        r = record()
        self.assertEqual(r.app_id, "org.example.App.desktop")
        self.assertEqual(r.wm_class_instance, "app")
        self.assertEqual(r.pid, 1234)

    def test_empty_dict_returns_none(self):
        self.assertIsNone(FocusRecord.from_dict(None))
        self.assertIsNone(FocusRecord.from_dict({}))

    def test_nullable_fields(self):
        r = FocusRecord.from_dict({"version": 1, "app_id": "", "title": None, "pid": ""})
        self.assertIsNone(r.app_id)
        self.assertIsNone(r.title)
        self.assertIsNone(r.pid)


class RuleMatchTest(unittest.TestCase):
    def test_single_field_regex(self):
        rule = Rule.from_dict({"name": "t", "match": {"app_id": ["^org\\.gnome\\.Terminal$"]}})
        self.assertTrue(rule.matches(record(app_id="org.gnome.Terminal")))
        self.assertFalse(rule.matches(record(app_id="org.example.App")))
        self.assertFalse(rule.matches(record(app_id=None)))

    def test_any_of_the_alternatives_wins(self):
        rule = Rule.from_dict(
            {"match": {"wm_class_instance": ["^kitty$", "^alacritty$", "^wezterm$"]}}
        )
        self.assertTrue(rule.matches(record(wm_class_instance="kitty")))
        self.assertTrue(rule.matches(record(wm_class_instance="wezterm")))
        self.assertFalse(rule.matches(record(wm_class_instance="konsole")))

    def test_all_present_fields_must_match(self):
        rule = Rule.from_dict(
            {
                "match": {
                    "app_id": ["^org\\.gnome\\.Terminal$"],
                    "wm_class": ["^Gnome-terminal$"],
                }
            }
        )
        self.assertTrue(rule.matches(record(app_id="org.gnome.Terminal", wm_class="Gnome-terminal")))
        self.assertFalse(rule.matches(record(app_id="org.gnome.Terminal", wm_class="Other")))
        self.assertFalse(rule.matches(record(app_id="other", wm_class="Gnome-terminal")))

    def test_absent_fields_are_not_required(self):
        rule = Rule.from_dict({"match": {"app_id": ["^org\\.example$"]}})
        self.assertTrue(rule.matches(record(app_id="org.example", wm_class=None, title=None)))

    def test_bad_regex_raises(self):
        with self.assertRaises(ValueError):
            Rule.from_dict({"name": "x", "match": {"app_id": ["(unclosed"]}})

    def test_string_shorthand_for_single_regex(self):
        rule = Rule.from_dict({"match": {"app_id": "^org\\.gnome\\.Terminal$"}})
        self.assertTrue(rule.matches(record(app_id="org.gnome.Terminal")))


class FindRuleTest(unittest.TestCase):
    def _config(self, rules):
        return [
            Rule.from_dict(r) if isinstance(r, dict) else r
            for r in rules
        ]

    def test_prefix_rule_must_not_catch_obsidian(self):
        # Regression: "obs" rule must not steal "obsidian" focus.
        rules = self._config(
            [
                {"name": "obs", "match": {"app_id": ["^obs$"], "wm_class": ["^obs$"]}, "folder": "Main / OBS Studio"},
                {"name": "obsidian", "match": {"app_id": ["^obsidian$"], "wm_class": ["^obsidian$"]}, "folder": "Main / Obsidian"},
            ]
        )
        r = find_rule(rules, record(app_id="obsidian", wm_class="obsidian"))
        self.assertEqual(r.name, "obsidian")
        self.assertEqual(r.folder, "Main / Obsidian")

    def test_exact_anchored_ids_from_captured_focus(self):
        # Bind every real window id seen on this machine to its folder.
        checks = {
            "blender": "Main / Blender",
            "com.obsproject.Studio": "Main / OBS Studio",
            "obsidian": "Main / Obsidian",
            "org.gnome.Terminal": "Main / Scripts",
            "codium": "Main / Apps",
            "app.zen_browser.zen": "Main / Apps",
        }
        rules = self._config(
            [
                {"name": "blender", "match": {"app_id": "^blender$"}, "folder": "Main / Blender"},
                {"name": "obs", "match": {"app_id": "^com\\.obsproject\\.Studio$"}, "folder": "Main / OBS Studio"},
                {"name": "obsidian", "match": {"app_id": "^obsidian$"}, "folder": "Main / Obsidian"},
                {"name": "term", "match": {"app_id": "^org\\.gnome\\.Terminal$"}, "folder": "Main / Scripts"},
                {"name": "editor", "match": {"app_id": "^codium$"}, "folder": "Main / Apps"},
                {"name": "browser", "match": {"app_id": "^app\\.zen_browser\\.zen$"}, "folder": "Main / Apps"},
            ]
        )
        for app_id, folder in checks.items():
            r = find_rule(rules, record(app_id=app_id, wm_class=app_id, wm_class_instance=app_id))
            self.assertIsNotNone(r)
            self.assertEqual(r.folder, folder, msg=f"for app_id={app_id}")

    def test_real_config_example_binds_captured_apps(self):
        example = os.path.join(os.path.dirname(__file__), "..", "config.json.example")
        config = load_config(example)
        self.assertIsNotNone(config)
        checks = {
            "blender": "Main / Blender",
            "com.obsproject.Studio": "Main / OBS Studio",
            "obsidian": "Main / Obsidian",
            "org.gnome.Terminal": "Main / Scripts",
            "ai.opencode.desktop": "Main / Scripts",
            "app.zen_browser.zen": "Main / Apps",
            "codium": "Main / Apps",
        }
        for app_id, folder in checks.items():
            r = find_rule(config.rules, record(app_id=app_id, wm_class=app_id, wm_class_instance=app_id))
            self.assertIsNotNone(r, msg=f"no rule for {app_id}")
            self.assertEqual(r.folder, folder, msg=f"for app_id={app_id}")

    def test_first_match_wins(self):
        rules = self._config(
            [
                {"name": "term", "match": {"app_id": [".*Terminal$"]}, "folder": "Terminal"},
                {"name": "all", "match": {"app_id": [".*"]}, "folder": "Other"},
            ]
        )
        r = find_rule(rules, record(app_id="org.gnome.Terminal"))
        self.assertEqual(r.name, "term")

    def test_no_focus_no_rule(self):
        rules = self._config([{"name": "all", "match": {"app_id": [".*"]}}])
        self.assertIsNone(find_rule(rules, None))

    def test_ignored_payload_no_rule(self):
        rules = self._config([{"name": "all", "match": {"app_id": [".*"]}}])
        r = record(app_id="org.gnome.Terminal", reason="ignored")
        self.assertIsNotNone(find_rule(rules, r))

    def test_unmatched_focus_returns_none(self):
        rules = self._config([{"name": "term", "match": {"app_id": ["Terminal"]}}])
        self.assertIsNone(find_rule(rules, record(app_id="some.random.thing")))


class LoadConfigTest(unittest.TestCase):
    def test_roundtrip(self):
        cfg = {
            "bridge": {"auto": True, "url": "http://x", "token": "s3"},
            "focus_file": "/tmp/f.json",
            "poll_interval_ms": 100,
            "debounce_ms": 50,
            "client": "device:1",
            "rules": [{"name": "t", "match": {"app_id": ["^x$"]}, "folder": "F"}],
        }
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as fh:
            json.dump(cfg, fh)
            path = fh.name
        try:
            config = load_config(path)
            self.assertIsNotNone(config)
            self.assertEqual(config.bridge_auto, True)
            self.assertEqual(config.bridge_token, "s3")
            self.assertEqual(config.default_client, "device:1")
            self.assertEqual(len(config.rules), 1)
            self.assertEqual(config.rules[0].folder, "F")
        finally:
            os.unlink(path)

    def test_missing_file_returns_none(self):
        self.assertIsNone(load_config("/no/such/file.json"))

    def test_bad_rules_are_dropped(self):
        cfg = {"rules": [{"name": "bad", "match": {"app_id": ["(oops"]}}, {"name": "ok", "match": {"app_id": ["^x$"]}}]}
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as fh:
            json.dump(cfg, fh)
            path = fh.name
        try:
            config = load_config(path)
            self.assertEqual(len(config.rules), 1)
            self.assertEqual(config.rules[0].name, "ok")
        finally:
            os.unlink(path)


class LoadFocusFileTest(unittest.TestCase):
    def test_read_file(self):
        import time as _t

        payload = record(time=_t.time())
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as fh:
            json.dump(payload.as_dict(), fh)
            path = fh.name
        try:
            r = load_focus_record(path)
            self.assertIsNotNone(r)
            self.assertEqual(r.app_id, payload.app_id)
        finally:
            os.unlink(path)

    def test_garbage_file_returns_none(self):
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as fh:
            fh.write("not json {{{")
            path = fh.name
        try:
            self.assertIsNone(load_focus_record(path))
        finally:
            os.unlink(path)

    def test_missing_file_returns_none(self):
        self.assertIsNone(load_focus_record("/no/such/focus.json"))


class BridgeRulesTest(unittest.TestCase):
    def test_simple_payload(self):
        payload = {
            "rules": [
                {
                    "name": "Blender",
                    "match": {"app_id": "blender"},
                    "folder": "Main / Blender",
                    "profile": None,
                    "return_on_focus_loss": True,
                    "client": None,
                },
                {
                    "name": "Obsidian",
                    "match": {"app_id": "obsidian"},
                    "folder": "Main / Obsidian",
                    "profile": None,
                    "return_on_focus_loss": False,
                    "client": None,
                },
            ]
        }
        rules = rules_from_bridge_payload(payload)
        self.assertEqual(len(rules), 2)
        self.assertEqual(rules[0].name, "Blender")
        self.assertEqual(rules[0].folder, "Main / Blender")
        self.assertTrue(rules[0].return_on_focus_loss)
        self.assertEqual(rules[1].name, "Obsidian")

    def test_match_is_anchored_whole_app_id(self):
        rules = rules_from_bridge_payload({"rules": [{"match": {"app_id": "blender"}, "folder": "Main / Blender"}]})
        self.assertTrue(rules[0].matches(record(app_id="blender")))
        self.assertFalse(rules[0].matches(record(app_id="blender3d")), "must not prefix-match")
        self.assertFalse(rules[0].matches(record(app_id="xblender")))
        self.assertFalse(rules[0].matches(record(app_id=None)))

    def test_entries_missing_app_id_are_dropped(self):
        payload = {
            "rules": [
                {"match": {"app_id": None}, "folder": "Main / X"},
                {"match": {}, "folder": "Main / X"},
                {"match": {"app_id": "ok"}, "folder": "Main / X"},
            ]
        }
        rules = rules_from_bridge_payload(payload)
        self.assertEqual(len(rules), 1)
        self.assertTrue(rules[0].matches(record(app_id="ok")))

    def test_non_dict_payload_is_empty(self):
        self.assertEqual(rules_from_bridge_payload(None), [])
        self.assertEqual(rules_from_bridge_payload({}), [])
        self.assertEqual(rules_from_bridge_payload({"rules": []}), [])

    def test_bare_list_also_accepted(self):
        payload = [{"match": {"app_id": "blender"}, "folder": "Main / Blender"}]
        rules = rules_from_bridge_payload(payload)
        self.assertEqual(len(rules), 1)
        self.assertTrue(rules[0].matches(record(app_id="blender")))

    def test_config_rules_refresh_ms_defaults(self):
        cfg = Config.from_dict({"bridge": {}, "rules": []})
        self.assertEqual(cfg.rules_refresh_ms, 15000)

    def test_bridge_rule_first_match_wins(self):
        rules = rules_from_bridge_payload(
            {
                "rules": [
                    {"match": {"app_id": "obsidian"}, "folder": "Main / Obsidian"},
                    {"match": {"app_id": "obsidian"}, "folder": "Main / Scripts"},
                ]
            }
        )
        matched = find_rule(rules, record(app_id="obsidian"))
        self.assertEqual(matched.folder, "Main / Obsidian")


class RuleSourceTest(unittest.TestCase):
    def _rule(self, app: str) -> Rule:
        return rules_from_bridge_payload({"rules": [{"match": {"app_id": app}, "folder": f"Main / {app}"}]})

    def test_bootstrap_uses_file_rules(self):
        file_rules = [self._rule("blender")]
        rules, source = select_rule_source(False, None, file_rules)
        self.assertEqual(rules, file_rules)
        self.assertEqual(source, "file config")

    def test_takeover_uses_bridge_rules(self):
        bridge = [self._rule("obsidian")]
        rules, source = select_rule_source(True, bridge, [self._rule("blender")])
        self.assertEqual(rules, bridge)
        self.assertEqual(source, "Macro Deck settings")

    def test_empty_after_takeover_switches_nothing(self):
        rules, source = select_rule_source(True, [], [self._rule("blender")])
        self.assertEqual(rules, [])
        self.assertEqual(source, "Macro Deck settings")

    def test_takeover_persists_across_bridge_downtime(self):
        # saw_bridge set; bridge rules not yet fetched (None) -> nothing switches, no file fallback.
        rules, source = select_rule_source(True, None, [self._rule("blender")])
        self.assertEqual(rules, [])
        self.assertEqual(source, "Macro Deck settings")


if __name__ == "__main__":
    unittest.main()