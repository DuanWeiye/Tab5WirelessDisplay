// WiFi 页（设置页的二级页；主页面没联网时点状态也到这里）：开关 + 当前连接状态 / 已保存网络（记忆列表）/ 扫描连接
#include "ui.h"
#include "theme.h"
#include "../net/wifi.hpp"

#include <cstdio>
#include <cstring>
#include <cstdint>

typedef struct {
    lv_obj_t* sw;          // WiFi 开关
    lv_obj_t* state_lbl;   // 状态文字
    lv_obj_t* ssid_lbl;    // 当前 SSID
    lv_obj_t* ip_lbl;      // IP / RSSI
    lv_obj_t* forget_btn;  // 忘记网络
    lv_obj_t* saved_list;  // 已保存网络容器
    uint32_t  saved_sig;   // 记忆列表状态签名（变化才重绘）
    lv_obj_t* scan_btn;
    lv_obj_t* scan_btn_lbl;
    lv_obj_t* list;        // 扫描结果容器
    bool      scan_pending;  // 等待扫描结果
    int       last_state;    // 上次状态（-1=未知）：状态没变就不重设颜色（同值 set_style 也会失效重绘）
} wifi_ui_t;

static const char* state_name(wifi::State s)
{
    switch (s) {
        case wifi::State::Disabled:   return "已关闭";
        case wifi::State::Idle:       return "未连接";
        case wifi::State::Starting:                         // 开机后无线模块还在起：和正在连一样写
        case wifi::State::Connecting: return "连接中…";
        case wifi::State::Connected:  return "已连接";
        case wifi::State::Fault:      return "无线模块故障，请重启设备";
    }
    return "?";
}

// ── 密码输入弹窗（扫描列表点选加密网络时弹出）──────────────────
typedef struct {
    char      ssid[33];
    lv_obj_t* modal;
    lv_obj_t* ta;
} pwd_ctx_t;

static void pwd_cancel_cb(lv_event_t* e)
{
    pwd_ctx_t* ctx = (pwd_ctx_t*)lv_event_get_user_data(e);
    lv_obj_delete(ctx->modal);
    lv_free(ctx);
}

static void pwd_connect_cb(lv_event_t* e)
{
    pwd_ctx_t* ctx = (pwd_ctx_t*)lv_event_get_user_data(e);
    wifi::connect(ctx->ssid, lv_textarea_get_text(ctx->ta));
    lv_obj_delete(ctx->modal);
    lv_free(ctx);
}

