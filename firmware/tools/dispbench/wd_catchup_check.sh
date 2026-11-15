#!/usr/bin/env bash
# 无线显示的「补解」真机检查：Tab5 的几块帧缓冲轮流上屏，每块都落后别的几帧，没重发的条带要靠留着的前几帧数据补上
# （main/wdisp.cpp「解码（排活和补解）」）。补错了不会报错，只会在屏幕上露出旧画面，所以这里直接看帧缓冲：
# 发一串随机的局部更新（wd_send_test.py --pattern patch：每帧随机盖 1~3 个色块；--strip-mix：每帧换一种条带高，
# 新老条带的边界对不齐），停住，用 fbshot 截下正在扫出的那一块，和发出去的最后一帧逐块比。
# 帧数连着取几个（最后一帧轮流落在每一块帧缓冲上），边压边发和事先压好连着发各跑一遍（后者会出现「一帧解完了等前一帧翻页」），
# 再按上一版发送端的 RGB565 帧缓冲跑一遍。
#
# 用法：wd_catchup_check.sh <日志前缀> [只跑名字以它开头的项目，如 fly；f 就是 free、fly、fine 都跑]
#       日志和截图在 logs/<前缀>_*；逐项打印「符合 / 不符」，有不符的退出码 1
# 前提：build/ 下的 ELF 就是设备上烧的那一份（fbshot 的要求）；wd_send_test.py 和 Tab5 配对过（没配过先跑一次带 --password 的）；
#       日常用的托盘程序没连着（它占着唯一的连接）。15 项约 25 分钟，独占 Tab5 的串口；每截一次图芯片暂停约一分钟、之后 WiFi 重连约 9 秒。
# 期间 Tab5 常亮、回到主页面（暂停着的话点「开始显示」）、关掉「调试信息」浮层（它会改画面），跑完取消常亮、浮层拨回原样，设备照常等电脑连。
set -uo pipefail
cd "$(dirname "$0")" && mkdir -p logs || exit 1
[ -n "${1:-}" ] || { echo "用法：$0 <日志前缀> [只跑名字以它开头的项目]"; exit 2; }
ONLY=${2:-}
P=logs/$1
. ../tab5_env.sh; tab5_need_port; tab5_need_ip; PORT=$TAB5_PORT
IP=$TAB5_IP
PY=${PY:-python3}
T=../../../windisplay/tools
SERLOG=${P}_serial.log
MIX=16,48,96,160,320            # 每帧挑一种条带高
BLOCK_TOL=30                    # 16×16 的一块里平均每个分量差多少算不一样。JPEG（质量 90）加色度抽样、RGB565 截位，色块边上的块实测最多差十几；
                                # 露出旧画面的块差的是两种不相干的颜色，远不止这个数

cmd() { printf '\n@@CMD %s\n' "$1" >> "$SERLOG"; printf '%s\n' "$1" > "$PORT"; }
SER=; OSD_WAS=; SENDER=
finish() {
    trap - EXIT
    [ -z "$SENDER" ] || kill "$SENDER" 2>/dev/null
    [ -z "$OSD_WAS" ] || cmd "wd osd $OSD_WAS"
    cmd "disp awake off"; sleep 1
    [ -z "$SER" ] || kill "$SER" 2>/dev/null
}
trap finish EXIT
trap 'exit 130' INT TERM

./ser.sh "$SERLOG" 3600 >/dev/null 2>&1 & SER=$!
sleep 1
# wd start = 点主页面上的「开始显示」：只有暂停着才点得到，没暂停时打一行提示、什么都不变
cmd "disp awake on"; sleep 0.5; cmd "open wdisp"; sleep 3; cmd "wd start"; sleep 1
cmd "wd osd"; sleep 1
case "$(grep -a -o 'dbgcon: wd：调试信息\(开\|关\)' "$SERLOG" | tail -1)" in
    *开) OSD_WAS=on; cmd "wd osd off"; sleep 0.5 ;;
    *关) ;;
    *) echo "读不到「调试信息」是开是关（wd osd 没有回话）"; exit 2 ;;
esac

