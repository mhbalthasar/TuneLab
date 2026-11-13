#!/usr/bin/env bash
# 把自包含发布目录打成 macOS 绿色版（.app 的 tar.gz）与拖拽安装的 dmg。
#
# 用法（在 macOS runner 上）：
#   RID=osx-arm64 VERSION=2.1.2 PAYLOAD_DIR=workspace OUT_DIR=dist \
#   CLIUtils/pack-macos.sh
#
# 输入：
#   PAYLOAD_DIR  自包含 publish 的输出（含 TuneLab、TuneLab.Cli、Resources/、bridge/…）
#   OUT_DIR      产物目录
#   RID          osx-x64 | osx-arm64
#   VERSION      版本号（不带 v）
#   ASSETS_DIR   图标目录（默认 CIUtils/assets）
set -euo pipefail

: "${RID:?RID is required}"
: "${VERSION:?VERSION is required}"
: "${PAYLOAD_DIR:?PAYLOAD_DIR is required}"
: "${OUT_DIR:?OUT_DIR is required}"
ASSETS_DIR="${ASSETS_DIR:-CIUtils/assets}"

APP_NAME="TuneLab"
APP_DIR="${OUT_DIR}/${APP_NAME}.app"
CONTENTS="${APP_DIR}/Contents"
MACOS_DIR="${CONTENTS}/MacOS"
RES_DIR="${CONTENTS}/Resources"

rm -rf "${APP_DIR}" "${OUT_DIR}/dmg-root"
mkdir -p "${MACOS_DIR}" "${RES_DIR}" "${OUT_DIR}/dmg-root"

echo "==> [1/5] staging app bundle"
# ── 1. 载荷进 Contents/MacOS（Avalonia 的运行时文件必须与可执行文件同目录）──
cp -R "${PAYLOAD_DIR}/." "${MACOS_DIR}/"
chmod +x "${MACOS_DIR}/TuneLab" "${MACOS_DIR}/TuneLab.Cli"
if [ -d "${MACOS_DIR}/bridge" ]; then
  chmod +x "${MACOS_DIR}"/bridge/*/TuneLab.ExperimentalPatch.VstBridge.* 2>/dev/null || true
fi

echo "==> [2/5] icon"
# ── 2. 图标：png → .icns（iconutil 是 macOS 自带）──
ICONSET="${OUT_DIR}/TuneLab.iconset"
rm -rf "${ICONSET}"
mkdir -p "${ICONSET}"

# 需要 16/32/128/256/512 的两倍图；源素材只有 32..1024，16 与 16@2x 用 sips 缩。
copy_icon() { # <src-size> <dst-name>
  cp "${ASSETS_DIR}/TuneLab-$1.png" "${ICONSET}/$2.png"
}
for spec in "32 icon_32x32" "64 icon_32x32@2x" \
            "128 icon_128x128" "256 icon_128x128@2x" "256 icon_256x256" \
            "512 icon_256x256@2x" "512 icon_512x512" "1024 icon_512x512@2x"; do
  set -- $spec
  copy_icon "$1" "$2"
done

# 16px 源：由 32px 缩出来（素材里没有 16px）。
sips -z 16 16 "${ASSETS_DIR}/TuneLab-32.png" --out "${ICONSET}/icon_16x16.png" >/dev/null
# 32@2x 必须真的是 64px：
sips -z 64 64 "${ASSETS_DIR}/TuneLab-64.png" --out "${ICONSET}/icon_16x16@2x.png" >/dev/null

iconutil -c icns "${ICONSET}" -o "${RES_DIR}/TuneLab.icns"

# ── 3. Info.plist：关联 .tlpx / .tlx ──
cat > "${CONTENTS}/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>TuneLab</string>
  <key>CFBundleDisplayName</key><string>TuneLab</string>
  <key>CFBundleIdentifier</key><string>app.tunelab.TuneLab</string>
  <key>CFBundleVersion</key><string>__VERSION__</string>
  <key>CFBundleShortVersionString</key><string>__VERSION__</string>
  <key>CFBundleExecutable</key><string>TuneLab</string>
  <key>CFBundleIconFile</key><string>TuneLab.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleSignature</key><string>????</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>TuneLab Project</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>Owner</string>
      <key>CFBundleTypeExtensions</key><array><string>tlpx</string><string>tlp</string></array>
    </dict>
    <dict>
      <key>CFBundleTypeName</key><string>TuneLab Extension</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Owner</string>
      <key>CFBundleTypeExtensions</key><array><string>tlx</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST

# 版本号注入（用 sed 而不是 heredoc，避免引号转义把 plist 弄坏）。
sed -i '' "s/__VERSION__/${VERSION}/g" "${CONTENTS}/Info.plist"
printf 'APPL????' > "${CONTENTS}/PkgInfo"

# ── 4. 绿色版：把 .app 打成 tar.gz ──
tar -C "${OUT_DIR}" -czf "${OUT_DIR}/TuneLab-${RID}-v${VERSION}.tar.gz" "${APP_NAME}.app"

# ── 5. dmg：拖到 Applications 即安装 ──
cp -R "${APP_DIR}" "${OUT_DIR}/dmg-root/"
ln -s /Applications "${OUT_DIR}/dmg-root/Applications"
hdiutil create -volname "TuneLab" -srcfolder "${OUT_DIR}/dmg-root" -ov -format UDZO \
  "${OUT_DIR}/TuneLab-${RID}-v${VERSION}.dmg" >/dev/null

echo "macOS packages:"
ls -l "${OUT_DIR}/TuneLab-${RID}-v${VERSION}.tar.gz" "${OUT_DIR}/TuneLab-${RID}-v${VERSION}.dmg"