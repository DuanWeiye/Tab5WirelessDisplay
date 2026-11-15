#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
重新生成中文字库 font_cn_20 / font_cn_28（全字集）和 font_cn_48（只有主页面那行大字状态用到的十几个字）。
font_cn_20 / 28 的字符集 = 现有字库字 ∪ 日文假名 ∪ JIS 汉字 ∪ 标点。
现有字库字里已经包含当初并入的拼音词典全字（拼音输入法 2026-11-11 随 AI 对话一起删除，
词典文件不在了，但这些字仍留在字库里，照常从旧 .c 的生成命令里带出来）。

背景/坑：
  - 假名：最初的字符集没带假名/日文，这里补平假名+片假名+长音符。
  - 日文汉字：并入 JIS 常用汉字 2136（tools/joyo_kanji.txt）∪ JIS X0208 全集 6356
    （第一+第二水准，动态从 euc_jp 生成，覆盖现代日文标准编码全部汉字、消除日文汉字豆腐）。
    实测 NotoSansSC-Regular 对 JIS X0208 全集 0 缺失，故无需换字体源。中日共享码点的字按该
    字体字形显示（能认、不影响读懂）；要纯日式字形才需双字库 + 按语言切 font，本工程不做。
    （曾只收第一水准，实测「餃/鮨/饅」等第二水准日常字仍豆腐 → 改全收根治。）
  - LV_SYMBOL_* 图标是 montserrat 私有区码点，font_cn 没有 → 带图标的 label 要显式
    设 montserrat 字体，不能靠 font_cn（否则豆腐）。

用法（在 firmware 目录下跑）：
    cd firmware
    python3 tools/gen_font.py              # 三个都重新生成
    python3 tools/gen_font.py font_cn_48   # 只生成点了名的（主页面的状态文字改了之后跑这一条，几秒钟）
