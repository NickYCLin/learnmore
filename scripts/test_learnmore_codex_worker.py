"""Regression tests for the Linux Codex queue and publication boundary."""
from contextlib import closing
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch
from uuid import uuid4

import learnmore_codex_worker as worker
from learnmore_codex_backfill import BackfillError, validate_rows


def source():
    return [
        {"LyricID": 1, "TimeStamp": 1.25, "Japanese": "テスト", "Chinese": "測試一",
         "JapaneseRuby": None, "Roman": None},
        {"LyricID": 2, "TimeStamp": 2.5, "Japanese": "テスト", "Chinese": "翻譯中…",
         "JapaneseRuby": "テスト", "Roman": "tesuto"},
    ]


def output():
    return {"rows": [
        {"lyricId": 1, "Chinese": None, "JapaneseRuby": "テスト", "Roman": "tesuto",
         "needsReview": False, "reason": ""},
        {"lyricId": 2, "Chinese": "測試二", "JapaneseRuby": None, "Roman": None,
         "needsReview": False, "reason": ""},
    ]}


def completed(rows):
    return worker.complete_snapshot(rows, {"needsReview": False, "patches": validate_rows(rows, output())})


class WorkerTests(unittest.TestCase):
    def test_placeholders_and_readings_are_filled_without_changing_valid_chinese(self):
        original = source()
        after = completed(original)
        self.assertEqual(after[0]["Chinese"], original[0]["Chinese"])
        self.assertEqual(after[0]["JapaneseRuby"], "テスト")
        self.assertEqual(after[0]["Roman"], "tesuto")
        self.assertEqual(after[1]["Chinese"], "測試二")
        self.assertEqual(after[1]["TimeStamp"], original[1]["TimeStamp"])

    def test_overwrite_and_incomplete_patches_are_rejected(self):
        with self.assertRaisesRegex(BackfillError, "overwrite_not_allowed"):
            worker.complete_snapshot(source(), {"needsReview": False, "patches": [{"lyricId": 1, "Chinese": "測試二"}]})
        with self.assertRaisesRegex(BackfillError, "incomplete_backfill"):
            worker.complete_snapshot(source(), {"needsReview": False, "patches": []})
        with self.assertRaisesRegex(BackfillError, "lyrics_need_review"):
            worker.complete_snapshot(source(), {"needsReview": True, "patches": []})

    def test_transformer_inherits_no_database_credentials_and_reports_auth_failure(self):
        runner = MagicMock(returncode=2, stdout='{"ok":false,"error":"codex_auth_or_quota"}')
        with patch.dict(os.environ, {"CODEX_WORKER_DB_PASSWORD": "test-marker"}), patch.object(
            worker.subprocess, "run", return_value=runner
        ) as run:
            with self.assertRaisesRegex(BackfillError, "codex_auth_or_quota"):
                worker.transform("fixture", source())
        self.assertNotIn("CODEX_WORKER_DB_PASSWORD", run.call_args.kwargs["env"])

    def test_invalid_legacy_uid_does_not_become_a_table_identifier(self):
        cursor = MagicMock()
        for uid in (None, "", "unexpected.identifier"):
            self.assertIsNone(worker.read_lyrics(cursor, uid))
        cursor.execute.assert_not_called()

    def test_one_song_and_retry_schedule(self):
        cursor = MagicMock()
        cursor.fetchone.return_value = {"result": 0}
        cursor.fetchall.return_value = [
            {"SongID": 1, "SongUid": "a", "HighAccuracyStatus": worker.PENDING},
            {"SongID": 2, "SongUid": "b", "HighAccuracyStatus": worker.PENDING},
        ]
        with tempfile.TemporaryDirectory() as directory, patch.object(
            worker, "read_lyrics", return_value=source()
        ), patch.object(worker, "transform", side_effect=BackfillError("codex_timeout")) as transform, patch.object(
            worker, "finish", return_value="needs_review"
        ) as finish, patch.object(worker.time, "time", return_value=1000) as now:
            path = Path(directory) / "state.json"
            for attempt, delay in enumerate((5, 15, 45), 1):
                expected = "needs_review" if attempt == 3 else "retry_scheduled"
                self.assertEqual(worker.process_one(cursor, path), expected)
                entry = json.loads(path.read_text())["retries"]["1"]
                self.assertEqual(entry["attempts"], attempt)
                self.assertEqual(entry["next_retry"], now.return_value + delay * 60)
                now.return_value = entry["next_retry"]
            self.assertEqual(transform.call_count, 3)
            self.assertTrue(all(call.args[0] == "a" for call in transform.call_args_list))
            finish.assert_called_once()

    def test_auth_cooldown_and_busy_lock_prevent_new_jobs(self):
        cursor = MagicMock()
        cursor.fetchone.return_value = {"result": 0}
        cursor.fetchall.return_value = [{"SongID": 1, "SongUid": "a", "HighAccuracyStatus": worker.PENDING}]
        with tempfile.TemporaryDirectory() as directory, patch.object(
            worker, "read_lyrics", return_value=source()
        ), patch.object(worker, "transform", side_effect=BackfillError("codex_auth_or_quota")) as transform, patch.object(
            worker.time, "time", return_value=1000
        ) as now:
            path = Path(directory) / "state.json"
            self.assertEqual(worker.process_one(cursor, path), "auth_or_quota")
            now.return_value += 1799
            self.assertEqual(worker.process_one(cursor, path), "cooldown")
            cursor.fetchone.return_value = {"result": -1}
            self.assertEqual(worker.process_one(cursor, path), "busy")
            transform.assert_called_once()


