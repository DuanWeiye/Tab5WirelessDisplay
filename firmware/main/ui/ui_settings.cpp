// 设置页（主页面顶栏的齿轮进来）：不常动的那几样，一行一项——WiFi（进 WiFi 页）、触摸板模式、触摸板加速、息屏时间、充电上限、信息。
// 常动的（亮度、音量、画质、性能信息）和已配对电脑在主页面上。
// 改设置一律走 app::set_*（main/app.hpp）。六行一屏刚好放完；以后加了行放不下，内容区自己能滚（接了滚动加速）。
#include "ui.h"
#include "theme.h"

#include <cstdio>
#include <cstring>

#include <esp_app_desc.h>
#include <esp_log.h>

#include "../app.hpp"
#include "../net/wifi.hpp"
#include "../settings_store.hpp"
#include "../wdisp.hpp"

namespace {

const char* TAG = "ui_settings";

// 息屏时间档位（秒），0 = 不自动息屏；默认 2 分钟
const char*    SCROFF_OPTS  = "1分钟\n1.5分钟\n2分钟\n3分钟\n5分钟\n10分钟\n15分钟\n30分钟\n60分钟\n不息屏";
const uint32_t SCROFF_SEC[] = {60, 90, 120, 180, 300, 600, 900, 1800, 3600, 0};

// 充电上限档位（%），100 = 不限制（充到 100%）
const char*    CHGLIM_OPTS  = "80%\n85%\n90%\n95%\n不限制";
const uint32_t CHGLIM_VAL[] = {80, 85, 90, 95, 100};

struct settings_ui_t {
    lv_obj_t*   wifi_lbl;       // 「WiFi」行右边：连着的网络名，或者短状态
    lv_obj_t*   tpad_sw;        // 「触摸板模式」开关
    lv_obj_t*   gain_dd;        // 「触摸板加速」下拉框
    lv_obj_t*   info_lbl;       // 「信息」行右边：这台设备的地址 · 版本 · 编译日期
    char        version[80];    // 版本 · 编译日期（开页时算一次）
    lv_timer_t* timer;
};

// 同主页面：换页时两个实例会短暂并存，这两个指着最新建的那个（销毁后是空）。只在 LVGL 线程里读写
lv_obj_t*      s_scr  = nullptr;
settings_ui_t* s_page = nullptr;

// 版本：编译时自动取的提交号（工作区有没提交的改动时带 -dirty）+ 编译日期。
// 日期 IDF 记的是 __DATE__ 的写法（"Nov 12 2026"），显示成 2026-11-12；认不出就原样显示
void format_version(char* out, size_t cap)
{
    const esp_app_desc_t* d = esp_app_get_description();
    static const char MONTHS[] = "JanFebMarAprMayJunJulAugSepOctNovDec";
    char mon[4] = {0};
    int  day = 0, year = 0;
    const char* m = sscanf(d->date, "%3s %d %d", mon, &day, &year) == 3 ? strstr(MONTHS, mon) : nullptr;
    if (m && (m - MONTHS) % 3) m = nullptr;     // 跨着两个月份名匹配上的不算
    if (m) snprintf(out, cap, "%s · %04d-%02d-%02d", d->version, year, (int)(m - MONTHS) / 3 + 1, day);
    else   snprintf(out, cap, "%s · %s", d->version, d->date);
}

// 页面开着时别处也能改这几样（调试命令 wd tpad、WiFi 连上 / 断开）：跟着定时器照现在的值摆
void refresh(settings_ui_t* ui)
{
    const settings::Values& v = settings::get();
    const wifi::Status      w = wifi::status();
    char buf[128];

    // WiFi：连着写网络名；关着是人自己关的，不算异常；开着却没连上、无线模块故障用错误色（点进去处理）。
    // 开机后无线模块还在起的那几秒（Starting）和正在连一样写
    if (w.state == wifi::State::Connected)       ui_label_set(ui->wifi_lbl, w.ssid, COL_TEXT);
    else if (w.state == wifi::State::Fault)      ui_label_set(ui->wifi_lbl, "故障", COL_BAD);
    else if (w.state == wifi::State::Disabled)   ui_label_set(ui->wifi_lbl, "关闭", COL_MUTED);
    else if (w.state == wifi::State::Connecting || w.state == wifi::State::Starting) ui_label_set(ui->wifi_lbl, "连接中", COL_MUTED);
    else                                         ui_label_set(ui->wifi_lbl, "未连接", COL_BAD);

    const bool tpad = v.wd_touchpad != 0;
    ui_set_checked(ui->tpad_sw, tpad);
    ui_set_disabled(ui->gain_dd, !tpad);        // 触摸板模式关着时加速那一行置灰：留着可拨但拨了没反应最让人困惑
    for (int i = 0; i < settings::WD_TPAD_GAIN_N; i++) {
        if (settings::WD_TPAD_GAINS[i] != v.wd_tpad_gain) continue;
        if (lv_dropdown_get_selected(ui->gain_dd) != (uint32_t)i) lv_dropdown_set_selected(ui->gain_dd, i);
        break;
    }

    // 信息：联着网时前面带这台设备的地址（手工指定地址的发送端要用；托盘程序自己找得到）
    if (w.state == wifi::State::Connected) snprintf(buf, sizeof buf, "%s:%u · %s", w.ip, (unsigned)wdisp::PORT, ui->version);
    else                                   snprintf(buf, sizeof buf, "%s", ui->version);
    ui_label_set_text_if_changed(ui->info_lbl, buf);
}

void tick_cb(lv_timer_t* t)
{
    refresh((settings_ui_t*)lv_timer_get_user_data(t));
}

void delete_cb(lv_event_t* e)
{
    settings_ui_t* ui = (settings_ui_t*)lv_event_get_user_data(e);
    if (lv_event_get_target_obj(e) == s_scr) { s_scr = nullptr; s_page = nullptr; }
    lv_timer_delete(ui->timer);
    lv_free(ui);
}

}  // namespace

