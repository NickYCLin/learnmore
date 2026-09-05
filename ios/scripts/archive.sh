#!/bin/bash
set -euo pipefail
ios_dir="$(cd "$(dirname "$0")/.." && pwd)"
python3 "$ios_dir/scripts/preflight.py" --stage archive
archive_path="${1:-$ios_dir/DerivedData/LearnMore.xcarchive}"
xcodebuild -project "$ios_dir/LearnMore.xcodeproj" -scheme LearnMore \
  -configuration Release -destination 'generic/platform=iOS' \
  -archivePath "$archive_path" archive
echo "Archive created: $archive_path"
echo "Validate and upload this archive using Xcode Organizer after signing is configured."
