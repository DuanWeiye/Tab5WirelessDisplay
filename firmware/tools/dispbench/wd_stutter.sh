#!/usr/bin/env bash
# 无线显示的「卡顿」测试：不靠人开游戏，在 Windows 电脑上开一个全屏的测试场景（wdsend --scene：整屏都在动、每次是同一段动画、
# 显卡 / CPU 负载可调），同时用命令行发送端抓屏发给 Tab5（wdsend --screen -v，逐帧记录），跑完用 wd_stutter.py 找出停顿、
# 说明每一次停顿卡在哪一段（场景自己 / 系统没合成新画面 / 缩放读回 / 编码 / 等 Tab5 确认 / Tab5 接收、解码、上屏）。
# 对照：--pattern 不开场景、不抓屏，发生成的画面（只压链路和 Tab5）。
#
# 用法：wd_stutter.sh <日志前缀> [--seconds N] [--no-sync] [--pattern scroll|noise] [--gpu N] [--cpu N] [--vsync 1|0] [--detail D] [--idle S]
#                     [--send "<给 wdsend 的另外的参数>"]
#   例：wd_stutter.sh st1 --seconds 120                    场景不加负载
#       wd_stutter.sh st2 --seconds 120 --vsync 0 --gpu 40 场景不限帧率、显卡满载
#       wd_stutter.sh st4 --seconds 60 --gpu 10            量帧率用这个：场景不加负载时正好卡在系统合成的时刻提交，
#                                                          系统隔几帧就少交一幅给抓屏（每分钟几十到几百幅，会漂），帧率低一截，和发送端、Tab5 无关
#       wd_stutter.sh st3 --seconds 120 --pattern scroll   不抓屏，整屏滚动的生成画面
# 日志：logs/<前缀>_send.log（发送端逐帧）、_scene.log（场景）、_serial.log（Tab5 串口：结束时的 wd、wd gaps、hosted）；最后打印分析结果。
# 画面有没有停顿以 Tab5 自己量的上屏间隔为准（wd gaps）；测的期间不往串口发命令，不给 Tab5 添负载。
#
# 期间：日常用的托盘程序先停掉、测完起回来（本来没在跑就不起）；Tab5 回到主页面（暂停着的话点「开始显示」）并常亮，
# 测完取消常亮（不管测之前是不是常亮着），设备照常等电脑连，托盘自己连回来。
# 有人在用就不跑，什么都不动、退出码 4（--pattern 也查：停托盘会打断正在投屏的人；--idle 0 = 都不查）。两条都要过：
#   Tab5 上没有正在放会动的画面（隔 2 秒看两次 wd，帧数涨了 10 帧以上算在放：拿它当显示器看视频时键鼠是没人碰的）；
#   那台电脑的键鼠空闲满 --idle 秒（默认 60）。查不了空闲时间是退出码 5。
# 场景开着的时候点鼠标就关（按键也关，但场景是计划任务起的，不一定拿得到键盘焦点）。
# 独占 Tab5 的串口；别同时烧录、别另发串口命令。
set -uo pipefail
cd "$(dirname "$0")" && mkdir -p logs || exit 1
HERE="$(cd ../../../windisplay/sender && pwd)"
. "$HERE/scripts/winenv.sh"
. ../tab5_env.sh; tab5_need_port; tab5_need_ip; PORT=$TAB5_PORT
IP=$TAB5_IP
PY=${PY:-python3}
EXE='src\WinDisplay.Cli\bin\Release\net10.0-windows10.0.22621.0\wdsend.exe'
TRAY_EXE='%LOCALAPPDATA%\Programs\WinDisplay\WinDisplayTray.exe'

