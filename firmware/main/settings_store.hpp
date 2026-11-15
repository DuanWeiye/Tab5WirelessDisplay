// 设置持久化（NVS）：亮度、息屏时间、充电上限，无线显示的几项
#pragma once

#include <cstdint>

namespace settings {

// 「触摸板加速」的几档（设置页的下拉框和调试命令 wd tpad accel 用同一张表）：wd_tpad_gain 能取的值和它们的叫法
constexpr uint8_t           WD_TPAD_GAINS[]      = {10, 15, 20, 30};
constexpr const char* const WD_TPAD_GAIN_NAMES[] = {"关", "低", "中", "高"};
constexpr int               WD_TPAD_GAIN_N       = sizeof(WD_TPAD_GAINS) / sizeof(WD_TPAD_GAINS[0]);

// 画质的四档（主页面上那排按钮和调试命令 wd pic 用同一张表）：wd_pic 存的是第几档，叫法就是界面上的字。
// 每一档电脑怎么压见协议「画质」：最佳画质 = 不限帧率、不降质量；高画质 / 均衡 = 限到屏幕刷新率的 1/2、2/3（30、45 帧上下），在这个帧率上尽量清楚；最佳速度 = 尽量快。
// 不认得档位报文的老发送端收到的是折算出来的一组「质量 / 画面在动时每帧多少 KB」（0 = 不降）：WD_PIC_OLD_QUALITY、WD_PIC_OLD_KB
constexpr const char* const WD_PIC_NAMES[]     = {"最佳画质", "高画质", "均衡", "最佳速度"};
constexpr uint8_t           WD_PIC_OLD_KB[]    = {0, 90, 60, 30};
constexpr uint8_t           WD_PIC_OLD_QUALITY = 90;
constexpr int               WD_PIC_N           = sizeof(WD_PIC_NAMES) / sizeof(WD_PIC_NAMES[0]);
constexpr uint8_t           WD_PIC_DEFAULT     = 2;

struct Values {
    uint8_t  brightness     = 70;   // 0-100
    uint32_t screenoff_s    = 120;  // 0 = 永不息屏（默认 2 分钟）
    uint8_t  charge_limit   = 100;  // 充电上限%（到此停充、电池旁路吃 C 口），100 = 不限制
    // 无线显示的触摸方式：0 = 手指点哪就点哪（电脑那边注入成触摸），1 = 触摸板模式（屏幕当触摸板，电脑那边换成鼠标动作）。
    // 存在 Tab5 上（在设置页上拨）、由 Tab5 告诉电脑（协议 INPUT_MODE）：用的时候人在 Tab5 跟前，电脑可能连显示器都没接。
    uint8_t  wd_touchpad    = 0;
    // 触摸板模式下光标的加速：手指滑得快时光标最多放大到几倍，单位 0.1 倍（10 = 不加速；慢慢滑时总是手指挪多远光标挪多远）。
    // 和上面那个开关一样存在 Tab5 上、由 Tab5 告诉电脑（协议 TPAD_GAIN），放大是电脑那边算的。取值见 WD_TPAD_GAINS。
    uint8_t  wd_tpad_gain   = 20;
    // 无线显示的画质：第几档（见 WD_PIC_NAMES）。存在 Tab5 上、由 Tab5 告诉电脑（协议 PICTURE_MODE），压画面是电脑那边的事
    uint8_t  wd_pic         = WD_PIC_DEFAULT;
    // 显示期间在画面顶上靠右叠一条半透明的调试信息（帧率、延迟）。Tab5 自己画，电脑不知道
    uint8_t  wd_osd         = 0;
    // 无线显示的音量 0~100（0 = 静音）：电脑的声音从 Tab5 放出来有多响。主页面上的「音量」滑块，不跟电脑的系统音量。
    uint8_t  wd_volume      = 60;
};

const Values& get();    // 进程内单例（load 时从 NVS 载入）。谁都可以读
Values&       mut();    // 改设置用。只给 app 的 set_*（见 app.hpp）用：界面和调试命令要改设置调那边，别直接写这里
void          load();
void          save();   // 没变的项 NVS 不会真的写

}  // namespace settings