@unittest.skipUnless(os.getenv("LEARNMORE_WORKER_TEST_SQL_PORT"), "requires an isolated loopback SQL Server")
class WorkerSqlTests(unittest.TestCase):
    def setUp(self):
        import pymssql

        self.database = "LearnMoreWorkerAudit_" + uuid4().hex
        self.options = dict(server="127.0.0.1", port=int(os.environ["LEARNMORE_WORKER_TEST_SQL_PORT"]),
                            user="sa", password=os.environ["LEARNMORE_WORKER_TEST_SQL_PASSWORD"],
                            autocommit=True, as_dict=True, timeout=15)
        with closing(pymssql.connect(**self.options, database="master")) as db:
            with closing(db.cursor()) as cursor:
                cursor.execute(f"CREATE DATABASE [{self.database}]")
        self.addCleanup(self.drop, pymssql)
        self.db = pymssql.connect(**self.options, database=self.database)
        self.addCleanup(self.db.close)

    def drop(self, pymssql):
        with closing(pymssql.connect(**self.options, database="master")) as db:
            with closing(db.cursor()) as cursor:
                cursor.execute(f"ALTER DATABASE [{self.database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{self.database}]")

    def test_worker_publishes_only_after_verified_readback_and_keeps_timing_pending(self):
        with closing(self.db.cursor()) as cursor, tempfile.TemporaryDirectory() as directory:
            cursor.execute("""
                CREATE TABLE dbo.Songs (SongID int PRIMARY KEY, SongUid nvarchar(100), YouTubeVideoUrl nvarchar(255),
                    IsPublished bit, HighAccuracyStatus nvarchar(50), HighAccuracyStatusReason nvarchar(200), AddedDate datetime);
                CREATE TABLE dbo.Songs_fixture (LyricID int PRIMARY KEY, TimeStamp float, Japanese nvarchar(max),
                    Chinese nvarchar(max), JapaneseRuby nvarchar(max), Roman nvarchar(max));
                INSERT INTO dbo.Songs VALUES (1,N'fixture',NULL,0,N'translation_pending_codex',N'timing_validation_pending',GETDATE());
                INSERT INTO dbo.Songs_fixture VALUES
                    (1,1.25,N'テスト',N'測試一',NULL,NULL),
                    (2,2.5,N'テスト',N'翻譯中…',N'テスト',N'tesuto');
                """)
            state = Path(directory) / "state.json"
            with patch.object(worker, "transform", side_effect=BackfillError("codex_timeout")), patch.object(
                worker.time, "time", return_value=1000
            ):
                self.assertEqual(worker.process_one(cursor, state), "retry_scheduled")
            cursor.execute("SELECT IsPublished,HighAccuracyStatus FROM dbo.Songs")
            self.assertEqual(cursor.fetchone(), {"IsPublished": False, "HighAccuracyStatus": worker.PENDING})
            self.assertEqual(worker.read_lyrics(cursor, "fixture"), source())

            with patch.object(worker, "transform", side_effect=completed_for_uid), patch.object(
                worker.time, "time", return_value=1300
            ):
                self.assertEqual(worker.process_one(cursor, state), "applied")
            cursor.execute("SELECT IsPublished,HighAccuracyStatus FROM dbo.Songs")
            self.assertEqual(cursor.fetchone(), {"IsPublished": True, "HighAccuracyStatus": worker.TIMING_PENDING})
            self.assertEqual(worker.read_lyrics(cursor, "fixture"), completed(source()))


def completed_for_uid(_uid, rows):
    return completed(rows)


if __name__ == "__main__":
    unittest.main()
