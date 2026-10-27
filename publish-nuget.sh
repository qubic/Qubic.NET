#!/usr/bin/env bash
set -euo pipefail

# Qubic.Net NuGet Package Publisher
# Usage:
#   ./publish-nuget.sh              # Build only (dry run)
#   ./publish-nuget.sh --push       # Build and push to nuget.org
#   ./publish-nuget.sh --push-local # Build and push to a local feed
#
# Environment:
#   NUGET_API_KEY  — required for --push (nuget.org API key)
#   NUGET_SOURCE   — optional, defaults to https://api.nuget.org/v3/index.json
#   LOCAL_FEED     — optional, defaults to ~/.nuget/local-feed (for --push-local)

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT_DIR="$SCRIPT_DIR/nupkgs"
NUGET_SOURCE="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"
LOCAL_FEED="${LOCAL_FEED:-$HOME/.nuget/local-feed}"

# Packages in dependency order (downstream depends on upstream)
PROJECTS=(
    "src/Qubic.Crypto/Qubic.Crypto.csproj"
    "src/Qubic.Core/Qubic.Core.csproj"
    "src/Qubic.Serialization/Qubic.Serialization.csproj"
    "src/Qubic.Network/Qubic.Network.csproj"
    "src/Qubic.Rpc/Qubic.Rpc.csproj"
    "src/Qubic.Bob/Qubic.Bob.csproj"
    "src/Qubic.Services/Qubic.Services.csproj"
    "src/Qubic.WalletConnect/Qubic.WalletConnect.csproj"
)

MODE="build"
if [[ "${1:-}" == "--push" ]]; then
    MODE="push"
    if [[ -z "${NUGET_API_KEY:-}" ]]; then
        echo "Error: NUGET_API_KEY environment variable is required for --push"
        echo "  export NUGET_API_KEY=your-api-key"
        exit 1
    fi
elif [[ "${1:-}" == "--push-local" ]]; then
    MODE="push-local"
fi

echo "=== Qubic.Net NuGet Publisher ==="
echo "Mode: $MODE"
echo "Output: $OUTPUT_DIR"
echo ""

# Clean output directory
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Build and pack each project
for proj in "${PROJECTS[@]}"; do
    name=$(basename "$proj" .csproj)
    echo "--- Packing $name ---"
    dotnet pack "$SCRIPT_DIR/$proj" -c Release -o "$OUTPUT_DIR" --no-restore 2>&1 || \
    dotnet pack "$SCRIPT_DIR/$proj" -c Release -o "$OUTPUT_DIR" 2>&1
    echo ""
done

# List results
echo "=== Built packages ==="
ls -1 "$OUTPUT_DIR"/*.nupkg
echo ""

# Push if requested
if [[ "$MODE" == "push" ]]; then
    echo "=== Pushing to $NUGET_SOURCE ==="
    for nupkg in "$OUTPUT_DIR"/*.nupkg; do
        echo "Pushing $(basename "$nupkg")..."
        dotnet nuget push "$nupkg" \
            --api-key "$NUGET_API_KEY" \
            --source "$NUGET_SOURCE" \
            --skip-duplicate
    done
    echo ""
    echo "Done! Packages pushed to $NUGET_SOURCE"
elif [[ "$MODE" == "push-local" ]]; then
    echo "=== Pushing to local feed: $LOCAL_FEED ==="
    mkdir -p "$LOCAL_FEED"
    for nupkg in "$OUTPUT_DIR"/*.nupkg; do
        echo "Adding $(basename "$nupkg")..."
        dotnet nuget push "$nupkg" --source "$LOCAL_FEED"
    done
    echo ""
    echo "Done! Packages added to $LOCAL_FEED"
    echo "Add this source with: dotnet nuget add source $LOCAL_FEED --name local"
else
    echo "Dry run complete. To push:"
    echo "  NUGET_API_KEY=your-key ./publish-nuget.sh --push"
    echo "  ./publish-nuget.sh --push-local"
fi
