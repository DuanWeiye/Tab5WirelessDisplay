#!/usr/bin/env python3
"""
Tab5 无线显示的"不守协议的发送端"（协议见 ../PROTOCOL.md）。

逐条故意违反协议，每条用一条新连接，检查接收端的反应：必须在限定时间内主动断开连接
（协议没有错误报文，违规一律断开），而且断开之后还能照常接受下一条连接。
分三组：握手里的违规（该来的报文不来、长度不对、点不在曲线上、公钥全 0、没有配对密钥……；只有「密码框开着的时候又发了一个报文」
那一项会让 Tab5 弹一下密码框，连接一断它就关掉，别的都不会弹）；
握手之后认证码对不上的（改了一个比特、重放、换了先后、明文）；握手之后内容不合规定的（和 v1 一样的那些）。
不只看「断开了没有」：该先回 AUTH_UNKNOWN、AUTH_OK + READY 的几项要先收到它们；该等满 5 秒才断的几项，断得太早也判不符
（那说明是别的原因断的，不是在测的那件事）。
要先配对过：没配对过时加 --password（Tab5 上要有人输同一个密码），配对存在 --pair-store 指的文件里
（默认和 wd_send_test.py 用同一个）。
其中一组是结构不对的 JPEG：表号越界、码字个数超过表的大小、段长度超出数据末尾……
这些瞄的是解码器解析文件头时可能越界的地方，接收端必须在把数据交给解码器之前就拒掉。
这一组每条只发一个条带、时限 3 秒：接收端要是把它当成合格的条带收下了，会接着等第一帧剩下的部分，
3 秒内不会断开，这一项就判不符——所以「符合」说明它是在文件头检查那一关被拒的，没有进解码器。
例外是名称后面带 ※ 的两条（宽 × 高不是 8 的倍数、尺寸太大）：它们的宽本来就不是 720，真机上就算文件头检查放行了，
也会被下一道「条带宽必须是 720」拦下，「符合」只说明连接被断开了，是哪一关拦的要看 Tab5 日志里的断开原因
（文件头检查拦的写「不是能解的 JPEG」）。条带宽固定 720、高不超过 1280 时这两条规矩本来就碰不到——它们管的是相册里
存储卡上的图片——所以按原因核对的是模拟接收端和自测（wd_selftest.py）。
最后发一帧合法的整屏画面，确认前面那些违规没有把接收端弄坏。

用法：
  wd_bad_sender.py 192.168.1.50            # 对着真机（先在 Tab5 上打开「无线显示」页；要已经配对过）
  wd_bad_sender.py 127.0.0.1 --port 17250  # 对着 wd_mock_tab5.py
  wd_bad_sender.py 192.168.1.50 --fuzz 300 # 另加 300 轮随机改文件头的条带（看接收端会不会被弄死）
  wd_bad_sender.py 192.168.1.50 --fuzz-body 100   # 另加 100 轮整屏画面，其中一个条带的压缩数据被随机改坏
                                           #（文件头合格，坏数据会进解码器；对着真机跑，压的是硬件解码出错后的恢复）

退出码：全部符合预期为 0，否则为 1。
"""
import argparse
import hashlib
import io
import os
import socket
import sys
import time

from PIL import Image

import wdcrypto as wc
import wdproto as wp

NAME = 'bad-sender'


def jpeg(w, h, color=(40, 90, 160), subsampling='4:2:2'):
    """一张纯色的基线 JPEG。默认 4:2:2：走 YUV422 帧缓冲的连接只收这一种，走 RGB565 的连接也收，
    所以各项用例里「合法的条带」在两种连接上都合法，被断开只会是因为那一项要测的违规。"""
    buf = io.BytesIO()
    Image.new('RGB', (w, h), color).save(buf, 'JPEG', quality=60, subsampling=subsampling)
    return buf.getvalue()


def jpeg_noise(w, h):
    """一张全是噪点的基线 JPEG（4:2:2）：压不动，720 × 160 有几十 KB。"""
    buf = io.BytesIO()
    Image.frombytes('RGB', (w, h), os.urandom(w * h * 3)).save(buf, 'JPEG', quality=60, subsampling='4:2:2')
    return buf.getvalue()


def split_header(jpg):
    """把一张 JPEG 拆成 [(标记, 段内容)]（SOI 之后、SOS 之前的各段）和 SOS 起的剩余字节。"""
    assert jpg[:2] == b'\xff\xd8'
    segs, pos = [], 2
    while jpg[pos + 1] != 0xDA:
        n = int.from_bytes(jpg[pos + 2:pos + 4], 'big')
        segs.append((jpg[pos + 1], jpg[pos + 4:pos + 2 + n]))
        pos += 2 + n
    return segs, jpg[pos:]


def join_header(segs, tail):
    out = b'\xff\xd8'
    for marker, body in segs:
        out += bytes([0xFF, marker]) + (len(body) + 2).to_bytes(2, 'big') + body
    return out + tail


def edit(jpg, marker, fn):
    """把第一个指定标记的段内容换成 fn(段内容)；fn 返回 (新标记, 新内容) 或新内容。"""
    segs, tail = split_header(jpg)
    for i, (m, body) in enumerate(segs):
        if m == marker:
            r = fn(body)
            segs[i] = r if isinstance(r, tuple) else (m, r)
            break
    else:
        raise ValueError(f'没有 0x{marker:02x} 段')
    return join_header(segs, tail)


