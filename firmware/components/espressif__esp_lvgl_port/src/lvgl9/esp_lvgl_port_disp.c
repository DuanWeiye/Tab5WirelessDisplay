/*
 * SPDX-FileCopyrightText: 2024-2025 Espressif Systems (Shanghai) CO LTD
 *
 * SPDX-License-Identifier: Apache-2.0
 */

#include <string.h>
#include "sdkconfig.h"
#include "esp_log.h"
#include "esp_timer.h"   // 自研淡入淡出过渡计时
#include "esp_cache.h"   // esp_cache_msync(直写帧缓冲后回写)
#include "esp_err.h"
#include "esp_check.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "freertos/idf_additions.h"   // 实验 worker 栈放 PSRAM
#include "esp_heap_caps.h"
#include "esp_idf_version.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lvgl_port.h"
#include "esp_lvgl_port_priv.h"
#include "driver/ppa.h"
#include "esp_heap_caps.h"
#include "esp_async_memcpy.h"   // 基准9：AXI-GDMA 线性拷贝
#include "esp_async_fbcpy.h"    // 滚动位移复用：DMA2D 2D 窗口拷贝（esp_lcd 私有头,CMake 已加路径）
#include "hal/color_types.h"
#include "esp_private/esp_cache_private.h"

#define ALIGN_UP_BY(num, align) (((num) + ((align)-1)) & ~((align)-1))
static const char* TAG = "LVGL";   // 提前到文件头：合成器/基准代码也要打日志
#define BLOCK_SIZE_SMALL        (32)
#define BLOCK_SIZE_LARGE        (256)
static ppa_client_handle_t ppa_srm_handle = NULL;

// ── 调试探针：最近一次 PPA 旋转的提交时刻/耗时/面积（多笔在途时为近似值，量级足够） ──
static volatile int64_t  s_dbg_rot_submit_us = 0;
static volatile uint32_t s_dbg_rot_us = 0;
static volatile uint32_t s_dbg_rot_px = 0;

void lvgl_port_dbg_rot_get(uint32_t* us, uint32_t* px)
{
    if (us) *us = s_dbg_rot_us;
    if (px) *px = s_dbg_rot_px;
}

// flush 路径探针：定位"渲染时间去哪了"（flush 次数、flush 内耗时、PPA 直写失败落 SW 回退次数）
static volatile uint32_t s_dbg_flush_calls = 0;
static volatile uint32_t s_dbg_flush_us    = 0;
static volatile uint32_t s_dbg_flush_fb    = 0;

void lvgl_port_dbg_flush_get(uint32_t* calls, uint32_t* us, uint32_t* fallbacks)
{
    if (calls)     *calls     = s_dbg_flush_calls;
    if (us)        *us        = s_dbg_flush_us;
    if (fallbacks) *fallbacks = s_dbg_flush_fb;
    s_dbg_flush_calls = s_dbg_flush_us = s_dbg_flush_fb = 0;
}

// ── 实验性调试设施（'disp' 命令族，2026-11-11 刷新性能探索）──────────────────
// 只在调试命令显式开启时生效；默认值下生产路径行为不变。
static volatile uint32_t s_dbg_vsync_cnt = 0;   // 帧边界 ISR 计数（量面板真实刷新率）
// flush 输出管线：0=PPA 直写(生产) 1=丢弃输出(只量渲染) 2=CPU 旋转单核 3=CPU 旋转双核
static int  s_dbg_pipe     = 0;
static bool s_dbg_need_m2c = false;             // 位移帧 DMA2D 改写后台 FB 后，CPU 管线首笔前要丢弃 cache 旧行
static int  s_dbg_row_min  = 0x7fffffff;        // CPU 管线：本刷新周期触及的 FB 行范围（末笔统一回写）
static int  s_dbg_row_max  = -1;
// 位移帧分段耗时探针（累计值，读取即清零）
typedef struct {
    uint32_t n;            // 位移帧数
    uint32_t fence_us;     // arm 里提交拷贝之前等的时间：翻页围栏（帧边界）+ 上一帧在途的 PPA 旋转
    uint32_t copy_us;      // DMA2D 错位拷贝本身（提交→完成中断）
    uint32_t cpwait_us;    // 首个 flush 等拷贝完成的阻塞时间
    uint32_t ppawait_us;   // finish 里等在途 PPA 清空
    uint32_t total_us;     // arm 入口 → finish 出口
} dbg_shift_t;
static dbg_shift_t      s_dbg_shift;
static int64_t          s_dbg_arm_t0    = 0;
static int64_t          s_dbg_copy_t0   = 0;
static volatile int64_t s_dbg_copy_done = 0;

// ── 滚动位移复用合成器（双帧缓冲）────────────────────────────────────────────
// 背景：滚动时"整屏重绘+整屏旋转"打满 PSRAM 带宽(~15fps)。滚动帧改为：
// GDMA 把前台 FB 错位平移到后台 FB(整幅线性拷贝,行边界垃圾落在待重绘条带内,~15ms)，
// LVGL 只渲染"新露条带+顶/底栏带+滚动条带"(由 ui 层收窄失效区)，PPA 旋转直写后台 FB，
// REFR_READY 等 PPA 清空后把扫出翻到后台 FB(帧边界生效,无撕裂)。
static uint16_t*    s_fbs[2]      = {NULL, NULL};
static int          s_fb_cur      = 0;       // 当前扫出的 FB 下标
static bool         s_have2fb     = false;
static size_t       data_cache_line_size = 0; // PSRAM 的缓存行大小（lvgl_port_add_disp_dsi 里取）
// 第三块帧缓冲（面板有的话）：只有直接送屏用。LVGL 和滚动合成器只认 s_fbs 那两块，直接送屏结束时扫出的一定换回那两块之一
// （lvgl_port_direct_end）。s_direct_scan = 直接送屏期间最近一次登记换上屏幕的那一块（NULL = 不在直接送屏）；截图工具 fbshot 也读它
static uint16_t*    s_fb3         = NULL;
static uint16_t*    s_direct_scan = NULL;
static bool         s_shift_cycle = false;   // 本刷新周期为"位移复用"帧：flush 写后台 FB
static bool         s_shift_wait  = false;   // GDMA 平移尚未确认完成（首个 flush 前等待）
static esp_lcd_panel_handle_t s_panel = NULL;
static volatile int s_ppa_pending  = 0;      // 非阻塞 PPA 旋转在途数（翻页前必须清零）
static esp_async_fbcpy_handle_t s_shift_cpy = NULL;   // DMA2D 2D 窗口拷贝（描述符 O(1)，无 GDMA 的描述符爆炸）
static SemaphoreHandle_t        s_shift_sem = NULL;
// 翻页围栏：draw_bitmap 只是"登记"翻页，真正切扫出在下一个帧 DMA 中断里重挂描述符时生效。
// 在此之前旧前台 FB 仍在扫出，往它(=新后台)写 DMA2D/PPA 会当场撕裂。
// finish 登记翻页时记下要换上去的那一块（s_flip_target）；帧边界 ISR(on_refresh_done)见到挂上去的正是它就发信号；
// 下一个位移帧写后台 FB 前先等（flip_fence_wait；s_flip_wait 标记"有围栏要等"）。
// 信号只管把等的一方叫醒，放不放行看状态：ISR 每帧把「接下来这一帧扫出的是哪一块」记进 s_scan_fb，
// 等的一方醒来后核对它是不是自己登记的那一块——所以陈旧的信号（见 flip_fence_wait）不会让人早放行。
static SemaphoreHandle_t        s_flip_sem     = NULL;
// 登记了、还没生效的那次翻页要换上去的帧缓冲（NULL = 没有）。登记的任务写，帧边界 ISR 见到「这一次挂上去的就是它」时清掉、发信号。
// 一个字，读写不会撕开。必须核对挂上去的是哪一块：ISR 在开头就定了这一次挂哪一块、到末尾才回调到这里，
// 登记正好落在这中间（任务和 ISR 在两个核上）的话，这一次挂的还是旧的那一块，只凭「登记之后来过中断」会早一帧放行——
// 等的一方就去写其实还要再扫一帧的那一块（撕一帧）。
// ISR 清它用比较并交换（还是自己读到的那一块才清）：任务可能正好在这时登记下一次翻页，不能把新登记的冲掉
static void* volatile           s_flip_target  = NULL;
// 帧边界 ISR 最近一次挂上去的那块帧缓冲 = 现在正在扫出的那一块（ISR 写，等围栏的任务读；一个字。
// 切色彩格式停住 DMA 的那一次、之后由任务重启的那一次都不更新，要到再下一个帧边界才跟上，只会让围栏晚一帧放行）
static void* volatile           s_scan_fb      = NULL;
// 探针：登记了翻页、帧边界 ISR 这一次挂上去的却不是它（围栏没放行）的次数，开机以来。含义见头文件 lvgl_port_dbg_flip_collisions
static volatile uint32_t        s_dbg_flip_early = 0;
static volatile uint32_t        s_flip_done_us = 0;       // 直接送屏：登记的翻页生效的时刻（esp_timer 微秒数的低 32 位，一个字、读写不会撕开；帧边界 ISR 写）
static bool                     s_flip_seen    = false;   // 直接送屏：这次登记的翻页，等的一方已经等到帧边界了（只有调用方的任务读写）
static bool                     s_flip_wait    = false;   // LVGL 任务消费

// 任务一侧写登记（记下要换上哪一块，或者 NULL = 作废）。ISR 那边是比较并交换，这边也用原子写，一个变量不混着两种写法；
// 带全序：登记是「先写它、再 draw_bitmap 改 cur_fb_index」，ISR 是「先读 cur_fb_index、后读它」，这个先后要让另一个核也看得到
static inline void flip_target_set(void* fb)
{
    __atomic_store_n(&s_flip_target, fb, __ATOMIC_SEQ_CST);
}

// 等登记的那次翻页（换上 fb）生效，最多等 ms 毫秒；生效了返回 true。只在登记成功之后调。
// 拿到信号还要核对 s_scan_fb：信号可能是陈旧的——上一次翻页没等到就不等了（超时），它的 ISR 后来才发信号，
// 而且正好落在这一次登记「清信号」之后（ISR 在另一个核上，读登记、清登记、发信号不是一步）。
// 陈旧信号那一刻扫出的是上一次登记的那一块，不会是这一次的（两次登记的不是同一块），所以核对不上、接着等剩下的时间。
// 反过来不能只看 s_scan_fb 不等信号：上一次登记没生效就作废了（超时）的话，s_scan_fb 还停在再上一次的那一块，
// 而那一块可能正是这一次登记的（滚动是两块来回换），正在进行的中断挂上去的却是作废的那一块——只看状态会当场放行。
// 信号只由「挂上去的正是登记的那一块」的中断发，不会被这种旧状态骗到。
// s_scan_fb 用带次序的原子读写（ISR 先写生效时刻、再写它）：核对上了，另一个核上读到的生效时刻一定是这一次的
static bool flip_fence_wait(void* fb, uint32_t ms)
{
    if (!s_flip_sem) return true;                    // 没有围栏信号（创建失败）：退化为不等
    TickType_t left = pdMS_TO_TICKS(ms);
    TimeOut_t  to;
    vTaskSetTimeOutState(&to);
    for (;;) {
        if (xSemaphoreTake(s_flip_sem, left) != pdTRUE) return false;
        if (__atomic_load_n(&s_scan_fb, __ATOMIC_ACQUIRE) == fb) return true;
        if (xTaskCheckForTimeOut(&to, &left) == pdTRUE) return false;   // 没超时的话 left 改成剩下的时间
    }
}

// 滚动合成器：上一个位移帧登记的翻页还没等过的话，等它生效（最多 40 ms）。等不到也往下走（那一帧可能撕），登记作废
static void scroll_flip_fence_wait(void)
{
    if (!s_flip_wait) return;
    flip_fence_wait(s_fbs[s_fb_cur], 40);            // 登记成功后 s_fb_cur 指的就是登记的那一块
    flip_target_set(NULL);                           // 没等到的：别让之后的帧边界再发一个没人要的信号
    s_flip_wait   = false;
}

// 当前"应写入"的帧缓冲（普通帧=前台直写；位移帧=后台合成）
static inline uint16_t* target_fb(void)
{
    return s_fbs[s_fb_cur ^ (s_shift_cycle ? 1 : 0)];
}

static bool shift_cpy_done_cb(esp_async_fbcpy_handle_t h, esp_async_fbcpy_event_data_t* ev, void* cb_args)
{
    BaseType_t hp = pdFALSE;
    s_dbg_copy_done = esp_timer_get_time();   // 探针：拷贝完成时刻
    xSemaphoreGiveFromISR((SemaphoreHandle_t)cb_args, &hp);
    return hp == pdTRUE;
}

// REFR_START(渲染前)由 ui 层调用：受理本周期的位移复用并立即启动 GDMA 平移(与渲染并行)。
// dy_landscape>0=内容上移(底部露新)。返回 false=未受理(调用方走整屏重绘)。
bool lvgl_port_scroll_shift_arm(lv_display_t* disp, int dy_landscape)
{
    if (!s_have2fb || s_shift_cycle || dy_landscape == 0) return false;
    int hres = lv_display_get_horizontal_resolution(disp);   // 1280
    int vres = lv_display_get_vertical_resolution(disp);     // 720
    int ady  = dy_landscape > 0 ? dy_landscape : -dy_landscape;
    if (ady >= vres) return false;
    if (!s_shift_cpy) {
        esp_async_fbcpy_config_t fc = {};
        if (esp_async_fbcpy_install(&fc, &s_shift_cpy) != ESP_OK) { s_shift_cpy = NULL; return false; }
        s_shift_sem = xSemaphoreCreateBinary();
        if (!s_shift_sem) return false;
    }
    // 竖屏 FB(宽 vres=720,高 hres=1280)里 col=y_landscape（见 lvgl_port_rotate_area 的 90° 映射：
    // rot.x1=area.y1，即横屏第 y 行旋转后落在 FB 第 y 列）：
    // 内容沿横屏 y 上移 dy ⇔ 竖屏 FB 内容向低列方向错位 dy 列(DMA2D 一笔 2D 拷贝)。
    // dy>0(内容上移、底部露新)：dst 从列 0 起 ← src 从列 dy 起，未覆盖的高列端 dy 列
    // = 横屏底部新露条带；dy<0 反之(未覆盖的低列端 = 横屏顶部新露条带)。由 ui 层安排重绘。
    esp_async_fbcpy_trans_desc_t tr = {
        .src_buffer        = s_fbs[s_fb_cur],
        .dst_buffer        = s_fbs[s_fb_cur ^ 1],
        .src_buffer_size_x = (size_t)vres,
        .src_buffer_size_y = (size_t)hres,
        .dst_buffer_size_x = (size_t)vres,
        .dst_buffer_size_y = (size_t)hres,
        .src_offset_x      = dy_landscape > 0 ? (size_t)ady : 0,
        .src_offset_y      = 0,
        .dst_offset_x      = dy_landscape > 0 ? 0 : (size_t)ady,
        .dst_offset_y      = 0,
        .copy_size_x       = (size_t)(vres - ady),
        .copy_size_y       = (size_t)hres,
        .pixel_format_unique_id = { .color_type_id = COLOR_TYPE_ID(COLOR_SPACE_RGB, COLOR_PIXEL_RGB565) },
    };
    // 翻页围栏：上次 finish 登记的翻页可能还没过帧边界。draw_bitmap 之后最长等一帧（约 15.3 ms）才切扫出——
    // 登记正好撞在帧边界中断中间的那一次也一样，只是从「这一个帧边界」推到「下一个」。40 ms 的超时有 25 ms 上下的余量，
    // 中断被压住很久、或者扫出停着（切色彩格式）才会超时。
    // 此时"后台 FB"= 仍在扫出的旧前台，必须等到翻页生效才能往里写，否则撕裂。
    // 若边界早已过去（ISR 已发信号），立即返回，不白等。
    s_dbg_arm_t0 = esp_timer_get_time();      // 探针：位移帧起点
    scroll_flip_fence_wait();
    // 上一帧是普通帧的话，它最后几笔 PPA 旋转可能还在往前台写（LVGL 一帧结束时不等最后一笔 flush）：
    // 等它们写完再拷，不然拷进后台的是没写完的前台，那几块就停在旧内容上。位移帧自己在 finish 里已经等过，这里通常不用等
    int64_t tp = esp_timer_get_time();
    while (s_ppa_pending > 0 && esp_timer_get_time() - tp < 100000) vTaskDelay(1);
    s_dbg_copy_t0 = esp_timer_get_time();     // 探针：围栏、在途旋转都等完，拷贝提交
    s_shift_cycle = true;
    s_shift_wait  = true;
    esp_err_t e = esp_async_fbcpy(s_shift_cpy, &tr, shift_cpy_done_cb, s_shift_sem);
    if (e != ESP_OK) {
        static int64_t s_last_err_us = 0;
        int64_t now = esp_timer_get_time();
        if (now - s_last_err_us > 500000) {   // 限频报错
            s_last_err_us = now;
            ESP_LOGW(TAG, "shift arm: fbcpy %s", esp_err_to_name(e));
        }
        s_shift_cycle = s_shift_wait = false;
        return false;
    }
    // 探针：围栏耗时只在提交成功后累加（失败帧不进 n/total，否则 fence 会大于 total）
    s_dbg_shift.fence_us += (uint32_t)(s_dbg_copy_t0 - s_dbg_arm_t0);
    return true;
}

