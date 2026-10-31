#!/bin/sh
# 跨平台发布脚本（macOS / Linux 用；Windows 用 build.ps1）
#
#   sh tools/publish.sh linux-x64                自包含单文件
#   sh tools/publish.sh osx-x64                  生成 dist/MoonLauncher.app
#   sh tools/publish.sh osx-arm64                Apple Silicon
#   sh tools/publish.sh linux-x64 --fd           框架依赖（需要目标机装 .NET 8）
#
# RID 可选：linux-x64 / linux-arm64 / osx-x64 / osx-arm64 / win-x64
set -e

RID="${1:?用法: sh tools/publish.sh <rid> [--fd]}"
FD="$2"
ROOT=$(cd "$(dirname "$0")/.." && pwd)
CSPROJ="$ROOT/src/MoonLauncher/MoonLauncher.csproj"
OUT="$ROOT/.publish/$RID"

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

rm -rf "$OUT"
"$DOTNET" publish "$CSPROJ" -c Release -r "$RID" --self-contained "$SC" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -nologo -o "$OUT"

mkdir -p "$ROOT/dist"

case "$RID" in
    osx-*)
        # macOS 的原生 dylib 无法压进单文件，组装成 .app（Finder 里就是一个应用）
        sh "$ROOT/tools/make-macos-app.sh" "$OUT" "$ROOT/dist/MoonLauncher.app"
        echo "已输出 dist/MoonLauncher.app"
        ;;
    win-*)
        cp "$OUT/MoonLauncher.exe" "$ROOT/dist/"
        echo "已输出 dist/MoonLauncher.exe"
        ;;
    *)
        cp "$OUT/MoonLauncher" "$ROOT/dist/"
        echo "已输出 dist/MoonLauncher"
        ;;
esac

ls -lh "$ROOT/dist" | tail -3
