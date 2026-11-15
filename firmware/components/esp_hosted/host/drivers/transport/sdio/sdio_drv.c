// SPDX-License-Identifier: Apache-2.0
// Copyright 2015-2023 Espressif Systems (Shanghai) PTE LTD
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

/** Includes **/
#include "string.h"
#include "sdio_drv.h"
#include "sdio_reg.h"
#include "serial_drv.h"
#include "stats.h"
#include "esp_log.h"
#include "esp_hosted_log.h"
#include "hci_drv.h"
#include "endian.h"
#include "esp_hosted_transport_init.h"
#include "esp_hosted_bufstat.h"
#include "tcp_ack_hold.h"
#include "esp_heap_caps.h"
#include "esp_system.h"

static const char TAG[] = "H_SDIO_DRV";

/* ════ 本工程对这个文件的改动（完整说明见组件根目录 LOCAL_CHANGES.md）════
 * 原实现的收发缓冲全在内部 DMA 内存里，用量没有上限，分配失败就断言重启；
 * 局域网里一阵突发流量（比如大包 ping 洪泛）就能把设备打重启。改成：
 *  1. 流式接收只用一块开机分配的定长缓冲，一次突发比它大就分几段读，读一段拆一段；
 *  2. 拆出来的每包缓冲放 PSRAM（它只是拷贝目标，不做 SDIO 传输），块数有上限；
 *  3. 所有分配失败都改成丢包并计数，不再断言。
 * 接收路径是按流式模式重写的，其它接收模式没有保留。
 */
#if H_SDIO_HOST_RX_MODE != H_SDIO_HOST_STREAMING_MODE
#error "本工程的 esp_hosted 副本只支持 SDIO 流式接收（CONFIG_ESP_HOSTED_SDIO_OPTIMIZATION_RX_STREAMING_MODE）"
#endif

/* when enabled, read all required SDIO slave registers in a single
 * read into a buffer, instead of reading individual SDIO slave
 * registers
 */
#define DO_COMBINED_REG_READ (1)

/** Constants/Macros **/
#define TO_SLAVE_QUEUE_SIZE               H_SDIO_TX_Q
#define FROM_SLAVE_QUEUE_SIZE             H_SDIO_RX_Q

#define RX_TASK_STACK_SIZE                4096
#define TX_TASK_STACK_SIZE                4096
#define PROCESS_RX_TASK_STACK_SIZE        4096
#define RX_TIMEOUT_TICKS                  50

#define BUFFER_AVAILABLE                  1
#define BUFFER_UNAVAILABLE                0

// max number of time to try to read write buffer available reg
#define MAX_WRITE_BUF_RETRIES             50

/* Actual data sdio_write max retry */
#define MAX_SDIO_WRITE_RETRY              2

/* 流式接收缓冲：每段最多读 24 个 SDIO 块 = 12288 字节 = 8 个最大包。
 * 缓冲前面留一个最大包的位置（RX_STREAM_HEADROOM），放上一段末尾被切开的半个包。
 * 整块缓冲 13824 字节，读任务启动时分配一次，之后不再变。 */
#define RX_STREAM_CHUNK_MAX               (24 * ESP_BLOCK_SIZE)
#define RX_STREAM_HEADROOM                MAX_SDIO_BUFFER_SIZE
_Static_assert((RX_STREAM_HEADROOM % 64) == 0, "DMA 读取的起始地址要保持 64 字节对齐");
_Static_assert(RX_STREAM_CHUNK_MAX >= MAX_SDIO_BUFFER_SIZE, "段长比一个最大包还小没有意义：每个包都要跨段拼接");
_Static_assert((RX_STREAM_CHUNK_MAX % ESP_BLOCK_SIZE) == 0, "除最后一段外每段都要读整块");

/* 出错后的恢复（理由见 sdio_recover_by_restart）：
 * - 一笔读（数据或寄存器）出错，同一笔最多重读 RX_READ_RETRIES 次，还不行就重启主机；
 * - 连续 RX_BAD_BURSTS_RESTART 次突发都拆不出包（流错位）也重启主机。
 * 连着重启的次数上限、到了上限判定链路故障，这些在传输层（transport_drv.c）。 */
#define RX_READ_RETRIES                   2
#define RX_BAD_BURSTS_RESTART             8

/* 每包接收缓冲的块数上限。在用的块数有结构性上限：三个优先级队列全满，
 * 加上处理任务手里的一个、读任务正在入队的一个。 */
#define RX_PKT_POOL_MAX_BLOCKS            (MAX_PRIORITY_QUEUES * FROM_SLAVE_QUEUE_SIZE + 2)

// this locks the sdio transaction at the driver level, instead of at the HAL layer
#define USE_DRIVER_LOCK

#if defined(USE_DRIVER_LOCK)
#define ACQUIRE_LOCK false
#else
#define ACQUIRE_LOCK true
#endif

#if defined(USE_DRIVER_LOCK)
static void * sdio_bus_lock;

#define SDIO_DRV_LOCK()   g_h.funcs->_h_lock_mutex(sdio_bus_lock, HOSTED_BLOCK_MAX);
#define SDIO_DRV_UNLOCK() g_h.funcs->_h_unlock_mutex(sdio_bus_lock);

#else
#define SDIO_DRV_LOCK()
#define SDIO_DRV_UNLOCK()
#endif

#if DO_COMBINED_REG_READ
// read data from ESP_SLAVE_INT_RAW_REG to ESP_SLAVE_PACKET_LEN_REG
// plus 4 for the len of the register
#define REG_BUF_LEN (ESP_SLAVE_PACKET_LEN_REG - ESP_SLAVE_INT_RAW_REG + 4)

// byte index into the buffer to locate the register
#define INT_RAW_INDEX (0)
#define PACKET_LEN_INDEX (ESP_SLAVE_PACKET_LEN_REG - ESP_SLAVE_INT_RAW_REG)

static uint8_t *reg_buf = NULL;
#endif

/* Create mempool for cache mallocs */
/* 非零拷贝发送（串口命令、蓝牙 HCI 走这条路）的拷贝缓冲：内部 DMA 内存，一块，常驻。
 * 只有写任务用，而且一次只发一包，所以不需要池子（原版和接收共用一个只增不减的池）。 */
static uint8_t * tx_copy_buf;

/* TODO to move this in transport drv */
extern transport_channel_t *chan_arr[ESP_MAX_IF];

static void * sdio_handle = NULL;
static void * sdio_bus_lock;
static void * sdio_read_thread;
static void * sdio_process_rx_thread;
static void * sdio_write_thread;

static queue_handle_t to_slave_queue[MAX_PRIORITY_QUEUES];
semaphore_handle_t sem_to_slave_queue;
static queue_handle_t from_slave_queue[MAX_PRIORITY_QUEUES];
semaphore_handle_t sem_from_slave_queue;

/* Counter to hold the amount of buffers already sent to sdio slave */
static uint32_t sdio_tx_buf_count = 0;

/* Counter to hold the amount of bytes already received from sdio slave */
static uint32_t sdio_rx_byte_count = 0;

// one-time trigger to start write thread
static bool sdio_start_write_thread = false;

/* 流式接收缓冲（内部 DMA 内存，读任务启动时分配一次）：
 * [ RX_STREAM_HEADROOM 字节：上一段切剩的半个包 ][ RX_STREAM_CHUNK_MAX 字节：SDIO 每段读到这里 ] */
