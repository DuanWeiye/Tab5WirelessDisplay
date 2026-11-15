# 由 winsync.sh / windesk.sh / wintray.sh / traysnap.sh 用 . 引入：读 Windows 电脑的连接信息，定义经 ssh 在那边跑命令的几个函数。调用方先设好 HERE（发送端工程的根目录）。
# 主机地址 / 用户名不入库：用环境变量给，或者写在 scripts/wininfo.env 里（本目录，不入库）：WIN_HOST / WIN_USER / SSH_KEY。
for f in "$HERE/scripts/wininfo.env"; do
  if [ -f "$f" ]; then
    # 文件里没有某一项时 grep 失败，不加 || true 的话脚本在这里就悄悄退出了，走不到下面「缺 …」的提示
    WIN_HOST="${WIN_HOST:-$(grep -E '^WIN_HOST=' "$f" | cut -d= -f2- || true)}"
    WIN_USER="${WIN_USER:-$(grep -E '^WIN_USER=' "$f" | cut -d= -f2- || true)}"
    SSH_KEY="${SSH_KEY:-$(grep -E '^SSH_KEY=' "$f" | cut -d= -f2- || true)}"
    break
  fi
done
: "${WIN_HOST:?缺 WIN_HOST}" "${WIN_USER:?缺 WIN_USER}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/id_ed25519}"
SSH_KEY="${SSH_KEY/#\~/$HOME}"
DEST='C:\_\src\WinDisplay'
STAGE='C:\_\src\WinDisplay.stage'
ssh_opts=(-i "$SSH_KEY" -o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new "$WIN_USER@$WIN_HOST")
ssh_win() { ssh -n "${ssh_opts[@]}" "$@"; }             # 不读标准输入：不然会把调用方的输入吞掉，个别时候还会卡住不返回
# 在远端跑没有控制台的程序（WinDisplayTray.exe）并要它的退出码时，命令写成：start /wait "" <程序> <参数> $RC
#   start /wait：这种程序 cmd 默认不等它跑完；
#   $RC：start /wait 把程序的退出码放进了 errorlevel，可 cmd /c 自己的退出码并不跟着它（实测总是 0），要接这一句才传得回 ssh 这边（0 = 成了，不是 0 的都传成 1）。
#   「if errorlevel 1」是「≥ 1」：程序因为没人接的异常结束时退出码是负的（.NET 是 0xE0434352），所以负的另外判一次，不然会当成成了。
RC='& if errorlevel 1 (exit /b 1) else if not errorlevel 0 (exit /b 1) else (exit /b 0)'
# 远端的输出按 UTF-8 收（先切代码页）；iconv 不转码，只滤掉非法字节
utf8() { iconv -f UTF-8 -t UTF-8 -c; }

# ssh 进去的是会话 0，没有桌面。要桌面的（托盘图标、抓屏、注入输入、照真显示器的缩放比例画窗口）经一次性的计划任务（/it）起在登录用户的桌面会话里。
# task_run <任务名> <远端 cmd 脚本的路径>：建这样一个任务并跑起来（用 conhost --headless 起，桌面上不出窗口）。输出是远端 schtasks / powershell 的话。
# 计划任务默认的优先级是 7：它起的进程、连同这些进程再起的，都是「低于正常」优先级（实测 PriorityClass = BelowNormal），
# 机器一忙抓屏和编码就让给别的程序，量出来的帧率偏低；从这里起的托盘程序也会一直这样跑到下次登录。所以建完先改成 5（普通）再跑。
task_run() {
  ssh_win "chcp 65001 >nul & schtasks /create /tn $1 /tr \"conhost.exe --headless $2\" /sc once /st 00:00 /it /rl limited /f >nul 2>&1 && powershell -NoProfile -Command \"\$t = Get-ScheduledTask -TaskName $1; \$t.Settings.Priority = 5; \$t | Set-ScheduledTask | Out-Null\" && schtasks /run /tn $1"
}
# desk <名字> <cmd 脚本的正文...>：在桌面会话里跑一段 cmd 脚本，等它开始跑（.up 标记）；起不来返回 125
desk() {
  local name="$1" task="WinDisplay$1$$" tmp; shift
  tmp="$(mktemp -d)"
  { printf '@echo off\r\nchcp 65001 >nul\r\ncd /d "%s"\r\necho up > .work\\%s.up\r\n' "$DEST" "$name"; printf '%s\r\n' "$@"; } > "$tmp/$name.cmd"
  ssh_win "if not exist \"$DEST\\.work\" md \"$DEST\\.work\"" >/dev/null
  ssh_win "del /q \"$DEST\\.work\\$name.up\" \"$DEST\\.work\\$name.rc\" 2>nul & exit /b 0" >/dev/null
  tar cf - -C "$tmp" "$name.cmd" | ssh "${ssh_opts[@]}" "cd /d \"$DEST\\.work\" && tar xf -"
  rm -rf "$tmp"
  local said
  if ! said="$(task_run "$task" "$DEST\\.work\\$name.cmd" 2>&1)"; then
    printf '%s\n' "$said" | utf8 >&2; echo "[desk] 计划任务起不来" >&2
    ssh_win "schtasks /delete /tn $task /f >nul 2>&1 & exit /b 0" >/dev/null 2>&1 || true     # 可能已经建出来了（停在改优先级 / 跑那一步）：别留在那台电脑上
    return 125
  fi
  local up=''
  for _ in $(seq 10); do
    up="$(ssh_win "if exist \"$DEST\\.work\\$name.up\" type \"$DEST\\.work\\$name.up\"" 2>/dev/null | tr -dc 'a-z' || true)"
    [ -n "$up" ] && break
    /usr/bin/sleep 1
  done
  ssh_win "schtasks /delete /tn $task /f >nul 2>&1 & exit /b 0" >/dev/null
  [ -n "$up" ] || { echo "[desk] 任务没跑起来（那台电脑上没有人登录着？）" >&2; return 125; }
}
