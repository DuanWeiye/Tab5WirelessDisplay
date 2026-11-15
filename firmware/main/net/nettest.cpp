// 网络收包自测实现（见 nettest.hpp）
#include "nettest.hpp"
#include "worker.hpp"

#include <cstdio>
#include <cstring>
#include <esp_log.h>
#include <esp_timer.h>
#include <esp_heap_caps.h>
#include <lwip/sockets.h>
#include <lwip/inet.h>
#include <esp_hosted_bufstat.h>

static const char* TAG = "nettest";

namespace nettest {

struct Job {
    bool     udp;
    char     ip[20];
    uint16_t port;
    int      secs;
    int      limit_kbytes;   // 仅 TCP：每秒最多读多少 KB，0 = 不限
};

static const size_t RXBUF = 16 * 1024;

// 内部 DMA 内存的水位（esp-hosted 的 SDIO 缓冲就在这里面）：当前空闲 / 历史最低 / 最大连续块
static void log_dma_pool(const char* when)
{
    const uint32_t caps = MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA;
    ESP_LOGW(TAG, "%s 内部DMA内存：空闲 %u 历史最低 %u 最大连续块 %u",
             when, (unsigned)heap_caps_get_free_size(caps),
             (unsigned)heap_caps_get_minimum_free_size(caps),
             (unsigned)heap_caps_get_largest_free_block(caps));
}

static void run_tcp(const Job& j, uint8_t* buf)
{
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) { ESP_LOGE(TAG, "socket 失败 errno=%d", errno); return; }
    struct timeval tv = {2, 0};   // 收不到数据最多等 2 秒，免得对端不发时卡死
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    struct sockaddr_in sa = {};
    sa.sin_family = AF_INET;
    sa.sin_port   = htons(j.port);
    sa.sin_addr.s_addr = inet_addr(j.ip);
    if (connect(fd, (struct sockaddr*)&sa, sizeof(sa)) != 0) {
        ESP_LOGE(TAG, "连不上 %s:%u errno=%d", j.ip, j.port, errno);
        close(fd);
        return;
    }
    if (j.limit_kbytes > 0)
        ESP_LOGW(TAG, "TCP 收包开始：%s:%u，%d 秒，限速每秒读 %d KB", j.ip, j.port, j.secs, j.limit_kbytes);
    else
        ESP_LOGW(TAG, "TCP 收包开始：%s:%u，%d 秒", j.ip, j.port, j.secs);
    const int64_t t0 = esp_timer_get_time();
    int64_t  t_sec = t0;        // 这一秒从什么时候开始
    uint64_t total = 0, in_sec = 0;
    int      sec_no = 0;
    for (;;) {
        const int64_t now = esp_timer_get_time();
        if (now - t_sec >= 1000000) {
            sec_no++;
            ESP_LOGW(TAG, "  第 %d 秒：%.2f Mbps，内部内存空闲 %u", sec_no, in_sec * 8.0 / ((now - t_sec) / 1e6) / 1e6,
                     (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));
            t_sec = now;
            in_sec = 0;
        }
        if (now - t0 >= (int64_t)j.secs * 1000000) break;
        // 限速：读到的总量超过「速率 × 已过时间」就先不读，让数据攒在协议栈里
        if (j.limit_kbytes > 0 && total > (uint64_t)j.limit_kbytes * 1024 * (uint64_t)(now - t0) / 1000000) {
            vTaskDelay(pdMS_TO_TICKS(10));
            continue;
        }
        int n = recv(fd, buf, RXBUF, 0);
        if (n > 0) { total += n; in_sec += n; continue; }
        if (n == 0) { ESP_LOGW(TAG, "对端关闭了连接"); break; }
        if (errno == EAGAIN || errno == EWOULDBLOCK) continue;   // 2 秒没数据，回去看是否到时
        ESP_LOGE(TAG, "recv 出错 errno=%d", errno);
        break;
    }
    const double dt = (esp_timer_get_time() - t0) / 1e6;
    close(fd);
    ESP_LOGW(TAG, "TCP 收包结束：%.1f 秒收到 %llu 字节，平均 %.2f Mbps",
             dt, (unsigned long long)total, dt > 0 ? total * 8.0 / dt / 1e6 : 0.0);
}

