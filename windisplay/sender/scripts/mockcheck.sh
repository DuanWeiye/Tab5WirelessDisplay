#!/usr/bin/env bash
# C# 发送端核心（WinDisplay.Core，经命令行程序 wdsend）对着 Python 模拟接收端（windisplay/tools/wd_mock_tab5.py）的集成检查。
# 在 Linux 开发机上跑，不需要 Tab5。模拟接收端逐条检查发送端有没有守协议（含 Tab5 把条带交给硬件解码器之前那道 JPEG 文件头检查），
# 违反就把原因打出来、退出码 1。这里再核对：配对和认证（第一次配对、之后免密码、密码不对、没配对过、Tab5 那边忘了）、
# 收到的画面和发出的一致、Tab5 发 BYE / 掉线 / ACK 晚到时发送端的反应、输入事件的解析、
# 键盘指示灯、画面压不进上限和按 Ctrl-C 时的收尾、参数越界时的报错、自动发现和自动连接（auto / --stay，询问只发到本机回环）。
# 算法和收尾的细处另有单元测试：dotnet test（tests/WinDisplay.Tests）。
# 用法：scripts/mockcheck.sh          全部符合退出码 0。日志在 .work/mockcheck/（不入库）。
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
TOOLS="$HERE/../tools"
PY="${PY:-python3}"                             # 要有 numpy 和 PIL
DOTNET="${DOTNET:-dotnet}"
PORT="${PORT:-17251}"
OUT="$HERE/.work/mockcheck"
rm -rf "$OUT"; mkdir -p "$OUT"
"$DOTNET" build "$HERE/src/WinDisplay.Cli/WinDisplay.Cli.csproj" -c Release -v quiet -nologo > "$OUT/build.log" 2>&1 || { cat "$OUT/build.log"; exit 1; }
WDSEND="$HERE/src/WinDisplay.Cli/bin/Release/net10.0/wdsend.dll"
bad=0
n=0
total=0
PW=2468                                 # 配对密码
SENDER_STORE="$OUT/sender.json"         # 两边的配对存储：开头配对一次，后面各项都靠它免密码连
MOCK_STORE="$OUT/mock.json"

item() {    # item <名称> <0 = 符合> [附注]
    total=$((total + 1))
    if [ "$2" = 0 ]; then echo "[符合] $1${3:+：$3}"; else echo "[不符] $1${3:+：$3}"; bad=$((bad + 1)); fi
}

# run "<模拟接收端的参数>" "<wdsend 的参数>" ["<套在 wdsend 外面的命令>"]：起一个只服务一条连接的模拟接收端，跑一遍 wdsend。
# 结果：$S / $M = 两边的输出文件，$RS / $RM = 两边的退出码，$DIR = 这一轮的目录（模拟端存的画面也在里面）。
# 两边用的配对存储默认是开头配对好的那一对；要换（没配对过的、拷出来另用的）就在调用前面写 SSTORE=… MSTORE=…。
# 不填地址、靠自动发现找模拟端：调用前面写 HOSTARG="$AUTO"，模拟端的参数里加 --discoverable。
AUTO="auto --broadcast 127.0.0.1"       # 询问只发到本机回环，不往局域网里广播（那里可能有真的 Tab5）
run() {
    n=$((n + 1)); DIR="$OUT/$n"; mkdir -p "$DIR"; S="$DIR/wdsend.log"; M="$DIR/mock.log"
    # shellcheck disable=SC2086
    # -u：输出不缓冲。不然「监听」那一行要等它退出才写进文件，下面等它起来的循环就白等满 5 秒
    "$PY" -u "$TOOLS/wd_mock_tab5.py" --bind 127.0.0.1 --port "$PORT" --once --save-dir "$DIR" --pair-store "${MSTORE:-$MOCK_STORE}" $1 > "$M" 2>&1 &
    local mock=$!
    for _ in $(seq 50); do grep -q '^监听' "$M" 2>/dev/null && break; sleep 0.1; done
    # shellcheck disable=SC2086
    ${3:-} "$DOTNET" "$WDSEND" ${HOSTARG:-127.0.0.1} --port "$PORT" --pair-store "${SSTORE:-$SENDER_STORE}" $2 > "$S" 2>&1; RS=$?
    wait $mock; RM=$?
}
# mock_bg "<模拟接收端的参数>"：起一个一直服务下去的模拟接收端（一条连接结束了接着等下一条），用完 mock_end。$S / $M / $DIR 同 run。
mock_bg() {
    n=$((n + 1)); DIR="$OUT/$n"; mkdir -p "$DIR"; S="$DIR/wdsend.log"; M="$DIR/mock.log"
    # shellcheck disable=SC2086
    "$PY" -u "$TOOLS/wd_mock_tab5.py" --bind 127.0.0.1 --port "$PORT" --pair-store "${MSTORE:-$MOCK_STORE}" $1 > "$M" 2>&1 &     # -u：被 kill 时日志不丢
    MOCK=$!
    for _ in $(seq 50); do grep -q '^监听' "$M" 2>/dev/null && break; sleep 0.1; done
}
mock_end() { kill "$MOCK" 2>/dev/null; wait "$MOCK" 2>/dev/null; }
peers() { "$PY" -c 'import json, sys; print(len(json.load(open(sys.argv[1]))["peers"]))' "$1" 2>/dev/null || echo '?'; }      # 存储里有几条配对
num() { grep -o "$2" "$1" | grep -o '[0-9.]*' | head -1; }      # 从文件里按字样取第一个数

