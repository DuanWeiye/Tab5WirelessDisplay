#!/usr/bin/env python3
"""无线显示协议 v2 的密码学部分（PROTOCOL.md「认证和加密」）：配对（SPAKE2，RFC 9382）、每次连接的会话密钥、逐报文加密。

只给测试工具用：P-256 的点运算是拿 Python 的大整数直接算的，用时随数据变化，不能搬到要防旁路的地方去。
X25519 和 ChaCha20-Poly1305 用 cryptography 库，其余用标准库。算式改了先改 PROTOCOL.md，再改这里（固件和 C# 各有一份同样的）。
"""
import hashlib
import hmac
import secrets
import struct

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305

ID_LEN, KEY_LEN, TAG_LEN, POINT_LEN, CONFIRM_LEN, X25519_LEN, KEY_ID_LEN = 16, 32, 16, 65, 32, 32, 8
PBKDF2_SALT, PBKDF2_ROUNDS, W0_LEN = b'Tab5 WinDisplay v2 password', 10000, 40
INFO_PAIRING_KEY = b'Tab5 WinDisplay v2 pairing key'
INFO_SESSION = b'Tab5 WinDisplay v2 session'
INFO_KEY_ID = b'Tab5 WinDisplay v2 key id'


class CryptoError(Exception):
    """对端给的东西过不了检查：点不在曲线上、共享值全 0、认证码对不上。"""


# ── P-256 ────────────────────────────────────────────────────────────────
_P = 0xffffffff00000001000000000000000000000000ffffffffffffffffffffffff
_N = 0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551     # 群的阶
_B = 0x5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b
_G = (0x6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296,
      0x4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5)
# 点用 (x, y) 表示，None 是无穷远点


def _on_curve(pt):
    x, y = pt
    return 0 <= x < _P and 0 <= y < _P and (y * y - (x * x * x - 3 * x + _B)) % _P == 0


def _add(p, q):
    if p is None:
        return q
    if q is None:
        return p
    (x1, y1), (x2, y2) = p, q
    if x1 == x2:
        if (y1 + y2) % _P == 0:
            return None
        lam = (3 * x1 * x1 - 3) * pow(2 * y1, -1, _P) % _P
    else:
        lam = (y2 - y1) * pow(x2 - x1, -1, _P) % _P
    x3 = (lam * lam - x1 - x2) % _P
    return x3, (lam * (x1 - x3) - y1) % _P


def _neg(p):
    return None if p is None else (p[0], -p[1] % _P)


def _mul(k, p):
    r = None
    while k:
        if k & 1:
            r = _add(r, p)
        p = _add(p, p)
        k >>= 1
    return r


