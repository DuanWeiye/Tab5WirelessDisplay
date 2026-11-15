#!/usr/bin/env python3
"""Tab5 无线显示协议（windisplay/PROTOCOL.md，v2）的公共定义：报文头、各载荷的排布、握手（两种角色）、加密收发、
配对的存取、自动发现、画面几何换算。算式在 wdcrypto.py。

发送端（wd_send_test.py）和模拟接收端（wd_mock_tab5.py）共用。协议改了先改 PROTOCOL.md，再改这里。
"""
import hashlib
import json
import os
import select
import socket
import struct
import tempfile
import time

from PIL import Image

import wdcrypto as wc

PORT = 7250
PROTO_VER = 2
FB_W, FB_H = 720, 1280        # 帧缓冲（面板原生竖屏）
VIEW_W, VIEW_H = 1280, 720    # 人看到的横屏画面
STRIP_ALIGN = 16              # 条带的起始行和高都是它的倍数

# 报文类型
T_HELLO, T_AUTH, T_AUTH_OK, T_AUTH_UNKNOWN, T_READY, T_PAIR, T_PAIR_REPLY, T_PAIR_OK = range(0x01, 0x09)     # 握手
T_STRIP, T_PRESENT, T_ACK = 0x10, 0x11, 0x12
T_TOUCH, T_KEY, T_MOUSE, T_HELD, T_INPUT_MODE, T_TPAD_GAIN, T_PICTURE, T_DEV_BYE = 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x2F
T_PICTURE_MODE = 0x27       # Tab5 上选的画质是第几档（arg）+ 屏幕的帧周期（载荷：u32 微秒），协议「画质」
CAP_INPUT_MODE = 1 << 0     # AUTH.caps：这个发送端认得 INPUT_MODE（Tab5 只对说了认得的发送端发它，协议「触摸方式」）
CAP_TPAD_GAIN = 1 << 1      # AUTH.caps：这个发送端认得 TPAD_GAIN（触摸板模式下光标的加速，同上）
CAP_PICTURE = 1 << 2        # AUTH.caps：这个发送端认得 PICTURE（Tab5 上选的画质，协议「画质」）
# AUTH.caps：这条连接的条带都是 4:2:2、画面压在有限范围里，要 Tab5 走 YUV422 帧缓冲（协议「画面的色彩」）。
# 只有 Tab5 在 HELLO 里说了能走（CAP_DEV_YUV422）才可以置：sender_handshake 会按 HELLO 把它去掉
CAP_YUV422 = 1 << 3
CAP_PICTURE_MODE = 1 << 4   # AUTH.caps：这个发送端认得 PICTURE_MODE（画质四档，协议「画质」）。认得它的发送端 Tab5 不再发 PICTURE
SENDER_CAPS = CAP_INPUT_MODE | CAP_TPAD_GAIN | CAP_PICTURE | CAP_YUV422 | CAP_PICTURE_MODE     # 现在的发送端声明的全部能力位
# 画质的四档和 Tab5 给不认得 PICTURE_MODE 的发送端折算的 PICTURE（质量、画面在动时每帧多少 KB，0 = 不降），和固件 settings_store.hpp 里的表一致
PIC_MODE_NAMES = ('最佳画质', '高画质', '均衡', '最佳速度')
PIC_OLD_QUALITY, PIC_OLD_KB = 90, (0, 90, 60, 30)
FRAME_US_MIN, FRAME_US_MAX = 5000, 50000            # PICTURE_MODE 里屏幕的帧周期（微秒）能取的范围
TPAD_GAIN_MIN, TPAD_GAIN_MAX = 10, 50               # TPAD_GAIN 的 arg（0.1 倍）能取的范围
T_KBD_LEDS, T_BYE = 0x30, 0x3F
T_AUDIO = 0x40                      # 电脑的声音（协议「声音」）
CAP_DEV_AUDIO = 1 << 0              # HELLO.caps：这台 Tab5 能收 AUDIO，电脑看到这一位才发
CAP_DEV_YUV422 = 1 << 1             # HELLO.caps：这台 Tab5 能走 YUV422 帧缓冲，电脑看到这一位才可以在 AUTH 里置 CAP_YUV422
DEV_CAPS = CAP_DEV_AUDIO | CAP_DEV_YUV422       # 现在的固件声明的全部能力位
AUDIO_RATE = 48000                  # 声音的格式是定死的：48 kHz、双声道、16 位小端，左右交替
AUDIO_FRAME = 4                     # 一个采样帧的字节数（左、右各 16 位）
AUDIO_MAX = AUDIO_RATE // 50 * AUDIO_FRAME      # 一个 AUDIO 报文最多 20 ms（3840 字节）

HDR = struct.Struct('<BBHI')            # type, flags, arg, len
# fb_w, fb_h, view_w, view_h, max_strip_bytes, max_frame_bytes, caps, Tab5 编号, 名字
HELLO = struct.Struct('<HHHHIII16s32s')
AUTH = struct.Struct('<I16s8s32s32s')           # caps, 电脑编号, 配对密钥的标识, 一次性 X25519 公钥, 名字
PAIR = struct.Struct('<16s32s65s')              # 电脑编号, 名字, pA
PAIR_REPLY = struct.Struct('<65s32s')           # pB, cB
NAME_LEN = 32
PRESENT = struct.Struct('<II')          # seq, t_ms
ACK = struct.Struct('<IIIII')           # seq, t_ms, recv_us, decode_us, present_us
TOUCH_PT = struct.Struct('<BHH')        # id, x, y（横屏画面坐标）
MOUSE = struct.Struct('<hhbB')          # dx, dy, wheel, buttons
KEY_LEN = 8                             # USB HID Boot 键盘报文


