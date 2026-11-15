#!/bin/bash
# 用法: netstress.sh <日志文件> <设备IP> [阶段...]
# WiFi-SDIO 收发压力分级测试：边抓串口边从本机打大包 ping，逐级加码，
# 每级结束后发 `disp mem` 记录 DMA 预留池水位；一旦串口里出现重启就停下并报告是哪一级。
# 阶段写法 "<每秒包数>x<包长>x<秒数>"（都是正整数），默认从轻到重四级。
# 注意：只碰 Tab5 的 USB-JTAG 口；读串口用 stty + cat 重定向（不要 pipe / pyserial）。
# 需要 iputils 的 ping 允许普通用户用 10ms 级间隔（新版下限 2ms；旧版 200ms 会在自检处报错退出）。
. "$(dirname "$0")/../tab5_env.sh"; tab5_need_port; PORT=$TAB5_PORT
if [ $# -lt 2 ]; then
  echo "用法: $0 <日志文件> <设备IP> [<每秒包数>x<包长>x<秒数> ...]" >&2; exit 2
fi
LOG="$1"; IP="$2"; shift 2
STAGES=("$@"); [ ${#STAGES[@]} -eq 0 ] && STAGES=(5x1400x20 20x1400x20 50x1400x30 100x1400x20)
for st in "${STAGES[@]}"; do
  if ! [[ "$st" =~ ^[1-9][0-9]*x[1-9][0-9]*x[1-9][0-9]*$ ]]; then
    echo "阶段格式不对: $st（应为 <每秒包数>x<包长>x<秒数>）" >&2; exit 2
  fi
done
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

sleep 1; cmd "disp mem"; sleep 3
if ! check_reboot "测试开始前"; then
  for st in "${STAGES[@]}"; do
    pps="${st%%x*}"; rest="${st#*x}"; size="${rest%%x*}"; secs="${rest#*x}"
    cnt=$((pps * secs)); itv=$(echo "scale=4; 1/$pps" | bc -l)
    printf '\n@@STAGE %s\n' "$st" >> "$LOG"
    res=$(ping -q -s "$size" -i "$itv" -c "$cnt" -W 1 "$IP" 2>&1 | tail -2 | tr '\n' ' ')
    echo "阶段 $st: $res"
    sleep 2
    check_reboot "阶段 $st" && break
    cmd "disp mem"; sleep 3
    check_reboot "阶段 $st 结束后" && break
  done
fi
[ "$REBOOTED" = 1 ] && sleep 20   # 把重启后的启动日志也收进来
kill $CATPID 2>/dev/null; wait $CATPID 2>/dev/null
# 预留池按长度认（CONFIG_SPIRAM_MALLOC_RESERVE_INTERNAL=65536 → 堆里显示 len 65535），地址每次构建都可能变
echo "== 各阶段后 DMA 预留池（len 65535 那一行）水位 =="
grep -a "@@STAGE\|At 0x[0-9a-f]* len 65535 \|^free:" "$LOG" | sed 's/allocated.*min_free/min_free/'
echo "== 断言/内存错误计数 =="
grep -a -c "assert failed\|Not enough heap" "$LOG"
exit 0