依赖：npx lv_font_conv（已装 1.5.3）；字体 dependencies/.../NotoSansSC-Regular.ttf（完整 Noto Sans SC）。
跑完直接覆盖 main/ui/fonts/ 下对应的 .c（font_cn_20 / font_cn_28 / font_cn_48），重新 idf.py build 即可。
"""
import re, subprocess, sys, os

# 必须在 firmware 根目录跑（相对路径引用 main/、dependencies/）
if not os.path.isfile('main/ui/fonts/font_cn_20.c'):
    sys.exit("请在 firmware 目录下运行：cd firmware && python3 tools/gen_font.py")

# 现有字库字集（从旧 .c 的生成命令 --symbols 提取，保证不丢已有 UI 用字）
src = open('main/ui/fonts/font_cn_20.c', encoding='utf-8').read(30000)
font_chars = set(ord(c) for c in re.search(r'--symbols (.*?) --size', src, re.S).group(1))

# 平假名 + 片假名 + 长音/叠字符
kana = set(range(0x3041, 0x3097)) | set(range(0x309D, 0x30A0)) \
     | set(range(0x30A1, 0x30FB)) | {0x30FC, 0x30FD, 0x30FE}

# JIS 常用汉字 2136（grade 1-6 + 8），用于显示日文（WiFi 名、LLM 出口名等外来文本）。
# 实测 NotoSansSC-Regular 对全部 2136 字 0 缺失（含和制汉字 働峠畑 等），故无需换字体源。
# 与中文重叠的字形按简体显示（能认，不影响阅读）；要纯日式字形才需双字库，本工程不做。
joyo = set()
joyo_path = 'tools/joyo_kanji.txt'
if os.path.isfile(joyo_path):
    joyo = {ord(c) for c in open(joyo_path, encoding='utf-8').read() if not c.isspace()}
    print(f"载入 JIS 常用汉字 {len(joyo)} 字（{joyo_path}）")
else:
    print(f"⚠️ 未找到 {joyo_path}，本次不含 joyo")

# JIS X 0208 汉字全集（第一+第二水准，6356 字）——动态从 euc_jp 编码空间生成（汉字首字节 0xB0–0xF4），
# 是 joyo 的超集，覆盖现代日文标准编码的全部汉字，消除日文汉字豆腐
# （当初是为翻译输出加的，翻译功能已删；现在还用得上的是 WiFi 名、LLM 出口名这类外来文本）。
# 最终定为"全收"（曾只收第一水准，实测「餃/鮨/饅/鰻」等日常字仍豆腐——它们在第二水准）：
# 一次根治不再打地鼠。NotoSansSC 对 JIS X0208 全集 0 缺失（实测），故无需换字体/双字库。
# 代价：相对只收一级再净增约 2541 字、固件 +约 2MB；要纯日式字形才需双字库，本工程不做。
jis = set()
for cp in range(0x4E00, 0xA000):
    try:
        b = chr(cp).encode('euc_jp')
    except Exception:
        continue
    if len(b) == 2 and 0xB0 <= b[0] <= 0xF4:   # 第一(0xB0–0xCF)+第二(0xD0–0xF4)水准
        jis.add(cp)
print(f"载入 JIS X0208 汉字 {len(jis)} 字（第一+第二水准全集，覆盖现代日文标准编码）")

# CJK 标点 + 全角符号 + 通用标点（日文/中文常用：「」。、（）！？〜…— 等，NotoSansSC 全有）。
# 原来只收 JIS 汉字区(0xB0-0xF4)漏了符号区 → 「」・〜全角空格等豆腐。
punct = set(range(0x3000, 0x3040))       # CJK 符号标点 　、。「」『』【】〜…
punct |= set(range(0xFF01, 0xFF61))      # 全角 ASCII ！？（）：；＆＊
punct |= set(range(0xFFE0, 0xFFE7))      # 全角符号 ￥￠£
punct |= set(range(0x2010, 0x2028))      # 通用标点 —''""…•‥
punct |= {0x30FB}                        # ・（片假名中点）
print(f"载入标点符号 {len(punct)} 个（CJK标点+全角+通用，修「」・〜等豆腐）")

target = font_chars | kana | joyo | jis | punct
syms = ''.join(chr(c) for c in sorted(target) if not (0x20 <= c <= 0x7e))  # ASCII 用 -r 单独覆盖
print(f"目标 {len(target)} 字（现有∪假名∪joyo∪JIS全集）；--symbols 段 {len(syms)} 字 + ASCII(-r 0x20-0x7E)")

# 主页面中间那行大字状态（main/ui/ui_main.cpp 的 refresh）：只收这几句话里的字，不带 ASCII（电脑名用 28 号另起一行）。
# 那里的字样改了，这里跟着改、重新生成，不然新字是豆腐。
HERO_TEXT = "等待连接已连接已暂停正在联网未联网无线模块故障"

TTF = "dependencies/lvgl/tests/src/test_files/fonts/noto/NotoSansSC-Regular.ttf"
FONTS = [  # (字号, 名字, --symbols 的字, 带不带 ASCII)
    (20, 'font_cn_20', syms, True),
    (28, 'font_cn_28', syms, True),
    (48, 'font_cn_48', ''.join(sorted(set(HERO_TEXT))), False),
]
only = sys.argv[1:]
unknown = [n for n in only if n not in [f[1] for f in FONTS]]
if unknown:
    sys.exit(f"没有这个字库：{' '.join(unknown)}（有 {' '.join(f[1] for f in FONTS)}）")
for size, name, chars, ascii_too in FONTS:
    if only and name not in only:
        continue
    out = f"main/ui/fonts/{name}.c"
    cmd = ["npx", "lv_font_conv", "--font", TTF] + (["-r", "0x20-0x7E"] if ascii_too else []) + ["--symbols", chars,
           "--size", str(size), "--bpp", "4", "--format", "lvgl", "--no-compress",
           "--lv-font-name", name, "-o", out]
    print(f"\n>>> 生成 {name} (size={size}，{len(chars)} 字{' + ASCII' if ascii_too else ''}) ...", flush=True)
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        print("FAIL:\n", r.stdout[-800:], r.stderr[-1500:]); sys.exit(1)
    print("OK ->", out)
print("\n全部完成，记得 idf.py build")
