#!/usr/bin/env bash
set -euo pipefail
IFS=$'\n\t'

# ---------------- compose detection + wrappers ----------------
COMPOSE_CMD=""
detect_compose() {
  # Try "docker compose" plugin first (preferred)
  if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    COMPOSE_CMD="docker compose"
    return 0
  fi
  # Fallback to legacy docker-compose
  if command -v docker-compose >/dev/null 2>&1 && docker-compose version >/dev/null 2>&1; then
    COMPOSE_CMD="docker-compose"
    return 0
  fi
  return 1
}

if ! detect_compose; then
  cat >&2 <<'ERR'
ERROR: neither 'docker compose' (plugin) nor 'docker-compose' (legacy) are available.
Install Docker Compose plugin or legacy docker-compose.

On Ubuntu/WSL you can install plugin:
  sudo apt-get update && sudo apt-get install -y docker-compose-plugin

Or legacy:
  sudo apt-get install -y docker-compose
ERR
  exit 1
fi

# wrapper call: careful to invoke command with/without space
compose_run() {
  if [ "$COMPOSE_CMD" = "docker compose" ]; then
    docker compose "$@"
  else
    docker-compose "$@"
  fi
}
compose_build() {
  PLATFORM=${PLATFORM:-} compose_run build "$@"
}
compose_up_detached() {
  PLATFORM=${PLATFORM:-} compose_run up -d --remove-orphans "$@"
}
compose_down() {
  PLATFORM=${PLATFORM:-} compose_run down "$@"
}

# ---------------- other helpers ----------------
normalize_to_lf() {
    local f="$1"
    if [ -f "$f" ] && grep -q $'\r' "$f" 2>/dev/null; then
        sed -i 's/\r$//' "$f" || true
    fi
}

# ---------------- host/platform detection ----------------
HOST_ARCH="$(uname -m || true)"
echo "Detected host architecture: $HOST_ARCH"

export PLATFORM=${PLATFORM:-}
echo "Using PLATFORM=${PLATFORM:-<auto>}"
echo "Using PLATFORM=$PLATFORM"

# report buildx if available
if command -v docker >/dev/null 2>&1 && docker buildx version >/dev/null 2>&1; then
  echo "Docker buildx available."
fi

# ---------------- normalize this script and tournament helper ----------------
# re-exec if script has CRLF
if command -v sed >/dev/null 2>&1 && grep -q $'\r' "$0" 2>/dev/null; then
    sed -i 's/\r$//' "$0" || true
    exec bash "$0" "$@"
fi

normalize_to_lf start-tournament.sh

