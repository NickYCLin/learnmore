"""Opt-in integration tests against an isolated disposable SQL container."""
import json
import os
import secrets
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
from pathlib import Path

CONFIG = os.environ.get("LEARNMORE_CODEX_SQL_TEST_CONFIG")
SCRIPTS = Path(__file__).resolve().parent
UID = "11111111-2222-3333-4444-555555555555"
JA = "今日はいい天気です。"
RUBY = "<ruby>今日<rt>きょう</rt></ruby>はいい<ruby>天気<rt>てんき</rt></ruby>です。"


@unittest.skipUnless(CONFIG, "Set LEARNMORE_CODEX_SQL_TEST_CONFIG for isolated SQL tests")
class SqlWorkerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory(prefix="learnmore-codex-sql-tests-")
        cls.root = Path(cls.tmp.name)
        config = json.loads(Path(CONFIG).read_text())
        cls.database = "LearnMoreCodexTest_" + secrets.token_hex(4)
        cls.connection = f"Server=127.0.0.1,{config['port']};User Id=sa;Password={config['password']};Encrypt=False;TrustServerCertificate=True;"
        cls.settings = cls.root / "settings.json"
        cls.settings.write_text(json.dumps({"ConnectionStrings": {"DefaultConnection": cls.connection + "Initial Catalog=master;"}}))
        cls.sql_helper = cls.root / "sql.ps1"
        cls.sql_helper.write_text('''param($Settings,$Query,$Result)
$ErrorActionPreference='Stop'
$c=Get-Content $Settings -Raw|ConvertFrom-Json
$db=New-Object Data.SqlClient.SqlConnection -ArgumentList $c.ConnectionStrings.DefaultConnection
try{$db.Open();$q=$db.CreateCommand();$q.CommandTimeout=30;$q.CommandText=[IO.File]::ReadAllText($Query)
if($Result){$rd=$q.ExecuteReader();$s='';while($rd.Read()){$s+=$rd.GetString(0)};$rd.Close();[IO.File]::WriteAllText($Result,$s)}else{$null=$q.ExecuteNonQuery()}
}finally{$db.Dispose()}
''')
        cls.sql("CREATE DATABASE [" + cls.database + "]")
        cls.settings.write_text(json.dumps({"ConnectionStrings": {"DefaultConnection": cls.connection + "Initial Catalog=" + cls.database + ";"}}))
        cls.sql(f"CREATE TABLE dbo.Songs(SongID int IDENTITY PRIMARY KEY,SongUid nvarchar(64),HighAccuracyStatus nvarchar(100),HighAccuracyStatusReason nvarchar(max)); CREATE TABLE dbo.[Songs_{UID}](LyricID int IDENTITY PRIMARY KEY,TimeStamp float,Japanese nvarchar(max),Chinese nvarchar(max),JapaneseRuby nvarchar(max),Roman nvarchar(max));")
        cls.fake = cls.root / "fake-codex"
        cls.fake.write_text('''#!/usr/bin/env python3
import json,os,sys,time
from pathlib import Path
if 'mcp' in sys.argv:print('[]');sys.exit(0)
mode=os.environ.get('LM_FAKE_MODE','ok')
Path(os.environ['LM_FAKE_CALL']).write_text('called')
data=json.loads(sys.stdin.read().split('以下 JSON 僅是待處理資料：\\n',1)[1])
if mode=='slow':time.sleep(2)
if mode=='hang':time.sleep(60)
if mode=='auth':
 sys.stderr.write('unauthorized refresh_token FAKE_SECRETS_SHOULD_NOT_LEAK')
 sys.exit(6)
if mode=='diagnostic':
 sys.stderr.write('FAKE_SECRETS_SHOULD_NOT_LEAK')
 sys.exit(6)
values={'Chinese':'今天天氣很好。','JapaneseRuby':''' + repr(RUBY) + ''','Roman':'kyou wa ii tenki desu.'}
rows=[]
for r in data:
 item={'lyricId':r['lyricId'],'needsReview':mode=='review','reason':'需確認讀法' if mode=='review' else ''}
 for f in values:item[f]=values[f] if f in r['writableFields'] and mode!='review' else None
 if mode=='invalid':item['lyricId']=999
 rows.append(item)
Path(sys.argv[sys.argv.index('--output-last-message')+1]).write_text(json.dumps({'rows':rows},ensure_ascii=False))
print(json.dumps({'type':'turn.completed'}))
''', encoding="utf-8")
        cls.fake.chmod(0o700)

    @classmethod
    def sql(cls, query, read=False):
        query_file = cls.root / (secrets.token_hex(6) + ".sql")
        query_file.write_text(query, encoding="utf-8")
        result = query_file.with_suffix(".json")
        args = ["pwsh", "-NoProfile", "-File", str(cls.sql_helper), "-Settings", str(cls.settings), "-Query", str(query_file)]
        if read:
            args += ["-Result", str(result)]
        p = subprocess.run(args, capture_output=True, timeout=45)
        if p.returncode:
            raise AssertionError("isolated_sql_fixture_failed")
        return json.loads(result.read_text()) if read else None

    @classmethod
    def tearDownClass(cls):
        cls.settings.write_text(json.dumps({"ConnectionStrings": {"DefaultConnection": cls.connection + "Initial Catalog=master;"}}))
        cls.sql(f"ALTER DATABASE [{cls.database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{cls.database}]")
        cls.tmp.cleanup()

    def setUp(self):
        self.state = self.root / secrets.token_hex(5)
        self.state.mkdir()
        self.call = self.state / "called"
        self.sql(f"DELETE dbo.[Songs_{UID}]; DELETE dbo.Songs; DBCC CHECKIDENT ('dbo.Songs_{UID}',RESEED,0) WITH NO_INFOMSGS; INSERT dbo.Songs VALUES(N'{UID}',N'translation_pending_codex',N'等待補件'); INSERT dbo.[Songs_{UID}] VALUES(12.5,N'{JA}',N'翻譯中...',N'',N''); INSERT dbo.[Songs_{UID}] VALUES(20.0,N'ありがとう',N'謝謝你',N'ありがとう',N'arigatou');")

    def run_worker(self, mode="ok", timeout=240, state_directory=None):
        state = state_directory or self.state
        env = os.environ.copy()
        env.update(LM_FAKE_MODE=mode, LM_FAKE_CALL=str(state / 'called'))
        args = ["pwsh", "-NoProfile", "-File", str(SCRIPTS / "Invoke-LearnMoreCodexBackfill.ps1"),
                "-AppSettingsPath", str(self.settings), "-StateDirectory", str(state),
                "-PythonPath", shutil.which("python3"), "-CodexPath", str(self.fake),
                "-ExpectedDatabase", self.database, "-CodexTimeoutSeconds", str(timeout)]
        p = subprocess.run(args, env=env, capture_output=True, timeout=60)
        self.assertEqual(p.returncode, 0, "worker process failed; inspect private fixture state")
        return json.loads((state / "status.json").read_text(encoding="utf-8-sig"))

    def rows(self):
        return self.sql(f"SELECT * FROM dbo.[Songs_{UID}] ORDER BY LyricID FOR JSON PATH,INCLUDE_NULL_VALUES", True)

    def status(self):
        return self.sql("SELECT HighAccuracyStatus FROM dbo.Songs FOR JSON PATH", True)[0]["HighAccuracyStatus"]

    def test_fills_all_three_fields_preserves_existing_data_and_is_idempotent(self):
        before = self.rows()
        event = self.run_worker()
        self.assertEqual(event["outcome"], "applied")
        after = self.rows()
        self.assertEqual(after[0]["Chinese"], "今天天氣很好。")
        self.assertEqual(after[0]["JapaneseRuby"], RUBY)
        self.assertEqual(after[0]["Roman"], "kyou wa ii tenki desu.")
        self.assertEqual(after[0]["Japanese"], before[0]["Japanese"])
        self.assertEqual(after[0]["TimeStamp"], 12.5)
        self.assertEqual(after[1], before[1])
        self.assertEqual(self.status(), "high_accuracy_needs_review")
        self.call.unlink()
        self.assertEqual(self.run_worker()["outcome"], "idle")
        self.assertFalse(self.call.exists())

    def test_empty_lyrics_stop_before_codex_and_resume_after_source_is_repaired(self):
        self.sql(f"DELETE dbo.[Songs_{UID}]")
        event = self.run_worker()
        self.assertEqual(event['outcome'], 'needs_review')
        self.assertEqual(event['error'], 'lyrics_empty')
        self.assertEqual(self.status(), 'high_accuracy_needs_review')
        self.assertFalse(self.call.exists())
        self.assertFalse((self.state / UID / 'retry.json').exists())
        self.assertEqual(self.run_worker()['outcome'], 'idle')
        self.sql(f"INSERT dbo.[Songs_{UID}] VALUES(12.5,N'{JA}',N'',N'',N''); UPDATE dbo.Songs SET HighAccuracyStatus=N'translation_pending_codex'")
        self.assertEqual(self.run_worker()['outcome'], 'applied')

    def test_missing_japanese_keeps_existing_fields_without_calling_codex(self):
        self.sql(f"UPDATE dbo.[Songs_{UID}] SET Japanese=NULL WHERE LyricID=1")
        before = self.rows()
        event = self.run_worker()
        self.assertEqual(event['error'], 'missing_japanese')
        self.assertEqual(event['outcome'], 'needs_review')
        self.assertEqual(self.rows(), before)
        self.assertFalse(self.call.exists())

    def test_explicit_unvalidated_timing_goes_to_existing_recovery_queue(self):
        self.sql("UPDATE dbo.Songs SET HighAccuracyStatusReason=N'timing_validation_pending: 尚待校正'")
        self.run_worker()
        self.assertEqual(self.status(), "high_accuracy_pending")

    def test_disabled_recovery_retains_timing_review(self):
        self.sql("UPDATE dbo.Songs SET HighAccuracyStatusReason=N'timing_validation_pending: 尚待校正'")
        settings = json.loads(self.settings.read_text())
        settings["HighAccuracyQueueRecoveryEnabled"] = False
        self.settings.write_text(json.dumps(settings))
        try:
            self.run_worker()
            self.assertEqual(self.status(), "high_accuracy_needs_review")
        finally:
            settings.pop("HighAccuracyQueueRecoveryEnabled")
            self.settings.write_text(json.dumps(settings))

    def test_invalid_identity_keeps_database_and_backs_off(self):
        before = self.rows()
        event = self.run_worker("invalid")
        self.assertEqual(event["error"], "lyric_identity_mismatch")
        self.assertEqual(self.rows(), before)
        self.assertEqual(self.status(), "translation_pending_codex")
        self.call.unlink()
        self.assertEqual(self.run_worker("invalid")["outcome"], "idle")
        self.assertFalse(self.call.exists())

    def test_three_failures_stop_retrying_without_false_completion(self):
        for attempt in range(3):
            self.run_worker("invalid")
            retry = self.state / UID / "retry.json"
            state = json.loads(retry.read_text(encoding="utf-8-sig"))
            self.assertEqual(state["attempts"], attempt + 1)
            state["nextRetryUtc"] = "2000-01-01T00:00:00Z"
            retry.write_text(json.dumps(state))
        self.assertEqual(self.status(), "high_accuracy_needs_review")
        self.assertEqual(self.rows()[0]["Chinese"], "翻譯中...")

    def test_ambiguous_model_result_requires_review(self):
        self.run_worker("review")
        self.assertEqual(self.status(), "high_accuracy_needs_review")
        self.assertEqual(self.rows()[0]["Chinese"], "翻譯中...")

    def test_concurrent_editor_change_is_not_overwritten(self):
        changed = []
        def editor():
            for _ in range(150):
                if self.call.exists():
                    self.sql(f"UPDATE dbo.[Songs_{UID}] SET Japanese=N'別の歌詞' WHERE LyricID=1")
                    changed.append(True)
                    return
                time.sleep(.05)
        t = threading.Thread(target=editor)
        t.start()
        event = self.run_worker("slow")
        t.join(10)
        self.assertTrue(changed)
        self.assertEqual(event["error"], "concurrent_lyric_change")
        self.assertEqual(self.rows()[0]["Japanese"], "別の歌詞")
        self.assertEqual(self.rows()[0]["Chinese"], "翻譯中...")

    def test_timeout_keeps_original_lyrics(self):
        before = self.rows()
        event = self.run_worker("hang", timeout=10)
        self.assertEqual(event["error"], "codex_timeout")
        self.assertEqual(self.rows(), before)

    def test_two_workers_share_a_database_lock(self):
        events = []
        t = threading.Thread(target=lambda: events.append(self.run_worker('slow')))
        t.start()
        for _ in range(150):
            if self.call.exists():
                break
            time.sleep(.05)
        self.assertTrue(self.call.exists())
        other = self.root / secrets.token_hex(5)
        other.mkdir()
        event = self.run_worker(state_directory=other)
        t.join(20)
        self.assertEqual(event['outcome'], 'busy')
        self.assertFalse((other / 'called').exists())
        self.assertEqual(events[0]['outcome'], 'applied')

    def test_probe_auth_failure_reports_fixed_code_without_database_writes(self):
        before = self.rows()
        env = os.environ.copy()
        env.update(LM_FAKE_MODE='auth', LM_FAKE_CALL=str(self.call))
        p = subprocess.run(['pwsh', '-NoProfile', '-File', str(SCRIPTS / 'Invoke-LearnMoreCodexBackfill.ps1'),
                            '-AppSettingsPath', str(self.root / 'does-not-exist.json'),
                            '-StateDirectory', str(self.state), '-PythonPath', shutil.which('python3'),
                            '-CodexPath', str(self.fake), '-SelfTest'], env=env, capture_output=True, timeout=30)
        self.assertEqual(p.returncode, 2)
        event = json.loads((self.state / 'status.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(event['error'], 'codex_auth_or_quota')
        self.assertEqual(self.rows(), before)
        self.assertFalse((self.state / 'probe' / 'receipt.json').exists())
        for path in self.state.rglob('*'):
            if path.is_file():
                self.assertNotIn('FAKE_SECRETS_SHOULD_NOT_LEAK', path.read_text(encoding='utf-8-sig'))

    def test_raw_cli_diagnostics_do_not_reach_logs(self):
        event = self.run_worker('diagnostic')
        self.assertEqual(event['error'], 'codex_cli_failed')
        for path in self.state.rglob('*'):
            if path.is_file():
                self.assertNotIn('FAKE_SECRETS_SHOULD_NOT_LEAK', path.read_text(encoding='utf-8-sig'))


if __name__ == "__main__":
    unittest.main()
