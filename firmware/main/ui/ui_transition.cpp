// 页面切换过渡：底色淡入淡出(拆成 out/in 两段，实现"先黑屏→建页/读数据→再淡入")。
// 背景：横屏 UI 跑竖屏面板，LVGL 每帧整屏 90° PPA 旋转 ≈45ms，整屏滑动很卡。
// 改为：换页开头 ui_fade_out() 先淡出到底色 → 期间同步建新屏(慢速读数据被黑屏遮挡) →
//       结尾 ui_fade_in(scr) 从底色淡入到新屏。只旋转两次，绕开每帧 45ms 旋转。
// 引擎在 esp_lvgl_port 的 lvgl_port_fade_out / lvgl_port_fade_in。
#include "ui.h"
#include "theme.h"
#include "esp_lvgl_port.h"

static bool s_faded = false;   // ui_fade_out 是否成功淡出(决定 ui_fade_in 走淡入还是瞬切)

// 各 ui_open_* 开头调用：把当前屏淡出到底色，帧缓冲停在纯底色以遮挡随后的建页/读数据。
void ui_fade_out(void)
{
    s_faded = false;
    lv_obj_t*     old  = lv_screen_active();
    lv_display_t* disp = lv_display_get_default();
    if (!old || !disp) return;                       // 开机首屏：无可淡出的当前屏
    uint16_t bg = lv_color_to_u16(lv_color_hex(COL_BG));
    if (lvgl_port_fade_out(disp, bg, 140) == ESP_OK) s_faded = true;
}

// 各 ui_open_* 结尾调用：新屏已建好(含数据)，从底色淡入到它，再正式加载、回收旧屏。
void ui_fade_in(lv_obj_t* new_scr)
{
    lv_obj_t*     old  = lv_screen_active();
    lv_display_t* disp = lv_display_get_default();

    if (s_faded && disp) {
        s_faded = false;
        lv_obj_update_layout(new_scr);
        lv_draw_buf_t* snap = lv_snapshot_take(new_scr, LV_COLOR_FORMAT_RGB565);
        int w = lv_display_get_horizontal_resolution(disp);
        int h = lv_display_get_vertical_resolution(disp);
        // 快照须紧凑(stride==w*2)才能直接喂旋转;否则跳过淡入(直接加载)避免画面错乱
        if (snap && (int)snap->header.stride == w * 2) {
            uint16_t bg = lv_color_to_u16(lv_color_hex(COL_BG));
            lvgl_port_fade_in(disp, (const uint16_t*)snap->data, w, h, bg, 160);
        }
        if (snap) lv_draw_buf_destroy(snap);
    }

    lv_screen_load(new_scr);
    // 旧屏可能正处于本次点击事件处理中，异步删除避免 use-after-free
    if (old && old != new_scr) lv_obj_delete_async(old);
}
