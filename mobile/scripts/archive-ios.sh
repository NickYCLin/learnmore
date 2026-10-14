#!/bin/bash
set -euo pipefail

mobile_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$mobile_dir"

xcode_major="$(xcodebuild -version | awk '/^Xcode / { split($2, version, "."); print version[1] }')"
if [[ -z "$xcode_major" || "$xcode_major" -lt 26 ]]; then
  echo "需要 Xcode 26 以上，請先安裝並選擇對應的 Xcode。" >&2
  exit 1
fi
if [[ ! "${LEARNMORE_APPLE_TEAM_ID:-}" =~ ^[A-Z0-9]{10}$ ]]; then
  echo "請以 LEARNMORE_APPLE_TEAM_ID 指定 Apple 開發團隊 ID。" >&2
  exit 1
fi
if [[ ! "${LEARNMORE_BUILD_NUMBER:-}" =~ ^[1-9][0-9]*$ ]]; then
  echo "請以 LEARNMORE_BUILD_NUMBER 指定本次上傳的新 build number（正整數）。" >&2
  exit 1
fi

npm run ios:sync
archive_path="$mobile_dir/../artifacts/ios/LearnMore-${LEARNMORE_BUILD_NUMBER}.xcarchive"
if [[ -e "$archive_path" ]]; then
  echo "封存已存在，請使用新的 build number：$archive_path" >&2
  exit 1
fi
mkdir -p "$(dirname "$archive_path")"
xcodebuild -project ios/App/App.xcodeproj -scheme App \
  -configuration Release -destination 'generic/platform=iOS' \
  -archivePath "$archive_path" \
  DEVELOPMENT_TEAM="$LEARNMORE_APPLE_TEAM_ID" \
  CURRENT_PROJECT_VERSION="$LEARNMORE_BUILD_NUMBER" \
  CODE_SIGN_STYLE=Automatic -allowProvisioningUpdates archive

echo "封存完成：$archive_path"
echo "請在 Xcode Organizer 驗證並上傳，再從 App Store Connect 選取 TestFlight build。"
