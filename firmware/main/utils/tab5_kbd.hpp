// Tab5 键盘模块（Tab5 Keyboard，A164，STM32F030 矩阵控制器）驱动
//
// 接口：Ext.Port1 → I2C 0x6D（SDA=GPIO0 SCL=GPIO1，INT=GPIO50 低有效）。可以随时插拔。
// 用键盘的「普通模式」：每个键的按下 / 松开各上报一次（行、列），这里换算成 USB HID Boot 键盘报文
// （8 字节，见 kbd_keymap.hpp），按键状态每变一次出一份完整的报文（键码按美式布局给）。
// 寄存器（官方协议文档和官方库 m5stack/M5Unit-KEYBOARD 一致）：
//   0x00 中断使能   0x01 中断状态（写 0 清除并放开 INT）   0x02 队列里的事件数（写 0 清空）
//   0x03 灯的总亮度 0~100   0x10 模式（0 普通 / 1 HID / 2 字符）   0x11 灯的模式（0 跟随状态 / 1 自定义）
//   0x20 普通模式的事件，1 字节：bit7 按下，bit6:4 行，bit3:0 列；队列空读到 0xFF   0x60~0x66 两颗灯的 B G R   0xFE 固件版本
#pragma once

#include <cstdint>

namespace kbd {

struct Report {
    uint8_t b[8];       // 修饰键位图、保留、6 个键码
};

struct Info {
    bool    present;    // 键盘接着
    uint8_t fw;         // 键盘固件版本（没接时是 0）
    Report  now;        // 现在的按键状态
    uint32_t stack_left;    // 任务栈最低余量（字节）
};

// 开机调一次（app_main）：起后台任务，探测键盘、读按键。
void start();

Info info();

// 有人要按键报文（现在只有无线显示）：开着时每次按键状态变化往队列里放一份报文；关着时按键照读、报文不留。
// 开的时候清空队列；关的时候顺带把指示灯灭掉。报文要按顺序一份一份地用，不能只取最新的一份
// （「按下又松开」、同一个键码松开再按下，都是靠中间那一份报文表达的）。
void listen(bool on);

// 取一份报文（不等待）。没有返回 false。
bool pop(Report& r);

// 主机的键盘指示灯状态（bit0 Num，bit1 Caps，bit2 Scroll）。Caps 亮在键盘左边那颗灯上。
void set_leds(uint8_t leds);

// 调试：当成键盘上报了一个按键事件（调试台 kbd key），没接键盘也能走通后面整条路。
void dbg_key(uint8_t row, uint8_t col, bool pressed);
// 调试：每份报文打一行日志
void dbg_log(bool on);

}  // namespace kbd
