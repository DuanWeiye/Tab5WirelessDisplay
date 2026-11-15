// SPDX-License-Identifier: Apache-2.0
// Copyright 2015-2021 Espressif Systems (Shanghai) PTE LTD
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
#include <inttypes.h>

#include "esp_wifi.h"
#include "transport_drv.h"
#include "esp_hosted_transport.h"
#include "esp_hosted_transport_init.h"
#include "esp_hosted_transport_config.h"
#include "stats.h"
#include "esp_log.h"
#include "esp_hosted_log.h"
#include "serial_drv.h"
#include "serial_ll_if.h"
#include "esp_hosted_config.h"
#include "mempool.h"
#include "stats.h"
#include "errno.h"
#include "hci_drv.h"
#include "esp_hosted_bufstat.h"
#include "tcp_ack_hold.h"
#include "esp_attr.h"
#include "esp_timer.h"
#include "esp_system.h"
#include <stdatomic.h>

/**
 * @brief  Slave capabilities are parsed
 *         Currently no added functionality to that
 * @param  None
 * @retval None
 */

DEFINE_LOG_TAG(transport);
static char chip_type = ESP_PRIV_FIRMWARE_CHIP_UNRECOGNIZED;
void(*transport_esp_hosted_up_cb)(void) = NULL;
transport_channel_t *chan_arr[ESP_MAX_IF];
volatile uint8_t wifi_tx_throttling;


static uint8_t transport_state = TRANSPORT_INACTIVE;

/* ════ 链路出错后的恢复（本工程新增，完整说明见组件根目录 LOCAL_CHANGES.md）════
 * 到从机的链路靠传输驱动自己恢复不了的时候重启主机，所有这类出口都走 transport_recover_by_restart：
 * 从机复位后一直没有就绪（transport_drv_reconfigure），以及 SDIO 驱动里的初始化失败、读写出错、接收流错位。
 * 硬件坏了的话重启也好不了，所以连着重启有次数上限：
 * - 为恢复链路重启出来的这次开机，不到 TRANSPORT_RECOVER_QUICK_US 就又要重启的算"连着的"；
 * - 连着重启 TRANSPORT_RECOVER_MAX_RESTARTS 次还没好就不再重启，判定链路故障（transport_failed）。 */
#define TRANSPORT_RECOVER_MAX_RESTARTS    3
#define TRANSPORT_RECOVER_QUICK_US        (10LL * 60 * 1000000)
#define TRANSPORT_RECOVER_MAGIC           0x5344494Fu
/* 从机复位后多久还没就绪算链路起不来（秒）。正常约 1.3 秒。 */
#define TRANSPORT_UP_TIMEOUT_S            10

/* 链路故障：这次开机里不再收发。置位后不会清。
 * esp_hosted_tx 立刻报失败（RPC 请求因此马上得到"发送失败"的应答，不用各等 5 秒超时），
 * 传输驱动的任务不再碰总线（SDMMC 主控和 TF 卡共用），transport_drv_reconfigure 立刻返回失败。 */
static volatile bool transport_failed;
/* 到这次开机为止，已经为恢复链路连着重启了几次（开机时定下来，见 transport_recover_boot_check） */
static uint32_t transport_recover_chain;
/* 留给下一次开机的话：为恢复链路重启之前写下"这是连着的第几次"。
 * 放 RTC 内存，软件重启后还在；刚上电时内容不定，magic 对得上才算数。 */
static RTC_NOINIT_ATTR uint32_t transport_recover_magic;
static RTC_NOINIT_ATTR uint32_t transport_recover_restarts;

static void process_event(uint8_t *evt_buf, uint16_t len);
static int sta_tx_held_ack(const uint8_t *frame, size_t len, bool wait);

bool transport_is_failed(void)
{
	return transport_failed;
}

/* 重启主机来恢复链路。几个任务都可能走到这里，只让先到的那个做决定。
 * 返回 = 没有重启（判定故障了，或者另一个任务已经在处理）。 */
void transport_recover_by_restart(const char *why)
{
	static atomic_flag decided = ATOMIC_FLAG_INIT;

	if (atomic_flag_test_and_set(&decided))
		return;

	/* 这次开机好好地跑过了 TRANSPORT_RECOVER_QUICK_US 才出的事：不算连着的 */
	if (esp_timer_get_time() >= TRANSPORT_RECOVER_QUICK_US)
		transport_recover_chain = 0;

	if (transport_recover_chain >= TRANSPORT_RECOVER_MAX_RESTARTS) {
		/* 先置标志再打日志：打日志要花时间，别的任务早一点看到标志就少碰一次总线 */
		transport_failed = true;
		ESP_LOGE(TAG, "%s，已经连着重启 %"PRIu32" 次没恢复，不再重启；WiFi 和蓝牙停用，到下次开机为止",
				why, transport_recover_chain);
		return;
	}
	transport_recover_restarts = transport_recover_chain + 1;
	transport_recover_magic = TRANSPORT_RECOVER_MAGIC;
	ESP_LOGE(TAG, "%s，重启主机恢复（连着第 %"PRIu32" 次）", why, transport_recover_restarts);
	g_h.funcs->_h_restart_host();
}

