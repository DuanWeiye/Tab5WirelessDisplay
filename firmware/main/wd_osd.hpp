// 无线显示期间叠在画面顶上靠右的一条半透明调试信息（帧率、延迟）。开关是主页面上的「性能信息」（settings 的 wd_osd）。
#pragma once

#include <cstdint>

namespace wdosd {

// 这几个函数都只在无线显示任务里调。
void reset(bool yuv);                                   // 一条连接的画面格式定下来时（握手走完）：统计清零，忘掉哪块帧缓冲上画着浮层。
                                                        // yuv = 这条连接的帧缓冲是 YUV422（字节顺序 U Y0 V Y1，有限范围），否则 RGB565
void frame_shown(uint32_t latency_us);                  // 一帧换上屏幕了：记进这一秒的统计。latency_us = 这一帧第一个字节到达 → 上屏
bool touched(const uint16_t* fb, int row, int h);       // 要让解码器往 fb 的第 [row, row + h) 行写了（排解码活的时候调）。返回这几行里有没有浮层占着的
void draw(uint16_t* fb, int64_t now_us);                // 一帧解完、登记翻页之前调：把浮层混到解码写的那块帧缓冲上；开关关着就把留着的擦掉。
                                                        // 调的时候解码器不能正在写、也不能等着要写浮层占着的那几行（别的行它可以同时在写）
void idle(uint16_t* front, int64_t now_us);             // 没有数据来的时候调：数字变了 / 开关拨了，就在正显示的那块上原地更新。
                                                        // 有登记了还没生效的翻页时不调（front 马上要换下去了）

}  // namespace wdosd