# 0. 第一次配对。「人」在 Tab5 上输密码花 8 秒（模拟端的默认值，比握手每一步的 5 秒长）：这期间发送端只能等 PAIR_REPLY，
#    等不及断开了、又发了报文，都算不符。配出来的密钥两边各自存下，后面各项就靠它
run "--vsync-hz 0 --password $PW" "--password $PW --frames 3"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '配对：密码对上了' "$S" && grep -q '刚配对' "$M" && [ "$(peers "$SENDER_STORE")" = 1 ] && [ "$(peers "$MOCK_STORE")" = 1 ]
item "第一次配对：等人在 Tab5 上输完密码（8 秒），两边存下配对" $? "退出码 $RS / $RM"
run "--vsync-hz 0" "--frames 3"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && ! grep -q '配对：' "$S" && ! grep -q '配对：\|刚配对' "$M" && grep -q '认证通过' "$S"
item "配对过之后再连：不用密码，Tab5 上不弹密码框" $? "退出码 $RS / $RM"

# 1. 局部更新：测试卡 30 帧，高质量。收到的画面应和发出的最后一帧几乎一样（转 90°、条带的位置、红蓝顺序都对才会一样）。
#    帧缓冲的两种格式各一遍（PROTOCOL.md「画面的色彩」）：默认走 YUV422（4:2:2、压到有限范围，模拟端展开回来再存）；--subsampling 444 走 RGB565
imgdiff() {     # imgdiff <图 1> <图 2>：平均每通道差
    "$PY" - "$1" "$2" <<'PYEOF'
import sys
import numpy as np
from PIL import Image
a, b = (np.asarray(Image.open(p).convert('RGB')).astype(int) for p in sys.argv[1:3])
print(f'{np.abs(a - b).mean():.2f}' if a.shape == b.shape else '999')
PYEOF
}
run "--vsync-hz 0" "--frames 30 --quality 92 --dump-last $OUT/sent_yuv.png"
diff=$(imgdiff "$OUT/sent_yuv.png" "$DIR/frame_last.png")
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '发送端 BYE' "$M" && grep -q '画面走 YUV422' "$M" && grep -q 'YUV422 帧缓冲' "$S"
item "测试卡 30 帧（YUV422 帧缓冲）：守协议、正常结束" $? "$(grep -o '[0-9]* 帧 [0-9]* 个条带' "$M")"
awk "BEGIN { exit !($diff < 2.0) }"; item "测试卡（YUV422 帧缓冲）：模拟端收到的画面展开之后和发出的最后一帧一致" $? "平均每通道差 $diff"
run "--vsync-hz 0" "--frames 30 --quality 92 --subsampling 444 --dump-last $OUT/sent.png"
diff=$(imgdiff "$OUT/sent.png" "$DIR/frame_last.png")
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '发送端 BYE' "$M" && grep -q '画面走 RGB565' "$M" && grep -q 'RGB565 帧缓冲（444）' "$S"
item "测试卡 30 帧（RGB565，4:4:4）：守协议、正常结束" $? "$(grep -o '[0-9]* 帧 [0-9]* 个条带' "$M")"
awk "BEGIN { exit !($diff < 2.0) }"; item "测试卡（RGB565）：模拟端收到的画面和发出的最后一帧一致" $? "平均每通道差 $diff"
strips=$(num "$M" '帧 [0-9]* 个条带'); [ "${strips:-999}" -lt $((8 + 29 * 4)) ]; item "测试卡：之后各帧只发了变化的条带" $? "$strips 个条带 / 30 帧"

# 2. 整屏滚动 20 帧，默认（YUV422 帧缓冲）
run "--vsync-hz 0" "--pattern scroll --frames 20"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '20 帧 160 个条带' "$M" && grep -q '画面走 YUV422' "$M"; item "整屏滚动 20 帧（YUV422 帧缓冲）" $? "$(grep -o '[0-9]* 帧 [0-9]* 个条带' "$M")"

# 3. RGB565：不要 YUV422（--rgb565）时采样按 --subsampling；只给 4:2:0 / 4:4:4 的采样也是 RGB565
run "--vsync-hz 0" "--pattern scroll --frames 5 --rgb565 --subsampling 422"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '画面走 RGB565' "$M" && grep -q 'RGB565 帧缓冲（422）' "$S"; item "整屏滚动 5 帧（RGB565，4:2:2）" $?
run "--vsync-hz 0" "--pattern scroll --frames 5 --subsampling 420"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '画面走 RGB565' "$M" && grep -q 'RGB565 帧缓冲（420）' "$S"; item "整屏滚动 5 帧（RGB565，4:2:0）" $?

# 3b. 加 YUV422 之前的固件（HELLO 里没说能走）：发送端不能在 AUTH 里说用它（说了模拟端判违规），照旧走 RGB565
run "--vsync-hz 0 --no-yuv" "--pattern scroll --frames 5"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '画面走 RGB565' "$M" && grep -q 'RGB565 帧缓冲（420）' "$S"; item "Tab5 没说能走 YUV422（老固件）：发送端照旧走 RGB565" $?

