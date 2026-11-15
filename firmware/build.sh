#!/usr/bin/env bash
# Tab5 固件构建/烧录脚本
# 用法:
#   ./build.sh            仅编译
#   ./build.sh -f         编译并烧录（只烧录到 Tab5 的 USB-JTAG 口，绝不碰其它串口）
#   ./build.sh -m         烧录后打开串口监视器
# 烧录用的串口：环境变量 TAB5_PORT，或者写在 tools/local.env 里；都没给时自动找（只认 Espressif 的 USB-JTAG 口，
# 而且要正好一个，见 tools/tab5_env.sh）。
set -e

IDF_PATH_DEFAULT="$HOME/esp/esp-idf-v5.4.2"
cd "$(dirname "$0")"

# LVGL 是源码依赖，放在 dependencies/lvgl（不入库）：固定用上游的 v9.2.2，没有就取一份
LVGL_TAG=v9.2.2
if [ ! -f dependencies/lvgl/lvgl.h ]; then
    echo "取 LVGL $LVGL_TAG 到 dependencies/lvgl ……"
    mkdir -p dependencies
    git clone --depth 1 --branch "$LVGL_TAG" https://github.com/lvgl/lvgl.git dependencies/lvgl
fi

if ! command -v idf.py >/dev/null 2>&1; then
    # shellcheck disable=SC1091
    source "${IDF_PATH:-$IDF_PATH_DEFAULT}/export.sh" >/dev/null
fi

# 固件里的版本串是 cmake 配置时取的 `git describe`（短提交号，工作区有没提交的改动时带 -dirty），
# 之后工作区变脏 / 变干净 cmake 察觉不到。这里比一下，对不上就重新配置一次。
# 前提是 IDF 取版本串用的就是下面这条命令（没设 PROJECT_VER、没有 version.txt 时是）；
# 以后改成别的取法的话这里永远对不上，每次编译都会多配置一遍，要跟着改。
if [ -f build/CMakeCache.txt ]; then
    want=$(git describe --always --tags --dirty 2>/dev/null || true)
    have=$(sed -n 's/^ *"project_version": *"\(.*\)",\{0,1\}$/\1/p' build/project_description.json 2>/dev/null || true)
    if [ -n "$want" ] && [ "$want" != "$have" ]; then
        # 不带 -G 时 `idf.py reconfigure` 不看构建目录现在用的生成器，按它自己探测到的来（有 ninja 就用 Ninja）；
        # 和构建目录的不一样就配置失败，还会把 CMakeCache.txt 删掉。所以把现在用的那个原样传给它。
        gen=$(sed -n 's/^CMAKE_GENERATOR:INTERNAL=//p' build/CMakeCache.txt)
        if [ -n "$gen" ]; then
            idf.py -G "$gen" reconfigure
        fi
    fi
fi

idf.py build

do_flash=false
do_monitor=false
for arg in "$@"; do
    case "$arg" in
        -f) do_flash=true ;;
        -m) do_flash=true; do_monitor=true ;;
    esac
done

if $do_flash; then
    # shellcheck disable=SC1091
    . tools/tab5_env.sh
    tab5_need_port
    idf.py -p "$TAB5_PORT" flash
    if $do_monitor; then
        idf.py -p "$TAB5_PORT" monitor
    fi
fi
