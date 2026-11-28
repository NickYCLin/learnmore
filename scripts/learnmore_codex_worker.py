#!/usr/bin/env python3
"""Linux queue runner for the existing, validated Codex text backfill."""
from __future__ import annotations

import argparse
from contextlib import closing, contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

from learnmore_codex_backfill import BackfillError, FIELDS, writable_fields

PENDING = "translation_pending_codex"
SOURCE_PENDING = "translation_source_pending"
REVIEW = "high_accuracy_needs_review"
TIMING_PENDING = "high_accuracy_pending"
RETRY_MINUTES = (5, 15, 45)
UID_PATTERN = re.compile(r"[A-Za-z0-9_-]{1,100}", re.ASCII)
TRANSFORMER = Path(__file__).with_name("learnmore_codex_backfill.py")
SONG_FIELDS = "SongID, SongUid, YouTubeVideoUrl, IsPublished, HighAccuracyStatus, HighAccuracyStatusReason"
LYRIC_FIELDS = "LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby, Roman"


def complete_snapshot(rows, result):
    """Build the exact readback expected after applying verified patches."""
    if result["needsReview"]:
        raise BackfillError("lyrics_need_review")
    expected = {row["LyricID"]: row.copy() for row in rows}
    for patch in result["patches"]:
        original = expected[patch["lyricId"]]
        allowed = set(writable_fields(original))
        for field in FIELDS:
            if field in patch:
                if field not in allowed:
                    raise BackfillError("overwrite_not_allowed")
                original[field] = patch[field]
    if not expected or any(writable_fields(row) for row in expected.values()):
        raise BackfillError("incomplete_backfill")
    return list(expected.values())


def transform(song_uid, rows):
    snapshot = json.dumps({"songUid": song_uid, "lyrics": rows}, ensure_ascii=False)
    with tempfile.TemporaryDirectory(prefix="learnmore-backfill-") as root:
        source, target = Path(root) / "before.json", Path(root) / "result.json"
        source.write_text(snapshot, encoding="utf-8")
        # Keep database credentials and other service environment out of the Codex process.
        environment = {key: os.environ[key] for key in ("PATH", "HOME", "CODEX_HOME", "LANG", "SSL_CERT_FILE")
                       if key in os.environ}
        result = subprocess.run(
            [sys.executable, str(TRANSFORMER), "--snapshot", str(source), "--output", str(target),
             "--codex", os.getenv("CODEX_BIN") or "codex", "--timeout", "240"],
            env=environment, capture_output=True, text=True, check=False,
        )
        if result.returncode:
            # The existing transformer emits only fixed failure codes; only this one affects the whole queue.
            try:
                code = json.loads(result.stdout)["error"]
            except (ValueError, KeyError, TypeError):
                code = "transform_failed"
            raise BackfillError("codex_auth_or_quota" if code == "codex_auth_or_quota" else "transform_failed")
        return complete_snapshot(rows, json.loads(target.read_text(encoding="utf-8")))


def save_state(path, state):
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        try:
            json.dump(state, stream)
            stream.flush()
            os.fsync(stream.fileno())
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)


@contextmanager
def transaction(cursor):
    cursor.execute("BEGIN TRANSACTION")
    try:
        yield
        cursor.execute("COMMIT TRANSACTION")
    except BaseException:
        cursor.execute("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION")
        raise


def read_lyrics(cursor, uid, locked=False):
    if not isinstance(uid, str) or not UID_PATTERN.fullmatch(uid):
        return None
    table = f"dbo.[Songs_{uid}]"
    cursor.execute("""
        SELECT COUNT(*) AS column_count FROM sys.columns
        WHERE object_id=OBJECT_ID(%s, 'U') AND name IN
            ('LyricID','TimeStamp','Japanese','Chinese','JapaneseRuby','Roman')
        """, (table,))
    if cursor.fetchone()["column_count"] != 6:
        return None
    hint = " WITH (UPDLOCK,HOLDLOCK)" if locked else ""
    cursor.execute(f"SELECT {LYRIC_FIELDS} FROM {table}{hint} ORDER BY LyricID")
    return cursor.fetchall()


def finish(cursor, song, rows, expected, reason=None):
    """Compare the master and lyrics under locks before writing any patch or status."""
    uid = song["SongUid"]
    with transaction(cursor):
        cursor.execute(f"SELECT {SONG_FIELDS} FROM dbo.Songs WITH (UPDLOCK,HOLDLOCK) WHERE SongID=%s", (song["SongID"],))
        if cursor.fetchone() != song or read_lyrics(cursor, uid, locked=True) != rows:
            return "source_changed"
        next_status = REVIEW
        if expected is not None:
            for original, updated in zip(rows, expected, strict=True):
                fields = [field for field in FIELDS if original[field] != updated[field]]
                if fields:
                    assignments = ",".join(f"[{field}]=%s" for field in fields)
                    cursor.execute(f"UPDATE dbo.[Songs_{uid}] SET {assignments} WHERE LyricID=%s",
                                   (*[updated[field] for field in fields], original["LyricID"]))
                    if cursor.rowcount != 1:
                        raise BackfillError("unexpected_update_count")
            if read_lyrics(cursor, uid, locked=True) != expected:
                raise BackfillError("readback_mismatch")
            if "timing_validation_pending" in (song["HighAccuracyStatusReason"] or ""):
                next_status = TIMING_PENDING
                reason = "中文、讀音與羅馬拼音已補齊，等待背景工作校正秒數。"
            else:
                reason = "中文、讀音與羅馬拼音已補齊，秒數仍依待校正流程檢查。"
        if expected is None and "timing_validation_pending" in (song["HighAccuracyStatusReason"] or ""):
            reason = "timing_validation_pending: " + reason
        if expected is not None and not song["IsPublished"]:
            cursor.execute("""
                UPDATE dbo.Songs SET HighAccuracyStatus=%s, HighAccuracyStatusReason=%s, IsPublished=1
                WHERE SongID=%s
                """, (next_status, reason, song["SongID"]))
        else:
            cursor.execute("""
                UPDATE dbo.Songs SET HighAccuracyStatus=%s, HighAccuracyStatusReason=%s
                WHERE SongID=%s
                """, (next_status, reason, song["SongID"]))
        if cursor.rowcount != 1:
            raise BackfillError("status_update_failed")
    return "applied" if expected is not None else "needs_review"


