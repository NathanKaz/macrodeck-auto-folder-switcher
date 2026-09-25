"""Unit tests for the watcher's bridge URL re-discovery.

The bridge binds an ephemeral port per run, so the daemon must re-read the
port file each tick and swap its client when the URL changed. Run with:
python3 -m unittest discover -s tests
"""

import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "focus-watcher"))

from bridge import BridgeClient  # noqa: E402
from matching import Config  # noqa: E402
from watcher import Watcher  # noqa: E402


def make_config() -> Config:
    return Config(
        bridge_url=None,
        bridge_auto=True,
        bridge_token="sekrit",
        focus_file="/tmp/focus.json",
        poll_interval_ms=250,
        debounce_ms=300,
        default_client=None,
        rules=[],
    )


class StubClient:
    """Minimal stand-in for BridgeClient: records calls, exposes .url."""

    def __init__(self, url: str):
        self.url = url
        self.calls: list = []

    def navigate(self, **kwargs):
        self.calls.append(("navigate", kwargs))

    def restore(self, client=None):
        self.calls.append(("restore", client))

    def rules(self):
        self.calls.append(("rules", None))
        return {"rules": []}

    def health(self):
        self.calls.append(("health", None))
        return {"ok": True}


class UrlRerollTest(unittest.TestCase):
    def make_watcher(self, urls):
        """distinct client classes: real BridgeClient would try network on rules()."""
        config = make_config()
        calls = {"discover_count": 0}

        def discover():
            calls["discover_count"] += 1
            return urls[calls["discover_count"] - 1] if calls["discover_count"] - 1 < len(urls) else urls[-1]

        watcher = Watcher(config, StubClient("http://127.0.0.1:1111"), discover=discover)
        return watcher, calls

    def test_swaps_client_on_new_url(self):
        watcher, _ = self.make_watcher(["http://127.0.0.1:2222", "http://127.0.0.1:3333"])
        watcher._refresh_url()
        self.assertEqual(watcher.client.url, "http://127.0.0.1:2222")
        self.assertIsInstance(watcher.client, BridgeClient)
        self.assertEqual(watcher.client.token, "sekrit")

    def test_keeps_client_when_url_unchanged(self):
        watcher, _ = self.make_watcher(["http://127.0.0.1:1111"])
        original = watcher.client
        watcher._refresh_url()
        self.assertIs(watcher.client, original)

    def test_keeps_client_when_discovery_returns_none(self):
        watcher, _ = self.make_watcher([None])
        original = watcher.client
        watcher._refresh_url()
        self.assertIs(watcher.client, original)

    def test_keeps_client_without_resolver(self):
        config = make_config()
        watcher = Watcher(config, StubClient("http://127.0.0.1:1111"))
        original = watcher.client
        watcher._refresh_url()
        self.assertIs(watcher.client, original)


if __name__ == "__main__":
    unittest.main()