// 息屏管理：无操作自动息屏（背光 0，系统照常运行）+ 双击亮屏
//
// 息屏不影响无线显示：照常等电脑连；电脑的画面要上屏时由 scrmgr_stream_begin 点亮（见 main/app.hpp）。
// 显示期间 LVGL 停着，这里的定时器不跑，也就不会息屏；画面结束时非活动计时从头算。
// Tab5 的物理按键直通电源电路，固件不可见，因此唤醒只能靠触摸屏
// （背光关闭时触摸控制器仍在工作）。息屏时加载一个纯黑全屏页拦截
// 所有触摸：短时间内连击两次 → 亮屏、回到息屏前的页面；单击无效（防误触）。
#include "ui.h"
#include "theme.h"
#include "../board.hpp"
#include "../settings_store.hpp"

// 读取待重绘脏区列表（inv_areas/inv_p），用于"按渲染面积升频"
#include "src/display/lv_display_private.h"
#include "esp_lvgl_port.h"   // 帧探针：读 PPA 旋转耗时

#include <esp_log.h>
#include <esp_timer.h>
#include <esp_cpu.h>

static const char* TAG = "scrmgr";

#define DOUBLE_TAP_MS 600       // 双击最大间隔
#define DOUBLE_TAP_PX 40        // 两次点击最大距离（像素）：超出视为划过/误触，不算双击
#define CPU_FAST_IDLE_MS 4000   // 无操作超过此(ms)释放满速锁 → CPU 降到 90MHz

// ── 按"渲染活动"升频 ─────────────────────────────────────────
// 只看触摸空闲时间有两个洞：①重新触摸后要等 500ms 轮询才恢复满速，头几帧在 90MHz 下渲染；
// ②不靠触摸、内容自己刷新的页面（曲线刷新/滚动惯性）会一直停在 90MHz。
// 因此在每个刷新周期开始（LV_EVENT_REFR_START，渲染之前）统计本轮脏区总面积，
// 超过阈值立即升满速；小面积更新（光标闪烁/小图标）不升，保住息屏前的降频收益。
#define RENDER_BOOST_MIN_PX   ((1280 * 720) / 16)  // 大面积重绘判定阈值（整屏的 1/16）
#define RENDER_FAST_HOLD_MS   2000                 // 最后一次大面积重绘后满速保持时长
static uint32_t s_last_bigrender_ms = 0;

static void refr_start_cb(lv_event_t* e)
{
    lv_display_t* disp = (lv_display_t*)lv_event_get_target(e);
    if (!disp || disp->inv_p == 0) return;
    uint32_t px = 0;
    for (uint32_t i = 0; i < disp->inv_p && i < LV_INV_BUF_SIZE; i++) {
        if (disp->inv_area_joined[i]) continue;   // 被合并进其它脏区的不重复计
        px += (uint32_t)lv_area_get_size(&disp->inv_areas[i]);
    }
    if (px >= RENDER_BOOST_MIN_PX) {
        s_last_bigrender_ms = lv_tick_get();
        board::cpuFastHold(true);   // 渲染开始前升满速，本帧就吃到 360MHz
    }
}

// 触摸按下瞬间升满速（事件同步于 LVGL 任务，先于本次交互的首帧渲染），
// 不再等 500ms 的 idle 轮询。
static void indev_pressed_cb(lv_event_t* e)
{
    (void)e;
    board::cpuFastHold(true);
}

// ── 帧探针（调试）：量化"帧生成"各段 ───────────────────────────
// REFR_START(渲染前) → RENDER_READY(渲染完) → REFR_READY(本轮结束)。
// 只打大面积帧（≥整屏 1/8），避免小刷新刷屏。PPA 旋转耗时来自 esp_lvgl_port 探针。
static bool     s_prof_on = false;
static int64_t  s_prof_refr_us = 0;        // 本轮 REFR_START 时刻
static int64_t  s_prof_prev_refr_us = 0;   // 上一大面积帧的 REFR_START（算帧间隔）
static uint32_t s_prof_px = 0;
static int64_t  s_prof_render_us = 0;      // 渲染段耗时
static uint32_t s_prof_cycles = 0;         // REFR_START 时的 CPU 周期计数（量实际频率）

