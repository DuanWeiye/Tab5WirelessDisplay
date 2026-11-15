/* 本工程新增（说明见组件根目录 LOCAL_CHANGES.md） */
#ifndef __ESP_HOSTED_BUFSTAT_H__
#define __ESP_HOSTED_BUFSTAT_H__

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* 打印 SDIO 收发缓冲的用量（各内存池向堆要了几块、上限、空闲、分配失败次数）和接收丢包计数 */
void esp_hosted_log_buf_stats(void);

/* 到 C6 的链路是不是已经判定故障：链路出错（或者开机就起不来）后连着重启几次都没好，
 * 这次开机里 WiFi 和蓝牙停用。为真以后不会再变回假（到下次开机为止），所有发送立刻失败。
 * 只读一个标志，不产生流量。 */
bool esp_hosted_link_failed(void);

/* 到 C6 的链路现在能不能用：握手已经完成，而且没有判定故障。只读标志，不产生流量。
 * 要用蓝牙 HCI 之前拿它把关：链路没起来就起蓝牙栈的话，蓝牙会自己去复位从机、和 WiFi 的初始化抢握手。 */
bool esp_hosted_link_up(void);

/* TCP 纯 ACK 合并：一个纯 ACK 最多暂存多久（微秒，0 = 不合并，上限 20000）。不落盘，重启后回到默认值。
 * 调试对比用，统计在 esp_hosted_log_buf_stats 里。说明见 host/drivers/transport/tcp_ack_hold.h。 */
void esp_hosted_set_ack_hold_us(uint32_t us);

#ifdef __cplusplus
}
#endif

#endif
