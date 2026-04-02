#!/usr/bin/env bash
# setup-abs.sh
# Inspects the local Jellyfin Docker setup and generates a docker-compose.yml
# for Audiobookshelf that shares the same media directories.
#
# Usage:
#   chmod +x setup-abs.sh
#   ./setup-abs.sh
#   # Review generated docker-compose.abs.yml, then:
#   docker compose -f docker-compose.abs.yml up -d

set -euo pipefail

# ── colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'; YELLOW='\033[1;33m'; GREEN='\033[0;32m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[info]${NC}  $*"; }
warn()  { echo -e "${YELLOW}[warn]${NC}  $*"; }
ok()    { echo -e "${GREEN}[ ok ]${NC}  $*"; }
die()   { echo -e "${RED}[fail]${NC}  $*" >&2; exit 1; }

# ── sanity checks ─────────────────────────────────────────────────────────────
command -v docker >/dev/null 2>&1 || die "docker is not installed or not on PATH"
docker info >/dev/null 2>&1       || die "Docker daemon is not running (or no permission — try sudo)"

OUTPUT_FILE="docker-compose.abs.yml"

# ── 1. Locate the Jellyfin container ─────────────────────────────────────────
info "Searching for Jellyfin container..."

JELLYFIN_ID=$(docker ps -a --format '{{.ID}} {{.Image}} {{.Names}}' \
  | grep -iE 'jellyfin' \
  | awk '{print $1}' \
  | head -1)

if [[ -z "$JELLYFIN_ID" ]]; then
  warn "No Jellyfin container found."
  warn "Will generate a template ABS compose file — fill in media paths manually."
  JELLYFIN_FOUND=false
else
  JELLYFIN_NAME=$(docker inspect --format '{{.Name}}' "$JELLYFIN_ID" | tr -d '/')
  JELLYFIN_IMAGE=$(docker inspect --format '{{.Config.Image}}' "$JELLYFIN_ID")
  JELLYFIN_STATUS=$(docker inspect --format '{{.State.Status}}' "$JELLYFIN_ID")
  ok "Found Jellyfin container: ${JELLYFIN_NAME} (${JELLYFIN_IMAGE}) — ${JELLYFIN_STATUS}"
  JELLYFIN_FOUND=true
fi

# ── 2. Extract Jellyfin media volume mounts ───────────────────────────────────
AUDIOBOOK_PATHS=()
OTHER_MEDIA_PATHS=()
JELLYFIN_NETWORK=""
JELLYFIN_UID=""
JELLYFIN_GID=""

