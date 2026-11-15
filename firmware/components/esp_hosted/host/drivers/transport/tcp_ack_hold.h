/* 本工程新增（说明见组件根目录 LOCAL_CHANGES.md「TCP 纯 ACK 合并」）
 *
 * 主机往 WiFi 发的 TCP 纯 ACK 按连接合并：短时间内同一条连接连着来的几个纯 ACK 只发最新的一个。
 *
 * 为什么要有：大量收数据时（投屏 15 Mbps），lwIP 每收两个段回一个纯 ACK，每秒一千多个，
 * 而且是一阵一阵来的（SDIO 一次突发交上来十几二十个段，协议栈连着回十来个）。从机（C6）往 WiFi 发不出去时
 * 只重试几毫秒就悄悄丢包，这一阵纯 ACK 把它的发送缓冲挤满，连同夹在里面的带数据的小段、ping 应答一起丢。
 * 带数据的段丢了要等 TCP 超时重传（两三百毫秒），对端这段时间发不了新数据。
 * 纯 ACK 是累计确认，后一个包含前一个的全部信息，所以中间的可以不发。
 *
 * 这个头文件分两部分：
 * - 判定逻辑（tcp_ack_hold_core.c）：只有表和包头解析，不依赖 FreeRTOS，电脑上能直接编出来做单元测试
 *   （firmware/tools/ackholdtest）。调用方负责加锁。
 * - 接到传输层的部分（tcp_ack_hold.c）：锁、定时器、调发送函数。
 */
#ifndef __TCP_ACK_HOLD_H__
#define __TCP_ACK_HOLD_H__

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 同时记着几条 TCP 连接（收的、发的都记）。投屏一条、看板页一两条、LLM 网关转发时每个请求两条，8 条够用；
 * 不够时把最久没动的那条让出来。被让出来的连接丢了"上一个确认"的记录，它之后的头一个重复确认会被当成
 * 普通纯 ACK 暂存（最多晚一个暂存时间，也可能被随后的包盖掉），对端的快速重传因此最多晚一个重复确认。 */
#define ACK_HOLD_SLOTS       8
/* 能暂存的帧最长多少字节。纯 ACK = 以太网头 14 + IP 头 20 + TCP 头 20 = 54；
 * 留到 60（不含校验的最短以太网帧），协议栈给帧补了零的话也放得下。 */
#define ACK_HOLD_FRAME_MAX   60

/* ──────────────── 判定逻辑 ──────────────── */

struct ack_hold_frame {
	uint8_t len;                        /* 0 = 空 */
	uint8_t data[ACK_HOLD_FRAME_MAX];
};

struct ack_hold_slot {
	bool     used;                      /* 这个槽记着一条连接（本机发过 TCP 包的连接都记，不只是有纯 ACK 的） */
	bool     held;                      /* frame 里有一个还没发的纯 ACK */
	bool     has_last;                  /* last_ack / last_win 有效 */
	uint8_t  key[12];                   /* 源地址 4 + 目的地址 4 + 源端口 2 + 目的端口 2（都是包里的原样字节） */
	uint32_t last_ack;                  /* 这条连接最近一个带 ACK 标志的包的确认号和窗口（暂存着的那个也算） */
	uint16_t last_win;
	uint32_t stamp;                     /* 最近一次用到这个槽时的 clock，挑最久没动的槽用 */
	struct ack_hold_frame frame;
};

struct ack_hold_stats {
	uint32_t merged;                    /* 被同一条连接更新的纯 ACK 盖掉、没有发的 */
	uint32_t superseded;                /* 作废没发的：同一条连接随后发的包确认号更新，或者确认号一样、带着数据 / FIN；
	                                     * 连接复位、重新开始（RST / SYN）时还暂存着的也算在这里 */
	uint32_t early;                     /* 没等到点、提前发出去的（见 ack_hold_core_on_tx） */
	uint32_t sent;                      /* 到点发出去的 */
	uint32_t retried;                   /* 到点时一时发不了（发送队列满 / 没有发送缓冲 / 从机叫停），留着再等一轮的次数 */
	uint32_t dropped;                   /* 发不出去、丢掉的 */
};

