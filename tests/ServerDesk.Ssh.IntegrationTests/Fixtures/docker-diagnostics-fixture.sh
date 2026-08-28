#!/bin/sh
set -eu
id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
case "${1:-}" in
  container)
    case "${2:-}" in
      inspect)
        [ "${3:-}" = '--' ] || exit 9
        [ "${4:-}" = "$id" ] || exit 9
        printf '%s\n' '[{"Id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Name":"/fixture-api","Created":"2026-08-28T03:00:00Z","Path":"dotnet","Args":["Api.dll"],"RestartCount":0,"Config":{"Image":"example/api:fixture","User":"1000:1000","WorkingDir":"/app","Env":["DATABASE_PASSWORD=hidden-value","ASPNETCORE_ENVIRONMENT=Production"],"Labels":{"fixture":"true"}},"State":{"Status":"running","Running":true,"Paused":false,"Restarting":false,"OOMKilled":false,"Dead":false,"Pid":123,"ExitCode":0,"StartedAt":"2026-08-28T03:00:01Z","FinishedAt":"0001-01-01T00:00:00Z","Health":{"Status":"healthy"}},"Mounts":[],"NetworkSettings":{"Networks":{}}}]'
        ;;
      logs)
        previous=''
        since=''
        for arg in "$@"; do
          if [ "$previous" = '--since' ]; then since="$arg"; fi
          previous="$arg"
        done
        if [ "$since" = '2026-08-28T04:00:02Z' ]; then
          printf '%s\n' '2026-08-28T04:00:03Z incremental'
        else
          printf '%s\n' '2026-08-28T04:00:01Z started'
          printf '%s\n' '2026-08-28T04:00:02Z warning' >&2
        fi
        ;;
      *) exit 8 ;;
    esac
    ;;
  stats)
    [ "${2:-}" = '--no-stream' ] || exit 9
    [ "${3:-}" = '--format' ] || exit 9
    [ "${4:-}" = '{{json .}}' ] || exit 9
    [ "${5:-}" = '--' ] || exit 9
    [ "${6:-}" = "$id" ] || exit 9
    printf '%s\n' '{"CPUPerc":"7.50%","MemUsage":"256MiB / 1GiB","MemPerc":"25.00%","NetIO":"1MB / 2MB","BlockIO":"4KiB / 8KiB","PIDs":"3"}'
    ;;
  *) exit 8 ;;
esac
