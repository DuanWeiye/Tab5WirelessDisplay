#!/bin/bash
# 临时测试脚本（配合 TESTBUILD 故障注入，补丁见同目录 inject.patch）。
# 用法：rxfault_seq.sh <日志> <设备IP> <步骤>...
# 步骤：
#   <数字>     hosted fault <数字>      接收侧注入（含义见 inject.patch）
#   t<数字>    hosted txfault <数字>    发送侧注入（1 = 从机不响应，2 = 写数据失败）
#   b<数字>    hosted bootfault <数字> + 软件重启   开机阶段的注入（1 卡初始化失败，2 数据读不出来，
#              3 INIT 事件不来，4 寄存器读不出来）；应该连着重启到上限后判定故障。做完自动撤掉注入（不重启）
#   sw         hosted swreset           和链路无关的软件重启，等重新联网
#   hard       esptool 硬复位，等重新联网
#   late       迟到应答：读任务睡 8 秒，其间发的同步 RPC 超时；之后的 RPC 应答不能错位
#   ui         界面：主页面 / WiFi 页的故障显示，点「故障」出确认框、点「取消」关掉
#   uirestart  界面：WiFi 页点故障文字 → 确认框点「重启」，等重新联网
#   cmd:<命令>  发一条调试台命令，等 3 秒
# 每个注入步骤：发命令（再补一条 w 制造发送流量），等 WAIT 秒看有没有重启 / 判定故障；重启了就等重新联网。
# 判定故障以后：照界面的方式调 wifi 接口并计时（uitest）、连发 RPC 看有没有崩和内存泄漏（rpcfail）、
# 开各个页面核对故障显示（uifind），再静置 QUIET 秒，数日志行数（看有没有刷屏）、看有没有重启。
# 每一步结束都检查新增日志里有没有崩溃痕迹。FLOOD=1 时注入步骤期间另打 4 路大包 ping（大流量下出故障）。
# 全程本机 ping 设备制造流量；抓串口的循环能扛住设备重启后 USB 口重新枚举。
. "$(dirname "$0")/../../tab5_env.sh"; tab5_need_port; PORT=$TAB5_PORT
LOG="$1"; IP="$2"; shift 2
WAIT="${WAIT:-20}"
QUIET="${QUIET:-30}"
BOOTWAIT="${BOOTWAIT:-150}"
PAUSE="$LOG.pause"
rm -f "$PAUSE"
: > "$LOG"
( while :; do
    if [ ! -e "$PAUSE" ] && [ -e "$PORT" ]; then stty -F "$PORT" 115200 raw -clocal -echo 2>/dev/null; cat "$PORT" >> "$LOG" 2>/dev/null; fi
    sleep 0.2
  done ) &
LOOP=$!
( while :; do ping -i 0.2 -w 30 "$IP" >/dev/null 2>&1; sleep 0.5; done ) &
PINGLOOP=$!
FLOODPIDS=""
flood_start() { [ -n "$FLOOD" ] || return; for i in 1 2 3 4; do ( while :; do ping -q -i 0.01 -s 1400 -w 20 "$IP" >/dev/null 2>&1; sleep 0.2; done ) & FLOODPIDS="$FLOODPIDS $!"; done; }
flood_stop()  { for p in $FLOODPIDS; do pkill -P "$p" 2>/dev/null; kill "$p" 2>/dev/null; done; FLOODPIDS=""; }
trap 'flood_stop; pkill -P $LOOP 2>/dev/null; kill $LOOP 2>/dev/null; pkill -P $PINGLOOP 2>/dev/null; kill $PINGLOOP 2>/dev/null; rm -f "$PAUSE"' EXIT
cmd() { printf '\n@@CMD %s\n' "$1" >> "$LOG"; printf '%s\n' "$1" > "$PORT" 2>/dev/null; }
newlines() { tail -n +$(($1+1)) "$LOG"; }
waitfor() { local pat="$1" to="$2" n0="$3" t=0
  while [ $t -lt $((to*5)) ]; do
    newlines "$n0" | grep -a -q "$pat" && return 0
    sleep 0.2; t=$((t+1))
  done; return 1; }
