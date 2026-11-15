/*
 * SPDX-FileCopyrightText: 2023-2025 Espressif Systems (Shanghai) CO LTD
 *
 * SPDX-License-Identifier: Apache-2.0
 */
#include <sys/param.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "soc/soc_caps.h"
#include "esp_check.h"
#include "esp_lcd_panel_interface.h"
#include "esp_lcd_mipi_dsi.h"
#include "esp_clk_tree.h"
#include "esp_cache.h"
#include "mipi_dsi_priv.h"
#include "esp_async_fbcpy.h"
#include "esp_memory_utils.h"
#include "esp_private/dw_gdma.h"
#include "esp_private/esp_clk_tree_common.h"
#include "hal/cache_hal.h"
#include "hal/cache_ll.h"
#include "hal/color_hal.h"
#include "esp_rom_sys.h"
#include "esp_timer.h"

static const char *TAG = "lcd.dsi.dpi";

typedef struct esp_lcd_dpi_panel_t esp_lcd_dpi_panel_t;

static esp_err_t dpi_panel_del(esp_lcd_panel_t *panel);
static esp_err_t dpi_panel_init(esp_lcd_panel_t *panel);
static esp_err_t dpi_panel_draw_bitmap(esp_lcd_panel_t *panel, int x_start, int y_start, int x_end, int y_end, const void *color_data);

struct esp_lcd_dpi_panel_t {
    esp_lcd_panel_t base;         // Base class of generic lcd panel
    esp_lcd_dsi_bus_handle_t bus; // DSI bus handle
    uint8_t virtual_channel;      // Virtual channel ID, index from 0
    uint8_t cur_fb_index;         // Current frame buffer index
    uint8_t num_fbs;              // Number of frame buffers
    uint8_t *fbs[DPI_PANEL_MAX_FB_NUM]; // Frame buffers
    uint32_t h_pixels;            // Horizontal pixels
    uint32_t v_pixels;            // Vertical pixels
    size_t fb_size;               // Frame buffer size, in bytes
    size_t bits_per_pixel;        // Bits per pixel
    lcd_color_format_t in_color_format;  // Input color format
    lcd_color_format_t out_color_format; // Output color format
    dw_gdma_channel_handle_t dma_chan;    // DMA channel
    dw_gdma_link_list_handle_t link_lists[DPI_PANEL_MAX_FB_NUM]; // DMA link list
    esp_async_fbcpy_handle_t fbcpy_handle; // Use DMA2D to do frame buffer copy
    SemaphoreHandle_t draw_sem;            // A semaphore used to synchronize the draw operations when DMA2D is used
    esp_pm_lock_handle_t pm_lock;          // Power management lock
    esp_lcd_dpi_panel_color_trans_done_cb_t on_color_trans_done; // Callback invoked when color data transfer has finished
    esp_lcd_dpi_panel_refresh_done_cb_t on_refresh_done; // Callback invoked when one refresh operation finished (kinda like a vsync end)
    void *user_ctx; // User context for the callback
    // 【本工程加的】以下都是运行中切色彩格式用的（esp_lcd_dpi_panel_switch_color_format）
    volatile bool switching_format; // 刚切过色彩格式，桥的欠载是切的过程中有意造成的
    portMUX_TYPE hold_lock;         // 保护 hold_dma / dma_held（任务和帧结束中断可能在两个核上）
    bool hold_dma;                  // 任务的请求：下一次帧结束中断不要重启 DMA
    bool dma_held;                  // 帧结束中断照办了：DMA 送完一整帧之后停着，等任务重启
    bool frame_end_valid;           // frame_end_us 是上一帧的结束时刻（DMA 停过之后头一次不算）。DMA 跑着时只有中断读写
    volatile uint32_t frame_end_us; // 最近一次帧结束中断的时刻（esp_timer 的微秒数，低 32 位）
    volatile uint32_t frame_us;     // 帧周期：相邻两次帧结束中断隔多久（微秒），0 = 还没量到
    uint32_t nominal_frame_us;      // 按时序和 DPI 时钟分频算出来的帧周期（微秒）。和实测差 1% 上下，只用来筛掉量歪了的 frame_us
    uint16_t v_blank_lines;         // 一帧里场消隐占几行（同步 + 前肩 + 后肩）
    uint16_t v_total_lines;         // 一帧一共几行
};

IRAM_ATTR
static bool async_fbcpy_done_cb(esp_async_fbcpy_handle_t mcp, esp_async_fbcpy_event_data_t *event, void *cb_args)
{
    bool need_yield = false;
    esp_lcd_dpi_panel_t *dpi_panel = (esp_lcd_dpi_panel_t *)cb_args;

    // release the draw semaphore first
    BaseType_t task_woken = pdFALSE;
    xSemaphoreGiveFromISR(dpi_panel->draw_sem, &task_woken);
    if (task_woken == pdTRUE) {
        need_yield = true;
    }

    if (dpi_panel->on_color_trans_done) {
        if (dpi_panel->on_color_trans_done(&dpi_panel->base, NULL, dpi_panel->user_ctx)) {
            need_yield = true;
        }
    }

    return need_yield;
}

// 【本工程加的】帧结束中断量出来的帧周期，离标称值（nominal_frame_us）这么远以内才收
#define FRAME_PERIOD_TOL_PERCENT 3

