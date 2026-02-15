#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$SCRIPT_DIR/Qubic.Toolkit.csproj"
PUBLISH_DIR="$SCRIPT_DIR/publish"

RIDS=("win-x64" "osx-x64" "osx-arm64" "linux-x64")
BINARY_NAME="Qubic.Net.Toolkit"

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

for rid in "${RIDS[@]}"; do
    echo ""
    echo "=========================================="
    echo "  Publishing $rid"
    echo "=========================================="

    # macOS dlopen does not search the .NET single-file extraction directory,
    # so native libs (Photino.Native.dylib) must stay next to the binary.
    if [[ "$rid" == osx-* ]]; then
        include_native="false"
    else
        include_native="true"
    fi

    dotnet publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract="$include_native" \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=none \
        -o "$PUBLISH_DIR/$rid"

    # Determine binary filename
    if [[ "$rid" == win-* ]]; then
        bin="$BINARY_NAME.exe"
    else
        bin="$BINARY_NAME"
    fi

    zip_name="$BINARY_NAME-$rid.zip"
    bin_hash_name="$bin.sha256"
    zip_hash_name="$BINARY_NAME-$rid.zip.sha256"

    # SHA-256 hash of the binary
    sha256sum "$PUBLISH_DIR/$rid/$bin" | awk -v f="$bin" '{print $1, f}' > "$PUBLISH_DIR/$rid/$bin_hash_name"

    # Remove build artifacts not needed at runtime (wwwroot is embedded in the binary)
    rm -f "$PUBLISH_DIR/$rid"/*.pdb
    rm -f "$PUBLISH_DIR/$rid"/*.json
    rm -f "$PUBLISH_DIR/$rid"/icon.ico
    rm -rf "$PUBLISH_DIR/$rid"/wwwroot

    # Move files into a named folder so the zip extracts cleanly
    folder_name="$BINARY_NAME-$rid"
    mkdir -p "$PUBLISH_DIR/$folder_name"
    mv "$PUBLISH_DIR/$rid"/* "$PUBLISH_DIR/$folder_name/"
    rm -rf "$PUBLISH_DIR/$rid"

    # Create zip with folder structure
    (cd "$PUBLISH_DIR" && zip -r "$zip_name" "$folder_name")

    # SHA-256 hash of the zip
    sha256sum "$PUBLISH_DIR/$zip_name" | awk -v f="$zip_name" '{print $1, f}' > "$PUBLISH_DIR/$zip_hash_name"

    # Clean up intermediate directory
    rm -rf "$PUBLISH_DIR/$folder_name"

    echo "  -> $zip_name"
    echo "  -> $zip_hash_name ($(awk '{print $1}' "$PUBLISH_DIR/$zip_hash_name"))"
done

echo ""
echo "=========================================="
echo "  Done! Files in: $PUBLISH_DIR"
echo "=========================================="
ls -lh "$PUBLISH_DIR"