/* 开机时调一次：这次开机是不是 transport_recover_by_restart 重启出来的，是的话接着数。
 * 留的话只认一次，而且复位原因得是软件重启。所以手动复位、断电、panic，以及中间夹着的别的软件重启
 * （调试台、OTA）之后都从 0 数起：人已经介入过，或者上一次开机不是因为链路结束的。 */
static void transport_recover_boot_check(void)
{
	if (transport_recover_magic == TRANSPORT_RECOVER_MAGIC && esp_reset_reason() == ESP_RST_SW)
		transport_recover_chain = transport_recover_restarts;
	transport_recover_magic = 0;

	if (transport_recover_chain)
		ESP_LOGW(TAG, "这次开机是为恢复链路重启出来的（连着第 %"PRIu32" 次，上限 %d）",
				transport_recover_chain, TRANSPORT_RECOVER_MAX_RESTARTS);
}

void transport_recover_get_stats(uint32_t *chain, uint32_t *max_restarts)
{
	/* 这次开机已经好好地跑过了 TRANSPORT_RECOVER_QUICK_US：再出事也不算连着的，按 0 报 */
	*chain = (!transport_failed && esp_timer_get_time() >= TRANSPORT_RECOVER_QUICK_US)
			? 0 : transport_recover_chain;
	*max_restarts = TRANSPORT_RECOVER_MAX_RESTARTS;
}

/* 到从机的链路是不是已经判定故障（声明在 esp_hosted_bufstat.h）。只读一个标志，不产生流量。 */
bool esp_hosted_link_failed(void)
{
	return transport_failed;
}

/* 到从机的链路现在能不能用：握手完成，而且没有判定故障。只读标志，不产生流量。 */
bool esp_hosted_link_up(void)
{
	return is_transport_tx_ready() && !transport_failed;
}

/* TCP 纯 ACK 最多暂存多久（声明在 esp_hosted_bufstat.h） */
void esp_hosted_set_ack_hold_us(uint32_t us)
{
	tcp_ack_hold_set_us(us);
}


uint8_t is_transport_rx_ready(void)
{
	return (transport_state >= TRANSPORT_RX_ACTIVE);
}

uint8_t is_transport_tx_ready(void)
{
	return (transport_state >= TRANSPORT_TX_ACTIVE);
}

static void reset_slave(void)
{
	gpio_pin_t reset_pin = { 0 };
	if (ESP_TRANSPORT_OK != esp_hosted_transport_get_reset_config(&reset_pin)) {
		ESP_LOGE(TAG, "Unable to get RESET config for transport");
		return;
	}

	ESP_LOGI(TAG, "Reset slave using GPIO[%u]", reset_pin.pin);
	g_h.funcs->_h_config_gpio(H_GPIO_PIN_RESET_Port, reset_pin.pin, H_GPIO_MODE_DEF_OUTPUT);

	g_h.funcs->_h_write_gpio(reset_pin.port, reset_pin.pin, H_RESET_VAL_ACTIVE);
	g_h.funcs->_h_msleep(50);
	g_h.funcs->_h_write_gpio(reset_pin.port, reset_pin.pin, H_RESET_VAL_INACTIVE);
	g_h.funcs->_h_msleep(50);
	g_h.funcs->_h_write_gpio(reset_pin.port, reset_pin.pin, H_RESET_VAL_ACTIVE);

	/* stop spi transactions short time to avoid slave sync issues */
	g_h.funcs->_h_sleep(1);
}

static void transport_driver_event_handler(uint8_t event)
{
	switch(event)
	{
		case TRANSPORT_TX_ACTIVE:
		{
			/* Initiate control path now */
			ESP_LOGI(TAG, "Base transport is set-up\n\r");
			if (transport_esp_hosted_up_cb)
				transport_esp_hosted_up_cb();
			transport_state = TRANSPORT_TX_ACTIVE;
			break;
		}

		default:
		break;
	}
}

esp_err_t transport_drv_deinit(void)
{
	transport_deinit_internal();
	transport_state = TRANSPORT_INACTIVE;
	return ESP_OK;
}

esp_err_t transport_drv_init(void(*esp_hosted_up_cb)(void))
{
	g_h.funcs->_h_hosted_init_hook();
	transport_recover_boot_check();
	transport_init_internal();
	tcp_ack_hold_init(sta_tx_held_ack);
	hci_drv_init();
	transport_esp_hosted_up_cb = esp_hosted_up_cb;

	return ESP_OK;
}

