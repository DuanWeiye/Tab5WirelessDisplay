#!/usr/bin/env python3
"""分析无线显示的卡顿：找出 Tab5 上画面的停顿，说明每一次卡在哪一段。
读 wd_stutter.sh 跑出来的三份日志：wdsend -v 的逐帧记录（*_send.log）、场景的记录（*_scene.log）、Tab5 串口（*_serial.log，里面有 wd gaps 的输出）。

用法：wd_stutter.py <send.log> [--scene <scene.log>] [--serial <serial.log>] [--min-ms 80] [--list 40]

怎么算：
  画面有没有停顿，以 Tab5 自己量的为准（串口日志里 wd gaps 的输出）：相邻两帧换上屏幕的时刻差，不短于 80 ms 的逐个带着帧序号。
  发送端这一头只知道每帧的确认什么时候到。确认在回来的路上被耽搁时（Tab5 → 电脑方向丢包），后面几帧的确认会被压住、一起到，
  按「发出 + 发出到 ACK」算出来的间隔就很大，而 Tab5 上那几帧其实是按时上屏的——所以这个量单列成「电脑收到确认的间隔」，不当成画面的停顿。
  没有 wd gaps 的输出时（老固件 / 没给 --serial）只有后者，只能当参考：多数时候比画面的停顿大，前一帧的确认迟到、和这一帧的挤在一起到时又会偏小。

  每一次停顿（Tab5 上相邻两帧上屏隔了 --min-ms 以上，最小 80）按帧序号对到发送端的逐帧记录，拆成：
    发送端这一头：这一帧比上一帧晚发了多少 = 等确认（流量控制，等 Tab5 的 ACK）+ 等画面（等屏幕变化 + 抓屏的缩放读回）+ 编码
                  + 写连接（上一帧往连接里写花的时间：连接的发送缓冲满了就堵在这里）；
    等画面的部分再对着「抓」的记录看：系统合成两幅画面隔了多久（场景 / 游戏没出新画面，或者系统的合成停了一下）、缩放读回花了多久（显卡忙）、
                  取到的时候这一幅已经出来多久（发送端没及时去取）；
    Tab5 这一头：Tab5 自己报的接收 + 上屏时间比上一帧多了多少（接收 = 第一个条带到 PRESENT，数据来得慢会拉长它；
                  解码和接收同时进行，PRESENT 到了还没解完的那一点等在「上屏」里，所以解码时间不另外加，只列出来参考）；
    其余的算在下行的路上（这一帧的第一个条带比上一帧的晚到了多少：电脑的发送缓冲、空中、C6、SDIO、lwIP 都在里面，分不开）。
  发送端的四项平时就不是 0（写连接中位约 20 ms），挑「卡在哪一段」时比的是各项比平时（这一段的中位数）多出来的部分。
"""
import argparse
import re
import statistics
import sys

FRAME = re.compile(r'^帧 (\d+)：(\d+) 个条带 ([\d.]+) KB，编码 ([\d.]+) ms，发出到 ACK ([\d.]+) ms（Tab5：接收 ([\d.]+) 解码 ([\d.]+) 上屏 ([\d.]+) ms）；'
                   r'发于 @([\d.]+)，离上一帧 ([\d.]+) ms（等确认 ([\d.]+) 等画面 ([\d.]+)）')
GRAB = re.compile(r'^抓 @([\d.]+)：缩放读回 ([\d.]+) ms，取到时这一幅已经出来 (-?[\d.]+) ms，离上一幅的合成 (-?[\d.]+) ms，中间没读的 (\d+) 幅')
HITCH = re.compile(r'^场景卡 @([\d.]+)：([\d.]+) ms')
SCENE = re.compile(r'^场景 \d+×\d+：@([\d.]+) 到 @([\d.]+)，')
GAPS = re.compile(r'wd gaps：量了 (\d+) 个上屏间隔；不短于 50 / 80 / 100 / 150 / 200 / 300 / 500 ms 的各 (\d+) / (\d+) / (\d+) / (\d+) / (\d+) / (\d+) / (\d+) 次；'
                  r'最长 ([\d.]+) ms')
GAPS_KEPT = re.compile(r'wd gaps：不短于 80 ms 的一共 (\d+) 次，只留着最后 (\d+) 次')
GAPS_BIG = re.compile(r'wd gaps：大间隔((?: \d+:\d+)+)')
THRESHOLDS = (50, 80, 100, 150, 200, 300, 500)


