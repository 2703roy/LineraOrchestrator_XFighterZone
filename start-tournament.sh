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
