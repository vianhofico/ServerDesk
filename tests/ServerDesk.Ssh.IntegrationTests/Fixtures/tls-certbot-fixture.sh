#!/bin/sh
set -eu

if [ "${1:-}" = "--version" ]; then
  echo "certbot 5.7.0"
  exit 0
fi

if [ "${1:-}" = "plugins" ]; then
  echo "* nginx"
  echo "Description: Nginx Web Server plugin"
  exit 0
fi

echo "unsupported certbot fixture command" >&2
exit 2
