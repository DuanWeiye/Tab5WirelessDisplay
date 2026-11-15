#!/usr/bin/env python3
"""wd_send_test.py / wd_mock_tab5.py / wdproto.py 的自测，不需要 Tab5：`python3 wd_selftest.py`，全部通过退出码 0。

覆盖：画面几何换算和 PROTOCOL.md 里的公式一致；密码学部分对得上 RFC 9382 的测试向量和 ../testvectors/v2.json
（C# 和固件的实现对的是同一份）；握手的各条路（配对、免密码重连、密码不对、一边忘了配对、人取消、密钥不一样）；
测试发送端经本机回环发给模拟接收端，收到的帧缓冲和发出的画面一致
（局部更新、整屏滚动、条带超限自动拆分、单帧超限自动降质量、输入事件解析、Tab5 发 BYE、连接中途断开、
触摸回显把触点画在对应的位置）；
模拟接收端能拦下各种不守协议的发送（含握手里的、认证码对不上的、重放的）；自动发现的询问和应答。

`python3 wd_selftest.py --write-vectors`：重新生成 ../testvectors/v2.json（算式改了才需要；RFC 的那一段是抄来的，不会重算）。
"""
import atexit
import contextlib
import hashlib
import io
import json
import os
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time

import numpy as np
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
from PIL import Image

import wd_bad_sender as bad_sender
import wd_mock_tab5 as mock
import wd_send_test as sender
import wdcrypto as wc
import wdproto as wp

failed = []
VECTORS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'testvectors', 'v2.json')

# 回环测试默认用一对已经配对好的存储：发送端的在临时文件里（wd_send_test.py 按路径读），模拟接收端的在内存里
TMP = tempfile.mkdtemp(prefix='wdselftest_')
atexit.register(shutil.rmtree, TMP, ignore_errors=True)     # 里面是配对密钥，用完就删
SENDER_STORE_PATH = os.path.join(TMP, 'sender.json')
SENDER_STORE = wp.PairStore(SENDER_STORE_PATH)
MOCK_STORE = wp.PairStore()
_key = os.urandom(wc.KEY_LEN)
SENDER_STORE.put(MOCK_STORE.id, _key, 'Tab5-mock')
MOCK_STORE.put(SENDER_STORE.id, _key, 'selftest')


def mopts(**kw):
    """模拟接收端的参数：不等帧边界、不打印、用配对好的那份存储（各项都可以另给）。"""
    kw.setdefault('vsync_hz', 0)
    kw.setdefault('quiet', True)
    kw.setdefault('store', MOCK_STORE)
    return mock.Options(**kw)


def P(mtype, arg=0, payload=b'', flags=0):
    """一个要发的报文（到发的时候才按当时的报文计数加密）。"""
    return mtype, arg, payload, flags


def wire(link, msgs):
    """把一串要发的东西变成线上的字节：P(...) 的用 link 打包（握手之后就是加密），bytes 的原样发。"""
    return b''.join(link.pack(*m) if isinstance(m, tuple) else m for m in msgs)


def check(name, ok, detail=''):
    print(('✓ ' if ok else '✗ ') + name + (f'：{detail}' if detail else ''))
    if not ok:
        failed.append(name)


def start_mock(opts):
    """在本机回环上起一个只服务一条连接的模拟接收端，返回 (端口, 取结果的函数)。"""
    srv = socket.create_server(('127.0.0.1', 0))
    box = {}

    def body():
        conn, _ = srv.accept()
        srv.close()
        conn.settimeout(10)
        box['res'] = mock.serve_conn(conn, opts, log=lambda *_: None)

    th = threading.Thread(target=body, daemon=True)
    th.start()

    def result():
        th.join(15)
        return box.get('res')

    return srv.getsockname()[1], result


def run_sender(port, *extra, store=SENDER_STORE_PATH):
    """跑一遍测试发送端，返回 (退出码, 它打印的内容, 参数)。"""
    args = sender.parse_args(['127.0.0.1', '--port', str(port), '--pair-store', store, '--name', 'selftest', *extra])
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        try:
            rc = sender.run(args)
        except SystemExit as e:
            rc = 1
            print(f'退出：{e}')
    return rc, out.getvalue(), args


def jpeg(w, h, mode='RGB', color=(90, 120, 200), **kw):
    """一张纯色的 JPEG。彩色的默认 4:2:2：走 YUV422 帧缓冲的连接（现在握手默认走它）只收这一种，走 RGB565 的也收。"""
    if mode == 'RGB':
        kw.setdefault('subsampling', '4:2:2')
    buf = io.BytesIO()
    Image.new(mode, (w, h), color).save(buf, 'JPEG', **kw)
    return buf.getvalue()


def png(w, h):
    buf = io.BytesIO()
    Image.new('RGB', (w, h)).save(buf, 'PNG')
    return buf.getvalue()


def test_geometry():
    rng = np.random.default_rng(7)
    view = rng.integers(0, 256, (wp.VIEW_H, wp.VIEW_W, 3), dtype=np.uint8)
    fb = np.asarray(wp.view_to_fb(Image.fromarray(view)))
    # PROTOCOL.md：帧缓冲第 r 行、第 c 列 = 横屏画面第 c 行、第 (1279 − r) 列
    r = np.arange(wp.FB_H)[:, None]
    c = np.arange(wp.FB_W)[None, :]
    check('几何：帧缓冲的排列和协议公式一致', fb.shape == (wp.FB_H, wp.FB_W, 3) and np.array_equal(fb, view[c, wp.VIEW_W - 1 - r]))
    back = np.asarray(wp.fb_to_view(Image.fromarray(fb)))
    check('几何：转回横屏和原图一致', np.array_equal(back, view))
    card = np.asarray(sender.make_card())
    corners = {'左上红': card[30, 30], '右上绿': card[30, wp.VIEW_W - 30], '左下蓝': card[wp.VIEW_H - 30, 30],
               '右下白': card[wp.VIEW_H - 30, wp.VIEW_W - 30]}
    want = {'左上红': (255, 0, 0), '右上绿': (0, 255, 0), '左下蓝': (0, 0, 255), '右下白': (255, 255, 255)}
    check('测试卡：四角色块的位置和颜色', all(tuple(corners[k]) == want[k] for k in want))


def test_jpeg_size():
    """接收端那道 JPEG 文件头检查不能把正常编码器的输出拦掉。"""
    bad = []
    for sub in (0, 1, 2):                       # 4:4:4 / 4:2:2 / 4:2:0
        for q in (5, 20, 60, 95):
            for extra in ({}, {'optimize': True}, {'restart_marker_rows': 2}, {'comment': b'x' * 300}):
                for size in ((720, 16), (720, 160), (720, 1280)):
                    try:
                        got = wp.jpeg_size(jpeg(*size, quality=q, subsampling=sub, **extra))
                    except wp.ProtoError as e:
                        got = str(e)
                    if got != size:
                        bad.append(f'{size} 质量 {q} 采样 {sub} {extra}: {got}')
    check('JPEG 文件头检查：放行 PIL 各种参数编出来的基线 JPEG（144 种）', not bad, '；'.join(bad[:3]))


