from __future__ import annotations

import subprocess

import pytest
from fastapi.testclient import TestClient


@pytest.fixture()
def client(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("LEARNMORE_API_TOKEN", "test-token")
    monkeypatch.setenv("CODEX_BIN", "codex")

    from learnmore_api import main

    with TestClient(main.app) as test_client:
        yield test_client, main


def test_health(client) -> None:
    test_client, _ = client
    response = test_client.get("/health")
    assert response.status_code == 200
    body = response.json()
    assert body["backend"] == "codex"
    assert body["codexConfigured"] is True
    assert body["whisperConfigured"] is True


def test_word_alignment_keeps_contextual_reading_and_word_boundaries(client) -> None:
    _, main = client
    words = [
        main.WhisperWord(index=1, start=0.0, end=0.4, text="光"),
        main.WhisperWord(index=2, start=0.4, end=0.6, text="る"),
        main.WhisperWord(index=3, start=0.6, end=1.0, text="花"),
    ]
    segment = main.WhisperSegment(index=1, start=0.0, end=1.0, text="光る花", words=words)

    alignment = main.build_word_alignment_index([segment])

    assert alignment["stream"] == main.normalize_for_alignment("光る花")
    assert alignment["charToWord"][0] == words[0]
    assert alignment["charToWord"][-1] == words[-1]
    assert alignment["wordEndByIndex"][3] == len(alignment["stream"])


def test_word_alignment_works_without_reading_converter(client, monkeypatch) -> None:
    _, main = client
    monkeypatch.setattr(main, "get_kakasi_converter", lambda: None)
    word = main.WhisperWord(index=1, start=0.0, end=1.0, text="花")
    segment = main.WhisperSegment(index=1, start=0.0, end=1.0, text="花", words=[word])

    alignment = main.build_word_alignment_index([segment])

    assert alignment["stream"] == "花"
    assert alignment["charToWord"] == [word]


def test_translate_lines(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    async def fake_run_codex(prompt, settings):
        return {
            "translations": [
                {"lyricId": 1, "chinese": "第一行翻譯"},
                {"lyricId": 2, "chinese": "第二行翻譯"},
            ]
        }

    monkeypatch.setattr(main, "run_codex", fake_run_codex)

    response = test_client.post(
        "/v1/translate-lines",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "lines": [
                {"lyricId": 1, "japanese": "一行目"},
                {"lyricId": 2, "japanese": "二行目"},
            ],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["songUid"] == "song-1"
    assert [item["chinese"] for item in body["translations"]] == ["第一行翻譯", "第二行翻譯"]


def test_translate_rejects_mismatched_codex_ids(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    async def fake_run_codex(prompt, settings):
        return {"translations": [{"lyricId": 999, "chinese": "錯誤翻譯"}]}

    monkeypatch.setattr(main, "run_codex", fake_run_codex)

    response = test_client.post(
        "/v1/translate-lines",
        headers={"Authorization": "Bearer test-token"},
        json={"lines": [{"lyricId": 1, "japanese": "一行目"}]},
    )

    assert response.status_code == 502


def test_annotate_lines(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    async def fake_run_codex(prompt, settings):
        return {
            "annotations": [
                {
                    "lyricId": 1,
                    "japaneseRuby": "<ruby>君<rt>きみ</rt></ruby>が好き",
                    "roman": "kimi ga suki",
                }
            ]
        }

    monkeypatch.setattr(main, "run_codex", fake_run_codex)

    response = test_client.post(
        "/v1/annotate-lines",
        headers={"Authorization": "Bearer test-token"},
        json={"songUid": "song-1", "lines": [{"lyricId": 1, "japanese": "君が好き"}]},
    )

    assert response.status_code == 200
    body = response.json()
    assert body["songUid"] == "song-1"
    assert body["annotations"] == [
        {
            "lyricId": 1,
            "japanese": "君が好き",
            "japaneseRuby": "<ruby>君<rt>きみ</rt></ruby>が好き",
            "roman": "kimi ga suki",
        }
    ]


def test_annotate_retries_missing_ids(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client
    calls = 0

    async def fake_run_codex(prompt, settings):
        nonlocal calls
        calls += 1
        if calls == 1:
            return {
                "annotations": [
                    {
                        "lyricId": 1,
                        "japaneseRuby": "<ruby>君<rt>きみ</rt></ruby>が好き",
                        "roman": "kimi ga suki",
                    }
                ]
            }
        return {
            "annotations": [
                {
                    "lyricId": 2,
                    "japaneseRuby": "<ruby>夢<rt>ゆめ</rt></ruby>を見る",
                    "roman": "yume wo miru",
                }
            ]
        }

    monkeypatch.setattr(main, "run_codex", fake_run_codex)

    response = test_client.post(
        "/v1/annotate-lines",
        headers={"Authorization": "Bearer test-token"},
        json={
            "lines": [
                {"lyricId": 1, "japanese": "君が好き"},
                {"lyricId": 2, "japanese": "夢を見る"},
            ]
        },
    )

    assert response.status_code == 200
    assert calls == 2
    assert [item["lyricId"] for item in response.json()["annotations"]] == [1, 2]


def test_annotate_falls_back_for_empty_values(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    async def fake_run_codex(prompt, settings):
        return {"annotations": [{"lyricId": 1, "japaneseRuby": "", "roman": "kimi ga suki"}]}

    monkeypatch.setattr(main, "run_codex", fake_run_codex)

    response = test_client.post(
        "/v1/annotate-lines",
        headers={"Authorization": "Bearer test-token"},
        json={"lines": [{"lyricId": 1, "japanese": "君が好き"}]},
    )

    assert response.status_code == 200
    annotation = response.json()["annotations"][0]
    assert annotation["japaneseRuby"] == "君が好き"
    assert annotation["roman"]


def test_generate_song_aliases(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    async def fake_run_codex(prompt, settings):
        assert "繁體中文搜尋 alias" in prompt
        return {
            "songs": [
                {
                    "songUid": "song-1",
                    "aliases": [
                        {
                            "aliasText": "貓的嫉妒 中文歌詞",
                            "aliasType": "alternate_title",
                            "note": "合理中文搜尋詞",
                        },
                        {
                            "aliasText": "貓的嫉妒 中文歌詞",
                            "aliasType": "alternate_title",
                            "note": "duplicate",
                        },
                    ],
                }
            ]
        }

    monkeypatch.setattr(main, "run_codex", fake_run_codex)

    response = test_client.post(
        "/v1/generate-song-aliases",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songs": [
                {
                    "songUid": "song-1",
                    "title": "猫にジェラシー",
                    "artist": "あいみょん",
                    "performer": "あいみょん",
                    "youtubeUrl": "https://www.youtube.com/watch?v=test",
                }
            ]
        },
    )

    assert response.status_code == 200
    assert response.json() == {
        "songs": [
            {
                "songUid": "song-1",
                "aliases": [
                    {
                        "aliasText": "貓的嫉妒 中文歌詞",
                        "aliasType": "alternate_title",
                        "note": "合理中文搜尋詞",
                    }
                ],
            }
        ]
    }


def test_generate_song_aliases_rejects_non_cjk_alias(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    async def fake_run_codex(prompt, settings):
        return {
            "songs": [
                {
                    "songUid": "song-1",
                    "aliases": [{"aliasText": "Neko ni Jealousy", "aliasType": "alternate_title"}],
                }
            ]
        }

    monkeypatch.setattr(main, "run_codex", fake_run_codex)

    response = test_client.post(
        "/v1/generate-song-aliases",
        headers={"Authorization": "Bearer test-token"},
        json={"songs": [{"songUid": "song-1", "title": "猫にジェラシー"}]},
    )

    assert response.status_code == 502


def test_translate_requires_token(client) -> None:
    test_client, _ = client
    response = test_client.post(
        "/v1/translate-lines",
        json={"lines": [{"lyricId": 1, "japanese": "一行目"}]},
    )
    assert response.status_code == 401


def test_transcribe_youtube(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_sync(request, settings):
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem",
            durationSeconds=12.3,
            segments=[
                main.WhisperSegment(index=1, start=0.0, end=2.5, text="君が好き")
            ],
        )

    monkeypatch.setattr(main, "transcribe_youtube_sync", fake_transcribe_youtube_sync)

    response = test_client.post(
        "/v1/transcribe-youtube",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["songUid"] == "song-1"
    assert body["segments"] == [
        {"index": 1, "start": 0.0, "end": 2.5, "text": "君が好き", "words": []}
    ]


def test_high_accuracy_align(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines=None):
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem",
            durationSeconds=8.0,
            segments=[
                main.WhisperSegment(
                    index=1,
                    start=1.0,
                    end=2.0,
                    text="君が好き",
                    words=[
                        main.WhisperWord(index=1, start=1.0, end=1.5, text="君が"),
                        main.WhisperWord(index=2, start=1.5, end=2.0, text="好き"),
                    ],
                ),
                main.WhisperSegment(index=2, start=4.0, end=5.0, text="夢を見る"),
            ],
        )

    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", fake_transcribe_youtube_for_high_accuracy_sync)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [
                {"lyricId": 1, "japanese": "君が好き", "currentStart": 0.0},
                {"lyricId": 2, "japanese": "夢を見る", "currentStart": 3.0},
            ],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["songUid"] == "song-1"
    assert body["model"].endswith("+high-accuracy-align")
    assert body["matchedCount"] == 2
    assert body["totalCount"] == 2
    assert [line["start"] for line in body["alignedLines"]] == [1.0, 4.0]


def test_high_accuracy_align_prefers_current_start_near_duplicate(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines=None):
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem",
            durationSeconds=120.0,
            segments=[
                main.WhisperSegment(index=1, start=10.0, end=12.0, text="同じ言葉"),
                main.WhisperSegment(index=2, start=100.0, end=102.0, text="同じ言葉"),
            ],
        )

    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", fake_transcribe_youtube_for_high_accuracy_sync)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [{"lyricId": 1, "japanese": "同じ言葉", "currentStart": 99.0}],
        },
    )

    assert response.status_code == 200
    line = response.json()["alignedLines"][0]
    assert line["start"] == 100.0
    assert line["source"] == "whisper_timing_hint_match"


def test_high_accuracy_align_does_not_count_context_timestamp_as_matched(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines=None):
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem",
            durationSeconds=30.0,
            segments=[
                main.WhisperSegment(index=1, start=5.0, end=6.0, text="朝が来る"),
                main.WhisperSegment(index=2, start=20.0, end=21.0, text="夜が終わる"),
            ],
        )

    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", fake_transcribe_youtube_for_high_accuracy_sync)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [
                {"lyricId": 1, "japanese": "朝が来る", "currentStart": 4.8},
                {"lyricId": 2, "japanese": "短い合いの手", "currentStart": 10.0},
                {"lyricId": 3, "japanese": "夜が終わる", "currentStart": 19.8},
            ],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["matchedCount"] == 2
    assert body["totalCount"] == 3
    assert body["alignedLines"][1]["source"] == "current_timestamp_context"


def test_high_accuracy_align_prefers_vocals_stem(client, monkeypatch: pytest.MonkeyPatch, tmp_path) -> None:
    test_client, main = client
    vocals_path = tmp_path / "song_(Vocals).wav"
    vocals_path.write_bytes(b"vocals")
    main.app.state.settings.uvr_command_template = "uvr --input {input} --output {output_dir}"

    def fake_separate_youtube_sync(request, settings):
        return main.SeparateYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            model=request.model,
            outputDir=str(tmp_path),
            stems=[
                main.AudioStem(
                    kind="vocals",
                    path=str(vocals_path),
                    fileName=vocals_path.name,
                    sizeBytes=vocals_path.stat().st_size,
                )
            ],
        )

    def fake_align_lyric_lines_audio_file_with_whisperx(audio_path, lyric_lines, language, settings):
        assert audio_path == vocals_path
        assert [line.japanese for line in lyric_lines] == ["君が好き"]
        segment = main.WhisperSegment(
            index=1,
            start=1.0,
            end=2.0,
            text="君が好き",
            words=[
                main.WhisperWord(index=1, start=1.0, end=1.5, text="君が"),
                main.WhisperWord(index=2, start=1.5, end=2.0, text="好き"),
            ],
        )
        segment._asr_anchor_score = 1.0
        return (
            [segment],
            10.0,
        )

    def fail_transcribe_original_audio(*_args, **_kwargs):
        raise AssertionError("original audio should not be transcribed when vocals already match every line")

    monkeypatch.setattr(main, "separate_youtube_sync", fake_separate_youtube_sync)
    monkeypatch.setattr(main, "align_lyric_lines_audio_file_with_whisperx", fake_align_lyric_lines_audio_file_with_whisperx)
    monkeypatch.setattr(main, "transcribe_youtube_sync", fail_transcribe_original_audio)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [{"lyricId": 1, "japanese": "君が好き", "currentStart": 1.0}],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["model"] == "medium+vocals-stem+whisperx-lyrics+asr-refined+high-accuracy-align"
    assert body["matchedCount"] == 1
    assert body["alignedLines"][0]["start"] == 1.0
    assert body["alignedLines"][0]["source"] == "whisperx_lyric_forced_alignment_asr_verified"


