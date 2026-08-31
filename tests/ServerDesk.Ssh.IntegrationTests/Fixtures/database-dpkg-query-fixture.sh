#!/usr/bin/env sh
set -eu
if [ "${1:-}" = "-W" ] && [ "${3:-}" = "mssql-server" ]; then
  printf '%s\n' '17.0.4075.5'
  exit 0
fi
printf '%s\n' 'unsupported dpkg-query fixture invocation' >&2
exit 64
