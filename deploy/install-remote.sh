#!/usr/bin/env bash
# Prepares a Linux server for CodexGate server sessions when CodexGate runs directly on the host
# (the "remote" Docker image already contains all of this).
#
#   sudo deploy/install-remote.sh [service-user]      (default service user: codexgate)
#
# Installs the root helper, lets the service user (and only it) run that one helper through sudo,
# and checks that tmux and Codex are there. It never changes anything else.
set -euo pipefail

SERVICE_USER=${1:-codexgate}
HELPER=/usr/local/libexec/codexgate-host
SUDOERS=/etc/sudoers.d/codexgate
HERE=$(cd "$(dirname "$0")" && pwd)

[[ $EUID -eq 0 ]] || { echo "Run this with sudo." >&2; exit 1; }
[[ "$SERVICE_USER" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || { echo "Invalid user name." >&2; exit 1; }
id -u "$SERVICE_USER" >/dev/null 2>&1 || { echo "User $SERVICE_USER does not exist; create the service user first." >&2; exit 1; }

missing=()
command -v tmux >/dev/null || missing+=(tmux)
command -v sudo >/dev/null || missing+=(sudo)
command -v runuser >/dev/null || missing+=(util-linux)
if ((${#missing[@]})); then
  echo "Install first: ${missing[*]}   (Debian/Ubuntu: apt-get install ${missing[*]})" >&2
  exit 1
fi
tmux_version=$(tmux -V | sed -E 's/[^0-9.]//g')
if [[ "$(printf '%s\n3.2\n' "$tmux_version" | sort -V | head -n 1)" != "3.2" ]]; then
  echo "tmux 3.2 or newer is needed (found $tmux_version)." >&2
  exit 1
fi
command -v codex >/dev/null || echo "Note: Codex is not installed yet: npm install -g @openai/codex" >&2

install -o root -g root -m 0755 -D "$HERE/codexgate-host" "$HELPER"
tmp=$(mktemp)
printf '%s ALL=(root) NOPASSWD: %s\n' "$SERVICE_USER" "$HELPER" > "$tmp"
visudo -cf "$tmp" >/dev/null
install -o root -g root -m 0440 "$tmp" "$SUDOERS"
rm -f "$tmp"

echo "Done. Start CodexGate as $SERVICE_USER with Gate__Remote__Enabled=true"
echo "and Gate__Remote__GatewayUrl pointing at its own address (e.g. http://127.0.0.1:8080/v1)."
