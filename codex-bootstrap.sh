#!/usr/bin/env bash
set -euo pipefail

# Codex/CI bootstrap: install the SDK pinned in global.json so local runs match automation.
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GLOBAL_JSON="$ROOT_DIR/global.json"
DOTNET_DIR="$ROOT_DIR/.dotnet"
DOTNET_INSTALL_SH="$DOTNET_DIR/dotnet-install.sh"

if [[ ! -f "$GLOBAL_JSON" ]]; then
  echo "global.json not found at $GLOBAL_JSON" >&2
  exit 1
fi

SDK_VERSION=$(python3 - <<'PY'
import json, pathlib
path = pathlib.Path("global.json")
data = json.loads(path.read_text())
print(data["sdk"]["version"])
PY
)

mkdir -p "$DOTNET_DIR"

curl -sSL https://dot.net/v1/dotnet-install.sh -o "$DOTNET_INSTALL_SH"
chmod +x "$DOTNET_INSTALL_SH"
"$DOTNET_INSTALL_SH" --version "$SDK_VERSION" --install-dir "$DOTNET_DIR" --quality "ga"

echo "Installed .NET SDK $SDK_VERSION to $DOTNET_DIR";
echo "Add the following to your PATH to use this SDK first:"
echo "  export DOTNET_ROOT=\"$DOTNET_DIR\""
echo "  export PATH=\"$DOTNET_DIR:$PATH\""
