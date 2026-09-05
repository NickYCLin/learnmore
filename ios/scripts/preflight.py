#!/usr/bin/env python3
"""Read-only App Store readiness checks. Exits 1 until all required checks pass."""
import argparse
import json
import pathlib
import plistlib
import re
import subprocess
import sys
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
checks = []

def check(name, passed, detail=""):
    checks.append({"check": name, "passed": bool(passed), "detail": detail})

def command(*args):
    return subprocess.run(args, capture_output=True, text=True, timeout=120)

parser = argparse.ArgumentParser()
parser.add_argument("--offline", action="store_true", help="Skip production checks; cannot establish release readiness")
parser.add_argument("--json", action="store_true")
parser.add_argument("--stage", choices=["archive", "submission"], default="submission",
                    help="Archive checks only build/signing prerequisites; submission also checks operations and live services")
args = parser.parse_args()

version = command("xcodebuild", "-version")
match = re.search(r"Xcode (\d+)", version.stdout)
check("Xcode 26 or newer", bool(match and int(match[1]) >= 26), version.stdout.strip())
signing = command("security", "find-identity", "-v", "-p", "codesigning")
check("Signing identity", bool(re.search(r"[1-9]\d* valid identities found", signing.stdout)), "Configure Apple Developer signing in Xcode")
config_path = ROOT / "Config/Local.xcconfig"
config = config_path.read_text() if config_path.exists() else ""
values = dict(re.findall(r"^([A-Z_]+)\s*=\s*([^\n]*)", config, re.M))
for key in ["LEARNMORE_BUNDLE_ID", "LEARNMORE_TEAM_ID", "GOOGLE_IOS_CLIENT_ID", "GOOGLE_SERVER_CLIENT_ID", "GOOGLE_REVERSED_CLIENT_ID"]:
    value = values.get(key, "").strip()
    check(key, value and not re.search(r"YOUR|yourcompany|com\.learnmore\.ios$", value), "Set in ios/Config/Local.xcconfig")

for name in ["Info.plist", "LearnMore.entitlements", "PrivacyInfo.xcprivacy"]:
    try:
        plistlib.loads((ROOT / "LearnMore" / name).read_bytes())
        check(name, True)
    except Exception as exc:
        check(name, False, type(exc).__name__)

icon = ROOT / "LearnMore/Assets.xcassets/AppIcon.appiconset/AppIcon.png"
if icon.exists():
    info = command("sips", "-g", "pixelWidth", "-g", "pixelHeight", "-g", "hasAlpha", str(icon)).stdout
    check("1024px opaque App Icon", "pixelWidth: 1024" in info and "pixelHeight: 1024" in info and "hasAlpha: no" in info)
else:
    check("App Icon", False)

status = json.loads((ROOT / "AppStore/release-status.json").read_text())
if args.stage == "submission":
    required_evidence = [
        "contentRightsConfirmed", "privacyOperationsConfirmed", "databaseMigrationVerified",
        "realDeviceOAuthVerified", "crossPlatformFavoritesVerified", "accountDeletionVerified",
        "testFlightVerified", "screenshotsReady", "reviewAccessReady", "ageRatingCompleted",
        "appPrivacyCompleted",
    ]
    for key in required_evidence:
        check(key, status.get(key) is True, "Requires boolean true and evidence in ios/AppStore/VALIDATION.md")
    check("Public support email", bool(re.fullmatch(r"[^\s@]+@[^\s@]+\.[^\s@]+", status.get("supportEmail", ""))))
    check("Operator name", bool(status.get("operatorName", "").strip()))

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

if args.stage == "archive":
    pass
elif args.offline:
    check("Production integration", False, "Not checked (--offline)")
else:
    info = plistlib.loads((ROOT / "LearnMore/Info.plist").read_bytes())
    base = info["LearnMoreServerURL"].rstrip("/") + "/"
    endpoints = {
        "api/mobile/v1/songs?pageSize=1": lambda data: isinstance(data.get("songs"), list) and len(data["songs"]) == 1,
        "api/mobile/v1/auth/providers": lambda data: data.get("google") is True and data.get("apple") is True,
        "Mobile/Privacy": None,
        "Mobile/Support": None,
    }
    opener = urllib.request.build_opener(NoRedirect)
    for path, validator in endpoints.items():
        try:
            with opener.open(urllib.request.Request(base + path, headers={"User-Agent": "LearnMore-Release-Preflight"}), timeout=20) as response:
                data = response.read(2_000_000)
                passed = response.status == 200
                if validator: passed = passed and validator(json.loads(data))
                else: passed = passed and b"LearnMore" in data and b"mailto:" in data
                check(path, passed, f"HTTP {response.status}")
        except Exception as exc:
            check(path, False, str(exc))

if args.json:
    print(json.dumps({"ready": all(c["passed"] for c in checks), "checks": checks}, ensure_ascii=False, indent=2))
else:
    for c in checks:
        print(f"{'PASS' if c['passed'] else 'BLOCKED'} {c['check']}" + (f" — {c['detail']}" if c['detail'] else ""))
sys.exit(0 if all(c["passed"] for c in checks) else 1)
