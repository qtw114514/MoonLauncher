#!/bin/sh
# 把 osx-x64 自包含发布的产物组装成 macOS 标准的 .app 包
# 用法：sh .nugettest/make_app.sh <发布目录> <输出 .app 路径>
set -e

SRC="${1:-.publish/osx-x64}"
OUT="${2:-dist/MoonLauncher.app}"
NAME="MoonLauncher"
VERSION="0.0.3"

rm -rf "$OUT"
mkdir -p "$OUT/Contents/MacOS"

cp "$SRC/$NAME" "$OUT/Contents/MacOS/"
cp "$SRC"/*.dylib "$OUT/Contents/MacOS/" 2>/dev/null || true

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
    <key>LSMinimumSystemVersion</key><string>10.15</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
PLIST

# ad-hoc 签名：不签的话 Apple Silicon / 新版 macOS 会拒绝运行未被信任的二进制
codesign --force --deep --sign - "$OUT" 2>/dev/null || echo "警告：ad-hoc 签名失败，首次打开可能需要右键 → 打开"

echo "已生成 $OUT"
find "$OUT" -type f | sed "s|$OUT/|  |"
