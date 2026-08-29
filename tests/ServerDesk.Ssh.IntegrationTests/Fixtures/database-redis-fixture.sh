#!/usr/bin/env sh
set -eu
if [ "${1:-}" = "--version" ]; then
  printf '%s\n' 'Redis server v=8.0.2 sha=00000000:0 malloc=jemalloc bits=64 build=fixture'
  exit 0
fi
printf '%s\n' 'unsupported redis-server fixture invocation' >&2
exit 64
