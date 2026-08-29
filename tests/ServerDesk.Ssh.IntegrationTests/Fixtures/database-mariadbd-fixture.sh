#!/usr/bin/env sh
set -eu
if [ "${1:-}" = "--version" ]; then
  printf '%s\n' 'mariadbd  Ver 11.4.5-MariaDB for debian-linux-gnu on x86_64'
  exit 0
fi
printf '%s\n' 'unsupported mariadbd fixture invocation' >&2
exit 64