def test_refine_lyric_segments_uses_asr_word_anchor(client) -> None:
    _, main = client

    lyric_segments = [
        main.WhisperSegment(
            index=1,
            start=36.481,
            end=38.9,
            text="いちどきりのきょうというひ",
            words=[
                main.WhisperWord(index=1, start=36.481, end=36.7, text="いち"),
                main.WhisperWord(index=2, start=36.7, end=38.9, text="どきりのきょうというひ"),
            ],
        )
    ]
    lyric_lines = [
        main.HighAccuracyLyricLine(
            lyricId=6,
            japanese="いちどきりのきょうというひ",
            currentStart=36.481,
        )
    ]
    asr_segments = [
        main.WhisperSegment(
            index=1,
            start=32.0,
            end=40.54,
            text="かなっているのはいちどきりのきょうというひ",
            words=[
                main.WhisperWord(index=10, start=34.22, end=35.56, text="のは"),
                main.WhisperWord(index=11, start=35.56, end=36.12, text="いち"),
                main.WhisperWord(index=12, start=36.12, end=36.42, text="ど"),
                main.WhisperWord(index=13, start=36.42, end=36.76, text="き"),
                main.WhisperWord(index=14, start=36.76, end=37.26, text="り"),
                main.WhisperWord(index=15, start=37.26, end=37.5, text="の"),
                main.WhisperWord(index=16, start=37.5, end=38.04, text="きょう"),
                main.WhisperWord(index=17, start=38.04, end=38.7, text="という"),
                main.WhisperWord(index=18, start=38.7, end=40.54, text="ひ"),
            ],
        )
    ]

    refined = main.refine_lyric_segments_with_asr_word_anchors(
        lyric_segments,
        lyric_lines,
        asr_segments,
    )

    assert refined[0].start == 35.56
    assert refined[0].end == 40.54
    assert refined[0]._asr_anchor_score == 1.0