def bad_jpegs():
    """结构不对的 JPEG：每一种都瞄着解码器解析文件头时可能越界的地方。接收端必须在解码之前就拒掉。"""
    good = jpeg(wp.FB_W, 160)
    segs, tail = split_header(good)
    dqt = next(body for m, body in segs if m == 0xDB)
    last_dht = max(i for i, (m, _) in enumerate(segs) if m == 0xC4)
    gray = io.BytesIO()
    Image.new('L', (wp.FB_W, 160), 128).save(gray, 'JPEG', quality=60)
    # SOF0 的段内容：精度、高、宽、分量数，然后每个分量 [编号][采样][量化表号]，三个分量的编号在第 6、9、12 字节；
    # tail 从 SOS 的标记起：FF DA、长度、分量数，然后每个分量 [编号][霍夫曼表号]，三个编号在第 5、7、9 字节
    dup_segs = [(m, b[:9] + b[6:7] + b[10:]) if m == 0xC0 else (m, b) for m, b in segs]
    return [
        ('霍夫曼表号 15', edit(good, 0xC4, lambda b: bytes([b[0] | 0x0F]) + b[1:])),
        ('霍夫曼表类别 7', edit(good, 0xC4, lambda b: bytes([0x70 | (b[0] & 0x0F)]) + b[1:])),
        ('霍夫曼表的码字个数 4080（表只有 256 项）', edit(good, 0xC4, lambda b: b[:1] + b'\xff' * 16 + bytes(4080))),
        ('霍夫曼表的码字个数比段里实际有的多', edit(good, 0xC4, lambda b: b[:1] + b'\x10' * 16 + b[17:])),
        ('量化表号 15', edit(good, 0xDB, lambda b: bytes([b[0] | 0x0F]) + b[1:])),
        ('量化表只有半张', edit(good, 0xDB, lambda b: b[:30])),
        ('量化表一共 6 张', join_header(segs[:1] + [(0xDB, dqt[:65])] * 6 + segs[1:], tail)),
        ('同一个量化表号定义两次', join_header(segs[:1] + [(0xDB, dqt[:65])] + segs[1:], tail)),
        ('量化表号不是从 0 起（0 号表改成 2 号）', edit(good, 0xDB, lambda b: bytes([b[0] & 0xF0 | 2]) + b[1:])),
        ('霍夫曼表段是空的', join_header(segs + [(0xC4, b'')], tail)),
        ('少一张霍夫曼表（SOS 里用到了）', join_header(segs[:last_dht] + segs[last_dht + 1:], tail)),
        ('灰度图（只有 1 个分量）', gray.getvalue()),
        ('宽 × 高不是 8 的倍数（721×3）※', jpeg(721, 3)),
        ('尺寸 46864×45824（驱动算输出大小时会溢出）※',
         edit(good, 0xC0, lambda b: b[:1] + (45824).to_bytes(2, 'big') + (46864).to_bytes(2, 'big') + b[5:])),
        ('SOS 的参数不是基线的（Se = 5）', join_header(segs, tail[:12] + b'\x05' + tail[13:])),
        ('APP0 段的长度超出数据末尾', good[:4] + b'\xff\xff' + good[6:]),
        ('段长度是 0', good[:4] + b'\x00\x00' + good[6:]),
        ('数据在 SOF0 标记之后就没了', good[:good.index(b'\xff\xc0') + 2]),
        ('渐进式（SOF2）', edit(good, 0xC0, lambda b: (0xC2, b))),
        ('SOF0 里写着 4 个分量', edit(good, 0xC0, lambda b: b[:5] + b'\x04' + b[6:] + b'\x04\x11\x00')),
        ('SOF0 里写着 200 个分量（段长度不变）', edit(good, 0xC0, lambda b: b[:5] + b'\xc8' + b[6:])),
        ('SOF0 的采样系数是 4×4', edit(good, 0xC0, lambda b: b[:7] + b'\x44' + b[8:])),
        ('SOF0 的量化表号 9', edit(good, 0xC0, lambda b: b[:8] + b'\x09' + b[9:])),
        ('标记前面有填充字节', good[:2] + b'\xff' + good[2:]),
        ('SOS 的分量数和 SOF0 不一致', good[:good.index(b'\xff\xda') + 4] + b'\x01' + good[good.index(b'\xff\xda') + 5:]),
        ('SOS 里分量的编号和 SOF0 不一致（第一个分量写成 9）', join_header(segs, tail[:5] + b'\x09' + tail[6:])),
        ('两个分量用同一个编号（SOF0 和 SOS 里都是）', join_header(dup_segs, tail[:7] + tail[5:6] + tail[8:])),
        ('只有文件头、没有压缩数据', join_header(segs, tail[:2 + int.from_bytes(tail[2:4], 'big')])),
    ]


# 源端口自己选、一路往上不重复，不用系统选的。这个工具几秒钟里对同一个接收端连几十次，接收端每次都是先关的一方，
# 它那头的旧连接要在 TIME_WAIT 里留很久（lwIP 默认 2 分钟）。系统选源端口偶尔会重用几秒前用过的；lwIP 对落在 TIME_WAIT
# 连接上的 SYN 只回一个旧 ACK，connect 于是超时——看上去像「接收端没有回到等待」，其实不是。
# 起点跟着时钟走（每秒往前 8 个，比这个工具用端口的速度快），紧接着再跑一遍也不会和上一遍的重叠；这一段端口在系统自选的范围之外。
SRC_PORT_LO, SRC_PORT_N = 20000, 12000
_src_port = int(time.time()) * 8 % SRC_PORT_N


