#!/usr/bin/env bash
set -euo pipefail

BUILD_DIR="${1:-./game/Build/Server}"
REMOTE="game"
REMOTE_DIR="/home/game/gameserver/build"

echo "==> stop server"
ssh "$REMOTE" 'sudo systemctl stop gameserver 2>/dev/null || true'

echo "==> clean remote build dir"
ssh "$REMOTE" "rm -rf $REMOTE_DIR && mkdir -p $REMOTE_DIR"

echo "==> scp build files"
scp -r "$BUILD_DIR"/* "$REMOTE:$REMOTE_DIR/"

echo "==> chmod + start"
ssh "$REMOTE" '
  chmod +x ~/gameserver/build/GameServer.x86_64
  sudo systemctl start gameserver
  sleep 3
  sudo systemctl is-active gameserver
'

echo "==> last 20 log lines"
ssh "$REMOTE" 'tail -n 20 ~/gameserver/logs/server.log 2>/dev/null || echo "(no log yet)"'