# 4. 条带超过单条带上限：自动对半拆
run "--vsync-hz 0 --max-strip 40960" "--pattern noise --frames 3 --quality 80"
strips=$(num "$M" '帧 [0-9]* 个条带'); [ "$RS" = 0 ] && [ "$RM" = 0 ] && [ "${strips:-0}" -gt 24 ]; item "噪点画面的条带超过 40 KB 上限时自动拆小" $? "$strips 个条带 / 3 帧"

# 5. 一帧超过单帧上限：自动降质量
run "--vsync-hz 0 --max-frame 307200" "--pattern noise --frames 3 --quality 80"
q=$(num "$S" '质量 [0-9]*'); [ "$RS" = 0 ] && [ "$RM" = 0 ] && [ "${q:-80}" -lt 80 ]; item "一帧超过 300 KB 上限时自动降质量重压" $? "质量降到 $q"

# 6. Tab5 那边用户退出了显示模式：第 5 帧之后发 BYE
run "--vsync-hz 0 --bye-after 5" "--frames 200 --fps 50"
[ "$RS" = 0 ] && grep -q 'Tab5 退出了显示模式' "$S" && grep -q '发了 5 帧' "$S"; item "Tab5 发 BYE 时正常收尾（退出码 0）" $? "退出码 $RS"

# 7. Tab5 掉线：第 5 帧之后直接断开
run "--vsync-hz 0 --drop-after 5" "--frames 200 --fps 50"
[ "$RS" = 2 ] && grep -q '连接中途断了' "$S"; item "连接中途断开时报出来（退出码 2）" $? "退出码 $RS"

# 流量控制（PROTOCOL.md「流量控制」）。发送端自己报的「先发了几次」之外，模拟端从接收这一头也量，连接结束时打印一行
# 「流量控制：N 帧是没等 ACK 就到了的；同时没回 ACK 的最多 M 帧，压住之后 a / b / c ms 各收到一帧」（wd_mock_tab5.py 开头有说明）
maxgap() { grep '相邻两帧发出的间隔' "$S" | grep -o '最大 [0-9.]*' | grep -o '[0-9.]*'; }
held() { grep '流量控制' "$M" | grep -o '压住之后 [0-9 /]* ms' | grep -o '[0-9]*' | tr '\n' ' '; }     # ACK 压住之后各帧到的时刻（毫秒）

# 8a. 平时保持 2 帧在途：模拟端一秒 20 个帧边界（比发送端编码一帧慢得多），每帧的 ACK 发出去之前下一帧都应该已经到了
#     （最后一帧后面没有帧，所以是 19 / 20）。--inflight 1 时收到 ACK 才发，一帧都不该提前到
run "--vsync-hz 20" "--pattern scroll --frames 20"
ahead=$(num "$M" '流量控制：[0-9]*'); early=$(num "$S" '先发下一帧：[0-9]*')
[ "$RS" = 0 ] && [ "$RM" = 0 ] && [ "${ahead:-0}" -ge 15 ] && [ "${early:-9}" = 0 ]      # 机器跑满时会少一两帧；收到 ACK 才发的是 0
item "平时保持 2 帧在途（不等上一帧的 ACK 就发下一帧）" $? "20 帧里 ${ahead:-?} 帧没等 ACK 就到了，先发了 ${early:-?} 次"
run "--vsync-hz 20" "--pattern scroll --frames 20 --inflight 1"
ahead=$(num "$M" '流量控制：[0-9]*'); early=$(num "$S" '先发下一帧：[0-9]*')
[ "$RS" = 0 ] && [ "$RM" = 0 ] && [ "${ahead:-9}" = 0 ] && [ "${early:-9}" = 0 ]
item "--inflight 1 时收到 ACK 才发下一帧" $? "20 帧里 ${ahead:-?} 帧没等 ACK 就到了"

