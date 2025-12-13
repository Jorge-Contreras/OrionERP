#!/usr/bin/env bash
# Codex bootstrap script: installs the SDK pinned in global.json using dotnet-install.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
JSON_FILE="$REPO_ROOT/global.json"
INSTALL_DIR="$REPO_ROOT/.dotnet"

if [[ ! -f "$JSON_FILE" ]]; then
  echo "global.json not found at $JSON_FILE" >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
curl -sSL https://dot.net/v1/dotnet-install.sh -o "$INSTALL_DIR/dotnet-install.sh"
bash "$INSTALL_DIR/dotnet-install.sh" --jsonfile "$JSON_FILE" --install-dir "$INSTALL_DIR"
"$INSTALL_DIR/dotnet" --list-sdks

echo "Dotnet SDK installed to $INSTALL_DIR. Add it to PATH to use the pinned version."
