#!/bin/sh
set -eu

state_file="${0}.state"
config_file="${0}.yaml"
project_dir=$(dirname "$config_file")

if [ ! -f "$state_file" ]; then
  printf 'up\n' > "$state_file"
fi

if [ "$#" -lt 2 ] || [ "$1" != "compose" ]; then
  echo "expected docker compose command" >&2
  exit 91
fi
shift

if [ "$1" = "version" ]; then
  [ "$#" -eq 2 ] && [ "$2" = "--short" ] || exit 92
  printf '2.39.1\n'
  exit 0
fi

if [ "$1" = "ls" ]; then
  [ "$#" -eq 4 ] && [ "$2" = "--all" ] && [ "$3" = "--format" ] && [ "$4" = "json" ] || exit 93
  state=$(cat "$state_file")
  if [ "$state" = "up" ]; then
    status="running(1)"
  else
    status="exited(0)"
  fi
  printf '[{"Name":"serverdesk","Status":"%s","ConfigFiles":"%s"}]\n' "$status" "$config_file"
  exit 0
fi

project_name=""
working_dir=""
files=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --project-name)
      [ "$#" -ge 2 ] || exit 94
      project_name="$2"
      shift 2
      ;;
    --project-directory)
      [ "$#" -ge 2 ] || exit 95
      working_dir="$2"
      shift 2
      ;;
    --file)
      [ "$#" -ge 2 ] || exit 96
      files="${files}${files:+|}$2"
      shift 2
      ;;
    *)
      break
      ;;
  esac
done

[ "$project_name" = "serverdesk" ] || { echo "bad project name" >&2; exit 97; }
[ "$working_dir" = "$project_dir" ] || { echo "bad project directory" >&2; exit 98; }
[ -n "$files" ] || { echo "missing --file" >&2; exit 99; }
[ "$#" -ge 1 ] || exit 100
verb="$1"
shift

case "$verb" in
  config)
    [ "$#" -eq 1 ] && [ "$1" = "--quiet" ] || exit 101
    old_ifs=$IFS
    IFS='|'
    for candidate in $files; do
      [ -f "$candidate" ] || { echo "no such file: $candidate" >&2; IFS=$old_ifs; exit 1; }
      if grep -q 'INVALID_COMPOSE' "$candidate"; then
        echo "services.api.image must be a string" >&2
        IFS=$old_ifs
        exit 1
      fi
    done
    IFS=$old_ifs
    exit 0
    ;;
  ps)
    [ "$#" -eq 3 ] && [ "$1" = "--all" ] && [ "$2" = "--format" ] && [ "$3" = "json" ] || exit 102
    state=$(cat "$state_file")
    if [ "$state" = "up" ]; then
      printf '[{"Name":"serverdesk-api-1","Service":"api","State":"running","Status":"Up 1 minute","Health":"healthy","Image":"example/api:latest","Publishers":[{"PublishedPort":8080,"TargetPort":80,"Protocol":"tcp"}]}]\n'
    else
      printf '[]\n'
    fi
    exit 0
    ;;
  logs)
    [ "$#" -eq 5 ] && [ "$1" = "--no-color" ] && [ "$2" = "--timestamps" ] && [ "$3" = "--tail" ] || exit 103
    printf 'api-1  | 2026-08-28T06:00:00Z ready\n'
    exit 0
    ;;
  down)
    [ "$#" -eq 0 ] || exit 104
    printf 'down\n' > "$state_file"
    exit 0
    ;;
  up)
    [ "$#" -eq 1 ] && [ "$1" = "--detach" ] || exit 105
    printf 'up\n' > "$state_file"
    exit 0
    ;;
  restart)
    [ "$#" -eq 0 ] || exit 106
    printf 'up\n' > "$state_file"
    exit 0
    ;;
  pull|build)
    [ "$#" -eq 0 ] || exit 107
    exit 0
    ;;
  *)
    echo "unexpected compose verb: $verb" >&2
    exit 108
    ;;
esac
