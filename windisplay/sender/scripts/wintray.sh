#!/usr/bin/env bash
# 管 Windows 电脑上的托盘程序（WinDisplayTray.exe）：装给人日常用的那一份，和真机测试用的那一份。
# 托盘程序要桌面（抓屏、注入输入、托盘图标），ssh 进去的是会话 0，所以和 windesk.sh 一样经一次性的计划任务（/it）起在登录用户的桌面会话里。
# 那台电脑上同一时间只能跑一份托盘程序。
#
# 日常用的那一份：程序在 %LOCALAPPDATA%\Programs\WinDisplay，数据（配对、密码、日志）在 %LOCALAPPDATA%\WinDisplay。
#   scripts/wintray.sh install [--no-sync]   同步 + 编译 + 发布，整份拷到程序目录旁边，正在跑的那一份正常退出，换上去再起来
#                                            （换不上去时装着的那一份原样不动、重新起来）。头一次装时把开机启动打开
#                                            （之后开不开照设置窗口里那个开关，更新不动它）。没设过密码的话起来之后会弹出设置窗口
#   scripts/wintray.sh uninstall             退出、关掉开机启动、删掉程序。数据留着（不要了就手动删 %LOCALAPPDATA%\WinDisplay）
#
# 测试用的那一份：直接跑远端源码目录里编译出来的，数据放远端 .work\traydata，不碰日常用的那一份数据。
#   scripts/wintray.sh start [--no-sync]     同步 + 编译（winsync.sh），然后起。已经有一份在跑（比如日常用的那一份）就拒绝
#   scripts/wintray.sh password <密码>       不开界面设密码（只许字母、数字和 . _ : / = -；要先 stop）
#   scripts/wintray.sh log [installed]       打印它的日志（installed = 日常用的那一份的）
#   scripts/wintray.sh wipe                  删掉测试用的数据目录（要先 stop）
#
#   scripts/wintray.sh stop                  叫正在跑的那一份（不管是哪一份）正常退出（给 Tab5 发 BYE、松开按着的键），等它退完；20 秒退不掉就强行结束
# 测试用的那一份用完必须 stop：留一份没人管的在那边，它会一直把屏幕发给开着「无线显示」页的 Tab5。
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
. "$HERE/scripts/winenv.sh"
EXE='src\WinDisplay.Tray\bin\Release\net10.0-windows10.0.22621.0\WinDisplayTray.exe'
DATA='.work\traydata'
IMAGE='WinDisplayTray.exe'
# 下面两个里的 %LOCALAPPDATA% 是给远端的 cmd 展开的（ssh 进去的和桌面上登录着的是同一个用户）
APPDIR='%LOCALAPPDATA%\Programs\WinDisplay'
APPDATA_DIR='%LOCALAPPDATA%\WinDisplay'

