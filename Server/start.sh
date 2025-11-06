#!/usr/bin/env bash
set -euo pipefail

WORKDIR="/opt/server"
LOBBY_BIN="${WORKDIR}/ServerLobby.x86_64"
BATTLE_BIN="${WORKDIR}/ServerBattle/ServerBattle.x86_64"

log(){ printf '%s %s\n' "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "$*"; }

# Safe clean CRLF if present
if command -v sed >/dev/null 2>&1; then
  sed -i 's/\r$//' "$0" || true
fi

cd "$WORKDIR" || { log "ERR cd $WORKDIR"; exit 1; }

# ensure exec bit
for b in "$LOBBY_BIN" "$BATTLE_BIN"; do
  if [ -f "$b" ] && [ ! -x "$b" ]; then
    chmod +x "$b" || log "WARN chmod failed $b"
    log "chmod +x $b"
  fi
done

if [ -x "$LOBBY_BIN" ]; then
  log "Starting ServerLobby"
  "$LOBBY_BIN" "$@" &
  child=$!
  trap 'log "Forward SIGTERM"; kill -TERM "$child" 2>/dev/null || true' SIGTERM SIGINT
  wait "$child"
  exit_code=$?
  log "ServerLobby exited $exit_code"
  exit "$exit_code"
else
  log "ERROR: ServerLobby missing or not executable"
  ls -la || true
  exit 2
fi