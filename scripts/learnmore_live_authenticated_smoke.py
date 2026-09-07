#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
import html
import json
import re
import secrets
import shlex
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from http.cookiejar import CookieJar
from pathlib import Path
from typing import Any

DEFAULT_BASE_URL = "https://magicplus-design.serveirc.com/LearnMore"
DEFAULT_HOST = os.environ.get("LEARNMORE_SSH_HOST", "localhost")
DEFAULT_SSH_PORT = int(os.environ.get("LEARNMORE_SSH_PORT", "22"))
DEFAULT_SSH_CONFIG = Path(os.environ.get("LEARNMORE_SSH_CONFIG", str(Path.home() / ".config/learnmore/ssh.txt")))
REMOTE_APPSETTINGS = os.environ.get("LEARNMORE_APPSETTINGS", r"D:\Web\LearnMore\appsettings.Local.json")
TEST_EMAIL = os.environ.get("LEARNMORE_TEST_EMAIL", "dev@example.com")
SMOKE_TOKEN_HEADER = "X-LearnMore-Smoke-Token"


@dataclass
class SshConfig:
    username: str
    password: str
    host: str = DEFAULT_HOST
    port: int = DEFAULT_SSH_PORT


class SmokeError(RuntimeError):
    pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="LearnMore live authenticated smoke for Manage/Edit/EditLyrics")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--ssh-port", type=int, default=DEFAULT_SSH_PORT)
    parser.add_argument("--ssh-config", type=Path, default=DEFAULT_SSH_CONFIG)
    parser.add_argument("--repair-test-login", action="store_true",
                        help="Create a smoke token and repair the TestAccount password in production appsettings.json when needed.")
    parser.add_argument("--borrow-song-if-empty", action="store_true",
                        help="If Manage has no editable songs, temporarily borrow one song for smoke and restore after verification.")
    parser.add_argument("--smoke-status", default="high_accuracy_processing")
    parser.add_argument("--smoke-reason", default="smoke 測試：高精度語音辨識中")
    parser.add_argument("--test-email", default=TEST_EMAIL)
    return parser.parse_args()


def load_ssh_config(path: Path, host: str, port: int) -> SshConfig:
    raw = path.read_text(encoding="utf-8")
    mapping: dict[str, str] = {}
    for line in raw.splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        mapping[key.strip()] = value.strip()
    username = mapping.get("username")
    password = mapping.get("password")
    if not username or not password:
        raise SmokeError(f"ssh config missing username/password: {path}")
    return SshConfig(username=username, password=password, host=host, port=port)


def run_local(command: list[str], *, input_text: str | None = None) -> str:
    result = subprocess.run(command, input=input_text, text=True, capture_output=True)
    if result.returncode != 0:
        safe_command = list(command)
        if len(safe_command) >= 3 and safe_command[0] == "sshpass" and safe_command[1] == "-p":
            safe_command[2] = "<redacted>"
        raise SmokeError(
            f"command failed ({result.returncode}): {shlex.join(safe_command)}"
            f"\nSTDOUT:\n{result.stdout}\nSTDERR:\n{result.stderr}"
        )
    return result.stdout


def ssh_base(config: SshConfig) -> list[str]:
    return [
        "sshpass", "-p", config.password,
        "ssh",
        "-o", "StrictHostKeyChecking=no",
        "-o", "PreferredAuthentications=password",
        "-o", "PubkeyAuthentication=no",
        "-p", str(config.port),
        f"{config.username}@{config.host}",
    ]


def scp_base(config: SshConfig) -> list[str]:
    return [
        "sshpass", "-p", config.password,
        "scp",
        "-o", "StrictHostKeyChecking=no",
        "-o", "PreferredAuthentications=password",
        "-o", "PubkeyAuthentication=no",
        "-P", str(config.port),
    ]


def remote_ps1(config: SshConfig, script_body: str) -> str:
    with tempfile.TemporaryDirectory(prefix="learnmore-smoke-") as tmpdir:
        local = Path(tmpdir) / "script.ps1"
        wrapped_script = f"""
try {{
{script_body}
}}
finally {{
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}}
""".strip()
        local.write_text(wrapped_script, encoding="utf-8")
        remote = "/C:/Temp/learnmore_live_authenticated_smoke.ps1"
        run_local(scp_base(config) + [str(local), f"{config.username}@{config.host}:{remote}"])
        return run_local(ssh_base(config) + [f"powershell -ExecutionPolicy Bypass -File C:\\Temp\\learnmore_live_authenticated_smoke.ps1"])


