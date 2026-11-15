#!/bin/bash
# Tab5 截图：经 USB-JTAG（OpenOCD）把芯片暂停一下，读出正在扫出的那块帧缓冲，恢复运行，转成横屏 PNG。
# 不用改固件、不重启、不动界面状态；暂停约一分钟（2026-11-15 量的是 59 秒；这期间 WiFi 会掉线、网络连接断开，屏幕上的画面不变）。
# 用法：fbshot.sh <输出.png> [back|both]      back = 读后台那一块（滚动加速、无线显示的解码在往里画的那块）；
#       both = 一次暂停里两块都读（前台存 <输出>.png、后台存 <输出>_back.png，暂停时间加倍）。
#       暂停会让网络连接超时、界面跟着重画，所以要看「现在屏幕上的样子」只有第一次暂停读到的才算数：拿不准在哪块就用 both
# 前提：build/ 下的 ELF 就是设备上烧的那一份（要从里面查帧缓冲指针的地址）；OpenOCD 开 USB 设备要 root，脚本里用 sudo -n。
# 无线显示期间帧缓冲有三块轮流上屏、格式可能是 YUV422：前台按移植层的 s_direct_scan（最近一次登记换上屏幕的那一块）取，
# 后台按 wdisp 的 s_back 取，格式按 wdisp 的 s_fb_yuv 认。暂停正好落在「登记了翻页、帧边界还没到」的那十几毫秒里时，
# 截到的前台是马上要上屏的那一帧，不是屏幕上这一刻的画面（画面在动的时候截，要记着这一点）。
# 读到的是 CPU 看到的内存（经缓存）：和面板实际扫出的不一致时（有人经缓存写了帧缓冲没回写），这里看不出来。
# 两块连读之后 USB 串口的输出方向可能卡死（命令照常执行，就是收不到日志），重启设备才恢复。
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
ELF=$(ls "$HERE"/../../build/*.elf | head -1)
OUT=${1:?用法：fbshot.sh <输出.png> [back|both]}
WHICH=${2:-front}
case "$WHICH" in front|back|both) ;; *) echo "第二个参数只能是 back 或 both（不给 = 前台）"; exit 1 ;; esac
NM=$(ls ~/.espressif/tools/riscv32-esp-elf/*/riscv32-esp-elf/bin/riscv32-esp-elf-nm | tail -1)
OCD=$(ls -d ~/.espressif/tools/openocd-esp32/*/openocd-esp32 | tail -1)
[ -n "$NM" ] && [ -n "$OCD" ] || { echo "找不到 IDF 的工具链 / OpenOCD（~/.espressif/tools）"; exit 1; }
FBS=0x$($NM "$ELF" | awk '$3 == "s_fbs" { print $1 }')
CUR=0x$($NM "$ELF" | awk '$3 == "s_fb_cur" { print $1 }')
SCAN=0x$($NM "$ELF" | awk '$3 == "s_direct_scan" { print $1 }')
WBACK=0x$($NM "$ELF" | awk '$3 == "_ZN5wdispL6s_backE" { print $1 }')
WYUV=0x$($NM "$ELF" | awk '$3 == "_ZN5wdispL8s_fb_yuvE" { print $1 }')
[ "$FBS" != 0x ] && [ "$CUR" != 0x ] && [ "$SCAN" != 0x ] && [ "$WBACK" != 0x ] && [ "$WYUV" != 0x ] \
    || { echo "ELF 里找不到 s_fbs / s_fb_cur / s_direct_scan / wdisp 的 s_back、s_fb_yuv"; exit 1; }
TMP=$(mktemp -d)
trap 'sudo -n rm -rf "$TMP"' EXIT
cat > "$TMP/dump.tcl" <<TCL
init
halt
# 读的时候出了错也要恢复运行，不然芯片一直停着
set failed [catch {
    set fbs [read_memory $FBS 32 2]
    set cur [lindex [read_memory $CUR 32 1] 0]
    set scan [lindex [read_memory $SCAN 32 1] 0]
    # 无线显示正占着屏幕（scan 不是 0）：前台 = scan，后台 = wdisp 的 s_back；否则是界面的两块
    set front [expr {\$scan ? \$scan : [lindex \$fbs \$cur]}]
    set back  [expr {\$scan ? [lindex [read_memory $WBACK 32 1] 0] : [lindex \$fbs [expr {\$cur ^ 1}]]}]
    set f [open $TMP/yuv w]
    puts \$f [lindex [read_memory $WYUV 8 1] 0]
    close \$f
    dump_image $TMP/fb.bin [expr {"$WHICH" eq "back" ? \$back : \$front}] 1843200
    if {"$WHICH" eq "both"} { dump_image $TMP/fb2.bin \$back 1843200 }
} err]
resume
if {\$failed} { echo "读帧缓冲出错：\$err"; shutdown error }
shutdown
TCL
sudo -n timeout 240 "$OCD/bin/openocd" -s "$OCD/share/openocd/scripts" -f board/esp32p4-builtin.cfg -f "$TMP/dump.tcl" > "$TMP/ocd.log" 2>&1 \
    || { tail -5 "$TMP/ocd.log"; echo "OpenOCD 没读成"; exit 1; }
sudo -n chmod a+r "$TMP"/fb*.bin "$TMP/yuv"
YUV=$(tr -dc 0-9 < "$TMP/yuv")
topng() {
"${PY:-python3}" - "$1" "$2" "${YUV:-0}" <<'PY'
import sys
import numpy as np
from PIL import Image
if int(sys.argv[3]):
    # 帧缓冲是竖屏排列、YUV422：每两个像素 4 个字节 U Y0 V Y1，有限范围的 BT.601（DSI 桥也是这么换算的）
    b = np.fromfile(sys.argv[1], dtype=np.uint8).reshape(1280, 360, 4).astype(np.float32)
    y = (b[:, :, [1, 3]].reshape(1280, 720) - 16) * 1.164
    u = np.repeat(b[:, :, 0], 2, axis=1) - 128
    v = np.repeat(b[:, :, 2], 2, axis=1) - 128
    rgb = np.clip(np.dstack([y + 1.596 * v, y - 0.392 * u - 0.813 * v, y + 2.017 * u]), 0, 255).astype(np.uint8)
else:
    a = np.fromfile(sys.argv[1], dtype='<u2').reshape(1280, 720)    # 帧缓冲是竖屏排列、RGB565
    rgb = np.dstack([((a >> 11) & 31) << 3, ((a >> 5) & 63) << 2, (a & 31) << 3]).astype(np.uint8)
Image.fromarray(np.rot90(rgb, k=-1)).save(sys.argv[2])              # 横屏画面 (x, y) = 帧缓冲第 1279 − x 行、第 y 列
PY
echo "$2"
}
topng "$TMP/fb.bin" "$OUT"
[ "$WHICH" = both ] && topng "$TMP/fb2.bin" "${OUT%.png}_back.png"
exit 0
