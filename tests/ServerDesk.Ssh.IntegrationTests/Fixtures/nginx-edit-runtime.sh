#!/bin/sh
set -eu

fixture_dir=$(dirname "$0")
target_path=$(cat "$fixture_dir/target-path")

if [ "${1:-}" = "-t" ]; then
  if grep -q 'INVALID_LIVE_ONLY' "$target_path"; then
    echo 'nginx: live configuration test failed' >&2
    exit 1
  fi
  exit 0
fi

if [ "${1:-}" = "-s" ] && [ "${2:-}" = "reload" ]; then
  if [ -f "$fixture_dir/fail-reload" ]; then
    echo 'nginx: deterministic reload failure' >&2
    exit 1
  fi
  printf '%s\n' reload >> "$fixture_dir/reload.log"
  exit 0
fi

echo 'unexpected nginx fixture arguments' >&2
exit 64