class ProtoError(Exception):
    """对端发来的东西不符合协议。"""


class AuthError(ProtoError):
    """认证没过：对端没有配对密钥（或者两边存的不一样）、配对时密码一直不对。"""


class DeviceBye(Exception):
    """Tab5 那边的人结束的（握手期间在密码框上点了取消，或者显示期间退出了）。"""


class BoxTimeout(Exception):
    """模拟接收端：密码框开到时间了人还没输完。按协议关掉密码框、断开（不发 BYE）。"""


def pack(mtype, arg=0, payload=b'', flags=0):
    """打一个明文报文（握手用；握手之后的报文用 Link.pack）。"""
    return HDR.pack(mtype, flags, arg, len(payload)) + payload


def pack_name(name):
    """名字 → 32 字节（UTF-8，太长就在字符的边界上截断，不足补 0）。"""
    raw = name.encode('utf-8')[:NAME_LEN]
    return raw.decode('utf-8', 'ignore').encode('utf-8').ljust(NAME_LEN, b'\0')


def _hidden(c):
    """名字里要换掉的字符（PROTOCOL.md「握手」表下面）：控制字符，和看不见又能搅乱显示的那几段（零宽、换行 / 分段、方向控制）。"""
    n = ord(c)
    return (n < 0x20 or 0x7F <= n <= 0x9F or 0x200B <= n <= 0x200F or 0x2028 <= n <= 0x202E or 0x2060 <= n <= 0x206F
            or n == 0xFEFF or n == 0xFFFD)


def unpack_name(raw):
    """对端报的名字只当显示用的文字：到第一个 0 为止，不是合法 UTF-8 的字节、控制字符、看不见又能搅乱显示的字符换成 ?。"""
    text = raw.split(b'\0', 1)[0].decode('utf-8', 'replace')
    return ''.join('?' if _hidden(c) else c for c in text)


PASSWORD_MAX = 64     # 配对密码最多这么多个字符：Tab5 的输入框只收这么多


def password_problem(password):
    """配对密码不合规定的话返回原因，合规定返回 None。1~64 个字符，只能是 ASCII 的可打印字符（0x20~0x7E）：
    Tab5 上只打得出这些、输入框最多收 64 个，不合规定的密码在 Tab5 上永远输不对。"""
    if not password:
        return '配对密码至少 1 个字符'
    if len(password) > PASSWORD_MAX:
        return '配对密码最多 %d 个字符' % PASSWORD_MAX
    if any(not 0x20 <= ord(c) <= 0x7E for c in password):
        return '配对密码只能用英文字母、数字、空格和键盘上的英文符号（Tab5 上只打得出这些）'
    return None


STALL_S = 5.0         # 接收端的规矩：一个报文发到一半，这么久没有新数据就当对端失联、断开
FIRST_FRAME_S = 5.0   # 接收端的规矩：收到发送端的 READY 之后这么久还没发完第一帧（收到它的 PRESENT）就断开
HANDSHAKE_S = 5.0     # 握手的每一步：对方的下一个报文这么久没到齐就断开（等人输密码的那一步另算）
PAIR_REPLY_S = 130.0  # 发送端发出 PAIR 之后等 PAIR_REPLY 最多这么久（Tab5 的密码框开 2 分钟，这里略长一点）
PASSWORD_BOX_S = 120.0  # 接收端的规矩：密码框从弹出 / 标红起这么久没输完就关掉、断开（不发 BYE）
PAIR_TRIES = 10       # 发送端：密码不对记满这么多次就断开、停止配对
MAX_TAB5_PEERS = 4    # Tab5 最多记这么多台配对过的电脑，满了挤掉最久没连过的
JPEG_MAX_DIM = 16383  # 解码器能处理的最大宽高（按 MCU 取整之后）


def recv_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError('对端断开了连接')
        buf += chunk
    return bytes(buf)