def read_tab5_gaps(path):
    """串口日志里最后一次 wd gaps 的输出 → dict(n, ge={门限: 次数}, max, big=[(帧序号, 毫秒)], complete)；没有就返回 None。"""
    last = None
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if m := GAPS.search(line):
                last = dict(n=int(m[1]), ge=dict(zip(THRESHOLDS, (int(x) for x in m.groups()[1:8]))), max=float(m[9]), big=[], complete=True)
            elif last is not None and GAPS_KEPT.search(line):
                last['complete'] = False
            elif last is not None and (m := GAPS_BIG.search(line)):
                last['big'] += [tuple(int(x) for x in item.split(':')) for item in m[1].split()]
    return last


def pct(values, q):
    v = sorted(values)
    return v[min(len(v) - 1, int(len(v) * q))]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('send')
    ap.add_argument('--scene')
    ap.add_argument('--serial')
    ap.add_argument('--min-ms', type=float, default=80, help='Tab5 上相邻两帧上屏隔了这么多毫秒以上算一次停顿（默认 80，也是最小值：Tab5 只逐个记 80 以上的）')
    ap.add_argument('--list', type=int, default=40, help='最多列出多少次停顿的明细（默认 40）')
    a = ap.parse_args()

    frames, grabs, tail = [], [], []
    with open(a.send, encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.rstrip('\r\n')
            if m := FRAME.match(line):
                seq, strips = int(m[1]), int(m[2])
                kb, enc, rtt, recv, dec, show, sent, gap, wait, fetch = (float(x) for x in m.groups()[2:])
                # acked = 电脑收到这一帧的确认的时刻；write = 上一帧往连接里写花的时间（两帧的间隔里另外三项之外的部分）
                frames.append(dict(seq=seq, strips=strips, kb=kb, enc=enc, rtt=rtt, recv=recv, dec=dec, show=show, sent=sent, gap=gap, wait=wait,
                                   fetch=fetch, write=max(0.0, gap - wait - fetch - enc), acked=sent + rtt))
            elif m := GRAB.match(line):
                grabs.append(dict(t=float(m[1]), scale=float(m[2]), age=float(m[3]), compose=float(m[4]), skipped=int(m[5])))
            elif not line.startswith(('  ←', '  →')):
                tail.append(line)
    if len(frames) < 10:
        print('\n'.join(tail[-15:]))
        sys.exit(f'{a.send}：只有 {len(frames)} 行逐帧记录，分析不了（wdsend 要带 -v）')
    frames.sort(key=lambda fr: fr['seq'])      # 行是收到 ACK 时打的，一般就是按序号的

    hitches, scene_span = [], None
    if a.scene:
        with open(a.scene, encoding='utf-8', errors='replace') as f:
            for line in f:
                if m := HITCH.match(line):
                    hitches.append((float(m[1]), float(m[2])))
                elif m := SCENE.match(line):
                    scene_span = (float(m[1]), float(m[2]))
                if line.startswith(('场景', 'rc=', '中途')) and not HITCH.match(line):
                    print(line.rstrip())
    # 只看场景开着的那一段（发送端先起来、场景后关时两头各有一小段不是场景的画面）；开头 2 秒不算（连接刚建、整屏第一帧）
    t0 = frames[0]['sent'] + 2000
    t1 = frames[-1]['acked']
    if scene_span:
        t0, t1 = max(t0, scene_span[0] + 500), min(t1, scene_span[1] - 500)
    use = [fr for fr in frames if t0 <= fr['sent'] and fr['acked'] <= t1]
    if len(use) < 10:
        sys.exit('场景开着的那一段里没有几帧：场景没开起来，或者发送端没连上')

    span = (use[-1]['sent'] - use[0]['sent']) / 1000
    by_seq = {fr['seq']: fr for fr in use}
    print(f"== 发送端逐帧记录：{len(use)} 帧，{span:.1f} 秒，{(len(use) - 1) / span:.1f} 帧/秒；每帧中位 {statistics.median(fr['kb'] for fr in use):.1f} KB、"
          f"{statistics.median(fr['strips'] for fr in use):.0f} 个条带，{sum(fr['kb'] for fr in use) * 8 / 1000 / span:.1f} Mbps")

    # 画面的间隔：Tab5 自己量的
    min_ms = max(a.min_ms, 80)
    tab5 = read_tab5_gaps(a.serial) if a.serial else None
    stalls = None
    if tab5:
        print(f"Tab5 上相邻两帧上屏的间隔（Tab5 自己量的，整条连接 {tab5['n']} 个）："
              + '，'.join(f"≥{th} ms {n} 次" for th, n in tab5['ge'].items()) + f"；最长 {tab5['max']:.1f} ms")
        if not tab5['complete']:
            print(f"  ≥80 ms 的太多，Tab5 只留着最后 {len(tab5['big'])} 次的明细，下面的停顿不全")
        # 落在分析这一段里的（上一帧也要在这一段里：间隔是相对上一帧说的）
        inwin = [(seq, ms) for seq, ms in tab5['big'] if seq in by_seq and seq - 1 in by_seq]
        stalls = [(seq, ms) for seq, ms in inwin if ms >= min_ms]       # --min-ms 只管下面列哪些，不影响这里各门限的次数
        print(f"  其中在分析的这一段里：" + '，'.join(f"≥{th} ms {sum(ms >= th for _, ms in inwin)} 次" for th in THRESHOLDS if th >= 80)
              + (f"；最长 {max(ms for _, ms in inwin)} ms" if inwin else ''))
    else:
        print('没有 Tab5 自己量的上屏间隔（串口日志里没有 wd gaps 的输出）：下面「电脑收到确认的间隔」只能当参考（多数时候偏大，也可能偏小）')

    # 电脑收到确认的间隔：确认迟到的指标，不是画面
    agaps = [b['acked'] - x['acked'] for x, b in zip(use, use[1:])]
    print(f"电脑收到确认的间隔（确认迟到时几帧的确认一起到，不等于画面停了）：中位 {statistics.median(agaps):.1f} / 95% {pct(agaps, 0.95):.1f} / "
          f"99% {pct(agaps, 0.99):.1f} / 最大 {max(agaps):.1f} ms；" + '，'.join(f"≥{th} ms {sum(g >= th for g in agaps)} 次" for th in THRESHOLDS))
    for name, key in (('等确认', 'wait'), ('等画面', 'fetch'), ('编码', 'enc'), ('写连接', 'write'), ('发出到 ACK', 'rtt'), ('Tab5 接收', 'recv'),
                      ('Tab5 解码', 'dec'), ('Tab5 上屏', 'show')):
        v = [fr[key] for fr in use]
        print(f"  {name}：中位 {statistics.median(v):.1f} / 95% {pct(v, 0.95):.1f} / 99% {pct(v, 0.99):.1f} / 最大 {max(v):.1f} ms")
    if grabs:
        g = [x for x in grabs if t0 <= x['t'] <= t1]
        if g:
            print(f"  抓屏 {len(g)} 幅：缩放读回 中位 {statistics.median(x['scale'] for x in g):.1f} / 99% {pct([x['scale'] for x in g], 0.99):.1f} / "
                  f"最大 {max(x['scale'] for x in g):.1f} ms；系统合成的间隔 中位 {statistics.median(x['compose'] for x in g):.1f} / "
                  f"99% {pct([x['compose'] for x in g], 0.99):.1f} / 最大 {max(x['compose'] for x in g):.1f} ms；没读就被顶掉的 {sum(x['skipped'] for x in g)} 幅")

    # 停顿：Tab5 上屏的间隔，按帧序号对到发送端的记录，看多出来的时间在哪一段
    if stalls is not None:
        normal = statistics.median(fr['gap'] for fr in use)             # 平时两帧隔多久发
        SENDER = {'等确认': 'wait', '等画面': 'fetch', '编码': 'enc', '写连接': 'write'}
        base = {name: statistics.median(fr[key] for fr in use) for name, key in SENDER.items()}     # 发送端各项平时是多少
        out = []
        for seq, ms in stalls:
            fr, prev = by_seq[seq], by_seq[seq - 1]
            # Tab5 这一帧比上一帧多花的。解码和接收同时进行、没解完的等在「上屏」里（见开头的说明），不另外加
            dev = (fr['recv'] + fr['show']) - (prev['recv'] + prev['show'])
            road = ms - fr['gap'] - dev                                 # 剩下的：这一帧的第一个条带比上一帧的晚到了多少（下行的路上）
            grab = max((x for x in grabs if x['t'] <= fr['sent'] + 0.5), key=lambda x: x['t'], default=None)
            if grab is not None and grab['t'] < prev['sent']:
                grab = None                                             # 这一帧发的是上一帧之前就取到的画面（不应该有）
            hit = [h for h in hitches if prev['sent'] - 60 <= h[0] <= fr['sent'] + 20]
            # 都比「比平时多出来的」：发送端四项减去各自的中位数，Tab5 和路上本来就是相对上一帧的差
            parts = {name: fr[key] - base[name] for name, key in SENDER.items()}
            parts.update({'Tab5': dev, '下行的路上': road})
            top = max(parts, key=parts.get)
            if top == '等画面':
                if hit:
                    kind, why = '场景自己卡了', f"场景自己卡了 {max(h[1] for h in hit):.0f} ms"
                elif grab and grab['scale'] >= 0.4 * fr['fetch']:
                    kind, why = '缩放读回慢（显卡忙）', f"缩放读回 {grab['scale']:.0f} ms（显卡忙）"
                elif grab and grab['compose'] >= 0.6 * fr['fetch']:
                    kind, why = '系统没及时合成出下一幅画面', f"系统隔了 {grab['compose']:.0f} ms 才合成出下一幅画面"
                elif grab and grab['age'] >= 0.4 * fr['fetch']:
                    kind, why = '画面出来以后没及时取', f"画面出来 {grab['age']:.0f} ms 后才取到"
                else:
                    kind = why = '等画面（原因不明）'
            elif top == '等确认':
                # 流量控制在等的是前面在途那几帧的 ACK：看上一帧
                pdev = prev['recv'] + prev['show']
                kind = '等 Tab5 的确认（确认迟到，在途帧数到了上限）'
                why = f"等 Tab5 确认（上一帧发出到 ACK {prev['rtt']:.0f} ms，其中 Tab5 接收 + 上屏 {pdev:.0f} ms）"
            elif top == '编码':
                kind = why = '编码慢'
            elif top == '写连接':
                kind = '上一帧往连接里写堵住了（发送缓冲满）'
                why = f"上一帧往连接里写花了 {fr['write']:.0f} ms"
            elif top == 'Tab5':
                kind = 'Tab5 接收 / 上屏慢'
                why = f"Tab5 这一帧多花了 {dev:.0f} ms（接收 {fr['recv']:.0f} 解码 {fr['dec']:.0f} 上屏 {fr['show']:.0f}）"
            else:
                kind, why = '下行的路上（电脑的发送缓冲 / 空中 / C6 / SDIO / lwIP，没分开）', f"这一帧在路上多花了 {road:.0f} ms"
            out.append(dict(t=fr['sent'], ms=ms, fr=fr, grab=grab, kind=kind, why=why, dev=dev, road=road))

        print(f"\n== 停顿（Tab5 上相邻两帧上屏隔了 {min_ms:g} ms 以上，平时隔 {normal:.0f} ms）：{len(out)} 次，平均每 {span / len(out):.1f} 秒一次" if out
              else f"\n== Tab5 上没有 {min_ms:g} ms 以上的停顿")
        if out:
            kinds = {}
            for o in out:
                kinds.setdefault(o['kind'], []).append(o['ms'])
            for k, v in sorted(kinds.items(), key=lambda kv: -len(kv[1])):
                print(f"  {len(v):4d} 次  {k}（间隔中位 {statistics.median(v):.0f} / 最大 {max(v):.0f} ms）")
            between = [b['t'] - x['t'] for x, b in zip(out, out[1:])]
            if len(between) >= 3:
                print(f"  相邻两次停顿隔：中位 {statistics.median(between) / 1000:.2f} 秒，最短 {min(between) / 1000:.2f}，最长 {max(between) / 1000:.2f}")
            print(f"  明细（时刻是分析的这一段开始起的秒数；最多列 {a.list} 次，按间隔从大到小）：")
            for o in sorted(out, key=lambda o: -o['ms'])[:a.list]:
                fr, g = o['fr'], o['grab']
                grab = f"；抓：缩放 {g['scale']:.0f} 合成间隔 {g['compose']:.0f} 年龄 {g['age']:.0f} 顶掉 {g['skipped']}" if g else ''
                print(f"    {(o['t'] - use[0]['sent']) / 1000:7.2f} 秒  帧 {fr['seq']:6d}  上屏间隔 {o['ms']:4d} ms = 晚发 {fr['gap']:.0f}"
                      f"（等确认 {fr['wait']:.0f} 等画面 {fr['fetch']:.0f} 编码 {fr['enc']:.0f} 写连接 {fr['write']:.0f}）"
                      f" + Tab5 {o['dev']:+.0f} + 路上 {o['road']:+.0f}{grab}  → {o['why']}")

    print('\n== 发送端最后的汇总')
    print('\n'.join(line for line in tail if line and not line.startswith(('[windesk]', '认证通过'))) or '（没有）')


if __name__ == '__main__':
    main()
