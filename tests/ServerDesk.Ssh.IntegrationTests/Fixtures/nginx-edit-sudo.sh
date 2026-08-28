#!/bin/sh
set -eu

if [ "${1:-}" = "-n" ]; then
  shift
fi

command_path=${1:?missing command}
shift
command_name=$(basename "$command_path")

if [ "$command_name" = "install" ]; then
  filtered=""
  while [ "$#" -gt 0 ]; do
    case "$1" in
      -o|-g)
        shift
        [ "$#" -gt 0 ] && shift
        ;;
      *)
        filtered="$filtered $(printf '%s' "$1" | sed "s/'/'\\''/g" | sed "s/^/'/;s/$/'/")"
        shift
        ;;
    esac
  done
  eval "exec install $filtered"
fi

exec "$command_path" "$@"