/* 本工程改动：
 * - 链路已经判定故障：立刻返回失败，不再复位从机（等的过程中判定的也一样）。
 * - 原版每 10 秒再复位一次从机，一共等 1000 秒，然后返回失败，而调用方全是 ESP_ERROR_CHECK：
 *   从机起不来的话设备卡 17 分钟后 abort。而且 SDIO 下从机被再次复位以后要主机重新枚举才能通，
 *   枚举只在读任务启动时做一次，所以头 10 秒没起来、后面怎么等都起不来。
 *   改成等 TRANSPORT_UP_TIMEOUT_S 秒，还没就绪就走重启恢复（重启后主机会重新枚举）。
 * 返回失败时调用方不要 abort：要么马上会重启，要么已经判定故障，上层按 esp_hosted_link_failed() 处理。 */
esp_err_t transport_drv_reconfigure(void)
{
	int waited_s = 0;

	if (transport_failed)
		return ESP_FAIL;

	ESP_LOGI(TAG, "Attempt connection with slave");
	if (!is_transport_tx_ready()) {
		reset_slave();
		transport_state = TRANSPORT_RX_ACTIVE;

		while (!is_transport_tx_ready()) {
			if (transport_failed)
				return ESP_FAIL;
			if (waited_s++ >= TRANSPORT_UP_TIMEOUT_S) {
				ESP_LOGE(TAG, "Failed to get ESP_Hosted slave transport up");
				transport_recover_by_restart("从机复位后一直没有就绪");
				return ESP_FAIL;
			}
			g_h.funcs->_h_sleep(1);
		}
	} else {
		ESP_LOGI(TAG, "Transport is already up");
	}
	return ESP_OK;
}

esp_err_t transport_drv_remove_channel(transport_channel_t *channel)
{
	if (!channel)
		return ESP_FAIL;

	switch (channel->if_type) {
	case ESP_AP_IF:
	case ESP_STA_IF:
		//Should we additionally do:
		//esp_wifi_internal_reg_rxcb(channel->if_type, NULL);
		break;
	case ESP_SERIAL_IF:
		/* TODO */
		break;
	default:
		break;
	}

	assert(chan_arr[channel->if_type] == channel);

	mempool_destroy(channel->memp);
	chan_arr[channel->if_type] = NULL;
	HOSTED_FREE(channel);

	return ESP_OK;
}

#if 0
esp_err_t transport_drv_tx(void *h, void *buffer, size_t len)
{
	if (!h) {
		esp_wifi_internal_free_rx_buffer(buffer);
		return ESP_FAIL;
	}

	/* Buffer will be freed always in the called function */
	return esp_hosted_tx(h->if_type, 0, buffer, len, H_BUFF_NO_ZEROCOPY, esp_wifi_internal_free_rx_buffer);

}
#endif

#if 0
static esp_err_t transport_drv_sta_tx(void *h, void *buffer, transport_free_cb_t free_cb, size_t len)
{
	ESP_LOGI(TAG, "%s", __func__);
	assert(h && h==chan_arr[ESP_STA_IF]->api_chan);
	return esp_hosted_tx(ESP_STA_IF, 0, buffer, len, H_BUFF_NO_ZEROCOPY, free_cb);
}
#endif

/* 每个通道的发送缓冲池上限（本工程新增，见 transport_drv_add_channel） */
#ifdef H_SDIO_TX_Q
#define CHAN_TX_POOL_MAX_BLOCKS   (H_SDIO_TX_Q + 4)
#else
#define CHAN_TX_POOL_MAX_BLOCKS   24
#endif
#define CHAN_TX_POOL_MAX_CACHED   4

/* 拿不到发送缓冲时的告警（本工程新增）：第一次立刻打，之后每 10 秒最多一条。
 * 调用方是协议栈线程，逐包打日志会把它拖慢。累计次数在通道内存池的统计里（esp_hosted_log_buf_stats）。 */
static void chan_tx_nomem_note(void)
{
	static uint32_t last_tick;   /* 0 = 还没打过。偶尔多打或少打一条无所谓 */
	uint32_t now = (uint32_t)xTaskGetTickCount();

	if (last_tick == 0 || (now - last_tick) >= pdMS_TO_TICKS(10000)) {
		last_tick = now ? now : 1;
		ESP_LOGW(TAG, "丢包：拿不到发送缓冲（内部 DMA 内存不足，或在途的包已到上限）");
	}
}

static void transport_sta_free_cb(void *buf)
{
	mempool_free(chan_arr[ESP_STA_IF]->memp, buf);
}

static void transport_ap_free_cb(void *buf)
{
	mempool_free(chan_arr[ESP_AP_IF]->memp, buf);
}