pingcheck() { ping -c 10 -i 0.2 -W 1 "$IP" 2>/dev/null | grep -o '[0-9.]*% packet loss'; }
# 新增日志里的崩溃痕迹（重启恢复本身不算）。
# panic 之后 ROM 打印的也是 rst:0xc (SW_CPU_RESET)，和重启恢复分不出来；崩溃时的打印又可能赶上串口丢日志。
# 所以除了崩溃时的打印，还认 `hosted` 输出里的「这次开机的复位原因」（多数步骤收尾时会发一次 hosted）。
crashcheck() { local n0="$1" what="$2" hits
  hits=$(newlines "$n0" | grep -a -c 'Guru Meditation\|abort() was called\|assert failed\|Backtrace:\|Stack smashing\|CORRUPT HEAP\|复位原因：异常\|复位原因：看门狗\|rst:.*WDT')
  [ "$hits" -gt 0 ] && { echo "  ✗ 失败：$what 期间日志里有 $hits 处崩溃痕迹："; newlines "$n0" | grep -a 'Guru Meditation\|abort() was called\|assert failed\|Stack smashing\|CORRUPT HEAP\|复位原因：异常\|复位原因：看门狗\|rst:.*WDT' | head -5 | sed 's/^/    /'; }
  return 0; }
bootcount() { newlines "$1" | grep -a -c 'ESP-ROM:\|cpu_start: Unicore app\|cpu_start: Multicore app\|app_init: Application information'; }
uptime_s() { grep -a -o '^[IWE] ([0-9]*)' "$LOG" | tail -1 | grep -o '[0-9]*' | awk '{printf "%d", $1/1000}'; }
wait_online() { local n0="$1" label="$2"
  if waitfor "got ip" 90 "$n0"; then sleep 6; echo "$label：已重新联网；ping $(pingcheck)"
  else echo "$label：✗ 90 秒没联上网"; fi; }
ui_checks() {
  cmd "open wifi"; sleep 3; cmd "hosted uifind"; sleep 2
  cmd "open wdisp"; sleep 3; cmd "hosted uifind"; sleep 2; }
# 判定故障之后的检查
fault_checks() { local n0="$1" nq boots
  sleep 8                      # 等 wifi 任务下一轮看到故障
  cmd "hosted"; sleep 2
  cmd "w"; sleep 2
  cmd "9"; sleep 2
  cmd "hosted uitest"; sleep 8
  cmd "hosted rpcfail 200"; sleep 8
  cmd "hosted uitest"; sleep 8
  cmd "6"; sleep 3
  ui_checks
  cmd "open wifi"; sleep 3
  cmd "?"; sleep 2
  cmd "+"; sleep 2
  cmd "+"; sleep 2
  cmd "open wdisp"; sleep 3
  nq=$(wc -l < "$LOG")
  printf '\n@@QUIET %s 秒\n' "$QUIET" >> "$LOG"
  sleep "$QUIET"
  boots=$(bootcount "$nq")
  echo "  故障后静置 $QUIET 秒：新增日志 $(( $(wc -l < "$LOG") - nq - 2 )) 行，重启 $boots 次；ping $(pingcheck)"
  cmd "hosted"; sleep 2
  cmd "9"; sleep 2
  cmd "}"; sleep 4
  echo "  此时开机已 $(uptime_s) 秒；$(newlines "$n0" | grep -a -c '已判定链路故障') 次 hosted 输出报告链路故障"
  crashcheck "$n0" "判定故障之后"; }