IRAM_ATTR
static bool dma_trans_done_cb(dw_gdma_channel_handle_t chan, const dw_gdma_trans_done_event_data_t *event_data, void *user_data)
{
    bool yield_needed = false;
    esp_lcd_dpi_panel_t *dpi_panel = (esp_lcd_dpi_panel_t *)user_data;
    mipi_dsi_hal_context_t *hal = &dpi_panel->bus->hal;
    uint8_t fb_index = dpi_panel->cur_fb_index;
    dw_gdma_link_list_handle_t link_list = dpi_panel->link_lists[fb_index];

    // clear the interrupt status
    uint32_t error_status = mipi_dsi_brg_ll_get_interrupt_status(hal->bridge);
    mipi_dsi_brg_ll_clear_interrupt_status(hal->bridge, error_status);
    if (unlikely(error_status & MIPI_DSI_LL_EVENT_UNDERRUN)) {
        // when an underrun happens, the LCD display may already becomes blue
        // it's too late to recover the display, so we just print an error message
        // as a hint to the user that he should optimize the memory bandwidth (with AXI-ICM)
        // 【本工程改的】切色彩格式时有意让桥断流，那之后头一两帧报的欠载不算
        if (!dpi_panel->switching_format) {
            ESP_DRAM_LOGE(TAG, "can't fetch data from external memory fast enough, underrun happens");
        }
    } else {
        dpi_panel->switching_format = false;      // 切完之后第一个没有欠载的帧：恢复照常报
    }

    // 【本工程加的】记下帧结束的时刻和帧周期：切色彩格式时拿它算「现在在一帧里的什么位置」
    const uint32_t now_us = (uint32_t)esp_timer_get_time();
    if (dpi_panel->frame_end_valid) {
        // 这次或上次中断晚到了（别处关着中断）、中间漏了一次，量出来的都不是帧周期：离标称值远的不收
        const uint32_t period = now_us - dpi_panel->frame_end_us;
        const uint32_t tol = dpi_panel->nominal_frame_us * FRAME_PERIOD_TOL_PERCENT / 100;
        if (period - (dpi_panel->nominal_frame_us - tol) <= 2 * tol) {
            dpi_panel->frame_us = period;
        }
    }
    dpi_panel->frame_end_us = now_us;
    dpi_panel->frame_end_valid = true;
    // 【本工程加的】切色彩格式的任务要求 DMA 停在帧尾：这一次不重启，由它改完格式之后重启
    portENTER_CRITICAL_ISR(&dpi_panel->hold_lock);
    const bool hold = dpi_panel->hold_dma;
    if (hold) {
        dpi_panel->dma_held = true;
    }
    portEXIT_CRITICAL_ISR(&dpi_panel->hold_lock);

    if (!hold) {
        // restart the DMA transfer, keep refreshing the LCD
        dw_gdma_block_markers_t markers = {
            .is_valid = true,
            .is_last = true,
        };
        dw_gdma_lli_set_block_markers(dw_gdma_link_list_get_item(link_list, 0), markers);
        dw_gdma_channel_use_link_list(chan, link_list);
        dw_gdma_channel_enable_ctrl(chan, true);
    }

    // the DMA descriptor is large enough to carry a whole frame buffer, so this event can also be treated as a fake "vsync end"
    if (dpi_panel->on_refresh_done) {
        // 【本工程改的】告诉回调这一次实际挂上去的是哪一块（开头读的 fb_index，不是现在的 cur_fb_index：任务可能刚改过它）
        esp_lcd_dpi_panel_event_data_t edata = {
            .next_fb = hold ? NULL : dpi_panel->fbs[fb_index],
        };
        if (dpi_panel->on_refresh_done(&dpi_panel->base, &edata, dpi_panel->user_ctx)) {
            yield_needed = true;
        }
    }
    return yield_needed;
}

// Please note, errors happened in this function is just propagated to the caller
// dpi_panel_del() is actually doing the error handling
static esp_err_t dpi_panel_create_dma_link(esp_lcd_dpi_panel_t *dpi_panel)
{
    dw_gdma_channel_handle_t dma_chan = NULL;
    dw_gdma_link_list_handle_t link_list = NULL;
    // sending image stream from memory to the DSI bridge
    dw_gdma_channel_alloc_config_t dma_alloc_config = {
        .src = {
            .block_transfer_type = DW_GDMA_BLOCK_TRANSFER_LIST,
            .role = DW_GDMA_ROLE_MEM,
            .handshake_type = DW_GDMA_HANDSHAKE_HW,
            .num_outstanding_requests = 5,
        },
        .dst = {
            .block_transfer_type = DW_GDMA_BLOCK_TRANSFER_LIST,
            .role = DW_GDMA_ROLE_PERIPH_DSI,
            .handshake_type = DW_GDMA_HANDSHAKE_HW,
            .num_outstanding_requests = 2,
        },
        .flow_controller = DW_GDMA_FLOW_CTRL_SELF, // DMA as the flow controller
        .chan_priority = 1,
    };
    ESP_RETURN_ON_ERROR(dw_gdma_new_channel(&dma_alloc_config, &dma_chan), TAG, "create DMA channel failed");
    dpi_panel->dma_chan = dma_chan;

    // create DMA link lists
    dw_gdma_link_list_config_t link_list_config = {
        .num_items = DPI_PANEL_MIN_DMA_NODES_PER_LINK,
        .link_type = DW_GDMA_LINKED_LIST_TYPE_SINGLY,
    };
    for (int i = 0; i < dpi_panel->num_fbs; i++) {
        ESP_RETURN_ON_ERROR(dw_gdma_new_link_list(&link_list_config, &link_list), TAG, "create DMA link list failed");
        dpi_panel->link_lists[i] = link_list;
    }

    // register DMA ISR callbacks
    dw_gdma_event_callbacks_t dsi_dma_cbs = {
        .on_full_trans_done = dma_trans_done_cb,
    };
    ESP_RETURN_ON_ERROR(dw_gdma_channel_register_event_callbacks(dma_chan, &dsi_dma_cbs, dpi_panel), TAG, "register DMA callbacks failed");

    return ESP_OK;
}

