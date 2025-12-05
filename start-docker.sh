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

# ============================ Common setup ============================
echo "=== Common setup ==="
echo "Creating directories..."
mkdir -p LineraOrchestrator/data LineraOrchestrator/logs LineraOrchestrator/linera_testnet
rm -rf ServerLobby/server_data 2>/dev/null || true
mkdir -p ServerLobby/server_data

# Dừng services cũ (không fail nếu chưa chạy)
echo "Stopping existing services (compose down)..."
compose_down || true

echo "Building Docker images..."
compose_build

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
    rm -f start-tournament.sh

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
