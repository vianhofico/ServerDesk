#!/bin/sh
set -eu
id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
state_file="${0}.state"
state='stopped'
if [ -f "$state_file" ]; then state=$(cat "$state_file"); fi
write_state() { printf '%s' "$1" > "$state_file"; }
inspect_json() {
  running=false
  paused=false
  status='exited'
  pid=0
  started='2026-08-28T04:00:00Z'
  case "$state" in
    running) running=true; status='running'; pid=123 ;;
    restarted) running=true; status='running'; pid=124; started='2026-08-28T04:01:00Z' ;;
    paused) running=true; paused=true; status='paused'; pid=123 ;;
    stopped) ;;
    removed) echo 'Error: No such container: aaaaaaaaaaaa' >&2; exit 1 ;;
  esac
  printf '[{"Id":"%s","Name":"/fixture-api","Created":"2026-08-28T03:00:00Z","Path":"/bin/sh","Args":[],"RestartCount":0,"Config":{"Image":"example/api:fixture","User":"","WorkingDir":"/app","Env":[],"Labels":{}},"State":{"Status":"%s","Running":%s,"Paused":%s,"Restarting":false,"OOMKilled":false,"Dead":false,"Pid":%s,"ExitCode":0,"StartedAt":"%s","FinishedAt":"","Health":{"Status":""}},"Mounts":[],"NetworkSettings":{"Networks":{}}}]\n' "$id" "$status" "$running" "$paused" "$pid" "$started"
}
case "${1:-}" in
  container)
    verb="${2:-}"
    [ "${3:-}" = '--' ] || { [ "$verb" = 'kill' ] || exit 9; }
    case "$verb" in
      inspect)
        [ "${4:-}" = "$id" ] || exit 9
        inspect_json
        ;;
      start)
        [ "${4:-}" = "$id" ] || exit 9
        write_state running
        printf '%s\n' "$id"
        ;;
      stop)
        [ "${4:-}" = "$id" ] || exit 9
        write_state stopped
        printf '%s\n' "$id"
        ;;
      restart)
        [ "${4:-}" = "$id" ] || exit 9
        write_state restarted
        printf '%s\n' "$id"
        ;;
      pause)
        [ "${4:-}" = "$id" ] || exit 9
        write_state paused
        printf '%s\n' "$id"
        ;;
      unpause)
        [ "${4:-}" = "$id" ] || exit 9
        write_state running
        printf '%s\n' "$id"
        ;;
      kill)
        [ "${3:-}" = '--signal' ] || exit 9
        [ "${4:-}" = 'KILL' ] || exit 9
        [ "${5:-}" = '--' ] || exit 9
        [ "${6:-}" = "$id" ] || exit 9
        write_state stopped
        printf '%s\n' "$id"
        ;;
      rm)
        [ "${4:-}" = "$id" ] || exit 9
        [ "$state" != 'running' ] && [ "$state" != 'paused' ] && [ "$state" != 'restarted' ] || { echo 'container is running' >&2; exit 1; }
        write_state removed
        printf '%s\n' "$id"
        ;;
      *) exit 8 ;;
    esac
    ;;
  *) exit 8 ;;
esac