def test_refine_lyric_segments_falls_back_to_full_asr_alignment(client) -> None:
    _, main = client

    lyric_segments = [
        main.WhisperSegment(
            index=1,
            start=60.0,
            end=62.0,
            text="鮮やかに光るその色に",
            words=[
                main.WhisperWord(index=1, start=60.0, end=60.5, text="鮮やか"),
                main.WhisperWord(index=2, start=60.5, end=62.0, text="光るその色に"),
            ],
        )
    ]
    lyric_lines = [
        main.HighAccuracyLyricLine(
            lyricId=1,
            japanese="鮮やかに光るその色に",
            currentStart=19.0,
        )
    ]
    asr_segments = [
        main.WhisperSegment(
            index=1,
            start=18.84,
            end=28.02,
            text="鮮やかに光るその色に 囚われて歩みを止めた",
            words=[
                main.WhisperWord(index=1, start=18.84, end=19.74, text="鮮"),
                main.WhisperWord(index=2, start=19.74, end=19.86, text="や"),
                main.WhisperWord(index=3, start=19.86, end=20.32, text="か"),
                main.WhisperWord(index=4, start=20.32, end=20.76, text="に"),
                main.WhisperWord(index=5, start=20.76, end=21.36, text="光"),
                main.WhisperWord(index=6, start=21.36, end=21.74, text="る"),
                main.WhisperWord(index=7, start=21.74, end=22.5, text="その"),
                main.WhisperWord(index=8, start=22.5, end=22.92, text="色"),
                main.WhisperWord(index=9, start=22.92, end=23.9, text="に"),
            ],
        )
    ]
    asr_aligned = main.align_high_accuracy_lyrics_to_whisper(lyric_lines, asr_segments, 120.0)

    refined = main.refine_lyric_segments_with_asr_word_anchors(
        lyric_segments,
        lyric_lines,
        asr_segments,
        asr_aligned,
    )

    assert refined[0].start == 18.84
    assert refined[0].end == 23.9
    assert refined[0]._asr_anchor_score == 1.0


def test_global_asr_sequence_alignment_handles_merged_segments(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=1, japanese="鮮やかに光るその色に", currentStart=19.0),
        main.HighAccuracyLyricLine(lyricId=2, japanese="捕らわれて歩みを止めた", currentStart=24.0),
        main.HighAccuracyLyricLine(lyricId=3, japanese="無くしてただ切なくて", currentStart=29.0),
    ]
    asr_segments = [
        main.WhisperSegment(
            index=1,
            start=18.84,
            end=28.02,
            text="鮮やかに光るその色に 囚われて歩みを止めた",
            words=[
                main.WhisperWord(index=1, start=18.84, end=19.74, text="鮮"),
                main.WhisperWord(index=2, start=19.74, end=19.86, text="や"),
                main.WhisperWord(index=3, start=19.86, end=20.32, text="か"),
                main.WhisperWord(index=4, start=20.32, end=20.76, text="に"),
                main.WhisperWord(index=5, start=20.76, end=21.36, text="光"),
                main.WhisperWord(index=6, start=21.36, end=21.74, text="る"),
                main.WhisperWord(index=7, start=21.74, end=22.5, text="その"),
                main.WhisperWord(index=8, start=22.5, end=22.92, text="色"),
                main.WhisperWord(index=9, start=22.92, end=23.9, text="に"),
                main.WhisperWord(index=10, start=23.9, end=24.04, text="囚"),
                main.WhisperWord(index=11, start=24.04, end=24.36, text="われて"),
                main.WhisperWord(index=12, start=24.96, end=25.78, text="歩"),
                main.WhisperWord(index=13, start=25.78, end=26.18, text="み"),
                main.WhisperWord(index=14, start=26.18, end=26.62, text="を"),
                main.WhisperWord(index=15, start=26.62, end=27.18, text="止"),
                main.WhisperWord(index=16, start=27.18, end=27.4, text="め"),
                main.WhisperWord(index=17, start=27.4, end=28.02, text="た"),
            ],
        ),
        main.WhisperSegment(
            index=2,
            start=28.02,
            end=36.12,
            text="失くしてただ切なくて 追い求めたのは幻",
            words=[
                main.WhisperWord(index=18, start=28.02, end=29.12, text="失"),
                main.WhisperWord(index=19, start=29.12, end=29.38, text="く"),
                main.WhisperWord(index=20, start=29.38, end=30.12, text="して"),
                main.WhisperWord(index=21, start=30.12, end=30.66, text="ただ"),
                main.WhisperWord(index=22, start=30.66, end=31.08, text="切"),
                main.WhisperWord(index=23, start=31.08, end=31.62, text="なく"),
                main.WhisperWord(index=24, start=31.62, end=32.3, text="て"),
            ],
        ),
    ]

    aligned = main.align_high_accuracy_lyrics_to_asr_sequence(lyric_lines, asr_segments, 120.0)

    assert [line.source for line in aligned] == ["whisper_global_sequence_match"] * 3
    assert [line.start for line in aligned] == [18.84, 23.9, 28.02]
    assert all(main.is_reliable_audio_alignment(line) for line in aligned)


