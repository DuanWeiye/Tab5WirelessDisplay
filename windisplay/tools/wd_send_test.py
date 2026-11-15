#!/usr/bin/env python3
"""Tab5 无线显示的测试发送端（协议见 ../PROTOCOL.md）。

生成测试画面 → 逆时针转 90° → 按条带压成基线 JPEG（只发变了的条带）→ 发 STRIP + PRESENT → 等 ACK，
最后打印帧率、码率、编码耗时、往返耗时，以及 Tab5 在 ACK 里报的接收 / 解码 / 上屏耗时。
流量控制照 PROTOCOL.md：平时保持 --inflight 帧在途（默认 2：在途的不到 2 帧就发下一帧，不等 ACK）；到了这个数就等 ACK，
最早在途的那一帧迟迟等不到 ACK 就接着发，最多 --max-inflight 帧在途（默认 4）。「迟迟」= 从那一帧发出算起超过 --ack-wait 毫秒（默认 60）
和「最近 16 帧往返时间的中位数 × 2」里大的那个，所以画面重、每帧本来就要一百多毫秒的时候不会白白多压几帧在路上；
开头 4 帧往返时间还没摸准，不算迟到。用处：Tab5 发回的 ACK 报文在空中丢了的时候，它的 TCP 要收到 3 个重复确认才立刻重传，
不然等它的重传超时（lwIP 默认配置 1 秒以上，Tab5 固件调小 TCP 定时器之后 0.23~0.5 秒，画面跟着停住）；
接着发到 4 帧在途，后面 3 帧的 ACK 正好凑够这 3 个重复确认（实测救回四次里的三次，见 PROTOCOL.md「流量控制」）。
--inflight 1 = 平时收到 ACK 才发下一帧（ACK 迟到时照样多发，最多 --max-inflight 帧）；--max-inflight 1 = 死等 ACK
（没给 --inflight 时它不超过 --max-inflight）。结果里「确认迟到、先发下一帧：N 次」数的是真的多发出去的帧（发出那一刻在途的已经不少于 --inflight）。
连上之后先握手：没和这台 Tab5 配对过（或者它不认识这边了）就用 --password 配对，Tab5 上要有人输同一个密码；
配对过的直接认证。配对存在 --pair-store 指的文件里（默认是本目录的 .wdpair_sender.json，不入库）。握手之后的报文都是加密的。
地址写 auto 就在局域网里广播询问：先连配对过的 Tab5；没配对过的只在给了 --password 时才连（PROTOCOL.md「自动发现」）。
退出码：0 = 正常结束（发完，或者 Tab5 那边用户退出了显示模式；触摸回显本来就是按 Ctrl-C 结束的，也算；
画面发完之后在 --hold 期间按 Ctrl-C 也算）；1 = 一帧都没上屏（一个 ACK 都没收到）；2 = 连接中途断了，或者 Tab5 发来的东西不合协议；
3 = 认证没过（没配对过又没给密码、密码连着不对、两边存的配对密钥不一样）；
130 = 画面还没发完就被 Ctrl-C 打断了（触摸回显除外。调用它的脚本据此停下来，不把被打断的一项当成跑完了）。

画面（--pattern）：
  card    测试卡：四角色块（左上红、右上绿、左下蓝、右下白，各 60×60）、色条、红绿蓝渐变、80 像素网格，
          中间一个帧计数、下方一个横向移动的方块。每帧只有一两个条带在变（局部更新）。
  scroll  测试卡整屏每帧横移 8 像素（整屏都在变，接近滚动 / 看视频）。
  noise   每帧一屏随机噪点（压不动，单帧最大，压链路用）。
  image   --image 指定的图片，缩放到 1280×720；配 --scroll-image 时每帧横移 8 像素，否则只发一帧静态画面。
  spots   浅灰底上两个隔开的小方块交替变色（横屏 x = 880~929 和 1050~1099）：每帧只有这两处在变，中间的条带一直不重发。
          配 --strip-h 80 用来测 Tab5 的调试信息浮层：它横跨这两处所在的条带，中间那一条解码器不重写，浮层要原样留着（不能越叠越暗）。
  patch   测试卡上每帧随机盖 1~3 个色块（位置、大小、颜色都随机，种子固定，块上写着帧号），盖上去的一直留着：
          每帧变的地方都不一样，屏幕上任何一处都是「最后一次盖到它的那一帧」留下的。配 --strip-mix（每帧换一种条带高）
          和 --dump-last 用来查 Tab5 的补解：它的几块帧缓冲轮流上屏，没变的条带要靠前几帧的数据补，补错了就会露出旧画面
          （firmware/tools/dispbench/wd_catchup_check.sh）。
  touch   触摸回显：Tab5 发来的触点画回屏幕上——手指周围一个圈、走过的轨迹、编号和坐标，上方是按下 / 抬起的次数。
          用手指验证触摸方向对不对（圈应该套在手指上）、按住不动会不会断（「按下」不该自己往上涨）、拖动连不连续。
          一直跑到在 Tab5 上做退出手势（左边缘向右滑）、按 Ctrl-C，或者到了 --duration；--frames 对它不起作用。

用法举例：
  wd_send_test.py 192.168.1.50 --password 1234       # 第一次：配对（Tab5 上输 1234），然后测试卡 200 帧
  wd_send_test.py 192.168.1.50                       # 配对过之后：测试卡 200 帧，能多快就多快
  wd_send_test.py auto                               # 不填地址，广播找 Tab5
  wd_send_test.py 192.168.1.50 --pattern scroll --quality 50 --frames 600
  wd_send_test.py 192.168.1.50 --fps 30 --hold 20    # 限 30 帧，发完后保持连接 20 秒，打印触摸 / 键盘事件
  wd_send_test.py 192.168.1.50 --pattern touch       # 触摸回显，在 Tab5 上从左边缘向右滑结束
  wd_send_test.py 192.168.1.50 --first-delay 3 --hold 10    # 连上 3 秒后才发第一帧（测「第一帧上屏之前 Tab5 不发输入事件」）
  wd_send_test.py 192.168.1.50 --pattern scroll --frames 100000 --bye-lag 1.5    # Tab5 的 BYE 晚 1.5 秒才到（测它等不等）
  wd_send_test.py 192.168.1.50 --pattern scroll --frames 100000 --bye-lag 0.05 --bye-query   # 收到 BYE、关连接之前问的那一次有没有应答
  wd_send_test.py 192.168.1.50 --pattern scroll --frames 40 --own-bye-query 0.015            # 自己发 BYE 之后 15 毫秒问的那一次有没有应答
  wd_send_test.py 192.168.1.50 --pattern scroll --frames 2000 --tone 1000                    # 画面之外同时发一个 1 kHz 的正弦波（协议「声音」）

声音（--tone 赫兹）：握手走完起，按墙上的钟每 10 ms 发一段正弦波（48 kHz、双声道、16 位，幅度默认是满幅的一半，--tone-db 改），直到结束。
做法和 C# 发送端一样：画面改成一个条带一个条带地发，每个条带之前先把到点的声音发掉；没在发画面时由声音线程自己发。
连接堵住、到点没发出去的声音超过 200 ms 时，最旧的不发了（数进「扔掉」）：延迟不往上积。Tab5 没说能收声音（HELLO 的能力位）就不发。
"""
import argparse
import collections
import io
import itertools
import os
import select
import socket
import statistics
import sys
import threading
import time

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFont

