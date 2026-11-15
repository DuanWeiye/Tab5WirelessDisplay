/* TCP 纯 ACK 合并判定逻辑（components/esp_hosted/host/drivers/transport/tcp_ack_hold_core.c）的单元测试。
 * 在电脑上直接编，不用 ESP-IDF：./run.sh
 *
 * 用的都是照真实情况拼出来的以太网帧（IP 头、TCP 头逐字节填），场景取自投屏时 Tab5 实际发的包：
 * 一阵连着的纯 ACK、夹在中间的应用层确认报文（带数据的小段）、丢包时的重复确认、窗口更新、几条连接同时在用。
 * 最后一段是随机序列 + 对照模型，核对三条总的性质（见 fuzz_once）。 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "tcp_ack_hold.h"

static int g_fail, g_checks;
#define CHECK(cond) do { g_checks++; if (!(cond)) { g_fail++; \
	printf("  失败 %s:%d: %s\n", __FILE__, __LINE__, #cond); } } while (0)

#define F_FIN 0x01
#define F_SYN 0x02
#define F_RST 0x04
#define F_PSH 0x08
#define F_ACK 0x10

/* 一条连接：Tab5（本机）→ 对端 */
struct conn {
	uint8_t  src[4], dst[4];
	uint16_t sport, dport;
};

static const struct conn WD   = { {192,168,1,50}, {192,168,1,2},   7250, 51724 };  /* 无线显示：电脑连过来 */
static const struct conn HTTP = { {192,168,1,50}, {203,0,113,20},  53100,   443 };  /* 看板页去拉数据 */
static const struct conn LLM  = { {192,168,1,50}, {192,168,1,10},53101,  1234 };

/* 局域网里别的机器连到 LLM 网关（本机 1234 端口）：第 i 台 */
static struct conn lan_conn(int i)
{
	struct conn c = { {192,168,1,50}, {192,168,1,(uint8_t)(100 + i)}, 1234, (uint16_t)(40000 + i) };
	return c;
}

struct frame {
	uint8_t d[1600];
	size_t  len;
};

/* 拼一帧：以太网 + IPv4（ip_opt 字节的 IP 选项）+ TCP（tcp_opt 字节的 TCP 选项）+ payload 字节载荷 */
static struct frame mk_tcp_ex(const struct conn *c, uint8_t flags, uint32_t seq, uint32_t ack, uint16_t win,
		unsigned payload, unsigned tcp_opt, unsigned ip_opt, unsigned pad)
{
	static uint16_t ip_id;
	struct frame f;
	unsigned ihl = 20 + ip_opt, doff = 20 + tcp_opt, tot = ihl + doff + payload;
	uint8_t *ip = f.d + 14, *tcp = ip + ihl;

	memset(&f, 0, sizeof(f));
	memcpy(f.d, "\x00\x11\x22\x33\x44\x55", 6);
	memcpy(f.d + 6, "\x80\xf1\xb2\xd1\x46\xc3", 6);
	f.d[12] = 0x08; f.d[13] = 0x00;
	ip[0] = (uint8_t)(0x40 | (ihl / 4));
	ip[2] = (uint8_t)(tot >> 8); ip[3] = (uint8_t)tot;
	ip_id++;
	ip[4] = (uint8_t)(ip_id >> 8); ip[5] = (uint8_t)ip_id;
	ip[8] = 64; ip[9] = 6;
	memcpy(ip + 12, c->src, 4);
	memcpy(ip + 16, c->dst, 4);
	tcp[0] = (uint8_t)(c->sport >> 8); tcp[1] = (uint8_t)c->sport;
	tcp[2] = (uint8_t)(c->dport >> 8); tcp[3] = (uint8_t)c->dport;
	tcp[4] = (uint8_t)(seq >> 24); tcp[5] = (uint8_t)(seq >> 16); tcp[6] = (uint8_t)(seq >> 8); tcp[7] = (uint8_t)seq;
	tcp[8] = (uint8_t)(ack >> 24); tcp[9] = (uint8_t)(ack >> 16); tcp[10] = (uint8_t)(ack >> 8); tcp[11] = (uint8_t)ack;
	tcp[12] = (uint8_t)((doff / 4) << 4);
	tcp[13] = flags;
	tcp[14] = (uint8_t)(win >> 8); tcp[15] = (uint8_t)win;
	for (unsigned i = 0; i < payload; i++)
		tcp[doff + i] = (uint8_t)(i * 7 + 1);
	f.len = 14 + tot + pad;
	return f;
}

static struct frame mk_ack(const struct conn *c, uint32_t ack, uint16_t win)
{
	return mk_tcp_ex(c, F_ACK, 1000, ack, win, 0, 0, 0, 0);
}

static struct frame mk_data(const struct conn *c, uint32_t seq, uint32_t ack, uint16_t win, unsigned payload)
{
	return mk_tcp_ex(c, F_ACK | F_PSH, seq, ack, win, payload, 0, 0, 0);
}

