#!/usr/bin/env sh
set -eu
if [ "${1:-}" = "--version" ]; then
  printf '%s\n' 'postgres (PostgreSQL) 17.4'
  exit 0
fi
printf '%s\n' 'unsupported postgres fixture invocation' >&2
exit 64
