#!/bin/bash
# 无线显示接收端的真机回归：改了 main/wdisp.cpp、main/utils/hwjpeg.cpp、main/app.cpp、界面（main/ui/）或息屏管理之后跑一遍。
# 约 13 分钟，期间独占 Tab5 的串口和 7250 端口（别同时烧录、别另发串口命令）。期间屏幕常亮（息屏时 open 命令会被拒），
# 只有第 2 段（暂停着息屏再唤醒）和 3b 那一段有意让它息屏。跑完或按 Ctrl-C 中止后设备停在主页面、照常等电脑连、恢复照常息屏。
# 这台设备开机就在等电脑连，和界面在哪一页无关；不想让它接受连接是「暂停」（密码框上点取消、退出手势、调试命令 wd pause），
# 接着等是点主页面上的「开始显示」（调试命令 wd start，主页面要在前台）。
# 开头会把 Tab5 上配对过的电脑全部移除、发送端这边（windisplay/tools/.wdpair_sender.json）的配对也清掉，重新配对（密码见 PW）；
# 跑完之后两边是配对好的。密码框上的操作用调试命令 wd pw / wd pwcancel 代替手指（走的是同一条界面路径：
# wd pw 是在屏幕软键盘上一个键一个键点出来再点「确定」）。
#
# 用法：wd_regress.sh <日志前缀>        例：wd_regress.sh wdisp6  →  logs/wdisp6_*.log
# 前提：Tab5 已烧好要测的固件并且联网（地址用环境变量 TAB5_IP 给，或者写在 tools/local.env 里，见 tools/tab5_env.sh）。
#       本机回环的自测（windisplay/tools/wd_selftest.py）不在这里面，另外跑。
# 结果：最后逐项打印「符合 / 不符」，有不符的退出码 1；依据在各个日志里。按 Ctrl-C 中止的退出码 130，不出判定。
# 测不到的：真手指的触摸（触摸芯片读出来对不对；之后的路用调试命令 wd touch 走到了）和退出手势、画面有没有撕裂、
#           密码框长什么样（要人看）；密码框 2 分钟没人输（太久，单独试）。
cd "$(dirname "$0")" && mkdir -p logs || exit 1
[ -n "$1" ] || { echo "用法：$0 <日志前缀>"; exit 2; }
P=logs/$1
. ../tab5_env.sh; tab5_need_port; tab5_need_ip; PORT=$TAB5_PORT
PY=${PY:-python3}                 # 要有 numpy 和 PIL；用别的解释器时设 PY（环境变量，或者写在 tools/local.env 里）
T=../../../windisplay/tools
IP=$TAB5_IP
SERLOG=${P}_serial.log

cmd() { printf '\n@@CMD %s\n' "$1" >> "$SERLOG"; printf '%s\n' "$1" > "$PORT"; }
send() {    # send <日志名> <wd_send_test.py 的参数...>
    local log=${P}_$1.log; shift
    $PY $T/wd_send_test.py "$IP" "$@" > "$log" 2>&1; echo "rc=$?" >> "$log"
}
wait_line() {   # wait_line <最多等几秒> <串口日志里要等的字样>
    for _ in $(seq "$1"); do grep -q "$2" "$SERLOG" && return 0; sleep 1; done
    return 1
}
PW=2468                         # 配对密码
# 另一个配对密码：空格和全部 32 个标点都有（软键盘的小写 / 大写 / 符号三页都要翻到），放在中间一次配对上用
PW_SYM='Aa1 ~^|`\{}[]_-:;"@#$%&*+=<>?!/.,()'"'"
mark() { wc -l < "$SERLOG"; }   # 串口日志现在到第几行（各段测试用它切出自己那一段）
wait_after() {  # wait_after <行号> <最多等几秒> <字样>：等串口日志在那一行之后出现这个字样
    for _ in $(seq $(($2 * 5))); do tail -n +"$(($1 + 1))" "$SERLOG" | grep -aq -- "$3" && return 0; sleep 0.2; done
    return 1
}
type_pw() {     # type_pw <行号> <等到哪个字样再输> <密码 | cancel>：替人在密码框上操作。先用 wd box 记下框的样子
    wait_after "$1" 20 "$2" || return 1
    sleep 1; cmd "wd box"; sleep 0.5
    if [ "$3" = cancel ]; then cmd "wd pwcancel"; else cmd "wd pw $3"; fi
}

# 跑完和中途退出都走这里：收掉还在跑的子进程，设备回主页面、接着等电脑连、取消常亮，停止抓串口
SER=; SP=; BG=
CONFIRM_OPEN=   # 3b-(3) 的确认框这会儿开着（非空）：中途退出时要把它关掉
SCROFF_WAS=     # 3b 那一项改的「息屏时间」原来是多少秒：测完、中途退出都改回去。空 = 没改过
TPAD_WAS=       # 跑之前 Tab5 设置页上的「触摸板模式」是开是关（1d 那一项要拨它）：测完、中途退出都拨回去。空 = 还没走到那一项
ACCEL_WAS=      # 同上，「触摸板加速」原来是哪一档（0~3）
PIC_WAS=        # 同上，1e 那一项拨的「画质」原来是第几档（0~3）
OSD_WAS=        # 同上，1f 那一项拨的调试信息浮层（主页面上叫「性能信息」）原来是 on 还是 off
tpad_restore() {    # 1d、1e、1f 拨过的几样（触摸板模式的开关、触摸板加速的档位、画质的档位、调试信息的开关）拨回原样
    [ -z "$TPAD_WAS" ] || cmd "wd tpad $TPAD_WAS"; TPAD_WAS=
    [ -z "$ACCEL_WAS" ] || cmd "wd tpad accel $ACCEL_WAS"; ACCEL_WAS=
    [ -z "$PIC_WAS" ] || cmd "wd pic $PIC_WAS"; PIC_WAS=
    [ -z "$OSD_WAS" ] || cmd "wd osd $OSD_WAS"; OSD_WAS=
}
scroff_restore() { [ -z "$SCROFF_WAS" ] || cmd "scroff $SCROFF_WAS"; SCROFF_WAS=; }
finish() {
    trap - EXIT
    trap '' INT             # 收尾只有几秒，这期间按 Ctrl-C 不理：做一半就退出的话，设备停在常亮、抓串口的进程也留着
    kill $SP $BG 2>/dev/null
    tpad_restore; scroff_restore
    # 中途退出时确认框还开着的话关掉它：息屏时顶层的弹框都会被清掉，所以息一下屏（下面的 disp awake on 再叫醒）
    [ -z "$CONFIRM_OPEN" ] || { cmd "disp awake on"; sleep 2; cmd o; sleep 2; }
    # 中途退出时可能息着屏（3b）、暂停着：先叫醒（disp awake on 顺带唤醒），回主页面点「开始显示」（没暂停时这一下点不到，只打一行提示）
    cmd "prof off"; cmd "disp awake on"; sleep 2; cmd "open wdisp"; sleep 3; cmd "wd start"; sleep 1; cmd "disp awake off"; sleep 1
    kill $SER 2>/dev/null; pkill -f "cat $PORT" 2>/dev/null
}
trap finish EXIT
# 按 Ctrl-C：前台的发送端收到同一个信号，自己收尾后退出（退出码 130）；脚本等它退出后在这里停下，不接着跑后面的项、不出判定
trap 'exit 130' INT

# ── 0. 常亮并唤醒；连开两次主页面（两个页面实例短暂并存）；上一次留下暂停着的话点「开始显示」。之后应在等连接 ──
./ser.sh "$SERLOG" 3000 "1:disp awake on" "3:open wdisp" "6:open wdisp" "9:wd start" &
SER=$!
sleep 12
cmd wd; sleep 1

# ── 0b. 配对：两边都先忘掉对方。Tab5 弹出密码框，先输错一次（标红、清空、重输），再输对 ──
cmd "wd forget all"; sleep 1
PAIR_L0=$(mark)
( type_pw "$PAIR_L0" '配对：等人输密码' 0000 && type_pw "$PAIR_L0" '上一次输的不对' $PW ) &
BG=$!
send pair --forget --password $PW --frames 60
wait $BG; BG=; sleep 2

# ── 0c. 密码框上点取消：Tab5 发 BYE（明文）、暂停（不再监听，主页面上露出「开始显示」）。发送端这边忘了配对而 Tab5 还留着，标题应是「重新配对」──
CANCEL_L0=$(mark)
( type_pw "$CANCEL_L0" '配对：等人输密码' cancel ) &
BG=$!
send cancel --forget --password $PW --frames 60
wait $BG; BG=; sleep 2; cmd wd; sleep 1; cmd "wd box"; sleep 0.5; cmd "wd page"; sleep 1

# ── 0c2. 密码输完了、Tab5 还在核对的两三秒里点取消：核对完不再把 PAIR_REPLY 发出去，直接 BYE ──
# 上一项点了取消、暂停着：先点「开始显示」
cmd "wd start"; sleep 2
BUSY_L0=$(mark)
( wait_after "$BUSY_L0" 20 '配对：等人输密码' && { sleep 1; cmd "wd pw $PW"; sleep 0.7; cmd "wd pwcancel"; } ) &
BG=$!
send busycancel --forget --password $PW --frames 60
wait $BG; BG=; sleep 2

# ── 0d. 密码框开着时发送端没了（被杀掉）：框要关掉。再来一次：界面停在 WiFi 页上，密码框照样弹出来（它挂在顶层，不属于哪一页）；
# 框开着时换页（回主页面），框原样留着、还是同一次询问，输对配上 ──
cmd "wd start"; sleep 2
GONE_L0=$(mark)
( wait_after "$GONE_L0" 20 '配对：等人输密码' && { sleep 1; cmd "wd box"; } ) &
BG=$!
timeout -s KILL 5 $PY $T/wd_send_test.py "$IP" --password $PW --frames 60 > "${P}_gone.log" 2>&1
wait $BG; BG=; sleep 3; cmd "wd box"; sleep 1
REPAIR_L0=$(mark)
cmd "open wifi"; sleep 3; cmd open; sleep 0.5
( wait_after "$REPAIR_L0" 20 '配对：等人输密码' && { sleep 1; cmd "wd box"; sleep 0.5; cmd "open wdisp"; sleep 3; cmd "wd box"; sleep 0.5; cmd "wd pw $PW"; } ) &
BG=$!
send repair --password $PW --frames 60
wait $BG; BG=; sleep 2

