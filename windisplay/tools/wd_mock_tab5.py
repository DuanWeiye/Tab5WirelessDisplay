#!/usr/bin/env python3
"""模拟的 Tab5 无线显示接收端（协议见 ../PROTOCOL.md），在电脑上跑，给发送端开发和调试用。

它照协议握手（配对、认证、换密钥）、收条带、解码、拼成帧缓冲、回 ACK，并且**逐条检查发送端有没有守协议**（握手每一步
该来什么、条带宽 720、高和起始行是 16 的倍数、基线 JPEG、色度采样、不超过 HELLO 里报的上限、同一帧条带不重叠、
第一帧覆盖整屏、READY 之后 5 秒内发完第一帧……）。发现违反就打印原因并断开。
配对时「人在 Tab5 上输的密码」用 --password 给，--password-delay 是人输它花的时间（密码框开着的这段时间里发送端只能等，
发了东西就算违规）；配对存在 --pair-store 指的文件里（默认是本目录的 .wdpair_mock.json，不入库），和真机一样最多记 4 台电脑。
它量不出真机的解码和上屏耗时：ACK 里的解码时间是 PIL 软解的，上屏时间按 --vsync-hz 模拟等到下一个帧边界。

用法举例：
  wd_mock_tab5.py --password 1234                 # 监听 0.0.0.0:7250，一直服务；发送端要配对时输 1234（输它花 8 秒，见 --password-delay）
  wd_mock_tab5.py --password 1234 --discoverable  # 另外应答局域网里的自动发现询问（等连接的时候才应答）
  wd_mock_tab5.py --password 1234 --password-delay 20   # 人输密码花 20 秒（到 120 秒 = 人没输完，密码框关掉）；给 0 = 秒回，测不出发送端等 PAIR_REPLY 的耐心
  wd_mock_tab5.py --save-dir /tmp/wd --save-every 30   # 每 30 帧把收到的画面（转回横屏）存一张 PNG
  wd_mock_tab5.py --inject-input                  # 第 3 帧之后发一组触摸 / 键盘 / HELD / 鼠标报文，测发送端的输入解析
  wd_mock_tab5.py --inject-input --inject-at 1    # 改成第 1 帧之后发（发送端的画面要等输入事件来了才变的时候，比如触摸回显）
  wd_mock_tab5.py --bye-after 100                 # 第 100 帧之后发 BYE 并断开 = 用户在 Tab5 上退出了（发送端不许自动重连）
  wd_mock_tab5.py --drop-after 100                # 第 100 帧之后直接断开 = Tab5 掉线了（发送端可以自动重连）
  wd_mock_tab5.py --late-ack 50:1.3               # 第 50 帧的 ACK 晚 1.3 秒才到 = 它在空中丢了，等 Tab5 的 TCP 重传
                                                  #（测发送端的流量控制：这期间应接着发后面的帧，见 PROTOCOL.md）

发送端的流量控制（PROTOCOL.md「流量控制」）它从接收的这一头量三样，连接结束时打印出来：
  · 「没等 ACK 就到了的」帧数：一帧的 ACK 发出去之前，发送端后面的数据已经到了 = 发送端没等这个 ACK 就发了下一帧。
    平时 2 帧在途的发送端几乎每帧都是这样，收到 ACK 才发的是 0。（要帧边界比发送端编码一帧慢才量得准，--vsync-hz 0 时量不准；
    看的是「套接字上有没有数据可读」，发送端在两帧之间发了别的报文（比如 KBD_LEDS）也会算进去。）
  · 「同时没回 ACK 的最多几帧」：收到一帧时，连它在内 ACK 还没发出去的帧数。平时是 1（一帧一帧地收、回）；--late-ack 把一个 ACK
    压住之后，后面各帧的 ACK 都排在它后面，这个数 = 发送端这时的在途帧数（迟到时最多放到几帧）。
  · 压住之后收到的各帧离压住那一刻多久：发送端是到了门限连着多发，还是发一帧再等一个门限。
"""
import argparse
import io
import os
import select
import socket
import sys
import threading
import time

from PIL import Image

import wdproto as wp

# 协议允许的色度采样：亮度分量的（水平, 垂直）采样因子
ALLOWED_SAMPLING = {(1, 1): '4:4:4', (2, 1): '4:2:2', (2, 2): '4:2:0'}


