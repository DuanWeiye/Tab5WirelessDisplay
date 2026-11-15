// 页面导航与息屏管理
#pragma once

#include <lvgl.h>

#ifdef __cplusplus
extern "C" {
#endif

// 入口：建主题 + 主页面 + 息屏管理（在 bsp_display_lock 内调用）
void ui_init(void);

// 页面切换（各页面用后即焚，切换时重建）。一共四页：主页面、它下面的设置页和「已配对的电脑」页、设置页下面的 WiFi 页
void ui_open_main(void);        // 主页面（开机就是它）：连接状态 + 常动的几样设置。等不等电脑连由 app 管（main/app.hpp），和这一页开没开着无关
void ui_main_refresh(void);     // 暂停 / 恢复了：主页面开着的话马上照新状态画，不等它自己的定时器。在 LVGL 线程里、或者拿着 LVGL 锁调
bool ui_main_in_front(void);    // 主页面是不是在前台（同上）
void ui_open_settings(void);    // 主页面 → 设置：不常动的几样（WiFi、触摸板模式和加速、息屏时间、充电上限、信息）
void ui_open_wifi(void);        // 设置 → WiFi（主页面没联网时点状态也到这里）：开关 / 已保存网络 / 扫描连接。返回键回它是从哪一页来的
void ui_open_pairs(void);       // 主页面 → 已配对的电脑：逐台移除
void ui_main_dbg_page(void);            // 调试：打印主页面现在的样子（在不在前台、暂停没有、状态、几个控件）
void ui_settings_dbg_page(void);        // 调试：打印设置页现在的样子（在不在前台、几个控件）
void ui_main_dbg_start(void);           // 调试：点主页面上的「开始显示」（暂停时才有）
void ui_main_dbg_volume(int pct);       // 调试：把主页面上的「音量」滑块拖到 pct 再松手（和手指走同一条路：当场生效、松手存盘）

// 页面切换过渡(底色淡入淡出，绕开每帧整屏 45ms PPA 旋转)。见 ui_transition.cpp。
// 用法:各 ui_open_* 开头调 ui_fade_out()(先黑屏)，建好新屏后结尾调 ui_fade_in(scr)。
// 这样慢速建页被黑屏遮挡，不会卡在旧画面上。
void ui_fade_out(void);
void ui_fade_in(lv_obj_t* scr);

// ── 导航辅助 ──────────────────────────────────────────────────
// 返回键/侧滑的目标：默认回主页面；上一级不是主页面的（WiFi 页）建完框架自己再设。
typedef void (*ui_page_fn)(void);
void ui_set_back_action(ui_page_fn fn);  // 设置返回目标（ui_page_frame 内每次重置为主页面）
void ui_nav_back(void);                  // 执行返回（返回键/侧滑调用）
// 记录"当前页面"开页函数：息屏→亮屏后据此恢复到原页面。各开页函数开头调一次。
void ui_nav_record(ui_page_fn opener);
void ui_nav_restore(void);               // 亮屏时调用：回到记录的页面（默认主页面）
ui_page_fn ui_nav_current(void);         // 记录的"当前页面"（调试台报现在在哪一页用）

// 息屏管理
void scrmgr_init(void);
void scrmgr_screen_off(void);   // 立即息屏（调用方负责删除旧屏幕）
void scrmgr_request_off(void);  // 从 UI 事件里安全请求息屏（异步执行）
void scrmgr_inhibit(bool on);   // true=禁止息屏(常亮)；false=撤回。可以嵌套（每个 true 配一个 false），都撤回后从此刻重新计时
void scrmgr_wake(void);         // 唤醒（LVGL 上下文；黑屏页双击/调试命令/要弹配对密码框时）
void scrmgr_set_wake_hook(void (*fn)(void));    // 亮屏、页面建回来之后要调的函数（一个；app 用它把没答完的配对密码框再弹出来）
void scrmgr_stream_begin(void); // 电脑的画面要上屏了：息屏着就点亮（拿着 LVGL 锁调，哪个任务都行）
void scrmgr_stream_end(bool reopen);    // 屏幕还给界面了：息屏着被点亮的那一次、或者 reopen（调用方要换页），把记录的页面建出来（拿着 LVGL 锁调）
bool scrmgr_is_off(void);       // 当前是否息屏
void scrmgr_watch_indev(lv_indev_t* indev);     // 给一个指针输入设备挂上「按下就升到满速」（开机时已有的设备 scrmgr_init 自己挂；之后才建的要调这个）
void scrmgr_profiler(bool on);  // 帧探针开关：逐帧打印 脏区/渲染耗时/帧间隔/PPA旋转耗时
bool scrmgr_profiler_on(void);  // 帧探针是否开着（其它模块据此决定要不要打详细诊断日志）
void ui_dbg_autoscroll(void);   // 调试：自动滚动当前页第一个可滚动容器（配合帧探针量滚动性能）
void ui_dbg_benchpage(int variant);     // 调试：合成滚动基准页（A纯色/B圆角/C中文/D位图/E瓦片平移/F边滚边变/G同F带滚动条样式 七变体）。variant 0~6 = 开点名的那个，别的数 = 接着上一次往下轮
void ui_scroll_accel_dbg_enable(bool on);   // 调试：临时关/开滚动加速（量整屏重绘基线）
void ui_scroll_accel_dbg_window(bool begin); // 调试：true 清零加速命中统计；false 打印清零以来的合计（帧探针开 / 关时调，只在 LVGL 线程里调）
void ui_scroll_accel(lv_obj_t* cont);  // 给滚动容器开启"滚动位移复用"加速（整宽/纯纵向/无浮层的页面 opt-in）

#ifdef __cplusplus
}
#endif