static void prof_refr_start_cb(lv_event_t* e)
{
    if (!s_prof_on) return;
    lv_display_t* disp = (lv_display_t*)lv_event_get_target(e);
    uint32_t px = 0;
    for (uint32_t i = 0; i < disp->inv_p && i < LV_INV_BUF_SIZE; i++) {
        if (disp->inv_area_joined[i]) continue;
        px += (uint32_t)lv_area_get_size(&disp->inv_areas[i]);
    }
    s_prof_px      = px;
    s_prof_refr_us = esp_timer_get_time();
    s_prof_cycles  = esp_cpu_get_cycle_count();   // 配合结束时刻算实际 CPU 频率（排除 DFS 疑点）
}

static void prof_render_ready_cb(lv_event_t* e)
{
    (void)e;
    if (!s_prof_on) return;
    s_prof_render_us = esp_timer_get_time() - s_prof_refr_us;
}

static void prof_refr_ready_cb(lv_event_t* e)
{
    (void)e;
    if (!s_prof_on || s_prof_px == 0) return;   // 小帧也打（小面积卡顿要看得见）
    int64_t now = esp_timer_get_time();
    uint32_t rot_us = 0, rot_px = 0, fl_n = 0, fl_us = 0, fl_fb = 0;
    lvgl_port_dbg_rot_get(&rot_us, &rot_px);
    lvgl_port_dbg_flush_get(&fl_n, &fl_us, &fl_fb);
    uint32_t mhz = (uint32_t)((esp_cpu_get_cycle_count() - s_prof_cycles) /
                              (uint32_t)(now - s_prof_refr_us + 1));
    ESP_LOGI(TAG, "frame px=%u render=%.1fms total=%.1fms dt=%.1fms rot=%.1fms(px=%u) flush=%u/%.1fms fb=%u cpu=%uMHz",
             (unsigned)s_prof_px, s_prof_render_us / 1000.0,
             (now - s_prof_refr_us) / 1000.0,
             s_prof_prev_refr_us ? (s_prof_refr_us - s_prof_prev_refr_us) / 1000.0 : 0.0,
             rot_us / 1000.0, (unsigned)rot_px, (unsigned)fl_n, fl_us / 1000.0,
             (unsigned)fl_fb, (unsigned)mhz);
    s_prof_prev_refr_us = s_prof_refr_us;
}

void scrmgr_profiler(bool on)
{
    s_prof_on = on;
    s_prof_prev_refr_us = 0;
    ESP_LOGI(TAG, "frame profiler %s", on ? "ON" : "OFF");
}

bool scrmgr_profiler_on(void) { return s_prof_on; }

// ── 调试：自动滚动当前页（远程量滚动性能，不必上手划屏）───────────
static lv_obj_t* find_scrollable(lv_obj_t* o)
{
    if (lv_obj_has_flag(o, LV_OBJ_FLAG_SCROLLABLE) &&
        (lv_obj_get_scroll_bottom(o) > 50 || lv_obj_get_scroll_top(o) > 50))
        return o;
    uint32_t n = lv_obj_get_child_count(o);
    for (uint32_t i = 0; i < n; i++) {
        lv_obj_t* r = find_scrollable(lv_obj_get_child(o, i));
        if (r) return r;
    }
    return nullptr;
}

// ── 调试：合成滚动基准页（'(' 命令循环 7 变体）──────────────────
// 同样滚动不同内容，用帧探针分离渲染耗时构成：
// A=纯色行(填充下限) B=A+圆角18(掩码成本) C=中文文字行(混合成本) D=不透明位图行(快照方案预演)
// E=瓦片网格自动平移 F=自己来回滚、滚的同时内容在变（验滚动加速画得对不对，见 churn_tick） G=F 但留着滚动条变亮的样式

