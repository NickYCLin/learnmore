#!/usr/bin/env python3
"""Verify LearnMore About page exposes Mika avatar attribution links."""

from __future__ import annotations

import argparse
import os
import sys
import urllib.error
import urllib.request


DEFAULT_BASE_URL = "https://magicplus-design.serveirc.com/LearnMore"
DEFAULT_MIKA_BASE_URL = os.environ.get("MIKA_AVATAR_BASE_URL", "http://localhost:8081/mika-avatar")

EXPECTED_SNIPPETS = [
    "Mika 正式角色尚未完成 LearnMore 驗收",
    "LearnMore 角色狀態",
    "mao_pro Live2D embed",
    "Mika 製作狀態",
    "角色清單 API",
    "LearnMore 接入狀態",
    "mao_pro",
    "mika_live2d",
    "不會出現在 LearnMore 選項",
    "Live2D Cubism Sample Data",
    "AivisSpeech: まお",
]


def fetch_text(url: str) -> str:
    try:
        with urllib.request.urlopen(url, timeout=45) as response:
            status = getattr(response, "status", 200)
            body = response.read().decode("utf-8", "ignore")
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"HTTP {error.code}: {url}") from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"request failed: {url}: {error}") from error

    if status != 200:
        raise RuntimeError(f"unexpected HTTP status {status}: {url}")
    return body


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify LearnMore Mika attribution on About page.")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--mika-base-url", default=DEFAULT_MIKA_BASE_URL)
    args = parser.parse_args()

    url = args.base_url.rstrip("/") + "/User/About?verify=mika-about-attribution"
    mika_base_url = args.mika_base_url.rstrip("/")
    integration_url = mika_base_url + "/api/integration/learnmore"
    dynamic_snippets = [
        f"{mika_base_url}/api/avatars",
        f"{mika_base_url}/api/integration/learnmore",
        f"{mika_base_url}/handoff/mika-live2d",
    ]
    dynamic_alternatives = [
        (
            f"{mika_base_url}/embed?avatar=mao_pro&runtime=live2d",
            f"{mika_base_url}/embed?avatar=mao_pro&amp;runtime=live2d",
        ),
    ]

    body = fetch_text(url)
    missing = [
        snippet
        for snippet in [*EXPECTED_SNIPPETS, *dynamic_snippets]
        if snippet and snippet not in body
    ]
    missing.extend(
        " or ".join(alternatives)
        for alternatives in dynamic_alternatives
        if not any(snippet in body for snippet in alternatives)
    )
    if missing:
        print(
            {
                "ok": False,
                "url": url,
                "integrationUrl": integration_url,
                "missing": missing,
            },
            file=sys.stderr,
        )
        return 1

    print(
        {
            "ok": True,
            "url": url,
            "integrationUrl": integration_url,
            "checkedSnippetCount": len(EXPECTED_SNIPPETS) + len(dynamic_snippets),
        }
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