/* 带 SACK 块的重复确认：NOP NOP + SACK(一块) = 12 字节选项 */
static struct frame mk_sack(const struct conn *c, uint32_t ack, uint16_t win)
{
	struct frame f = mk_tcp_ex(c, F_ACK, 1000, ack, win, 0, 12, 0, 0);
	uint8_t *o = f.d + 14 + 20 + 20;
	o[0] = 1; o[1] = 1; o[2] = 5; o[3] = 10;
	return f;
}

static uint32_t frame_ack(const uint8_t *d)
{
	const uint8_t *tcp = d + 14 + (d[14] & 15) * 4;
	return ((uint32_t)tcp[8] << 24) | ((uint32_t)tcp[9] << 16) | ((uint32_t)tcp[10] << 8) | tcp[11];
}

static uint16_t frame_win(const uint8_t *d)
{
	const uint8_t *tcp = d + 14 + (d[14] & 15) * 4;
	return (uint16_t)((tcp[14] << 8) | tcp[15]);
}

/* ── 测试台：记下"对端实际收到的包"的先后 ── */
struct wire {
	struct frame f[4096];
	int n;
};

static struct ack_hold_table T;
static struct wire W;

static void reset(void)
{
	memset(&T, 0, sizeof(T));
	W.n = 0;
}

static void wire_put(const uint8_t *d, size_t len)
{
	if (W.n >= (int)(sizeof(W.f) / sizeof(W.f[0]))) {
		printf("测试台：记录满了\n");
		exit(2);
	}
	memcpy(W.f[W.n].d, d, len);
	W.f[W.n].len = len;
	W.n++;
}

/* 协议栈发一帧。返回 true = 被暂存了 */
static bool tx_en(const struct frame *f, bool enabled)
{
	struct ack_hold_frame early;
	bool held = ack_hold_core_on_tx(&T, f->d, f->len, enabled, &early);

	if (early.len)
		wire_put(early.data, early.len);
	if (!held)
		wire_put(f->d, f->len);
	return held;
}

static bool tx(const struct frame *f)
{
	return tx_en(f, true);
}

/* 定时器到点时的发送函数。arg 指向一个"还要失败几次"的计数：不为 0 就报一时发不了 */
static enum ack_hold_sent send_to_wire(const uint8_t *frame, size_t len, void *arg)
{
	int *fail = arg;

	if (fail && *fail > 0) {
		(*fail)--;
		return ACK_HOLD_RETRY;
	}
	wire_put(frame, len);
	return ACK_HOLD_SENT;
}

/* 定时器到点，都发得出去。返回发出去几个 */
static int fire(void)
{
	int before = W.n;

	CHECK(ack_hold_core_flush(&T, send_to_wire, NULL) == 0);
	return W.n - before;
}

static bool same(const struct frame *a, const struct frame *b)
{
	return a->len == b->len && memcmp(a->d, b->d, a->len) == 0;
}

/* ───────────────────────── 场景 ───────────────────────── */

/* 投屏：SDIO 一次突发交上来 20 个段，协议栈每两个段回一个纯 ACK，连着 10 个；窗口一边被填一边被应用读走 */
static void t_burst_merges_to_latest(void)
{
	struct frame last = {0};

	printf("一阵纯 ACK 只发最后一个\n");
	reset();
	for (int i = 1; i <= 10; i++) {
		last = mk_ack(&WD, 5000 + (uint32_t)i * 2880, (uint16_t)(65535 - i * 2880));
		CHECK(tx(&last));
	}
	CHECK(W.n == 0);
	CHECK(fire() == 1);
	CHECK(W.n == 1 && same(&W.f[0], &last));
	CHECK(T.st.merged == 9);
	CHECK(fire() == 0);       /* 发过了，槽里没东西 */

	/* 下一阵接着来：照样合并，不会被上一阵的记录当成重复确认 */
	for (int i = 11; i <= 14; i++) {
		last = mk_ack(&WD, 5000 + (uint32_t)i * 2880, 30000);
		CHECK(tx(&last));
	}
	CHECK(fire() == 1);
	CHECK(W.n == 2 && same(&W.f[1], &last));
}

/* 每帧画面收完，Tab5 回一个应用层的确认报文（带数据的小段），它自己带着最新的确认号：
 * 暂存的纯 ACK 作废，带数据的段立刻发、不延迟 */
static void t_data_segment_supersedes(void)
{
	struct frame a, d;

	printf("带数据的段立刻发，暂存的纯 ACK 作废\n");
	reset();
	a = mk_ack(&WD, 80000, 20000);
	CHECK(tx(&a));
	d = mk_data(&WD, 1000, 80000, 60000, 13);
	CHECK(!tx(&d));
	CHECK(W.n == 1 && same(&W.f[0], &d));
	CHECK(T.st.superseded == 1);
	CHECK(fire() == 0);

	/* 触摸报文（带数据）在没有暂存时也照常发 */
	d = mk_data(&WD, 1013, 80000, 60000, 9);
	CHECK(!tx(&d));
	CHECK(W.n == 2);

	/* 带数据的段之后，确认号和窗口都没变的纯 ACK 对端会算成重复确认：不暂存，原样发 */
	a = mk_ack(&WD, 80000, 60000);
	CHECK(!tx(&a));
	CHECK(W.n == 3 && same(&W.f[2], &a));
}