def process_one(cursor, state_path):
    cursor.execute("""
        DECLARE @result int;
        EXEC @result=sys.sp_getapplock @Resource=N'LearnMore.CodexBackfill',
            @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=0;
        SELECT @result AS result;
        """)
    if cursor.fetchone()["result"] < 0:
        return "busy"
    state = json.loads(state_path.read_text(encoding="utf-8")) if state_path.exists() else {}
    retries = state.setdefault("retries", {})
    now = time.time()
    if state.get("cooldown_until", 0) > now:
        return "cooldown"
    cursor.execute(f"""
        SELECT {SONG_FIELDS} FROM dbo.Songs
        WHERE HighAccuracyStatus=%s OR
            (HighAccuracyStatus=%s AND IsPublished=0 AND AddedDate<DATEADD(hour,-2,GETDATE()))
        ORDER BY SongID
        """, (PENDING, SOURCE_PENDING))
    songs = cursor.fetchall()
    for song in songs:
        key = str(song["SongID"])
        if song["HighAccuracyStatus"] == SOURCE_PENDING:
            cursor.execute("""
                UPDATE dbo.Songs SET HighAccuracyStatus=%s, HighAccuracyStatusReason=N'上傳翻譯中斷，等待背景補件。'
                WHERE SongID=%s AND HighAccuracyStatus=%s AND IsPublished=0
                """, (PENDING, song["SongID"], SOURCE_PENDING))
            return "recovered_upload" if cursor.rowcount == 1 else "source_changed"

        rows = read_lyrics(cursor, song["SongUid"])
        if not rows or any(not (row["Japanese"] or "").strip() for row in rows):
            return finish(cursor, song, rows, None, "日文歌詞或資料欄位不完整，請先補上原文。")

        fingerprint = hashlib.sha256(json.dumps(rows, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
        retry = retries.get(key, {})
        attempts = 0
        if retry.get("fingerprint") == fingerprint:
            if retry["next_retry"] > now:
                continue
            attempts = retry["attempts"]
        if attempts >= len(RETRY_MINUTES):
            return finish(cursor, song, rows, None, "補翻已達重試上限，請核對原文與缺漏欄位。")

        retries[key] = {"fingerprint": fingerprint, "attempts": attempts + 1,
                        "next_retry": now + 60 * RETRY_MINUTES[attempts]}
        save_state(state_path, state)
        try:
            expected = transform(song["SongUid"], rows)
        except BackfillError as exc:
            if str(exc) == "codex_auth_or_quota":
                state["cooldown_until"] = time.time() + 30 * 60
            retries[key]["next_retry"] = time.time() + 60 * RETRY_MINUTES[attempts]
            save_state(state_path, state)
            if attempts + 1 >= len(RETRY_MINUTES):
                return finish(cursor, song, rows, None, "補翻已達重試上限，請核對原文與缺漏欄位。")
            return "auth_or_quota" if str(exc) == "codex_auth_or_quota" else "retry_scheduled"
        result = finish(cursor, song, rows, expected)
        if result == "applied":
            retries.pop(key, None)
            save_state(state_path, state)
        return result
    return "idle"


def connect():
    import pymssql

    return pymssql.connect(
        server=os.environ["CODEX_WORKER_DB_HOST"], port=os.getenv("CODEX_WORKER_DB_PORT", "1433"),
        database=os.environ["CODEX_WORKER_DB_NAME"], user=os.environ["CODEX_WORKER_DB_USER"],
        password=os.environ["CODEX_WORKER_DB_PASSWORD"], login_timeout=10, timeout=30,
        autocommit=True, as_dict=True,
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--state-dir", type=Path, default=Path("/var/lib/learnmore-codex-worker"))
    args = parser.parse_args()
    args.state_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
    while True:
        try:
            with closing(connect()) as session, closing(session.cursor()) as cursor:
                outcome = process_one(cursor, args.state_dir / "retry.json")
            print(f"codex_worker outcome={outcome}", flush=True)
        except Exception:
            print("codex_worker outcome=infrastructure_unavailable", flush=True)
            if args.once:
                return 1
        if args.once:
            return 0
        time.sleep(60)


if __name__ == "__main__":
    raise SystemExit(main())