# 8. 第 8 帧的 ACK 晚 0.6 秒才到（它在空中丢了、等 Tab5 的 TCP 重传）：这时在途的是第 8、9 两帧；第 8 帧发出 60 ms 还没确认，
#    发送端接着发第 10、11 两帧（到 4 帧在途为止）。多发的两帧要过了门限才发、而且是连着发的（隔一帧的编码时间）：
#    门限从最早在途的那一帧算起；要是从最近发出的那一帧算起（发一帧再等一个门限），两帧会隔 60 ms 以上。
#    空闲时隔 3 ms 左右，机器跑满时见过 23 ms，判定线取 45 ms
run "--vsync-hz 0 --late-ack 8:0.6" "--pattern scroll --frames 16"
early=$(num "$S" '先发下一帧：[0-9]*'); gap=$(maxgap); unacked=$(num "$M" '同时没回 ACK 的最多 [0-9]*'); read -r -a H <<< "$(held)"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && [ "${early:-0}" = 2 ] && [ "${unacked:-0}" = 4 ] && awk "BEGIN { exit !(${gap:-0} > 200 && ${gap:-0} < 600) }"
item "一个 ACK 晚到 0.6 秒时接着发后面的帧（多发 2 帧，到 4 帧在途为止）" $? "先发了 $early 次，同时没回 ACK 的最多 $unacked 帧，画面最长停 $gap ms"
[ "${#H[@]}" = 3 ] && [ "${H[1]}" -ge 45 ] && [ $((H[2] - H[1])) -lt 45 ]
item "迟到之后多发的两帧是过了门限连着发的" $? "ACK 压住之后 ${H[*]} ms 各收到一帧"
# 对照一：--inflight 1（平时只有 1 帧在途）时，压住之后到的 3 帧都是迟到多发的
run "--vsync-hz 0 --late-ack 8:0.6" "--pattern scroll --frames 16 --inflight 1"
early=$(num "$S" '先发下一帧：[0-9]*'); unacked=$(num "$M" '同时没回 ACK 的最多 [0-9]*'); read -r -a H <<< "$(held)"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && [ "${early:-0}" = 3 ] && [ "${unacked:-0}" = 4 ] && [ "${#H[@]}" = 3 ] && [ "${H[0]}" -ge 45 ]
item "对照：--inflight 1 时 ACK 晚到后多发 3 帧" $? "先发了 $early 次，ACK 压住之后 ${H[*]} ms 各收到一帧"
# 对照二：死等 ACK（--max-inflight 1）时一帧都不多发，画面停满 0.6 秒
run "--vsync-hz 0 --late-ack 8:0.6" "--pattern scroll --frames 16 --max-inflight 1"
gap=$(maxgap); unacked=$(num "$M" '同时没回 ACK 的最多 [0-9]*')
[ "$RS" = 0 ] && [ "$RM" = 0 ] && [ "${unacked:-0}" = 1 ] && awk "BEGIN { exit !(${gap:-0} >= 590) }"
item "对照：死等 ACK 时画面停满 0.6 秒" $? "画面最长停 $gap ms，同时没回 ACK 的最多 $unacked 帧"

# 9. 每帧的 ACK 本来就慢（模拟端等帧边界，一秒 5 次）：不算迟到，不多压帧
run "--vsync-hz 5" "--pattern scroll --frames 10"
early=$(num "$S" '先发下一帧：[0-9]*'); [ "$RS" = 0 ] && [ "$RM" = 0 ] && [ "${early:-9}" = 0 ]; item "每帧的 ACK 本来就慢时不多压帧" $? "先发了 $early 次"

# 10. 输入事件：模拟端在第 3 帧之后发一组触摸 / 键盘 / HELD / 鼠标报文
run "--vsync-hz 0 --inject-input" "--frames 10 --fps 20"
ok=0
for want in 'TOUCH #0(640,360)' 'TOUCH #0(650,360) #1(100,700)' 'TOUCH 全部抬起' 'KEY 02 00 04 00 00 00 00 00' '← HELD' 'KEY 00 00 00 00 00 00 00 00' 'MOUSE dx=5 dy=-3 wheel=0 buttons=0x01'; do
    grep -qF "$want" "$S" || ok=1
done
[ "$RS" = 0 ] && [ "$RM" = 0 ] && [ $ok = 0 ]; item "解析出触摸 / 键盘 / HELD / 鼠标事件" $?

# 10b. 触摸方式：Tab5 握手走完报「触摸板」，发送端在第一份触摸之前收到；报一个没有的方式算不守协议
run "--vsync-hz 0 --inject-input --input-mode 1" "--frames 10 --fps 20"
mode_at=$(grep -n -m1 -F '← INPUT_MODE 触摸板' "$S" | cut -d: -f1); touch_at=$(grep -n -m1 -F 'TOUCH #0(640,360)' "$S" | cut -d: -f1)
[ "$RS" = 0 ] && [ "$RM" = 0 ] && [ -n "$mode_at" ] && [ -n "$touch_at" ] && [ "$mode_at" -lt "$touch_at" ]
item "Tab5 报的触摸方式（触摸板）排在第一份触摸之前收到" $? "第 ${mode_at:-?} 行 / 第 ${touch_at:-?} 行"
run "--vsync-hz 0 --input-mode 2" "--frames 10 --fps 20"
[ "$RS" = 2 ] && grep -q 'INPUT_MODE' "$S"; item "INPUT_MODE 报了一个没有的方式时断开（退出码 2）" $? "退出码 $RS"

# 10c. 触摸板加速：Tab5 握手走完报「最多 1.5 倍」，发送端在第一份触摸之前收到；报一个范围外的倍数算不守协议
run "--vsync-hz 0 --inject-input --input-mode 1 --tpad-gain 15" "--frames 10 --fps 20"
gain_at=$(grep -n -m1 -F '← TPAD_GAIN 1.5 倍' "$S" | cut -d: -f1); touch_at=$(grep -n -m1 -F 'TOUCH #0(640,360)' "$S" | cut -d: -f1)
[ "$RS" = 0 ] && [ "$RM" = 0 ] && [ -n "$gain_at" ] && [ -n "$touch_at" ] && [ "$gain_at" -lt "$touch_at" ]
item "Tab5 报的触摸板加速（最多 1.5 倍）排在第一份触摸之前收到" $? "第 ${gain_at:-?} 行 / 第 ${touch_at:-?} 行"
run "--vsync-hz 0 --tpad-gain 9" "--frames 10 --fps 20"
[ "$RS" = 2 ] && grep -q 'TPAD_GAIN' "$S"; item "TPAD_GAIN 报了一个范围外的倍数时断开（退出码 2）" $? "退出码 $RS"