class Options:
    """模拟接收端的参数（命令行和自测共用）。"""

    def __init__(self, max_strip=256 * 1024, max_frame=1024 * 1024, vsync_hz=66.0,
                 save_dir=None, save_every=50, inject_input=False, quiet=False, stall_s=wp.STALL_S,
                 first_frame_s=wp.FIRST_FRAME_S, bye_after=0, drop_after=0, inject_at=3, inject_msgs=None, late_ack=None,
                 store=None, name='Tab5-mock', passwords=(), handshake_s=wp.HANDSHAKE_S, password_delay=0.0,
                 password_box_s=wp.PASSWORD_BOX_S, over=None, tail_s=0.0, input_mode=None, tpad_gain=None,
                 picture=None, audio=True, keep_audio=False, yuv=True, picture_mode=None, frame_us=15270):
        # 自己的编号和配对过的电脑（和真机一样最多记 4 台）
        self.store = store if store is not None else wp.PairStore(max_peers=wp.MAX_TAB5_PEERS)
        self.name = name                        # HELLO 里报的名字
        self.passwords = list(passwords)        # 配对时「人」依次输的密码；输完了 = 点取消
        self.handshake_s = handshake_s          # 握手每一步的时限（真机是 5 秒；自测用短的）
        self.password_delay = password_delay    # 人输一次密码花多少秒（密码框开着的时间）
        self.password_box_s = password_box_s    # 密码框最多开多久（真机是 2 分钟；自测用短的），到时间人还没输完就关掉、断开
        self.max_strip = max_strip
        self.max_frame = max_frame
        self.vsync_hz = vsync_hz
        self.save_dir = save_dir
        self.save_every = save_every
        self.inject_input = inject_input
        # 握手走完之后报的触摸方式（INPUT_MODE 的 arg：0 触摸、1 触摸板；别的数是故意不守协议）；None = 不报，像老固件。
        # 和真机一样，发送端没在 AUTH 里说认得这个报文就不发
        self.input_mode = input_mode
        # 握手走完之后报的触摸板加速（TPAD_GAIN 的 arg，0.1 倍，10~50；范围外的数是故意不守协议）；None = 不报。同样看发送端的能力位
        self.tpad_gain = tpad_gain
        # 握手走完之后报的画质：(画面不动时的 JPEG 质量 1~100, 画面在动时每帧的预算 KB，0 = 不降)；质量给范围外的数是故意不守协议。
        # None = 不报。同样看发送端的能力位
        self.picture = picture
        # 握手走完之后报画质的档位（现在的固件）：认得 PICTURE_MODE 的发送端收到「第几档 + 屏幕的帧周期 frame_us」，
        # 只认得 PICTURE 的收到折算出来的那一组，都不认得的不报。None = 不报
        self.picture_mode = picture_mode
        self.frame_us = frame_us
        # HELLO 里说不说「能收声音」（现在的固件说；False = 像加声音之前的固件，发送端这时发 AUDIO 算违规）
        self.audio = audio
        self.keep_audio = keep_audio            # 收到的声音留在 Result.audio 里（自测用；长时间跑别开，一秒 192 KB）
        # HELLO 里说不说「能走 YUV422 帧缓冲」（现在的固件说；False = 像加它之前的固件，发送端这时在 AUTH 里说用它算违规）
        self.yuv = yuv
        self.inject_at = inject_at              # inject_input：第几帧之后发
        self.inject_msgs = inject_msgs          # inject_input：发哪些报文，[(类型, arg, 载荷)]；None = default_input() 那一组
        self.quiet = quiet
        self.stall_s = stall_s      # 报文发到一半停多久算对端失联（真机是 5 秒；自测用短的）
        self.first_frame_s = first_frame_s      # 连上之后多久之内要发完第一帧（真机是 5 秒；自测用短的）
        self.bye_after = bye_after              # 第几帧之后发 BYE 并断开（0 = 不）
        self.drop_after = drop_after            # 第几帧之后直接断开（0 = 不）
        self.late_ack = late_ack                # (第几帧, 秒)：这一帧的 ACK 晚这么多秒才到；None = 不
        # 知道这条连接结束的那一刻调：我们发 BYE 之前；别的结束方式，一进收尾。可能调不止一次。
        # 应答自动发现的一方用它回到「等连接」：这之后到的询问都算数（PROTOCOL.md「自动发现」）。
        # 和固件差在什么时候应答：这里是马上（收尾、等对方关连接期间也应答），固件是留着、关掉这条连接之后才应答。
        # 发送端收到 BYE 马上关连接时看不出差别；它迟迟不关时，这里 300 毫秒之内就有应答，真机没有
        self.over = over or (lambda: None)
        # 收尾多花这么多秒才关连接（真机收尾要停解码、把屏幕还给界面，不是一瞬间）。
        # 自测用它测「收尾期间到的询问不丢」：收尾要是比询问到得还快，就测不出 over 调得早不早
        self.tail_s = tail_s