# ---------- Registry prefetch + auto-fix ----------
ensure_registry_access() {
  local MAX_TRIES=3
  local DOCKER_CFG="$HOME/.docker/config.json"
  local -a IMAGES=()

  # collect FROM images from Dockerfiles in repo root (simple heuristic)
  while IFS= read -r line; do
    img=$(printf '%s' "$line" | awk '{print $2}')
    [ -n "$img" ] && IMAGES+=("$img")
  done < <(grep -hR --line-number -E '^FROM[[:space:]]+' --exclude-dir=.git --binary-files=without-match . 2>/dev/null || true)

  # collect image: entries from docker-compose*.yml
  while IFS= read -r line; do
    img=$(echo "$line" | sed -E 's/^[[:space:]]*image:[[:space:]]*//')
    [ -n "$img" ] && IMAGES+=("$img")
  done < <(grep -hR -E '^[[:space:]]*image:[[:space:]]*' docker-compose*.yml 2>/dev/null || true)

  # always include common expected bases to be safe
  IMAGES+=("ubuntu:24.04" "nginx:stable-alpine")

  # dedupe preserving order
  IFS=$'\n' read -r -d '' -a IMAGES <<< "$(printf "%s\n" "${IMAGES[@]}" | awk '!seen[$0]++')" || true

  pull_with_fix() {
    local image="$1"
    local tries=1
    while [ $tries -le $MAX_TRIES ]; do
      echo "Pull attempt $tries for $image"
      if docker pull "$image" >/dev/null 2>&1; then
        echo "Pulled $image"
        return 0
      fi
      out=$(docker pull "$image" 2>&1 || true)
      if printf '%s' "$out" | grep -qi "error getting credentials"; then
        echo "Credential helper failure detected for $image."
        if [ -f "$DOCKER_CFG" ]; then
          bak="$DOCKER_CFG.bak.$(date +%s)"
          cp "$DOCKER_CFG" "$bak"
          echo "Backed up docker config to $bak"
          if command -v jq >/dev/null 2>&1; then
            jq 'del(.credsStore, .credHelpers)' "$bak" > "$DOCKER_CFG.tmp" && mv "$DOCKER_CFG.tmp" "$DOCKER_CFG"
          else
            # fallback python edit
            python3 - <<PY >/dev/null 2>&1 || true
import json,os
p=os.path.expanduser("$DOCKER_CFG")
b=p+".bak"
j=json.load(open(b))
j.pop('credsStore',None); j.pop('credHelpers',None)
open(p,'w').write(json.dumps(j))
PY
          fi
          echo "Temporarily removed credsStore/credHelpers from $DOCKER_CFG."
          # retry immediate
          if docker pull "$image" >/dev/null 2>&1; then
            echo "Pull succeeded for $image after fixing config."
            return 0
          else
            echo "Pull still failing after config tweak; will retry loop."
          fi
        else
          echo "No $DOCKER_CFG found to fix."
        fi
      else
        echo "Pull failed for $image: $out"
      fi
      tries=$((tries+1))
      sleep 2
    done
    return 1
  }

  for img in "${IMAGES[@]}"; do
    [ -z "$img" ] && continue
    echo "Prefetching base image: $img"
    if ! pull_with_fix "$img"; then
      echo "Warning: failed to prefetch $img (will continue, build may still fail)."
    fi
  done

  return 0
}
# ---------- end registry prefetch ----------

# ============================ Common setup ============================
echo "=== Common setup ==="
echo "Creating directories..."
mkdir -p LineraOrchestrator/data LineraOrchestrator/logs LineraOrchestrator/linera_testnet
rm -rf ServerLobby/server_data 2>/dev/null || true
mkdir -p ServerLobby/server_data

# Dừng services cũ (không fail nếu chưa chạy)
echo "Stopping existing services (compose down)..."
compose_down || true

# Registry pre-check + prefetch images and fix credential helper issues if detected
echo "=== Registry pre-check: ensure base image available / creds ok ==="
ensure_registry_access || {
  echo "Registry prefetch reported issues; attempting docker-compose pull --ignore-pull-failures"
  compose_run pull --ignore-pull-failures || true
}

# Build images with retries (single build invocation)
echo "Building Docker images (compose build with retries)..."
BUILD_RETRIES=3
i=1
until [ $i -gt $BUILD_RETRIES ]; do
  echo "Compose build attempt $i/$BUILD_RETRIES..."
  if compose_build; then
    echo "Compose build succeeded."
    break
  fi
  echo "Compose build failed on attempt $i; retrying..."
  i=$((i+1))
  sleep 2
done
if [ $i -gt $BUILD_RETRIES ]; then
  echo "ERROR: compose build failed after $BUILD_RETRIES attempts."
  exit 1
fi

