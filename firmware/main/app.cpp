// 应用层的实现，说明见 app.hpp
#include "app.hpp"

#include "board.hpp"
#include "settings_store.hpp"
#include "ui/ui.h"
#include "ui/ui_pairbox.hpp"
#include "wd_audio.hpp"
#include "wdisp.hpp"

namespace app {

namespace {

bool s_paused = false;      // 只在拿着 LVGL 锁时读写（LVGL 线程里，或者 wdisp 的任务上拿着锁）

// 下面四个是给 wdisp 的回调（wdisp::Ui）。前两个在 LVGL 线程里被调，后两个在 wdisp 的任务上、拿着 LVGL 锁被调。

void on_pair_prompt(const wdisp::PairPrompt& p)
{
    if (s_paused) return;                       // 刚点了取消，这一次询问已经作废（那条连接正在收尾）
    if (scrmgr_is_off()) scrmgr_wake();         // 息屏着：先亮屏，人才看得见、输得了
    ui_pairbox_show(p);
}

void on_pair_close()
{
    ui_pairbox_close();
}

void on_screen_take()
{
    scrmgr_stream_begin();
}

// user_exit = 人在显示期间做了退出手势，wdisp 已经自己停止监听了：记成暂停，界面回主页面（「开始显示」在那一页上）。
// 暂停在这里当场记下，不另排异步：随后建回页面、亮屏钩子（on_wake）看到的就已经是暂停着，不会又把监听交出去
void on_screen_back(bool user_exit)
{
    bool reopen = false;
    if (user_exit) {
        s_paused = true;
        if (ui_main_in_front()) {
            ui_main_refresh();
        } else {                                // 显示之前停在别的页（WiFi、已配对的电脑），或者是息屏着被点亮的
            ui_nav_record(ui_open_main);
            reopen = true;
        }
    }
    scrmgr_stream_end(reopen);
}

void listen()
{
    wdisp::open(wdisp::Ui{on_pair_prompt, on_pair_close, on_screen_take, on_screen_back});
}

// 亮屏之后（界面重建好了）：息屏时顶层的弹框都关掉了，正等着人输配对密码的话让密码框再弹出来（wdisp::open 可以重复调，
// 它会把没答完的那一次询问重新交过来）
void on_wake()
{
    if (!s_paused) listen();
}

int clamp(int v, int lo, int hi)
{
    return v < lo ? lo : v > hi ? hi : v;
}

}  // namespace

void start()
{
    scrmgr_set_wake_hook(on_wake);
    listen();
}

bool paused()
{
    return s_paused;
}

void pause()
{
    s_paused = true;
    wdisp::close();
    ui_pairbox_close();
    ui_main_refresh();
}

void resume()
{
    s_paused = false;
    listen();
    ui_main_refresh();
}

void set_brightness(int pct, bool save)
{
    const uint8_t v = (uint8_t)clamp(pct, 5, 100);
    settings::mut().brightness = v;
    if (board::screenOn()) board::setBrightness(v);     // 息屏着不点亮，亮屏时照新值来
    if (save) settings::save();
}

void set_screenoff(uint32_t seconds)
{
    settings::mut().screenoff_s = seconds;
    settings::save();
}

void set_charge_limit(int pct)
{
    settings::mut().charge_limit = (uint8_t)clamp(pct, 50, 100);    // 界面给的档位是 80~100；充电维持任务每一拍读它，改了就生效
    settings::save();
}

void set_volume(int pct, bool save)
{
    const int v = clamp(pct, 0, 100);
    settings::mut().wd_volume = (uint8_t)v;
    wdaudio::set_volume(v);
    if (save) settings::save();
}

// 下面四项 wdisp / 调试信息浮层每一帧都在读：改了就生效（正连着的话 wdisp 当场告诉电脑）
bool set_picture(int tier)
{
    if (tier < 0 || tier >= settings::WD_PIC_N) return false;
    settings::mut().wd_pic = (uint8_t)tier;
    settings::save();
    return true;
}

void set_touchpad(bool on)
{
    settings::mut().wd_touchpad = on ? 1 : 0;
    settings::save();
}

bool set_touchpad_gain(int gain)
{
    bool known = false;
    for (uint8_t g : settings::WD_TPAD_GAINS) known |= (g == gain);
    if (!known) return false;
    settings::mut().wd_tpad_gain = (uint8_t)gain;
    settings::save();
    return true;
}

void set_osd(bool on)
{
    settings::mut().wd_osd = on ? 1 : 0;
    settings::save();
}

}  // namespace app
