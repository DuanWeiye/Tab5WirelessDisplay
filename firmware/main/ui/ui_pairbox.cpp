// 配对密码框：没配对过的电脑连上来时，让人输电脑上设的配对密码（协议「配对」，接收端见 main/wdisp.hpp）。
// 挂在顶层（lv_layer_top），不属于哪一页：界面在主页面、WiFi 页、「已配对的电脑」页都照样弹。什么时候弹、什么时候关由 app 定
// （main/app.cpp：wdisp 要人输密码时弹、息屏着先亮屏；配上了 / 连接断了 / 暂停了就关）。息屏时顶层的东西会被清掉（screen_manager.cpp），
// 这个框也在内，亮屏后那一次询问还没答完的话 app 会让它再弹出来。同一时刻最多一个。只在 LVGL 线程里读写。
#include "ui_pairbox.hpp"

#include <cstring>

#include <esp_log.h>

#include "../app.hpp"
#include "theme.h"
#include "ui.h"

namespace {

const char* TAG = "ui_pairbox";

struct pair_box_t {
    lv_obj_t* modal;
    lv_obj_t* dlg;
    lv_obj_t* title;
    lv_obj_t* ta;
    lv_obj_t* ok;
    lv_obj_t* ok_lbl;
    lv_obj_t* spinner;      // 核对期间代替「确定」两个字（平时是空）
    lv_obj_t* cancel;
    lv_obj_t* kb;
    uint32_t  seq;          // 这个框问的是哪一次询问（wdisp::PairPrompt::seq）
    bool      busy;         // 密码交出去了，在等结果（两三秒）：要么框被关掉，要么标红重输
    bool      wrong;        // 标着红：上一次输的不对
};
pair_box_t* s_box = nullptr;

void box_set_busy(pair_box_t* b, bool busy)
{
    b->busy = busy;
    if (busy) {
        lv_obj_add_state(b->ok, LV_STATE_DISABLED);
        lv_obj_add_state(b->kb, LV_STATE_DISABLED);
        lv_obj_add_flag(b->ok_lbl, LV_OBJ_FLAG_HIDDEN);
        if (!b->spinner) {
            b->spinner = lv_spinner_create(b->ok);
            lv_obj_set_size(b->spinner, 36, 36);
            lv_obj_set_style_arc_width(b->spinner, 4, LV_PART_MAIN);
            lv_obj_set_style_arc_width(b->spinner, 4, LV_PART_INDICATOR);
            lv_obj_set_style_arc_color(b->spinner, lv_color_hex(COL_TEXT), LV_PART_INDICATOR);
            lv_spinner_set_anim_params(b->spinner, 1000, 200);
            lv_obj_center(b->spinner);
        }
    } else {
        lv_obj_remove_state(b->ok, LV_STATE_DISABLED);
        lv_obj_remove_state(b->kb, LV_STATE_DISABLED);
        lv_obj_remove_flag(b->ok_lbl, LV_OBJ_FLAG_HIDDEN);
        if (b->spinner) {
            lv_obj_delete(b->spinner);
            b->spinner = nullptr;
        }
    }
}

void box_set_wrong(pair_box_t* b, bool wrong)
{
    b->wrong = wrong;
    if (wrong) {
        lv_obj_set_style_border_color(b->ta, lv_color_hex(COL_BAD), 0);
        lv_obj_set_style_border_width(b->ta, 2, 0);
    } else {
        lv_obj_remove_local_style_prop(b->ta, LV_STYLE_BORDER_COLOR, 0);
        lv_obj_remove_local_style_prop(b->ta, LV_STYLE_BORDER_WIDTH, 0);
    }
}

// 「确定」、键盘上的 ✓ 和回车
void box_ok_cb(lv_event_t* e)
{
    pair_box_t* b = (pair_box_t*)lv_event_get_user_data(e);
    if (b->busy) return;
    const char* pw = lv_textarea_get_text(b->ta);
    if (!pw[0]) return;
    wdisp::pair_answer(b->seq, pw);
    box_set_wrong(b, false);
    box_set_busy(b, true);
}

// 「取消」= 暂停：这条连接以 BYE 结束、不再监听（不然会自动重连的电脑马上又连上来、又弹这个框），
// 主页面上露出「开始显示」。框由 app::pause() 关掉
void box_cancel_cb(lv_event_t*)
{
    app::pause();
}

void box_delete_cb(lv_event_t* e)
{
    pair_box_t* b = (pair_box_t*)lv_event_get_user_data(e);
    if (s_box == b) s_box = nullptr;
    lv_free(b);
}

// 排布同 WiFi 页的密码框（ui_wifi.cpp）：对话卡在上半区，下面是软键盘
pair_box_t* box_create()
{
    pair_box_t* b = (pair_box_t*)lv_malloc(sizeof(pair_box_t));
    lv_memzero(b, sizeof(pair_box_t));

    // 半透明遮罩，盖住整屏
    lv_obj_t* modal = lv_obj_create(lv_layer_top());
    b->modal = modal;
    lv_obj_set_size(modal, LV_PCT(100), LV_PCT(100));
    lv_obj_set_pos(modal, 0, 0);
    lv_obj_set_style_bg_color(modal, lv_color_hex(0x000000), 0);
    lv_obj_set_style_bg_opa(modal, LV_OPA_60, 0);
    lv_obj_set_style_border_width(modal, 0, 0);
    lv_obj_set_style_radius(modal, 0, 0);
    lv_obj_set_style_pad_all(modal, 0, 0);
    lv_obj_remove_flag(modal, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_event_cb(modal, box_delete_cb, LV_EVENT_DELETE, b);

    lv_obj_t* dlg = ui_card(modal);
    b->dlg = dlg;
    lv_obj_set_size(dlg, 640, 280);
    lv_obj_align(dlg, LV_ALIGN_TOP_MID, 0, 60);
    lv_obj_set_style_pad_all(dlg, 28, 0);

    b->title = lv_label_create(dlg);
    lv_obj_set_width(b->title, LV_PCT(100));
    lv_label_set_long_mode(b->title, LV_LABEL_LONG_DOT);
    lv_obj_set_style_text_font(b->title, &font_cn_28, 0);
    lv_obj_set_style_text_color(b->title, lv_color_hex(COL_TEXT), 0);
    lv_obj_align(b->title, LV_ALIGN_TOP_LEFT, 0, 0);

    lv_obj_t* ta = lv_textarea_create(dlg);
    b->ta = ta;
    lv_textarea_set_one_line(ta, true);
    lv_textarea_set_password_mode(ta, true);
    lv_textarea_set_max_length(ta, wdisp::PASSWORD_MAX);
    lv_obj_set_size(ta, LV_PCT(100), 64);
    lv_obj_align(ta, LV_ALIGN_TOP_LEFT, 0, 64);
    lv_obj_set_style_bg_color(ta, lv_color_hex(COL_BG), 0);
    lv_obj_set_style_text_font(ta, &lv_font_montserrat_24, 0);
    lv_obj_add_event_cb(ta, box_ok_cb, LV_EVENT_READY, b);      // 键盘上的 ✓ 和回车

    b->cancel = lv_button_create(dlg);
    lv_obj_set_size(b->cancel, 200, 60);
    lv_obj_align(b->cancel, LV_ALIGN_BOTTOM_LEFT, 0, 0);
    lv_obj_set_style_bg_color(b->cancel, lv_color_hex(COL_CARD_HI), 0);
    lv_obj_t* cancel_t = lv_label_create(b->cancel);
    lv_label_set_text(cancel_t, "取消");
    lv_obj_set_style_text_font(cancel_t, &font_cn_20, 0);
    lv_obj_center(cancel_t);
    lv_obj_add_event_cb(b->cancel, box_cancel_cb, LV_EVENT_CLICKED, nullptr);

    b->ok = lv_button_create(dlg);
    lv_obj_set_size(b->ok, 200, 60);
    lv_obj_align(b->ok, LV_ALIGN_BOTTOM_RIGHT, 0, 0);
    lv_obj_set_style_bg_color(b->ok, lv_color_hex(COL_ACCENT), 0);
    b->ok_lbl = lv_label_create(b->ok);
    lv_label_set_text(b->ok_lbl, "确定");
    lv_obj_set_style_text_font(b->ok_lbl, &font_cn_20, 0);
    lv_obj_center(b->ok_lbl);
    lv_obj_add_event_cb(b->ok, box_ok_cb, LV_EVENT_CLICKED, b);

    b->kb = ui_keyboard_create(modal);
    lv_obj_set_size(b->kb, LV_PCT(100), 360);
    lv_obj_align(b->kb, LV_ALIGN_BOTTOM_MID, 0, 0);
    lv_keyboard_set_textarea(b->kb, ta);
    return b;
}

}  // namespace

void ui_pairbox_show(const wdisp::PairPrompt& p)
{
    if (s_box && s_box->seq == p.seq) return;       // 这一次询问已经在框里了
    if (!s_box) s_box = box_create();
    pair_box_t* b = s_box;
    b->seq = p.seq;
    lv_label_set_text_fmt(b->title, p.known ? "重新配对 %s" : "配对 %s", p.pc_name[0] ? p.pc_name : "未命名的电脑");
    lv_textarea_set_text(b->ta, "");
    box_set_wrong(b, p.wrong);
    box_set_busy(b, false);
}

// 可能是在框里的按钮自己的事件里被调到的（点「取消」）：不当场删，先藏起来、和 s_box 脱钩，下一拍再删
void ui_pairbox_close(void)
{
    if (!s_box) return;
    lv_obj_t* modal = s_box->modal;
    s_box = nullptr;
    lv_obj_add_flag(modal, LV_OBJ_FLAG_HIDDEN);
    lv_obj_delete_async(modal);
}

void ui_pairbox_dbg_password(const char* pw)
{
    pair_box_t* b = s_box;
    if (!b) {
        ESP_LOGW(TAG, "配对密码框没开着");
        return;
    }
    if (!pw) {
        lv_obj_send_event(b->cancel, LV_EVENT_CLICKED, nullptr);
        return;
    }
    if (b->busy) {
        ESP_LOGW(TAG, "上一次输的密码还在核对");
        return;
    }
    lv_textarea_set_text(b->ta, "");
    if (!ui_keyboard_dbg_type(b->kb, pw)) return;       // 键盘上打不出来的密码，人也输不进去：不点「确定」
    lv_obj_send_event(b->ok, LV_EVENT_CLICKED, nullptr);
}

void ui_pairbox_dbg_box(void)
{
    pair_box_t* b = s_box;
    if (!b) {
        ESP_LOGW(TAG, "配对密码框：没开着");
        return;
    }
    lv_obj_update_layout(b->modal);
    lv_area_t dlg, kb;
    lv_obj_get_coords(b->dlg, &dlg);
    lv_obj_get_coords(b->kb, &kb);
    const uint32_t border = lv_color_to_u32(lv_obj_get_style_border_color(b->ta, LV_PART_MAIN)) & 0xFFFFFF;
    char missing[96];
    const int n_missing = ui_keyboard_dbg_missing(b->kb, missing, sizeof missing);
    ESP_LOGW(TAG, "配对密码框：开着，标题「%s」，第 %u 次询问，%s，%s（输入框边框 #%06x），框里 %u 个字符；对话卡 (%d,%d)-(%d,%d)，键盘 (%d,%d)-(%d,%d)，"
                  "键盘上打不出的字符：%s",
             lv_label_get_text(b->title), (unsigned)b->seq,
             b->busy ? "正在核对" : "等人输入", b->wrong ? "标着红" : "没标红", (unsigned)border,
             (unsigned)strlen(lv_textarea_get_text(b->ta)), (int)dlg.x1, (int)dlg.y1, (int)dlg.x2, (int)dlg.y2, (int)kb.x1, (int)kb.y1,
             (int)kb.x2, (int)kb.y2, n_missing ? missing : "无");
}
