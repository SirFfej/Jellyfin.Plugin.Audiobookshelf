#!/usr/bin/env bash
# test-connection.sh
# Verifies that Audiobookshelf and Jellyfin are reachable, the ABS API token is
# valid, and the plugin is correctly installed — with actionable fix guidance for
# every failure.
#
# Usage:
#   chmod +x test-connection.sh
#   ./test-connection.sh                              # interactive — prompts for tokens
#   ABS_TOKEN=abc123 ./test-connection.sh             # non-interactive ABS token
#   ./test-connection.sh --abs-url http://localhost:13378 --token abc123
#   ./test-connection.sh --abs-url http://… --token abc123 --jf-url http://… --jf-token xyz

set -euo pipefail

# ── colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'; YELLOW='\033[1;33m'; GREEN='\033[0;32m'; CYAN='\033[0;36m'
BOLD='\033[1m'; NC='\033[0m'
info()    { echo -e "${CYAN}[info]${NC}  $*"; }
warn()    { echo -e "${YELLOW}[warn]${NC}  $*"; }
ok()      { echo -e "${GREEN}[ ok ]${NC}  $*"; }
fail()    { echo -e "${RED}[FAIL]${NC}  $*"; }
hint()    { echo -e "        ${YELLOW}↳${NC} $*"; }
die()     { echo -e "${RED}[fail]${NC}  $*" >&2; exit 1; }
section() { echo -e "\n${CYAN}── $* ${NC}"; }

PASS=0; FAIL=0
SUGGESTIONS=()   # collected per-failure fix guidance, printed at end

pass()    { ok "$1";  PASS=$((PASS+1)); }
flunk() {
  # Usage: flunk "what failed" "fix suggestion 1" ["fix suggestion 2" ...]
  local msg="$1"; shift
  fail "$msg"
  FAIL=$((FAIL+1))
  for s in "$@"; do
    hint "$s"
    SUGGESTIONS+=("$s")
  done
}

# ── argument parsing ──────────────────────────────────────────────────────────
OPT_ABS_URL=""
OPT_TOKEN="${ABS_TOKEN:-}"
OPT_JF_URL=""
OPT_JF_TOKEN="${JF_TOKEN:-}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --abs-url)   OPT_ABS_URL="$2";   shift 2 ;;
    --jf-url)    OPT_JF_URL="$2";    shift 2 ;;
    --token|-t)  OPT_TOKEN="$2";     shift 2 ;;
    --jf-token)  OPT_JF_TOKEN="$2";  shift 2 ;;
    *) die "Unknown argument: $1" ;;
  esac
done

# ── sanity checks ─────────────────────────────────────────────────────────────
command -v docker >/dev/null 2>&1 || die "docker is not installed or not on PATH"
command -v curl   >/dev/null 2>&1 || die "curl is not installed or not on PATH"
docker info >/dev/null 2>&1       || die "Docker daemon is not running (or no permission — try: sudo ./test-connection.sh)"

# ── 1. Locate containers ──────────────────────────────────────────────────────
section "Containers"
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
  local cid="$1" cport="$2"
  local host_port
  host_port=$(docker inspect --format \
    "{{(index (index .NetworkSettings.Ports \"${cport}/tcp\") 0).HostPort}}" \
    "$cid" 2>/dev/null || true)
  if [[ -n "$host_port" ]]; then
    echo "http://localhost:${host_port}"
  fi
}

# ABS URL (internal port 80)
if [[ -z "$OPT_ABS_URL" && -n "$ABS_ID" ]]; then
  OPT_ABS_URL=$(host_url_for "$ABS_ID" "80")
fi
if [[ -z "$OPT_ABS_URL" ]]; then
  read -rp "Enter ABS base URL [http://localhost:13378]: " OPT_ABS_URL
  OPT_ABS_URL="${OPT_ABS_URL:-http://localhost:13378}"
fi
ABS_URL="${OPT_ABS_URL%/}"
info "ABS URL   : ${ABS_URL}"

# Jellyfin URL (internal port 8096)
if [[ -z "$OPT_JF_URL" && -n "$JF_ID" ]]; then
  OPT_JF_URL=$(host_url_for "$JF_ID" "8096")
fi
if [[ -z "$OPT_JF_URL" ]]; then
  read -rp "Enter Jellyfin base URL [http://localhost:8096]: " OPT_JF_URL
  OPT_JF_URL="${OPT_JF_URL:-http://localhost:8096}"
fi
JF_URL="${OPT_JF_URL%/}"
info "JF URL    : ${JF_URL}"

# Extract host+port pieces for inter-container tests
ABS_HOST_PORT="${ABS_URL#http://}"   # e.g. localhost:13378
JF_HOST_PORT="${JF_URL#http://}"     # e.g. localhost:8096
ABS_PORT="${ABS_HOST_PORT##*:}"
JF_PORT="${JF_HOST_PORT##*:}"

