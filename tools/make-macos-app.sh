#!/bin/sh
# 把自包含发布的产物组装成 macOS 标准的 .app 包（由 tools/publish.sh 调用）
#
#   sh tools/make-macos-app.sh <发布目录> <输出 .app 路径> [版本号]
#
# 版本号不传就从 csproj 读，唯一来源始终是 MoonLauncher.csproj 的 <Version>。
set -e

ROOT=$(cd "$(dirname "$0")/.." && pwd)
SRC="${1:-$ROOT/.publish/osx-x64}"
OUT="${2:-$ROOT/dist/osx-x64/MoonLauncher.app}"
NAME="MoonLauncher"

VERSION="$3"
if [ -z "$VERSION" ]; then
    VERSION=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT/src/MoonLauncher/MoonLauncher.csproj" | head -1)
fi
if [ -z "$VERSION" ]; then
    echo "无法确定版本号：请在第三个参数传入，或检查 csproj 的 <Version>" >&2
    exit 1
fi

ICNS="$ROOT/src/MoonLauncher/Assets/icon.icns"
if [ ! -f "$ICNS" ]; then
    echo "提示：找不到 Assets/icon.icns，.app 会用系统通用图标（跑 sh tools/make_icns.sh 生成）" >&2
fi

rm -rf "$OUT"
mkdir -p "$OUT/Contents/MacOS" "$OUT/Contents/Resources"

cp "$SRC/$NAME" "$OUT/Contents/MacOS/"
cp "$SRC"/*.dylib "$OUT/Contents/MacOS/" 2>/dev/null || true

# 图标放进 Resources，Info.plist 用 CFBundleIconFile 引用（不带 .icns 后缀）
if [ -f "$ICNS" ]; then
    cp "$ICNS" "$OUT/Contents/Resources/icon.icns"
fi

cat > "$OUT/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$NAME</string>
    <key>CFBundleDisplayName</key><string>$NAME</string>
    <key>CFBundleIdentifier</key><string>studio.cqfb.moonlauncher</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>$NAME</string>
    <key>CFBundleIconFile</key><string>icon</string>
    <key>LSMinimumSystemVersion</key><string>10.15</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
PLIST

# 先清掉可能残留的扩展属性，否则 codesign 会因 _CodeSignature 里混进 xattr 而失败
xattr -cr "$OUT" 2>/dev/null || true

# ad-hoc 签名：不签的话 Apple Silicon / 新版 macOS 会拒绝运行未被信任的二进制
codesign --force --deep --sign - "$OUT" 2>/dev/null || echo "警告：ad-hoc 签名失败，首次打开可能需要右键 → 打开"

echo "已生成 $OUT"
echo "版本 $VERSION"
find "$OUT" -type f | sed "s|$OUT/|  |"
