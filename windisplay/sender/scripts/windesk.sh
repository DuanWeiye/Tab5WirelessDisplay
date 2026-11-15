#!/usr/bin/env bash
# 在 Windows 电脑上、登录用户的桌面会话里跑 Windows 版的 wdsend。抓屏只能在那里跑：ssh 进去的是会话 0，没有桌面。
# 做法：先同步 + 编译（winsync.sh）→ 建一个一次性的计划任务（/it = 在登录着的用户的会话里跑；见 winenv.sh 的 task_run）→ 用 conhost --headless 起，桌面上不出窗口
# → 输出写到远端 .work\ 下 → 跑完把日志取回来打印 → 删掉任务。那台电脑上要有人登录着（锁屏也算登录着）。
# 用法：scripts/windesk.sh [--timeout 秒] [--no-sync] <wdsend 的参数...>
#   scripts/windesk.sh --screen-check                         核对那台电脑的抓屏
#   scripts/windesk.sh 192.168.1.50 --screen --seconds 20                      把它的屏幕发给 Tab5 20 秒（配对过才连得上）
#   scripts/windesk.sh --timeout 180 192.168.1.50 --screen --password 2468     第一次：配对（在 Tab5 上输同一个密码），然后发屏幕
#   （参数里只许字母、数字和 . _ : / = -，所以经这个脚本给的配对密码也只能用这些字符）
# 退出码 = wdsend 的退出码。超过时限（默认 120 秒）还没跑完：结束它，退出码 124。任务没跑起来：退出码 125。
# 这个脚本不管怎么退出（跑完、超时、按 Ctrl-C、被 kill），都先把那边这次起的 wdsend 结束掉再走：
# 留一个没人管的 wdsend 在那边接着发屏幕，是最不该出的事。给了 --screen 没给 --seconds 时自动补一个比时限短 10 秒的，
# 让它到点自己发 BYE 正常收尾，而不是等到时限被硬结束。
# 做不到的两种情况：这个脚本被 kill -9（来不及收尾）、收尾时连不上那台电脑（会说一句）。这时候只能靠 --seconds 到点自己停，
# 所以自己给 --seconds 的时候别给得太大。
# 计划任务和远端文件的名字都带这次运行的编号，几次同时跑互不相干。
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
. "$HERE/scripts/winenv.sh"
RUN="$(date +%H%M%S)$$"                     # 这次运行的编号：只有数字
TASK="WinDisplayDesk$RUN"
BASE="desk$RUN"                             # 远端 .work\ 下这次用的文件：$BASE.cmd / .log / .rc / .up
EXE='src\WinDisplay.Cli\bin\Release\net10.0-windows10.0.22621.0\wdsend.exe'
timeout=120
sync=1
while [ $# -gt 0 ]; do
  case "$1" in
    --timeout)
      [ $# -ge 2 ] || { echo "[windesk] --timeout 后面缺秒数" >&2; exit 1; }
      case "$2" in ''|*[!0-9]*) echo "[windesk] --timeout 要一个整数秒数，不是 $2" >&2; exit 1 ;; esac
      timeout=$((10#$2))                    # 写明按十进制：不然带前导 0 的（080、0120）在后面的算术里会被当成八进制
      [ "$timeout" -ge 15 ] || { echo "[windesk] --timeout 至少 15 秒" >&2; exit 1; }
      shift 2 ;;
    --no-sync) sync=0; shift ;;
    *) break ;;
  esac
done
[ $# -gt 0 ] || { sed -n '2,10p' "$0"; exit 1; }
screen=0; has_seconds=0
for a in "$@"; do
  case "$a" in *[!A-Za-z0-9._:/=-]*) echo "[windesk] 参数 $a 里有不能原样写进 cmd 脚本的字符" >&2; exit 1 ;; esac
  case "$a" in --screen) screen=1 ;; --seconds) has_seconds=1 ;; esac
done
[ "$screen" = 1 ] && [ "$has_seconds" = 0 ] && set -- "$@" --seconds $((timeout - 10))

[ "$sync" = 1 ] && "$HERE/scripts/winsync.sh"

