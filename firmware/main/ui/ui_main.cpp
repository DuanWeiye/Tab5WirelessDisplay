// 主页面：这台设备只做无线显示（接收端见 main/wdisp.hpp，协议见 windisplay/PROTOCOL.md），开机就是这一页。
// 中间是连接状态：一个显示器图标（两侧的弧线在等电脑连的时候呼吸）加一行大字——等待连接 / 已连接 / 已暂停 / 正在联网 / 未联网；
// 下面两行是显示期间常要动的几样：亮度、音量；画质、已配对电脑、性能信息。其余设置在设置页（ui_settings.cpp，顶栏的齿轮）。
// 一屏放完，不滚动。
// 这一页只管把状态画出来、把人改的设置交给 app（main/app.hpp）：等不等电脑连由 app 定，和这一页开没开着、息没息屏无关。
// 电脑的第一帧画面到齐，屏幕就交给 wdisp：LVGL 停着，这一页的定时器也不跑；连接结束后 LVGL 恢复，回到这一页。
// 电脑开着的时候几秒之内屏幕就被占了，来不及改设置：人在显示期间做退出手势（左边缘右滑）就暂停，改完点「开始显示」接着等。
#include "ui.h"
#include "theme.h"

#include <cstdio>
#include <cstring>

#include <esp_log.h>

#include "../app.hpp"
#include "../net/wifi.hpp"
#include "../settings_store.hpp"
#include "../wdisp.hpp"
#include "../wdisp_pair.hpp"

