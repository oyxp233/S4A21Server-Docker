#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

echo "Cleaning .NET build outputs..."

if command -v dotnet >/dev/null 2>&1; then
  dotnet clean Server/DfoServer.sln -c Debug --nologo -v q || true
  dotnet clean Server/DfoServer.sln -c Release --nologo -v q || true
else
  echo "dotnet not found; skipping 'dotnet clean'."
fi

remove_dir() {
  if [[ -d "$1" ]]; then
    echo "  removing $1"
    rm -rf "$1"
  fi
}

remove_dir "$ROOT/dist"
remove_dir "$ROOT/publish"
remove_dir "$ROOT/out"
remove_dir "$ROOT/artifacts"
remove_dir "$ROOT/Server/DfoServer/bin"
remove_dir "$ROOT/Server/DfoServer/obj"
remove_dir "$ROOT/Tool/PvfLib/bin"
remove_dir "$ROOT/Tool/PvfLib/obj"

find "$ROOT/Server" "$ROOT/Tool" -type f \( -name 'server.log' -o -name 'packet_log.txt' \) -delete 2>/dev/null || true

echo "Done."