def test_whisperx_alignment_fills_short_gaps_after_global_sequence_verification(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=1, japanese="一行目", currentStart=1.0),
        main.HighAccuracyLyricLine(lyricId=2, japanese="二行目", currentStart=2.0),
        main.HighAccuracyLyricLine(lyricId=3, japanese="三行目", currentStart=3.0),
        main.HighAccuracyLyricLine(lyricId=4, japanese="四行目", currentStart=4.0),
    ]
    segments = []
    for index, text in enumerate(["一行目", "二行目", "三行目", "四行目"], start=1):
        segment = main.WhisperSegment(
            index=index,
            start=float(index),
            end=float(index) + 0.8,
            text=text,
            words=[main.WhisperWord(index=index, start=float(index), end=float(index) + 0.8, text=text)],
        )
        if index != 2:
            segment._asr_anchor_score = 1.0
            segment._asr_anchor_source = "whisper_global_sequence_match"
        segments.append(segment)

    aligned = main.align_high_accuracy_lyrics_to_whisperx_lyrics(lyric_lines, segments, 10.0)

    assert [line.source for line in aligned] == [
        "whisper_global_sequence_match",
        "whisper_lyric_forced_alignment_global_context",
        "whisper_global_sequence_match",
        "whisper_global_sequence_match",
    ]
    assert [line.start for line in aligned] == [1.0, 2.0, 3.0, 4.0]
    assert all(main.is_reliable_audio_alignment(line) for line in aligned)


def test_contextual_forced_alignment_uses_previous_anchor_for_tail_gap(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=1, japanese="一行目", currentStart=1.0),
        main.HighAccuracyLyricLine(lyricId=2, japanese="二行目", currentStart=2.0),
        main.HighAccuracyLyricLine(lyricId=3, japanese="さようなら", currentStart=10.0),
        main.HighAccuracyLyricLine(lyricId=4, japanese="戻れないなら", currentStart=14.0),
    ]
    segments = []
    for index, start, end, text in [
        (1, 1.0, 1.8, "一行目"),
        (2, 2.0, 2.8, "二行目"),
        (3, 10.0, 13.0, "さようなら"),
        (4, 20.0, 23.0, "戻れないなら"),
    ]:
        segment = main.WhisperSegment(
            index=index,
            start=start,
            end=end,
            text=text,
            words=[main.WhisperWord(index=index, start=start, end=end, text=text)],
        )
        if index != 4:
            segment._asr_anchor_score = 1.0
            segment._asr_anchor_source = "whisper_global_sequence_match"
        segments.append(segment)

    aligned = main.align_high_accuracy_lyrics_to_whisperx_lyrics(lyric_lines, segments, 30.0)

    assert aligned[3].source == "whisper_lyric_forced_alignment_global_context"
    assert aligned[3].start == 13.0
    assert main.is_reliable_audio_alignment(aligned[3])


def test_contextual_forced_alignment_accepts_majority_verified_song(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=index + 1, japanese=f"歌詞{index + 1}", currentStart=float(index))
        for index in range(100)
    ]
    segments = []
    for index, line in enumerate(lyric_lines):
        segment = main.WhisperSegment(
            index=index + 1,
            start=float(index),
            end=float(index) + 0.8,
            text=line.japanese,
            words=[main.WhisperWord(index=index + 1, start=float(index), end=float(index) + 0.8, text=line.japanese)],
        )
        if index < 50 or 50 < index <= 67:
            segment._asr_anchor_score = 1.0
            segment._asr_anchor_source = "whisper_global_sequence_match"
        segments.append(segment)

    aligned = main.align_high_accuracy_lyrics_to_whisperx_lyrics(lyric_lines, segments, 120.0)

    assert aligned[50].source == "whisper_lyric_forced_alignment_global_context"
    assert aligned[50].start == 50.0
    assert main.is_reliable_audio_alignment(aligned[50])
    assert aligned[80].source == "current_timestamp_context"


def test_contextual_forced_alignment_allows_gap_between_verified_anchors(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=index + 1, japanese=f"歌詞{index + 1}", currentStart=float(index))
        for index in range(13)
    ]
    segments = []
    for index, line in enumerate(lyric_lines):
        segment = main.WhisperSegment(
            index=index + 1,
            start=float(index * 2),
            end=float(index * 2) + 1.0,
            text=line.japanese,
            words=[
                main.WhisperWord(
                    index=index + 1,
                    start=float(index * 2),
                    end=float(index * 2) + 1.0,
                    text=line.japanese,
                )
            ],
        )
        if index not in {4, 5, 6, 7, 8}:
            segment._asr_anchor_score = 1.0
            segment._asr_anchor_source = "whisper_global_sequence_match"
        segments.append(segment)

    aligned = main.align_high_accuracy_lyrics_to_whisperx_lyrics(lyric_lines, segments, 32.0)

    assert [line.source for line in aligned[4:9]] == [
        "whisper_lyric_forced_alignment_global_context",
        "whisper_lyric_forced_alignment_global_context",
        "whisper_lyric_forced_alignment_global_context",
        "whisper_lyric_forced_alignment_global_context",
        "whisper_lyric_forced_alignment_global_context",
    ]
    assert all(main.is_reliable_audio_alignment(line) for line in aligned)


def test_whisperx_alignment_uses_line_specific_asr_anchor_when_forced_text_differs(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=1, japanese="Thanks to my dad and mom", currentStart=157.4),
    ]
    segment = main.WhisperSegment(
        index=1,
        start=157.44,
        end=159.06,
        text="オーライ",
        words=[
            main.WhisperWord(index=1, start=157.44, end=157.58, text="thanks"),
            main.WhisperWord(index=2, start=157.58, end=157.84, text="to"),
            main.WhisperWord(index=3, start=157.84, end=158.06, text="my"),
            main.WhisperWord(index=4, start=158.06, end=158.28, text="dad"),
            main.WhisperWord(index=5, start=158.28, end=158.64, text="and"),
            main.WhisperWord(index=6, start=158.64, end=159.06, text="mom"),
        ],
    )
    segment._asr_anchor_score = 0.99
    segment._asr_anchor_source = "whisper_local_asr_anchor_match"

    aligned = main.align_high_accuracy_lyrics_to_whisperx_lyrics(lyric_lines, [segment], 180.0)

    assert aligned[0].source == "whisper_local_asr_anchor_match"
    assert aligned[0].start == 157.44
    assert main.is_reliable_audio_alignment(aligned[0])


def test_whisperx_lyric_transcript_prefers_asr_anchors_over_current_starts(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=1, japanese="一行目", currentStart=100.0),
        main.HighAccuracyLyricLine(lyricId=2, japanese="二行目", currentStart=101.0),
        main.HighAccuracyLyricLine(lyricId=3, japanese="三行目", currentStart=102.0),
    ]
    anchors = [
        main.AlignedLyricLine(
            lyricId=1,
            japanese="一行目",
            start=10.0,
            end=12.0,
            source="whisper_global_sequence_match",
            score=1.0,
        ),
        main.AlignedLyricLine(
            lyricId=3,
            japanese="三行目",
            start=30.0,
            end=32.0,
            source="whisper_global_sequence_match",
            score=1.0,
        ),
    ]

    transcript = main.build_whisperx_lyric_transcript(lyric_lines, 40.0, anchors)

    assert transcript[0]["start"] == 8.5
    assert transcript[1]["start"] == 18.5
    assert transcript[1]["end"] == 31.5
    assert transcript[2]["start"] == 28.5