// REFR_READY 由 ui 层调用：等 PPA 在途旋转清空 → 扫出翻到后台 FB（帧边界生效）。
void lvgl_port_scroll_shift_finish(lv_display_t* disp)
{
    (void)disp;
    if (!s_shift_cycle) return;
    // 兜底：本周期一个 flush 都没发生（不应出现）也要把 GDMA 等掉，避免信号量残留
    if (s_shift_wait) { xSemaphoreTake(s_shift_sem, pdMS_TO_TICKS(100)); s_shift_wait = false; }
    int64_t t0 = esp_timer_get_time();
    while (s_ppa_pending > 0 && esp_timer_get_time() - t0 < 100000) vTaskDelay(1);
    s_dbg_shift.ppawait_us += (uint32_t)(esp_timer_get_time() - t0);
    int back = s_fb_cur ^ 1;
    // draw_bitmap 传"FB 内部指针"= 仅 msync + 切 cur_fb_index（零拷贝翻页）；范围给 1 行即可。
    // 注意翻页在下一个帧边界才真正生效——登记围栏，下次写(新)后台 FB 前等边界信号。
    // 先说要换上哪一块、再登记：ISR 挂上的正是这一块才发信（见 s_flip_target），所以不怕它在登记之前先来一次；
    // 反过来先登记后说的话，ISR 要是落在两步中间，翻页生效了这边却没收到信号，白等一帧
    if (s_flip_sem) {
        xSemaphoreTake(s_flip_sem, 0);       // 清掉无人消费的陈旧信号（上次滚动早已过边界）
        flip_target_set(s_fbs[back]);
    }
    if (s_panel && esp_lcd_panel_draw_bitmap(s_panel, 0, 0, 1, 1, s_fbs[back]) == ESP_OK) {
        s_fb_cur = back;
        if (s_flip_sem) s_flip_wait = true;
    } else {
        flip_target_set(NULL);
    }
    s_shift_cycle = false;
    // 探针汇总（拷贝完成时刻由 ISR 记录；本帧拷贝必已完成）
    if (s_dbg_copy_done > s_dbg_copy_t0) s_dbg_shift.copy_us += (uint32_t)(s_dbg_copy_done - s_dbg_copy_t0);
    s_dbg_shift.total_us += (uint32_t)(esp_timer_get_time() - s_dbg_arm_t0);
    s_dbg_shift.n++;
}

// ── 直接送屏（无线显示用，说明见头文件）──────────────────────────────────
uint32_t lvgl_port_dbg_flip_collisions(void) { return s_dbg_flip_early; }

bool lvgl_port_direct_begin(uint16_t* fbs[3])
{
    if (!s_have2fb || !s_fb3 || s_shift_cycle) return false;
    // 滚动合成器刚登记过翻页的话，等它过了帧边界再把后台缓冲交出去（否则那块还在被扫出）
    scroll_flip_fence_wait();
    // LVGL 最后一帧在途的 PPA 旋转也等完
    int64_t t0 = esp_timer_get_time();
    while (s_ppa_pending > 0 && esp_timer_get_time() - t0 < 100000) vTaskDelay(1);
    fbs[0] = s_fbs[s_fb_cur];
    fbs[1] = s_fbs[s_fb_cur ^ 1];
    fbs[2] = s_fb3;
    s_direct_scan = fbs[0];
    return true;
}

bool lvgl_port_direct_flip_begin(uint16_t* fb)
{
    if (!s_direct_scan || !s_panel || fb == s_direct_scan) return false;
    if (fb != s_fbs[0] && fb != s_fbs[1] && fb != s_fb3) return false;
    if (s_flip_sem) xSemaphoreTake(s_flip_sem, 0);   // 清掉无人消费的陈旧信号
    // 先说要换上哪一块、再登记（原因见 lvgl_port_scroll_shift_finish）
    flip_target_set(fb);
    // draw_bitmap 传"帧缓冲内部指针" = 只登记翻页（见 lvgl_port_scroll_shift_finish）
    if (esp_lcd_panel_draw_bitmap(s_panel, 0, 0, 1, 1, fb) != ESP_OK) {
        flip_target_set(NULL);
        return false;
    }
    s_flip_seen   = false;
    s_direct_scan = fb;
    if (fb != s_fb3) s_fb_cur = fb == s_fbs[0] ? 0 : 1;     // LVGL 的两块里哪块在扫出，一直记着（换到第三块上时不动，见 lvgl_port_direct_end）
    return true;
}

bool lvgl_port_direct_flip_wait(uint32_t wait_ms)
{
    if (s_flip_seen) return true;
    if (!flip_fence_wait(s_direct_scan, wait_ms)) return false;     // s_direct_scan = 最近一次登记的那一块
    s_flip_seen = true;
    return true;
}

int64_t lvgl_port_direct_flip_end(void)
{
    flip_target_set(NULL);                           // 等超时了不再等的：别让之后的帧边界再发一个没人要的信号
    if (!s_flip_seen) return 0;
    // ISR 只记了低 32 位：帧边界是刚过去的事（远不到 71 分钟），拿现在的时刻减去隔了多久就是完整的时刻
    const int64_t now = esp_timer_get_time();
    return now - (int64_t)(uint32_t)((uint32_t)now - s_flip_done_us);
}

bool lvgl_port_direct_end(void)
{
    if (!s_direct_scan) return true;
    bool ok = true;
    if (s_direct_scan == s_fb3) {
        // 扫出的是第三块，LVGL 不认它：把画面原样拷到 LVGL 的一块上（两块这时都没在扫出），换过去，等它生效。
        // 不拷直接换的话，那一块里是两三帧之前的画面（桥刚从 YUV422 切回来的话还是按另一种格式写的），会闪一下。
        // 第三块是解码器经 DMA 写的、不过缓存：读之前先把缓存里那一段作废；写完的那一块回写到内存（面板也是经 DMA 读）
        lv_display_t* d        = lv_display_get_default();
        const size_t  line     = data_cache_line_size ? data_cache_line_size : 64;
        const size_t  fb_bytes = ALIGN_UP_BY((size_t)lv_display_get_horizontal_resolution(d) *
                                             lv_display_get_vertical_resolution(d) * 2, line);
        uint16_t*     dst      = s_fbs[s_fb_cur ^ 1];
        esp_cache_msync(s_fb3, fb_bytes, ESP_CACHE_MSYNC_FLAG_DIR_M2C);
        memcpy(dst, s_fb3, fb_bytes);
        esp_cache_msync(dst, fb_bytes, ESP_CACHE_MSYNC_FLAG_DIR_C2M);
        // 登记不上（面板驱动拒了，平时不会）再试一次：还不行的话屏幕就停在第三块上，LVGL 画的东西要到滚动合成器下一次翻页才看得见
        ok = lvgl_port_direct_flip_begin(dst) || lvgl_port_direct_flip_begin(dst);
        if (!ok) {
            ESP_LOGE(TAG, "direct_end: 翻页登记不上，屏幕停在第三块帧缓冲上");
        } else {
            // 等超时只是这边不等了：翻页已经登记，下一个帧边界照样生效
            if (!lvgl_port_direct_flip_wait(100)) ESP_LOGW(TAG, "direct_end: 换回 LVGL 的帧缓冲，等帧边界超时");
            lvgl_port_direct_flip_end();
        }
    }
    s_direct_scan = NULL;
    return ok;
}

void lvgl_port_dbg_shift_get(uint32_t out[6])
{
    out[0] = s_dbg_shift.n;         out[1] = s_dbg_shift.fence_us;   out[2] = s_dbg_shift.copy_us;
    out[3] = s_dbg_shift.cpwait_us; out[4] = s_dbg_shift.ppawait_us; out[5] = s_dbg_shift.total_us;
    memset(&s_dbg_shift, 0, sizeof(s_dbg_shift));
}

// 【本地优化·刀2】PPA 非阻塞事务完成回调（ISR 上下文）：user_data=发起 flush 的 lv_display_t。
// 仅"直写帧缓冲"的 flush 事务带 user_data；阻塞事务（fade 快照等）传 NULL 不触发。
static bool lvgl_port_ppa_trans_done_cb(ppa_client_handle_t ppa_client, ppa_event_data_t* event_data, void* user_data)
{
    if (user_data) {
        __atomic_fetch_sub(&s_ppa_pending, 1, __ATOMIC_SEQ_CST);
        s_dbg_rot_us = (uint32_t)(esp_timer_get_time() - s_dbg_rot_submit_us);
        lv_disp_flush_ready((lv_display_t*)user_data);
    }
    return false;
}

#if CONFIG_IDF_TARGET_ESP32S3 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 0, 0)
#include "esp_lcd_panel_rgb.h"
#endif

#if (CONFIG_IDF_TARGET_ESP32P4 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 3, 0))
#include "esp_lcd_mipi_dsi.h"
#endif

#if (ESP_IDF_VERSION < ESP_IDF_VERSION_VAL(4, 4, 4)) || (ESP_IDF_VERSION == ESP_IDF_VERSION_VAL(5, 0, 0))
#define LVGL_PORT_HANDLE_FLUSH_READY 0
#else
#define LVGL_PORT_HANDLE_FLUSH_READY 1
#endif

#ifndef CONFIG_LV_DRAW_BUF_ALIGN
#define CONFIG_LV_DRAW_BUF_ALIGN 1
#endif


/*******************************************************************************
 * Types definitions
 *******************************************************************************/

typedef struct {
    lvgl_port_disp_type_t disp_type;       /* Display type */
    esp_lcd_panel_io_handle_t io_handle;   /* LCD panel IO handle */
    esp_lcd_panel_handle_t panel_handle;   /* LCD panel handle */
    esp_lcd_panel_handle_t control_handle; /* LCD panel control handle */
    lvgl_port_rotation_cfg_t rotation;     /* Default values of the screen rotation */
    lv_color_t* draw_buffs[3];             /* Display draw buffers */
    uint8_t* oled_buffer;
    lv_display_t* disp_drv; /* LVGL display driver */
    lv_display_rotation_t current_rotation;
    SemaphoreHandle_t trans_sem; /* Idle transfer mutex */
    struct {
        unsigned int monochrome : 1;   /* True, if display is monochrome and using 1bit for 1px */
        unsigned int swap_bytes : 1;   /* Swap bytes in RGB656 (16-bit) before send to LCD driver */
        unsigned int full_refresh : 1; /* Always make the whole screen redrawn */
        unsigned int direct_mode : 1;  /* Use screen-sized buffers and draw to absolute coordinates */
        unsigned int sw_rotate : 1;    /* Use software rotation (slower) or PPA if available */
    } flags;
} lvgl_port_display_ctx_t;

/*******************************************************************************
 * Function definitions
 *******************************************************************************/
static lv_display_t* lvgl_port_add_disp_priv(const lvgl_port_display_cfg_t* disp_cfg,
                                             const lvgl_port_disp_priv_cfg_t* priv_cfg);
#if LVGL_PORT_HANDLE_FLUSH_READY
static bool lvgl_port_flush_io_ready_callback(esp_lcd_panel_io_handle_t panel_io, esp_lcd_panel_io_event_data_t* edata,
                                              void* user_ctx);
#if CONFIG_IDF_TARGET_ESP32S3 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 0, 0)
static bool lvgl_port_flush_rgb_vsync_ready_callback(esp_lcd_panel_handle_t panel_io,
                                                     const esp_lcd_rgb_panel_event_data_t* edata, void* user_ctx);
#endif
#if (CONFIG_IDF_TARGET_ESP32P4 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 3, 0))
static bool lvgl_port_flush_dpi_panel_ready_callback(esp_lcd_panel_handle_t panel_io,
                                                     esp_lcd_dpi_panel_event_data_t* edata, void* user_ctx);
static bool lvgl_port_flush_dpi_vsync_ready_callback(esp_lcd_panel_handle_t panel_io,
                                                     esp_lcd_dpi_panel_event_data_t* edata, void* user_ctx);
static bool lvgl_port_flip_fence_refresh_cb(esp_lcd_panel_handle_t panel,
                                            esp_lcd_dpi_panel_event_data_t* edata, void* user_ctx);
#endif
#endif
static void lvgl_port_flush_callback(lv_display_t* drv, const lv_area_t* area, uint8_t* color_map);
static void dbg_flush_alt(lv_display_t* drv, const lv_area_t* area, const uint8_t* color_map, uint16_t* fb);
static void lvgl_port_disp_size_update_callback(lv_event_t* e);
static void lvgl_port_disp_rotation_update(lvgl_port_display_ctx_t* disp_ctx);
static void lvgl_port_display_invalidate_callback(lv_event_t* e);

/*******************************************************************************
 * Public API functions
 *******************************************************************************/

lv_display_t* lvgl_port_add_disp(const lvgl_port_display_cfg_t* disp_cfg)
{
    lvgl_port_lock(0);
    lv_disp_t* disp = lvgl_port_add_disp_priv(disp_cfg, NULL);

    if (disp != NULL) {
        lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp);
        /* Set display type */
        disp_ctx->disp_type = LVGL_PORT_DISP_TYPE_OTHER;

        assert(disp_cfg->io_handle != NULL);

#if LVGL_PORT_HANDLE_FLUSH_READY
        const esp_lcd_panel_io_callbacks_t cbs = {
            .on_color_trans_done = lvgl_port_flush_io_ready_callback,
        };
        /* Register done callback */
        esp_lcd_panel_io_register_event_callbacks(disp_ctx->io_handle, &cbs, disp);
#endif

        /* Apply rotation from initial display configuration */
        lvgl_port_disp_rotation_update(disp_ctx);
    }
    lvgl_port_unlock();

    return disp;
}

