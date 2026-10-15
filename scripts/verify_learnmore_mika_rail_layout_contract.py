#!/usr/bin/env python3
"""Guard LearnMore desktop lyrics layout so the Mika rail is a real showcase column."""

from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LYRICS_CSS = ROOT / "LearnMore/wwwroot/css/lyrics.css"
MIKA_CSS = ROOT / "LearnMore/wwwroot/css/mika-avatar.css"
LYRICS_VIEW = ROOT / "LearnMore/Views/Lyrics/Index.cshtml"


def require(source: str, needle: str, message: str) -> None:
    if needle not in source:
        raise AssertionError(message)


def require_condition(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    lyrics_css = LYRICS_CSS.read_text(encoding="utf-8")
    mika_css = MIKA_CSS.read_text(encoding="utf-8")
    lyrics_view = LYRICS_VIEW.read_text(encoding="utf-8")

    require_condition(
        "~/css/lyrics.css" in lyrics_view
        and "~/css/mika-avatar.css" in lyrics_view
        and lyrics_view.index("~/css/lyrics.css") < lyrics_view.index("~/css/mika-avatar.css"),
        "lyrics page must load mika-avatar.css after lyrics.css so the final Mika rail stage styling wins",
    )

    for needle, message in [
        ("@media (min-width: 1200px)", "desktop lyrics layout must have a 1200px breakpoint"),
        ("grid-template-columns: minmax(400px, 1fr) minmax(310px, 0.82fr) minmax(300px, 0.78fr);", "desktop layout must reserve a prominent third column for Mika"),
        (".lyrics-section {\n        grid-column: 2;", "lyrics column must stay in the middle column on desktop"),
        (".lyrics-avatar-rail {\n        grid-column: 3;", "Mika rail must occupy the third desktop column"),
        ("grid-row: 1;", "Mika rail must start beside the first lyrics viewport instead of below the song"),
        ("position: sticky;", "Mika rail must stay visible while lyrics scroll"),
        ("top: 96px;", "sticky Mika rail must clear the top navigation"),
        ("radial-gradient(circle at 50% 18%", "Mika rail must have a visible stage background instead of a blank white void"),
        ("box-shadow: var(--shadow-md);", "Mika rail stage must read as a polished card"),
    ]:
        require(lyrics_css, needle, message)

    for needle, message in [
        (".lyrics-avatar-rail .mika-avatar-stage", "Mika rail stage must have lyrics-page-specific styling"),
        ("min-height: 420px;", "Mika rail must keep enough vertical room for full-body display"),
        ("overflow: visible;", "desktop Mika rail must not clip the Live2D body"),
        ("@media (min-width: 1200px)", "Mika stylesheet must carry final desktop rail-stage overrides because it loads after lyrics.css"),
        ("radial-gradient(circle at 50% 18%", "final Mika stylesheet must keep the visible stage background"),
        ("box-shadow: var(--shadow-md);", "final Mika stylesheet must keep the polished stage shadow"),
    ]:
        require(mika_css, needle, message)

    print("[learnmore-mika-rail-layout] ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
