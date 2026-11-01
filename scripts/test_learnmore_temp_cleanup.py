import json
import contextlib
import io
import os
from pathlib import Path
import tempfile
import time
import unittest
import subprocess
from unittest.mock import patch

import learnmore_temp_cleanup as cleanup


class CleanupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='learnmore-cleanup-test-', suffix='-publish', dir='/tmp')
        self.root = Path(self.temp.name)
        # ctime 不能直接回填；以未來的截止時間模擬已經過保留期。
        self.cutoff = time.time() + 86400

    def tearDown(self):
        self.temp.cleanup()

    def file(self, name, value='payload'):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(value)
        return path

    def test_preview_and_apply_preserve_recent_files_evidence_models_and_links(self):
        old = self.file('build/app.dll')
        fresh = self.file('build/recent.dll')
        os.utime(fresh, (self.cutoff + 10, self.cutoff + 10))
        protected = [self.file(name) for name in ['receipt.json', 'tests.log', 'notes.md',
                     'appsettings.Production.json', 'models/weights.bin', 'model.pth', 'script.py']]
        link = self.root / 'linked.dll'
        link.symlink_to(fresh)
        hard = self.file('shared.dll')
        os.link(hard, self.root / 'shared-copy.dll')
        before = cleanup.cleanup_root(self.root, self.cutoff)
        self.assertEqual(before['files'], 1)
        self.assertTrue(old.exists())
        result = cleanup.cleanup_root(self.root, self.cutoff, apply=True)
        self.assertEqual(result['files'], 1)
        self.assertFalse(old.exists())
        self.assertTrue(fresh.exists())
        self.assertTrue(link.is_symlink())
        self.assertTrue(hard.exists())
        self.assertTrue(all(p.exists() for p in protected))

    def test_symlink_directory_never_traversed(self):
        with tempfile.TemporaryDirectory() as other:
            valuable = Path(other) / 'keep.dll'
            valuable.write_text('keep')
            (self.root / 'external').symlink_to(other, target_is_directory=True)
            cleanup.cleanup_root(self.root, self.cutoff, apply=True)
            self.assertTrue(valuable.exists())

    def test_repository_anywhere_blocks_entire_root(self):
        payload = self.file('a.dll')
        self.file('nested/.git', 'gitdir: elsewhere')
        result = cleanup.cleanup_root(self.root, self.cutoff, apply=True)
        self.assertEqual(result['reason'], 'repository_or_keep_marker')
        self.assertTrue(payload.exists())

    def test_keep_marker_blocks_entire_root(self):
        self.file('.keep')
        self.file('a.dll')
        self.assertEqual(cleanup.cleanup_root(self.root, self.cutoff, True)['files'], 0)

    def test_mounted_and_busy_roots_are_skipped(self):
        self.file('a.dll')
        for mounts, busy in [([self.root / 'nested'], []), ([self.root.parent], []), ([], [self.root])]:
            result = cleanup.cleanup_root(self.root, self.cutoff, True, mounts, busy)
            self.assertEqual(result['reason'], 'active_or_mounted')
            self.assertTrue((self.root / 'a.dll').exists())

    def test_registry_requires_narrow_paths_and_private_regular_file(self):
        registry = self.file('registry.json', json.dumps({'retired_publish_roots': [str(self.root)]}))
        registry.chmod(0o600)
        self.assertEqual(cleanup.load_registry(registry), [self.root])
        for root in ['/tmp', '/', '/tmp/learnmore-auto-codex', '/tmp/../etc', '/tmp/learnmore-model-cache']:
            registry.write_text(json.dumps({'retired_publish_roots': [root]}))
            with self.assertRaises(ValueError):
                cleanup.load_registry(registry)
        registry.write_text(json.dumps({'retired_publish_roots': [str(self.root)]}))
        registry.chmod(0o666)
        with self.assertRaises(ValueError):
            cleanup.load_registry(registry)

    def test_root_symlink_rejected(self):
        link = self.root / 'linked-root'
        link.symlink_to(self.root, target_is_directory=True)
        result = cleanup.cleanup_root(link, self.cutoff, True)
        self.assertEqual(result['reason'], 'unsafe_root')

    def test_virtual_environment_marker_blocks_cleanup(self):
        self.file('nested/pyvenv.cfg')
        payload = self.file('a.dll')
        result = cleanup.cleanup_root(self.root, self.cutoff, True)
        self.assertEqual(result['files'], 0)
        self.assertTrue(payload.exists())

    def test_docker_failure_stops_before_any_delete(self):
        registry = self.file('registry.json', json.dumps({'retired_publish_roots': [str(self.root)]}))
        registry.chmod(0o600)
        with tempfile.TemporaryDirectory() as home:
            with patch('sys.argv', ['cleanup', '--registry', str(registry), '--apply']), \
                    patch.object(Path, 'home', return_value=Path(home)), \
                    patch.object(cleanup, 'docker_mounts', side_effect=subprocess.CalledProcessError(1, 'docker')), \
                    patch.object(cleanup, 'cleanup_root') as remove, contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(SystemExit) as result:
                    cleanup.main()
                self.assertEqual(result.exception.code, 1)
                remove.assert_not_called()

    def test_replaced_root_rejected_before_deletion(self):
        self.file('a.dll')
        real_fstat = os.fstat
        def changed(fd):
            result = real_fstat(fd)
            values = list(result)
            values[1] += 1
            return os.stat_result(values)
        with patch.object(os, 'fstat', side_effect=changed):
            self.assertEqual(cleanup.cleanup_root(self.root, self.cutoff, True)['reason'], 'root_changed')
        self.assertTrue((self.root / 'a.dll').exists())


if __name__ == '__main__':
    unittest.main()