def read_remote_json(config: SshConfig, remote_path: str) -> dict[str, Any]:
    output = run_local(ssh_base(config) + [f"powershell -NoProfile -Command \"Get-Content '{remote_path}' -Raw\""])
    return json.loads(output)


PLACEHOLDER_PATTERNS = ("***", "__", "placeholder")


def looks_like_placeholder(password: str) -> bool:
    if not password.strip():
        return True
    lowered = password.lower()
    return any(token in lowered for token in PLACEHOLDER_PATTERNS)


def powershell_single_quote(value: str) -> str:
    return value.replace("'", "''")


def update_remote_test_account(
    config: SshConfig,
    *,
    new_password: str | None = None,
    new_smoke_token: str | None = None,
) -> None:
    updates: list[str] = []
    if new_password is not None:
        updates.append(
            f"$json.TestAccount.Password = '{powershell_single_quote(new_password)}'"
        )
    if new_smoke_token is not None:
        updates.append(
            "$json.TestAccount | Add-Member -NotePropertyName SmokeToken "
            f"-NotePropertyValue '{powershell_single_quote(new_smoke_token)}' -Force"
        )
    if not updates:
        return

    update_script = "\n".join(updates)
    script = f"""
$ErrorActionPreference = 'Stop'
$path = '{REMOTE_APPSETTINGS}'
$json = Get-Content $path -Raw | ConvertFrom-Json
if ($null -eq $json.TestAccount) {{ throw 'TestAccount section is missing' }}
{update_script}
$json | ConvertTo-Json -Depth 20 | Set-Content $path -Encoding UTF8
[pscustomobject]@{{ Updated = $true }} | ConvertTo-Json -Compress
""".strip()
    remote_ps1(config, script)


class SessionClient:
    def __init__(self) -> None:
        self.jar = CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))

    def post_json(
        self,
        url: str,
        payload: dict[str, Any],
        headers: dict[str, str] | None = None,
    ) -> tuple[int, str]:
        request_headers = {"Content-Type": "application/json"}
        request_headers.update(headers or {})
        req = urllib.request.Request(
            url,
            data=json.dumps(payload).encode(),
            headers=request_headers,
        )
        with self.opener.open(req, timeout=45) as response:
            return response.status, response.read().decode("utf-8", "ignore")

    def get_text(self, url: str) -> str:
        with self.opener.open(url, timeout=45) as response:
            return response.read().decode("utf-8", "ignore")


EDIT_LINK_PATTERNS = [
    re.compile(r'/LearnMore/Edit/([0-9A-Fa-f\-]+)'),
    re.compile(r'href="(/LearnMore/Edit/[^"]+)"'),
]


def try_login(
    session: SessionClient,
    base_url: str,
    email: str,
    password: str,
    smoke_token: str,
) -> tuple[int | None, str | None]:
    try:
        return session.post_json(
            base_url + "/Login/TestLogin",
            {"email": email, "password": password},
            {SMOKE_TOKEN_HEADER: smoke_token},
        )
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", "ignore")


def extract_song_uid(manage_html: str) -> str | None:
    for pattern in EDIT_LINK_PATTERNS:
        match = pattern.search(manage_html)
        if not match:
            continue
        value = match.group(1)
        if value.startswith("/LearnMore/Edit/"):
            value = value.rsplit("/", 1)[-1]
        return value
    return None