lv_display_t* lvgl_port_add_disp_dsi(const lvgl_port_display_cfg_t* disp_cfg,
                                     const lvgl_port_display_dsi_cfg_t* dsi_cfg)
{
    // Initialize the PPA
    // 【本地优化·刀2】允许多笔非阻塞事务在途（LVGL 双 draw_buf 最多 2 笔 + 余量）；
    // 完成回调里发 lv_disp_flush_ready → 渲染与旋转流水线化。
    ppa_client_config_t ppa_srm_config = {
        .oper_type = PPA_OPERATION_SRM,
        .max_pending_trans_num = 4,
    };
    ESP_ERROR_CHECK(ppa_register_client(&ppa_srm_config, &ppa_srm_handle));
    ppa_event_callbacks_t ppa_cbs = {
        .on_trans_done = lvgl_port_ppa_trans_done_cb,
    };
    ESP_ERROR_CHECK(ppa_client_register_event_callbacks(ppa_srm_handle, &ppa_cbs));
    ESP_ERROR_CHECK(esp_cache_get_alignment(MALLOC_CAP_SPIRAM, &data_cache_line_size));

    assert(dsi_cfg != NULL);
    const lvgl_port_disp_priv_cfg_t priv_cfg = {
        .avoid_tearing = dsi_cfg->flags.avoid_tearing,
    };
    lvgl_port_lock(0);
    lv_disp_t* disp = lvgl_port_add_disp_priv(disp_cfg, &priv_cfg);

    if (disp != NULL) {
        lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp);
        /* Set display type */
        disp_ctx->disp_type = LVGL_PORT_DISP_TYPE_DSI;

#if (CONFIG_IDF_TARGET_ESP32P4 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 3, 0))
        esp_lcd_dpi_panel_event_callbacks_t cbs = {0};
        if (dsi_cfg->flags.avoid_tearing) {
            cbs.on_refresh_done = lvgl_port_flush_dpi_vsync_ready_callback;
        } else {
            cbs.on_color_trans_done = lvgl_port_flush_dpi_panel_ready_callback;
            cbs.on_refresh_done     = lvgl_port_flip_fence_refresh_cb;   // 滚动位移复用的翻页围栏
        }
        /* Register done callback */
        esp_lcd_dpi_panel_register_event_callbacks(disp_ctx->panel_handle, &cbs, disp);

        /* 滚动位移复用：登记双帧缓冲。面板有两块以上帧缓冲（BSP 的 num_fbs）时启用；只有 1 块则整套特性关闭。
         * 有第三块的话记下来，只给直接送屏用。面板驱动没有「有几块」的接口，只能先按 3 块取、不行再按 2 块取：
         * 不够数的那一次驱动会打一条 E 级的 "invalid frame buffer number"（两块的面板开机一条，一块的两条），是预期的 */
        {
            void *f0 = NULL, *f1 = NULL;
            s_panel = disp_ctx->panel_handle;
            void *f2 = NULL;
            if (esp_lcd_dpi_panel_get_frame_buffer(disp_ctx->panel_handle, 3, &f0, &f1, &f2) != ESP_OK) f2 = NULL;
            if ((f2 || esp_lcd_dpi_panel_get_frame_buffer(disp_ctx->panel_handle, 2, &f0, &f1) == ESP_OK) && f0 && f1) {
                s_fbs[0] = (uint16_t*)f0;
                s_fbs[1] = (uint16_t*)f1;
                s_fb3    = (uint16_t*)f2;
                s_have2fb = true;
                s_flip_sem = xSemaphoreCreateBinary();   // 翻页围栏信号（创建失败则围栏退化为不等待）
                ESP_LOGI(TAG, "scroll-shift compositor: dual FB %p/%p; direct-present third FB %p", f0, f1, f2);
            } else if (f0) {
                s_fbs[0] = s_fbs[1] = (uint16_t*)f0;
            }
        }

        /* Apply rotation from initial display configuration */
        lvgl_port_disp_rotation_update(disp_ctx);
#else
        ESP_RETURN_ON_FALSE(false, NULL, TAG, "MIPI-DSI is supported only on ESP32P4 and from IDF 5.3!");
#endif
    }
    lvgl_port_unlock();

    return disp;
}

lv_display_t* lvgl_port_add_disp_rgb(const lvgl_port_display_cfg_t* disp_cfg,
                                     const lvgl_port_display_rgb_cfg_t* rgb_cfg)
{
    lvgl_port_lock(0);
    assert(rgb_cfg != NULL);
    const lvgl_port_disp_priv_cfg_t priv_cfg = {
        .avoid_tearing = rgb_cfg->flags.avoid_tearing,
    };
    lv_disp_t* disp = lvgl_port_add_disp_priv(disp_cfg, &priv_cfg);

    if (disp != NULL) {
        lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp);
        /* Set display type */
        disp_ctx->disp_type = LVGL_PORT_DISP_TYPE_RGB;

#if (CONFIG_IDF_TARGET_ESP32S3 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 0, 0))
        /* Register done callback */
        const esp_lcd_rgb_panel_event_callbacks_t vsync_cbs = {
            .on_vsync = lvgl_port_flush_rgb_vsync_ready_callback,
        };

        const esp_lcd_rgb_panel_event_callbacks_t bb_cbs = {
#if ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 1, 2)
            .on_bounce_frame_finish = lvgl_port_flush_rgb_vsync_ready_callback,
#endif
        };

        if (rgb_cfg->flags.bb_mode && (ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 1, 2))) {
            ESP_ERROR_CHECK(
                esp_lcd_rgb_panel_register_event_callbacks(disp_ctx->panel_handle, &bb_cbs, disp_ctx->disp_drv));
        } else {
            ESP_ERROR_CHECK(
                esp_lcd_rgb_panel_register_event_callbacks(disp_ctx->panel_handle, &vsync_cbs, disp_ctx->disp_drv));
        }
#else
        ESP_RETURN_ON_FALSE(false, NULL, TAG, "RGB is supported only on ESP32S3 and from IDF 5.0!");
#endif

        /* Apply rotation from initial display configuration */
        lvgl_port_disp_rotation_update(disp_ctx);
    }
    lvgl_port_unlock();

    return disp;
}

esp_err_t lvgl_port_remove_disp(lv_display_t* disp)
{
    assert(disp);
    lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp);

    lvgl_port_lock(0);
    lv_disp_remove(disp);
    lvgl_port_unlock();

    if (disp_ctx->draw_buffs[0]) {
        free(disp_ctx->draw_buffs[0]);
    }

    if (disp_ctx->draw_buffs[1]) {
        free(disp_ctx->draw_buffs[1]);
    }

    if (disp_ctx->draw_buffs[2]) {
        free(disp_ctx->draw_buffs[2]);
    }

    if (disp_ctx->oled_buffer) {
        free(disp_ctx->oled_buffer);
    }

    if (disp_ctx->trans_sem) {
        vSemaphoreDelete(disp_ctx->trans_sem);
    }

    free(disp_ctx);

    return ESP_OK;
}

void lvgl_port_flush_ready(lv_display_t* disp)
{
    assert(disp);
    lv_disp_flush_ready(disp);
}

/*******************************************************************************
 * Private functions
 *******************************************************************************/

static lv_display_t* lvgl_port_add_disp_priv(const lvgl_port_display_cfg_t* disp_cfg,
                                             const lvgl_port_disp_priv_cfg_t* priv_cfg)
{
    esp_err_t ret               = ESP_OK;
    lv_display_t* disp          = NULL;
    lv_color_t* buf1            = NULL;
    lv_color_t* buf2            = NULL;
    uint32_t buffer_size        = 0;
    SemaphoreHandle_t trans_sem = NULL;
    assert(disp_cfg != NULL);
    assert(disp_cfg->panel_handle != NULL);
    assert(disp_cfg->buffer_size > 0);
    assert(disp_cfg->hres > 0);
    assert(disp_cfg->vres > 0);

    buffer_size = disp_cfg->buffer_size;

    /* Check supported display color formats */
    ESP_RETURN_ON_FALSE(
        disp_cfg->color_format == 0 || disp_cfg->color_format == LV_COLOR_FORMAT_RGB565 ||
            disp_cfg->color_format == LV_COLOR_FORMAT_RGB888 || disp_cfg->color_format == LV_COLOR_FORMAT_XRGB8888 ||
            disp_cfg->color_format == LV_COLOR_FORMAT_ARGB8888 || disp_cfg->color_format == LV_COLOR_FORMAT_I1,
        NULL, TAG, "Not supported display color format!");

    lv_color_format_t display_color_format =
        (disp_cfg->color_format != 0 ? disp_cfg->color_format : LV_COLOR_FORMAT_RGB565);
    uint8_t color_bytes = lv_color_format_get_size(display_color_format);
    if (disp_cfg->flags.swap_bytes) {
        /* Swap bytes can be used only in RGB565 color format */
        ESP_RETURN_ON_FALSE(display_color_format == LV_COLOR_FORMAT_RGB565, NULL, TAG,
                            "Swap bytes can be used only in display color format RGB565!");
    }

    if (disp_cfg->flags.buff_dma) {
        /* DMA buffer can be used only in RGB565 color format */
        ESP_RETURN_ON_FALSE(display_color_format == LV_COLOR_FORMAT_RGB565, NULL, TAG,
                            "DMA buffer can be used only in display color format RGB565 (not aligned copy)!");
    }

    /* Display context */
    lvgl_port_display_ctx_t* disp_ctx = malloc(sizeof(lvgl_port_display_ctx_t));
    ESP_GOTO_ON_FALSE(disp_ctx, ESP_ERR_NO_MEM, err, TAG, "Not enough memory for display context allocation!");
    memset(disp_ctx, 0, sizeof(lvgl_port_display_ctx_t));
    disp_ctx->io_handle         = disp_cfg->io_handle;
    disp_ctx->panel_handle      = disp_cfg->panel_handle;
    disp_ctx->control_handle    = disp_cfg->control_handle;
    disp_ctx->rotation.swap_xy  = disp_cfg->rotation.swap_xy;
    disp_ctx->rotation.mirror_x = disp_cfg->rotation.mirror_x;
    disp_ctx->rotation.mirror_y = disp_cfg->rotation.mirror_y;
    disp_ctx->flags.swap_bytes  = disp_cfg->flags.swap_bytes;
    disp_ctx->flags.sw_rotate   = disp_cfg->flags.sw_rotate;
    disp_ctx->current_rotation  = LV_DISPLAY_ROTATION_0;

    uint32_t buff_caps = 0;
#if SOC_PSRAM_DMA_CAPABLE == 0
    if (disp_cfg->flags.buff_dma && disp_cfg->flags.buff_spiram) {
        ESP_GOTO_ON_FALSE(false, ESP_ERR_NOT_SUPPORTED, err, TAG,
                          "Alloc DMA capable buffer in SPIRAM is not supported!");
    }
#endif
    if (disp_cfg->flags.buff_dma) {
        buff_caps |= MALLOC_CAP_DMA;
    }
    if (disp_cfg->flags.buff_spiram) {
        buff_caps |= MALLOC_CAP_SPIRAM;
    }
    if (buff_caps == 0) {
        buff_caps |= MALLOC_CAP_DEFAULT;
    }

    /* Use RGB internal buffers for avoid tearing effect */
    if (priv_cfg && priv_cfg->avoid_tearing) {
#if CONFIG_IDF_TARGET_ESP32S3 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 0, 0)
        buffer_size = disp_cfg->hres * disp_cfg->vres;
        ESP_GOTO_ON_ERROR(esp_lcd_rgb_panel_get_frame_buffer(disp_cfg->panel_handle, 2, (void*)&buf1, (void*)&buf2),
                          err, TAG, "Get RGB buffers failed");
#elif CONFIG_IDF_TARGET_ESP32P4 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 3, 0)
        buffer_size = disp_cfg->hres * disp_cfg->vres;
        ESP_GOTO_ON_ERROR(esp_lcd_dpi_panel_get_frame_buffer(disp_cfg->panel_handle, 2, (void*)&buf1, (void*)&buf2),
                          err, TAG, "Get RGB buffers failed");
#endif

        trans_sem = xSemaphoreCreateCounting(1, 0);
        ESP_GOTO_ON_FALSE(trans_sem, ESP_ERR_NO_MEM, err, TAG, "Failed to create transport counting Semaphore");
        disp_ctx->trans_sem = trans_sem;
    } else {
        /* alloc draw buffers used by LVGL */
        /* it's recommended to choose the size of the draw buffer(s) to be at least 1/10 screen sized */
        buf1 = heap_caps_aligned_alloc(CONFIG_LV_DRAW_BUF_ALIGN, buffer_size * color_bytes, buff_caps);
        ESP_GOTO_ON_FALSE(buf1, ESP_ERR_NO_MEM, err, TAG, "Not enough memory for LVGL buffer (buf1) allocation!");
        if (disp_cfg->double_buffer) {
            buf2 = heap_caps_aligned_alloc(CONFIG_LV_DRAW_BUF_ALIGN, buffer_size * color_bytes, buff_caps);
            ESP_GOTO_ON_FALSE(buf2, ESP_ERR_NO_MEM, err, TAG, "Not enough memory for LVGL buffer (buf2) allocation!");
        }

        disp_ctx->draw_buffs[0] = buf1;
        disp_ctx->draw_buffs[1] = buf2;
    }

    disp = lv_display_create(disp_cfg->hres, disp_cfg->vres);

    /* Set display color format */
    lv_display_set_color_format(disp, display_color_format);

    /* Monochrome display settings */
    if (disp_cfg->monochrome) {
#if CONFIG_LV_COLOR_DEPTH_1
#error please disable LV_COLOR_DEPTH_1 for using monochromatic screen
#endif

        /* Monochrome can be used only in RGB565 color format */
        ESP_RETURN_ON_FALSE(
            display_color_format == LV_COLOR_FORMAT_RGB565 || display_color_format == LV_COLOR_FORMAT_I1, NULL, TAG,
            "Monochrome can be used only in display color format RGB565 or I1!");

        /* When using monochromatic display, there must be used full bufer! */
        ESP_GOTO_ON_FALSE((disp_cfg->hres * disp_cfg->vres == buffer_size), ESP_ERR_INVALID_ARG, err, TAG,
                          "Monochromatic display must using full buffer!");

        disp_ctx->flags.monochrome = 1;
        lv_display_set_buffers(disp, buf1, buf2, buffer_size * color_bytes, LV_DISPLAY_RENDER_MODE_FULL);

        if (display_color_format == LV_COLOR_FORMAT_I1) {
            /* OLED monochrome buffer */
            // To use LV_COLOR_FORMAT_I1, we need an extra buffer to hold the converted data
            disp_ctx->oled_buffer = heap_caps_malloc(buffer_size, buff_caps);
            ESP_GOTO_ON_FALSE(disp_ctx->oled_buffer, ESP_ERR_NO_MEM, err, TAG,
                              "Not enough memory for LVGL buffer (OLED buffer) allocation!");
        }

    } else if (disp_cfg->flags.direct_mode) {
        /* When using direct_mode, there must be used full bufer! */
        ESP_GOTO_ON_FALSE((disp_cfg->hres * disp_cfg->vres == buffer_size), ESP_ERR_INVALID_ARG, err, TAG,
                          "Direct mode must using full buffer!");

        disp_ctx->flags.direct_mode = 1;
        lv_display_set_buffers(disp, buf1, buf2, buffer_size * color_bytes, LV_DISPLAY_RENDER_MODE_DIRECT);
    } else if (disp_cfg->flags.full_refresh) {
        /* When using full_refresh, there must be used full bufer! */
        ESP_GOTO_ON_FALSE((disp_cfg->hres * disp_cfg->vres == buffer_size), ESP_ERR_INVALID_ARG, err, TAG,
                          "Full refresh must using full buffer!");

        disp_ctx->flags.full_refresh = 1;
        lv_display_set_buffers(disp, buf1, buf2, buffer_size * color_bytes, LV_DISPLAY_RENDER_MODE_FULL);
    } else {
        lv_display_set_buffers(disp, buf1, buf2, buffer_size * color_bytes, LV_DISPLAY_RENDER_MODE_PARTIAL);
        // lv_display_set_buffers(disp, buf1, buf2, buffer_size * color_bytes, LV_DISPLAY_RENDER_MODE_FULL);
    }

    lv_display_set_flush_cb(disp, lvgl_port_flush_callback);
    lv_display_add_event_cb(disp, lvgl_port_disp_size_update_callback, LV_EVENT_RESOLUTION_CHANGED, disp_ctx);
    lv_display_add_event_cb(disp, lvgl_port_display_invalidate_callback, LV_EVENT_INVALIDATE_AREA, disp_ctx);
    lv_display_add_event_cb(disp, lvgl_port_display_invalidate_callback, LV_EVENT_REFR_REQUEST, disp_ctx);

    lv_display_set_driver_data(disp, disp_ctx);
    disp_ctx->disp_drv = disp;

    /* Use SW rotation */
    /* 【本地优化·刀1】旋转中转 buf 不再预分配：90° 直写帧缓冲路径命中时根本用不到，
     * 改为 flush 回退路径里惰性分配 → 常态省下一整块整屏缓冲的 PSRAM（≈1.8MB）。 */

err:
    if (ret != ESP_OK) {
        if (disp_ctx->draw_buffs[0]) {
            free(disp_ctx->draw_buffs[0]);
        }
        if (disp_ctx->draw_buffs[1]) {
            free(disp_ctx->draw_buffs[1]);
        }
        if (disp_ctx->draw_buffs[2]) {
            free(disp_ctx->draw_buffs[2]);
        }
        if (disp_ctx->oled_buffer) {
            free(disp_ctx->oled_buffer);
        }
        if (disp_ctx) {
            free(disp_ctx);
        }
        if (trans_sem) {
            vSemaphoreDelete(trans_sem);
        }
    }

    return disp;
}