# ── 0e. 显示中在 Tab5 上移除这台电脑的配对：那条连接以 BYE 结束。之后电脑带着旧密钥来：没给密码的认证不过；给了密码的重新配对 ──
LIVE_L0=$(mark)
( wait_after "$LIVE_L0" 20 '握手走完' && { sleep 3; cmd "wd forget 1"; } ) &
BG=$!
timeout 60 $PY $T/wd_send_test.py "$IP" --frames 100000 --fps 30 > "${P}_live.log" 2>&1; echo "rc=$?" >> "${P}_live.log"
wait $BG; BG=; sleep 2
send unknown --frames 30; sleep 2
# 密码框开着时在 Tab5 上全部移除，再把密码输对：握手走得完，但这次配出来的不存（人刚移除的不能被存回来），连接以 BYE 结束
WIPE_L0=$(mark)
( wait_after "$WIPE_L0" 20 '配对：等人输密码' && { sleep 1; cmd "wd forget all"; sleep 0.5; cmd "wd pw $PW"; } ) &
BG=$!
send wipe --password $PW --frames 60
wait $BG; BG=; sleep 2; cmd "wd pairs"; sleep 1
# 重新配对，密码用带符号的那个：在软键盘上一个键一个键点出来
NEW_L0=$(mark)
( type_pw "$NEW_L0" '配对：等人输密码' "$PW_SYM" ) &
BG=$!
send newpair --password "$PW_SYM" --frames 60
wait $BG; BG=; sleep 2
# 同上，这回 Tab5 上留着这台电脑（标题「重新配对」），密码框开着时单独移除它
DROP_L0=$(mark)
( wait_after "$DROP_L0" 20 '配对：等人输密码' && { sleep 1; cmd "wd box"; sleep 0.5; cmd "wd forget 1"; sleep 0.5; cmd "wd pw $PW"; } ) &
BG=$!
send drop --forget --password $PW --frames 60
wait $BG; BG=; sleep 2; cmd "wd pairs"; sleep 1
# 后面的项要两边配对着
AGAIN_L0=$(mark)
( type_pw "$AGAIN_L0" '配对：等人输密码' $PW ) &
BG=$!
send again --password $PW --frames 60
wait $BG; BG=; sleep 2

# ── 0f. 自动发现：不给地址，广播找到 Tab5 再连；再量应答有多快（每秒问一次，问 10 次）──
AUTO_L0=$(mark)
$PY $T/wd_send_test.py auto --frames 60 > "${P}_auto.log" 2>&1; echo "rc=$?" >> "${P}_auto.log"; sleep 2
$PY $T/wd_discover.py -n 10 -i 1 > "${P}_disc.log" 2>&1; echo "rc=$?" >> "${P}_disc.log"

# ── 1. 测试卡（期间读帧缓冲核对四角色块）、整屏滚动、噪点 ──
# 画面的色彩（协议同名一节）有两条路，各走一遍：现在的发送端说用 YUV422 帧缓冲（4:2:2、有限范围，进出显示时切 DSI 桥的格式）；
# 加它之前的发送端（--caps 7）照旧走 RGB565。wd check / wd px 按帧缓冲当时的格式读
CARD_L0=$(mark)
( sleep 3; cmd "wd check" ) &
BG=$!
send card --frames 300
wait $BG; BG=; sleep 2
CARDRGB_L0=$(mark)
( sleep 3; cmd "wd check" ) &
BG=$!
send cardrgb --caps 7 --frames 300
wait $BG; BG=; sleep 2
CARDRGB_L1=$(mark)
send scroll --pattern scroll --frames 300; sleep 2
SCROLLRGB_L0=$(mark)
send scrollrgb --caps 7 --pattern scroll --frames 300; sleep 2
SCROLLRGB_L1=$(mark)
send noise --pattern noise --frames 60; sleep 2

# ── 1b. 键盘回传（不用接键盘：调试命令 kbd key 当成键盘上报了一个按键事件，后面走的是同一条路）──
# 显示期间依次按 a、Aa + !、Ctrl + c、按住 0 再按 )（两个键同一个键码，按下 ) 时 Tab5 连发两份），每次变化各发一份报文、顺序不乱。
# 不应该发给发送端的三种：连接之前按的（q）；连上了、第一帧还没上屏时按的（w：发送端连上 3 秒后才发第一帧）；
# 连接结束之后的（显示期间按下 s 一直不松，连接结束后才松开——这一份不能留给下一条连接，下一条就是后面 wd exit 那一条）
cmd "kbd log on"; sleep 0.3; cmd "kbd key 2 1 down"; sleep 0.3; cmd "kbd key 2 1 up"; sleep 1
KEYS_L0=$(wc -l < "$SERLOG")        # 这一段串口日志从这一行之后开始
(
    for _ in $(seq 50); do tail -n +"$((KEYS_L0 + 1))" "$SERLOG" | grep -aq '连上了' && break; sleep 0.2; done
    cmd "kbd key 2 2 down"; sleep 0.3; cmd "kbd key 2 2 up"
    sleep 5
    for k in "3 2 down" "3 2 up" "3 1 down" "1 1 down" "1 1 up" "3 1 up" "4 0 down" "4 4 down" "4 4 up" "4 0 up" \
             "0 10 down" "1 10 down" "1 10 up" "0 10 up" "3 3 down"; do
        cmd "kbd key $k"; sleep 0.3
    done
) &
BG=$!
send keys --first-delay 3 --frames 30 --hold 12
wait $BG; BG=
sleep 1; cmd "kbd key 3 3 up"; sleep 0.5; cmd "kbd log off"; sleep 2

# ── 1c. 触摸回传和「还按着」（不用手指：调试命令 wd touch 当成有一根手指按在那一点上，认人、边缘区、发报文走的是同一条路）──
# 按在 (640,360) 不动 1.2 秒、挪到 (700,400) 再停 0.7 秒、抬起；再从左边缘区（x ≤ 32）按下、抬起——这一根是 Tab5 自己的手势，不发给发送端。
# 按着不动的时候每 200 毫秒左右要有一个 HELD（协议「按着的时候」）；抬起之后、只有边缘那一根按着的时候不能有。
# 键盘那一半用上面 1b 的日志判：按着键的每一段都有 HELD，全松开的时候没有
TOUCH_L0=$(mark)
(
    wait_after "$TOUCH_L0" 20 '连上了' && sleep 2.5
    cmd "wd touch 640 360"; sleep 1.2; cmd "wd touch 700 400"; sleep 0.7; cmd "wd touch up"; sleep 1
    cmd "wd touch 10 300"; sleep 0.7; cmd "wd touch up"
) &
BG=$!
send touch --frames 30 --hold 9
wait $BG; BG=; sleep 2

# ── 1d. 触摸方式（协议「触摸方式」）：触摸板模式的开关在 Tab5 上，调试命令 wd tpad on|off 拨的和设置页上那个开关是同一个值 ──
# 连接之前拨开：握手走完发送端先收到 INPUT_MODE 触摸板（和 TPAD_GAIN），排在第一份 TOUCH 之前；显示期间拨回去（加速也换一档）：收到新的，排在之后那份 TOUCH 之前。
# 两种方式下 Tab5 发的 TOUCH 一样（换成鼠标动作是电脑的事）。先记下开关原来的样子，测完拨回去
# （wd tpad 不带 on / off 只报开关，不带连接状态：后面「各阶段的监听状态」数的是不带参数的 wd，这里不能多发一条）
MODE_L0=$(mark)
cmd "wd tpad"; wait_after "$MODE_L0" 5 'dbgcon: wd：触摸板模式'
case "$(tail -n +"$((MODE_L0 + 1))" "$SERLOG" | grep -a -o 'dbgcon: wd：触摸板模式\(开\|关\)' | tail -1)" in
    *开) TPAD_WAS=on ;;
    *关) TPAD_WAS=off ;;
    *)   echo "读不到触摸板模式开关原来的状态：测完不拨回去，请自己用 wd tpad on|off 恢复" ;;
esac
# 触摸板加速（设置页上的「触摸板加速」，调试命令 wd tpad accel [0~3] = 关 / 低 / 中 / 高）是一样的做法，报文是 TPAD_GAIN：也先记下原来是哪一档
ACCEL_L0=$(mark)
cmd "wd tpad accel"; wait_after "$ACCEL_L0" 5 'dbgcon: wd：触摸板加速'
case "$(tail -n +"$((ACCEL_L0 + 1))" "$SERLOG" | grep -a -o 'dbgcon: wd：触摸板加速 \(关\|低\|中\|高\)' | tail -1)" in
    *关) ACCEL_WAS=0 ;;
    *低) ACCEL_WAS=1 ;;
    *中) ACCEL_WAS=2 ;;
    *高) ACCEL_WAS=3 ;;
    *)   echo "读不到触摸板加速原来的档位：测完不拨回去，请自己用 wd tpad accel 0~3 恢复" ;;
esac
cmd "wd tpad on"; sleep 0.5
cmd "wd tpad accel 1"; sleep 0.5
# 先用没在 AUTH 里声明认得这些报文的发送端各连一次（以前的托盘程序就是这样）：设置开着 Tab5 也不能发——旧发送端收到不认识的报文会断开。
# --caps 0 = 加 INPUT_MODE 之前的版本，两个都不能发；--caps 1 = 只认得 INPUT_MODE 的那一版，TPAD_GAIN 不能发。多留 2 秒，看握手走完之后确实没来
OLD_L0=$(mark)
send modeold --caps 0 --frames 30 --hold 2
sleep 1
CAP1_L0=$(mark)
send modecap1 --caps 1 --frames 30 --hold 2
sleep 1
MODE_L0=$(mark)
(
    wait_after "$MODE_L0" 20 '连上了' && sleep 2.5
    cmd "wd touch 640 360"; sleep 0.4; cmd "wd touch up"; sleep 0.8
    cmd "wd tpad off"; sleep 0.4; cmd "wd tpad accel 3"; sleep 0.8
    cmd "wd touch 700 400"; sleep 0.4; cmd "wd touch up"
) &
BG=$!
send mode --frames 30 --hold 7
wait $BG; BG=; sleep 1
tpad_restore; sleep 1

