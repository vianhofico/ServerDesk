#!/bin/sh
set -eu

case "${1:-}" in
  --version)
    [ "$#" -eq 1 ] || { echo "unexpected ufw version args" >&2; exit 64; }
    printf '%s\n' 'ufw 0.36.2'
    ;;
  status)
    [ "$#" -eq 2 ] && [ "${2:-}" = "numbered" ] || { echo "unexpected ufw status args" >&2; exit 65; }
    cat <<'EOF'
Status: active

     To                         Action      From
     --                         ------      ----
[ 1] 22/tcp                     ALLOW IN    10.20.0.0/16
[ 2] 443/tcp                    ALLOW IN    Anywhere
[ 3] 53/udp                     DENY OUT    192.0.2.53
EOF
    ;;
  *)
    echo "mutation or unsupported ufw invocation blocked: $*" >&2
    exit 66
    ;;
esac
