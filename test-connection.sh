#!/usr/bin/env bash
# test-connection.sh
# Verifies that Audiobookshelf and Jellyfin are reachable and that the ABS API
# token is valid before loading the plugin.
#
# Usage:
#   chmod +x test-connection.sh
#   ./test-connection.sh                        # interactive — prompts for token
#   ABS_TOKEN=abc123 ./test-connection.sh       # non-interactive
#   ./test-connection.sh --abs-url http://localhost:13378 --token abc123

set -euo pipefail

# ── colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'; YELLOW='\033[1;33m'; GREEN='\033[0;32m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[info]${NC}  $*"; }
warn()  { echo -e "${YELLOW}[warn]${NC}  $*"; }
ok()    { echo -e "${GREEN}[ ok ]${NC}  $*"; }
fail()  { echo -e "${RED}[FAIL]${NC}  $*"; }
die()   { echo -e "${RED}[fail]${NC}  $*" >&2; exit 1; }

PASS=0
FAIL=0
pass() { ok "$1";  PASS=$((PASS+1)); }
flunk() { fail "$1"; FAIL=$((FAIL+1)); }

# ── argument parsing ──────────────────────────────────────────────────────────
OPT_ABS_URL=""
OPT_TOKEN="${ABS_TOKEN:-}"
OPT_JF_URL=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --abs-url)  OPT_ABS_URL="$2";  shift 2 ;;
    --jf-url)   OPT_JF_URL="$2";   shift 2 ;;
    --token|-t) OPT_TOKEN="$2";    shift 2 ;;
    *) die "Unknown argument: $1" ;;
  esac
done

# ── sanity checks ─────────────────────────────────────────────────────────────
command -v docker >/dev/null 2>&1 || die "docker is not installed or not on PATH"
command -v curl   >/dev/null 2>&1 || die "curl is not installed or not on PATH"
docker info >/dev/null 2>&1       || die "Docker daemon is not running (or no permission — try sudo)"

# ── 1. Locate containers ──────────────────────────────────────────────────────
echo ""
info "Scanning Docker for Jellyfin and Audiobookshelf containers..."

find_container() {
  local pattern="$1"
  docker ps --format '{{.ID}} {{.Image}} {{.Names}}' \
    | grep -iE "$pattern" \
    | awk '{print $1}' \
    | head -1
}

JF_ID=$(find_container 'jellyfin')
ABS_ID=$(find_container 'audiobookshelf|advplyr')

if [[ -z "$JF_ID" ]]; then
  warn "Jellyfin container not found (not running?). Jellyfin checks will be skipped."
else
  JF_NAME=$(docker inspect --format '{{.Name}}' "$JF_ID" | tr -d '/')
  JF_STATUS=$(docker inspect --format '{{.State.Status}}' "$JF_ID")
  ok "Jellyfin  : ${JF_NAME} (${JF_STATUS})"
fi

if [[ -z "$ABS_ID" ]]; then
  warn "Audiobookshelf container not found (not running?). ABS checks will be skipped."
else
  ABS_NAME=$(docker inspect --format '{{.Name}}' "$ABS_ID" | tr -d '/')
  ABS_STATUS=$(docker inspect --format '{{.State.Status}}' "$ABS_ID")
  ok "ABS       : ${ABS_NAME} (${ABS_STATUS})"
fi

# ── 2. Resolve URLs from exposed ports ────────────────────────────────────────
host_url_for() {
  # Given a container ID and container port, return http://localhost:HOST_PORT
  local cid="$1" cport="$2"
  local host_port
  host_port=$(docker inspect --format \
    "{{(index (index .NetworkSettings.Ports \"${cport}/tcp\") 0).HostPort}}" \
    "$cid" 2>/dev/null || true)
  if [[ -n "$host_port" ]]; then
    echo "http://localhost:${host_port}"
  fi
}

# ABS URL
if [[ -z "$OPT_ABS_URL" && -n "$ABS_ID" ]]; then
  OPT_ABS_URL=$(host_url_for "$ABS_ID" "80")
fi
if [[ -z "$OPT_ABS_URL" ]]; then
  read -rp "Enter ABS base URL [http://localhost:13378]: " OPT_ABS_URL
  OPT_ABS_URL="${OPT_ABS_URL:-http://localhost:13378}"
fi
ABS_URL="${OPT_ABS_URL%/}"
info "ABS URL   : ${ABS_URL}"

