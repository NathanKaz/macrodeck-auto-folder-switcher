"""Small HTTP client for the Macro Deck bridge plugin.

The bridge exposes a loopback-only REST surface in front of the plugin host's
deck navigation API. The base URL is discovered either from config
(``bridge.url``) or from the port file the plugin writes next to its socket
(``$XDG_RUNTIME_DIR/macrodeck-bridge.url``), which is the preferred mode.
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from typing import Any, Optional

PORT_FILE_NAME = "macrodeck-bridge.url"


class BridgeError(Exception):
    pass


def runtime_port_file() -> str:
    xdg = os.environ.get("XDG_RUNTIME_DIR")
    return os.path.join(xdg, PORT_FILE_NAME) if xdg else PORT_FILE_NAME


def discover_bridge_url(configured: Optional[str], auto: bool = True) -> Optional[str]:
    if auto:
        try:
            with open(runtime_port_file(), "r", encoding="utf-8") as fh:
                url = fh.read().strip()
            if url:
                return url
        except OSError:
            pass
    return configured


class BridgeClient:
    def __init__(self, url: str, token: Optional[str] = None):
        self.url = url.rstrip("/")
        self.token = token

    def _request(self, method: str, path: str, payload: Optional[dict] = None) -> dict:
        headers = {"Accept": "application/json"}
        if self.token:
            headers["X-Bridge-Token"] = self.token
        body = None
        if payload is not None:
            body = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(self.url + path, data=body, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                data = resp.read()
                return json.loads(data.decode("utf-8")) if data else {}
        except urllib.error.HTTPError as err:
            detail = ""
            try:
                detail = err.read().decode("utf-8", "replace")[:300]
            except OSError:
                pass
            raise BridgeError(f"{method} {path} -> HTTP {err.code}: {detail}") from err
        except (urllib.error.URLError, OSError, json.JSONDecodeError) as err:
            raise BridgeError(f"{method} {path} failed: {err}") from err

    def health(self) -> dict:
        return self._request("GET", "/health")

    def clients(self) -> list:
        return self._request("GET", "/clients").get("clients", [])

    def folders(self) -> list:
        return self._request("GET", "/folders").get("folders", [])

    def profiles(self) -> list:
        return self._request("GET", "/profiles").get("profiles", [])

    def rules(self) -> list:
        """In-app configured rules. Raises BridgeError when the session is down (503)."""
        return self._request("GET", "/rules").get("rules", [])

    def apps(self) -> list:
        """App ids seen in the focus history (suggestions for the settings form)."""
        return self._request("GET", "/apps").get("apps", [])

    def navigate(
        self,
        *,
        folder: Optional[str] = None,
        profile: Optional[str] = None,
        folder_id: Optional[str] = None,
        profile_id: Optional[str] = None,
        client: Optional[str] = None,
    ) -> dict:
        payload: dict[str, Any] = {}
        for key, val in (
            ("folder", folder),
            ("profile", profile),
            ("folderId", folder_id),
            ("profileId", profile_id),
            ("client", client),
        ):
            if val is not None:
                payload[key] = val
        return self._request("POST", "/navigate", payload)

    def restore(self, client: Optional[str] = None) -> dict:
        payload = {"client": client} if client is not None else {}
        return self._request("POST", "/restore", payload)

    def back(self, client: Optional[str] = None) -> dict:
        payload = {"client": client} if client is not None else {}
        return self._request("POST", "/back", payload)