#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMPROVER_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BETTERBOT_ROOT="$(cd "$IMPROVER_ROOT/.." && pwd)"

PYTHON_BIN="${PYTHON_BIN:-python3}"
DEMOS_DIR="${DEMOS_DIR:-$BETTERBOT_ROOT/demos}"
JOBS="${JOBS:-8}"
MAX_TASKS_PER_CHILD="${MAX_TASKS_PER_CHILD:-1}"
RESET="${RESET:-1}"
WORK_DIR="${WORK_DIR:-$SCRIPT_DIR/data/archive_work}"
if [[ -z "${EXPORT_MANIFEST:-}" ]]; then
  EXPORT_MANIFEST="$IMPROVER_ROOT/addons/counterstrikesharp/plugins/ProOpeningReplay/data/{map}_openings_manifest.json"
fi

"$PYTHON_BIN" - <<'PY'
import importlib.util
import sys

missing = [name for name in ("demoparser2", "tqdm") if importlib.util.find_spec(name) is None]
if missing:
    print("Missing Python package(s): " + ", ".join(missing), file=sys.stderr)
    print("Install them with: python3 -m pip install demoparser2 tqdm", file=sys.stderr)
    raise SystemExit(1)
PY

args=(
  "$SCRIPT_DIR/pro_opening_pipeline.py"
  extract
  "$DEMOS_DIR"
  --export-manifest "$EXPORT_MANIFEST"
  --work-dir "$WORK_DIR"
  --tickrate 64
  --jobs "$JOBS"
  --max-tasks-per-child "$MAX_TASKS_PER_CHILD"
  --progress
)

if [[ "$RESET" != "0" ]]; then
  args+=(--reset)
fi

exec "$PYTHON_BIN" "${args[@]}" "$@"
