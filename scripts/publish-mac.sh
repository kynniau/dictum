#!/usr/bin/env bash
# Publishes Dictum for macOS (Apple Silicon) to dist/Dictum.app and zips it to dist/Dictum-<version>-osx-arm64.zip.
# The macOS counterpart of scripts/publish.ps1; runs on a Mac and on GitHub's macOS runners. Needs the .NET 10 SDK on PATH.
#
# Signing and notarisation read NAMES from the environment, never a certificate, key or password:
#   DICTUM_MAC_SIGN_IDENTITY   the Developer ID Application identity name in the keychain. Unset: ad-hoc signed (--sign -),
#                              which Apple Silicon needs to run arm64 code at all, but Gatekeeper rejects (beta limitation).
#   DICTUM_MAC_NOTARY_PROFILE  a notarytool keychain profile name (made once with `xcrun notarytool store-credentials`).
#                              Set: the signed bundle is submitted, stapled and re-zipped. Needs DICTUM_MAC_SIGN_IDENTITY.
#   DICTUM_MAC_DIST            the output folder (default dist/), so a second build never clobbers one still being notarised.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
dist="${DICTUM_MAC_DIST:-$root/dist}"
publish="$dist/mac-publish"
app="$dist/Dictum.app"
macos="$app/Contents/MacOS"
resources="$app/Contents/Resources"
identity="${DICTUM_MAC_SIGN_IDENTITY:-}"
profile="${DICTUM_MAC_NOTARY_PROFILE:-}"
bundled_model="base.en"
model_file="ggml-$bundled_model.bin"

if [ -n "$profile" ] && [ -z "$identity" ]; then
  echo "DICTUM_MAC_NOTARY_PROFILE is set but DICTUM_MAC_SIGN_IDENTITY is not: notarisation needs a Developer ID signature." >&2
  exit 1
fi

version="$(dotnet msbuild src/Dictum.Mac/Dictum.Mac.csproj -getProperty:Version -nologo | tr -d '[:space:]')"
case "$version" in
  [0-9]*.[0-9]*) ;;
  *) echo "Could not read <Version> (got '$version')." >&2; exit 1 ;;
esac
echo "Dictum $version, osx-arm64"

rm -rf "$publish" "$app"
mkdir -p "$dist"

# 1. Publish, self-contained.
dotnet publish src/Dictum.Mac -c Release -r osx-arm64 --self-contained true -o "$publish" -nologo