# ── 1e. 画质（协议「画质」）：主页面上那排画质按钮（四档），调试命令 wd pic <0~3> 拨的是同一个值 ──
# 连接之前拨成第 1 档（高画质）：握手走完发送端收到 PICTURE_MODE（档位 + 屏幕的帧周期）；显示期间拨成第 3 档（最佳速度）：再收到一份。
# 只认得 PICTURE 的发送端（上一版，--caps 15）收到的是折算出来的质量和预算；两种都不认得的（--caps 3）收不到。
# 这里的发送端（Python 工具）只把收到的打印出来，画质照它自己命令行的；跟着改画质的是 C# 发送端（它的单元测试和 mockcheck.sh 管）
PIC_L0=$(mark)
cmd "wd pic"; wait_after "$PIC_L0" 5 'dbgcon: wd：画质第'
PIC_WAS=$(tail -n +"$((PIC_L0 + 1))" "$SERLOG" | grep -a -o 'dbgcon: wd：画质第 [0-9]* 档' | tail -1 | grep -o '[0-9]\+')
[ -n "$PIC_WAS" ] || echo "读不到画质原来是第几档：测完不拨回去，请自己用 wd pic <0~3> 恢复"
cmd "wd pic 1"; sleep 0.5
PICOLD_L0=$(mark)
send picold --caps 3 --frames 30 --hold 2
sleep 1
PICFOLD_L0=$(mark)
send picfold --caps 15 --frames 30 --hold 2
sleep 1
PICNEW_L0=$(mark)
(
    wait_after "$PICNEW_L0" 20 '连上了' && sleep 2.5
    cmd "wd pic 3"
) &
BG=$!
send pic --frames 30 --hold 5
wait $BG; BG=; sleep 1
tpad_restore; sleep 1

# ── 1f. 调试信息浮层（主页面上的「性能信息」，调试命令 wd osd on|off）：Tab5 自己叠在画面顶上靠右的一条（横屏 x = 944~1119、y = 0~31）──
# 画面是增量解码的，浮层要自己记哪些行被解码器重写过。发 spots（浅灰底 200，只有两处隔开的小块在变）、条带高 80：浮层（帧缓冲第 160~335 行）横跨的三个条带里只有两头的在重发，
# 中间的不重发。读三个点（wd px）：(979, 1) 在浮层里、一直没重写的行上；(1070, 1) 在浮层里、每帧重写的行上；(700, 1) 在浮层外。
# 开着：浮层里两个点都是压暗一半的 100 上下（记错了行的话没重写的那几行会越叠越暗）、浮层外 200 上下；显示期间关掉：三个点都回到 200 上下；再打开又压暗
OSD_L0=$(mark)
cmd "wd osd"; wait_after "$OSD_L0" 5 'dbgcon: wd：调试信息'
case "$(tail -n +"$((OSD_L0 + 1))" "$SERLOG" | grep -a -o 'dbgcon: wd：调试信息\(开\|关\)' | tail -1)" in
    *开) OSD_WAS=on ;;
    *关) OSD_WAS=off ;;
    *)   echo "读不到调试信息开关原来的状态：测完不拨回去，请自己用 wd osd on|off 恢复" ;;
esac
px3() { cmd "wd px 979 1"; sleep 0.2; cmd "wd px 1070 1"; sleep 0.2; cmd "wd px 700 1"; sleep 0.3; }
osd_run() {     # osd_run <日志名> [发送端的参数…]：开着浮层发 spots，中途读点、关掉再读、再打开再读
    local log=$1 l0; shift
    cmd "wd osd on"; sleep 0.5
    l0=$(mark)
    (
        wait_after "$l0" 20 '连上了' && sleep 7
        px3
        cmd "wd osd off"; sleep 1.5; px3
        cmd "wd osd on"; sleep 1.5; px3
    ) &
    BG=$!
    send "$log" --pattern spots --strip-h 80 --frames 150 --fps 30 --hold 9 "$@"
    wait $BG; BG=; sleep 1
}
# 两种帧缓冲格式各一遍（混色是两套写法）：YUV422，然后 RGB565（--caps 7）
OSD_L1=$(mark)
osd_run osd
OSD_L2=$(mark)
osd_run osdrgb --caps 7
OSD_L3=$(mark)
tpad_restore; sleep 1

# ── 2. 退出手势（用调试命令 wd exit 走同一条路）：发送端应收到 BYE，之后连不上，Tab5 回到主页面、暂停着不再监听 ──
# 发送端满速发整屏滚动：Tab5 发 BYE 的时候路上一定还有数据，这样才测得出它是等发送端关了才关、还是立刻关（回 RST）
# 套一层 timeout：wd exit 万一没起作用（比如那一刻第一帧还没上屏），十万帧要发几十分钟，整个回归就卡在这里了
timeout 60 $PY $T/wd_send_test.py "$IP" --pattern scroll --frames 100000 > "${P}_exit.log" 2>&1 &
SP=$!
sleep 4; cmd "wd exit"
wait $SP; echo "rc=$?" >> "${P}_exit.log"; SP=
$PY - "$IP" >> "${P}_exit.log" 2>&1 <<'PYEOF'
import socket, sys, time
def attempt(label):
    try:
        s = socket.create_connection((sys.argv[1], 7250), timeout=3)
    except OSError as e:
        print(f'{label}：连不上（{e}）'); return
    try:
        s.settimeout(2)
        d = s.recv(64)
        print(f'{label}：连上了，' + (f'还收到 {len(d)} 字节（不应该）' if d else '随即被对端关闭'))
    except OSError as e:
        print(f'{label}：连上了但没有 HELLO（{e}）')
    finally:
        s.close()
attempt('退出手势后立刻重连')
time.sleep(1.5)
attempt('退出手势后 1.5 秒重连')
PYEOF
sleep 1; cmd wd; sleep 1
# 退出之后主页面的样子（wd page）：在前台、暂停着、「开始显示」露出来；「已配对的电脑」那一行写着前面配上的那一台
PAGE_L0=$(mark)
cmd "wd page"; sleep 1
# 暂停着息屏、唤醒（o 息屏 / 唤醒）：回来的还是暂停着的主页面，不是又开始监听（亮屏时 app 只在没暂停时才重新交出监听）。
# 界面上写的「已暂停」是 app 自己记的，所以另外问 wdisp（wd：应是没在监听）、看这一段里它有没有打过「开始监听」
LOCK_L0=$(mark)
cmd o; sleep 2; cmd o; sleep 3; cmd wd; sleep 0.5; cmd open; sleep 0.5; cmd "wd page"; sleep 1

# ── 2b. Tab5 的 BYE 晚到发送端 ──
# Tab5 发出的报文（BYE 自己，或者排在它前面的 ACK）在空中丢了的时候，BYE 要等 TCP 重传才到发送端（实测 1.1~1.5 秒），
# 这期间发送端在等确认，不发数据也不关连接。用发送端的 --bye-lag 模拟：收到 BYE 之后隔 1.5 秒才关连接。Tab5 应该还等着，它关了才关
# 这一条连接是点主页面上的「开始显示」（wd start）之后连上的：点了之后不再暂停、接着监听
START_L0=$(mark)
# 连上之前把界面换到「已配对的电脑」页、点开第一台的「移除？」确认框（不点确认；再点一下框外，应点在顶层的遮罩上）：
# 退出手势之后应回到主页面（「开始显示」在那一页上），不是回那一页；那一页的确认框跟着关掉，不带到主页面上；配对一台没少
cmd "wd start"; sleep 1; cmd "wd page"; sleep 1; cmd "open pairs"; sleep 3; cmd open; sleep 0.5
CONFIRM_OPEN=1; cmd "tap 1157 128"; sleep 1; cmd "tap 100 600"; sleep 0.5
timeout 60 $PY $T/wd_send_test.py "$IP" --pattern scroll --frames 100000 --bye-lag 1.5 > "${P}_byelag.log" 2>&1 &
SP=$!
sleep 4; cmd "wd exit"
wait $SP; echo "rc=$?" >> "${P}_byelag.log"; SP=
sleep 3; CONFIRM_OPEN=; cmd open; sleep 0.5; cmd "tap 100 600"; sleep 0.5; cmd "wd page"; sleep 1; cmd "wd pairs"; sleep 1
BYEQ_L0=$(mark)

# ── 2c. 连接一结束，之后到的询问就要应答 ──
# 电脑收到 Tab5 的 BYE 就关连接、马上再问（自动连接），关连接和询问几乎同时到 Tab5，谁先到不一定。询问先到的话，
# Tab5 不能把它当成「连接期间攒下的」丢掉，不然电脑要过 3 秒问下一轮才连得上。这里把这种先后做成确定的：
# wd kick 让 Tab5 发 BYE（没暂停、接着监听）；发送端收到 BYE 先单播问一次，隔 50 毫秒才关连接，300 毫秒之内要有应答
# 上一项以退出手势结束、暂停着：先点「开始显示」
cmd "wd start"; sleep 3
timeout 60 $PY $T/wd_send_test.py "$IP" --pattern scroll --frames 100000 --bye-lag 0.05 --bye-query > "${P}_byeq.log" 2>&1 &
SP=$!
sleep 4; cmd "wd kick"
wait $SP; echo "rc=$?" >> "${P}_byeq.log"; SP=
sleep 2

# ── 2d. 发送端自己发 BYE 结束的也一样：Tab5 收尾期间到的询问不能丢 ──
# 电脑发完 BYE 等 Tab5 关连接最多等 300 毫秒，到时间就自己关、马上再问；Tab5 收尾慢的时候，这个询问在它收尾期间就到了。
# 做成确定的：发送端发完 BYE 隔 15 毫秒单播问一次（比 BYE 到 Tab5 的时间长、比 Tab5 收尾的时间短），300 毫秒之内要有应答。
# 「收尾做完、关连接之前才读掉旧询问」的写法在这一项上 6 次都没有应答（logs/wdisp37_ownbye_old_*），现在的写法每次都有
send ownbye --pattern scroll --frames 40 --own-bye-query 0.015
sleep 2

