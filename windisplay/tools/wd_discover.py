#!/usr/bin/env python3
"""广播自动发现的询问，打印哪些 Tab5 应答了、各用了多久（协议见 ../PROTOCOL.md「自动发现」）。

发送端发出询问后只收 300 毫秒的应答，所以这里把「300 毫秒之内应答了几次」单独数出来：
Tab5 等连接时 WiFi 还在省电的话，广播要等信标才送到它，应答会晚。

用法举例：
  wd_discover.py                  # 问 10 次，每 3 秒一次（发送端平时的节奏）
  wd_discover.py -n 40 -i 1       # 问 40 次，每秒一次
"""
import argparse
import os
import socket
import statistics
import time

import wdcrypto as wc
import wdproto as wp

WINDOW_MS = 300     # 发送端收应答的时间


def main():
    ap = argparse.ArgumentParser(description='Tab5 无线显示：自动发现的应答有多快', formatter_class=argparse.RawDescriptionHelpFormatter,
                                 epilog=__doc__)
    ap.add_argument('-n', '--count', type=int, default=10, help='问多少次（默认 10）')
    ap.add_argument('-i', '--interval', type=float, default=3.0, help='两次询问隔多少秒（默认 3，至少 1）')
    ap.add_argument('--broadcast', default='255.255.255.255', help='往哪个广播地址问（默认 255.255.255.255）')
    ap.add_argument('--port', type=int, default=wp.PORT)
    a = ap.parse_args()
    if a.count < 1 or a.interval < 1:
        ap.error('-n 至少 1，-i 至少 1 秒')

    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    seen = {}       # (地址, TCP 端口, 编号) -> [每次应答用的毫秒数]
    for k in range(a.count):
        nonce = os.urandom(8)
        t0 = time.monotonic()
        s.sendto(wp.DISC.pack(wp.DISC_MAGIC, wp.PROTO_VER, wp.DISC_QUERY, 0, bytes(wc.ID_LEN), nonce), (a.broadcast, a.port))
        got = []
        while (left := t0 + a.interval - time.monotonic()) > 0:     # 一直收到下一次询问之前：晚到的应答也量得到
            s.settimeout(left)
            try:
                data, addr = s.recvfrom(256)
            except socket.timeout:
                break
            ms = (time.monotonic() - t0) * 1000
            if len(data) != wp.DISC.size:
                continue
            magic, ver, kind, tcp_port, peer_id, echo = wp.DISC.unpack(data)
            if (magic, ver, kind, echo) != (wp.DISC_MAGIC, wp.PROTO_VER, wp.DISC_REPLY, nonce) or not tcp_port:
                continue
            seen.setdefault((addr[0], tcp_port, peer_id), []).append(ms)
            got.append(f'{addr[0]}:{tcp_port}（编号 {peer_id[:4].hex()}…）{ms:.0f} ms')
        print(f'第 {k + 1} 次：' + ('；'.join(got) if got else '没有应答'), flush=True)
    s.close()

    for (ip, tcp_port, peer_id), ms in sorted(seen.items()):
        fast = sum(1 for m in ms if m <= WINDOW_MS)
        print(f'{ip}:{tcp_port}（编号 {peer_id[:4].hex()}…）：{a.count} 次询问应答了 {len(ms)} 次，其中 {fast} 次在 {WINDOW_MS} 毫秒之内；'
              f'用时 最短 {min(ms):.0f} / 中位 {statistics.median(ms):.0f} / 最长 {max(ms):.0f} ms')
    if not seen:
        print('一个应答都没有（Tab5 要开着「无线显示」页、而且没有连接时才应答）')
    return 0 if seen else 1


if __name__ == '__main__':
    raise SystemExit(main())