def test_whisperx_lyric_transcript_keeps_current_starts_when_they_match_anchors(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=1, japanese="一行目", currentStart=10.0),
        main.HighAccuracyLyricLine(lyricId=2, japanese="二行目", currentStart=22.0),
        main.HighAccuracyLyricLine(lyricId=3, japanese="三行目", currentStart=30.0),
    ]
    anchors = [
        main.AlignedLyricLine(
            lyricId=1,
            japanese="一行目",
            start=10.2,
            end=12.0,
            source="whisper_global_sequence_match",
            score=1.0,
        ),
        main.AlignedLyricLine(
            lyricId=3,
            japanese="三行目",
            start=30.1,
            end=32.0,
            source="whisper_global_sequence_match",
            score=1.0,
        ),
    ]

    transcript = main.build_whisperx_lyric_transcript(lyric_lines, 40.0, anchors)

    assert transcript[1]["start"] == 20.5
    assert transcript[1]["end"] == 31.5


def test_whisperx_lyric_transcript_accepts_moderate_anchor_ratio_when_hints_match(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=index + 1, japanese=f"歌詞{index + 1}", currentStart=float(index * 3))
        for index in range(59)
    ]
    anchors = [
        (
            index,
            float(index * 3) + 0.2,
        )
        for index in range(24)
    ]

    assert main.should_use_current_start_hints(lyric_lines, len(lyric_lines), 190.0, anchors)


def test_latin_fuzzy_asr_anchor_matches_common_english_asr_variants(client) -> None:
    _, main = client

    asr_segments = [
        main.WhisperSegment(
            index=1,
            start=80.0,
            end=84.2,
            text="I gotta go Let's go",
            words=[
                main.WhisperWord(index=1, start=80.0, end=80.2, text="I"),
                main.WhisperWord(index=2, start=80.2, end=80.7, text="gotta"),
                main.WhisperWord(index=3, start=80.7, end=81.0, text="go"),
                main.WhisperWord(index=4, start=81.0, end=81.6, text="Let's"),
                main.WhisperWord(index=5, start=81.6, end=82.0, text="go"),
            ],
        )
    ]

    match = main.find_asr_word_anchor_for_lyric_line(
        "I got to go, way to go!",
        main.build_word_alignment_index(asr_segments),
        80.0,
        70.0,
        None,
        current_start=80.0,
        next_current_start=84.0,
    )

    assert match is not None
    assert match[0] == 80.0
    assert match[3].text == "go"
    assert match[4] >= 0.58


def test_latin_fuzzy_asr_anchor_handles_walk_work_road_variants(client) -> None:
    _, main = client

    asr_segments = [
        main.WhisperSegment(
            index=1,
            start=9.8,
            end=12.8,
            text="Let's work that road",
            words=[
                main.WhisperWord(index=1, start=9.8, end=10.2, text="Let"),
                main.WhisperWord(index=2, start=10.2, end=10.3, text="'s"),
                main.WhisperWord(index=3, start=10.3, end=10.7, text="work"),
                main.WhisperWord(index=4, start=10.7, end=11.1, text="that"),
                main.WhisperWord(index=5, start=11.1, end=11.6, text="road"),
            ],
        )
    ]

    match = main.find_asr_word_anchor_for_lyric_line(
        "Let's walk the road 立ち止まらず",
        main.build_word_alignment_index(asr_segments),
        10.0,
        0.0,
        None,
        current_start=10.0,
        next_current_start=14.0,
    )

    assert match is not None
    assert match[0] == 9.8
    assert match[4] >= 0.58


def test_katakana_english_fuzzy_asr_anchor_matches_all_right(client) -> None:
    _, main = client

    asr_segments = [
        main.WhisperSegment(
            index=1,
            start=155.9,
            end=157.0,
            text="All right",
            words=[
                main.WhisperWord(index=1, start=155.9, end=156.34, text="All"),
                main.WhisperWord(index=2, start=156.34, end=157.0, text="right"),
            ],
        )
    ]

    match = main.find_asr_word_anchor_for_lyric_line(
        "オーライ",
        main.build_word_alignment_index(asr_segments),
        212.0,
        150.0,
        None,
        current_start=156.0,
        next_current_start=158.0,
    )

    assert match is not None
    assert match[0] == 155.9
    assert match[4] >= 0.58


def test_latin_fuzzy_asr_anchor_starts_at_first_latin_word_after_japanese_prefix(client) -> None:
    _, main = client

    asr_segments = [
        main.WhisperSegment(
            index=1,
            start=53.6,
            end=61.2,
            text="すぐそばにいるよ Hey! Hey! Everyone has mission",
            words=[
                main.WhisperWord(index=1, start=53.6, end=54.1, text="すぐ"),
                main.WhisperWord(index=2, start=54.1, end=54.8, text="そば"),
                main.WhisperWord(index=3, start=54.8, end=55.4, text="に"),
                main.WhisperWord(index=4, start=55.4, end=57.74, text="いるよ"),
                main.WhisperWord(index=5, start=57.74, end=58.2, text="Hey!"),
                main.WhisperWord(index=6, start=58.2, end=58.7, text="Hey!"),
                main.WhisperWord(index=7, start=58.7, end=59.5, text="Everyone"),
                main.WhisperWord(index=8, start=59.5, end=59.9, text="has"),
                main.WhisperWord(index=9, start=59.9, end=61.16, text="mission"),
            ],
        )
    ]

    match = main.find_asr_word_anchor_for_lyric_line(
        "(Hey! Hey!) Everyone has a mission",
        main.build_word_alignment_index(asr_segments),
        55.4,
        53.0,
        None,
        current_start=55.4,
        next_current_start=61.2,
    )

    assert match is not None
    assert match[0] == 57.74
    assert match[2].text == "Hey!"


def test_internal_forced_sequence_rejects_alignment_far_from_current_start(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=1, japanese="全うするために", currentStart=61.8),
    ]
    segment = main.WhisperSegment(
        index=1,
        start=83.8,
        end=84.3,
        text="全うするために",
        words=[main.WhisperWord(index=1, start=83.8, end=84.3, text="全うするために")],
    )
    segment._forced_lyric_alignment = True

    aligned = main.build_internal_forced_sequence_alignment_by_id(lyric_lines, [segment], 120.0)

    assert aligned == {}


def test_promote_contextual_timestamp_bridge_when_song_is_mostly_verified(client) -> None:
    _, main = client

    aligned = []
    for index in range(20):
        source = "current_timestamp_context" if index in {5, 6, 7} else "whisper_global_sequence_match"
        score = 0.34 if source == "current_timestamp_context" else 1.0
        aligned.append(
            main.AlignedLyricLine(
                lyricId=index + 1,
                japanese=f"歌詞{index + 1}",
                start=float(index * 2),
                end=float(index * 2) + 1.0,
                source=source,
                score=score,
            )
        )

    promoted = main.promote_contextual_timestamp_bridges(aligned)

    assert promoted[5].source == "whisper_contextual_timestamp_bridge"
    assert promoted[5].score == 0.85
    assert [line.source for line in promoted[5:8]] == ["whisper_contextual_timestamp_bridge"] * 3
    assert all(main.is_reliable_audio_alignment(line) for line in promoted[5:8])