# ── 3. 违规发送端（含畸形 JPEG）+ 随机改文件头 + 随机改坏压缩数据 ──
$PY $T/wd_bad_sender.py "$IP" --fuzz 100 --fuzz-body 100 > "${P}_bad.log" 2>&1; echo "rc=$?" >> "${P}_bad.log"
sleep 1; cmd wd; sleep 1; cmd m; sleep 2; cmd hosted; sleep 2

# ── 3b. 息屏：等连接时到时间就息屏、照常监听；电脑的画面要上屏时自动点亮；顶层的弹框不挡双击亮屏 ──
# 这一段先取消常亮、把「息屏时间」改成 5 秒（scroff，和设置页上那一项是同一个值；界面上最短 1 分钟，这里不干等），测完都改回去。
# 「双击」用调试命令 tap 连点两下黑屏页（和手指点到谁是同一套找法：先顶层、再当前页）。
SCROFF_L0=$(mark)
cmd scroff; wait_after "$SCROFF_L0" 5 'dbgcon: scroff：息屏时间'
SCROFF_WAS=$(tail -n +"$((SCROFF_L0 + 1))" "$SERLOG" | grep -a -o 'dbgcon: scroff：息屏时间 [0-9]* 秒' | tail -1 | grep -o '[0-9]\+')
[ -n "$SCROFF_WAS" ] || echo "读不到息屏时间原来是多少秒：测完不改回去，请自己在设置页上重选"
dbltap() { cmd "tap 640 400"; sleep 0.2; cmd "tap 640 400"; }
# (1) 到时间息屏之后：询问照常应答（每秒问一次，问 5 次）；配对过的电脑连上来，第一帧上屏时点亮；连接结束后回到主页面、没暂停、接着等
OFF1_L0=$(mark)
cmd "disp awake off"; sleep 0.5; cmd "scroff 5"
wait_after "$OFF1_L0" 15 'scrmgr: screen off'; sleep 2
$PY $T/wd_discover.py -n 5 -i 1 > "${P}_offdisc.log" 2>&1; echo "rc=$?" >> "${P}_offdisc.log"
send offshow --frames 120 --fps 30
sleep 2; cmd "wd page"; sleep 1
# (1b) 又到时间息屏之后电脑连上来、显示中做退出手势：要暂停住（建回页面、亮屏的那一下不能又把监听交出去），回到主页面、露出「开始显示」，再连连不上。
#      点亮之后先把息屏时间改长，免得下面问状态的几秒里又息屏；问完点「开始显示」、改回 5 秒，给 (2) 用
OFF1B_L0=$(mark)
wait_after "$OFF1B_L0" 15 'scrmgr: screen off'; sleep 1
timeout 60 $PY $T/wd_send_test.py "$IP" --pattern scroll --frames 100000 > "${P}_offexit.log" 2>&1 &
SP=$!
wait_after "$OFF1B_L0" 20 'scrmgr: wake（电脑的画面上屏）' && { cmd "scroff 600"; sleep 3; }
cmd "wd exit"
wait $SP; echo "rc=$?" >> "${P}_offexit.log"; SP=
sleep 3; cmd wd; sleep 0.5; cmd open; sleep 0.5; cmd "wd page"; sleep 1
$PY - "$IP" >> "${P}_offexit.log" 2>&1 <<'PYEOF'
import socket, sys
try:
    socket.create_connection((sys.argv[1], 7250), timeout=3).close()
    print('暂停着重连：连上了（不应该）')
except OSError as e:
    print(f'暂停着重连：连不上（{e}）')
PYEOF
cmd "wd start"; sleep 1; cmd "scroff 5"
# (2) 又到时间息屏之后，没配对的电脑连上来：亮屏、弹密码框。框开着没人理、又到时间息屏：框关掉（留着的话盖在黑屏页上，双击叫不醒）；
#     双击亮屏：那一次询问还没答完，框再弹出来，输对配上。发送端全程等着
OFF2_L0=$(mark)
wait_after "$OFF2_L0" 15 'scrmgr: screen off'; sleep 1
(
    wait_after "$OFF2_L0" 20 '配对：等人输密码' || exit 1
    sleep 1; cmd "wd box"; l1=$(mark)
    wait_after "$l1" 15 'scrmgr: screen off' || exit 1
    sleep 1; cmd "wd box"; sleep 0.5
    # 亮屏之后先把息屏时间改回去：核对密码要两三秒，不然核对期间又到 5 秒息屏了
    dbltap; sleep 1; cmd "scroff ${SCROFF_WAS:-120}"; sleep 2; cmd "wd box"; sleep 0.5; cmd "wd pw $PW"
) &
BG=$!
# 发送端这边忘了配对而 Tab5 还留着：标题是「重新配对」
send offpair --forget --password $PW --frames 60
wait $BG; BG=; sleep 2
OFF2_L1=$(mark)
scroff_restore; sleep 0.5; cmd "disp awake on"; sleep 3
# (3) 确认框开着时息屏、双击亮屏：到「已配对的电脑」页点第一台的「移除」（弹确认框，不点确认），再点一下框外（应点在顶层的遮罩上）；
#     息屏；双击：应点到黑屏页上、亮屏，回到「已配对的电脑」页；配对一台没少
OFF3_L0=$(mark)
cmd "open pairs"; sleep 3; cmd open; sleep 0.5; CONFIRM_OPEN=1; cmd "tap 1157 128"; sleep 1; cmd "tap 100 600"; sleep 0.5
cmd o; sleep 2; dbltap; sleep 3; CONFIRM_OPEN=; cmd open; sleep 0.5; cmd "tap 100 600"; sleep 0.5; cmd "wd pairs"; sleep 1
OFF3_L1=$(mark)

# ── 4. 暂停（wd pause，应停止监听）、点「开始显示」接着等；滚动加速（和无线显示共用帧缓冲。主页面、设置页一屏放完、不滚，
#       WiFi 页要扫出一屏以上的网络才滚：在滚动基准页的「中文文字行」变体上自动滚动，测完回主页面）──
cmd "open wdisp"; sleep 4
PAUSE_L0=$(mark)
cmd "wd pause"; sleep 1; cmd wd; sleep 1; cmd "wd page"; sleep 1; cmd "wd start"; sleep 1; cmd wd; sleep 0.5; cmd "wd page"; sleep 1
PAUSE_L1=$(mark)
# 画质那排按钮用手指点（press：经输入设备走，和真手指同一条路；wd pic 只改值、走不到按钮自己的切换）：
# 先用 wd pic 定在第 2 档，点第 4 段 → 应变成第 3 档、按钮上选中的也是它；再点一下已经选中的这一段 → 不变；最后点回原来那一档所在的段。
# 四段在横屏上的位置：y = 652，x = 104 / 253 / 402 / 551（主页面第二行左半边四等分）
PICB_L0=$(mark)
cmd "wd pic"; wait_after "$PICB_L0" 5 'dbgcon: wd：画质第'
PIC_BEFORE=$(tail -n +"$((PICB_L0 + 1))" "$SERLOG" | grep -a -o 'dbgcon: wd：画质第 [0-9]* 档' | tail -1 | grep -o '[0-9]\+')
[ -n "$PIC_BEFORE" ] || echo "读不到画质原来是第几档：这一项测完停在第 3 档，请自己用 wd pic <0~3> 恢复"
PIC_WAS=$PIC_BEFORE     # 中途退出、或者下面点回去那一下没点中：收尾时照样拨回原来那一档
SEG_L0=$(mark)
cmd "wd pic 2"; sleep 1.2; cmd "press 551 652"; sleep 1.2; cmd "wd pic"; sleep 0.3; cmd "wd page"; sleep 0.5
cmd "press 551 652"; sleep 1.2; cmd "wd pic"; sleep 0.3
case "$PIC_BEFORE" in 0) cmd "press 104 652" ;; 1) cmd "press 253 652" ;; 2) cmd "press 402 652" ;; *) : ;; esac
sleep 1.2; cmd "wd pic"; sleep 0.3; cmd "wd page"; sleep 0.5
SEG_L1=$(mark)
cmd "bench c"; sleep 3; cmd "prof on"; sleep 1; cmd "%"; sleep 6; cmd "%"; sleep 6; cmd "prof off"; sleep 1; cmd "open wdisp"; sleep 3
cmd m; sleep 2
finish

# ── 判定 ──
bad=0; total=0
item() {    # item <名称> <0 = 符合> [附注]
    total=$((total + 1))
    if [ "$2" = 0 ]; then echo "[符合] $1${3:+：$3}"; else echo "[不符] $1${3:+：$3}"; bad=$((bad + 1)); fi
}
has() { grep -q -- "$2" "$1"; echo $?; }
sent() {    # sent <日志名> <帧数>：发送端退出码 0 而且帧数发够了（Tab5 中途发 BYE 时退出码也是 0，但帧数不够）
    grep -q '^rc=0$' "${P}_$1.log" && grep -q "^发了 $2 帧，" "${P}_$1.log"; echo $?
}
fps() { grep -o '[0-9.]* 帧/秒' "${P}_$1.log" | head -1; }
seg() { tr -d '\r' < "$SERLOG" | sed -n "$(($1 + 1)),${2:-\$}p"; }     # seg <起> [止]：串口日志的一段（行号见 mark）
# 串口里各次 wd 命令（不带参数的）报的状态，按先后：连开两次页面后 / 密码框上点取消后 / 退出手势后 / 暂停着息屏再唤醒后 /
# 违规发送端跑完后 / 息屏着连上来、退出手势后 / wd pause 后 / 再点「开始显示」后
states=$(grep -a -o 'dbgcon: wd：\(没在监听\|等待连接\|已连接（第一帧还没到齐）\|显示中\)' "$SERLOG" | sed 's/dbgcon: wd：//' | paste -sd, -)
[ "$states" = "等待连接,没在监听,没在监听,没在监听,等待连接,没在监听,没在监听,等待连接" ]
item "各阶段的监听状态（连开两次主页面后 / 密码框上点取消后 / wd exit 后 / 暂停着息屏再唤醒后 / 点「开始显示」、违规发送端跑完后 / 息屏着连上来、wd exit 后 / wd pause 后 / 再点「开始显示」后）" $? "$states"
# 配对那几段。密码框的样子取自 wd box 打的那一行（ui_pairbox: 配对密码框：…）
grep -q '配对：Tab5 上输的密码不对（第 1 次）' "${P}_pair.log" && grep -q '配对：密码对上了' "${P}_pair.log" && [ "$(sent pair 60)" = 0 ] \
    && seg "$PAIR_L0" "$CANCEL_L0" | grep -aq '配对密码框：开着，标题「配对 .*等人输入，没标红' \
    && seg "$PAIR_L0" "$CANCEL_L0" | grep -aq '配对密码框：开着，标题「配对 .*等人输入，标着红（输入框边框 #f87171）' \
    && seg "$PAIR_L0" "$CANCEL_L0" | grep -aq '握手走完：.*刚配对的'
