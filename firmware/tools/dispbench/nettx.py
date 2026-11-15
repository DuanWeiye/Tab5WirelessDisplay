#!/usr/bin/env python3
"""给 Tab5 发测试流量（配套设备侧调试命令 netrx，见 main/net/nettest.cpp）。

  nettx.py tcp-serve <本机IP> <端口>
      在本机监听；Tab5 连上来以后一直给它发数据，直到它断开。可以连续接多次，Ctrl-C 结束。
      设备侧：netrx tcp <本机IP> <端口> <秒数>

  nettx.py udp <设备IP> <端口> <Mbps> <秒数> [包长=1400]
      按给定速率往设备发 UDP 包（没有流控，专门用来压 WiFi-SDIO 的突发承受能力）。
      每个包开头 4 字节是大端序号，设备侧据此统计丢包。
      设备侧要先开始收：netrx udp <端口> <秒数>
"""
import socket
import sys
import time


def tcp_serve(bind_ip: str, port: int) -> None:
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((bind_ip, port))
    srv.listen(1)
    print(f"监听 {bind_ip}:{port}", flush=True)
    chunk = bytes(64 * 1024)
    while True:
        conn, peer = srv.accept()
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        t0 = time.monotonic()
        sent = 0
        try:
            while True:
                sent += conn.send(chunk)
        except OSError:
            pass   # 对端断开（管道破裂、连接被重置、超时等）：这一次结束，继续等下一个连接
        finally:
            conn.close()
        dt = max(time.monotonic() - t0, 1e-6)   # 对端一连上就断开时间隔可能是 0
        print(f"{peer[0]} 断开：{dt:.1f} 秒发出 {sent} 字节，{sent * 8 / dt / 1e6:.2f} Mbps", flush=True)


def udp_blast(dst_ip: str, port: int, mbps: float, secs: float, size: int) -> None:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    payload = bytearray(size)
    interval = size * 8 / (mbps * 1e6)   # 每个包之间的间隔（秒）
    t0 = time.monotonic()
    seq = 0
    while True:
        now = time.monotonic()
        if now - t0 >= secs:
            break
        # 按时间表发：落后了就连着补发，不累积误差
        due = int((now - t0) / interval) + 1
        while seq < due:
            payload[0:4] = seq.to_bytes(4, "big")
            sock.sendto(payload, (dst_ip, port))
            seq += 1
        time.sleep(min(interval, 0.0005))
    dt = time.monotonic() - t0
    print(f"发出 {seq} 包，{seq * size * 8 / dt / 1e6:.2f} Mbps，{seq / dt:.0f} 包/秒", flush=True)


def main() -> int:
    a = sys.argv[1:]
    if len(a) == 3 and a[0] == "tcp-serve":
        tcp_serve(a[1], int(a[2]))
        return 0
    if len(a) in (5, 6) and a[0] == "udp":
        udp_blast(a[1], int(a[2]), float(a[3]), float(a[4]), int(a[5]) if len(a) == 6 else 1400)
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
