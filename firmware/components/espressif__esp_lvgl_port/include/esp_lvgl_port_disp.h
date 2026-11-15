/*
 * SPDX-FileCopyrightText: 2024 Espressif Systems (Shanghai) CO LTD
 *
 * SPDX-License-Identifier: Apache-2.0
 */

/**
 * @file
 * @brief ESP LVGL port display
 */

#pragma once

#include "esp_err.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "lvgl.h"

#if LVGL_VERSION_MAJOR == 8
#include "esp_lvgl_port_compatibility.h"
#endif

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief Rotation configuration
 */
typedef struct {
    bool swap_xy;  /*!< LCD Screen swapped X and Y (in esp_lcd driver) */
    bool mirror_x; /*!< LCD Screen mirrored X (in esp_lcd driver) */
    bool mirror_y; /*!< LCD Screen mirrored Y (in esp_lcd driver) */
} lvgl_port_rotation_cfg_t;

/**
 * @brief Configuration display structure
 */
typedef struct {
    esp_lcd_panel_io_handle_t io_handle;   /*!< LCD panel IO handle */
    esp_lcd_panel_handle_t panel_handle;   /*!< LCD panel handle */
    esp_lcd_panel_handle_t control_handle; /*!< LCD panel control handle */

    uint32_t buffer_size; /*!< Size of the buffer for the screen in pixels */
    bool double_buffer;   /*!< True, if should be allocated two buffers */
    uint32_t trans_size;  /*!< Allocated buffer will be in SRAM to move framebuf (optional) */

    uint32_t hres; /*!< LCD display horizontal resolution */
    uint32_t vres; /*!< LCD display vertical resolution */

    bool monochrome; /*!< True, if display is monochrome and using 1bit for 1px */

    lvgl_port_rotation_cfg_t
        rotation; /*!< Default values of the screen rotation (Only HW state. Not supported for default SW rotation!) */
#if LVGL_VERSION_MAJOR >= 9
    lv_color_format_t color_format; /*!< The color format of the display */
#endif
    struct {
        unsigned int buff_dma : 1;    /*!< Allocated LVGL buffer will be DMA capable */
        unsigned int buff_spiram : 1; /*!< Allocated LVGL buffer will be in PSRAM */
        unsigned int sw_rotate : 1;   /*!< Use software rotation (slower) or PPA if available */
#if LVGL_VERSION_MAJOR >= 9
        unsigned int swap_bytes : 1; /*!< Swap bytes in RGB656 (16-bit) color format before send to LCD driver */
#endif
        unsigned int full_refresh : 1; /*!< 1: Always make the whole screen redrawn */
        unsigned int direct_mode : 1;  /*!< 1: Use screen-sized buffers and draw to absolute coordinates */
    } flags;
} lvgl_port_display_cfg_t;

/**
 * @brief Configuration RGB display structure
 */
typedef struct {
    struct {
        unsigned int bb_mode : 1; /*!< 1: Use bounce buffer mode */
        unsigned int
            avoid_tearing : 1; /*!< 1: Use internal RGB buffers as a LVGL draw buffers to avoid tearing effect, enabling
                                  this option requires over two LCD buffers and may reduce the frame rate */
    } flags;
} lvgl_port_display_rgb_cfg_t;

/**
 * @brief Configuration MIPI-DSI display structure
 */
typedef struct {
    struct {
        unsigned int
            avoid_tearing : 1; /*!< 1: Use internal MIPI-DSI buffers as a LVGL draw buffers to avoid tearing effect,
                                  enabling this option requires over two LCD buffers and may reduce the frame rate */
    } flags;
} lvgl_port_display_dsi_cfg_t;

/**
 * @brief Add I2C/SPI/I8080 display handling to LVGL
 *
 * @note Allocated memory in this function is not free in deinit. You must call lvgl_port_remove_disp for free all
 * memory!
 *
 * @param disp_cfg Display configuration structure
 * @return Pointer to LVGL display or NULL when error occurred
 */
lv_display_t *lvgl_port_add_disp(const lvgl_port_display_cfg_t *disp_cfg);

/**
 * @brief Add MIPI-DSI display handling to LVGL
 *
 * @note Allocated memory in this function is not free in deinit. You must call lvgl_port_remove_disp for free all
 * memory!
 *
 * @param disp_cfg Display configuration structure
 * @param dsi_cfg MIPI-DSI display specific configuration structure
 * @return Pointer to LVGL display or NULL when error occurred
 */
lv_display_t *lvgl_port_add_disp_dsi(const lvgl_port_display_cfg_t *disp_cfg,
                                     const lvgl_port_display_dsi_cfg_t *dsi_cfg);