def borrow_song(config: SshConfig, email: str, smoke_status: str, smoke_reason: str) -> dict[str, str]:
    escaped_reason = smoke_reason.replace("'", "''")
    script = f"""
$ErrorActionPreference = 'Stop'
$config = Get-Content '{REMOTE_APPSETTINGS}' -Raw | ConvertFrom-Json
$connString = $config.ConnectionStrings.DefaultConnection
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
try {{
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT TOP 1 SongUid,
       ISNULL(HighAccuracyStatus, '') AS HighAccuracyStatus,
       ISNULL(HighAccuracyStatusReason, '') AS HighAccuracyStatusReason
FROM dbo.Songs
WHERE SongUid IS NOT NULL
  AND LTRIM(RTRIM(ISNULL(SongUid, ''))) <> ''
ORDER BY SongID DESC
"@
    $reader = $cmd.ExecuteReader()
    if (-not $reader.Read()) {{ throw 'NO_SONG_FOUND' }}
    $songUid = $reader['SongUid'].ToString()
    $originalStatus = $reader['HighAccuracyStatus'].ToString()
    $originalReason = $reader['HighAccuracyStatusReason'].ToString()
    $reader.Close()

    $userCmd = $conn.CreateCommand()
    $userCmd.CommandText = "SELECT Producer, Collaboration FROM dbo.Users WHERE Email = @Email"
    $null = $userCmd.Parameters.AddWithValue('@Email', '{email}')
    $userReader = $userCmd.ExecuteReader()
    if (-not $userReader.Read()) {{ throw 'TEST_USER_NOT_FOUND' }}
    $originalProducer = if ($userReader['Producer'] -eq [DBNull]::Value) {{ '' }} else {{ $userReader['Producer'].ToString() }}
    $originalCollaboration = if ($userReader['Collaboration'] -eq [DBNull]::Value) {{ '' }} else {{ $userReader['Collaboration'].ToString() }}
    $userReader.Close()

    $statusCmd = $conn.CreateCommand()
    $statusCmd.CommandText = "UPDATE dbo.Songs SET HighAccuracyStatus = @Status, HighAccuracyStatusReason = @Reason WHERE SongUid = @SongUid"
    $null = $statusCmd.Parameters.AddWithValue('@Status', '{smoke_status}')
    $null = $statusCmd.Parameters.AddWithValue('@Reason', '{escaped_reason}')
    $null = $statusCmd.Parameters.AddWithValue('@SongUid', $songUid)
    $null = $statusCmd.ExecuteNonQuery()

    $updateCmd = $conn.CreateCommand()
    $updateCmd.CommandText = "UPDATE dbo.Users SET Producer = @Producer WHERE Email = @Email"
    $null = $updateCmd.Parameters.AddWithValue('@Producer', $songUid)
    $null = $updateCmd.Parameters.AddWithValue('@Email', '{email}')
    $null = $updateCmd.ExecuteNonQuery()

    [pscustomobject]@{{
        SongUid = $songUid
        OriginalStatus = $originalStatus
        OriginalReason = $originalReason
        OriginalProducer = $originalProducer
        OriginalCollaboration = $originalCollaboration
    }} | ConvertTo-Json -Compress
}}
finally {{
    $conn.Close()
}}
""".strip()
    return json.loads(remote_ps1(config, script))


def mark_existing_song_for_smoke(config: SshConfig, song_uid: str, smoke_status: str, smoke_reason: str) -> dict[str, str]:
    escaped_reason = smoke_reason.replace("'", "''")
    script = f"""
$ErrorActionPreference = 'Stop'
$config = Get-Content '{REMOTE_APPSETTINGS}' -Raw | ConvertFrom-Json
$connString = $config.ConnectionStrings.DefaultConnection
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
try {{
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT SongUid,
       ISNULL(HighAccuracyStatus, '') AS HighAccuracyStatus,
       ISNULL(HighAccuracyStatusReason, '') AS HighAccuracyStatusReason
FROM dbo.Songs
WHERE SongUid = @SongUid
"@
    $null = $cmd.Parameters.AddWithValue('@SongUid', '{song_uid}')
    $reader = $cmd.ExecuteReader()
    if (-not $reader.Read()) {{ throw 'SONG_NOT_FOUND' }}
    $originalStatus = $reader['HighAccuracyStatus'].ToString()
    $originalReason = $reader['HighAccuracyStatusReason'].ToString()
    $reader.Close()

    $statusCmd = $conn.CreateCommand()
    $statusCmd.CommandText = "UPDATE dbo.Songs SET HighAccuracyStatus = @Status, HighAccuracyStatusReason = @Reason WHERE SongUid = @SongUid"
    $null = $statusCmd.Parameters.AddWithValue('@Status', '{smoke_status}')
    $null = $statusCmd.Parameters.AddWithValue('@Reason', '{escaped_reason}')
    $null = $statusCmd.Parameters.AddWithValue('@SongUid', '{song_uid}')
    $null = $statusCmd.ExecuteNonQuery()

    [pscustomobject]@{{
        SongUid = '{song_uid}'
        OriginalStatus = $originalStatus
        OriginalReason = $originalReason
        OriginalProducer = ''
        ModifiedProducer = $false
    }} | ConvertTo-Json -Compress
}}
finally {{
    $conn.Close()
}}
""".strip()
    return json.loads(remote_ps1(config, script))


