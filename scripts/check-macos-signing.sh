#!/usr/bin/env bash
set -euo pipefail

bundle="${1:?Usage: check-macos-signing.sh /path/to/CapFast.app}"
helper="$bundle/Contents/MacOS/CapFast.AudioCapture"
identity="${CAPFAST_SIGN_IDENTITY:-CapFast Local Signing}"

codesign --verify --deep --strict "$bundle"
app_info="$(codesign -dv --verbose=4 "$bundle" 2>&1)"
helper_info="$(codesign -dv --verbose=4 "$helper" 2>&1)"

[[ "$app_info" == *"Identifier=app.capfast.captions"* ]] || { echo "Unexpected app identifier" >&2; exit 1; }
[[ "$helper_info" == *"Identifier=app.capfast.captions.audio-capture"* ]] || { echo "Unstable audio-helper identifier" >&2; exit 1; }
[[ "$app_info" == *"Authority=$identity"* ]] || { echo "App is not signed by $identity" >&2; exit 1; }
[[ "$helper_info" == *"Authority=$identity"* ]] || { echo "Audio helper is not signed by $identity" >&2; exit 1; }

echo "CapFast app and audio helper have stable identifiers and share the Keychain signing identity."
