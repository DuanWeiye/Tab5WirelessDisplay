// UI 入口 + 导航辅助（返回目标 / 息屏页面恢复）
#include "ui.h"
#include "theme.h"

// 返回键/侧滑目标；息屏前的当前页面。二者默认都是主页面。
static ui_page_fn s_back_action  = ui_open_main;
static ui_page_fn s_page_restore = ui_open_main;

void ui_set_back_action(ui_page_fn fn) { s_back_action = fn ? fn : ui_open_main; }
void ui_nav_back(void)                 { s_back_action(); }

void ui_nav_record(ui_page_fn opener)  { if (opener) s_page_restore = opener; }
ui_page_fn ui_nav_current(void)        { return s_page_restore; }
void ui_nav_restore(void)              { s_page_restore(); }

void ui_init(void)
{
    theme_init(lv_display_get_default());
    scrmgr_init();
    ui_open_main();
}