def restore_song(config: SshConfig, email: str, snapshot: dict[str, Any]) -> None:
    original_status = str(snapshot.get("OriginalStatus", ""))
    original_reason = str(snapshot.get("OriginalReason", ""))
    original_producer = str(snapshot.get("OriginalProducer", "")).replace("'", "''")
    modified_producer = bool(snapshot.get("ModifiedProducer", True))
    status_expr = "$null" if not original_status else "'" + original_status.replace("'", "''") + "'"
    reason_expr = "$null" if not original_reason else "'" + original_reason.replace("'", "''") + "'"
    restore_producer = ""
    if modified_producer:
        restore_producer = f"""
    $userCmd = $conn.CreateCommand()
    $userCmd.CommandText = "UPDATE dbo.Users SET Producer = @Producer WHERE Email = @Email"
    $null = $userCmd.Parameters.AddWithValue('@Producer', '{original_producer}')
    $null = $userCmd.Parameters.AddWithValue('@Email', '{email}')
    $null = $userCmd.ExecuteNonQuery()
"""
    script = f"""
$ErrorActionPreference = 'Stop'
$config = Get-Content '{REMOTE_APPSETTINGS}' -Raw | ConvertFrom-Json
$connString = $config.ConnectionStrings.DefaultConnection
Add-Type -AssemblyName System.Data
$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
try {{
    $songCmd = $conn.CreateCommand()
    $songCmd.CommandText = "UPDATE dbo.Songs SET HighAccuracyStatus = @Status, HighAccuracyStatusReason = @Reason WHERE SongUid = @SongUid"
    $statusValue = {status_expr}
    $reasonValue = {reason_expr}
    $null = $songCmd.Parameters.AddWithValue('@Status', $(if ($null -eq $statusValue) {{ [DBNull]::Value }} else {{ $statusValue }}))
    $null = $songCmd.Parameters.AddWithValue('@Reason', $(if ($null -eq $reasonValue) {{ [DBNull]::Value }} else {{ $reasonValue }}))
    $null = $songCmd.Parameters.AddWithValue('@SongUid', '{snapshot['SongUid']}')
    $null = $songCmd.ExecuteNonQuery()
{restore_producer}
}}
finally {{
    $conn.Close()
}}
""".strip()
    remote_ps1(config, script)


def verify_restore(config: SshConfig, email: str, snapshot: dict[str, Any]) -> dict[str, Any]:
    expected_status = str(snapshot.get("OriginalStatus", "")).replace("'", "''")
    expected_reason = str(snapshot.get("OriginalReason", "")).replace("'", "''")
    expected_producer = str(snapshot.get("OriginalProducer", "")).replace("'", "''")
    modified_producer = bool(snapshot.get("ModifiedProducer", True))
    script = f"""
$ErrorActionPreference = 'Stop'
$config = Get-Content '{REMOTE_APPSETTINGS}' -Raw | ConvertFrom-Json
$conn = New-Object System.Data.SqlClient.SqlConnection($config.ConnectionStrings.DefaultConnection)
$conn.Open()
try {{
  $cmd = $conn.CreateCommand()
  $cmd.CommandText = "SELECT Producer FROM dbo.Users WHERE Email = @Email"
  $null = $cmd.Parameters.AddWithValue('@Email', '{email}')
  $producer = [string]$cmd.ExecuteScalar()

  $cmd2 = $conn.CreateCommand()
  $cmd2.CommandText = "SELECT ISNULL(HighAccuracyStatus,''), ISNULL(HighAccuracyStatusReason,'') FROM dbo.Songs WHERE SongUid = @SongUid"
  $null = $cmd2.Parameters.AddWithValue('@SongUid', '{snapshot['SongUid']}')
  $r = $cmd2.ExecuteReader()
  $null = $r.Read()
  $status = $r.GetString(0)
  $reason = $r.GetString(1)
  $r.Close()

  [pscustomobject]@{{
    ProducerRestored = if (${str(modified_producer).lower()}) {{ $producer -eq '{expected_producer}' }} else {{ $true }}
    StatusRestored = ($status -eq '{expected_status}')
    ReasonRestored = ($reason -eq '{expected_reason}')
    ProducerContainsSmokeSong = ($producer.Split(',') -contains '{snapshot['SongUid']}')
    Status = $status
    Reason = $reason
  }} | ConvertTo-Json -Compress
}}
finally {{
  $conn.Close()
}}
""".strip()
    return json.loads(remote_ps1(config, script))


