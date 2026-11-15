/* 本工程新增：TCP 纯 ACK 合并的判定逻辑（说明见 tcp_ack_hold.h）。
 * 只有表和包头解析，不依赖 FreeRTOS / IDF；调用方负责加锁。单元测试在 firmware/tools/ackholdtest。 */
#include <string.h>
#include "tcp_ack_hold.h"

#define ETH_HDR_LEN     14
#define IP_HDR_MIN      20
#define TCP_HDR_MIN     20

#define TCP_FLAG_FIN    0x01
#define TCP_FLAG_SYN    0x02
#define TCP_FLAG_RST    0x04
#define TCP_FLAG_ACK    0x10

/* 从一帧里看出来的 TCP 信息 */
struct tcp_view {
	uint8_t  key[12];
	uint8_t  flags;
	uint32_t ack;
	uint16_t win;
	bool     has_data;     /* 有载荷 */
	bool     pure_ack;     /* 可以合并的纯 ACK */
};

/* 帧是不是一个完整的 IPv4 TCP 包；是的话填 v。
 * 分片不看（后面的分片里没有 TCP 头；lwIP 的 TCP 也不会发分片）。IPv6 不看，照常发。 */
static bool tcp_parse(const uint8_t *f, size_t len, struct tcp_view *v)
{
	unsigned ihl, tot, doff;
	const uint8_t *tcp;

	if (len < ETH_HDR_LEN + IP_HDR_MIN + TCP_HDR_MIN)
		return false;
	if (f[12] != 0x08 || f[13] != 0x00)             /* 以太网类型：IPv4 */
		return false;
	if ((f[14] >> 4) != 4 || f[23] != 6)            /* IP 版本 4，协议 TCP */
		return false;
	if ((f[20] & 0x3f) || f[21])                    /* 分片（MF 置位或偏移不为 0） */
		return false;

	ihl = (f[14] & 0x0f) * 4u;
	tot = ((unsigned)f[16] << 8) | f[17];
	if (ihl < IP_HDR_MIN || len < ETH_HDR_LEN + ihl + TCP_HDR_MIN)
		return false;

	tcp = f + ETH_HDR_LEN + ihl;
	doff = (tcp[12] >> 4) * 4u;
	if (doff < TCP_HDR_MIN || tot < ihl + doff || len < ETH_HDR_LEN + tot)
		return false;

	memcpy(v->key, f + 26, 8);                      /* 源地址、目的地址 */
	memcpy(v->key + 8, tcp, 4);                     /* 源端口、目的端口 */
	v->flags = tcp[13];
	v->ack = ((uint32_t)tcp[8] << 24) | ((uint32_t)tcp[9] << 16) | ((uint32_t)tcp[10] << 8) | tcp[11];
	v->win = (uint16_t)((tcp[14] << 8) | tcp[15]);

	v->has_data = tot > ihl + doff;

	/* 可以合并的纯 ACK：没有载荷、IP 头和 TCP 头都没有选项、标志位只有 ACK。
	 * 带选项的确认不合并：丢包时的重复确认带着 SACK 块，对端靠它们决定重传哪些段。 */
	v->pure_ack = (ihl == IP_HDR_MIN && doff == TCP_HDR_MIN && tot == IP_HDR_MIN + TCP_HDR_MIN &&
			v->flags == TCP_FLAG_ACK && len <= ACK_HOLD_FRAME_MAX);
	return true;
}

static struct ack_hold_slot *slot_find(struct ack_hold_table *t, const uint8_t *key)
{
	for (int i = 0; i < ACK_HOLD_SLOTS; i++) {
		struct ack_hold_slot *s = &t->slot[i];
		if (s->used && memcmp(s->key, key, sizeof(s->key)) == 0)
			return s;
	}
	return NULL;
}

/* 把槽里暂存的那个取出来 */
static void slot_take(struct ack_hold_slot *s, struct ack_hold_frame *out)
{
	*out = s->frame;
	s->held = false;
	s->frame.len = 0;
}

/* 给一条新连接找个槽：先用空的；没有空的就用没暂存东西的里面最久没动的；
 * 全都暂存着东西，就用最久没动的那个，它暂存的放进 *early 让调用方先发。 */
static struct ack_hold_slot *slot_claim(struct ack_hold_table *t, const uint8_t *key, struct ack_hold_frame *early)
{
	struct ack_hold_slot *pick = NULL;

	for (int i = 0; i < ACK_HOLD_SLOTS; i++) {
		struct ack_hold_slot *s = &t->slot[i];
		if (!s->used) {
			pick = s;
			break;
		}
		if (!pick) {
			pick = s;
			continue;
		}
		/* 没暂存的优先；同类里挑离现在最久的（clock 回绕也成立的比法） */
		if (pick->held != s->held) {
			if (pick->held)
				pick = s;
		} else if ((uint32_t)(t->clock - s->stamp) > (uint32_t)(t->clock - pick->stamp)) {
			pick = s;
		}
	}