def test_promote_low_score_internal_forced_alignment_as_context_bridge(client) -> None:
    _, main = client

    aligned = []
    for index in range(20):
        source = "whisper_global_sequence_match"
        score = 1.0
        if index in {10, 11}:
            source = "whisper_lyric_forced_alignment_internal"
            score = 0.0
        if index == 12:
            source = "current_timestamp_context"
            score = 0.34
        aligned.append(
            main.AlignedLyricLine(
                lyricId=index + 1,
                japanese=f"歌詞{index + 1}",
                start=float(index * 2),
                end=float(index * 2) + 1.0,
                source=source,
                score=score,
            )
        )

    promoted = main.promote_contextual_timestamp_bridges(aligned)

    assert [line.source for line in promoted[10:13]] == ["whisper_contextual_timestamp_bridge"] * 3
    assert all(main.is_reliable_audio_alignment(line) for line in promoted[10:13])


def test_stabilize_unreliable_lines_uses_current_timeline_when_verified_deltas_match(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=index + 1, japanese=f"Line {index + 1}", currentStart=float(index * 3))
        for index in range(20)
    ]
    aligned = []
    for index, line in enumerate(lyric_lines):
        source = "whisper_local_asr_anchor_match"
        score = 0.99
        start = float(index * 3) + 0.2
        if 6 <= index <= 13:
            source = "current_timestamp_context"
            score = 0.34
            start = float(index * 3)
        aligned.append(
            main.AlignedLyricLine(
                lyricId=line.lyricId,
                japanese=line.japanese,
                start=start,
                end=start + 1.0,
                source=source,
                score=score,
            )
        )

    stabilized = main.stabilize_unreliable_lines_with_current_start_context(lyric_lines, aligned, 80.0)

    assert [line.source for line in stabilized[6:14]] == ["whisper_contextual_timestamp_bridge"] * 8
    assert all(main.is_reliable_audio_alignment(line) for line in stabilized)


def test_stabilize_unreliable_lines_ignores_current_timeline_when_verified_deltas_are_large(client) -> None:
    _, main = client

    lyric_lines = [
        main.HighAccuracyLyricLine(lyricId=index + 1, japanese=f"Line {index + 1}", currentStart=float(index * 3))
        for index in range(12)
    ]
    aligned = [
        main.AlignedLyricLine(
            lyricId=line.lyricId,
            japanese=line.japanese,
            start=float(index * 3) + 30.0,
            end=float(index * 3) + 31.0,
            source="whisper_local_asr_anchor_match",
            score=0.99,
        )
        for index, line in enumerate(lyric_lines)
    ]

    stabilized = main.stabilize_unreliable_lines_with_current_start_context(lyric_lines, aligned, 80.0)

    assert [line.start for line in stabilized] == [line.start for line in aligned]


def test_high_accuracy_align_does_not_count_unverified_whisperx_lyrics(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines=None):
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem+whisperx-lyrics+asr-refined",
            durationSeconds=8.0,
            segments=[
                main.WhisperSegment(
                    index=1,
                    start=1.0,
                    end=2.0,
                    text="錯誤歌詞",
                    words=[
                        main.WhisperWord(index=1, start=1.0, end=1.5, text="錯誤"),
                        main.WhisperWord(index=2, start=1.5, end=2.0, text="歌詞"),
                    ],
                )
            ],
        )

    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", fake_transcribe_youtube_for_high_accuracy_sync)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [{"lyricId": 1, "japanese": "錯誤歌詞", "currentStart": 1.0}],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["matchedCount"] == 0
    assert body["totalCount"] == 1
    assert body["alignedLines"][0]["source"] == "current_timestamp_context"


def test_high_accuracy_align_counts_internal_forced_lyric_alignment(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines=None):
        segment = main.WhisperSegment(
            index=1,
            start=1.0,
            end=2.0,
            text="正しい歌詞",
            words=[
                main.WhisperWord(index=1, start=1.0, end=1.4, text="正しい"),
                main.WhisperWord(index=2, start=1.4, end=2.0, text="歌詞"),
            ],
        )
        segment._forced_lyric_alignment = True
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem+whisperx-lyrics+asr-refined",
            durationSeconds=8.0,
            segments=[segment],
        )

    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", fake_transcribe_youtube_for_high_accuracy_sync)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [{"lyricId": 1, "japanese": "正しい歌詞", "currentStart": 1.0}],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["matchedCount"] == 1
    assert body["totalCount"] == 1
    assert body["alignedLines"][0]["source"] == "whisperx_lyric_forced_alignment_sequence"


def test_high_accuracy_align_does_not_count_latin_forced_sequence_as_reliable(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines=None):
        segment = main.WhisperSegment(
            index=1,
            start=80.0,
            end=81.0,
            text="Time to go!",
            words=[
                main.WhisperWord(index=1, start=80.0, end=80.4, text="Time"),
                main.WhisperWord(index=2, start=80.4, end=80.7, text="to"),
                main.WhisperWord(index=3, start=80.7, end=81.0, text="go"),
            ],
        )
        segment._forced_lyric_alignment = True
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem+whisperx-lyrics+asr-refined",
            durationSeconds=100.0,
            segments=[segment],
        )

    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", fake_transcribe_youtube_for_high_accuracy_sync)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [{"lyricId": 1, "japanese": "Time to go!", "currentStart": 80.0}],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["matchedCount"] == 0
    assert body["totalCount"] == 1
    assert "whisperx_lyric_forced_alignment_" in body["alignedLines"][0]["source"]
    assert body["alignedLines"][0]["source"].endswith("_unverified_latin")


def test_mixed_latin_forced_sequence_is_not_reliable(client) -> None:
    _, main = client

    line = main.AlignedLyricLine(
        lyricId=1,
        japanese="Let's walk the road 立ち止まらず",
        start=9.0,
        end=10.0,
        source="whisperx_lyric_forced_alignment_sequence",
        score=1.0,
    )

    assert main.is_latin_dominant_lyric(line.japanese)
    assert not main.is_reliable_audio_alignment(line)


def test_latin_asr_verified_alignment_remains_reliable(client) -> None:
    _, main = client

    line = main.AlignedLyricLine(
        lyricId=1,
        japanese="Time to go!",
        start=80.0,
        end=81.0,
        source="whisperx_lyric_forced_alignment_asr_verified",
        score=1.0,
    )

    assert main.is_reliable_audio_alignment(line)


def test_latin_contextual_timestamp_bridge_is_reliable(client) -> None:
    _, main = client

    line = main.AlignedLyricLine(
        lyricId=1,
        japanese="Time to go!",
        start=80.0,
        end=81.0,
        source="whisperx_contextual_timestamp_bridge",
        score=0.85,
    )

    assert main.is_reliable_audio_alignment(line)