def test_roundtrip():
    # 局部更新：测试卡 12 帧，高质量 4:4:4（指定了 4:2:2 之外的采样，发送端就不说用 YUV422，连接走 RGB565），收到的帧缓冲应和最后一帧几乎一样
    port, result = start_mock(mopts())
    rc, out, args = run_sender(port, '--frames', '12', '--quality', '92', '--subsampling', '444')
    res = result()
    last = np.asarray(wp.view_to_fb(sender.Source(args).frame(11))).astype(int)
    diff = np.abs(np.asarray(res.fb).astype(int) - last).mean() if res else 999
    check('回环：测试卡 12 帧（4:4:4，走 RGB565），正常结束', rc == 0 and res is not None and res.ended_by == 'bye' and res.violation is None and not res.yuv,
          f'违反协议：{res.violation}' if res and res.violation else '')
    check('回环：收到的画面和发出的最后一帧一致', diff < 2.0, f'平均每通道差 {diff:.2f}')
    check('回环：之后各帧只发了变化的条带', res is not None and 8 < res.strips < 8 + 11 * 4, f'{res.strips if res else 0} 个条带 / 12 帧')

    # 画面的色彩：什么都不指定时走 YUV422 帧缓冲——条带是 4:2:2、画面压在有限范围里，接收端展开回来应和发出的一样。
    # 测试卡里有纯黑、纯白和饱和的红绿蓝：范围压错 / 展开错的话黑白两头或者饱和色会差一截
    port, result = start_mock(mopts())
    rc, out, args = run_sender(port, '--frames', '12', '--quality', '92')
    res = result()
    last = np.asarray(wp.view_to_fb(sender.Source(args).frame(11))).astype(int)
    got = np.asarray(res.fb).astype(int) if res else np.full_like(last, 999)
    diff = np.abs(got - last).mean()
    corners = [tuple(got[wp.VIEW_W - 1 - x, y]) for x, y in ((30, 30), (wp.VIEW_W - 30, 30), (30, wp.VIEW_H - 30), (wp.VIEW_W - 30, wp.VIEW_H - 30))]
    want = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255)]
    near = all(abs(a - b) <= 6 for c, w in zip(corners, want) for a, b in zip(c, w))
    check('回环：测试卡 12 帧走 YUV422（4:2:2、有限范围），展开回来和发出的一致，四角的红绿蓝白不走样',
          rc == 0 and res is not None and res.violation is None and res.yuv and '画面走 YUV422' in out and diff < 2.5 and near,
          f'yuv={res.yuv if res else None}，平均每通道差 {diff:.2f}，四角 {corners}')
    # 接收端没说能走 YUV422（加它之前的固件）：发送端不说用它，照旧发 4:2:0、全范围
    port, result = start_mock(mopts(yuv=False))
    rc, out, args = run_sender(port, '--frames', '12', '--quality', '92')
    res = result()
    diff = np.abs(np.asarray(res.fb).astype(int) - last).mean() if res else 999
    check('回环：接收端没说能走 YUV422（旧固件）时发送端照旧走 RGB565（4:2:0、全范围）',
          rc == 0 and res is not None and res.violation is None and not res.yuv and '画面走 RGB565' in out and '，420，' in out and diff < 3.0,
          f'yuv={res.yuv if res else None}，平均每通道差 {diff:.2f}')
    # 发送端没说用 YUV422（加它之前的发送端，--caps 7）：一样照旧
    port, result = start_mock(mopts())
    rc, out, args = run_sender(port, '--frames', '12', '--quality', '92', '--caps', '7')
    res = result()
    diff = np.abs(np.asarray(res.fb).astype(int) - last).mean() if res else 999
    check('回环：发送端没说用 YUV422（旧版发送端）时照旧走 RGB565',
          rc == 0 and res is not None and res.violation is None and not res.yuv and diff < 3.0, f'平均每通道差 {diff:.2f}')

    # 整屏滚动 + 输入事件（走 YUV422）
    port, result = start_mock(mopts(inject_input=True))
    rc, out, args = run_sender(port, '--pattern', 'scroll', '--frames', '6', '--quality', '60')
    res = result()
    last = np.asarray(wp.view_to_fb(sender.Source(args).frame(5))).astype(int)
    diff = np.abs(np.asarray(res.fb).astype(int) - last).mean() if res else 999
    check('回环：整屏滚动 6 帧（YUV422，质量 60）', rc == 0 and res is not None and res.violation is None and res.yuv and res.strips == 48 and diff < 6.0,
          f'{res.strips if res else 0} 个条带，平均每通道差 {diff:.2f}')
    want = ['TOUCH #0(640,360)', 'TOUCH #0(650,360) #1(100,700)', 'TOUCH 全部抬起', 'KEY 02 00 04 00 00 00 00 00',
            'HELD（离上一个输入报文 ', 'KEY 00 00 00 00 00 00 00 00', 'MOUSE dx=5 dy=-3 wheel=0 buttons=0x01']
    check('回环：发送端解析出触摸 / 键盘 / HELD / 鼠标事件', all(w in out for w in want), '' if all(w in out for w in want) else out)

    # 随机局部更新（--pattern patch）、每帧换一种条带高（--strip-mix）：真机上用它查 Tab5 的补解（wd_catchup_check.sh）。
    # 这里只管工具自己：新老条带的边界对不齐时发出去的条带照样拼得回最后一帧，--dump-last 存下的就是最后一帧；
    # 事先压好再连着发（--prebuild）发出去的和边压边发的是同一串帧
    with tempfile.TemporaryDirectory() as tmp:
        dump = os.path.join(tmp, 'last.png')
        port, result = start_mock(mopts())
        rc, out, args = run_sender(port, '--pattern', 'patch', '--strip-mix', '16,48,96,160,320', '--frames', '30', '--quality', '90', '--prebuild', '--inflight', '3', '--dump-last', dump)
        res = result()
        last = np.asarray(wp.view_to_fb(Image.open(dump).convert('RGB'))).astype(int) if os.path.exists(dump) else None
        # 按 16×16 的块比、看差得最多的一块：漏发一个 16 行的条带只占整屏的百分之一，整屏求平均看不出来
        worst = 999.0
        if res and last is not None:
            d = np.abs(np.asarray(res.fb).astype(int) - last).mean(axis=2)
            worst = float(d.reshape(wp.FB_H // 16, 16, wp.FB_W // 16, 16).mean(axis=(1, 3)).max())
        try:
            sender.run(sender.parse_args(['127.0.0.1', '--prebuild', '--pattern', 'touch']))
            refused = False
        except SystemExit as e:
            refused = '触摸回显' in str(e)
        check('--prebuild 配触摸回显：不连接，直接拒绝', refused)
        check('回环：随机局部更新 30 帧、每帧换一种条带高，收到的画面和存下的最后一帧一致',
              rc == 0 and res is not None and res.violation is None and '里挑' in out and '事先压好' in out and res.frames == 30 and worst < 20.0,
              f'16×16 的块里差得最多的一块平均每通道差 {worst:.1f}' + (f'，违反协议：{res.violation}' if res and res.violation else ''))

    # 触摸方式：模拟端握手走完报「触摸板」（发送端在 AUTH 里说了认得才会发），发送端在第一份触摸之前收到
    port, result = start_mock(mopts(inject_input=True, input_mode=1))
    rc, out, args = run_sender(port, '--frames', '6')
    res = result()
    at_mode, at_touch = out.find('INPUT_MODE 触摸板'), out.find('TOUCH #0(640,360)')
    check('回环：Tab5 报的触摸方式（触摸板）排在第一份触摸之前收到', rc == 0 and res is not None and res.violation is None and 0 <= at_mode < at_touch,
          '' if 0 <= at_mode < at_touch else out)
    port, result = start_mock(mopts(inject_input=True))
    rc, out, args = run_sender(port, '--frames', '6')
    result()
    check('回环：不报触摸方式的 Tab5（老固件）照常', rc == 0 and 'INPUT_MODE' not in out and 'TOUCH #0(640,360)' in out, out if 'INPUT_MODE' in out else '')
    # 发送端没在 AUTH 里说认得 INPUT_MODE（加这个报文之前的版本）：Tab5 开着触摸板模式也不发，别的照常
    port, result = start_mock(mopts(inject_input=True, input_mode=1, tpad_gain=15))
    rc, out, args = run_sender(port, '--frames', '6', '--caps', '0')
    res = result()
    check('回环：发送端没声明认得触摸方式、触摸板加速的报文（旧版发送端）时 Tab5 都不发，别的照常',
          rc == 0 and res is not None and res.violation is None and 'INPUT_MODE' not in out and 'TPAD_GAIN' not in out and 'TOUCH #0(640,360)' in out,
          out if 'INPUT_MODE' in out or 'TPAD_GAIN' in out else '')
    # 触摸板加速：模拟端握手走完报「最多 1.5 倍」，发送端在第一份触摸之前收到；只认得 INPUT_MODE 的那一版发送端（能力位只有 bit0）收不到它
    port, result = start_mock(mopts(inject_input=True, input_mode=1, tpad_gain=15))
    rc, out, args = run_sender(port, '--frames', '6')
    res = result()
    at_gain, at_touch = out.find('TPAD_GAIN 1.5 倍'), out.find('TOUCH #0(640,360)')
    check('回环：Tab5 报的触摸板加速（最多 1.5 倍）排在第一份触摸之前收到',
          rc == 0 and res is not None and res.violation is None and 0 <= at_gain < at_touch and 'INPUT_MODE 触摸板' in out,
          '' if 0 <= at_gain < at_touch else out)
    port, result = start_mock(mopts(inject_input=True, input_mode=1, tpad_gain=15))
    rc, out, args = run_sender(port, '--frames', '6', '--caps', '1')
    res = result()
    check('回环：只认得触摸方式的发送端（上一版）收到触摸方式、收不到触摸板加速，别的照常',
          rc == 0 and res is not None and res.violation is None and 'INPUT_MODE 触摸板' in out and 'TPAD_GAIN' not in out and 'TOUCH #0(640,360)' in out,
          out if 'TPAD_GAIN' in out else '')

    # 画质：模拟端握手走完报「质量 85、预算 60 KB」/「质量 40、不降」，发送端收到并认得；加这个报文之前的发送端（能力位 3）收不到，别的照常
    for pic, said in (((85, 60), 'PICTURE 质量 85，预算 60 KB'), ((40, 0), 'PICTURE 质量 40，预算 不降')):
        port, result = start_mock(mopts(inject_input=True, picture=pic))
        rc, out, args = run_sender(port, '--frames', '6')
        res = result()
        check(f'回环：Tab5 报的画质（{said[8:]}）发送端收到了', rc == 0 and res is not None and res.violation is None and said in out and '报文 0x26' not in out,
              '' if said in out else out)
    port, result = start_mock(mopts(inject_input=True, input_mode=1, tpad_gain=15, picture=(85, 60)))
    rc, out, args = run_sender(port, '--frames', '6', '--caps', '3')
    res = result()
    check('回环：没声明认得画质报文的发送端（上一版）收到触摸方式和触摸板加速、收不到画质，别的照常',
          rc == 0 and res is not None and res.violation is None and 'INPUT_MODE 触摸板' in out and 'TPAD_GAIN 1.5 倍' in out
          and 'PICTURE' not in out and '0x26' not in out and 'TOUCH #0(640,360)' in out,
          out if 'PICTURE' in out or '0x26' in out else '')

    # 画质的档位（现在的固件）：认得 PICTURE_MODE 的发送端收到档位和帧周期；只认得 PICTURE 的（上一版，能力位 15）收到折算出来的那一组、收不到 PICTURE_MODE
    port, result = start_mock(mopts(inject_input=True, picture_mode=1, frame_us=15279))
    rc, out, args = run_sender(port, '--frames', '6')
    res = result()
    check('回环：Tab5 报的画质档位（第 1 档、帧周期 15279 微秒）发送端收到了',
          rc == 0 and res is not None and res.violation is None and 'PICTURE_MODE 第 1 档「高画质」，屏幕的帧周期 15279 微秒' in out
          and 'PICTURE 质量' not in out and '报文 0x27' not in out, out)
    port, result = start_mock(mopts(inject_input=True, picture_mode=2))
    rc, out, args = run_sender(port, '--frames', '6', '--caps', '15')
    res = result()
    check('回环：只认得 PICTURE 的发送端（上一版）收到的是档位折算出来的质量和预算，收不到 PICTURE_MODE',
          rc == 0 and res is not None and res.violation is None and 'PICTURE 质量 90，预算 60 KB' in out and 'PICTURE_MODE' not in out and '0x27' not in out, out)
    port, result = start_mock(mopts(inject_input=True, picture_mode=3))
    rc, out, args = run_sender(port, '--frames', '6', '--caps', '11')
    res = result()
    check('回环：两种画质报文都没声明认得的发送端什么都收不到，别的照常',
          rc == 0 and res is not None and res.violation is None and 'PICTURE' not in out and '0x26' not in out and '0x27' not in out and 'TOUCH #0(640,360)' in out, out)

    # 声音：画面之外同时发正弦波。限 20 帧/秒发 30 帧（约 1.5 秒）：声音按墙上的钟发，收到的应该是一段没断过的正弦波，
    # 夹在条带之间来（相邻两个 AUDIO 的间隔远小于一帧的时间不好判，只看最长间隔没有离谱）
    port, result = start_mock(mopts(keep_audio=True))
    rc, out, args = run_sender(port, '--pattern', 'scroll', '--frames', '30', '--fps', '20', '--tone', '1000')
    res = result()
    n = len(res.audio) // 4 if res else 0
    got = np.frombuffer(bytes(res.audio[:n * 4]), dtype='<i2').reshape(-1, 2) if n else np.zeros((0, 2), dtype='<i2')
    want = np.round(32768 * 10 ** (-6 / 20) * np.sin(2 * np.pi * 1000 * np.arange(n) / wp.AUDIO_RATE)).astype('<i2')     # --tone-db 默认 -6
    same = n > 0 and np.array_equal(got[:, 0], want) and np.array_equal(got[:, 1], want)
    check('声音：画面 + 1 kHz 正弦波一起发，正常结束，画面照常',
          rc == 0 and res is not None and res.violation is None and res.ended_by == 'bye' and res.frames == 30,
          f'违反协议：{res.violation}' if res and res.violation else out if rc else '')
    check('声音：收到的是一段没断过的正弦波（左右一样），1 秒以上，每个报文 10 ms',
          same and n >= wp.AUDIO_RATE and res.audio_bytes == res.audio_msgs * 1920 and '扔掉 0 段' in out,
          f'{n} 个采样帧，{res.audio_msgs if res else 0} 个报文，和期望的波形{"一致" if same else "不一致"}')
    check('声音：相邻两个报文到达的间隔没有超过 150 ms', res is not None and 0 < res.audio_gap < 0.15, f'{res.audio_gap * 1e3:.0f} ms' if res else '')
    # Tab5 没说能收声音（加声音之前的固件）：发送端不发，别的照常
    port, result = start_mock(mopts(audio=False))
    rc, out, args = run_sender(port, '--frames', '6', '--fps', '20', '--tone', '1000')
    res = result()
    check('声音：Tab5 没说能收声音（旧固件）时发送端不发，画面照常',
          rc == 0 and res is not None and res.violation is None and res.audio_msgs == 0 and res.frames == 6 and '没说能收声音' in out,
          f'违反协议：{res.violation}' if res and res.violation else '')

    # 条带超过单条带上限：自动对半拆
    port, result = start_mock(mopts(max_strip=40 * 1024))
    rc, out, _ = run_sender(port, '--pattern', 'noise', '--frames', '2', '--quality', '80')
    res = result()
    check('回环：噪点画面的条带超过 40 KB 上限时自动拆小', rc == 0 and res is not None and res.violation is None and res.strips > 16,
          f'{res.strips if res else 0} 个条带 / 2 帧' + (f'，违反协议：{res.violation}' if res and res.violation else ''))

    # 一帧超过单帧上限：自动降质量
    port, result = start_mock(mopts(max_frame=300 * 1024))
    rc, out, _ = run_sender(port, '--pattern', 'noise', '--frames', '2', '--quality', '80')
    res = result()
    check('回环：一帧超过 300 KB 上限时自动降质量重压', rc == 0 and res is not None and res.violation is None and '质量降到' in out
          and res.bytes <= 2 * 300 * 1024, f'{res.bytes // 1024 if res else 0} KB / 2 帧')

    # 流量控制（PROTOCOL.md「流量控制」）。发送端自己报的「先发了几次」之外，模拟端从接收这一头也量（wd_mock_tab5.py 开头）：
    # ahead = ACK 发出去之前下一帧已经到了的帧数，max_unacked = 同时没回 ACK 的最多几帧，held_arrivals = ACK 被压住之后各帧到的时刻
    def max_gap(text):
        return float(text.split('相邻两帧发出的间隔：')[1].split('最大 ')[1].split(' ms')[0]) if '相邻两帧发出的间隔：' in text else -1

    def late_sends(text):
        return int(text.split('先发下一帧：')[1].split(' 次')[0]) if '先发下一帧：' in text else -1

    # 平时保持 2 帧在途：模拟端一秒 20 个帧边界（比发送端编码一帧慢得多），每帧的 ACK 发出去之前下一帧都应该已经到了
    # （最后一帧后面没有帧，所以是 19 / 20）。--inflight 1 时收到 ACK 才发，一帧都不该提前到
    port, result = start_mock(mopts(vsync_hz=20))
    rc, out, _ = run_sender(port, '--pattern', 'scroll', '--frames', '20')
    res = result()
    check('回环：平时保持 2 帧在途（不等上一帧的 ACK 就发下一帧）', rc == 0 and res is not None and res.frames == 20
          and res.ahead >= 15 and res.max_unacked == 1 and late_sends(out) == 0,       # 机器跑满时会少一两帧；收到 ACK 才发的是 0
          f'20 帧里 {res.ahead if res else 0} 帧没等 ACK 就到了，先发了 {late_sends(out)} 次')
    port, result = start_mock(mopts(vsync_hz=20))
    rc, out, _ = run_sender(port, '--pattern', 'scroll', '--frames', '20', '--inflight', '1')
    res = result()
    check('回环：--inflight 1 时收到 ACK 才发下一帧', rc == 0 and res is not None and res.frames == 20
          and res.ahead == 0 and late_sends(out) == 0, f'20 帧里 {res.ahead if res else 0} 帧没等 ACK 就到了')

    # 第 6 帧的 ACK 晚 0.6 秒才到（它在空中丢了、等 Tab5 的 TCP 重传）：这时在途的是第 6、7 两帧；第 6 帧发出 60 ms 还没确认，
    # 发送端应接着发第 8、9 两帧（到 4 帧在途为止），收到的画面仍然和最后一帧一致。
    # 多发的两帧要过了门限才发、而且是连着发的（隔一帧的编码时间）：门限从最早在途的那一帧算起；
    # 要是从最近发出的那一帧算起（发一帧再等一个门限），两帧会隔 60 ms 以上。空闲时隔 3 ms 左右，机器跑满时见过 23 ms，判定线取 45 ms。
    # （模拟端把晚到的 ACK 之后的报文也一起压住，量不出「多发的几帧让丢的 ACK 更快重传」，那要对真机量。）
    port, result = start_mock(mopts(late_ack=(6, 0.6)))
    rc, out, args = run_sender(port, '--pattern', 'scroll', '--frames', '12')
    res = result()
    last = np.asarray(wp.view_to_fb(sender.Source(args).frame(11))).astype(int)
    diff = np.abs(np.asarray(res.fb).astype(int) - last).mean() if res else 999
    held = [round(t * 1e3) for t in res.held_arrivals] if res else []
    check('回环：一个 ACK 晚到 0.6 秒时接着发后面的帧（多发 2 帧，到 4 帧在途为止）', rc == 0 and res is not None and res.violation is None
          and res.frames == 12 and diff < 6.0 and late_sends(out) == 2 and res.max_unacked == 4 and 200 < max_gap(out) < 600,
          f'{res.frames if res else 0} 帧，先发了 {late_sends(out)} 次，同时没回 ACK 的最多 {res.max_unacked if res else 0} 帧，'
          f'画面最长停 {max_gap(out):.0f} ms，平均每通道差 {diff:.2f}')
    check('回环：迟到之后多发的两帧是过了门限连着发的', len(held) == 3 and held[1] >= 45 and held[2] - held[1] < 45,
          f'ACK 压住之后 {held} ms 各收到一帧')
    # 对照一：--inflight 1（平时只有 1 帧在途）时，压住之后到的 3 帧都是迟到多发的
    port, result = start_mock(mopts(late_ack=(6, 0.6)))
    rc, out, _ = run_sender(port, '--pattern', 'scroll', '--frames', '12', '--inflight', '1')
    res = result()
    held = [round(t * 1e3) for t in res.held_arrivals] if res else []
    check('回环：对照——--inflight 1 时 ACK 晚到后多发 3 帧', rc == 0 and res is not None and res.frames == 12 and late_sends(out) == 3
          and res.max_unacked == 4 and len(held) == 3 and held[0] >= 45, f'先发了 {late_sends(out)} 次，ACK 压住之后 {held} ms 各收到一帧')
    # 对照二：死等 ACK（--max-inflight 1）时一帧都不多发，画面停满 0.6 秒
    port, result = start_mock(mopts(late_ack=(6, 0.6)))
    rc, out, _ = run_sender(port, '--pattern', 'scroll', '--frames', '12', '--max-inflight', '1')
    res = result()
    check('回环：对照——死等 ACK 时画面停满 0.6 秒', rc == 0 and res is not None and res.frames == 12 and res.max_unacked == 1
          and max_gap(out) >= 590, f'画面最长停 {max_gap(out):.0f} ms，同时没回 ACK 的最多 {res.max_unacked if res else 0} 帧')
    # 倒数第二帧的 ACK 晚到：最后一帧这时已经在路上了（平时的 2 帧），后面没有帧可发——「先发了几次」数的是真多发出去的帧，这里是 0
    port, result = start_mock(mopts(late_ack=(11, 0.3)))
    rc, out, _ = run_sender(port, '--pattern', 'scroll', '--frames', '12')
    res = result()
    check('回环：ACK 晚到但后面没有帧可发时不算多发', rc == 0 and res is not None and res.frames == 12 and late_sends(out) == 0
          and res.max_unacked == 2, f'先发了 {late_sends(out)} 次')
    # --inflight 的范围：1 到 --max-inflight，报错里说的是 --inflight
    for bad_args, want in ((('--inflight', '0'), '--inflight 要在 1 和 --max-inflight（4）之间'),
                           (('--inflight', '5'), '--inflight 要在 1 和 --max-inflight（4）之间'),
                           (('--inflight', '3', '--max-inflight', '2'), '--inflight 要在 1 和 --max-inflight（2）之间')):
        rc, out, _ = run_sender(1, '--frames', '3', *bad_args)      # 参数在连接之前就查，端口随便填
        check(f'发送工具：{" ".join(bad_args)} 连接之前就报出来', rc == 1 and want in out, out.strip())

    # 每帧的 ACK 本来就慢（模拟端等帧边界，一秒 5 次，往返约 0.2 秒，比 --ack-wait 的 60 ms 长得多）：不算迟到，不应该往路上多压帧
    port, result = start_mock(mopts(vsync_hz=5))
    rc, out, _ = run_sender(port, '--pattern', 'scroll', '--frames', '10')
    res = result()
    check('回环：每帧的 ACK 本来就慢时不多压帧', rc == 0 and res is not None and res.frames == 10 and late_sends(out) == 0,
          f'先发了 {late_sends(out)} 次')

    # Tab5 那边用户退出了显示模式：第 3 帧之后发 BYE
    port, result = start_mock(mopts(bye_after=3))
    rc, out, _ = run_sender(port, '--frames', '10')
    res = result()
    check('回环：Tab5 发 BYE 时发送端正常收尾', rc == 0 and res is not None and res.ended_by == 'dev_bye' and res.frames == 3
          and 'Tab5 退出了显示模式' in out and '发了 3 帧' in out, '' if rc == 0 else out)

    # Tab5 发 BYE 的时候前面还有一个 ACK 卡着没到（第 6 帧的 ACK 晚 0.6 秒，发送端接着发了第 7 帧，Tab5 收完第 7 帧发 BYE）：
    # 卡着的 ACK、第 7 帧的 ACK 和 BYE 应该都到发送端手里，发送端看到的是 BYE 而不是断线
    port, result = start_mock(mopts(late_ack=(6, 0.6), bye_after=7))
    rc, out, _ = run_sender(port, '--pattern', 'scroll', '--frames', '12')
    res = result()
    check('回环：BYE 前面有 ACK 卡着时发送端仍然收到 BYE', rc == 0 and res is not None and res.ended_by == 'dev_bye'
          and res.frames == 7 and 'Tab5 退出了显示模式' in out and '发了 7 帧' in out, '' if rc == 0 else f'退出码 {rc}：{out}')

    # 发了 BYE 的一方要等对方关了连接再关。发送端满速发整屏滚动，Tab5 发 BYE 的时候路上还有数据：
    # 立刻关的话系统回的是 RST（Windows 上的发送端会因此读不到 BYE），发送端的工具会把它报出来
    closed = []
    for _ in range(3):
        port, result = start_mock(mopts(bye_after=5))
        rc, out, _ = run_sender(port, '--pattern', 'scroll', '--frames', '200')
        result()
        closed.append(next((line for line in out.splitlines() if line.startswith('BYE 之后')), f'退出码 {rc}，没有报怎么关的'))
    check('回环：Tab5 发完 BYE 等发送端关了才关连接（3 次都没有重置）', all(c == 'BYE 之后 Tab5 正常关闭' for c in closed),
          '；'.join(sorted(set(closed))))

    # BYE 晚到发送端（Tab5 发出的报文在空中丢了、等重传）：发送端隔 1 秒才关连接，接收端应该还等着，而不是半秒就自己关了
    port, result = start_mock(mopts(bye_after=3))
    t0 = time.monotonic()
    rc, out, _ = run_sender(port, '--frames', '10', '--bye-lag', '1')
    took = time.monotonic() - t0
    res = result()
    check('回环：BYE 晚 1 秒才到发送端，接收端等到发送端关了连接才关', rc == 0 and res is not None and res.ended_by == 'dev_bye'
          and res.drain_end == 'closed' and took >= 1.0 and 'BYE 之后 Tab5 正常关闭' in out,
          f'退出码 {rc}，用了 {took:.2f} 秒，收尾 {res.drain_end if res else None}')

    # Tab5 掉线：第 3 帧之后直接断开
    port, result = start_mock(mopts(drop_after=3))
    rc, out, _ = run_sender(port, '--frames', '10')
    res = result()
    check('回环：连接中途断开时发送端报出来（退出码 2）', rc == 2 and res is not None and res.ended_by == 'dev_drop'
          and '连接中途断了' in out and '发了 3 帧' in out, f'退出码 {rc}：{out}' if rc != 2 else '')

    # --first-delay：连上之后晚一点才发第一帧。没超过接收端给第一帧的期限时照常跑完；超过了被接收端断开（说明确实是在第一帧之前等的）
    port, result = start_mock(mopts(first_frame_s=1.0))
    t0 = time.monotonic()
    rc, out, _ = run_sender(port, '--frames', '3', '--first-delay', '0.3')
    took = time.monotonic() - t0
    res = result()
    check('回环：晚 0.3 秒发第一帧（期限 1 秒），照常跑完', rc == 0 and res is not None and res.ended_by == 'bye' and res.frames == 3 and took >= 0.3,
          f'退出码 {rc}，用了 {took:.2f} 秒，模拟端 {res.ended_by if res else None}')
    port, result = start_mock(mopts(first_frame_s=0.5))
    rc, out, _ = run_sender(port, '--frames', '3', '--first-delay', '1.2')
    res = result()
    check('回环：晚 1.2 秒发第一帧（期限 0.5 秒），被接收端断开', rc == 2 and res is not None and res.ended_by == 'violation'
          and '没有发完第一帧' in (res.violation or ''), f'退出码 {rc}，模拟端 {res.ended_by if res else None}：{res.violation if res else None}')


def test_drain_peer():
    """模拟接收端发完 BYE 之后的收尾（和固件是同一套规矩）：四种结束方式和各自的用时。"""
    def run(peer_job, idle, most):
        a, b = socket.socketpair()
        th = threading.Thread(target=peer_job, args=(b,), daemon=True)
        th.start()
        t0 = time.monotonic()
        how = mock.drain_peer(a, idle=idle, most=most)
        took = time.monotonic() - t0
        a.close()
        th.join(3)
        b.close()
        return how, took

    def trickle(sec):       # 每 0.05 秒发一点数据，发 sec 秒，不关连接
        def job(s):
            t_end = time.monotonic() + sec
            try:
                while time.monotonic() < t_end:
                    s.sendall(b'x' * 100)
                    time.sleep(0.05)
            except OSError:
                pass
        return job

    how, took = run(lambda s: (time.sleep(0.2), s.shutdown(socket.SHUT_WR)), 0.4, 1.0)
    check('收尾：对方 0.2 秒后关了连接', how == 'closed' and 0.15 <= took < 0.4, f'{how}，{took:.2f} 秒')
    how, took = run(lambda s: time.sleep(1.0), 0.4, 1.0)
    check('收尾：对方既不发数据也不关，等满空闲时限（0.4 秒）就不等了', how == 'idle' and 0.35 <= took < 0.7, f'{how}，{took:.2f} 秒')
    how, took = run(trickle(1.5), 0.4, 1.0)
    check('收尾：对方一直在发数据，到总时限（1 秒）为止', how == 'limit' and 0.95 <= took < 1.3, f'{how}，{took:.2f} 秒')
    how, took = run(trickle(0.8), 0.4, 1.0)
    check('收尾：对方发了 0.8 秒数据之后不发也不关，还是到总时限（1 秒）为止，不再另等一个空闲时限', how == 'limit' and 0.95 <= took < 1.15,
          f'{how}，{took:.2f} 秒')


def test_touch_echo():
    """触摸回显：模拟接收端在第一帧之后发一串触摸和一个键盘报文，发送端应把触点画回画面里对应的位置。
    场景：0 号手指从 (300,200) 按下、拖到 (900,500)、一直没抬起；这期间 1 号手指在 (1100,600) 点了一下。"""
    drag = [(300 + 50 * k, 200 + 25 * k) for k in range(13)]
    end = wp.TOUCH_PT.pack(0, *drag[-1])
    msgs = [(wp.T_TOUCH, 1, wp.TOUCH_PT.pack(0, x, y)) for x, y in drag]
    msgs += [(wp.T_TOUCH, 2, end + wp.TOUCH_PT.pack(1, 1100, 600)),
             (wp.T_TOUCH, 1, end),
             (wp.T_KEY, 0, bytes([0x02, 0, 0x04, 0, 0, 0, 0, 0]))]
    port, result = start_mock(mopts(inject_input=True, inject_at=1, inject_msgs=msgs))
    rc, out, _ = run_sender(port, '--pattern', 'touch', '--duration', '1.5', '--quality', '92', '--subsampling', '444')
    res = result()
    ok = rc == 0 and res is not None and res.violation is None and res.ended_by == 'bye' and res.frames >= 2
    check('触摸回显：到时间正常结束，中间画面变过', ok, '' if ok else f'退出码 {rc}，{res.frames if res else 0} 帧：{out}')
    want = 'TOUCH 报文 15 个，按下 2 次，抬起 1 次'
    check('触摸回显：按下 / 抬起的次数', want in out, '' if want in out else out)
    # 0 号手指从第 1 帧之后一直按到结束（约 1.5 秒）：「最长的一笔」要把它算进去，不能只算已经抬起的 1 号（点了一下）
    held = float(out.split('最长的一笔按住了 ')[1].split(' 秒')[0]) if '最长的一笔按住了 ' in out else -1
    check('触摸回显：最长的一笔算上结束时还按着的', 1.0 <= held <= 2.0, f'{held:.1f} 秒')
    if res is None:
        return
    view = np.asarray(wp.fb_to_view(res.fb)).astype(int)

    def near(xy, color):
        return int(np.abs(view[xy[1], xy[0]] - color).max()) <= 40

    c0 = sender.TouchEcho.COLORS[0]                         # 0 号手指：按着，原色
    c1 = tuple(v // 2 for v in sender.TouchEcho.COLORS[1])  # 1 号手指：已经抬起，变暗
    bg = (24, 28, 36)
    # 圈上的取样点取在对角线方向：正右 / 正下方向落在十字线上，圈不画也是这个颜色。圈的线宽 3，取线宽中间
    k = round((sender.TouchEcho.RING - 1.5) / 2 ** 0.5)
    spots = {'0 号的轨迹中段 (600,350)': ((600, 350), c0), f'0 号手指周围的圈 ({900 + k},{500 + k})': ((900 + k, 500 + k), c0),
             '1 号点过的位置 (1100,600)': ((1100, 600), c1), '没碰过的地方 (700,650)': ((700, 650), bg),
             '左右镜像的位置 (679,350) 应是底色': ((679, 350), bg), '上下镜像的位置 (600,369) 应是底色': ((600, 369), bg)}
    wrong = [f'{k} 读到 {tuple(view[xy[1], xy[0]])}' for k, (xy, c) in spots.items() if not near(xy, c)]
    check('触摸回显：触点画在对应的位置（经过转 90°、压缩、解码、转回来）', not wrong, '；'.join(wrong))
    check('触摸回显：键盘事件显示在状态行下面', int(view[54:80, 480:900].max()) > 120)


def test_ctrl_c():
    """按 Ctrl-C：没发完就被打断的退出码是 130（调用它的脚本据此停下来）；触摸回显本来就是按 Ctrl-C 结束的，退出码 0。
    两种情况都要先给 Tab5 发 BYE 再走。发送端要单独起一个进程才能收到信号。"""
    script = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'wd_send_test.py')
    for name, extra, want in (('没发完被 Ctrl-C 打断：退出码 130', ['--frames', '100000', '--fps', '20'], 130),
                              ('画面发完后在 --hold 期间按 Ctrl-C：退出码 0', ['--frames', '5', '--hold', '30'], 0),
                              ('触摸回显按 Ctrl-C 结束：退出码 0', ['--pattern', 'touch'], 0)):
        port, result = start_mock(mopts())
        p = subprocess.Popen([sys.executable, script, '127.0.0.1', '--port', str(port),
                              '--pair-store', SENDER_STORE_PATH, '--name', 'selftest', *extra],
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        time.sleep(1.5)
        p.send_signal(signal.SIGINT)
        try:
            out = p.communicate(timeout=10)[0]
        except subprocess.TimeoutExpired:
            p.kill()
            out = p.communicate()[0] + '\n（10 秒没退出，已强行结束）'
        res = result()
        ok = p.returncode == want and '按了 Ctrl-C' in out and res is not None and res.ended_by == 'bye' and res.frames >= 1
        check(name, ok, '' if ok else f'退出码 {p.returncode}，模拟端 {res.ended_by if res else None}：{out}')


def violation_case(name, keyword, msgs, opts=None, handshake=True, store=None):
    """连上模拟接收端，发一串报文，看它是不是因为 keyword 这个原因把连接断了。
    msgs 里 P(...) 是握手之后加密发的报文，bytes 是原样发的字节；handshake=False 时不握手（收下 HELLO 就发）。
    返回这条连接的 Result。"""
    port, result = start_mock(opts or mopts())
    s = socket.create_connection(('127.0.0.1', port), timeout=5)
    try:
        if handshake:
            link, _ = wp.sender_handshake(s, store or SENDER_STORE, 'selftest')
        else:
            link = wp.Link(s)
            link.recv(256)
        s.sendall(wire(link, msgs))
        s.settimeout(3)
        while s.recv(4096):     # 等对方断开（中间可能有 ACK）
            pass
    except (OSError, wp.ProtoError):
        pass
    finally:
        s.close()
    res = result()
    ok = res is not None and res.ended_by == 'violation' and keyword in (res.violation or '')
    check(f'拦截：{name}', ok, '' if ok else f'结束方式 {res.ended_by if res else None}，原因 {res.violation if res else None}')
    return res


def test_violations():
    full = [P(wp.T_STRIP, r0, jpeg(720, 160)) for r0 in range(0, 1280, 160)]
    present = P(wp.T_PRESENT, 0, wp.PRESENT.pack(0, 0))
    present1 = P(wp.T_PRESENT, 0, wp.PRESENT.pack(1, 0))
    violation_case('渐进式 JPEG', '渐进式', [P(wp.T_STRIP, 0, jpeg(720, 160, progressive=True))])
    violation_case('条带宽不是 720', '宽 704', [P(wp.T_STRIP, 0, jpeg(704, 160))])
    violation_case('条带高不是 16 的倍数', '高 24', [P(wp.T_STRIP, 0, jpeg(720, 24))])
    violation_case('起始行不是 16 的倍数', '起始行 8', [P(wp.T_STRIP, 8, jpeg(720, 160))])
    violation_case('条带超出帧缓冲', '超出帧缓冲', [P(wp.T_STRIP, 1200, jpeg(720, 160))])
    violation_case('灰度 JPEG', '颜色分量', [P(wp.T_STRIP, 0, jpeg(720, 160, mode='L', color=128))])
    violation_case('PNG 冒充条带', '开头不是 SOI', [P(wp.T_STRIP, 0, png(720, 16))])
    violation_case('同一帧条带重叠', '重叠', [P(wp.T_STRIP, 0, jpeg(720, 160)), P(wp.T_STRIP, 144, jpeg(720, 160))])
    violation_case('第一帧没覆盖整屏', '第一帧必须覆盖整屏', full[:7] + [present])
    violation_case('没有条带就 PRESENT', '一个条带都没有', full + [present, present1])
    violation_case('条带超过单条带上限', '超过上限 2048',
                   [P(wp.T_STRIP, 0, jpeg(720, 160) + bytes(3000))], mopts(max_strip=2048))
    violation_case('条带超过单条带上限时只发了报文头', '超过上限 2048',
                   [wp.HDR.pack(wp.T_STRIP, 0, 0, 5000)], mopts(max_strip=2048))
    violation_case('条带长度是 0', '长度是 0', [P(wp.T_STRIP, 0)])
    violation_case('报文头只发一半就不发了', '没有新数据', [wp.HDR.pack(wp.T_STRIP, 0, 0, 100)[:3]],
                   mopts(stall_s=0.5))
    violation_case('条带载荷只发一部分就不发了', '没有新数据', [wp.HDR.pack(wp.T_STRIP, 0, 0, 2000) + bytes(500)],
                   mopts(stall_s=0.5))
    violation_case('握手之后什么都不发', '没有发完第一帧', [], mopts(first_frame_s=0.5))
    violation_case('第一帧只发了一部分就不发了', '没有发完第一帧', full[:3], mopts(first_frame_s=0.5))
    violation_case('第一帧的报文头卡在一半，过了第一帧的期限', '没有发完第一帧', full[:3] + [wp.HDR.pack(wp.T_STRIP, 0, 480, 100)[:3]],
                   mopts(first_frame_s=0.5))
    for name, data in bad_sender.bad_jpegs():
        violation_case(f'JPEG 结构不对：{name}', 'JPEG 文件头', [P(wp.T_STRIP, 0, data)])
    violation_case('一帧超过单帧上限', '单帧上限', full, mopts(max_frame=4096))
    violation_case('不认识的报文类型', '不认识的报文类型', [P(0x77)])
    violation_case('PRESENT 载荷长度不对', 'PRESENT 载荷', full + [P(wp.T_PRESENT, 0, b'\0' * 4)])
    violation_case('AUDIO 没有载荷', 'AUDIO 载荷 0 字节', [P(wp.T_AUDIO)])
    violation_case('AUDIO 的长度不是 4 的倍数', 'AUDIO 载荷 1922 字节', [P(wp.T_AUDIO, 0, bytes(1922))])
    violation_case('AUDIO 超过 20 ms', '超过上限 3840', [P(wp.T_AUDIO, 0, bytes(3844))])
    violation_case('AUDIO 的 arg 不是 0', 'AUDIO 的 arg', [P(wp.T_AUDIO, 1, bytes(1920))])
    violation_case('Tab5 没说能收声音却发来 AUDIO', '没说能收声音', [P(wp.T_AUDIO, 0, bytes(1920))], mopts(audio=False))
    violation_case('握手之后只发声音、不发画面（第一帧的期限不因为声音往后挪）', '没有发完第一帧',
                   [P(wp.T_AUDIO, 0, bytes(1920))] * 5, mopts(first_frame_s=0.5))
    # 画面的色彩：说了走 YUV422 的连接只能发 4:2:2
    violation_case('走 YUV422 的连接上发 4:2:0 的条带', '不是 4:2:2', [P(wp.T_STRIP, 0, jpeg(720, 160, subsampling='4:2:0'))])
    violation_case('走 YUV422 的连接上发 4:4:4 的条带', '不是 4:2:2', [P(wp.T_STRIP, 0, jpeg(720, 160, subsampling='4:4:4'))])
    # Tab5 没说能走（旧固件）而发送端硬说用它：守规矩的 sender_handshake 不会这么发，这里自己拼 AUTH 的能力位
    port, result = start_mock(mopts(yuv=False))
    s = socket.create_connection(('127.0.0.1', port), timeout=5)
    try:
        class Misread(wp.Hello):        # 发送端把 HELLO 看错了：以为 Tab5 说了能走
            def __init__(self, ver, payload):
                super().__init__(ver, payload)
                self.caps |= wp.CAP_DEV_YUV422
        real, wp.Hello = wp.Hello, Misread
        wp.sender_handshake(s, SENDER_STORE, 'selftest')
        s.settimeout(3)
        while s.recv(4096):
            pass
    except (OSError, wp.ProtoError):
        pass
    finally:
        wp.Hello = real
        s.close()
    res = result()
    ok = res is not None and res.ended_by == 'violation' and '没说能走 YUV422' in (res.violation or '')
    check('拦截：Tab5 没说能走 YUV422，发送端却在 AUTH 里说用它', ok, '' if ok else f'结束方式 {res.ended_by if res else None}，原因 {res.violation if res else None}')

    # 守协议的两帧（第二帧只有一个条带）不应被拦。两帧之间停的时间比第一帧的期限长：期限只管第一帧，之后可以一直不发
    port, result = start_mock(mopts(first_frame_s=0.5))
    s = socket.create_connection(('127.0.0.1', port), timeout=5)
    link, _ = wp.sender_handshake(s, SENDER_STORE, 'selftest')
    # 声音可以在第一帧之前来、夹在条带之间来，长度 4 字节到 3840 字节都行
    s.sendall(wire(link, [P(wp.T_AUDIO, 0, bytes(4))] + full[:4] + [P(wp.T_AUDIO, 0, bytes(wp.AUDIO_MAX))] + full[4:] + [present]))
    a1 = link.recv(256)
    time.sleep(1.0)
    s.sendall(wire(link, [P(wp.T_STRIP, 320, jpeg(720, 32, subsampling='4:2:2')), present1, P(wp.T_KBD_LEDS, 0b011)]))
    a2 = link.recv(256)
    link.send(wp.T_BYE)
    s.close()
    res = result()
    ok = (res is not None and res.violation is None and res.ended_by == 'bye' and res.frames == 2 and res.leds == [3]
          and res.audio_msgs == 2 and res.audio_bytes == 4 + wp.AUDIO_MAX
          and a1[0] == wp.T_ACK and wp.ACK.unpack(a1[3])[0] == 0 and a2[0] == wp.T_ACK and wp.ACK.unpack(a2[3])[0] == 1)
    check('放行：守协议的两帧（中间停 1 秒，第一帧之前和中间各夹一个 AUDIO）+ KBD_LEDS + BYE', ok, '' if ok else f'{res.violation if res else None}')


# ── 密码学：RFC 9382 的测试向量 + 本协议自己的向量（../testvectors/v2.json，C# 和固件对的是同一份）──────────
def x25519_pub(priv):
    return X25519PrivateKey.from_private_bytes(priv).public_key().public_bytes_raw()


def make_vectors(rfc):
    """照现在的 wdcrypto / wdproto 把 v2.json 的内容算一遍。rfc = RFC 9382 附录 B 的四组向量（抄来的，原样带进去）。"""
    h = bytes.hex
    v = {'说明': '无线显示协议 v2 的测试向量：spake2_rfc9382 抄自 RFC 9382 附录 B；其余由 windisplay/tools/wd_selftest.py --write-vectors '
                 '照 PROTOCOL.md 的算式生成。十六进制串；报文是线上的原始字节（8 字节头 + 载荷）。',
         'spake2_rfc9382': rfc}
    v['password'] = [{'password': pw, 'w0': h(wc.password_to_w0(pw)), 'w': '%064x' % wc.w_from_w0(wc.password_to_w0(pw))}
                     for pw in ('1234', 'correct horse battery staple', ' ~')]

    id_pc, id_tab5 = bytes(range(16)), bytes(range(16, 32))
    w, x, y = wc.w_from_w0(wc.password_to_w0('1234')), int('11' * 32, 16), int('22' * 32, 16)
    hello = wp.pack(wp.T_HELLO, wp.PROTO_VER, wp.HELLO.pack(wp.FB_W, wp.FB_H, wp.VIEW_W, wp.VIEW_H, 256 * 1024, 1024 * 1024, 0,
                                                             id_tab5, wp.pack_name('Tab5-TEST')))
    a, b = wc.Spake2('A', w, x), wc.Spake2('B', w, y)
    pair = wp.pack(wp.T_PAIR, 0, wp.PAIR.pack(id_pc, wp.pack_name('电脑-TEST'), a.pub))
    aad = hashlib.sha256(hello + pair).digest()
    d = a.finish_detail(b.pub, id_pc, id_tab5, aad)
    assert d == b.finish_detail(a.pub, id_pc, id_tab5, aad)
    ke, c_a, c_b = d['Ke'], d['cA'], d['cB']
    key = wc.pairing_key(ke)
    v['pairing'] = {'password': '1234', 'id_pc': h(id_pc), 'id_tab5': h(id_tab5), 'w': '%064x' % w, 'x': '%064x' % x, 'y': '%064x' % y,
                    'hello': h(hello), 'pair': h(pair), 'aad': h(aad), 'pA': h(a.pub), 'pB': h(b.pub),
                    'pair_reply': h(wp.pack(wp.T_PAIR_REPLY, 0, wp.PAIR_REPLY.pack(b.pub, c_b))),
                    'pair_ok': h(wp.pack(wp.T_PAIR_OK, 0, c_a)),
                    'K': h(d['K']), 'TT': h(d['TT']), 'Ke': h(ke), 'Ka': h(d['Ka']), 'KcA': h(d['KcA']), 'KcB': h(d['KcB']),
                    'cA': h(c_a), 'cB': h(c_b), 'pairing_key': h(key), 'key_id': h(wc.key_id(key))}

    priv_pc, priv_tab5 = bytes(range(32, 64)), bytes(range(64, 96))
    auth = wp.pack(wp.T_AUTH, 0, wp.AUTH.pack(0, id_pc, wc.key_id(key), x25519_pub(priv_pc), wp.pack_name('电脑-TEST')))
    auth_ok = wp.pack(wp.T_AUTH_OK, 0, x25519_pub(priv_tab5))
    dh = wc.x25519_shared(X25519PrivateKey.from_private_bytes(priv_pc), x25519_pub(priv_tab5))
    assert dh == wc.x25519_shared(X25519PrivateKey.from_private_bytes(priv_tab5), x25519_pub(priv_pc))
    th = hashlib.sha256(hello + auth + auth_ok).digest()
    k_p2t, k_t2p = wc.session_keys(key, dh, th)
    v['session'] = {'pairing_key': h(key), 'priv_pc': h(priv_pc), 'priv_tab5': h(priv_tab5), 'hello': h(hello), 'auth': h(auth),
                    'auth_ok': h(auth_ok), 'dh': h(dh), 'th': h(th), 'key_pc_to_tab5': h(k_p2t), 'key_tab5_to_pc': h(k_t2p)}

    def record(what, key, count, mtype, arg, payload):
        c = wc.Cipher(key)
        c.count = count
        hdr = wp.HDR.pack(mtype, 0, arg, len(payload))
        return {'what': what, 'key': h(key), 'count': count, 'header': h(hdr), 'payload': h(payload), 'wire': h(hdr + c.seal(hdr, payload))}

    v['records'] = [
        record('Tab5 的 READY（这个方向的第 0 个报文，没有载荷）', k_t2p, 0, wp.T_READY, 0, b''),
        record('电脑的 READY', k_p2t, 0, wp.T_READY, 0, b''),
        record('PRESENT', k_p2t, 9, wp.T_PRESENT, 0, wp.PRESENT.pack(7, 123456)),
        record('STRIP（起始行 160，载荷 300 字节）', k_p2t, 1, wp.T_STRIP, 160, bytes(i * 7 & 0xFF for i in range(300))),
        record('KEY，报文计数超过 32 位', k_t2p, (1 << 32) + 5, wp.T_KEY, 0, bytes([2, 0, 4, 0, 0, 0, 0, 0])),
    ]

    # 该拒的输入。实现要把每一项都拒掉（握手里遇到就断开）
    good = a.pub
    # 曲线上 X 坐标最小的一个点：X 很小，加上 p 之后还放得进 32 字节——对 p 取模的实现会把它当成合格的点收下
    small_x = next(x for x in range(1, 100) if wc._on_curve((x, pow((x * x * x - 3 * x + wc._B) % wc._P, (wc._P + 1) // 4, wc._P))))
    small_y = pow((small_x ** 3 - 3 * small_x + wc._B) % wc._P, (wc._P + 1) // 4, wc._P)
    v['reject'] = {
        'points': [     # 配对里对方给的点（pA / pB）：要 65 字节、04 开头、坐标小于 p、在曲线上
            {'what': '坐标全 0', 'point': h(b'\x04' + bytes(64))},
            {'what': '少一个字节', 'point': h(good[:-1])},
            {'what': '多一个字节', 'point': h(good + b'\0')},
            {'what': '最后一位改了（不在曲线上）', 'point': h(good[:-1] + bytes([good[-1] ^ 1]))},
            {'what': '压缩格式（02 开头，33 字节）', 'point': h(b'\x02' + good[1:33])},
            {'what': '02 开头、长度却是 65', 'point': h(b'\x02' + good[1:])},
            {'what': '混合格式（06 开头，坐标是对的）', 'point': h(b'\x06' + good[1:])},
            {'what': '无穷远点的编码（一个 00）', 'point': '00'},
            {'what': '00 开头、长度是 65', 'point': h(bytes(65))},
            {'what': f'X 坐标是 p + {small_x}（对 p 取模之后在曲线上，但坐标超出范围）',
             'point': '04%064x%064x' % (small_x + wc._P, small_y)},
        ],
        # 配对里 K 是无穷远点：对方给的点正好是 w·N（电脑这边，等于对方的随机数是 0）或者 w·M（Tab5 这边）
        'k_infinity': {'w': '%064x' % w, 'pB_for_pc': h(wc.point_to_bytes(wc._mul(w, wc._NPT))),
                       'pA_for_tab5': h(wc.point_to_bytes(wc._mul(w, wc._M)))},
        'x25519_pub': [  # 一次性公钥是低阶点：共享值全 0
            {'what': '全 0', 'pub': '00' * 32},
            {'what': '1', 'pub': '01' + '00' * 31},
            {'what': '8 阶点', 'pub': 'e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800'},
            {'what': '8 阶点（另一个）', 'pub': '5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157'},
            {'what': 'p − 1', 'pub': 'ec' + 'ff' * 30 + '7f'},
            {'what': 'p', 'pub': 'ed' + 'ff' * 30 + '7f'},
            {'what': 'p + 1', 'pub': 'ee' + 'ff' * 30 + '7f'},
            # 下面两个最高位是 1：X25519 算之前会把这一位清掉，所以还是上面的「全 0」和「p」。要按共享值全 0 来拒，不能拿公钥对一张表
            {'what': '全 0，只有最高位是 1', 'pub': '00' * 31 + '80'},
            {'what': 'p，最高位置 1', 'pub': 'ed' + 'ff' * 31},
        ],
    }
    return v


def test_crypto():
    with open(VECTORS, encoding='utf-8') as f:
        vec = json.load(f)
    bad = []
    for t in vec['spake2_rfc9382']:
        w, x, y = (int(t[k], 16) for k in ('w', 'x', 'y'))
        a, b = wc.Spake2('A', w, x), wc.Spake2('B', w, y)
        ra = a.finish_detail(b.pub, t['A'].encode(), t['B'].encode())
        rb = b.finish_detail(a.pub, t['A'].encode(), t['B'].encode())
        wrong = [k for k in ('K', 'TT', 'Ke', 'Ka', 'KcA', 'KcB', 'cA', 'cB') if ra[k].hex() != t[k] or rb[k].hex() != t[k]]
        wrong += [k for k, pub in (('pA', a.pub), ('pB', b.pub)) if pub.hex() != t[k]]
        if wrong:
            bad.append(f"A='{t['A']}' B='{t['B']}'：{'、'.join(wrong)}")
    check('密码学：SPAKE2 对得上 RFC 9382 附录 B 的四组向量（含 K、TT、Ka、KcA、KcB）', len(vec['spake2_rfc9382']) == 4 and not bad, '；'.join(bad))
    now = make_vectors(vec['spake2_rfc9382'])
    diff = [k for k in now if now[k] != vec.get(k)] + [k for k in vec if k not in now]
    check('密码学：算式和 ../testvectors/v2.json 一致（C# 和固件对的是同一份）', not diff, '对不上的部分：' + '、'.join(diff))

    # 该拒的要拒
    def rejects(fn):
        try:
            fn()
        except wc.CryptoError:
            return True
        return False

    rej = vec['reject']
    kept = [t['what'] for t in rej['points'] if not rejects(lambda t=t: wc.point_from_bytes(bytes.fromhex(t['point'])))]
    check(f'密码学：不在曲线上、编码不对、坐标超出范围的点被拒（向量里的 {len(rej["points"])} 种）', len(rej['points']) >= 10 and not kept,
          '没拒掉：' + '、'.join(kept))
    # K 是无穷远点：对方给的点正好是 w·N / w·M（等于它那边的随机数是 0）
    w = int(rej['k_infinity']['w'], 16)
    check('密码学：K 是无穷远点时拒掉（两个角色）',
          rejects(lambda: wc.Spake2('A', w).finish(bytes.fromhex(rej['k_infinity']['pB_for_pc']), b'a', b'b'))
          and rejects(lambda: wc.Spake2('B', w).finish(bytes.fromhex(rej['k_infinity']['pA_for_tab5']), b'a', b'b')))
    priv, _ = wc.x25519_keypair()
    kept = [t['what'] for t in rej['x25519_pub'] if not rejects(lambda t=t: wc.x25519_shared(priv, bytes.fromhex(t['pub'])))]
    check(f'密码学：X25519 的共享值全 0（对方给了低阶点，向量里的 {len(rej["x25519_pub"])} 种）时拒掉', len(rej['x25519_pub']) >= 9 and not kept,
          '没拒掉：' + '、'.join(kept))
    key, hdr = os.urandom(32), wp.HDR.pack(wp.T_STRIP, 0, 0, 5)
    sealed = wc.Cipher(key).seal(hdr, b'hello')
    rx = wc.Cipher(key)
    ok = rx.open(hdr, sealed) == b'hello'
    other = wc.Cipher(key)
    other.count = 1
    check('密码学：报文能解开；改了头、改了内容、计数不对、密钥不对都认证不过',
          ok and rejects(lambda: wc.Cipher(key).open(wp.HDR.pack(wp.T_STRIP, 0, 16, 5), sealed))
          and rejects(lambda: wc.Cipher(key).open(hdr, bytes([sealed[0] ^ 1]) + sealed[1:]))
          and rejects(lambda: other.open(hdr, sealed)) and rejects(lambda: wc.Cipher(os.urandom(32)).open(hdr, sealed))
          and rejects(lambda: wc.Cipher(key).open(hdr, sealed[:-1])))
    # 两边输的密码不一样：配对的校验值对不上
    a, b = wc.Spake2('A', w), wc.Spake2('B', wc.w_from_w0(wc.password_to_w0('1235')))
    ra, rb = a.finish(b.pub, b'a', b'b'), b.finish(a.pub, b'a', b'b')
    check('密码学：两边密码不一样时校验值对不上', ra[1] != rb[1] and ra[2] != rb[2] and ra[0] != rb[0])
    names = [('Tab5-46C3', 'Tab5-46C3'), ('书房的电脑', '书房的电脑'), ('一二三四五六七八九十一二', '一二三四五六七八九十'), ('a\x01b\tc', 'a?b?c'),
             ('del\x7f c1\x85', 'del? c1?'), ('零宽\u200b方向\u202e分段\u2029', '零宽?方向?分段?'), ('\ufeff头', '?头'),
             ('全角\u3000空格 不换', '全角\u3000空格 不换'), ('笔记本 💻', '笔记本 💻'), ('a\u00a0b', 'a\u00a0b')]
    check('名字：打包成 32 字节（在字符边界上截断）；控制字符、零宽和方向控制字符、不合法的字节换成 ?，别的原样',
          all(wp.unpack_name(wp.pack_name(n)) == want and len(wp.pack_name(n)) == 32 for n, want in names)
          and wp.unpack_name(b'ab\xff\xfe\0zz' + bytes(25)) == 'ab??')
    check('密码：空的、带中文或控制字符的、超过 64 个字符的不收；空格到 ~、1~64 个字符都可以',
          all(wp.password_problem(pw) for pw in ('', '密码', 'a\tb', 'caf\u00e9', 'a\x7f', 'x' * 65))
          and not any(wp.password_problem(pw) for pw in ('1', ' ~', 'correct horse battery staple', '!"#$%&\'()*+,-./:;<=>?@[\\]^_`{|}', 'x' * 64)))


# ── 握手的各条路 ─────────────────────────────────────────────────────────
def same_tab5(store, peers=()):
    """同一台 Tab5（编号一样）换了一份配对存储：peers = 它记得的 [(电脑编号, 密钥)]。"""
    new = wp.PairStore()
    new.id = store.id
    for pc_id, key in peers:
        new.put(pc_id, key, '旧名字')
    return new


def test_handshake():
    spath = os.path.join(TMP, 'pair_sender.json')
    tab5 = wp.PairStore()

    def go(store, passwords=(), *extra, frames='2'):
        port, result = start_mock(mopts(store=store, passwords=passwords))
        rc, out, _ = run_sender(port, '--frames', frames, *extra, store=spath)
        return rc, out, result(), wp.PairStore(spath)

    rc, out, res, pc = go(tab5, ['pw-1234'], '--password', 'pw-1234')
    k1 = pc.get(tab5.id)
    check('握手：第一次连接，配对后照常显示，两边存下同一把密钥', rc == 0 and res is not None and res.paired and res.asked == 1
          and not res.repair and res.frames == 2 and res.peer_name == 'selftest' and k1 is not None and k1 == tab5.get(pc.id),
          f'退出码 {rc}：{out}')
    rc, out, res, pc = go(tab5)
    check('握手：配对过之后不用密码、不问人', rc == 0 and res is not None and not res.paired and res.asked == 0 and res.frames == 2,
          f'退出码 {rc}：{out}')
    rc, out, res, pc = go(tab5, [], '--name', '改了名字')
    check('握手：电脑改了名字，Tab5 那边跟着改', rc == 0 and tab5.peers[pc.id][1] == '改了名字', f'{tab5.peers.get(pc.id)}')

    other = wp.PairStore()
    rc, out, res, pc = go(other, ['a', 'b', 'pw-1234'], '--password', 'pw-1234')
    check('握手：Tab5 上密码输错两次、第三次对', rc == 0 and res is not None and res.paired and res.asked == 3
          and out.count('输的密码不对') == 2 and pc.get(other.id) == other.get(pc.id) is not None, f'退出码 {rc}：{out}')
    third = wp.PairStore()
    rc, out, res, pc = go(third, ['x'] * (wp.PAIR_TRIES + 4), '--password', 'pw-1234')
    check('握手：密码连着不对 10 次，发送端放弃（退出码 3），两边什么都没存', rc == 3 and res is not None and res.asked == 10
          and res.frames == 0 and not third.peers and pc.get(third.id) is None, f'退出码 {rc}，问了 {res.asked if res else None} 次：{out}')
    rc, out, res, pc = go(wp.PairStore(), ['pw-1234'])
    check('握手：没配对过又没给密码，发送端不配对（退出码 3），Tab5 不问人', rc == 3 and res is not None and res.asked == 0
          and '没和这台 Tab5 配对过' in out, f'退出码 {rc}：{out}')
    rc, out, res, pc = go(wp.PairStore(), [], '--password', 'pw-1234')
    check('握手：人在密码框上点了取消，发送端收到 BYE', rc == 1 and res is not None and res.ended_by == 'dev_bye' and '取消' in out
          and 'BYE 之后 Tab5 正常关闭' in out, f'退出码 {rc}，模拟端 {res.ended_by if res else None}：{out}')
    shy = wp.PairStore()
    rc, out, res, pc = go(shy, ['oops'], '--password', 'pw-1234')
    check('握手：密码输错一次之后人点了取消，发送端收到 BYE，两边什么都没存', rc == 1 and res is not None and res.ended_by == 'dev_bye'
          and res.asked == 2 and out.count('输的密码不对') == 1 and not shy.peers and pc.get(shy.id) is None,
          f'退出码 {rc}，问了 {res.asked if res else None} 次：{out}')

    # 人输密码要花时间：发送端这期间只能等（模拟端盯着，它发了东西就算违规）
    slow = wp.PairStore()
    port, result = start_mock(mopts(store=slow, passwords=['pw-1234'], password_delay=0.8))
    t0 = time.monotonic()
    rc, out, _ = run_sender(port, '--frames', '2', '--password', 'pw-1234', store=spath)
    took, res = time.monotonic() - t0, result()
    check('握手：人输密码花了 0.8 秒，发送端老实等着，照常配对', rc == 0 and took >= 0.8 and res is not None and res.paired
          and res.violation is None and res.frames == 2, f'退出码 {rc}，用时 {took:.2f} 秒，模拟端 {res.violation if res else None}：{out}')
    # 密码框开到时间了人还没输完：Tab5 关掉密码框、断开，不发 BYE（发送端看到的是断线，可以再连）
    idle = wp.PairStore()
    port, result = start_mock(mopts(store=idle, passwords=['pw-1234'], password_delay=5, password_box_s=0.5))
    rc, out, _ = run_sender(port, '--password', 'pw-1234', store=spath)
    res = result()
    check('握手：密码框开到时间没人输完，Tab5 断开（不是 BYE），两边什么都没存', rc == 2 and '连接断了' in out and res is not None
          and res.ended_by == 'box_timeout' and not idle.peers and wp.PairStore(spath).get(idle.id) is None,
          f'退出码 {rc}，模拟端 {res.ended_by if res else None}：{out}')
    # 反过来：发送端等 PAIR_REPLY 等过了自己的时限，它放弃；Tab5 发现连接断了，关掉密码框
    port, result = start_mock(mopts(store=wp.PairStore(), passwords=['pw-1234'], password_delay=3))
    s = socket.create_connection(('127.0.0.1', port), timeout=5)
    gave_up = False
    try:
        wp.sender_handshake(s, wp.PairStore(), 'selftest', 'pw-1234', pair_reply_s=0.5)
    except socket.timeout:
        gave_up = True
    finally:
        s.close()
    res = result()
    check('握手：发送端等 PAIR_REPLY 过了时限就放弃，Tab5 关掉密码框', gave_up and res is not None and res.ended_by == 'disconnect'
          and res.asked == 1, f'发送端放弃了 {gave_up}，模拟端 {res.ended_by if res else None}')

    # Tab5 那边没有这台电脑的配对了（被重置 / 移除过）：回 AUTH_UNKNOWN。没给密码就到此为止，电脑不删自己的配对；给了就重新配对
    forgot = same_tab5(tab5)
    rc, out, res, pc = go(forgot, ['pw-1234'])
    check('握手：Tab5 不认识这台电脑了、电脑没设密码：不配对（退出码 3），电脑的配对还在', rc == 3 and res is not None and res.asked == 0
          and 'AUTH_UNKNOWN' in out and pc.get(tab5.id) == k1 and not forgot.peers, f'退出码 {rc}：{out}')
    rc, out, res, pc = go(forgot, ['pw-1234'], '--password', 'pw-1234')
    k2 = pc.get(tab5.id)
    check('握手：Tab5 不认识这台电脑了、电脑设了密码：同一条连接上重新配对并显示', rc == 0 and res is not None and res.paired
          and res.asked == 1 and res.frames == 2 and k2 not in (None, k1) and forgot.get(pc.id) == k2, f'退出码 {rc}：{out}')
    # 两边存的不是同一把（比如上次重新配对的最后一步断了）：靠密钥标识看出来，也是 AUTH_UNKNOWN → 重新配对
    stale = same_tab5(tab5, [(pc.id, os.urandom(32))])
    rc, out, res, pc = go(stale, ['pw-1234'])
    check('握手：两边存的密钥不是同一把、电脑没设密码：退出码 3，都不动', rc == 3 and res is not None and res.asked == 0
          and pc.get(tab5.id) == k2 and stale.get(pc.id) not in (None, k2), f'退出码 {rc}：{out}')
    rc, out, res, pc = go(stale, ['pw-1234'], '--password', 'pw-1234')
    check('握手：两边存的密钥不是同一把、电脑设了密码：重新配对（Tab5 知道这台电脑配对过），两边换成新的', rc == 0 and res is not None
          and res.paired and res.repair and pc.get(tab5.id) not in (None, k2) and pc.get(tab5.id) == stale.get(pc.id), f'退出码 {rc}：{out}')
    k3 = pc.get(tab5.id)

    # 冒充的 Tab5：报的是配对过的那台的编号，回了 AUTH_OK，可它没有配对密钥——READY 的认证码对不上。
    # 发送端要按认证没过处理（退出码 3），之后什么都不发，自己存的配对不动
    srv = socket.create_server(('127.0.0.1', 0))
    after_ready = []

    def impostor():
        conn, _ = srv.accept()
        srv.close()
        conn.settimeout(5)
        conn.sendall(wp.pack(wp.T_HELLO, wp.PROTO_VER, wp.HELLO.pack(wp.FB_W, wp.FB_H, wp.VIEW_W, wp.VIEW_H, 1 << 18, 1 << 20, 0,
                                                                      tab5.id, wp.pack_name('Tab5-mock'))))
        wp.recv_msg(conn, 256)          # AUTH
        ready = wp.HDR.pack(wp.T_READY, 0, 0, 0)
        conn.sendall(wp.pack(wp.T_AUTH_OK, 0, wc.x25519_keypair()[1]) + ready + wc.Cipher(os.urandom(32)).seal(ready, b''))
        try:
            after_ready.append(conn.recv(4096))     # 发送端应该直接关连接：读到的是空
        except OSError as e:
            after_ready.append(e)
        conn.close()

    th = threading.Thread(target=impostor, daemon=True)
    th.start()
    rc, out, _ = run_sender(srv.getsockname()[1], store=spath)
    th.join(8)
    check('握手：冒充的 Tab5（没有配对密钥，READY 认证不过）：退出码 3，之后什么都不发，配对不动', rc == 3 and '认证码对不上' in out
          and after_ready == [b''] and wp.PairStore(spath).get(tab5.id) == k3, f'退出码 {rc}，之后收到 {after_ready}：{out}')

    # Tab5 最多记 4 台电脑，满了挤掉最久没连过的：被挤掉的那台再连，Tab5 回 AUTH_UNKNOWN
    small = wp.PairStore(max_peers=wp.MAX_TAB5_PEERS)
    pcs = [os.path.join(TMP, f'pc{i}.json') for i in range(5)]

    def visit(i, *extra):
        port, result = start_mock(mopts(store=small, passwords=['pw-1234']))
        rc, out, _ = run_sender(port, '--frames', '1', *extra, store=pcs[i])
        return rc, out, result()

    first4 = [visit(i, '--password', 'pw-1234')[0] for i in range(4)]
    again0 = visit(0)[0]                            # 0 号又连了一次：现在最久没连过的是 1 号
    fifth = visit(4, '--password', 'pw-1234')[0]
    rc1, out1, res1 = visit(1)
    rc0 = visit(0)[0]
    check('握手：Tab5 记满 4 台之后再配一台，挤掉最久没连过的那台（它再连得到 AUTH_UNKNOWN），别的不受影响',
          first4 == [0] * 4 and again0 == 0 and fifth == 0 and rc1 == 3 and 'AUTH_UNKNOWN' in out1 and res1.asked == 0 and rc0 == 0
          and len(small.peers) == 4 and small.get(wp.PairStore(pcs[1]).id) is None,
          f'前 4 台 {first4}，0 号再连 {again0}，第 5 台 {fifth}，1 号 {rc1}，0 号 {rc0}，记着 {len(small.peers)} 台')

    # 新配的密钥要等握手走完才存。Tab5 收下 PAIR_OK 就消失：电脑不能留下这把密钥
    srv = socket.create_server(('127.0.0.1', 0))
    ghost = wp.PairStore()

    def vanish_after_pair_ok():
        conn, _ = srv.accept()
        srv.close()
        hello = wp.pack(wp.T_HELLO, wp.PROTO_VER, wp.HELLO.pack(wp.FB_W, wp.FB_H, wp.VIEW_W, wp.VIEW_H, 1 << 18, 1 << 20, 0,
                                                                 ghost.id, wp.pack_name('ghost')))
        conn.sendall(hello)
        mtype, flags, arg, payload = wp.recv_msg(conn, 256)
        pc_id, _name, p_a = wp.PAIR.unpack(payload)
        spake = wc.Spake2('B', wc.w_from_w0(wc.password_to_w0('pw-1234')))
        _ke, _ca, c_b = spake.finish(p_a, pc_id, ghost.id, hashlib.sha256(hello + wp.pack(mtype, arg, payload)).digest())
        conn.sendall(wp.pack(wp.T_PAIR_REPLY, 0, wp.PAIR_REPLY.pack(spake.pub, c_b)))
        wp.recv_msg(conn, 256)          # PAIR_OK
        conn.close()

    th = threading.Thread(target=vanish_after_pair_ok, daemon=True)
    th.start()
    rc, out, _ = run_sender(srv.getsockname()[1], '--password', 'pw-1234', store=spath)
    th.join(5)
    check('握手：配对到最后一步 Tab5 消失了，电脑不留下这把密钥', rc == 2 and '密码对上了' in out and wp.PairStore(spath).get(ghost.id) is None,
          f'退出码 {rc}：{out}')
    # 反过来：电脑发完 PAIR_OK 就消失，Tab5 也不留
    lonely = wp.PairStore()
    port, result = start_mock(mopts(store=lonely, passwords=['pw-1234'], handshake_s=0.5))
    s = socket.create_connection(('127.0.0.1', port), timeout=5)
    mtype, flags, ver, payload = wp.recv_msg(s, 256)
    hello_raw, tab5_id = wp.pack(mtype, ver, payload), wp.Hello(ver, payload).tab5_id
    spake, me = wc.Spake2('A', wc.w_from_w0(wc.password_to_w0('pw-1234'))), os.urandom(16)
    pair_raw = wp.pack(wp.T_PAIR, 0, wp.PAIR.pack(me, wp.pack_name('x'), spake.pub))
    s.sendall(pair_raw)
    p_b, c_b = wp.PAIR_REPLY.unpack(wp.recv_msg(s, 256)[3])
    _ke, c_a, want = spake.finish(p_b, me, tab5_id, hashlib.sha256(hello_raw + pair_raw).digest())
    s.sendall(wp.pack(wp.T_PAIR_OK, 0, c_a))
    s.close()
    res = result()
    check('握手：配对到最后一步电脑消失了，Tab5 不留下这把密钥', c_b == want and res is not None and res.ended_by == 'disconnect'
          and not lonely.peers, f'模拟端 {res.ended_by if res else None}，存了 {len(lonely.peers)} 条')

    # Tab5 这一侧该拒的：PAIR_OK 里的 cA 不对；配对之后的 AUTH 换了电脑编号，或者带的不是刚配出来那把密钥的标识
    def pair_then(last):
        """对着模拟端配对到 PAIR_REPLY 核对通过，然后发 last(me, c_a, 配对密钥) 给的字节。返回 (模拟端的结果, 它的存储)。"""
        strict = wp.PairStore()
        port, result = start_mock(mopts(store=strict, passwords=['pw-1234'], handshake_s=0.5))
        s = socket.create_connection(('127.0.0.1', port), timeout=5)
        try:
            mtype, flags, ver, payload = wp.recv_msg(s, 256)
            hello_raw, tab5_id = wp.pack(mtype, ver, payload), wp.Hello(ver, payload).tab5_id
            spake, me = wc.Spake2('A', wc.w_from_w0(wc.password_to_w0('pw-1234'))), os.urandom(16)
            pair_raw = wp.pack(wp.T_PAIR, 0, wp.PAIR.pack(me, wp.pack_name('x'), spake.pub))
            s.sendall(pair_raw)
            p_b, _c_b = wp.PAIR_REPLY.unpack(wp.recv_msg(s, 256)[3])
            ke, c_a, _want = spake.finish(p_b, me, tab5_id, hashlib.sha256(hello_raw + pair_raw).digest())
            s.sendall(last(me, c_a, wc.pairing_key(ke)))
            s.settimeout(3)
            while s.recv(4096):
                pass
        except OSError:
            pass
        finally:
            s.close()
        return result(), strict

    pub = wc.x25519_keypair()[1]
    auth = lambda pc_id, key: wp.pack(wp.T_AUTH, 0, wp.AUTH.pack(0, pc_id, wc.key_id(key), pub, wp.pack_name('x')))
    ok = lambda c_a: wp.pack(wp.T_PAIR_OK, 0, c_a)
    for name, last, why in (
            ('PAIR_OK 里的 cA 不对', lambda me, c_a, key: ok(bytes([c_a[0] ^ 1]) + c_a[1:]), 'cA 不对'),
            ('配对之后的 AUTH 换了电脑编号', lambda me, c_a, key: ok(c_a) + auth(os.urandom(16), key), '不是配对时的电脑编号'),
            ('配对之后的 AUTH 带的不是刚配出来那把密钥的标识', lambda me, c_a, key: ok(c_a) + auth(me, os.urandom(32)), '不是配对时的电脑编号')):
        res, strict = pair_then(last)
        check(f'握手：{name}，Tab5 断开、不留下密钥', res is not None and res.ended_by == 'violation' and why in (res.violation or '')
              and not strict.peers, f'模拟端 {res.ended_by if res else None}：{res.violation if res else None}，存了 {len(strict.peers)} 条')


def test_bad_sender_cases():
    """违规发送端（wd_bad_sender.py）的每一项，对着模拟接收端都应该被断开；握手和认证码那两组还要核对断开的原因。"""
    why = {
        '握手：连上之后什么都不发': ['没有等到 AUTH 或 PAIR'],
        '握手：第一个报文是明文的条带（载荷超过这一步的上限）': ['超过上限 0'],
        '握手：第一个报文是 PAIR_OK（长度合法，但这一步不该来）': ['这一步该来 AUTH 或 PAIR'],
        '握手：AUTH 的长度不对': ['这一步该来 AUTH 或 PAIR'],
        '握手：AUTH 的 arg 不是 0': ['这一步该来 AUTH 或 PAIR'],
        '握手：AUTH 的 flags 不是 0': ['这一步该来 AUTH 或 PAIR'],
        '握手：AUTH 只发一半': ['没有新数据', '没有等到 AUTH 或 PAIR'],
        '握手：AUTH 里的 X25519 公钥全 0': ['AUTH：X25519'],
        '握手：AUTH 之后不发 READY（先要收到 AUTH_OK 和 Tab5 的 READY）': ['没有等到 READY'],
        '握手：READY 的认证码不对（用的不是会话密钥）': ['认证码对不上'],
        '握手：收到 AUTH_OK 之后还发明文的 READY': ['认证码对不上'],
        '握手：加密后的第一个报文是条带（载荷超过这一步的上限）': ['超过上限 0'],
        '握手：加密后的第一个报文是 KBD_LEDS（长度合法，但不是 READY）': ['不是 READY'],
        '握手：没配对过的编号发 AUTH，收到 AUTH_UNKNOWN 之后不发 PAIR': ['没有等到 PAIR'],
        '握手：没配对过的编号发 AUTH，收到 AUTH_UNKNOWN 之后再发一个 AUTH': ['这一步该来 PAIR'],
        '握手：编号配对过、密钥标识不对，收到 AUTH_UNKNOWN 之后再发 AUTH': ['这一步该来 PAIR'],
        '握手：PAIR 的长度不对': ['超过上限 113'],
        '握手：PAIR 里的点不在曲线上': ['PAIR：点不在曲线上'],
        '握手：PAIR 里的点不是 04 开头': ['PAIR：点的编码不对'],
        bad_sender.BOX_CASE: ['密码框开着的时候电脑发来了东西'],
        '认证码改了一个比特': ['认证码对不上'],
        '密文改了一个比特': ['认证码对不上'],
        '几十 KB 的条带，密文中间改了一个比特': ['认证码对不上'],
        '几百字节的条带，认证码改了一个比特': ['认证码对不上'],
        '报文头改了（起始行 0 改成 16）而认证码没变': ['认证码对不上'],
        '同一个报文发两遍（重放一个内容上合法的 KBD_LEDS）': ['认证码对不上'],
        '两个报文换了先后': ['认证码对不上'],
        '握手之后发明文的一帧（没有认证码）': ['认证码对不上'],
        '握手之后发明文的 BYE（没有认证码，接收端等不到那 16 字节）': ['没有新数据', '没有发完第一帧'],
    }
    groups = {'握手里的违规': [], '认证码对不上的': [], '内容不合规定的': []}
    counts = dict.fromkeys(groups, 0)
    asked = box_asked = 0
    for name, run, limit in bad_sender.cases():
        if limit < 0:       # 「不应断开」的那一项不在这里测
            continue
        group = '握手里的违规' if name.startswith('握手：') else '认证码对不上的' if name in why else '内容不合规定的'
        counts[group] += 1
        port, result = start_mock(mopts(stall_s=0.4, handshake_s=0.4, first_frame_s=0.4, passwords=['pw'] * 3))
        s = socket.create_connection(('127.0.0.1', port), timeout=5)
        missed = None           # 没走到要测的地方（守规矩的那一段就出了岔子）
        try:
            run(s, SENDER_STORE)
            s.settimeout(3)
            while s.recv(4096):
                pass
        except wp.ProtoError as e:
            missed = str(e)
        except OSError:
            pass
        finally:
            s.close()
        res = result()
        if name == bad_sender.BOX_CASE:
            box_asked += res.asked if res else 0
        else:
            asked += res.asked if res else 0
        keys = why.get(name)
        if missed or not (res is not None and res.ended_by == 'violation' and (keys is None or any(k in res.violation for k in keys))):
            groups[group].append(f'{name} → {res.ended_by if res else None}：{res.violation if res else None}'
                                 + (f'（没走到：{missed}）' if missed else ''))
    for group, bad in groups.items():
        check(f'违规发送端：{group} {counts[group]} 项，模拟接收端都拦下了', counts[group] > 0 and not bad, '；'.join(bad[:4]))
    check('违规发送端：握手里的违规只有「密码框开着又发 PAIR」那一项让接收端问了密码（一次）', asked == 0 and box_asked == 1,
          f'别的项问了 {asked} 次，那一项问了 {box_asked} 次')
    missing = [k for k in why if k not in {n for n, _, _ in bad_sender.cases()}]
    check('违规发送端：上面核对原因的名单和用例对得上', not missing, '、'.join(missing))


def test_bad_sender_judges():
    """wd_bad_sender.py 对着真机跑时只看得到线上的东西，看不到断开的原因。它自己的判定要够严：
    接收端没回该回的报文就断开、守规矩的那一段就被断开、该等满 5 秒却早早断开，都不能算「拦下了」。"""
    todo = {name: (run, limit) for name, run, limit in bad_sender.cases()}
    hello = wp.pack(wp.T_HELLO, wp.PROTO_VER, wp.HELLO.pack(wp.FB_W, wp.FB_H, wp.VIEW_W, wp.VIEW_H, 1 << 18, 1 << 20, 0,
                                                             MOCK_STORE.id, wp.pack_name('buggy')))

    def judge(name, receiver):
        """起一个 receiver(conn) 那样的接收端，跑 name 这一项，返回 (判定, 打印的那一行)。"""
        srv = socket.create_server(('127.0.0.1', 0))

        def body():
            conn, _ = srv.accept()
            srv.close()
            conn.settimeout(10)
            try:
                receiver(conn)
            except (OSError, wp.ProtoError):
                pass
            finally:
                conn.close()

        th = threading.Thread(target=body, daemon=True)
        th.start()
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            ok = bad_sender.run_case('127.0.0.1', srv.getsockname()[1], SENDER_STORE, name, *todo[name])
        th.join(12)
        return ok, out.getvalue().strip()

    def drops_unknown_auth(conn):       # 有毛病的接收端：收到不认识的 AUTH 直接断开，不回 AUTH_UNKNOWN
        conn.sendall(hello)
        wp.recv_msg(conn, 256)

    def hasty(conn):                    # 回了 AUTH_UNKNOWN，但不等对方的 PAIR，马上断开
        conn.sendall(hello)
        wp.recv_msg(conn, 256)
        conn.sendall(wp.pack(wp.T_AUTH_UNKNOWN))

    unknown = '握手：没配对过的编号发 AUTH，收到 AUTH_UNKNOWN 之后不发 PAIR'
    ok, line = judge(unknown, drops_unknown_auth)
    check('违规发送端的判定：接收端没回 AUTH_UNKNOWN 就断开，判不符', not ok and '该回 AUTH_UNKNOWN' in line, line)
    ok, line = judge(unknown, hasty)
    check('违规发送端的判定：该等满 5 秒的一项早早就断开了，判不符', not ok and '应该等满 5 秒' in line, line)
    ok, line = judge('握手：READY 的认证码不对（用的不是会话密钥）', drops_unknown_auth)
    check('违规发送端的判定：合法的 AUTH 就被断开（没走到要测的地方），判不符', not ok and '没走到要测的地方' in line, line)
    ok, line = judge('认证码改了一个比特', drops_unknown_auth)
    check('违规发送端的判定：守规矩的握手走不通，判不符', not ok and '没走到要测的地方' in line, line)
    # 对照：守规矩的接收端（真机的时限，5 秒）上这一项是符合的
    t0 = time.monotonic()
    ok, line = judge(unknown, lambda conn: mock.serve_conn(conn, mopts()))
    check('违规发送端的判定：守规矩的接收端等满 5 秒才断，判符合', ok and time.monotonic() - t0 >= 4.5, line)


def test_discovery():
    tab5_id = os.urandom(16)
    query = wp.DISC.pack(wp.DISC_MAGIC, wp.PROTO_VER, wp.DISC_QUERY, 0, bytes(16), b'12345678')
    reply = wp.discovery_reply(query, tab5_id, 7250)
    check('自动发现：对询问的应答带着自己的编号、端口，随机数原样带回',
          reply is not None and wp.DISC.unpack(reply) == (wp.DISC_MAGIC, wp.PROTO_VER, wp.DISC_REPLY, 7250, tab5_id, b'12345678'))
    junk = [query[:-1], query + b'\0', b'XXXX' + query[4:], query[:4] + b'\x01' + query[5:], reply, b'']
    check('自动发现：长度、标记、版本不对的包，和应答包，都不应答', all(wp.discovery_reply(j, tab5_id, 7250) is None for j in junk))
    # 本机回环上走一遍：等连接的时候应答，不等的时候不应答
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]
    probe.close()
    waiting = threading.Event()
    threading.Thread(target=mock.answer_discovery, args=('127.0.0.1', port, tab5_id, waiting), daemon=True).start()
    time.sleep(0.2)
    quiet = wp.discover(wait=0.3, rounds=1, port=port, broadcast='127.0.0.1')
    waiting.set()
    found = wp.discover(wait=0.5, rounds=2, port=port, broadcast='127.0.0.1')
    check('自动发现：模拟接收端只在等连接的时候应答', quiet == [] and found == [('127.0.0.1', port, tab5_id)], f'{quiet} / {found}')
    # 一秒最多应答 10 个；询问方早早关了套接字、乱七八糟的包，都不能让应答从此停掉。
    # 限速是按整秒数的，上面那次询问的应答也算在它那一秒里：等过了那一秒再发，数出来的才是这 40 个里应答了几个
    time.sleep(1.05)
    flood = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    flood.settimeout(0.3)
    for i in range(40):
        flood.sendto(wp.DISC.pack(wp.DISC_MAGIC, wp.PROTO_VER, wp.DISC_QUERY, 0, bytes(16), i.to_bytes(8, 'little')), ('127.0.0.1', port))
    got = 0
    try:
        while True:
            flood.recvfrom(256)
            got += 1
    except socket.timeout:
        pass
    flood.sendto(b'junk', ('127.0.0.1', port))
    flood.close()
    time.sleep(1.1)
    still = wp.discover(wait=0.5, rounds=2, port=port, broadcast='127.0.0.1')
    check('自动发现：连着来 40 个询问，一秒最多应答 10 个；之后照常应答', 10 <= got <= 20 and still == [('127.0.0.1', port, tab5_id)],
          f'应答了 {got} 个，之后 {still}')
    # 连接一结束（这里是接收端第 3 帧之后发了 BYE），之后到的询问就要应答，哪怕发送端还没关连接、接收端还在收尾：
    # 真的发送端收到 BYE 就关连接、马上再问，两样几乎同时到，询问先到的话不能被当成「连接期间到的」丢掉
    srv = socket.create_server(('127.0.0.1', 0))
    port = srv.getsockname()[1]
    waiting = threading.Event()
    waiting.set()
    threading.Thread(target=mock.answer_discovery, args=('127.0.0.1', port, MOCK_STORE.id, waiting), daemon=True).start()
    during = []

    def serve_one():
        conn, _ = srv.accept()
        srv.close()
        waiting.clear()
        conn.settimeout(10)
        during.extend(wp.discover(wait=0.2, rounds=1, port=port, broadcast='127.0.0.1'))    # 连接期间问的：不应答
        mock.serve_conn(conn, mopts(bye_after=3, over=waiting.set), log=lambda *_: None)

    th = threading.Thread(target=serve_one, daemon=True)
    th.start()
    rc, out, _ = run_sender(port, '--frames', '10', '--bye-lag', '0.05', '--bye-query')
    th.join(15)
    check('自动发现：连接期间到的询问不应答；接收端发了 BYE 之后、发送端关连接之前到的那一个要应答',
          rc == 0 and during == [] and '关连接之前问的那一次：Tab5 应答了' in out,
          f'退出码 {rc}，连接期间应答 {len(during)} 个；' + next((x for x in out.splitlines() if x.startswith('关连接之前')), '发送端没有问'))
    # 发送端自己发 BYE 结束的也一样：接收端一读到 BYE 就算回到等连接，收尾（这里是把最后一帧存成 PNG，几十毫秒）期间到的询问要应答。
    # 真的发送端发完 BYE 等对方关连接最多等 300 毫秒，到时间就自己关、马上再问
    srv = socket.create_server(('127.0.0.1', 0))
    port = srv.getsockname()[1]
    waiting = threading.Event()
    waiting.set()
    threading.Thread(target=mock.answer_discovery, args=('127.0.0.1', port, MOCK_STORE.id, waiting), daemon=True).start()
    ended = []

    def serve_own_bye():
        conn, _ = srv.accept()
        srv.close()
        waiting.clear()
        conn.settimeout(10)
        # 收尾拖 0.2 秒：询问是 BYE 之后 15 毫秒到的，一定落在收尾中间（不拖的话收尾十来毫秒就完了，over 调晚了也照样应答）
        ended.append(mock.serve_conn(conn, mopts(over=waiting.set, tail_s=0.2), log=lambda *_: None).ended_by)

    th = threading.Thread(target=serve_own_bye, daemon=True)
    th.start()
    rc, out, _ = run_sender(port, '--frames', '10', '--own-bye-query', '0.015')
    th.join(15)
    check('自动发现：发送端自己发 BYE 结束的，接收端收尾期间到的那一个询问要应答',
          rc == 0 and ended == ['bye'] and '自己发 BYE 之后问的那一次：Tab5 应答了' in out,
          f'退出码 {rc}，接收端记的结束方式 {ended}；' + next((x for x in out.splitlines() if x.startswith('自己发 BYE')), '发送端没有问'))
    # 按应答去连的：HELLO 里报的编号得是应答里的那个。不是就断开，连 AUTH 都不发——哪怕 HELLO 里那个编号配对过、认证过得了
    # （连不连它、它是哪一台，是按应答里的编号定的）。编号对得上的照常握手
    outcome = []
    for announced in (os.urandom(16), MOCK_STORE.id):
        port, result = start_mock(mopts())
        s = socket.create_connection(('127.0.0.1', port), timeout=5)
        try:
            link, _ = wp.sender_handshake(s, SENDER_STORE, 'selftest', expect_id=announced)
            link.send(wp.T_BYE)
            said = 'ok'
        except wp.ProtoError as e:
            said = str(e)
        finally:
            s.close()
        res = result()
        outcome.append((said, res.ended_by if res else None, res.peer_name if res else None))
    check('自动发现：HELLO 里的编号不是应答里的那个，发送端不认证就断开；是那个就照常握手',
          '编号' in outcome[0][0] and outcome[0][1:] == ('disconnect', None) and outcome[1] == ('ok', 'bye', 'selftest'), f'{outcome}')
    # 应答的几台里先连谁：配对过的优先；一个配对过的都没有、又给了密码，才连没配对过的
    store = wp.PairStore()
    old_a, old_b, new_c = os.urandom(16), os.urandom(16), os.urandom(16)
    store.put(old_a, os.urandom(32), '客厅的 Tab5')
    store.put(old_b, os.urandom(32), '书房的 Tab5')
    answers = [('192.168.1.50', 7250, old_a), ('192.168.1.40', 7250, new_c), ('192.168.1.51', 7250, old_b)]
    no_pw, with_pw = sender.pick_targets(answers, store, False), sender.pick_targets(answers, store, True)
    only_new = answers[1:2]
    check('自动发现：配对过的优先，给没给密码都一样；只有没配对过的应答时，给了密码才连它',
          [f[2] for f in no_pw] == [old_a, old_b] and [f[2] for f in with_pw] == [old_a, old_b]
          and sender.pick_targets(only_new, store, False) == [] and sender.pick_targets(only_new, store, True) == only_new,
          f'{no_pw} / {with_pw}')
    # 询问包里不带电脑自己的编号（Tab5 用不着，电脑也不必把固定的编号广播出去）
    ear = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ear.bind(('127.0.0.1', 0))
    ear.settimeout(2)
    threading.Thread(target=wp.discover, kwargs=dict(wait=0.2, rounds=1, port=ear.getsockname()[1], broadcast='127.0.0.1'), daemon=True).start()
    heard = wp.DISC.unpack(ear.recvfrom(256)[0])
    ear.close()
    check('自动发现：询问包的编号一栏是全 0', heard[:4] == (wp.DISC_MAGIC, wp.PROTO_VER, wp.DISC_QUERY, 0) and heard[4] == bytes(16), f'{heard}')


if __name__ == '__main__':
    if sys.argv[1:] == ['--write-vectors']:
        with open(VECTORS, encoding='utf-8') as f:
            rfc = json.load(f)['spake2_rfc9382']
        with open(VECTORS, 'w', encoding='utf-8') as f:
            json.dump(make_vectors(rfc), f, ensure_ascii=False, indent=1)
            f.write('\n')
        print(f'写好了 {os.path.normpath(VECTORS)}')
        sys.exit(0)
    test_geometry()
    test_jpeg_size()
    test_crypto()
    test_handshake()
    test_roundtrip()
    test_drain_peer()
    test_touch_echo()
    test_ctrl_c()
    test_violations()
    test_bad_sender_cases()
    test_bad_sender_judges()
    test_discovery()
    print(f'\n{"全部通过" if not failed else "失败 " + str(len(failed)) + " 项：" + "、".join(failed)}')
    sys.exit(1 if failed else 0)
