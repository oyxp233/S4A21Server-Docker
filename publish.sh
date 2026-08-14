#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$ROOT/Server/DfoServer/DfoServer.csproj"
CONFIG="${1:-Release}"

detect_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"

  case "$os" in
    Darwin)
      case "$arch" in
        arm64) echo "osx-arm64" ;;
        x86_64) echo "osx-x64" ;;
        *) echo "unsupported macOS arch: $arch" >&2; return 1 ;;
      esac
      ;;
    Linux)
      case "$arch" in
        x86_64) echo "linux-x64" ;;
        aarch64|arm64) echo "linux-arm64" ;;
        *) echo "unsupported Linux arch: $arch" >&2; return 1 ;;
      esac
      ;;
    MINGW*|MSYS*|CYGWIN*)
      echo "win-x64"
      ;;
    *)
      echo "unsupported OS: $os" >&2
      return 1
      ;;
  esac
}

publish_rid() {
  local rid="$1"
  local out="$ROOT/dist/$rid"

  echo "Publishing self-contained single-file build for $rid -> $out"
  dotnet publish "$PROJECT" \
    -c "$CONFIG" \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$out"

  echo
  echo "Done. Run with:"
  echo "  ./start-server.sh"
  echo "or directly:"
  if [[ "$rid" == win-* ]]; then
    echo "  dist/$rid/DfoServer.exe"
  else
    echo "  dist/$rid/DfoServer"
  fi
}

if [[ "${PUBLISH_ALL:-}" == "1" ]]; then
  for rid in win-x64 osx-arm64 osx-x64 linux-x64 linux-arm64; do
    publish_rid "$rid"
  done
else
  publish_rid "$(detect_rid)"
fi
