#!/bin/sh
if [ "${1:-}" = 'stats' ]; then
  sleep 10
  printf '%s\n' '{"CPUPerc":"1%","MemUsage":"1MiB / 2MiB","MemPerc":"50%","NetIO":"1kB / 2kB","BlockIO":"3kB / 4kB","PIDs":"1"}'
  exit 0
fi
exit 8
