#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
bundle="$root/artifacts/Simple Listen.app"
mkdir -p "$bundle/Contents/MacOS" "$root/artifacts/swift-module-cache"
xcrun swiftc "$root/simple-listen/SimpleListen.swift" -O \
  -module-cache-path "$root/artifacts/swift-module-cache" \
  -o "$bundle/Contents/MacOS/SimpleListen"
cp "$root/simple-listen/Info.plist" "$bundle/Contents/Info.plist"
codesign --force --sign "${SIMPLE_LISTEN_SIGN_IDENTITY:-CapFast Local Signing}" \
  --identifier app.capfast.simple-listen "$bundle"
codesign --verify --strict "$bundle"
echo "Built $bundle"