static void run_udp(const Job& j, uint8_t* buf)
{
    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) { ESP_LOGE(TAG, "socket 失败 errno=%d", errno); return; }
    struct timeval tv = {1, 0};
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    struct sockaddr_in sa = {};
    sa.sin_family = AF_INET;
    sa.sin_port   = htons(j.port);
    sa.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(fd, (struct sockaddr*)&sa, sizeof(sa)) != 0) {
        ESP_LOGE(TAG, "bind %u 失败 errno=%d", j.port, errno);
        close(fd);
        return;
    }
    ESP_LOGW(TAG, "UDP 收包开始：本机端口 %u，%d 秒", j.port, j.secs);
    const int64_t t0 = esp_timer_get_time();
    uint64_t bytes = 0;
    uint32_t pkts = 0, seq_min = 0, seq_max = 0;
    while (esp_timer_get_time() - t0 < (int64_t)j.secs * 1000000) {
        int n = recvfrom(fd, buf, RXBUF, 0, nullptr, nullptr);
        if (n < 4) continue;    // 超时，或不是 nettx.py 发的包（包头 4 字节是大端序号）
        const uint32_t seq = ((uint32_t)buf[0] << 24) | ((uint32_t)buf[1] << 16) | ((uint32_t)buf[2] << 8) | buf[3];
        if (pkts == 0) { seq_min = seq_max = seq; }
        else { if (seq < seq_min) seq_min = seq; if (seq > seq_max) seq_max = seq; }
        pkts++;
        bytes += n;
    }
    const double dt = (esp_timer_get_time() - t0) / 1e6;
    close(fd);
    const uint32_t sent = pkts ? (seq_max - seq_min + 1) : 0;   // 首尾序号之间对端发了多少个
    // 收到的可能比这个数多（网络重复投递，或者中途重启了发送端、序号从头再来）：这时丢包数按 0 算
    const uint32_t lost = sent > pkts ? sent - pkts : 0;
    ESP_LOGW(TAG, "UDP 收包结束：%.1f 秒收到 %u 包 %llu 字节，平均 %.2f Mbps；首尾序号间对端发了 %u 包，丢 %.1f%%",
             dt, (unsigned)pkts, (unsigned long long)bytes, dt > 0 ? bytes * 8.0 / dt / 1e6 : 0.0,
             (unsigned)sent, sent ? lost * 100.0 / sent : 0.0);
}

static void job(void* arg)
{
    Job* j = (Job*)arg;
    // 收包缓冲放 PSRAM：不和 WiFi-SDIO 抢内部内存
    uint8_t* buf = (uint8_t*)heap_caps_malloc(RXBUF, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (!buf) {
        ESP_LOGE(TAG, "收包缓冲分配失败");
    } else {
        log_dma_pool("开始前");
        if (j->udp) run_udp(*j, buf);
        else        run_tcp(*j, buf);
        log_dma_pool("结束后");
        esp_hosted_log_buf_stats();
        heap_caps_free(buf);
    }
    delete j;
}

static bool submit(Job* j)
{
    if (netw::submit(job, j)) return true;
    ESP_LOGE(TAG, "网络作业队列满或未启动");
    delete j;
    return false;
}

bool start_tcp_rx(const char* ip, uint16_t port, int secs, int limit_kbytes)
{
    Job* j = new Job{};
    j->udp = false;
    snprintf(j->ip, sizeof(j->ip), "%s", ip);
    j->port = port;
    j->secs = secs;
    j->limit_kbytes = limit_kbytes;
    return submit(j);
}

bool start_udp_rx(uint16_t port, int secs)
{
    Job* j = new Job{};
    j->udp = true;
    j->port = port;
    j->secs = secs;
    return submit(j);
}

}  // namespace nettest