def recv_msg(sock, max_len, stall=None, deadline=None, cipher=None, late=None):
    """收一个完整报文，返回 (type, flags, arg, payload)。
    max_len：载荷上限，一个数，或者 函数(type) -> 上限。超过时不去读载荷，直接报错。
    cipher：这个方向的 wdcrypto.Cipher；给了就按加密的报文收（载荷后面跟 16 字节认证码），认证不过抛 AuthError。
    下面几个是接收端的规矩，接收端用；都不给时按套接字自己的超时收。
    stall：报文的第一个字节到了以后，后面每次等数据最多等这么多秒，等不到抛 ProtoError
           （报文之间可以一直等：画面不变就没有数据）。
    deadline：time.monotonic() 的一个时刻，到这时整个报文还没收完就抛 ProtoError（握手的每一步、第一帧上屏之前用）；
    late：那时报的原因，不给就是「没有发完第一帧」。"""
    timed = stall is not None or deadline is not None
    old = sock.gettimeout()
    started = False         # 这个报文的第一个字节到了没有

    def take(n):
        nonlocal started
        if not timed:
            return recv_exact(sock, n)
        buf = bytearray()
        while len(buf) < n:
            waits = [stall] if started and stall is not None else []
            if deadline is not None:
                waits.append(max(deadline - time.monotonic(), 0.001))
            sock.settimeout(min(waits) if waits else None)
            try:
                chunk = sock.recv(n - len(buf))
            except socket.timeout:
                if deadline is not None and deadline - time.monotonic() < 0.01:
                    raise ProtoError(late or f'READY 之后 {FIRST_FRAME_S:g} 秒内没有发完第一帧') from None
                raise ProtoError(f'报文发到一半，{stall:g} 秒没有新数据') from None
            if not chunk:
                raise ConnectionError('对端断开了连接')
            buf += chunk
            started = True
        return bytes(buf)

    try:
        hdr = take(HDR.size)
        mtype, flags, arg, length = HDR.unpack(hdr)
        limit = max_len(mtype) if callable(max_len) else max_len
        if length > limit:
            raise ProtoError(f'报文 0x{mtype:02x} 的载荷 {length} 字节，超过上限 {limit}')
        if cipher is None:
            return mtype, flags, arg, (take(length) if length else b'')
        try:
            return mtype, flags, arg, cipher.open(hdr, take(length + wc.TAG_LEN))
        except wc.CryptoError:
            raise AuthError(f'报文 0x{mtype:02x}（载荷 {length} 字节）的认证码对不上') from None
    finally:
        if timed:
            sock.settimeout(old)


class Link:
    """一条连接上的收发。握手之前两个方向都是明文；握手里给 tx / rx 装上密钥之后，那个方向的报文逐个加密。"""

    def __init__(self, sock):
        self.sock = sock
        self.tx = self.rx = None        # wdcrypto.Cipher；None = 这个方向还是明文
        self.pc_caps = 0                # Tab5 这一侧：电脑在 AUTH 里填的能力位（握手走完才有）
        self.yuv = False                # 发送端这一侧：这条连接说好了走 YUV422（条带要发 4:2:2、有限范围；握手走完才有）

    def pack(self, mtype, arg=0, payload=b'', flags=0):
        """打一个报文，返回线上的字节。加密时每打一个就占用一个报文计数，所以**必须按打包的先后发出去**。"""
        hdr = HDR.pack(mtype, flags, arg, len(payload))
        return hdr + (payload if self.tx is None else self.tx.seal(hdr, payload))

    def send(self, mtype, arg=0, payload=b''):
        self.sock.sendall(self.pack(mtype, arg, payload))

    def recv(self, max_len, stall=None, deadline=None, late=None):
        return recv_msg(self.sock, max_len, stall, deadline, self.rx, late)


class PairStore:
    """自己的编号，和配对过的对端（编号 → 配对密钥、名字）。path 是 None 时只放在内存里（自测用）。
    文件里是密钥，只给自己读写；它不入库（.gitignore）。
    max_peers：最多记几个对端（Tab5 是 4 个），满了挤掉最久没连过的；None = 不限（电脑这边）。peers 里越靠后的越是最近连过的。
    文件是启动时读一遍、每次改动整个重写的：同一个文件不要让两个工具同时改（后写的会盖掉先写的那一条）。"""

    def __init__(self, path=None, max_peers=None):
        self.path, self.id, self.peers, self.max_peers = path, None, {}, max_peers
        if path and os.path.exists(path):
            with open(path, encoding='utf-8') as f:
                d = json.load(f)
            self.id = bytes.fromhex(d['id'])
            self.peers = {bytes.fromhex(k): (bytes.fromhex(v['key']), v.get('name', '')) for k, v in d['peers'].items()}
        if self.id is None:
            self.id = os.urandom(wc.ID_LEN)
            self._save()

    def get(self, peer_id):
        """这个对端的配对密钥；没配对过返回 None。"""
        ent = self.peers.get(peer_id)
        return ent[0] if ent else None

    def put(self, peer_id, key, name):
        """握手走完时调：记下（或者换掉）这个对端的密钥和名字，并把它排到「最近连过」的位置。"""
        if self.peers.get(peer_id) == (key, name) and next(reversed(self.peers)) == peer_id:
            return
        self.peers.pop(peer_id, None)
        self.peers[peer_id] = (key, name)
        while self.max_peers and len(self.peers) > self.max_peers:
            del self.peers[next(iter(self.peers))]
        self._save()

    def forget(self, peer_id=None):
        """忘掉一个对端（None = 全部）。"""
        if peer_id is None:
            self.peers.clear()
        else:
            self.peers.pop(peer_id, None)
        self._save()

    def _save(self):
        if not self.path:
            return
        d = {'id': self.id.hex(), 'peers': {k.hex(): {'key': v[0].hex(), 'name': v[1]} for k, v in self.peers.items()}}
        # 先写临时文件再改名：写到一半出事不会把原来的弄坏。临时文件每次一个新名字（mkstemp 建出来就是 0600），
        # 名字以原文件名开头，.gitignore 的规则同样盖得住它
        folder, base = os.path.split(os.path.abspath(self.path))
        fd, tmp = tempfile.mkstemp(dir=folder, prefix=base + '.', suffix='.tmp')
        try:
            with open(fd, 'w', encoding='utf-8') as f:
                json.dump(d, f, ensure_ascii=False, indent=1)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp, self.path)
        except BaseException:
            try:
                os.unlink(tmp)
            except OSError:
                pass
            raise


