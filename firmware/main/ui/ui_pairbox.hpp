// 配对密码框（说明见 ui_pairbox.cpp）。都只在 LVGL 线程里调。
#pragma once

#include "../wdisp.hpp"

// 弹出密码框（已经开着的话：换成这一次的内容；同一次询问重复调不动它）
void ui_pairbox_show(const wdisp::PairPrompt& p);
// 关掉（没开着就什么都不做）
void ui_pairbox_close(void);

void ui_pairbox_dbg_password(const char* pw);   // 调试：框开着时在软键盘上一个字一个字点出 pw、再点「确定」（和手指走同一条路）；pw 给空 = 点「取消」
void ui_pairbox_dbg_box(void);                  // 调试：打印框现在的样子（开没开、标题、在不在核对、标没标红、排布）