static void open_password_modal(const char* ssid)
{
    pwd_ctx_t* ctx = (pwd_ctx_t*)lv_malloc(sizeof(pwd_ctx_t));
    lv_memzero(ctx, sizeof(pwd_ctx_t));
    snprintf(ctx->ssid, sizeof(ctx->ssid), "%s", ssid);

    // 半透明遮罩。⚠️ 页面根被 ui_page_frame 设成了 flex 纵向布局；遮罩若参与布局
    // 会被排到内容下面（飞出屏幕，键盘也跟着出界）→ 必须 IGNORE_LAYOUT 并钉到 (0,0)。
    lv_obj_t* modal = lv_obj_create(lv_screen_active());
    ctx->modal = modal;
    lv_obj_add_flag(modal, LV_OBJ_FLAG_IGNORE_LAYOUT);
    lv_obj_set_size(modal, LV_PCT(100), LV_PCT(100));
    lv_obj_set_pos(modal, 0, 0);
    lv_obj_set_style_bg_color(modal, lv_color_hex(0x000000), 0);
    lv_obj_set_style_bg_opa(modal, LV_OPA_60, 0);
    lv_obj_set_style_border_width(modal, 0, 0);
    lv_obj_set_style_radius(modal, 0, 0);
    lv_obj_set_style_pad_all(modal, 0, 0);
    lv_obj_remove_flag(modal, LV_OBJ_FLAG_SCROLLABLE);

    // 对话卡（放上半区，给键盘留空间）
    lv_obj_t* dlg = ui_card(modal);
    lv_obj_set_size(dlg, 640, 280);
    lv_obj_align(dlg, LV_ALIGN_TOP_MID, 0, 60);
    lv_obj_set_style_pad_all(dlg, 28, 0);

    lv_obj_t* title = lv_label_create(dlg);
    lv_label_set_text_fmt(title, "连接到 %s", ssid);
    lv_obj_set_style_text_font(title, &font_cn_28, 0);
    lv_obj_set_style_text_color(title, lv_color_hex(COL_TEXT), 0);
    lv_obj_align(title, LV_ALIGN_TOP_LEFT, 0, 0);

    lv_obj_t* ta = lv_textarea_create(dlg);
    ctx->ta = ta;
    lv_textarea_set_one_line(ta, true);
    lv_textarea_set_password_mode(ta, true);
    lv_textarea_set_placeholder_text(ta, "password");
    lv_obj_set_size(ta, LV_PCT(100), 64);
    lv_obj_align(ta, LV_ALIGN_TOP_LEFT, 0, 64);
    lv_obj_set_style_bg_color(ta, lv_color_hex(COL_BG), 0);
    lv_obj_set_style_text_font(ta, &lv_font_montserrat_24, 0);

    lv_obj_t* cancel = lv_button_create(dlg);
    lv_obj_set_size(cancel, 200, 60);
    lv_obj_align(cancel, LV_ALIGN_BOTTOM_LEFT, 0, 0);
    lv_obj_set_style_bg_color(cancel, lv_color_hex(COL_CARD_HI), 0);
    lv_obj_t* cancel_t = lv_label_create(cancel);
    lv_label_set_text(cancel_t, "取消");
    lv_obj_set_style_text_font(cancel_t, &font_cn_20, 0);
    lv_obj_center(cancel_t);
    lv_obj_add_event_cb(cancel, pwd_cancel_cb, LV_EVENT_CLICKED, ctx);

    lv_obj_t* ok = lv_button_create(dlg);
    lv_obj_set_size(ok, 200, 60);
    lv_obj_align(ok, LV_ALIGN_BOTTOM_RIGHT, 0, 0);
    lv_obj_set_style_bg_color(ok, lv_color_hex(COL_ACCENT), 0);
    lv_obj_t* ok_t = lv_label_create(ok);
    lv_label_set_text(ok_t, "连接");
    lv_obj_set_style_text_font(ok_t, &font_cn_20, 0);
    lv_obj_center(ok_t);
    lv_obj_add_event_cb(ok, pwd_connect_cb, LV_EVENT_CLICKED, ctx);

    lv_obj_t* kb = ui_keyboard_create(modal);
    lv_obj_set_size(kb, LV_PCT(100), 360);
    lv_obj_align(kb, LV_ALIGN_BOTTOM_MID, 0, 0);
    lv_keyboard_set_textarea(kb, ta);
}

// ── 扫描列表 ──────────────────────────────────────────────────
static void ap_clicked_cb(lv_event_t* e)
{
    wifi::ApInfo* ap = (wifi::ApInfo*)lv_event_get_user_data(e);
    if (ap->secure) {
        open_password_modal(ap->ssid);
    } else {
        wifi::connect(ap->ssid, "");
    }
}

