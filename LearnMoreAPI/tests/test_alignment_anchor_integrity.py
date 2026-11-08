import pytest

from learnmore_api import main


def test_global_sequence_cannot_split_one_acoustic_word_between_lines(monkeypatch):
    monkeypatch.setattr(main, "get_kakasi_converter", lambda: None)
    lyrics = [main.HighAccuracyLyricLine(lyricId=1, japanese="花", currentStart=1),
              main.HighAccuracyLyricLine(lyricId=2, japanese="空", currentStart=2)]
    segments = [main.WhisperSegment(index=1, start=10, end=14, text="花空",
                words=[main.WhisperWord(index=1, start=10, end=14, text="花空")])]

    aligned = main.align_high_accuracy_lyrics_to_asr_sequence(lyrics, segments, 30)

    assert not any(main.is_reliable_audio_alignment(line) for line in aligned)


def test_global_sequence_does_not_manufacture_end_for_overlapping_words(monkeypatch):
    monkeypatch.setattr(main, "get_kakasi_converter", lambda: None)
    lyrics = [main.HighAccuracyLyricLine(lyricId=1, japanese="花", currentStart=1),
              main.HighAccuracyLyricLine(lyricId=2, japanese="空", currentStart=2)]
    segments = [main.WhisperSegment(index=1, start=10, end=14, text="花空", words=[
        main.WhisperWord(index=1, start=10, end=14, text="花"),
        main.WhisperWord(index=2, start=10, end=13, text="空")])]

    aligned = main.align_high_accuracy_lyrics_to_asr_sequence(lyrics, segments, 30)

    assert main.is_reliable_audio_alignment(aligned[0])
    assert not main.is_reliable_audio_alignment(aligned[1])


def test_monotonic_adjustment_cannot_keep_acoustic_verification():
    rows = [main.AlignedLyricLine(lyricId=1, japanese="花", start=20, end=24,
                                 source="current_timestamp_context", score=0.34),
            main.AlignedLyricLine(lyricId=2, japanese="空", start=10, end=14,
                                 source="whisper_global_sequence_match", score=1)]

    adjusted = main.enforce_monotonic_times(rows, 30)

    assert not main.is_reliable_audio_alignment(adjusted[1])
    assert rows[1].start == 10


def test_unchanged_acoustic_anchor_retains_verification():
    row = main.AlignedLyricLine(lyricId=1, japanese="花", start=10, end=14,
                               source="whisper_global_sequence_match", score=1)

    adjusted = main.enforce_monotonic_times([row], 30)

    assert adjusted[0] == row
    assert main.is_reliable_audio_alignment(adjusted[0])


@pytest.mark.parametrize("start,end", [(10, 10), (10, 9), (-1, 10), (10, 31)])
def test_global_sequence_rejects_invalid_raw_word_times(monkeypatch, start, end):
    monkeypatch.setattr(main, "get_kakasi_converter", lambda: None)
    lyrics = [main.HighAccuracyLyricLine(lyricId=1, japanese="花", currentStart=1)]
    segments = [main.WhisperSegment(index=1, start=0, end=30, text="花",
                words=[main.WhisperWord(index=1, start=start, end=end, text="花")])]

    aligned = main.align_high_accuracy_lyrics_to_asr_sequence(lyrics, segments, 30)

    assert not main.is_reliable_audio_alignment(aligned[0])


def test_forced_sequence_cannot_promote_shared_word_to_verified(monkeypatch):
    monkeypatch.setattr(main, "get_kakasi_converter", lambda: None)
    lyrics = [main.HighAccuracyLyricLine(lyricId=1, japanese="花", currentStart=10),
              main.HighAccuracyLyricLine(lyricId=2, japanese="空", currentStart=11)]
    segment = main.WhisperSegment(index=1, start=10, end=14, text="花空",
                words=[main.WhisperWord(index=1, start=10, end=14, text="花空")])
    segment._forced_lyric_alignment = True

    aligned = main.build_internal_forced_sequence_alignment_by_id(lyrics, [segment], 30)

    assert aligned == {}