# ── 3. Tokens ─────────────────────────────────────────────────────────────────
if [[ -z "$OPT_TOKEN" ]]; then
  echo ""
  info "An ABS API token is required for authenticated checks."
  info "Find yours at: ABS → Settings → Users → (your user) → API Token"
  read -rsp "  ABS API token: " OPT_TOKEN
  echo ""
fi
ABS_TOKEN_VAL="$OPT_TOKEN"

if [[ -z "$OPT_JF_TOKEN" ]]; then
  echo ""
  info "A Jellyfin API key enables plugin-installed check (optional)."
  info "Generate one at: Jellyfin → Dashboard → API Keys → +"
  read -rsp "  Jellyfin API key (press Enter to skip): " OPT_JF_TOKEN
  echo ""
fi
JF_TOKEN_VAL="$OPT_JF_TOKEN"

# ── helper: HTTP GET ──────────────────────────────────────────────────────────
# Returns body on 2xx; empty string + return 1 on error.
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
section "Audiobookshelf"

# 4a. Ping (no auth)
info "GET ${ABS_URL}/ping"
if body=$(http_get "${ABS_URL}/ping"); then
  pass "ABS reachable — /ping → ${body}"
else
  flunk "ABS /ping failed — server not reachable at ${ABS_URL}" \
    "Verify ABS is running: docker ps | grep -i audiobookshelf" \
    "Check the URL and port — default is http://localhost:13378" \
    "If using a custom port: ./test-connection.sh --abs-url http://localhost:YOUR_PORT"
fi

# 4b. Authenticated: GET /api/me
abs_is_admin=false
abs_username=""
info "GET ${ABS_URL}/api/me  (token auth)"
if body=$(http_get "${ABS_URL}/api/me" "Authorization: Bearer ${ABS_TOKEN_VAL}"); then
  abs_username=$(echo "$body" | grep -oP '"username"\s*:\s*"\K[^"]+' | head -1 || true)
  abs_type=$(echo "$body" | grep -oP '"type"\s*:\s*"\K[^"]+' | head -1 || true)
  if [[ -n "$abs_username" ]]; then
    pass "Token valid — ABS user: ${abs_username} (${abs_type})"
    [[ "$abs_type" == "admin" || "$abs_type" == "root" ]] && abs_is_admin=true
    if [[ "$abs_is_admin" == false ]]; then
      warn "Token belongs to a non-admin user ('${abs_type}')"
      hint "User Discovery in the plugin requires an admin token"
      hint "Generate an admin token: ABS → Settings → Users → (admin user) → API Token"
    fi
  else
    flunk "GET /api/me succeeded but response did not contain a username — unexpected API response"
  fi
else
  flunk "GET /api/me failed — token invalid or ABS not reachable" \
    "Generate a fresh token: ABS → Settings → Users → (your user) → API Token" \
    "Ensure there are no leading/trailing spaces in the token"
fi

# 4c. Libraries
book_lib_count=0
first_book_lib=""
info "GET ${ABS_URL}/api/libraries"
if body=$(http_get "${ABS_URL}/api/libraries" "Authorization: Bearer ${ABS_TOKEN_VAL}"); then
  lib_count=$({ echo "$body" | grep -oP '"id"\s*:\s*"[^"]+' || true; } | wc -l | tr -d ' ')
  pass "Libraries accessible — ${lib_count} librar$([ "$lib_count" -eq 1 ] && echo y || echo ies) found"

  # Count book libraries specifically
  # Each library object has a "mediaType" field; we need book, not podcast
  book_lib_count=$({ echo "$body" | grep -oP '"mediaType"\s*:\s*"book"' || true; } | wc -l | tr -d ' ')
  first_book_lib=$(echo "$body" | grep -oP '"id"\s*:\s*"\K[^"]+' | head -1 || true)

  if [[ "$book_lib_count" -eq 0 ]]; then
    flunk "No book libraries found — only podcast libraries will be ignored by the plugin" \
      "Create a book library in ABS: ABS → Libraries → + Add Library → select 'Book'" \
      "Podcast libraries are intentionally filtered out by the plugin"
  else
    pass "Book librar$([ "$book_lib_count" -eq 1 ] && echo y || echo ies): ${book_lib_count} found (plugin will use these)"
  fi
else
  flunk "GET /api/libraries failed" \
    "Check token has sufficient permissions" \
    "Confirm ABS is fully started (not still loading libraries)"
fi