/* 下行丢了一个段、对端没协商 SACK：协议栈对后面每个乱序到的段回一个一模一样的纯 ACK。
 * 对端数到 3 个重复确认才快速重传，所以一个都不能少，也不能拖 */
static void t_dup_acks_keep_count(void)
{
	struct frame a = mk_ack(&WD, 40000, 30000);

	printf("重复确认个数不变\n");
	reset();
	CHECK(tx(&a));                 /* 正常推进的确认，暂存 */
	CHECK(!tx(&a));                /* 第 1 个重复确认：暂存的先发，它自己紧跟着发 */
	CHECK(W.n == 2);
	CHECK(!tx(&a));
	CHECK(!tx(&a));
	CHECK(W.n == 4);
	for (int i = 0; i < 4; i++)
		CHECK(same(&W.f[i], &a));
	CHECK(fire() == 0);
	CHECK(T.st.early == 1 && T.st.merged == 0);

	/* 重传的段到了，确认号往前跳：又是可以暂存的 */
	a = mk_ack(&WD, 48640, 30000);
	CHECK(tx(&a));
	CHECK(fire() == 1);
}

/* 协商了 SACK（本工程的 lwIP 开着）：下行丢了一个段，后面乱序到的每个段协议栈都回一个带 SACK 块的重复确认，
 * TCP 头有选项，不合并、立刻发。它们的确认号和暂存的那个一样、又没有载荷：暂存的要先发出去
 * （对端先看到确认号推进，后面三个才是第 1、2、3 个重复确认），不能被顶替掉 */
static void t_sack_acks_pass(void)
{
	struct frame a, s, d;

	printf("带 SACK 的确认原样发，不顶替暂存的\n");
	reset();
	a = mk_ack(&WD, 40000, 30000);
	CHECK(tx(&a));
	s = mk_sack(&WD, 40000, 30000);
	CHECK(!tx(&s));
	CHECK(!tx(&s));
	CHECK(!tx(&s));
	CHECK(W.n == 4 && same(&W.f[0], &a) && same(&W.f[1], &s) && same(&W.f[3], &s));
	CHECK(T.st.early == 1 && T.st.superseded == 0);
	CHECK(fire() == 0);

	/* 重传的段到了，确认号往前跳，暂存；这时又来一个带 SACK 的确认、确认号更新：它顶替暂存的 */
	a = mk_ack(&WD, 41440, 30000);
	CHECK(tx(&a));
	s = mk_sack(&WD, 44320, 30000);
	CHECK(!tx(&s));
	CHECK(W.n == 5 && same(&W.f[4], &s) && T.st.superseded == 1);

	/* 确认号一样、带数据的段照旧顶替暂存的（它不算重复确认） */
	a = mk_ack(&WD, 50000, 30000);
	CHECK(tx(&a));
	d = mk_data(&WD, 1000, 50000, 30000, 13);
	CHECK(!tx(&d));
	CHECK(W.n == 6 && same(&W.f[5], &d) && T.st.superseded == 2 && T.st.early == 1);

	/* 确认号一样的 FIN 也一样：它带着同一个确认，暂存的不用再发 */
	a = mk_ack(&WD, 60000, 30000);
	CHECK(tx(&a));
	d = mk_tcp_ex(&WD, F_FIN | F_ACK, 1013, 60000, 30000, 0, 0, 0, 0);
	CHECK(!tx(&d));
	CHECK(W.n == 7 && same(&W.f[6], &d) && T.st.superseded == 3 && T.st.early == 1);
	CHECK(fire() == 0);
}

/* 应用把数据读走，窗口变大：确认号不变、窗口变了的纯 ACK 是窗口更新，可以合并（留最新的窗口） */
static void t_window_update_merges(void)
{
	struct frame a;

	printf("窗口更新合并成最新的\n");
	reset();
	a = mk_ack(&WD, 70000, 1000);
	CHECK(tx(&a));
	a = mk_ack(&WD, 70000, 17000);
	CHECK(tx(&a));
	a = mk_ack(&WD, 70000, 65535);
	CHECK(tx(&a));
	CHECK(fire() == 1);
	CHECK(W.n == 1 && frame_win(W.f[0].d) == 65535 && frame_ack(W.f[0].d) == 70000);
}

/* 投屏的同时看板页在拉数据：两条连接各记各的 */
static void t_two_connections(void)
{
	struct frame a;

	printf("两条连接互不影响\n");
	reset();
	for (int i = 1; i <= 6; i++) {
		a = mk_ack(&WD, 1000 + (uint32_t)i * 2880, 40000);
		CHECK(tx(&a));
		a = mk_ack(&HTTP, 900000 + (uint32_t)i * 1440, 5000);
		CHECK(tx(&a));
	}
	/* 看板那条连接发请求（带数据）：只作废它自己暂存的 */
	a = mk_data(&HTTP, 300, 900000 + 6 * 1440, 5744, 200);
	CHECK(!tx(&a));
	CHECK(W.n == 1);
	CHECK(fire() == 1);
	CHECK(W.n == 2 && frame_ack(W.f[1].d) == 1000 + 6 * 2880);
	CHECK(memcmp(W.f[1].d + 26, WD.src, 4) == 0 && memcmp(W.f[1].d + 30, WD.dst, 4) == 0);
}