static void transport_serial_free_cb(void *buf)
{
	mempool_free(chan_arr[ESP_SERIAL_IF]->memp, buf);
}

/* 从机叫主机暂停往 WiFi 发的期间被拒掉的包数（只有协议栈线程在写，hosted 命令打印；声明在 transport_drv.h） */
uint32_t sta_tx_throttle_rejects;

/* TCP 纯 ACK 合并到点时要用不等待的入队 esp_hosted_tx_nowait，只有 SDIO 驱动实现了 */
#if H_TRANSPORT_IN_USE != H_TRANSPORT_SDIO
#error "TCP 纯 ACK 合并要用 esp_hosted_tx_nowait，本工程只在 SDIO 驱动里实现了它"
#endif

/* 把 STA 的一帧拷进发送缓冲、交给传输驱动。wait = 发送队列满的时候可不可以等。
 * 返回 ESP_ERR_NO_MEM = 拿不到发送缓冲；ESP_ERR_TIMEOUT = 不许等而队列满了；别的失败 = 链路不通。 */
static esp_err_t sta_tx_frame(const void *frame, size_t len, bool wait)
{
	void *copy_buff;

	/*  Prepare transport buffer directly consumable */
	copy_buff = mempool_alloc(((struct mempool*)chan_arr[ESP_STA_IF]->memp), MAX_TRANSPORT_BUFFER_SIZE, true);
	if (!copy_buff)
		return ESP_ERR_NO_MEM;
	g_h.funcs->_h_memcpy(copy_buff+H_ESP_PAYLOAD_HEADER_OFFSET, frame, len);

	if (wait)
		return esp_hosted_tx(ESP_STA_IF, 0, copy_buff, len, H_BUFF_ZEROCOPY, transport_sta_free_cb);
	return esp_hosted_tx_nowait(ESP_STA_IF, 0, copy_buff, len, H_BUFF_ZEROCOPY, transport_sta_free_cb);
}

/* TCP 纯 ACK 合并（tcp_ack_hold.c）发暂存的纯 ACK 用。
 * wait 为假 = 定时器到点发的：从机叫停期间先不发，按"一时发不了"报回去，它会放回去等下一轮。
 * wait 为真 = 协议栈线程里提前发的：调用方 transport_drv_sta_tx 刚看过没有叫停。 */
static int sta_tx_held_ack(const uint8_t *frame, size_t len, bool wait)
{
	if (!chan_arr[ESP_STA_IF])
		return ESP_FAIL;
	if (!wait) {
		struct mempool_stats st;

		if (wifi_tx_throttling)
			return ESP_ERR_TIMEOUT;
		/* 发送缓冲已经全在途（到上限、没有空闲块）：不去要了，等下一轮。
		 * 每 2 ms 重试一次，都去要的话池子的「分配失败」计数会被刷高，那个数是留给真丢包看的。 */
		mempool_get_stats((struct mempool *)chan_arr[ESP_STA_IF]->memp, &st);
		if (st.max_blocks && st.num_blocks >= st.max_blocks && !st.num_cached)
			return ESP_ERR_NO_MEM;
	}
	return sta_tx_frame(frame, len, wait);
}

static esp_err_t transport_drv_sta_tx(void *h, void *buffer, size_t len)
{
	esp_err_t err;

	if (!buffer || !len)
		return ESP_OK;

	if (unlikely(wifi_tx_throttling)) {
	#if ESP_PKT_STATS
		pkt_stats.sta_tx_in_drop++;
	#endif
		sta_tx_throttle_rejects++;
		errno = -ENOBUFS;
		//return ESP_ERR_NO_BUFFS;
#if defined(ESP_ERR_ESP_NETIF_TX_FAILED)
		return ESP_ERR_ESP_NETIF_TX_FAILED;
#else
		return ESP_ERR_ESP_NETIF_NO_MEM;
#endif
	}

	assert(h && h==chan_arr[ESP_STA_IF]->api_chan);

	/* 本工程新增：TCP 纯 ACK 先暂存，短时间内同一条连接只发最新的一个（见 tcp_ack_hold.h） */
	if (tcp_ack_hold_on_tx((const uint8_t *)buffer, len))
		return ESP_OK;

	err = sta_tx_frame(buffer, len, true);
	if (err == ESP_ERR_NO_MEM) {
		/* 本工程改动：拿不到发送缓冲就报"没内存"，lwIP 会把它当成这一包没发出去，
		 * TCP 自己重传（原实现在这里断言重启） */
		chan_tx_nomem_note();
	}
	return err;
}