// F 变体：每 5 行有一行带三个色块，另有一个不跟着滚的悬浮色块。定时器每一拍给所有色块换成同一个新颜色，中间夹两次滚动：
//   A 列（滚动之前换色）→ 滚半步 → B 列（两次滚动之间换色）→ 再滚半步 → C 列和悬浮块（滚动之后换色）
// 三个时机的失效记下时的滚动量不一样（0、半步、一整步）。B、C、悬浮块的失效被滚动自己那条整容器失效吞掉，不在 LVGL 的失效表里；
// A 隔一拍在表里，另一拍前面先发一条整容器失效（模拟滚动条过渡的一步、容器自己换样式），也被吞掉。
// 每 4 拍有一拍第二个半步往回滚（一个周期里方向反过）；悬浮块隔一拍藏起来、下一拍再露出来（藏的那一拍它已经不在了，
// 平移过去的旧像素要靠它原处那条失效补画）。
// F 去掉了「滚动中滚动条变亮」的样式：不然每次滚动都让滚动条的过渡重新开始，过渡的每一步整屏重画一次，画错的地方
// 一两帧就被盖掉，截图碰不上。去掉之后没有整屏重画，画错了就一直留在屏幕上。
// G 留着这个样式：每次滚动开始 / 结束 LVGL 给容器换状态、把所有行挨个标一遍失效，验 scroll_accel 的 restyle_cb 把这一串排除掉了
// （没排除的话记录表每帧都满，window 行里 dirty 不为 0、ok 小于 frames）。G 里隔几帧有一次整屏重画，颜色只能大致看。
// 画对了的话，任何一刻屏幕上所有色块同色、悬浮块要么整块在原处要么没有；哪一列颜色落后，或者悬浮块上下多出一截别的颜色，
// 就是那个时机的失效没重画对。
// 用法：'(' 切到 F（或 G）→ prof on → 等几秒 → prof off 看 scrlacc 的 window 行（frames 应等于 ok、lost 不为 0）；
//       滚动中 fbshot 截前台帧缓冲，数色块有几种颜色。
// 测不到的：容器外的窄带（这里容器就是整个屏幕）；对象挪位置；一个周期里滚了两拍以上（每拍之后刷新定时器都已到点，一帧里只有一拍）。
struct Churn {
    lv_obj_t*   scr;
    lv_obj_t*   sq[3][6];    // [列][第几个带色块的行]
    int         rows;
    lv_obj_t*   ref;         // 悬浮色块
    lv_timer_t* timer;
    int         dir;         // +1 往下看（内容上移），-1 往回
    uint32_t    tick;
};
#define CHURN_STEP 6         // 每半步滚几行

static void churn_tick(lv_timer_t* tm)
{
    Churn* c = (Churn*)lv_timer_get_user_data(tm);
    if (c->dir > 0 && lv_obj_get_scroll_bottom(c->scr) <= 0) c->dir = -1;
    else if (c->dir < 0 && lv_obj_get_scroll_top(c->scr) <= 0) c->dir = 1;
    const lv_color_t col = lv_color_hsv_to_rgb((uint16_t)(++c->tick * 47 % 360), 100, 100);
    if (c->tick % 2) lv_obj_invalidate(c->scr);     // 这一拍 A 的失效被这条吞掉
    // 两个半步：平时同向；每 4 拍有一拍第二个半步往回滚一半
    const int step[2] = {-c->dir * CHURN_STEP, c->tick % 4 == 3 ? c->dir * CHURN_STEP / 2 : -c->dir * CHURN_STEP};
    for (int col_i = 0; col_i < 3; col_i++) {
        for (int r = 0; r < c->rows; r++) lv_obj_set_style_bg_color(c->sq[col_i][r], col, 0);
        if (col_i < 2) lv_obj_scroll_by_bounded(c->scr, 0, step[col_i], LV_ANIM_OFF);
    }
    lv_obj_set_style_bg_color(c->ref, col, 0);
    lv_obj_update_flag(c->ref, LV_OBJ_FLAG_HIDDEN, c->tick % 2 == 0);
}