/* 连接数比槽多：新连接占最久没动的槽，那个槽里暂存的先发出去，不丢 */
static void t_table_full_evicts_oldest(void)
{
	struct conn c, extra = lan_conn(ACK_HOLD_SLOTS), extra2 = lan_conn(ACK_HOLD_SLOTS + 1);
	struct frame a;

	printf("表满了让出最久没动的槽\n");
	reset();
	for (int i = 0; i < ACK_HOLD_SLOTS; i++) {
		c = lan_conn(i);
		a = mk_ack(&c, 100 + (uint32_t)i, 4000);
		CHECK(tx(&a));
	}
	c = lan_conn(0);
	a = mk_ack(&c, 200, 4000);           /* 第 0 条又动了一下，最久没动的变成第 1 条 */
	CHECK(tx(&a));
	a = mk_ack(&extra, 555, 4000);
	CHECK(tx(&a));
	CHECK(W.n == 1 && frame_ack(W.f[0].d) == 101);    /* 第 1 条暂存的那个被提前发了 */
	CHECK(T.st.early == 1);
	CHECK(fire() == ACK_HOLD_SLOTS);
	CHECK(W.n == 1 + ACK_HOLD_SLOTS);

	/* 都发完以后（槽都没暂存东西），再来新连接：直接占最久没动的，没有东西要提前发 */
	a = mk_ack(&extra2, 300, 4000);
	CHECK(tx(&a));
	CHECK(W.n == 1 + ACK_HOLD_SLOTS && T.st.early == 1);
	CHECK(fire() == 1);

	/* 有的槽暂存着、有的没有：占没暂存的那个，暂存着的不动 */
	reset();
	for (int i = 0; i < ACK_HOLD_SLOTS; i++) {
		c = lan_conn(i);
		a = mk_ack(&c, 100 + (uint32_t)i, 4000);
		CHECK(tx(&a));
	}
	c = lan_conn(ACK_HOLD_SLOTS - 1);
	a = mk_data(&c, 1000, 100 + ACK_HOLD_SLOTS - 1, 4000, 13);   /* 最后一条发了带数据的段：它暂存的作废 */
	CHECK(!tx(&a));
	a = mk_ack(&extra, 555, 4000);       /* 新连接占的是它（虽然它最近才动过），不挤掉暂存着的 */
	CHECK(tx(&a));
	CHECK(W.n == 1 && T.st.early == 0);
	CHECK(fire() == ACK_HOLD_SLOTS);
}

/* 只发不收的连接（上传、LLM 网关往上游转发请求）也记着最近的确认号：
 * 之后它回的纯 ACK 如果和上一个带数据的段确认号、窗口都一样，是重复确认，原样发 */
static void t_data_only_connection_tracked(void)
{
	struct frame d, a;

	printf("只发数据的连接也记账\n");
	reset();
	d = mk_tcp_ex(&LLM, F_SYN, 999, 0, 5744, 0, 4, 0, 0);
	CHECK(!tx(&d));
	a = mk_ack(&LLM, 7001, 5744);                    /* 三次握手的第三个包：暂存一下无妨 */
	CHECK(tx(&a));
	d = mk_data(&LLM, 1000, 7001, 5744, 1440);       /* 请求：带着同样的确认，暂存的作废 */
	CHECK(!tx(&d));
	d = mk_data(&LLM, 2440, 7001, 5744, 600);
	CHECK(!tx(&d));
	CHECK(W.n == 3 && T.st.superseded == 1);
	a = mk_ack(&LLM, 7001, 5744);                    /* 对端来了保活探测 / 乱序段：重复确认，不暂存 */
	CHECK(!tx(&a));
	CHECK(W.n == 4 && same(&W.f[3], &a));
	a = mk_ack(&LLM, 8441, 4304);                    /* 应答开始到了：正常推进的确认，暂存 */
	CHECK(tx(&a));
	CHECK(fire() == 1);
}

