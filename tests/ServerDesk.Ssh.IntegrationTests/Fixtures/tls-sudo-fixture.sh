#!/bin/sh
set -eu

dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

if [ "${1:-}" = "-n" ]; then
  shift
fi

command_name=${1:-}
[ -n "$command_name" ] || exit 2
shift

if [ "$command_name" = "certbot" ]; then
  action=${1:-}
  shift || true

  if [ "$action" = "certificates" ]; then
    cat <<EOF
Found the following certificates:
  Certificate Name: example.test
    Domains: example.test www.example.test
    Expiry Date: simulated
    Certificate Path: $dir/cert.pem
    Private Key Path: $dir/key.pem
EOF
    if [ -f "$dir/obtained.state" ]; then
      cat <<EOF
  Certificate Name: new.example.test
    Domains: new.example.test
    Expiry Date: simulated
    Certificate Path: $dir/new-cert.pem
    Private Key Path: $dir/new-key.pem
EOF
    fi
    exit 0
  fi

  if [ "$action" = "renew" ]; then
    certificate_name=""
    while [ "$#" -gt 0 ]; do
      case "$1" in
        --cert-name)
          shift
          certificate_name=${1:-}
          ;;
      esac
      shift || true
    done
    [ "$certificate_name" = "example.test" ] || exit 3
    /usr/bin/openssl req -x509 -new -key "$dir/key.pem" -out "$dir/cert.pem" -days 120 \
      -subj "/CN=example.test" -addext "subjectAltName=DNS:example.test,DNS:www.example.test" >/dev/null 2>&1
    echo "renew" >> "$dir/certbot.log"
    exit 0
  fi

  if [ "$action" = "certonly" ]; then
    certificate_name=""
    domains=""
    while [ "$#" -gt 0 ]; do
      case "$1" in
        --cert-name)
          shift
          certificate_name=${1:-}
          ;;
        -d)
          shift
          domains="$domains ${1:-}"
          ;;
      esac
      shift || true
    done
    [ "$certificate_name" = "new.example.test" ] || exit 4
    echo "$domains" | grep -q "new.example.test" || exit 5
    /usr/bin/openssl req -x509 -newkey rsa:2048 -nodes \
      -keyout "$dir/new-key.pem" -out "$dir/new-cert.pem" -days 90 \
      -subj "/CN=new.example.test" -addext "subjectAltName=DNS:new.example.test" >/dev/null 2>&1
    : > "$dir/obtained.state"
    echo "obtain" >> "$dir/certbot.log"
    exit 0
  fi
fi

if [ "$command_name" = "nginx" ]; then
  if [ "${1:-}" = "-t" ]; then
    echo "nginx: configuration file test is successful" >&2
    exit 0
  fi
  if [ "${1:-}" = "-s" ] && [ "${2:-}" = "reload" ]; then
    echo "reload" >> "$dir/nginx.log"
    exit 0
  fi
fi

echo "unsupported sudo fixture command" >&2
exit 2