# 只看、只结束 ssh 进去的这个用户（也就是桌面上登录着的那个）的：那台电脑上别的用户跑着的同名程序不归这里管
MINE="/fi \"imagename eq $IMAGE\" /fi \"username eq %USERNAME%\""
running() { ssh_win "tasklist $MINE /nh" 2>/dev/null | grep -qi "$IMAGE"; }
installed() { [ "$(ssh_win "if exist \"$APPDIR\\$IMAGE\" echo yes" 2>/dev/null | tr -dc 'a-z')" = yes ]; }

# 叫正在跑的那一份正常退出，等它退完。--exit 要在桌面会话里跑（它靠同一个登录会话里的具名事件找到正在跑的那一份），
# 用哪一份 exe 去叫都行：<exe> 给一份现成的
stop_running() {
  local exe="$1"
  running || return 0
  desk traystop "\"$exe\" --exit" "echo %errorlevel% > .work\\traystop.rc" || true
  for _ in $(seq 20); do running || return 0; /usr/bin/sleep 1; done
  echo "[wintray] 20 秒还没退出，强行结束（按着的键这时来不及松开）" >&2
  ssh_win "taskkill /f $MINE" >/dev/null 2>&1 || true
  for _ in $(seq 5); do running || return 0; /usr/bin/sleep 1; done
  return 1
}

wait_running() { for _ in $(seq 10); do running && return 0; /usr/bin/sleep 1; done; return 1; }

case "${1:-}" in
  install)
    [ "${2:-}" = "--no-sync" ] || "$HERE/scripts/winsync.sh"
    # 版本串里带上提交号：装的是哪一版，托盘程序启动时记在它的日志里（远端是 tar 副本，提交号要从这边给）
    rev="$(git -C "$HERE" rev-parse --short HEAD)$(git -C "$HERE" diff --quiet HEAD -- . || echo -dirty)"
    echo "[wintray] 发布（$rev）"
    ssh_win "chcp 65001 >nul & cd /d \"$DEST\" & (if exist .work\\publish rd /s /q .work\\publish) & dotnet publish src\\WinDisplay.Tray -c Release -o .work\\publish -nologo -v quiet -p:SourceRevisionId=$rev" | utf8
    [ "$(ssh_win "if exist \"$DEST\\.work\\publish\\$IMAGE\" echo yes" | tr -dc 'a-z')" = yes ] || { echo "[wintray] 没发布出来" >&2; exit 1; }
    # 先整份拷到旁边的 .new（这时旧的那一份还在跑，不碍事），拷全了再换上去：拷到一半失败的话，装着的那一份原样没动。
    # robocopy 退出码 ≥ 8 才是失败
    ssh_win "(if exist \"$APPDIR.new\" rd /s /q \"$APPDIR.new\") & robocopy \"$DEST\\.work\\publish\" \"$APPDIR.new\" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul & if errorlevel 8 (exit /b 1) else (exit /b 0)" \
      || { echo "[wintray] 拷不进 $APPDIR.new，没装（装着的那一份没动）" >&2; exit 1; }
    first=1; installed && first=0
    stop_running "$DEST\\.work\\publish\\$IMAGE" || { echo "[wintray] 正在跑的那一份结束不了，没装" >&2; exit 1; }
    # 换上去，一条远端命令做完（几步挨着做，停在半路的机会小；连接中途断了时远端做到了哪一步不好说，下面按看到的样子说）：
    # 旧的改名 .old → .new 改名成正式的 → 删 .old。
    # 哪一步不成都退回去，不留半新半旧的：3 = 旧的那一份挪不开（里面有文件被占着），4 = 新的换不上去（旧的已经挪回来），5 = 上次留下的 .old 删不掉
    swap="(if exist \"$APPDIR.old\" rd /s /q \"$APPDIR.old\")"
    swap+=" & if exist \"$APPDIR.old\" (exit /b 5) else ("
    swap+="(if exist \"$APPDIR\" move /y \"$APPDIR\" \"$APPDIR.old\" >nul 2>&1)"
    swap+=" & if exist \"$APPDIR\" (exit /b 3) else ("
    swap+="move /y \"$APPDIR.new\" \"$APPDIR\" >nul 2>&1"
    swap+=" & if exist \"$APPDIR\\$IMAGE\" (rd /s /q \"$APPDIR.old\" 2>nul & exit /b 0) else ((if exist \"$APPDIR.old\" move /y \"$APPDIR.old\" \"$APPDIR\" >nul 2>&1) & exit /b 4)))"
    rc=0; ssh_win "$swap" || rc=$?
    if [ "$rc" != 0 ]; then
      # 程序现在在不在原处：yes / no；连不上那台电脑时是空的
      at="$(ssh_win "if exist \"$APPDIR\\$IMAGE\" (echo yes) else (echo no)" 2>/dev/null | tr -dc 'a-z' || true)"
      if [ -z "$at" ]; then
        echo "[wintray] 换的时候连接断了（$rc），不知道停在哪一步；连得上之后重跑 install" >&2
      elif [ "$at" = no ] && [ "$first" = 0 ]; then
        # 旧的挪开了、新的没换上去、旧的也没挪回来：两份都留着不删（这时删 .new 的话程序就只剩 .old 里那一份了）
        echo "[wintray] 换到一半停了（$rc）：程序现在不在 $APPDIR，旧的那一份在 $APPDIR.old、新的在 $APPDIR.new；重跑 install" >&2
      else
        case "$rc" in
          3|4|5)    # 没挪过东西（3、5），或者新的换不上去、旧的挪回来了（4）：装着的那一份原样没动，清掉 .new
            ssh_win "(if exist \"$APPDIR.new\" rd /s /q \"$APPDIR.new\") & exit /b 0" >/dev/null 2>&1 || true
            echo "[wintray] 换不上去（$rc），没装" >&2 ;;
          *)        # 不是那条命令自己报的退出码（比如连接断了一下）：做到了哪一步不知道，不删东西
            echo "[wintray] 换的时候出了错（$rc），不知道换上去没有；重跑 install" >&2 ;;
        esac
        if [ "$first" = 0 ]; then
          # 程序目录里那一份刚才被停掉了：把它重新起来
          if desk traystart "start \"\" \"$APPDIR\\$IMAGE\"" && wait_running; then echo "[wintray] 原来那一份重新起来了" >&2
          else echo "[wintray] 原来那一份没能重新起来" >&2; fi
        fi
      fi
      exit 1
    fi
    if [ "$first" = 1 ]; then
      ssh_win "start /wait \"\" \"$APPDIR\\$IMAGE\" --autostart on $RC" || { echo "[wintray] 开机启动没设上" >&2; exit 1; }
      echo "[wintray] 头一次装：开机启动已打开"
    fi
    desk traystart "start \"\" \"$APPDIR\\$IMAGE\""
    wait_running || { echo "[wintray] 装好了但没起来；日志：" >&2; "$0" log installed >&2 || true; exit 1; }
    echo "[wintray] 装好了，在跑：$("$0" log installed | grep '启动' | tail -1)" ;;
  uninstall)
    installed || { echo "[wintray] 没装过"; exit 0; }
    stop_running "$APPDIR\\$IMAGE" || { echo "[wintray] 正在跑的那一份结束不了，没卸" >&2; exit 1; }
    ssh_win "start /wait \"\" \"$APPDIR\\$IMAGE\" --autostart off $RC" || echo "[wintray] 开机启动那一条没删掉" >&2
    ssh_win "rd /s /q \"$APPDIR\" & (if exist \"$APPDIR.new\" rd /s /q \"$APPDIR.new\") & (if exist \"$APPDIR.old\" rd /s /q \"$APPDIR.old\") & exit /b 0" >/dev/null
    installed && { echo "[wintray] 程序目录删不掉（有文件被占着？）：$APPDIR" >&2; exit 1; }
    echo "[wintray] 卸了（数据还在那台电脑的 $APPDATA_DIR）" ;;
  start)
    [ "${2:-}" = "--no-sync" ] || "$HERE/scripts/winsync.sh"
    if running; then echo "[wintray] 那台电脑上已经有一份托盘程序在跑，不再起" >&2; exit 1; fi
    desk traystart "start \"\" $EXE --data-dir $DATA"
    wait_running || { echo "[wintray] 没起来；日志：" >&2; "$0" log >&2 || true; exit 1; }
    echo "[wintray] 起来了" ;;
  stop)
    running || { echo "[wintray] 没有在跑"; exit 0; }
    if installed; then exe="$APPDIR\\$IMAGE"; else exe="$DEST\\$EXE"; fi
    stop_running "$exe" || { echo "[wintray] 结束不了" >&2; exit 1; }
    echo "[wintray] 退出了" ;;
  password)
    [ $# -eq 2 ] || { echo "用法：$0 password <密码>" >&2; exit 2; }
    case "$2" in ''|*[!A-Za-z0-9._:/=-]*) echo "[wintray] 经这个脚本给的密码只许字母、数字和 . _ : / = -" >&2; exit 2 ;; esac
    if running; then echo "[wintray] 先 stop" >&2; exit 1; fi
    # 它没有控制台，原因传不回来：没成的时候把它日志的最后几行打出来
    ssh_win "chcp 65001 >nul & cd /d \"$DEST\" & start /wait \"\" $EXE --data-dir $DATA --set-password $2 $RC" | utf8 \
      || { echo "[wintray] 密码没设上；日志：" >&2; "$0" log 2>/dev/null | tail -3 >&2 || true; exit 1; }
    echo "[wintray] 密码设了" ;;
  log)
    if [ "${2:-}" = installed ]; then file="$APPDATA_DIR\\tray.log"; else file="$DEST\\$DATA\\tray.log"; fi
    ssh_win "chcp 65001 >nul & if exist \"$file\" type \"$file\"" | utf8 ;;
  wipe)
    if running; then echo "[wintray] 先 stop" >&2; exit 1; fi
    ssh_win "if exist \"$DEST\\$DATA\" rd /s /q \"$DEST\\$DATA\"" >/dev/null
    echo "[wintray] 测试用的数据删了" ;;
  *)
    sed -n '2,19p' "$0"; exit 2 ;;
esac