[ $# -ge 1 ] || { sed -n '2,16p' "$0"; exit 1; }
case "$1" in ''|*/*|-*) echo "日志前缀是 logs/ 下的文件名开头，不是路径也不是选项：$1" >&2; exit 1 ;; esac
P=logs/$1; shift
seconds=60; sync=1; pattern=; idle=60; scene_args=(); send_extra=()
while [ $# -gt 0 ]; do
    case "$1" in
        --seconds) seconds=$2; shift 2 ;;
        --no-sync) sync=0; shift ;;
        --pattern) pattern=$2; shift 2 ;;
        --gpu|--cpu|--vsync|--detail) scene_args+=("$1" "$2"); shift 2 ;;
        --idle) idle=$2; shift 2 ;;
        --send) read -r -a send_extra <<< "$2"; shift 2 ;;
        *) echo "不认识的参数：$1" >&2; exit 1 ;;
    esac
done
case "$seconds" in ''|*[!0-9]*) echo "--seconds 要一个整数" >&2; exit 1 ;; esac
case "$idle" in ''|*[!0-9]*) echo "--idle 要一个整数（秒）" >&2; exit 1 ;; esac
[ -z "$pattern" ] || [ ${#scene_args[@]} = 0 ] || { echo "--pattern（不开场景）和场景的选项不能一起给" >&2; exit 1; }
for a in "${scene_args[@]}" "${send_extra[@]}"; do
    case "$a" in *[!A-Za-z0-9._:/=-]*) echo "参数 $a 里有不能原样写进 cmd 脚本的字符" >&2; exit 1 ;; esac
done

SERLOG=${P}_serial.log
cmd() { printf '\n@@CMD %s\n' "$1" >> "$SERLOG"; printf '%s\n' "$1" > "$PORT"; }
tray_running() { ssh_win 'tasklist /fi "imagename eq WinDisplayTray.exe" /nh' 2>/dev/null | grep -qi WinDisplayTray.exe; }

# Tab5 上是不是正在放会动的画面：隔 2 秒发两次 wd，都是「显示中」而且帧数涨了 10 帧以上（桌面不动时一秒只有两三帧：光标、时钟）
tab5_playing() {
    local tmp n
    tmp="$(mktemp)"
    ./ser.sh "$tmp" 3.5 "0.3:wd" "2.3:wd" >/dev/null 2>&1
    mapfile -t n < <(grep -a 'wd：显示中' "$tmp" | sed -n 's/.*；\([0-9][0-9]*\) 帧 .*/\1/p')
    rm -f "$tmp"
    [ ${#n[@]} = 2 ] && [ $((n[1] - n[0])) -ge 10 ]
}

SER=; tray_was=0
finish() {
    trap - EXIT; trap '' INT TERM           # 收尾期间不让 Ctrl-C 打断
    # 场景还开着（比如按了 Ctrl-C）就关掉：按命令行找这次起的那个
    ssh_win "powershell -NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object { \$_.Name -eq 'wdsend.exe' -and \$_.CommandLine -like '*--scene*' } | ForEach-Object { taskkill /f /pid \$_.ProcessId }\"" >/dev/null 2>&1
    if [ -n "$SER" ]; then
        cmd "disp awake off"; /usr/bin/sleep 1
        kill "$SER" 2>/dev/null
    fi
    if [ "$tray_was" = 1 ] && ! tray_running; then
        desk traystart "start \"\" \"$TRAY_EXE\"" || echo "托盘程序没起回来，要手动起" >&2
    fi
    trap - INT TERM                         # 收尾做完了：后面的分析可以照常被 Ctrl-C 打断
}
trap finish EXIT
trap 'exit 130' INT TERM

if [ "$sync" = 1 ]; then
    synclog="$(mktemp)"
    "$HERE/scripts/winsync.sh" > "$synclog" 2>&1 || { tail -5 "$synclog"; rm -f "$synclog"; echo "同步 / 编译没成" >&2; exit 1; }
    rm -f "$synclog"
fi

# 那台电脑上有人在用就不跑：要停托盘（正在投屏的话画面会断）、要换 Tab5 的页面，场景还要占整个屏幕
if [ "$idle" != 0 ]; then
    if tab5_playing; then
        echo "不跑：Tab5 上正在放会动的画面（有人在看）" >&2
        trap - EXIT; exit 4
    fi
    ssh_win "del /q \"$DEST\\.work\\idle.log\" \"$DEST\\.work\\idle.rc\" 2>nul & exit /b 0" >/dev/null
    desk idle "$EXE --scene --check-idle --idle $idle > .work\\idle.log 2>&1" "echo %errorlevel% > .work\\idle.rc" || exit 125
    rc=
    for _ in $(seq 10); do
        rc="$(ssh_win "if exist \"$DEST\\.work\\idle.rc\" type \"$DEST\\.work\\idle.rc\"" 2>/dev/null | tr -dc '0-9-')"
        [ -n "$rc" ] && break
        /usr/bin/sleep 0.5
    done
    if [ "$rc" != 0 ]; then
        ssh_win "chcp 65001 >nul & type \"$DEST\\.work\\idle.log\"" 2>/dev/null | utf8 >&2
        trap - EXIT
        [ "$rc" = 4 ] && { echo "不跑：那台电脑上有人在用" >&2; exit 4; }
        echo "不跑：查不了那台电脑的空闲时间（退出码 ${rc:-没有}）" >&2
        exit 5
    fi
fi

# 托盘程序在跑就先停（它占着 Tab5 的那一个连接位置）
if tray_running; then
    tray_was=1
    "$HERE/scripts/wintray.sh" stop >/dev/null 2>&1; tray_running && "$HERE/scripts/wintray.sh" stop >/dev/null 2>&1
    tray_running && { echo "托盘程序停不掉" >&2; exit 1; }
fi

./ser.sh "$SERLOG" 100000 > /dev/null 2>&1 &
SER=$!
/usr/bin/sleep 1
cmd "ver"; /usr/bin/sleep 1
# wd start = 点主页面上的「开始显示」：只有暂停着才点得到，没暂停时打一行提示、什么都不变
cmd "disp awake on"; /usr/bin/sleep 0.5; cmd "open wdisp"; /usr/bin/sleep 3; cmd "wd start"; /usr/bin/sleep 1

if [ -z "$pattern" ]; then
    ssh_win "del /q \"$DEST\\.work\\scene.log\" \"$DEST\\.work\\scene.rc\" 2>nul & exit /b 0" >/dev/null
    desk scene "$EXE --scene --seconds $((seconds + 12)) --idle $idle ${scene_args[*]} > .work\\scene.log 2>&1" "echo %errorlevel% > .work\\scene.rc" || exit 125
    /usr/bin/sleep 3
    rc="$(ssh_win "if exist \"$DEST\\.work\\scene.rc\" type \"$DEST\\.work\\scene.rc\"" 2>/dev/null | tr -dc '0-9-')"
    if [ -n "$rc" ]; then      # 3 秒就退出了：没开起来
        ssh_win "chcp 65001 >nul & type \"$DEST\\.work\\scene.log\"" | utf8 > "${P}_scene.log"; cat "${P}_scene.log"
        echo "场景没开起来（退出码 $rc；4 = 那台电脑上有人在用）" >&2; exit "${rc:-1}"
    fi
    send_args=("$IP" --screen -v --seconds "$seconds")
