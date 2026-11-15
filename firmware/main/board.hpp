// 板级初始化与硬件读数（Tab5：I2C / IO 扩展器 / INA226 / 显示）
#pragma once

#include <cstdint>

namespace board {

// 电源读数快照（INA226，电池路径）
struct PowerData {
    bool  valid    = false;  // 这次读数是否有效。false = I2C 读失败，其余字段都没有意义，调用方丢弃这次采样
    float voltage  = 0;  // V（电池总线电压，2S）
    float current  = 0;  // A（负 = 充电流入电池，正 = 放电）
    float watts    = 0;  // W
    int   percent  = 0;  // 0-100（按 2S 放电曲线折算）
    bool  charging = false;
    bool  present  = false;  // 是否装有电池（电压滞回判定，详见 readPower）
    bool  full     = false;  // 是否充满（达到充电上限被停充，或不限制时 percent>=100 且 |电流|<10mA）
};

// 初始化全部硬件（I2C → 扩展器 → 充电使能 → INA226 → 显示）。
// 返回前显示已可用、背光已按 brightness（0-100）点亮。
void init(uint8_t brightness_pct);

PowerData readPower();

// 充电上限维持：后台任务按 settings.charge_limit 控制充电开关（到上限停充、掉 10% 滞回重充）。
void chargeGovernorStart();

// 调试台 '`'：打印限充全链路现场（设置值/状态机/软件期望/CHG_EN 硬件回读/实时电参）
void chargeDbgDump();

// 背光亮度 0-100（0 = 熄屏，触摸仍在工作）
void setBrightness(uint8_t pct);

// 电源管理（DFS 动态调频）：配置 max=360/min=90 并持开机满速锁。init 末尾自动调用。
// 仅调 CPU 频率，不开 light sleep（light sleep 会断 SDIO → WiFi 掉线）。
void pmInit();
// 满速锁开关（幂等）：on=锁 360MHz（操作/渲染），off=释放→空闲降到 90MHz。
// 由息屏管理按"无操作时长"调用（无操作数秒后降频，有操作立即升回）。
void cpuFastHold(bool on);

// 屏幕亮/灭的全局唯一状态源（scrmgr 亮灭屏时调 notifyScreen；各后台任务读 screenOn
// 决定采样/轮询节奏——息屏拉长周期省电，亮屏恢复；开机默认亮）。
void notifyScreen(bool on);
bool screenOn();

// 显示面板 IC 名（"ili9881c" / "st7703" / ...，调试命令 ver 用）
const char* panelName();

}  // namespace board