# 10d. 画质：Tab5 握手走完报「质量 85、动态预算不降」，发送端之后的帧按 85 压（头一两帧可能还是报之前的 60）；
#      命令行给了 --quality 的不声明认得这个报文（画质定死），Tab5 就不报；报一个范围外的质量算不守协议
run "--vsync-hz 0 --picture 85,0" "--frames 20 --fps 20 -v"
q_first=$(grep -o -m1 '；质量 [0-9]*' "$S" | grep -o '[0-9]*'); q_last=$(grep -o '；质量 [0-9]*' "$S" | tail -1 | grep -o '[0-9]*')
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q -F '← PICTURE 质量 85，预算 不降' "$S" && [ "$q_last" = 85 ] && [ "$(grep -c '；质量 85，预算 不降' "$S")" -ge 15 ]
item "Tab5 报的画质（质量 85）之后的帧就按它压" $? "第一帧质量 ${q_first:-?}，最后一帧 ${q_last:-?}，按 85 压的 $(grep -c '；质量 85，预算 不降' "$S") 帧"
run "--vsync-hz 0 --picture 85,0" "--frames 20 --fps 20 -v --quality 50"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && ! grep -q 'PICTURE' "$S" && grep -q '发送端没说认得 PICTURE' "$M" && [ "$(grep -c '；质量 50，预算' "$S")" = 20 ]
item "命令行给了 --quality 的发送端不声明认得 PICTURE，Tab5 不报，各帧都按命令行的质量" $? "按 50 压的 $(grep -c '；质量 50，预算' "$S") 帧"
run "--vsync-hz 0 --picture 101,40" "--frames 10 --fps 20"
[ "$RS" = 2 ] && grep -q 'PICTURE' "$S"; item "PICTURE 报了一个范围外的质量时断开（退出码 2）" $? "退出码 $RS"

# 10e. 画质的四档（现在的固件）：Tab5 握手走完报第几档 + 屏幕的帧周期，发送端按档位限帧率、定画面在动时的预算。
#      整屏滚动的画面要多快给多快：不限的话一秒一百多帧，限了就是屏幕刷新率的 1/2、2/3
gapmid() { grep '相邻两帧发出的间隔' "$S" | grep -o '中位 [0-9.]*' | grep -o '[0-9.]*'; }
within() { "$PY" -c 'import sys; v, lo, hi = (float(x) for x in sys.argv[1:]); sys.exit(0 if lo <= v <= hi else 1)' "${1:-0}" "$2" "$3"; }   # within <数> <下限> <上限>
run "--vsync-hz 0 --picture-mode 1" "--pattern scroll --frames 100 -v"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q -F '← PICTURE_MODE 第 1 档「高画质」，屏幕的帧周期 15270 微秒' "$S" && grep -q '限 32.7 帧/秒' "$S" && within "$(gapmid)" 29.5 31.5
item "Tab5 报画质第 1 档（高画质）：每两次屏幕刷新发一帧" $? "相邻两帧发出的间隔中位 $(gapmid) ms（应为 30.5）"
run "--vsync-hz 0 --picture-mode 2 --frame-us 16667" "--pattern scroll --frames 100 -v"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q -F '← PICTURE_MODE 第 2 档「均衡」，屏幕的帧周期 16667 微秒' "$S" && grep -q '限 40.0 帧/秒' "$S" && within "$(gapmid)" 24 26
item "Tab5 报画质第 2 档（均衡），屏幕 60 Hz：每三次刷新发两帧，节拍按它报的帧周期算" $? "相邻两帧发出的间隔中位 $(gapmid) ms（应为 25.0）"
run "--vsync-hz 0 --picture-mode 0" "--pattern scroll --frames 40 -v"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '第 0 档「最佳画质」.*不限帧率，画面在动也不降质量' "$S" && [ "$(grep -c '；质量 90，预算 不降' "$S")" -ge 35 ]
item "Tab5 报画质第 0 档（最佳画质）：不限帧率，整屏在动也按 90 压" $? "按 90、不降质量压的 $(grep -c '；质量 90，预算 不降' "$S") 帧"
run "--vsync-hz 0 --picture-mode 3" "--pattern scroll --frames 60 -v"
kb_hi=$(grep -o '，预算 [0-9]* KB' "$S" | grep -o '[0-9]*' | sort -n | tail -1); kb_lo=$(grep -o '，预算 [0-9]* KB' "$S" | grep -o '[0-9]*' | sort -n | head -1)
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '第 3 档「最佳速度」.*不限帧率，画面在动时每帧预算在 20~60 KB 里调' "$S" && [ "${kb_lo:-0}" -ge 20 ] && [ "${kb_hi:-99}" -le 60 ]
# 这一项只证明这一档接通了（发送端说了它的做法、帧上带的预算是这一档的）；闭环降不降得下来、停在哪儿是 BudgetControlTests 和真机量的事
item "Tab5 报画质第 3 档（最佳速度）：发送端按这一档走（不限帧率，帧上带的预算是这一档 20~60 KB 里的数）" $? "用过的预算 ${kb_lo:-?}~${kb_hi:-?} KB"
run "--vsync-hz 0 --picture-mode 2" "--pattern scroll --frames 20 --fps 20 -v --quality 50"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && ! grep -q 'PICTURE' "$S" && [ "$(grep -c '；质量 [0-9]*，预算 40 KB' "$S")" = 20 ]
item "命令行给了 --quality 的发送端两个画质的报文都不声明认得，Tab5 不报档位，预算照命令行的固定" $? "预算 40 KB 的 $(grep -c '；质量 [0-9]*，预算 40 KB' "$S") 帧"
run "--vsync-hz 0 --picture-mode 4" "--frames 10 --fps 20"
[ "$RS" = 2 ] && grep -q 'PICTURE_MODE' "$S"; item "PICTURE_MODE 报了没有的档位时断开（退出码 2）" $? "退出码 $RS"
run "--vsync-hz 0 --picture-mode 2 --frame-us 4000" "--frames 10 --fps 20"
[ "$RS" = 2 ] && grep -q 'PICTURE_MODE' "$S"; item "PICTURE_MODE 里屏幕的帧周期在范围外时断开（退出码 2）" $? "退出码 $RS"

