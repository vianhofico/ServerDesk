#!/usr/bin/env sh
set -eu
if [ "${1:-}" = "--version" ]; then
  printf '%s\n' 'mysqld  Ver 8.4.4 for Linux on x86_64 (MySQL Community Server - GPL)'
  exit 0
fi
printf '%s\n' 'unsupported mysqld fixture invocation' >&2
exit 64