def connect(host, port):
    """连上接收端，返回套接字（HELLO 还没收）。"""
    global _src_port
    for _ in range(64):
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.settimeout(5)
        src = SRC_PORT_LO + _src_port
        _src_port = (_src_port + 1) % SRC_PORT_N
        try:
            s.bind(('', src))
        except OSError:             # 这个端口被别的程序占着：换下一个
            s.close()
            continue
        try:
            s.connect((host, port))
        except socket.timeout:
            s.close()
            raise OSError(f'5 秒没连上（源端口 {src}，SYN 没有得到应答）') from None
        except OSError:
            s.close()
            raise
        break
    else:
        raise OSError(f'{SRC_PORT_LO}~{SRC_PORT_LO + SRC_PORT_N - 1} 里找不到空闲的源端口')
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    return s


def take_hello(s):
    """收 HELLO，返回 (Hello, 它在线上的原始字节)。"""
    try:
        mtype, flags, ver, payload = wp.recv_msg(s, 4096)
    except socket.timeout:
        raise OSError('连上了，但 5 秒没等到 HELLO') from None
    if mtype != wp.T_HELLO or len(payload) != wp.HELLO.size:
        raise wp.ProtoError(f'第一个报文是 0x{mtype:02x}（载荷 {len(payload)} 字节），不是 HELLO')
    return wp.Hello(ver, payload), wp.HDR.pack(mtype, flags, ver, len(payload)) + payload


def handshake(s, store, password=None, caps=wp.SENDER_CAPS):
    """守规矩地握手，返回 Link。caps 是 AUTH 里声明的能力位（默认和现在的发送端一样：接收端能走 YUV422 就走）。"""
    link, _ = wp.sender_handshake(s, store, NAME, password, caps=caps)
    return link


def auth_msg(store, hello, pub, pc_id=None):
    """一个 AUTH 报文。pc_id 不给就是自己的编号，带的是和这台 Tab5 配对的那把密钥的标识。"""
    return wp.pack(wp.T_AUTH, 0, wp.AUTH.pack(0, pc_id or store.id, wc.key_id(store.get(hello.tab5_id)), pub, wp.pack_name(NAME)))


def half_handshake(s, store):
    """握手做到「收下 AUTH_OK 和 Tab5 的 READY、算出密钥」为止，自己的 READY 先不发。返回 Link（两个方向的密钥都装好了）。"""
    hello, hello_raw = take_hello(s)
    priv, pub = wc.x25519_keypair()
    auth_raw = auth_msg(store, hello, pub)
    s.sendall(auth_raw)
    mtype, flags, arg, payload = wp.recv_msg(s, 64)
    if mtype != wp.T_AUTH_OK or flags or arg or len(payload) != wc.X25519_LEN:
        raise wp.ProtoError(f'AUTH 之后来的是 0x{mtype:02x}（flags={flags} arg={arg}，载荷 {len(payload)} 字节），'
                            f'不是 AUTH_OK（没配对过？先用 --password 配对）')
    try:
        dh = wc.x25519_shared(priv, payload)
    except wc.CryptoError as e:
        raise wp.ProtoError(f'AUTH_OK：{e}') from None
    th = hashlib.sha256(hello_raw + auth_raw + wp.HDR.pack(mtype, flags, arg, len(payload)) + payload).digest()
    k_p2t, k_t2p = wc.session_keys(store.get(hello.tab5_id), dh, th)
    link = wp.Link(s)
    link.rx = wc.Cipher(k_t2p)
    mtype, flags, arg, _ = link.recv(0)         # Tab5 的 READY（认证码对不上会抛 AuthError）
    if mtype != wp.T_READY or flags or arg:
        raise wp.ProtoError(f'AUTH_OK 之后来的是 0x{mtype:02x}（flags={flags} arg={arg}），不是 READY')
    link.tx = wc.Cipher(k_p2t)
    return link


def expect_plain(s, mtype, length, what):
    """接收端这时应该回一个明文的报文（类型、载荷长度都得对）。不是就抛 ProtoError：没走到要测的地方。"""
    s.settimeout(5)
    try:
        got, flags, arg, payload = wp.recv_msg(s, 256)
    except (OSError, wp.ProtoError) as e:
        raise wp.ProtoError(f'该回 {what}，结果{"连接断了" if isinstance(e, ConnectionError) else f"：{e or type(e).__name__}"}') from None
    if got != mtype or flags or arg or len(payload) != length:
        raise wp.ProtoError(f'该回 {what}，来的是 0x{got:02x}（flags={flags} arg={arg}，载荷 {len(payload)} 字节）')


def bye_and_close(link):
    """守规矩地结束：发 BYE，只关发送方向，把对方还在路上的报文读掉，等它关了连接再关（PROTOCOL.md「BYE 之后怎么关连接」）。
    发完立刻关的话，对方还在路上的报文到了一条已经关掉的连接上，这头回的是 RST。"""
    s = link.sock
    try:
        link.send(wp.T_BYE)
        s.shutdown(socket.SHUT_WR)
        s.settimeout(3)
        while s.recv(4096):
            pass
    except OSError:
        pass
    finally:
        s.close()


