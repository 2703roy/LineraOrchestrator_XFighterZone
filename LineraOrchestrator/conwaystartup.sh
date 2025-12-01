#!/bin/bash

echo "=== Linera Orchestrator Conway Testnet Publisher Setup ==="

# Tạo thư mục và set env
mkdir -p /build/linera-publisher
export LINERA_WALLET=/build/linera-publisher/wallet.json
export LINERA_KEYSTORE=/build/linera-publisher/keystore.json
export LINERA_STORAGE=rocksdb:/build/linera-publisher/client.db

echo "Conway Environment variables set"
echo "LINERA_WALLET=$LINERA_WALLET"
echo "LINERA_KEYSTORE=$LINERA_KEYSTORE"
echo "LINERA_STORAGE=$LINERA_STORAGE"

# KIỂM TRA ĐẦU TIÊN: ĐÃ CÓ PUBLISHER CHAIN CHƯA?
if [ -f "$LINERA_WALLET" ]; then
    echo "Wallet exists. Checking for default chain..."
    
    # Kiểm tra xem wallet file có default chain không (giống logic C#)
    if grep -q '"default"' "$LINERA_WALLET" 2>/dev/null; then
        CHAIN_ID=$(grep '"default"' "$LINERA_WALLET" | head -1 | sed 's/.*"default": *"\([^"]*\)".*/\1/')
        if [ ! -z "$CHAIN_ID" ]; then
            echo "Default chain found: $CHAIN_ID"
            echo "Publisher setup already completed. Skipping initialization."
            
            echo "=== Starting Application ==="
            # QUAN TRỌNG: Truyền environment variables sang dotnet process
            exec dotnet bin/Debug/net8.0/LineraOrchestrator.dll
            exit 0
        fi
    fi
    echo "Wallet exists but no default chain found. Need to setup chain..."
fi

# NẾU CHƯA CÓ CHAIN, THI SETUP
echo "🚀 Initializing wallet and chain..."

# INIT WALLET VỚI RETRY CHO SEGMENTATION FAULT
MAX_RETRIES=3
RETRY_COUNT=0

while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
    echo "Wallet init attempt $((RETRY_COUNT + 1)) of $MAX_RETRIES..."
    
    if linera wallet init --faucet https://faucet.testnet-conway.linera.net; then
        echo "Wallet initialized successfully!"
        break
    else
        EXIT_CODE=$?
        echo " Wallet init failed with exit code: $EXIT_CODE"
        
        if [ $EXIT_CODE -eq 139 ] || [ $EXIT_CODE -eq 255 ]; then
            echo "Segmentation fault detected, retrying after 2 seconds..."
            RETRY_COUNT=$((RETRY_COUNT + 1))
            sleep 2
        else
            echo "Other error during wallet init, stopping."
            exit 1
        fi
    fi
done

if [ $RETRY_COUNT -eq $MAX_RETRIES ]; then
    echo "❌ Failed to initialize wallet after $MAX_RETRIES attempts"
    exit 1
fi

# REQUEST CHAIN VỚI RETRY CHO SEGMENTATION FAULT
RETRY_COUNT=0

while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
    echo "Chain request attempt $((RETRY_COUNT + 1)) of $MAX_RETRIES..."
    
    if linera wallet request-chain --faucet https://faucet.testnet-conway.linera.net; then
        echo " Chain requested successfully!"
        break
    else
        EXIT_CODE=$?
        echo " Chain request failed with exit code: $EXIT_CODE"
        
        if [ $EXIT_CODE -eq 139 ] || [ $EXIT_CODE -eq 255 ]; then
            echo "Segmentation fault detected, retrying after 2 seconds..."
            RETRY_COUNT=$((RETRY_COUNT + 1))
            sleep 2
        else
            echo "Other error during chain request, stopping."
            exit 1
        fi
    fi
done

if [ $RETRY_COUNT -eq $MAX_RETRIES ]; then
    echo " Failed to request chain after $MAX_RETRIES attempts"
    exit 1
fi

# CHỜ WALLET FILE ĐƯỢC GHI ĐẦY ĐỦ
echo "Waiting for wallet file to be fully written..."
for i in {1..10}; do
    if [ -f "$LINERA_WALLET" ] && [ -s "$LINERA_WALLET" ] && grep -q '"default"' "$LINERA_WALLET" 2>/dev/null; then
        CHAIN_ID=$(grep '"default"' "$LINERA_WALLET" | head -1 | sed 's/.*"default": *"\([^"]*\)".*/\1/')
        if [ ! -z "$CHAIN_ID" ]; then
            echo "✅ Default chain confirmed: $CHAIN_ID"
            break
        fi
    fi
    sleep 0.5
done

echo "✅ Setup completed successfully!"

echo "=== Starting Application ==="
# QUAN TRỌNG: Dùng exec để thay thế process hiện tại, giữ nguyên environment variables
exec dotnet bin/Debug/net8.0/LineraOrchestrator.dll