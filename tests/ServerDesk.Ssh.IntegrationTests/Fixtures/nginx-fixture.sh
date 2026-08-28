#!/bin/sh
set -eu

if [ "$#" -lt 2 ] || [ "$1" != "nginx" ]; then
  echo "unexpected fixture invocation" >&2
  exit 64
fi
shift

case "$1" in
  -v)
    echo "nginx version: nginx/1.26.3" >&2
    exit 0
    ;;
  -T)
    cat <<'EOF'
# configuration file /etc/nginx/nginx.conf:
user www-data;
http { include /etc/nginx/sites-enabled/*; }
# configuration file /etc/nginx/sites-enabled/serverdesk.conf:
server {
    listen 80;
    server_name ssh.example.test;
    location / {
        proxy_pass http://fixture-user:fixture-secret@127.0.0.1:5050;
        proxy_set_header Host $host;
    }
}
EOF
    echo "nginx: configuration file /etc/nginx/nginx.conf test is successful" >&2
    exit 0
    ;;
  *)
    echo "unsupported nginx fixture argument: $1" >&2
    exit 64
    ;;
esac
