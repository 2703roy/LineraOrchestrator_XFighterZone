#!/usr/bin/env bash
set -euo pipefail
IFS=$'\n\t'

# Normalize line endings of this script (and re-exec under bash if needed).
if command -v sed >/dev/null 2>&1 && grep -q $'\r' "$0" 2>/dev/null; then
    sed -i 's/\r$//' "$0" || true
    exec bash "$0" "$@"
fi

normalize_to_lf() {
    local f="$1"
    if [ -f "$f" ] && grep -q $'\r' "$f" 2>/dev/null; then
        sed -i 's/\r$//' "$f" || true
    fi
}

normalize_to_lf start-tournament.sh

# ============================ Common setup ============================
echo "=== Common setup ==="
echo "Creating directories..."
mkdir -p LineraOrchestrator/data LineraOrchestrator/logs LineraOrchestrator/linera_testnet
rm -rf ServerLobby/server_data 2>/dev/null || true
mkdir -p ServerLobby/server_data

# Kiểm tra WASM files
echo "Checking WASM files..."
if [ -d "LineraOrchestrator/wasm" ] && compgen -G "LineraOrchestrator/wasm/*.wasm" >/dev/null; then
    echo "WASM files found:"
    ls -la LineraOrchestrator/wasm/*.wasm
else
    echo "ERROR: No WASM files found in ./LineraOrchestrator/wasm/"
    exit 1
fi

# Dừng services cũ (không fail nếu chưa chạy)
echo "Stopping existing services (docker-compose down)..."
docker-compose down || true

echo "Building Docker images..."
docker-compose build

# ============================ LineraOrch ==============================
start_linera_orch() {
    echo "=== LineraOrch: starting linera-orchestrator ==="

    docker-compose up -d linera-orchestrator

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
        docker-compose logs linera-orchestrator || true
        return 1
    fi

    echo "LineraOrch finished successfully."
    return 0
}

# ============================ ServerLobby ============================
start_server_lobby() {
    echo "=== ServerLobby: starting server lobby ==="
    docker-compose up -d serverlobby
    echo "Final check - waiting for services to stabilize..."
    sleep 5

    # Kiểm tra service serverlobby có chạy không
    if docker-compose ps --services --filter "status=running" | grep -qE '^serverlobby$'; then
        echo "ServerLobby is running."
        return 0
    else
        echo "Some services may have issues, check logs: docker-compose logs"
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
        docker-compose up -d webgl_frontend
        setup_tournament
        exit 0
    else
        echo "ERROR: ServerLobby failed to start correctly."
        exit 1
    fi
else
    echo "ERROR: LineraOrch setup failed; aborting ServerLobby startup."
    exit 1
fi