static uint8_t * rx_stream_buf;

/* 每包接收缓冲的内存池（PSRAM） */
static struct mempool * buf_mp_rx_g;

/* 丢包计数（各自只有一个任务在写） */
static uint32_t rx_drop_nomem;       /* 读任务：拿不到每包缓冲 */
static uint32_t rx_drop_bad_stream;  /* 读任务：流里出现坏包头，丢弃这次突发剩下的部分（按次计） */
static uint32_t rx_drop_copy_nomem;  /* 处理任务：交给上层前的那次拷贝拿不到内存 */
static uint32_t rx_read_err;         /* 读任务：SDIO 读（数据或寄存器）出错（每失败一笔计一次，重读成功的也算） */
static int rx_read_err_last;         /* 读任务：最近一次读出错的错误码 */

/* 突发统计（只有读任务在写）：用来确认分段读确实被走到、离上限还有多远 */
static uint32_t rx_burst_max;        /* 见过的最大一次突发（字节） */
static uint32_t rx_burst_chunked;    /* 超过一段、分了段读的突发次数 */

static uint32_t rx_bad_burst_run;    /* 读任务：连续多少次突发拆不出包 */

/* 发送统计（各自只有一个任务在写：tx_throttle_starts 是读任务，另外两个是写任务） */
static uint32_t tx_slave_buf_waits;  /* 从机一时没有空的接收缓冲、等过它的包数 */
static uint32_t tx_throttle_starts;  /* 从机叫主机暂停往 WiFi 发的次数 */
static uint32_t tx_drop_no_slave_buf;/* 等到头从机还是没有空的接收缓冲，丢掉的包数 */

static esp_err_t sdio_generate_slave_intr(uint8_t intr_no);

static void sdio_write_task(void const* pvParameters);
static void sdio_read_task(void const* pvParameters);
static void sdio_process_rx_task(void const* pvParameters);

static inline void sdio_mempool_create(void)
{
	MEM_DUMP("sdio_mempool_create");
	/* 每包接收缓冲放 PSRAM；备用块全留着（PSRAM 不紧张，省得反复向堆要） */
	buf_mp_rx_g = mempool_create_ex(MAX_SDIO_BUFFER_SIZE, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT,
			RX_PKT_POOL_MAX_BLOCKS, RX_PKT_POOL_MAX_BLOCKS);
#ifdef H_USE_MEMPOOL
	assert(buf_mp_rx_g);
#endif
}

static inline void sdio_mempool_destroy(void)
{
	mempool_destroy(buf_mp_rx_g);
}

/* 每包接收缓冲。拿到时不清零，由 sdio_stream_to_queue 拷进包以后把包后面的部分清零。 */
static inline void *sdio_rx_pkt_alloc(void)
{
	return mempool_alloc(buf_mp_rx_g, MAX_SDIO_BUFFER_SIZE, MEMSET_NOT_REQUIRED);
}

static void sdio_rx_pkt_free(void *buf)
{
	mempool_free(buf_mp_rx_g, buf);
}

/* 接收异常的计数和告警。告警限流：第一次立刻打，之后每 10 秒最多一条，带上累计数。
 * 高负载下逐包打日志，光是打日志就能把收包任务拖死。 */
static void sdio_rx_note(uint32_t *counter, const char *what)
{
	static uint32_t last_tick;   /* 0 = 还没打过。几个任务共用，偶尔多打或少打一条无所谓 */
	uint32_t now = (uint32_t)xTaskGetTickCount();

	(*counter)++;
	if (last_tick == 0 || (now - last_tick) >= pdMS_TO_TICKS(10000)) {
		last_tick = now ? now : 1;
		ESP_LOGW(TAG, "%s（累计：没有每包缓冲 %"PRIu32"，坏流 %"PRIu32" 次，上交拷贝没内存 %"PRIu32"，SDIO 读出错 %"PRIu32" 次、最近错误码 0x%x）",
				what, rx_drop_nomem, rx_drop_bad_stream, rx_drop_copy_nomem, rx_read_err, (unsigned)rx_read_err_last);
	}
}

/* SDIO 这一层靠自己恢复不了的时候重启主机。本文件里所有"重启恢复"都走这里：
 *
 * 1. 初始化：SDIO 卡初始化失败（从机没上电、没固件、线路不通）。
 * 2. 接收：一笔数据重读几次都读不出来。从机（IDF sdio_slave 的流模式）是一批一批发的：把排着队的包
 *    全部取出来当作在途的一批、更新长度寄存器、发新包中断，要等主机把这一批读完才取下一批。
 *    主机没读完就放下的话，从机一直停在这一批上，不再有新包中断，接收从此停住
 *    （实测：WiFi 收不到任何包，RPC 全部超时）。
 * 3. 接收：中断来了，但寄存器（中断状态、可读长度）重读几次都读不出来。不知道有没有新包、有多长，
 *    后果和第 2 条一样。
 * 4. 接收：读得出来、但连续多次突发都拆不出包。主机和从机对"读到哪了"对不上了，
 *    流里没有同步标记，对不回来。
 * 5. 发送：从机不响应、发送重试后仍然失败（上游原有的两处，原来是直接重启）。
 *
 * 连着重启有次数上限，到了就判定链路故障（传输层的 transport_recover_by_restart）：
 * 这次开机里不再收发，也不再碰总线；本文件里 esp_hosted_tx、读任务、写任务都看 transport_is_failed()。
 * 返回 = 没有重启（判定故障了，或者另一个任务已经在处理）。 */
static void sdio_recover_by_restart(const char *why)
{
	ESP_LOGE(TAG, "%s（累计坏流 %"PRIu32" 次，SDIO 读出错 %"PRIu32" 次、最近错误码 0x%x）",
			why, rx_drop_bad_stream, rx_read_err, (unsigned)rx_read_err_last);
	transport_recover_by_restart(why);
}

/* 链路判定故障以后（或者初始化失败、正等着重启的时候）读任务停在这里，不再回去。
 * 任务函数不能返回（FreeRTOS 里任务函数返回 = abort）。 */
static void sdio_task_park(void)
{
	for (;;)
		vTaskDelay(portMAX_DELAY);
}

/* 读任务每读完一次突发调一次。good = 整次突发都拆成了包。
 * 偶发的单个坏包只坏一次突发，下一次突发从包的边界重新开始，不会连续。 */
static void sdio_rx_burst_done(bool good)
{
	if (good) {
		rx_bad_burst_run = 0;
		return;
	}

	if (++rx_bad_burst_run >= RX_BAD_BURSTS_RESTART)
		sdio_recover_by_restart("接收流连续多次突发错位");
}

void transport_deinit_internal(void)
{
	/* TODO */
}

static int sdio_generate_slave_intr(uint8_t intr_no)
{
	uint8_t intr_mask = BIT(intr_no + ESP_SDIO_CONF_OFFSET);

	if (intr_no >= BIT(ESP_MAX_HOST_INTERRUPT)) {
		ESP_LOGE(TAG,"Invalid slave interrupt number");
		return ESP_ERR_INVALID_ARG;
	}

	return g_h.funcs->_h_sdio_write_reg(sdio_handle, HOST_TO_SLAVE_INTR, &intr_mask,
		sizeof(intr_mask), ACQUIRE_LOCK);
}

