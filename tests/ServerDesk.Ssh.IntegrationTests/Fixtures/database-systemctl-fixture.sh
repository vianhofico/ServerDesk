#!/usr/bin/env sh
set -eu
if [ "${1:-}" != "show" ]; then
  printf '%s\n' 'unsupported systemctl fixture invocation' >&2
  exit 64
fi
unit="${2:-}"
case "$unit" in
  postgresql.service|mysql.service|mariadb.service|redis-server.service)
    printf '%s\n' 'LoadState=loaded' 'ActiveState=active' 'SubState=running'
    exit 0
    ;;
  *)
    printf 'Unit %s could not be found.\n' "$unit" >&2
    exit 1
    ;;
esac
