#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PATCH_FILE="${PATCH_FILE:-$SCRIPT_DIR/demoparser2_custom.patch}"
PYTHON_BIN="${PYTHON_BIN:-python3}"
DEMOPARSER_REPO="${DEMOPARSER_REPO:-https://github.com/LaihoE/demoparser.git}"
DEMOPARSER_REF="${DEMOPARSER_REF:-e8c1ad452ced4d5938219ac9a5ee6300ee1ea37c}"
WORK_DIR="${WORK_DIR:-$SCRIPT_DIR/data/demoparser2_build}"

if [[ ! -f "$PATCH_FILE" ]]; then
  echo "Missing patch file: $PATCH_FILE" >&2
  exit 1
fi

if ! "$PYTHON_BIN" - <<'PY'
import importlib.util
raise SystemExit(0 if importlib.util.find_spec("maturin") else 1)
PY
then
  echo "Missing Python package: maturin" >&2
  echo "Install it with: $PYTHON_BIN -m pip install maturin" >&2
  exit 1
fi

mkdir -p "$(dirname "$WORK_DIR")"

if [[ -d "$WORK_DIR/.git" ]]; then
  git -C "$WORK_DIR" fetch origin
else
  rm -rf "$WORK_DIR"
  git clone "$DEMOPARSER_REPO" "$WORK_DIR"
fi

git -C "$WORK_DIR" reset --hard "$DEMOPARSER_REF"
git -C "$WORK_DIR" clean -fdx
git -C "$WORK_DIR" apply "$PATCH_FILE"

(
  cd "$WORK_DIR/src/python"
  "$PYTHON_BIN" -m maturin build --release
)

wheel="$(
  find "$WORK_DIR/src/python/target/wheels" -maxdepth 1 -type f -name 'demoparser2-*.whl' \
    -printf '%T@ %p\n' | sort -nr | awk 'NR == 1 {print $2}'
)"

if [[ -z "$wheel" ]]; then
  echo "No demoparser2 wheel was produced." >&2
  exit 1
fi

"$PYTHON_BIN" -m pip install --force-reinstall "$wheel"

"$PYTHON_BIN" - <<'PY'
import importlib.metadata
import pathlib

dist = importlib.metadata.distribution("demoparser2")
root = pathlib.Path(dist.locate_file(""))
print(f"Installed demoparser2 {dist.version} from {root}")
PY