struct ack_hold_table {
	struct ack_hold_slot  slot[ACK_HOLD_SLOTS];
	uint32_t              clock;
	struct ack_hold_stats st;
};

/* 一帧（以太网帧）交给传输层之前调。返回 true = 这一帧暂存下来了，调用方不要发；false = 照常发。
 * hold_enabled 为假时不暂存新的，但照样记账（已经暂存着的该作废的作废）。
 * *early：返回时 len != 0 的话，是之前暂存的一个纯 ACK，调用方要在这一帧之前把它先发出去。有这几种情况：
 *   - 这一帧是重复确认（和这条连接上一个确认的确认号、窗口都一样的纯 ACK）。重复确认的个数对端要数
 *     （数到 3 个触发快速重传），所以不合并：暂存着的那个先发，这一个照常发。
 *   - 表满了，新连接要占最久没动的槽，那个槽里正好还暂存着一个。
 *   - 同一条连接来了一个没有载荷、确认号和暂存的一样的带选项确认（丢包时带 SACK 块的重复确认）：
 *     对端要先看到暂存的那个，这一个才算重复确认。
 *   - 同一条连接来了一个确认号比暂存的还旧的包（协议栈不会这样发，保底）。 */
bool ack_hold_core_on_tx(struct ack_hold_table *t, const uint8_t *frame, size_t len,
		bool hold_enabled, struct ack_hold_frame *early);

/* 到点时发一个暂存的纯 ACK 的结果 */
enum ack_hold_sent {
	ACK_HOLD_SENT,                      /* 发出去了 */
	ACK_HOLD_RETRY,                     /* 一时发不了，留着下一轮再发 */
	ACK_HOLD_DROP,                      /* 发不了（链路不通），丢掉 */
};
typedef enum ack_hold_sent (*ack_hold_flush_fn)(const uint8_t *frame, size_t len, void *arg);

/* 到点了：暂存着的逐个交给 send。发出去的（和发不了要丢的）从槽里拿掉，一时发不了的留在槽里。
 * 返回还留着几个（不为 0 的话调用方要让定时器再响一次）。
 * 调用方拿着锁调（send 也就在锁里执行，所以 send 不能等）：这样"取出来"和"发出去"之间，
 * 同一条连接不会有别的包插到它前面。 */
int ack_hold_core_flush(struct ack_hold_table *t, ack_hold_flush_fn send, void *arg);

/* ──────────────── 接到传输层 ──────────────── */

/* 把一帧交给传输层发出去。wait = 发送队列满的时候可不可以等。
 * 返回 ESP_OK = 交出去了；ESP_ERR_TIMEOUT / ESP_ERR_NO_MEM = 一时发不了，过一会儿可以再试；其它 = 发不了。 */
typedef int (*tcp_ack_hold_send_fn)(const uint8_t *frame, size_t len, bool wait);

/* 开机调一次。建不出定时器的话这个功能不起作用，所有帧照常发。 */
void tcp_ack_hold_init(tcp_ack_hold_send_fn send);

/* STA 的一帧交给传输层之前调（调用方：协议栈线程）。返回 true = 暂存了，不要发。
 * 可能先用 send(…, wait = true) 把之前暂存的一个发出去。 */
bool tcp_ack_hold_on_tx(const uint8_t *frame, size_t len);

/* 一个纯 ACK 最多暂存多久（微秒）。0 = 不合并。不落盘，调试对比用。 */
void     tcp_ack_hold_set_us(uint32_t us);
uint32_t tcp_ack_hold_get_us(void);

void tcp_ack_hold_get_stats(struct ack_hold_stats *st);

#ifdef __cplusplus
}
#endif

#endif