/**
 * @brief 自研页面过渡：底色淡入淡出(预旋转 + 每帧廉价混合直写 DPI 帧缓冲)。
 *
 * 横屏 UI 跑竖屏面板时 LVGL 每帧整屏 90° PPA 旋转 ≈45ms，整屏滑动很卡。本组过渡换页时
 * 只旋转两次(当前屏读帧缓冲、新屏旋转一次)，淡入淡出每帧只做 RGB565 与底色的 alpha 混合
 * 并直写帧缓冲，绕开每帧 45ms 旋转。必须在 LVGL 任务上下文同步调用(调用方持显示锁)。
 *
 * 用法(先黑屏再加载,慢速建页被黑屏遮挡)：
 *   lvgl_port_fade_out(disp, bg, ms);   // 淡出到底色
 *   ...建新屏(可含慢速读数据)...          // 期间帧缓冲停在底色
 *   lvgl_port_fade_in(disp, snap, w, h, bg, ms);  // 从底色淡入到新屏
 *
 * @param disp      LVGL display
 * @param color565  淡入淡出的底色(RGB565)
 * @param ms        本段时长(ms)
 * @param newB_land 新屏的横屏 RGB565 快照(land_w×land_h，紧凑 stride)
 * @param land_w    横屏宽(=LVGL 水平分辨率，如 1280)
 * @param land_h    横屏高(=LVGL 垂直分辨率，如 720)
 * @return ESP_OK 成功
 */
esp_err_t lvgl_port_fade_out(lv_display_t *disp, uint16_t color565, int ms);
esp_err_t lvgl_port_fade_in(lv_display_t *disp, const uint16_t *newB_land,
                            int land_w, int land_h, uint16_t color565, int ms);

/**
 * @brief 调试探针：取最近一次非阻塞 PPA 旋转的耗时与面积（多笔在途时为近似值）
 */
void lvgl_port_dbg_rot_get(uint32_t *us, uint32_t *px);
/* 调试探针：读取并清零 自上次读取以来的 flush 统计（次数/flush 回调内累计耗时/SW 回退次数） */
void lvgl_port_dbg_flush_get(uint32_t *calls, uint32_t *us, uint32_t *fallbacks);

/**
 * @brief 调试微基准：PSRAM/SRAM 带宽、PPA/CPU 旋转耗时（LVGL 上下文同步跑，会糟蹋屏幕内容）
 */
void lvgl_port_dbg_render_bench(lv_display_t *disp);

/* ── 实验性调试设施（'disp' 命令族）：均须在 LVGL 任务上下文调用 ── */
/* 扩展微基准：内存区真实速度/面板刷新率/CPU 旋转/DMA2D 拷贝/PPA 小块（会糟蹋屏幕，结束后需整屏重绘） */
void lvgl_port_dbg_render_bench2(lv_display_t *disp);
/* 实验输出管线：0=PPA 直写(生产) 1=丢弃输出(只量渲染) 2=CPU 旋转单核 3=CPU 旋转双核 */
void lvgl_port_dbg_set_pipe(int mode);
/* 运行时把绘制缓冲换成 rows 行高小条带（rows<=0 恢复整屏缓冲） */
esp_err_t lvgl_port_dbg_set_drawbuf_rows(lv_display_t *disp, int rows);
/* 位移帧分段耗时（读取即清零）：out = {帧数, 等围栏us, 拷贝us, flush等拷贝us, 等PPAus, 总us} */
void lvgl_port_dbg_shift_get(uint32_t out[6]);

/**
 * @brief 滚动位移复用：REFR_START(渲染前)受理本周期位移帧并启动 GDMA 平移（与渲染并行）
 * @param dy_landscape 横屏纵向滚动增量，>0=内容上移（底部露新）
 * @return true=已受理（本周期 flush 写后台 FB，REFR_READY 必须调 finish 翻页）
 */
bool lvgl_port_scroll_shift_arm(lv_display_t *disp, int dy_landscape);

/**
 * @brief 滚动位移复用：REFR_READY 时等在途 PPA 清空并把扫出翻到后台 FB（帧边界生效）
 */
void lvgl_port_scroll_shift_finish(lv_display_t *disp);

