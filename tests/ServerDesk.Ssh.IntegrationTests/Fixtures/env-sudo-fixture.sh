#!/usr/bin/env bash
set -euo pipefail

if [[ "${1:-}" != "-n" ]]; then
  echo "env sudo fixture requires -n" >&2
  exit 64
fi
shift

command_name="${1:-}"
shift || true

case "$command_name" in
  install)
    mode=""
    expected_uid=""
    expected_gid=""
    while [[ $# -gt 0 ]]; do
      case "$1" in
        -m)
          mode="${2:-}"
          shift 2
          ;;
        -o)
          expected_uid="${2:-}"
          shift 2
          ;;
        -g)
          expected_gid="${2:-}"
          shift 2
          ;;
        --)
          shift
          break
          ;;
        *)
          echo "unsupported install argument: $1" >&2
          exit 64
          ;;
      esac
    done

    if [[ $# -ne 2 || -z "$mode" || -z "$expected_uid" || -z "$expected_gid" ]]; then
      echo "invalid install shape" >&2
      exit 64
    fi
    if [[ "$expected_uid" != "$(id -u)" || "$expected_gid" != "$(id -g)" ]]; then
      echo "fixture refuses ownership change" >&2
      exit 77
    fi

    source_path="$1"
    destination_path="$2"
    /usr/bin/install -m "$mode" -- "$source_path" "$destination_path"
    ;;

  mv)
    if [[ "${1:-}" != "-f" || "${2:-}" != "--" || $# -ne 4 ]]; then
      echo "invalid mv shape" >&2
      exit 64
    fi
    /usr/bin/mv -f -- "$3" "$4"
    ;;

  rm)
    if [[ "${1:-}" != "-f" || "${2:-}" != "--" || $# -ne 3 ]]; then
      echo "invalid rm shape" >&2
      exit 64
    fi
    /usr/bin/rm -f -- "$3"
    ;;

  *)
    echo "unsupported privileged command: $command_name" >&2
    exit 77
    ;;
esac