def main() -> int:
    args = parse_args()
    config = load_ssh_config(args.ssh_config, args.host, args.ssh_port)
    appsettings = read_remote_json(config, REMOTE_APPSETTINGS)
    test_account = appsettings.get("TestAccount", {})
    email = test_account.get("Email") or args.test_email
    password = test_account.get("Password") or ""
    smoke_token = test_account.get("SmokeToken") or ""

    repaired_password: str | None = None
    repaired_smoke_token: str | None = None
    if looks_like_placeholder(password):
        if not args.repair_test_login:
            raise SmokeError("TestAccount.Password looks like placeholder; rerun with --repair-test-login")
        repaired_password = secrets.token_urlsafe(24)
        password = repaired_password
    if looks_like_placeholder(smoke_token):
        if not args.repair_test_login:
            raise SmokeError("TestAccount.SmokeToken is missing; rerun with --repair-test-login")
        repaired_smoke_token = secrets.token_urlsafe(32)
        smoke_token = repaired_smoke_token

    update_remote_test_account(
        config,
        new_password=repaired_password,
        new_smoke_token=repaired_smoke_token,
    )

    session = SessionClient()
    login_status, login_body = try_login(session, args.base_url, email, password, smoke_token)
    if login_status == 404 and repaired_smoke_token is not None:
        for _ in range(10):
            time.sleep(0.5)
            session = SessionClient()
            login_status, login_body = try_login(session, args.base_url, email, password, smoke_token)
            if login_status != 404:
                break
    if login_status == 401:
        if not args.repair_test_login:
            raise SmokeError("TestLogin returned 401; rerun with --repair-test-login")
        repaired_password = secrets.token_urlsafe(24)
        update_remote_test_account(config, new_password=repaired_password)
        password = repaired_password
        session = SessionClient()
        login_status, login_body = try_login(session, args.base_url, email, password, smoke_token)

    if login_status != 200 or not login_body or 'success' not in login_body.lower():
        raise SmokeError(f"TestLogin failed: status={login_status} body={login_body}")

    smoke_snapshot: dict[str, Any] | None = None
    try:
        manage_html = html.unescape(session.get_text(args.base_url + "/Media/Manage"))
        song_uid = extract_song_uid(manage_html)
        if not song_uid:
            if not args.borrow_song_if_empty:
                raise SmokeError("Manage has no editable songs; rerun with --borrow-song-if-empty")
            smoke_snapshot = borrow_song(config, email, args.smoke_status, args.smoke_reason)
            song_uid = smoke_snapshot["SongUid"]
        else:
            smoke_snapshot = mark_existing_song_for_smoke(config, song_uid, args.smoke_status, args.smoke_reason)

        manage_html = html.unescape(session.get_text(args.base_url + "/Media/Manage"))

        edit_html = html.unescape(session.get_text(f"{args.base_url}/Edit/{song_uid}"))
        edit_lyrics_html = html.unescape(session.get_text(f"{args.base_url}/EditLyrics/{song_uid}"))

        expected_label = {
            "high_accuracy_pending": "高精度排隊中",
            "high_accuracy_processing": "高精度處理中",
            "high_accuracy_completed": "高精度已完成",
            "high_accuracy_failed": "高精度失敗",
        }.get(args.smoke_status, args.smoke_status)

        checks = {
            "login_status": login_status,
            "email": email,
            "repaired_test_password": repaired_password is not None,
            "song_uid": song_uid,
            "manage_has_status_card": expected_label in manage_html and args.smoke_reason in manage_html,
            "edit_has_status_card": expected_label in edit_html and args.smoke_reason in edit_html,
            "editlyrics_has_status_card": expected_label in edit_lyrics_html and args.smoke_reason in edit_lyrics_html,
            "manage_edit_link": f"/LearnMore/Edit/{song_uid}" in manage_html,
            "manage_editlyrics_link": f"/LearnMore/EditLyrics/{song_uid}" in manage_html,
        }
        print(json.dumps(checks, ensure_ascii=False, indent=2))

        if not all(v for k, v in checks.items() if k.endswith("status_card") or k.endswith("edit_link") or k.endswith("editlyrics_link")):
            raise SmokeError("One or more live smoke assertions failed")
        return 0
    finally:
        if smoke_snapshot:
            restore_song(config, email, smoke_snapshot)
            verify = verify_restore(config, email, smoke_snapshot)
            print(json.dumps({"restore": verify}, ensure_ascii=False, indent=2))
            if not verify.get("ProducerRestored") or not verify.get("StatusRestored") or not verify.get("ReasonRestored"):
                raise SmokeError(f"Restore verification failed: {verify}")


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except SmokeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)