class Result:
    """一次连接的结果。"""

    def __init__(self):
        self.frames = 0
        self.strips = 0
        self.bytes = 0
        self.violation = None       # 违反协议（含认证没过）的原因；None = 没有
        self.ended_by = None        # 'bye' / 'disconnect' / 'violation' / 'dev_bye' / 'dev_drop' / 'box_timeout'
        self.peer_name = None       # 握手走完了的话：发送端报的名字
        self.paired = False         # 这条连接上配对过
        self.asked = 0              # 这条连接上问了几次密码
        self.repair = False         # 问密码时这台电脑是配对过的（真机的密码框标题会写「重新配对」）
        self.drain_end = None       # ended_by 是 'dev_bye' 时：发完 BYE 之后的收尾是怎么结束的，见 drain_peer
        self.fb = Image.new('RGB', (wp.FB_W, wp.FB_H))     # 屏幕上的画面（走 YUV422 的连接上是展开回全范围之后的）
        self.yuv = False            # 这条连接走 YUV422 帧缓冲（发送端在 AUTH 里说的；握手走完才有）
        self.leds = []              # 收到的 KBD_LEDS
        self.audio_msgs = 0         # 收到的 AUDIO 报文数
        self.audio_bytes = 0        # 它们的载荷合计
        self.audio_gap = 0.0        # 相邻两个 AUDIO 到达的间隔，最长的一次（秒）
        self.audio = bytearray()    # Options.keep_audio 时：收到的声音原样接在一起
        # 发送端的流量控制，从接收这一头量的（见文件开头）
        self.ahead = 0              # ACK 发出去之前、发送端后面的数据已经到了的帧数
        self.max_unacked = 0        # 收到一帧的 PRESENT 时，连它在内还没发出 ACK 的最多几帧（平时是 1；ACK 被压住时往上涨）
        self.held_arrivals = []     # ACK 被压住期间收到的各帧：PRESENT 收完时离压住那一刻多少秒


def check_strip(arg, flags, payload, opts, frame_rows, frame_bytes, yuv=False):
    """检查一个 STRIP 报文，合格就返回解码好的图（720×h）。不合格抛 ProtoError。
    yuv = 这条连接走 YUV422 帧缓冲：条带只能是 4:2:2；返回的图是把有限范围展开之后的（真机上是 DSI 桥展开的）。"""
    if flags != 0:
        raise wp.ProtoError(f'STRIP 的 flags 是 {flags}，保留字段应填 0')
    if arg % wp.STRIP_ALIGN:
        raise wp.ProtoError(f'条带起始行 {arg} 不是 {wp.STRIP_ALIGN} 的倍数')
    if not payload:
        raise wp.ProtoError(f'条带（起始行 {arg}）长度是 0')
    if frame_bytes + len(payload) > opts.max_frame:
        raise wp.ProtoError(f'这一帧已有 {frame_bytes + len(payload)} 字节，超过 HELLO 里的单帧上限 {opts.max_frame}')
    try:
        wp.jpeg_size(payload)       # Tab5 交给硬件解码器之前的那道检查，比下面的 PIL 严
    except wp.ProtoError as e:
        raise wp.ProtoError(f'条带（起始行 {arg}）{e}')
    try:
        im = Image.open(io.BytesIO(payload))
    except Exception as e:
        raise wp.ProtoError(f'条带（起始行 {arg}）不是能解的图片：{e}')
    if im.format != 'JPEG':
        raise wp.ProtoError(f'条带（起始行 {arg}）是 {im.format}，不是 JPEG')
    if im.info.get('progressive') or im.info.get('progression'):
        raise wp.ProtoError(f'条带（起始行 {arg}）是渐进式 JPEG，硬件解码器只认基线')
    w, h = im.size
    if w != wp.FB_W:
        raise wp.ProtoError(f'条带（起始行 {arg}）宽 {w}，必须是 {wp.FB_W}')
    if h % wp.STRIP_ALIGN:
        raise wp.ProtoError(f'条带（起始行 {arg}）高 {h}，不是 {wp.STRIP_ALIGN} 的倍数')
    if arg + h > wp.FB_H:
        raise wp.ProtoError(f'条带（起始行 {arg}，高 {h}）超出帧缓冲的 {wp.FB_H} 行')
    layers = getattr(im, 'layer', None) or []
    if len(layers) != 3:
        raise wp.ProtoError(f'条带（起始行 {arg}）有 {len(layers)} 个颜色分量，必须是 YCbCr 三分量')
    y_samp = (layers[0][1], layers[0][2])
    if y_samp not in ALLOWED_SAMPLING or any((l[1], l[2]) != (1, 1) for l in layers[1:]):
        raise wp.ProtoError(f'条带（起始行 {arg}）的色度采样不是 4:2:0 / 4:2:2 / 4:4:4（各分量采样因子 '
                            f'{[(l[1], l[2]) for l in layers]}）')
    if yuv and y_samp != (2, 1):
        raise wp.ProtoError(f'条带（起始行 {arg}）的色度采样不是 4:2:2（握手里说了走 YUV422 的连接只能发 4:2:2）')
    for r0, r1 in frame_rows:
        if arg < r1 and r0 < arg + h:
            raise wp.ProtoError(f'条带 [{arg}, {arg + h}) 和同一帧里的 [{r0}, {r1}) 重叠')
    try:
        im.load()
    except Exception as e:
        raise wp.ProtoError(f'条带（起始行 {arg}）解码失败：{e}')
    return wp.from_limited(im) if yuv else im


def covered_rows(frame_rows):
    return sum(r1 - r0 for r0, r1 in frame_rows)


