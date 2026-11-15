// 深色主题：配色、字体、通用控件样式
#pragma once

#include <lvgl.h>

#ifdef __cplusplus
extern "C" {
#endif

// ── 配色（深色主题）────────────────────────────────────────────
#define COL_BG        0x0E1116  // 全局背景（近黑）
#define COL_CARD      0x171C26  // 卡片底
#define COL_CARD_HI   0x1F2736  // 卡片按下/高亮
#define COL_BORDER    0x232B3A  // 卡片描边
#define COL_TEXT      0xE9EDF5  // 主文字
#define COL_MUTED     0x8A94A8  // 次要文字
#define COL_ACCENT    0x4E9EFF  // 主题蓝
#define COL_GOOD      0x4ADE80
#define COL_WARN      0xF7B84E
#define COL_BAD       0xF87171

// ── 字体 ──────────────────────────────────────────────────────
LV_FONT_DECLARE(font_cn_20);      // 中文 20px（正文）
LV_FONT_DECLARE(font_cn_28);      // 中文 28px（标题/按钮）
LV_FONT_DECLARE(font_cn_48);      // 中文 48px：只有主页面那行大字状态里的十几个字（改了字样要重新生成），没有 ASCII（字集见 tools/gen_font.py 的 HERO_TEXT）

// ── 通用构件 ──────────────────────────────────────────────────
// 全局主题初始化（深色 + 默认中文字体）
void theme_init(lv_display_t* disp);

// 标准卡片容器
lv_obj_t* ui_card(lv_obj_t* parent);

// 设置页里一组的标题（「显示」「触摸」……）
lv_obj_t* ui_group_title(lv_obj_t* parent, const char* text);
// 设置行：一张 88 高的卡片，左边是标题，右边留给调用方放控件（开关 / 下拉框 / 短状态值）
lv_obj_t* ui_setting_row(lv_obj_t* parent, const char* title);
// 设置行右边的下拉框：opts 是用换行分开的选项，vals 是各选项对应的值（共 n 个），cur 是现在的值（按它选中）
lv_obj_t* ui_dropdown(lv_obj_t* row, const char* opts, const uint32_t* vals, int n, uint32_t cur, lv_event_cb_t cb);
// 设置行右边的拨钮
lv_obj_t* ui_row_switch(lv_obj_t* row);
// 设置行右边的短状态值（几个字的 label）。link = 这一行点了去二级页：值的右边带一个箭头，整行可点、按下时变色
// （点了做什么由调用方给行挂 CLICKED）。返回值的 label（link = true 时它的 user_data 归本构件用，记着箭头）
lv_obj_t* ui_row_value(lv_obj_t* row, bool link);
// 带箭头的行（ui_row_value 的 link = true 建的）现在能不能点进去：不能时收起箭头、整行点不动、值靠到最右边。value 是那一行的值的 label
void      ui_row_set_link(lv_obj_t* value, bool on);
// 分段选择：几个选项摊开成一排按钮（只支持一排：map 里不能有 "\n"），选中的那个是主题色，点一下就换。
// map 是各段的字样、以 "" 结尾，调用方要让它一直有效（控件只记指针）；cur 是一开始选中第几段。
// 人点了别的段（松手时才算；点已经选中的那一段不算）调 cb（事件是 LV_EVENT_VALUE_CHANGED），选中的是第几段用 ui_segmented_get 取。大小由调用方定。
// cb 不能是空的。控件的 user_data 和这个事件的 user_data 归本构件用（记上一次报的是第几段、转给哪个 cb）：调用方别往控件上 set_user_data，cb 里也取不到自己的数据
lv_obj_t* ui_segmented(lv_obj_t* parent, const char* const* map, int cur, lv_event_cb_t cb);
int       ui_segmented_get(lv_obj_t* seg);              // 现在选中第几段（没有选中的 = -1）
void      ui_segmented_set(lv_obj_t* seg, int idx);     // 改成选中第 idx 段（不调 cb；本来就是它的话不动）

// 周期刷新里用的「变了才动」（同值再设一遍也会失效重绘，见 ui_label_set_text_if_changed）
void ui_set_checked(lv_obj_t* o, bool on);              // 拨钮开 / 关
void ui_set_hidden(lv_obj_t* o, bool hide);
void ui_set_disabled(lv_obj_t* o, bool off);            // 置灰、点不动
void ui_label_set(lv_obj_t* label, const char* text, uint32_t color);   // 字样和颜色

// 顶栏：标题 + 状态簇。show_back 为真时带返回键，并支持左右边缘侧滑返回。
// 返回顶栏下方的内容容器（已铺满剩余空间）。
lv_obj_t* ui_page_frame(lv_obj_t* screen, const char* title, bool show_back);
// 顶栏右边、状态簇左边的图标按钮（symbol 是 LV_SYMBOL_*）：主页面的齿轮。screen 要已经过了 ui_page_frame
lv_obj_t* ui_topbar_button(lv_obj_t* screen, const char* symbol, lv_event_cb_t cb);

// 状态簇：WiFi + 电量，手机状态栏样式。
// ui_status_cluster 构建并返回句柄（grp 删除时自动释放）；调用方用 _obj 取容器对齐到右上；
// 调用方在自己的 1s 定时器里调 _update 刷新。
typedef struct status_cluster status_cluster_t;
status_cluster_t* ui_status_cluster(lv_obj_t* parent);
void              ui_status_cluster_update(status_cluster_t* c);
lv_obj_t*         ui_status_cluster_obj(status_cluster_t* c);

// 确认框：标题 + 可选的正文（nullptr = 没有）+ [主操作][取消]。
// 点主操作：调 on_ok(user) 然后关框；点取消：只关框。danger = 主操作不可撤销，按钮用错误色。
// 返回对话框对象（调试量排布用；要关掉它必须用 lv_msgbox_close）。
lv_obj_t* ui_confirm(const char* title, const char* text, const char* ok_text, bool danger,
                     void (*on_ok)(void* user), void* user);

// 「重启设备？」确认框，确认后软件重启。无线模块故障这类要重启才能恢复的状态，点一下状态文字就到这里。
void ui_confirm_restart(void);

// 屏幕软键盘（全工程共用，别直接 lv_keyboard_create）：小写 / 大写 / 符号三页合起来能打出全部可打印 ASCII。大小和位置由调用方定
lv_obj_t* ui_keyboard_create(lv_obj_t* parent);
bool ui_keyboard_dbg_type(lv_obj_t* kb, const char* text);        // 调试：在键面上一个字一个字点出 text（要翻页就点翻页键），和手指走同一条路；有打不出的字符就停下、返回 false
int  ui_keyboard_dbg_missing(lv_obj_t* kb, char* out, int cap);   // 调试：三页合起来打不出的可打印 ASCII 写进 out（以 0 结尾），返回个数

// 文本没变就不动 label（避免周期 timer 无条件 set_text 造成的白重绘，见 theme.cpp 注释）。
// 周期刷新的 label 一律用这个代替 lv_label_set_text(_fmt)。
void ui_label_set_text_if_changed(lv_obj_t* label, const char* text);

#ifdef __cplusplus
}
#endif