def closed_within(s, seconds):
    """等接收端断开连接。返回 (断开了没有, 用了多久)。期间收到的报文（比如 ACK、触摸）读掉不管。"""
    t0 = time.monotonic()
    s.settimeout(0.2)
    while time.monotonic() - t0 < seconds:
        try:
            if not s.recv(4096):
                return True, time.monotonic() - t0
        except socket.timeout:
            continue
        except OSError:
            return True, time.monotonic() - t0
    return False, seconds


def full_frame(link, strip_h=160, subsampling='4:2:2'):
    """一帧合法的整屏画面（每个条带一种颜色）+ PRESENT。"""
    out = b''
    for i, row in enumerate(range(0, wp.FB_H, strip_h)):
        out += link.pack(wp.T_STRIP, row, jpeg(wp.FB_W, strip_h, (30 * i, 255 - 30 * i, 120), subsampling))
    return out + link.pack(wp.T_PRESENT, 0, wp.PRESENT.pack(1, 0))


def flip(data, pos):
    """把 data 的第 pos 个字节改掉一个比特（pos 可以是负数，从末尾数）。"""
    out = bytearray(data)
    out[pos] ^= 0x01
    return bytes(out)


class reaching:
    """用例里「走到要测的地方」的那一段（守规矩的握手、等接收端该回的报文）。这一段里连接断了、等过了时限、
    Tab5 上的人取消了，都是没走到（改抛 ProtoError）——不能让它落到「违规之后被断开了」那边去、算成拦下了。"""

    def __enter__(self):
        return self

    def __exit__(self, kind, err, tb):
        if kind is not None and issubclass(kind, (OSError, wp.DeviceBye)):
            raise wp.ProtoError(f'守规矩的那一段就断了：{err or kind.__name__}') from None
        return False


def after(build, yuv=None):
    """一项用例的做法：守规矩地握手，然后发 build(link) 返回的字节。
    yuv：None = 照现在的发送端声明能力位（接收端能走 YUV422 就走）；True = 这一项测的是走 YUV422 的连接上的规矩，
    接收端没说能走的话算没走到；False = 不声明 YUV422（像加它之前的发送端），连接走 RGB565。"""
    def run(s, store):
        with reaching():
            link = handshake(s, store, caps=wp.SENDER_CAPS & ~wp.CAP_YUV422 if yuv is False else wp.SENDER_CAPS)
            if yuv and not link.yuv:
                raise wp.ProtoError('接收端没说能走 YUV422 帧缓冲（HELLO 的能力位），这一项测不到')
        s.sendall(build(link))
        return link
    return run


def before(build, then=None):
    """一项用例的做法：收下 HELLO 就发 build(store, hello) 返回的字节（握手里的违规）。
    then 给了的话：接收端这时应该先回 AUTH_UNKNOWN，收到之后再发 then(store, hello) 返回的字节。"""
    def run(s, store):
        with reaching():
            hello, _ = take_hello(s)
        s.sendall(build(store, hello))
        if then is not None:
            expect_plain(s, wp.T_AUTH_UNKNOWN, 0, 'AUTH_UNKNOWN')
            s.sendall(then(store, hello))
    return run


def late(build):
    """一项用例的做法：握手做到只差自己的 READY（收下了 AUTH_OK，核对过 Tab5 的 READY），然后发 build(link) 返回的字节。"""
    def run(s, store):
        with reaching():
            link = half_handshake(s, store)
        s.sendall(build(link))
    return run


QUICK, SLOW = 3, 9      # 等断开的时限（秒）：一看到就该断的；接收端要等满 5 秒才判的
SLOW_MIN = 4            # SLOW 的那些项，断开不能早于这么多秒：早了说明不是等到时限才断的，是别的原因
BOX_CASE = '握手：密码框开着的时候又发了一个 PAIR'       # 唯一会让接收端弹密码框的一项