# 11. 键盘指示灯：连上后发一次 Num + Caps
run "--vsync-hz 0" "--frames 3 --kbd-leds 3"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q 'KBD_LEDS：Num=1 Caps=1 Scroll=0' "$M"; item "键盘指示灯状态发到了 Tab5" $?

# 11b. 声音：画面之外同时发一个生成的正弦波（--tone，10 ms 一段，按墙上的钟发）。限 20 帧/秒发 40 帧（约 2 秒）：
#      模拟端收到的声音和发送端说发了的一样长（它逐个报文核对认证码，声音和条带两个线程发、先后乱了就过不了）
run "--vsync-hz 0" "--pattern scroll --frames 40 --fps 20 --tone 1000"
sent=$(num "$S" '声音：发了 [0-9.]* 秒'); got=$(num "$M" '个报文，[0-9.]* 秒')
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '扔掉 0 ms' "$S" \
    && "$PY" -c 'import sys; s, g = float(sys.argv[1]), float(sys.argv[2]); sys.exit(0 if s >= 1.5 and abs(s - g) <= 0.06 else 1)' "${sent:-0}" "${got:-0}"
item "声音：画面 + 正弦波一起发，Tab5 收到的和发出的一样长" $? "发了 ${sent:-?} 秒，收到 ${got:-?} 秒，退出码 $RS / $RM"
# Tab5 没说能收声音（加声音之前的固件，HELLO 的能力位是 0）：不发，画面照常
run "--vsync-hz 0 --no-audio" "--frames 6 --tone 1000"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '没说能收声音' "$S" && grep -q '声音：发了 0.0 秒' "$S" && ! grep -q '个报文，' "$M"
item "声音：Tab5 没说能收声音（旧固件）时不发，画面照常" $? "退出码 $RS / $RM"

# 12. 画面怎么压都超过单帧上限（噪点、上限 20 KB）：报错退出，但连接要收干净——模拟端看到的是 BYE 而不是断线
run "--vsync-hz 0 --max-frame 20480" "--pattern noise --frames 3"
[ "$RS" = 1 ] && grep -q '单帧上限' "$S" && grep -q '发送端 BYE' "$M"; item "画面压不进单帧上限时报错（退出码 1），连接照样收干净" $? "退出码 $RS"

# 13. 没发完按 Ctrl-C（2 秒后给它发 SIGINT）：先发 BYE 再走，退出码 130
run "--vsync-hz 0" "--frames 100000 --fps 20" "timeout --preserve-status -s INT 2"
[ "$RS" = 130 ] && [ "$RM" = 0 ] && grep -q '按了 Ctrl-C' "$S" && grep -q '发送端 BYE' "$M"; item "没发完按 Ctrl-C：发 BYE 后结束（退出码 130）" $? "退出码 $RS"

# 14. Tab5 没开「无线显示」页（没人监听）
"$DOTNET" "$WDSEND" 127.0.0.1 --port "$PORT" --pair-store "$SENDER_STORE" --frames 3 > "$OUT/noconn.log" 2>&1; RS=$?
[ "$RS" = 1 ] && grep -q '没连上' "$OUT/noconn.log"; item "没人监听时报没连上（退出码 1）" $? "退出码 $RS"

# 15. 参数不合理：连接之前就报出来、打印用法、退出码 1（不是抛一个没人接的异常）
ok=0; note=; kinds=0
for a in "--max-inflight 0" "--inflight 0" "--inflight 5" "--inflight 3 --max-inflight 2" "--port 99999999999" "--port 0" "--quality 101" "--strip-h 100" "--ack-wait 0" "--frames abc" "--fps" \
         "--frames -5" "--seconds -1" "--screen --pattern card" "--screen --fps 10" "--screen --dump-last x.png" "--screen-check" \
         "--allow-plaintext" "--password" "--password 密码" "--pair-store" "--name" \
         "--stay" "--broadcast 127.0.0.1" "--broadcast"; do
    # shellcheck disable=SC2086
    "$DOTNET" "$WDSEND" 127.0.0.1 --pair-store "$SENDER_STORE" $a > "$OUT/badarg.log" 2>&1; RS=$?
    kinds=$((kinds + 1))
    if [ "$RS" != 1 ] || ! grep -q '^用法' "$OUT/badarg.log" || grep -qi 'unhandled' "$OUT/badarg.log"; then ok=1; note="$note [$a → 退出码 $RS]"; fi
