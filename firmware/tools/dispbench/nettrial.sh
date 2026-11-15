#!/bin/bash
# 用法: nettrial.sh <标签> <轮数> <设备IP> [阶段...]
# 新开机对照试验：每轮先用 esptool 硬复位设备，等它连上 WiFi 再静置 90 秒，
# 然后跑 netstress.sh 的分级压力，日志存 logs/nettrial_<标签>_<轮次>.log。
# 两种固件配置用同一套流程各跑几轮，比较崩溃次数（崩溃取决于突发时的卡顿，要看统计）。
. "$(dirname "$0")/../tab5_env.sh"; tab5_need_port; PORT=$TAB5_PORT
if [ $# -lt 3 ] || ! [[ "$2" =~ ^[1-9][0-9]*$ ]]; then
  echo "用法: $0 <标签> <轮数> <设备IP> [阶段...]" >&2; exit 2
fi
LABEL="$1"; N="$2"; IP="$3"; shift 3
cd "$(dirname "$0")" || exit 1
mkdir -p logs || exit 1
source ~/esp/esp-idf-v5.4.2/export.sh >/dev/null 2>&1
for i in $(seq 1 "$N"); do
  echo "######## $LABEL 第 $i 轮"
  # 复位失败（串口被占、设备不在）时设备并没有重启，这一轮就不是"新开机"，不能算数
  if ! python -m esptool --chip esp32p4 -p "$PORT" --before default_reset --after hard_reset chip_id >/dev/null 2>&1; then
    echo "!! esptool 复位失败，跳过本轮"; continue
  fi
  sleep 5
  up=0
  for _ in $(seq 1 90); do   # 最多等 180 秒
    if ping -c 1 -W 1 "$IP" >/dev/null 2>&1; then up=1; break; fi
    sleep 2
  done
  if [ "$up" != 1 ]; then echo "!! 复位后 180 秒内设备没连上网络，跳过本轮"; continue; fi
  sleep 90
  ./netstress.sh "logs/nettrial_${LABEL}_${i}.log" "$IP" "$@" | grep -v "^free:\|^@@STAGE\|^==\|At 0x"
done