if [[ "$JELLYFIN_FOUND" == "true" ]]; then
  info "Inspecting Jellyfin container mounts..."

  # Pull every bind-mount (type=bind) and volume (type=volume)
  while IFS= read -r mount_line; do
    [[ -z "$mount_line" ]] && continue
    host_path=$(echo "$mount_line" | cut -d: -f1)
    container_path=$(echo "$mount_line" | cut -d: -f2)

    # Skip Jellyfin config/cache/log directories — ABS doesn't need those
    if echo "$container_path" | grep -qiE '(config|cache|log|transcode|metadata)'; then
      info "  Skipping Jellyfin config mount: $host_path → $container_path"
      continue
    fi

    # Classify by container path or directory name keywords
    if echo "$host_path $container_path" | grep -qiE '(audiobook|audio.?book|abs)'; then
      AUDIOBOOK_PATHS+=("$host_path")
      ok "  Audiobook dir: $host_path"
    else
      OTHER_MEDIA_PATHS+=("$host_path")
      info "  Other media dir: $host_path (will be mounted read-only for browsing)"
    fi
  done < <(docker inspect --format \
    '{{range .HostConfig.Binds}}{{.}}{{"\n"}}{{end}}' "$JELLYFIN_ID")

  # Also check Mounts (covers named volumes and newer compose mounts)
  while IFS= read -r mount_json; do
    [[ -z "$mount_json" ]] && continue
    src=$(echo "$mount_json" | grep -oP '"Source"\s*:\s*"\K[^"]+' || true)
    dst=$(echo "$mount_json" | grep -oP '"Destination"\s*:\s*"\K[^"]+' || true)
    [[ -z "$src" || -z "$dst" ]] && continue
    # skip if already captured via Binds
    if echo "$dst" | grep -qiE '(config|cache|log|transcode)'; then continue; fi
    if echo "${AUDIOBOOK_PATHS[*]-} ${OTHER_MEDIA_PATHS[*]-}" | grep -q "$src"; then continue; fi
    if echo "$src $dst" | grep -qiE '(audiobook|audio.?book|abs)'; then
      AUDIOBOOK_PATHS+=("$src")
      ok "  Audiobook dir (mount): $src"
    elif [[ "$src" == /* ]]; then
      OTHER_MEDIA_PATHS+=("$src")
      info "  Other media dir (mount): $src"
    fi
  done < <(docker inspect --format \
    '{{json .Mounts}}' "$JELLYFIN_ID" | python3 -c \
    "import sys,json; [print(json.dumps(m)) for m in json.load(sys.stdin)]" 2>/dev/null || true)

  # Docker network
  JELLYFIN_NETWORK=$(docker inspect --format \
    '{{range $k,$v := .NetworkSettings.Networks}}{{$k}}{{end}}' \
    "$JELLYFIN_ID" | head -1)
  info "Jellyfin network: ${JELLYFIN_NETWORK:-bridge}"

  # User mapping
  JELLYFIN_UID=$(docker inspect --format '{{.Config.User}}' "$JELLYFIN_ID")
  if [[ -z "$JELLYFIN_UID" ]]; then
    JELLYFIN_UID=$(id -u)
    JELLYFIN_GID=$(id -g)
    info "No explicit user in Jellyfin container — using current user ${JELLYFIN_UID}:${JELLYFIN_GID}"
  else
    JELLYFIN_GID=$(echo "$JELLYFIN_UID" | cut -d: -f2)
    JELLYFIN_UID=$(echo "$JELLYFIN_UID" | cut -d: -f1)
    info "Jellyfin runs as ${JELLYFIN_UID}:${JELLYFIN_GID}"
  fi
fi

# ── 3. Prompt for any missing paths ──────────────────────────────────────────
if [[ ${#AUDIOBOOK_PATHS[@]} -eq 0 ]]; then
  warn "No audiobook directory detected from Jellyfin."
  read -rp "  Enter absolute path to your audiobooks directory: " user_ab_path
  [[ -n "$user_ab_path" ]] && AUDIOBOOK_PATHS=("$user_ab_path")
fi

# ABS config/metadata directories — default to sibling of first audiobook path
DEFAULT_BASE=$(dirname "${AUDIOBOOK_PATHS[0]:-/srv/media}")
read -rp "ABS config directory [${DEFAULT_BASE}/abs-config]: " ABS_CONFIG
ABS_CONFIG="${ABS_CONFIG:-${DEFAULT_BASE}/abs-config}"

read -rp "ABS metadata directory [${DEFAULT_BASE}/abs-metadata]: " ABS_METADATA
ABS_METADATA="${ABS_METADATA:-${DEFAULT_BASE}/abs-metadata}"

read -rp "ABS host port [13378]: " ABS_PORT
ABS_PORT="${ABS_PORT:-13378}"

# ── 4. Confirm and create directories ────────────────────────────────────────
echo ""
info "Creating ABS data directories if they don't exist..."
mkdir -p "$ABS_CONFIG" "$ABS_METADATA"
ok "  $ABS_CONFIG"
ok "  $ABS_METADATA"

# ── 5. Build volume block ─────────────────────────────────────────────────────
build_volumes() {
  local indent="      "
  # Config and metadata first
  echo "${indent}- ${ABS_CONFIG}:/config"
  echo "${indent}- ${ABS_METADATA}:/metadata"

  # Audiobook directories — full read/write so ABS can write its own metadata
  local idx=0
  for p in "${AUDIOBOOK_PATHS[@]}"; do
    if [[ $idx -eq 0 ]]; then
      echo "${indent}- ${p}:/audiobooks"
    else
      echo "${indent}- ${p}:/audiobooks${idx}"
    fi
    idx=$((idx+1))
  done

  # Other media directories from Jellyfin — read-only
  for p in "${OTHER_MEDIA_PATHS[@]}"; do
    local dirname
    dirname=$(basename "$p" | tr '[:upper:]' '[:lower:]' | tr ' ' '_')
    echo "${indent}- ${p}:/media/${dirname}:ro"
  done
}

# ── 6. Build network block ────────────────────────────────────────────────────
build_networks_service() {
  if [[ -n "${JELLYFIN_NETWORK:-}" && "$JELLYFIN_NETWORK" != "bridge" ]]; then
    echo "    networks:"
    echo "      - ${JELLYFIN_NETWORK}"
  fi
}

build_networks_top() {
  if [[ -n "${JELLYFIN_NETWORK:-}" && "$JELLYFIN_NETWORK" != "bridge" ]]; then
    echo ""
    echo "networks:"
    echo "  ${JELLYFIN_NETWORK}:"
    echo "    external: true"
  fi
}

# ── 7. Write docker-compose.abs.yml ──────────────────────────────────────────
info "Writing ${OUTPUT_FILE}..."

cat > "$OUTPUT_FILE" <<EOF
# Generated by setup-abs.sh on $(date -u +"%Y-%m-%dT%H:%M:%SZ")
# Jellyfin container detected: ${JELLYFIN_NAME:-none}
#
# Start:   docker compose -f ${OUTPUT_FILE} up -d
# Logs:    docker compose -f ${OUTPUT_FILE} logs -f
# Stop:    docker compose -f ${OUTPUT_FILE} down

services:
  audiobookshelf:
    image: ghcr.io/advplyr/audiobookshelf:latest
    container_name: audiobookshelf
    ports:
      - "${ABS_PORT}:80"
    volumes:
$(build_volumes)
    environment:
      - AUDIOBOOKSHELF_UID=${JELLYFIN_UID:-1000}
      - AUDIOBOOKSHELF_GID=${JELLYFIN_GID:-1000}
    restart: unless-stopped
$(build_networks_service)
$(build_networks_top)
EOF

ok "Written: ${OUTPUT_FILE}"
echo ""

# ── 8. Summary ───────────────────────────────────────────────────────────────
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${GREEN}  Audiobookshelf compose file ready${NC}"
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""
echo "  Config dir  : $ABS_CONFIG"
echo "  Metadata dir: $ABS_METADATA"
echo "  Port        : $ABS_PORT → container:80"
if [[ ${#AUDIOBOOK_PATHS[@]} -gt 0 ]]; then
  echo "  Audiobooks  : ${AUDIOBOOK_PATHS[*]}"
fi
if [[ ${#OTHER_MEDIA_PATHS[@]} -gt 0 ]]; then
  echo "  Other media : ${OTHER_MEDIA_PATHS[*]} (read-only)"
fi
echo ""
echo "  Review the file, then run:"
echo -e "  ${CYAN}docker compose -f ${OUTPUT_FILE} up -d${NC}"
echo ""
if [[ -n "${JELLYFIN_NETWORK:-}" && "$JELLYFIN_NETWORK" != "bridge" ]]; then
  echo -e "  ${YELLOW}Note:${NC} ABS is joined to the '${JELLYFIN_NETWORK}' network so it can"
  echo "  reach Jellyfin by container name for future API integration."
  echo ""
fi
