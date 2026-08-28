#!/bin/sh
set -eu

SELF="$0"
CRON_STATE="${SELF}.cron"
TIMER_STATE="${SELF}.timer"

if [ ! -f "$CRON_STATE" ]; then
  printf '%s\n' '0 1 * * * /srv/old-job' > "$CRON_STATE"
fi
if [ ! -f "$TIMER_STATE" ]; then
  printf '%s\n' 'disabled' > "$TIMER_STATE"
fi

exe="${1:-}"
shift || true

case "$exe" in
  crontab)
    case "${1:-}" in
      -l)
        cat "$CRON_STATE"
        ;;
      -T)
        candidate="${2:-}"
        [ -f "$candidate" ] || { echo 'candidate not found' >&2; exit 2; }
        if grep -q 'BROKEN_CRON' "$candidate"; then
          echo 'errors in crontab file, cannot install' >&2
          exit 1
        fi
        ;;
      '')
        echo 'missing crontab argument' >&2
        exit 2
        ;;
      *)
        candidate="$1"
        [ -f "$candidate" ] || { echo 'candidate not found' >&2; exit 2; }
        cp "$candidate" "$CRON_STATE"
        ;;
    esac
    ;;
  systemctl)
    verb="${1:-}"
    case "$verb" in
      list-unit-files)
        state="$(cat "$TIMER_STATE")"
        printf 'serverdesk-demo.timer %s enabled\n' "$state"
        ;;
      show)
        unit="${2:-}"
        state="$(cat "$TIMER_STATE")"
        active='inactive'
        [ "$state" = 'enabled' ] && active='active'
        if printf '%s\n' "$*" | grep -q -- '--property=LoadState'; then
          printf 'LoadState=loaded\n'
          exit 0
        fi
        cat <<EOF
Id=$unit
LoadState=loaded
ActiveState=$active
UnitFileState=$state
NextElapseUSecRealtime=Sat 2026-08-29 07:00:00 UTC
LastTriggerUSec=Fri 2026-08-28 07:00:00 UTC
TimersCalendar={ OnCalendar=*-*-* 07:00:00 }
Triggers=serverdesk-demo.service
FragmentPath=/usr/lib/systemd/system/serverdesk-demo.timer
EOF
        ;;
      enable)
        [ "${2:-}" = '--now' ] || exit 9
        printf '%s\n' 'enabled' > "$TIMER_STATE"
        ;;
      disable)
        [ "${2:-}" = '--now' ] || exit 9
        printf '%s\n' 'disabled' > "$TIMER_STATE"
        ;;
      cat)
        cat <<'EOF'
[Unit]
Description=ServerDesk fixture timer
[Timer]
OnCalendar=*-*-* 07:00:00
[Install]
WantedBy=timers.target
EOF
        ;;
      daemon-reload)
        ;;
      *)
        echo "unsupported systemctl fixture verb: $verb" >&2
        exit 9
        ;;
    esac
    ;;
  journalctl)
    printf '%s\n' 'Aug 28 07:00:00 fixture service started' 'Aug 28 07:00:01 fixture service completed'
    ;;
  *)
    echo "unsupported fixture executable: $exe" >&2
    exit 127
    ;;
esac
