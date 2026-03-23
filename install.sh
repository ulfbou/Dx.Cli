#!/usr/bin/env bash
# install.sh — dxs installer for Linux and macOS
# Usage: curl -sSL https://raw.githubusercontent.com/ulfbou/dx.cli/main/install.sh | bash
set -e

REPO="ulfbou/dx.cli"
BINARY_NAME="dxs"
INSTALL_DIR="/usr/local/bin"

# Cleanup on exit — ensures no temp files are left behind on failure
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

# ── 1. Detect OS and architecture ─────────────────────────────────────────────

OS_TYPE=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH_TYPE=$(uname -m)

case "$OS_TYPE" in
  darwin) RID_OS="osx"   ;;
  linux)  RID_OS="linux" ;;
  *)
    echo "❌ Unsupported OS: $OS_TYPE"
    echo "   Supported: linux, darwin (macOS)"
    echo "   For Windows, use: winget install ulfbou.dxs"
    echo "   For any platform with .NET: dotnet tool install -g dxs"
    exit 1
    ;;
esac

case "$ARCH_TYPE" in
  x86_64)        RID_ARCH="x64"   ;;
  arm64|aarch64) RID_ARCH="arm64" ;;
  *)
    echo "❌ Unsupported architecture: $ARCH_TYPE"
    echo "   Supported: x86_64, arm64, aarch64"
    echo "   For any platform with .NET: dotnet tool install -g dxs"
    exit 1
    ;;
esac

RID="${RID_OS}-${RID_ARCH}"

# ── 2. Fetch latest release tag (dependency-free HTML scrape) ─────────────────

echo "🔍 Finding latest release..."
TAG_NAME=$(curl -sSL "https://github.com/${REPO}/releases/latest" \
  | grep -o 'tag/[^"]*' | head -1 | cut -d/ -f2)

if [ -z "$TAG_NAME" ]; then
  echo "❌ Could not determine latest release tag."
  echo "   Check https://github.com/${REPO}/releases for available versions."
  exit 1
fi

VERSION_BARE="${TAG_NAME#v}"
echo "   Latest: ${TAG_NAME}"

# ── 3. Construct download URL ─────────────────────────────────────────────────

ASSET_NAME="dxs-${VERSION_BARE}-${RID}.tar.gz"
DOWNLOAD_URL="https://github.com/${REPO}/releases/download/${TAG_NAME}/${ASSET_NAME}"

# ── 4. Download ───────────────────────────────────────────────────────────────

echo "📥 Downloading ${TAG_NAME} for ${RID}..."
# -f: fail on HTTP errors (404 etc.) instead of silently downloading an error page
curl -fL "$DOWNLOAD_URL" -o "$TMP_DIR/$ASSET_NAME"

# ── 5. Extract ────────────────────────────────────────────────────────────────

echo "📦 Extracting..."
tar -xzf "$TMP_DIR/$ASSET_NAME" -C "$TMP_DIR"

if [ ! -f "$TMP_DIR/$BINARY_NAME" ]; then
  echo "❌ Binary '${BINARY_NAME}' not found in archive."
  exit 1
fi

# ── 6. Install ────────────────────────────────────────────────────────────────

echo "🚀 Installing to ${INSTALL_DIR}..."
if [ -w "$INSTALL_DIR" ]; then
  mv "$TMP_DIR/$BINARY_NAME" "$INSTALL_DIR/$BINARY_NAME"
  chmod +x "$INSTALL_DIR/$BINARY_NAME"
else
  sudo mv "$TMP_DIR/$BINARY_NAME" "$INSTALL_DIR/$BINARY_NAME"
  sudo chmod +x "$INSTALL_DIR/$BINARY_NAME"
fi

# ── 7. Verify ─────────────────────────────────────────────────────────────────

echo "✅ ${BINARY_NAME} ${TAG_NAME} installed to ${INSTALL_DIR}/${BINARY_NAME}"

if [ "$OS_TYPE" = "darwin" ]; then
  echo ""
  echo "   macOS note: If you see 'Developer cannot be verified' on first run, clear"
  echo "   the quarantine attribute with:"
  echo "     xattr -d com.apple.quarantine ${INSTALL_DIR}/${BINARY_NAME}"
  echo ""
  echo "   To avoid this, install via Homebrew instead:"
  echo "     brew install ulfbou/tap/dxs"
fi

echo ""
echo "   Run 'dxs --version' to confirm the installation."