def test_high_accuracy_align_splits_merged_internal_forced_lyric_alignment(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines=None):
        segment = main.WhisperSegment(
            index=1,
            start=10.0,
            end=18.0,
            text="一行目 二行目 三行目",
            words=[
                main.WhisperWord(index=1, start=10.0, end=11.0, text="一行目"),
                main.WhisperWord(index=2, start=13.0, end=14.0, text="二行目"),
                main.WhisperWord(index=3, start=16.0, end=17.0, text="三行目"),
            ],
        )
        segment._forced_lyric_alignment = True
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem+whisperx-lyrics+asr-refined",
            durationSeconds=30.0,
            segments=[segment],
        )

    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", fake_transcribe_youtube_for_high_accuracy_sync)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [
                {"lyricId": 1, "japanese": "一行目", "currentStart": 10.0},
                {"lyricId": 2, "japanese": "二行目", "currentStart": 13.0},
                {"lyricId": 3, "japanese": "三行目", "currentStart": 16.0},
            ],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["matchedCount"] == 3
    assert body["totalCount"] == 3
    assert [line["source"] for line in body["alignedLines"]] == [
        "whisperx_lyric_forced_alignment_sequence",
        "whisperx_lyric_forced_alignment_sequence",
        "whisperx_lyric_forced_alignment_sequence",
    ]
    assert [line["start"] for line in body["alignedLines"]] == [10.0, 13.0, 16.0]


def test_high_accuracy_align_labels_whisperx_sources(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines=None):
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=f"{settings.whisper_model}+vocals-stem+whisperx",
            durationSeconds=8.0,
            segments=[
                main.WhisperSegment(
                    index=1,
                    start=1.0,
                    end=2.0,
                    text="君が好き",
                    words=[
                        main.WhisperWord(index=1, start=1.0, end=1.5, text="君が"),
                        main.WhisperWord(index=2, start=1.5, end=2.0, text="好き"),
                    ],
                )
            ],
        )

    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", fake_transcribe_youtube_for_high_accuracy_sync)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [{"lyricId": 1, "japanese": "君が好き"}],
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["matchedCount"] == 1
    assert body["alignedLines"][0]["source"] == "whisperx_word_match"


def test_high_accuracy_align_does_not_fallback_to_original_audio_when_vocals_fail(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client
    main.app.state.settings.uvr_command_template = "uvr --input {input} --output {output_dir}"

    def fake_transcribe_vocals(*_args, **_kwargs):
        raise RuntimeError("UVR failed")

    def fail_transcribe_original_audio(*_args, **_kwargs):
        raise AssertionError("original audio must not be used for high-accuracy alignment")

    monkeypatch.setattr(main, "transcribe_youtube_vocals_stem_sync", fake_transcribe_vocals)
    monkeypatch.setattr(main, "transcribe_youtube_sync", fail_transcribe_original_audio)

    response = test_client.post(
        "/v1/high-accuracy-align",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "language": "ja",
            "lyrics": [{"lyricId": 1, "japanese": "君が好き", "currentStart": 1.0}],
        },
    )

    assert response.status_code == 502
    assert "UVR failed" in response.json()["detail"]


