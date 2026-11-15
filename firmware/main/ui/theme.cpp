#include "theme.h"
#include "ui.h"
#include "../board.hpp"
#include "../net/wifi.hpp"

#include <cstdio>
#include <cstring>
#include <esp_system.h>

void theme_init(lv_display_t* disp)
{
    lv_theme_t* th = lv_theme_default_init(disp, lv_color_hex(COL_ACCENT),
                                           lv_color_hex(0xF472B6), true /*dark*/,
                                           &font_cn_20);
    lv_display_set_theme(disp, th);
}

lv_obj_t* ui_card(lv_obj_t* parent)
{
    lv_obj_t* card = lv_obj_create(parent);
    lv_obj_set_style_bg_color(card, lv_color_hex(COL_CARD), 0);
    lv_obj_set_style_border_color(card, lv_color_hex(COL_BORDER), 0);
    lv_obj_set_style_border_width(card, 1, 0);
    lv_obj_set_style_radius(card, 18, 0);
    lv_obj_set_style_shadow_width(card, 0, 0);
    lv_obj_remove_flag(card, LV_OBJ_FLAG_SCROLLABLE);
    return card;
}

// ── 设置行：左标题 + 右控件 ───────────
lv_obj_t* ui_setting_row(lv_obj_t* parent, const char* title)
{
    lv_obj_t* row = ui_card(parent);
    lv_obj_set_size(row, LV_PCT(100), 88);
    lv_obj_set_style_pad_hor(row, 28, 0);

    lv_obj_t* t = lv_label_create(row);
    lv_label_set_text(t, title);
    lv_obj_set_style_text_font(t, &font_cn_28, 0);
    lv_obj_set_style_text_color(t, lv_color_hex(COL_TEXT), 0);
    lv_obj_align(t, LV_ALIGN_LEFT_MID, 0, 0);
    return row;
}

// 设置页里一组的标题：小字、次要色，左边和卡片里的标题对齐
lv_obj_t* ui_group_title(lv_obj_t* parent, const char* text)
{
    lv_obj_t* t = lv_label_create(parent);
    lv_label_set_text(t, text);
    lv_obj_set_style_text_font(t, &font_cn_20, 0);
    lv_obj_set_style_text_color(t, lv_color_hex(COL_MUTED), 0);
    lv_obj_set_style_pad_left(t, 28, 0);
    lv_obj_set_style_pad_top(t, 12, 0);
    return t;
}

// 通用下拉框（中文列表 + Montserrat 指示箭头），按当前值选中
lv_obj_t* ui_dropdown(lv_obj_t* row, const char* opts,
                      const uint32_t* vals, int n, uint32_t cur,
                      lv_event_cb_t cb)
{
    lv_obj_t* dd = lv_dropdown_create(row);
    lv_dropdown_set_options(dd, opts);
    lv_obj_set_width(dd, 280);
    lv_obj_align(dd, LV_ALIGN_RIGHT_MID, 0, 0);
    lv_obj_set_style_text_font(dd, &font_cn_20, 0);
    // 下拉箭头是 FontAwesome 字形，中文字体没有 → 指示器单独用 Montserrat
    lv_obj_set_style_text_font(dd, &lv_font_montserrat_20, LV_PART_INDICATOR);
    lv_obj_set_style_text_font(lv_dropdown_get_list(dd), &font_cn_20, 0);
    for (int i = 0; i < n; i++) {
        if (vals[i] == cur) { lv_dropdown_set_selected(dd, i); break; }
    }
    lv_obj_add_event_cb(dd, cb, LV_EVENT_VALUE_CHANGED, nullptr);
    return dd;
}

lv_obj_t* ui_row_switch(lv_obj_t* row)
{
    lv_obj_t* sw = lv_switch_create(row);
    lv_obj_set_size(sw, 72, 38);
    lv_obj_align(sw, LV_ALIGN_RIGHT_MID, 0, 0);
    return sw;
}