/* 连接关了又在同样的地址端口上重开：旧连接的确认号不能拿来判断新连接的包 */
static void t_rst_syn_reset_history(void)
{
	struct frame a, r, s;

	printf("RST / SYN 清掉旧连接的记录\n");
	reset();
	a = mk_ack(&WD, 7000, 9000);
	CHECK(tx(&a));
	r = mk_tcp_ex(&WD, F_RST | F_ACK, 1000, 7000, 0, 0, 0, 0, 0);
	CHECK(!tx(&r));
	CHECK(W.n == 1 && same(&W.f[0], &r));     /* 暂存的作废，只发 RST */
	CHECK(fire() == 0);

	/* 新连接：SYN+ACK，然后第一个纯 ACK 碰巧和旧连接最后一个的确认号、窗口一样——不是重复确认，照常暂存 */
	s = mk_tcp_ex(&WD, F_SYN | F_ACK, 5, 6999, 9000, 0, 4, 0, 0);
	CHECK(!tx(&s));
	CHECK(tx(&a));
	CHECK(fire() == 1);

	/* FIN 带着确认：暂存的作废，FIN 照常发 */
	a = mk_ack(&WD, 8000, 9000);
	CHECK(tx(&a));
	r = mk_tcp_ex(&WD, F_FIN | F_ACK, 1000, 8000, 9000, 0, 0, 0, 0);
	CHECK(!tx(&r));
	CHECK(fire() == 0);

	/* 正常关掉（FIN，没有 RST）以后本机用同样的地址端口再连出去（端口复用）：槽还记着旧连接最后的确认号 8001 / 窗口 9000。
	 * 新连接的 SYN（不带确认）把它清掉；之后三次握手的第三个包哪怕确认号、窗口碰巧和旧连接最后一个一样，
	 * 也不是重复确认，照常暂存 */
	a = mk_ack(&WD, 8001, 9000);                      /* 对端的 FIN 的确认 */
	CHECK(tx(&a));
	CHECK(fire() == 1);
	s = mk_tcp_ex(&WD, F_SYN, 77, 0, 9000, 0, 4, 0, 0);
	CHECK(!tx(&s));
	a = mk_ack(&WD, 8001, 9000);
	CHECK(tx(&a));                                    /* 暂存了（没被当成重复确认直接发） */
	CHECK(fire() == 1);
}

/* 确认号跨过 2^32 回绕 */
static void t_ack_wraparound(void)
{
	struct frame a, d;

	printf("确认号回绕\n");
	reset();
	a = mk_ack(&WD, 0xFFFFFF00u, 9000);
	CHECK(tx(&a));
	d = mk_data(&WD, 1000, 0x00000200u, 9000, 13);     /* 比 0xFFFFFF00 新 */
	CHECK(!tx(&d));
	CHECK(W.n == 1 && same(&W.f[0], &d));
	CHECK(T.st.superseded == 1 && T.st.early == 0);

	/* 反过来：带数据的段的确认号比暂存的旧（协议栈不会这样发，保底）——暂存的先发，不丢 */
	a = mk_ack(&WD, 0x00001000u, 9000);
	CHECK(tx(&a));
	d = mk_data(&WD, 1013, 0x00000800u, 9000, 13);
	CHECK(!tx(&d));
	CHECK(W.n == 3 && same(&W.f[1], &a) && same(&W.f[2], &d));
	CHECK(T.st.early == 1);
}

static enum ack_hold_sent send_link_down(const uint8_t *frame, size_t len, void *arg)
{
	(void)frame; (void)len; (void)arg;
	return ACK_HOLD_DROP;
}

/* 到点时一时发不出去（发送队列满 / 从机叫停）：留在槽里，下一轮再发；这期间来了更新的照样盖掉 */
static void t_retry_keeps_held(void)
{
	struct frame a, b, d;
	int fail;

	printf("一时发不出去的留到下一轮\n");
	reset();
	a = mk_ack(&WD, 3000, 9000);
	CHECK(tx(&a));
	fail = 1;
	CHECK(ack_hold_core_flush(&T, send_to_wire, &fail) == 1);     /* 没发出去，还留着 1 个 */
	CHECK(W.n == 0 && T.st.retried == 1);
	CHECK(fire() == 1 && same(&W.f[0], &a));                      /* 下一轮发出去的还是它 */

	/* 留着的时候来了更新的纯 ACK：盖掉，下一轮发新的 */
	a = mk_ack(&WD, 4000, 9000);
	CHECK(tx(&a));
	fail = 1;
	CHECK(ack_hold_core_flush(&T, send_to_wire, &fail) == 1);
	a = mk_ack(&WD, 5000, 9000);
	CHECK(tx(&a));
	CHECK(fire() == 1 && frame_ack(W.f[1].d) == 5000);

	/* 留着的时候这条连接发了带数据的段：作废 */
	a = mk_ack(&WD, 6000, 9000);
	CHECK(tx(&a));
	fail = 1;
	CHECK(ack_hold_core_flush(&T, send_to_wire, &fail) == 1);
	d = mk_data(&WD, 1000, 6000, 9000, 13);
	CHECK(!tx(&d));
	CHECK(fire() == 0);

	/* 两条连接都暂存着，头一个发不出去、第二个发得出去：各算各的 */
	reset();
	a = mk_ack(&WD, 100, 9000);
	b = mk_ack(&HTTP, 200, 9000);
	CHECK(tx(&a));
	CHECK(tx(&b));
	fail = 1;
	CHECK(ack_hold_core_flush(&T, send_to_wire, &fail) == 1);
	CHECK(W.n == 1 && same(&W.f[0], &b));
	CHECK(fire() == 1 && same(&W.f[1], &a));

	/* 链路不通：丢掉，不再留着（不然定时器会一直响下去） */
	a = mk_ack(&WD, 300, 9000);
	CHECK(tx(&a));
	CHECK(ack_hold_core_flush(&T, send_link_down, NULL) == 0);
	CHECK(T.st.dropped == 1);
	CHECK(fire() == 0);
}