static esp_err_t transport_drv_ap_tx(void *h, void *buffer, size_t len)
{
	void * copy_buff = NULL;

	if (!buffer || !len)
		return ESP_OK;

	assert(h && h==chan_arr[ESP_AP_IF]->api_chan);

	/*  Prepare transport buffer directly consumable */
	copy_buff = mempool_alloc(((struct mempool*)chan_arr[ESP_AP_IF]->memp), MAX_TRANSPORT_BUFFER_SIZE, true);
	if (!copy_buff) {
		/* 本工程改动：拿不到发送缓冲就报"没内存"，lwIP 会把它当成这一包没发出去，
		 * TCP 自己重传（原实现在这里断言重启） */
		chan_tx_nomem_note();
		return ESP_ERR_NO_MEM;
	}
	g_h.funcs->_h_memcpy(copy_buff+H_ESP_PAYLOAD_HEADER_OFFSET, buffer, len);

	return esp_hosted_tx(ESP_AP_IF, 0, copy_buff, len, H_BUFF_ZEROCOPY, transport_ap_free_cb);
}

esp_err_t transport_drv_serial_tx(void *h, void *buffer, size_t len)
{
	/* TODO */
	assert(h && h==chan_arr[ESP_SERIAL_IF]->api_chan);
	return esp_hosted_tx(ESP_SERIAL_IF, 0, buffer, len, H_BUFF_NO_ZEROCOPY, transport_serial_free_cb);
}


transport_channel_t *transport_drv_add_channel(void *api_chan,
		esp_hosted_if_type_t if_type, uint8_t secure,
		transport_channel_tx_fn_t *tx, const transport_channel_rx_fn_t rx)
{
	transport_channel_t *channel = NULL;

	ESP_ERROR_CHECK(if_type >= ESP_MAX_IF);

	if (!tx || !rx) {
		ESP_LOGE(TAG, "%s fail for IF[%u]: tx or rx is NULL", __func__, if_type );
		return NULL;
	}

	if (chan_arr[if_type]) {
		/* Channel config already existed */
		ESP_LOGW(TAG, "Channel [%u] already created, replace with new callbacks", if_type);
		HOSTED_FREE(chan_arr[if_type]);
	}


	chan_arr[if_type] = g_h.funcs->_h_calloc(sizeof(transport_channel_t), 1);
	assert(chan_arr[if_type]);
	channel = chan_arr[if_type];

	switch (if_type) {

	case ESP_STA_IF:
		*tx = transport_drv_sta_tx;
		break;

	case ESP_AP_IF:
		*tx = transport_drv_ap_tx;
		break;

	case ESP_SERIAL_IF:
		*tx = transport_drv_serial_tx;
		break;

	default:
		//*tx = transport_drv_tx;
		ESP_LOGW(TAG, "Not yet suppported ESP_Hosted interface for if_type[%u]", if_type);
		return NULL;
	}

	channel->api_chan = api_chan;
	channel->if_type = if_type;
	channel->secure = secure;
	channel->tx = *tx;
	channel->rx = rx;

	/* Need to change size wrt transport */
	/* 本工程改动：发送缓冲要直接交给 SDIO 发送，必须是内部 DMA 内存，所以给它设上限：
	 * 在用的块数有结构性上限（发送队列满，加上写任务手里的一个、正堵在入队处的一个）；
	 * 用完的块只留 CHAN_TX_POOL_MAX_CACHED 个备用，其余还给堆。 */
	channel->memp = mempool_create_ex(MAX_TRANSPORT_BUFFER_SIZE, 0,
			CHAN_TX_POOL_MAX_BLOCKS, CHAN_TX_POOL_MAX_CACHED);
#ifdef H_USE_MEMPOOL
	assert(channel->memp);
#endif

	ESP_LOGI(TAG, "Add ESP-Hosted channel IF[%u]: S[%u] Tx[%p] Rx[%p]",
			if_type, secure, *tx, rx);

	return channel;
}

void process_capabilities(uint8_t cap)
{
	ESP_LOGI(TAG, "capabilities: 0x%x",cap);
}

static uint32_t process_ext_capabilities(uint8_t * ptr)
{
	// ptr address may be not be 32-bit aligned
	uint32_t cap;

	cap = (uint32_t)ptr[0] +
		((uint32_t)ptr[1] << 8) +
		((uint32_t)ptr[2] << 16) +
		((uint32_t)ptr[3] << 24);
	ESP_LOGI(TAG, "extended capabilities: 0x%"PRIx32,cap);

	return cap;
}

void process_priv_communication(interface_buffer_handle_t *buf_handle)
{
	if (!buf_handle || !buf_handle->payload || !buf_handle->payload_len)
		return;

	process_event(buf_handle->payload, buf_handle->payload_len);
}