def test_separate_youtube_runs_configured_uvr_command(client, monkeypatch: pytest.MonkeyPatch, tmp_path) -> None:
    test_client, main = client
    input_audio = tmp_path / "input.wav"
    input_audio.write_bytes(b"audio")
    output_root = tmp_path / "uvr-output"

    main.app.state.settings.uvr_command_template = "uvr --input {input} --output {output_dir} --model {model}"
    main.app.state.settings.uvr_output_dir = str(output_root)
    main.app.state.settings.uvr_timeout_seconds = 30

    monkeypatch.setattr(main, "download_youtube_audio", lambda youtube_url, settings: input_audio)

    def fake_run(args, text, capture_output, timeout):
        assert args[:2] == ["uvr", "--input"]
        output_dir = tmp_path / "uvr-output" / "song-1"
        output_dir.mkdir(parents=True, exist_ok=True)
        (output_dir / "song-1_(Vocals).wav").write_bytes(b"vocals")
        (output_dir / "song-1_(Instrumental).wav").write_bytes(b"instrumental")
        return subprocess.CompletedProcess(args=args, returncode=0, stdout="ok", stderr="")

    monkeypatch.setattr(main.subprocess, "run", fake_run)

    response = test_client.post(
        "/v1/separate-youtube",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "model": "UVR-MDX-NET-Inst_HQ_3",
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["songUid"] == "song-1"
    assert body["model"] == "UVR-MDX-NET-Inst_HQ_3"
    assert {stem["kind"] for stem in body["stems"]} == {"vocals", "instrumental"}
    assert all(stem["downloadPath"].startswith("v1/audio-stem-file?path=") for stem in body["stems"])
    assert not input_audio.exists()

    vocals = next(stem for stem in body["stems"] if stem["kind"] == "vocals")
    download_response = test_client.get(
        "/" + vocals["downloadPath"],
        headers={"Authorization": "Bearer test-token"},
    )
    assert download_response.status_code == 200
    assert download_response.content == b"vocals"


def test_separate_youtube_requires_uvr_command_template(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client
    main.app.state.settings.uvr_command_template = ""

    response = test_client.post(
        "/v1/separate-youtube",
        headers={"Authorization": "Bearer test-token"},
        json={"songUid": "song-1", "youtubeUrl": "https://www.youtube.com/watch?v=test"},
    )

    assert response.status_code == 502
    assert "UVR_COMMAND_TEMPLATE" in response.json()["detail"]


def test_shazam_lyrics(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_fetch_shazam_lyrics(shazam_url, settings):
        return main.ShazamLyricsResponse(
            shazamUrl=shazam_url,
            title="Song",
            artist="Artist",
            lines=[main.LyricLine(lyricId=1, japanese="正式歌詞")],
        )

    monkeypatch.setattr(main, "fetch_shazam_lyrics", fake_fetch_shazam_lyrics)

    response = test_client.post(
        "/v1/shazam-lyrics",
        headers={"Authorization": "Bearer test-token"},
        json={"shazamUrl": "https://www.shazam.com/zh-tw/song/1/song"},
    )

    assert response.status_code == 200
    assert response.json()["lines"] == [{"lyricId": 1, "japanese": "正式歌詞"}]


def test_shazam_timed_lyrics_rejects_script_contamination(client) -> None:
    _, main = client

    assert main.is_valid_shazam_timed_lyric_content("半端ならK.O.") is True
    assert main.is_valid_shazam_timed_lyric_content("半端ならK.O. self.__next_f.push startTimeInSeconds") is False


def test_shazam_search(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_search_shazam_song(request, settings):
        return main.ShazamSearchResponse(
            query=f"{request.artist} {request.title}".strip(),
            candidates=[
                main.ShazamSearchCandidate(
                    shazamUrl="https://www.shazam.com/zh-tw/song/1/song",
                    title=request.title,
                    artist=request.artist,
                    appleMusicId="1",
                    durationSeconds=180.0,
                    hasLyrics=True,
                    hasTimeSyncedLyrics=True,
                    score=1.0,
                )
            ],
        )

    monkeypatch.setattr(main, "search_shazam_song", fake_search_shazam_song)

    response = test_client.post(
        "/v1/shazam-search",
        headers={"Authorization": "Bearer test-token"},
        json={"title": "Song", "artist": "Artist"},
    )

    assert response.status_code == 200
    assert response.json()["candidates"][0]["shazamUrl"] == "https://www.shazam.com/zh-tw/song/1/song"


def test_transcribe_align_shazam(client, monkeypatch: pytest.MonkeyPatch) -> None:
    test_client, main = client

    def fake_transcribe_align_shazam_sync(request, settings):
        return main.TranscribeAlignShazamResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            shazamUrl=request.shazamUrl,
            title="Song",
            artist="Artist",
            language=request.language,
            model=settings.whisper_model,
            durationSeconds=10.0,
            whisperSegments=[
                main.WhisperSegment(index=1, start=1.0, end=3.0, text="正式歌詞")
            ],
            alignedLines=[
                main.AlignedLyricLine(
                    lyricId=1,
                    japanese="正式歌詞",
                    start=1.0,
                    end=3.0,
                    source="whisper_word_match",
                    score=1.0,
                    whisperSegmentIndex=1,
                )
            ],
        )

    monkeypatch.setattr(main, "transcribe_align_shazam_sync", fake_transcribe_align_shazam_sync)

    response = test_client.post(
        "/v1/transcribe-align-shazam",
        headers={"Authorization": "Bearer test-token"},
        json={
            "songUid": "song-1",
            "youtubeUrl": "https://www.youtube.com/watch?v=test",
            "shazamUrl": "https://www.shazam.com/zh-tw/song/1/song",
            "language": "ja",
        },
    )

    assert response.status_code == 200
    body = response.json()
    assert body["alignedLines"][0]["source"] == "whisper_word_match"


def test_transcribe_align_shazam_falls_back_when_timed_lyrics_do_not_match(client, monkeypatch: pytest.MonkeyPatch) -> None:
    _, main = client

    def fake_fetch_shazam_lyrics(shazam_url, settings):
        return main.ShazamLyricsResponse(
            shazamUrl=shazam_url,
            title="Song",
            artist="Artist",
            lines=[main.LyricLine(lyricId=1, japanese="hello")],
        )

    def fake_fetch_shazam_timed_lyrics(shazam_url, settings):
        return [
            main.AlignedLyricLine(
                lyricId=1,
                japanese="wrong words",
                start=0.0,
                end=1.0,
                source="shazam_timed_lyrics",
                score=1.0,
            )
        ]

    def fake_transcribe_youtube_sync(request, settings):
        return main.TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=settings.whisper_model,
            durationSeconds=20.0,
            segments=[
                main.WhisperSegment(
                    index=1,
                    start=10.0,
                    end=11.0,
                    text="hello",
                    words=[
                        main.WhisperWord(index=1, start=10.0, end=10.5, text="hello"),
                    ],
                )
            ],
        )

    monkeypatch.setattr(main, "fetch_shazam_lyrics", fake_fetch_shazam_lyrics)
    monkeypatch.setattr(main, "fetch_shazam_timed_lyrics", fake_fetch_shazam_timed_lyrics)
    monkeypatch.setattr(main, "transcribe_youtube_sync", fake_transcribe_youtube_sync)

    result = main.transcribe_align_shazam_sync(
        main.TranscribeAlignShazamRequest(
            songUid="song-1",
            youtubeUrl="https://www.youtube.com/watch?v=test",
            shazamUrl="https://www.shazam.com/zh-tw/song/1/song",
        ),
        main.app.state.settings,
    )

    assert result.model.endswith("+shazam-text-fallback")
    assert result.alignedLines[0].source == "whisper_word_match"
    assert result.alignedLines[0].start == 10.0


def test_correct_timed_lyrics_offset_from_whisper(client) -> None:
    _, main = client

    lines = [
        main.AlignedLyricLine(
            lyricId=1,
            japanese="一行目",
            start=1.0,
            end=2.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=1,
        ),
        main.AlignedLyricLine(
            lyricId=2,
            japanese="二行目",
            start=5.0,
            end=6.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=2,
        ),
        main.AlignedLyricLine(
            lyricId=3,
            japanese="三行目",
            start=9.0,
            end=10.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=3,
        ),
    ]
    segments = [
        main.WhisperSegment(index=1, start=4.0, end=5.0, text="一行目"),
        main.WhisperSegment(index=2, start=8.0, end=9.0, text="二行目"),
        main.WhisperSegment(index=3, start=12.0, end=13.0, text="三行目"),
    ]

    corrected = main.correct_timed_lyrics_offset_from_whisper(lines, segments, 20.0)

    assert [line.start for line in corrected] == [4.0, 8.0, 12.0]
    assert all(line.source.endswith("_offset_corrected") for line in corrected)


def test_correct_timed_lyrics_offset_uses_one_anchor_per_whisper_segment(client) -> None:
    _, main = client

    lines = [
        main.AlignedLyricLine(
            lyricId=1,
            japanese="一行目",
            start=10.0,
            end=11.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=1,
        ),
        main.AlignedLyricLine(
            lyricId=2,
            japanese="二行目",
            start=16.0,
            end=17.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=1,
        ),
        main.AlignedLyricLine(
            lyricId=3,
            japanese="三行目",
            start=30.0,
            end=31.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=2,
        ),
        main.AlignedLyricLine(
            lyricId=4,
            japanese="四行目",
            start=37.0,
            end=38.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=2,
        ),
        main.AlignedLyricLine(
            lyricId=5,
            japanese="五行目",
            start=50.0,
            end=51.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=3,
        ),
        main.AlignedLyricLine(
            lyricId=6,
            japanese="六行目",
            start=56.0,
            end=57.0,
            source="shazam_timed_lyrics_verified",
            score=0.9,
            whisperSegmentIndex=3,
        ),
    ]
    segments = [
        main.WhisperSegment(index=1, start=12.0, end=18.0, text="一行目 二行目"),
        main.WhisperSegment(index=2, start=32.0, end=39.0, text="三行目 四行目"),
        main.WhisperSegment(index=3, start=52.0, end=58.0, text="五行目 六行目"),
    ]

    corrected = main.correct_timed_lyrics_offset_from_whisper(lines, segments, 70.0)

    assert [line.start for line in corrected] == [12.0, 18.0, 32.0, 39.0, 52.0, 58.0]


def test_reject_duration_mismatched_timed_lyrics(client) -> None:
    _, main = client

    lines = [
        main.AlignedLyricLine(
            lyricId=1,
            japanese="一行目",
            start=20.0,
            end=25.0,
            source="shazam_timed_lyrics_unverified",
            score=0.0,
        ),
        main.AlignedLyricLine(
            lyricId=2,
            japanese="二行目",
            start=140.0,
            end=145.0,
            source="shazam_timed_lyrics_unverified",
            score=0.0,
        ),
    ]

    with pytest.raises(RuntimeError):
        main.reject_duration_mismatched_timed_lyrics(lines, 90.0)
