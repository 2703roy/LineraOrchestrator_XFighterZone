#!/bin/bash

CONTAINER=linera-orchestrator
echo "=== Resetting Linera Orchestrator Data linera-publisher, linera-users & state inside container ==="

docker exec $CONTAINER rm -f /build/data/linera_orchestrator_state.json
docker exec $CONTAINER rm -rf /build/linera-publisher
docker exec $CONTAINER rm -rf /build/linera-users

docker exec $CONTAINER mkdir -p /build/linera-publisher
docker exec $CONTAINER mkdir -p /build/linera-users

echo "Restarting container..."
docker compose restart linera-orchestrator

echo "Waiting for API to become ready (port 5290)..."

until curl -s http://localhost:5290/health >/dev/null 2>&1; do
    sleep 1
done

echo "API ready. Starting Linera node..."

curl -sS -X POST http://localhost:5290/linera/start-linera-node | jq .