void print_capabilities(uint32_t cap)
{
	ESP_LOGI(TAG, "Features supported are:");
	if (cap & ESP_WLAN_SDIO_SUPPORT)
		ESP_LOGI(TAG, "\t * WLAN");
	if (cap & ESP_BT_UART_SUPPORT)
		ESP_LOGI(TAG, "\t   - HCI over UART");
	if (cap & ESP_BT_SDIO_SUPPORT)
		ESP_LOGI(TAG, "\t   - HCI over SDIO");
	if (cap & ESP_BT_SPI_SUPPORT)
		ESP_LOGI(TAG, "\t   - HCI over SPI");
	if ((cap & ESP_BLE_ONLY_SUPPORT) && (cap & ESP_BR_EDR_ONLY_SUPPORT))
		ESP_LOGI(TAG, "\t   - BT/BLE dual mode");
	else if (cap & ESP_BLE_ONLY_SUPPORT)
		ESP_LOGI(TAG, "\t   - BLE only");
	else if (cap & ESP_BR_EDR_ONLY_SUPPORT)
		ESP_LOGI(TAG, "\t   - BR EDR only");
}

static void print_ext_capabilities(uint8_t * ptr)
{
	// ptr address may be not be 32-bit aligned
	uint32_t cap;

	cap = (uint32_t)ptr[0] +
		((uint32_t)ptr[1] << 8) +
		((uint32_t)ptr[2] << 16) +
		((uint32_t)ptr[3] << 24);

	ESP_LOGI(TAG, "Extended Features supported:");
#if H_SPI_HD_HOST_INTERFACE
	if (cap & ESP_SPI_HD_INTERFACE_SUPPORT_2_DATA_LINES)
		ESP_LOGI(TAG, "\t * SPI HD 2 data lines interface");
	if (cap & ESP_SPI_HD_INTERFACE_SUPPORT_4_DATA_LINES)
		ESP_LOGI(TAG, "\t * SPI HD 4 data lines interface");
	if (cap & ESP_WLAN_SUPPORT)
		ESP_LOGI(TAG, "\t * WLAN");
	if (cap & ESP_BT_INTERFACE_SUPPORT)
		ESP_LOGI(TAG, "\t * BT/BLE");
#elif H_UART_HOST_TRANSPORT
	if (cap & ESP_WLAN_UART_SUPPORT)
		ESP_LOGI(TAG, "\t * WLAN over UART");
	if (cap & ESP_BT_VHCI_UART_SUPPORT)
		ESP_LOGI(TAG, "\t * BT over UART (VHCI)");
#else
	ESP_LOGI(TAG, "\t No extended features. capabilities[%" PRIu32 "]", cap);
#endif
}

static void process_event(uint8_t *evt_buf, uint16_t len)
{
	int ret = 0;
	struct esp_priv_event *event;

	if (!evt_buf || !len)
		return;

	event = (struct esp_priv_event *) evt_buf;

	if (event->event_type == ESP_PRIV_EVENT_INIT) {

		ESP_LOGI(TAG, "Received INIT event from ESP32 peripheral");
		ESP_HEXLOGD("Slave_init_evt", event->event_data, event->event_len);

		ret = process_init_event(event->event_data, event->event_len);
		if (ret) {
			ESP_LOGE(TAG, "failed to init event\n\r");
		}
	} else {
		ESP_LOGW(TAG, "Drop unknown event\n\r");
	}
}

static esp_err_t get_chip_str_from_id(int chip_id, char* chip_str)
{
	int ret = ESP_OK;
	assert(chip_str);

	switch(chip_id) {
	case ESP_PRIV_FIRMWARE_CHIP_ESP32:
		strcpy(chip_str, "esp32");
		break;
	case ESP_PRIV_FIRMWARE_CHIP_ESP32C2:
		strcpy(chip_str, "esp32c2");
		break;
	case ESP_PRIV_FIRMWARE_CHIP_ESP32C3:
		strcpy(chip_str, "esp32c3");
		break;
	case ESP_PRIV_FIRMWARE_CHIP_ESP32C6:
		strcpy(chip_str, "esp32c6");
		break;
	case ESP_PRIV_FIRMWARE_CHIP_ESP32S2:
		strcpy(chip_str, "esp32s2");
		break;
	case ESP_PRIV_FIRMWARE_CHIP_ESP32S3:
		strcpy(chip_str, "esp32s3");
		break;
	case ESP_PRIV_FIRMWARE_CHIP_ESP32C5:
		strcpy(chip_str, "esp32c5");
		break;
	default:
		ESP_LOGW(TAG, "Unsupported chip id: %u", chip_id);
		strcpy(chip_str, "unsupported");
		ret = ESP_FAIL;
		break;
	}
	return ret;
}

