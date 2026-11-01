#!/bin/sh
# 跨平台发布脚本（macOS / Linux 开发机用）
#
#   sh tools/publish.sh linux-x64          自包含单文件 → dist/linux-x64/MoonLauncher
#   sh tools/publish.sh osx-arm64          Apple Silicon → dist/osx-arm64/MoonLauncher.app
#   sh tools/publish.sh osx-x64            Intel Mac → dist/osx-x64/MoonLauncher.app
#   sh tools/publish.sh linux-x64 --fd     框架依赖（需要目标机装 .NET 8）
#
# 每个 RID 输出到各自的子目录，多平台产物不会互相覆盖或混淆。
# RID 可选：linux-x64 / linux-arm64 / osx-x64 / osx-arm64 / win-x64
set -e

if [ -z "$1" ]; then
    echo "用法: sh tools/publish.sh <rid> [--fd]" >&2
    echo "可选: linux-x64 / linux-arm64 / osx-x64 / osx-arm64 / win-x64" >&2
    exit 1
fi

RID="$1"
FD="$2"
ROOT=$(cd "$(dirname "$0")/.." && pwd)
CSPROJ="$ROOT/src/MoonLauncher/MoonLauncher.csproj"
BUILD="$ROOT/.publish/$RID"
OUT="$ROOT/dist/$RID"

case "$RID" in
    linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64) ;;
    *)
        echo "不支持的 RID：$RID" >&2
        echo "可选：linux-x64 / linux-arm64 / osx-x64 / osx-arm64 / win-x64" >&2
        exit 1
        ;;
esac

if [ "$FD" = "--fd" ]; then
    SC="false"
else
    SC="true"
fi

DOTNET=$(command -v dotnet || true)
if [ -z "$DOTNET" ] && [ -x "/usr/local/share/dotnet/dotnet" ]; then
    DOTNET="/usr/local/share/dotnet/dotnet"
fi
if [ -z "$DOTNET" ]; then
    echo "找不到 dotnet，请先安装 .NET SDK 8+" >&2
    exit 1
fi

# 版本号唯一来源是 csproj 的 <Version>，打包脚本不再各自硬编码
VERSION=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$CSPROJ" | head -1)
if [ -z "$VERSION" ]; then
    echo "无法从 $CSPROJ 读取 <Version>" >&2
    exit 1
fi

rm -rf "$BUILD" "$OUT"
"$DOTNET" publish "$CSPROJ" -c Release -r "$RID" --self-contained "$SC" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version="$VERSION" \
    -nologo -o "$BUILD"

mkdir -p "$OUT"

case "$RID" in
    osx-*)
        # macOS 的原生 dylib 无法压进单文件，组装成 .app（Finder 里就是一个应用）
        sh "$ROOT/tools/make-macos-app.sh" "$BUILD" "$OUT/MoonLauncher.app" "$VERSION"
        echo "已输出 dist/$RID/MoonLauncher.app 版本 $VERSION"
        ;;
    win-*)
        cp "$BUILD/MoonLauncher.exe" "$OUT/"
        echo "已输出 dist/$RID/MoonLauncher.exe 版本 $VERSION"
        ;;
    *)
        cp "$BUILD/MoonLauncher" "$OUT/"
        echo "已输出 dist/$RID/MoonLauncher 版本 $VERSION"
        ;;
esac

echo
echo "dist/ 下的产物："
find "$ROOT/dist" -maxdepth 2 -mindepth 1 | sed "s|$ROOT/||" | sort
