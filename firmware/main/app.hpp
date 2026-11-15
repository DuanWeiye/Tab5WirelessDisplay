// 应用层：这台设备只做无线显示。这里管「界面」和「无线显示服务」（wdisp）之间的那几件事——
//   · 运行 / 暂停：开机就等电脑连；人在显示期间做了退出手势、或者在配对密码框上点了取消就暂停（不接受连接），
//     点主页面上的「开始显示」接着等。暂停不存盘：重启之后总是从等连接开始。
//   · 配对密码框：wdisp 要人输密码时弹出来（息屏着先亮屏），不管界面在哪一页。
//   · 屏幕：电脑的画面要上屏时息屏着就点亮，画面结束把界面还回来。
//   · 设置：界面和调试命令改设置都走这里的 set_*（收进合法范围 → 记下 → 生效 → 存盘），不各写一遍。
// 页面（ui/）只读这里和 wdisp 的状态来画自己，不拥有监听：换页、息屏都不影响等连接和正连着的那一条。
#pragma once

#include <cstdint>

namespace app {

// 开机调一次（拿着 LVGL 锁、WiFi 初始化之后）：开始等电脑连
void start();

// ── 运行 / 暂停（在 LVGL 线程里、或者拿着 LVGL 锁调）──
bool paused();
void pause();       // 不再接受连接；正连着的那一条以 BYE 结束，配对密码框开着的话关掉
void resume();      // 接着等电脑连

// ── 设置 ──
// 值不合法的（不是表里的档位）不改、返回 false；数值类的收进范围。除了 set_brightness，哪个线程都能调。
// save = false 用在拖滑块的中途：只生效、不存盘，松手时再调一次带存盘的。
void set_brightness(int pct, bool save = true);     // 5~100。要看屏幕亮没亮再动背光：在 LVGL 线程里、或者拿着 LVGL 锁调
void set_screenoff(uint32_t seconds);               // 0 = 不息屏
void set_charge_limit(int pct);                     // 100 = 不限制
void set_volume(int pct, bool save = true);         // 0~100，0 = 静音
bool set_picture(int tier);                         // 画质第几档（settings::WD_PIC_NAMES）
void set_touchpad(bool on);
bool set_touchpad_gain(int gain);                   // 必须是 settings::WD_TPAD_GAINS 里的值
void set_osd(bool on);

}  // namespace app