static lv_obj_t* churn_block(lv_obj_t* parent, int32_t x, int32_t y)
{
    lv_obj_t* b = lv_obj_create(parent);
    lv_obj_set_size(b, 120, 24);
    lv_obj_set_pos(b, x, y);
    lv_obj_set_style_bg_opa(b, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(b, 0, 0);
    lv_obj_set_style_border_width(b, 0, 0);
    lv_obj_remove_flag(b, (lv_obj_flag_t)(LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE));
    return b;
}

static void benchpage_build(int variant)
{
    static lv_draw_buf_t* s_rowimg = nullptr;   // D 变体共享位图(常驻,反复进入不重复分配)
    lv_obj_t* old = lv_screen_active();
    lv_obj_t* scr = lv_obj_create(nullptr);
    lv_obj_set_style_bg_color(scr, lv_color_hex(0x101418), 0);
    lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);
    lv_obj_set_flex_flow(scr, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_all(scr, 0, 0);
    lv_obj_set_style_pad_row(scr, 8, 0);

    if (variant == 3 && !s_rowimg) {
        s_rowimg = lv_draw_buf_create(1280, 80, LV_COLOR_FORMAT_RGB565, 0);
        if (s_rowimg) {
            for (uint32_t y = 0; y < 80; y++) {
                uint16_t* p = (uint16_t*)(s_rowimg->data + y * s_rowimg->header.stride);
                for (int x = 0; x < 1280; x++)
                    p[x] = (uint16_t)(((x * 31 / 1280) << 11) | ((y * 63 / 80) << 5) | (x & 31));
            }
        }
    }

    if (variant == 4) {
        // E: 5×3 张 512×512 位图网格 + 自动平移 —— 大位图整屏平移的绘制负载，
        // src 是 lv_draw_buf（内存位图）而非文件路径 → 当初用它和文件源的 272ms/帧对照，
        // 分离"file-src 图像管线"和"纯位图 blit"的成本。
        static lv_draw_buf_t* s_tileimg = nullptr;
        if (!s_tileimg) {
            s_tileimg = lv_draw_buf_create(512, 512, LV_COLOR_FORMAT_RGB565, 0);
            if (s_tileimg) {
                for (uint32_t y = 0; y < 512; y++) {
                    uint16_t* p = (uint16_t*)(s_tileimg->data + y * s_tileimg->header.stride);
                    for (int x = 0; x < 512; x++) p[x] = (uint16_t)(x * y);
                }
            }
        }
        lv_obj_set_layout(scr, LV_LAYOUT_NONE);   // 网格绝对定位，不走 flex
        lv_obj_t* layer = lv_obj_create(scr);
        lv_obj_set_size(layer, 5 * 512, 3 * 512);
        lv_obj_set_style_bg_opa(layer, LV_OPA_0, 0);
        lv_obj_set_style_border_width(layer, 0, 0);
        lv_obj_set_style_pad_all(layer, 0, 0);
        lv_obj_remove_flag(layer, LV_OBJ_FLAG_SCROLLABLE);
        for (int idx = 0; idx < 15 && s_tileimg; idx++) {
            lv_obj_t* img = lv_image_create(layer);
            lv_image_set_src(img, s_tileimg);
            lv_obj_set_pos(img, (idx % 5) * 512, (idx / 5) * 512);
        }
        // 自动平移：x 三角波往返（±12px/tick）
        lv_timer_t* t = lv_timer_create([](lv_timer_t* tm) {
            lv_obj_t* ly = (lv_obj_t*)lv_timer_get_user_data(tm);
            static int ph = 0;
            int cyc = ph++ % 100;
            int off = (cyc < 50 ? cyc : 100 - cyc) * 12;
            lv_obj_set_pos(ly, -600 + off, -200);
        }, 16, layer);
        lv_obj_add_event_cb(scr, [](lv_event_t* e) {
            lv_timer_delete((lv_timer_t*)lv_event_get_user_data(e));
        }, LV_EVENT_DELETE, t);
        lv_screen_load(scr);
        if (old && old != scr) lv_obj_delete_async(old);
        return;
    }

    for (int i = 0; i < 30; i++) {
        if (variant == 3 && s_rowimg) {
            lv_obj_t* img = lv_image_create(scr);
            lv_image_set_src(img, s_rowimg);
        } else {
            lv_obj_t* row = lv_obj_create(scr);
            lv_obj_set_size(row, LV_PCT(100), 80);
            lv_obj_set_style_bg_color(row, lv_color_hex((i & 1) ? 0x2A2F3A : 0x39404E), 0);
            lv_obj_set_style_bg_opa(row, LV_OPA_COVER, 0);
            lv_obj_set_style_radius(row, variant == 1 ? 18 : 0, 0);
            lv_obj_set_style_border_width(row, 0, 0);
            lv_obj_set_style_pad_all(row, 0, 0);
            lv_obj_remove_flag(row, LV_OBJ_FLAG_SCROLLABLE);
            if (variant == 2) {
                lv_obj_t* l = lv_label_create(row);
                lv_label_set_text_fmt(l, "第%02d行·设置页典型中文文字渲染负载基准测试样例", i);
                lv_obj_center(l);
            }
        }
    }
    if (variant == 5 || variant == 6) {
        Churn* c = new Churn{};
        c->scr = scr;
        c->dir = 1;
        for (int i = 0; i < 30; i += 5, c->rows++) {
            lv_obj_t* row = lv_obj_get_child(scr, i);
            for (int col_i = 0; col_i < 3; col_i++) c->sq[col_i][c->rows] = churn_block(row, 100 + col_i * 220, 28);
        }
        if (variant == 5) lv_obj_remove_style(scr, nullptr, LV_PART_SCROLLBAR | LV_STATE_SCROLLED);
        c->ref = churn_block(scr, 900, 20);
        lv_obj_add_flag(c->ref, LV_OBJ_FLAG_FLOATING);
        c->timer = lv_timer_create(churn_tick, 40, c);
        lv_obj_add_event_cb(scr, [](lv_event_t* e) {
            Churn* c = (Churn*)lv_event_get_user_data(e);
            lv_timer_delete(c->timer);
            delete c;
        }, LV_EVENT_DELETE, c);
    }
    ui_scroll_accel(scr);   // 基准页也走滚动位移复用（远程验证加速效果）
    lv_screen_load(scr);
    if (old && old != scr) lv_obj_delete_async(old);
}

void ui_dbg_benchpage(int variant)
{
    static int v = 0;       // 不点名时轮到第几个
    // 息屏时不建：前台的黑屏页会被换掉删除，而背光还关着、「息屏着」的标记还在——之后双击叫不醒（和 open 命令拒绝换页是一个道理）
    if (scrmgr_is_off()) {
        ESP_LOGW(TAG, "息屏中，先发 o 唤醒再开基准页");
        return;
    }
    if (variant >= 0 && variant < 7) v = variant;
    ESP_LOGI(TAG, "benchpage variant=%c (A纯色/B圆角/C中文/D位图/E瓦片网格平移/F边滚边变/G同F带滚动条样式)", 'A' + v);
    benchpage_build(v);
    v = (v + 1) % 7;
}

void ui_dbg_autoscroll(void)
{
    lv_obj_t* scr = lv_screen_active();
    lv_obj_t* sc  = scr ? find_scrollable(scr) : nullptr;
    if (!sc) { ESP_LOGW(TAG, "autoscroll: 当前页无可滚动容器"); return; }
    int32_t down = lv_obj_get_scroll_bottom(sc);
    int32_t up   = lv_obj_get_scroll_top(sc);
    // 朝剩余空间大的方向滚，来回按 '%' 即可反复滚动
    int32_t dy = (down >= up) ? LV_MIN(down, 900) : -LV_MIN(up, 900);
    ESP_LOGI(TAG, "autoscroll dy=%d (top=%d bottom=%d)", (int)-dy, (int)up, (int)down);
    lv_obj_scroll_by_bounded(sc, 0, -dy, LV_ANIM_ON);
}

static bool     s_off = false;
static uint32_t s_last_tap_ms = 0;
static int32_t  s_last_tap_x  = 0;
static int32_t  s_last_tap_y  = 0;
static int      s_inhibit = 0;       // 有几处要求不息屏（常亮）。见 scrmgr_inhibit
static bool     s_restore_after_stream = false;     // 息屏着被画面流点亮的：前台还是黑屏页，画面结束时要把页面建回来
static void   (*s_wake_hook)(void) = nullptr;       // 亮屏、页面建回来之后调（见 scrmgr_set_wake_hook）

// 息屏降 LVGL 刷新率：黑屏页无脏区，60Hz 空转纯属白唤醒（每次都要跑一遍 timer 链）。
// 200ms 足够黑屏页的双击事件路径（触摸是事件驱动直接投递，不依赖刷新节拍）。
#define REFR_MS_ON  16
#define REFR_MS_OFF 200
static void set_refr_period(uint32_t ms)
{
    lv_timer_t* t = lv_display_get_refr_timer(nullptr);
    if (t) lv_timer_set_period(t, ms);
}

// 亮屏时除了建页面、开背光之外的那几步
static void light_up(void)
{
    s_off = false;
    s_last_tap_ms = 0;
    // 亮屏本身算一次用户活动：双击唤醒时触摸已重置过计时，但串口 'o' 唤醒、被画面流点亮没有——
    // 长时间闲置后亮屏，idle_check 下一拍(≤500ms)就会因"无操作超时"立刻再次息屏。
    lv_display_trigger_activity(nullptr);
    board::cpuFastHold(true);      // 唤醒即升满速，建页面跟手
    set_refr_period(REFR_MS_ON);   // 先恢复 60Hz 再建页面，动画不掉帧
    board::notifyScreen(true);     // 后台任务（chggov/wifi）恢复亮屏节奏
}

// 唤醒（LVGL 上下文调用：双击回调 / dbgcon 经 ui_async 投递 / 要弹配对密码框时）
void scrmgr_wake(void)
{
    if (!s_off) return;
    ESP_LOGI(TAG, "wake");
    light_up();
    ui_nav_restore();              // 回到息屏前的页面（黑屏页由换页过程回收）
    board::setBrightness(settings::get().brightness);
    if (s_wake_hook) s_wake_hook();
}

void scrmgr_set_wake_hook(void (*fn)(void))
{
    s_wake_hook = fn;
}

// 电脑的画面要上屏了（拿着 LVGL 锁调，不一定在 LVGL 线程上）：息屏着就点亮。页面先不建——LVGL 这就要停了，
// 前台留着黑屏页，等屏幕还回来（scrmgr_stream_end）再建。
void scrmgr_stream_begin(void)
{
    if (!s_off) return;
    ESP_LOGI(TAG, "wake（电脑的画面上屏）");
    light_up();
    s_restore_after_stream = true;
    board::setBrightness(settings::get().brightness);
}

// 背光关掉、各处转入息屏节奏（黑屏页由调用方管）
static void go_dark(void)
{
    s_off = true;
    board::setBrightness(0);
    board::notifyScreen(false);   // 后台任务转入息屏节奏
    set_refr_period(REFR_MS_OFF); // LVGL 刷新 60Hz→5Hz（黑屏页无脏区，纯省唤醒）
}

// 屏幕还给界面了（拿着 LVGL 锁调）：是息屏着被点亮的那一次、或者调用方要换页（reopen，它已经用 ui_nav_record 记好了去哪一页）的话，
// 把记录的页面建出来。建页面要走淡入淡出，放到 LVGL 线程里做
void scrmgr_stream_end(bool reopen)
{
    const bool from_off = s_restore_after_stream;
    s_restore_after_stream = false;
    if (!from_off && !reopen) return;
    // 调用方要换页：原来那一页开在顶层的弹框（「移除？」确认框）跟着关掉，不带到新页面上去。
    // 息屏着被点亮的那种来路顶层本来就是空的（息屏时清过）
    if (reopen) lv_obj_clean(lv_layer_top());
    const lv_result_t queued = lv_async_call([](void*) {
        if (s_off) return;         // 轮到这里之前又息屏了：亮屏时自然会建
        ui_nav_restore();
        if (s_wake_hook) s_wake_hook();
    }, nullptr);
    if (queued == LV_RESULT_OK) return;
    // 排不上（内存分配不出来）。前台还是黑屏页的话退回息屏：不然背光亮着、屏幕是黑的，双击也不认（它只在息屏时亮屏）
    ESP_LOGE(TAG, "画面结束：建页面的异步调用排不上%s", from_off ? "，退回息屏" : "");
    if (from_off) go_dark();
}

bool scrmgr_is_off(void) { return s_off; }

// ── 黑屏页：双击检测（时间窗口 + 落点距离双重判定）──────────────
// 手掌划过屏幕会在短时间内连续触发多次 CLICKED，但各触点分散在屏幕不同位置；
// 真双击是同一根手指在几乎同一点连点两下。只卡时间间隔挡不住划过，需再卡落点距离。
static void off_screen_clicked_cb(lv_event_t* e)
{
    lv_indev_t* indev = lv_event_get_indev(e);
    lv_point_t p = {0, 0};
    if (indev) lv_indev_get_point(indev, &p);

    uint32_t now = lv_tick_get();
    int32_t  dx  = p.x - s_last_tap_x;
    int32_t  dy  = p.y - s_last_tap_y;
    bool near = (dx * dx + dy * dy) <= (DOUBLE_TAP_PX * DOUBLE_TAP_PX);

    if (now - s_last_tap_ms < DOUBLE_TAP_MS && near) {
        ESP_LOGI(TAG, "double tap -> wake");
        scrmgr_wake();
    } else {
        s_last_tap_ms = now;
        s_last_tap_x  = p.x;
        s_last_tap_y  = p.y;
    }
}

void scrmgr_screen_off(void)
{
    if (s_off) return;
    ESP_LOGI(TAG, "screen off");
    go_dark();

    lv_obj_t* scr = lv_obj_create(nullptr);
    lv_obj_set_style_bg_color(scr, lv_color_hex(0x000000), 0);
    lv_obj_remove_flag(scr, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_flag(scr, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_event_cb(scr, off_screen_clicked_cb, LV_EVENT_CLICKED, nullptr);
    lv_screen_load(scr);
    // 顶层开着的弹框（确认框、配对密码框）都关掉：它们盖在黑屏页上面，留着的话双击落在它们身上，屏幕就叫不醒了。
    // 配对密码框问的那一次还没答完的话，亮屏之后会再弹出来（见 app.cpp 的 on_wake）
    lv_obj_clean(lv_layer_top());

    // 旧屏幕手动删除：lv_screen_load 不带 auto_del
    // → 由各页面在 LV_EVENT_DELETE 里释放资源
}

// 从控件事件里直接删自己所在的屏幕不安全 → 异步执行
static void request_off_cb(void* arg)
{
    (void)arg;
    if (s_off) return;
    lv_obj_t* old = lv_screen_active();
    scrmgr_screen_off();
    lv_obj_delete(old);
}

void scrmgr_request_off(void)
{
    lv_async_call(request_off_cb, nullptr);
}

// ── 无操作检测 ────────────────────────────────────────────────
static void idle_check_cb(lv_timer_t* timer)
{
    (void)timer;
    uint32_t inact = lv_display_get_inactive_time(nullptr);
    // DFS：触摸空闲且近期无大面积渲染才释放满速锁 → CPU 降到 90MHz。
    // 升速由 refr_start_cb / indev_pressed_cb 即时完成，这里只负责"降"。
    bool render_recent = (lv_tick_get() - s_last_bigrender_ms) < RENDER_FAST_HOLD_MS;
    board::cpuFastHold(inact < CPU_FAST_IDLE_MS || render_recent);

    if (s_off) return;
    if (s_inhibit) return;   // 常亮模式：跳过息屏判断（上面 cpuFastHold 已执行→DFS 降频仍生效，常亮不等于满速）

    uint32_t timeout_s = settings::get().screenoff_s;
    if (timeout_s == 0) return;  // 永不息屏（降频仍照常生效）

    if (inact > timeout_s * 1000) {
        lv_obj_t* old = lv_screen_active();
        scrmgr_screen_off();
        lv_obj_delete(old);
    }
}

void scrmgr_init(void)
{
    lv_timer_create(idle_check_cb, 500, nullptr);

    // 大面积重绘 → 渲染前升满速（治"内容自刷新页面停在 90MHz 渲染"）
    lv_display_t* disp = lv_display_get_default();
    if (disp) {
        lv_display_add_event_cb(disp, refr_start_cb, LV_EVENT_REFR_START, nullptr);
        // 帧探针（常注册、s_prof_on 门控，'#' 命令开关）
        lv_display_add_event_cb(disp, prof_refr_start_cb,   LV_EVENT_REFR_START,   nullptr);
        lv_display_add_event_cb(disp, prof_render_ready_cb, LV_EVENT_RENDER_READY, nullptr);
        lv_display_add_event_cb(disp, prof_refr_ready_cb,   LV_EVENT_REFR_READY,   nullptr);
    }

    // 触摸按下 → 立即升满速（治"停顿 4s 后再触摸，头 500ms 在 90MHz 下渲染"）
    for (lv_indev_t* indev = lv_indev_get_next(nullptr); indev; indev = lv_indev_get_next(indev)) scrmgr_watch_indev(indev);
}

void scrmgr_watch_indev(lv_indev_t* indev)
{
    if (lv_indev_get_type(indev) == LV_INDEV_TYPE_POINTER) lv_indev_add_event_cb(indev, indev_pressed_cb, LV_EVENT_PRESSED, nullptr);
}

// 禁止/恢复息屏。true = 无视 screenoff_s 一直亮屏（DFS 降频不受影响）；false = 撤回一次。
// 按次数计：几处各自要求常亮（现在只有调试命令 disp awake）互不影响，都撤回了才恢复息屏，
// 这时**重置非活动计时**，从此刻起按 screenoff_s 重新倒计时（不是立即息屏）。只在 LVGL 线程里调。
void scrmgr_inhibit(bool on)
{
    if (on) {
        s_inhibit++;
    } else if (s_inhibit > 0 && --s_inhibit == 0) {
        lv_display_trigger_activity(nullptr);
    }
    ESP_LOGI(TAG, "inhibit=%d", s_inhibit);
}