# 2. Whisper.net's and LLamaSharp's build targets copy every platform's natives; keep this app's two folders only
#    (Whisper.net.Runtime: runtimes/macos-arm64; LLamaSharp.Backend.Cpu: runtimes/osx-arm64/native, both seen in the publish output).
pruned=()
for dir in "$publish"/runtimes/*; do
  [ -d "$dir" ] || continue
  case "$(basename "$dir")" in
    macos-arm64|osx-arm64) ;;
    *) pruned+=("runtimes/$(basename "$dir")"); rm -rf "$dir" ;;
  esac
done
for dir in "$publish"/runtimes/*/*; do # a nested architecture folder such as a Rosetta 2 build, if a package ever adds one
  [ -d "$dir" ] || continue
  case "$dir" in
    */osx-arm64/native|*/macos-arm64/*) ;;
    *) pruned+=("${dir#"$publish"/}"); rm -rf "$dir" ;;
  esac
done
echo "Pruned: ${pruned[*]:-nothing}"

# 3. The bundled speech model. The pinned commit, size and SHA-256 live in Core's catalogue only: the published app fetches
#    and verifies the file (a copy already in dist/model-cache is placed first, so it is verified instead of downloaded).
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
models_in_scratch="$scratch/Library/Application Support/Dictum/models"
mkdir -p "$models_in_scratch" "$dist/model-cache"
if [ -f "$dist/model-cache/$model_file" ]; then
  cp "$dist/model-cache/$model_file" "$models_in_scratch/$model_file"
  echo "Reusing dist/model-cache/$model_file (the app verifies it next)."
fi
HOME="$scratch" "$publish/Dictum.Mac" --download-model "$bundled_model"
[ -f "$models_in_scratch/$model_file" ] || { echo "--download-model left no $model_file in $models_in_scratch" >&2; exit 1; }
cp "$models_in_scratch/$model_file" "$dist/model-cache/$model_file"

# 4. The icon, from the same SVG mark as Dictum.ico.
iconset="$scratch/Dictum.iconset"
dotnet run --project packaging/tools -c Release -- iconset site/dictum.svg "$iconset"
mkdir -p "$resources"
iconutil -c icns "$iconset" -o "$resources/Dictum.icns"

# 5. Assemble the bundle.
mkdir -p "$macos"
cp -R "$publish"/. "$macos"/
mv "$macos/Dictum.Mac" "$macos/Dictum" # the apphost finds Dictum.Mac.dll by the name built into it, not its own file name
mkdir -p "$macos/models"
cp "$dist/model-cache/$model_file" "$macos/models/$model_file"
sed "s/@VERSION@/$version/g" packaging/mac/Info.plist > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist" >/dev/null
cp THIRD-PARTY-NOTICES.txt "$resources/THIRD-PARTY-NOTICES.txt"

# 6. Required files: fail on any that is missing.
required=(
  Dictum Dictum.Mac.dll profanity.txt calibration-sentences.txt libAvaloniaNative.dylib libSkiaSharp.dylib libHarfBuzzSharp.dylib
  "models/$model_file"
)
for n in libwhisper libggml-whisper libggml-base-whisper libggml-cpu-whisper libggml-metal-whisper libggml-blas-whisper; do
  required+=("runtimes/macos-arm64/$n.dylib")
done
for n in libllama libggml libggml-base libggml-cpu libggml-metal libggml-blas libmtmd; do
  required+=("runtimes/osx-arm64/native/$n.dylib")
done
missing=()
for f in "${required[@]}"; do [ -e "$macos/$f" ] || missing+=("$f"); done
for f in Contents/Info.plist Contents/Resources/Dictum.icns Contents/Resources/THIRD-PARTY-NOTICES.txt; do [ -e "$app/$f" ] || missing+=("$f"); done
if [ ${#missing[@]} -gt 0 ]; then echo "The bundle is missing: ${missing[*]}" >&2; exit 1; fi
echo "Required files present: ${#required[@]} under Contents/MacOS, plus Info.plist, Dictum.icns, THIRD-PARTY-NOTICES.txt"

# 7. Sign. Every file under Contents/MacOS first (inside out), then the bundle, never --deep: man codesign on macOS 27 says --deep is
#    deprecated for signing as of macOS 13.0. Hardened runtime and a secure timestamp are what notarisation requires.
if [ -n "$identity" ]; then
  sign_args=(--force --timestamp --options runtime --entitlements packaging/mac/Dictum.entitlements --sign "$identity")
else
  sign_args=(--force --sign -)
  echo "NOTE: DICTUM_MAC_SIGN_IDENTITY is not set. This build is signed ad-hoc and is NOT signed for distribution:"
  echo "      Gatekeeper will reject it on other Macs, and it cannot be notarised."
fi
# codesign treats every file under Contents/MacOS as nested code, and refuses to sign the main executable (or the bundle)
# while any of them is unsigned ("code object is not signed at all", seen for managed .dll files on 22/10/2026). Microsoft's
# notarization guide signs every file for the same reason, so all are signed, data files included (non-Mach-O files keep
# their signature in extended attributes, which ditto carries); the main executable goes last.
signed=0
while IFS= read -r -d '' f; do
  codesign "${sign_args[@]}" "$f" 2>&1 | { grep -v "replacing existing signature" || true; }
  [ "${PIPESTATUS[0]}" -eq 0 ] || { echo "codesign failed on $f" >&2; exit 1; }
  signed=$((signed + 1))
done < <(find "$macos" -type f ! -path "$macos/Dictum" -print0)
codesign "${sign_args[@]}" "$macos/Dictum" 2>&1 | { grep -v "replacing existing signature" || true; }
[ "${PIPESTATUS[0]}" -eq 0 ] || { echo "codesign failed on the main executable" >&2; exit 1; }
signed=$((signed + 1))
codesign "${sign_args[@]}" "$app"
echo "Signed $signed files under Contents/MacOS and the bundle."
codesign --verify --deep --strict --verbose=2 "$app"

# 8. Notarise and staple, when a notarytool keychain profile is named.
zip="$dist/Dictum-$version-osx-arm64.zip"
rm -f "$zip"
if [ -n "$profile" ]; then
  submit_zip="$scratch/Dictum-notarise.zip"
  ditto -c -k --keepParent "$app" "$submit_zip"
  xcrun notarytool submit "$submit_zip" --keychain-profile "$profile" --wait
  xcrun stapler staple "$app"
  xcrun stapler validate "$app"
fi

# 9. Zip (ditto keeps the bundle's attributes) and checksum.
ditto -c -k --keepParent "$app" "$zip"
( cd "$dist" && shasum -a 256 "$(basename "$zip")" > SHA256SUMS-mac.txt )
cat "$dist/SHA256SUMS-mac.txt"

size() { du -sk "$1" | awk '{printf "%.1f MiB", $1 / 1024}'; }
echo "Bundle: $app ($(size "$app")); zip: $zip ($(size "$zip"))"

# 10. Gatekeeper's verdict: informational (an ad-hoc or unnotarised bundle is rejected, which is expected for the beta).
if spctl --assess --type execute --verbose "$app"; then
  echo "Gatekeeper accepts this bundle."
else
  echo "Gatekeeper rejects this bundle (expected unless it is signed with a Developer ID identity and notarised)."
fi