item "配对（密码框弹出；输错：标红重输；输对：配上，随后 60 帧）" $? "$(seg "$PAIR_L0" "$CANCEL_L0" | grep -a -o '配对：算了 [0-9]* ms' | paste -sd, -)"
grep -q '握手期间 Tab5 上的人取消了（BYE）' "${P}_cancel.log" && grep -q 'BYE 之后 Tab5 正常关闭' "${P}_cancel.log" && grep -q '^rc=1$' "${P}_cancel.log" \
    && seg "$CANCEL_L0" "$BUSY_L0" | grep -aq '配对密码框：开着，标题「重新配对 ' \
    && seg "$CANCEL_L0" "$BUSY_L0" | grep -aq '连接结束（本机断开）' && seg "$CANCEL_L0" "$BUSY_L0" | grep -aq '停止监听' \
    && seg "$CANCEL_L0" "$BUSY_L0" | grep -aq '配对密码框：没开着' \
    && seg "$CANCEL_L0" "$BUSY_L0" | grep -aq '无线显示页：在前台，已暂停，.*「开始显示」露着'
item "密码框上点取消（Tab5 留着这台电脑：标题「重新配对」；发送端收到 BYE；框关掉、Tab5 暂停、主页面上露出「开始显示」）" $?
# 点取消是在「配对：算了 … ms」之前（核对期间），而发送端没等到 PAIR_REPLY（等到的话它会说密码对上了）
busy=$(seg "$BUSY_L0" "$GONE_L0" | awk '/^@@CMD wd pwcancel/ && st == 0 { st = 1 } /配对：算了 [0-9]* ms/ && st == 1 { st = 2 } /连接结束（本机断开）/ && st == 2 { st = 3 } END { print st + 0 }')
grep -q '握手期间 Tab5 上的人取消了（BYE）' "${P}_busycancel.log" && ! grep -q '配对：密码对上了' "${P}_busycancel.log" && grep -q '^rc=1$' "${P}_busycancel.log" \
    && [ "$busy" = 3 ]