# 每一项：(名称, 做法, 等断开的时限秒)。做法是 函数(套接字, 配对存储)，握手完了的返回 Link。
# 时限 QUICK 的是"一看到就该断"的；SLOW 的是接收端要等 5 秒才判的（报文发到一半就不发了、握手的下一步迟迟不来、
# 迟迟不发完第一帧）。时限是负数的反过来：这么多秒之内不应该断开。
def cases():
    good = jpeg(wp.FB_W, 160)
    pub = wc.x25519_keypair()[1]
    off_curve = b'\x04' + bytes(31) + b'\x01' + bytes(31) + b'\x01'       # (1, 1) 不在 P-256 上
    pair = lambda store, p_a: wp.pack(wp.T_PAIR, 0, wp.PAIR.pack(store.id, wp.pack_name(NAME), p_a))
    on_curve = lambda: wc.Spake2('A', 1).pub        # 一个合格的 pA（每次一个新的）
    stranger = os.urandom(wc.ID_LEN)        # 没和接收端配对过的电脑编号
    wrong_kid = lambda st, h: wp.pack(wp.T_AUTH, 0, wp.AUTH.pack(0, st.id, bytes(8), pub, wp.pack_name(NAME)))
    handshake_cases = [
        ('握手：连上之后什么都不发', before(lambda st, h: b''), SLOW),
        ('握手：第一个报文是明文的条带（载荷超过这一步的上限）', before(lambda st, h: wp.pack(wp.T_STRIP, 0, good)), QUICK),
        ('握手：第一个报文是 PAIR_OK（长度合法，但这一步不该来）', before(lambda st, h: wp.pack(wp.T_PAIR_OK, 0, bytes(wc.CONFIRM_LEN))), QUICK),
        ('握手：AUTH 的长度不对', before(lambda st, h: wp.pack(wp.T_AUTH, 0, bytes(wp.AUTH.size - 1))), QUICK),
        ('握手：AUTH 的 arg 不是 0', before(lambda st, h: wp.pack(wp.T_AUTH, 7, auth_msg(st, h, pub)[wp.HDR.size:])), QUICK),
        ('握手：AUTH 的 flags 不是 0', before(lambda st, h: wp.pack(wp.T_AUTH, 0, auth_msg(st, h, pub)[wp.HDR.size:], flags=1)), QUICK),
        ('握手：AUTH 只发一半', before(lambda st, h: auth_msg(st, h, pub)[:50]), SLOW),
        ('握手：AUTH 里的 X25519 公钥全 0', before(lambda st, h: auth_msg(st, h, bytes(32))), QUICK),
        ('握手：AUTH 之后不发 READY（先要收到 AUTH_OK 和 Tab5 的 READY）', late(lambda link: b''), SLOW),
        ('握手：READY 的认证码不对（用的不是会话密钥）',
         late(lambda link: wp.HDR.pack(wp.T_READY, 0, 0, 0) + wc.Cipher(os.urandom(32)).seal(wp.HDR.pack(wp.T_READY, 0, 0, 0), b'')), QUICK),
        ('握手：收到 AUTH_OK 之后还发明文的 READY', late(lambda link: wp.pack(wp.T_READY) + bytes(wc.TAG_LEN)), QUICK),
        ('握手：加密后的第一个报文是条带（载荷超过这一步的上限）', late(lambda link: link.pack(wp.T_STRIP, 0, good)), QUICK),
        ('握手：加密后的第一个报文是 KBD_LEDS（长度合法，但不是 READY）', late(lambda link: link.pack(wp.T_KBD_LEDS, 2)), QUICK),
        ('握手：没配对过的编号发 AUTH，收到 AUTH_UNKNOWN 之后不发 PAIR',
         before(lambda st, h: auth_msg(st, h, pub, stranger), then=lambda st, h: b''), SLOW),
        ('握手：没配对过的编号发 AUTH，收到 AUTH_UNKNOWN 之后再发一个 AUTH',
         before(lambda st, h: auth_msg(st, h, pub, stranger), then=lambda st, h: auth_msg(st, h, pub, stranger)), QUICK),
        ('握手：编号配对过、密钥标识不对，收到 AUTH_UNKNOWN 之后再发 AUTH', before(wrong_kid, then=lambda st, h: auth_msg(st, h, pub)), QUICK),
        ('握手：PAIR 的长度不对', before(lambda st, h: wp.pack(wp.T_PAIR, 0, bytes(wp.PAIR.size + 1))), QUICK),
        ('握手：PAIR 里的点不在曲线上', before(lambda st, h: pair(st, off_curve)), QUICK),
        ('握手：PAIR 里的点不是 04 开头', before(lambda st, h: pair(st, b'\x02' + on_curve()[1:])), QUICK),
        # 密码框弹出来之后电脑只能等 PAIR_REPLY。真机上这一项会让密码框弹一下，连接一断就关掉，不用人输
        (BOX_CASE, before(lambda st, h: pair(st, on_curve()) + pair(st, on_curve())), QUICK),
    ]
    two = lambda link: (link.pack(wp.T_STRIP, 0, good), link.pack(wp.T_STRIP, 160, good))
    # good 只有 2 KB 多。接收端可以按条带的大小走不同的路（真机上大一点的条带交给另一个任务解密、不等结果接着收），
    # 所以几十 KB 的（要分好几次才收完）和几百字节的各来一项。每一项发完都不再发东西：接收端不能等到下一个报文来了才发现
    big, tiny = jpeg_noise(wp.FB_W, 160), jpeg(wp.FB_W, 16)
    crypto_cases = [
        ('认证码改了一个比特', after(lambda link: flip(link.pack(wp.T_STRIP, 0, good), -1)), QUICK),
        ('密文改了一个比特', after(lambda link: flip(link.pack(wp.T_STRIP, 0, good), wp.HDR.size + 100)), QUICK),
        ('几十 KB 的条带，密文中间改了一个比特', after(lambda link: flip(link.pack(wp.T_STRIP, 0, big), wp.HDR.size + len(big) // 2)), QUICK),
        ('几百字节的条带，认证码改了一个比特', after(lambda link: flip(link.pack(wp.T_STRIP, 0, tiny), -1)), QUICK),
        ('报文头改了（起始行 0 改成 16）而认证码没变',
         after(lambda link: wp.HDR.pack(wp.T_STRIP, 0, 16, len(good)) + link.pack(wp.T_STRIP, 0, good)[wp.HDR.size:]), QUICK),
        ('同一个报文发两遍（重放一个内容上合法的 KBD_LEDS）', after(lambda link: link.pack(wp.T_KBD_LEDS, 2) * 2), QUICK),
        ('两个报文换了先后', after(lambda link: b''.join(reversed(two(link)))), QUICK),
        ('握手之后发明文的一帧（没有认证码）',
         after(lambda link: wp.pack(wp.T_STRIP, 0, good) + wp.pack(wp.T_PRESENT, 0, wp.PRESENT.pack(1, 0))), QUICK),
        ('握手之后发明文的 BYE（没有认证码，接收端等不到那 16 字节）', after(lambda link: wp.pack(wp.T_BYE)), SLOW),
    ]
    hdr = lambda link, mtype, flags, arg, length: wp.HDR.pack(mtype, flags, arg, length)     # 只发报文头（头是明文的）
    content_cases = [
        ('条带起始行不是 16 的倍数', after(lambda link: link.pack(wp.T_STRIP, 5, good)), QUICK),
        ('条带长度为 0', after(lambda link: link.pack(wp.T_STRIP, 0)), QUICK),
        ('条带超过单条带上限（只发报文头）', after(lambda link: hdr(link, wp.T_STRIP, 0, 0, 300 * 1024)), QUICK),
        ('条带的 flags 不是 0', after(lambda link: link.pack(wp.T_STRIP, 0, good, flags=1)), QUICK),
        ('KBD_LEDS 的 flags 不是 0', after(lambda link: link.pack(wp.T_KBD_LEDS, 2, flags=1)), QUICK),
        ('条带不是 JPEG', after(lambda link: link.pack(wp.T_STRIP, 0, os.urandom(300))), QUICK),
        ('条带宽度不是 720', after(lambda link: link.pack(wp.T_STRIP, 0, jpeg(704, 160))), QUICK),
        ('条带高度不是 16 的倍数', after(lambda link: link.pack(wp.T_STRIP, 0, jpeg(wp.FB_W, 100))), QUICK),
        ('条带超出帧缓冲底部', after(lambda link: link.pack(wp.T_STRIP, wp.FB_H - 160, jpeg(wp.FB_W, 320))), QUICK),
        ('同一帧里两个条带重叠', after(lambda link: link.pack(wp.T_STRIP, 0, good) + link.pack(wp.T_STRIP, 0, good)), QUICK),
        ('PRESENT 之前没有条带', after(lambda link: link.pack(wp.T_PRESENT, 0, wp.PRESENT.pack(1, 0))), QUICK),
        ('PRESENT 的长度不对', after(lambda link: link.pack(wp.T_STRIP, 0, good) + link.pack(wp.T_PRESENT, 0, b'\0' * 4)), QUICK),
        ('第一帧没有覆盖整屏', after(lambda link: link.pack(wp.T_STRIP, 0, good) + link.pack(wp.T_PRESENT, 0, wp.PRESENT.pack(1, 0))), QUICK),
        ('不认识的报文类型', after(lambda link: link.pack(0x77)), QUICK),
        ('AUDIO 没有载荷', after(lambda link: link.pack(wp.T_AUDIO)), QUICK),
        ('AUDIO 的长度不是 4 的倍数', after(lambda link: link.pack(wp.T_AUDIO, 0, bytes(1922))), QUICK),
        ('AUDIO 超过 20 ms（只发报文头）', after(lambda link: hdr(link, wp.T_AUDIO, 0, 0, wp.AUDIO_MAX + wp.AUDIO_FRAME)), QUICK),
        ('AUDIO 的 arg 不是 0', after(lambda link: link.pack(wp.T_AUDIO, 1, bytes(1920))), QUICK),
        ('AUDIO 的 flags 不是 0', after(lambda link: link.pack(wp.T_AUDIO, 0, bytes(1920), flags=1)), QUICK),
        ('握手之后只发声音、不发画面（第一帧的期限不因为声音往后挪）',
         after(lambda link: b''.join(link.pack(wp.T_AUDIO, 0, bytes(1920)) for _ in range(5))), SLOW),
        ('只该由 Tab5 发的报文（TOUCH）带着载荷发过来', after(lambda link: link.pack(wp.T_TOUCH, 1, wp.TOUCH_PT.pack(0, 10, 10))), QUICK),
        ('握手之后又来一个 READY', after(lambda link: link.pack(wp.T_READY)), QUICK),
        ('报文头只发一半', after(lambda link: hdr(link, wp.T_STRIP, 0, 0, len(good))[:3]), SLOW),
        ('条带载荷只发一部分', after(lambda link: link.pack(wp.T_STRIP, 0, good)[:wp.HDR.size + len(good) // 3]), SLOW),
        ('条带发完了，认证码只发一半', after(lambda link: link.pack(wp.T_STRIP, 0, good)[:-8]), SLOW),
        ('握手之后什么都不发', after(lambda link: b''), SLOW),
        ('第一帧只发一个条带就不发了', after(lambda link: link.pack(wp.T_STRIP, 0, good)), SLOW),
        ('整屏画面之后再违规（屏幕已被接管）', after(lambda link: full_frame(link) + link.pack(0x77)), QUICK),
        ('整屏画面之后报文头只发一半', after(lambda link: full_frame(link) + hdr(link, wp.T_STRIP, 0, 0, len(good))[:3]), SLOW),
        ('整屏画面之后认证码对不上', after(lambda link: full_frame(link) + flip(link.pack(wp.T_STRIP, 0, good), -1)), QUICK),
        ('整屏画面之后一直不发（画面没变，不应断开）', after(full_frame), -8),
        # 画面的色彩（协议同名一节）：说了走 YUV422 的连接只能发 4:2:2；没说的（加它之前的发送端）三种采样照旧都收
        ('走 YUV422 的连接上发 4:2:0 的条带', after(lambda link: link.pack(wp.T_STRIP, 0, jpeg(wp.FB_W, 160, subsampling='4:2:0')), yuv=True), QUICK),
        ('走 YUV422 的连接上发 4:4:4 的条带', after(lambda link: link.pack(wp.T_STRIP, 0, jpeg(wp.FB_W, 160, subsampling='4:4:4')), yuv=True), QUICK),
        ('走 YUV422 的连接上，整屏画面之后发 4:2:0 的条带（屏幕已被接管）',
         after(lambda link: full_frame(link) + link.pack(wp.T_STRIP, 0, jpeg(wp.FB_W, 160, subsampling='4:2:0')), yuv=True), QUICK),
        ('没说走 YUV422 的连接上发 4:2:0 的整屏画面（旧版发送端，不应断开）', after(lambda link: full_frame(link, subsampling='4:2:0'), yuv=False), -8),
        ('没说走 YUV422 的连接上发 4:4:4 的整屏画面（旧版发送端，不应断开）', after(lambda link: full_frame(link, subsampling='4:4:4'), yuv=False), -8),
    ] + [(f'条带的 JPEG 结构不对：{name}', after(lambda link, data=data: link.pack(wp.T_STRIP, 0, data)), QUICK) for name, data in bad_jpegs()]
    return handshake_cases + crypto_cases + content_cases


def fuzz(host, port, rounds, seed, store):
    """随机改一张合法条带的文件头（SOS 之前的部分）再发过去，一次一条连接。
    接收端怎么处理都行（拒掉、解码失败、照常解出来），就是不能死：每一轮都得还能连上，最后还能正常显示。
    两种连接轮着来：走 YUV422 的连接上采样被改掉的条带在查文件头时就拒了，走 RGB565 的连接上它会进解码器。
    返回连不上的次数。"""
    import random
    rng = random.Random(seed)
    good = jpeg(wp.FB_W, 160)
    head = good.index(b'\xff\xda') + 14      # 文件头 + SOS 段
    closed = kept = dead = 0
    for i in range(rounds):
        data = bytearray(good)
        kind = rng.randrange(4)
        if kind == 0:                          # 改 1~4 个字节
            for _ in range(rng.randint(1, 4)):
                data[rng.randrange(2, head)] = rng.randrange(256)
        elif kind == 1:                        # 把某个字节改成 0xFF / 0x00（最容易造出假标记、假长度）
            data[rng.randrange(2, head)] = rng.choice((0xFF, 0x00))
        elif kind == 2:                        # 从文件头中间截断
            data = data[:rng.randrange(2, head)]
        else:                                  # 删掉 / 重复一小段
            a = rng.randrange(2, head - 8)
            b = a + rng.randint(1, 8)
            data = data[:a] + (data[a:b] * 2 if rng.random() < 0.5 else b'') + data[b:]
        try:
            s = connect(host, port)
            link = handshake(s, store, caps=wp.SENDER_CAPS if i % 2 == 0 else wp.SENDER_CAPS & ~wp.CAP_YUV422)
        except (OSError, wp.ProtoError) as e:
            dead += 1
            print(f'[不符] 随机第 {i} 轮（种子 {seed}）：连不上（{e}）')
            time.sleep(3)
            continue
        try:
            link.send(wp.T_STRIP, 0, bytes(data))
            ok, _ = closed_within(s, 1.5)
            if ok:
                closed += 1
            else:
                kept += 1
                bye_and_close(link)
        except OSError:
            closed += 1
        finally:
            s.close()
        time.sleep(0.15)
    print(f'随机改文件头 {rounds} 轮（种子 {seed}）：{closed} 轮被断开，{kept} 轮条带被收下，{dead} 轮连不上')
    return dead


def fuzz_body(host, port, rounds, seed, store):
    """发整屏画面，其中一个条带的压缩数据（SOS 之后的部分）被随机改坏：文件头合格，坏数据会进解码器。
    接收端怎么处理都行（解码失败断开，或者照常解出一幅花的画面、回 ACK），就是不能死、不能卡住：
    每一轮都得还能连上，而且 4 秒之内要么回 ACK 要么断开。返回不符的次数。"""
    import random
    rng = random.Random(seed)
    strip_h = 160
    good = [jpeg(wp.FB_W, strip_h, (30 * i, 255 - 30 * i, 120)) for i in range(wp.FB_H // strip_h)]
    acked = closed = bad = 0
    for i in range(rounds):
        strips = list(good)
        k = rng.randrange(len(strips))
        data = bytearray(strips[k])
        body = data.index(b'\xff\xda') + 14      # 压缩数据从这里开始
        kind = rng.randrange(5)
        if kind == 0:                              # 改 1~8 个字节
            for _ in range(rng.randint(1, 8)):
                data[rng.randrange(body, len(data))] = rng.randrange(256)
        elif kind == 1:                            # 从中间截断（没有 EOI）
            data = data[:rng.randrange(body + 1, len(data))]
        elif kind == 2:                            # 中间插一个标记：EOI / RST0 / SOS / SOI / DQT
            at = rng.randrange(body, len(data))
            data[at:at] = bytes([0xFF, rng.choice((0xD9, 0xD0, 0xDA, 0xD8, 0xDB))])
        elif kind == 3:                            # 后面多出一截随机数据
            data += bytes(rng.randrange(256) for _ in range(rng.randint(1, 2000)))
        else:                                      # 压缩数据整段换成随机字节
            data[body:] = bytes(rng.randrange(256) for _ in range(len(data) - body))
        strips[k] = bytes(data)
        try:
            s = connect(host, port)
            link = handshake(s, store)
        except (OSError, wp.ProtoError) as e:
            bad += 1
            print(f'[不符] 改坏压缩数据第 {i} 轮（种子 {seed}）：连不上（{e}）')
            time.sleep(3)
            continue
        try:
            s.sendall(b''.join(link.pack(wp.T_STRIP, r * strip_h, j) for r, j in enumerate(strips))
                      + link.pack(wp.T_PRESENT, 0, wp.PRESENT.pack(1, 0)))
            s.settimeout(4)
            mtype = None
            while mtype != wp.T_ACK:               # ACK 之前可能夹着触摸报文
                mtype, _, _, _ = link.recv(4096)
            acked += 1
            bye_and_close(link)
        except socket.timeout:
            bad += 1
            print(f'[不符] 改坏压缩数据第 {i} 轮（种子 {seed}，第 {k} 条，改法 {kind}）：4 秒内既没有 ACK 也没有断开')
        except OSError:
            closed += 1
        finally:
            s.close()
        time.sleep(0.15)
    print(f'改坏压缩数据 {rounds} 轮（种子 {seed}）：{acked} 轮回了 ACK，{closed} 轮被断开，{bad} 轮不符')
    return bad


def run_case(host, port, store, name, run, limit):
    """跑一项用例，打印结果，返回符不符合。"""
    try:
        s = connect(host, port)
    except OSError as e:
        print(f'[不符] {name}：连不上（{e}）——上一条之后接收端没有回到等待')
        return False
    link = None
    try:
        link = run(s, store)
        gone, took = closed_within(s, abs(limit))
        if limit < 0 and not gone and link is not None:
            bye_and_close(link)
    except wp.ProtoError as e:      # 握手没走通（没配对过、接收端回的东西不对），不是在测的那件事
        print(f'[不符] {name}：没走到要测的地方（{e}）')
        return False
    except OSError:
        gone, took = True, 0.0      # 还没发完就被断开了，也算断开
    finally:
        s.close()
    if limit == SLOW and gone and took < SLOW_MIN:
        print(f'[不符] {name}：{took:.1f} 秒就被断开了——这一项接收端应该等满 5 秒才断，这么早是别的原因断的')
        return False
    ok = gone == (limit > 0)
    print(f'[{"符合" if ok else "不符"}] {name}：' + (f'{took:.1f} 秒后被断开' if gone else f'{abs(limit)} 秒内没有断开'))
    return ok


def main():
    p = argparse.ArgumentParser(description='Tab5 无线显示：不守协议的发送端')
    p.add_argument('host', help='Tab5 的地址')
    p.add_argument('--port', type=int, default=wp.PORT)
    p.add_argument('--fuzz', type=int, default=0, metavar='N', help='再加 N 轮随机改文件头的条带')
    p.add_argument('--fuzz-body', type=int, default=0, metavar='N', help='再加 N 轮整屏画面，其中一个条带的压缩数据被随机改坏')
    p.add_argument('--seed', type=int, default=1, help='随机那一部分的种子（默认 1）')
    p.add_argument('--only', metavar='字样', help='只跑名字里带这个字样的用例（开头的握手和收尾的合法画面照常）')
    p.add_argument('--password', help='配对密码：没和这台 Tab5 配对过时要给，Tab5 上的人输同一个')
    p.add_argument('--pair-store', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), '.wdpair_sender.json'),
                   metavar='文件', help='自己的编号和配对过的 Tab5 存在哪（默认和 wd_send_test.py 用同一个）')
    args = p.parse_args()
    if args.password is not None and wp.password_problem(args.password):
        sys.exit('--password：' + wp.password_problem(args.password))
    store = wp.PairStore(args.pair_store)

    # 开头先守规矩地连一次：没配对过的在这里配对，后面每一项都靠这份配对握手
    try:
        s = connect(args.host, args.port)
        bye_and_close(handshake(s, store, args.password))
    except wp.DeviceBye:
        print('开头的握手没走通：Tab5 那边结束了连接（配对时在密码框上点了取消？）')
        return 1
    except (OSError, wp.ProtoError) as e:
        print(f'开头的握手没走通：{e}')
        return 1
    time.sleep(0.3)

    bad = 0
    chosen = [c for c in cases() if not args.only or args.only in c[0]]
    if not chosen:
        sys.exit(f'--only：没有名字里带「{args.only}」的用例')
    for name, run, limit in chosen:
        bad += 0 if run_case(args.host, args.port, store, name, run, limit) else 1
        time.sleep(0.3)

    if args.fuzz:
        bad += fuzz(args.host, args.port, args.fuzz, args.seed, store)
    if args.fuzz_body:
        bad += fuzz_body(args.host, args.port, args.fuzz_body, args.seed, store)

    # 收尾：一帧合法的整屏画面，要收到 ACK
    try:
        s = connect(args.host, args.port)
        link = handshake(s, store)
        s.sendall(full_frame(link))
        s.settimeout(3)
        mtype = None
        while mtype != wp.T_ACK:      # ACK 之前可能夹着触摸报文
            mtype, _, _, _ = link.recv(4096)
        bye_and_close(link)
        print('[符合] 违规之后发一帧合法画面：收到 ACK')
    except (OSError, wp.ProtoError) as e:
        print(f'[不符] 违规之后发一帧合法画面：{e}')
        bad += 1

    print(f'共 {len(chosen) + 1} 项，{bad} 项不符')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