static void list_render(wifi_ui_t* ui)
{
    lv_obj_clean(ui->list);

    static wifi::ApInfo aps[16];   // 行回调引用，需在页面生命周期内有效
    int n = wifi::scan_results(aps, 16);
    if (n == 0) {
        lv_obj_t* empty = lv_label_create(ui->list);
        lv_label_set_text(empty, "未发现网络");
        lv_obj_set_style_text_font(empty, &font_cn_20, 0);
        lv_obj_set_style_text_color(empty, lv_color_hex(COL_MUTED), 0);
        return;
    }
    for (int i = 0; i < n; i++) {
        lv_obj_t* row = lv_obj_create(ui->list);
        lv_obj_set_size(row, LV_PCT(100), 76);
        lv_obj_set_style_bg_color(row, lv_color_hex(COL_BG), 0);
        lv_obj_set_style_bg_color(row, lv_color_hex(COL_CARD_HI), LV_STATE_PRESSED);
        lv_obj_set_style_border_width(row, 0, 0);
        lv_obj_set_style_radius(row, 12, 0);
        lv_obj_set_style_pad_hor(row, 20, 0);
        lv_obj_remove_flag(row, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_add_flag(row, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_add_event_cb(row, ap_clicked_cb, LV_EVENT_CLICKED, &aps[i]);

        lv_obj_t* name = lv_label_create(row);
        lv_label_set_text(name, aps[i].ssid);
        lv_obj_set_style_text_font(name, &font_cn_20, 0);
        lv_obj_set_style_text_color(name, lv_color_hex(COL_TEXT), 0);
        lv_obj_align(name, LV_ALIGN_LEFT_MID, 0, 0);

        lv_obj_t* meta = lv_label_create(row);
        lv_label_set_text_fmt(meta, "%s  %d dBm  %s",
                              aps[i].secure ? LV_SYMBOL_EYE_CLOSE : "",
                              aps[i].rssi, LV_SYMBOL_WIFI);
        lv_obj_set_style_text_font(meta, &lv_font_montserrat_20, 0);
        lv_obj_set_style_text_color(meta, lv_color_hex(COL_MUTED), 0);
        lv_obj_align(meta, LV_ALIGN_RIGHT_MID, 0, 0);
    }
}

// ── 已保存网络（记忆列表）────────────────────────────────────
static wifi::SavedNet s_saved[wifi::MAX_SAVED];  // 行回调引用，页面生命周期内有效

// 列表状态签名：条数 + 各 SSID + 当前项 + 连接状态变化时签名改变 → 触发重绘
static uint32_t saved_signature(void)
{
    wifi::SavedNet tmp[wifi::MAX_SAVED];
    int n = wifi::saved_list(tmp, wifi::MAX_SAVED);
    wifi::Status st = wifi::status();

    uint32_t h = 2166136261u;
    auto mix = [&](const char* s) {
        for (; *s; ++s) { h ^= (uint8_t)*s; h *= 16777619u; }
        h ^= 0xffu; h *= 16777619u;
    };
    char buf[16];
    snprintf(buf, sizeof(buf), "%d#%d#", n, (int)st.state);
    mix(buf);
    mix(st.ssid);
    for (int i = 0; i < n; i++) {
        mix(tmp[i].ssid);
        h ^= tmp[i].current ? 1u : 0u; h *= 16777619u;
    }
    return h;
}

static void saved_connect_cb(lv_event_t* e)
{
    wifi::SavedNet* net = (wifi::SavedNet*)lv_event_get_user_data(e);
    wifi::connect_saved(net->ssid);
}

static void saved_del_cb(lv_event_t* e)
{
    wifi::SavedNet* net = (wifi::SavedNet*)lv_event_get_user_data(e);
    wifi::forget(net->ssid);   // 列表会在下一次 tick 检测到签名变化并重绘
}

static void saved_render(wifi_ui_t* ui)
{
    lv_obj_clean(ui->saved_list);

    int n = wifi::saved_list(s_saved, wifi::MAX_SAVED);
    if (n == 0) {
        lv_obj_t* empty = lv_label_create(ui->saved_list);
        lv_label_set_text(empty, "暂无保存的网络");
        lv_obj_set_style_text_font(empty, &font_cn_20, 0);
        lv_obj_set_style_text_color(empty, lv_color_hex(COL_MUTED), 0);
        return;
    }
    wifi::Status st = wifi::status();
    for (int i = 0; i < n; i++) {
        lv_obj_t* row = lv_obj_create(ui->saved_list);
        lv_obj_set_size(row, LV_PCT(100), 72);
        lv_obj_set_style_bg_color(row, lv_color_hex(COL_BG), 0);
        lv_obj_set_style_bg_color(row, lv_color_hex(COL_CARD_HI), LV_STATE_PRESSED);
        lv_obj_set_style_border_width(row, 0, 0);
        lv_obj_set_style_radius(row, 12, 0);
        lv_obj_set_style_pad_hor(row, 20, 0);
        lv_obj_remove_flag(row, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_add_flag(row, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_add_event_cb(row, saved_connect_cb, LV_EVENT_CLICKED, &s_saved[i]);

        lv_obj_t* name = lv_label_create(row);
        lv_label_set_text(name, s_saved[i].ssid);
        lv_obj_set_style_text_font(name, &font_cn_20, 0);
        lv_obj_set_style_text_color(name, lv_color_hex(COL_TEXT), 0);
        lv_obj_align(name, LV_ALIGN_LEFT_MID, 0, 0);

        // 当前项标注（已连接/连接中；别的状态不标）
        if (s_saved[i].current &&
            (st.state == wifi::State::Connected || st.state == wifi::State::Connecting)) {
            lv_obj_t* mark = lv_label_create(row);
            lv_label_set_text(mark, st.state == wifi::State::Connected ? "已连接" : "连接中…");
            lv_obj_set_style_text_font(mark, &font_cn_20, 0);
            lv_obj_set_style_text_color(mark, lv_color_hex(
                st.state == wifi::State::Connected ? COL_GOOD : COL_WARN), 0);
            lv_obj_align(mark, LV_ALIGN_RIGHT_MID, -84, 0);
        }

        // 删除按钮（垃圾桶图标 → Montserrat 防豆腐）
        lv_obj_t* del = lv_button_create(row);
        lv_obj_set_size(del, 64, 52);
        lv_obj_align(del, LV_ALIGN_RIGHT_MID, 0, 0);
        lv_obj_set_style_bg_color(del, lv_color_hex(COL_CARD_HI), 0);
        lv_obj_t* del_t = lv_label_create(del);
        lv_label_set_text(del_t, LV_SYMBOL_TRASH);
        lv_obj_set_style_text_font(del_t, &lv_font_montserrat_20, 0);
        lv_obj_set_style_text_color(del_t, lv_color_hex(COL_TEXT), 0);
        lv_obj_center(del_t);
        lv_obj_add_event_cb(del, saved_del_cb, LV_EVENT_CLICKED, &s_saved[i]);
    }
}

// ── 事件 / 定时刷新 ───────────────────────────────────────────
static void forget_cb(lv_event_t* e)
{
    (void)e;
    wifi::forget();
}

static void scan_cb(lv_event_t* e)
{
    wifi_ui_t* ui = (wifi_ui_t*)lv_event_get_user_data(e);
    if (wifi::scan_start()) {
        ui->scan_pending = true;
        lv_label_set_text(ui->scan_btn_lbl, "扫描中…");
    }
}

static void wifi_tick(lv_timer_t* timer)
{
    wifi_ui_t* ui = (wifi_ui_t*)lv_timer_get_user_data(timer);
    wifi::Status s = wifi::status();
    ui_set_checked(ui->sw, wifi::enabled());    // 调试命令也能开关 WiFi

    // ⚠️ 周期 tick 里一律"变了才写"：lv_label_set_text 同文本也会无条件 invalidate，
    // 滚动中每一次无谓失效都会让滚动位移复用放弃加速、退回整屏重绘直写扫出中的
    // 前台帧缓冲 → 每秒闪一次左右错位的撕裂（2026-07-30 真机目击的根因）。
    const int cur_state = (int)s.state;
    ui_label_set_text_if_changed(ui->state_lbl, state_name(s.state));
    if (ui->last_state != cur_state) {   // 同值 set_style 也会无条件失效 → 变了才设
        ui->last_state = cur_state;
        uint32_t col = (s.state == wifi::State::Connected) ? COL_GOOD
                     : (s.state == wifi::State::Connecting || s.state == wifi::State::Starting) ? COL_WARN
                     : (s.state == wifi::State::Fault) ? COL_BAD : COL_MUTED;
        lv_obj_set_style_text_color(ui->state_lbl, lv_color_hex(col), 0);
    }

    ui_label_set_text_if_changed(ui->ssid_lbl, s.ssid[0] ? s.ssid : "—");
    if (s.state == wifi::State::Connected) {
        char buf[48];
        snprintf(buf, sizeof(buf), "%s  %d dBm", s.ip, s.rssi);  // 西文字体标签只放 ASCII
        ui_label_set_text_if_changed(ui->ip_lbl, buf);
    } else {
        ui_label_set_text_if_changed(ui->ip_lbl, "-");
    }

    // 记忆列表变化（新增/删除/当前项/连接状态）→ 重绘
    uint32_t sig = saved_signature();
    if (sig != ui->saved_sig) {
        ui->saved_sig = sig;
        saved_render(ui);
    }

    // 扫描完成 → 渲染列表
    if (ui->scan_pending && !wifi::scan_busy()) {
        ui->scan_pending = false;
        lv_label_set_text(ui->scan_btn_lbl, "扫描");
        list_render(ui);
    }
}

static void wifi_delete_cb(lv_event_t* e)
{
    lv_timer_t* timer = (lv_timer_t*)lv_event_get_user_data(e);
    lv_free(lv_timer_get_user_data(timer));
    lv_timer_delete(timer);
}

// 返回键回哪一页：这一页是从哪一页点进来的（设置页，或者主页面上没联网时点状态）。
// 息屏后亮屏、调试命令重开这一页时不变。
static ui_page_fn s_back = ui_open_settings;

void ui_open_wifi(void)
{
    const ui_page_fn from = ui_nav_current();
    if (from == ui_open_main || from == ui_open_settings) s_back = from;
    ui_nav_record(ui_open_wifi);          // 息屏后恢复用
    ui_fade_out();
    lv_obj_t* scr = lv_obj_create(nullptr);
    lv_obj_t* content = ui_page_frame(scr, "WiFi", true);
    ui_set_back_action(s_back);

    wifi_ui_t* ui = (wifi_ui_t*)lv_malloc(sizeof(wifi_ui_t));
    lv_memzero(ui, sizeof(wifi_ui_t));
    ui->last_state = -1;   // 未知：首个 tick 必设一次颜色

    lv_obj_set_flex_flow(content, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(content, 20, 0);
    lv_obj_add_flag(content, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_scroll_dir(content, LV_DIR_VER);
    ui_scroll_accel(content);   // 滚动位移复用：整宽纵向列表（状态卡+已存网络+扫描结果）

    // ── 状态卡：当前连接 ─────────────────────────────────────
    lv_obj_t* card = ui_card(content);
    lv_obj_set_size(card, LV_PCT(100), LV_SIZE_CONTENT);
    lv_obj_set_style_pad_all(card, 24, 0);

    lv_obj_t* title = lv_label_create(card);
    lv_label_set_text(title, "WiFi");
    lv_obj_set_style_text_font(title, &font_cn_28, 0);
    lv_obj_set_style_text_color(title, lv_color_hex(COL_TEXT), 0);
    lv_obj_align(title, LV_ALIGN_TOP_LEFT, 0, 0);

    // 开关：在卡片右上角，和标题同一行
    ui->sw = lv_switch_create(card);
    lv_obj_set_size(ui->sw, 72, 38);
    lv_obj_align(ui->sw, LV_ALIGN_TOP_RIGHT, 0, 0);
    ui_set_checked(ui->sw, wifi::enabled());
    lv_obj_add_event_cb(ui->sw, [](lv_event_t* e) {
        wifi::set_enabled(lv_obj_has_state((lv_obj_t*)lv_event_get_target(e), LV_STATE_CHECKED));
    }, LV_EVENT_VALUE_CHANGED, nullptr);

    ui->state_lbl = lv_label_create(card);
    lv_label_set_text(ui->state_lbl, "—");
    lv_obj_set_style_text_font(ui->state_lbl, &font_cn_20, 0);
    lv_obj_set_style_text_color(ui->state_lbl, lv_color_hex(COL_MUTED), 0);
    lv_obj_align(ui->state_lbl, LV_ALIGN_TOP_LEFT, 0, 52);
    // 无线模块故障时点状态文字 → 重启确认框；别的状态点了没反应
    lv_obj_add_flag(ui->state_lbl, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_ext_click_area(ui->state_lbl, 12);
    lv_obj_add_event_cb(ui->state_lbl, [](lv_event_t* e) {
        (void)e;
        if (wifi::status().state == wifi::State::Fault) ui_confirm_restart();
    }, LV_EVENT_CLICKED, nullptr);

    ui->ssid_lbl = lv_label_create(card);
    lv_label_set_text(ui->ssid_lbl, "—");
    // SSID 可能含中文 → 用中文字体（西文字体遇全角/中文变豆腐）
    lv_obj_set_style_text_font(ui->ssid_lbl, &font_cn_28, 0);
    lv_obj_set_style_text_color(ui->ssid_lbl, lv_color_hex(COL_TEXT), 0);
    lv_obj_align(ui->ssid_lbl, LV_ALIGN_TOP_LEFT, 0, 92);

    ui->ip_lbl = lv_label_create(card);
    lv_label_set_text(ui->ip_lbl, "-");
    lv_obj_set_style_text_font(ui->ip_lbl, &lv_font_montserrat_20, 0);
    lv_obj_set_style_text_color(ui->ip_lbl, lv_color_hex(COL_MUTED), 0);
    lv_obj_align(ui->ip_lbl, LV_ALIGN_TOP_LEFT, 0, 140);

    ui->forget_btn = lv_button_create(card);
    lv_obj_set_size(ui->forget_btn, 200, 56);
    lv_obj_align(ui->forget_btn, LV_ALIGN_BOTTOM_RIGHT, 0, 0);
    lv_obj_set_style_bg_color(ui->forget_btn, lv_color_hex(COL_CARD_HI), 0);
    lv_obj_t* fg_t = lv_label_create(ui->forget_btn);
    lv_label_set_text(fg_t, "忘记网络");
    lv_obj_set_style_text_font(fg_t, &font_cn_20, 0);
    lv_obj_center(fg_t);
    lv_obj_add_event_cb(ui->forget_btn, forget_cb, LV_EVENT_CLICKED, nullptr);
    lv_obj_set_style_pad_bottom(card, 96, 0);

    // ── 已保存网络卡：记忆列表（点击切换连接 / 删除单条）──────
    lv_obj_t* vcard = ui_card(content);
    lv_obj_set_size(vcard, LV_PCT(100), LV_SIZE_CONTENT);
    lv_obj_set_style_pad_all(vcard, 24, 0);
    lv_obj_set_flex_flow(vcard, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(vcard, 14, 0);
    lv_obj_remove_flag(vcard, LV_OBJ_FLAG_SCROLLABLE);

    lv_obj_t* v_title = lv_label_create(vcard);
    lv_label_set_text(v_title, "已保存的网络");
    lv_obj_set_style_text_font(v_title, &font_cn_20, 0);
    lv_obj_set_style_text_color(v_title, lv_color_hex(COL_MUTED), 0);

    ui->saved_list = lv_obj_create(vcard);
    lv_obj_set_size(ui->saved_list, LV_PCT(100), LV_SIZE_CONTENT);
    lv_obj_set_style_bg_opa(ui->saved_list, LV_OPA_0, 0);
    lv_obj_set_style_border_width(ui->saved_list, 0, 0);
    lv_obj_set_style_pad_all(ui->saved_list, 0, 0);
    lv_obj_set_flex_flow(ui->saved_list, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(ui->saved_list, 10, 0);
    lv_obj_remove_flag(ui->saved_list, LV_OBJ_FLAG_SCROLLABLE);

    saved_render(ui);
    ui->saved_sig = saved_signature();

    // ── 扫描卡：按钮 + 列表 ──────────────────────────────────
    lv_obj_t* scard = ui_card(content);
    lv_obj_set_size(scard, LV_PCT(100), LV_SIZE_CONTENT);
    lv_obj_set_style_pad_all(scard, 24, 0);
    lv_obj_set_flex_flow(scard, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(scard, 14, 0);
    lv_obj_remove_flag(scard, LV_OBJ_FLAG_SCROLLABLE);

    lv_obj_t* shead = lv_obj_create(scard);
    lv_obj_set_size(shead, LV_PCT(100), 60);
    lv_obj_set_style_bg_opa(shead, LV_OPA_0, 0);
    lv_obj_set_style_border_width(shead, 0, 0);
    lv_obj_set_style_pad_all(shead, 0, 0);
    lv_obj_remove_flag(shead, LV_OBJ_FLAG_SCROLLABLE);

    lv_obj_t* s_title = lv_label_create(shead);
    lv_label_set_text(s_title, "附近网络");
    lv_obj_set_style_text_font(s_title, &font_cn_20, 0);
    lv_obj_set_style_text_color(s_title, lv_color_hex(COL_MUTED), 0);
    lv_obj_align(s_title, LV_ALIGN_LEFT_MID, 0, 0);

    ui->scan_btn = lv_button_create(shead);
    lv_obj_set_size(ui->scan_btn, 160, 56);
    lv_obj_align(ui->scan_btn, LV_ALIGN_RIGHT_MID, 0, 0);
    lv_obj_set_style_bg_color(ui->scan_btn, lv_color_hex(COL_ACCENT), 0);
    ui->scan_btn_lbl = lv_label_create(ui->scan_btn);
    lv_label_set_text(ui->scan_btn_lbl, "扫描");
    lv_obj_set_style_text_font(ui->scan_btn_lbl, &font_cn_20, 0);
    lv_obj_center(ui->scan_btn_lbl);
    lv_obj_add_event_cb(ui->scan_btn, scan_cb, LV_EVENT_CLICKED, ui);

    ui->list = lv_obj_create(scard);
    lv_obj_set_size(ui->list, LV_PCT(100), LV_SIZE_CONTENT);
    lv_obj_set_style_bg_opa(ui->list, LV_OPA_0, 0);
    lv_obj_set_style_border_width(ui->list, 0, 0);
    lv_obj_set_style_pad_all(ui->list, 0, 0);
    lv_obj_set_flex_flow(ui->list, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(ui->list, 10, 0);
    lv_obj_remove_flag(ui->list, LV_OBJ_FLAG_SCROLLABLE);

    lv_obj_t* hint = lv_label_create(ui->list);
    lv_label_set_text(hint, "点击右上角扫描，查找附近网络");
    lv_obj_set_style_text_font(hint, &font_cn_20, 0);
    lv_obj_set_style_text_color(hint, lv_color_hex(COL_MUTED), 0);

    lv_timer_t* timer = lv_timer_create(wifi_tick, 1000, ui);
    wifi_tick(timer);
    lv_obj_add_event_cb(scr, wifi_delete_cb, LV_EVENT_DELETE, timer);

    ui_fade_in(scr);
}