import wdproto as wp

SUBSAMPLING = {'420': '4:2:0', '422': '4:2:2', '444': '4:4:4'}
RTT_MIN_SAMPLES = 4     # 流量控制：收到这么多个 ACK 之后才开始「ACK 迟到就先发下一帧」，见文件开头
STEADY_INFLIGHT = 2     # 流量控制：平时保持几帧在途（--inflight 的默认值）
STALL_MS = 500          # 相邻两帧发出的间隔超过这么多毫秒算一次卡顿（结果里单独数出来）
BYE_QUERY_S = 0.3       # --bye-query：问了之后收多久的应答（真的发送端每轮也只收这么久）
CJK_FONT = '/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc'


def load_font(size):
    for path in (CJK_FONT, '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf'):
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            continue
    return ImageFont.load_default()


def make_backdrop():
    """底图（横屏 1280×720）：80 像素网格 + 四角色块。四角色块的位置和颜色是固定的，Tab5 侧拿它核对方向和红蓝顺序。"""
    img = Image.new('RGB', (wp.VIEW_W, wp.VIEW_H), (24, 28, 36))
    d = ImageDraw.Draw(img)
    for x in range(0, wp.VIEW_W, 80):
        d.line([(x, 0), (x, wp.VIEW_H - 1)], fill=(52, 58, 70))
    for y in range(0, wp.VIEW_H, 80):
        d.line([(0, y), (wp.VIEW_W - 1, y)], fill=(52, 58, 70))
    font = load_font(28)
    corners = [((0, 0), (255, 0, 0), '左上 TL'), ((wp.VIEW_W - 60, 0), (0, 255, 0), '右上 TR'),
               ((0, wp.VIEW_H - 60), (0, 0, 255), '左下 BL'), ((wp.VIEW_W - 60, wp.VIEW_H - 60), (255, 255, 255), '右下 BR')]
    for (x, y), c, label in corners:
        d.rectangle([x, y, x + 59, y + 59], fill=c)
        tw = d.textlength(label, font=font)
        tx = x + 70 if x == 0 else x - 10 - tw
        d.text((tx, y + 12), label, fill=(233, 237, 245), font=font)
    return img


def make_card():
    """测试卡的静态部分：底图 + 色条 + 红绿蓝渐变 + 标题。"""
    img = make_backdrop()
    d = ImageDraw.Draw(img)
    bars = [(255, 255, 255), (255, 255, 0), (0, 255, 255), (0, 255, 0),
            (255, 0, 255), (255, 0, 0), (0, 0, 255), (0, 0, 0)]
    for i, c in enumerate(bars):
        d.rectangle([160 + i * 120, 100, 160 + (i + 1) * 120 - 1, 219], fill=c)
    ramp = np.linspace(0, 255, 960).astype(np.uint8)
    for i, ch in enumerate(range(3)):          # 红、绿、蓝三条渐变
        band = np.zeros((40, 960, 3), np.uint8)
        band[:, :, ch] = ramp
        img.paste(Image.fromarray(band), (160, 240 + i * 44))
    d.text((160, 400), 'Tab5 无线显示测试卡 1280×720', fill=(233, 237, 245), font=load_font(36))
    return img