static inline int sdio_get_intr(uint32_t *interrupts)
{
	return g_h.funcs->_h_sdio_read_reg(sdio_handle, ESP_SLAVE_INT_RAW_REG, (uint8_t *)interrupts,
		sizeof(uint32_t), ACQUIRE_LOCK);
}

static inline int sdio_clear_intr(uint32_t interrupts)
{
	return g_h.funcs->_h_sdio_write_reg(sdio_handle, ESP_SLAVE_INT_CLR_REG, (uint8_t *)&interrupts,
		sizeof(uint32_t), ACQUIRE_LOCK);
}

static int sdio_get_tx_buffer_num(uint32_t *tx_num, bool is_lock_needed)
{
	uint32_t len = 0;
	int ret = 0;

	ret = g_h.funcs->_h_sdio_read_reg(sdio_handle, ESP_SLAVE_TOKEN_RDATA, (uint8_t *)&len,
		sizeof(len), is_lock_needed);

	if (ret) {
		ESP_LOGE(TAG, "%s: err: %d", __func__, ret);
		return ret;
	}

	len = (len >> 16) & ESP_TX_BUFFER_MASK;
	len = (len + ESP_TX_BUFFER_MAX - sdio_tx_buf_count) % ESP_TX_BUFFER_MAX;

	*tx_num = len;

	return ret;
}

#if DO_COMBINED_REG_READ
static int sdio_read_regs(uint8_t * buf)
{
	return g_h.funcs->_h_sdio_read_reg(sdio_handle, ESP_SLAVE_INT_RAW_REG, buf, REG_BUF_LEN, ACQUIRE_LOCK);
}
#endif

#if H_SDIO_HOST_RX_MODE != H_SDIO_ALWAYS_HOST_RX_MAX_TRANSPORT_SIZE

#if DO_COMBINED_REG_READ
// get the length from the provided register value
static int sdio_get_len_from_slave(uint32_t *rx_size, uint32_t reg_val, bool is_lock_needed)
{
	uint32_t len = reg_val;
	uint32_t temp;

	if (!rx_size)
		return ESP_FAIL;
	*rx_size = 0;

	len &= ESP_SLAVE_LEN_MASK;

	if (len >= sdio_rx_byte_count)
		len = (len + ESP_RX_BYTE_MAX - sdio_rx_byte_count) % ESP_RX_BYTE_MAX;
	else {
		/* Handle a case of roll over */
		temp = ESP_RX_BYTE_MAX - sdio_rx_byte_count;
		len = temp + len;
	}

#if H_SDIO_HOST_RX_MODE != H_SDIO_HOST_STREAMING_MODE
	if (len > ESP_RX_BUFFER_SIZE) {
		ESP_LOGE(TAG, "%s: Len from slave[%ld] exceeds max [%d]",
				__func__, len, ESP_RX_BUFFER_SIZE);
		return ESP_FAIL;
	}
#endif

	*rx_size = len;

	return 0;
}
#else
// get the length by reading the register
static int sdio_get_len_from_slave(uint32_t *rx_size, bool is_lock_needed)
{
	uint32_t len;
	uint32_t temp;
	int ret = 0;

	if (!rx_size)
		return ESP_FAIL;
	*rx_size = 0;

	ret = g_h.funcs->_h_sdio_read_reg(sdio_handle, ESP_SLAVE_PACKET_LEN_REG,
		(uint8_t *)&len, sizeof(len), is_lock_needed);

	if (ret) {
		ESP_LOGE(TAG, "len read err: %d", ret);
		return ret;
	}

	len &= ESP_SLAVE_LEN_MASK;

	if (len >= sdio_rx_byte_count)
		len = (len + ESP_RX_BYTE_MAX - sdio_rx_byte_count) % ESP_RX_BYTE_MAX;
	else {
		/* Handle a case of roll over */
		temp = ESP_RX_BYTE_MAX - sdio_rx_byte_count;
		len = temp + len;
	}

#if H_SDIO_HOST_RX_MODE != H_SDIO_HOST_STREAMING_MODE
	if (len > ESP_RX_BUFFER_SIZE) {
		ESP_LOGE(TAG, "%s: Len from slave[%ld] exceeds max [%d]",
				__func__, len, ESP_RX_BUFFER_SIZE);
		return ESP_FAIL;
	}
#endif

	*rx_size = len;

	return 0;
}
#endif

#endif

static int sdio_is_write_buffer_available(uint32_t buf_needed)
{
	static uint32_t buf_available = 0;
	uint8_t retry = MAX_WRITE_BUF_RETRIES;
	uint32_t max_retry_sdio_not_responding = 2;

	/*If buffer needed are less than buffer available
	  then only read for available buffer number from slave*/
	if (buf_available < buf_needed) {
		while (retry) {
			if (sdio_get_tx_buffer_num(&buf_available, ACQUIRE_LOCK) ==
					ESP_HOSTED_SDIO_UNRESPONSIVE_CODE) {
				max_retry_sdio_not_responding--;
				/* restart the host to avoid the sdio locked out state */

				if (!max_retry_sdio_not_responding) {
					/* 本工程改动：原版直接重启主机。走到下一行说明没有重启，这一包发不出去 */
					sdio_recover_by_restart("SDIO 从机不响应");
					return BUFFER_UNAVAILABLE;
				}
				continue;
			}

			if (buf_available < buf_needed) {

				ESP_LOGV(TAG, "Retry get write buffers %d", retry);
				retry--;

				if (retry < MAX_WRITE_BUF_RETRIES/2)
					g_h.funcs->_h_msleep(1);

				continue;
			}
			break;
		}
	}

	if (buf_available >= buf_needed)
		buf_available -= buf_needed;

	if (retry != MAX_WRITE_BUF_RETRIES)
		tx_slave_buf_waits++;

	if (!retry) {
		/* No buffer available at slave */
		tx_drop_no_slave_buf++;
		return BUFFER_UNAVAILABLE;
	}

	return BUFFER_AVAILABLE;
}