bad=0; ran=0
# check <名字> <帧数> <held | any> [发送端的参数…]      held = 这条连接上要出现过「一帧解完了、放着等前一帧的翻页」（连接结束那行日志里的数），
#                                                        没出现过的话这一项没走到它要查的那条路，不算符合
check() {
    local name=$1 frames=$2 need=$3; shift 3
    case "$name" in "$ONLY"*) ;; *) return ;; esac
    ran=$((ran + 1))
    local log=${P}_$name.log expect=${P}_${name}_expect.png shot=${P}_${name}_shot.png
    local line0; line0=$(wc -l < "$SERLOG")     # 串口日志现在到第几行：这一项的「连接结束」在它之后
    rm -f "$expect" "$shot"
    $PY -u $T/wd_send_test.py "$IP" --pattern patch --strip-mix "$MIX" --quality 90 --frames "$frames" --hold 40 --dump-last "$expect" "$@" > "$log" 2>&1 &
    SENDER=$!
    for _ in $(seq 150); do grep -q '画面发完' "$log" && break; kill -0 "$SENDER" 2>/dev/null || break; sleep 0.2; done
    local verdict
    if ! grep -q '画面发完' "$log"; then
        verdict="不符：发送端没发完（$(tail -1 "$log")）"
    elif ! ../fbshot/fbshot.sh "$shot" > "${P}_${name}_fbshot.txt" 2>&1; then
        verdict="不符：截图没截成（$(tail -1 "${P}_${name}_fbshot.txt")）"
    else
        verdict=$($PY - "$expect" "$shot" "$BLOCK_TOL" <<'PY'
import sys
import numpy as np
from PIL import Image
a = np.asarray(Image.open(sys.argv[1]).convert('RGB'), dtype=np.int16)
b = np.asarray(Image.open(sys.argv[2]).convert('RGB'), dtype=np.int16)
tol = int(sys.argv[3])
d = np.abs(a - b).mean(axis=2).reshape(45, 16, 80, 16).mean(axis=(1, 3))     # 16×16 一块的平均差
n = int((d > tol).sum())
if n:
    ys, xs = np.nonzero(d > tol)
    print(f'不符：{n} 块和发出去的最后一帧不一样（最大差 {d.max():.0f}；横屏 x {xs.min() * 16}~{xs.max() * 16 + 15}，y {ys.min() * 16}~{ys.max() * 16 + 15}）')
else:
    print(f'符合（3600 块里差得最多的一块 {d.max():.1f}）')
PY
)
    fi
    kill "$SENDER" 2>/dev/null; wait "$SENDER" 2>/dev/null; SENDER=
    # 截图那一下暂停之后 Tab5 的 WiFi 会掉线重连（约 9 秒），这条连接也就断了：等它重新连上网、回到监听
    sleep 3
    for _ in $(seq 40); do ping -c 1 -W 1 "$IP" >/dev/null 2>&1 && break; sleep 1; done
    sleep 2
    # 这条连接的「连接结束」在芯片恢复运行之后才打出来（截图期间它停着）。按帧数认：相邻两项的帧数不一样，不会取到上一项晚到的那一条
    local held; held=$(tail -n +"$((line0 + 1))" "$SERLOG" | grep -a "连接结束.*：$frames 帧，" | tail -1 | sed -n 's/.*等过前一帧翻页的 \([0-9][0-9]*\) 帧.*/\1/p')
    case "$verdict" in
        符合*) if [ "$need" = held ] && [ "${held:-0}" -eq 0 ]; then verdict="不符：画面对，但这条连接上没有出现过「解完放着等前一帧翻页」的帧（${held:-读不到}）"
               else verdict="$verdict，放着等过前一帧翻页的 ${held:-?} 帧"; fi ;;
    esac
    case "$verdict" in 符合*) ;; *) bad=1 ;; esac
    printf '%-44s %s\n' "$name（$frames 帧 $*）" "$verdict"
}

for n in 90 91 92; do check "free$n" "$n" any; done                     # 边压边发、平时 2 帧在途：帧来得比屏幕刷新慢，多数帧到的时候前一帧已经上屏
# 事先压好、3 帧在途连着发：发送端不拖后腿，Tab5 上会出现「解完了、放着等前一帧翻页」的帧，后面还压着一帧的条带。
# 这种画面每帧要补前两帧的条带，解一帧多半比一个帧周期长，所以放着等的只有几帧（实测 90 帧里 2~10 帧）：
# 这里要求至少出现过一次；它出现得多的场景（整屏滚动、游戏）画面每帧整屏重发，补解用不上，不在这个检查里
for n in 90 91 92; do check "fly$n" "$n" held --prebuild --inflight 3; done
# 同上，条带切细（一帧几十个条带，连同补解的排满解码队列）
for n in 90 91 92; do check "fine$n" "$n" held --prebuild --inflight 3 --strip-mix 16,32,48; done
for n in 60 61 62; do check "slow$n" "$n" any --fps 20; done            # 慢慢发：每帧都是上一帧的翻页生效之后才到
for n in 90 91 92; do check "rgb$n" "$n" any --caps 7; done             # 上一版发送端：RGB565 帧缓冲
[ "$ran" -gt 0 ] || { echo "没有名字以「$ONLY」开头的项目（有 free / fly / fine / slow / rgb）"; exit 2; }
exit $bad
