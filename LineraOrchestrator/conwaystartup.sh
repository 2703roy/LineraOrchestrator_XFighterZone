#!/bin/bash

echo "=== Linera Orchestrator Conway Testnet Setup ==="

# Tạo thư mục và set env
mkdir -p /root/.linera_testnet
export LINERA_WALLET=/root/.linera_testnet/wallet_0.json
export LINERA_KEYSTORE=/root/.linera_testnet/keystore_0.json
export LINERA_STORAGE=rocksdb:/root/.linera_testnet/client_0.db

echo "Conway Environment variables set"

# Kiểm tra wallet
if [ ! -f "$LINERA_WALLET" ]; then
    echo "Initializing new wallet..."
    linera wallet init --faucet https://faucet.testnet-conway.linera.net
    echo "Wallet initialized!"
else
    echo "Existing wallet found."
fi

# SỬA LOGIC RETRY - CHỈ RETRY KHI THẬT SỰ FAIL
echo "Requesting chain from faucet..."

MAX_RETRIES=3
RETRY_COUNT=0

for ((RETRY_COUNT=0; RETRY_COUNT<MAX_RETRIES; RETRY_COUNT++)); do
    echo "Attempt $((RETRY_COUNT + 1)) of $MAX_RETRIES..."
    
    # Request chain
    if linera wallet request-chain --faucet https://faucet.testnet-conway.linera.net; then
        echo "Chain requested successfully!"
        break  # THOÁT LUÔN KHI THÀNH CÔNG
    else
        EXIT_CODE=$?
        echo "Request failed with exit code: $EXIT_CODE"
        
        # Chỉ retry nếu là segmentation fault
        if [ $EXIT_CODE -eq 139 ] || [ $EXIT_CODE -eq 255 ]; then
            echo "Segmentation fault, retrying after 2 seconds..."
            sleep 2
        else
            echo "Other error, stopping."
            exit 1
        fi
    fi
done

if [ $RETRY_COUNT -eq $MAX_RETRIES ]; then
    echo "Failed after $MAX_RETRIES attempts"
    exit 1
fi

echo "=== Starting Application ==="
dotnet bin/Debug/net8.0/LineraOrchestrator.dll