# ============================ LineraOrch ==============================
start_linera_orch() {
    echo "=== LineraOrch: starting linera-orchestrator ==="

    PLATFORM=$PLATFORM compose_up_detached linera-orchestrator

    echo "Waiting for Linera-Orchestrator API to become ready..."
    until curl -fsS http://localhost:5290/health >/dev/null 2>&1; do
        echo "API not ready yet, waiting 2 seconds..."
        sleep 2
    done
    echo "API ready!"

    echo "Starting Linera node via Orchestrator API..."
    local MAX_RETRIES=3
    local RETRY_COUNT=0
    local SETUP_SUCCESS=false

    while [ "$RETRY_COUNT" -lt "$MAX_RETRIES" ] && [ "$SETUP_SUCCESS" = false ]; do
        printf 'API Attempt %d of %d...\n' $((RETRY_COUNT + 1)) "$MAX_RETRIES"
        if RESPONSE=$(curl -fsS -X POST http://localhost:5290/linera/start-linera-node 2>/dev/null); then
            printf 'API Response: %s\n' "$RESPONSE"
            if printf '%s' "$RESPONSE" | grep -E -q '"isReady":[[:space:]]*true' && \
               printf '%s' "$RESPONSE" | grep -E -q '"success":[[:space:]]*true'; then
                printf '%s\n' "Linera node setup completed successfully!"
                SETUP_SUCCESS=true
                break
            else
                printf '%s\n' "Linera node not ready yet, retrying in 15 seconds..."
                RETRY_COUNT=$((RETRY_COUNT + 1))
                sleep 15
            fi
        else
            printf '%s\n' "Failed to connect to Linera-Orchestrator API, retrying in 15 seconds..."
            RETRY_COUNT=$((RETRY_COUNT + 1))
            sleep 15
        fi
    done

    if [ "$SETUP_SUCCESS" = false ]; then
        echo "Failed to setup Linera node after $MAX_RETRIES attempts"
        echo "===== Linera-Orchestrator logs ====="
        PLATFORM=$PLATFORM compose_run logs linera-orchestrator || true
        return 1
    fi

    echo "LineraOrch finished successfully."
    return 0
}

# ============================ ServerLobby ============================
start_server_lobby() {
    echo "=== ServerLobby: starting server lobby ==="
    PLATFORM=$PLATFORM compose_up_detached serverlobby
    echo "Final check - waiting for services to stabilize..."
    sleep 5

    # Kiểm tra service serverlobby có chạy không
    if compose_run ps --services --filter "status=running" | grep -qE '^serverlobby$'; then
        echo "ServerLobby is running."
        # report container arch for verification
        CID=$(compose_run ps -q serverlobby || true)
        if [ -n "$CID" ]; then
          echo "serverlobby container id: $CID"
          docker exec "$CID" uname -m || true
        fi
        return 0
    else
        echo "Some services may have issues, check logs: compose logs"
        return 1
    fi
}

# ============================ Tournament Setup ============================
setup_tournament() {
    echo "=== Setting up tournament script ==="
    if [ -f start-tournament.sh ]; then
        echo "start-tournament.sh already exists — skipping overwrite"
        return 0
    fi

    cat > start-tournament.sh <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
IFS=$'\n\t'

echo "Starting server tournament..."
docker-compose up -d servertournament

echo "Waiting for servertournament to be fully started..."
while true; do
    if docker-compose ps --services --filter "status=running" | grep -qE '^servertournament$'; then
        sleep 5
        echo "servertournament is now running and ready"
        break
    else
        echo "Waiting for servertournament to start..."
        sleep 2
    fi
done

echo "Starting admin webgl frontend..."
docker-compose up -d admin_webgl_frontend
EOF

    chmod +x start-tournament.sh
    echo "Tournament script created and made executable"
}

# ============================ Main flow ==============================
if start_linera_orch; then
    if start_server_lobby; then
        echo "ALL DONE: LineraOrchestrator + ServerLobby started."
        echo "XFighterZone Docker setup completed successfully!"
        PLATFORM=$PLATFORM compose_up_detached webgl_frontend
        setup_tournament
        exit 0
    else
        echo "ERROR: ServerLobby failed to start correctly."
        PLATFORM=$PLATFORM compose_run logs serverlobby || true
        exit 1
    fi
else
    echo "ERROR: LineraOrch setup failed; aborting ServerLobby startup."
    exit 1
fi