class Hello:
    """Tab5 的 HELLO。"""

    def __init__(self, ver, payload):
        (self.fb_w, self.fb_h, self.view_w, self.view_h, self.max_strip, self.max_frame, self.caps,
         self.tab5_id, name) = HELLO.unpack(payload)
        self.ver, self.name = ver, unpack_name(name)


def _raw(mtype, flags, arg, payload):
    """收到的报文在线上的原始字节（握手里要拿它算摘要）。"""
    return HDR.pack(mtype, flags, arg, len(payload)) + payload


def sender_handshake(sock, store, name, password=None, log=lambda *_: None, step_s=HANDSHAKE_S, tries=PAIR_TRIES,
                     pair_reply_s=PAIR_REPLY_S, expect_id=None, caps=SENDER_CAPS):
    """发送端这一侧的握手（PROTOCOL.md「认证和加密」）：收 HELLO；有配对就认证，没有（或者 Tab5 回了 AUTH_UNKNOWN）
    而且给了 password 就配对再认证；换出会话密钥。返回 (Link, Hello)，之后的收发都走 Link。
    caps：AUTH 里声明的能力位。其中的 CAP_YUV422 只有 Tab5 在 HELLO 里说了能走才真的发出去（协议「画面的色彩」），
    说好了没有看返回的 Link.yuv：是 True 的话之后的条带必须是 4:2:2、画面先过 to_limited()。
    password：电脑上设的配对密码。不给 = 没设，这时需要配对就抛 AuthError。
    tries：密码不对记满几次就断开。工具跑一次就是一条连接，所以在这里按调用计、不存盘；
    真的发送端（托盘程序）要跨连接累计并存盘，记满了停止配对、等人恢复（PROTOCOL.md「什么时候可以配对」）。
    expect_id：按自动发现的应答去连时给应答里的编号。HELLO 里报的不是它就抛 ProtoError，什么都不发（PROTOCOL.md「自动发现」）。
    抛 ProtoError（对端不守协议）、AuthError（认证没过、要配对而不许配、密码不对到了次数）、DeviceBye（Tab5 上的人结束的）。"""
    link = Link(sock)
    old = sock.gettimeout()
    sock.settimeout(step_s)

    def step(what, sizes):
        """收握手里的一个明文报文：类型得是 sizes 里有的（或者明文的 BYE = Tab5 上的人结束了，抛 DeviceBye），
        长度是定长，flags 和 arg 都是 0。返回 (type, 线上的原始字节, 载荷)。"""
        mtype, flags, arg, payload = link.recv(256)
        if mtype == T_DEV_BYE and not (flags or arg or payload):
            raise DeviceBye()
        if sizes.get(mtype) != len(payload) or flags or arg:
            raise ProtoError(f'{what}之后来的是 0x{mtype:02x}（flags={flags} arg={arg}，载荷 {len(payload)} 字节），不是这一步该来的')
        return mtype, _raw(mtype, flags, arg, payload), payload

    try:
        mtype, flags, ver, payload = link.recv(256)
        if mtype != T_HELLO or flags or len(payload) != HELLO.size:
            raise ProtoError(f'连上后第一个报文不是 HELLO（type=0x{mtype:02x}，flags={flags}，载荷 {len(payload)} 字节）')
        hello, hello_raw = Hello(ver, payload), _raw(mtype, flags, ver, payload)
        if ver != PROTO_VER or (hello.fb_w, hello.fb_h, hello.view_w, hello.view_h) != (FB_W, FB_H, VIEW_W, VIEW_H):
            raise ProtoError(f'Tab5 报的协议版本 {ver}、帧缓冲 {hello.fb_w}×{hello.fb_h}、画面 {hello.view_w}×{hello.view_h} '
                             f'和本工具（版本 {PROTO_VER}）不一致')
        if expect_id is not None and hello.tab5_id != expect_id:
            # 连不连它、它配对过没有是按应答里的编号定的；下面查配对用 HELLO 里的。不一样 = 连着的不是当初决定去连的那一台
            raise ProtoError('HELLO 里的 Tab5 编号和它应答自动发现时报的不一样')
        if not hello.caps & CAP_DEV_YUV422:
            caps &= ~CAP_YUV422         # 这台 Tab5 走不了 YUV422（加这个能力之前的固件）：不说，照旧发

        def auth(key):
            """走「每次连接」。认证过了返回 True；Tab5 回 AUTH_UNKNOWN 返回 False。"""
            priv, pub = wc.x25519_keypair()
            auth_raw = pack(T_AUTH, 0, AUTH.pack(caps, store.id, wc.key_id(key), pub, pack_name(name)))
            sock.sendall(auth_raw)
            mtype, ok_raw, payload = step('AUTH ', {T_AUTH_OK: wc.X25519_LEN, T_AUTH_UNKNOWN: 0})
            if mtype == T_AUTH_UNKNOWN:
                return False
            try:
                dh = wc.x25519_shared(priv, payload)
            except wc.CryptoError as e:
                raise ProtoError(f'AUTH_OK：{e}') from None
            th = hashlib.sha256(hello_raw + auth_raw + ok_raw).digest()
            k_p2t, k_t2p = wc.session_keys(key, dh, th)
            link.rx = wc.Cipher(k_t2p)          # AUTH_OK 之后 Tab5 发的都是加密的，第一个必须是 READY
            mtype, flags, arg, _payload = link.recv(0)          # 认证码对不上 = Tab5 那把配对密钥不是这一把，抛 AuthError
            if mtype != T_READY or flags or arg:
                raise ProtoError(f'Tab5 加密后的第一个报文是 0x{mtype:02x}（flags={flags} arg={arg}），不是 READY')
            link.tx = wc.Cipher(k_p2t)
            link.send(T_READY)
            store.put(hello.tab5_id, key, hello.name)       # 握手走完了才存（新配的密钥、改了的名字）
            link.yuv = bool(caps & CAP_YUV422)
            return True

        def pair():
            """走「配对」，返回配出来的密钥（还没存）。"""
            w = wc.w_from_w0(wc.password_to_w0(password))
            for n in range(1, tries + 1):
                spake = wc.Spake2('A', w)       # 每次重新取随机数
                pair_raw = pack(T_PAIR, 0, PAIR.pack(store.id, pack_name(name), spake.pub))
                sock.sendall(pair_raw)
                log('配对：等 Tab5 上输入密码')
                sock.settimeout(pair_reply_s)
                try:
                    _mtype, _raw_reply, payload = step('PAIR ', {T_PAIR_REPLY: PAIR_REPLY.size})
                finally:
                    sock.settimeout(step_s)
                p_b, c_b = PAIR_REPLY.unpack(payload)
                try:
                    ke, c_a, want = spake.finish(p_b, store.id, hello.tab5_id, hashlib.sha256(hello_raw + pair_raw).digest())
                except wc.CryptoError as e:
                    raise ProtoError(f'PAIR_REPLY：{e}') from None
                if wc.same(c_b, want):
                    sock.sendall(pack(T_PAIR_OK, 0, c_a))
                    log(f'配对：密码对上了（{hello.name}）')
                    return wc.pairing_key(ke)
                log(f'配对：Tab5 上输的密码不对（第 {n} 次）')
            raise AuthError(f'配对：密码连着 {tries} 次不对')

        key = store.get(hello.tab5_id)
        if key is None or not auth(key):
            if password is None:
                raise AuthError('没和这台 Tab5 配对过' if key is None else 'Tab5 那边没有这把配对密钥了（AUTH_UNKNOWN）'
                                '，要重新配对：给配对密码')
            if not auth(pair()):
                raise ProtoError('刚配对完，Tab5 却回了 AUTH_UNKNOWN')
        return link, hello
    finally:
        sock.settimeout(old)