/**
 * @brief 调试用：开机以来，围栏没放行的帧边界次数——登记了翻页（滚动位移帧、直接送屏都算）、
 *        帧边界中断那一次挂上去的却不是登记的那一块。包括：登记正好撞在中断中间（中断开头已经定了挂旧的那块，
 *        只凭「登记之后来过中断」会早一帧放行的就是这种）；中断落在「记下要换哪一块」和「登记」两步之间；
 *        切色彩格式停住扫出的那几帧（停着、重启的那一次都不报挂的是哪一块）。翻页生效的时刻和平时一样，围栏只是按真实生效的时刻放行。
 */
uint32_t lvgl_port_dbg_flip_collisions(void);

/**
 * @brief 直接送屏（无线显示用）：调用方绕开 LVGL，自己往帧缓冲里写、在帧边界翻页。用面板的三块帧缓冲：
 *        一块在扫出，一块登记了等着上屏，还有一块空着可以写——一帧画完登记翻页之后不用等它生效，马上能往空着的那块画下一帧。
 *        哪块在扫出、哪块等着上屏、哪块能写由调用方自己记（规矩见下面三个 flip 函数）。
 *
 * 用法：先 lvgl_port_stop() 停掉 LVGL 的刷新 → lvgl_port_direct_begin() 拿到三块帧缓冲 →
 * 往能写的那块里写、lvgl_port_direct_flip_begin() 登记换上屏幕（循环）→ 结束时 lvgl_port_direct_end()、
 * lvgl_port_resume() 并让当前屏整屏失效。
 *
 * 帧缓冲是面板原生方向（竖屏 720×1280，每像素 2 字节）。横屏画面第 y 行、第 x 列在帧缓冲的第 (1279−x) 行、第 y 列。
 *
 * @param[out] fbs fbs[0] 正在扫出，fbs[1]、fbs[2] 没在扫出、可以写
 * @return false = 面板没有三块帧缓冲，或者正处在滚动位移帧当中（不应出现：LVGL 应该已经停了）
 */
bool lvgl_port_direct_begin(uint16_t *fbs[3]);

/**
 * @brief 登记翻页、等它生效。
 *
 * lvgl_port_direct_flip_begin(fb)：登记把 fb（三块之一，不能是正在扫出、或者上一次登记的那块）换上屏幕，不等。
 *   翻页在面板的下一个帧边界生效；**生效之前**原来扫出的那块还在被扫出、fb 马上要上屏，这两块都不能写，剩下的一块可以。
 *   生效之后原来扫出的那块空出来。false = 没登记上。
 * lvgl_port_direct_flip_wait()：等登记的翻页生效，最多 wait_ms 毫秒（0 = 只看一眼）。true = 帧边界过去了。
 * lvgl_port_direct_flip_end()：翻页生效之后（或者不打算再等了）调。返回翻页生效的时刻（esp_timer_get_time() 的微秒数）；
 *   没等到帧边界（flip_wait 没返回过 true）就调的话返回 0，这一帧可能撕裂一下。
 *
 * begin 之后必须调 end 才能再 begin（同一时刻只能有一次登记了没生效的翻页）；这些函数只能在同一个任务里用。
 */
bool lvgl_port_direct_flip_begin(uint16_t *fb);
bool lvgl_port_direct_flip_wait(uint32_t wait_ms);
int64_t lvgl_port_direct_flip_end(void);

/**
 * @brief 直接送屏结束、lvgl_port_resume() 之前调（不能有登记了没生效的翻页）：扫出的是第三块的话，
 *        把它的画面拷到 LVGL 用的两块之一、换过去、等它生效（屏幕上的画面不变），之后 LVGL 照常接着用那两块。
 *
 * @return false = 换回去的那次翻页没登记上（试了两次）：屏幕还停在第三块上，LVGL 画的东西要到滚动合成器下一次翻页才看得见。
 *         等帧边界超时不算失败（翻页已经登记，下一个帧边界照样生效），只打一条日志
 */
bool lvgl_port_direct_end(void);

/**
 * @brief Add RGB display handling to LVGL
 *
 * @note Allocated memory in this function is not free in deinit. You must call lvgl_port_remove_disp for free all
 * memory!
 *
 * @param disp_cfg Display configuration structure
 * @param rgb_cfg RGB display specific configuration structure
 * @return Pointer to LVGL display or NULL when error occurred
 */
lv_display_t *lvgl_port_add_disp_rgb(const lvgl_port_display_cfg_t *disp_cfg,
                                     const lvgl_port_display_rgb_cfg_t *rgb_cfg);

/**
 * @brief Remove display handling from LVGL
 *
 * @note Free all memory used for this display.
 *
 * @return
 *      - ESP_OK                    on success
 */
esp_err_t lvgl_port_remove_disp(lv_display_t *disp);

#ifdef __cplusplus
}
#endif
