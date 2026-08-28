#!/bin/sh
set -eu

if [ "${1:-}" = "-n" ]; then
  shift
fi

command_path=${1:?missing command}
shift
command_name=$(basename "$command_path")

if [ "$command_name" = "install" ]; then
  [ "${1:-}" = "-m" ] || exit 64
  mode=${2:?missing mode}
  shift 2
  [ "${1:-}" = "-o" ] || exit 64
  shift 2
  [ "${1:-}" = "-g" ] || exit 64
  shift 2
  [ "${1:-}" = "--" ] || exit 64
  shift
  source_path=${1:?missing source}
  destination_path=${2:?missing destination}
  exec install -m "$mode" -- "$source_path" "$destination_path"
fi

exec "$command_path" "$@"
