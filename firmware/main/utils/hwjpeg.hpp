// 硬件 JPEG 解码引擎（ESP32-P4）：全工程只建一个，无线显示的条带解码用它。
// IDF 驱动内部有互斥，几个任务同时调是安全的（一个解完才轮到下一个）。
#pragma once

#include <cstddef>
#include <cstdint>
#include <esp_err.h>

namespace hwjpeg {

// 开机时调一次，把引擎建好（只在这里建：等运行一阵、内部 DMA 内存碎了，最大连续块常只剩 8KB，再建会 NO_MEM）。
// 要在起任何用到解码的任务之前调。返回 false = 建不出来，之后各解码接口都返回 ESP_ERR_INVALID_STATE。
bool preinit();

struct Info {
    int w, h;            // 图片的宽高
    int out_w, out_h;    // 解码输出的宽高。硬件按 MCU 整块输出，宽高各向上取整到 MCU 的宽和高：
                         // 4:4:4 是 8×8，4:2:2 是 16×8，4:2:0 是 16×16。输出每行 out_w 个像素，共 out_h 行
    uint8_t hv;          // 亮度的采样因子（色度都是 1×1）：0x11 = 4:4:4，0x21 = 4:2:2，0x22 = 4:2:0
    size_t out_bytes() const { return (size_t)out_w * out_h * 2; }   // 输出的字节数（RGB565、YUV422 都是每像素 2 字节；一定是 128 的倍数）
};

// 只读文件头，取图片的尺寸。不占引擎。
// 同时是把关的地方：文件头的结构在这里严格校验（原因和规矩见 hwjpeg.cpp 的 parse_header），返回 false 的数据
// 硬件解不了或者结构不对——渐进式、灰度、CMYK、损坏的文件都在此列。decode_rgb565 内部也会先过这一关。
bool info(const uint8_t* jpg, size_t len, Info& out);

// 输出缓冲的对齐要求（字节）：PSRAM 的 cache 行，本工程的配置下是 128。
size_t out_align();

// 把一张基线 JPEG 解成 RGB565（小端，和 LVGL / 面板帧缓冲的像素格式一致），排列见 Info。
// out 的地址和 out_size 都必须按 out_align() 对齐，out_size 不小于 Info::out_bytes()；
// 解码器经 DMA 直接写内存，所以 out 可以是别人还在读的大缓冲中间的一段（比如帧缓冲的若干整行）。
// 输入 jpg 没有对齐要求。got 返回实际写了多少字节（可以传 nullptr）。
// 文件头过不了校验、out 不合要求时返回 ESP_ERR_INVALID_ARG，都不会交给驱动。
esp_err_t decode_rgb565(const uint8_t* jpg, size_t len, uint8_t* out, size_t out_size, uint32_t* got = nullptr);

// 把一张 4:2:2 的基线 JPEG 解成 YUV422：不做色彩转换，JPEG 里的 Y / Cb / Cr 原样写出来，每两个像素 4 个字节，
// 顺序 U Y0 V Y1（内存里从低地址到高地址）。别的采样硬件解不成这种输出，返回 ESP_ERR_NOT_SUPPORTED（不交给驱动）。
// 其余要求和 decode_rgb565 一样。
esp_err_t decode_yuv422(const uint8_t* jpg, size_t len, uint8_t* out, size_t out_size, uint32_t* got = nullptr);

}  // namespace hwjpeg
