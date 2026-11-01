from types import SimpleNamespace

import numpy as np
import pytest

from learnmore_api import reference_timing as reference


def fixture():
    request = SimpleNamespace(songUid="song", youtubeUrl="https://www.youtube.com/watch?v=-D_Me-bDFGU",
        language="ja", lyrics=[SimpleNamespace(lyricId=1, japanese="君が好き"),
                               SimpleNamespace(lyricId=2, japanese="空を見る")])
    registration = {"targetVideoId": "-D_Me-bDFGU", "referenceVideoId": "x-37TLZcqbQ",
        "sourceUrl": "https://www.marumaru-x.com/japanese-song/play-vxn5w9eln9"}
    rows = [{"text": "君が好き", "start": 2.0, "end": 5.0},
            {"text": "空を見る", "start": 6.0, "end": 9.0}]
    evidence = {"offsetSeconds": 0.1, "correlation": 0.99, "minimumWindowCorrelation": 0.97,
        "windowCount": 12, "overlapFraction": 0.99, "targetDuration": 60, "referenceDuration": 60}
    return request, registration, rows, evidence


def test_recording_identity_survives_level_and_short_intro_change():
    rng = np.random.default_rng(17)
    audio = rng.normal(size=40 * 4000)
    target = np.concatenate([np.zeros(400), audio * 0.7])
    proof = reference.compare_recordings(target, audio)
    assert proof["offsetSeconds"] == 0.1
    assert proof["correlation"] > 0.99999
    assert proof["minimumWindowCorrelation"] > 0.99999
    assert proof["windowCount"] == 12


@pytest.mark.parametrize("change", ["other_recording", "edited_middle", "silence", "speed_change"])
def test_reference_rejects_unverified_recordings(change):
    rng = np.random.default_rng(19)
    audio = rng.normal(size=40 * 4000)
    target = audio.copy()
    if change == "other_recording":
        target = rng.normal(size=len(audio))
    elif change == "edited_middle":
        target[15 * 4000:20 * 4000] *= -1
    elif change == "silence":
        target[:] = 0
    else:
        target = np.interp(np.arange(len(audio)) * 1.001, np.arange(len(audio)), audio)
    with pytest.raises(ValueError):
        reference.compare_recordings(target, audio)


def test_source_timing_is_transferred_without_interpolation_or_text_changes():
    request, registration, rows, evidence = fixture()
    result = reference.build_candidate(request, registration, rows, evidence)
    assert [x["start"] for x in result["alignedLines"]] == [2.1, 6.1]
    assert [x["japanese"] for x in result["alignedLines"]] == [x.japanese for x in request.lyrics]
    assert result["referenceEvidence"]["matchedLineCount"] == 2
    assert result["referenceEvidence"]["sourceUrl"] == registration["sourceUrl"]


@pytest.mark.parametrize("change", ["text", "video", "missing_line", "duplicate_time", "beyond_audio", "duplicate_id"])
def test_source_timing_rejects_changed_identity_or_incomplete_timeline(change):
    request, registration, rows, evidence = fixture()
    if change == "text":
        rows[0]["text"] = "違う歌詞"
    elif change == "video":
        request.youtubeUrl = "https://www.youtube.com/watch?v=LjuJgrdh5eY"
    elif change == "missing_line":
        rows.pop()
    elif change == "duplicate_time":
        rows[1]["start"] = rows[0]["start"]
    elif change == "beyond_audio":
        rows[1]["end"] = 61
    else:
        request.lyrics[1].lyricId = 1
    with pytest.raises(ValueError):
        reference.build_candidate(request, registration, rows, evidence)


def test_provider_redirect_or_unregistered_domain_is_not_followed():
    with pytest.raises(ValueError):
        reference.fetch_source({"sourceUrl": "https://example.invalid/lyrics"})


def test_unregistered_songs_keep_existing_audio_pipeline(tmp_path, monkeypatch):
    monkeypatch.setattr(reference, "MANIFEST", tmp_path / "missing.json")
    assert reference.try_align_reference(SimpleNamespace(songUid="unknown"), None,
        lambda *args: pytest.fail("unregistered reference downloaded")) is None


@pytest.mark.parametrize("contents", ["{", "[]"])
def test_invalid_reference_registry_keeps_existing_pipeline(tmp_path, monkeypatch, contents):
    manifest = tmp_path / "sources.json"
    manifest.write_text(contents)
    monkeypatch.setattr(reference, "MANIFEST", manifest)
    assert reference.try_align_reference(SimpleNamespace(songUid="unknown"), None,
        lambda *args: pytest.fail("invalid registry downloaded audio")) is None


def test_failed_reference_download_cleans_only_its_temporary_audio(tmp_path, monkeypatch):
    import json

    request, registration, rows, _ = fixture()
    manifest = tmp_path / "sources.json"
    manifest.write_text(json.dumps({request.songUid: registration}))
    monkeypatch.setattr(reference, "MANIFEST", manifest)
    monkeypatch.setattr(reference, "fetch_source", lambda _: rows)
    temporary = tmp_path / "download.wav"
    preserved = tmp_path / "existing.wav"
    preserved.write_bytes(b"existing")

    def download(url, _):
        if reference.video_id(url) == registration["referenceVideoId"]:
            raise RuntimeError("download_failed")
        temporary.write_bytes(b"temporary")
        return temporary

    assert reference.try_align_reference(request, None, download) is None
    assert not temporary.exists()
    assert preserved.read_bytes() == b"existing"
