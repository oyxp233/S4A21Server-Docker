#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"

usage() {
  cat <<'EOF'
Usage:
  ./start-server.sh [--server-ip <ip|auto>] [extra server args...]
  SERVER_IP=<ip> ./start-server.sh

  --server-ip   IP address sent to the game client (login/channel packets).
                Use "auto" to pick this machine's LAN IPv4.
                Default: 127.0.0.1

VM tip: run the server on the host with the host's LAN IP, e.g.
  ./start-server.sh --server-ip auto
  ./start-server.sh --server-ip 192.168.1.10
EOF
}

detect_lan_ip() {
  local ip=""

  if [[ "$(uname -s)" == "Darwin" ]]; then
    for iface in en0 en1 bridge0; do
      ip="$(ipconfig getifaddr "$iface" 2>/dev/null || true)"
      [[ -n "$ip" ]] && { echo "$ip"; return 0; }
    done
  else
    ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
    [[ -n "$ip" ]] && { echo "$ip"; return 0; }
  fi

  return 1
}

detect_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"

  case "$os" in
    Darwin)
      case "$arch" in
        arm64) echo "osx-arm64" ;;
        x86_64) echo "osx-x64" ;;
        *) return 1 ;;
      esac
      ;;
    Linux)
      case "$arch" in
        x86_64) echo "linux-x64" ;;
        aarch64|arm64) echo "linux-arm64" ;;
        *) return 1 ;;
      esac
      ;;
    *)
      return 1
      ;;
  esac
}

pick_server() {
  local rid="$1"
  local local_binary="$ROOT/DfoServer"
  local published="$ROOT/dist/$rid/DfoServer"
  local dev_dll="$ROOT/Server/DfoServer/bin/Debug/DfoServer.dll"

  if [[ -x "$local_binary" ]]; then
    echo "local|$local_binary"
    return 0
  fi

  if [[ -x "$published" ]]; then
    echo "local|$published"
    return 0
  fi

  if [[ -f "$dev_dll" ]] && command -v dotnet >/dev/null 2>&1; then
    echo "dotnet|$dev_dll"
    return 0
  fi

  return 1
}

SERVER_IP="${SERVER_IP:-}"
SERVER_ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server-ip)
      [[ $# -ge 2 ]] || { echo "--server-ip requires a value" >&2; usage; exit 1; }
      SERVER_IP="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      SERVER_ARGS+=("$1")
      shift
      ;;
  esac
done

if [[ -z "$SERVER_IP" ]]; then
  SERVER_IP="127.0.0.1"
elif [[ "$SERVER_IP" == "auto" ]]; then
  SERVER_IP="$(detect_lan_ip)" || {
    echo "Could not detect LAN IP. Set SERVER_IP manually." >&2
    exit 1
  }
fi

export SERVER_IP

RID="$(detect_rid)" || {
  echo "Unsupported platform." >&2
  exit 1
}

TARGET="$(pick_server "$RID")" || {
  echo "Server binary not found." >&2
  echo "Build a self-contained package first:" >&2
  echo "  ./publish.sh" >&2
  echo "Developers can also use:" >&2
  echo "  dotnet build Server/DfoServer.sln -c Debug" >&2
  exit 1
}

echo "Using SERVER_IP=$SERVER_IP"

run_server() {
  if [[ ${#SERVER_ARGS[@]} -gt 0 ]]; then
    exec "$@" --server-ip "$SERVER_IP" "${SERVER_ARGS[@]}"
  else
    exec "$@" --server-ip "$SERVER_IP"
  fi
}

if [[ "$TARGET" == dotnet\|* ]]; then
  DLL="${TARGET#dotnet|}"
  cd "$ROOT/Server/DfoServer/bin/Debug"
  run_server dotnet "$DLL"
else
  BIN="${TARGET#local|}"
  cd "$(dirname "$BIN")"
  run_server "./$(basename "$BIN")"
fi
