#!/bin/bash
# 用法: ser.sh <日志文件> <总时长秒> [延迟秒:命令]...
# 先开始抓串口，再按给定延迟依次发送调试命令（整行，带换行），到时结束。
# 每发一条命令，在日志里插入一行 "@@CMD <命令>" 作为分段标记（summ.py 用它切段）。
# 注意：只碰 Tab5 的 USB-JTAG 口；读串口必须用 stty + cat 重定向（不要 pipe / pyserial）。
. "$(dirname "$0")/../tab5_env.sh"; tab5_need_port; PORT=$TAB5_PORT
LOG="$1"; DUR="$2"; shift 2
stty -F "$PORT" 115200 raw -clocal -echo
: > "$LOG"
cat "$PORT" >> "$LOG" 2>/dev/null &
CATPID=$!
NAPPID=
# 被调用方提前 kill 时（回归脚本测完就收尾，不等到总时长）也要把 cat 和正在睡的 sleep 带走：
# sleep 留下来的话会一直拿着调用方的输出管道，后台任务要等它睡完（几十分钟）才算结束
trap 'kill $CATPID $NAPPID 2>/dev/null' EXIT
trap 'exit 143' TERM INT
nap() { sleep "$1" & NAPPID=$!; wait $NAPPID; NAPPID=; }      # 放后台再 wait：这样等的时候收到信号能马上处理
START=$(date +%s.%N)
for item in "$@"; do
  d="${item%%:*}"; c="${item#*:}"
  # 等到相对起点 d 秒
  now=$(date +%s.%N)
  wait_s=$(echo "$START + $d - $now" | bc -l)
  if (( $(echo "$wait_s > 0" | bc -l) )); then nap "$wait_s"; fi
  printf '\n@@CMD %s\n' "$c" >> "$LOG"
  printf '%s\n' "$c" > "$PORT"
done
now=$(date +%s.%N)
wait_s=$(echo "$START + $DUR - $now" | bc -l)
if (( $(echo "$wait_s > 0" | bc -l) )); then nap "$wait_s"; fi
kill $CATPID 2>/dev/null; wait $CATPID 2>/dev/null
exit 0