# Jellyfin URL
if [[ -z "$OPT_JF_URL" && -n "$JF_ID" ]]; then
  OPT_JF_URL=$(host_url_for "$JF_ID" "8096")
fi
if [[ -z "$OPT_JF_URL" ]]; then
  read -rp "Enter Jellyfin base URL [http://localhost:8096]: " OPT_JF_URL
  OPT_JF_URL="${OPT_JF_URL:-http://localhost:8096}"
fi
JF_URL="${OPT_JF_URL%/}"
info "JF URL    : ${JF_URL}"

# ── 3. ABS token ──────────────────────────────────────────────────────────────
if [[ -z "$OPT_TOKEN" ]]; then
  echo ""
  info "An ABS API token is required for authenticated checks."
  info "Find yours at: ABS → Settings → Users → (your user) → API Token"
  read -rsp "  ABS API token: " OPT_TOKEN
  echo ""
fi
ABS_TOKEN="$OPT_TOKEN"

# ── helper: HTTP GET ──────────────────────────────────────────────────────────
# Returns body on success; prints error and returns empty string on failure.
http_get() {
  local url="$1" auth_header="${2:-}"
  local curl_args=(-s -S --max-time 10 -w '\n%{http_code}')
  [[ -n "$auth_header" ]] && curl_args+=(-H "$auth_header")

  local raw
  raw=$(curl "${curl_args[@]}" "$url" 2>&1) || true

  local body http_code
  http_code=$(echo "$raw" | tail -1)
  body=$(echo "$raw" | sed '$d')

  if [[ "$http_code" =~ ^2 ]]; then
    echo "$body"
    return 0
  else
    echo ""
    return 1
  fi
}

# ── 4. ABS checks ─────────────────────────────────────────────────────────────
echo ""
echo -e "${CYAN}── Audiobookshelf ───────────────────────────────────────────${NC}"

# 4a. Ping (no auth)
info "GET ${ABS_URL}/ping"
if body=$(http_get "${ABS_URL}/ping"); then
  pass "ABS reachable — /ping → ${body}"
else
  flunk "ABS /ping failed — server not reachable at ${ABS_URL}"
fi

# 4b. Authenticated: GET /api/me
info "GET ${ABS_URL}/api/me  (token auth)"
if body=$(http_get "${ABS_URL}/api/me" "Authorization: Bearer ${ABS_TOKEN}"); then
  # Extract username from JSON without requiring jq
  abs_username=$(echo "$body" | grep -oP '"username"\s*:\s*"\K[^"]+' | head -1 || true)
  abs_type=$(echo "$body" | grep -oP '"type"\s*:\s*"\K[^"]+' | head -1 || true)
  if [[ -n "$abs_username" ]]; then
    pass "Token valid — ABS user: ${abs_username} (${abs_type})"
  else
    flunk "GET /api/me succeeded but response did not contain a username"
  fi
else
  flunk "GET /api/me failed — token may be invalid or ABS not running"
fi

# 4c. Libraries
info "GET ${ABS_URL}/api/libraries"
if body=$(http_get "${ABS_URL}/api/libraries" "Authorization: Bearer ${ABS_TOKEN}"); then
  lib_count=$({ echo "$body" | grep -oP '"id"\s*:\s*"[^"]+' || true; } | wc -l | tr -d ' ')
  pass "Libraries accessible — ${lib_count} librar$([ "$lib_count" -eq 1 ] && echo y || echo ies) found"
else
  flunk "GET /api/libraries failed"
fi

# 4d. Cover image (no auth) — fetch first item's cover if we found libraries
if [[ "$FAIL" -eq 0 ]]; then
  first_lib=$(echo "$body" | grep -oP '"id"\s*:\s*"\K[^"]+' | head -1 || true)
  if [[ -n "$first_lib" ]]; then
    items_body=$(http_get "${ABS_URL}/api/libraries/${first_lib}/items?limit=1" \
      "Authorization: Bearer ${ABS_TOKEN}" || true)
    first_item=$(echo "$items_body" | grep -oP '"id"\s*:\s*"\K[^"]+' | head -1 || true)
    if [[ -n "$first_item" ]]; then
      info "GET ${ABS_URL}/api/items/${first_item}/cover  (no auth — public)"
      cover_code=$(curl -s -o /dev/null -w '%{http_code}' \
        --max-time 10 "${ABS_URL}/api/items/${first_item}/cover" || true)
      if [[ "$cover_code" =~ ^2 ]]; then
        pass "Cover image endpoint reachable without auth (HTTP ${cover_code})"
      else
        flunk "Cover image endpoint returned HTTP ${cover_code} — expected 2xx"
      fi
    fi
  fi
