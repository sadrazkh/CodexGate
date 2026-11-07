#!/usr/bin/env bash
# CodexGate installer.
#
#   bash <(curl -fsSL https://raw.githubusercontent.com/sadrazkh/CodexGate/main/install.sh)
#
# Installs Docker if needed, builds CodexGate (with server sessions) and starts it, then prints a
# one-time link to the setup page, where you choose the admin, the domain and the certificate.
# It never touches ports or services that are already in use (x-ui, a web server, ...): busy ports
# are left alone and only free ones are taken. Run it again to update; your data is kept.
#
# Options:
#   --https-port N     server port for the panel (default 8443; 443 if you want and it is free)
#   --setup-port N     server port for the one-time setup page (default: a random free port)
#   --memory SIZE      memory cap for CodexGate, e.g. 16g (default: 60% of the server's memory)
#   --source DIR       build from this checkout instead of cloning from GitHub
#   --branch NAME      GitHub branch to install (default main)
#   --yes              do not ask questions (install Docker without asking)
#   --env K=V          extra setting for the container (repeatable)
#   --docker-arg ARG   extra docker run argument (repeatable)
set -euo pipefail

REPO=https://github.com/sadrazkh/CodexGate.git
BRANCH=main
SRC=/opt/codexgate
CONF_DIR=/etc/codexgate
CONF=$CONF_DIR/install.env
NAME=codexgate
IMAGE=codexgate-remote
HTTPS_PORT=""
SETUP_PORT=""
MEMORY=""
SOURCE=""
YES=0
EXTRA_ENV=()
EXTRA_DOCKER=()

say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mxx\033[0m %s\n' "$*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --https-port) HTTPS_PORT=$2; shift 2 ;;
    --setup-port) SETUP_PORT=$2; shift 2 ;;
    --memory) MEMORY=$2; shift 2 ;;
    --source) SOURCE=$2; shift 2 ;;
    --branch) BRANCH=$2; shift 2 ;;
    --yes|-y) YES=1; shift ;;
    --env) EXTRA_ENV+=("-e" "$2"); shift 2 ;;
    --docker-arg) EXTRA_DOCKER+=("$2"); shift 2 ;;
    -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done

[[ $EUID -eq 0 ]] || die "Run as root (sudo bash install.sh)."
command -v ss >/dev/null || die "The 'ss' command is missing (package iproute2)."

port_busy() { ss -Hltn "sport = :$1" 2>/dev/null | grep -q .; }
valid_port() { [[ "$1" =~ ^[0-9]+$ ]] && (( $1 >= 1 && $1 <= 65535 )); }

ask_yes() {
  [[ $YES -eq 1 ]] && return 0
  local reply
  read -r -p "$1 [Y/n] " reply </dev/tty || return 1
  [[ -z "$reply" || "$reply" =~ ^[Yy] ]]
}

# --- earlier install: keep its ports and token ----------------------------------------------
if [[ -f "$CONF" ]]; then
  # shellcheck disable=SC1090
  source "$CONF"
  HTTPS_PORT=${HTTPS_PORT:-$SAVED_HTTPS_PORT}
  SETUP_PORT=${SETUP_PORT:-$SAVED_SETUP_PORT}
  say "Updating the existing installation (ports $HTTPS_PORT/$SETUP_PORT kept)."
fi
EXISTING=0
if command -v docker >/dev/null && docker inspect "$NAME" >/dev/null 2>&1; then
  EXISTING=1
fi

# --- Docker ---------------------------------------------------------------------------------
if ! command -v docker >/dev/null; then
  ask_yes "Docker is not installed. Install it now (get.docker.com)?" || die "Docker is needed."
  curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker >/dev/null 2>&1 || true
docker info >/dev/null 2>&1 || die "Docker is installed but not running."

# --- ports: only free ones are taken --------------------------------------------------------
# Ports our own running container holds do not count as busy when updating.
if [[ $EXISTING -eq 1 ]]; then
  say "Stopping the running CodexGate for the update."
  docker stop "$NAME" >/dev/null 2>&1 || true
fi
HTTPS_PORT=${HTTPS_PORT:-8443}
valid_port "$HTTPS_PORT" || die "Invalid --https-port."
if port_busy "$HTTPS_PORT"; then
  [[ $EXISTING -eq 1 ]] && docker start "$NAME" >/dev/null 2>&1 || true
  die "Port $HTTPS_PORT is already in use. Choose another one with --https-port (nothing was changed)."
fi
if [[ -z "$SETUP_PORT" ]]; then
  for _ in $(seq 1 50); do
    candidate=$(( 20000 + RANDOM % 20000 ))
    if ! port_busy "$candidate" && [[ $candidate -ne $HTTPS_PORT ]]; then SETUP_PORT=$candidate; break; fi
  done
