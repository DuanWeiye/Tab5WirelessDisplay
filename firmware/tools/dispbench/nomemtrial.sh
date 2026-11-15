#!/bin/bash
# 用法: nomemtrial.sh <日志文件> <设备IP>
# 「内部 DMA 内存耗尽」下的 WiFi-SDIO 收发试验：
#   1. `disp dmahog on` 把内部 DMA 内存（含预留池）占满；
#   2. 六路并发的大包 ping 洪泛（设备要收也要回，收发两条路都压到；并发是为了让在途的发送包
#      超过发送池留着的备用块，逼它向堆要新块）+ UDP 灌包；
#   3. `disp dmahog off` 释放，再打一轮普通 ping 看能否完全恢复。
# 期望：全程不重启；占满期间允许丢包（`hosted` 里分配失败/丢包计数上涨）；释放后 ping 不丢。
# 官方原版 esp-hosted 1.4.0 在第 2 步会断言重启（sdio_rx_get_buffer / transport_drv_sta_tx）。
# 注意：只碰 Tab5 的 USB-JTAG 口；读串口用 stty + cat 重定向（不要 pipe / pyserial）。
. "$(dirname "$0")/../tab5_env.sh"; tab5_need_port; PORT=$TAB5_PORT
UDP_PORT=5201
HERE="$(cd "$(dirname "$0")" && pwd)"
if [ $# -ne 2 ]; then echo "用法: $0 <日志文件> <设备IP>" >&2; exit 2; fi
LOG="$1"; IP="$2"
if ! ping -q -c 1 -i 0.01 127.0.0.1 >/dev/null 2>&1; then
  echo "本机 ping 不允许 10ms 间隔（iputils 太旧或权限不够），无法做压力测试" >&2; exit 2
fi
if [ ! -e "$PORT" ]; then echo "找不到 Tab5 串口 $PORT" >&2; exit 1; fi
if ! : > "$LOG"; then echo "写不了日志文件 $LOG" >&2; exit 1; fi

stty -F "$PORT" 115200 raw -clocal -echo
cat "$PORT" >> "$LOG" 2>/dev/null &
CATPID=$!
trap 'kill $CATPID 2>/dev/null' EXIT

cmd() { printf '\n@@CMD %s\n' "$1" >> "$LOG"; printf '%s\n' "$1" > "$PORT"; }
rebooted() { grep -a -q "ESP-ROM:\|Rebooting\.\.\." "$LOG" || ! kill -0 $CATPID 2>/dev/null; }
finish() {
  kill $CATPID 2>/dev/null; wait $CATPID 2>/dev/null
  echo "== 占满 / 释放 =="
  grep -a 'dmahog' "$LOG" | grep -a -v '@@CMD' | sed 's/^[WIE] ([0-9]*) //'
  echo "== esp-hosted 统计（占满期间压测之后）=="
  grep -a '每包接收缓冲\|WiFi 发送缓冲\|接收丢包\|接收突发' "$LOG" | sed 's/^[WIE] ([0-9]*) //'
  echo "== 压测期间的告警 / 错误日志（按内容归并计数）=="
  grep -a '^[WE] (' "$LOG" | grep -a -v 'dbgcon\|nettest\|向堆要了\|接收丢包：\|接收突发：' \
    | sed 's/^\([WE]\) ([0-9]*) /\1 /' | cut -c1-150 | sort | uniq -c | sort -rn | head -12
  echo "== 断言/重启 =="
  grep -a "assert failed\|Guru Meditation\|abort()" "$LOG" | head -3
  grep -a -c "ESP-ROM:" "$LOG"
}

sleep 1
r=$(ping -q -s 1400 -i 0.05 -c 100 -W 1 "$IP" 2>&1 | tail -2 | head -1); echo "占满前 ping 20包/秒: $r"
cmd "disp dmahog on"; sleep 2
cmd "m"; sleep 1
PIDS=()
for i in 1 2 3 4 5 6; do
  ping -q -s 1400 -i 0.002 -c 6000 -W 1 "$IP" 2>&1 | tail -2 | head -1 | sed "s/^/占满时并发 ping 500包\/秒 第 $i 路: /" &
  PIDS+=($!)      # 管道最后一环（sed）的进程号：它退出时这一路的汇总行已经打完
done
wait "${PIDS[@]}"   # 只等这六路；不带参数的 wait 会连抓串口的 cat 一起等，脚本就挂住了
if rebooted; then echo "!! 占满时 ping 洪泛期间设备重启"; sleep 20; finish; exit 1; fi
cmd "netrx udp $UDP_PORT 19"; sleep 2
r=$(python3 "$HERE/nettx.py" udp "$IP" "$UDP_PORT" 20 15 2>&1 | tail -1); echo "占满时 UDP 20 Mbps: 本机$r"
sleep 5
if rebooted; then echo "!! 占满时 UDP 灌包期间设备重启"; sleep 20; finish; exit 1; fi
grep -a 'UDP 收包结束' "$LOG" | tail -1 | sed 's/^.*UDP 收包结束：/  设备：/'
cmd "hosted"; sleep 1
cmd "disp dmahog off"; sleep 2
r=$(ping -q -s 1400 -i 0.05 -c 200 -W 1 "$IP" 2>&1 | tail -2 | head -1); echo "释放后 ping 20包/秒: $r"
cmd "m"; sleep 1
if rebooted; then echo "!! 释放后设备重启"; sleep 20; finish; exit 1; fi
finish
exit 0
