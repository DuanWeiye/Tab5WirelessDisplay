#!/usr/bin/env bash
# 看托盘程序的界面长什么样：同步 + 编译（winsync.sh）→ 在 Windows 电脑上跑 WinDisplayTray.exe --snapshot → 把画出来的 PNG 取回来。
# --snapshot 只把程序自己的窗口（设置窗口的几种局面、几个对话框、托盘图标）画成图片：不连 Tab5、不抓屏、不碰存的配对和密码、不截桌面。
# 默认经 ssh 跑（会话 0，不用桌面）：那里的缩放是 100%。
# --desk：起在登录用户的桌面会话里，照真显示器的缩放比例画（那台是 125%）。窗口会在那台电脑的屏幕上闪一下、抢一下焦点，有人在用的时候别跑。
# 两种都只画窗口里面，标题栏不在图里（真跑起来是深色的）。
# 用法：scripts/traysnap.sh [--no-sync] [--desk] [取回到哪个目录，默认 .work/snap]
# 取回来的是 settings_*.png、dialog_*.png、tray_icons*.png。目录里上一次取回来的这几种图先删掉；别的东西不碰。
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
. "$HERE/scripts/winenv.sh"
sync=1; desk=0
while [ $# -gt 0 ]; do
  case "$1" in
    --no-sync) sync=0; shift ;;
    --desk) desk=1; shift ;;
    *) break ;;
  esac
done
out="${1:-$HERE/.work/snap}"
[ "$sync" = 1 ] && "$HERE/scripts/winsync.sh"
EXE='src\WinDisplay.Tray\bin\Release\net10.0-windows10.0.22621.0\WinDisplayTray.exe'
ssh_win "cd /d \"$DEST\" & (if exist .work\\snap rd /s /q .work\\snap) & exit /b 0" >/dev/null
if [ "$desk" = 1 ]; then
  desk traysnap "$EXE --snapshot .work\\snap" "echo done > .work\\traysnap.rc"
  drawn=''
  for _ in $(seq 60); do
    # || true：60 次里有一次 ssh 偶然没连上的话，这条赋值失败会让整个脚本在这里一声不吭地退出（set -e）
    drawn="$(ssh_win "if exist \"$DEST\\.work\\traysnap.rc\" echo yes" 2>/dev/null | tr -dc 'a-z' || true)"
    [ -n "$drawn" ] && break
    /usr/bin/sleep 1
  done
  [ -n "$drawn" ] || { echo "[traysnap] 等了 60 秒还没画完" >&2; exit 1; }
else
  timeout 90 ssh -n "${ssh_opts[@]}" "cd /d \"$DEST\" & start /wait \"\" $EXE --snapshot .work\\snap $RC" >/dev/null \
    || { echo "[traysnap] 没画出来" >&2; exit 1; }
fi
# 只删这个脚本自己取回来的那几种图：$out 是调用方给的目录，里面可能有别的东西（给的是 . 或者 ~/Pictures 也不能出事）
mkdir -p "$out"
rm -f "$out"/settings_*.png "$out"/dialog_*.png "$out"/tray_icons*.png
timeout 60 ssh "${ssh_opts[@]}" "cd /d \"$DEST\\.work\\snap\" && tar cf - ." < /dev/null | tar xf - -C "$out"
echo "[traysnap] 取回到 $out："
ls "$out"
