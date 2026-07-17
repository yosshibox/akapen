#!/bin/bash
# Akapen.app バンドルと配布用 dmg を組み立てる(Mac V1.2 / M-mac5)。
#
# 使い方(リポジトリルートから):
#   bash apps/mac/scripts/make-app.sh            # 未署名 .app + dmg
#   CODESIGN_ID="Developer ID Application: ..." bash apps/mac/scripts/make-app.sh
#
# 署名 ID を渡すと codesign まで行う。公証(notarytool)は Apple ID 資格情報が
# 必要なため本スクリプトの範囲外(README 参照)。
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../../.." && pwd)"
mac_dir="$repo_root/apps/mac"
version="1.2.0"
out_dir="$repo_root/dist"
app="$out_dir/Akapen.app"
dmg="$out_dir/Akapen-$version-macos.dmg"

echo "[make-app] cargo build (release dylib/staticlib)"
(cd "$repo_root" && cargo build -p akapen-ffi --release)

echo "[make-app] swift build -c release (release Rust core)"
(cd "$mac_dir" && rm -rf .build/release && AKAPEN_RUST_LIB_DIR="$repo_root/target/release" swift build -c release)

bin_dir="$mac_dir/.build/release"
test -x "$bin_dir/AkapenApp"

echo "[make-app] assemble $app"
rm -rf "$app" "$dmg"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin_dir/AkapenApp" "$app/Contents/MacOS/Akapen"
# SwiftPM リソースバンドル(マニュアル・アイコン)を同梱
if [ -d "$bin_dir/Akapen_AkapenApp.bundle" ]; then
  cp -R "$bin_dir/Akapen_AkapenApp.bundle" "$app/Contents/Resources/"
fi

echo "[make-app] generate .icns from assets/app-icon/akapen-pixel-source.png"
iconset="$(mktemp -d)/akapen.iconset"
mkdir -p "$iconset"
src="$repo_root/assets/app-icon/akapen-pixel-source.png"
for size in 16 32 64 128 256 512; do
  sips -z $size $size "$src" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$src" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/akapen.icns"

cat > "$app/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Akapen</string>
  <key>CFBundleDisplayName</key><string>Akapen</string>
  <key>CFBundleIdentifier</key><string>jp.yosshibox.akapen</string>
  <key>CFBundleExecutable</key><string>Akapen</string>
  <key>CFBundleIconFile</key><string>akapen</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>(C) 2026 Yoshino Yoshikawa</string>
</dict>
</plist>
PLIST

if [ -n "${CODESIGN_ID:-}" ]; then
  echo "[make-app] codesign with: $CODESIGN_ID"
  codesign --force --deep --options runtime --sign "$CODESIGN_ID" "$app"
else
  echo "[make-app] CODESIGN_ID 未指定: 未署名バンドル(配布前に署名・公証が必要)"
fi

echo "[make-app] create dmg"
staging="$(mktemp -d)/Akapen"
mkdir -p "$staging"
cp -R "$app" "$staging/"
cp "$repo_root/apps/windows/installer/EULA-ja.txt" "$staging/使用許諾契約.txt"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "Akapen $version" -srcfolder "$staging" -ov -format UDZO "$dmg" >/dev/null

echo "[make-app] done:"
echo "  $app"
echo "  $dmg"
shasum -a 256 "$dmg"
