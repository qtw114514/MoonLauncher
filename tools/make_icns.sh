#!/bin/sh
# 从 Assets/icon.png 生成 macOS 的 Assets/icon.icns（只在 macOS 上跑，依赖 iconutil）。
#
#   sh tools/make_icns.sh
#
# 图标是像素风，放大一律用最近邻，避免边缘被插值糊掉。
# 生成结果提交进仓库，打包时直接拷贝，不必每次构建都重新生成；换图标后重跑本脚本即可。
set -e

ROOT=$(cd "$(dirname "$0")/.." && pwd)
SRC="$ROOT/src/MoonLauncher/Assets/icon.png"
DST="$ROOT/src/MoonLauncher/Assets/icon.icns"
SET="$ROOT/.publish/icon.iconset"

if [ ! -f "$SRC" ]; then
    echo "找不到源图：$SRC" >&2
    exit 1
fi
if ! command -v iconutil > /dev/null 2>&1; then
    echo "找不到 iconutil，本脚本只能在 macOS 上运行" >&2
    exit 1
fi

rm -rf "$SET"
mkdir -p "$SET"

python3 - "$SRC" "$SET" <<'PY'
import sys
from PIL import Image

src, out = sys.argv[1], sys.argv[2]
img = Image.open(src).convert("RGBA")

# iconutil 要求的整套尺寸：(文件名, 边长)
targets = [
    ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32),
    ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
    ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256),
    ("icon_256x256.png", 256), ("icon_256x256@2x.png", 512),
    ("icon_512x512.png", 512), ("icon_512x512@2x.png", 1024),
]
for name, size in targets:
    img.resize((size, size), Image.NEAREST).save(f"{out}/{name}")
print(f"已生成 {len(targets)} 个尺寸")
PY

rm -f "$DST"
iconutil -c icns "$SET" -o "$DST"
rm -rf "$SET"

echo "已生成 src/MoonLauncher/Assets/icon.icns"
ls -lh "$DST" | awk '{print "  大小 " $5}'