/* 不是"IPv4 上没有选项的 TCP 纯 ACK"的帧：一律照常发，表不动 */
static void t_other_frames_untouched(void)
{
	struct ack_hold_table before;
	struct frame f;

	printf("别的帧不碰\n");
	reset();
	before = T;

	f = mk_ack(&WD, 100, 100);  f.d[12] = 0x08; f.d[13] = 0x06;      /* ARP */
	CHECK(!tx(&f));
	f = mk_ack(&WD, 100, 100);  f.d[12] = 0x86; f.d[13] = 0xdd;      /* IPv6 */
	CHECK(!tx(&f));
	f = mk_ack(&WD, 100, 100);  f.d[23] = 17;                         /* UDP */
	CHECK(!tx(&f));
	f = mk_ack(&WD, 100, 100);  f.d[23] = 1;                          /* ICMP（ping 应答） */
	CHECK(!tx(&f));
	f = mk_ack(&WD, 100, 100);  f.d[20] = 0x20;                       /* IP 分片：MF */
	CHECK(!tx(&f));
	f = mk_ack(&WD, 100, 100);  f.d[21] = 0xb9;                       /* IP 分片：偏移不为 0 */
	CHECK(!tx(&f));
	f = mk_ack(&WD, 100, 100);  f.len = 40;                           /* 截断的帧 */
	CHECK(!tx(&f));
	f = mk_ack(&WD, 100, 100);  f.d[16] = 0x05;                       /* IP 总长比帧长 */
	CHECK(!tx(&f));
	f = mk_ack(&WD, 100, 100);  f.d[14 + 20 + 12] = 0x40;             /* TCP 头长 16：不合法 */
	CHECK(!tx(&f));
	CHECK(memcmp(&before, &T, sizeof(T)) == 0);

	/* 没记过的连接来的 RST（比如回给一个没人听的端口）：不占槽 */
	f = mk_tcp_ex(&WD, F_RST | F_ACK, 0, 100, 0, 0, 0, 0, 0);
	CHECK(!tx(&f));
	CHECK(memcmp(&before, &T, sizeof(T)) == 0);

	/* 是 TCP、但不是可以合并的纯 ACK：照常发，不暂存 */
	f = mk_tcp_ex(&WD, F_ACK, 1000, 100, 100, 0, 0, 4, 0);            /* IP 头带选项 */
	CHECK(!tx(&f));
	f = mk_tcp_ex(&WD, F_ACK | 0x40, 1000, 101, 100, 0, 0, 0, 0);     /* ACK + ECE */
	CHECK(!tx(&f));
	f = mk_tcp_ex(&WD, F_ACK, 1000, 102, 100, 0, 0, 0, 10);           /* 补零补到 64 字节，放不下 */
	CHECK(!tx(&f));
	f = mk_data(&WD, 1000, 103, 100, 1440);
	CHECK(!tx(&f));
	CHECK(W.n == 14);
	CHECK(fire() == 0);

	/* 补零补到 60 字节的纯 ACK 可以合并，发出去的还是 60 字节 */
	f = mk_tcp_ex(&WD, F_ACK, 1000, 104, 100, 0, 0, 0, 6);
	CHECK(tx(&f));
	CHECK(fire() == 1);
	CHECK(W.f[W.n - 1].len == 60 && same(&W.f[W.n - 1], &f));
}

/* 合并关着（hosted ackhold 0）：全都照常发；关之前暂存的由后面的包作废或者到点发掉 */
static void t_disabled(void)
{
	struct frame a;

	printf("关掉以后全部照常发\n");
	reset();
	a = mk_ack(&WD, 100, 9000);
	CHECK(tx(&a));                         /* 开着时暂存了一个 */
	a = mk_ack(&WD, 200, 9000);
	CHECK(!tx_en(&a, false));              /* 关了：新的照常发，暂存的那个更旧，作废 */
	a = mk_ack(&WD, 300, 9000);
	CHECK(!tx_en(&a, false));
	CHECK(W.n == 2 && frame_ack(W.f[0].d) == 200 && frame_ack(W.f[1].d) == 300);
	CHECK(fire() == 0);

	a = mk_ack(&HTTP, 100, 9000);          /* 关着：没记过的连接的纯 ACK 也照常发 */
	CHECK(!tx_en(&a, false));
	CHECK(W.n == 3 && fire() == 0);
}

/* ───────────────────────── 随机序列 + 对照模型 ─────────────────────────
 * 几条连接随机地发：推进的纯 ACK、窗口更新、重复确认、带数据的段、带 SACK 的确认、FIN、RST 后重连；定时器随机地到点，
 * 到点时随机地"一时发不出去"。结束时再到点一次（什么都能发出去）。核对：
 *  1. 不是可以合并的纯 ACK 的包：一个不少、先后不变地到了对端。
 *  2. 每条连接最后到对端的那个包，带的确认号和窗口就是协议栈最后发的那个（最新的状态没有丢）。
 *  3. 对端看到的"重复确认"（没有载荷、确认号和窗口都和这条连接前一个到的包一样的确认，带不带 SACK 块都算）的个数，和协议栈发的一样多：
 *     没少（快速重传靠它），也没多（多了会让对端误以为丢包）。连接比槽多时允许少、不许多。
 *  4. 同一次连接里，对端看到的确认号不往回走（过时的纯 ACK 没有被发出去）。
 * 窗口照真实的协议栈来：确认号推进时窗口被占掉，确认号不变时窗口只增不减（通告过的右边界不往回缩）。 */
