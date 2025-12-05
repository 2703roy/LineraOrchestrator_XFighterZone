#!/usr/bin/env bash
set -euo pipefail
IFS=$'\n\t'

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

# CONFIG (override by env before running)
REGISTRY_PREFIX="${REGISTRY_PREFIX:-}"
TAG_LOCAL="${TAG_LOCAL:-local}"
PLATFORM="${PLATFORM:-}"   # auto-detect below if empty

# image local tags (no remote push)
LINERA_IMAGE="${REGISTRY_PREFIX:+${REGISTRY_PREFIX}/}linera-orchestrator:${TAG_LOCAL}"
SERVERLOBBY_IMAGE="${REGISTRY_PREFIX:+${REGISTRY_PREFIX}/}server-lobby:${TAG_LOCAL}"
SERVERTOURN_IMAGE="${REGISTRY_PREFIX:+${REGISTRY_PREFIX}/}servertournament:${TAG_LOCAL}"
WEBGL_IMAGE="${REGISTRY_PREFIX:+${REGISTRY_PREFIX}/}webgl_frontend:${TAG_LOCAL}"
ADMINWEBGL_IMAGE="${REGISTRY_PREFIX:+${REGISTRY_PREFIX}/}admin_webgl_frontend:${TAG_LOCAL}"

echo "Running local_dev_start.sh from: $ROOT_DIR"

# sanity checks
command -v docker >/dev/null 2>&1 || { echo "docker CLI not found"; exit 1; }
if ! docker info >/dev/null 2>&1; then
  echo "docker daemon not reachable. Start Docker Desktop / daemon and try again."; exit 1
fi

# buildx + binfmt setup (if not present)
if ! docker buildx version >/dev/null 2>&1; then
  echo "docker buildx not present; attempting to set up builder..."
  docker run --rm --privileged tonistiigi/binfmt --install all || true
  docker buildx create --name multi-builder --use >/dev/null 2>&1 || docker buildx create --use
  docker buildx inspect --bootstrap >/dev/null 2>&1 || true
fi

# ensure binfmt registered
docker run --rm --privileged tonistiigi/binfmt --install all >/dev/null 2>&1 || true

# platform auto-detect
if [ -z "${PLATFORM:-}" ]; then
  HOST_ARCH="$(uname -m || true)"
  case "$HOST_ARCH" in
    aarch64|arm64) PLATFORM="linux/arm64" ;;
    x86_64|amd64) PLATFORM="linux/amd64" ;;
    *) PLATFORM="linux/amd64" ;;
  esac
fi
echo "Using PLATFORM=$PLATFORM"

# build function: uses buildx --load to load into local docker (single-platform)
build_load() {
  local name="$1"; local context="$2"; local dockerfile="$3"; local tag="$4"
  echo
  echo "=== Building $name => $tag (context=$context, dockerfile=$dockerfile) ==="
  docker buildx build --progress=plain --platform "$PLATFORM" -t "$tag" -f "$context/$dockerfile" --load "$context"
}

# Build images (single-platform load)
build_load "linera-orchestrator" "./LineraOrchestrator" "Dockerfile" "$LINERA_IMAGE"
build_load "server-lobby" "./ServerLobby" "Dockerfile" "$SERVERLOBBY_IMAGE"
build_load "servertournament" "./ServerTournament" "Dockerfile" "$SERVERTOURN_IMAGE"
build_load "webgl_frontend" "./ClientFrontend" "Docker/Dockerfile" "$WEBGL_IMAGE"
build_load "admin_webgl_frontend" "./AdminTournamentFrontend" "Docker/Dockerfile" "$ADMINWEBGL_IMAGE"

echo
echo "=== Image build finished. Verifying image architectures ==="
for img in "$LINERA_IMAGE" "$SERVERLOBBY_IMAGE" "$SERVERTOURN_IMAGE" "$WEBGL_IMAGE" "$ADMINWEBGL_IMAGE"; do
  echo "Image: $img"
  docker image inspect --format '  -> {{.RepoTags}}  (OS={{.Os}} / Arch={{.Architecture}})' "$img" || echo "  (inspect failed)"
done

# quick sanity: check server-lobby binary inside container
echo
echo "Inspecting server-lobby image filesystem (brief):"
docker run --rm --entrypoint sh "$SERVERLOBBY_IMAGE" -c 'echo "uname -m: $(uname -m)"; file /opt/serverLobby/ServerLobby.x86_64 || ls -la /opt/serverLobby || true'

# Export env vars for start script / compose
export LINERA_IMAGE SERVERLOBBY_IMAGE SERVERTOURN_IMAGE WEBGL_IMAGE ADMINWEBGL_IMAGE
echo
echo "Exported images:"
echo "  LINERA_IMAGE=$LINERA_IMAGE"
echo "  SERVERLOBBY_IMAGE=$SERVERLOBBY_IMAGE"
echo "  SERVERTOURN_IMAGE=$SERVERTOURN_IMAGE"
echo "  WEBGL_IMAGE=$WEBGL_IMAGE"
echo "  ADMINWEBGL_IMAGE=$ADMINWEBGL_IMAGE"

# set SKIP_BUILD so start-docker.sh will not rebuild
export SKIP_BUILD=1

echo
echo "Ready to start services with start-docker.sh (SKIP_BUILD=1)."
echo "Running ./start-docker.sh ..."

# execute start-docker.sh (ensure executable)
chmod +x ./start-docker.sh
./start-docker.sh "$@"