class TouchEcho:
    """触摸回显（--pattern touch）：记着 Tab5 发来的触点，画成画面。

    Tab5 每次发的是当前所有触点的快照（编号 + 横屏画面坐标）：快照里新出现的编号 = 按下，不见了的编号 = 抬起。
    一根手指从按下到抬起是「一笔」。按着的笔画：手指周围一个圈（半径比指尖大，不会被手指挡住）、十字、走过的轨迹和每个采样点、
    旁边写编号和坐标。抬起的笔变暗，再留 KEEP_S 秒。"""
    KEEP_S = 3.0
    COLORS = [(45, 212, 191), (250, 204, 21), (248, 113, 113), (96, 165, 250), (192, 132, 252)]     # 按触点编号取色
    RING = 70           # 圈的半径（像素）。屏幕约 294 像素/英寸，指尖宽约 90 像素
    EDGE_ZONE = 32      # Tab5 的退出手势从左边缘这么宽以内起手（固件 wdisp.cpp 的 EDGE_ZONE），这里面起手的触点不会发过来

    def __init__(self):
        self.base = make_backdrop()
        d = ImageDraw.Draw(self.base)
        d.line([(self.EDGE_ZONE, 70), (self.EDGE_ZONE, wp.VIEW_H - 71)], fill=(100, 116, 139))
        d.text((self.EDGE_ZONE + 12, wp.VIEW_H // 2 - 14), '→ 右滑退出', fill=(100, 116, 139), font=load_font(22))
        self.font = load_font(28)
        self.small = load_font(22)
        self.live = {}          # 按着的笔：编号 → [(x, y), ...]
        self.t_down = {}        # 编号 → 按下的时刻
        self.done = []          # 抬起的笔：(抬起的时刻, 编号, 点)
        self.msgs = self.downs = self.ups = 0
        self.longest = 0.0      # 已经抬起的笔里，最长的一笔按住了多少秒（算上还按着的见 longest_stroke）
        self.last_event = ''    # 最近一次键盘 / 鼠标事件

    def on_touch(self, pts):
        """pts：一个 TOUCH 报文里的触点 [(编号, x, y), ...]。"""
        now = time.monotonic()
        self.msgs += 1
        for i, x, y in pts:
            if i not in self.live:
                self.live[i] = [(x, y)]
                self.t_down[i] = now
                self.downs += 1
            elif self.live[i][-1] != (x, y):
                self.live[i].append((x, y))
        for i in set(self.live) - {i for i, _, _ in pts}:
            self.ups += 1
            self.longest = max(self.longest, now - self.t_down.pop(i))
            self.done.append((now, i, self.live.pop(i)))

    def longest_stroke(self):
        """最长的一笔按住了多少秒。还按着的笔算到现在为止（一直按到结束的那一笔往往正是最长的）。"""
        now = time.monotonic()
        return max([self.longest] + [now - t for t in self.t_down.values()])

    def _stroke(self, d, i, pts, live):
        c = self.COLORS[i % len(self.COLORS)]
        if not live:
            c = tuple(v // 2 for v in c)
        if len(pts) > 1:
            d.line(pts, fill=c, width=3)
        for x, y in pts:        # 每个采样点一个小方块：采样稀不稀一眼能看出来
            d.rectangle([x - 2, y - 2, x + 2, y + 2], fill=c)
        x0, y0 = pts[0]
        d.ellipse([x0 - 8, y0 - 8, x0 + 8, y0 + 8], outline=c, width=2)       # 起点
        if not live:
            return
        x, y = pts[-1]
        r = self.RING
        d.ellipse([x - r, y - r, x + r, y + r], outline=c, width=3)
        d.line([(x - r - 30, y), (x + r + 30, y)], fill=c)
        d.line([(x, y - r - 30), (x, y + r + 30)], fill=c)
        label = f'#{i} ({x},{y})'
        tw = d.textlength(label, font=self.small)
        ty = y - r - 62 if y - r - 62 >= 0 else y + r + 36      # 写在圈的上方，太靠上就写到下方
        d.text((min(max(x - tw / 2, 4), wp.VIEW_W - tw - 4), ty), label, fill=c, font=self.small)

    def render(self):
        now = time.monotonic()
        self.done = [s for s in self.done if now - s[0] < self.KEEP_S]
        img = self.base.copy()
        d = ImageDraw.Draw(img)
        for _, i, pts in self.done:
            self._stroke(d, i, pts, False)
        for i, pts in self.live.items():
            self._stroke(d, i, pts, True)
        d.text((480, 14), f'按下 {self.downs}   抬起 {self.ups}   触点 {len(self.live)}', fill=(233, 237, 245), font=self.font)
        if self.last_event:
            d.text((480, 54), self.last_event, fill=(148, 163, 184), font=self.small)
        return img


class Source:
    """按帧号给出横屏画面。"""

    def __init__(self, args):
        self.pattern = args.pattern
        self.rng = np.random.default_rng(1)
        self.font = load_font(64)
        self.echo = None
        if self.pattern == 'touch':
            self.echo = TouchEcho()
        elif self.pattern == 'image':
            if not args.image:
                sys.exit('--pattern image 要配 --image <图片路径>')
            self.base = Image.open(args.image).convert('RGB').resize((wp.VIEW_W, wp.VIEW_H), Image.Resampling.LANCZOS)
            self.scroll = args.scroll_image
        elif self.pattern == 'spots':
            self.base = Image.new('RGB', (wp.VIEW_W, wp.VIEW_H), (200, 200, 200))
        elif self.pattern == 'patch':
            self.canvas = make_card()       # 一帧一帧往上盖，frame() 要按帧号的先后调
            self.small = load_font(28)
        else:
            self.base = make_card()
            self.scroll = self.pattern == 'scroll'

    def frame(self, n):
        if self.echo:
            return self.echo.render()
        if self.pattern == 'spots':
            img = self.base.copy()
            d = ImageDraw.Draw(img)
            color = (255, 0, 0) if n % 2 else (0, 0, 255)
            d.rectangle([880, 300, 929, 339], fill=color)       # 帧缓冲第 350~399 行
            d.rectangle([1050, 300, 1099, 339], fill=color)     # 帧缓冲第 180~229 行
            return img
        if self.pattern == 'patch':
            if n:
                d = ImageDraw.Draw(self.canvas)
                for _ in range(int(self.rng.integers(1, 4))):
                    w, h = int(self.rng.integers(40, 400)), int(self.rng.integers(40, 300))
                    x, y = int(self.rng.integers(0, wp.VIEW_W - w)), int(self.rng.integers(0, wp.VIEW_H - h))
                    color = tuple(int(c) for c in self.rng.integers(40, 216, 3))
                    d.rectangle([x, y, x + w - 1, y + h - 1], fill=color)
                    d.text((x + 4, y + 4), str(n), fill=(255, 255, 255), font=self.small)
            return self.canvas.copy()
        if self.pattern == 'noise':
            return Image.fromarray(self.rng.integers(0, 256, (wp.VIEW_H, wp.VIEW_W, 3), dtype=np.uint8))
        if self.scroll:
            return ImageChops.offset(self.base, (n * 8) % wp.VIEW_W, 0)
        if self.pattern == 'image':
            return self.base
        img = self.base.copy()
        d = ImageDraw.Draw(img)
        d.rectangle([520, 470, 759, 559], fill=(15, 23, 42), outline=(45, 212, 191), width=2)
        d.text((540, 476), f'{n:06d}', fill=(45, 212, 191), font=self.font)
        x = (n * 12) % (wp.VIEW_W - 240) + 100
        d.rectangle([x, 600, x + 39, 639], fill=(248, 113, 113))
        return img


# --prebuild 最多压这么久：Tab5 要求握手走完 5 秒之内发完第一帧（PROTOCOL.md「连接过程」），留 1 秒给发第一帧
PREBUILD_MAX_S = 4.0


def encode_region(fb, r0, h, quality, subsampling, max_strip):
    """把帧缓冲的 [r0, r0+h) 行压成一张或几张 JPEG。超过单条带上限就对半拆（拆到 16 行为止）。
    fb 是要编码的那一份（走 YUV422 的连接上是压到有限范围之后的）。"""
    buf = io.BytesIO()
    fb.crop((0, r0, wp.FB_W, r0 + h)).save(buf, 'JPEG', quality=quality, subsampling=subsampling,
                                             progressive=False, optimize=False)
    data = buf.getvalue()
    if len(data) <= max_strip:
        return [(r0, data)]
    if h <= wp.STRIP_ALIGN:
        raise ValueError(f'第 {r0} 行起的 16 行条带压出来 {len(data)} 字节，超过 Tab5 的单条带上限 {max_strip}；把 --quality 调低')
    h1 = (h // 2 + wp.STRIP_ALIGN - 1) // wp.STRIP_ALIGN * wp.STRIP_ALIGN
    return (encode_region(fb, r0, h1, quality, subsampling, max_strip)
            + encode_region(fb, r0 + h1, h - h1, quality, subsampling, max_strip))


def touch_points(arg, payload):
    """TOUCH 报文里的触点 [(编号, x, y), ...]；长度对不上返回 None。"""
    if len(payload) != arg * wp.TOUCH_PT.size:
        return None
    return [wp.TOUCH_PT.unpack_from(payload, i * wp.TOUCH_PT.size) for i in range(arg)]


class InputGap:
    """离上一个输入报文过了多少毫秒（头一个没有上一个，是 None）。HELD 是照这个间隔发的，打印出来好核对。"""

    def __init__(self):
        self.last = None

    def __call__(self):
        now = time.monotonic()
        gap = None if self.last is None else (now - self.last) * 1000
        self.last = now
        return gap


def describe_input(mtype, arg, payload, gap_ms=None):
    """gap_ms：离上一个输入报文多久了，只有 HELD 用（协议「按着的时候」）。"""
    if mtype == wp.T_HELD and not payload:
        return 'HELD' + (f'（离上一个输入报文 {gap_ms:.0f} 毫秒）' if gap_ms is not None else '')
    if mtype == wp.T_INPUT_MODE and not payload and arg in (0, 1):
        return 'INPUT_MODE ' + ('触摸板' if arg else '触摸')      # Tab5 上选的触摸方式（协议「触摸方式」）
    if mtype == wp.T_TPAD_GAIN and not payload and wp.TPAD_GAIN_MIN <= arg <= wp.TPAD_GAIN_MAX:
        return f'TPAD_GAIN {arg / 10:.1f} 倍'                     # Tab5 上选的触摸板加速（同上）
    if mtype == wp.T_PICTURE and not payload and 1 <= (arg & 0xFF) <= 100:
        # Tab5 上选的画质（协议「画质」）。这个工具只打印出来：它的质量是 --quality 定死的，也没有「画面在动时降质量」
        return f'PICTURE 质量 {arg & 0xFF}，预算 ' + (f'{arg >> 8} KB' if arg >> 8 else '不降')
    if mtype == wp.T_PICTURE_MODE and len(payload) == 4 and arg < len(wp.PIC_MODE_NAMES) \
            and wp.FRAME_US_MIN <= int.from_bytes(payload, 'little') <= wp.FRAME_US_MAX:
        # Tab5 上选的画质是第几档（协议「画质」）。这个工具同样只打印出来
        return f'PICTURE_MODE 第 {arg} 档「{wp.PIC_MODE_NAMES[arg]}」，屏幕的帧周期 {int.from_bytes(payload, "little")} 微秒'
    if mtype == wp.T_TOUCH:
        pts = touch_points(arg, payload)
        if pts is None:
            return f'TOUCH 长度不对：{arg} 个触点却有 {len(payload)} 字节'
        return 'TOUCH ' + (' '.join(f'#{i}({x},{y})' for i, x, y in pts) if pts else '全部抬起')
    if mtype == wp.T_KEY:
        return 'KEY ' + payload.hex(' ')
    if mtype == wp.T_MOUSE and len(payload) == wp.MOUSE.size:
        dx, dy, wheel, buttons = wp.MOUSE.unpack(payload)
        return f'MOUSE dx={dx} dy={dy} wheel={wheel} buttons={buttons:#04x}'
    return f'报文 0x{mtype:02x} arg={arg} 载荷 {len(payload)} 字节'


def how_tab5_closed(sock):
    """收到 Tab5 的 BYE 之后，看它是怎么关连接的：'正常关闭' / '重置了连接（RST）' / '2 秒内没有关连接'。

    Tab5 应该等发送端关了再关。发完 BYE 立刻关的话，发送端还在路上的数据到了一条已经关掉的连接上，回来的是 RST；
    Windows 收到 RST 会把接收缓冲里还没读的数据（就是那个 BYE）丢掉，发送端于是把「Tab5 结束的」误判成掉线、自动重连。

    这里量得到的是「我们关了发送方向之后，Tab5 回的是 FIN 还是 RST」。量不到的：Tab5 的 FIN 到了之后才来的 RST
    （这时这条连接在本机已经算关完了，后到的 RST 不会报给程序）。所以还要对着 Tab5 自己那行「BYE 之后：…」日志看。
    """
    try:
        sock.settimeout(2.0)
        sock.shutdown(socket.SHUT_WR)   # 按协议：收到 BYE 就不再发了。Tab5 不会先关，它读到头（我们关了发送方向）才关连接
        while sock.recv(4096):          # BYE 之后 Tab5 不该再发东西；读到头 = 它关了
            pass
        return '正常关闭'
    except socket.timeout:
        return '2 秒内没有关连接'
    except OSError:                     # 读或者关发送方向时发现连接已经被重置
        return '重置了连接（RST）'


TONE_CHUNK = wp.AUDIO_RATE // 100       # 声音一段 10 ms = 480 个采样帧
TONE_BACKLOG = 20                       # 到点没发出去的声音最多留这么多段（200 ms），再多就把最旧的扔掉


class Tone(threading.Thread):
    """--tone：按墙上的钟发正弦波。第 i 段在握手走完之后 (i + 1) × 10 ms 到点（那时这一段才「录完」）。

    发送都要拿着 lock（和画面共用一把：报文是逐个加密的，打包的先后就是发出的先后）。发画面的线程在每个条带之前调 flush()，
    本线程每几毫秒醒一次、拿到锁就 flush()——发画面的线程正堵在一个条带上时拿不到锁，声音就排在那个条带后面。
    """

    def __init__(self, link, lock, hz, db=-6.0, plan=None):
        super().__init__(daemon=True)
        self.link, self.lock, self.hz = link, lock, hz
        # plan：一段响、一段停地交替（秒），比如 [0.03, 1, 2] = 响 0.03 秒、停 1 秒、响 2 秒，之后一直停；None = 一直响。
        # 停的那些段不发（和电脑上一路声音放完了一样），用来看 Tab5 怎么处理很短的声音、声音停了又来
        self.edges = None if plan is None else [round(sum(plan[:k + 1]) * 100) for k in range(len(plan))]
        self.amp = 32768 * 10 ** (db / 20)      # 幅度：满幅的 db 分贝（默认 -6 = 一半）
        self.t0 = time.perf_counter()
        self.next = 0               # 下一个要发的是第几段
        self.sent = 0               # 发出去的段数
        self.dropped = 0            # 积压太多、没发的段数
        self.late_ms = []           # 每一段从到点到交给系统隔了多久
        self.failed = None          # 发送出的错（连接断了）；之后不再发
        self._quit = threading.Event()

    def chunk(self, i):
        n = np.arange(i * TONE_CHUNK, (i + 1) * TONE_CHUNK)
        v = np.clip(np.round(self.amp * np.sin(2 * np.pi * self.hz * n / wp.AUDIO_RATE)), -32768, 32767).astype('<i2')   # --tone-db 0 时波峰是 32768，不夹住会绕成负的
        return np.repeat(v, 2).tobytes()        # 左右一样

    def on(self, i):
        """第 i 段在 plan 里是不是「响」的。"""
        if self.edges is None:
            return True
        k = sum(1 for e in self.edges if e <= i)        # 它前面过了几个交界
        return k < len(self.edges) and k % 2 == 0

    def flush(self):
        """把到点的声音都发掉。调用时必须拿着 lock。"""
        if self.failed or self._quit.is_set():
            return
        due = int((time.perf_counter() - self.t0) * 100)        # 已经到点的段数
        if due - self.next > TONE_BACKLOG:
            self.dropped += due - self.next - TONE_BACKLOG
            self.next = due - TONE_BACKLOG
        if due <= self.next:
            return
        now = time.perf_counter()
        todo = [i for i in range(self.next, due) if self.on(i)]
        self.late_ms += [(now - self.t0 - (i + 1) / 100) * 1e3 for i in todo]
        data = b''.join(self.link.pack(wp.T_AUDIO, 0, self.chunk(i)) for i in todo)
        self.sent += len(todo)
        self.next = due
        if not todo:
            return
        try:
            self.link.sock.sendall(data)
        except OSError as e:        # 连接断了：发画面 / 收 ACK 的那一头会发现并报告
            self.failed = e

    def run(self):
        while not self._quit.wait(0.004):
            with self.lock:
                self.flush()

    def stop(self):
        """不再发声音（之后的 flush() 什么都不做）。在发 BYE 之前调。"""
        self._quit.set()
        if self.is_alive() and threading.current_thread() is not self:
            self.join()


def say_bye(link):
    """我们这边结束：发 BYE，然后按协议不立刻关——只关发送方向，把 Tab5 还在路上的报文（ACK、触摸）读掉，等它关了再关。

    发完立刻关的话，Tab5 还在路上的报文到了一条已经关掉的连接上，我们这头回的是 RST。
    """
    sock = link.sock
    link.send(wp.T_BYE)
    sock.shutdown(socket.SHUT_WR)
    sock.settimeout(2.0)
    while sock.recv(4096):
        pass


def stat(values, unit='ms'):
    """平均 / 中位 / 95% / 最大。比两种跑法时看中位数：无线上偶尔丢一个包要等 TCP 重传，一次就停一秒左右，平均数会被它带偏。"""
    if not values:
        return '无数据'
    v = sorted(values)
    return (f'平均 {sum(v) / len(v):.1f} / 中位 {statistics.median(v):.1f} / 95% {v[min(len(v) - 1, int(len(v) * 0.95))]:.1f}'
            f' / 最大 {v[-1]:.1f} {unit}')


def pick_targets(found, store, can_pair):
    """自动发现应答的那些 Tab5 里可以去连的，按先后（PROTOCOL.md「自动发现」）：配对过的优先；
    一个配对过的都没有、又可以配对（给了 --password），才连没配对过的。found 是 wp.discover() 的结果。"""
    paired = [f for f in found if store.get(f[2]) is not None]
    if paired or not can_pair:
        return paired
    return list(found)


def run(args):
    """跑一遍测试，返回退出码（见文件开头）。"""
    if args.strip_h <= 0 or args.strip_h % wp.STRIP_ALIGN:
        sys.exit(f'--strip-h 必须是 {wp.STRIP_ALIGN} 的正整数倍')
    if args.prebuild and args.pattern == 'touch':
        sys.exit('--prebuild 不能和触摸回显一起用（它的画面要等触点来了才知道）')
    if args.prebuild and args.tone:
        sys.exit('--prebuild 不能和 --tone 一起用（声音从握手走完就开始发，画面要等压完才发，两边对不上）')
    if args.strip_mix and any(h <= 0 or h % wp.STRIP_ALIGN for h in args.strip_mix):
        sys.exit(f'--strip-mix 里每一种条带高都要是 {wp.STRIP_ALIGN} 的正整数倍')
    if args.max_inflight < 1 or args.ack_wait <= 0:
        sys.exit('--max-inflight 至少是 1，--ack-wait 必须是正数')
    inflight = min(STEADY_INFLIGHT, args.max_inflight) if args.inflight is None else args.inflight
    if not 1 <= inflight <= args.max_inflight:
        sys.exit(f'--inflight 要在 1 和 --max-inflight（{args.max_inflight}）之间，不是 {inflight}')
    if args.first_delay < 0 or args.bye_lag < 0 or (args.own_bye_query or 0) < 0:
        sys.exit('--first-delay、--bye-lag、--own-bye-query 不能是负数')
    src = Source(args)
    echo = src.echo                 # 不是空 = 触摸回显：画面跟着 Tab5 发来的输入事件变，没有固定的帧数
    if args.password is not None and wp.password_problem(args.password):
        sys.exit('--password：' + wp.password_problem(args.password))
    store = wp.PairStore(args.pair_store)
    if args.forget:
        store.forget()
    host, port = args.host, args.port
    tab5_id = None                  # auto：挑中的那一台应答时报的编号（握手时核对 HELLO 里的是不是它）；手填地址时没有
    if host == 'auto':
        found = wp.discover(port=port)
        if not found:
            sys.exit('广播询问没有 Tab5 应答（Tab5 上要开着「无线显示」页，而且正在等连接）')
        usable = pick_targets(found, store, args.password is not None)
        if not usable:
            sys.exit(f'应答的 {len(found)} 台 Tab5 都没配对过：要配对就给 --password')
        host, port, tab5_id = usable[0]
        print(f'{len(found)} 台 Tab5 应答，连 {host}:{port}（编号 {tab5_id.hex()}，{"" if store.get(tab5_id) else "没"}配对过）')
    sock = socket.create_connection((host, port), timeout=5)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    # 画面不动的时候两边都没有数据，对端没了靠保活探测发现（约 11 秒，和 C# 发送端一样；这几个选项只有 Linux 有）
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
    for opt, val in (('TCP_KEEPIDLE', 5), ('TCP_KEEPINTVL', 2), ('TCP_KEEPCNT', 3)):
        if hasattr(socket, opt):
            sock.setsockopt(socket.IPPROTO_TCP, getattr(socket, opt), val)
    # 走 YUV422 帧缓冲的连接只能发 4:2:2（协议「画面的色彩」）：指定了别的采样就不声明这个能力，照旧走 RGB565
    caps = args.caps
    if args.subsampling not in (None, '422') and caps & wp.CAP_YUV422:
        caps &= ~wp.CAP_YUV422
        print(f'--subsampling {args.subsampling}：不声明 YUV422（它只能发 4:2:2），这条连接走 RGB565')
    try:
        link, hello = wp.sender_handshake(sock, store, args.name, args.password, log=print, expect_id=tab5_id, caps=caps)
    except wp.DeviceBye:
        print('握手期间 Tab5 上的人取消了（BYE）')
        print(f'BYE 之后 Tab5 {how_tab5_closed(sock)}')
        sock.close()
        return 1
    except wp.AuthError as e:
        sock.close()
        print(f'认证没过：{e}')
        return 3
    except wp.ProtoError as e:
        sock.close()
        print(f'握手期间 Tab5 发来的东西不合协议：{e}')
        return 2
    except (ConnectionError, socket.timeout) as e:
        sock.close()
        print(f'握手期间连接断了：{e or type(e).__name__}')
        return 2
    max_strip, max_frame = hello.max_strip, hello.max_frame
    print(f'HELLO：{hello.name}，协议 {hello.ver}，帧缓冲 {hello.fb_w}×{hello.fb_h}，画面 {hello.view_w}×{hello.view_h}，'
          f'单条带上限 {max_strip}，单帧上限 {max_frame} 字节；认证通过；'
          + ('画面走 YUV422（4:2:2、有限范围）' if link.yuv else '画面走 RGB565'))
    subsampling = '422' if link.yuv else args.subsampling or '420'

    if args.first_delay > 0:
        # 握手完了但先不发画面：这段时间屏幕还是 Tab5 自己的界面，它不应该发触摸和按键过来（发了的话下面收报文时会打印出来）
        print(f'握手完了，等 {args.first_delay:g} 秒再发第一帧')
        time.sleep(args.first_delay)

    # 声音：和画面走同一条连接，发送都拿着 tx_lock（没有 --tone 时只有本线程在发，这把锁没人争）
    tx_lock = threading.Lock()
    tone = None
    if args.tone:
        if hello.caps & wp.CAP_DEV_AUDIO:
            tone = Tone(link, tx_lock, args.tone, args.tone_db, args.tone_plan)
            tone.start()
        else:
            print('Tab5 没说能收声音（HELLO 的能力位），--tone 不发')

    sub = SUBSAMPLING[subsampling]
    quality = args.quality
    prev = None
    enc_ms, rtt_ms, dev_recv, dev_dec, dev_present, frame_bytes, frame_strips, gap_ms = [], [], [], [], [], [], [], []
    pending = collections.deque()   # 已发出、还没收到 ACK 的帧：(序号, 发出时刻, 条带数, 字节数, 编码毫秒)
    sent = 0                        # 已发出的帧数
    late = 0                        # 因为 ACK 迟到、在平时的在途帧数之外多发的帧数
    recent_rtt = collections.deque(maxlen=16)       # 最近几帧发出到 ACK 的毫秒数，定「等多久算 ACK 迟到」用
    t_last_send = None
    dropped = False
    interrupted = False             # 没发完就被 Ctrl-C 打断了（触摸回显不算，它本来就是这么结束的）
    all_sent = False                # 要发的帧都发出去并且都收到 ACK 了（之后只剩 --hold 和收尾）
    input_gap = InputGap()
    t_begin = time.perf_counter()

    def take_msg():
        """收一个报文。是 ACK：对上最早在途的那一帧，记下各项耗时；是输入事件：打印出来。"""
        mtype, _flags, arg, payload = link.recv(4096)
        if mtype == wp.T_DEV_BYE:
            raise wp.DeviceBye()
        if mtype != wp.T_ACK:
            text = describe_input(mtype, arg, payload, input_gap())
            if echo:
                pts = touch_points(arg, payload) if mtype == wp.T_TOUCH else None
                if pts is not None:
                    echo.on_touch(pts)
                elif mtype != wp.T_HELD:        # HELD 不是一件事（按着的还按着），不占「最近一次事件」那一行
                    echo.last_event = text
            if not echo or args.verbose:        # 触摸回显时一秒几十个触摸报文，不加 -v 就不逐个打印
                print('  ← ' + text)
            return
        if len(payload) != wp.ACK.size:
            raise wp.ProtoError(f'ACK 载荷 {len(payload)} 字节，应为 {wp.ACK.size}')
        ack = wp.ACK.unpack(payload)
        if not pending or ack[0] != pending[0][0]:
            raise wp.ProtoError(f'来了第 {ack[0]} 帧的 ACK，在途的最早一帧是' + (f'第 {pending[0][0]} 帧' if pending else '（没有）'))
        seq, t_send, n_strips, n_bytes, enc = pending.popleft()
        enc_ms.append(enc)
        rtt_ms.append((time.perf_counter() - t_send) * 1e3)
        recent_rtt.append(rtt_ms[-1])
        dev_recv.append(ack[2] / 1e3)
        dev_dec.append(ack[3] / 1e3)
        dev_present.append(ack[4] / 1e3)
        frame_bytes.append(n_bytes)
        frame_strips.append(n_strips)
        if args.verbose:
            print(f'帧 {seq}：{n_strips} 个条带 {n_bytes / 1024:.1f} KB，编码 {enc:.1f} ms，'
                  f'发出到 ACK {rtt_ms[-1]:.1f} ms（Tab5：接收 {dev_recv[-1]:.1f} 解码 {dev_dec[-1]:.1f} 上屏 {dev_present[-1]:.1f} ms）')

    def build(n):
        """第 n 帧：画出来、和上一帧比、把变了的条带压好。返回（横屏画面，条带，字节数，开始编码的时刻）。要按帧号的先后调。"""
        nonlocal quality, prev
        view = src.frame(n)
        fb = wp.view_to_fb(view)
        strip_h = int(mix_rng.choice(args.strip_mix)) if args.strip_mix else args.strip_h
        arr = np.asarray(fb)        # 变没变拿原画面比
        if link.yuv:
            fb = wp.to_limited(fb)
        while True:
            t0 = time.perf_counter()
            strips = []
            for r0 in range(0, wp.FB_H, strip_h):
                h = min(strip_h, wp.FB_H - r0)
                if prev is None or args.all_strips or not np.array_equal(arr[r0:r0 + h], prev[r0:r0 + h]):
                    strips += encode_region(fb, r0, h, quality, sub, max_strip)
            total = sum(len(j) for _, j in strips)
            if total <= max_frame:
                break
            if quality <= 10:
                sys.exit(f'质量降到 {quality} 一帧仍有 {total} 字节，超过 Tab5 的单帧上限 {max_frame}')
            quality = max(10, int(quality * 0.75))
            print(f'  第 {n} 帧 {total} 字节超过单帧上限 {max_frame}，质量降到 {quality} 重压')
        if strips:
            prev = arr
        return view, strips, total, t0

    t_stop = time.monotonic() + args.duration if echo and args.duration > 0 else None
    mix_rng = np.random.default_rng(2)      # --strip-mix 每帧挑哪一种条带高
    last_view = None                        # 发出去的最后一帧（--dump-last）
    prebuilt = None
    try:
        if args.prebuild:
            # 握手走完之后才知道走哪种帧缓冲格式，所以在这里压（出了错也走下面的收尾，把连接关掉）
            t_pre = time.perf_counter()
            prebuilt = [build(n) for n in range(args.frames)]
            took = time.perf_counter() - t_pre
            print(f'--prebuild：{args.frames} 帧事先压好，用了 {took:.1f} 秒')
            if took > PREBUILD_MAX_S:
                sys.exit(f'--prebuild 压了 {took:.1f} 秒，超过 {PREBUILD_MAX_S:g} 秒：减少 --frames')
            t_begin = time.perf_counter()       # 帧率从开始发算起
        for n in (itertools.count() if echo else range(args.frames)):
            if echo:
                if t_stop is not None and time.monotonic() >= t_stop:
                    break
                while select.select([sock], [], [], 0)[0]:      # 已经到了的输入事件先都收下，画的是最新的状态
                    take_msg()
            t_frame = time.perf_counter()
            view, strips, total, t0 = prebuilt[n] if prebuilt else build(n)
            if not strips:              # 画面没变：不编码不发
                if echo and select.select([sock], [], [], 0.1)[0]:     # 等输入事件；最多 0.1 秒就回去重画（抬起的笔到时间要擦掉）
                    take_msg()
                continue
            t1 = time.perf_counter()
            if prebuilt:
                t0 = t1                 # 编码是事先做的，不算在这一帧头上
            t_ms = int(time.monotonic() * 1000) & 0xFFFFFFFF
            if len(pending) >= inflight:        # 平时到了这个数是要等 ACK 的：这一帧是因为 ACK 迟到多发的
                late += 1
            if tone:
                # 一个条带一个条带地发，每个之前先把到点的声音发掉：声音不用等一整帧发完
                for r0, j in strips:
                    with tx_lock:
                        tone.flush()
                        sock.sendall(link.pack(wp.T_STRIP, r0, j))
                with tx_lock:
                    tone.flush()
                    sock.sendall(link.pack(wp.T_PRESENT, 0, wp.PRESENT.pack(sent, t_ms)))
            else:
                sock.sendall(b''.join(link.pack(wp.T_STRIP, r0, j) for r0, j in strips)
                             + link.pack(wp.T_PRESENT, 0, wp.PRESENT.pack(sent, t_ms)))
            pending.append((sent, t1, len(strips), total, (t1 - t0) * 1e3))
            if t_last_send is not None:
                gap_ms.append((t1 - t_last_send) * 1e3)
            t_last_send = t1
            last_view = view
            sent += 1
            # 在途的不到 inflight 帧就不等，直接去发下一帧。到了 inflight 帧就等 ACK：到了上限一直等；没到上限时，
            # 最早在途的那一帧发出去超过 ack_wait 还没确认就先去发下一帧（门限从最早那一帧算起，所以一迟到会连着放到上限）。
            # ack_wait 不低于最近往返时间中位数的两倍：每帧本来就慢的时候（画面重、链路慢）不算迟到，不往路上多压帧。
            # 往返时间还没摸准（收到的 ACK 不到 RTT_MIN_SAMPLES 个）时不先发：只凭一两个样本定门限，
            # 画面重的时候一开头就会压上好几帧。门限每收进一个报文重算一次（往返样本变了）
            while len(pending) >= inflight:
                ack_wait = max(args.ack_wait, 2 * statistics.median(recent_rtt)) if len(recent_rtt) >= RTT_MIN_SAMPLES else None
                if ack_wait is not None and len(pending) < args.max_inflight:
                    # 时限过了也先看一眼有没有已经到了的报文（前面卡住的几个 ACK 常常是一起到的），都没有才算迟到
                    left = pending[0][1] + ack_wait / 1e3 - time.perf_counter()
                    if not select.select([sock], [], [], max(left, 0))[0]:
                        break       # 迟到了：去发下一帧（真发出去的时候数进 late）
                take_msg()
            while pending and select.select([sock], [], [], 0)[0]:      # 已经到了的 ACK 先收下，不等
                take_msg()
            if args.fps > 0:
                rest = t_frame + 1.0 / args.fps - time.perf_counter()
                if rest > 0:
                    time.sleep(rest)
        while pending:              # 最后几帧的 ACK
            take_msg()
        all_sent = True
        if args.dump_last and last_view is not None:
            last_view.save(args.dump_last)
        elapsed = time.perf_counter() - t_begin
        if args.hold > 0:
            print(f'画面发完，保持连接 {args.hold} 秒，打印 Tab5 发来的输入事件')
            t_end = time.monotonic() + args.hold
            while (left := t_end - time.monotonic()) > 0:
                # 先等到有数据再收：收到一半超时的话读掉的那几个字节就丢了，后面的报文全错位
                if not select.select([sock], [], [], min(left, 0.5))[0]:
                    continue
                mtype, _flags, arg, payload = link.recv(4096)
                if mtype == wp.T_DEV_BYE:
                    raise wp.DeviceBye()
                print('  ← ' + describe_input(mtype, arg, payload, input_gap()))
        asked, answers = None, []
        if args.own_bye_query is not None:
            # 我们发完 BYE 隔一小会儿问一次（单播）。Tab5 的应答要等它收完尾、关了连接才发出来，所以放到后台去问、去收
            def ask():
                time.sleep(args.own_bye_query)
                answers.extend(wp.discover(wait=BYE_QUERY_S, rounds=1, port=port, broadcast=host))
            asked = threading.Thread(target=ask, daemon=True)
            asked.start()
        if tone:
            tone.stop()
        say_bye(link)
        if asked:
            asked.join()
            print('自己发 BYE 之后问的那一次：' + ('Tab5 应答了' if answers else f'{BYE_QUERY_S * 1000:.0f} 毫秒之内没有应答'))
    except wp.DeviceBye:
        elapsed = time.perf_counter() - t_begin
        print('Tab5 退出了显示模式（BYE）')
        asked, answers = None, []
        if args.bye_query:
            # 关连接之前先问一次（单播，马上就到）。Tab5 的应答要等我们关了连接、它收完尾才发出来，所以放到后台去收
            asked = threading.Thread(target=lambda: answers.extend(wp.discover(wait=BYE_QUERY_S, rounds=1, port=port, broadcast=host)),
                                     daemon=True)
            asked.start()
        if args.bye_lag > 0:
            # 当成这个 BYE 晚到了：Tab5 发出的报文在空中丢了的时候，BYE 要等它的 TCP 重传才到，这期间我们既不发数据也不关连接
            print(f'当成 BYE 晚到了 {args.bye_lag:g} 秒：隔这么久再关连接')
            time.sleep(args.bye_lag)
        print(f'BYE 之后 Tab5 {how_tab5_closed(sock)}')
        if asked:
            asked.join()
            print('关连接之前问的那一次：' + ('Tab5 应答了' if answers else f'{BYE_QUERY_S * 1000:.0f} 毫秒之内没有应答'))
    except KeyboardInterrupt:
        elapsed = time.perf_counter() - t_begin
        print('按了 Ctrl-C，结束')
        interrupted = not echo and not all_sent
        try:
            if tone:
                tone.stop()
            say_bye(link)
        except OSError:
            pass
    except wp.ProtoError as e:      # Tab5 发来的东西不合协议（含认证码对不上）：这条连接不能再用了
        elapsed = time.perf_counter() - t_begin
        dropped = True
        print(f'Tab5 发来的东西不合协议：{e}')
    except (ConnectionError, socket.timeout) as e:      # Tab5 关了连接（发的东西不合规定、它掉线了……）或者一直没回 ACK
        elapsed = time.perf_counter() - t_begin
        dropped = True
        print(f'连接中途断了：{e or type(e).__name__}')
    finally:
        if tone:
            tone.stop()
        sock.close()

    if tone:
        print(f'声音：{args.tone:g} Hz，发了 {tone.sent} 段（{tone.sent / 100:.1f} 秒），积压超过 {TONE_BACKLOG * 10} ms 扔掉 {tone.dropped} 段（{tone.dropped * 10} ms）'
              + (f'；从到点到发出：{stat(tone.late_ms)}' if tone.late_ms else ''))
    if echo:
        print(f'触摸：TOUCH 报文 {echo.msgs} 个，按下 {echo.downs} 次，抬起 {echo.ups} 次，最长的一笔按住了 {echo.longest_stroke():.1f} 秒')
    acked = len(rtt_ms)             # 收到 ACK 的帧数；下面各项统计都只算这些帧
    if not acked:
        print('一帧都没发出去' if not sent else f'发了 {sent} 帧，一个 ACK 都没收到')
        return 2 if dropped else 130 if interrupted else 1
    total_bytes = sum(frame_bytes)
    print(f'== {args.pattern}，质量 {quality}，{subsampling}{"（YUV422 帧缓冲）" if link.yuv else ""}，条带高 {"每帧在 " + "/".join(map(str, args.strip_mix)) + " 里挑" if args.strip_mix else args.strip_h} ==')
    print(f'发了 {acked} 帧，用时 {elapsed:.2f} 秒，{acked / elapsed:.1f} 帧/秒，{total_bytes * 8 / elapsed / 1e6:.2f} Mbps'
          + (f'（另有 {sent - acked} 帧发出后没等到 ACK）' if sent != acked else ''))
    print(f'每帧：{sum(frame_strips) / acked:.1f} 个条带，平均 {total_bytes / acked / 1024:.1f} KB，最大 {max(frame_bytes) / 1024:.1f} KB')
    print(f'编码：{stat(enc_ms)}')
    print(f'相邻两帧发出的间隔：{stat(gap_ms)}')
    if not echo and args.fps <= 0 and gap_ms:       # 限了帧率、触摸回显时两帧之间本来就有空档，不数
        stalls = [g for g in gap_ms if g > STALL_MS]
        smooth = (len(gap_ms) + 1) / ((sum(gap_ms) - sum(stalls) + len(stalls) * statistics.median(gap_ms)) / 1e3)
        print(f'卡顿（相邻两帧发出的间隔超过 {STALL_MS} ms）：{len(stalls)} 次，共 {sum(stalls):.0f} ms；不算卡顿 {smooth:.1f} 帧/秒')
    if args.max_inflight > 1:
        print(f'确认迟到、先发下一帧：{late} 次（平时 {inflight} 帧在途，迟到时最多 {args.max_inflight} 帧，至少等 {args.ack_wait:g} ms）')
    print(f'发出到 ACK：{stat(rtt_ms)}')
    print(f'Tab5 接收：{stat(dev_recv)}；解码：{stat(dev_dec)}；上屏：{stat(dev_present)}')
    return 2 if dropped else 130 if interrupted else 0


def parse_args(argv=None):
    p = argparse.ArgumentParser(description='Tab5 无线显示测试发送端', formatter_class=argparse.RawDescriptionHelpFormatter,
                                epilog=__doc__)
    p.add_argument('host', help='Tab5 的地址；auto = 在局域网里广播询问（先连配对过的，没配对过的要给 --password 才连）')
    p.add_argument('--port', type=int, default=wp.PORT)
    p.add_argument('--pattern', choices=['card', 'scroll', 'noise', 'image', 'spots', 'patch', 'touch'], default='card')
    p.add_argument('--image', help='--pattern image 用的图片')
    p.add_argument('--scroll-image', action='store_true', help='图片每帧横移 8 像素')
    p.add_argument('--quality', type=int, default=60, help='JPEG 质量 1~95（默认 60）')
    p.add_argument('--subsampling', choices=sorted(SUBSAMPLING), default=None,
                   help='色度采样。不给：这条连接走 YUV422 帧缓冲时是 422（只能是它），走 RGB565 时是 420。'
                        '给了 420 / 444 就不声明 YUV422、走 RGB565；要「RGB565 + 4:2:2」给 --caps 7 --subsampling 422')
    p.add_argument('--strip-h', type=int, default=160, help='条带高（帧缓冲的行数，16 的倍数，默认 160 = 每帧 8 条）')
    p.add_argument('--strip-mix', type=lambda v: [int(x) for x in v.split(',')], default=None, metavar='行,行,…',
                   help='每帧从这几种条带高里随机挑一种（种子固定），代替 --strip-h：相邻两帧的条带边界不一样')
    p.add_argument('--prebuild', action='store_true',
                   help='握手之后先把全部帧压好再连着发：发的时候不花编码时间，帧到得比 Tab5 的屏幕刷新快（配 --inflight 3 压它「一帧解完了、前一帧还没上屏」那条路）。'
                        '帧数要少到 4 秒之内压得完；触摸回显不能用')
    p.add_argument('--dump-last', metavar='PNG', help='把发出去的最后一帧（横屏画面）存成图片：屏幕上应该是它')
    p.add_argument('--frames', type=int, default=200, help='发多少帧（默认 200）')
    p.add_argument('--duration', type=float, default=0, metavar='秒',
                   help='--pattern touch 跑多少秒；0 = 一直跑到 Tab5 上做退出手势或按 Ctrl-C（默认）')
    p.add_argument('--fps', type=float, default=0, help='限帧率；0 = 收到 ACK 就发下一帧（默认）')
    p.add_argument('--all-strips', action='store_true', help='每帧都发全部条带，不跳过没变的')
    p.add_argument('--inflight', type=int, default=None, metavar='N',
                   help=f'平时保持几帧在途（已发出、还没收到 ACK）。默认 {STEADY_INFLIGHT}，没给时不超过 --max-inflight；'
                        '1 = 平时收到 ACK 才发下一帧（ACK 迟到时照样多发，最多 --max-inflight 帧）')
    p.add_argument('--max-inflight', type=int, default=4, metavar='N',
                   help='最早在途的那一帧的 ACK 迟到时，最多放到几帧在途。默认 4；1 = 死等 ACK')
    p.add_argument('--ack-wait', type=float, default=60, metavar='MS',
                   help='最早在途的那一帧发出后至少等这么多毫秒的 ACK（往返本来就慢时等得更久，见文件开头），等不到才算迟到（默认 60）')
    p.add_argument('--tone', type=float, default=0, metavar='赫兹',
                   help='画面之外同时发一个这个频率的正弦波（协议「声音」，见文件开头）；Tab5 没说能收声音就不发')
    p.add_argument('--tone-db', type=float, default=-6.0, metavar='分贝',
                   help='--tone 的幅度，相对满幅的分贝数（默认 -6；夜里测就给 -50 这样的小数，统计不受影响）')
    p.add_argument('--tone-plan', type=lambda v: [float(x) for x in v.split(',')], default=None, metavar='秒,秒,…',
                   help='--tone 一段响、一段停地交替：响,停,响,…（秒），列完之后一直停；不给 = 一直响。例：0.03,1,2 = 响 0.03 秒、停 1 秒、响 2 秒')
    p.add_argument('--hold', type=float, default=0, help='发完后保持连接多少秒，用来看输入事件')
    p.add_argument('--caps', type=lambda v: int(v, 0), default=wp.SENDER_CAPS, metavar='位图',
                   help='AUTH 里声明的能力位（默认 31：bit0 认得 INPUT_MODE、bit1 认得 TPAD_GAIN、bit2 认得 PICTURE、bit3 走 YUV422 帧缓冲、bit4 认得 PICTURE_MODE）。'
                        '少声明 bit0~2、bit4 里的哪一位，Tab5 就不该发那种报文（不声明 bit4 而声明 bit2：画质折成 PICTURE 发）；不声明 bit3 画面走 RGB565：'
                        '0 = 像加这几个报文之前的发送端，1 = 像只认得 INPUT_MODE 的那一版，3 = 像加 PICTURE 之前的那一版，7 = 像加 YUV422 之前的那一版，'
                        '15 = 像加 PICTURE_MODE 之前的那一版')
    p.add_argument('--bye-lag', type=float, default=0, metavar='秒',
                   help='收到 Tab5 的 BYE 之后隔这么久才关连接（默认 0）。模拟 BYE 在空中丢了、等 TCP 重传才到；Tab5 应该等到我们关了才关')
    p.add_argument('--bye-query', action='store_true',
                   help='收到 Tab5 的 BYE 时、关连接之前，先单播问它一次（自动发现的询问），报告 300 毫秒之内有没有应答。'
                        '真的发送端是关了连接马上再问，两样几乎同时到 Tab5、谁先到不一定；这里把「询问先到」这一种做成确定的'
                        '（配 --bye-lag 0.05）。Tab5 还开着页面的话应该应答：连接一结束，之后到的询问都算数')
    p.add_argument('--own-bye-query', type=float, default=None, metavar='秒',
                   help='画面发完、我们自己发了 BYE 之后隔这么久，单播问 Tab5 一次（自动发现的询问），报告 300 毫秒之内有没有应答。'
                        '真的发送端发完 BYE 等 Tab5 关连接最多等 300 毫秒，到时间就自己关、马上再问；Tab5 收尾慢的时候这个询问在它收尾期间就到了，'
                        '不能被当成「连接期间攒下的」丢掉。这里把它做成确定的：隔的时间要比 BYE 到 Tab5 的时间长、比 Tab5 收尾的时间短（给 0.015）')
    p.add_argument('--first-delay', type=float, default=0, metavar='秒',
                   help='握手完了之后等这么久再发第一帧（默认 0）。Tab5 要求 READY 之后 5 秒内发完第一帧，超过它会断开')
    p.add_argument('--password', help='配对密码：没和这台 Tab5 配对过时要给，Tab5 上的人输同一个')
    p.add_argument('--pair-store', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), '.wdpair_sender.json'),
                   metavar='文件', help='自己的编号和配对过的 Tab5 存在哪（默认本目录的 .wdpair_sender.json）')
    p.add_argument('--forget', action='store_true', help='先忘掉所有配对过的 Tab5（重新配对）')
    p.add_argument('--name', default=socket.gethostname(), help='报给 Tab5 的名字（默认本机的机器名）')
    p.add_argument('-v', '--verbose', action='store_true', help='每帧打印一行')
    return p.parse_args(argv)


if __name__ == '__main__':
    sys.exit(run(parse_args()))