static void verify_host_config_for_slave(uint8_t chip_type)
{
	uint8_t exp_chip_id = 0xff;


#if H_SLAVE_TARGET_ESP32
	exp_chip_id = ESP_PRIV_FIRMWARE_CHIP_ESP32;
#elif H_SLAVE_TARGET_ESP32C2
	exp_chip_id = ESP_PRIV_FIRMWARE_CHIP_ESP32C2;
#elif H_SLAVE_TARGET_ESP32C3
	exp_chip_id = ESP_PRIV_FIRMWARE_CHIP_ESP32C3;
#elif H_SLAVE_TARGET_ESP32C6
	exp_chip_id = ESP_PRIV_FIRMWARE_CHIP_ESP32C6;
#elif H_SLAVE_TARGET_ESP32S2
	exp_chip_id = ESP_PRIV_FIRMWARE_CHIP_ESP32S2;
#elif H_SLAVE_TARGET_ESP32S3
	exp_chip_id = ESP_PRIV_FIRMWARE_CHIP_ESP32S3;
#elif H_SLAVE_TARGET_ESP32C5
	exp_chip_id = ESP_PRIV_FIRMWARE_CHIP_ESP32C5;
#else
	ESP_LOGW(TAG, "Incorrect host config for ESP slave chipset[%x]", chip_type);
#endif
	if (chip_type!=exp_chip_id) {
		char slave_str[20], exp_str[20];

		memset(slave_str, '\0', 20);
		memset(exp_str, '\0', 20);

		get_chip_str_from_id(chip_type, slave_str);
		get_chip_str_from_id(exp_chip_id, exp_str);
		ESP_LOGE(TAG, "Identified slave [%s] != Expected [%s]\n\t\trun 'idf.py menuconfig' at host to reselect the slave?\n\t\tAborting.. ", slave_str, exp_str);
		g_h.funcs->_h_sleep(10);
		assert(0!=0);
	}
}

esp_err_t send_slave_config(uint8_t host_cap, uint8_t firmware_chip_id,
		uint8_t raw_tp_direction, uint8_t low_thr_thesh, uint8_t high_thr_thesh)
{
#define LENGTH_1_BYTE 1
	struct esp_priv_event *event = NULL;
	uint8_t *pos = NULL;
	uint16_t len = 0;
	uint8_t *sendbuf = NULL;

	sendbuf = g_h.funcs->_h_malloc(512);
	assert(sendbuf);

	/* Populate event data */
	//event = (struct esp_priv_event *) (sendbuf + sizeof(struct esp_payload_header)); //ZeroCopy
	event = (struct esp_priv_event *) (sendbuf);

	event->event_type = ESP_PRIV_EVENT_INIT;

	/* Populate TLVs for event */
	pos = event->event_data;

	/* TLVs start */

	/* TLV - Board type */
	ESP_LOGI(TAG, "Slave chip Id[%x]", ESP_PRIV_FIRMWARE_CHIP_ID);
	*pos = HOST_CAPABILITIES;                          pos++;len++;
	*pos = LENGTH_1_BYTE;                              pos++;len++;
	*pos = host_cap;                                   pos++;len++;

	/* TLV - Capability */
	*pos = RCVD_ESP_FIRMWARE_CHIP_ID;                  pos++;len++;
	*pos = LENGTH_1_BYTE;                              pos++;len++;
	*pos = firmware_chip_id;                           pos++;len++;

	*pos = SLV_CONFIG_TEST_RAW_TP;                     pos++;len++;
	*pos = LENGTH_1_BYTE;                              pos++;len++;
	*pos = raw_tp_direction;                           pos++;len++;

	*pos = SLV_CONFIG_THROTTLE_HIGH_THRESHOLD;         pos++;len++;
	*pos = LENGTH_1_BYTE;                              pos++;len++;
	*pos = high_thr_thesh;                             pos++;len++;

	*pos = SLV_CONFIG_THROTTLE_LOW_THRESHOLD;          pos++;len++;
	*pos = LENGTH_1_BYTE;                              pos++;len++;
	*pos = low_thr_thesh;                              pos++;len++;

	/* TLVs end */

	event->event_len = len;

	/* payload len = Event len + sizeof(event type) + sizeof(event len) */
	len += 2;

	return esp_hosted_tx(ESP_PRIV_IF, 0, sendbuf, len, H_BUFF_NO_ZEROCOPY, g_h.funcs->_h_free);
}

