// 麦克风互斥：麦克风是 BSP 里的单例 codec(mic_dev)，同一时刻只能开一次——两处同时 open 会把 I2S 通道状态弄乱，
// 压垮 WiFi-SDIO 的内部 DMA(sdio_rx_get_buffer 断言)重启。
// 现在用麦克风的只有无线显示声音的几条自检命令（wd audiotest / listen / pop，见 wd_audio.cpp）：
// open codec 前 acquire、close codec 后 release，串行独占。
#pragma once
#include <stdint.h>

namespace audiomic {

// 开机一次性初始化(建互斥量)。须在任何 acquire 之前调(app_main 里)。
void init();

// 获取麦克风独占(open codec 前调)。timeout_ms 内拿不到返回 false → 调用方应放弃 open、不读。
bool acquire(uint32_t timeout_ms);

// 释放麦克风(close codec 后调)。
void release();

}  // namespace audiomic