#if LVGL_PORT_HANDLE_FLUSH_READY
static bool lvgl_port_flush_io_ready_callback(esp_lcd_panel_io_handle_t panel_io, esp_lcd_panel_io_event_data_t* edata,
                                              void* user_ctx)
{
    lv_display_t* disp_drv = (lv_display_t*)user_ctx;
    assert(disp_drv != NULL);
    lv_disp_flush_ready(disp_drv);
    return false;
}

#if (CONFIG_IDF_TARGET_ESP32P4 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 3, 0))
static bool lvgl_port_flush_dpi_panel_ready_callback(esp_lcd_panel_handle_t panel_io,
                                                     esp_lcd_dpi_panel_event_data_t* edata, void* user_ctx)
{
    lv_display_t* disp_drv = (lv_display_t*)user_ctx;
    assert(disp_drv != NULL);
    lv_disp_flush_ready(disp_drv);
    return false;
}

static bool lvgl_port_flush_dpi_vsync_ready_callback(esp_lcd_panel_handle_t panel_io,
                                                     esp_lcd_dpi_panel_event_data_t* edata, void* user_ctx)
{
    BaseType_t need_yield = pdFALSE;

    lv_display_t* disp_drv = (lv_display_t*)user_ctx;
    assert(disp_drv != NULL);
    lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp_drv);
    assert(disp_ctx != NULL);

    if (disp_ctx->trans_sem) {
        xSemaphoreGiveFromISR(disp_ctx->trans_sem, &need_yield);
    }

    return (need_yield == pdTRUE);
}

// 翻页围栏（帧边界 ISR）：扫出 DMA 每帧完成中断里、重挂描述符之后被调，edata->next_fb 是这一次实际挂上去的那一块
// （中断开头读的 cur_fb_index；这一次没重启 DMA 时是 NULL）。它正是登记的那一块（s_flip_target）时，
// 那次翻页才真正生效、旧前台 FB 脱离扫出，给等的一方（位移帧的 arm、直接送屏）放行；
// 不是的话（登记撞在这次中断中间，挂的还是旧块）这一次不放行，下一个帧边界再看。
IRAM_ATTR static bool lvgl_port_flip_fence_refresh_cb(esp_lcd_panel_handle_t panel,
                                                      esp_lcd_dpi_panel_event_data_t* edata, void* user_ctx)
{
    BaseType_t need_yield = pdFALSE;
    s_dbg_vsync_cnt++;   // 探针：量真实刷新率
    void* const next   = edata ? edata->next_fb : NULL;
    void*       target = __atomic_load_n(&s_flip_target, __ATOMIC_SEQ_CST);
    // 顺序：先记生效时刻、再记扫出的是哪一块、最后发信号——等的一方核对上 s_scan_fb 时，时刻一定已经是这一次的
    if (next && next == target) s_flip_done_us = (uint32_t)esp_timer_get_time();
    if (next) __atomic_store_n(&s_scan_fb, next, __ATOMIC_RELEASE);
    if (target && s_flip_sem) {
        if (next != target) {
            // 这一次挂上去的不是登记的那一块（登记撞在这次中断中间，或者这一次 DMA 没重启）：翻页还没生效，下一次再看
            s_dbg_flip_early++;
            return false;
        }
        // 还是刚才读到的那一块才清、才发信号：任务这时要是已经换了登记（上一次没等到就登记了下一次），不动它的
        if (__atomic_compare_exchange_n(&s_flip_target, &target, NULL, false, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)) {
            xSemaphoreGiveFromISR(s_flip_sem, &need_yield);
        }
    }
    return (need_yield == pdTRUE);
}
#endif

#if CONFIG_IDF_TARGET_ESP32S3 && ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(5, 0, 0)
static bool lvgl_port_flush_rgb_vsync_ready_callback(esp_lcd_panel_handle_t panel_io,
                                                     const esp_lcd_rgb_panel_event_data_t* edata, void* user_ctx)
{
    BaseType_t need_yield = pdFALSE;

    lv_display_t* disp_drv = (lv_display_t*)user_ctx;
    assert(disp_drv != NULL);
    lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp_drv);
    assert(disp_ctx != NULL);

    if (disp_ctx->trans_sem) {
        xSemaphoreGiveFromISR(disp_ctx->trans_sem, &need_yield);
    }

    return (need_yield == pdTRUE);
}
#endif
#endif

static void _lvgl_port_transform_monochrome(lv_display_t* display, const lv_area_t* area, uint8_t** color_map)
{
    assert(color_map);
    assert(*color_map);
    uint8_t* src                      = *color_map;
    lv_color16_t* color               = (lv_color16_t*)*color_map;
    lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(display);
    uint16_t hor_res                  = lv_display_get_physical_horizontal_resolution(display);
    uint16_t ver_res                  = lv_display_get_physical_vertical_resolution(display);
    uint16_t res                      = hor_res;
    bool swap_xy                      = (lv_display_get_rotation(display) == LV_DISPLAY_ROTATION_90 ||
                    lv_display_get_rotation(display) == LV_DISPLAY_ROTATION_270);

    int x1 = area->x1;
    int x2 = area->x2;
    int y1 = area->y1;
    int y2 = area->y2;

    lv_color_format_t color_format = lv_display_get_color_format(display);
    if (color_format == LV_COLOR_FORMAT_I1) {
        // This is necessary because LVGL reserves 2 x 4 bytes in the buffer, as these are assumed to be used as a
        // palette. Skip the palette here More information about the monochrome, please refer to
        // https://docs.lvgl.io/9.2/porting/display.html#monochrome-displays
        src += 8;
        /*Use oled_buffer as output */
        *color_map = disp_ctx->oled_buffer;
    }

    int out_x, out_y;
    for (int y = y1; y <= y2; y++) {
        for (int x = x1; x <= x2; x++) {
            bool chroma_color = 0;
            if (color_format == LV_COLOR_FORMAT_I1) {
                chroma_color = (src[(hor_res >> 3) * y + (x >> 3)] & 1 << (7 - x % 8));
            } else {
                chroma_color = (color[hor_res * y + x].blue > 16);
            }

            if (swap_xy) {
                out_x = y;
                out_y = x;
                res   = ver_res;
            } else {
                out_x = x;
                out_y = y;
                res   = hor_res;
            }

            /* Write to the buffer as required for the display.
             * It writes only 1-bit for monochrome displays mapped vertically.*/
            uint8_t* outbuf = NULL;
            outbuf          = *color_map + res * (out_y >> 3) + (out_x);
            if (chroma_color) {
                (*outbuf) &= ~(1 << (out_y % 8));
            } else {
                (*outbuf) |= (1 << (out_y % 8));
            }
        }
    }
}

void lvgl_port_rotate_area(lv_display_t* disp, lv_area_t* area)
{
    lv_display_rotation_t rotation = lv_display_get_rotation(disp);

    int32_t w = lv_area_get_width(area);
    int32_t h = lv_area_get_height(area);

    int32_t hres = lv_display_get_horizontal_resolution(disp);
    int32_t vres = lv_display_get_vertical_resolution(disp);
    if (rotation == LV_DISPLAY_ROTATION_90 || rotation == LV_DISPLAY_ROTATION_270) {
        vres = lv_display_get_horizontal_resolution(disp);
        hres = lv_display_get_vertical_resolution(disp);
    }

    switch (rotation) {
        case LV_DISPLAY_ROTATION_0:
            return;
        case LV_DISPLAY_ROTATION_90:
            area->y2 = vres - area->x1 - 1;
            area->x1 = area->y1;
            area->x2 = area->x1 + h - 1;
            area->y1 = area->y2 - w + 1;
            break;
        case LV_DISPLAY_ROTATION_180:
            area->y2 = vres - area->y1 - 1;
            area->y1 = area->y2 - h + 1;
            area->x2 = hres - area->x1 - 1;
            area->x1 = area->x2 - w + 1;
            break;
        case LV_DISPLAY_ROTATION_270:
            area->x1 = hres - area->y2 - 1;
            area->y2 = area->x2;
            area->x2 = area->x1 + h - 1;
            area->y1 = area->y2 - w + 1;
            break;
    }
}

IRAM_ATTR static void rotate_copy_pixel(const uint16_t* from, uint16_t* to, uint16_t x_start, uint16_t y_start,
                                        uint16_t x_end, uint16_t y_end, uint16_t w, uint16_t h, uint16_t rotation)
{
    ppa_srm_rotation_angle_t ppa_rotation;
    int x_offset = 0, y_offset = 0;

    // Determine rotation settings once and reuse
    switch (rotation) {
        case 90:
            ppa_rotation = PPA_SRM_ROTATION_ANGLE_270;
            x_offset     = h - y_end - 1;
            y_offset     = x_start;
            break;
        case 180:
            ppa_rotation = PPA_SRM_ROTATION_ANGLE_180;
            x_offset     = w - x_end - 1;
            y_offset     = h - y_end - 1;
            break;
        case 270:
            ppa_rotation = PPA_SRM_ROTATION_ANGLE_90;
            x_offset     = y_start;
            y_offset     = w - x_end - 1;
            break;
        default:
            ppa_rotation = PPA_SRM_ROTATION_ANGLE_0;
            break;
    }

    // Fill operation config for PPA rotation, without recalculating each time
    ppa_srm_oper_config_t oper_config = {
        .in.buffer         = from,
        .in.pic_w          = w,
        .in.pic_h          = h,
        .in.block_w        = x_end - x_start + 1,
        .in.block_h        = y_end - y_start + 1,
        .in.block_offset_x = x_start,
        .in.block_offset_y = y_start,
        .in.srm_cm         = (LV_COLOR_DEPTH == 24) ? PPA_SRM_COLOR_MODE_RGB888 : PPA_SRM_COLOR_MODE_RGB565,

        .out.buffer      = to,
        .out.buffer_size = ALIGN_UP_BY(sizeof(lv_color_t) * w * h, data_cache_line_size),
        .out.pic_w = (ppa_rotation == PPA_SRM_ROTATION_ANGLE_90 || ppa_rotation == PPA_SRM_ROTATION_ANGLE_270) ? h : w,
        .out.pic_h = (ppa_rotation == PPA_SRM_ROTATION_ANGLE_90 || ppa_rotation == PPA_SRM_ROTATION_ANGLE_270) ? w : h,
        .out.block_offset_x = x_offset,
        .out.block_offset_y = y_offset,
        .out.srm_cm         = (LV_COLOR_DEPTH == 24) ? PPA_SRM_COLOR_MODE_RGB888 : PPA_SRM_COLOR_MODE_RGB565,

        .rotation_angle = ppa_rotation,
        .scale_x        = 1.0,
        .scale_y        = 1.0,
        .rgb_swap       = 0,
        .byte_swap      = 0,
        .mode           = PPA_TRANS_MODE_BLOCKING,
    };

    ESP_ERROR_CHECK(ppa_do_scale_rotate_mirror(ppa_srm_handle, &oper_config));
}

// 【本地优化·刀1+刀2】横屏脏块经 PPA 90°旋转**直写 DPI 帧缓冲**的旋转后位置，非阻塞提交。
// 旧路径：PPA 旋转→整屏中转 buf(1.84MB)→DMA2D 再拷进帧缓冲——同一份像素在 PSRAM 上多走一个
// 来回，且 BLOCKING 模式让 LVGL 任务干等旋转。直写后中转 buf 不再需要（惰性分配，正常永不
// 分配），flush_ready 由 PPA 完成回调发出，LVGL 立刻在另一块 draw_buf 渲染下一帧与旋转并行。
// 撕裂语义与旧路径相同（DMA2D 原本也是直写正在扫出的单帧缓冲）。
static esp_err_t rotate_block_to_fb(const uint16_t* from, int block_w, int block_h,
                                    uint16_t* fb, int dst_x, int dst_y, int fb_w, int fb_h,
                                    lv_display_t* drv)
{
    ppa_srm_oper_config_t cfg = {
        .in.buffer         = from,
        .in.pic_w          = block_w,
        .in.pic_h          = block_h,
        .in.block_w        = block_w,
        .in.block_h        = block_h,
        .in.block_offset_x = 0,
        .in.block_offset_y = 0,
        .in.srm_cm         = (LV_COLOR_DEPTH == 24) ? PPA_SRM_COLOR_MODE_RGB888 : PPA_SRM_COLOR_MODE_RGB565,

        .out.buffer         = fb,
        .out.buffer_size    = ALIGN_UP_BY((size_t)fb_w * fb_h * sizeof(lv_color_t), data_cache_line_size),
        .out.pic_w          = fb_w,
        .out.pic_h          = fb_h,
        .out.block_offset_x = dst_x,
        .out.block_offset_y = dst_y,
        .out.srm_cm         = (LV_COLOR_DEPTH == 24) ? PPA_SRM_COLOR_MODE_RGB888 : PPA_SRM_COLOR_MODE_RGB565,

        .rotation_angle = PPA_SRM_ROTATION_ANGLE_90,    // 横屏→竖屏面板，90° 旋转对应 PPA_SRM_ROTATION_ANGLE_90
        .scale_x        = 1.0,
        .scale_y        = 1.0,
        .rgb_swap       = 0,
        .byte_swap      = 0,
        .mode           = PPA_TRANS_MODE_NON_BLOCKING,
        .user_data      = drv,                          // 完成回调据此发 lv_disp_flush_ready
    };
    s_dbg_rot_submit_us = esp_timer_get_time();         // 探针：提交时刻（完成回调算耗时）
    s_dbg_rot_px        = (uint32_t)block_w * block_h;
    // 在途计数先加后提交（避免 ISR 先于计数触发）；提交失败再回退
    __atomic_fetch_add(&s_ppa_pending, 1, __ATOMIC_SEQ_CST);
    esp_err_t err = ppa_do_scale_rotate_mirror(ppa_srm_handle, &cfg);
    if (err != ESP_OK) __atomic_fetch_sub(&s_ppa_pending, 1, __ATOMIC_SEQ_CST);
    return err;
}