static uint32_t rng_state;
static uint32_t rnd(void)
{
	rng_state = rng_state * 1664525u + 1013904223u;
	return rng_state >> 8;
}

static bool is_mergeable_ack(const struct frame *f)
{
	const uint8_t *d = f->d;
	return f->len <= ACK_HOLD_FRAME_MAX && (d[14] & 15) == 5 && d[16] == 0 && d[17] == 40 && d[14 + 20 + 13] == F_ACK;
}

#define FUZZ_MAX_CONNS  12

static int conn_index(const uint8_t *d, const struct conn *cs, int ncs)
{
	for (int i = 0; i < ncs; i++)
		if (memcmp(d + 30, cs[i].dst, 4) == 0)
			return i;
	return -1;
}

static uint8_t frame_flags(const uint8_t *d)
{
	return d[14 + (d[14] & 15) * 4 + 13];
}

/* 没有载荷、标志位只有 ACK 的包（带不带选项都算）：对端拿它们数重复确认 */
static bool is_bare_ack(const struct frame *f)
{
	const uint8_t *d = f->d;
	unsigned ihl = (d[14] & 15) * 4u, tot = ((unsigned)d[16] << 8) | d[17];
	unsigned doff = (d[14 + ihl + 12] >> 4) * 4u;

	return tot == ihl + doff && frame_flags(d) == F_ACK;
}

/* 数一条序列里每条连接的重复确认个数（没有载荷、确认号和窗口都和这条连接上一个包一样的确认）。
 * 顺带核对：同一条连接（同一次连接：SYN / RST 之后重新算）里确认号不往回走。返回往回走了几次。 */
static int count_dups(const struct frame *fs, int n, const struct conn *cs, int ncs, int *dups)
{
	int backwards = 0;

	uint32_t la[FUZZ_MAX_CONNS] = {0};
	uint16_t lw[FUZZ_MAX_CONNS] = {0};
	bool has[FUZZ_MAX_CONNS] = {0};

	for (int i = 0; i < ncs; i++)
		dups[i] = 0;
	for (int i = 0; i < n; i++) {
		int c = conn_index(fs[i].d, cs, ncs);
		uint32_t a = frame_ack(fs[i].d);
		uint16_t w = frame_win(fs[i].d);

		if (frame_flags(fs[i].d) & (F_SYN | F_RST))
			has[c] = false;                 /* 旧连接没了 / 新连接开始 */
		if (!(frame_flags(fs[i].d) & F_ACK))
			continue;
		if (has[c] && (int32_t)(a - la[c]) < 0)
			backwards++;
		if (is_bare_ack(&fs[i]) && has[c] && a == la[c] && w == lw[c])
			dups[c]++;
		has[c] = true; la[c] = a; lw[c] = w;
	}
	return backwards;
}

static struct frame g_sent[4096];