static void sdio_write_task(void const* pvParameters)
{
	uint16_t len = 0;
	uint8_t *sendbuf = NULL;
	void (*free_func)(void* ptr) = NULL;
	struct esp_payload_header * payload_header = NULL;
	uint8_t * payload  = NULL;
	interface_buffer_handle_t buf_handle = {0};
	int retries = 0;

	int ret = 0;
	uint8_t *pos = NULL;
	uint32_t data_left;
	uint32_t len_to_send;
	uint32_t buf_needed;
	uint8_t tx_needed = 1;

	while (!sdio_start_write_thread) {
		/* 握手没完成就判定了链路故障：不会再有东西要发，停住（不然这里 10 ms 一醒，一直到关机） */
		if (transport_is_failed())
			sdio_task_park();
		g_h.funcs->_h_msleep(10);
	}

	for (;;) {
		/* Check if higher layers have anything to transmit */
		g_h.funcs->_h_get_semaphore(sem_to_slave_queue, HOSTED_BLOCK_MAX);

		/* Tx msg is present as per sem */
		if (g_h.funcs->_h_dequeue_item(to_slave_queue[PRIO_Q_SERIAL], &buf_handle, 0))
			if (g_h.funcs->_h_dequeue_item(to_slave_queue[PRIO_Q_BT], &buf_handle, 0))
				if (g_h.funcs->_h_dequeue_item(to_slave_queue[PRIO_Q_OTHERS], &buf_handle, 0)) {
					tx_needed = 0; /* No Tx msg */
				}

		if (tx_needed)
			len = buf_handle.payload_len;

		if (!len) {
			ESP_LOGE(TAG, "%s: Empty len", __func__);
			goto done;
		}

		if (!buf_handle.payload_zcopy) {
			/* 用常驻的拷贝缓冲，不用释放（原实现每包从池里拿一块，拿不到就断言重启） */
			sendbuf = tx_copy_buf;
			g_h.funcs->_h_memset(sendbuf, 0, MAX_SDIO_BUFFER_SIZE);
			free_func = NULL;
		} else {
			sendbuf = buf_handle.payload;
			free_func = buf_handle.free_buf_handle;
		}

		if (!sendbuf) {
			ESP_LOGE(TAG, "%s: Tx buffer is NULL", __func__);
			free_func = NULL;
			goto done;
		}

		if (buf_handle.payload_len > MAX_SDIO_BUFFER_SIZE - sizeof(struct esp_payload_header)) {
			ESP_LOGE(TAG, "Pkt len [%u] > Max [%u]. Drop",
					buf_handle.payload_len, MAX_SDIO_BUFFER_SIZE - sizeof(struct esp_payload_header));
			goto done;
		}

		/* Form Tx header */
		payload_header = (struct esp_payload_header *) sendbuf;
		payload  = sendbuf + sizeof(struct esp_payload_header);

		payload_header->len = htole16(len);
		payload_header->offset = htole16(sizeof(struct esp_payload_header));
		payload_header->if_type = buf_handle.if_type;
		payload_header->if_num = buf_handle.if_num;
		payload_header->seq_num = htole16(buf_handle.seq_num);
		payload_header->flags = buf_handle.flag;

		if (payload_header->if_type == ESP_HCI_IF) {
			// special handling for HCI
			if (!buf_handle.payload_zcopy) {
				// copy first byte of payload into header
				payload_header->hci_pkt_type = buf_handle.payload[0];
				// adjust actual payload len
				len -= 1;
				payload_header->len = htole16(len);
				g_h.funcs->_h_memcpy(payload, &buf_handle.payload[1], len);
			}
		} else
		if (!buf_handle.payload_zcopy)
			g_h.funcs->_h_memcpy(payload, buf_handle.payload, len);

#if H_SDIO_CHECKSUM
		payload_header->checksum = htole16(compute_checksum(sendbuf,
			sizeof(struct esp_payload_header) + len));
#endif

		buf_needed = (len + sizeof(struct esp_payload_header) + ESP_RX_BUFFER_SIZE - 1)
			/ ESP_RX_BUFFER_SIZE;

		/* 链路已经判定故障：队列里剩下的包直接丢，不再碰总线 */
		if (transport_is_failed())
			goto done;

		SDIO_DRV_LOCK();

		ret = sdio_is_write_buffer_available(buf_needed);
		if (ret != BUFFER_AVAILABLE) {
			ESP_LOGV(TAG, "no SDIO write buffers on slave device");
			goto unlock_done;
		}

		pos = sendbuf;
		data_left = len + sizeof(struct esp_payload_header);

		ESP_HEXLOGV("h_sdio_tx", sendbuf, min(32,data_left));

		len_to_send = 0;
		retries = 0;
		do {
			len_to_send = data_left;

#if H_SDIO_TX_BLOCK_ONLY_XFER
			/* Extend the transfer length to do block only transfers.
			 * This is safe as slave only reads up to data_left, which
			 * is not changed here. Rest of data is discarded by
			 * slave.
			 */
			uint32_t block_send_len = ((len_to_send + ESP_BLOCK_SIZE - 1) / ESP_BLOCK_SIZE) * ESP_BLOCK_SIZE;

			ret = g_h.funcs->_h_sdio_write_block(sdio_handle, ESP_SLAVE_CMD53_END_ADDR - data_left,
				pos, block_send_len, ACQUIRE_LOCK);
#else
			ret = g_h.funcs->_h_sdio_write_block(sdio_handle, ESP_SLAVE_CMD53_END_ADDR - data_left,
				pos, len_to_send, ACQUIRE_LOCK);
#endif
			if (ret) {
				ESP_LOGE(TAG, "%s: %d: Failed to send data: %d %ld %ld", __func__,
					retries, ret, len_to_send, data_left);
				retries++;
				if (retries < MAX_SDIO_WRITE_RETRY) {
					ESP_LOGD(TAG, "retry");
					continue;
				} else {
					SDIO_DRV_UNLOCK();
					/* 本工程改动：原版直接重启主机 */
					sdio_recover_by_restart("SDIO 发送重试后仍然出错");
					goto done;
				}
			}

			data_left -= len_to_send;
			pos += len_to_send;
		} while (data_left);

		sdio_tx_buf_count += buf_needed;
		sdio_tx_buf_count = sdio_tx_buf_count % ESP_TX_BUFFER_MAX;

#if ESP_PKT_STATS
			if (buf_handle.if_type == ESP_STA_IF)
				pkt_stats.sta_tx_out++;
#endif

unlock_done:
		SDIO_DRV_UNLOCK();
done:
		if (len && !buf_handle.payload_zcopy) {
			/* free allocated buffer, only if zerocopy is not requested */
			H_FREE_PTR_WITH_FUNC(buf_handle.free_buf_handle, buf_handle.priv_buffer_handle);
		}
		H_FREE_PTR_WITH_FUNC(free_func, sendbuf);
	}
}

static int is_valid_sdio_rx_packet(uint8_t *rxbuff_a, uint16_t *len_a, uint16_t *offset_a)
{
	struct esp_payload_header * h = (struct esp_payload_header *)rxbuff_a;
	uint16_t len = 0, offset = 0;
#if H_SDIO_CHECKSUM
	uint16_t rx_checksum = 0, checksum = 0;
#endif

	if (!h || !len_a || !offset_a)
		return 0;

	/* Fetch length and offset from payload header */
	len = le16toh(h->len);
	offset = le16toh(h->offset);

	if ((!len) ||
		(len > MAX_PAYLOAD_SIZE) ||
		(offset != sizeof(struct esp_payload_header))) {

		/* Free up buffer, as one of following -
		 * 1. no payload to process
		 * 2. input packet size > driver capacity
		 * 3. payload header size mismatch,
		 * wrong header/bit packing?
		 * */
		return 0;

	}

#if H_SDIO_CHECKSUM
	rx_checksum = le16toh(h->checksum);
	h->checksum = 0;
	checksum = compute_checksum((uint8_t*)h, len + offset);

	if (checksum != rx_checksum) {
		ESP_LOGE(TAG, "SDIO RX rx_chksum[%u] != checksum[%u]. Drop.",
				checksum, rx_checksum);
		return 0;
	}
#endif

#if ESP_PKT_STATS
	if (h->if_type == ESP_STA_IF)
		pkt_stats.sta_rx_in++;
#endif

	*len_a = len;
	*offset_a = offset;

	return 1;
}