// ── 调试微基准（'^' 命令触发，LVGL 上下文同步跑）────────────────────────────
// 量化渲染管线候选方案的关键数字：PSRAM/内部SRAM 写与读改写带宽、
// PPA 整屏/分条旋转、CPU 分块转置旋转。会短暂糟蹋屏幕内容，调用方结束后整屏重绘。
#include "esp_pm.h"

// bench 9 的 GDMA 完成回调（ISR）：释放等待信号量
static bool bench_mcp_done_cb(async_memcpy_handle_t h, async_memcpy_event_t* ev, void* cb_args)
{
    BaseType_t hp = pdFALSE;
    xSemaphoreGiveFromISR((SemaphoreHandle_t)cb_args, &hp);
    return hp == pdTRUE;
}

// CPU 旋转一条横屏带(band=W×rows,紧凑)到竖屏帧缓冲(fb=H宽)的对应竖条。
// 依赖 cache 做隐式分块：band 行常驻 cache，dst 每行写 2*rows 字节连续。
static void cpu_rot_band(const uint16_t* band, uint16_t* fb, int W, int H, int y0, int rows)
{
    for (int x0 = 0; x0 < W; x0 += 32) {
        int xe = x0 + 32 > W ? W : x0 + 32;
        for (int x = x0; x < xe; x++) {
            uint16_t*       drow = fb + (size_t)x * H + (H - y0 - rows);
            const uint16_t* s    = band + x;
            for (int y = 0; y < rows; y++) drow[rows - 1 - y] = s[(size_t)y * W];
        }
    }
}

void lvgl_port_dbg_render_bench(lv_display_t* disp)
{
    lvgl_port_display_ctx_t* ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp);
    if (!ctx || !ctx->panel_handle) return;
    void* fb = s_have2fb ? (void*)s_fbs[s_fb_cur] : NULL;
    if (!fb && (esp_lcd_dpi_panel_get_frame_buffer(ctx->panel_handle, 1, &fb) != ESP_OK || !fb)) return;
    const int    W    = lv_display_get_horizontal_resolution(disp);   // 1280
    const int    H    = lv_display_get_vertical_resolution(disp);     // 720
    const size_t NPX  = (size_t)W * H;
    const size_t line = data_cache_line_size ? data_cache_line_size : 64;
    const int    SH   = 32;                                           // 条带高

    // 基准期间锁满速，排除 DFS 干扰
    static esp_pm_lock_handle_t bench_lock = NULL;
    if (!bench_lock) esp_pm_lock_create(ESP_PM_CPU_FREQ_MAX, 0, "bench", &bench_lock);
    if (bench_lock) esp_pm_lock_acquire(bench_lock);

    ESP_LOGI(TAG, "== bench: internal free=%u largest=%u ==",
             (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),
             (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL));

    uint16_t* land  = heap_caps_aligned_alloc(line, ALIGN_UP_BY(NPX * 2, line), MALLOC_CAP_SPIRAM);
    int       sh_s  = SH;   // SRAM 条带实际高度（内存紧张时自适应缩小）
    uint16_t* strip = heap_caps_aligned_alloc(line, ALIGN_UP_BY((size_t)W * sh_s * 2, line), MALLOC_CAP_INTERNAL);
    if (!strip) { sh_s = 8; strip = heap_caps_aligned_alloc(line, ALIGN_UP_BY((size_t)W * sh_s * 2, line), MALLOC_CAP_INTERNAL); }
    if (!land) { ESP_LOGE(TAG, "bench: PSRAM 测试缓冲分配失败"); goto out; }
    if (!strip) ESP_LOGW(TAG, "bench: 内部 SRAM 条带分配失败 → 跳过 SRAM 项");
    else ESP_LOGI(TAG, "bench: SRAM 条带高=%d", sh_s);
    for (size_t i = 0; i < NPX; i++) land[i] = (uint16_t)i;

    {
        int64_t t;
        // 1) PSRAM 整屏纯写
        t = esp_timer_get_time();
        memset(land, 0x5A, NPX * 2);
        esp_cache_msync(land, ALIGN_UP_BY(NPX * 2, line), ESP_CACHE_MSYNC_FLAG_DIR_C2M);
        ESP_LOGI(TAG, "PSRAM 整屏纯写+msync : %.1f ms", (esp_timer_get_time() - t) / 1000.0);

        // 2) PSRAM 整屏逐 px 读改写（模拟 alpha 混合的访存模式）
        t = esp_timer_get_time();
        for (size_t i = 0; i < NPX; i++) {
            uint32_t s  = land[i];
            uint32_t ps = (s | (s << 16)) & 0x07E0F81FU;
            ps          = (ps + ((0x07E0F81FU - ps) >> 1)) & 0x07E0F81FU;
            land[i]     = (uint16_t)(ps | (ps >> 16));
        }
        ESP_LOGI(TAG, "PSRAM 整屏逐px读改写: %.1f ms", (esp_timer_get_time() - t) / 1000.0);

        // 3) SRAM 条带逐 px 读改写，等量像素（重复同一条带凑满整屏像素数）
        if (strip) {
            memset(strip, 0xA5, (size_t)W * sh_s * 2);
            t = esp_timer_get_time();
            for (int r = 0; r < H / sh_s; r++) {
                for (size_t i = 0; i < (size_t)W * sh_s; i++) {
                    uint32_t s  = strip[i];
                    uint32_t ps = (s | (s << 16)) & 0x07E0F81FU;
                    ps          = (ps + ((0x07E0F81FU - ps) >> 1)) & 0x07E0F81FU;
                    strip[i]    = (uint16_t)(ps | (ps >> 16));
                }
            }
            ESP_LOGI(TAG, "SRAM 等量逐px读改写 : %.1f ms", (esp_timer_get_time() - t) / 1000.0);
        }

        // 4) PPA 整屏旋转（BLOCKING，预热一次后计时）
        rotate_copy_pixel(land, (uint16_t*)fb, 0, 0, W - 1, H - 1, W, H, 270);
        t = esp_timer_get_time();
        rotate_copy_pixel(land, (uint16_t*)fb, 0, 0, W - 1, H - 1, W, H, 270);
        ESP_LOGI(TAG, "PPA 整屏旋转        : %.1f ms", (esp_timer_get_time() - t) / 1000.0);

        // 5) PPA 分条旋转（W×SH 条带，BLOCKING 串行；末条截短防越界）
        t = esp_timer_get_time();
        for (int y0 = 0; y0 < H; y0 += SH) {
            int rows = (y0 + SH <= H) ? SH : H - y0;
            rotate_copy_pixel(land, (uint16_t*)fb, 0, y0, W - 1, y0 + rows - 1, W, H, 270);
        }
        ESP_LOGI(TAG, "PPA 分条旋转(%d行/条): %.1f ms", SH, (esp_timer_get_time() - t) / 1000.0);

        // 6) CPU 分块转置：PSRAM 横屏 → 竖屏 FB（含 msync；末条截短）
        t = esp_timer_get_time();
        for (int y0 = 0; y0 < H; y0 += SH) {
            int rows = (y0 + SH <= H) ? SH : H - y0;
            cpu_rot_band(land + (size_t)y0 * W, (uint16_t*)fb, W, H, y0, rows);
        }
        esp_cache_msync(fb, ALIGN_UP_BY(NPX * 2, line), ESP_CACHE_MSYNC_FLAG_DIR_C2M);
        ESP_LOGI(TAG, "CPU 转置 PSRAM→FB   : %.1f ms", (esp_timer_get_time() - t) / 1000.0);

        // 7) CPU 分块转置：SRAM 条带 → 竖屏 FB（渲染直出 SRAM 时的旋转成本）
        if (strip) {
            t = esp_timer_get_time();
            for (int y0 = 0; y0 + sh_s <= H; y0 += sh_s) cpu_rot_band(strip, (uint16_t*)fb, W, H, y0, sh_s);
            esp_cache_msync(fb, ALIGN_UP_BY(NPX * 2, line), ESP_CACHE_MSYNC_FLAG_DIR_C2M);
            ESP_LOGI(TAG, "CPU 转置 SRAM→FB    : %.1f ms", (esp_timer_get_time() - t) / 1000.0);
        }

        // 8) PPA 0° 平移拷贝：竖屏几何(720×1280)整幅错位 30 列拷贝（滚动位移复用方案的核心操作）
        //    land 缓冲当"上一帧 FB"，写入真 FB —— 两侧都是顺序行访存，验证非转置时 PPA 的真实带宽
        {
            ppa_srm_oper_config_t cp = {
                .in.buffer          = land,
                .in.pic_w           = H,            // 竖屏几何：宽 720
                .in.pic_h           = W,            // 高 1280
                .in.block_w         = H - 30,
                .in.block_h         = W,
                .in.block_offset_x  = 30,
                .in.block_offset_y  = 0,
                .in.srm_cm          = PPA_SRM_COLOR_MODE_RGB565,
                .out.buffer         = fb,
                .out.buffer_size    = ALIGN_UP_BY(NPX * 2, line),
                .out.pic_w          = H,
                .out.pic_h          = W,
                .out.block_offset_x = 0,
                .out.block_offset_y = 0,
                .out.srm_cm         = PPA_SRM_COLOR_MODE_RGB565,
                .rotation_angle     = PPA_SRM_ROTATION_ANGLE_0,
                .scale_x            = 1.0,
                .scale_y            = 1.0,
                .mode               = PPA_TRANS_MODE_BLOCKING,
            };
            t = esp_timer_get_time();
            esp_err_t e = ppa_do_scale_rotate_mirror(ppa_srm_handle, &cp);
            ESP_LOGI(TAG, "PPA 0°平移拷贝(整幅): %.1f ms (%s)", (esp_timer_get_time() - t) / 1000.0, esp_err_to_name(e));
        }

        // 9) AXI-GDMA async_memcpy 错位线性拷贝（历史对照项；生产的位移复用已改用 DMA2D fbcpy）
        //    ⚠️ 大拷贝的 DMA 描述符 ~30KB 吃内部 RAM，测完立即卸载，不许常驻（WiFi 红线）
        {
            async_memcpy_handle_t mcp  = NULL;
            SemaphoreHandle_t     done = xSemaphoreCreateBinary();
            async_memcpy_config_t mc   = { .backlog = 4, .dma_burst_size = 128 };
            if (done && esp_async_memcpy_install_gdma_axi(&mc, &mcp) == ESP_OK) {
                size_t cp_len = NPX * 2 - 128;   // 错位后按 cache 行收尾
                t = esp_timer_get_time();
                esp_err_t e = esp_async_memcpy(mcp, fb, (uint8_t*)land + 60, cp_len,
                                               bench_mcp_done_cb, done);
                if (e == ESP_OK) xSemaphoreTake(done, pdMS_TO_TICKS(2000));
                ESP_LOGI(TAG, "GDMA 错位线性拷贝    : %.1f ms (%s)", (esp_timer_get_time() - t) / 1000.0, esp_err_to_name(e));
                esp_async_memcpy_uninstall(mcp);
            } else {
                ESP_LOGW(TAG, "GDMA async_memcpy 安装失败");
            }
            if (done) vSemaphoreDelete(done);
        }
    }

out:
    if (land) heap_caps_free(land);
    if (strip) heap_caps_free(strip);
    if (bench_lock) esp_pm_lock_release(bench_lock);
}

// ═══ 实验性调试设施（'disp' 命令族，2026-11-11 刷新性能探索）════════════════════
// 目的：用真机数字回答"整屏刷新还能不能更快"。全部由调试命令显式触发，默认不改变生产路径。

// CPU 旋转：横屏紧凑块 src(w×h，左上角在横屏坐标 (x1,y1)) 的第 [i0,i1) 列 → 竖屏 FB。
// 映射与 PPA 90° 直写完全一致：横屏 (X,Y) → FB 行 fb_h-1-X、列 Y（见 lvgl_port_rotate_area）。
// 每个横屏列落在 FB 一行里连续的 h 个像素（顺序写）；源按 32 列分块，块内每条源行只占 1 条 L1 行。
static void cpu_rot_block_to_fb(const uint16_t* src, int w, int h, int i0, int i1,
                                uint16_t* fb, int x1, int y1, int fb_w, int fb_h)
{
    for (int b0 = i0; b0 < i1; b0 += 32) {
        int be = b0 + 32 < i1 ? b0 + 32 : i1;
        for (int i = b0; i < be; i++) {
            uint16_t*       d = fb + (size_t)(fb_h - 1 - (x1 + i)) * fb_w + y1;
            const uint16_t* s = src + i;
            for (int j = 0; j < h; j++) {
                d[j] = *s;
                s += w;
            }
        }
    }
}

// 双核旋转用的 worker（核0）：只在实验管线 3 / bench2 里惰性创建，栈放 PSRAM 不占内部 RAM
typedef struct {
    const uint16_t* src;
    int w, h, i0, i1;
    uint16_t* fb;
    int x1, y1, fb_w, fb_h;
} dbg_rot_job_t;
static dbg_rot_job_t     s_rotw_job;
static SemaphoreHandle_t s_rotw_go   = NULL;
static SemaphoreHandle_t s_rotw_done = NULL;
static TaskHandle_t      s_rotw_task = NULL;

static void dbg_rotw_task(void* arg)
{
    (void)arg;
    for (;;) {
        xSemaphoreTake(s_rotw_go, portMAX_DELAY);
        const dbg_rot_job_t* j = &s_rotw_job;
        cpu_rot_block_to_fb(j->src, j->w, j->h, j->i0, j->i1, j->fb, j->x1, j->y1, j->fb_w, j->fb_h);
        xSemaphoreGive(s_rotw_done);
    }
}