fi

# ── 5. Jellyfin checks ────────────────────────────────────────────────────────
echo ""
echo -e "${CYAN}── Jellyfin ─────────────────────────────────────────────────${NC}"

# 5a. Public system info (no auth)
info "GET ${JF_URL}/System/Info/Public"
if body=$(http_get "${JF_URL}/System/Info/Public"); then
  jf_version=$(echo "$body" | grep -oP '"Version"\s*:\s*"\K[^"]+' | head -1 || true)
  jf_server=$(echo "$body" | grep -oP '"ServerName"\s*:\s*"\K[^"]+' | head -1 || true)
  pass "Jellyfin reachable — \"${jf_server}\" v${jf_version}"
else
  flunk "Jellyfin /System/Info/Public not reachable at ${JF_URL}"
fi

# ── 6. Inter-container network checks ─────────────────────────────────────────
echo ""
echo -e "${CYAN}── Inter-container network ──────────────────────────────────${NC}"

# Helper: run a fire-and-forget HTTP GET inside a container.
# Tries curl first, falls back to wget (many minimal images only have one).
container_http_get() {
  local cid="$1" url="$2"
  if docker exec "$cid" sh -c "curl -sf --max-time 5 '$url' -o /dev/null" 2>/dev/null; then
    return 0
  elif docker exec "$cid" sh -c "wget -q --timeout=5 '$url' -O /dev/null" 2>/dev/null; then
    return 0
  fi
  return 1
}

if [[ -n "$ABS_ID" && -n "$JF_ID" ]]; then
  # Detect whether either container is using host networking.
  abs_net=$(docker inspect --format '{{.HostConfig.NetworkMode}}' "$ABS_ID" 2>/dev/null || true)
  jf_net=$(docker inspect  --format '{{.HostConfig.NetworkMode}}' "$JF_ID"  2>/dev/null || true)

  if [[ "$abs_net" == "host" || "$jf_net" == "host" ]]; then
    # host-network containers share the host's network stack — container-name
    # DNS is not used; both services are reachable via localhost on their ports.
    info "One or both containers use host networking — testing via localhost instead of container names"

    if container_http_get "$ABS_ID" "http://localhost:8096/System/Info/Public"; then
      pass "ABS container can reach Jellyfin at http://localhost:8096"
    else
      flunk "ABS container cannot reach Jellyfin at http://localhost:8096"
    fi

    if container_http_get "$JF_ID" "http://localhost:80/ping"; then
      pass "Jellyfin container can reach ABS at http://localhost:80"
    else
      flunk "Jellyfin container cannot reach ABS at http://localhost:80"
    fi
  else
    # User-defined network — test by container name (Docker DNS)
    info "ABS → Jellyfin  (docker exec from ABS container)"
    if container_http_get "$ABS_ID" "http://${JF_NAME}:8096/System/Info/Public"; then
      pass "ABS can reach Jellyfin at http://${JF_NAME}:8096"
    else
      flunk "ABS cannot reach Jellyfin at http://${JF_NAME}:8096"
      warn "  → Check that both containers are on the same Docker network"
      warn "  → docker network inspect <network> | grep -A2 'Containers'"
    fi

    info "Jellyfin → ABS  (docker exec from Jellyfin container)"
    if container_http_get "$JF_ID" "http://${ABS_NAME}:80/ping"; then
      pass "Jellyfin can reach ABS at http://${ABS_NAME}:80"
    else
      flunk "Jellyfin cannot reach ABS at http://${ABS_NAME}:80"
      warn "  → docker network connect <network> ${JF_NAME}"
    fi
  fi
else
  warn "Skipping inter-container checks (one or both containers not found)"
fi

# ── 7. Summary ────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
if [[ $FAIL -eq 0 ]]; then
  echo -e "${GREEN}  All checks passed  (${PASS} passed, 0 failed)${NC}"
else
  echo -e "${RED}  ${FAIL} check(s) failed  (${PASS} passed, ${FAIL} failed)${NC}"
fi
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""

if [[ $FAIL -gt 0 ]]; then
  exit 1
fi