def default_input():
    """一组输入报文：单点按下、移动、抬起；按下 A 键、还按着、松开；鼠标左键拖一下。"""
    return [
        (wp.T_TOUCH, 1, wp.TOUCH_PT.pack(0, 640, 360)),
        (wp.T_TOUCH, 2, wp.TOUCH_PT.pack(0, 650, 360) + wp.TOUCH_PT.pack(1, 100, 700)),
        (wp.T_TOUCH, 0, b''),
        (wp.T_KEY, 0, bytes([0x02, 0, 0x04, 0, 0, 0, 0, 0])),    # 左 Shift + A
        (wp.T_HELD, 0, b''),                                     # 还按着（真机是按下 200 毫秒之后才发，这里只测发送端认不认得）
        (wp.T_KEY, 0, bytes(8)),
        (wp.T_MOUSE, 0, wp.MOUSE.pack(5, -3, 0, 0x01)),
    ]


class Outbox:
    """握手之后往发送端发东西都走这里（给它的是 Link.pack 打好的字节，打包的先后就是发出的先后）。
    hold(秒) 之后这段时间里要发的先攒着，到点一起发出去：模拟「Tab5 发的一个报文在空中丢了，
    过了 TCP 的重传超时才补到」——TCP 按序交付，丢的那个补到之前，后面发的也都到不了发送端的程序手里。"""

    def __init__(self, conn):
        self.conn = conn
        self.lock = threading.Lock()
        self.held = None        # 不是 None = 正攒着
        self.timer = None       # 最近一次 hold 的定时器
        self.acks_held = 0      # 攒着的东西里有几个 ACK
        self.held_since = None  # 从什么时刻（perf_counter）开始攒的

    def send(self, data, ack=False):
        with self.lock:
            if self.held is not None:
                self.held.append(data)
                self.acks_held += ack
            else:
                self.conn.sendall(data)

    def holding(self):
        """（攒着几个 ACK, 从什么时刻开始攒的）；没在攒是 (0, None)。"""
        with self.lock:
            return (self.acks_held, self.held_since) if self.held is not None else (0, None)

    def hold(self, seconds):
        with self.lock:
            if self.held is None:
                self.held = []
                self.acks_held = 0
                self.held_since = time.perf_counter()
                self.timer = threading.Timer(seconds, self._release)
                self.timer.daemon = True
                self.timer.start()

    def drain(self):
        """等攒着的东西到点发出去才返回（没在攒就立刻返回）。真机关连接时 lwIP 会先把发送缓冲里的东西发完再发 FIN，
        所以「发完 BYE 就关连接」之前要调它：不然攒着的 ACK 和 BYE 跟着连接一起没了，发送端看到的是断线而不是 BYE。"""
        timer = self.timer
        if timer is not None:
            timer.join()

    def _release(self):
        with self.lock:
            data, self.held = b''.join(self.held), None
            try:
                self.conn.sendall(data)
            except OSError:         # 这期间连接已经结束了
                pass


def drain_peer(conn, idle=2.0, most=3.0):
    """发了 BYE 之后的收尾，和固件一样（wdisp.cpp 的 drain_peer）：不马上关，把发送端还在路上的数据读掉，等它关连接再关
    （不先关发送方向，原因见固件那边的注释）。总共最多等 most 秒；这期间 idle 秒没有新数据、它也没关，就不等了
    （idle 要比 TCP 的重传超时长：BYE 在空中丢了的话要等重传才到发送端）。
    发完 BYE 立刻关的话系统回的是 RST，见 PROTOCOL.md「BYE 之后怎么关连接」。
    返回怎么结束的：'closed' = 发送端关了连接；'idle' = idle 秒没有新数据、它也没关；'limit' = 到了 most 秒它还没关；'reset' = 连接断了。"""
    t0 = time.monotonic()
    try:
        while True:
            left = most - (time.monotonic() - t0)
            if left <= 0:
                return 'limit'
            conn.settimeout(min(idle, left))
            try:
                if not conn.recv(65536):
                    return 'closed'
            except socket.timeout:
                if left >= idle:        # 等满了 idle 秒；否则是总时限先到，回到循环开头
                    return 'idle'
    except OSError:
        return 'reset'


DISCOVERY_PER_S = 10    # 和真机一样：一秒最多应答这么多个询问


def answer_discovery(bind, port, my_id, waiting):
    """应答自动发现的询问。和真机一样，只在等连接的时候（waiting 置位）应答，一秒最多 DISCOVERY_PER_S 个。"""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind((bind, port))
    second, sent = 0, 0
    while True:
        try:
            data, addr = s.recvfrom(256)
            reply = wp.discovery_reply(data, my_id, port)
            if not reply or not waiting.is_set():
                continue
            now = int(time.monotonic())
            if now != second:
                second, sent = now, 0
            if sent < DISCOVERY_PER_S:
                sent += 1
                s.sendto(reply, addr)
        except OSError:
            # 一个包出错不能让应答从此停掉。Windows 上给一个已经关掉的询问方回了应答之后，下一次 recvfrom 会报「连接被重置」
            time.sleep(0.01)


