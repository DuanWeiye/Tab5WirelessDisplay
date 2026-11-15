/* 本工程新增：TCP 纯 ACK 合并接到传输层的部分（说明见 tcp_ack_hold.h，判定逻辑在 tcp_ack_hold_core.c）。
 *
 * 两个线程会进来：
 * - 协议栈线程（tcp_ack_hold_on_tx）：每发一帧来问一次，纯 ACK 暂存进表里，表里原来没东西就起定时器；
 * - esp_timer 任务（到点）：把表里暂存的发掉。它不能久等，所以发送用不等待的入队；
 *   一时发不了（发送队列满 / 没有发送缓冲 / 从机叫停）就留在表里，下一轮再发，不丢。
 *
 * 表用互斥锁保护。到点时是拿着锁"取出来 + 交给发送队列"的：要是取出来以后放了锁再发，协议栈线程可能正好在
 * 这个空当发了同一条连接的下一个包，旧的纯 ACK 反而排到它后面，对端会多数一个重复确认。
 * 协议栈线程在锁里只做判定（查表、几十字节的拷贝），它自己那一帧的发送（可能要等发送队列）在锁外，
 * 所以 esp_timer 任务等锁只等这么一小会儿。
 */
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "tcp_ack_hold.h"

static const char TAG[] = "H_ACK_HOLD";

/* 一个纯 ACK 最多暂存多久（微秒）。
 * 要比协议栈处理完一次 SDIO 突发（十几二十个段，连着回十来个纯 ACK）的时间长，这一阵才并得成一个；
 * 又要远小于对端的重传超时和每帧的间隔，确认晚这么一点对端感觉不到。
 * 实测（2026-11-14，投屏 79 KB/帧、约 32 帧/秒，同时每 5 ms ping 一次，每档 3000 个）：
 *   不合并 纯 ACK 约 1100 个/秒，ping 丢 0.8~1.5%
 *   500    约 600 个/秒，丢 0.2%
 *   1000   约 415 个/秒，四轮里丢 0 / 0.03 / 0.03 / 0.07%
 *   2000   约 260 个/秒，三轮都是 0
 *   3000、4000  约 190 / 150 个/秒，都是 0
 * 各档帧率一样（32.1~32.4 帧/秒）。取刚好不丢的 2000。 */
#define ACK_HOLD_DEFAULT_US   2000
#define ACK_HOLD_MAX_US       20000
#define ACK_HOLD_RETRY_MIN_US 1000      /* 到点发不出去、留着重试时，至少隔这么久再试（见 hold_timer_arm） */

static SemaphoreHandle_t s_mutex;            /* 保护 s_tab、s_armed。建不出来 = 这个功能不起作用 */
static struct ack_hold_table s_tab;
static bool s_armed;                         /* 定时器已经（或者马上就要）起了 */
static esp_timer_handle_t s_timer;
static tcp_ack_hold_send_fn s_send;
static volatile uint32_t s_hold_us = ACK_HOLD_DEFAULT_US;

/* 起定时器。调用前已经在锁里把 s_armed 置上了（所以同一时刻只有一个线程走到这里）。
 * 单次定时器在回调执行期间已经是停着的，回调里、回调执行期间别的线程都可以再起它。 */
static void hold_timer_arm(bool retry)
{
	uint32_t us = s_hold_us;

	/* retry = 到点了一时发不出去（发送队列满 / 没有发送缓冲 / 从机叫停）、留着再等一轮。这不是几百微秒能缓过来的，
	 * 暂存时间设得很短（调试台能设到 1 微秒）或者合并关着（0）的时候，照那个间隔重试等于让定时器任务空转 */
	if (retry && us < ACK_HOLD_RETRY_MIN_US)
		us = ACK_HOLD_RETRY_MIN_US;

	if (esp_timer_start_once(s_timer, us) != ESP_OK) {
		/* 不该发生。把标志放下来，下一个纯 ACK 进来时再试 */
		xSemaphoreTake(s_mutex, portMAX_DELAY);
		s_armed = false;
		xSemaphoreGive(s_mutex);
	}
}

static enum ack_hold_sent hold_flush_one(const uint8_t *frame, size_t len, void *arg)
{
	int err = s_send(frame, len, false);

	(void)arg;
	if (err == ESP_OK)
		return ACK_HOLD_SENT;
	if (err == ESP_ERR_TIMEOUT || err == ESP_ERR_NO_MEM)
		return ACK_HOLD_RETRY;
	return ACK_HOLD_DROP;
}

static void hold_timer_cb(void *arg)
{
	bool arm = false;

	(void)arg;

	xSemaphoreTake(s_mutex, portMAX_DELAY);
	s_armed = false;
	if (ack_hold_core_flush(&s_tab, hold_flush_one, NULL)) {
		s_armed = true;
		arm = true;
	}
	xSemaphoreGive(s_mutex);

	if (arm)
		hold_timer_arm(true);
}

void tcp_ack_hold_init(tcp_ack_hold_send_fn send)
{
	const esp_timer_create_args_t args = {
		.callback = hold_timer_cb,
		.dispatch_method = ESP_TIMER_TASK,
		.name = "ack_hold",
	};

	s_send = send;
	if (s_mutex)
		return;    /* 传输层重新初始化：锁和定时器已经有了 */

	if (esp_timer_create(&args, &s_timer) != ESP_OK) {
		ESP_LOGE(TAG, "建不出定时器，TCP 纯 ACK 不合并");
		return;
	}
	s_mutex = xSemaphoreCreateMutex();
	if (!s_mutex) {
		ESP_LOGE(TAG, "建不出锁，TCP 纯 ACK 不合并");
		esp_timer_delete(s_timer);
		s_timer = NULL;
	}
}

bool tcp_ack_hold_on_tx(const uint8_t *frame, size_t len)
{
	struct ack_hold_frame early;
	bool held, arm = false;

	if (!s_mutex)
		return false;

	xSemaphoreTake(s_mutex, portMAX_DELAY);
	held = ack_hold_core_on_tx(&s_tab, frame, len, s_hold_us != 0, &early);
	if (held && !s_armed) {
		s_armed = true;
		arm = true;
	}
	xSemaphoreGive(s_mutex);

	/* 要提前发的那个已经从表里拿出来了，定时器看不到它；它和调用方随后发的这一帧都在本线程里发，先后不会乱 */
	if (early.len && s_send(early.data, early.len, true) != ESP_OK) {
		xSemaphoreTake(s_mutex, portMAX_DELAY);
		s_tab.st.dropped++;
		xSemaphoreGive(s_mutex);
	}
	if (arm)
		hold_timer_arm(false);
	return held;
}

void tcp_ack_hold_set_us(uint32_t us)
{
	s_hold_us = us > ACK_HOLD_MAX_US ? ACK_HOLD_MAX_US : us;
}

uint32_t tcp_ack_hold_get_us(void)
{
	return s_hold_us;
}

void tcp_ack_hold_get_stats(struct ack_hold_stats *st)
{
	memset(st, 0, sizeof(*st));
	if (!s_mutex)
		return;
	xSemaphoreTake(s_mutex, portMAX_DELAY);
	*st = s_tab.st;
	xSemaphoreGive(s_mutex);
}
