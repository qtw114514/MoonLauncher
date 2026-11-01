#!/bin/sh
# 检查 XAML 里引用的资源键是否都有定义。
#
#   sh tools/check-xaml-keys.sh
#
# 为什么需要这个：StaticResource 缺失会在编译期报 AVLN 错误，但
# DynamicResource 缺失在运行时是静默的 —— 属性直接不生效，界面看起来"少了点什么"
# 却没有任何报错，--selftest 也抓不到。所以单独做一遍静态比对。
set -e

ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"

TMP_DEF=$(mktemp)
TMP_USE=$(mktemp)
trap 'rm -f "$TMP_DEF" "$TMP_USE"' EXIT

# 所有已定义的资源键（全局样式表 + 各页面内定义的）
grep -rhoE 'x:Key="[A-Za-z0-9_.]+"' src/MoonLauncher --include=*.axaml \
    | sed 's/x:Key="//;s/"//' | sort -u > "$TMP_DEF"

# 所有被引用的资源键
grep -rhoE '\{(Static|Dynamic)Resource [A-Za-z0-9_.]+\}' src/MoonLauncher --include=*.axaml \
    | sed -E 's/\{(Static|Dynamic)Resource //;s/\}//' | sort -u > "$TMP_USE"

MISSING=$(comm -23 "$TMP_USE" "$TMP_DEF")

if [ -n "$MISSING" ]; then
    echo "以下资源键被引用但没有定义：" >&2
    echo "$MISSING" | sed 's/^/  /' >&2
    echo >&2
    echo "它们会在运行时静默失效（DynamicResource）或导致编译失败（StaticResource）。" >&2
    exit 1
fi

DEFINED=$(wc -l < "$TMP_DEF" | tr -d ' ')
USED=$(wc -l < "$TMP_USE" | tr -d ' ')
echo "资源键检查通过：引用 $USED 个键，全部有定义（共定义 $DEFINED 个）"