static bool dbg_rotw_ready(void)
{
    if (s_rotw_task) return true;
    if (!s_rotw_go)   s_rotw_go   = xSemaphoreCreateBinary();
    if (!s_rotw_done) s_rotw_done = xSemaphoreCreateBinary();
    if (!s_rotw_go || !s_rotw_done) return false;
    // 优先级 8：与 fade worker 同档，低于 esp-hosted SDIO，不抢网络
    if (xTaskCreatePinnedToCoreWithCaps(dbg_rotw_task, "dbgrot", 4096, NULL, 8, &s_rotw_task, 0,
                                        MALLOC_CAP_SPIRAM) != pdPASS) {
        s_rotw_task = NULL;
        return false;
    }
    return true;
}

// 双核 CPU 旋转：核0 worker 转后半列，调用者(LVGL 任务,核1)转前半列。两半落在不同 FB 行。
static void cpu_rot_block_dual(const uint16_t* src, int w, int h, uint16_t* fb, int x1, int y1, int fb_w, int fb_h)
{
    if (w < 64 || !dbg_rotw_ready()) {
        cpu_rot_block_to_fb(src, w, h, 0, w, fb, x1, y1, fb_w, fb_h);
        return;
    }
    int mid = (w / 2) & ~31;
    s_rotw_job = (dbg_rot_job_t){ src, w, h, mid, w, fb, x1, y1, fb_w, fb_h };
    xSemaphoreGive(s_rotw_go);
    cpu_rot_block_to_fb(src, w, h, 0, mid, fb, x1, y1, fb_w, fb_h);
    xSemaphoreTake(s_rotw_done, portMAX_DELAY);
}

// 实验输出管线（s_dbg_pipe != 0 时由 flush 回调转入）：
//   1 = 丢弃输出，只量 LVGL 渲染本身；2/3 = CPU 单核/双核旋转直写 FB，取代 PPA。
// CPU 写 FB 走 cache：本刷新周期最后一笔 flush 统一「回写 + 失效」触及的 FB 行范围，
// 这样 cache 里不留 FB 的行，之后 DMA2D/PPA 旁路改写 FB 也不会被陈旧 cache 行盖回去。
static void dbg_flush_alt(lv_display_t* drv, const lv_area_t* area, const uint8_t* color_map, uint16_t* fb)
{
    if (s_dbg_pipe >= 2) {
        const int    w        = lv_area_get_width(area);
        const int    h        = lv_area_get_height(area);
        const int    fb_w     = lv_display_get_vertical_resolution(drv);     // 720
        const int    fb_h     = lv_display_get_horizontal_resolution(drv);   // 1280
        const size_t line     = data_cache_line_size ? data_cache_line_size : 64;
        const size_t fb_bytes = ALIGN_UP_BY((size_t)fb_w * fb_h * 2, line);
        if (s_dbg_need_m2c) {
            // 位移帧：DMA2D 刚旁路 cache 改写了整块后台 FB，先丢弃 cache 里它的旧行（此刻都是干净行）
            esp_cache_msync(fb, fb_bytes, ESP_CACHE_MSYNC_FLAG_DIR_M2C);
            s_dbg_need_m2c = false;
        }
        if (s_dbg_pipe == 3) cpu_rot_block_dual((const uint16_t*)color_map, w, h, fb, area->x1, area->y1, fb_w, fb_h);
        else                 cpu_rot_block_to_fb((const uint16_t*)color_map, w, h, 0, w, fb, area->x1, area->y1, fb_w, fb_h);
        int r0 = fb_h - 1 - area->x2;
        int r1 = fb_h - 1 - area->x1;
        if (r0 < s_dbg_row_min) s_dbg_row_min = r0;
        if (r1 > s_dbg_row_max) s_dbg_row_max = r1;
        if (lv_display_flush_is_last(drv) && s_dbg_row_max >= s_dbg_row_min) {
            size_t b0 = ((size_t)s_dbg_row_min * fb_w * 2) & ~(line - 1);
            size_t b1 = ALIGN_UP_BY((size_t)(s_dbg_row_max + 1) * fb_w * 2, line);
            if (b1 > fb_bytes) b1 = fb_bytes;
            esp_cache_msync((uint8_t*)fb + b0, b1 - b0,
                            ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_INVALIDATE);
            s_dbg_row_min = 0x7fffffff;
            s_dbg_row_max = -1;
        }
    } else {
        s_dbg_need_m2c = false;
    }
    lv_disp_flush_ready(drv);
}

// 调试切换前等在途 PPA 旋转清空（最多 200ms）。超时返回 false：调用方应放弃本次切换，
// 不能在 PPA 还在读写缓冲时换管线 / 换缓冲。
static bool dbg_wait_ppa_idle(void)
{
    int64_t t0 = esp_timer_get_time();
    while (s_ppa_pending > 0 && esp_timer_get_time() - t0 < 200000) vTaskDelay(1);
    return s_ppa_pending <= 0;
}

// 切换实验输出管线（LVGL 任务上下文调用）。0=恢复生产路径。
void lvgl_port_dbg_set_pipe(int mode)
{
    if (!dbg_wait_ppa_idle()) {
        ESP_LOGW(TAG, "dbg pipe: PPA 一直在忙，本次不切换");
        return;
    }
    if (mode < 0 || mode > 3) mode = 0;
    if (mode >= 2 && s_fbs[0]) {
        // 进 CPU 旋转管线前，丢弃 cache 里两块 FB 可能残留的旧行：淡入淡出引擎经 cache 读写过 FB，
        // 之后 PPA/DMA2D 旁路 cache 更新了内存。不丢弃的话，CPU 第一笔非整行写会命中旧行，
        // 末笔回写时把旧的相邻像素盖回 FB（区域边缘花点）。
        lv_display_t* d        = lv_display_get_default();
        const size_t  line     = data_cache_line_size ? data_cache_line_size : 64;
        const size_t  fb_bytes = ALIGN_UP_BY((size_t)lv_display_get_horizontal_resolution(d) *
                                             lv_display_get_vertical_resolution(d) * 2, line);
        esp_cache_msync(s_fbs[0], fb_bytes, ESP_CACHE_MSYNC_FLAG_DIR_M2C);
        if (s_fbs[1] && s_fbs[1] != s_fbs[0]) esp_cache_msync(s_fbs[1], fb_bytes, ESP_CACHE_MSYNC_FLAG_DIR_M2C);
    }
    s_dbg_pipe     = mode;
    s_dbg_row_min  = 0x7fffffff;
    s_dbg_row_max  = -1;
    s_dbg_need_m2c = false;
    ESP_LOGW(TAG, "dbg pipe=%d (0=PPA直写 1=丢弃输出 2=CPU旋转单核 3=CPU旋转双核)", mode);
}

// 运行时把 LVGL 绘制缓冲换成 rows 行高的小条带（两块，PSRAM）；rows<=0 恢复整屏缓冲。
// 用于对比"整屏 PSRAM 缓冲"与"cache 常驻小条带"的渲染耗时。LVGL 任务上下文调用。
static void* s_dbg_small[2] = { NULL, NULL };
esp_err_t lvgl_port_dbg_set_drawbuf_rows(lv_display_t* disp, int rows)
{
    lvgl_port_display_ctx_t* ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp);
    if (!ctx || !ctx->draw_buffs[0] || !ctx->draw_buffs[1]) return ESP_ERR_INVALID_STATE;
    if (!dbg_wait_ppa_idle()) return ESP_ERR_TIMEOUT;   // PPA 可能还在读旧缓冲：不换、不释放
    const int    W    = lv_display_get_horizontal_resolution(disp);
    const int    H    = lv_display_get_vertical_resolution(disp);
    const size_t line = data_cache_line_size ? data_cache_line_size : 64;
    void* nb0 = NULL;
    void* nb1 = NULL;
    if (rows > 0 && rows < H) {
        size_t sz = ALIGN_UP_BY((size_t)W * rows * 2, line);
        nb0 = heap_caps_aligned_alloc(line, sz, MALLOC_CAP_SPIRAM);
        nb1 = heap_caps_aligned_alloc(line, sz, MALLOC_CAP_SPIRAM);
        if (!nb0 || !nb1) {
            if (nb0) heap_caps_free(nb0);
            if (nb1) heap_caps_free(nb1);
            return ESP_ERR_NO_MEM;
        }
        lv_display_set_buffers(disp, nb0, nb1, (uint32_t)W * rows * 2, LV_DISPLAY_RENDER_MODE_PARTIAL);
    } else {
        rows = 0;
        lv_display_set_buffers(disp, ctx->draw_buffs[0], ctx->draw_buffs[1], (uint32_t)W * H * 2,
                               LV_DISPLAY_RENDER_MODE_PARTIAL);
    }
    if (s_dbg_small[0]) heap_caps_free(s_dbg_small[0]);
    if (s_dbg_small[1]) heap_caps_free(s_dbg_small[1]);
    s_dbg_small[0] = nb0;
    s_dbg_small[1] = nb1;
    ESP_LOGW(TAG, "dbg drawbuf rows=%d (0=整屏缓冲)", rows);
    return ESP_OK;
}

// bench2：某内存区的等量逐 px 读改写。buf 反复处理到 total_px，结果乘 scale 折算成"整屏等量"。
static void bench2_rmw(const char* name, uint32_t caps, size_t bytes, size_t total_px, int scale)
{
    uint16_t* p = heap_caps_aligned_alloc(128, bytes, caps);
    if (!p) {
        ESP_LOGW(TAG, "RMW %s: 分配 %u 字节失败，跳过", name, (unsigned)bytes);
        return;
    }
    size_t n = bytes / 2;
    memset(p, 0xA5, bytes);
    int64_t t    = esp_timer_get_time();
    size_t  done = 0;   // 实际处理的像素数（最后一轮会多跑一截，折算时按它算）
    for (; done < total_px; done += n) {
        for (size_t i = 0; i < n; i++) {
            uint32_t s  = p[i];
            uint32_t ps = (s | (s << 16)) & 0x07E0F81FU;
            ps          = (ps + ((0x07E0F81FU - ps) >> 1)) & 0x07E0F81FU;
            p[i]        = (uint16_t)(ps | (ps >> 16));
        }
    }
    float ms = (esp_timer_get_time() - t) / 1000.0f * scale * (float)total_px / (float)done;
    ESP_LOGI(TAG, "RMW %s @%p %uKB : %.1f ms", name, p, (unsigned)(bytes / 1024), ms);
    heap_caps_free(p);
}

static bool bench2_cpy_cb(esp_async_fbcpy_handle_t h, esp_async_fbcpy_event_data_t* ev, void* arg)
{
    BaseType_t hp = pdFALSE;
    xSemaphoreGiveFromISR((SemaphoreHandle_t)arg, &hp);
    return hp == pdTRUE;
}

// bench2：一笔 DMA2D 窗口拷贝（竖屏 FB 几何：x=列 y=行）
static esp_err_t bench2_fbcpy(esp_async_fbcpy_handle_t h, SemaphoreHandle_t sem, const void* src, void* dst,
                              int fbw, int fbh, int sx, int sy, int dx, int dy, int cw, int ch)
{
    esp_async_fbcpy_trans_desc_t tr = {
        .src_buffer        = src,
        .dst_buffer        = dst,
        .src_buffer_size_x = (size_t)fbw,
        .src_buffer_size_y = (size_t)fbh,
        .dst_buffer_size_x = (size_t)fbw,
        .dst_buffer_size_y = (size_t)fbh,
        .src_offset_x      = (size_t)sx,
        .src_offset_y      = (size_t)sy,
        .dst_offset_x      = (size_t)dx,
        .dst_offset_y      = (size_t)dy,
        .copy_size_x       = (size_t)cw,
        .copy_size_y       = (size_t)ch,
        .pixel_format_unique_id = { .color_type_id = COLOR_TYPE_ID(COLOR_SPACE_RGB, COLOR_PIXEL_RGB565) },
    };
    return esp_async_fbcpy(h, &tr, bench2_cpy_cb, sem);
}