static void fuzz_once(uint32_t seed, int ncs)
{
	struct conn cs[FUZZ_MAX_CONNS];
	uint32_t ack[FUZZ_MAX_CONNS], seq[FUZZ_MAX_CONNS];
	uint16_t win[FUZZ_MAX_CONNS];
	int nsent = 0, d_sent[FUZZ_MAX_CONNS], d_wire[FUZZ_MAX_CONNS], wi;

	rng_state = seed;
	reset();
	for (int i = 0; i < ncs; i++) {
		cs[i] = lan_conn(i);
		ack[i] = rnd() * 977u;      /* 起点随机，有的会跨回绕 */
		if (i == 0)
			ack[i] = 0xFFFFF000u;
		seq[i] = 1000;
		win[i] = 30000;
	}

	for (int step = 0; step < 3000; step++) {
		int c = (int)(rnd() % (uint32_t)ncs);
		uint32_t r = rnd() % 100;
		struct frame f;

		if (r < 55) {                       /* 收到两个段，确认号推进，窗口被占掉这么多 */
			ack[c] += 2880;
			win[c] = (uint16_t)(win[c] > 2880 ? win[c] - 2880 : 0);
			f = mk_ack(&cs[c], ack[c], win[c]);
		} else if (r < 65) {                /* 应用读走数据，窗口更新：确认号不变时窗口只会变大 */
			uint32_t grow = 1 + rnd() % 20000;
			win[c] = (uint16_t)(win[c] + grow > 65535 ? 65535 : win[c] + grow);
			f = mk_ack(&cs[c], ack[c], win[c]);     /* 已经是 65535 的话就是个重复确认 */
		} else if (r < 75) {                /* 重复确认（没协商 SACK 时丢包） */
			f = mk_ack(&cs[c], ack[c], win[c]);
		} else if (r < 90) {                /* 带数据的小段（应用层确认 / 触摸） */
			f = mk_data(&cs[c], seq[c], ack[c], win[c], 13);
			seq[c] += 13;
		} else if (r < 93) {                /* 带 SACK 的重复确认 */
			f = mk_sack(&cs[c], ack[c], win[c]);
		} else if (r < 94) {                /* 本机先关（FIN 带着当前的确认），连接还在收 */
			f = mk_tcp_ex(&cs[c], F_FIN | F_ACK, seq[c], ack[c], win[c], 0, 0, 0, 0);
			seq[c] += 1;
		} else if (r < 95) {                /* 连接被复位，对端用同样的地址端口重连：先 RST，再回新连接的 SYN+ACK */
			f = mk_tcp_ex(&cs[c], F_RST | F_ACK, seq[c], ack[c], 0, 0, 0, 0, 0);
			g_sent[nsent++] = f;
			tx(&f);
			ack[c] = rnd() * 977u;
			seq[c] = rnd();
			win[c] = 30000;
			f = mk_tcp_ex(&cs[c], F_SYN | F_ACK, seq[c], ack[c], win[c], 0, 4, 0, 0);
			seq[c] += 1;
		} else {                            /* 定时器到点，有时头几个发不出去 */
			int fail = (int)(rnd() % 3);
			ack_hold_core_flush(&T, send_to_wire, &fail);
			continue;
		}
		g_sent[nsent++] = f;
		tx(&f);
	}
	fire();

	/* 1. 不可合并的包原样、按顺序到达 */
	wi = 0;
	for (int i = 0; i < nsent; i++) {
		if (is_mergeable_ack(&g_sent[i]))
			continue;
		while (wi < W.n && is_mergeable_ack(&W.f[wi]))
			wi++;
		if (wi >= W.n || !same(&W.f[wi], &g_sent[i])) {
			CHECK(!"不可合并的包丢了或者顺序变了");
			printf("  seed=%u ncs=%d 第 %d 个发送的包\n", (unsigned)seed, ncs, i);
			return;
		}
		wi++;
	}

	/* 2. 每条连接最后到的包带着最新的确认号和窗口 */
	for (int c = 0; c < ncs; c++) {
		int last = -1;
		for (int i = W.n - 1; i >= 0 && last < 0; i--)
			if (conn_index(W.f[i].d, cs, ncs) == c)
				last = i;
		if (last < 0)
			continue;
		if (frame_ack(W.f[last].d) != ack[c] || frame_win(W.f[last].d) != win[c]) {
			CHECK(!"连接最后的确认号 / 窗口没到对端");
			printf("  seed=%u ncs=%d 连接 %d\n", (unsigned)seed, ncs, c);
		}
	}

	/* 3. 重复确认的个数。表里放得下所有连接时和协议栈发的严格相等。
	 * 连接比槽多时，被挤出去的连接丢了"上一个确认"的记录，之后的头一个重复确认会被当成普通纯 ACK 暂存，
	 * 可能被随后的包盖掉：个数可以少，但不能多（多了会让对端误以为丢包）。 */
	CHECK(count_dups(g_sent, nsent, cs, ncs, d_sent) == 0);      /* 模型自己发的确认号不往回走 */
	if (count_dups(W.f, W.n, cs, ncs, d_wire) != 0) {
		CHECK(!"对端看到确认号往回走了（过时的纯 ACK 被发了出去）");
		printf("  seed=%u ncs=%d\n", (unsigned)seed, ncs);
	}
	for (int c = 0; c < ncs; c++) {
		if (ncs <= ACK_HOLD_SLOTS ? d_sent[c] != d_wire[c] : d_wire[c] > d_sent[c]) {
			CHECK(!"重复确认的个数变了");
			printf("  seed=%u ncs=%d 连接 %d：协议栈发了 %d 个，对端看到 %d 个\n",
					(unsigned)seed, ncs, c, d_sent[c], d_wire[c]);
		}
	}
	g_checks += 4;
}

static void t_fuzz(void)
{
	printf("随机序列对照模型\n");
	for (uint32_t seed = 1; seed <= 300; seed++) {
		fuzz_once(seed, 1);
		fuzz_once(seed, 3);
		fuzz_once(seed, ACK_HOLD_SLOTS);
		fuzz_once(seed, ACK_HOLD_SLOTS + 3);
	}
}

int main(void)
{
	t_burst_merges_to_latest();
	t_data_segment_supersedes();
	t_dup_acks_keep_count();
	t_sack_acks_pass();
	t_window_update_merges();
	t_two_connections();
	t_table_full_evicts_oldest();
	t_data_only_connection_tracked();
	t_rst_syn_reset_history();
	t_ack_wraparound();
	t_retry_keeps_held();
	t_other_frames_untouched();
	t_disabled();
	t_fuzz();

	printf("%s：%d 项检查，%d 项失败\n", g_fail ? "有失败" : "全部通过", g_checks, g_fail);
	return g_fail ? 1 : 0;
}
