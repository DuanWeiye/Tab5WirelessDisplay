// 「已配对的电脑」页（主页面的二级页）：无线显示配对过的电脑，逐台移除。
#include "ui.h"
#include "theme.h"

#include <cstdio>

#include "../wdisp.hpp"
#include "../wdisp_pair.hpp"

namespace {

// 电脑报的名字可以是空的
const char* shown_name(const char* name)
{
    return name[0] ? name : "未命名的电脑";
}

struct pairs_ui_t {
    wdpair::Entry e[wdpair::MAX_PEERS];
};
lv_obj_t*     s_pairs_scr = nullptr;
// 「移除？」确认框问的是哪一台。确认框挂在顶层，可能比这一页活得久，所以另存一份、不指向页面里的东西
wdpair::Entry s_rm;

// 移除之后重画：还有就重开这一页，一台都不剩了回主页面。等确认框关了再换页，所以是异步调的
void pairs_reopen(void*)
{
    if (!s_pairs_scr || lv_screen_active() != s_pairs_scr) return;
    wdpair::Entry left[wdpair::MAX_PEERS];
    if (wdpair::list(left) > 0) ui_open_pairs();
    else ui_open_main();
}

void rm_confirmed(void*)
{
    wdisp::forget(s_rm.id);
    lv_async_call(pairs_reopen, nullptr);
}

void rm_clicked(lv_event_t* e)
{
    s_rm = *(const wdpair::Entry*)lv_event_get_user_data(e);
    char title[80];
    snprintf(title, sizeof title, "移除 %s？", shown_name(s_rm.name));
    ui_confirm(title, nullptr, "移除", true, rm_confirmed, nullptr);
}

void pairs_delete_cb(lv_event_t* e)
{
    if (lv_event_get_target_obj(e) == s_pairs_scr) s_pairs_scr = nullptr;
    lv_free(lv_event_get_user_data(e));
}

}  // namespace

void ui_open_pairs(void)
{
    ui_nav_record(ui_open_pairs);
    ui_fade_out();
    lv_obj_t* scr     = lv_obj_create(nullptr);
    lv_obj_t* content = ui_page_frame(scr, "已配对的电脑", true);

    lv_obj_set_flex_flow(content, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(content, 16, 0);

    pairs_ui_t* ui = (pairs_ui_t*)lv_malloc(sizeof(pairs_ui_t));
    lv_memzero(ui, sizeof(pairs_ui_t));
    const int n = wdpair::list(ui->e);      // 最多 4 台，一屏放得下，不用滚动
    for (int i = 0; i < n; i++) {
        lv_obj_t* row = ui_card(content);
        lv_obj_set_size(row, LV_PCT(100), 88);
        lv_obj_set_style_pad_hor(row, 28, 0);

        lv_obj_t* name = lv_label_create(row);
        lv_obj_set_width(name, 900);
        lv_label_set_long_mode(name, LV_LABEL_LONG_DOT);
        lv_label_set_text(name, shown_name(ui->e[i].name));
        lv_obj_set_style_text_font(name, &font_cn_28, 0);
        lv_obj_set_style_text_color(name, lv_color_hex(COL_TEXT), 0);
        lv_obj_align(name, LV_ALIGN_LEFT_MID, 0, 0);

        lv_obj_t* rm = lv_button_create(row);
        lv_obj_set_size(rm, 140, 56);
        lv_obj_align(rm, LV_ALIGN_RIGHT_MID, 0, 0);
        lv_obj_set_style_bg_color(rm, lv_color_hex(COL_CARD_HI), 0);
        lv_obj_set_style_radius(rm, 12, 0);
        lv_obj_set_style_shadow_width(rm, 0, 0);
        lv_obj_t* rm_t = lv_label_create(rm);
        lv_label_set_text(rm_t, "移除");
        lv_obj_set_style_text_font(rm_t, &font_cn_20, 0);
        lv_obj_set_style_text_color(rm_t, lv_color_hex(COL_BAD), 0);
        lv_obj_center(rm_t);
        lv_obj_add_event_cb(rm, rm_clicked, LV_EVENT_CLICKED, &ui->e[i]);
    }

    s_pairs_scr = scr;
    lv_obj_add_event_cb(scr, pairs_delete_cb, LV_EVENT_DELETE, ui);
    ui_fade_in(scr);
}
