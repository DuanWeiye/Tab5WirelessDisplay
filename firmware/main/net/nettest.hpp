// 网络收包自测（调试台 netrx 命令用）：量 Tab5 这一侧实际收到多少数据。
// 配套的发送端在 tools/dispbench/nettx.py（跑在局域网里的另一台机器上）。
#pragma once
#include <cstdint>

namespace nettest {

// TCP：连到 ip:port，对端一直发、本机只收；收 secs 秒后断开，打印每秒吞吐和总吞吐。
//      limit_kbytes > 0 时每秒最多读这么多 KB，模拟「应用读得比网络慢」（软件解 TLS、解码跟不上）：
//      这时接收窗口会被填满，用来看攒在协议栈里的数据占了多少内部内存。0 = 尽快读。
// UDP：在本机 port 上收；收 secs 秒，按包头里的序号统计丢包率。
// 两者都丢到常驻网络任务上跑（期间别的网络作业排队）。返回是否成功投递。
bool start_tcp_rx(const char* ip, uint16_t port, int secs, int limit_kbytes = 0);
bool start_udp_rx(uint16_t port, int secs);

}  // namespace nettest
