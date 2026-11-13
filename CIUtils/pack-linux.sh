#!/usr/bin/env bash
# 把自包含发布目录打成 Linux 绿色版（tar.gz）与 deb / rpm 安装包。
#
# 用法（在 Linux runner 上）：
#   RID=linux-x64 VERSION=2.1.2 PAYLOAD_DIR=workspace OUT_DIR=dist CIUtils/pack-linux.sh
#
# 环境：
#   NFPM           nfpm 可执行文件路径（默认 nfpm，在 PATH 里）
#   SDL2_SRC       可选：目标架构的 libSDL2 共享库路径。linux-arm64 必须给（NuGet 包没有
#                  linux-arm64 的 SDL2），x64 可省略（包自带）。
#   ASSETS_DIR     图标目录（默认 CIUtils/assets）
set -euo pipefail

: "${RID:?RID is required}"
: "${VERSION:?VERSION is required}"
: "${PAYLOAD_DIR:?PAYLOAD_DIR is required}"
: "${OUT_DIR:?OUT_DIR is required}"

NFPM="${NFPM:-nfpm}"
ASSETS_DIR="$(pwd)/${ASSETS_DIR:-CIUtils/assets}"
PAYLOAD_ABS="$(realpath "${PAYLOAD_DIR}")"
INSTALL_ROOT="/opt/tunelab"

case "${RID}" in
  linux-x64)  NFPM_ARCH="amd64" ;;
  linux-arm64) NFPM_ARCH="arm64" ;;
  *) echo "unsupported linux rid: ${RID}" >&2; exit 1 ;;
esac

mkdir -p "${OUT_DIR}"

# ── 1. 便携载荷：先拼一个干净的 TuneLab/ 目录 ──
PORTABLE="${OUT_DIR}/portable"
rm -rf "${PORTABLE}"
mkdir -p "${PORTABLE}/TuneLab"
cp -R "${PAYLOAD_ABS}/." "${PORTABLE}/TuneLab/"

# linux-arm64：NuGet 的 ppy.SDL2-CS 只带 linux-x64 的 libSDL2.so；
# 由 CI 从目标架构的系统包拷一份进来，放在 exe 旁边即可被 DllImport("SDL2") 命中。
if [ -n "${SDL2_SRC:-}" ]; then
  cp "${SDL2_SRC}" "${PORTABLE}/TuneLab/libSDL2.so"
fi

chmod +x "${PORTABLE}/TuneLab/TuneLab" "${PORTABLE}/TuneLab/TuneLab.Cli"
if [ -d "${PORTABLE}/TuneLab/bridge" ]; then
  find "${PORTABLE}/TuneLab/bridge" -type f -maxdepth 2 -name 'TuneLab.ExperimentalPatch.VstBridge.*' -exec chmod +x {} \;
fi

# 命令行入口的别名（与 Windows 的 tunelab.cmd 对齐；包内不便建系统级软链，
# 系统级 /usr/bin/tunelab 由 deb/rpm 的 contents 提供）。
ln -sf TuneLab.Cli "${PORTABLE}/TuneLab/tunelab"

tar -C "${PORTABLE}" -czf "${OUT_DIR}/TuneLab-${RID}-v${VERSION}.tar.gz" TuneLab

# ── 2. nfpm：一份配置出 deb + rpm ──
NFPM_STAGE="${OUT_DIR}/nfpm-stage"
rm -rf "${NFPM_STAGE}"
mkdir -p "${NFPM_STAGE}/share/applications" "${NFPM_STAGE}/share/icons/hicolor/256x256/apps" \
         "${NFPM_STAGE}/share/mime/packages"
cp CIUtils/linux/tunelab.desktop "${NFPM_STAGE}/share/applications/tunelab.desktop"
cp "${ASSETS_DIR}/TuneLab-256.png" "${NFPM_STAGE}/share/icons/hicolor/256x256/apps/tunelab.png"
cp CIUtils/linux/tunelab.xml "${NFPM_STAGE}/share/mime/packages/tunelab.xml"

# nfpm 的 src 相对配置文件所在目录解析 —— 一律用绝对路径，免得布局依赖 OUT_DIR。
NFPM_STAGE_ABS="$(realpath "${NFPM_STAGE}")"
OUT_ABS="$(realpath "${OUT_DIR}")"

# nfpm 的 scripts 字段是**文件路径**（不是内联内容），先落两个脚本再引用。
cat > "${NFPM_STAGE}/postinstall.sh" <<'SH'
#!/bin/sh
update-mime-database /usr/share/mime >/dev/null 2>&1 || true
update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
SH
cp "${NFPM_STAGE}/postinstall.sh" "${NFPM_STAGE}/postremove.sh"
chmod +x "${NFPM_STAGE}/postinstall.sh" "${NFPM_STAGE}/postremove.sh"

cat > "${OUT_DIR}/nfpm.yaml" <<YAML
name: tunelab
arch: ${NFPM_ARCH}
platform: linux
version: ${VERSION}
section: audio
priority: optional
maintainer: TuneLab <tunelab@tunelab.app>
description: |
  TuneLab singing synthesis studio.
  Portable .NET build with VST3 / CLAP / JSFX support through isolated bridges.
vendor: TuneLab
homepage: https://tunelab.app
license: GPL-3.0-or-later
depends:
  - libx11-6
  - libxi6
  - libice6
  - libsm6
  - libfontconfig1
  - libglib2.0-0
  - libgtk-3-0
overrides:
  rpm:
    depends:
      - libX11
      - libXi
      - libICE
      - libSM
      - fontconfig
      - glib2
      - gtk3
contents:
  - src: ${PAYLOAD_ABS}
    dst: ${INSTALL_ROOT}
    type: tree
  - src: ${NFPM_STAGE_ABS}/share/applications/tunelab.desktop
    dst: /usr/share/applications/tunelab.desktop
  - src: ${NFPM_STAGE_ABS}/share/icons/hicolor/256x256/apps/tunelab.png
    dst: /usr/share/icons/hicolor/256x256/apps/tunelab.png
  - src: ${NFPM_STAGE_ABS}/share/mime/packages/tunelab.xml
    dst: /usr/share/mime/packages/tunelab.xml
  - src: ${INSTALL_ROOT}/TuneLab.Cli
    dst: /usr/bin/tunelab
    type: symlink
scripts:
  postinstall: ${NFPM_STAGE_ABS}/postinstall.sh
  postremove: ${NFPM_STAGE_ABS}/postremove.sh
YAML

"${NFPM}" package -f "${OUT_DIR}/nfpm.yaml" -p deb -t "${OUT_DIR}/TuneLab-${RID}-v${VERSION}.deb"
"${NFPM}" package -f "${OUT_DIR}/nfpm.yaml" -p rpm -t "${OUT_DIR}/TuneLab-${RID}-v${VERSION}.rpm"

echo "Linux packages:"
ls -l "${OUT_DIR}/TuneLab-${RID}-v${VERSION}".{tar.gz,deb,rpm}

# 中间产物不留在 OUT_DIR（CI 整个目录当 artifact 上传，未压缩副本会让上传翻倍）。
rm -rf "${PORTABLE}" "${NFPM_STAGE}" "${OUT_DIR}/nfpm.yaml"