// bench2（'disp bench'）：补齐第一版基准没测/测错的项——
//   内存区真实速度(带地址)、面板真实刷新率、cache 扫描成本、CPU 旋转(热条带/冷整屏/双核)、
//   DMA2D 拷贝(整幅/带状)、PPA 小块。LVGL 上下文同步跑，会糟蹋两块 FB，调用方结束后整屏重绘。
void lvgl_port_dbg_render_bench2(lv_display_t* disp)
{
    lvgl_port_display_ctx_t* ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp);
    if (!ctx || !s_have2fb) {
        ESP_LOGW(TAG, "bench2: 需要双帧缓冲");
        return;
    }
    if (!dbg_wait_ppa_idle()) {
        ESP_LOGW(TAG, "bench2: PPA 一直在忙，放弃");
        return;
    }

    uint16_t*    front = s_fbs[s_fb_cur];
    uint16_t*    back  = s_fbs[s_fb_cur ^ 1];
    const int    W     = lv_display_get_horizontal_resolution(disp);   // 1280
    const int    H     = lv_display_get_vertical_resolution(disp);     // 720
    const size_t NPX   = (size_t)W * H;
    const size_t line  = data_cache_line_size ? data_cache_line_size : 64;
    const size_t fbsz  = ALIGN_UP_BY(NPX * 2, line);

    static esp_pm_lock_handle_t lock = NULL;
    if (!lock) esp_pm_lock_create(ESP_PM_CPU_FREQ_MAX, 0, "bench2", &lock);
    if (lock) esp_pm_lock_acquire(lock);

    ESP_LOGI(TAG, "== bench2: INTERNAL free=%u largest=%u | L2MEM(INTERNAL+DMA) largest=%u | RTCRAM largest=%u | PSRAM free=%u ==",
             (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),
             (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL),
             (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA),
             (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_RTCRAM),
             (unsigned)heap_caps_get_free_size(MALLOC_CAP_SPIRAM));

    // 0) 面板真实刷新率：数 500ms 内的帧边界中断
    {
        uint32_t c0 = s_dbg_vsync_cnt;
        int64_t  t0 = esp_timer_get_time();
        vTaskDelay(pdMS_TO_TICKS(500));
        uint32_t c1 = s_dbg_vsync_cnt;
        int64_t  t1 = esp_timer_get_time();
        float    hz = (c1 - c0) * 1e6f / (float)(t1 - t0);
        ESP_LOGI(TAG, "面板帧边界中断      : %.2f Hz (周期 %.2f ms)", hz, hz > 0 ? 1000.0f / hz : 0.0f);
    }

    // 1) 各内存区逐 px 读改写（整屏等量）。打印地址：0x4FFxxxxx=L2MEM 0x5010xxxx=LP RAM 0x48xxxxxx=PSRAM
    bench2_rmw("L2MEM        ", MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA, 8 * 1024, NPX, 1);
    vTaskDelay(1);
    bench2_rmw("RTCRAM(LP)   ", MALLOC_CAP_RTCRAM, 8 * 1024, NPX / 16, 16);   // 只跑 1/16 再折算，免得长时间占核
    vTaskDelay(1);
    bench2_rmw("PSRAM 16行条带", MALLOC_CAP_SPIRAM, (size_t)W * 16 * 2, NPX, 1);
    bench2_rmw("PSRAM 32行条带", MALLOC_CAP_SPIRAM, (size_t)W * 32 * 2, NPX, 1);
    bench2_rmw("PSRAM 64行条带", MALLOC_CAP_SPIRAM, (size_t)W * 64 * 2, NPX, 1);
    vTaskDelay(1);
    bench2_rmw("PSRAM 整屏    ", MALLOC_CAP_SPIRAM, NPX * 2, NPX, 1);
    vTaskDelay(1);

    // 2) cache 扫描成本（整块 FB 范围、无脏行）
    {
        int64_t t = esp_timer_get_time();
        esp_cache_msync(front, fbsz, ESP_CACHE_MSYNC_FLAG_DIR_C2M);
        int64_t t1 = esp_timer_get_time();
        esp_cache_msync(front, fbsz, ESP_CACHE_MSYNC_FLAG_DIR_M2C);
        int64_t t2 = esp_timer_get_time();
        ESP_LOGI(TAG, "cache 扫描整块FB    : C2M(无脏行) %.2f ms / M2C 失效 %.2f ms", (t1 - t) / 1000.0, (t2 - t1) / 1000.0);
    }

    // 3) CPU 旋转：cache 常驻的热条带 → FB（真实映射），单核 / 双核
    static const int rows_list[] = { 16, 32, 64 };
    for (size_t k = 0; k < sizeof(rows_list) / sizeof(rows_list[0]); k++) {
        const int rows  = rows_list[k];
        uint16_t* strip = heap_caps_aligned_alloc(line, ALIGN_UP_BY((size_t)W * rows * 2, line), MALLOC_CAP_SPIRAM);
        if (!strip) continue;
        for (size_t i = 0; i < (size_t)W * rows; i++) strip[i] = (uint16_t)(i * 7);
        int64_t t = esp_timer_get_time();
        for (int y0 = 0; y0 < H; y0 += rows) {
            int hh = (y0 + rows <= H) ? rows : H - y0;
            cpu_rot_block_to_fb(strip, W, hh, 0, W, front, 0, y0, H, W);
        }
        int64_t t1 = esp_timer_get_time();
        esp_cache_msync(front, fbsz, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_INVALIDATE);
        int64_t t2 = esp_timer_get_time();
        ESP_LOGI(TAG, "CPU旋转 热条带%2d行→FB 单核: 旋转 %.1f + 回写 %.1f = %.1f ms", rows,
                 (t1 - t) / 1000.0, (t2 - t1) / 1000.0, (t2 - t) / 1000.0);
        vTaskDelay(1);
        t = esp_timer_get_time();
        for (int y0 = 0; y0 < H; y0 += rows) {
            int hh = (y0 + rows <= H) ? rows : H - y0;
            cpu_rot_block_dual(strip, W, hh, front, 0, y0, H, W);
        }
        t1 = esp_timer_get_time();
        esp_cache_msync(front, fbsz, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_INVALIDATE);
        t2 = esp_timer_get_time();
        ESP_LOGI(TAG, "CPU旋转 热条带%2d行→FB 双核: 旋转 %.1f + 回写 %.1f = %.1f ms", rows,
                 (t1 - t) / 1000.0, (t2 - t1) / 1000.0, (t2 - t) / 1000.0);
        heap_caps_free(strip);
        vTaskDelay(1);
    }

    // 3b) CPU 旋转：冷的整屏 PSRAM 源（=现行整屏绘制缓冲的情形），整列顺序写 FB
    uint16_t* land = heap_caps_aligned_alloc(line, fbsz, MALLOC_CAP_SPIRAM);
    if (land) {
        for (size_t i = 0; i < NPX; i++) land[i] = (uint16_t)(i * 5);
        esp_cache_msync(land, fbsz, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_INVALIDATE);   // 变冷
        int64_t t = esp_timer_get_time();
        cpu_rot_block_to_fb(land, W, H, 0, W, front, 0, 0, H, W);
        int64_t t1 = esp_timer_get_time();
        esp_cache_msync(front, fbsz, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_INVALIDATE);
        int64_t t2 = esp_timer_get_time();
        ESP_LOGI(TAG, "CPU旋转 冷整屏源→FB 单核 : 旋转 %.1f + 回写 %.1f = %.1f ms",
                 (t1 - t) / 1000.0, (t2 - t1) / 1000.0, (t2 - t) / 1000.0);
        vTaskDelay(1);
        esp_cache_msync(land, fbsz, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_INVALIDATE);
        t = esp_timer_get_time();
        cpu_rot_block_dual(land, W, H, front, 0, 0, H, W);
        t1 = esp_timer_get_time();
        esp_cache_msync(front, fbsz, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_INVALIDATE);
        t2 = esp_timer_get_time();
        ESP_LOGI(TAG, "CPU旋转 冷整屏源→FB 双核 : 旋转 %.1f + 回写 %.1f = %.1f ms",
                 (t1 - t) / 1000.0, (t2 - t1) / 1000.0, (t2 - t) / 1000.0);
        vTaskDelay(1);
    }

    // 4) DMA2D 窗口拷贝 front→back（竖屏几何 720×1280）
    {
        // 注意：一次只提交一笔。IDF 的 esp_async_fbcpy() 内部用 static 事务配置、dma2d_enqueue 只存指针，
        // 背靠背提交两笔在 DMA2D 通道被占用（要排队）时会串台。两路并发的实测结论
        // （各半 12.91ms vs 整幅 13.99ms，总线受限、无收益）已记入文档，测试项已删。
        esp_async_fbcpy_handle_t h1 = NULL;
        SemaphoreHandle_t        s1 = xSemaphoreCreateBinary();
        esp_async_fbcpy_config_t fc = {};
        if (s1 && esp_async_fbcpy_install(&fc, &h1) == ESP_OK) {
            int64_t   t;
            esp_err_t e;
            // a) 整幅错位 30 列（=生产位移帧的那笔拷贝）
            t = esp_timer_get_time();
            e = bench2_fbcpy(h1, s1, front, back, H, W, 30, 0, 0, 0, H - 30, W);
            if (e == ESP_OK) xSemaphoreTake(s1, pdMS_TO_TICKS(500));
            ESP_LOGI(TAG, "DMA2D 整幅错位30列  : %.2f ms (%s)", (esp_timer_get_time() - t) / 1000.0, esp_err_to_name(e));
            // b) 顶栏带：64 列 × 全高，不错位（容器外区域"原样拷贝"取代 LVGL 重绘的成本）
            t = esp_timer_get_time();
            e = bench2_fbcpy(h1, s1, front, back, H, W, 0, 0, 0, 0, 64, W);
            if (e == ESP_OK) xSemaphoreTake(s1, pdMS_TO_TICKS(500));
            ESP_LOGI(TAG, "DMA2D 顶栏带64列    : %.2f ms (%s)", (esp_timer_get_time() - t) / 1000.0, esp_err_to_name(e));
            // c) 只平移容器列范围（去掉 64 列顶栏）
            t = esp_timer_get_time();
            e = bench2_fbcpy(h1, s1, front, back, H, W, 94, 0, 64, 0, H - 94, W);
            if (e == ESP_OK) xSemaphoreTake(s1, pdMS_TO_TICKS(500));
            ESP_LOGI(TAG, "DMA2D 容器区错位30列: %.2f ms (%s)", (esp_timer_get_time() - t) / 1000.0, esp_err_to_name(e));
        } else {
            ESP_LOGW(TAG, "bench2: DMA2D fbcpy 安装失败，跳过");
        }
        if (h1) esp_async_fbcpy_uninstall(h1);
        if (s1) vSemaphoreDelete(s1);
        vTaskDelay(1);
    }

    // 5) PPA 小块旋转（阻塞，8 次平均）：位移帧里每帧要付的条带/顶栏/滚动条带
    if (land) {
        int64_t t = esp_timer_get_time();
        for (int i = 0; i < 8; i++) rotate_copy_pixel(land, front, 0, 0, W - 1, 31, W, H, 270);
        ESP_LOGI(TAG, "PPA 条带 1280x32    : %.2f ms/次", (esp_timer_get_time() - t) / 8000.0);
        t = esp_timer_get_time();
        for (int i = 0; i < 8; i++) rotate_copy_pixel(land, front, 0, 0, W - 1, 63, W, H, 270);
        ESP_LOGI(TAG, "PPA 顶栏 1280x64    : %.2f ms/次", (esp_timer_get_time() - t) / 8000.0);
        t = esp_timer_get_time();
        for (int i = 0; i < 8; i++) rotate_copy_pixel(land, front, W - 28, 0, W - 1, 655, W, H, 270);
        ESP_LOGI(TAG, "PPA 滚动条带 28x656 : %.2f ms/次", (esp_timer_get_time() - t) / 8000.0);
        heap_caps_free(land);
    }

    if (lock) esp_pm_lock_release(lock);
    ESP_LOGI(TAG, "== bench2 done ==");
}

// ── 自研页面过渡：底色淡入淡出 ───────────────────────────────────────────────
// 背景：横屏 UI 跑竖屏面板，LVGL 每帧整屏 90° PPA 旋转 ≈45ms，整屏滑动只能 ~6 帧很卡。
// 思路：换页时只旋转两次(当前屏直接读帧缓冲、新屏旋转一次)，淡入淡出的每一帧只做
// "RGB565 与底色的廉价 alpha 混合 + 直写 DPI 帧缓冲"，绕开每帧 45ms 旋转。
// 必须在 LVGL 任务上下文同步调用(调用方持显示锁)，期间独占帧缓冲。
//
// 区间混合：RGB565 三通道打散到 32 位(0x07E0F81F)，一次乘法完成混合，通道间留位防借位串扰。
static void fade_blend_range(uint16_t* dst, const uint16_t* img, size_t begin, size_t end,
                             uint32_t pcolor, uint32_t a)
{
    for (size_t i = begin; i < end; i++) {
        uint32_t s  = img[i];
        uint32_t ps = (s | (s << 16)) & 0x07E0F81FU;
        uint32_t r  = (ps + (((pcolor - ps) * a) >> 5)) & 0x07E0F81FU;
        dst[i] = (uint16_t)(r | (r >> 16));
    }
}

// 双核并行：LVGL 任务(核1)混合前半段，worker(核0)混合后半段，帧时约砍半 → 同样时长内帧数翻倍。
// worker 优先级低于 esp-hosted/WiFi 任务，过渡期间不会饿死 SDIO（红线）。
typedef struct {
    uint16_t* dst;
    const uint16_t* img;
    size_t begin, end;
    uint32_t pcolor, a;
} fade_job_t;
static fade_job_t        fade_job;
static SemaphoreHandle_t fade_start_sem, fade_done_sem;
static TaskHandle_t      fade_worker_handle = NULL;

static void fade_worker_task(void* arg)
{
    (void)arg;
    for (;;) {
        xSemaphoreTake(fade_start_sem, portMAX_DELAY);
        fade_blend_range(fade_job.dst, fade_job.img, fade_job.begin, fade_job.end,
                         fade_job.pcolor, fade_job.a);
        xSemaphoreGive(fade_done_sem);
    }
}

// 惰性建 worker（只在首次过渡时建一次）；失败则退回单核路径。
static bool fade_worker_ready(void)
{
    if (fade_worker_handle) return true;
    if (!fade_start_sem) fade_start_sem = xSemaphoreCreateBinary();
    if (!fade_done_sem)  fade_done_sem  = xSemaphoreCreateBinary();
    if (!fade_start_sem || !fade_done_sem) return false;
    // 优先级 8：高于普通后台任务保证并行度，低于 esp-hosted SDIO(≥20) 不抢网络
    if (xTaskCreatePinnedToCore(fade_worker_task, "fadeblend", 3072, NULL, 8,
                                &fade_worker_handle, 0) != pdPASS) {
        fade_worker_handle = NULL;
        return false;
    }
    return true;
}

// 单帧混合并直写帧缓冲。to_color=true: img→底色(底色权重 0→32)；false: 底色→img(32→0)。
static void fade_blend_to_fb(void* fb, const uint16_t* img, size_t npx, uint16_t color,
                             int dur_ms, bool to_color, size_t sync_bytes)
{
    uint16_t* dst = (uint16_t*)fb;
    uint32_t pcolor = ((uint32_t)color | ((uint32_t)color << 16)) & 0x07E0F81FU;
    bool dual = fade_worker_ready();
    int64_t t0 = esp_timer_get_time();
    for (;;) {
        int el = (int)((esp_timer_get_time() - t0) / 1000);
        if (el > dur_ms) el = dur_ms;
        uint32_t a = (uint32_t)(el * 32 / dur_ms);   // 底色权重 0..32
        if (!to_color) a = 32 - a;
        if (dual) {
            size_t half = npx / 2;
            fade_job = (fade_job_t){ dst, img, half, npx, pcolor, a };
            xSemaphoreGive(fade_start_sem);
            fade_blend_range(dst, img, 0, half, pcolor, a);
            xSemaphoreTake(fade_done_sem, portMAX_DELAY);
        } else {
            fade_blend_range(dst, img, 0, npx, pcolor, a);
        }
        esp_cache_msync(fb, sync_bytes, ESP_CACHE_MSYNC_FLAG_DIR_C2M);
        if (el >= dur_ms) break;
        vTaskDelay(pdMS_TO_TICKS(4));
    }
}

// 取帧缓冲指针 + 像素数(像素数旋转前后相同)。失败返回 false。
// 双 FB 翻页后"当前屏"不再固定是 fbs[0]，必须走合成器跟踪的 s_fb_cur。
static bool fade_get_fb(lv_display_t* disp, void** fb0, size_t* npx, size_t* sync_bytes)
{
    lvgl_port_display_ctx_t* ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(disp);
    if (!ctx || !ctx->panel_handle) return false;
    if (s_have2fb) *fb0 = s_fbs[s_fb_cur];
    else if (esp_lcd_dpi_panel_get_frame_buffer(ctx->panel_handle, 1, fb0) != ESP_OK || !*fb0) return false;
    size_t n  = (size_t)lv_display_get_horizontal_resolution(disp) * lv_display_get_vertical_resolution(disp);
    size_t ln = data_cache_line_size ? data_cache_line_size : 64;
    *npx = n;
    *sync_bytes = ALIGN_UP_BY(n * 2, ln);
    return true;
}

// 公开①:淡出当前屏到底色。结束后帧缓冲停在纯底色(供其后慢速建页时遮挡)。
esp_err_t lvgl_port_fade_out(lv_display_t* disp, uint16_t color565, int ms)
{
    void* fb0 = NULL; size_t npx = 0, sync_bytes = 0;
    if (!fade_get_fb(disp, &fb0, &npx, &sync_bytes)) return ESP_FAIL;
    size_t line = data_cache_line_size ? data_cache_line_size : 64;
    uint16_t* src = heap_caps_aligned_alloc(line, sync_bytes, MALLOC_CAP_SPIRAM);
    if (!src) return ESP_ERR_NO_MEM;
    // FB 由 PPA/GDMA 直写 PSRAM(旁路 cache)，CPU 读前先失效，防读到翻页前的陈旧缓存行
    esp_cache_msync(fb0, sync_bytes, ESP_CACHE_MSYNC_FLAG_DIR_M2C);
    memcpy(src, fb0, npx * 2);                               // 旋转-A = 当前帧缓冲(已旋转)
    fade_blend_to_fb(fb0, src, npx, color565, ms, true, sync_bytes);   // A → 底色
    heap_caps_free(src);
    return ESP_OK;
}