def serve_conn(conn, opts, log=print):
    """服务一条连接直到它结束，返回 Result。"""
    res = Result()
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    out = Outbox(conn)
    frame_rows, frame_bytes, decode_s, t_first = [], 0, 0.0, None
    passwords = iter(opts.passwords)

    def ask_password(pc_name, wrong, known):
        res.asked += 1
        res.repair = res.repair or known
        password = next(passwords, None)
        if password is None:
            opts.over()         # 当作点了取消：握手那边接着就发 BYE
        if not opts.quiet:
            log(f'  配对：{pc_name} 要{"重新" if known else ""}配对' + ('（上一次输的密码不对）' if wrong else '')
                + ('，输入密码' if password is not None else '，没有密码可输（--password），当作点了取消'))
        return password

    try:
        try:
            link, res.peer_name, res.paired = wp.tab5_handshake(conn, opts.store, opts.name, opts.max_strip, opts.max_frame,
                                                                 ask_password, step_s=opts.handshake_s,
                                                                 password_delay=opts.password_delay, password_box_s=opts.password_box_s,
                                                                 caps=(wp.CAP_DEV_AUDIO if opts.audio else 0)
                                                                 | (wp.CAP_DEV_YUV422 if opts.yuv else 0))
        except wp.DeviceBye:        # 「人」在密码框上点了取消：BYE 已经发了，照常收尾（在最后的 finally 里）
            res.ended_by = 'dev_bye'
            return res
        except wp.BoxTimeout:       # 密码框开到时间了「人」还没输完：关掉密码框、断开，不发 BYE
            res.ended_by = 'box_timeout'
            return res
        res.yuv = bool(link.pc_caps & wp.CAP_YUV422)
        if res.yuv and not opts.yuv:
            raise wp.ProtoError('HELLO 里没说能走 YUV422 帧缓冲（caps 的 bit1），发送端却在 AUTH 里说用它（caps 的 bit3）')
        if not opts.quiet:
            log('  画面走 YUV422（4:2:2、有限范围）' if res.yuv else '  画面走 RGB565（发送端没说用 YUV422）')
        if opts.input_mode is not None:
            if link.pc_caps & wp.CAP_INPUT_MODE:
                conn.sendall(link.pack(wp.T_INPUT_MODE, opts.input_mode))
            elif not opts.quiet:
                log('  发送端没说认得 INPUT_MODE（AUTH 的能力位），触摸方式不报')
        if opts.tpad_gain is not None:
            if link.pc_caps & wp.CAP_TPAD_GAIN:
                conn.sendall(link.pack(wp.T_TPAD_GAIN, opts.tpad_gain))
            elif not opts.quiet:
                log('  发送端没说认得 TPAD_GAIN（AUTH 的能力位），触摸板加速不报')
        if opts.picture is not None:
            if link.pc_caps & wp.CAP_PICTURE:
                conn.sendall(link.pack(wp.T_PICTURE, opts.picture[0] | opts.picture[1] << 8))
            elif not opts.quiet:
                log('  发送端没说认得 PICTURE（AUTH 的能力位），画质不报')
        if opts.picture_mode is not None:
            if link.pc_caps & wp.CAP_PICTURE_MODE:
                conn.sendall(link.pack(wp.T_PICTURE_MODE, opts.picture_mode, opts.frame_us.to_bytes(4, 'little')))
            elif link.pc_caps & wp.CAP_PICTURE:
                if 0 <= opts.picture_mode < len(wp.PIC_OLD_KB):
                    conn.sendall(link.pack(wp.T_PICTURE, wp.PIC_OLD_QUALITY | wp.PIC_OLD_KB[opts.picture_mode] << 8))
                    if not opts.quiet:
                        log('  发送端不认得 PICTURE_MODE，画质的档位折成 PICTURE 报')
                elif not opts.quiet:
                    log(f'  发送端不认得 PICTURE_MODE，只认得 PICTURE；第 {opts.picture_mode} 档不在折算表里（没有这一档），画质不报')
            elif not opts.quiet:
                log('  发送端没说认得 PICTURE_MODE / PICTURE（AUTH 的能力位），画质不报')
        first_deadline = time.monotonic() + opts.first_frame_s
        t_audio = None              # 上一个 AUDIO 到达的时刻
        while True:
            # 和真机一样：看了报文头就定载荷上限（超了不去读它），报文发到一半停 5 秒就断开，
            # 第一帧上屏之前还有一个总的期限
            mtype, flags, arg, payload = link.recv(
                lambda t: {wp.T_STRIP: opts.max_strip, wp.T_PRESENT: wp.PRESENT.size, wp.T_AUDIO: wp.AUDIO_MAX}.get(t, 0),
                stall=opts.stall_s, deadline=None if res.frames else first_deadline)
            if flags != 0:
                raise wp.ProtoError(f'报文 0x{mtype:02x} 的 flags 是 {flags}，保留字段应填 0')
            if mtype == wp.T_STRIP:
                if t_first is None:
                    t_first = time.perf_counter()
                t0 = time.perf_counter()
                im = check_strip(arg, flags, payload, opts, frame_rows, frame_bytes, res.yuv)
                res.fb.paste(im, (0, arg))
                decode_s += time.perf_counter() - t0
                frame_rows.append((arg, arg + im.size[1]))
                frame_bytes += len(payload)
            elif mtype == wp.T_PRESENT:
                t_present = time.perf_counter()
                if flags != 0 or arg != 0:
                    raise wp.ProtoError(f'PRESENT 的 flags / arg 是 {flags} / {arg}，都应填 0')
                if len(payload) != wp.PRESENT.size:
                    raise wp.ProtoError(f'PRESENT 载荷 {len(payload)} 字节，应为 {wp.PRESENT.size}')
                if not frame_rows:
                    raise wp.ProtoError('PRESENT 之前这一帧一个条带都没有（画面没变就不要发）')
                if res.frames == 0 and covered_rows(frame_rows) != wp.FB_H:
                    raise wp.ProtoError(f'第一帧必须覆盖整屏 {wp.FB_H} 行，只覆盖了 {covered_rows(frame_rows)} 行')
                seq, t_ms = wp.PRESENT.unpack(payload)
                held, since = out.holding()
                res.max_unacked = max(res.max_unacked, held + 1)
                if held:
                    res.held_arrivals.append(t_present - since)
                if opts.vsync_hz > 0:           # 模拟等到下一个帧边界才换上屏幕
                    period = 1.0 / opts.vsync_hz
                    time.sleep(period - (time.perf_counter() % period))
                now = time.perf_counter()
                if opts.late_ack and res.frames + 1 == opts.late_ack[0]:
                    out.hold(opts.late_ack[1])
                if select.select([conn], [], [], 0)[0]:     # 这个 ACK 还没发，发送端后面的数据已经到了
                    res.ahead += 1
                out.send(link.pack(wp.T_ACK, 0, wp.ACK.pack(seq, t_ms, int((t_present - t_first) * 1e6),
                                                            int(decode_s * 1e6), int((now - t_present) * 1e6))), ack=True)
                res.frames += 1
                res.strips += len(frame_rows)
                res.bytes += frame_bytes
                if opts.save_dir and (res.frames == 1 or res.frames % opts.save_every == 0):
                    wp.fb_to_view(res.fb).save(os.path.join(opts.save_dir, f'frame_{res.frames:05d}.png'))
                if opts.inject_input and res.frames == opts.inject_at:
                    out.send(b''.join(link.pack(*m) for m in (opts.inject_msgs if opts.inject_msgs is not None else default_input())))
                frame_rows, frame_bytes, decode_s, t_first = [], 0, 0.0, None
                if opts.bye_after and res.frames >= opts.bye_after:
                    opts.over()
                    out.send(link.pack(wp.T_DEV_BYE))
                    out.drain()
                    res.ended_by = 'dev_bye'        # 收尾在最后的 finally 里
                    break
                if opts.drop_after and res.frames >= opts.drop_after:
                    res.ended_by = 'dev_drop'
                    break
            elif mtype == wp.T_AUDIO:
                # 声音：不算一帧、不回 ACK，第一帧之前也可以来（第一帧的期限不因为它往后挪）
                if not opts.audio:
                    raise wp.ProtoError('HELLO 里没说能收声音（caps 的 bit0），发送端却发来 AUDIO')
                if arg != 0:
                    raise wp.ProtoError(f'AUDIO 的 arg 是 {arg}，应填 0')
                if not payload or len(payload) % wp.AUDIO_FRAME:
                    raise wp.ProtoError(f'AUDIO 载荷 {len(payload)} 字节，应是 {wp.AUDIO_FRAME} 的倍数、1~{wp.AUDIO_MAX}')
                now = time.perf_counter()
                if t_audio is not None:
                    res.audio_gap = max(res.audio_gap, now - t_audio)
                t_audio = now
                res.audio_msgs += 1
                res.audio_bytes += len(payload)
                if opts.keep_audio:
                    res.audio += payload
            elif mtype == wp.T_KBD_LEDS:
                if arg & ~0x07:
                    raise wp.ProtoError(f'KBD_LEDS 的 arg 是 0x{arg:x}，只有低 3 位有定义')
                res.leds.append(arg)
                if not opts.quiet:
                    log(f'  KBD_LEDS：Num={arg & 1} Caps={arg >> 1 & 1} Scroll={arg >> 2 & 1}')
            elif mtype == wp.T_BYE:
                if arg != 0:
                    raise wp.ProtoError(f'BYE 的 arg 是 {arg}，应填 0')
                res.ended_by = 'bye'
                break
            else:
                raise wp.ProtoError(f'不认识的报文类型 0x{mtype:02x}（arg={arg}，载荷 {len(payload)} 字节）')
    except wp.ProtoError as e:
        res.violation = str(e)
        res.ended_by = 'violation'
    except (ConnectionError, socket.timeout, OSError) as e:
        res.ended_by = 'disconnect'
        if frame_rows and not opts.quiet:
            log(f'  连接断开时有一帧没发完（{len(frame_rows)} 个条带没等到 PRESENT）：{e}')
    finally:
        # 收尾的第一件事就是回到「等连接」，在存图这些之前：发送端发完 BYE 等不到我们关连接也会自己关、马上再问（自动连接），
        # 看到我们关了连接也是马上再问，那个询问不能没人应答。我们发 BYE 结束的，发之前已经调过了（发送端收到 BYE 就再问，不等我们关）
        opts.over()
        if opts.tail_s:
            time.sleep(opts.tail_s)
        if opts.save_dir and res.frames:
            wp.fb_to_view(res.fb).save(os.path.join(opts.save_dir, 'frame_last.png'))
        if res.ended_by == 'dev_bye':
            res.drain_end = drain_peer(conn)
        conn.close()
    return res


