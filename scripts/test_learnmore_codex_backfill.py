import copy
import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("backfill", Path(__file__).with_name("learnmore_codex_backfill.py"))
b = importlib.util.module_from_spec(spec)
spec.loader.exec_module(b)
JA = "今日はいい天気です。"
RUBY = "<ruby>今日<rt>きょう</rt></ruby>はいい<ruby>天気<rt>てんき</rt></ruby>です。"


def source():
    return [{"LyricID": 8, "Japanese": JA, "TimeStamp": 12.5,
             "Chinese": "翻譯中...", "JapaneseRuby": "", "Roman": ""}]


def answer():
    return {"rows": [{"lyricId": 8, "Chinese": "今天天氣很好。", "JapaneseRuby": RUBY,
                      "Roman": "kyou wa ii tenki desu.", "needsReview": False, "reason": ""}]}


class ValidationTests(unittest.TestCase):
    def rejected(self, data, code, original=None):
        with self.assertRaisesRegex(b.BackfillError, code):
            b.validate_rows(original or source(), data)

    def test_valid_patch_keeps_source_and_timestamp_out_of_output(self):
        before = source()
        result = b.validate_rows(before, answer())
        self.assertEqual(before, source())
        self.assertEqual(result[0]["Chinese"], "今天天氣很好。")
        self.assertNotIn("Japanese", result[0])
        self.assertNotIn("TimeStamp", result[0])

    def test_refuses_changed_identity(self):
        value = answer()
        value["rows"][0]["lyricId"] = 9
        self.rejected(value, "lyric_identity_mismatch")

    def test_refuses_duplicate_or_extra_rows(self):
        value = answer()
        value["rows"].append(copy.deepcopy(value["rows"][0]))
        self.rejected(value, "row_count_mismatch")

    def test_refuses_reordered_rows(self):
        original = source() + [{**source()[0], "LyricID": 9}]
        value = {"rows": [{**answer()["rows"][0], "lyricId": 9}, answer()["rows"][0]]}
        self.rejected(value, "lyric_identity_mismatch", original)

    def test_refuses_extra_mutation_fields(self):
        for key in ["Japanese", "TimeStamp", "SongUid"]:
            with self.subTest(key=key):
                value = answer()
                value["rows"][0][key] = "changed"
                self.rejected(value, "invalid_row_shape")

    def test_existing_translation_cannot_be_overwritten(self):
        original = source()
        original[0]["Chinese"] = "原有譯文"
        self.rejected(answer(), "overwrite_not_allowed", original)

    def test_existing_valid_readings_cannot_be_overwritten(self):
        for field, value in [("JapaneseRuby", RUBY), ("Roman", "kyou wa ii tenki desu")]:
            with self.subTest(field=field):
                original = source()
                original[0][field] = value
                self.rejected(answer(), "overwrite_not_allowed", original)

    def test_missing_fields_need_explicit_review(self):
        value = answer()
        value["rows"][0]["Chinese"] = None
        self.rejected(value, "missing_required_backfill")
        value["rows"][0].update(needsReview=True, reason="需確認原文")
        result = b.validate_rows(source(), value)
        self.assertTrue(result[0]["needsReview"])
        self.assertNotIn("Chinese", result[0])

    def test_review_must_have_reason(self):
        value = answer()
        value["rows"][0]["needsReview"] = True
        self.rejected(value, "missing_review_reason")

    def test_refuses_blank_and_placeholder_translation(self):
        for text in ["", "  ", "翻譯中...", "待翻譯"]:
            value = answer()
            value["rows"][0]["Chinese"] = text
            self.rejected(value, "invalid_backfill_text")

    def test_refuses_html_in_plain_text_fields(self):
        for field in ["Chinese", "Roman"]:
            value = answer()
            value["rows"][0][field] = "<script>alert(1)</script>"
            self.rejected(value, "invalid_plain_text")

    def test_refuses_japanese_in_roman(self):
        value = answer()
        value["rows"][0]["Roman"] = "kyou は tenki"
        self.rejected(value, "invalid_roman_text")

    def test_iteration_marks_and_non_latin_letters_are_not_roman(self):
        for text in ["々", "ー", "α", "한글"]:
            self.assertFalse(b.valid_roman(text))
        self.assertTrue(b.valid_roman("kyō wa ii tenki desu."))

    def test_ruby_base_must_equal_source(self):
        value = answer()
        value["rows"][0]["JapaneseRuby"] = "別の歌詞"
        self.rejected(value, "ruby_source_mismatch")

    def test_ruby_rejects_script_attributes_comments_and_broken_nesting(self):
        for text in [RUBY + "<script/>", RUBY.replace("<ruby>", '<ruby onclick="x">', 1),
                     "<!--x-->" + RUBY, "<?x?>" + RUBY, RUBY.replace("</rt>", "</ruby>", 1),
                     "<ruby>今日</ruby>はいい天気です。", RUBY.replace('きょう',''), JA]:
            with self.subTest(text=text):
                self.assertFalse(b.valid_ruby(text, JA))

    def test_plain_kana_and_english_need_no_ruby(self):
        self.assertTrue(b.valid_ruby("ありがとう", "ありがとう"))
        self.assertTrue(b.valid_ruby("Hello!", "Hello!"))

    def test_unannotated_kanji_and_japanese_roman_are_eligible(self):
        original = source()[0]
        original.update(JapaneseRuby=JA, Roman="今日 wa")
        self.assertEqual(set(b.writable_fields(original)), set(b.FIELDS))


