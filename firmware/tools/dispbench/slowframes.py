#!/usr/bin/env python3
"""从 wd_send_test.py -v 的输出里挑出「发出到 ACK」超过阈值的帧，连同末尾的汇总一起打印。

  slowframes.py <日志>... [--ms 150]

整屏滚动的长跑日志每帧一行、一份 300 KB，入库只留这个脚本的输出（慢帧 + 汇总）。
"""
import re
import sys

args = sys.argv[1:]
limit = 150.0
if '--ms' in args:
    i = args.index('--ms')
    limit = float(args[i + 1])
    del args[i:i + 2]
pat = re.compile(r'帧 (\d+)：.*发出到 ACK ([\d.]+) ms（Tab5：接收 ([\d.]+) 解码 ([\d.]+) 上屏 ([\d.]+)')
for path in args:
    frames, slow, tail = 0, [], []
    for line in open(path, errors='replace'):
        m = pat.match(line)
        if m:
            frames += 1
            if float(m.group(2)) > limit:
                slow.append(line.rstrip())
        elif not line.startswith('HELLO'):
            tail.append(line.rstrip())
    print(f'== {path}：{frames} 帧，发出到 ACK > {limit:g} ms 的 {len(slow)} 帧')
    for s in slow:
        print('  ' + s)
    for t in tail:
        print('  ' + t)