tmp="$(mktemp -d)"
started=0
# 结束这次起的进程的那条远端命令：找出命令行里带着这次那个 cmd 脚本名字的进程（conhost 和它起的 cmd），连同底下的 wdsend 整棵树结束。
# 不用 schtasks /end：实测它只结束 conhost，底下的 cmd 和 wdsend 照样活着。也不按映像名杀（taskkill /im wdsend.exe）：会误伤那台电脑上别的 wdsend。
# 脚本名在这条命令里是拆开写的（'desk' + '编号'）：不然跑这条命令的 cmd 和 powershell 自己的命令行里也带着它，会把自己算进去。
KILL="powershell -NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like ('*' + 'desk' + '$RUN' + '.cmd*') } | ForEach-Object { taskkill /f /t /pid \$_.ProcessId }\""
# 收尾。顺序有讲究：先删任务和那个 cmd 脚本，再结束进程，最后删其余文件。
# 任务刚 /run、进程还没起来的时候来收尾的话，先结束进程会扑空（找进程的那一刻它还不在），随后它起来就没人管了；
# 先把脚本删掉，还没起来的就跑不成，已经起来的在下一步被找到。日志文件被 wdsend 占着的时候删不掉，所以结束进程之后再删一遍。
cleanup() {
  trap - EXIT
  trap '' INT TERM                          # 收尾只有几秒，这期间不理 Ctrl-C：做一半就走的话，那边的 wdsend 就留下了
  if [ "$started" = 1 ]; then
    # 那个 cmd 脚本在这里写成 $BASE.c*：这条命令里不能出现它的全名，不然 KILL 会把跑这条命令的 cmd 自己也找出来结束掉
    if ! ssh_win "schtasks /delete /tn $TASK /f & del /q \"$DEST\\.work\\$BASE.c*\" 2>nul & $KILL & del /q \"$DEST\\.work\\$BASE.*\" 2>nul & exit /b 0" >/dev/null 2>&1; then
      echo "[windesk] 收尾时连不上那台电脑：没能确认那边这次起的 wdsend 已经结束（任务 $TASK）" >&2
    fi
  fi
  rm -rf "$tmp"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# 要跑的命令写成一个 cmd 脚本传过去（计划任务的命令行最长 261 个字符，直接写不下）。
# 一开始先写一个「起来了」的标记，这边靠它知道任务真的跑起来了；跑完把退出码写进 .rc，这边靠它知道跑完了
printf '@echo off\r\nchcp 65001 >nul\r\ncd /d "%s"\r\necho up > .work\\%s.up\r\n%s %s > .work\\%s.log 2>&1\r\necho %%errorlevel%% > .work\\%s.rc\r\n' \
  "$DEST" "$BASE" "$EXE" "$*" "$BASE" "$BASE" > "$tmp/$BASE.cmd"
ssh_win "if not exist \"$DEST\\.work\" md \"$DEST\\.work\"" >/dev/null
tar cf - -C "$tmp" "$BASE.cmd" | ssh "${ssh_opts[@]}" "cd /d \"$DEST\\.work\" && tar xf -"

echo "[windesk] 在桌面会话里跑：wdsend $*（最多 $timeout 秒）"
started=1
if ! task_run "$TASK" "$DEST\\.work\\$BASE.cmd" > "$tmp/task.log" 2>&1; then
  utf8 < "$tmp/task.log" >&2
  echo "[windesk] 计划任务起不来" >&2
  exit 125
fi

# 远端有没有这次的某个文件；有就把内容打出来
remote_file() { ssh_win "if exist \"$DEST\\.work\\$BASE.$1\" type \"$DEST\\.work\\$BASE.$1\"" 2>/dev/null || true; }

# /it 的任务在没有人登录着的时候，/run 照样报成功，但其实不会跑：等 10 秒看「起来了」的标记
up=''
for _ in $(seq 10); do
  up="$(remote_file up | tr -dc 'a-z')"
  [ -n "$up" ] && break
  /usr/bin/sleep 1
done
if [ -z "$up" ]; then
  echo "[windesk] 任务没跑起来（那台电脑上没有人登录着？）" >&2
  exit 125
fi

rc=''
deadline=$((SECONDS + timeout))
while [ $SECONDS -lt $deadline ]; do
  rc="$(remote_file rc | tr -dc '0-9-')"
  [ -n "$rc" ] && break
  /usr/bin/sleep 1
done
if [ -z "$rc" ]; then
  ssh_win "$KILL" >/dev/null 2>&1 || true
  rc=124
  echo "[windesk] $timeout 秒还没跑完，已经结束它" >&2
fi
ssh_win "chcp 65001 >nul & if exist \"$DEST\\.work\\$BASE.log\" type \"$DEST\\.work\\$BASE.log\"" | utf8 || true
[ "$rc" != 0 ] && echo "[windesk] wdsend 的退出码 $rc" >&2
exit "$rc"