def tab5_handshake(sock, store, name, max_strip, max_frame, ask_password, step_s=HANDSHAKE_S, password_delay=0.0,
                   password_box_s=PASSWORD_BOX_S, caps=DEV_CAPS):
    """Tab5 这一侧的握手（模拟接收端用），照着协议逐条检查发送端。返回 (Link, 电脑的名字, 这条连接上是不是刚配对的)。
    ask_password(电脑的名字, 上一次输的不对吗, 这台电脑配对过吗)：返回人输的密码；返回 None = 人点了取消（发 BYE，抛 DeviceBye）。
    password_delay：人输密码花多少秒（密码框开着的时间）。这期间发送端发了东西算它不守协议；它断了连接就关掉密码框（抛 ConnectionError）；
    到了 password_box_s 人还没输完，抛 BoxTimeout（调用方关连接，不发 BYE）。
    caps：HELLO 里报的能力位（默认和现在的固件一样：能收声音、能走 YUV422 帧缓冲）。
    发送端不守协议抛 ProtoError（它没有配对密钥不算：回 AUTH_UNKNOWN，等它配对）。"""
    link = Link(sock)
    hello_raw = pack(T_HELLO, PROTO_VER, HELLO.pack(FB_W, FB_H, VIEW_W, VIEW_H, max_strip, max_frame, caps,
                                                    store.id, pack_name(name)))
    sock.sendall(hello_raw)
    sizes = {T_AUTH: AUTH.size, T_PAIR: PAIR.size, T_PAIR_OK: wc.CONFIRM_LEN}
    names = {T_AUTH: 'AUTH', T_PAIR: 'PAIR', T_PAIR_OK: 'PAIR_OK'}

    def step(*expect):
        """收握手的下一个报文：限时，类型得是这一步该来的，长度得是定长。返回 (type, 线上的原始字节, 载荷)。"""
        want = ' 或 '.join(names[t] for t in expect)
        mtype, flags, arg, payload = recv_msg(sock, lambda t: sizes.get(t, 0), stall=step_s,
                                              deadline=time.monotonic() + step_s, late=f'握手：{step_s:g} 秒内没有等到 {want}')
        if mtype not in expect or flags or arg or len(payload) != sizes[mtype]:
            raise ProtoError(f'握手：这一步该来 {want}，来的是 0x{mtype:02x}（flags={flags} arg={arg}，载荷 {len(payload)} 字节）')
        return mtype, _raw(mtype, flags, arg, payload), payload

    def known(auth_payload):
        """AUTH 里的电脑配对过、而且它那把密钥就是这边存的这一把。"""
        _caps, pc_id, kid, _pub, _name = AUTH.unpack(auth_payload)
        key = store.get(pc_id)
        return key is not None and kid == wc.key_id(key)

    new_key = None          # 这条连接上新配出来的密钥：握手走完才存
    mtype, msg_raw, payload = step(T_AUTH, T_PAIR)
    if mtype == T_AUTH and not known(payload):
        sock.sendall(pack(T_AUTH_UNKNOWN))
        mtype, msg_raw, payload = step(T_PAIR)
    if mtype == T_PAIR:
        wrong = False
        while True:
            pair_id, pc_name, p_a = PAIR.unpack(payload)
            try:
                wc.point_from_bytes(p_a)        # 点不合格的不去问密码
            except wc.CryptoError as e:
                raise ProtoError(f'PAIR：{e}') from None
            password = ask_password(unpack_name(pc_name), wrong, store.get(pair_id) is not None)
            # 密码框开着：这期间电脑什么都不该发；连接断了就关掉密码框；人迟迟不输完，到时间也关掉
            if select.select([sock], [], [], min(password_delay, password_box_s))[0]:
                if sock.recv(1, socket.MSG_PEEK):
                    raise ProtoError('密码框开着的时候电脑发来了东西（这期间它只能等 PAIR_REPLY）')
                raise ConnectionError('密码框开着的时候对端断开了连接')
            if password_delay >= password_box_s:
                raise BoxTimeout()
            if password is None:
                sock.sendall(pack(T_DEV_BYE))       # AUTH_OK 之前：明文
                raise DeviceBye()
            spake = wc.Spake2('B', wc.w_from_w0(wc.password_to_w0(password)))
            try:
                ke, want_ca, c_b = spake.finish(p_a, pair_id, store.id, hashlib.sha256(hello_raw + msg_raw).digest())
            except wc.CryptoError as e:
                raise ProtoError(f'PAIR：{e}') from None
            sock.sendall(pack(T_PAIR_REPLY, 0, PAIR_REPLY.pack(spake.pub, c_b)))
            mtype, msg_raw, payload = step(T_PAIR, T_PAIR_OK)
            if mtype == T_PAIR_OK:
                if not wc.same(payload, want_ca):
                    raise ProtoError('PAIR_OK 里的 cA 不对')
                new_key = wc.pairing_key(ke)
                break
            wrong = True
        mtype, msg_raw, payload = step(T_AUTH)
        _caps, pc_id, kid, _pub, _name = AUTH.unpack(payload)
        if pc_id != pair_id or kid != wc.key_id(new_key):
            raise ProtoError('配对之后的 AUTH 用的不是配对时的电脑编号、或者不是刚配出来的那把密钥')

    link.pc_caps, pc_id, _kid, pc_pub, pc_name = AUTH.unpack(payload)       # 不认识的能力位不管
    key = new_key or store.get(pc_id)
    priv, pub = wc.x25519_keypair()
    ok_raw = pack(T_AUTH_OK, 0, pub)
    try:
        dh = wc.x25519_shared(priv, pc_pub)
    except wc.CryptoError as e:
        raise ProtoError(f'AUTH：{e}') from None
    k_p2t, k_t2p = wc.session_keys(key, dh, hashlib.sha256(hello_raw + msg_raw + ok_raw).digest())
    link.tx = wc.Cipher(k_t2p)              # AUTH_OK 之后发的都加密
    sock.sendall(ok_raw + link.pack(T_READY))       # AUTH_OK 和 READY 一次发出，中间不插别的
    link.rx = wc.Cipher(k_p2t)
    mtype, flags, arg, _payload = link.recv(0, stall=step_s, deadline=time.monotonic() + step_s,
                                            late=f'握手：{step_s:g} 秒内没有等到 READY')
    if mtype != T_READY or flags or arg:
        raise ProtoError(f'发送端加密后的第一个报文是 0x{mtype:02x}（flags={flags} arg={arg}），不是 READY')
    store.put(pc_id, key, unpack_name(pc_name))       # 握手走完了才存（新配的密钥、改了的名字）
    return link, unpack_name(pc_name), new_key is not None


