#!/bin/sh
set -eu

while [ "$#" -gt 0 ] && [ "$1" != "--" ]; do
  shift
done
[ "$#" -gt 0 ] || exit 64
shift

shell_path=${1:?missing shell}
helper_path=${2:?missing helper}
candidate_path=${3:?missing candidate}
target_path=${4:?missing target}
nginx_path=${5:?missing nginx}

# The production service uses a private mount namespace and root-owned helper.
# This disposable CI fixture validates the staged candidate directly so the
# OpenSSH test never needs mount privileges on the runner.
[ -x "$shell_path" ] || exit 64
[ -f "$helper_path" ] || exit 64
[ -n "$target_path" ] || exit 64
[ -x "$nginx_path" ] || exit 64

if grep -q 'INVALID_CANDIDATE' "$candidate_path"; then
  echo 'nginx: staged candidate rejected' >&2
  exit 1
fi

exit 0