fi
valid_port "$SETUP_PORT" || die "Could not find a free port for the setup page; give one with --setup-port."
if port_busy "$SETUP_PORT"; then
  [[ $EXISTING -eq 1 ]] && docker start "$NAME" >/dev/null 2>&1 || true
  die "Port $SETUP_PORT is already in use; give another with --setup-port."
fi
HTTP80=false
PORT80=()
if ! port_busy 80; then
  HTTP80=true
  PORT80=(-p 80:8081)
else
  warn "Port 80 is in use by another program: it is left alone. Get the certificate through Cloudflare DNS in the setup page."
fi
if port_busy 443; then
  say "Port 443 is in use by another program (x-ui?): it is left alone; the panel uses $HTTPS_PORT."
fi

# --- source and image -----------------------------------------------------------------------
if [[ -n "$SOURCE" ]]; then
  SRC=$(cd "$SOURCE" && pwd)
else
  command -v git >/dev/null || { apt-get update -qq && apt-get install -y -qq git; } || die "git is needed."
  if [[ -d "$SRC/.git" ]]; then
    git -C "$SRC" fetch -q origin "$BRANCH" && git -C "$SRC" checkout -q "$BRANCH" && git -C "$SRC" reset -q --hard "origin/$BRANCH"
  else
    git clone -q --branch "$BRANCH" "$REPO" "$SRC"
  fi
fi
say "Building CodexGate (the first time takes a few minutes)."
docker build -t "$IMAGE" --target remote "$SRC" > /tmp/codexgate-build.log 2>&1 \
  || { tail -n 40 /tmp/codexgate-build.log >&2; die "The build failed; the end of its log is above (full log: /tmp/codexgate-build.log)."; }

# --- settings that survive updates ----------------------------------------------------------
mkdir -p "$CONF_DIR"
chmod 700 "$CONF_DIR"
if [[ -z "${SETUP_TOKEN:-}" ]]; then
  SETUP_TOKEN=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
fi
PUBLIC_IP=$(curl -fsS -4 --max-time 5 https://api.ipify.org 2>/dev/null || ip -4 route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if ($i=="src") print $(i+1)}' || true)
if [[ -z "$MEMORY" ]]; then
  total_mb=$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo)
  MEMORY="$(( total_mb * 60 / 100 ))m"
fi
umask 077
cat > "$CONF" <<EOF
SAVED_HTTPS_PORT=$HTTPS_PORT
SAVED_SETUP_PORT=$SETUP_PORT
SETUP_TOKEN=$SETUP_TOKEN
EOF

# --- firewall: open only our ports if ufw is on ---------------------------------------------
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  ufw allow "$HTTPS_PORT/tcp" >/dev/null
  ufw allow "$SETUP_PORT/tcp" >/dev/null
  [[ $HTTP80 == true ]] && ufw allow 80/tcp >/dev/null
  opened="$HTTPS_PORT, $SETUP_PORT"; [[ $HTTP80 == true ]] && opened="$opened, 80"
  say "ufw: opened $opened. After setup you can close the setup port: ufw delete allow $SETUP_PORT/tcp"
fi

# --- run --------------------------------------------------------------------------------------
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" --restart unless-stopped \
  -p "$HTTPS_PORT:8443" -p "$SETUP_PORT:8082" "${PORT80[@]}" \
  --memory "$MEMORY" \
  -v codexgate-data:/data -v codexgate-homes:/home \
  -e Gate__Managed__Enabled=true \
  -e "Gate__Managed__SetupToken=$SETUP_TOKEN" \
  -e "Gate__Managed__PublicHttpsPort=$HTTPS_PORT" \
  -e "Gate__Managed__HttpChallengeAvailable=$HTTP80" \
  -e "Gate__Managed__PublicIp=$PUBLIC_IP" \
  "${EXTRA_ENV[@]}" "${EXTRA_DOCKER[@]}" \
  "$IMAGE" >/dev/null

say "Waiting for CodexGate to start."
ready=0
for _ in $(seq 1 60); do
  if curl -fsS --max-time 2 "http://127.0.0.1:$SETUP_PORT/healthz" >/dev/null 2>&1 \
     || curl -fsSk --max-time 2 "https://127.0.0.1:$HTTPS_PORT/healthz" >/dev/null 2>&1; then
    ready=1; break
  fi
  sleep 2
done
[[ $ready -eq 1 ]] || { docker logs --tail 40 "$NAME" >&2; die "CodexGate did not start; its log is above."; }

if curl -fsS --max-time 2 "http://127.0.0.1:$SETUP_PORT/healthz" >/dev/null 2>&1; then
  echo
  say "Open this link to finish setting up (it works once; keep it private):"
  echo
  echo "    http://${PUBLIC_IP:-<server-ip>}:$SETUP_PORT/#$SETUP_TOKEN"
  echo
  echo "    The page asks for the admin, the domain and the certificate, then the panel moves to"
  echo "    https://<your-domain>:$HTTPS_PORT and the setup page closes."
else
  say "CodexGate is updated and running on port $HTTPS_PORT (HTTPS)."
fi