	if (pick->used && pick->held) {
		slot_take(pick, early);
		t->st.early++;
	}
	memset(pick, 0, sizeof(*pick));
	pick->used = true;
	memcpy(pick->key, key, sizeof(pick->key));
	return pick;
}

bool ack_hold_core_on_tx(struct ack_hold_table *t, const uint8_t *frame, size_t len,
		bool hold_enabled, struct ack_hold_frame *early)
{
	struct tcp_view v;
	struct ack_hold_slot *s;

	early->len = 0;
	if (!tcp_parse(frame, len, &v))
		return false;

	s = slot_find(t, v.key);

	if (v.pure_ack && hold_enabled) {
		if (s && s->has_last && v.ack == s->last_ack && v.win == s->last_win) {
			/* 重复确认：不合并，个数原样交给对端。暂存着的那个（内容和它一样）先发。 */
			if (s->held) {
				slot_take(s, early);
				t->st.early++;
			}
			s->stamp = ++t->clock;
			return false;
		}

		if (!s)
			s = slot_claim(t, v.key, early);
		if (s->held)
			t->st.merged++;        /* 盖掉上一个：这一个的确认号、窗口更新 */
		s->held = true;
		s->frame.len = (uint8_t)len;
		memcpy(s->frame.data, frame, len);
		s->has_last = true;
		s->last_ack = v.ack;
		s->last_win = v.win;
		s->stamp = ++t->clock;
		return true;
	}

	/* 其它 TCP 包（带数据的段、带选项的确认、SYN / FIN / RST；或者合并关着时的纯 ACK）：照常发，只记账。 */
	if (v.flags & TCP_FLAG_RST) {
		/* 连接没了：暂存的作废，槽让出来 */
		if (s) {
			if (s->held)
				t->st.superseded++;
			memset(s, 0, sizeof(*s));
		}
		return false;
	}
	if (!s) {
		/* 没记过的连接也占一个槽、记下它最近的确认号和窗口：之后来的纯 ACK 是不是重复确认要靠它判断 */
		s = slot_claim(t, v.key, early);
	} else if (v.flags & TCP_FLAG_SYN) {
		/* 同样的地址端口上开了新连接：旧连接暂存的作废，记的确认号也不再算数 */
		if (s->held)
			t->st.superseded++;
		s->held = false;
		s->frame.len = 0;
		s->has_last = false;
	}

	if (s->held) {
		/* 暂存着的那个的确认号就是 last_ack。这一包什么时候能顶替它（它不用发了）：
		 * - 确认号比它新；
		 * - 确认号一样，而且这一包带数据或者 FIN：对端照样看到这个确认号，这一包本身不算重复确认。
		 * 协议栈每次发包都填当前的确认号和窗口，带数据的段都走这里。
		 * 确认号一样、又没有载荷的（丢包时带 SACK 块的重复确认）不能顶替：对端要先看到暂存的那个把确认号推进，
		 * 这一个才算第 1 个重复确认；顶替掉的话对端的重复确认少数一个。暂存的先发。
		 * 确认号比它旧的（协议栈不会这样发）也先发暂存的，保底。 */
		const int32_t newer = (int32_t)(v.ack - s->last_ack);
		const bool carries = v.has_data || (v.flags & TCP_FLAG_FIN);

		if ((v.flags & TCP_FLAG_ACK) && (newer > 0 || (newer == 0 && carries))) {
			s->held = false;
			s->frame.len = 0;
			t->st.superseded++;
		} else {
			slot_take(s, early);
			t->st.early++;
		}
	}
	if (v.flags & TCP_FLAG_ACK) {
		s->has_last = true;
		s->last_ack = v.ack;
		s->last_win = v.win;
	}
	s->stamp = ++t->clock;
	return false;
}

int ack_hold_core_flush(struct ack_hold_table *t, ack_hold_flush_fn send, void *arg)
{
	int left = 0;

	for (int i = 0; i < ACK_HOLD_SLOTS; i++) {
		struct ack_hold_slot *s = &t->slot[i];

		if (!s->used || !s->held)
			continue;
		switch (send(s->frame.data, s->frame.len, arg)) {
		case ACK_HOLD_SENT:
			t->st.sent++;
			break;
		case ACK_HOLD_RETRY:
			t->st.retried++;
			left++;
			continue;           /* 留在槽里；这期间来了更新的照样盖掉它 */
		default:
			t->st.dropped++;
			break;
		}
		s->held = false;
		s->frame.len = 0;
	}
	return left;
}