item "密码输完、核对期间点取消：核对完不发 PAIR_REPLY，直接 BYE" $? "走到第 $busy 步（共 3 步）"
gone=$(seg "$GONE_L0" "$REPAIR_L0" | awk '/配对密码框：开着/ && st == 0 { st = 1 } /连接结束（连接断开）/ && st == 1 { st = 2 } /配对密码框：没开着/ && st == 2 { st = 3 } END { print st + 0 }')
[ "$gone" = 3 ]; item "密码框开着时发送端没了：框关掉，回到等连接" $? "走到第 $gone 步（共 3 步）"
# 换页前后两次 wd box（第一次界面在 WiFi 页上，第二次回了主页面）：都开着、是同一次询问。先后要对：在 WiFi 页 → 框开着 → 换页 → 框开着
repair=$(seg "$REPAIR_L0" "$LIVE_L0" | awk '/open：记录的当前页是 wifi，屏幕亮着/ && st == 0 { st = 1 } /配对密码框：开着/ && st == 1 { st = 2 }
    /^@@CMD open wdisp/ && st == 2 { st = 3 } /配对密码框：开着/ && st == 3 { st = 4 } END { print st + 0 }')
asks=$(seg "$REPAIR_L0" "$LIVE_L0" | grep -a -o '配对密码框：开着，标题「重新配对 [^，]*，第 [0-9]* 次询问，等人输入' | grep -o '第 [0-9]* 次' | paste -sd, -)
[ "$(echo "$asks" | tr ',' '\n' | wc -l)" = 2 ] && [ "$(echo "$asks" | tr ',' '\n' | sort -u | wc -l)" = 1 ] \
    && seg "$REPAIR_L0" "$LIVE_L0" | grep -aq '握手走完：.*刚配对的' && [ "$(sent repair 60)" = 0 ] && [ "$repair" = 4 ]
item "界面在 WiFi 页上时密码框照样弹；框开着时换页（回主页面）：框留着（同一次询问），输对配上" $? "走到第 $repair 步（共 4 步）；两次 wd box：${asks:-没有}"
live=$(seg "$LIVE_L0" "$NEW_L0" | awk '/的配对移除了/ && st == 0 { st = 1 } /连接结束（本机断开）/ && st == 1 { st = 2 } /BYE 之后：发送端关了连接/ && st == 2 { st = 3 } END { print st + 0 }')
grep -q 'Tab5 退出了显示模式（BYE）' "${P}_live.log" && grep -q '^rc=0$' "${P}_live.log" && [ "$live" = 3 ]
item "显示中移除这台电脑的配对：那条连接以 BYE 结束" $? "走到第 $live 步（共 3 步）"
grep -q 'AUTH_UNKNOWN' "${P}_unknown.log" && grep -q '^rc=3$' "${P}_unknown.log"
item "Tab5 没有这台电脑的配对了，电脑带着旧密钥来、没给密码：认证不过（AUTH_UNKNOWN）" $?
# 握手期间移除配对的两项：串口里依次是 等人输密码 → 移除 → 连接以「握手期间 Tab5 上移除过配对」结束 → wd pairs 报 0 台；
# 发送端那头密码是对上了的（握手走完了），随后收到 BYE、60 帧没发成
revoked() {     # revoked <起> <止> <移除那一行的字样>
    seg "$1" "$2" | awk -v rm="$3" '/配对：等人输密码/ && st == 0 { st = 1 } index($0, rm) && st == 1 { st = 2 }
        /连接结束（握手期间 Tab5 上移除过配对）/ && st == 2 { st = 3 } /wd：配对过 0 台电脑/ && st == 3 { st = 4 } END { print st + 0 }'
}
wipe=$(revoked "$WIPE_L0" "$NEW_L0" '台电脑的配对都移除了')
grep -q '配对：密码对上了' "${P}_wipe.log" && grep -q 'Tab5 退出了显示模式（BYE）' "${P}_wipe.log" && [ "$(sent wipe 60)" != 0 ] && [ "$wipe" = 4 ]
item "密码框开着时在 Tab5 上全部移除、再输对密码：这次配出来的不存，连接以 BYE 结束" $? "走到第 $wipe 步（共 4 步）"
seg "$NEW_L0" "$DROP_L0" | grep -aq '配对密码框：开着，标题「配对 .*键盘上打不出的字符：无' && seg "$NEW_L0" "$DROP_L0" | grep -aq '握手走完：.*刚配对的' \
    && [ "$(sent newpair 60)" = 0 ]
item "重新配对（Tab5 上没有它了：标题「配对」），密码带空格和全部 32 个标点、在软键盘上逐键点出；键盘三页合起来没有打不出的字符；随后 60 帧" $?
drop=$(revoked "$DROP_L0" "$AGAIN_L0" '」的配对移除了')
grep -q '配对：密码对上了' "${P}_drop.log" && grep -q 'Tab5 退出了显示模式（BYE）' "${P}_drop.log" && [ "$(sent drop 60)" != 0 ] && [ "$drop" = 4 ] \
    && seg "$DROP_L0" "$AGAIN_L0" | grep -aq '配对密码框：开着，标题「重新配对 '
item "同上、Tab5 上留着这台电脑时单独移除它：也不存" $? "走到第 $drop 步（共 4 步）"
seg "$AGAIN_L0" "$AUTO_L0" | grep -aq '握手走完：.*刚配对的' && [ "$(sent again 60)" = 0 ]
item "之后照常配对，60 帧" $?
seg "$AUTO_L0" | grep -aq '握手走完：.*配对过的' && [ "$(sent auto 60)" = 0 ]
item "自动发现：不给地址，广播找到 Tab5，免密连上，60 帧" $? "$(fps auto)"
disc=$(grep -a '次询问应答了' "${P}_disc.log" | head -1)
echo "$disc" | grep -q "10 次询问应答了 10 次，其中 10 次在 300 毫秒之内"
item "自动发现的应答：10 次询问都在 300 毫秒之内应答" $? "${disc:-没有应答}"
item "测试卡 300 帧（YUV422 帧缓冲）" "$(sent card 300)" "$(fps card)"
seg "$CARD_L0" "$CARDRGB_L0" | grep -aq '握手走完.*画面走 YUV422' && seg "$CARD_L0" "$CARDRGB_L0" | grep -aq 'check：四角都对.*帧缓冲 YUV422'
item "测试卡四角像素（方向和红蓝顺序；握手定下走 YUV422，颜色按有限范围展开之后是对的）" $?
[ "$(sent cardrgb 300)" = 0 ] && seg "$CARDRGB_L0" "$CARDRGB_L1" | grep -aq '握手走完.*画面走 RGB565' \
    && seg "$CARDRGB_L0" "$CARDRGB_L1" | grep -aq 'check：四角都对.*帧缓冲 RGB565'
item "测试卡 300 帧、四角像素：发送端没说用 YUV422（上一版发送端）时照旧走 RGB565" $? "$(fps cardrgb)"
# 进出显示时切 DSI 桥的格式：整个回归里不应该有切不成的，也不应该有「取数跟不上」（切的那一下有意造成的欠载不打印）
! grep -aq '帧缓冲格式切不\|切帧缓冲格式：\|underrun happens\|lcd.dsi.dpi' "$SERLOG" && grep -aq '握手走完.*画面走 YUV422' "$SERLOG"
item "进出显示时切帧缓冲格式（RGB565 ↔ YUV422）：整个回归里没有切不成、没有取数跟不上的报错" $? "$(grep -a -c '握手走完.*画面走 YUV422' "$SERLOG") 条连接走 YUV422，$(grep -a -c '握手走完.*画面走 RGB565' "$SERLOG") 条走 RGB565"
[ "$(sent scroll 300)" = 0 ] && seg "$CARDRGB_L1" "$SCROLLRGB_L0" | grep -aq '握手走完.*画面走 YUV422'
item "整屏滚动 300 帧（YUV422 帧缓冲）" $? "$(fps scroll)"
[ "$(sent scrollrgb 300)" = 0 ] && seg "$SCROLLRGB_L0" "$SCROLLRGB_L1" | grep -aq '握手走完.*画面走 RGB565'
item "整屏滚动 300 帧（RGB565，上一版发送端）" $? "$(fps scrollrgb)"
item "噪点 60 帧" "$(sent noise 60)" "$(fps noise)"
keys=$(grep -a -o 'KEY [0-9a-f ]*' "${P}_keys.log" | sed 's/^KEY //; s/ //g' | paste -sd, -)
want=0000040000000000,0000000000000000,0200000000000000,02001e0000000000,0200000000000000,0000000000000000
want=$want,0100000000000000,0100060000000000,0100000000000000,0000000000000000
want=$want,0000270000000000,0000000000000000,0200270000000000,0000270000000000,0000000000000000,0000160000000000
[ "$keys" = "$want" ] && [ "$(sent keys 30)" = 0 ]
item "键盘回传（显示期间的 16 份报文按顺序发给了发送端，一份不多一份不少）" $? "发送端收到 $(grep -a -c 'KEY ' "${P}_keys.log") 份报文"
# 「还按着」：held <日志> 打印「HELD 的个数、离上一个输入报文最短和最长多少毫秒、没有键也没有触点按着时来的个数」
held() {
    grep -a -o '← \(KEY [0-9a-f ]*\|TOUCH .*\|HELD.*\)' "$1" | awk '
        /← KEY /   { keys = ($0 !~ /KEY 00 00 00 00 00 00 00 00/) }
        /← TOUCH / { fingers = ($0 !~ /全部抬起/) }
        /← HELD/   { n++; if (!keys && !fingers) stray++
                     gap = $0; sub(/.*离上一个输入报文 /, "", gap); sub(/ 毫秒.*/, "", gap); gap += 0
                     if (n == 1 || gap < lo) lo = gap; if (gap > hi) hi = gap }
        END { printf "%d %d %d %d\n", n, lo, hi, stray }'
}
# 间隔是发送端收到的时刻量的（Tab5 每 20 毫秒左右看一次，再加上路上的抖动），所以放宽到 120~450 毫秒。
# 按着键的有 12 段、每段 0.3 秒多（各一个），最后的 s 一直按到连接结束（好几秒）
read -r hn hlo hhi hstray <<< "$(held "${P}_keys.log")"
[ "${hn:-0}" -ge 20 ] && [ "$hlo" -ge 120 ] && [ "$hhi" -le 450 ] && [ "$hstray" = 0 ]
item "按着键的时候每 200 毫秒左右一个 HELD，全松开的时候不发" $? "$hn 个，离上一个输入报文 $hlo~$hhi 毫秒，不该有的 $hstray 个"
touches=$(grep -a -o '← TOUCH .*' "${P}_touch.log" | sed 's/^← //' | paste -sd'|' -)
[ "$touches" = 'TOUCH #0(640,360)|TOUCH #0(700,400)|TOUCH 全部抬起' ] && [ "$(sent touch 30)" = 0 ]
item "触摸回传（wd touch 按下、挪动、抬起各一份报文；从左边缘起手的那一根不发）" $? "发送端收到：${touches:-（没有）}"
modes=$(grep -a -o '← \(INPUT_MODE\|TPAD_GAIN\|TOUCH\) .*' "${P}_mode.log" | sed 's/^← //' | paste -sd'|' -)
[ "$modes" = 'INPUT_MODE 触摸板|TPAD_GAIN 1.5 倍|TOUCH #0(640,360)|TOUCH 全部抬起|INPUT_MODE 触摸|TPAD_GAIN 3.0 倍|TOUCH #0(700,400)|TOUCH 全部抬起' ] && [ "$(sent mode 30)" = 0 ]
item "触摸方式和触摸板加速（Tab5 上开着触摸板模式、加速「低」：握手走完先报；显示期间关掉、加速换成「高」：再报；都排在之后的触点前面）" $? "发送端收到：${modes:-（没有）}"
[ "$(has "${P}_modecap1.log" 'INPUT_MODE 触摸板')" = 0 ] && [ "$(has "${P}_modecap1.log" 'TPAD_GAIN')" != 0 ] && [ "$(sent modecap1 30)" = 0 ] \
    && seg "$CAP1_L0" "$MODE_L0" | grep -aq '它不认得触摸板加速的报文'
item "触摸板加速：发送端只声明了认得触摸方式（上一版发送端）时，报触摸方式、不报加速，照常显示" $? "发送端收到的 TPAD_GAIN：$(grep -a -o -m1 'TPAD_GAIN .*' "${P}_modecap1.log" || echo 没有)"
[ "$(has "${P}_modeold.log" 'INPUT_MODE')" != 0 ] && [ "$(has "${P}_modeold.log" 'TPAD_GAIN')" != 0 ] && [ "$(sent modeold 30)" = 0 ] \
    && seg "$OLD_L0" "$CAP1_L0" | grep -aq '它不认得触摸方式的报文'
item "触摸方式：发送端什么都没声明（更早的发送端）时，开着触摸板模式也不发这两种报文，照常显示" $? "发送端收到的：$(grep -a -o -m1 '\(INPUT_MODE\|TPAD_GAIN\) .*' "${P}_modeold.log" || echo 没有)"
pics=$(grep -a -o '← PICTURE.*' "${P}_pic.log" | sed 's/^← //' | paste -sd'|' -)
# 屏幕的帧周期是 Tab5 量的（15270 微秒上下，65.4 帧/秒），每次差几微秒：核对在 15000~15500 之间，比的时候换成 N
echo "$pics" | sed 's/帧周期 15[0-4][0-9][0-9] 微秒/帧周期 N 微秒/g' | grep -qx 'PICTURE_MODE 第 1 档「高画质」，屏幕的帧周期 N 微秒|PICTURE_MODE 第 3 档「最佳速度」，屏幕的帧周期 N 微秒' \
    && [ "$(sent pic 30)" = 0 ]
item "画质（Tab5 上选的第 1 档「高画质」：握手走完先报档位和屏幕的帧周期；显示期间改成第 3 档「最佳速度」：再报）" $? "发送端收到：${pics:-（没有）}"
[ "$(grep -a -o '← PICTURE.*' "${P}_picfold.log" | sed 's/^← //' | paste -sd'|' -)" = 'PICTURE 质量 90，预算 90 KB' ] && [ "$(sent picfold 30)" = 0 ] \
    && seg "$PICFOLD_L0" "$PICNEW_L0" | grep -aq '它不认得画质档位的报文'
item "画质：发送端只认得 PICTURE（上一版发送端）时，档位折成质量和预算报给它，照常显示" $? "发送端收到的：$(grep -a -o -m1 'PICTURE.*' "${P}_picfold.log" || echo 没有)"
[ "$(has "${P}_picold.log" 'PICTURE')" != 0 ] && [ "$(has "${P}_picold.log" '0x26')" != 0 ] && [ "$(has "${P}_picold.log" '0x27')" != 0 ] && [ "$(sent picold 30)" = 0 ] \
    && seg "$PICOLD_L0" "$PICFOLD_L0" | grep -aq '它没说认得画质的报文'
item "画质：发送端两种画质报文都没声明认得（更早的发送端）时不报，照常显示" $? "发送端收到的：$(grep -a -o -m1 'PICTURE.*' "${P}_picold.log" || echo 没有)"
# 九个读数依次是：开着的三个点、关掉之后的三个点、再打开的三个点；每个取绿色分量。拿「开着时浮层外的那个点」当基准 B（浅灰压成 JPEG 再解出来是 200 多一点，
# 具体多少跟质量、色度采样有关，所以不按绝对值判）：浮层里的点开着时是 B 的一半上下，关掉之后三个点都回到 B，再打开又是一半；差 8（两个色阶）以内算对
osd_read() { seg "$1" "$2" | grep -a -o 'px：([0-9]*, 1) = 0x[0-9a-f]* = ([0-9]*,[0-9]*' | sed 's/.*,//' | paste -sd' ' -; }     # osd_read <起> <止>：那一段里各次 wd px 读到的绿色分量
osd_px=$(osd_read "$OSD_L1" "$OSD_L2")
osd_px_rgb=$(osd_read "$OSD_L2" "$OSD_L3")
osd_judge() {   # osd_judge <九个读数>：符合返回 0
    [ $# = 9 ] || return 1
    local B=$3 i=0 v want d
    [ "$B" -ge 150 ] && [ "$B" -le 250 ] || return 1
    for v in "$@"; do
        case $i in 0|1|6|7) want=$((B / 2)) ;; *) want=$B ;; esac
        d=$((v - want)); [ "${d#-}" -le 8 ] || return 1
        i=$((i + 1))
    done
}
# shellcheck disable=SC2086
osd_judge $osd_px; osd_ok=$?
[ $osd_ok = 0 ] && [ "$(sent osd 150)" = 0 ] && seg "$OSD_L1" "$OSD_L2" | grep -aq '握手走完.*画面走 YUV422'
item "调试信息浮层（YUV422 帧缓冲）：只有两处隔开的小块在变时，没重写的行上浮层原样留着；显示期间关掉原样还原、再打开又画上；浮层外不受影响" $? "九个读数（开 / 关 / 再开，各三个点）：${osd_px:-（没有）}"
# shellcheck disable=SC2086
osd_judge $osd_px_rgb; osd_ok=$?
[ $osd_ok = 0 ] && [ "$(sent osdrgb 150)" = 0 ] && seg "$OSD_L2" "$OSD_L3" | grep -aq '握手走完.*画面走 RGB565'
item "调试信息浮层（RGB565 帧缓冲，上一版发送端）：同上" $? "九个读数：${osd_px_rgb:-（没有）}"
# 按着 1.2 秒 + 0.7 秒：5 + 3 个上下
read -r hn hlo hhi hstray <<< "$(held "${P}_touch.log")"
[ "${hn:-0}" -ge 6 ] && [ "${hn:-0}" -le 12 ] && [ "$hlo" -ge 120 ] && [ "$hhi" -le 450 ] && [ "$hstray" = 0 ]
item "按着不动的触点每 200 毫秒左右一个 HELD，抬起之后、只按着边缘那一根的时候不发" $? "$hn 个，离上一个输入报文 $hlo~$hhi 毫秒，不该有的 $hstray 个"
# 闸门：三种不该发的都没有发。同时要确认这三种情况真的测到了——
#   w 是在第一帧上屏之前按的：Tab5 自己的时间戳里，w 的报文比「连上了」晚不到 3000 毫秒（发送端连上 3 秒后才发第一帧）；
#   s 是在连接结束之后才松开的：串口日志里依次是 连接结束 → kbd key 3 3 up → 全松开的报文
gate_ms=$(tr -d '\r' < "$SERLOG" | tail -n +"$((KEYS_L0 + 1))" | awk '
    function ms(s) { sub(/^[^(]*\(/, "", s); sub(/\).*/, "", s); return s }
    /wdisp: .*连上了/ && c == "" { c = ms($0) }
    /kbd: 报文 00 00 1a / && w == "" { w = ms($0) }
    END { if (c != "" && w != "") print w - c }')
after=$(tr -d '\r' < "$SERLOG" | tail -n +"$((KEYS_L0 + 1))" | awk '
    /wdisp: 连接结束/ && st == 0 { st = 1 }
    /^@@CMD kbd key 3 3 up/ && st == 1 { st = 2 }
    /kbd: 报文 00 00 00 00 00 00 00 00/ && st == 2 { st = 3 }
    END { print st + 0 }')
! grep -aqE 'KEY 00 00 (14|1a) ' "${P}_keys.log" && ! grep -aq 'KEY ' "${P}_exit.log" \
    && [ -n "$gate_ms" ] && [ "$gate_ms" -ge 0 ] && [ "$gate_ms" -lt 3000 ] && [ "$after" = 3 ]
item "键盘回传的闸门（连接之前、第一帧上屏之前、连接结束之后的按键都没有发给发送端）" $? \
     "w 在连上后 ${gate_ms:-?} 毫秒按下；连接结束后才松开 s：$([ "$after" = 3 ] && echo 是 || echo "没测到（$after）")；下一条连接收到 $(grep -a -c 'KEY ' "${P}_exit.log") 份报文"
grep -q 'Tab5 退出了显示模式（BYE）' "${P}_exit.log" && grep -q '^rc=0$' "${P}_exit.log" && grep -q '连接结束（退出手势）' "$SERLOG"
item "wd exit：发送端收到 BYE、正常收尾" $?
# 两头都要对：发送端看到的是正常关闭，Tab5 自己记的是「发送端关了连接」（只看发送端那一头有盲区：Tab5 的 FIN 之后才到的 RST 它看不到）
# 只在 wd exit 之后那一段里找：前面别的测试里 Tab5 也可能说过 BYE
dev_bye=$(tr -d '\r' < "$SERLOG" | awk '/^@@CMD wd exit/ { on = 1 } on && /BYE 之后：/ { sub(/.*BYE 之后：/, "BYE 之后："); print; exit }')
grep -q 'BYE 之后 Tab5 正常关闭' "${P}_exit.log" && [ "${dev_bye#BYE 之后：发送端关了连接}" != "$dev_bye" ]
item "wd exit：Tab5 发完 BYE 等发送端关了才关连接（没有重置）" $? "发送端：$(grep -a -o 'BYE 之后.*' "${P}_exit.log" | head -1)；Tab5：${dev_bye:-没有这行日志}"
[ "$(grep -c '重连：连不上' "${P}_exit.log")" = 2 ]; item "wd exit 之后两次重连都连不上" $?
page1=$(seg "$PAGE_L0" "$LOCK_L0" | grep -a -o -m1 '无线显示页：.*'); page1b=$(seg "$LOCK_L0" "$START_L0" | grep -a -o -m1 '无线显示页：.*'); page2=$(seg "$START_L0" | grep -a -o -m1 '无线显示页：.*')
case "$page1" in 无线显示页：在前台，已暂停，状态「已暂停」，「开始显示」露着*已配对\ [1-9]\ 台（可点）*) p1=0 ;; *) p1=1 ;; esac
case "$page1b" in 无线显示页：在前台，已暂停，状态「已暂停」，「开始显示」露着*) p1b=0 ;; *) p1b=1 ;; esac
case "$page2" in 无线显示页：在前台，没暂停，状态「等待连接」，「开始显示」藏着*) p2=0 ;; *) p2=1 ;; esac
[ $p1 = 0 ] && [ $p2 = 0 ] && grep -q 'Tab5 退出了显示模式（BYE）' "${P}_byelag.log"
item "wd exit 之后回到主页面暂停着；点「开始显示」之后接着监听，发送端又连得上" $? "退出后：${page1:-（没有）}；点了之后：${page2:-（没有）}"
# 真的息了屏、又亮了屏；亮屏之后 wdisp 自己说没在监听、这一段里没打过「开始监听」；回来的是暂停着的主页面
lock=$(seg "$LOCK_L0" "$START_L0" | awk '/scrmgr: screen off/ && st == 0 { st = 1 } /scrmgr: wake$/ && st == 1 { st = 2 } /dbgcon: wd：没在监听/ && st == 2 { st = 3 }
    /open：记录的当前页是 wdisp，屏幕亮着/ && st == 3 { st = 4 } END { print st + 0 }')
[ $p1b = 0 ] && [ "$lock" = 4 ] && ! seg "$LOCK_L0" "$START_L0" | grep -aq '开始监听端口'
item "暂停着息屏、唤醒，回来还是暂停着的主页面（wdisp 没有又开始监听）" $? "走到第 $lock 步（共 4 步）；${page1b:-（没有）}"
# 显示之前界面停在「已配对的电脑」页、开着确认框：退出手势之后回的是主页面（暂停着、「开始显示」露着），确认框没带过来，配对还在
wexit=$(seg "$START_L0" "$BYEQ_L0" | awk '/open：记录的当前页是 pairs，屏幕亮着/ && st == 0 { st = 1 } /tap \(1157, 128\)：点到当前页的对象/ && st == 1 { st = 2 }
    /tap \(100, 600\)：点到顶层的对象/ && st == 2 { st = 3 } /连接结束（退出手势）/ && st == 3 { st = 4 } /open：记录的当前页是 wdisp，屏幕亮着/ && st == 4 { st = 5 }
    /tap \(100, 600\)：(点到当前页的对象|这一点上没有可点的对象)/ && st == 5 { st = 6 }
    /无线显示页：在前台，已暂停，状态「已暂停」，「开始显示」露着/ && st == 6 { st = 7 } /wd：配对过 [1-9] 台电脑/ && st == 7 { st = 8 } END { print st + 0 }')
[ "$wexit" = 8 ]; item "界面在「已配对的电脑」页上、开着「移除？」确认框时连上来，显示中做退出手势：回到主页面、暂停着，确认框关掉，配对没少" $? "走到第 $wexit 步（共 8 步）"
# 第二次 wd exit（发送端隔 1.5 秒才关连接）：Tab5 记的也应该是「发送端关了连接」，而且确实等了 1 秒以上
# （它量的用时从屏幕还回去之后算起，比 1.5 秒少几十毫秒；门槛定在 1 秒：只要求明显超过「半秒就不等了」）
lag_bye=$(tr -d '\r' < "$SERLOG" | awk '/^@@CMD wd exit/ { k++ } k == 2 && /BYE 之后：/ { sub(/.*BYE 之后：/, "BYE 之后："); print; exit }')
lag_ms=$(echo "$lag_bye" | sed -n 's/.*用了 \([0-9]*\) ms.*/\1/p')
grep -q 'Tab5 退出了显示模式（BYE）' "${P}_byelag.log" && grep -q 'BYE 之后 Tab5 正常关闭' "${P}_byelag.log" && grep -q '^rc=0$' "${P}_byelag.log" \
    && [ "${lag_bye#BYE 之后：发送端关了连接}" != "$lag_bye" ] && [ -n "$lag_ms" ] && [ "$lag_ms" -ge 1000 ]
item "BYE 晚 1.5 秒才到发送端（报文在空中丢了、等重传）：Tab5 还等着，发送端关了它才关" $? "Tab5：${lag_bye:-没有这行日志}"
grep -q '关连接之前问的那一次：Tab5 应答了' "${P}_byeq.log" && grep -q '^rc=0$' "${P}_byeq.log"
item "被 Tab5 断开（wd kick）时、发送端关连接之前到的询问：有应答（连接一结束，之后到的询问都算数）" $? "$(grep -a -o '关连接之前问的那一次：.*' "${P}_byeq.log" | head -1)"
grep -q '自己发 BYE 之后问的那一次：Tab5 应答了' "${P}_ownbye.log" && grep -q '^rc=0$' "${P}_ownbye.log"
item "发送端自己发 BYE 结束、15 毫秒之后到的询问：有应答（Tab5 收尾期间到的询问不丢）" $? "$(grep -a -o '自己发 BYE 之后问的那一次：.*' "${P}_ownbye.log" | head -1)"
grep -q '0 项不符' "${P}_bad.log" && grep -q '^rc=0$' "${P}_bad.log"; item "违规发送端 + 两种随机" $? "$(grep '^共 ' "${P}_bad.log")"
n_heap=$(grep -c '堆结构检查=' "$SERLOG"); n_ok=$(grep -c '堆结构检查=通过' "$SERLOG")
[ "$n_heap" = 2 ] && [ "$n_ok" = 2 ]; item "堆结构检查（违规发送端之后、全部跑完之后）" $? "$n_ok / $n_heap 次通过"
item "这次开机的复位原因是 USB（中途没有重启过）" "$(has "$SERLOG" '复位原因：USB')"
# 息屏那一段（3b）。(1)：串口里依次是 息屏 → 握手走完（配对过的）→ 被画面点亮；发送端 120 帧发够；之后主页面在前台、没暂停、等连接
disc_off=$(grep -a '次询问应答了' "${P}_offdisc.log" | head -1)
echo "$disc_off" | grep -q "5 次询问应答了 5 次，其中 5 次在 300 毫秒之内"
item "息屏着（等连接时到时间息的屏）：自动发现的 5 次询问都在 300 毫秒之内应答" $? "${disc_off:-没有应答}"
off1=$(seg "$OFF1_L0" "$OFF1B_L0" | awk '/scrmgr: screen off/ && st == 0 { st = 1 } /握手走完：.*配对过的/ && st == 1 { st = 2 }
    /scrmgr: wake（电脑的画面上屏）/ && st == 2 { st = 3 } /wdisp: 连接结束/ && st == 3 { st = 4 } END { print st + 0 }')
page_off=$(seg "$OFF1_L0" "$OFF1B_L0" | grep -a -o -m1 '无线显示页：.*')
case "$page_off" in 无线显示页：在前台，没暂停，状态「等待连接」，「开始显示」藏着*) pf=0 ;; *) pf=1 ;; esac
[ "$off1" = 4 ] && [ "$(sent offshow 120)" = 0 ] && [ $pf = 0 ]
item "息屏着电脑连上来：第一帧上屏时自动点亮，120 帧；连接结束后回到主页面、接着等" $? "走到第 $off1 步（共 4 步）；之后：${page_off:-（没有）}"
# (1b)：息屏 → 被画面点亮 → 退出手势 → wdisp 说没在监听 → 在主页面、屏幕亮着 → 主页面暂停着；
# 从连接结束到脚本点「开始显示」之间 wdisp 没打过「开始监听」；暂停着再连连不上
off1b=$(seg "$OFF1B_L0" "$OFF2_L0" | awk '/scrmgr: screen off/ && st == 0 { st = 1 } /scrmgr: wake（电脑的画面上屏）/ && st == 1 { st = 2 }
    /连接结束（退出手势）/ && st == 2 { st = 3 } /dbgcon: wd：没在监听/ && st == 3 { st = 4 } /open：记录的当前页是 wdisp，屏幕亮着/ && st == 4 { st = 5 }
    /无线显示页：在前台，已暂停，状态「已暂停」，「开始显示」露着/ && st == 5 { st = 6 }
    /^@@CMD wd start/ { started = 1 } /开始监听端口/ && st >= 3 && !started { relisten = 1 } END { print st + 0, relisten + 0 }')
[ "$off1b" = "6 0" ] && grep -q 'Tab5 退出了显示模式（BYE）' "${P}_offexit.log" && grep -q '暂停着重连：连不上' "${P}_offexit.log"
item "息屏着电脑连上来、显示中做退出手势：暂停住（没有又开始监听）、回到主页面、再连连不上" $? "走到第 ${off1b% *} 步（共 6 步），期间又开始监听 ${off1b#* } 次；$(grep -a -o '暂停着重连：.*' "${P}_offexit.log" | head -1)"
# (2)：息屏 → 等人输密码 → 亮屏（wake）→ 框开着 → 又息屏 → 框没开着 → 双击亮屏 → 框又开着（同一次询问）→ 配上
off2=$(seg "$OFF2_L0" "$OFF2_L1" | awk '/scrmgr: screen off/ && st == 0 { st = 1 } /配对：等人输密码/ && st == 1 { st = 2 } /scrmgr: wake$/ && st == 2 { st = 3 }
    /配对密码框：开着/ && st == 3 { st = 4 } /scrmgr: screen off/ && st == 4 { st = 5 } /配对密码框：没开着/ && st == 5 { st = 6 }
    /scrmgr: double tap -> wake/ && st == 6 { st = 7 } /配对密码框：开着/ && st == 7 { st = 8 } /握手走完：.*刚配对的/ && st == 8 { st = 9 } END { print st + 0 }')
asks_off=$(seg "$OFF2_L0" "$OFF2_L1" | grep -a -o '配对密码框：开着，标题「重新配对 [^，]*，第 [0-9]* 次询问，等人输入' | grep -o '第 [0-9]* 次' | paste -sd, -)
[ "$off2" = 9 ] && [ "$(echo "$asks_off" | tr ',' '\n' | wc -l)" = 2 ] && [ "$(echo "$asks_off" | tr ',' '\n' | sort -u | wc -l)" = 1 ] && [ "$(sent offpair 60)" = 0 ]
item "息屏着没配对的电脑连上来：亮屏、弹密码框；框开着又到时间息屏：框关掉；双击亮屏：框再弹出来（同一次询问），输对配上" $? "走到第 $off2 步（共 9 步）；两次 wd box：${asks_off:-没有}"
# (3)：点「移除」→ 框外那一下点在顶层（确认框开着）→ 息屏 → 双击点在当前页（黑屏页）→ 亮屏 → 框外那一下点在当前页（确认框没了）→ 配对还在
off3=$(seg "$OFF3_L0" "$OFF3_L1" | awk '/tap \(1157, 128\)：点到当前页的对象/ && st == 0 { st = 1 } /tap \(100, 600\)：点到顶层的对象/ && st == 1 { st = 2 }
    /scrmgr: screen off/ && st == 2 { st = 3 } /tap \(640, 400\)：点到当前页的对象/ && st == 3 { st = 4 } /scrmgr: double tap -> wake/ && st == 4 { st = 5 }
    /open：记录的当前页是 pairs，屏幕亮着/ && st == 5 { st = 6 }
    /tap \(100, 600\)：(点到当前页的对象|这一点上没有可点的对象)/ && st == 6 { st = 7 } /wd：配对过 [1-9] 台电脑/ && st == 7 { st = 8 } END { print st + 0 }')
[ "$off3" = 8 ] && seg "$OFF3_L0" "$OFF3_L1" | grep -a -m1 'open：记录的当前页是' | grep -aq 'open：记录的当前页是 pairs，屏幕亮着'
item "确认框开着时息屏：双击照样亮屏、回到「已配对的电脑」页（顶层的弹框息屏时关掉），没确认的操作不执行" $? "走到第 $off3 步（共 8 步）"
# 暂停 / 开始显示（wd pause 走的是 app::pause，和密码框上的取消同一条路）
pz1=$(seg "$PAUSE_L0" "$PAUSE_L1" | grep -a -o '无线显示页：.*' | sed -n 1p); pz2=$(seg "$PAUSE_L0" "$PAUSE_L1" | grep -a -o '无线显示页：.*' | sed -n 2p)
case "$pz1" in 无线显示页：在前台，已暂停，状态「已暂停」，「开始显示」露着*) z1=0 ;; *) z1=1 ;; esac
case "$pz2" in 无线显示页：在前台，没暂停，状态「等待连接」，「开始显示」藏着*) z2=0 ;; *) z2=1 ;; esac
[ $z1 = 0 ] && [ $z2 = 0 ]; item "暂停（wd pause）：主页面写「已暂停」、露出「开始显示」；点了之后回到「等待连接」" $? "暂停后：${pz1:-（没有）}；点了之后：${pz2:-（没有）}"
# 画质按钮用手指点：点第 4 段之后值是第 3 档、主页面上选中的是「最佳速度」；再点一次不变；点回原来的段之后值和选中的都回到原来那一档
segs=$(seg "$SEG_L0" "$SEG_L1" | grep -a -o 'dbgcon: wd：画质第 [0-3] 档' | grep -o '[0-3]' | tr '\n' ' ')
segpages=$(seg "$SEG_L0" "$SEG_L1" | grep -a -o '无线显示页：.*' | grep -o '画质「[^」]*」' | tr '\n' ' ')
case "$PIC_BEFORE" in 0) want_name=最佳画质 ;; 1) want_name=高画质 ;; 2) want_name=均衡 ;; *) want_name=最佳速度 ;; esac
[ "$segs" = "2 3 3 ${PIC_BEFORE:-3} " ] && [ "$segpages" = "画质「最佳速度」 画质「$want_name」 " ]
item "画质按钮用手指点（经输入设备）：点别的段就换过去并存下，再点已经选中的那一段值不变，点回原来的段恢复" $? "wd pic 报的档位依次：${segs:-（没有）}；主页面上选中的：${segpages:-（没有）}"
# 滚动加速：帧探针开着的那一段（滚动基准页上两次自动滚动，一下一上）里，位移帧全部走了加速。
# 固件在 prof on 时把统计清零、prof off 时打一行这一段的合计（scrlacc: window frames=… ok=… 加各种没走成加速的原因）：
# frames 是位移帧总数，ok 是其中走了加速的，两个数相等才算全部走了加速（退回整屏重绘、帧缓冲没接上的都在差额里）。
# 另外要求两次自动滚动都滚起来了、位移帧不少于 10 个：一帧都没有的话 frames = ok = 0 也相等。
# 只看最后一次 prof on 之后的日志，免得取到别处留下的 window 行
acc=$(tr -d '\r' < "$SERLOG" | awk '/^@@CMD prof on/ { n = 0; w = "" }
                                     /scrmgr: autoscroll dy=-?[1-9]/ { n++ }
                                     /scrlacc: window / { sub(/.*scrlacc: window /, ""); w = $0 }
                                     END { print n " 次滚动；" w }')
acc_fr=$(echo "$acc" | sed -n 's/.*frames=\([0-9]*\) .*/\1/p'); acc_ok=$(echo "$acc" | sed -n 's/.* ok=\([0-9]*\) .*/\1/p')
[ "${acc%% *}" = 2 ] && [ -n "$acc_fr" ] && [ "$acc_fr" = "$acc_ok" ] && [ "$acc_ok" -ge 10 ]
item "滚动加速（两次自动滚动的位移帧全部走了加速）" $? "$acc"
echo "共 $total 项，$bad 项不符"
[ "$bad" = 0 ]
