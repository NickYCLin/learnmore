#!/usr/bin/env python3
"""Verify LearnMore can consume Mika Avatar Core embed-config contract."""

from __future__ import annotations

import argparse
import os
import json
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


DEFAULT_LEARNMORE_URL = (
    "https://magicplus-design.serveirc.com/LearnMore/Lyrics/"
    "ea3d8e96-fb5c-4bff-bf47-a9683e844eff"
)
DEFAULT_MIKA_BASE_URL = os.environ.get("MIKA_AVATAR_BASE_URL", "http://localhost:8081/mika-avatar")
EXPECTED_SAMPLE_AVATAR_IDS = ["mao_pro", "hiyori_pro"]
BLOCKED_LEARNMORE_AVATAR_IDS = ["mika_live2d", "mika_stretchy_test", "mika_formal_2d", "mika_vrm", "miara_pro", "kei_vowels_pro", "ren_foster"]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Verify LearnMore Mika embed-config contract.")
    parser.add_argument("--learnmore-url", default=DEFAULT_LEARNMORE_URL)
    parser.add_argument("--mika-base-url", default=DEFAULT_MIKA_BASE_URL)
    parser.add_argument("--json-output", type=Path)
    return parser.parse_args()


def fetch_text(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "LearnMore-Mika-Contract-Smoke/1.0"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read().decode("utf-8", errors="replace")


def fetch_json(url: str) -> Any:
    return json.loads(fetch_text(url))


def require(condition: bool, message: str, failures: list[str]) -> None:
    if not condition:
        failures.append(message)


def main() -> int:
    args = parse_args()
    mika_base_url = args.mika_base_url.rstrip("/")
    learnmore_url = args.learnmore_url
    embed_config_url = f"{mika_base_url}/api/integration/learnmore/embed-config"
    failures: list[str] = []

    embed_config = fetch_json(embed_config_url)
    integration = fetch_json(f"{mika_base_url}/api/integration/learnmore")
    ui = embed_config.get("ui") if isinstance(embed_config, dict) and isinstance(embed_config.get("ui"), dict) else {}
    readiness = embed_config.get("readiness") if isinstance(embed_config, dict) and isinstance(embed_config.get("readiness"), dict) else {}
    raw_available_avatar_ids = integration.get("availableAvatarIds") if isinstance(integration, dict) else []
    available_avatar_ids = raw_available_avatar_ids if isinstance(raw_available_avatar_ids, list) else []

    require(embed_config.get("avatarId") == "mao_pro", "embed-config avatarId must target mao_pro", failures)
    require(embed_config.get("runtime") == "live2d", "embed-config runtime must remain live2d for mao_pro", failures)
    require(embed_config.get("displayName") == "mao_pro", "embed-config displayName must be mao_pro", failures)
    require(embed_config.get("embedSrc") == f"{mika_base_url}/embed?avatar=mao_pro&runtime=live2d", "embed-config embedSrc must target mao_pro", failures)
    for avatar_id in EXPECTED_SAMPLE_AVATAR_IDS:
        require(avatar_id in available_avatar_ids, f"integration must expose official sample avatar {avatar_id}", failures)
    for avatar_id in BLOCKED_LEARNMORE_AVATAR_IDS:
        require(avatar_id not in available_avatar_ids, f"integration must not expose blocked avatar {avatar_id} to LearnMore", failures)
    require(ui.get("defaultConnectToMika") is True, "embed-config must default connect to Mika", failures)
    require(ui.get("showConnectionStatus") is False, "embed-config must hide connection status", failures)
    require(ui.get("showAskMika") is False, "embed-config must hide ask-Mika UI", failures)
    require(readiness.get("canSwitchToLive2D") is False, "embed-config must keep unfinished Mika Live2D switching disabled", failures)

    page_html = fetch_text(learnmore_url)
    require("data-mika-avatar-panel" in page_html, "LearnMore page must render Mika avatar panel", failures)
    require(f'data-avatar-base-url="{mika_base_url}"' in page_html, "Mika panel must point to Mika Avatar Core", failures)
    require("/js/mika-avatar.js" in page_html, "LearnMore page must load mika-avatar.js", failures)

    report = {
        "ready": not failures,
        "failureCount": len(failures),
        "failures": failures,
        "learnmoreUrl": learnmore_url,
        "embedConfigUrl": embed_config_url,
        "avatarId": embed_config.get("avatarId") if isinstance(embed_config, dict) else "",
        "runtime": embed_config.get("runtime") if isinstance(embed_config, dict) else "",
        "embedSrc": embed_config.get("embedSrc") if isinstance(embed_config, dict) else "",
        "availableAvatarIds": available_avatar_ids,
    }

    if args.json_output:
        args.json_output.parent.mkdir(parents=True, exist_ok=True)
        args.json_output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["ready"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
