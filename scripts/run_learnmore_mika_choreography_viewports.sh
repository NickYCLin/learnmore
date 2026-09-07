#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPEAT_COUNT="${MIKA_SMOKE_REPEAT:-1}"

if ! [[ "$REPEAT_COUNT" =~ ^[1-9][0-9]*$ ]]; then
  echo "[mika-smoke] failed: MIKA_SMOKE_REPEAT must be a positive integer" >&2
  exit 1
fi

for ((run_index = 1; run_index <= REPEAT_COUNT; run_index += 1)); do
  if [[ "$REPEAT_COUNT" != "1" ]]; then
    echo "[mika-smoke] run $run_index/$REPEAT_COUNT"
  fi

  echo "[mika-smoke] mobile viewport 390x844"
  "$SCRIPT_DIR/run_learnmore_mika_choreography_table.sh" \
    --viewport-width 390 \
    --viewport-height 844 \
    "$@"

  echo
  echo "[mika-smoke] desktop viewport 1600x900"
  "$SCRIPT_DIR/run_learnmore_mika_choreography_table.sh" "$@"

  if [[ "$run_index" -lt "$REPEAT_COUNT" ]]; then
    echo
  fi
done
