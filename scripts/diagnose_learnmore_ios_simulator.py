"""Collect bounded, read-only host diagnostics; never boot or reset a simulator."""

import datetime
import json
import os
import platform
import signal
import subprocess
import tempfile


OUTPUT_LIMIT = 65536
PROBES = (
    ("os", ("sw_vers",)),
    ("architecture", ("uname", "-m")),
    ("developer_directory", ("xcode-select", "-p")),
    ("xcode", ("xcodebuild", "-version")),
    ("sdks", ("xcodebuild", "-showsdks")),
    ("runtimes", ("xcrun", "simctl", "list", "runtimes", "-j")),
    ("devices", ("xcrun", "simctl", "list", "devices", "-j")),
    ("disk", ("df", "-h", "/")),
    ("memory", ("sysctl", "hw.memsize", "vm.swapusage")),
    ("memory_pages", ("vm_stat",)),
    ("processes", ("ps", "-axo", "pid,ppid,%cpu,%mem,rss,comm")),
    ("simulator_logs", (
        "/usr/bin/log", "show", "--last", "5m", "--style", "compact",
        "--predicate", 'process CONTAINS "CoreSimulator" OR process == "backboardd"',
    )),
)


def run_probe(command, timeout=15, output_limit=OUTPUT_LIMIT, process_filter=False):
    with tempfile.TemporaryFile() as captured:
        try:
            process = subprocess.Popen(
                command, stdout=captured, stderr=subprocess.STDOUT,
                start_new_session=True,
            )
        except OSError as error:
            return {"status": "unavailable", "error": str(error)}
        try:
            returncode = process.wait(timeout=timeout)
            status = "ok" if returncode == 0 else "failed"
        except subprocess.TimeoutExpired:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            returncode = process.returncode
            status = "timeout"
        captured.seek(0)
        output = captured.read(output_limit + 1)
        truncated = len(output) > output_limit
        text = output[:output_limit].decode("utf-8", errors="replace")
        if process_filter:
            text = "\n".join(
                line for line in text.splitlines()
                if any(name in line.lower() for name in
                       ("pid", "simulator", "backboard", "springboard", "xcode"))
            )
        return {"status": status, "returncode": returncode,
                "output": text, "truncated": truncated}


def collect_report():
    report = {
        "captured_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "platform": platform.system(),
        "read_only": True,
        "notice": "Review paths, device names and logs before sharing; no environment or credentials are collected.",
    }
    if report["platform"] != "Darwin":
        report["status"] = "unsupported"
        report["reason"] = "macOS is required; no probes ran."
        return report
    report["status"] = "collected"
    report["probes"] = {
        name: run_probe(command, timeout=20 if name == "simulator_logs" else 15,
                        process_filter=name == "processes")
        for name, command in PROBES
    }
    return report


if __name__ == "__main__":
    print(json.dumps(collect_report(), ensure_ascii=False, indent=2))
