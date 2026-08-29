#!/bin/sh
set -eu

case "${1:-}" in
  --version)
    [ "$#" -eq 1 ] || { echo "unexpected firewalld version args" >&2; exit 64; }
    printf '%s\n' '2.3.0'
    ;;
  --state)
    [ "$#" -eq 1 ] || { echo "unexpected firewalld state args" >&2; exit 65; }
    printf '%s\n' 'not running'
    exit 252
    ;;
  *)
    echo "mutation or unsupported firewalld invocation blocked: $*" >&2
    exit 66
    ;;
esac