// pushes received packet data on to rx queue
static esp_err_t sdio_push_pkt_to_queue(uint8_t * rxbuff, uint16_t len, uint16_t offset)
{
	uint8_t pkt_prio = PRIO_Q_OTHERS;
	struct esp_payload_header *h= NULL;
	interface_buffer_handle_t buf_handle;

	h = (struct esp_payload_header *)rxbuff;

	memset(&buf_handle, 0, sizeof(interface_buffer_handle_t));

	buf_handle.priv_buffer_handle = rxbuff;
	buf_handle.free_buf_handle    = sdio_rx_pkt_free;
	buf_handle.payload_len        = len;
	buf_handle.if_type            = h->if_type;
	buf_handle.if_num             = h->if_num;
	buf_handle.payload            = rxbuff + offset;
	buf_handle.seq_num            = le16toh(h->seq_num);
	buf_handle.flag               = h->flags;

	if (buf_handle.if_type == ESP_SERIAL_IF)
		pkt_prio = PRIO_Q_SERIAL;
	else if (buf_handle.if_type == ESP_HCI_IF)
		pkt_prio = PRIO_Q_BT;
	/* else OTHERS by default */

	g_h.funcs->_h_queue_item(from_slave_queue[pkt_prio], &buf_handle, HOSTED_BLOCK_MAX);
	g_h.funcs->_h_post_semaphore(sem_from_slave_queue);

	return ESP_OK;
}

/* 把一段数据流拆成包、逐个入队。
 * 返回没用完的尾巴长度（末尾不足一个整包的部分，调用方留着和下一段拼）；
 * 流里出现坏包头时返回 -1：流是靠包头里的长度一个接一个定位的，坏了一个后面就都对不上了。 */
static int sdio_stream_to_queue(uint8_t * buf, uint32_t buf_len)
{
	struct esp_payload_header * h = NULL;
	uint8_t * pkt_rxbuff = NULL;
	uint16_t len = 0;
	uint16_t offset = 0;
	uint32_t packet_size;

	while (buf_len) {
		if (buf_len < sizeof(struct esp_payload_header))
			break;   /* 包头还没到齐 */

		/* 整包到齐之前只能先看包头里的长度（校验和要整包到齐才能算） */
		h = (struct esp_payload_header *)buf;
		len = le16toh(h->len);
		offset = le16toh(h->offset);
		if ((!len) || (len > MAX_PAYLOAD_SIZE) ||
				(offset != sizeof(struct esp_payload_header)))
			return -1;

		packet_size = (uint32_t)len + offset;
		if (packet_size > buf_len)
			break;   /* 这个包还没到齐，留给下一段 */

		if (!is_valid_sdio_rx_packet(buf, &len, &offset))
			return -1;

		pkt_rxbuff = sdio_rx_pkt_alloc();
		if (pkt_rxbuff) {
			memcpy(pkt_rxbuff, buf, packet_size);
			/* 包后面清零（原版是整块缓冲先清零再拷）：下游有的处理函数（HCI、INIT 事件）不核对包长
			 * 就按包内字段读，从机发来畸形短包时，包后面读到的应该是 0，而不是上一个包留下的数据。 */
			memset(pkt_rxbuff + packet_size, 0, MAX_SDIO_BUFFER_SIZE - packet_size);
			sdio_push_pkt_to_queue(pkt_rxbuff, len, offset);
		} else {
			/* 拿不到每包缓冲：丢这一个包，流照常往下拆（原实现在这里断言重启） */
			sdio_rx_note(&rx_drop_nomem, "丢包：没有每包缓冲");
		}

		buf_len -= packet_size;
		buf     += packet_size;
	}

	return (int)buf_len;
}

