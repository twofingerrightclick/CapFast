#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${1:-osx-arm64}"
case "$runtime" in
  osx-arm64|osx-x64) ;;
  *) echo "Usage: $0 [osx-arm64|osx-x64]" >&2; exit 2 ;;
esac
sign_identity="${CAPFAST_SIGN_IDENTITY:-CapFast Local Signing}"
if ! security find-identity -v -p codesigning | grep -Fq "\"$sign_identity\""; then
  echo "Code-signing identity '$sign_identity' is not available in Keychain." >&2
  exit 1
fi

output_dir="$repo_dir/artifacts/macos/$runtime"
bundle="$output_dir/CapFast.app"
mkdir -p "$output_dir"
# Remove only this script's generated app so stale native files cannot survive a rebuild.
rm -rf -- "$bundle"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
dotnet publish "$repo_dir/src/CapFast/CapFast.csproj" \
  --configuration Release --runtime "$runtime" --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false -p:UsedAvaloniaProducts= \
  --output "$bundle/Contents/MacOS"
cp "$repo_dir/packaging/macos/Info.plist" "$bundle/Contents/Info.plist"
chmod +x "$bundle/Contents/MacOS/CapFast"
case "$runtime" in
  osx-arm64) swift_target="arm64-apple-macosx12.0" ;;
  osx-x64) swift_target="x86_64-apple-macosx12.0" ;;
esac
mkdir -p "$repo_dir/artifacts/swift-module-cache"
xcrun swiftc "$repo_dir/native/macos/AudioCapture.swift" -O \
  -target "$swift_target" -module-cache-path "$repo_dir/artifacts/swift-module-cache" \
  -o "$bundle/Contents/MacOS/CapFast.AudioCapture"
helper="$bundle/Contents/MacOS/CapFast.AudioCapture"
codesign --force --sign "$sign_identity" \
  --identifier app.capfast.captions.audio-capture "$helper"
signed_dependencies=0
while IFS= read -r -d '' dependency; do
  if ! codesign --force --sign "$sign_identity" "$dependency" >/dev/null 2>&1; then
    echo "Could not sign $dependency" >&2
    exit 1
  fi
  signed_dependencies=$((signed_dependencies + 1))
done < <(find "$bundle/Contents/MacOS" -type f \
  ! -name 'CapFast' ! -name 'CapFast.AudioCapture' -print0)
echo "Signed $signed_dependencies bundled files."
codesign --force --sign "$sign_identity" \
  --identifier app.capfast.captions "$bundle"
bash "$repo_dir/scripts/check-macos-signing.sh" "$bundle"
echo "Built $bundle"
echo "Launch with: open \"$bundle\""