sleep 1
cmd "hosted"; sleep 2
r=0
for STEP in "$@"; do
  r=$((r+1)); n0=$(wc -l < "$LOG")
  printf '\n@@ROUND %s STEP %s\n' "$r" "$STEP" >> "$LOG"
  case "$STEP" in
    sw)
      cmd "hosted swreset"
      wait_online "$n0" "第 $r 步（软件重启）"
      cmd "hosted"; sleep 2
      crashcheck "$n0" "软件重启"
      continue ;;
    hard)
      touch "$PAUSE"; pkill -P $LOOP cat 2>/dev/null; sleep 0.5
      ( source ~/esp/esp-idf-v5.4.2/export.sh >/dev/null 2>&1
        python -m esptool --chip esp32p4 -p "$PORT" -b 460800 --before default_reset --after hard_reset chip_id >/dev/null 2>&1 ) \
        || echo "第 $r 步（硬复位）：✗ esptool 失败"
      rm -f "$PAUSE"
      wait_online "$n0" "第 $r 步（硬复位）"
      cmd "hosted"; sleep 2
      crashcheck "$n0" "硬复位"
      continue ;;
    cmd:*)
      cmd "${STEP#cmd:}"; sleep 3
      echo "第 $r 步（${STEP#cmd:}）：已发"
      crashcheck "$n0" "${STEP#cmd:}"
      continue ;;
    ui)
      ui_checks
      cmd "open wdisp"; sleep 3
      cmd "hosted uiclick 故障"; sleep 2; cmd "hosted uifind"; sleep 2
      cmd "hosted uiclick 取消"; sleep 2; cmd "hosted uifind"; sleep 2
      cmd "open wifi"; sleep 3
      cmd "hosted uiclick 已连接"; sleep 2; cmd "hosted uifind"; sleep 2
      cmd "open wdisp"; sleep 3
      echo "第 $r 步（界面）：uifind $(newlines "$n0" | grep -a -c 'uifind: 完') 次，见日志"
      crashcheck "$n0" "界面"
      continue ;;
    uirestart)
      cmd "open wifi"; sleep 3
      cmd "hosted uiclick 无线模块故障"; sleep 2; cmd "hosted uifind"; sleep 2
      n1=$(wc -l < "$LOG")
      cmd "hosted uiclick 重启"
      wait_online "$n1" "第 $r 步（界面点重启）"
      cmd "hosted"; sleep 2
      crashcheck "$n0" "界面点重启"
      continue ;;
    late)
      cmd "hosted fault 9"
      if waitfor "注入 9：读任务睡" 10 "$n0"; then
        cmd "6"
        waitfor "注入 9：读任务醒了" 12 "$n0"; sleep 3
        cmd "6"; sleep 3
        cmd "w"; sleep 3
        echo "第 $r 步（迟到应答）：超时 $(newlines "$n0" | grep -a -c 'Timeout waiting for Resp') 次，丢弃迟到应答 $(newlines "$n0" | grep -a -c '没有调用方在等') 次，之后查版本成功 $(newlines "$n0" | grep -a -c 'C6 slave fw') 次，MAC $(newlines "$n0" | grep -a -o 'wifi mac=[0-9a-f:]*([A-Z_]*)' | tail -1)；ping $(pingcheck)"
      else echo "第 $r 步（迟到应答）：✗ 10 秒内读任务没走到注入点"; fi
      cmd "hosted"; sleep 2
      crashcheck "$n0" "迟到应答"
      continue ;;
    b*)
      cmd "hosted bootfault ${STEP#b}"; sleep 1
      cmd "hosted swreset"
      t=0; ok=0
      while [ $t -lt "$BOOTWAIT" ]; do
        sleep 5; t=$((t+5))
        cmd "hosted"; sleep 1
        if newlines "$n0" | grep -a -q "已判定链路故障"; then ok=1; break; fi
      done
      if [ $ok = 1 ]; then
        echo "第 $r 步（开机注入 ${STEP#b}）：约 $t 秒后判定故障，$(newlines "$n0" | grep -a -o '连着重启过 [0-9]* 次' | tail -1)，其间开机 $(bootcount "$n0") 次；ping $(pingcheck)"
        fault_checks "$n0"
      else
        echo "第 $r 步（开机注入 ${STEP#b}）：✗ $BOOTWAIT 秒内没有判定故障；开机 $(bootcount "$n0") 次；ping $(pingcheck)"
        crashcheck "$n0" "开机注入"
      fi
      cmd "hosted bootfault 0"; sleep 1
      continue ;;
    t*) flood_start; cmd "hosted txfault ${STEP#t}" ;;
    *)  flood_start; cmd "hosted fault $STEP" ;;
  esac
  sleep 0.5; cmd "w"            # 制造一次发送（RPC），发送侧的注入靠它触发
  if waitfor "重启主机恢复\|不再重启" "$WAIT" "$n0"; then
    flood_stop
    if newlines "$n0" | grep -a -q "不再重启"; then
      echo "第 $r 步（$STEP）：到上限，判定故障；ping $(pingcheck)"
      fault_checks "$n0"
    else
      n1=$(wc -l < "$LOG")
      wait_online "$n1" "第 $r 步（$STEP）：已重启"
      cmd "hosted"; sleep 2; cmd "d"; sleep 4
      crashcheck "$n0" "重启恢复"
    fi
  else
    flood_stop
    echo "第 $r 步（$STEP）：$WAIT 秒内没有重启；ping $(pingcheck)"
    cmd "hosted"; sleep 2; cmd "d"; sleep 4
    crashcheck "$n0" "注入"
  fi
done