else
    send_args=("$IP" --pattern "$pattern" --frames 0 -v --seconds "$seconds")
fi

# 那台电脑开机以来重传过多少个 TCP 段（全机的数，不只这条连接）：测试前后各读一次，差就是这期间重传的——画面是电脑发给 Tab5 的，
# 这个数大致就是「电脑发出去的数据在路上丢了多少次」
resent() { ssh_win 'powershell -NoProfile -Command "[System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetTcpIPv4Statistics().SegmentsResent"' 2>/dev/null | tr -dc '0-9'; }
resent0=$(resent)
"$HERE/scripts/windesk.sh" --timeout $((seconds + 40)) --no-sync "${send_args[@]}" "${send_extra[@]}" > "${P}_send.log" 2>&1
echo "rc=$?" >> "${P}_send.log"
resent1=$(resent)
[ -n "$resent0" ] && [ -n "$resent1" ] && echo "这期间那台电脑重传了 $((resent1 - resent0)) 个 TCP 段（全机的数）" >> "${P}_send.log"
# Tab5 自己量的上屏间隔：下一条连接进来就清零了，所以要赶在托盘起回来之前问
/usr/bin/sleep 1; cmd wd; /usr/bin/sleep 0.3; cmd "wd gaps"; /usr/bin/sleep 0.5; cmd hosted

if [ -z "$pattern" ]; then
    for _ in $(seq 20); do
        rc="$(ssh_win "if exist \"$DEST\\.work\\scene.rc\" type \"$DEST\\.work\\scene.rc\"" 2>/dev/null | tr -dc '0-9-')"
        [ -n "$rc" ] && break
        /usr/bin/sleep 1
    done
    ssh_win "chcp 65001 >nul & type \"$DEST\\.work\\scene.log\"" | utf8 > "${P}_scene.log"
    echo "rc=${rc:-没跑完}" >> "${P}_scene.log"
fi
/usr/bin/sleep 1
finish
if [ -z "$pattern" ]; then $PY wd_stutter.py "${P}_send.log" --scene "${P}_scene.log" --serial "$SERLOG"
else $PY wd_stutter.py "${P}_send.log" --serial "$SERLOG"; fi