int process_init_event(uint8_t *evt_buf, uint16_t len)
{
	uint8_t len_left = len, tag_len;
	uint8_t *pos;
	uint8_t raw_tp_config = H_TEST_RAW_TP_DIR;
	uint32_t ext_cap = 0;

	if (!evt_buf)
		return ESP_FAIL;

	pos = evt_buf;
	ESP_LOGD(TAG, "Init event length: %u", len);
	if (len > 64) {
		ESP_LOGE(TAG, "Init event length: %u", len);
#if H_TRANSPORT_IN_USE == H_TRANSPORT_SPI
		ESP_LOGE(TAG, "Seems incompatible SPI mode try changing SPI mode. Asserting for now.");
#endif
		assert(len < 64);
	}

	while (len_left) {
		tag_len = *(pos + 1);

		if (*pos == ESP_PRIV_CAPABILITY) {
			ESP_LOGI(TAG, "EVENT: %2x", *pos);
			process_capabilities(*(pos + 2));
			print_capabilities(*(pos + 2));
		} else if (*pos == ESP_PRIV_CAP_EXT) {
			ESP_LOGI(TAG, "EVENT: %2x", *pos);
			ext_cap = process_ext_capabilities(pos + 2);
			print_ext_capabilities(pos + 2);
		} else if (*pos == ESP_PRIV_FIRMWARE_CHIP_ID) {
			ESP_LOGI(TAG, "EVENT: %2x", *pos);
			chip_type = *(pos+2);
			verify_host_config_for_slave(chip_type);
		} else if (*pos == ESP_PRIV_TEST_RAW_TP) {
			ESP_LOGI(TAG, "EVENT: %2x", *pos);
#if TEST_RAW_TP
			process_test_capabilities(*(pos + 2));
#else
			if (*(pos + 2))
				ESP_LOGW(TAG, "Slave enabled Raw Throughput Testing, but not enabled on Host");
#endif
		} else if (*pos == ESP_PRIV_RX_Q_SIZE) {
			ESP_LOGD(TAG, "slave rx queue size: %u", *(pos + 2));
		} else if (*pos == ESP_PRIV_TX_Q_SIZE) {
			ESP_LOGD(TAG, "slave tx queue size: %u", *(pos + 2));
		} else {
			ESP_LOGD(TAG, "Unsupported EVENT: %2x", *pos);
		}
		pos += (tag_len+2);
		len_left -= (tag_len+2);
	}

	if ((chip_type != ESP_PRIV_FIRMWARE_CHIP_ESP32) &&
		(chip_type != ESP_PRIV_FIRMWARE_CHIP_ESP32S2) &&
		(chip_type != ESP_PRIV_FIRMWARE_CHIP_ESP32S3) &&
		(chip_type != ESP_PRIV_FIRMWARE_CHIP_ESP32C2) &&
		(chip_type != ESP_PRIV_FIRMWARE_CHIP_ESP32C3) &&
		(chip_type != ESP_PRIV_FIRMWARE_CHIP_ESP32C6) &&
		(chip_type != ESP_PRIV_FIRMWARE_CHIP_ESP32C5)) {
		ESP_LOGI(TAG, "ESP board type is not mentioned, ignoring [%d]\n\r", chip_type);
		chip_type = ESP_PRIV_FIRMWARE_CHIP_UNRECOGNIZED;
		return -1;
	} else {
		ESP_LOGI(TAG, "ESP board type is : %d \n\r", chip_type);
	}

	if (ext_cap) {
#if H_SPI_HD_HOST_INTERFACE
		// reconfigure SPI_HD interface based on host and slave capabilities
		if (H_SPI_HD_HOST_NUM_DATA_LINES == 4) {
			// SPI_HD on host is configured to use 4 data bits
			if (ext_cap & ESP_SPI_HD_INTERFACE_SUPPORT_4_DATA_LINES) {
				// slave configured to use 4 bits
				ESP_LOGI(TAG, "configure SPI_HD interface to use 4 data lines");
				g_h.funcs->_h_spi_hd_set_data_lines(H_SPI_HD_CONFIG_4_DATA_LINES);
			} else {
				// slave configured to use 2 bits
				ESP_LOGI(TAG, "configure SPI_HD interface to use 2 data lines");
				g_h.funcs->_h_spi_hd_set_data_lines(H_SPI_HD_CONFIG_2_DATA_LINES);
			}
		} else {
			// SPI_HD on host is configured to use 2 data bits
			if (ext_cap & ESP_SPI_HD_INTERFACE_SUPPORT_4_DATA_LINES) {
				// slave configured to use 4 bits
				ESP_LOGI(TAG, "SPI_HD on slave uses 4 data lines but Host is configure to use 2 data lines");
				g_h.funcs->_h_spi_hd_set_data_lines(H_SPI_HD_CONFIG_2_DATA_LINES);
			} else {
				// slave configured to use 2 bits
				ESP_LOGI(TAG, "configure SPI_HD interface to use 2 data lines");
				g_h.funcs->_h_spi_hd_set_data_lines(H_SPI_HD_CONFIG_2_DATA_LINES);
			}
		}
#endif
	}

	transport_driver_event_handler(TRANSPORT_TX_ACTIVE);
	return send_slave_config(0, chip_type, raw_tp_config,
		H_WIFI_TX_DATA_THROTTLE_LOW_THRESHOLD,
		H_WIFI_TX_DATA_THROTTLE_HIGH_THRESHOLD);
}

int serial_rx_handler(interface_buffer_handle_t * buf_handle)
{
	return serial_ll_rx_handler(buf_handle);
}