# 4d. Cover image (no auth)
if [[ -n "$first_book_lib" ]]; then
  items_body=$(http_get "${ABS_URL}/api/libraries/${first_book_lib}/items?limit=1" \
    "Authorization: Bearer ${ABS_TOKEN_VAL}" || true)
  first_item=$(echo "$items_body" | grep -oP '"id"\s*:\s*"\K[^"]+' | head -1 || true)
  if [[ -n "$first_item" ]]; then
    info "GET ${ABS_URL}/api/items/${first_item}/cover  (no auth — public endpoint)"
    cover_code=$(curl -s -o /dev/null -w '%{http_code}' \
      --max-time 10 "${ABS_URL}/api/items/${first_item}/cover" || true)
    if [[ "$cover_code" =~ ^2 ]]; then
      pass "Cover image endpoint public — HTTP ${cover_code} (no auth required)"
    elif [[ "$cover_code" == "404" ]]; then
      warn "Cover image returned 404 — item may have no cover art (not a plugin issue)"
    else
      flunk "Cover image endpoint returned HTTP ${cover_code} — plugin cover art will fail" \
        "ABS cover images should be accessible without authentication by default" \
        "Check ABS Settings → Security for any IP allowlist or auth restrictions"
    fi
  else
    warn "No items in first library — skipping cover image check"
  fi
fi

# ── 5. Jellyfin checks ────────────────────────────────────────────────────────
section "Jellyfin"
jf_version=""

# 5a. Public system info (no auth)
info "GET ${JF_URL}/System/Info/Public"
if body=$(http_get "${JF_URL}/System/Info/Public"); then
  jf_version=$(echo "$body" | grep -oP '"Version"\s*:\s*"\K[^"]+' | head -1 || true)
  jf_server=$(echo "$body" | grep -oP '"ServerName"\s*:\s*"\K[^"]+' | head -1 || true)
  pass "Jellyfin reachable — \"${jf_server}\" v${jf_version}"

  # 5b. Version compatibility check (requires 10.10.7+)
  jf_major=$(echo "$jf_version" | cut -d. -f1)
  jf_minor=$(echo "$jf_version" | cut -d. -f2)
  jf_patch=$(echo "$jf_version" | cut -d. -f3)
  if [[ "$jf_major" -gt 10 ]] || \
     [[ "$jf_major" -eq 10 && "$jf_minor" -gt 10 ]] || \
     [[ "$jf_major" -eq 10 && "$jf_minor" -eq 10 && "${jf_patch:-0}" -ge 7 ]]; then
    pass "Jellyfin version ${jf_version} meets minimum requirement (10.10.7)"
  else
    flunk "Jellyfin ${jf_version} is below the required minimum (10.10.7)" \
      "Update Jellyfin: https://jellyfin.org/downloads" \
      "If using Docker: docker pull jellyfin/jellyfin:latest && restart container"
  fi
else
  flunk "Jellyfin not reachable at ${JF_URL}" \
    "Check container is running: docker ps | grep jellyfin" \
    "Check that port 8096 is exposed: docker inspect jellyfin | grep -A5 Ports" \
    "Try a different URL: ./test-connection.sh --jf-url http://localhost:YOUR_PORT"
fi

# 5c. Plugin installed check (requires JF API key)
if [[ -n "$JF_TOKEN_VAL" ]]; then
  info "GET ${JF_URL}/Plugins  (checking plugin is installed)"
  if plugins_body=$(http_get "${JF_URL}/Plugins" "Authorization: MediaBrowser Token=\"${JF_TOKEN_VAL}\""); then
    if echo "$plugins_body" | grep -qi "audiobookshelf"; then
      plugin_ver=$(echo "$plugins_body" | grep -oP '(?i)"Version"\s*:\s*"\K[^"]+' | head -1 || true)
      pass "Audiobookshelf plugin installed (v${plugin_ver:-unknown})"
    else
      flunk "Audiobookshelf plugin not found in Jellyfin's plugin list" \
        "Copy the DLL to Jellyfin's plugins directory:" \
        "  cp Jellyfin.Plugin.Audiobookshelf.dll /path/to/jellyfin/plugins/" \
        "Then restart Jellyfin and check Dashboard → Plugins"
    fi
  else
    warn "Could not retrieve plugin list — API key may be invalid or Jellyfin not ready"
    hint "Generate an API key: Jellyfin → Dashboard → API Keys → +"
  fi
else
  info "Jellyfin API key not provided — skipping plugin-installed check"
  hint "Re-run with --jf-token YOUR_KEY to verify plugin installation"
fi

# ── 6. Inter-container network ────────────────────────────────────────────────
section "Inter-container network"

container_http_get() {
  local cid="$1" url="$2"
  if docker exec "$cid" sh -c "curl -sf --max-time 5 '${url}' -o /dev/null" 2>/dev/null; then
    return 0
  elif docker exec "$cid" sh -c "wget -q --timeout=5 '${url}' -O /dev/null" 2>/dev/null; then
    return 0
  fi
  return 1
}

