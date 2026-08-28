#!/bin/sh
set -eu

STATE_FILE="$0.state"
[ -f "$STATE_FILE" ] || printf '%s\n' running > "$STATE_FILE"
STATE="$(cat "$STATE_FILE")"

has_arg() {
  needle="$1"
  shift
  for arg in "$@"; do
    [ "$arg" = "$needle" ] && return 0
  done
  return 1
}

[ "${1:-}" = "compose" ] || { echo "expected compose" >&2; exit 64; }
shift

if has_arg version "$@"; then
  printf '%s\n' '2.40.0'
  exit 0
fi

if has_arg ls "$@"; then
  if [ "$STATE" = down ]; then
    printf '%s\n' '[]'
  else
    printf '%s\n' '[{"Name":"serverdesk-demo","Status":"running(1)","ConfigFiles":"/srv/demo/compose.yaml"}]'
  fi
  exit 0
fi

if has_arg ps "$@"; then
  if [ "$STATE" = down ]; then
    printf '%s\n' '[]'
  else
    printf '%s\n' '[{"ID":"aaaaaaaaaaaaaaaa","Name":"serverdesk-demo-api-1","Service":"api","Image":"demo/api:latest","State":"running","Status":"Up","Publishers":[]}]'
  fi
  exit 0
fi

if has_arg config "$@"; then
  if has_arg --quiet "$@"; then
    exit 0
  fi
  printf '%s\n' '{"name":"serverdesk-demo","services":{"api":{"image":"demo/api:latest"}}}'
  exit 0
fi

if has_arg logs "$@"; then
  printf '%s\n' '2026-08-28T08:00:00Z api | ready'
  printf '%s\n' '2026-08-28T08:00:01Z api | healthy'
  exit 0
fi

if has_arg up "$@"; then
  printf '%s\n' running > "$STATE_FILE"
  exit 0
fi

if has_arg restart "$@"; then
  printf '%s\n' running > "$STATE_FILE"
  exit 0
fi

if has_arg pull "$@" || has_arg build "$@"; then
  exit 0
fi

if has_arg down "$@"; then
  printf '%s\n' down > "$STATE_FILE"
  exit 0
fi

echo "unexpected compose invocation: $*" >&2
exit 65
