#!/usr/bin/env bash
# 把 windisplay/sender 经 tar-over-ssh 同步到 Windows 电脑，在那边编译、跑单元测试或者跑 wdsend。
# 主机地址 / 用户名怎么给见 scripts/winenv.sh。
# 用法：scripts/winsync.sh                    同步 + 编译
#       scripts/winsync.sh --no-build         只同步
#       scripts/winsync.sh --test [过滤条件]  同步 + 编译 + dotnet test（过滤条件原样给 --filter，比如 'DisplayName~取消'）
#       scripts/winsync.sh --run <参数...>    同步 + 编译，然后在 Windows 上跑 wdsend，参数原样给它（比如 --run 192.168.1.50 --pattern scroll）
# 远端目录固定 C:\_\src\WinDisplay。ssh 进去的是会话 0：单元测试和发测试画面的 wdsend 能跑，抓不了桌面——要抓屏的用 scripts/windesk.sh（起在桌面会话里）。
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
. "$HERE/scripts/winenv.sh"

echo "[winsync] 同步到 $DEST（镜像：本地删掉 / 改名的文件远端也删；保留 bin / obj 以利增量编译，保留远端的 .work）"
# 先解到一个全新的暂存目录，再 robocopy /MIR 到 DEST：直接解进 DEST 的话，本地删掉的源文件会一直留在远端、照样参与编译。
# robocopy 退出码 ≥ 8 才是失败
ssh_win "if exist \"$STAGE\" rd /s /q \"$STAGE\"" >/dev/null
ssh_win "md \"$STAGE\"" >/dev/null
# 测试向量（windisplay/testvectors/）在 sender/ 的外面，单元测试要对它：一起带过去，远端落在 sender 这一层的 testvectors\ 下
# （测试工程两个位置都找，见 tests/WinDisplay.Tests/WinDisplay.Tests.csproj）
tar czf - --exclude='*/bin' --exclude='*/obj' --exclude='./.work' --exclude='./scripts/wininfo.env' --exclude='.env' --exclude='*.local.json' \
    -C "$HERE" . -C "$HERE/.." testvectors \
  | ssh "${ssh_opts[@]}" "cd /d \"$STAGE\" && tar xzf -"
ssh_win "robocopy \"$STAGE\" \"$DEST\" /MIR /XD bin obj .work /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul & if errorlevel 8 (exit /b 1) else (rd /s /q \"$STAGE\")"
echo "[winsync] 本地 HEAD: $(git -C "$HERE" rev-parse --short HEAD)$(git -C "$HERE" diff --quiet -- . || echo '（工作区有没提交的改动）')；远端是 tar 副本，不是 git 仓库"

mode="${1:-}"
[ "$mode" = "--no-build" ] && exit 0
echo "[winsync] 远端编译（Release）"
ssh_win "chcp 65001 >nul && cd /d \"$DEST\" && dotnet build WinDisplay.slnx -c Release -nologo -v quiet" | utf8
case "$mode" in
  --test)
    echo "[winsync] 远端 dotnet test${2:+（过滤：$2）}"
    ssh_win "chcp 65001 >nul && cd /d \"$DEST\" && dotnet test WinDisplay.slnx -c Release -nologo -v quiet --no-build${2:+ --filter \"$2\"}" | utf8
    ;;
  --run)
    shift
    echo "[winsync] 远端 wdsend $*"
    ssh_win "chcp 65001 >nul && cd /d \"$DEST\" && dotnet src\\WinDisplay.Cli\\bin\\Release\\net10.0\\wdsend.dll $*" | utf8
    ;;
esac