class RunnerTests(unittest.TestCase):
    def test_argv_disables_tools_and_all_configured_mcp_servers(self):
        settings = '[mcp_servers.external]\ncommand="secret-path"\n[profiles.other.mcp_servers."extra.server"]\ncommand="other-secret"\n'
        with patch.object(Path, "exists", return_value=True), patch.object(Path, "read_text", return_value=settings):
            args = b.cli_arguments(Path("codex"), Path("job"), Path("schema"), Path("out"))
        for value in ["features.shell_tool=false", "features.apps=false", "features.hooks=false",
                      "features.multi_agent=false", 'web_search="disabled"',
                      'mcp_servers={"external"={command="__learnmore_backfill_disabled__",enabled=false},"extra.server"={command="__learnmore_backfill_disabled__",enabled=false}}']:
            self.assertIn(value, args)
        self.assertNotIn("secret-path", " ".join(args))
        self.assertEqual(args[args.index("--sandbox") + 1], "read-only")

    def test_fake_cli_structured_output_without_network(self):
        if os.name == "nt":
            self.skipTest("Native Windows Codex is verified by the scheduled probe")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            fake = root / "fake-codex"
            fake.write_text("#!/usr/bin/env python3\nimport json,sys\nfrom pathlib import Path\nsys.stdin.read()\nPath(sys.argv[sys.argv.index('--output-last-message')+1]).write_text(" + repr(json.dumps(answer())) + ")\nprint(json.dumps({'type':'turn.completed'}))\n")
            fake.chmod(0o700)
            result = b.run_codex(fake, source(), 10, root)
            self.assertEqual(b.validate_rows(source(), result)[0]["lyricId"], 8)

    def test_tool_event_is_rejected(self):
        if os.name == "nt":
            self.skipTest("Linux subprocess fixture")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            fake = root / "fake-codex"
            fake.write_text('#!/usr/bin/env python3\nimport sys\nsys.stdin.read()\nprint(\'{"item":{"type":"command_execution"}}\')\n')
            fake.chmod(0o700)
            with self.assertRaisesRegex(b.BackfillError, "unexpected_tool_use"):
                b.run_codex(fake, source(), 10, root)

    def test_snapshot_batches_preserve_ids_and_hash_original_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            snapshot = root / "in.json"
            snapshot.write_text(json.dumps({"songUid": "fixture", "lyrics": source()}))
            with patch.object(b, "resolve_codex", return_value=Path("codex")), patch.object(b, "run_codex", return_value=answer()):
                result = b.process_snapshot(snapshot, root / "out.json", "codex", 16, 10)
            self.assertEqual(result["sourceSha256"], b.hashlib.sha256(snapshot.read_bytes()).hexdigest())
            self.assertNotIn("TimeStamp", result["patches"][0])
            self.assertFalse(result["needsReview"])


if __name__ == "__main__":
    unittest.main()