// 带箭头的行：箭头记在值的 label 的 user_data 里（ui_row_set_link 靠它找到箭头）
lv_obj_t* ui_row_value(lv_obj_t* row, bool link)
{
    lv_obj_t* l = lv_label_create(row);
    lv_label_set_text(l, "");
    lv_obj_set_style_text_font(l, &font_cn_20, 0);      // 中文字库：值可能是网络名、电脑名
    lv_obj_set_style_text_color(l, lv_color_hex(COL_TEXT), 0);
    lv_obj_align(l, LV_ALIGN_RIGHT_MID, 0, 0);
    if (link) {
        lv_obj_t* arrow = lv_label_create(row);
        lv_label_set_text(arrow, LV_SYMBOL_RIGHT);
        lv_obj_set_style_text_font(arrow, &lv_font_montserrat_20, 0);
        lv_obj_set_style_text_color(arrow, lv_color_hex(COL_MUTED), 0);
        lv_obj_align(arrow, LV_ALIGN_RIGHT_MID, 0, 0);
        lv_obj_add_flag(arrow, LV_OBJ_FLAG_HIDDEN);     // 先按「不能点」摆好，再打开
        lv_obj_set_user_data(l, arrow);
        lv_obj_remove_flag(row, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_set_style_bg_color(row, lv_color_hex(COL_CARD_HI), LV_STATE_PRESSED);
        ui_row_set_link(l, true);
    }
    return l;
}

void ui_row_set_link(lv_obj_t* value, bool on)
{
    lv_obj_t* arrow = (lv_obj_t*)lv_obj_get_user_data(value);
    if (!arrow || lv_obj_has_flag(arrow, LV_OBJ_FLAG_HIDDEN) == !on) return;
    lv_obj_t* row = lv_obj_get_parent(value);
    lv_obj_align(value, LV_ALIGN_RIGHT_MID, on ? -32 : 0, 0);      // 给箭头让出位置
    if (on) {
        lv_obj_remove_flag(arrow, LV_OBJ_FLAG_HIDDEN);
        lv_obj_add_flag(row, LV_OBJ_FLAG_CLICKABLE);
    } else {
        lv_obj_add_flag(arrow, LV_OBJ_FLAG_HIDDEN);
        lv_obj_remove_flag(row, LV_OBJ_FLAG_CLICKABLE);
    }
}

// ── 分段选择 ──────────────────────────────────────────────────
// 用的是按钮矩阵的「只能有一个选中」：点别的段，选中跟着过去；点已经选中的那一段，它还是选中的。
// 按钮矩阵的 VALUE_CHANGED 不等于「选中换了」：默认在按下时就发（选中要到松手才换，回调里读到的还是旧的），按住不放还会连发，
// 点已经选中的那一段也发。所以每一段都设成松手时才发、不连发；再由这里比一下选中的和上一次报给调用方的是不是同一段，换了才转给调用方。
// 上一次报的是第几段记在对象的 user_data 里（加 1 存，0 = 没有选中的）。
static void segmented_changed_cb(lv_event_t* e)
{
    lv_obj_t* seg = lv_event_get_target_obj(e);
    const int cur = ui_segmented_get(seg);
    if (cur + 1 == (int)(intptr_t)lv_obj_get_user_data(seg)) return;
    lv_obj_set_user_data(seg, (void*)(intptr_t)(cur + 1));
    ((lv_event_cb_t)lv_event_get_user_data(e))(e);
}

lv_obj_t* ui_segmented(lv_obj_t* parent, const char* const* map, int cur, lv_event_cb_t cb)
{
    lv_obj_t* seg = lv_buttonmatrix_create(parent);
    lv_buttonmatrix_set_map(seg, map);
    lv_buttonmatrix_set_button_ctrl_all(seg, (lv_buttonmatrix_ctrl_t)(LV_BUTTONMATRIX_CTRL_CHECKABLE | LV_BUTTONMATRIX_CTRL_CLICK_TRIG
                                                                     | LV_BUTTONMATRIX_CTRL_NO_REPEAT));
    lv_buttonmatrix_set_one_checked(seg, true);
    if (cur >= 0) lv_buttonmatrix_set_button_ctrl(seg, cur, LV_BUTTONMATRIX_CTRL_CHECKED);
    lv_obj_set_user_data(seg, (void*)(intptr_t)(cur >= 0 ? cur + 1 : 0));
    // 外框和设置行的卡片一个样；里面每一段平时没有底色，选中的是主题色底、深色字
    lv_obj_set_style_bg_color(seg, lv_color_hex(COL_CARD), 0);
    lv_obj_set_style_border_color(seg, lv_color_hex(COL_BORDER), 0);
    lv_obj_set_style_border_width(seg, 1, 0);
    lv_obj_set_style_radius(seg, 18, 0);
    lv_obj_set_style_shadow_width(seg, 0, 0);
    lv_obj_set_style_pad_all(seg, 8, 0);
    lv_obj_set_style_pad_column(seg, 6, 0);
    lv_obj_set_style_text_font(seg, &font_cn_28, 0);
    lv_obj_set_style_bg_opa(seg, LV_OPA_0, LV_PART_ITEMS);
    lv_obj_set_style_border_width(seg, 0, LV_PART_ITEMS);
    lv_obj_set_style_shadow_width(seg, 0, LV_PART_ITEMS);
    lv_obj_set_style_radius(seg, 12, LV_PART_ITEMS);
    lv_obj_set_style_text_color(seg, lv_color_hex(COL_TEXT), LV_PART_ITEMS);
    const lv_style_selector_t pressed = (lv_style_selector_t)LV_PART_ITEMS | (lv_style_selector_t)LV_STATE_PRESSED;
    const lv_style_selector_t checked = (lv_style_selector_t)LV_PART_ITEMS | (lv_style_selector_t)LV_STATE_CHECKED;
    lv_obj_set_style_bg_color(seg, lv_color_hex(COL_CARD_HI), pressed);
    lv_obj_set_style_bg_opa(seg, LV_OPA_COVER, pressed);
    lv_obj_set_style_bg_color(seg, lv_color_hex(COL_ACCENT), checked);
    lv_obj_set_style_bg_opa(seg, LV_OPA_COVER, checked);
    lv_obj_set_style_text_color(seg, lv_color_hex(COL_BG), checked);
    // 按住已经选中的那一段：两种状态都成立时 LVGL 取「按下」那一条的底色，深色字就压在深灰底上了。单给这种情况定底色
    lv_obj_set_style_bg_color(seg, lv_color_hex(COL_ACCENT), checked | (lv_style_selector_t)LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(seg, LV_OPA_COVER, checked | (lv_style_selector_t)LV_STATE_PRESSED);
    lv_obj_add_event_cb(seg, segmented_changed_cb, LV_EVENT_VALUE_CHANGED, (void*)cb);
    return seg;
}

int ui_segmented_get(lv_obj_t* seg)
{
    const char* const* map = lv_buttonmatrix_get_map(seg);
    for (int i = 0; map[i][0]; i++) {
        if (lv_buttonmatrix_has_button_ctrl(seg, i, LV_BUTTONMATRIX_CTRL_CHECKED)) return i;
    }
    return -1;
}

void ui_segmented_set(lv_obj_t* seg, int idx)
{
    if (ui_segmented_get(seg) == idx) return;
    lv_buttonmatrix_set_button_ctrl(seg, idx, LV_BUTTONMATRIX_CTRL_CHECKED);    // 「只能有一个选中」：原来那一段跟着取消
    lv_obj_set_user_data(seg, (void*)(intptr_t)(idx + 1));
}

// ── 周期刷新里用的「变了才动」────────────────────────────────
void ui_set_checked(lv_obj_t* o, bool on)
{
    if (lv_obj_has_state(o, LV_STATE_CHECKED) == on) return;
    if (on) lv_obj_add_state(o, LV_STATE_CHECKED);
    else    lv_obj_remove_state(o, LV_STATE_CHECKED);
}

// lv_obj_remove_flag(HIDDEN) 对本就可见的对象也会无条件失效（LVGL9 lv_obj.c 实测），
// 1Hz tick 里裸调会把顶栏图标每秒白重绘一遍。
void ui_set_hidden(lv_obj_t* o, bool hide)
{
    if (lv_obj_has_flag(o, LV_OBJ_FLAG_HIDDEN) == hide) return;
    if (hide) lv_obj_add_flag(o, LV_OBJ_FLAG_HIDDEN);
    else      lv_obj_remove_flag(o, LV_OBJ_FLAG_HIDDEN);
}

void ui_set_disabled(lv_obj_t* o, bool off)
{
    if (lv_obj_has_state(o, LV_STATE_DISABLED) == off) return;
    if (off) lv_obj_add_state(o, LV_STATE_DISABLED);
    else     lv_obj_remove_state(o, LV_STATE_DISABLED);
}

void ui_label_set(lv_obj_t* label, const char* text, uint32_t color)
{
    ui_label_set_text_if_changed(label, text);
    if (!lv_color_eq(lv_obj_get_style_text_color(label, 0), lv_color_hex(color))) lv_obj_set_style_text_color(label, lv_color_hex(color), 0);
}

// ── 状态簇：WiFi + 电量，手机状态栏样式 ─────────
// 各页顶栏共用同一组件。⚠️ 容器用固定宽度，避免 flex+SIZE_CONTENT+align
// 漏算首个子元素被裁的坑（见经验库 tab5-lvgl-pitfalls 第 7 条）。
struct status_cluster {
    lv_obj_t* grp;
    lv_obj_t* wifi_icon;     // WiFi 已连接指示 / 无线模块故障的红色警告（其余不显示）
    int8_t    last_wifi;     // 上次画的 WiFi 图标：0=不显示 1=已连接 2=故障
    lv_obj_t* bat_label;
    int32_t   last_bat_col;  // 上次电量颜色（-1=未知；同值 set_style 也会失效）
};

// 文本没变就不动 label：lv_label_set_text 同文本也会无条件 invalidate + free/malloc，
// 在"任何脏区都要过一遍 PPA 旋转"的本机上，周期 timer 里的无条件 set_text 是被放大的
// 反模式（顶栏 1Hz 曾因此空转重绘）。周期刷新的 label 一律走这个。
void ui_label_set_text_if_changed(lv_obj_t* label, const char* text)
{
    const char* cur = lv_label_get_text(label);
    if (cur && text && strcmp(cur, text) == 0) return;
    lv_label_set_text(label, text);
}

void ui_status_cluster_update(status_cluster_t* c)
{
    // WiFi 已连接（拿到 IP）显示灰白 WiFi 图标；无线模块故障显示红色警告图标（每个页面都看得见）；
    // 其余不显示。变了才动：同值 set_text / set_style 也会失效重绘。
    const wifi::State ws = wifi::status().state;
    const int8_t wifi_mode = ws == wifi::State::Connected ? 1 : ws == wifi::State::Fault ? 2 : 0;
    if (c->last_wifi != wifi_mode) {
        c->last_wifi = wifi_mode;
        if (wifi_mode) {
            lv_label_set_text(c->wifi_icon, wifi_mode == 2 ? LV_SYMBOL_WARNING : LV_SYMBOL_WIFI);
            lv_obj_set_style_text_color(c->wifi_icon,
                                        lv_color_hex(wifi_mode == 2 ? COL_BAD : COL_MUTED), 0);
        }
        ui_set_hidden(c->wifi_icon, wifi_mode == 0);
    }

    board::PowerData p = board::readPower();
    if (!p.valid) return;   // 读失败：电池图标保持上一次的显示
    if (!p.present) {
        // 无电池：电压在 8.4↔4.2V 跳变会让百分比在 0↔100 闪烁，固定显示 N/A
        ui_label_set_text_if_changed(c->bat_label, LV_SYMBOL_BATTERY_EMPTY " N/A");
        if (c->last_bat_col != (int32_t)COL_BAD) {
            c->last_bat_col = (int32_t)COL_BAD;
            lv_obj_set_style_text_color(c->bat_label, lv_color_hex(COL_BAD), 0);
        }
        return;
    }
    // 充电或充满都用充电图标（满充后系统缓慢放电，避免误显示为放电）
    const char* sym = (p.charging || p.full) ? LV_SYMBOL_CHARGE
                      : p.percent >= 80   ? LV_SYMBOL_BATTERY_FULL
                      : p.percent >= 55   ? LV_SYMBOL_BATTERY_3
                      : p.percent >= 30   ? LV_SYMBOL_BATTERY_2
                      : p.percent >= 10   ? LV_SYMBOL_BATTERY_1
                                          : LV_SYMBOL_BATTERY_EMPTY;
    char bat[24];
    snprintf(bat, sizeof(bat), "%s %d%%", sym, p.percent);
    ui_label_set_text_if_changed(c->bat_label, bat);
    int32_t col = (int32_t)(p.percent < 15 && !p.charging ? COL_BAD : COL_MUTED);
    if (c->last_bat_col != col) {   // 同值 set_style 也会失效 → 变了才设
        c->last_bat_col = col;
        lv_obj_set_style_text_color(c->bat_label, lv_color_hex((uint32_t)col), 0);
    }
}

static void status_cluster_delete_cb(lv_event_t* e)
{
    lv_free(lv_event_get_user_data(e));
}

status_cluster_t* ui_status_cluster(lv_obj_t* parent)
{
    status_cluster_t* c = (status_cluster_t*)lv_malloc(sizeof(status_cluster_t));
    lv_memzero(c, sizeof(*c));
    c->last_bat_col = -1;

    lv_obj_t* grp = lv_obj_create(parent);
    c->grp = grp;
    // 固定宽度（避坑）；高度容纳并垂直居中。
    // ⚠️ 宽度必须容得下**全亮**时的总和，否则 flex ALIGN_END 会把最左那个裁掉。
    //    满载：WiFi20 + 电量62 + 14 列距 = 96 → 取 260 留余量。
    //    以后再往状态簇里加图标，先把这笔账重算一遍。
    lv_obj_set_size(grp, 260, 48);
    lv_obj_set_style_bg_opa(grp, LV_OPA_0, 0);
    lv_obj_set_style_border_width(grp, 0, 0);
    lv_obj_set_style_pad_all(grp, 0, 0);
    lv_obj_set_style_pad_column(grp, 14, 0);
    lv_obj_set_flex_flow(grp, LV_FLEX_FLOW_ROW);
    lv_obj_set_flex_align(grp, LV_FLEX_ALIGN_END, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_remove_flag(grp, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_event_cb(grp, status_cluster_delete_cb, LV_EVENT_DELETE, c);

    // WiFi（默认隐藏，灰白；连上才显示）
    c->wifi_icon = lv_label_create(grp);
    lv_label_set_text(c->wifi_icon, LV_SYMBOL_WIFI);
    lv_obj_set_style_text_font(c->wifi_icon, &lv_font_montserrat_16, 0);   // 比电池略小一点
    lv_obj_set_style_text_color(c->wifi_icon, lv_color_hex(COL_MUTED), 0);
    lv_obj_add_flag(c->wifi_icon, LV_OBJ_FLAG_HIDDEN);

    // 电量
    c->bat_label = lv_label_create(grp);
    lv_obj_set_style_text_font(c->bat_label, &lv_font_montserrat_20, 0);
    lv_obj_set_style_text_color(c->bat_label, lv_color_hex(COL_MUTED), 0);
    lv_label_set_text(c->bat_label, "");

    ui_status_cluster_update(c);
    return c;
}

lv_obj_t* ui_status_cluster_obj(status_cluster_t* c) { return c->grp; }

// ── 顶栏（返回键 + 标题 + 状态簇），状态簇 1s 自刷新 ─────────────────
static void topbar_tick(lv_timer_t* timer)
{
    ui_status_cluster_update((status_cluster_t*)lv_timer_get_user_data(timer));
}

static void topbar_delete_cb(lv_event_t* e)
{
    lv_timer_delete((lv_timer_t*)lv_event_get_user_data(e));   // 状态簇自己随容器释放
}

static void back_clicked_cb(lv_event_t* e)
{
    (void)e;
    ui_nav_back();   // 回上一级（默认主页面，见 ui_page_frame）
}

// ── 安卓式侧滑返回：从屏幕左/右边缘横划 ──────────────────────
typedef struct {
    int32_t start_x;
    bool    fired;
    bool    from_left;
} edge_swipe_t;

static void edge_swipe_cb(lv_event_t* e)
{
    edge_swipe_t* ed = (edge_swipe_t*)lv_event_get_user_data(e);
    lv_event_code_t code = lv_event_get_code(e);

    lv_indev_t* indev = lv_indev_active();
    if (!indev) return;
    lv_point_t p;
    lv_indev_get_point(indev, &p);

    if (code == LV_EVENT_PRESSED) {
        ed->start_x = p.x;
        ed->fired   = false;
    } else if (code == LV_EVENT_PRESSING && !ed->fired) {
        int32_t dx = p.x - ed->start_x;
        if ((ed->from_left && dx > 60) || (!ed->from_left && dx < -60)) {
            ed->fired = true;
            ui_nav_back();   // 与返回键一致
        }
    }
}

static void edge_swipe_delete_cb(lv_event_t* e)
{
    lv_free(lv_event_get_user_data(e));
}

static void add_edge_swipe(lv_obj_t* screen, bool from_left)
{
    lv_obj_t* strip = lv_obj_create(screen);
    lv_obj_set_size(strip, 32, LV_PCT(100));
    lv_obj_align(strip, from_left ? LV_ALIGN_LEFT_MID : LV_ALIGN_RIGHT_MID, 0, 0);
    lv_obj_set_style_bg_opa(strip, LV_OPA_0, 0);
    lv_obj_set_style_border_width(strip, 0, 0);
    lv_obj_add_flag(strip, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_remove_flag(strip, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_flag(strip, LV_OBJ_FLAG_IGNORE_LAYOUT);  // 不参与 flex 布局，浮在边缘

    edge_swipe_t* ed = (edge_swipe_t*)lv_malloc(sizeof(edge_swipe_t));
    lv_memzero(ed, sizeof(edge_swipe_t));
    ed->from_left = from_left;
    lv_obj_add_event_cb(strip, edge_swipe_cb, LV_EVENT_PRESSED, ed);
    lv_obj_add_event_cb(strip, edge_swipe_cb, LV_EVENT_PRESSING, ed);
    lv_obj_add_event_cb(strip, edge_swipe_delete_cb, LV_EVENT_DELETE, ed);
}

// 顶部状态栏双击 → 息屏。状态栏是各页公共组件，在此统一接入，所有页面顶栏双击均可息屏。
static void topbar_dbltap_cb(lv_event_t* e)
{
    (void)e;
    static uint32_t last_tap = 0;
    uint32_t now = lv_tick_get();
    if (now - last_tap < 600) { last_tap = 0; scrmgr_request_off(); }
    else last_tap = now;
}

// 顶栏上的图标按钮（返回键、主页面的齿轮）：没有底色，按下时才有；触摸范围比图标大
static lv_obj_t* topbar_icon_button(lv_obj_t* parent, const char* symbol, int32_t w, int32_t h, lv_event_cb_t cb)
{
    lv_obj_t* btn = lv_button_create(parent);
    lv_obj_set_size(btn, w, h);
    lv_obj_set_style_bg_opa(btn, LV_OPA_0, 0);
    lv_obj_set_style_bg_color(btn, lv_color_hex(COL_CARD_HI), LV_STATE_PRESSED);
    lv_obj_set_style_bg_opa(btn, LV_OPA_COVER, LV_STATE_PRESSED);
    lv_obj_set_style_radius(btn, 14, 0);
    lv_obj_set_style_shadow_width(btn, 0, 0);
    lv_obj_t* icon = lv_label_create(btn);
    lv_label_set_text(icon, symbol);
    lv_obj_set_style_text_font(icon, &lv_font_montserrat_28, 0);
    lv_obj_set_style_text_color(icon, lv_color_hex(COL_TEXT), 0);
    lv_obj_center(icon);
    lv_obj_add_event_cb(btn, cb, LV_EVENT_CLICKED, nullptr);
    return btn;
}

lv_obj_t* ui_topbar_button(lv_obj_t* screen, const char* symbol, lv_event_cb_t cb)
{
    lv_obj_t* bar = lv_obj_get_child(screen, 0);        // ui_page_frame 头一个建的就是顶栏
    lv_obj_t* btn = topbar_icon_button(bar, symbol, 60, 60, cb);
    // 状态簇靠右、里面的图标也靠右排，全亮时占 96（见 ui_status_cluster 里那笔账；电量写「N/A」时再宽一点）：按钮排在它左边
    lv_obj_align(btn, LV_ALIGN_RIGHT_MID, -124, 0);
    return btn;
}

lv_obj_t* ui_page_frame(lv_obj_t* screen, const char* title, bool show_back)
{
    ui_set_back_action(ui_open_main);   // 返回键 / 侧滑默认回主页面；上一级不是主页面的（WiFi 页）建完框架自己再设
    lv_obj_set_style_bg_color(screen, lv_color_hex(COL_BG), 0);
    lv_obj_remove_flag(screen, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_flex_flow(screen, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_all(screen, 0, 0);
    lv_obj_set_style_pad_row(screen, 0, 0);

    // ── 顶栏（加高，便于点返回键）──
    lv_obj_t* bar = lv_obj_create(screen);
    lv_obj_set_size(bar, LV_PCT(100), 76);
    lv_obj_set_style_bg_color(bar, lv_color_hex(COL_BG), 0);
    lv_obj_set_style_border_width(bar, 0, 0);
    lv_obj_set_style_radius(bar, 0, 0);
    lv_obj_set_style_pad_hor(bar, 20, 0);
    lv_obj_remove_flag(bar, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_event_cb(bar, topbar_dbltap_cb, LV_EVENT_CLICKED, nullptr);  // 双击顶栏息屏

    // 左：返回键
    lv_obj_t* left = lv_obj_create(bar);
    lv_obj_set_style_bg_opa(left, LV_OPA_0, 0);
    lv_obj_set_style_border_width(left, 0, 0);
    lv_obj_set_style_pad_all(left, 0, 0);
    lv_obj_set_size(left, LV_SIZE_CONTENT, LV_PCT(100));
    lv_obj_align(left, LV_ALIGN_LEFT_MID, 0, 0);
    lv_obj_set_flex_flow(left, LV_FLEX_FLOW_ROW);
    lv_obj_set_flex_align(left, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_column(left, 12, 0);
    lv_obj_remove_flag(left, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_remove_flag(left, LV_OBJ_FLAG_CLICKABLE);  // 让点击穿透到 bar（返回键自身仍可点）

    if (show_back) {
        topbar_icon_button(left, LV_SYMBOL_LEFT, 60, 68, back_clicked_cb);     // 返回键：窄但高，更好点
    }

    // 中：标题
    if (title && title[0]) {
        lv_obj_t* tl = lv_label_create(bar);
        lv_label_set_text(tl, title);
        lv_obj_set_style_text_font(tl, &font_cn_28, 0);
        lv_obj_set_style_text_color(tl, lv_color_hex(COL_TEXT), 0);
        lv_obj_align(tl, LV_ALIGN_CENTER, 0, 0);
    }

    // 右：状态簇（WiFi / 电量）
    status_cluster_t* cluster = ui_status_cluster(bar);     // 建的时候已经填过一次
    lv_obj_align(ui_status_cluster_obj(cluster), LV_ALIGN_RIGHT_MID, 0, 0);
    lv_obj_remove_flag(ui_status_cluster_obj(cluster), LV_OBJ_FLAG_CLICKABLE);  // 同上：双击穿透到 bar

    lv_timer_t* timer = lv_timer_create(topbar_tick, 1000, cluster);
    lv_obj_add_event_cb(bar, topbar_delete_cb, LV_EVENT_DELETE, timer);

    // ── 内容区 ──
    lv_obj_t* content = lv_obj_create(screen);
    lv_obj_set_width(content, LV_PCT(100));
    lv_obj_set_flex_grow(content, 1);
    lv_obj_set_style_bg_opa(content, LV_OPA_0, 0);
    lv_obj_set_style_border_width(content, 0, 0);
    lv_obj_set_style_pad_all(content, 24, 0);
    lv_obj_set_style_pad_top(content, 8, 0);

    // 子页面支持安卓式侧滑返回（最后创建 → 浮在最上层）。
    if (show_back) {
        add_edge_swipe(screen, true);
        add_edge_swipe(screen, false);
    }
    return content;
}

// ── 确认框 ────────────────────────────────────────────────────
typedef struct {
    void (*on_ok)(void* user);
    void* user;
    lv_obj_t* mbox;
} confirm_ctx_t;

static void confirm_ok_cb(lv_event_t* e)
{
    confirm_ctx_t* ctx = (confirm_ctx_t*)lv_event_get_user_data(e);
    lv_obj_t* mbox = ctx->mbox;
    if (ctx->on_ok) ctx->on_ok(ctx->user);
    lv_msgbox_close(mbox);     // ctx 随对话框一起释放（confirm_delete_cb）
}

static void confirm_cancel_cb(lv_event_t* e)
{
    lv_msgbox_close(((confirm_ctx_t*)lv_event_get_user_data(e))->mbox);
}

static void confirm_delete_cb(lv_event_t* e)
{
    lv_free(lv_event_get_user_data(e));
}

lv_obj_t* ui_confirm(const char* title, const char* text, const char* ok_text, bool danger,
                     void (*on_ok)(void* user), void* user)
{
    // 确认的往往是不可撤销的操作，字太小、按钮太挤会让人看不清
    // 就按下去。宽 760、标题 28px、正文行距拉开、按钮 180×56。
    lv_obj_t* mbox = lv_msgbox_create(nullptr);
    confirm_ctx_t* ctx = (confirm_ctx_t*)lv_malloc(sizeof(confirm_ctx_t));
    ctx->on_ok = on_ok;
    ctx->user  = user;
    ctx->mbox  = mbox;
    lv_obj_add_event_cb(mbox, confirm_delete_cb, LV_EVENT_DELETE, ctx);

    lv_obj_set_width(mbox, 760);
    lv_obj_set_style_bg_color(mbox, lv_color_hex(COL_CARD), 0);
    lv_obj_set_style_border_color(mbox, lv_color_hex(COL_BORDER), 0);
    lv_obj_set_style_border_width(mbox, 2, 0);
    lv_obj_set_style_radius(mbox, 16, 0);
    lv_obj_set_style_text_font(mbox, &font_cn_20, 0);

    // 三段（header/content/footer）默认各自带底色和很小的内边距，看着像三条横杠。
    // 统一成透明底 + 大内边距，整体才是一张卡片。
    lv_obj_t* tl = lv_msgbox_add_title(mbox, title);
    lv_obj_set_style_text_font(tl, &font_cn_28, 0);
    lv_obj_t* header = lv_msgbox_get_header(mbox);
    lv_obj_set_style_bg_opa(header, LV_OPA_0, 0);
    // ⚠️ 关键：LVGL 把 header/footer 的高度**写死**成 `LV_DPI_DEF / 3` = 43px
    //    （见 lv_msgbox.c 的 class 定义 height_def），和内容、内边距统统无关。
    //    不改成 SIZE_CONTENT 的话，设多大的 pad 都没用：54 高的标题、56 高的按钮
    //    会直接被裁掉一截。实测证据：header h=43 而标题 h=54、footer h=43 而按钮 h=56，
    //    两处都报"超出父对象"。content 段不用改（它的 height_def 本来就是 SIZE_CONTENT）。
    lv_obj_set_height(header, LV_SIZE_CONTENT);
    lv_obj_set_style_pad_all(header, 28, 0);
    lv_obj_set_style_pad_bottom(header, 4, 0);

    if (text) {
        lv_obj_t* txt = lv_msgbox_add_text(mbox, text);
        lv_obj_set_style_text_color(txt, lv_color_hex(COL_MUTED), 0);
        lv_obj_set_style_text_line_space(txt, 10, 0);
    }
    lv_obj_t* content = lv_msgbox_get_content(mbox);
    lv_obj_set_style_bg_opa(content, LV_OPA_0, 0);
    lv_obj_set_style_pad_hor(content, 28, 0);
    lv_obj_set_style_pad_ver(content, 8, 0);

    lv_obj_t* ok = lv_msgbox_add_footer_button(mbox, ok_text);
    lv_obj_set_size(ok, 180, 56);
    lv_obj_set_style_radius(ok, 12, 0);
    lv_obj_set_style_shadow_width(ok, 0, 0);
    lv_obj_set_style_bg_color(ok, lv_color_hex(danger ? COL_BAD : COL_ACCENT), 0);
    lv_obj_add_event_cb(ok, confirm_ok_cb, LV_EVENT_CLICKED, ctx);

    lv_obj_t* cancel = lv_msgbox_add_footer_button(mbox, "取消");
    lv_obj_set_size(cancel, 180, 56);
    lv_obj_set_style_radius(cancel, 12, 0);
    lv_obj_set_style_shadow_width(cancel, 0, 0);
    lv_obj_set_style_bg_color(cancel, lv_color_hex(COL_CARD_HI), 0);
    lv_obj_add_event_cb(cancel, confirm_cancel_cb, LV_EVENT_CLICKED, ctx);

    lv_obj_t* footer = lv_msgbox_get_footer(mbox);
    lv_obj_set_style_bg_opa(footer, LV_OPA_0, 0);
    lv_obj_set_height(footer, LV_SIZE_CONTENT);      // 同上：不改就把按钮裁掉
    lv_obj_set_style_pad_all(footer, 28, 0);
    lv_obj_set_style_pad_column(footer, 16, 0);
    // 两个按钮靠右成组，而不是 LVGL 默认的 SPACE_EVENLY
    // 把它们摊在 760 宽里 —— 摊开后反而看不出哪个是主操作。
    lv_obj_set_flex_align(footer, LV_FLEX_ALIGN_END, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    return mbox;
}

void ui_confirm_restart(void)
{
    ui_confirm("重启设备？", nullptr, "重启", false, [](void*) { esp_restart(); }, nullptr);
}