done
item "参数不合理时报错并打印用法（退出码 1）" $ok "${note:-$kinds 种写法}"
# --inflight 比 --max-inflight 多：报错里说的是人给的那个选项（--inflight），不是它没碰过的 --max-inflight
"$DOTNET" "$WDSEND" 127.0.0.1 --pair-store "$SENDER_STORE" --inflight 5 > "$OUT/badarg.log" 2>&1
head -1 "$OUT/badarg.log" | grep -q -- '^--inflight 要在 1 和 --max-inflight（4）之间'
item "--inflight 超出范围时报错里说的是 --inflight" $? "$(head -1 "$OUT/badarg.log")"

# 16. --seconds：到时间就正常收尾（发 BYE、退出码 0）；--frames 0 = 帧数不限（要是被当成「一帧都不发」，这里一帧也收不到）
run "--vsync-hz 0" "--pattern card --frames 0 --fps 20 --seconds 2"
sec=$(num "$S" '用时 [0-9.]* 秒'); fr=$(num "$S" '发了 [0-9]* 帧')
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '^画面发完' "$S" && awk -v s="${sec:-0}" -v f="${fr:-0}" 'BEGIN { exit !(s >= 1.5 && s <= 3.5 && f >= 20 && f <= 60) }'
item "--frames 0 --seconds 2：帧数不限，两秒后正常收尾" $? "${fr:-?} 帧，用时 ${sec:-?} 秒，退出码 $RS"

# 17. 这一份（net10.0，不带抓屏）遇到 --screen / --screen-check：说清楚要用 Windows 版，退出码 1，不去连 Tab5
"$DOTNET" "$WDSEND" 127.0.0.1 --port "$PORT" --pair-store "$SENDER_STORE" --screen > "$OUT/noscreen.log" 2>&1; RS=$?
"$DOTNET" "$WDSEND" --screen-check > "$OUT/nocheck.log" 2>&1; RS2=$?
[ "$RS" = 1 ] && [ "$RS2" = 1 ] && grep -q '不带抓屏' "$OUT/noscreen.log" && grep -q '不带抓屏' "$OUT/nocheck.log"
item "不带抓屏的这一份遇到 --screen / --screen-check 时说明并退出（退出码 1）" $? "退出码 $RS / $RS2"

# 18. 没配对过、又没给密码：不配对，退出码 3，告诉人要给 --password。Tab5 上不弹密码框
SSTORE="$OUT/new_sender.json" MSTORE="$OUT/new_mock.json" run "--vsync-hz 0 --password $PW --password-delay 0" "--frames 3"
[ "$RS" = 3 ] && [ "$RM" = 0 ] && grep -q '认证没过' "$S" && grep -q -- '--password' "$S" && ! grep -q '配对：' "$M"
item "没配对过又没给密码：不配对（退出码 3）" $? "退出码 $RS / $RM"

# 19. Tab5 上输的密码一直不对：满 10 次就断开，退出码 3，什么都不存
SSTORE="$OUT/new_sender.json" MSTORE="$OUT/new_mock.json" run "--vsync-hz 0 --password 0000 --password-delay 0" "--password $PW --frames 3"
wrong=$(grep -c '^配对：Tab5 上输的密码不对' "$S")
[ "$RS" = 3 ] && [ "$RM" = 0 ] && [ "$wrong" = 10 ] && [ "$(peers "$OUT/new_sender.json")" = 0 ] && [ "$(peers "$OUT/new_mock.json")" = 0 ]
item "密码一直不对：试满 10 次就断开（退出码 3），两边都不存" $? "不对 $wrong 次，退出码 $RS / $RM"

# 20. Tab5 上的人在密码框上点了取消（模拟端没给密码）：是 Tab5 那边结束的，退出码 0
SSTORE="$OUT/new_sender.json" MSTORE="$OUT/new_mock.json" run "--vsync-hz 0 --password-delay 0" "--password $PW --frames 3"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q 'Tab5 那边的人结束了连接' "$S" && grep -q '密码框上点了取消，发了 BYE，之后发送端关了连接' "$M"
item "Tab5 上点了取消：收到 BYE 就关连接（退出码 0）" $? "退出码 $RS / $RM"

# 21. Tab5 那边把这台电脑忘了（拷一份配对好的存储出来，模拟端 --forget）。没给密码：退出码 3，电脑这边的配对不删；
#     给了密码：同一条连接上重新配对，接着发画面
cp "$SENDER_STORE" "$OUT/kept_sender.json"; cp "$MOCK_STORE" "$OUT/forgot_mock.json"
SSTORE="$OUT/kept_sender.json" MSTORE="$OUT/forgot_mock.json" run "--vsync-hz 0 --forget --password $PW --password-delay 0" "--frames 3"
[ "$RS" = 3 ] && [ "$RM" = 0 ] && grep -q '认证没过' "$S" && cmp -s "$SENDER_STORE" "$OUT/kept_sender.json"
item "Tab5 那边没有这台电脑的配对了、没给密码：报出来（退出码 3），电脑这边的配对不删" $? "退出码 $RS / $RM"
SSTORE="$OUT/kept_sender.json" MSTORE="$OUT/forgot_mock.json" run "--vsync-hz 0 --password $PW --password-delay 0" "--password $PW --frames 3"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '配对：密码对上了' "$S" && grep -q '刚配对' "$M" && ! cmp -s "$SENDER_STORE" "$OUT/kept_sender.json"
item "Tab5 那边没有这台电脑的配对了、给了密码：同一条连接上重新配对" $? "退出码 $RS / $RM"

