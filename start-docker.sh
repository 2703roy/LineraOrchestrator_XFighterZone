#!/bin/bash
set -e

echo "Starting XFighterZone with Docker..."

export SERVER_IP=127.0.0.1

# Nếu đã có biến môi trường SERVER_IP thì giữ nguyên.
if [ -n "${SERVER_IP:-}" ]; then
    echo "SERVER_IP được đặt sẵn: $SERVER_IP"
else
    AUTO_IP=$(detect_local_ip)
    if [ -n "$AUTO_IP" ]; then
        export SERVER_IP="$AUTO_IP"
        echo "Tự động phát hiện SERVER_IP: $SERVER_IP"
    else
        # fallback mặc định nếu không detect được
        export SERVER_IP=192.168.1.51
        echo "Không thể tự động phát hiện IP. Dùng fallback SERVER_IP: $SERVER_IP"
        echo "Để override, chạy: export SERVER_IP=10.0.0.5 && ./this_script.sh"
    fi
fi


echo "Using SERVER_IP: $SERVER_IP"

# Tạo thư mục
echo "Creating directories..."
mkdir -p LineraOrchestrator/data
mkdir -p LineraOrchestrator/logs
mkdir -p Server/server_data

# Kiểm tra WASM files
echo "🔍 Checking WASM files..."
if [ -d "LineraOrchestrator/wasm" ] && [ "$(ls -A LineraOrchestrator/wasm/*.wasm 2>/dev/null)" ]; then
    echo "WASM files found:" 
    ls -la LineraOrchestrator/wasm/*.wasm
else
    echo "ERROR: No WASM files found in ./LineraOrchestrator/wasm/"
    exit 1
fi

# Dừng services cũ
echo "Stopping existing services..."
docker-compose down

# Build 
echo "Building Docker images..."
docker-compose build #--no-cache

# Bước 1: Chỉ chạy Linera-Orchestrator trước
echo "Starting Linera-Orchestrator first..."
docker-compose up -d linera-orchestrator

# Bước 2: Chờ Conway setup hoàn tất (dựa vào log)
echo "Waiting for Conway setup to complete..."
echo "This may take a while as it includes wallet initialization and chain request..."
sleep 30

# Bước 3: Start Linera node - SỬA ĐIỀU KIỆN KIỂM TRA
echo "Starting Linera node via API..."
MAX_RETRIES=3
RETRY_COUNT=0
SETUP_SUCCESS=false

while [ $RETRY_COUNT -lt $MAX_RETRIES ] && [ "$SETUP_SUCCESS" = false ]; do
    echo "API Attempt $((RETRY_COUNT + 1)) of $MAX_RETRIES..."
    
    if RESPONSE=$(curl -sS -X POST http://localhost:5290/linera/start-linera-node); then
        echo "API Response: $RESPONSE"
        
        # SỬA: Kiểm tra linh hoạt - cho phép có hoặc không có khoảng trắng
        if echo "$RESPONSE" | grep -q '"isReady": *true' && echo "$RESPONSE" | grep -q '"success": *true'; then
            echo "✅ Linera node setup completed successfully!"
            SETUP_SUCCESS=true
            break
        else
            echo "⏳ Linera node not ready yet, retrying in 15 seconds..."
            RETRY_COUNT=$((RETRY_COUNT + 1))
            sleep 15
        fi
    else
        echo "❌ Failed to connect to Linera-Orchestrator API, retrying in 15 seconds..."
        RETRY_COUNT=$((RETRY_COUNT + 1))
        sleep 15
    fi
done

if [ "$SETUP_SUCCESS" = false ]; then
    echo "❌ Failed to setup Linera node after $MAX_RETRIES attempts"
    echo "Checking Linera-Orchestrator logs:"
    docker-compose logs linera-orchestrator
    exit 1
fi

# Bước 4: Start Server Lobby
echo "Starting Server Lobby..."
docker-compose up -d serverlobby

# Kiểm tra final đơn giản
echo "Final check - waiting for services to stabilize..."
sleep 10

if docker-compose ps | grep -q "Up"; then
    echo "🎉 All services started successfully!"
    echo "📊 LineraOrchestrator: http://localhost:5290"
    echo "🎮 ServerLobby: UDP ${SERVER_IP}:1111"
    echo "🔢 Port range: 10000-10100"
    echo ""
    echo "You can check logs with: docker-compose logs -f"
else
    echo "❌ Some services may have issues, check logs: docker-compose logs"
    exit 1
fi