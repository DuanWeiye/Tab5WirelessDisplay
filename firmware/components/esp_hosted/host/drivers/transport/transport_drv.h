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

/** prevent recursive inclusion **/
#ifndef __TRANSPORT_DRV_H
#define __TRANSPORT_DRV_H

#ifdef __cplusplus
extern "C" {
#endif

/** Includes **/

#include "common.h"
#if 0
#include "os_wrapper.h"
#include "trace.h"
#endif
//#include "netdev_if.h"
#include "esp_hosted_transport.h"
#include "esp_hosted_config.h"
#include "esp_hosted_api_types.h"
#include "esp_hosted_interface.h"
#include "esp_hosted_header.h"

/* ESP in sdkconfig has CONFIG_IDF_FIRMWARE_CHIP_ID entry.
 * supported values of CONFIG_IDF_FIRMWARE_CHIP_ID are - */
#define ESP_PRIV_FIRMWARE_CHIP_UNRECOGNIZED (0xff)
#define ESP_PRIV_FIRMWARE_CHIP_ESP32        (0x0)
#define ESP_PRIV_FIRMWARE_CHIP_ESP32S2      (0x2)
#define ESP_PRIV_FIRMWARE_CHIP_ESP32C3      (0x5)
#define ESP_PRIV_FIRMWARE_CHIP_ESP32S3      (0x9)
#define ESP_PRIV_FIRMWARE_CHIP_ESP32C2      (0xC)
#define ESP_PRIV_FIRMWARE_CHIP_ESP32C6      (0xD)
#define ESP_PRIV_FIRMWARE_CHIP_ESP32C5      (0x17)


#if H_TRANSPORT_IN_USE == H_TRANSPORT_SPI
#include "spi_wrapper.h"
#define SPI_MODE0                           (0)
#define SPI_MODE1                           (1)
#define SPI_MODE2                           (2)
#define SPI_MODE3                           (3)
#else
#include "sdio_wrapper.h"
#endif

struct esp_private {
	uint8_t     if_type;
	uint8_t     if_num;
	void        *netdev;
};

struct hosted_transport_context_t {
    uint8_t  *tx_buf;
    uint32_t  tx_buf_size;
    uint8_t  *rx_buf;
};

extern volatile uint8_t wifi_tx_throttling;
/* 本工程新增：从机叫主机暂停往 WiFi 发的期间，transport_drv_sta_tx 拒掉的包数 */
extern uint32_t sta_tx_throttle_rejects;

typedef int (*hosted_rxcb_t)(void *buffer, uint16_t len, void *free_buff_hdl);

typedef void (transport_free_cb_t)(void* buffer);
typedef esp_err_t (*transport_channel_tx_fn_t)(void *h, void *buffer, size_t len);
typedef esp_err_t (*transport_channel_rx_fn_t)(void *h, void *buffer, void * buff_to_free, size_t len);

typedef struct {
	void * api_chan;
	esp_hosted_if_type_t if_type;
    uint8_t secure;
	transport_channel_tx_fn_t tx;
	transport_channel_rx_fn_t rx;
	void *memp;
} transport_channel_t;

#if 0
/* netdev APIs*/
int esp_netdev_open(netdev_handle_t netdev);
int esp_netdev_close(netdev_handle_t netdev);
int esp_netdev_xmit(netdev_handle_t netdev, struct pbuf *net_buf);
#endif


esp_err_t transport_drv_init(void(*esp_hosted_up_cb)(void));
esp_err_t transport_drv_deinit(void);
esp_err_t transport_drv_reconfigure(void);
transport_channel_t *transport_drv_add_channel(void *api_chan,
		esp_hosted_if_type_t if_type, uint8_t secure,
		transport_channel_tx_fn_t *tx, const transport_channel_rx_fn_t rx);
esp_err_t transport_drv_remove_channel(transport_channel_t *channel);

/* TODO To move to private header */
void process_capabilities(uint8_t cap);
void transport_init_internal(void);
void transport_deinit_internal(void);

void process_priv_communication(interface_buffer_handle_t *buf_handle);
void print_capabilities(uint32_t cap);
int process_init_event(uint8_t *evt_buf, uint16_t len);
esp_err_t send_slave_config(uint8_t host_cap, uint8_t firmware_chip_id,
		uint8_t raw_tp_direction, uint8_t low_thr_thesh, uint8_t high_thr_thesh);

uint8_t is_transport_rx_ready(void);
uint8_t is_transport_tx_ready(void);

/* 链路出错后的恢复（本工程新增，说明见 transport_drv.c 开头和组件根目录 LOCAL_CHANGES.md） */
/* 重启主机来恢复链路；连着重启到上限就不再重启、判定链路故障。返回 = 没有重启。 */
void transport_recover_by_restart(const char *why);
/* 链路是不是已经判定故障（这次开机里不再收发） */
bool transport_is_failed(void);
/* 到这次开机为止为恢复链路连着重启了几次、上限是几次（调试输出用） */
void transport_recover_get_stats(uint32_t *chain, uint32_t *max_restarts);

#define H_BUFF_NO_ZEROCOPY 0
#define H_BUFF_ZEROCOPY 1

#define H_DEFLT_FREE_FUNC g_h.funcs->_h_free


int esp_hosted_tx(uint8_t iface_type, uint8_t iface_num,
		uint8_t * buffer, uint16_t len, uint8_t buff_zerocopy, void (*free_buf_fun)(void* ptr));
/* 本工程新增：同 esp_hosted_tx，但发送队列满的时候不等，返回 ESP_ERR_TIMEOUT（缓冲照样已经释放）。
 * 给不能阻塞的调用方用（esp_timer 任务里发暂存的 TCP 纯 ACK）。只有 SDIO 驱动实现了。 */
int esp_hosted_tx_nowait(uint8_t iface_type, uint8_t iface_num,
		uint8_t * buffer, uint16_t len, uint8_t buff_zerocopy, void (*free_buf_fun)(void* ptr));

int esp_hosted_register_wifi_rxcb(int ifx, hosted_rxcb_t fn);
int esp_hosted_register_wifi_txcb(int ifx, hosted_rxcb_t fn);

int serial_rx_handler(interface_buffer_handle_t * buf_handle);
#ifdef __cplusplus
}
#endif

#endif
