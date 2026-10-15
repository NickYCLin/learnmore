import sys
import unittest
from unittest.mock import patch

import diagnose_learnmore_ios_simulator as diagnostics


class SimulatorDiagnosticsTests(unittest.TestCase):
    def test_linux_does_not_launch_mac_probes(self):
        with patch.object(diagnostics.platform, "system", return_value="Linux"), \
                patch.object(diagnostics, "run_probe") as probe:
            report = diagnostics.collect_report()
        self.assertEqual("unsupported", report["status"])
        probe.assert_not_called()

    def test_missing_tool_is_reported(self):
        with patch.object(diagnostics.subprocess, "Popen", side_effect=FileNotFoundError("missing")):
            self.assertEqual("unavailable", diagnostics.run_probe(["missing"])["status"])

    def test_probe_timeout_keeps_partial_output(self):
        result = diagnostics.run_probe(
            [sys.executable, "-c", "import time; print('started', flush=True); time.sleep(30)"],
            timeout=0.5,
        )
        self.assertEqual("timeout", result["status"])
        self.assertIn("started", result["output"])

    def test_output_is_capped_and_failure_preserved(self):
        result = diagnostics.run_probe(
            [sys.executable, "-c", "print('diagnostic' * 100); raise SystemExit(3)"],
            output_limit=32,
        )
        self.assertEqual("failed", result["status"])
        self.assertEqual(3, result["returncode"])
        self.assertTrue(result["truncated"])
        self.assertEqual(32, len(result["output"]))

    def test_process_output_excludes_unrelated_processes(self):
        result = diagnostics.run_probe(
            [sys.executable, "-c", "print('PID COMM\\n123 backboardd\\n456 unrelated')"],
            process_filter=True,
        )
        self.assertIn("backboardd", result["output"])
        self.assertNotIn("unrelated", result["output"])


if __name__ == "__main__":
    unittest.main()