if [[ -n "$ABS_ID" && -n "$JF_ID" ]]; then
  abs_net=$(docker inspect --format '{{.HostConfig.NetworkMode}}' "$ABS_ID" 2>/dev/null || true)
  jf_net=$(docker inspect  --format '{{.HostConfig.NetworkMode}}' "$JF_ID"  2>/dev/null || true)

  if [[ "$abs_net" == "host" || "$jf_net" == "host" ]]; then
    info "One or both containers use host networking — testing via localhost"

    if container_http_get "$ABS_ID" "http://localhost:${JF_PORT}/System/Info/Public"; then
      pass "ABS container → Jellyfin (localhost:${JF_PORT})"
    else
      flunk "ABS container cannot reach Jellyfin at localhost:${JF_PORT}" \
        "Both containers share the host network stack — check Jellyfin is listening on port ${JF_PORT}" \
        "ss -tlnp | grep ${JF_PORT}"
    fi

    if container_http_get "$JF_ID" "http://localhost:${ABS_PORT}/ping"; then
      pass "Jellyfin container → ABS (localhost:${ABS_PORT})"
    else
      flunk "Jellyfin container cannot reach ABS at localhost:${ABS_PORT}" \
        "Check ABS is listening on port ${ABS_PORT}: ss -tlnp | grep ${ABS_PORT}"
    fi
  else
    # User-defined network — test by container name
    info "ABS → Jellyfin via container name DNS"
    if container_http_get "$ABS_ID" "http://${JF_NAME}:8096/System/Info/Public"; then
      pass "ABS can reach Jellyfin (http://${JF_NAME}:8096)"
    else
      flunk "ABS cannot reach Jellyfin at http://${JF_NAME}:8096" \
        "Both containers must be on the same Docker network" \
        "List shared networks: docker inspect ${ABS_NAME} ${JF_NAME} --format '{{.Name}}: {{keys .NetworkSettings.Networks}}'" \
        "Connect to shared network: docker network connect NETWORK_NAME ${ABS_NAME}" \
        "Or add both to a shared network in docker-compose.yml"
    fi

    info "Jellyfin → ABS via container name DNS"
    if container_http_get "$JF_ID" "http://${ABS_NAME}:80/ping"; then
      pass "Jellyfin can reach ABS (http://${ABS_NAME}:80)"
    else
      flunk "Jellyfin cannot reach ABS at http://${ABS_NAME}:80" \
        "Connect to shared network: docker network connect NETWORK_NAME ${JF_NAME}" \
        "Inspect networks: docker network ls && docker network inspect NETWORK_NAME"
    fi
  fi
else
  warn "Skipping inter-container checks (one or both containers not found)"
fi

# ── 7. Plugin configuration guidance ─────────────────────────────────────────
section "Plugin setup checklist"
info "After verifying connectivity, configure the plugin:"
echo "  1. Jellyfin → Dashboard → Plugins → Audiobookshelf → Settings"
echo "  2. Server URL  : ${ABS_URL}"
if [[ -n "$abs_username" ]]; then
  echo "  3. Admin Token : your ABS token for user '${abs_username}'"
else
  echo "  3. Admin Token : ABS → Settings → Users → (admin user) → API Token"
fi
echo "  4. Click 'Test Connection'"
echo "  5. Click 'Discover Users' to auto-match Jellyfin ↔ ABS accounts"
echo "  6. Enable inbound/outbound sync as desired"
echo "  7. Click 'Save'"
echo "  8. Run a metadata refresh on your audiobook library"

# ── 8. Summary ────────────────────────────────────────────────────────────────
echo ""
echo -e "${BOLD}$([ $FAIL -eq 0 ] && echo "${GREEN}" || echo "${RED}")━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
if [[ $FAIL -eq 0 ]]; then
  echo -e "${GREEN}${BOLD}  All checks passed  (${PASS} passed, 0 failed)${NC}"
  echo -e "${GREEN}  Your environment looks ready for the plugin.${NC}"
else
  echo -e "${RED}${BOLD}  ${FAIL} check(s) failed  (${PASS} passed, ${FAIL} failed)${NC}"
  echo ""
  echo -e "${YELLOW}  Fix suggestions:${NC}"
  seen=()
  for s in "${SUGGESTIONS[@]}"; do
    # Deduplicate
    already=false
    for prev in "${seen[@]:-}"; do [[ "$prev" == "$s" ]] && already=true && break; done
    if [[ "$already" == false ]]; then
      echo -e "    ${YELLOW}•${NC} $s"
      seen+=("$s")
    fi
  done
fi
echo -e "${BOLD}$([ $FAIL -eq 0 ] && echo "${GREEN}" || echo "${RED}")━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""

if [[ $FAIL -gt 0 ]]; then
  exit 1
fi