namespace {

const char* TAG = "ui_main";

// 中间那块现在画的是哪一种状态
enum class Hero : uint8_t { None, Waiting, Connected, Paused, Joining, Offline };

// 等电脑连的时候弧线呼吸一轮多久；靠外的一对比靠里的一对晚多少（看着像一圈圈往外传）
const int32_t BREATH_MS     = 2400;
const int32_t BREATH_LAG_MS = 500;

struct main_ui_t {
    lv_obj_t*   hero;           // 中间那块（点它：没联网去 WiFi 页，无线模块故障弹重启确认框）
    lv_obj_t*   icon;           // 状态图标的容器，呼吸动画挂在它上面
    lv_obj_t*   mon;            // 显示器的外框
    lv_obj_t*   stand[2];       // 支架、底座
    lv_obj_t*   glyph;          // 屏幕里的符号（暂停 / 对勾 / 警告；等连接时没有）
    lv_obj_t*   arc[4];         // 两侧的弧线：0、1 是靠里的一对，2、3 是靠外的一对
    uint8_t     arc_opa[2];     // 里、外两对现在的不透明度
    Hero        shown;          // 图标现在画的是哪一种
    lv_obj_t*   state_lbl;      // 大字状态
    lv_obj_t*   name_lbl;       // 连着的那台电脑的名字（只在已连接时露出来）
    lv_obj_t*   start_btn;      // 「开始显示」：只在暂停时露出来
    lv_obj_t*   bri_sld;        // 「亮度」滑块
    lv_obj_t*   vol_sld;        // 「音量」滑块
    lv_obj_t*   pic_seg;        // 画质：四档摊开的分段选择（settings 的 wd_pic）
    lv_obj_t*   peers_row;      // 「已配对电脑」那一块：有配对过的才可点
    lv_obj_t*   peers_lbl;      // 右边的台数（可点时它右边有箭头）
    int         n_peers;        // 那一块现在写的是几台（-1 = 还没写过）
    lv_obj_t*   perf_sw;        // 「性能信息」开关
    lv_timer_t* timer;
};

// 换页时新页面先建好、旧页面晚一拍才销毁（见 ui_fade_in）：从这一页再打开这一页时两个实例会短暂并存。
// 这两个指着最新建的那个实例（销毁后是空）。只在 LVGL 线程里读写。
lv_obj_t*  s_scr  = nullptr;
main_ui_t* s_page = nullptr;

// 画质各档的叫法，照 settings 里那张表排（调试命令 wd pic 用的是同一张）；分段选择只记指针，所以放在静态区
const char* s_pic_map[settings::WD_PIC_N + 1];

// ── 状态图标 ──────────────────────────────────────────────────
void set_arc_opa(main_ui_t* ui, int pair, uint8_t opa)
{
    if (ui->arc_opa[pair] == opa) return;       // 没变就不动：设一次样式就重画一次
    ui->arc_opa[pair] = opa;
    lv_obj_set_style_arc_opa(ui->arc[pair * 2], opa, 0);
    lv_obj_set_style_arc_opa(ui->arc[pair * 2 + 1], opa, 0);
}

// 呼吸：t 在 0~BREATH_MS 里循环。每一对弧线的不透明度走三角波，分成 16 级——
// 动画每一拍都调到这里，分了级之后一对弧线一秒只重画十几次（每次就是弧线自己那一小块）
void breathe_cb(void* icon, int32_t t)
{
    main_ui_t* ui = (main_ui_t*)lv_obj_get_user_data((lv_obj_t*)icon);
    for (int pair = 0; pair < 2; pair++) {
        const int32_t p     = (t + BREATH_MS - pair * BREATH_LAG_MS) % BREATH_MS;
        const int32_t level = (p < BREATH_MS / 2 ? p : BREATH_MS - p) * 15 / (BREATH_MS / 2);     // 0~15
        set_arc_opa(ui, pair, (uint8_t)(45 + level * 14));
    }
}

// 图标照状态画：等连接 = 白色显示器、蓝色弧线呼吸；已连接 = 全绿、屏幕里一个对勾；已暂停 = 全灰、暂停符号、弧线淡着；
// 正在联网（开机、掉线后自己在连，不用人管）= 全灰、弧线淡着、没有符号；连不了、要人动手（没联网 / 无线模块故障）= 红色、警告符号、没有弧线
void set_hero(main_ui_t* ui, Hero h)
{
    if (ui->shown == h) return;
    ui->shown = h;
    const uint32_t body = h == Hero::Waiting ? COL_TEXT : h == Hero::Connected ? COL_GOOD : h == Hero::Offline ? COL_BAD : COL_MUTED;
    const uint32_t wave = h == Hero::Waiting ? COL_ACCENT : body;
    lv_obj_set_style_border_color(ui->mon, lv_color_hex(body), 0);
    for (lv_obj_t* s : ui->stand) lv_obj_set_style_bg_color(s, lv_color_hex(body), 0);
    for (lv_obj_t* a : ui->arc) lv_obj_set_style_arc_color(a, lv_color_hex(wave), 0);
    lv_label_set_text(ui->glyph, h == Hero::Connected ? LV_SYMBOL_OK : h == Hero::Paused ? LV_SYMBOL_PAUSE : h == Hero::Offline ? LV_SYMBOL_WARNING : "");
    lv_obj_set_style_text_color(ui->glyph, lv_color_hex(body), 0);

    lv_anim_delete(ui->icon, breathe_cb);
    if (h == Hero::Waiting) {
        lv_anim_t a;
        lv_anim_init(&a);
        lv_anim_set_var(&a, ui->icon);
        lv_anim_set_exec_cb(&a, breathe_cb);
        lv_anim_set_values(&a, 0, BREATH_MS);
        lv_anim_set_duration(&a, BREATH_MS);
        lv_anim_set_repeat_count(&a, LV_ANIM_REPEAT_INFINITE);
        lv_anim_start(&a);
    } else {
        const uint8_t opa = h == Hero::Connected ? LV_OPA_COVER : h == Hero::Offline ? LV_OPA_0 : LV_OPA_30;
        set_arc_opa(ui, 0, opa);
        set_arc_opa(ui, 1, opa);
    }
}

// 图标里的一个实心小块（支架、底座）
lv_obj_t* icon_bar(lv_obj_t* icon, int32_t x, int32_t y, int32_t w, int32_t h)
{
    lv_obj_t* o = lv_obj_create(icon);
    lv_obj_remove_style_all(o);
    lv_obj_set_pos(o, x, y);
    lv_obj_set_size(o, w, h);
    lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(o, 3, 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
    return o;
}

// 图标里的一段弧线：圆心 (cx, cy)、半径 r（量到线的正中），从 a0 度画到 a1 度（0 度在三点钟方向，顺时针）。线宽 6
lv_obj_t* icon_arc(lv_obj_t* icon, int32_t cx, int32_t cy, int32_t r, int32_t a0, int32_t a1)
{
    lv_obj_t* a = lv_arc_create(icon);
    lv_obj_remove_style_all(a);         // 只留底下那一段弧：指示弧、旋钮都不要
    lv_obj_set_size(a, 2 * (r + 3), 2 * (r + 3));
    lv_obj_set_pos(a, cx - r - 3, cy - r - 3);
    lv_arc_set_bg_angles(a, a0, a1);
    lv_obj_set_style_arc_width(a, 6, 0);
    lv_obj_set_style_arc_rounded(a, true, 0);
    lv_obj_remove_flag(a, LV_OBJ_FLAG_CLICKABLE);
    return a;
}

// 状态图标，280 × 140：中间一台显示器（屏幕 100 × 68，下面支架和底座），左右各两段弧线
void build_icon(main_ui_t* ui, lv_obj_t* parent)
{
    lv_obj_t* icon = lv_obj_create(parent);
    lv_obj_remove_style_all(icon);
    lv_obj_set_size(icon, 280, 140);
    lv_obj_remove_flag(icon, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_remove_flag(icon, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_user_data(icon, ui);
    ui->icon = icon;

    ui->mon = lv_obj_create(icon);
    lv_obj_remove_style_all(ui->mon);
    lv_obj_set_pos(ui->mon, 90, 22);
    lv_obj_set_size(ui->mon, 100, 68);
    lv_obj_set_style_border_width(ui->mon, 6, 0);
    lv_obj_set_style_radius(ui->mon, 10, 0);
    lv_obj_remove_flag(ui->mon, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_remove_flag(ui->mon, LV_OBJ_FLAG_SCROLLABLE);
    ui->glyph = lv_label_create(ui->mon);
    lv_label_set_text(ui->glyph, "");
    lv_obj_set_style_text_font(ui->glyph, &lv_font_montserrat_28, 0);
    lv_obj_center(ui->glyph);

    ui->stand[0] = icon_bar(icon, 137, 90, 6, 20);
    ui->stand[1] = icon_bar(icon, 114, 108, 52, 6);

    // 弧线的圆心在屏幕左右两条边的中点上，各张开 ±48 度
    ui->arc[0] = icon_arc(icon, 190, 56, 22, 312, 48);
    ui->arc[1] = icon_arc(icon, 90, 56, 22, 132, 228);
    ui->arc[2] = icon_arc(icon, 190, 56, 40, 312, 48);
    ui->arc[3] = icon_arc(icon, 90, 56, 40, 132, 228);
    ui->arc_opa[0] = ui->arc_opa[1] = LV_OPA_COVER;     // 样式的默认值：不透明
}

// ── 照现在的状态摆 ────────────────────────────────────────────
// 几个控件照现在的值摆：页面开着时调试命令（wd vol / wd pic / wd osd）也能改这几个值
void sync_settings(main_ui_t* ui)
{
    const settings::Values& v = settings::get();
    ui_segmented_set(ui->pic_seg, v.wd_pic);
    ui_set_checked(ui->perf_sw, v.wd_osd != 0);
    // 手指正拖着滑块时不动它（它的值这时比 settings 里的新，回调马上会写过去）
    if (!lv_obj_has_state(ui->vol_sld, LV_STATE_PRESSED) && lv_slider_get_value(ui->vol_sld) != v.wd_volume)
        lv_slider_set_value(ui->vol_sld, v.wd_volume, LV_ANIM_OFF);
    if (!lv_obj_has_state(ui->bri_sld, LV_STATE_PRESSED) && lv_slider_get_value(ui->bri_sld) != v.brightness)
        lv_slider_set_value(ui->bri_sld, v.brightness, LV_ANIM_OFF);
}

// 「已配对电脑」那一块：台数，有的话整块可点。配对是在连接上发生的、调试命令也能移除配对，所以跟着定时器更新
void sync_peers(main_ui_t* ui)
{
    wdpair::Entry peers[wdpair::MAX_PEERS];
    const int     n = wdpair::list(peers);
    if (n == ui->n_peers) return;
    ui->n_peers = n;
    if (n > 0) lv_label_set_text_fmt(ui->peers_lbl, "%d 台", n);
    else       lv_label_set_text(ui->peers_lbl, "无");
    ui_row_set_link(ui->peers_lbl, n > 0);
}

void refresh(main_ui_t* ui)
{
    const wifi::Status w  = wifi::status();
    const wdisp::Stats st = wdisp::stats();
    sync_settings(ui);
    sync_peers(ui);
    ui_set_hidden(ui->start_btn, !app::paused());
    const char* name = "";
    if (w.state == wifi::State::Starting || w.state == wifi::State::Connecting) {
        // 开机后无线模块还在起、正在连路由器：它自己会连上，不用人管，不用错误色
        set_hero(ui, Hero::Joining);
        ui_label_set(ui->state_lbl, "正在联网", COL_MUTED);
    } else if (w.state != wifi::State::Connected) {
        // 要人动手的状态（WiFi 关着、没有连得上的网络、无线模块故障）：错误色，点一下去处理（见 hero_clicked）
        set_hero(ui, Hero::Offline);
        ui_label_set(ui->state_lbl, w.state == wifi::State::Fault ? "无线模块故障" : "未联网", COL_BAD);
    } else if (app::paused()) {
        set_hero(ui, Hero::Paused);
        ui_label_set(ui->state_lbl, "已暂停", COL_MUTED);
    } else if (st.state == wdisp::State::Connected || st.state == wdisp::State::Showing) {
        set_hero(ui, Hero::Connected);
        ui_label_set(ui->state_lbl, "已连接", COL_GOOD);
        name = st.name[0] ? st.name : st.peer;      // 握手走完才有名字，之前先显示地址
    } else {
        // wdisp 报「没在监听」而又没暂停的：平时只有开机后、刚点「开始显示」后它的任务还没轮到的那一小会儿，也按等连接写
        // （不单列一种字样，免得每次闪一下）。一直这样的是它起不来（建任务、开监听口失败），那是故障，它自己打错误日志
        set_hero(ui, Hero::Waiting);
        ui_label_set(ui->state_lbl, "等待连接", COL_TEXT);
    }
    ui_label_set_text_if_changed(ui->name_lbl, name);
    ui_set_hidden(ui->name_lbl, !name[0]);
}

// 点中间那块：没联网（含「正在联网」：想换一个网络的可以进去）→ 去 WiFi 页；无线模块故障 → 重启确认框（重启是这个故障唯一的出路）；
// 连上了的各种状态点了没反应
void hero_clicked(lv_event_t*)
{
    const wifi::State s = wifi::status().state;
    if (s == wifi::State::Fault) ui_confirm_restart();
    else if (s != wifi::State::Connected) ui_open_wifi();
}

void tick_cb(lv_timer_t* t)
{
    refresh((main_ui_t*)lv_timer_get_user_data(t));
}

void delete_cb(lv_event_t* e)
{
    main_ui_t* ui = (main_ui_t*)lv_event_get_user_data(e);
    if (lv_event_get_target_obj(e) == s_scr) { s_scr = nullptr; s_page = nullptr; }
    lv_anim_delete(ui->icon, breathe_cb);       // 动画的回调要用 ui：先停掉再释放
    lv_timer_delete(ui->timer);
    lv_free(ui);
}

// ── 下面两行 ──────────────────────────────────────────────────
// 一行：里面的几块横着排，间距 16
lv_obj_t* tile_row(lv_obj_t* parent)
{
    lv_obj_t* r = lv_obj_create(parent);
    lv_obj_remove_style_all(r);
    lv_obj_set_size(r, LV_PCT(100), 88);
    lv_obj_set_flex_flow(r, LV_FLEX_FLOW_ROW);
    lv_obj_set_style_pad_column(r, 16, 0);
    lv_obj_remove_flag(r, LV_OBJ_FLAG_SCROLLABLE);
    return r;
}

// 一行里的一块：和设置行一个样（左边标题、右边控件），宽度按 grow 的比例分这一行
lv_obj_t* tile(lv_obj_t* row, const char* title, uint8_t grow)
{
    lv_obj_t* t = ui_setting_row(row, title);
    lv_obj_set_flex_grow(t, grow);
    return t;
}

// 滑块：拖的时候当场生效，松手才存盘。set(值, 存不存盘) 是 app 里对应的那个 set_*
lv_obj_t* add_slider(lv_obj_t* row, int lo, int hi, int cur, void (*set)(int, bool))
{
    lv_obj_t* s = lv_slider_create(row);
    lv_obj_set_size(s, 400, 16);
    lv_obj_align(s, LV_ALIGN_RIGHT_MID, -20, 0);
    lv_slider_set_range(s, lo, hi);
    lv_slider_set_value(s, cur, LV_ANIM_OFF);
    lv_obj_add_event_cb(s, [](lv_event_t* e) {
        ((void (*)(int, bool))lv_event_get_user_data(e))(lv_slider_get_value((lv_obj_t*)lv_event_get_target(e)), false);
    }, LV_EVENT_VALUE_CHANGED, (void*)set);
    auto done = [](lv_event_t* e) {
        ((void (*)(int, bool))lv_event_get_user_data(e))(lv_slider_get_value((lv_obj_t*)lv_event_get_target(e)), true);
    };
    lv_obj_add_event_cb(s, done, LV_EVENT_RELEASED, (void*)set);
    lv_obj_add_event_cb(s, done, LV_EVENT_PRESS_LOST, (void*)set);      // 按着的时候滑出去了：没有 RELEASED
    return s;
}

// 第一行：亮度、音量各占一半。第二行：画质和上面的亮度一样宽，剩下一半分给已配对电脑、性能信息。
// 画质、性能信息只是告诉电脑 / 自己叠一条浮层（协议 PICTURE_MODE，见 wdisp.cpp 的 send_mode；浮层见 wd_osd.cpp），正连着的话当场生效
void build_tiles(main_ui_t* ui, lv_obj_t* content)
{
    lv_obj_t* row = tile_row(content);
    ui->bri_sld = add_slider(tile(row, "亮度", 1), 5, 100, settings::get().brightness, app::set_brightness);
    ui->vol_sld = add_slider(tile(row, "音量", 1), 0, 100, settings::get().wd_volume, app::set_volume);     // 电脑的声音从 Tab5 放出来有多响（0 = 静音）

    row = tile_row(content);
    for (int i = 0; i < settings::WD_PIC_N; i++) s_pic_map[i] = settings::WD_PIC_NAMES[i];
    s_pic_map[settings::WD_PIC_N] = "";
    ui->pic_seg = ui_segmented(row, s_pic_map, settings::get().wd_pic, [](lv_event_t* e) {
        app::set_picture(ui_segmented_get((lv_obj_t*)lv_event_get_target(e)));
    });
    lv_obj_set_height(ui->pic_seg, 88);
    lv_obj_set_flex_grow(ui->pic_seg, 1);

    lv_obj_t* right = lv_obj_create(row);       // 右半边：和上一行的音量对齐
    lv_obj_remove_style_all(right);
    lv_obj_set_height(right, 88);
    lv_obj_set_flex_grow(right, 1);
    lv_obj_set_flex_flow(right, LV_FLEX_FLOW_ROW);
    lv_obj_set_style_pad_column(right, 16, 0);
    lv_obj_remove_flag(right, LV_OBJ_FLAG_SCROLLABLE);

    // 配对过的电脑：有的话整块可点，进二级页逐台移除（台数、可不可点由 sync_peers 定）
    ui->peers_row   = tile(right, "已配对电脑", 8);
    ui->peers_lbl   = ui_row_value(ui->peers_row, true);
    lv_obj_add_event_cb(ui->peers_row, [](lv_event_t*) { ui_open_pairs(); }, LV_EVENT_CLICKED, nullptr);
    ui->n_peers = -1;

    // 显示期间在画面顶上靠右叠一条半透明的帧率、延迟（Tab5 自己画的，见 wd_osd.cpp）
    ui->perf_sw = ui_row_switch(tile(right, "性能信息", 7));
    lv_obj_add_event_cb(ui->perf_sw, [](lv_event_t* e) {
        app::set_osd(lv_obj_has_state((lv_obj_t*)lv_event_get_target(e), LV_STATE_CHECKED));
    }, LV_EVENT_VALUE_CHANGED, nullptr);
}

}  // namespace

void ui_open_main(void)
{
    ui_nav_record(ui_open_main);
    ui_fade_out();
    lv_obj_t* scr     = lv_obj_create(nullptr);
    lv_obj_t* content = ui_page_frame(scr, "无线显示", false);
    ui_topbar_button(scr, LV_SYMBOL_SETTINGS, [](lv_event_t*) { ui_open_settings(); });
    lv_obj_set_flex_flow(content, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(content, 16, 0);
    lv_obj_remove_flag(content, LV_OBJ_FLAG_SCROLLABLE);

    main_ui_t* ui = (main_ui_t*)lv_malloc(sizeof(main_ui_t));
    lv_memzero(ui, sizeof(main_ui_t));

    // 中间那块：图标、大字状态，已连接时多一行电脑名，暂停时多一个「开始显示」。占掉两行设置上面的全部高度，里面的东西居中
    lv_obj_t* hero = lv_obj_create(content);
    lv_obj_remove_style_all(hero);
    lv_obj_set_width(hero, LV_PCT(100));
    lv_obj_set_flex_grow(hero, 1);
    lv_obj_set_flex_flow(hero, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_flex_align(hero, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_row(hero, 16, 0);
    lv_obj_remove_flag(hero, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_flag(hero, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_event_cb(hero, hero_clicked, LV_EVENT_CLICKED, nullptr);
    ui->hero = hero;

    build_icon(ui, hero);

    ui->state_lbl = lv_label_create(hero);
    lv_label_set_text(ui->state_lbl, "");
    lv_obj_set_style_text_font(ui->state_lbl, &font_cn_48, 0);      // 这个字库只有这几句状态里的字（见 theme.h）

    ui->name_lbl = lv_label_create(hero);
    lv_label_set_text(ui->name_lbl, "");
    lv_obj_set_style_text_font(ui->name_lbl, &font_cn_28, 0);
    lv_obj_set_style_text_color(ui->name_lbl, lv_color_hex(COL_MUTED), 0);
    // 名字太长时只留一行、末尾变省略号：宽度封顶之外高度也要定死成一行（只封宽度的话是折成两行）
    lv_obj_set_style_max_width(ui->name_lbl, 900, 0);
    lv_obj_set_height(ui->name_lbl, lv_font_get_line_height(&font_cn_28));
    lv_label_set_long_mode(ui->name_lbl, LV_LABEL_LONG_DOT);
    lv_obj_add_flag(ui->name_lbl, LV_OBJ_FLAG_HIDDEN);

    ui->start_btn = lv_button_create(hero);
    lv_obj_set_size(ui->start_btn, 240, 80);
    lv_obj_set_style_bg_color(ui->start_btn, lv_color_hex(COL_ACCENT), 0);
    lv_obj_set_style_radius(ui->start_btn, 14, 0);
    lv_obj_set_style_shadow_width(ui->start_btn, 0, 0);
    lv_obj_set_style_margin_top(ui->start_btn, 8, 0);       // 和上面的大字隔开一点
    lv_obj_t* start_t = lv_label_create(ui->start_btn);
    lv_label_set_text(start_t, "开始显示");
    lv_obj_set_style_text_font(start_t, &font_cn_28, 0);
    lv_obj_set_style_text_color(start_t, lv_color_hex(COL_BG), 0);
    lv_obj_center(start_t);
    lv_obj_add_event_cb(ui->start_btn, [](lv_event_t*) { app::resume(); }, LV_EVENT_CLICKED, nullptr);

    build_tiles(ui, content);

    s_scr  = scr;
    s_page = ui;
    refresh(ui);
    ui->timer = lv_timer_create(tick_cb, 500, ui);
    lv_obj_add_event_cb(scr, delete_cb, LV_EVENT_DELETE, ui);

    ui_fade_in(scr);
}

void ui_main_refresh(void)
{
    if (s_page) refresh(s_page);
}

bool ui_main_in_front(void)
{
    return s_scr && lv_screen_active() == s_scr;
}

void ui_main_dbg_page(void)
{
    main_ui_t* ui = s_page;
    if (!ui) {
        ESP_LOGW(TAG, "无线显示页：没开着");
        return;
    }
    const int pic = ui_segmented_get(ui->pic_seg);
    ESP_LOGW(TAG, "无线显示页：%s，%s，状态「%s」，「开始显示」%s，电脑名%s「%s」，亮度滑块 %d，音量滑块 %d，画质「%s」，已配对 %d 台（%s），性能信息%s，弧线不透明度 %d / %d",
             lv_screen_active() == s_scr ? "在前台" : "不在前台", app::paused() ? "已暂停" : "没暂停", lv_label_get_text(ui->state_lbl),
             lv_obj_has_flag(ui->start_btn, LV_OBJ_FLAG_HIDDEN) ? "藏着" : "露着",
             lv_obj_has_flag(ui->name_lbl, LV_OBJ_FLAG_HIDDEN) ? "藏着" : "露着", lv_label_get_text(ui->name_lbl),
             (int)lv_slider_get_value(ui->bri_sld), (int)lv_slider_get_value(ui->vol_sld),
             pic >= 0 ? s_pic_map[pic] : "（没有选中的）", ui->n_peers,
             lv_obj_has_flag(ui->peers_row, LV_OBJ_FLAG_CLICKABLE) ? "可点" : "不可点",
             lv_obj_has_state(ui->perf_sw, LV_STATE_CHECKED) ? "开" : "关", ui->arc_opa[0], ui->arc_opa[1]);
}

void ui_main_dbg_volume(int pct)
{
    main_ui_t* ui = s_page;
    if (!ui || lv_screen_active() != s_scr) {
        ESP_LOGW(TAG, "「音量」滑块现在拖不到（主页面不在前台）");
        return;
    }
    // 和手指拖一样：滑块到了新位置（VALUE_CHANGED：当场生效），然后松手（RELEASED：存盘）
    lv_slider_set_value(ui->vol_sld, pct, LV_ANIM_OFF);
    lv_obj_send_event(ui->vol_sld, LV_EVENT_VALUE_CHANGED, nullptr);
    lv_obj_send_event(ui->vol_sld, LV_EVENT_RELEASED, nullptr);
}

void ui_main_dbg_start(void)
{
    main_ui_t* ui = s_page;
    if (!ui || lv_screen_active() != s_scr || lv_obj_has_flag(ui->start_btn, LV_OBJ_FLAG_HIDDEN)) {
        ESP_LOGW(TAG, "「开始显示」现在点不到（主页面不在前台，或者没暂停）");
        return;
    }
    lv_obj_send_event(ui->start_btn, LV_EVENT_CLICKED, nullptr);
}
