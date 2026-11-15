#!/bin/bash
# 用法: udpstress.sh <日志文件> <设备IP> [<Mbps>x<秒数>[x<包长>] ...]
# WiFi-SDIO 接收突发压力测试：边抓串口边从本机按给定速率灌 UDP（没有流控），逐级加码。
# 每级：设备侧 `netrx udp` 开始收 → 本机 nettx.py 发 → 设备打印收包统计、内部 DMA 内存水位、
# esp-hosted 缓冲用量和丢包计数（见 main/net/nettest.cpp）→ 再发 `disp mem` 记各内部堆的水位。
# 一旦串口里出现重启就停下并报告是哪一级。
# 注意：只碰 Tab5 的 USB-JTAG 口；读串口用 stty + cat 重定向（不要 pipe / pyserial）。
. "$(dirname "$0")/../tab5_env.sh"; tab5_need_port; PORT=$TAB5_PORT
UDP_PORT=5201
HERE="$(cd "$(dirname "$0")" && pwd)"
if [ $# -lt 2 ]; then
  echo "用法: $0 <日志文件> <设备IP> [<Mbps>x<秒数>[x<包长>] ...]" >&2; exit 2
fi
LOG="$1"; IP="$2"; shift 2
STAGES=("$@"); [ ${#STAGES[@]} -eq 0 ] && STAGES=(5x20 10x20 20x20 30x20)
for st in "${STAGES[@]}"; do
  if ! [[ "$st" =~ ^[1-9][0-9]*x[1-9][0-9]*(x[1-9][0-9]*)?$ ]]; then
    echo "阶段格式不对: $st（应为 <Mbps>x<秒数>[x<包长>]）" >&2; exit 2
  fi
done
if [ ! -e "$PORT" ]; then echo "找不到 Tab5 串口 $PORT" >&2; exit 1; fi
if ! : > "$LOG"; then echo "写不了日志文件 $LOG" >&2; exit 1; fi

stty -F "$PORT" 115200 raw -clocal -echo
cat "$PORT" >> "$LOG" 2>/dev/null &
CATPID=$!
trap 'kill $CATPID 2>/dev/null' EXIT

cmd() { printf '\n@@CMD %s\n' "$1" >> "$LOG"; printf '%s\n' "$1" > "$PORT"; }
# 检查设备是否重启过；重启了就报告并返回 0。$1 = 当前所处的阶段名。
REBOOTED=0
check_reboot() {
  if grep -a -q "ESP-ROM:\|Rebooting\.\.\." "$LOG"; then
    echo "!! $1 期间设备重启: $(grep -a -m1 'assert failed\|Guru Meditation\|abort()' "$LOG")"
    REBOOTED=1; return 0
  fi
  # 设备崩溃时 USB 口可能重新枚举，抓串口的 cat 会跟着退出；此后的日志是缺的，要提示
  if ! kill -0 $CATPID 2>/dev/null; then
    echo "!! $1 期间串口抓取进程退出了（USB 口可能重新枚举过），之后的串口日志不完整"
    REBOOTED=1; return 0
  fi
  return 1
}

sleep 1
if ! check_reboot "测试开始前"; then
  for st in "${STAGES[@]}"; do
    IFS=x read -r mbps secs size <<< "$st"; size="${size:-1400}"
    printf '\n@@STAGE %s\n' "$st" >> "$LOG"
    cmd "netrx udp $UDP_PORT $((secs + 4))"     # 设备多收 4 秒：前 2 秒等它起来，后 2 秒收尾
    sleep 2
    res=$(python3 "$HERE/nettx.py" udp "$IP" "$UDP_PORT" "$mbps" "$secs" "$size" 2>&1 | tail -1)
    echo "阶段 $st: 本机$res"
    sleep 5                                      # 等设备收完并打印统计
    check_reboot "阶段 $st" && break
    grep -a 'UDP 收包结束' "$LOG" | tail -1 | sed 's/^.*UDP 收包结束：/  设备：/'
    cmd "disp mem"; sleep 3
    check_reboot "阶段 $st 结束后" && break
  done
fi
[ "$REBOOTED" = 1 ] && sleep 20   # 把重启后的启动日志也收进来
kill $CATPID 2>/dev/null; wait $CATPID 2>/dev/null

echo "== 各阶段的 esp-hosted 统计（每级最后一次）=="
grep -a '@@STAGE\|接收丢包\|接收突发\|每包接收缓冲\|WiFi 发送缓冲\|结束后 内部DMA' "$LOG" | sed 's/^[WIE] ([0-9]*) //'
echo "== 断言/重启计数 =="
grep -a -c "assert failed\|Guru Meditation\|ESP-ROM:" "$LOG"
[ "$REBOOTED" = 1 ] && exit 1
exit 0