# 22. --forget：先忘掉所有配对。没给密码就连不了（退出码 3）
cp "$SENDER_STORE" "$OUT/forget_sender.json"
SSTORE="$OUT/forget_sender.json" run "--vsync-hz 0" "--forget --frames 3"
[ "$RS" = 3 ] && [ "$RM" = 0 ] && [ "$(peers "$OUT/forget_sender.json")" = 0 ]
item "--forget：忘掉所有配对，之后没给密码连不了（退出码 3）" $? "退出码 $RS / $RM"

# 23. 自动发现：不填地址，问到在等连接的模拟端，配对过的免密连上
HOSTARG="$AUTO" run "--vsync-hz 0 --discoverable" "--frames 3"
[ "$RS" = 0 ] && [ "$RM" = 0 ] && grep -q '^1 台 Tab5 应答' "$S" && grep -q '认证通过' "$S" && ! grep -q '配对：' "$S"
item "auto：不填地址，询问找到 Tab5，配对过的免密连上" $? "退出码 $RS / $RM"

# 24. 应答的那台没配对过、又没给密码：不去连（退出码 1），Tab5 那边一条连接都没进来；--broadcast 写得不对：打印用法
MSTORE="$OUT/new_mock.json" mock_bg "--vsync-hz 0 --discoverable"
"$DOTNET" "$WDSEND" $AUTO --port "$PORT" --pair-store "$OUT/new_sender.json" --frames 3 > "$S" 2>&1; RS=$?
"$DOTNET" "$WDSEND" auto --broadcast 局域网 --port "$PORT" --pair-store "$OUT/new_sender.json" --frames 3 > "$DIR/badaddr.log" 2>&1; RS2=$?
mock_end
[ "$RS" = 1 ] && grep -q '都没配对过' "$S" && [ "$RS2" = 1 ] && grep -q '^用法' "$DIR/badaddr.log" && ! grep -q '连上了' "$M"
item "auto：应答的 Tab5 没配对过又没给密码时不去连（退出码 1）" $? "退出码 $RS / $RS2"

# 24b. 应答里报的编号和连上之后 HELLO 里报的不一样（真机两处报的是同一个）：模拟端 HELLO 里是配对过的那个编号、认证本来过得了，
#      应答里报的是另一个。连不连它、它是哪一台是按应答里的编号定的，所以发送端看到 HELLO 就断开，认证不走、一帧不发（退出码 2）
HOSTARG="$AUTO" run "--vsync-hz 0 --discoverable --announce-id 5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a" "--password $PW --frames 3"
[ "$RS" = 2 ] && grep -q 'HELLO 里的 Tab5 编号' "$S" && ! grep -q '认证通过\|配对：' "$S" && grep -q '连接断开）：0 帧' "$M" && ! grep -q '配对：\|刚配对' "$M"
item "auto：HELLO 里的编号不是应答里的那个时不认证、不配对就断开（退出码 2）" $? "退出码 $RS / $RM"

# 25. 没有 Tab5 应答（没人在等连接）：问 3 轮就报出来，退出码 1
"$DOTNET" "$WDSEND" $AUTO --port "$PORT" --pair-store "$SENDER_STORE" --frames 3 > "$OUT/noanswer.log" 2>&1; RS=$?
[ "$RS" = 1 ] && grep -q '没有 Tab5 应答' "$OUT/noanswer.log"; item "auto：没有 Tab5 应答时报出来（退出码 1）" $? "退出码 $RS"

# 26. 一直自动连接：模拟端每显示 5 帧就发 BYE（页面还开着，接着等连接）。发送端每次都马上再问、问到了重新连上，
#     直到 40 帧画面发完（退出码 0）；中间没有哪一轮问不到——模拟端和真机一样，BYE 一发出去就算回到等连接、之后到的询问都应答
mock_bg "--vsync-hz 0 --discoverable --bye-after 5"
"$DOTNET" "$WDSEND" $AUTO --stay --port "$PORT" --pair-store "$SENDER_STORE" --frames 40 --fps 50 > "$S" 2>&1; RS=$?
mock_end
again=$(grep -c '马上再找' "$S"); noans=$(grep -c '没有 Tab5 应答' "$S"); conns=$(grep -c '连上了$' "$M")
[ "$RS" = 0 ] && grep -q '^画面发完' "$S" && [ "$again" -ge 2 ] && [ "$noans" = 0 ] && [ "$conns" = $((again + 1)) ] && ! grep -q '✗' "$M"
item "auto --stay：Tab5 发 BYE 之后马上再问、重新连上，直到画面发完（退出码 0）" $? "重连 $again 次，问不到 $noans 轮，模拟端收到 $conns 条连接，退出码 $RS"

echo "共 $total 项，$bad 项不符"
[ "$bad" = 0 ]
