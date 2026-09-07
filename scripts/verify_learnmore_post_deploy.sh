#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${LEARNMORE_BASE_URL:-https://magicplus-design.serveirc.com/LearnMore}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

cd "$REPO_ROOT"

echo "[verify] health check: $BASE_URL/"
curl --http2 -L --max-time 45 -sS -D - -o /dev/null "$BASE_URL/"

echo "[verify] authenticated smoke"
bash "$SCRIPT_DIR/run_learnmore_live_authenticated_smoke.sh" "$@"

echo "[verify] Mika About attribution"
bash "$SCRIPT_DIR/run_learnmore_mika_about_attribution.sh" --base-url "$BASE_URL"

echo "[verify] Mika embed-config contract"
bash "$SCRIPT_DIR/run_learnmore_mika_embed_config_contract.sh" \
  --learnmore-url "$BASE_URL/Lyrics/ea3d8e96-fb5c-4bff-bf47-a9683e844eff"

echo "[verify] Mika avatar switch smoke"
bash "$SCRIPT_DIR/run_learnmore_mika_avatar_switch_smoke.sh" \
  --url "$BASE_URL/Lyrics/ea3d8e96-fb5c-4bff-bf47-a9683e844eff"