# ── 自动发现（UDP）───────────────────────────────────────────────────────
DISC = struct.Struct('<4sBBH16s8s')     # 标记, 版本, 类型, 端口, 编号, 随机数
DISC_ID_LEN = wc.ID_LEN                 # 应答里的编号就是 HELLO 里的那个 Tab5 编号
DISC_MAGIC, DISC_QUERY, DISC_REPLY = b'T5WD', 1, 2


def discover(wait=1.0, rounds=3, port=PORT, broadcast='255.255.255.255'):
    """广播询问，返回应答了的 Tab5：[(地址, TCP 端口, Tab5 编号)]。一轮等 wait 秒，有应答就返回，最多 rounds 轮。
    测试工具从简：只往一个广播地址发（协议要求真的发送端每块网卡各发一个）。"""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    try:
        for _ in range(rounds):
            nonce = os.urandom(8)
            s.sendto(DISC.pack(DISC_MAGIC, PROTO_VER, DISC_QUERY, 0, bytes(wc.ID_LEN), nonce), (broadcast, port))    # 询问里不带自己的编号
            found, t_end = {}, time.monotonic() + wait
            while (left := t_end - time.monotonic()) > 0:
                s.settimeout(left)
                try:
                    data, addr = s.recvfrom(256)
                except socket.timeout:
                    break
                if len(data) != DISC.size:
                    continue
                magic, ver, kind, tcp_port, peer_id, echo = DISC.unpack(data)
                if (magic, ver, kind, echo) == (DISC_MAGIC, PROTO_VER, DISC_REPLY, nonce) and tcp_port:
                    found[(addr[0], tcp_port)] = peer_id
            if found:
                return [(a, p, i) for (a, p), i in found.items()]
        return []
    finally:
        s.close()