def main():
    p = argparse.ArgumentParser(description='模拟的 Tab5 无线显示接收端', formatter_class=argparse.RawDescriptionHelpFormatter,
                                epilog=__doc__)
    p.add_argument('--bind', default='0.0.0.0')
    p.add_argument('--port', type=int, default=wp.PORT)
    p.add_argument('--max-strip', type=int, default=256 * 1024, help='HELLO 里报的单条带上限（字节）')
    p.add_argument('--max-frame', type=int, default=1024 * 1024, help='HELLO 里报的单帧上限（字节）')
    p.add_argument('--vsync-hz', type=float, default=66.0, help='模拟的面板刷新率，0 = 不等帧边界')
    p.add_argument('--save-dir', help='把收到的画面（转回横屏）存成 PNG 的目录')
    p.add_argument('--save-every', type=int, default=50, help='每多少帧存一张（第一帧和最后一帧总会存）')
    p.add_argument('--inject-input', action='store_true', help='第 3 帧之后发一组触摸 / 键盘 / 鼠标报文')
    p.add_argument('--inject-at', type=int, default=3, metavar='N', help='配 --inject-input：改成第 N 帧之后发')
    p.add_argument('--input-mode', type=int, metavar='N', help='握手走完之后报触摸方式：0 触摸、1 触摸板（别的数 = 故意不守协议）；'
                                                               '不给 = 不报（老固件）。发送端没说认得这个报文时不发')
    p.add_argument('--tpad-gain', type=int, metavar='N', help='握手走完之后报触摸板加速：最多放大到 N × 0.1 倍，10~50（范围外的数 = 故意不守协议）；'
                                                              '不给 = 不报（老固件）。发送端没说认得这个报文时不发')
    def picture(v):
        q, kb = (int(x) for x in v.split(','))
        if not (0 <= q <= 255 and 0 <= kb <= 255):
            raise argparse.ArgumentTypeError('质量和预算都要在 0~255 之间（各占报文里的 8 位）')
        return q, kb
    p.add_argument('--picture', metavar='质量,预算KB', type=picture,
                   help='握手走完之后报画质：画面不动时的 JPEG 质量 1~100（范围外的数 = 故意不守协议）和画面在动时每帧的预算（KB，0~255，0 = 不降），'
                        '例 85,60；不给 = 不报（老固件）。发送端没说认得这个报文时不发')
    p.add_argument('--picture-mode', type=int, metavar='N',
                   help='握手走完之后报画质的档位（现在的固件）：0 最佳画质、1 高画质、2 均衡、3 最佳速度（别的数 = 故意不守协议）；不给 = 不报。'
                        '发送端认得 PICTURE_MODE 就发它（带 --frame-us），只认得 PICTURE 的发折算出来的 PICTURE，都不认得的不发')
    p.add_argument('--frame-us', type=int, default=15270, metavar='微秒', help='PICTURE_MODE 里报的屏幕帧周期（默认 15270；5000~50000 之外 = 故意不守协议）')
    p.add_argument('--no-audio', action='store_true', help='HELLO 里不说能收声音（像加声音之前的固件）；发送端这时发 AUDIO 算违规')
    p.add_argument('--no-yuv', action='store_true', help='HELLO 里不说能走 YUV422 帧缓冲（像加它之前的固件）；发送端这时在 AUTH 里说用它算违规')
    p.add_argument('--bye-after', type=int, default=0, metavar='N', help='每条连接第 N 帧之后发 BYE 并断开（用户在 Tab5 上退出）')
    p.add_argument('--drop-after', type=int, default=0, metavar='N', help='每条连接第 N 帧之后直接断开（Tab5 掉线）')
    p.add_argument('--late-ack', metavar='N:秒', help='每条连接第 N 帧的 ACK 晚这么多秒才到（它在空中丢了、等 TCP 重传）')
    p.add_argument('--once', action='store_true', help='服务完一条连接就退出；违反协议时退出码 1')
    p.add_argument('--password', help='配对时「人在 Tab5 上输的密码」；不给的话发送端要配对时当作点了取消')
    p.add_argument('--password-delay', type=float, default=8.0, metavar='秒',
                   help=f'人输一次密码花多少秒（默认 8：比握手每一步的 {wp.HANDSHAKE_S:g} 秒长，发送端等 PAIR_REPLY 的时限给短了、'
                        f'等不及又发报文，都会露出来；只想快点跑完给 0）。这期间发送端发了东西算违规；'
                        f'到了 {wp.PASSWORD_BOX_S:g} 秒 = 人没输完，关掉密码框、断开')
    p.add_argument('--pair-store', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), '.wdpair_mock.json'),
                   metavar='文件', help='自己的编号和配对过的电脑存在哪（默认本目录的 .wdpair_mock.json）')
    p.add_argument('--forget', action='store_true', help='启动时忘掉所有配对过的电脑')
    p.add_argument('--name', default='Tab5-mock', help='HELLO 里报的名字')
    p.add_argument('--discoverable', action='store_true', help='应答自动发现的询问（UDP，端口同 --port；只在等连接时应答）')
    p.add_argument('--announce-id', metavar='十六进制', type=bytes.fromhex,
                   help='应答自动发现时报这个编号（32 位十六进制），而不是 HELLO 里报的那个——真机两处报的是同一个；'
                        '测发送端核不核对用（守规矩的发送端连上之后看到 HELLO 就断开）')
    a = p.parse_args()
    if a.save_every <= 0:
        sys.exit('--save-every 必须是正整数')
    if a.announce_id is not None and len(a.announce_id) != wp.DISC_ID_LEN:
        sys.exit(f'--announce-id 要 {wp.DISC_ID_LEN * 2} 位十六进制')
    if a.password is not None and wp.password_problem(a.password):
        sys.exit('--password：' + wp.password_problem(a.password))
    if a.password_delay < 0:
        sys.exit('--password-delay 不能是负数')
    if a.save_dir:
        os.makedirs(a.save_dir, exist_ok=True)
    late_ack = None
    if a.late_ack:
        try:
            n, _, sec = a.late_ack.partition(':')
            late_ack = (int(n), float(sec))
        except ValueError:
            sys.exit('--late-ack 的写法是 N:秒，比如 50:1.3')
    store = wp.PairStore(a.pair_store, max_peers=wp.MAX_TAB5_PEERS)
    if a.forget:
        store.forget()
    waiting = threading.Event()         # 置位 = 正在等连接（这时才应答自动发现）
    opts = Options(a.max_strip, a.max_frame, a.vsync_hz, a.save_dir, a.save_every, a.inject_input,
                   bye_after=a.bye_after, drop_after=a.drop_after, inject_at=a.inject_at, late_ack=late_ack,
                   store=store, name=a.name, password_delay=a.password_delay, over=waiting.set, input_mode=a.input_mode, tpad_gain=a.tpad_gain, picture=a.picture,
                   audio=not a.no_audio, yuv=not a.no_yuv, picture_mode=a.picture_mode, frame_us=a.frame_us)
    srv = socket.create_server((a.bind, a.port), reuse_port=False)
    print(f'监听 {a.bind}:{a.port}，编号 {store.id.hex()}，配对过 {len(store.peers)} 台电脑')
    if a.discoverable:
        threading.Thread(target=answer_discovery, args=(a.bind, a.port, a.announce_id or store.id, waiting), daemon=True).start()
    while True:
        waiting.set()
        conn, peer = srv.accept()
        waiting.clear()
        print(f'{peer[0]} 连上了')
        t0 = time.perf_counter()
        opts.passwords = [a.password] * 100 if a.password is not None else []      # 问几次答几次
        res = serve_conn(conn, opts)
        dt = time.perf_counter() - t0
        how = {'bye': '发送端 BYE', 'disconnect': '连接断开', 'violation': '违反协议，已断开',
               'dev_bye': '按 --bye-after 发了 BYE' if res.frames else '密码框上点了取消，发了 BYE',
               'dev_drop': '按 --drop-after 断开', 'box_timeout': '密码框开到时间了没输完，已断开'}[res.ended_by]
        if res.drain_end:
            how += '，之后' + {'closed': '发送端关了连接', 'idle': '发送端没有关连接、不等了', 'limit': '到了总时限发送端还没关连接、不等了',
                             'reset': '连接断了'}[res.drain_end]
        who = f'{res.peer_name}，' + ('刚配对，' if res.paired else '') if res.peer_name is not None else ''
        print(f'{peer[0]} 结束（{who}{how}）：{res.frames} 帧 {res.strips} 个条带 {res.bytes / 1024:.0f} KB，{dt:.1f} 秒'
              + (f'，{res.frames / dt:.1f} 帧/秒' if dt > 0 and res.frames else ''))
        if res.frames:
            print(f'  流量控制：{res.ahead} 帧是没等 ACK 就到了的；同时没回 ACK 的最多 {res.max_unacked} 帧'
                  + (f'，压住之后 {" / ".join(f"{t * 1e3:.0f}" for t in res.held_arrivals)} ms 各收到一帧' if res.held_arrivals else ''))
        if res.audio_msgs:
            print(f'  声音：{res.audio_msgs} 个报文，{res.audio_bytes / wp.AUDIO_FRAME / wp.AUDIO_RATE:.2f} 秒，'
                  f'相邻两个到达的间隔最长 {res.audio_gap * 1e3:.0f} ms')
        if res.violation:
            print(f'  ✗ {res.violation}')
        if a.once:
            return 1 if res.violation else 0


if __name__ == '__main__':
    sys.exit(main())