static void sdio_read_task(void const* pvParameters)
{
	esp_err_t res;
	int ret;
	uint32_t len_from_slave;

	uint32_t data_left;
	uint32_t len_to_read;
	uint8_t *pos;
	uint32_t interrupts;
	uint32_t carry;       /* 上一段切剩、放在 pos 前面的半个包有多长 */
	bool stream_ok;       /* 这次突发里有没有出现过坏包头 */
	int retries;          /* 当前这一笔已经重读了几次 */
	int leftover;

#if DO_COMBINED_REG_READ
	uint32_t *intr_index = NULL;
	uint32_t *read_len_index = NULL;
#endif

	assert(sdio_handle);

	// wait for transport to be in reset state
	while (true) {
		vTaskDelay(pdMS_TO_TICKS(100));
		if (is_transport_rx_ready()) {
			break;
		}
	}

	res = g_h.funcs->_h_sdio_card_init(sdio_handle);
	if (res != ESP_OK) {
		/* 本工程改动：原版在这里从任务函数返回（= abort）。走重启恢复；没重启就是判定故障了，停住。 */
		ESP_LOGE(TAG, "sdio card init failed");
		sdio_recover_by_restart("SDIO 卡初始化失败");
		sdio_task_park();
	}

	create_debugging_tasks();

#if DO_COMBINED_REG_READ
	reg_buf = MEM_ALLOC(REG_BUF_LEN);
	assert(reg_buf);
#endif

	// display which SDIO mode we are operating in
#if H_SDIO_HOST_RX_MODE == H_SDIO_HOST_STREAMING_MODE
	ESP_LOGI(TAG, "SDIO Host operating in STREAMING MODE");
#else
	ESP_LOGI(TAG, "SDIO Host operating in PACKET MODE");
#endif

	ESP_LOGI(TAG, "generate slave intr");

	// inform the slave device that we are ready
	sdio_generate_slave_intr(ESP_OPEN_DATA_PATH);

	for (;;) {
		/* 链路已经判定故障：不再碰总线，任务停在这里 */
		if (transport_is_failed())
			sdio_task_park();

		// wait for sdio interrupt from slave
		// call will block until there is an interrupt, timeout or error
		res = g_h.funcs->_h_sdio_wait_slave_intr(sdio_handle, HOSTED_BLOCK_MAX);

		/* 等中断的时候别的任务判定了故障：回到循环开头停住 */
		if (transport_is_failed())
			continue;

		if (res != ESP_OK) {
			ESP_LOGE(TAG, "wait_slave_intr error: %d", res);
			continue;
		}

		SDIO_DRV_LOCK();

#if DO_COMBINED_REG_READ
		/* 本工程改动：原版读不出来就放下、等下一次中断。读这几个寄存器没有副作用，先重读；
		 * 还不行就走重启恢复（理由见 sdio_recover_by_restart 第 3 条）。 */
		for (retries = 0; ; retries++) {
			ret = sdio_read_regs(reg_buf);
			if (!ret)
				break;
			rx_read_err_last = ret;
			if (retries >= RX_READ_RETRIES) {
				sdio_rx_note(&rx_read_err, "读 SDIO 寄存器出错，重读次数用完");
				break;
			}
			sdio_rx_note(&rx_read_err, "读 SDIO 寄存器出错，重读");
		}
		if (ret) {
			SDIO_DRV_UNLOCK();
			sdio_recover_by_restart("读 SDIO 寄存器重读后仍然出错");
			continue;
		}

		intr_index = (uint32_t *)&reg_buf[INT_RAW_INDEX];
		read_len_index = (uint32_t *)&reg_buf[PACKET_LEN_INDEX];

		interrupts = *intr_index;
#else
		// clear slave interrupts
		if (sdio_get_intr(&interrupts)) {
			ESP_LOGE(TAG, "failed to read interrupt register");

			SDIO_DRV_UNLOCK();
			/* 本工程改动：原版直接重启主机 */
			sdio_recover_by_restart("读 SDIO 中断寄存器出错");
			continue;
		}
#endif
		sdio_clear_intr(interrupts);

		ESP_LOGV(TAG, "Intr: %08"PRIX32, interrupts);

		/* Check all supported interrupts */
		if (BIT(SDIO_INT_START_THROTTLE) & interrupts) {
			wifi_tx_throttling = 1;
			tx_throttle_starts++;
		}

		if (BIT(SDIO_INT_STOP_THROTTLE) & interrupts)
			wifi_tx_throttling = 0;

		if (!(BIT(SDIO_INT_NEW_PACKET) & interrupts)) {

			SDIO_DRV_UNLOCK();
			continue;
		}

#if H_SDIO_HOST_RX_MODE == H_SDIO_ALWAYS_HOST_RX_MAX_TRANSPORT_SIZE
		/* Bypass the check to find the bytes to be read from slave to host
		 * always assume max transport size to be read.
		 * slave sdio driver will automatically pad the remaining bytes after
		 * actual written bytes till requested size from host
		 * This typically improves throughput for larger packet sizes
		 **/
		len_from_slave = MAX_TRANSPORT_BUFFER_SIZE;
#else
		/* check the length to be read */
#if DO_COMBINED_REG_READ
		ret = sdio_get_len_from_slave(&len_from_slave, *read_len_index, ACQUIRE_LOCK);
#else
		ret = sdio_get_len_from_slave(&len_from_slave, ACQUIRE_LOCK);
#endif
		if (ret || !len_from_slave) {
			ESP_LOGD(TAG, "invalid ret or len_from_slave: %d %ld", ret, len_from_slave);

			SDIO_DRV_UNLOCK();
			continue;
		}
#endif

		/* 分段读进定长缓冲，读一段拆一段。
		 * SDIO 从机协议允许一次读取分多笔传输：每笔的地址是 "结束地址 - 还剩多少没读"，
		 * 除最后一笔外每笔都是整块（RX_STREAM_CHUNK_MAX 是块大小的整数倍）。
		 * 从机报的可读长度总是落在包的边界上，所以半个包只会出现在段与段之间，
		 * 不会跨到下一次突发。 */
		if (len_from_slave > rx_burst_max)
			rx_burst_max = len_from_slave;
		if (len_from_slave > RX_STREAM_CHUNK_MAX)
			rx_burst_chunked++;

		data_left = len_from_slave;
		pos = rx_stream_buf + RX_STREAM_HEADROOM;
		carry = 0;
		stream_ok = true;
		ret = 0;
		retries = 0;

		do {
			len_to_read = (data_left > RX_STREAM_CHUNK_MAX) ? RX_STREAM_CHUNK_MAX : data_left;

#if H_SDIO_RX_BLOCK_ONLY_XFER
			/* Extend the transfer length to do block only transfers.
			 * This is safe as slave will pad data with 0, which we
			 * will ignore.
			 * 只有最后一段可能不是整块，补齐不会读走后面的真数据。
			 */
			uint32_t block_read_len = ((len_to_read + ESP_BLOCK_SIZE - 1) / ESP_BLOCK_SIZE) * ESP_BLOCK_SIZE;
			ret = g_h.funcs->_h_sdio_read_block(sdio_handle,
					ESP_SLAVE_CMD53_END_ADDR - data_left,
					pos, block_read_len, ACQUIRE_LOCK);
#else
			ret = g_h.funcs->_h_sdio_read_block(sdio_handle,
					ESP_SLAVE_CMD53_END_ADDR - data_left,
					pos, len_to_read, ACQUIRE_LOCK);
#endif
			/* 拆包和入队不占着总线锁：入队在队列满时会等，不能让发送任务跟着一起等 */
			SDIO_DRV_UNLOCK();

			if (ret) {
				/* 同一笔重读。地址是"结束地址 - 还剩多少没读"，从机没把这一笔发出来的话，重读拿到的就是它，
				 * 一个字节都不丢。不能读不出来就放下这次突发去等下一次：从机要等主机把这一批读完
				 * 才发下一批（见 sdio_recover_by_restart）。
				 * 从机其实已经发出来、只是主机没收到的话，重读会读过头，从机就此不再响应（实测），
				 * 重读失败后走重启。这两种情况主机分不出来；不重读的话前一种会永久停住，而且没有任何迹象。 */
				rx_read_err_last = ret;
				if (retries++ < RX_READ_RETRIES) {
					sdio_rx_note(&rx_read_err, "SDIO 读数据出错，重读同一笔");
					SDIO_DRV_LOCK();
					continue;
				}
				sdio_rx_note(&rx_read_err, "SDIO 读数据出错，重读次数用完");
				break;
			}
			retries = 0;
			data_left -= len_to_read;

			if (stream_ok) {
				leftover = sdio_stream_to_queue(pos - carry, carry + len_to_read);
				if (leftover < 0) {
					/* 坏包头：这次突发剩下的部分照读（不读走，下次的起点就不在包的边界上了）但不再拆 */
					stream_ok = false;
					carry = 0;
					sdio_rx_note(&rx_drop_bad_stream, "丢包：流里出现坏包头，丢弃这次突发剩下的部分");
				} else {
					carry = (uint32_t)leftover;
					if (carry)
						memmove(pos - carry, pos + len_to_read - carry, carry);
				}
			}

			if (data_left)
				SDIO_DRV_LOCK();
		} while (data_left);

		if (ret) {
			sdio_recover_by_restart("SDIO 读数据重读后仍然出错");
		} else {
			if (stream_ok && carry) {
				/* 不该发生：从机报的长度应该落在包的边界上 */
				stream_ok = false;
				sdio_rx_note(&rx_drop_bad_stream, "丢包：突发在半个包处结束");
			}
			sdio_rx_burst_done(stream_ok);
		}

		//TODO: unclear, on failure case
		//sdio_rx_byte_count += (len_from_slave-data_left);
		sdio_rx_byte_count += len_from_slave;
		sdio_rx_byte_count = sdio_rx_byte_count % ESP_RX_BYTE_MAX;
	}
}

/**
 * TODO: unify sdio_process_rx_task() and spi_process_rx_task()
 */