def discovery_reply(data, my_id, tcp_port):
    """收到一个 UDP 包：是合格的询问就返回该回的应答，不是返回 None。"""
    if len(data) != DISC.size:
        return None
    magic, ver, kind, _port, _peer_id, nonce = DISC.unpack(data)
    if (magic, ver, kind) != (DISC_MAGIC, PROTO_VER, DISC_QUERY):
        return None
    return DISC.pack(DISC_MAGIC, PROTO_VER, DISC_REPLY, tcp_port, my_id, nonce)


def jpeg_size(data):
    """按接收端的规矩检查一张 JPEG 的文件头，合格返回 (宽, 高)，不合格抛 ProtoError 说明哪里不对。

    Tab5 把条带交给硬件解码器之前有一道同样的检查（固件 main/utils/hwjpeg.cpp 的 parse_header），过不了的条带
    会被断开。规矩见 PROTOCOL.md「条带的 JPEG 文件头要规整」：SOI 之后是一段一段紧挨着的 APPn / COM / DQT / DHT / SOF0 / DRI，
    最后是 SOS 和压缩数据。两边的规矩要保持一致：改了一边就改另一边。"""
    def bad(why):
        return ProtoError(f'JPEG 文件头：{why}')

    n_all = len(data)
    if n_all < 4 or data[0] != 0xFF or data[1] != 0xD8:
        raise bad('开头不是 SOI（FF D8）')
    pos, size = 2, None
    dqt_ids, dht_ids, comp_tq, comp_id = [], set(), [], []      # dht_ids 里是 (类别, 表号)
    while True:
        if pos + 4 > n_all:
            raise bad('没有 SOS 段就结束了')
        if data[pos] != 0xFF:
            raise bad(f'第 {pos} 字节应该是段的标记（FF xx），实际是 {data[pos]:02X}')
        marker = data[pos + 1]
        seg_len = data[pos + 2] << 8 | data[pos + 3]
        if seg_len < 2 or pos + 2 + seg_len > n_all:
            raise bad(f'FF {marker:02X} 段的长度 {seg_len} 不对（数据一共 {n_all} 字节，这一段从第 {pos} 字节开始）')
        d = data[pos + 4:pos + 2 + seg_len]
        if 0xE0 <= marker <= 0xEF or marker == 0xFE:
            pass
        elif marker == 0xDB:
            i = 0
            while i < len(d):
                prec, tid = d[i] >> 4, d[i] & 0x0F
                need = 1 + (128 if prec else 64)
                if prec > 1 or tid > 3 or i + need > len(d):
                    raise bad(f'量化表不对（精度 {prec}，表号 {tid}，段内还剩 {len(d) - i} 字节）')
                if tid in dqt_ids:
                    raise bad(f'{tid} 号量化表定义了两次')
                dqt_ids.append(tid)
                i += need
        elif marker == 0xC4:
            if not d:
                raise bad('霍夫曼表段是空的')
            i = 0
            while i < len(d):
                if i + 17 > len(d):
                    raise bad('霍夫曼表不完整')
                cls, tid = d[i] >> 4, d[i] & 0x0F
                codes = sum(d[i + 1:i + 17])
                if cls > 1 or tid > 1 or codes > (256 if cls else 16) or i + 17 + codes > len(d):
                    raise bad(f'霍夫曼表不对（类别 {cls}，表号 {tid}，码字 {codes} 个）')
                dht_ids.add((cls, tid))
                i += 17 + codes
        elif marker == 0xC0:
            if size is not None:
                raise bad('有两个 SOF0')
            nf = d[5] if len(d) >= 6 else 0
            if nf != 3:
                raise bad(f'SOF0 里是 {nf} 个颜色分量，必须是 YCbCr 三分量' + ('（灰度图硬件解不成 RGB565）' if nf == 1 else ''))
            if len(d) != 6 + 3 * nf or d[0] != 8:
                raise bad(f'SOF0 不对（精度 {d[0]}，段内容 {len(d)} 字节）')
            size = (d[3] << 8 | d[4], d[1] << 8 | d[2])
            if 0 in size:
                raise bad('宽或高是 0')
            if size[0] * size[1] % 8:
                raise bad(f'宽 × 高（{size[0]}×{size[1]}）不是 8 的倍数，解码器的驱动不收')
            for c in range(nf):
                hv = d[6 + 3 * c + 1]
                if not (hv in (0x11, 0x21, 0x22) if c == 0 else hv == 0x11):
                    raise bad(f'第 {c} 个分量的采样系数 {hv >> 4}×{hv & 15} 不支持（只支持 4:4:4、4:2:2、4:2:0）')
            comp_tq = [d[6 + 3 * c + 2] for c in range(nf)]
            comp_id = [d[6 + 3 * c] for c in range(nf)]
            if len(set(comp_id)) != nf:
                raise bad(f'SOF0 里各分量的编号 {comp_id} 有重复')
            mcu_w, mcu_h = (d[7] >> 4) * 8, (d[7] & 15) * 8       # 解码输出按 MCU 取整
            if -(-size[0] // mcu_w) * mcu_w > JPEG_MAX_DIM or -(-size[1] // mcu_h) * mcu_h > JPEG_MAX_DIM:
                raise bad(f'{size[0]}×{size[1]} 太大，按 MCU 取整后宽高不能超过 {JPEG_MAX_DIM}')
        elif marker == 0xDD:
            if len(d) != 2:
                raise bad('DRI 段的长度不是 4')
        elif marker == 0xDA:
            if size is None or not dqt_ids:
                raise bad('SOS 之前没有 SOF0 或量化表')
            if len(d) != 4 + 2 * 3 or d[0] != 3:
                raise bad('SOS 的分量数和 SOF0 不一致')
            if sorted(dqt_ids) != list(range(len(dqt_ids))):
                raise bad(f'量化表号是 {sorted(dqt_ids)}，必须从 0 起连续（解码器的驱动按"第几张"而不是按表号用表）')
            for c in range(3):
                if d[1 + 2 * c] != comp_id[c]:
                    raise bad(f'SOS 里第 {c} 个分量的编号 {d[1 + 2 * c]} 和 SOF0 里的 {comp_id[c]} 不一致')
                if comp_tq[c] not in dqt_ids:
                    raise bad(f'第 {c} 个分量用的 {comp_tq[c]} 号量化表没有定义')
                td, ta = d[2 + 2 * c] >> 4, d[2 + 2 * c] & 15
                if td > 1 or ta > 1:
                    raise bad(f'SOS 里第 {c} 个分量的霍夫曼表号 {td} / {ta} 不支持（只能是 0、1）')
                if dht_ids and ((0, td) not in dht_ids or (1, ta) not in dht_ids):       # 一张都没有时解码器用标准表
                    raise bad(f'SOS 里第 {c} 个分量用的霍夫曼表（DC {td} / AC {ta}）没有定义')
            if (d[7], d[8], d[9]) != (0, 63, 0):
                raise bad('SOS 的参数不是基线的固定值（Ss = 0，Se = 63，Ah = Al = 0）')
            if pos + 2 + seg_len >= n_all:
                raise bad('SOS 后面没有压缩数据')
            return size
        elif marker == 0xC2:
            raise bad('渐进式（SOF2），硬件解码器只认基线')
        else:
            raise bad(f'文件头里出现了不支持的段 FF {marker:02X}（标记之间也不能有填充字节）')
        pos += 2 + seg_len


# ── 有限范围（协议「画面的色彩」）─────────────────────────────────────────
# 走 YUV422 帧缓冲时 Tab5 的 DSI 桥按有限范围把 YCbCr 换回 RGB：亮度 16 是黑、235 是白。JPEG 里的 YCbCr 是全范围的，
# 所以发送端编码之前先把画面压到有限范围（三个通道各自 v → 16 + v × 219 / 255），桥展开回来就是原来的颜色。
_TO_LIMITED = [16 + (v * 219 + 127) // 255 for v in range(256)]
_FROM_LIMITED = [min(255, max(0, ((v - 16) * 255 + 109) // 219)) for v in range(256)]


def to_limited(img):
    """全范围的 RGB 画面 → 有限范围（走 YUV422 的连接上，编码之前过这一遍）。"""
    return img.point(_TO_LIMITED * 3)


def from_limited(img):
    """to_limited 的逆变换：接收端把解出来的画面展开回全范围（模拟 DSI 桥做的事）。16 以下都是 0、235 以上都是 255。"""
    return img.point(_FROM_LIMITED * 3)


def view_to_fb(view):
    """横屏画面（1280×720）→ 帧缓冲的排列（720×1280）：逆时针转 90°。
    帧缓冲第 r 行、第 c 列 = 横屏画面第 c 行、第 (1279 − r) 列。"""
    if view.size != (VIEW_W, VIEW_H):
        raise ValueError(f'画面必须是 {VIEW_W}×{VIEW_H}，现在是 {view.size}')
    return view.transpose(Image.Transpose.ROTATE_90)


def fb_to_view(fb):
    """view_to_fb 的逆变换：把帧缓冲排列的图转回人看的横屏画面。"""
    if fb.size != (FB_W, FB_H):
        raise ValueError(f'帧缓冲必须是 {FB_W}×{FB_H}，现在是 {fb.size}')
    return fb.transpose(Image.Transpose.ROTATE_270)