esp_err_t esp_lcd_new_panel_dpi(esp_lcd_dsi_bus_handle_t bus, const esp_lcd_dpi_panel_config_t *panel_config, esp_lcd_panel_handle_t *ret_panel)
{
    esp_err_t ret = ESP_OK;
    esp_lcd_dpi_panel_t *dpi_panel = NULL;
    esp_async_fbcpy_handle_t fbcpy_ctx = NULL;
    ESP_RETURN_ON_FALSE(bus && panel_config && ret_panel, ESP_ERR_INVALID_ARG, TAG, "invalid argument");
    ESP_RETURN_ON_FALSE(panel_config->virtual_channel < 4, ESP_ERR_INVALID_ARG, TAG, "invalid virtual channel %d", panel_config->virtual_channel);
    ESP_RETURN_ON_FALSE(panel_config->dpi_clock_freq_mhz, ESP_ERR_INVALID_ARG, TAG, "invalid DPI clock frequency %"PRIu32, panel_config->dpi_clock_freq_mhz);
#if !SOC_DMA2D_SUPPORTED
    ESP_RETURN_ON_FALSE(!panel_config->flags.use_dma2d, ESP_ERR_NOT_SUPPORTED, TAG, "DMA2D is not supported");
#endif // !SOC_DMA2D_SUPPORTED
    size_t num_fbs = panel_config->num_fbs;
    // if the user doesn't specify the number of frame buffers, then fallback to use one frame buffer
    if (num_fbs == 0) {
        num_fbs = 1;
    }
    ESP_RETURN_ON_FALSE(num_fbs <= DPI_PANEL_MAX_FB_NUM, ESP_ERR_INVALID_ARG, TAG, "num_fbs not within [1,%d]", DPI_PANEL_MAX_FB_NUM);

    size_t bits_per_pixel = 0;
    switch (panel_config->pixel_format) {
    case LCD_COLOR_PIXEL_FORMAT_RGB565:
        bits_per_pixel = 16;
        break;
    case LCD_COLOR_PIXEL_FORMAT_RGB666:
        // RGB data in the memory must be constructed in 6-6-6 (18 bits) for each pixel
        bits_per_pixel = 18;
        break;
    case LCD_COLOR_PIXEL_FORMAT_RGB888:
        bits_per_pixel = 24;
        break;
    }
    lcd_color_format_t in_color_format = COLOR_TYPE_ID(COLOR_SPACE_RGB, panel_config->pixel_format);
    // if user sets the in_color_format, it can override the pixel format setting
    if (panel_config->in_color_format) {
        color_space_pixel_format_t in_color_id = {
            .color_type_id = panel_config->in_color_format,
        };
        bits_per_pixel = color_hal_pixel_format_get_bit_depth(in_color_id);
        in_color_format = panel_config->in_color_format;
    }
    lcd_color_format_t out_color_format = in_color_format;
    if (panel_config->out_color_format) {
        out_color_format = panel_config->out_color_format;
    }
    ESP_RETURN_ON_FALSE(panel_config->video_timing.h_size * panel_config->video_timing.v_size * bits_per_pixel % 8 == 0,
                        ESP_ERR_INVALID_ARG, TAG, "frame buffer size not aligned to byte boundary");

    int bus_id = bus->bus_id;
    mipi_dsi_hal_context_t *hal = &bus->hal;

    dpi_panel = heap_caps_calloc(1, sizeof(esp_lcd_dpi_panel_t), DSI_MEM_ALLOC_CAPS);
    ESP_GOTO_ON_FALSE(dpi_panel, ESP_ERR_NO_MEM, err, TAG, "no memory for DPI panel");
    dpi_panel->virtual_channel = panel_config->virtual_channel;
    dpi_panel->in_color_format = in_color_format;
    dpi_panel->out_color_format = out_color_format;
    dpi_panel->bus = bus;
    dpi_panel->num_fbs = num_fbs;

    // allocate frame buffer from PSRAM
    uint32_t cache_line_size = cache_hal_get_cache_line_size(CACHE_LL_LEVEL_EXT_MEM, CACHE_TYPE_DATA);
    // DMA doesn't have requirement on the buffer alignment, but the cache does
    uint32_t alignment = cache_line_size;
    size_t fb_size = panel_config->video_timing.h_size * panel_config->video_timing.v_size * bits_per_pixel / 8;
    uint8_t *frame_buffer = NULL;
    for (int i = 0; i < num_fbs; i++) {
        frame_buffer = heap_caps_aligned_calloc(alignment, 1, fb_size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
        ESP_GOTO_ON_FALSE(frame_buffer, ESP_ERR_NO_MEM, err, TAG, "no memory for frame buffer");
        dpi_panel->fbs[i] = frame_buffer;
        ESP_LOGD(TAG, "fb[%d] @%p", i, frame_buffer);
        // preset the frame buffer with black color
        // the frame buffer address alignment is ensured by `heap_caps_aligned_calloc`
        // while the value of the fb_size may not be aligned to the cache line size
        // but that's not a problem because the `heap_caps_aligned_calloc` internally allocated a buffer whose size is aligned up to the cache line size
        ESP_GOTO_ON_ERROR(esp_cache_msync(frame_buffer, fb_size, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_UNALIGNED),
                          err, TAG, "cache write back failed");
    }
    dpi_panel->fb_size = fb_size;
    dpi_panel->bits_per_pixel = bits_per_pixel;
    dpi_panel->h_pixels = panel_config->video_timing.h_size;
    dpi_panel->v_pixels = panel_config->video_timing.v_size;
    // 【本工程加的】
    portMUX_INITIALIZE(&dpi_panel->hold_lock);
    dpi_panel->v_blank_lines = panel_config->video_timing.vsync_pulse_width + panel_config->video_timing.vsync_back_porch
                               + panel_config->video_timing.vsync_front_porch;
    dpi_panel->v_total_lines = dpi_panel->v_blank_lines + panel_config->video_timing.v_size;

#if SOC_DMA2D_SUPPORTED
    if (panel_config->flags.use_dma2d) {
        esp_async_fbcpy_config_t fbcpy_config = {};
        ESP_GOTO_ON_ERROR(esp_async_fbcpy_install(&fbcpy_config, &fbcpy_ctx), err, TAG, "install async memcpy 2d failed");
        dpi_panel->fbcpy_handle = fbcpy_ctx;
        dpi_panel->draw_sem = xSemaphoreCreateBinaryWithCaps(DSI_MEM_ALLOC_CAPS);
        ESP_GOTO_ON_FALSE(dpi_panel->draw_sem, ESP_ERR_NO_MEM, err, TAG, "no memory for draw semaphore");
        xSemaphoreGive(dpi_panel->draw_sem);
    }
#endif // SOC_DMA2D_SUPPORTED

    // if the clock source is not assigned, fallback to the default clock source
    mipi_dsi_dpi_clock_source_t dpi_clk_src = panel_config->dpi_clk_src;
    if (dpi_clk_src == 0) {
        dpi_clk_src = MIPI_DSI_DPI_CLK_SRC_DEFAULT;
    }
    // get the clock source frequency
    uint32_t dpi_clk_src_freq_hz = 0;
    ESP_GOTO_ON_ERROR(esp_clk_tree_src_get_freq_hz(dpi_clk_src, ESP_CLK_TREE_SRC_FREQ_PRECISION_CACHED,
                                                   &dpi_clk_src_freq_hz), err, TAG, "get clock source frequency failed");
    // divide the source clock to get the final DPI clock
    uint32_t dpi_div = mipi_dsi_hal_host_dpi_calculate_divider(hal, dpi_clk_src_freq_hz / 1000 / 1000, panel_config->dpi_clock_freq_mhz);
    // 【本工程加的】标称帧周期 = 一帧的像素时钟数 ÷ 实际的 DPI 时钟（时钟源 ÷ 整数分频，不一定等于配置里写的那个频率）
    const uint32_t h_total = panel_config->video_timing.hsync_pulse_width + panel_config->video_timing.hsync_back_porch
                             + panel_config->video_timing.h_size + panel_config->video_timing.hsync_front_porch;
    dpi_panel->nominal_frame_us = (uint32_t)((uint64_t)h_total * dpi_panel->v_total_lines * dpi_div * 1000000 / dpi_clk_src_freq_hz);
    esp_clk_tree_enable_src((soc_module_clk_t)dpi_clk_src, true);
    // set the clock source, set the divider, and enable the dpi clock
    DSI_CLOCK_SRC_ATOMIC() {
        mipi_dsi_ll_set_dpi_clock_source(bus_id, dpi_clk_src);
        mipi_dsi_ll_set_dpi_clock_div(bus_id, dpi_div);
        mipi_dsi_ll_enable_dpi_clock(bus_id, true);
    }

#if CONFIG_PM_ENABLE
    // When MIPI DSI is working, we don't expect the clock source would be turned off
    // use CPU_MAX lock to ensure PSRAM bandwidth and usability during DFS
    esp_pm_lock_type_t pm_lock_type = ESP_PM_CPU_FREQ_MAX;
    ret  = esp_pm_lock_create(pm_lock_type, 0, "dsi_dpi", &dpi_panel->pm_lock);
    ESP_GOTO_ON_ERROR(ret, err, TAG, "create PM lock failed");
    esp_pm_lock_acquire(dpi_panel->pm_lock);
#endif

    // create DMA resources
    ESP_GOTO_ON_ERROR(dpi_panel_create_dma_link(dpi_panel), err, TAG, "initialize DMA link failed");

    mipi_dsi_host_ll_dpi_set_vcid(hal->host, panel_config->virtual_channel);
    mipi_dsi_hal_host_dpi_set_color_coding(hal, out_color_format, 0);
    // these signals define how the DPI interface interacts with the controller
    mipi_dsi_host_ll_dpi_set_timing_polarity(hal->host, false, false, false, false, false);

    if (panel_config->flags.disable_lp) {
        // configure the low-power transitions: defines the video periods which are NOT permitted to goto low-power
        mipi_dsi_host_ll_dpi_enable_lp_horizontal_timing(hal->host, false, false);
        mipi_dsi_host_ll_dpi_enable_lp_vertical_timing(hal->host, false, false, false, false);
        // commands are NOT transmitted in low-power mode
        mipi_dsi_host_ll_dpi_enable_lp_command(hal->host, false);
    } else {
        // configure the low-power transitions: defines the video periods which are permitted to goto low-power if the time available to do so
        mipi_dsi_host_ll_dpi_enable_lp_horizontal_timing(hal->host, true, true);
        mipi_dsi_host_ll_dpi_enable_lp_vertical_timing(hal->host, true, true, true, true);
        // commands are transmitted in low-power mode
        mipi_dsi_host_ll_dpi_enable_lp_command(hal->host, true);
    }
    // after sending a frame, the DSI device should return an ack
    mipi_dsi_host_ll_dpi_enable_frame_ack(hal->host, true);
    // using the burst mode because it's energy-efficient
    mipi_dsi_host_ll_dpi_set_video_burst_type(hal->host, MIPI_DSI_LL_VIDEO_BURST_WITH_SYNC_PULSES);
    // configure the size of the active lin period, measured in pixels
    mipi_dsi_host_ll_dpi_set_video_packet_pixel_num(hal->host, panel_config->video_timing.h_size);
    // disable multi-packets
    mipi_dsi_host_ll_dpi_set_trunks_num(hal->host, 0);
    // disable "null packets"
    mipi_dsi_host_ll_dpi_set_null_packet_size(hal->host, 0);
    // set horizontal and vertical timing configuration
    mipi_dsi_hal_host_dpi_set_horizontal_timing(hal, panel_config->video_timing.hsync_pulse_width,
                                                panel_config->video_timing.hsync_back_porch,
                                                panel_config->video_timing.h_size,
                                                panel_config->video_timing.hsync_front_porch);
    mipi_dsi_hal_host_dpi_set_vertical_timing(hal, panel_config->video_timing.vsync_pulse_width,
                                              panel_config->video_timing.vsync_back_porch,
                                              panel_config->video_timing.v_size,
                                              panel_config->video_timing.vsync_front_porch);
    mipi_dsi_brg_ll_set_num_pixel_bits(hal->bridge, panel_config->video_timing.h_size * panel_config->video_timing.v_size * bits_per_pixel);
    mipi_dsi_brg_ll_set_underrun_discard_count(hal->bridge, panel_config->video_timing.h_size);
    // set input color space
    mipi_dsi_brg_ll_set_input_color_space(hal->bridge, COLOR_SPACE_TYPE(in_color_format));
    // use the DW_GDMA as the flow controller
    mipi_dsi_brg_ll_set_flow_controller(hal->bridge, MIPI_DSI_LL_FLOW_CONTROLLER_DMA);
    mipi_dsi_brg_ll_set_multi_block_number(hal->bridge, DPI_PANEL_MIN_DMA_NODES_PER_LINK);
    mipi_dsi_brg_ll_set_burst_len(hal->bridge, 256);
    mipi_dsi_brg_ll_set_empty_threshold(hal->bridge, 1024 - 256);
    // enable DSI bridge
    mipi_dsi_brg_ll_enable(hal->bridge, true);
    mipi_dsi_brg_ll_update_dpi_config(hal->bridge);

    dpi_panel->base.del = dpi_panel_del;
    dpi_panel->base.init = dpi_panel_init;
    dpi_panel->base.draw_bitmap = dpi_panel_draw_bitmap;
    *ret_panel = &dpi_panel->base;
    ESP_LOGD(TAG, "dpi panel created @%p", dpi_panel);
    return ESP_OK;
err:
    if (dpi_panel) {
        dpi_panel_del(&dpi_panel->base);
    }
    return ret;
}

static esp_err_t dpi_panel_del(esp_lcd_panel_t *panel)
{
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
    esp_lcd_dsi_bus_handle_t bus = dpi_panel->bus;
    int bus_id = bus->bus_id;
    mipi_dsi_hal_context_t *hal = &bus->hal;
    // disable the DPI clock
    DSI_CLOCK_SRC_ATOMIC() {
        mipi_dsi_ll_enable_dpi_clock(bus_id, false);
    }
    // disable the DSI bridge
    mipi_dsi_brg_ll_enable(hal->bridge, false);
    // free memory
    if (dpi_panel->dma_chan) {
        dw_gdma_del_channel(dpi_panel->dma_chan);
    }
    for (int i = 0; i < DPI_PANEL_MAX_FB_NUM; i++) {
        if (dpi_panel->fbs[i]) {
            free(dpi_panel->fbs[i]);
        }
    }
    for (int i = 0; i < DPI_PANEL_MAX_FB_NUM; i++) {
        if (dpi_panel->link_lists[i]) {
            dw_gdma_del_link_list(dpi_panel->link_lists[i]);
        }
    }
    if (dpi_panel->fbcpy_handle) {
        esp_async_fbcpy_uninstall(dpi_panel->fbcpy_handle);
    }
    if (dpi_panel->draw_sem) {
        vSemaphoreDeleteWithCaps(dpi_panel->draw_sem);
    }
    if (dpi_panel->pm_lock) {
        esp_pm_lock_release(dpi_panel->pm_lock);
        esp_pm_lock_delete(dpi_panel->pm_lock);
    }
    free(dpi_panel);
    return ESP_OK;
}

esp_err_t esp_lcd_dpi_panel_get_frame_buffer(esp_lcd_panel_handle_t panel, uint32_t fb_num, void **fb0, ...)
{
    ESP_RETURN_ON_FALSE(panel, ESP_ERR_INVALID_ARG, TAG, "invalid argument");
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
    ESP_RETURN_ON_FALSE(fb_num && fb_num <= dpi_panel->num_fbs, ESP_ERR_INVALID_ARG, TAG, "invalid frame buffer number");
    void **fb_itor = fb0;
    va_list args;
    va_start(args, fb0);
    for (uint32_t i = 0; i < fb_num; i++) {
        if (fb_itor) {
            *fb_itor = dpi_panel->fbs[i];
            fb_itor = va_arg(args, void **);
        }
    }
    va_end(args);
    return ESP_OK;
}

static esp_err_t dpi_panel_init(esp_lcd_panel_t *panel)
{
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
    esp_lcd_dsi_bus_handle_t bus = dpi_panel->bus;
    mipi_dsi_hal_context_t *hal = &bus->hal;
    dw_gdma_channel_handle_t dma_chan = dpi_panel->dma_chan;
    dw_gdma_link_list_handle_t link_list = NULL;

    dw_gdma_block_transfer_config_t dma_transfer_config = {
        .src = {
            .burst_mode = DW_GDMA_BURST_MODE_INCREMENT,
            .burst_items = DW_GDMA_BURST_ITEMS_512,
            .burst_len = 16,
            .width = DW_GDMA_TRANS_WIDTH_64,
        },
        .dst = {
            .addr = MIPI_DSI_BRG_MEM_BASE,
            .burst_mode = DW_GDMA_BURST_MODE_FIXED,
            .burst_items = DW_GDMA_BURST_ITEMS_256,
            .burst_len = 16,
            .width = DW_GDMA_TRANS_WIDTH_64,
        },
        .size = dpi_panel->fb_size * 8 / 64,
    };
    for (int i = 0; i < dpi_panel->num_fbs; i++) {
        link_list = dpi_panel->link_lists[i];
        dma_transfer_config.src.addr = (uint32_t)(dpi_panel->fbs[i]);
        dw_gdma_lli_config_transfer(dw_gdma_link_list_get_item(link_list, 0), &dma_transfer_config);
        dw_gdma_block_markers_t markers = {
            .is_valid = true,
            .is_last = true,
        };
        dw_gdma_lli_set_block_markers(dw_gdma_link_list_get_item(link_list, 0), markers);
    }

    // by default, we use the fb0 as the first working frame buffer
    dpi_panel->cur_fb_index = 0;
    link_list = dpi_panel->link_lists[0];
    dw_gdma_channel_use_link_list(dma_chan, link_list);
    // enable the DMA channel
    dw_gdma_channel_enable_ctrl(dma_chan, true);

    // enable the video mode
    mipi_dsi_host_ll_enable_video_mode(hal->host, true);
    // switch the clock lane to high speed mode
    mipi_dsi_host_ll_set_clock_lane_state(hal->host, MIPI_DSI_LL_CLOCK_LANE_STATE_AUTO);

    // enable the DPI output of the DSI bridge
    mipi_dsi_brg_ll_enable_dpi_output(hal->bridge, true);
    mipi_dsi_brg_ll_update_dpi_config(hal->bridge);

    // enable the underrun interrupt, we use this as a signal of bandwidth shortage
    // note, we opt to not install a dedicated interrupt handler just for this error condition, instead, we check it in the DMA callback
    mipi_dsi_brg_ll_enable_interrupt(hal->bridge, MIPI_DSI_LL_EVENT_UNDERRUN, true);

    return ESP_OK;
}

static esp_err_t dpi_panel_draw_bitmap(esp_lcd_panel_t *panel, int x_start, int y_start, int x_end, int y_end, const void *color_data)
{
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
    uint8_t cur_fb_index = dpi_panel->cur_fb_index;
    uint8_t *frame_buffer = dpi_panel->fbs[cur_fb_index];
    uint8_t *draw_buffer = (uint8_t *)color_data;
    size_t fb_size = dpi_panel->fb_size;
    size_t bits_per_pixel = dpi_panel->bits_per_pixel;

    // clip to boundaries
    int h_res = dpi_panel->h_pixels;
    int v_res = dpi_panel->v_pixels;
    x_start = MAX(x_start, 0);
    x_end = MIN(x_end, h_res);
    y_start = MAX(y_start, 0);
    y_end = MIN(y_end, v_res);

    bool do_copy = false;
    uint8_t draw_buf_fb_index = 0;
    // check if the user draw buffer resides in any frame buffer's memory range
    // if so, we don't need to copy the data, just do cache write back
    if (draw_buffer >= dpi_panel->fbs[0] && draw_buffer < dpi_panel->fbs[0] + fb_size) {
        draw_buf_fb_index = 0;
    } else if (draw_buffer >= dpi_panel->fbs[1] && draw_buffer < dpi_panel->fbs[1] + fb_size) {
        draw_buf_fb_index = 1;
    } else if (draw_buffer >= dpi_panel->fbs[2] && draw_buffer < dpi_panel->fbs[2] + fb_size) {
        draw_buf_fb_index = 2;
    } else {
        do_copy = true;
    }

    if (!do_copy) { // no copy, just do cache memory write back
        ESP_LOGV(TAG, "draw buffer is in frame buffer memory range, do cache write back only");
        // only write back the LCD lines that updated by the draw buffer
        uint8_t *cache_sync_start = dpi_panel->fbs[draw_buf_fb_index] + (y_start * dpi_panel->h_pixels) * bits_per_pixel / 8;
        size_t cache_sync_size = (y_end - y_start) * dpi_panel->h_pixels * bits_per_pixel / 8;
        // the buffer to be flushed is still within the frame buffer, so even an unaligned address is OK
        esp_cache_msync(cache_sync_start, cache_sync_size, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_UNALIGNED);

        dpi_panel->cur_fb_index = draw_buf_fb_index;
        // invoke the trans done callback
        if (dpi_panel->on_color_trans_done) {
            dpi_panel->on_color_trans_done(&dpi_panel->base, NULL, dpi_panel->user_ctx);
        }
    } else if (!dpi_panel->fbcpy_handle) { // copy by CPU
        ESP_LOGV(TAG, "copy draw buffer by CPU");
        const uint8_t *from = draw_buffer;
        uint8_t *to = frame_buffer + (y_start * dpi_panel->h_pixels + x_start) * bits_per_pixel / 8;
        uint32_t copy_bytes_per_line = (x_end - x_start) * bits_per_pixel / 8;
        uint32_t bytes_per_line = bits_per_pixel * dpi_panel->h_pixels / 8;
        // please note, we assume the user provided draw_buffer is compact,
        // but the destination is a sub-window of the frame buffer, so we need to skip the stride
        for (int y = y_start; y < y_end; y++) {
            memcpy(to, from, copy_bytes_per_line);
            to += bytes_per_line;
            from += copy_bytes_per_line;
        }
        uint8_t *cache_sync_start = frame_buffer + (y_start * dpi_panel->h_pixels) * bits_per_pixel / 8;
        size_t cache_sync_size = (y_end - y_start) * dpi_panel->h_pixels * bits_per_pixel / 8;
        // the buffer to be flushed is still within the frame buffer, so even an unaligned address is OK
        esp_cache_msync(cache_sync_start, cache_sync_size, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_UNALIGNED);
        // invoke the trans done callback
        if (dpi_panel->on_color_trans_done) {
            dpi_panel->on_color_trans_done(&dpi_panel->base, NULL, dpi_panel->user_ctx);
        }
    } else { // copy by DMA2D
        ESP_LOGV(TAG, "copy draw buffer by DMA2D");
        // ensure the previous draw operation is finished
        ESP_RETURN_ON_FALSE(xSemaphoreTake(dpi_panel->draw_sem, 0) == pdTRUE, ESP_ERR_INVALID_STATE,
                            TAG, "previous draw operation is not finished");

        // write back the user's draw buffer, so that the DMA can see the correct data
        // Note, the user draw buffer should be 1D array, and contiguous in memory, no stride
        size_t color_data_size = (x_end - x_start) * (y_end - y_start) * bits_per_pixel / 8;
        esp_cache_msync(draw_buffer, color_data_size, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_UNALIGNED);

        esp_async_fbcpy_trans_desc_t fbcpy_trans_config = {
            .src_buffer = draw_buffer,
            .dst_buffer = (void *)frame_buffer,
            .src_buffer_size_x = x_end - x_start,
            .src_buffer_size_y = y_end - y_start,
            .dst_buffer_size_x = dpi_panel->h_pixels,
            .dst_buffer_size_y = dpi_panel->v_pixels,
            .src_offset_x = 0,
            .src_offset_y = 0,
            .dst_offset_x = x_start,
            .dst_offset_y = y_start,
            .copy_size_x = x_end - x_start,
            .copy_size_y = y_end - y_start,
            .pixel_format_unique_id = {
                .color_type_id = dpi_panel->in_color_format,
            }
        };
        ESP_RETURN_ON_ERROR(esp_async_fbcpy(dpi_panel->fbcpy_handle, &fbcpy_trans_config, async_fbcpy_done_cb, dpi_panel), TAG, "async memcpy failed");
    }

    return ESP_OK;
}

esp_err_t esp_lcd_dpi_panel_set_color_conversion(esp_lcd_panel_handle_t panel, const esp_lcd_color_conv_config_t *config)
{
    ESP_RETURN_ON_FALSE(panel, ESP_ERR_INVALID_ARG, TAG, "invalid argument");
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
    esp_lcd_dsi_bus_handle_t bus = dpi_panel->bus;
    mipi_dsi_hal_context_t *hal = &bus->hal;

    if (dpi_panel->in_color_format == COLOR_TYPE_ID(COLOR_SPACE_YUV, COLOR_PIXEL_YUV422)
            && COLOR_SPACE_TYPE(dpi_panel->out_color_format) == LCD_COLOR_SPACE_RGB) {
        // YUV422->RGB
        mipi_dsi_brg_ll_set_yuv_convert_std(hal->bridge, config->spec.yuv.conv_std);
        mipi_dsi_brg_ll_set_yuv422_pack_order(hal->bridge, config->spec.yuv.yuv422.in_pack_order);
    } else {
        ESP_RETURN_ON_FALSE(false, ESP_ERR_NOT_SUPPORTED, TAG, "unsupported conversion mode");
    }
    return ESP_OK;
}

// 【本工程加的】DMA 停着（dma_held）的时候从当前帧缓冲的开头重新送，和 dma_trans_done_cb 里每帧重启的做法一样
static void dpi_panel_restart_dma(esp_lcd_dpi_panel_t *dpi_panel)
{
    dw_gdma_link_list_handle_t link_list = dpi_panel->link_lists[dpi_panel->cur_fb_index];
    dw_gdma_block_markers_t markers = {
        .is_valid = true,
        .is_last = true,
    };
    dw_gdma_lli_set_block_markers(dw_gdma_link_list_get_item(link_list, 0), markers);
    dw_gdma_channel_use_link_list(dpi_panel->dma_chan, link_list);
    // 这一帧隔了不止一个帧周期才结束，不能拿来量帧周期（DMA 停着，没有帧结束中断来抢着写）
    dpi_panel->frame_end_valid = false;
    portENTER_CRITICAL(&dpi_panel->hold_lock);
    dpi_panel->hold_dma = false;
    dpi_panel->dma_held = false;
    portEXIT_CRITICAL(&dpi_panel->hold_lock);
    dw_gdma_channel_enable_ctrl(dpi_panel->dma_chan, true);
}

// 【本工程加的】运行中切换帧缓冲的色彩格式，说明见头文件。
//
// 实测出来的规矩（ESP32-P4，IDF v5.4.2）：
//   - 桥的 pixel_type（输入是 RGB 还是 YUV、输出的位深）不能在桥的 FIFO 里有数据时改：一改桥就不再从 FIFO 取数，
//     DMA 跟着停住，只有重启才恢复（改回去、停 / 起 DPI 输出、关 / 开桥都救不回来）。
//     所以先让 DMA 停下、时序发生器把 FIFO 里剩下的取完。之后时序发生器照常走、输出「欠载填充色」（这里设成黑），
//     这时候改 pixel_type 是安全的；DMA 重新从帧缓冲开头送，桥等到自己下一帧的帧头接着显示，画面是对齐的。
//     改的时刻要在有效行里：在场消隐期里改，之后桥只出黑画面。
//   - 主机的色彩编码（每像素几个字节）直接写的话马上生效，正传到一半的那一行在主机的行缓冲里留下残余，
//     之后每一行都错开同样多的像素（这块面板横着用，看上去是画面上下卷了一截），只有重启才恢复。
//     要经主机的影子寄存器改（vid_shadow_ctrl）：新值先放着，主机自己在下一帧的帧头换上去。
//   - 停 / 起 DPI 输出（esp_lcd_dpi_panel_set_pattern 的做法）用不着，也不解决上面任何一条。
//
// DMA 怎么停、什么时候改：不在任务里直接关 DMA 通道——每帧结束的中断（dma_trans_done_cb）会把它重新打开，任务和中断在两个核上时
// 谁先谁后说不准，FIFO 「看着空了」之后又进了数据、或者空的那一刻其实已经进了场消隐，都会撞上第一条。做法是请帧结束中断自己别重启
// （hold_dma）：DMA 送完一整帧自然停下，中断记下那一刻 t（frame_end_us）。之后的事都按 t 算，时序发生器是自己走的：
//   t + 几十微秒      FIFO 里最后几行取完，有效行结束，进场消隐
//   t + 场消隐的长度   下一帧的有效行开始，FIFO 是空的，桥输出填充色（黑），每行记一次欠载
//   t + 一个帧周期     这一帧的有效行结束，再进场消隐……
// 等到有效行的中段（离两头的场消隐都留 SWITCH_MARGIN_US），关着中断再核对一遍「时刻在窗口里、FIFO 是空的」，改。
// 「现在在一帧里的什么位置」是拿 t 和帧周期推的，帧周期差一点、隔的帧数越多推得越偏：只在 t 之后头 SWITCH_WINDOW_FRAMES 帧里找，
// 过了还没改成（任务被别的事拖住了）就不改了。帧周期最多偏 FRAME_PERIOD_TOL_PERCENT 再加上标称值本身的 1%，
// 到第 3 帧累计偏 1.2 ms，在 SWITCH_MARGIN_US 以内。
// （想过拿桥的欠载标志当「这会儿确实在有效行里」的旁证：清掉之后它并不会马上再立起来，用不上。）
#define SWITCH_MARGIN_US        1500    // 改 pixel_type 的时刻离场消隐至少这么远（帧结束中断晚到、帧周期量得不准都在这里面消化）
#define SWITCH_HOLD_TIMEOUT_MS  100     // 等帧结束中断把 DMA 停住，最多这么久（平时不到一个帧周期）
#define SWITCH_WINDOW_FRAMES    3       // 只在 DMA 停住之后头这么多帧里找有效行的中段
#define SWITCH_HOST_TIMEOUT_MS  60      // 等主机在帧头把新的色彩编码换上去，最多这么久（平时不到一个帧周期）
esp_err_t esp_lcd_dpi_panel_switch_color_format(esp_lcd_panel_handle_t panel, lcd_color_format_t in_color_format,
                                                lcd_color_format_t out_color_format, const esp_lcd_color_conv_config_t *conv)
{
    ESP_RETURN_ON_FALSE(panel, ESP_ERR_INVALID_ARG, TAG, "invalid argument");
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
    mipi_dsi_hal_context_t *hal = &dpi_panel->bus->hal;
    const bool in_yuv = COLOR_SPACE_TYPE(in_color_format) == LCD_COLOR_SPACE_YUV;
    ESP_RETURN_ON_FALSE(COLOR_SPACE_TYPE(out_color_format) == LCD_COLOR_SPACE_RGB, ESP_ERR_INVALID_ARG, TAG, "DSI can only send RGB");
    if (in_yuv) {
        ESP_RETURN_ON_FALSE(in_color_format == LCD_COLOR_FMT_YUV422, ESP_ERR_NOT_SUPPORTED, TAG, "only YUV422 is supported as YUV input");
        ESP_RETURN_ON_FALSE(conv, ESP_ERR_INVALID_ARG, TAG, "YUV input needs the conversion config");
    } else {
        ESP_RETURN_ON_FALSE(in_color_format == out_color_format, ESP_ERR_NOT_SUPPORTED, TAG, "no conversion between RGB formats");
    }
    color_space_pixel_format_t in_color_id = {
        .color_type_id = in_color_format,
    };
    ESP_RETURN_ON_FALSE(color_hal_pixel_format_get_bit_depth(in_color_id) == dpi_panel->bits_per_pixel, ESP_ERR_NOT_SUPPORTED, TAG,
                        "bits per pixel must stay the same");
    // 帧周期用帧结束中断量的（按 DPI 时钟算出来的不准，实测差 1%）。量到过一次之后就一直有值；用的时候等 DMA 停住了再取
    ESP_RETURN_ON_FALSE(dpi_panel->frame_us, ESP_ERR_INVALID_STATE, TAG, "frame period not measured yet");
    // 有效行要够长，两头各留 SWITCH_MARGIN_US 之后还有得剩。按量出来最短的帧周期算，实际的只会更宽裕
    const uint32_t frame_min_us = dpi_panel->nominal_frame_us - dpi_panel->nominal_frame_us * FRAME_PERIOD_TOL_PERCENT / 100;
    ESP_RETURN_ON_FALSE((uint64_t)frame_min_us * (dpi_panel->v_total_lines - dpi_panel->v_blank_lines) / dpi_panel->v_total_lines
                        > 2 * SWITCH_MARGIN_US, ESP_ERR_NOT_SUPPORTED, TAG, "active period too short");

    // 欠载填充色设成黑（之后不改回去：默认是蓝的，黑的更好）：DMA 停下之后，时序发生器在有效行里输出的就是它
    hal->bridge->dpi_rsv_dpi_data.dpi_rsv_data = 0;
    mipi_dsi_brg_ll_update_dpi_config(hal->bridge);

    // 1. 请帧结束中断把 DMA 停在帧尾
    portENTER_CRITICAL(&dpi_panel->hold_lock);
    dpi_panel->dma_held = false;
    dpi_panel->hold_dma = true;
    portEXIT_CRITICAL(&dpi_panel->hold_lock);
    bool held = false;
    for (int i = 0; i < SWITCH_HOLD_TIMEOUT_MS && !held; i++) {
        vTaskDelay(pdMS_TO_TICKS(1));
        portENTER_CRITICAL(&dpi_panel->hold_lock);
        held = dpi_panel->dma_held;
        portEXIT_CRITICAL(&dpi_panel->hold_lock);
    }
    if (!held) {
        // 帧结束中断一直没来（DMA 没在跑？）：撤掉请求。撤的那一刻它正好来了的话 DMA 已经停住，由下面重启
        portENTER_CRITICAL(&dpi_panel->hold_lock);
        dpi_panel->hold_dma = false;
        held = dpi_panel->dma_held;
        portEXIT_CRITICAL(&dpi_panel->hold_lock);
        if (held) {
            dpi_panel_restart_dma(dpi_panel);
        }
        ESP_RETURN_ON_FALSE(false, ESP_ERR_TIMEOUT, TAG, "no frame-end interrupt, color format not changed");
    }
    // DMA 停着，不会再有帧结束中断来改这两个数。场消隐按行数的比例折算
    const uint32_t t_end = dpi_panel->frame_end_us;
    const uint32_t frame_us = dpi_panel->frame_us;
    const uint32_t vblank_us = (uint32_t)((uint64_t)frame_us * dpi_panel->v_blank_lines / dpi_panel->v_total_lines);
    const uint32_t win_lo = vblank_us + SWITCH_MARGIN_US, win_hi = frame_us - SWITCH_MARGIN_US;
    const uint32_t give_up_us = SWITCH_WINDOW_FRAMES * frame_us;

    // 2. 等到有效行的中段，改桥的格式
    bool changed = false;
    while (!changed) {
        const uint32_t elapsed = (uint32_t)esp_timer_get_time() - t_end;
        if (elapsed >= give_up_us) {
            break;
        }
        const uint32_t phase = elapsed % frame_us;
        if (phase < win_lo || phase >= win_hi) {
            const uint32_t wait_us = (phase < win_lo ? win_lo - phase : frame_us - phase + win_lo) + 500;      // 等到窗口开头再过一点
            vTaskDelay(pdMS_TO_TICKS((wait_us + 999) / 1000));
        }
        // 关着中断核对、改：这几步之间不能被打断（被别的任务抢走几毫秒，时刻就不在窗口里了）
        portENTER_CRITICAL(&dpi_panel->hold_lock);
        const uint32_t el = (uint32_t)esp_timer_get_time() - t_end;
        const uint32_t ph = el % frame_us;
        if (el < give_up_us && ph >= win_lo && ph < win_hi && hal->bridge->fifo_flow_status.raw_buf_depth == 0) {
            mipi_dsi_brg_ll_set_pixel_format(hal->bridge, out_color_format, 0);
            mipi_dsi_brg_ll_set_input_color_space(hal->bridge, COLOR_SPACE_TYPE(in_color_format));
            if (in_yuv) {
                mipi_dsi_brg_ll_set_yuv_convert_std(hal->bridge, conv->spec.yuv.conv_std);
                mipi_dsi_brg_ll_set_yuv422_pack_order(hal->bridge, conv->spec.yuv.yuv422.in_pack_order);
            }
            mipi_dsi_brg_ll_update_dpi_config(hal->bridge);
            changed = true;
        }
        portEXIT_CRITICAL(&dpi_panel->hold_lock);
        if (!changed) {
            vTaskDelay(1);      // 时刻在窗口里但 FIFO 还没空（不该有）、或者刚才等过头了：让一让再看
        }
    }

    if (changed) {
        // 主机：经影子寄存器改，下一帧的帧头生效（生效了主机把 vid_shadow_req 清掉）
        hal->host->vid_shadow_ctrl.vid_shadow_en = 1;
        mipi_dsi_host_ll_dpi_set_color_coding(hal->host, out_color_format, 0);
        hal->host->vid_shadow_ctrl.vid_shadow_req = 1;
        dpi_panel->in_color_format = in_color_format;
        dpi_panel->out_color_format = out_color_format;
    }

    // 3. DMA 从当前帧缓冲的开头重新送；桥等到自己下一帧的帧头接着显示。
    // DMA 停着的这一段桥一直在记欠载，到它下一帧的帧头之前还会接着记：这是我们有意造成的，叫帧结束的中断先别报「取数跟不上」
    // （它见到第一个没有欠载的帧就恢复照常报）。在 DMA 停着的时候置位：这时不会有帧结束的中断来抢着清它
    dpi_panel->switching_format = true;
    dpi_panel_restart_dma(dpi_panel);
    ESP_RETURN_ON_FALSE(changed, ESP_ERR_TIMEOUT, TAG, "missed the active period or bridge not idle, color format not changed");

    // 等主机把新的色彩编码换上去，再把影子寄存器关掉，恢复成写了就生效
    bool host_done = false;
    for (int i = 0; i < SWITCH_HOST_TIMEOUT_MS && !host_done; i++) {
        if (hal->host->vid_shadow_ctrl.vid_shadow_req == 0) {
            host_done = true;
        } else {
            vTaskDelay(pdMS_TO_TICKS(1));
        }
    }
    hal->host->vid_shadow_ctrl.vid_shadow_en = 0;
    ESP_RETURN_ON_FALSE(host_done, ESP_ERR_INVALID_RESPONSE, TAG, "DSI host did not take the new color coding");
    return ESP_OK;
}

// 【本工程加的】说明见头文件
uint32_t esp_lcd_dpi_panel_get_frame_period_us(esp_lcd_panel_handle_t panel)
{
    if (!panel) {
        return 0;
    }
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
    const uint32_t measured = dpi_panel->frame_us;
    return measured ? measured : dpi_panel->nominal_frame_us;
}

esp_err_t esp_lcd_dpi_panel_set_pattern(esp_lcd_panel_handle_t panel, mipi_dsi_pattern_type_t pattern)
{
    ESP_RETURN_ON_FALSE(panel, ESP_ERR_INVALID_ARG, TAG, "invalid argument");
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
    esp_lcd_dsi_bus_handle_t bus = dpi_panel->bus;
    mipi_dsi_hal_context_t *hal = &bus->hal;

    if (pattern != MIPI_DSI_PATTERN_NONE) {
        // stop the DSI bridge from generating the DPI stream
        mipi_dsi_brg_ll_enable_dpi_output(hal->bridge, false);
        mipi_dsi_brg_ll_update_dpi_config(hal->bridge);
    }

    // set the pattern type and enable the pattern generator for the DSI host controller
    mipi_dsi_host_ll_dpi_set_pattern_type(hal->host, pattern);

    if (pattern == MIPI_DSI_PATTERN_NONE) {
        // re-enable the DSI bridge to generate the DPI stream
        mipi_dsi_brg_ll_enable_dpi_output(hal->bridge, true);
        mipi_dsi_brg_ll_update_dpi_config(hal->bridge);
    }

    return ESP_OK;
}

esp_err_t esp_lcd_dpi_panel_register_event_callbacks(esp_lcd_panel_handle_t panel, const esp_lcd_dpi_panel_event_callbacks_t *cbs, void *user_ctx)
{
    ESP_RETURN_ON_FALSE(panel && cbs, ESP_ERR_INVALID_ARG, TAG, "invalid argument");
    esp_lcd_dpi_panel_t *dpi_panel = __containerof(panel, esp_lcd_dpi_panel_t, base);
#if CONFIG_LCD_DSI_ISR_IRAM_SAFE
    if (cbs->on_color_trans_done) {
        ESP_RETURN_ON_FALSE(esp_ptr_in_iram(cbs->on_color_trans_done), ESP_ERR_INVALID_ARG, TAG, "on_color_trans_done callback not in IRAM");
    }
    if (cbs->on_refresh_done) {
        ESP_RETURN_ON_FALSE(esp_ptr_in_iram(cbs->on_refresh_done), ESP_ERR_INVALID_ARG, TAG, "on_refresh_done callback not in IRAM");
    }
    if (user_ctx) {
        ESP_RETURN_ON_FALSE(esp_ptr_internal(user_ctx), ESP_ERR_INVALID_ARG, TAG, "user context not in internal RAM");
    }
#endif // CONFIG_LCD_RGB_ISR_IRAM_SAFE
    dpi_panel->on_color_trans_done = cbs->on_color_trans_done;
    dpi_panel->on_refresh_done = cbs->on_refresh_done;
    dpi_panel->user_ctx = user_ctx;

    return ESP_OK;
}