// 公开②:从底色淡入到新屏。newB_land=新屏横屏 RGB565 快照(land_w×land_h，紧凑)。
esp_err_t lvgl_port_fade_in(lv_display_t* disp, const uint16_t* newB_land,
                            int land_w, int land_h, uint16_t color565, int ms)
{
    if (!newB_land) return ESP_ERR_INVALID_ARG;
    void* fb0 = NULL; size_t npx = 0, sync_bytes = 0;
    if (!fade_get_fb(disp, &fb0, &npx, &sync_bytes)) return ESP_FAIL;
    size_t line = data_cache_line_size ? data_cache_line_size : 64;
    uint16_t* src = heap_caps_aligned_alloc(line, sync_bytes, MALLOC_CAP_SPIRAM);
    if (!src) return ESP_ERR_NO_MEM;
    // 新屏横屏→竖屏旋转写入 src，与 flush 同走 270°
    rotate_copy_pixel(newB_land, src, 0, 0, land_w - 1, land_h - 1, land_w, land_h, 270);
    fade_blend_to_fb(fb0, src, npx, color565, ms, false, sync_bytes);  // 底色 → B
    heap_caps_free(src);
    return ESP_OK;
}

static void lvgl_port_flush_callback(lv_display_t* drv, const lv_area_t* area, uint8_t* color_map)
{
    assert(drv != NULL);
    assert(area != NULL);
    assert(color_map != NULL);
    lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_display_get_driver_data(drv);
    assert(disp_ctx != NULL);
    int64_t dbg_t0 = esp_timer_get_time();   // flush 探针
    s_dbg_flush_calls++;

    int offsetx1 = area->x1;
    int offsetx2 = area->x2;
    int offsety1 = area->y1;
    int offsety2 = area->y2;

    // printf("%d %d %d %d\n", offsetx1, offsetx2, offsety1, offsety2);

    /* SW rotation enabled */
    if (disp_ctx->flags.sw_rotate && (disp_ctx->current_rotation > LV_DISPLAY_ROTATION_0)) {
        /* 【本地优化·刀1+刀2】90°+DSI+彩色+PARTIAL：PPA 旋转直写 DPI 帧缓冲，非阻塞返回。
         * flush_ready 由 PPA 完成回调发出（见 lvgl_port_ppa_trans_done_cb）。 */
        if (disp_ctx->current_rotation == LV_DISPLAY_ROTATION_90 &&
            disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_DSI &&
            !disp_ctx->flags.swap_bytes && !disp_ctx->flags.monochrome &&
            !disp_ctx->flags.direct_mode && !disp_ctx->flags.full_refresh) {
            // 位移复用帧：首个 flush 前必须等 GDMA 平移完成（PPA 条带写在平移之后才不被覆盖）
            if (s_shift_wait) {
                int64_t tw = esp_timer_get_time();
                xSemaphoreTake(s_shift_sem, pdMS_TO_TICKS(100));
                s_dbg_shift.cpwait_us += (uint32_t)(esp_timer_get_time() - tw);
                s_shift_wait   = false;
                s_dbg_need_m2c = true;
            }
            void* fb = s_have2fb ? (void*)target_fb() : NULL;
            if (!fb) esp_lcd_dpi_panel_get_frame_buffer(disp_ctx->panel_handle, 1, &fb);
            if (fb && s_dbg_pipe != 0) {   // 实验输出管线（调试命令开启时才走）
                dbg_flush_alt(drv, area, color_map, (uint16_t*)fb);
                s_dbg_flush_us += (uint32_t)(esp_timer_get_time() - dbg_t0);
                return;
            }
            if (fb) {
                lv_area_t rot = *area;
                lvgl_port_rotate_area(drv, &rot);   // 帧缓冲坐标系下的目标位置
                // 90° 时帧缓冲宽/高 = 逻辑竖/横向分辨率（与 lvgl_port_rotate_area 同一约定）
                int fb_w = lv_display_get_vertical_resolution(drv);
                int fb_h = lv_display_get_horizontal_resolution(drv);
                if (rotate_block_to_fb((const uint16_t*)color_map,
                                       offsetx2 - offsetx1 + 1, offsety2 - offsety1 + 1,
                                       (uint16_t*)fb, rot.x1, rot.y1, fb_w, fb_h, drv) == ESP_OK) {
                    static bool logged = false;   // 一次性：确认直写路径命中（远程验证用）
                    if (!logged) { logged = true; ESP_LOGI(TAG, "PPA direct-FB rotate active (fb=%p %dx%d)", fb, fb_w, fb_h); }
                    s_dbg_flush_us += (uint32_t)(esp_timer_get_time() - dbg_t0);
                    return;   // 非阻塞：LVGL 立即渲染下一帧，与 PPA 旋转并行
                }
                // 提交失败（队列满等罕见情况）→ 落回下方中转路径
                s_dbg_flush_fb++;
            }
        }

        /* SW rotation 回退路径（180/270 或直写不可用）：中转 buf 惰性分配——
         * 直写路径命中时永不分配，省下一整块整屏缓冲的 PSRAM（720p RGB565 ≈1.8MB）。 */
        if (!disp_ctx->draw_buffs[2]) {
            size_t rot_size = (size_t)lv_display_get_horizontal_resolution(drv) *
                              lv_display_get_vertical_resolution(drv) *
                              lv_color_format_get_size(lv_display_get_color_format(drv));
            size_t line = data_cache_line_size ? data_cache_line_size : 64;
            disp_ctx->draw_buffs[2] = heap_caps_aligned_alloc(line, ALIGN_UP_BY(rot_size, line),
                                                              MALLOC_CAP_SPIRAM);
            if (!disp_ctx->draw_buffs[2]) {
                ESP_LOGE(TAG, "rotation buffer alloc failed (%u bytes)", (unsigned)rot_size);
                lv_disp_flush_ready(drv);
                return;
            }
        }
        {
            int32_t ww           = lv_area_get_width(area);
            int32_t hh           = lv_area_get_height(area);
            lv_color_format_t cf = lv_display_get_color_format(drv);
            uint32_t w_stride    = lv_draw_buf_width_to_stride(ww, cf);
            uint32_t h_stride    = lv_draw_buf_width_to_stride(hh, cf);
            (void)w_stride;
            if (disp_ctx->current_rotation == LV_DISPLAY_ROTATION_180) {
                lv_draw_sw_rotate(color_map, disp_ctx->draw_buffs[2], hh, ww, h_stride, h_stride,
                                  LV_DISPLAY_ROTATION_180, cf);
            } else if (disp_ctx->current_rotation == LV_DISPLAY_ROTATION_90) {
                rotate_copy_pixel((uint16_t*)color_map, (uint16_t*)disp_ctx->draw_buffs[2], 0, 0, offsetx2 - offsetx1,
                                  offsety2 - offsety1, offsetx2 - offsetx1 + 1, offsety2 - offsety1 + 1, 270);
            } else if (disp_ctx->current_rotation == LV_DISPLAY_ROTATION_270) {
                lv_draw_sw_rotate(color_map, disp_ctx->draw_buffs[2], ww, hh, w_stride, h_stride,
                                  LV_DISPLAY_ROTATION_270, cf);
            }
            color_map = (uint8_t*)disp_ctx->draw_buffs[2];
            lvgl_port_rotate_area(drv, (lv_area_t*)area);
            offsetx1 = area->x1;
            offsetx2 = area->x2;
            offsety1 = area->y1;
            offsety2 = area->y2;
        }
    }

    if (disp_ctx->flags.swap_bytes) {
        size_t len = lv_area_get_size(area);
        lv_draw_sw_rgb565_swap(color_map, len);
    }

    /* Transfer data in buffer for monochromatic screen */
    if (disp_ctx->flags.monochrome) {
        _lvgl_port_transform_monochrome(drv, area, &color_map);
    }

    if ((disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_RGB || disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_DSI) &&
        (disp_ctx->flags.direct_mode || disp_ctx->flags.full_refresh)) {
        if (lv_disp_flush_is_last(drv)) {
            /* If the interface is I80 or SPI, this step cannot be used for drawing. */
            esp_lcd_panel_draw_bitmap(disp_ctx->panel_handle, 0, 0, lv_disp_get_hor_res(drv), lv_disp_get_ver_res(drv),
                                      color_map);
            /* Waiting for the last frame buffer to complete transmission */
            // xSemaphoreTake(disp_ctx->trans_sem, 0);
            // xSemaphoreTake(disp_ctx->trans_sem, portMAX_DELAY);
        }
    } else {
        esp_lcd_panel_draw_bitmap(disp_ctx->panel_handle, offsetx1, offsety1, offsetx2 + 1, offsety2 + 1, color_map);
    }

    if (disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_RGB ||
        (disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_DSI &&
         (disp_ctx->flags.direct_mode || disp_ctx->flags.full_refresh))) {
        lv_disp_flush_ready(drv);
    }
    s_dbg_flush_us += (uint32_t)(esp_timer_get_time() - dbg_t0);
}

// static void lvgl_port_flush_callback(lv_display_t *drv, const lv_area_t *area, uint8_t *color_map)
// {
//     assert(drv != NULL);
//     assert(area != NULL);
//     assert(color_map != NULL);
//     lvgl_port_display_ctx_t *disp_ctx = (lvgl_port_display_ctx_t *)lv_display_get_driver_data(drv);
//     assert(disp_ctx != NULL);

//     int offsetx1 = area->x1;
//     int offsetx2 = area->x2;
//     int offsety1 = area->y1;
//     int offsety2 = area->y2;

//     /* SW rotation enabled */
//     if (disp_ctx->flags.sw_rotate && (disp_ctx->current_rotation > LV_DISPLAY_ROTATION_0)) {
//         /* SW rotation */
//         if (disp_ctx->draw_buffs[2]) {
//             int32_t ww = lv_area_get_width(area);
//             int32_t hh = lv_area_get_height(area);
//             lv_color_format_t cf = lv_display_get_color_format(drv);
//             uint32_t w_stride = lv_draw_buf_width_to_stride(ww, cf);
//             uint32_t h_stride = lv_draw_buf_width_to_stride(hh, cf);
//             if (disp_ctx->current_rotation == LV_DISPLAY_ROTATION_180) {
//                 lv_draw_sw_rotate(color_map, disp_ctx->draw_buffs[2], hh, ww, h_stride, h_stride,
//                 LV_DISPLAY_ROTATION_180, cf);
//             } else if (disp_ctx->current_rotation == LV_DISPLAY_ROTATION_90) {
//                 lv_draw_sw_rotate(color_map, disp_ctx->draw_buffs[2], ww, hh, w_stride, h_stride,
//                 LV_DISPLAY_ROTATION_90, cf);
//             } else if (disp_ctx->current_rotation == LV_DISPLAY_ROTATION_270) {
//                 lv_draw_sw_rotate(color_map, disp_ctx->draw_buffs[2], ww, hh, w_stride, h_stride,
//                 LV_DISPLAY_ROTATION_270, cf);
//             }
//             color_map = (uint8_t *)disp_ctx->draw_buffs[2];
//             lvgl_port_rotate_area(drv, (lv_area_t *)area);
//             offsetx1 = area->x1;
//             offsetx2 = area->x2;
//             offsety1 = area->y1;
//             offsety2 = area->y2;
//         }
//     }

//     if (disp_ctx->flags.swap_bytes) {
//         size_t len = lv_area_get_size(area);
//         lv_draw_sw_rgb565_swap(color_map, len);
//     }

//     /* Transfer data in buffer for monochromatic screen */
//     if (disp_ctx->flags.monochrome) {
//         _lvgl_port_transform_monochrome(drv, area, &color_map);
//     }

//     if ((disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_RGB || disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_DSI) &&
//     (disp_ctx->flags.direct_mode || disp_ctx->flags.full_refresh)) {
//         if (lv_disp_flush_is_last(drv)) {
//             /* If the interface is I80 or SPI, this step cannot be used for drawing. */
//             esp_lcd_panel_draw_bitmap(disp_ctx->panel_handle, 0, 0, lv_disp_get_hor_res(drv),
//             lv_disp_get_ver_res(drv), color_map);
//             /* Waiting for the last frame buffer to complete transmission */
//             xSemaphoreTake(disp_ctx->trans_sem, 0);
//             xSemaphoreTake(disp_ctx->trans_sem, portMAX_DELAY);
//         }
//     } else {
//         esp_lcd_panel_draw_bitmap(disp_ctx->panel_handle, offsetx1, offsety1, offsetx2 + 1, offsety2 + 1, color_map);
//     }

//     if (disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_RGB || (disp_ctx->disp_type == LVGL_PORT_DISP_TYPE_DSI &&
//     (disp_ctx->flags.direct_mode || disp_ctx->flags.full_refresh))) {
//         lv_disp_flush_ready(drv);
//     }
// }

static void lvgl_port_disp_rotation_update(lvgl_port_display_ctx_t* disp_ctx)
{
    assert(disp_ctx != NULL);

    disp_ctx->current_rotation = lv_display_get_rotation(disp_ctx->disp_drv);
    if (disp_ctx->flags.sw_rotate) {
        return;
    }

    esp_lcd_panel_handle_t control_handle =
        (disp_ctx->control_handle ? disp_ctx->control_handle : disp_ctx->panel_handle);
    /* Solve rotation screen and touch */
    switch (lv_display_get_rotation(disp_ctx->disp_drv)) {
        case LV_DISPLAY_ROTATION_0:
            /* Rotate LCD display */
            esp_lcd_panel_swap_xy(control_handle, disp_ctx->rotation.swap_xy);
            esp_lcd_panel_mirror(control_handle, disp_ctx->rotation.mirror_x, disp_ctx->rotation.mirror_y);
            break;
        case LV_DISPLAY_ROTATION_90:
            /* Rotate LCD display */
            esp_lcd_panel_swap_xy(control_handle, !disp_ctx->rotation.swap_xy);
            if (disp_ctx->rotation.swap_xy) {
                esp_lcd_panel_mirror(control_handle, !disp_ctx->rotation.mirror_x, disp_ctx->rotation.mirror_y);
            } else {
                esp_lcd_panel_mirror(control_handle, disp_ctx->rotation.mirror_x, !disp_ctx->rotation.mirror_y);
            }
            break;
        case LV_DISPLAY_ROTATION_180:
            /* Rotate LCD display */
            esp_lcd_panel_swap_xy(control_handle, disp_ctx->rotation.swap_xy);
            esp_lcd_panel_mirror(control_handle, !disp_ctx->rotation.mirror_x, !disp_ctx->rotation.mirror_y);
            break;
        case LV_DISPLAY_ROTATION_270:
            /* Rotate LCD display */
            esp_lcd_panel_swap_xy(control_handle, !disp_ctx->rotation.swap_xy);
            if (disp_ctx->rotation.swap_xy) {
                esp_lcd_panel_mirror(control_handle, disp_ctx->rotation.mirror_x, !disp_ctx->rotation.mirror_y);
            } else {
                esp_lcd_panel_mirror(control_handle, !disp_ctx->rotation.mirror_x, disp_ctx->rotation.mirror_y);
            }
            break;
    }

    /* Wake LVGL task, if needed */
    lvgl_port_task_wake(LVGL_PORT_EVENT_DISPLAY, disp_ctx->disp_drv);
}

static void lvgl_port_disp_size_update_callback(lv_event_t* e)
{
    assert(e);
    lvgl_port_display_ctx_t* disp_ctx = (lvgl_port_display_ctx_t*)lv_event_get_user_data(e);
    lvgl_port_disp_rotation_update(disp_ctx);
}

static void lvgl_port_display_invalidate_callback(lv_event_t* e)
{
    /* Wake LVGL task, if needed */
    lvgl_port_task_wake(LVGL_PORT_EVENT_DISPLAY, NULL);
}