void ui_open_settings(void)
{
    ui_nav_record(ui_open_settings);
    ui_fade_out();
    lv_obj_t* scr     = lv_obj_create(nullptr);
    lv_obj_t* content = ui_page_frame(scr, "设置", true);
    lv_obj_set_flex_flow(content, LV_FLEX_FLOW_COLUMN);
    lv_obj_set_style_pad_row(content, 16, 0);
    lv_obj_add_flag(content, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_set_scroll_dir(content, LV_DIR_VER);
    ui_scroll_accel(content);   // 滚动位移复用：整宽纵向列表，里面没有不跟着动的浮层（下拉列表盖在它上面时框架自己处理）

    settings_ui_t* ui = (settings_ui_t*)lv_malloc(sizeof(settings_ui_t));
    lv_memzero(ui, sizeof(settings_ui_t));
    format_version(ui->version, sizeof ui->version);

    // WiFi：整行点了进 WiFi 页（开关、已保存的网络、扫描连接都在那一页）
    lv_obj_t* row = ui_setting_row(content, "WiFi");
    ui->wifi_lbl = ui_row_value(row, true);
    lv_obj_add_event_cb(row, [](lv_event_t*) { ui_open_wifi(); }, LV_EVENT_CLICKED, nullptr);

    // 触摸方式：关 = 手指点哪就点哪，开 = 屏幕当触摸板用。这一项和下面的加速只是告诉电脑（协议 INPUT_MODE / TPAD_GAIN，
    // 见 wdisp.cpp 的 send_mode），把触点换成鼠标动作是它的事；正连着的话当场告诉它
    row = ui_setting_row(content, "触摸板模式");
    ui->tpad_sw = ui_row_switch(row);
    lv_obj_add_event_cb(ui->tpad_sw, [](lv_event_t* e) {
        app::set_touchpad(lv_obj_has_state((lv_obj_t*)lv_event_get_target(e), LV_STATE_CHECKED));
        refresh((settings_ui_t*)lv_event_get_user_data(e));     // 加速那一行跟着置灰 / 恢复
    }, LV_EVENT_VALUE_CHANGED, ui);

    // 触摸板模式下光标的加速（手指滑得快时光标最多放大到几倍）。选项和取值都照 settings 里那张表排（调试命令 wd tpad accel 用的是同一张）
    row = ui_setting_row(content, "触摸板加速");
    uint32_t gain_vals[settings::WD_TPAD_GAIN_N];
    char     gain_opts[settings::WD_TPAD_GAIN_N * 8] = "";      // 每档的叫法是一个汉字（3 字节）加一个换行
    for (int i = 0; i < settings::WD_TPAD_GAIN_N; i++) {
        gain_vals[i] = settings::WD_TPAD_GAINS[i];
        if (i) strlcat(gain_opts, "\n", sizeof gain_opts);
        strlcat(gain_opts, settings::WD_TPAD_GAIN_NAMES[i], sizeof gain_opts);
    }
    ui->gain_dd = ui_dropdown(row, gain_opts, gain_vals, settings::WD_TPAD_GAIN_N, settings::get().wd_tpad_gain, [](lv_event_t* e) {
        const uint32_t sel = lv_dropdown_get_selected((lv_obj_t*)lv_event_get_target(e));
        if (sel < (uint32_t)settings::WD_TPAD_GAIN_N) app::set_touchpad_gain(settings::WD_TPAD_GAINS[sel]);
    });

    row = ui_setting_row(content, "息屏时间");
    ui_dropdown(row, SCROFF_OPTS, SCROFF_SEC, sizeof(SCROFF_SEC) / sizeof(SCROFF_SEC[0]), settings::get().screenoff_s, [](lv_event_t* e) {
        const uint32_t sel = lv_dropdown_get_selected((lv_obj_t*)lv_event_get_target(e));
        if (sel < sizeof(SCROFF_SEC) / sizeof(SCROFF_SEC[0])) app::set_screenoff(SCROFF_SEC[sel]);
    });

    // 到上限停充、电池旁路吃 C 口（board.cpp 的充电维持任务照它办）
    row = ui_setting_row(content, "充电上限");
    ui_dropdown(row, CHGLIM_OPTS, CHGLIM_VAL, sizeof(CHGLIM_VAL) / sizeof(CHGLIM_VAL[0]), settings::get().charge_limit, [](lv_event_t* e) {
        const uint32_t sel = lv_dropdown_get_selected((lv_obj_t*)lv_event_get_target(e));
        if (sel < sizeof(CHGLIM_VAL) / sizeof(CHGLIM_VAL[0])) app::set_charge_limit((int)CHGLIM_VAL[sel]);
    });

    row = ui_setting_row(content, "信息");
    ui->info_lbl = ui_row_value(row, false);

    s_scr  = scr;
    s_page = ui;
    refresh(ui);
    ui->timer = lv_timer_create(tick_cb, 500, ui);
    lv_obj_add_event_cb(scr, delete_cb, LV_EVENT_DELETE, ui);

    ui_fade_in(scr);
}

void ui_settings_dbg_page(void)
{
    settings_ui_t* ui = s_page;
    if (!ui) {
        ESP_LOGW(TAG, "设置页：没开着");
        return;
    }
    char gain[16] = "";
    lv_dropdown_get_selected_str(ui->gain_dd, gain, sizeof gain);
    lv_obj_t* content = lv_obj_get_parent(lv_obj_get_parent(ui->info_lbl));
    ESP_LOGW(TAG, "设置页：%s，WiFi「%s」，触摸板模式%s，触摸板加速 %s（%s），信息「%s」，内容还能往下滚 %d",
             lv_screen_active() == s_scr ? "在前台" : "不在前台", lv_label_get_text(ui->wifi_lbl),
             lv_obj_has_state(ui->tpad_sw, LV_STATE_CHECKED) ? "开" : "关", gain,
             lv_obj_has_state(ui->gain_dd, LV_STATE_DISABLED) ? "置灰" : "可拨", lv_label_get_text(ui->info_lbl),
             (int)lv_obj_get_scroll_bottom(content));
}