def _decompress(hexstr):
    """SEC1 压缩格式（02 / 03 ‖ X）→ 点。只用来展开下面两个常量。"""
    raw = bytes.fromhex(hexstr)
    x = int.from_bytes(raw[1:], 'big')
    y = pow((x * x * x - 3 * x + _B) % _P, (_P + 1) // 4, _P)       # p ≡ 3 (mod 4)
    if y & 1 != raw[0] & 1:
        y = _P - y
    assert _on_curve((x, y))
    return x, y


# RFC 9382 给 P-256 定的两个点
_M = _decompress('02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f')
_NPT = _decompress('03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49')


def point_to_bytes(pt):
    return b'\x04' + pt[0].to_bytes(32, 'big') + pt[1].to_bytes(32, 'big')


def point_from_bytes(raw):
    """65 字节的非压缩格式 → 点。长度不对、不是 04 开头、不在曲线上都抛 CryptoError。"""
    if len(raw) != POINT_LEN or raw[0] != 4:
        raise CryptoError('点的编码不对（要 65 字节、04 开头）')
    pt = (int.from_bytes(raw[1:33], 'big'), int.from_bytes(raw[33:], 'big'))
    if not _on_curve(pt):
        raise CryptoError('点不在曲线上')
    return pt


# ── 基本件 ───────────────────────────────────────────────────────────────
def hkdf(ikm, salt, info, length):
    """HKDF-SHA256（RFC 5869）。salt 是空串时按规定当成 32 字节的 0。"""
    prk = hmac.new(salt or bytes(32), ikm, hashlib.sha256).digest()
    out, block = b'', b''
    for i in range(1, -(-length // 32) + 1):
        block = hmac.new(prk, block + info + bytes([i]), hashlib.sha256).digest()
        out += block
    return out[:length]


def password_to_w0(password):
    """密码 → w0（40 字节）。电脑那边存它就行，不用存密码本身。"""
    return hashlib.pbkdf2_hmac('sha256', password.encode('utf-8'), PBKDF2_SALT, PBKDF2_ROUNDS, W0_LEN)


def w_from_w0(w0):
    return int.from_bytes(w0, 'big') % _N


# ── 配对：SPAKE2 ─────────────────────────────────────────────────────────
class Spake2:
    """SPAKE2 的一方。role 是 'A'（电脑）或 'B'（Tab5）。建出来就有 pub（自己要发的 pA / pB，65 字节）；
    收到对方的之后调 finish()。每个对象只用一次（随机数不能重复使用）。scalar 只给自测对测试向量用。"""

    def __init__(self, role, w, scalar=None):
        assert role in ('A', 'B')
        self.role, self.w = role, w
        self._k = scalar if scalar is not None else 1 + secrets.randbelow(_N - 1)
        self.pub = point_to_bytes(_add(_mul(self._k, _G), _mul(w, _M if role == 'A' else _NPT)))

    def finish(self, peer_pub, id_a, id_b, aad=b''):
        """返回 (Ke, cA, cB)。对方给的点不合格、或者算出来的 K 是无穷远点，抛 CryptoError。"""
        d = self.finish_detail(peer_pub, id_a, id_b, aad)
        return d['Ke'], d['cA'], d['cB']

    def finish_detail(self, peer_pub, id_a, id_b, aad=b''):
        """和 finish 一样，但把中间值也给出来（K、TT、Ka、KcA、KcB）：测试向量要带上它们，
        另外两种语言的实现对不上时才看得出错在哪一步。"""
        peer = point_from_bytes(peer_pub)
        k_pt = _mul(self._k, _add(peer, _neg(_mul(self.w, _NPT if self.role == 'A' else _M))))
        if k_pt is None:
            raise CryptoError('K 是无穷远点')
        p_a, p_b = (self.pub, peer_pub) if self.role == 'A' else (peer_pub, self.pub)
        k_raw = point_to_bytes(k_pt)
        tt = b''.join(struct.pack('<Q', len(v)) + v for v in (id_a, id_b, p_a, p_b, k_raw, self.w.to_bytes(32, 'big')))
        digest = hashlib.sha256(tt).digest()
        ke, ka = digest[:16], digest[16:]
        kc = hkdf(ka, b'', b'ConfirmationKeys' + aad, 32)
        return {'K': k_raw, 'TT': tt, 'Ke': ke, 'Ka': ka, 'KcA': kc[:16], 'KcB': kc[16:],
                'cA': hmac.new(kc[:16], tt, hashlib.sha256).digest(), 'cB': hmac.new(kc[16:], tt, hashlib.sha256).digest()}


def pairing_key(ke):
    return hkdf(ke, b'', INFO_PAIRING_KEY, KEY_LEN)


def key_id(pair_key):
    """配对密钥的标识（AUTH 里带着）：两边存的是不是同一把，Tab5 靠它看出来。"""
    return hmac.new(pair_key, INFO_KEY_ID, hashlib.sha256).digest()[:KEY_ID_LEN]


def same(a, b):
    """比较两个校验值，用时不随内容变化。"""
    return hmac.compare_digest(a, b)


# ── 每次连接：一次性 X25519 + 配对密钥 → 会话密钥 ────────────────────────
def x25519_keypair():
    priv = X25519PrivateKey.generate()
    return priv, priv.public_key().public_bytes_raw()


def x25519_shared(priv, peer_pub):
    try:
        dh = priv.exchange(X25519PublicKey.from_public_bytes(peer_pub))
    except ValueError as e:
        raise CryptoError(f'X25519：{e}') from None
    if dh == bytes(X25519_LEN):
        raise CryptoError('X25519 的共享值全 0')
    return dh


def session_keys(pair_key, dh, th):
    """返回 (电脑 → Tab5 方向的密钥, Tab5 → 电脑方向的密钥)。th = SHA-256(HELLO ‖ AUTH ‖ AUTH_OK 三个报文)。"""
    okm = hkdf(dh, pair_key, INFO_SESSION + th, 2 * KEY_LEN)
    return okm[:KEY_LEN], okm[KEY_LEN:]


# ── 逐报文加密 ───────────────────────────────────────────────────────────
class Cipher:
    """一个方向的加密状态：密钥 + 报文计数。seal / open 各调一次就往前走一个报文。"""

    def __init__(self, key):
        self._aead = ChaCha20Poly1305(key)
        self.count = 0

    def _nonce(self):
        n = self.count
        self.count += 1
        return bytes(4) + struct.pack('<Q', n)

    def seal(self, hdr, payload):
        """返回 密文 ‖ 16 字节认证码。hdr 是 8 字节报文头（明文发，但算进认证码）。"""
        return self._aead.encrypt(self._nonce(), payload, hdr)

    def open(self, hdr, sealed):
        """sealed = 密文 ‖ 认证码。认证不过抛 CryptoError。"""
        try:
            return self._aead.decrypt(self._nonce(), sealed, hdr)
        except InvalidTag:
            raise CryptoError('报文的认证码对不上') from None