static void sdio_process_rx_task(void const* pvParameters)
{
	interface_buffer_handle_t buf_handle_l = {0};
	interface_buffer_handle_t *buf_handle = NULL;
	int ret = 0;

	struct esp_priv_event *event = NULL;

	while (true) {
		vTaskDelay(pdMS_TO_TICKS(100));
		if (is_transport_rx_ready()) {
			break;
		}
	}
	ESP_LOGI(TAG, "Starting SDIO process rx task");

	while (1) {
		g_h.funcs->_h_get_semaphore(sem_from_slave_queue, HOSTED_BLOCK_MAX);

		if (g_h.funcs->_h_dequeue_item(from_slave_queue[PRIO_Q_SERIAL], &buf_handle_l, 0))
			if (g_h.funcs->_h_dequeue_item(from_slave_queue[PRIO_Q_BT], &buf_handle_l, 0))
				if (g_h.funcs->_h_dequeue_item(from_slave_queue[PRIO_Q_OTHERS], &buf_handle_l, 0)) {
					ESP_LOGI(TAG, "No element in any queue found");
					continue;
				}

		buf_handle = &buf_handle_l;

		ESP_LOGV(TAG, "h_sdio_rx: iftype:%d", (int)buf_handle->if_type);
		ESP_HEXLOGV("h_sdio_rx", buf_handle->payload, min(buf_handle->payload_len,32));

		if (buf_handle->if_type == ESP_SERIAL_IF) {
			/* serial interface path */
			serial_rx_handler(buf_handle);
		} else if((buf_handle->if_type == ESP_STA_IF) ||
				(buf_handle->if_type == ESP_AP_IF)) {
#if 1
			if (chan_arr[buf_handle->if_type] && chan_arr[buf_handle->if_type]->rx) {
				/* 本工程改动：交给上层（lwIP）的这份拷贝放 PSRAM。原版用 malloc，小块优先落在内部 RAM；
				 * 应用来不及读的时候，每条 TCP 连接会攒下一个接收窗口那么多的包（窗口 64K 就是 64KB），
				 * 两三条连接就能把内部 RAM 吃光。上层用 free() 释放，对 PSRAM 的块同样适用。
				 * 留在内部 RAM 的只剩 lwIP 给每个包分配的 pbuf 头（几十字节）。 */
				uint8_t * copy_payload = (uint8_t *)heap_caps_malloc(buf_handle->payload_len,
						MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
				assert(buf_handle->payload_len);
				assert(buf_handle->payload);
				if (!copy_payload) {
					/* 拿不到内存：丢这一个包，每包缓冲由循环末尾统一释放（原实现在这里断言重启） */
					sdio_rx_note(&rx_drop_copy_nomem, "丢包：上交拷贝没内存");
				} else {
					memcpy(copy_payload, buf_handle->payload, buf_handle->payload_len);
					H_FREE_PTR_WITH_FUNC(buf_handle->free_buf_handle, buf_handle->priv_buffer_handle);

					ret = chan_arr[buf_handle->if_type]->rx(chan_arr[buf_handle->if_type]->api_chan,
							copy_payload, copy_payload, buf_handle->payload_len);
					if (unlikely(ret))
						HOSTED_FREE(copy_payload);
				}
			}
#else
			if (chan_arr[buf_handle->if_type] && chan_arr[buf_handle->if_type]->rx) {
				chan_arr[buf_handle->if_type]->rx(chan_arr[buf_handle->if_type]->api_chan,
						buf_handle->payload, NULL, buf_handle->payload_len);
			}
#endif
		} else if (buf_handle->if_type == ESP_PRIV_IF) {
			process_priv_communication(buf_handle);
			hci_drv_show_configuration();
			/* priv transaction received */
			ESP_LOGI(TAG, "Received INIT event");
			sdio_start_write_thread = true;

			event = (struct esp_priv_event *) (buf_handle->payload);
			if (event->event_type != ESP_PRIV_EVENT_INIT) {
				/* User can re-use this type of transaction */
			}
		} else if (buf_handle->if_type == ESP_HCI_IF) {
			hci_rx_handler(buf_handle);
		} else if (buf_handle->if_type == ESP_TEST_IF) {
#if TEST_RAW_TP
			update_test_raw_tp_rx_len(buf_handle->payload_len +
				H_ESP_PAYLOAD_HEADER_OFFSET);
#endif
		} else {
			ESP_LOGW(TAG, "unknown type %d ", buf_handle->if_type);
		}

		/* Free buffer handle */
		/* When buffer offloaded to other module, that module is
		 * responsible for freeing buffer. In case not offloaded or
		 * failed to offload, buffer should be freed here.
		 */
		if (!buf_handle->payload_zcopy) {
			H_FREE_PTR_WITH_FUNC(buf_handle->free_buf_handle,
				buf_handle->priv_buffer_handle);
		}
	}
}

void transport_init_internal(void)
{
	uint8_t prio_q_idx = 0;
	/* register callback */

	sdio_bus_lock = g_h.funcs->_h_create_mutex();
	assert(sdio_bus_lock);

	sem_to_slave_queue = g_h.funcs->_h_create_semaphore(TO_SLAVE_QUEUE_SIZE*MAX_PRIORITY_QUEUES);
	assert(sem_to_slave_queue);
	g_h.funcs->_h_get_semaphore(sem_to_slave_queue, 0);

	sem_from_slave_queue = g_h.funcs->_h_create_semaphore(FROM_SLAVE_QUEUE_SIZE*MAX_PRIORITY_QUEUES);
	assert(sem_from_slave_queue);
	g_h.funcs->_h_get_semaphore(sem_from_slave_queue, 0);

	/* cleanup the semaphores */


	for (prio_q_idx=0; prio_q_idx<MAX_PRIORITY_QUEUES;prio_q_idx++) {
		/* Queue - rx */
		from_slave_queue[prio_q_idx] = g_h.funcs->_h_create_queue(FROM_SLAVE_QUEUE_SIZE, sizeof(interface_buffer_handle_t));
		assert(from_slave_queue[prio_q_idx]);

		/* Queue - tx */
		to_slave_queue[prio_q_idx] = g_h.funcs->_h_create_queue(TO_SLAVE_QUEUE_SIZE, sizeof(interface_buffer_handle_t));
		assert(to_slave_queue[prio_q_idx]);
	}

	sdio_mempool_create();

	/* 流式接收缓冲（读任务用）和非零拷贝发送的拷贝缓冲（写任务用）：各分配一次，之后不变。
	 * 这里由 app_main 开头的 esp_hosted_init() 调到，内部 DMA 内存还是整块的；
	 * 拿不到说明内部内存不够，开机就该暴露出来。
	 * 注意：如果哪天又把 esp_hosted_init() 挪回调度器启动之前（构造函数），这两块不能留在这里——
	 * 那时内部堆只有最前面的一小块（本工程 66KB）可用，多占十几 KB 会让 IDLE 任务的栈分不到合法内存，开机即断言。 */
	if (!rx_stream_buf) {
		rx_stream_buf = (uint8_t *)MEM_ALLOC(RX_STREAM_HEADROOM + RX_STREAM_CHUNK_MAX);
		assert(rx_stream_buf);
	}
	if (!tx_copy_buf) {
		tx_copy_buf = (uint8_t *)MEM_ALLOC(MAX_SDIO_BUFFER_SIZE);
		assert(tx_copy_buf);
	}

	/* initialise SDMMC before starting read/write threads
	 * which depend on SDMMC*/
	sdio_handle = g_h.funcs->_h_bus_init();
	if (!sdio_handle) {
		ESP_LOGE(TAG, "could not create sdio handle, exiting\n");
		assert(sdio_handle);
	}

	sdio_read_thread = g_h.funcs->_h_thread_create("sdio_read",
		DFLT_TASK_PRIO, DFLT_TASK_STACK_SIZE, sdio_read_task, NULL);

	sdio_process_rx_thread = g_h.funcs->_h_thread_create("sdio_process_rx",
		DFLT_TASK_PRIO, DFLT_TASK_STACK_SIZE, sdio_process_rx_task, NULL);

	sdio_write_thread = g_h.funcs->_h_thread_create("sdio_write",
		DFLT_TASK_PRIO, DFLT_TASK_STACK_SIZE, sdio_write_task, NULL);

#if defined(USE_DRIVER_LOCK)
	// initialise mutex for bus locking
	sdio_bus_lock = g_h.funcs->_h_create_mutex();
	assert(sdio_bus_lock);
#endif
}

/* 把一包放进发送队列。timeout = 队列满的时候最多等多久（HOSTED_BLOCK_MAX = 一直等，0 = 不等）。
 * 不管成败，缓冲都交出去了（失败时在这里释放）。 */
static int sdio_tx_enqueue(uint8_t iface_type, uint8_t iface_num,
		uint8_t * wbuffer, uint16_t wlen, uint8_t buff_zcopy,
		void (*free_wbuf_fun)(void* ptr), int timeout)
{
	interface_buffer_handle_t buf_handle = {0};
	void (*free_func)(void* ptr) = NULL;
	uint8_t pkt_prio = PRIO_Q_OTHERS;
	uint8_t transport_up = is_transport_tx_ready();

	if (free_wbuf_fun)
		free_func = free_wbuf_fun;

	/* 链路已经判定故障（见 sdio_recover_by_restart）：立刻报失败，不排队，也不逐包打日志 */
	if (transport_is_failed()) {
		H_FREE_PTR_WITH_FUNC(free_func, wbuffer);
		return ESP_FAIL;
	}

	if (!wbuffer || !wlen ||
		(wlen > MAX_PAYLOAD_SIZE) ||
		!transport_up) {
		ESP_LOGE(TAG, "tx fail: NULL buff, invalid len (%u) or len > max len (%u), transport_up(%u))",
				wlen, MAX_PAYLOAD_SIZE, transport_up);
		H_FREE_PTR_WITH_FUNC(free_func, wbuffer);
		return ESP_FAIL;
	}
	buf_handle.payload_zcopy = buff_zcopy;
	buf_handle.if_type = iface_type;
	buf_handle.if_num = iface_num;
	buf_handle.payload_len = wlen;
	buf_handle.payload = wbuffer;
	buf_handle.priv_buffer_handle = wbuffer;
	buf_handle.free_buf_handle = free_func;

	if (buf_handle.if_type == ESP_SERIAL_IF)
		pkt_prio = PRIO_Q_SERIAL;
	else if (buf_handle.if_type == ESP_HCI_IF)
		pkt_prio = PRIO_Q_BT;
	/* else OTHERS by default */

	if (g_h.funcs->_h_queue_item(to_slave_queue[pkt_prio], &buf_handle, timeout)) {
		/* 队列满，而且调用方不让等（一直等的话到不了这里） */
		H_FREE_PTR_WITH_FUNC(free_func, wbuffer);
		return ESP_ERR_TIMEOUT;
	}
	g_h.funcs->_h_post_semaphore(sem_to_slave_queue);

#if ESP_PKT_STATS
	if (buf_handle.if_type == ESP_STA_IF)
		pkt_stats.sta_tx_in_pass++;
#endif

	return ESP_OK;
}

int esp_hosted_tx(uint8_t iface_type, uint8_t iface_num,
		uint8_t * wbuffer, uint16_t wlen, uint8_t buff_zcopy,
		void (*free_wbuf_fun)(void* ptr))
{
	return sdio_tx_enqueue(iface_type, iface_num, wbuffer, wlen, buff_zcopy, free_wbuf_fun, HOSTED_BLOCK_MAX);
}

/* 同 esp_hosted_tx，但发送队列满的时候不等，返回 ESP_ERR_TIMEOUT。给不能阻塞的调用方用。 */
int esp_hosted_tx_nowait(uint8_t iface_type, uint8_t iface_num,
		uint8_t * wbuffer, uint16_t wlen, uint8_t buff_zcopy,
		void (*free_wbuf_fun)(void* ptr))
{
	return sdio_tx_enqueue(iface_type, iface_num, wbuffer, wlen, buff_zcopy, free_wbuf_fun, 0);
}

/* 打印收发缓冲的用量和丢包计数（本工程新增，调试台 hosted 命令用） */
static const char *reset_reason_name(esp_reset_reason_t r)
{
	switch (r) {
	case ESP_RST_POWERON:  return "上电";
	case ESP_RST_EXT:      return "复位脚";
	case ESP_RST_SW:       return "软件重启";
	case ESP_RST_PANIC:    return "异常";
	case ESP_RST_INT_WDT:
	case ESP_RST_TASK_WDT:
	case ESP_RST_WDT:      return "看门狗";
	case ESP_RST_BROWNOUT: return "欠压";
	case ESP_RST_USB:      return "USB";
	default:               return "其它";
	}
}

void esp_hosted_log_buf_stats(void)
{
	struct mempool_stats st;
	struct ack_hold_stats ah;
	uint32_t chain, max_restarts;

	mempool_get_stats(buf_mp_rx_g, &st);
	ESP_LOGW(TAG, "每包接收缓冲(PSRAM)：向堆要了 %u 块（上限 %u），其中空闲 %u，分配失败 %"PRIu32" 次",
			st.num_blocks, st.max_blocks, st.num_cached, st.num_alloc_fail);

	if (chan_arr[ESP_STA_IF]) {
		mempool_get_stats((struct mempool *)chan_arr[ESP_STA_IF]->memp, &st);
		ESP_LOGW(TAG, "WiFi 发送缓冲(内部 DMA)：向堆要了 %u 块（上限 %u），其中空闲 %u，分配失败 %"PRIu32" 次",
				st.num_blocks, st.max_blocks, st.num_cached, st.num_alloc_fail);
	}

	ESP_LOGW(TAG, "接收丢包：没有每包缓冲 %"PRIu32"，坏流 %"PRIu32" 次，上交拷贝没内存 %"PRIu32"，SDIO 读出错 %"PRIu32" 次（最近错误码 0x%x）；流式接收缓冲定长 %u 字节",
			rx_drop_nomem, rx_drop_bad_stream, rx_drop_copy_nomem, rx_read_err, (unsigned)rx_read_err_last,
			(unsigned)(RX_STREAM_HEADROOM + RX_STREAM_CHUNK_MAX));
	ESP_LOGW(TAG, "发送：等过从机腾接收缓冲的包 %"PRIu32" 个，等到头也没有、丢掉的 %"PRIu32" 个",
			tx_slave_buf_waits, tx_drop_no_slave_buf);
	ESP_LOGW(TAG, "发送限流：从机叫停 %"PRIu32" 次，叫停期间被拒的包 %"PRIu32" 个",
			tx_throttle_starts, sta_tx_throttle_rejects);
	tcp_ack_hold_get_stats(&ah);
	ESP_LOGW(TAG, "TCP 纯 ACK 合并（最多暂存 %"PRIu32" 微秒，0 = 关）：合并掉 %"PRIu32" 个，到点发出 %"PRIu32" 个，作废 %"PRIu32" 个，提前发 %"PRIu32" 个，放回重试 %"PRIu32" 次，丢 %"PRIu32" 个",
			tcp_ack_hold_get_us(), ah.merged, ah.sent, ah.superseded, ah.early, ah.retried, ah.dropped);
	ESP_LOGW(TAG, "接收突发：最大一次 %"PRIu32" 字节，分段读 %"PRIu32" 次（每段最多 %u 字节）",
			rx_burst_max, rx_burst_chunked, (unsigned)RX_STREAM_CHUNK_MAX);
	transport_recover_get_stats(&chain, &max_restarts);
	ESP_LOGW(TAG, "重启恢复：当前连续 %"PRIu32" 次突发拆不出包（到 %d 次重启），为恢复链路连着重启过 %"PRIu32" 次（上限 %"PRIu32"）%s",
			rx_bad_burst_run, RX_BAD_BURSTS_RESTART, chain, max_restarts,
			transport_is_failed() ? "，已判定链路故障、收发停用" : "");
	/* 为恢复链路的重启和崩溃后的重启，在 ROM 的开机信息里都是 SW_CPU_RESET，要看这里才分得出来 */
	ESP_LOGW(TAG, "这次开机的复位原因：%s（%d）", reset_reason_name(esp_reset_reason()), (int)esp_reset_reason());
}
