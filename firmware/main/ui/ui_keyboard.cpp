// 屏幕软键盘：全工程共用这一个建法（WiFi 密码、KVM 改名、无线显示的配对密码）。
// LVGL 自带的三页（小写 / 大写 / 符号）合起来打不出 ~ ^ | ` 四个字符，这里把符号页换成补齐的，
// 三页合起来能打出全部可打印 ASCII（0x20~0x7E）：密码里什么字符都可能有。
#include "theme.h"

#include <cstring>

#include <esp_log.h>

namespace {

const char* TAG = "ui_kbd";

// 符号页。比 LVGL 自带的多四个键：第二行末尾的 ^ ~，第三行的 |（紧跟 \）和末尾的 `
constexpr const char* const SPEC_MAP[] = {
    "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", LV_SYMBOL_BACKSPACE, "\n",
    "abc", "+", "&", "/", "*", "=", "%", "!", "?", "#", "<", ">", "^", "~", "\n",
    "\\", "|", "@", "$", "(", ")", "{", "}", "[", "]", ";", "\"", "'", "`", "\n",
    LV_SYMBOL_KEYBOARD, LV_SYMBOL_LEFT, " ", LV_SYMBOL_RIGHT, LV_SYMBOL_OK, "",
};

// 各键的控制位，写法照 LVGL 自带的那几页（lv_keyboard.c）：
// 字符键 = 可以弹放大泡；功能键（翻页、收起、✓）= 松手才算、不连发、深色；退格和方向键 = 深色、按住连发。低位是键宽
constexpr lv_buttonmatrix_ctrl_t ch(int w)   { return (lv_buttonmatrix_ctrl_t)(LV_BUTTONMATRIX_CTRL_POPOVER | w); }
constexpr lv_buttonmatrix_ctrl_t fn(int w)   { return (lv_buttonmatrix_ctrl_t)(LV_KEYBOARD_CTRL_BUTTON_FLAGS | w); }
constexpr lv_buttonmatrix_ctrl_t dark(int w) { return (lv_buttonmatrix_ctrl_t)(LV_BUTTONMATRIX_CTRL_CHECKED | w); }
const lv_buttonmatrix_ctrl_t SPEC_CTRL[] = {
    ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), dark(2),
    fn(2), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1),
    ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1), ch(1),
    fn(2), dark(2), (lv_buttonmatrix_ctrl_t)6, dark(2), fn(2),
};

// 键的个数（"\n" 是换行记号，不算键；空串是结尾）
constexpr int count_keys(const char* const* map)
{
    int n = 0;
    for (int i = 0; map[i][0]; i++)
        if (!(map[i][0] == '\n' && !map[i][1])) n++;
    return n;
}
static_assert(count_keys(SPEC_MAP) == sizeof(SPEC_CTRL) / sizeof(SPEC_CTRL[0]), "符号页的键和控制位要一一对应");

const lv_keyboard_mode_t PAGES[] = {LV_KEYBOARD_MODE_TEXT_LOWER, LV_KEYBOARD_MODE_TEXT_UPPER, LV_KEYBOARD_MODE_SPECIAL};

// 在当前这一页上找键面是 label 的键，点它（和手指点到这个键之后走的是同一条路：按钮矩阵报 VALUE_CHANGED）
bool click(lv_obj_t* kb, const char* label)
{
    const char* const* map = lv_keyboard_get_map_array(kb);
    uint32_t id = 0;
    for (int i = 0; map[i][0]; i++) {
        if (strcmp(map[i], "\n") == 0) continue;
        if (strcmp(map[i], label) == 0) {
            lv_buttonmatrix_set_selected_button(kb, id);
            lv_obj_send_event(kb, LV_EVENT_VALUE_CHANGED, &id);
            lv_buttonmatrix_set_selected_button(kb, LV_BUTTONMATRIX_BUTTON_NONE);
            return true;
        }
        id++;
    }
    return false;
}

}  // namespace

lv_obj_t* ui_keyboard_create(lv_obj_t* parent)
{
    lv_obj_t* kb = lv_keyboard_create(parent);
    // 键面上的退格 / ✓ / 收起 / 方向键是 LV_SYMBOL 图标，中文字库里没有：用 Montserrat
    lv_obj_set_style_text_font(kb, &lv_font_montserrat_24, 0);
    // LVGL 里各页的布局是所有键盘共用的一张表，每次建都设一遍也只是写同样的值
    lv_keyboard_set_map(kb, LV_KEYBOARD_MODE_SPECIAL, SPEC_MAP, SPEC_CTRL);
    return kb;
}

bool ui_keyboard_dbg_type(lv_obj_t* kb, const char* text)
{
    if (lv_obj_has_state(kb, LV_STATE_DISABLED)) {
        ESP_LOGW(TAG, "软键盘现在点不了");
        return false;
    }
    for (const char* p = text; *p; p++) {
        const char key[2] = {*p, 0};
        bool       hit    = false;
        // 这一页没有就点翻页键换下一页（小写 → 大写 → 符号 → 小写），三页转一圈
        for (int turn = 0; turn < 3 && !hit; turn++) {
            hit = click(kb, key);
            if (hit) break;
            const lv_keyboard_mode_t m = lv_keyboard_get_mode(kb);
            if (!click(kb, m == LV_KEYBOARD_MODE_TEXT_LOWER ? "ABC" : m == LV_KEYBOARD_MODE_TEXT_UPPER ? "1#" : "abc")) break;
        }
        if (!hit) {
            ESP_LOGW(TAG, "软键盘上打不出第 %d 个字符（0x%02x）", (int)(p - text) + 1, (unsigned)(uint8_t)*p);
            return false;
        }
    }
    return true;
}

int ui_keyboard_dbg_missing(lv_obj_t* kb, char* out, int cap)
{
    bool have[0x7F] = {};
    const lv_keyboard_mode_t now = lv_keyboard_get_mode(kb);
    for (lv_keyboard_mode_t m : PAGES) {
        lv_keyboard_set_mode(kb, m);
        const char* const* map = lv_keyboard_get_map_array(kb);
        for (int i = 0; map[i][0]; i++)
            if (!map[i][1] && map[i][0] >= 0x20 && map[i][0] <= 0x7E) have[(int)map[i][0]] = true;
    }
    lv_keyboard_set_mode(kb, now);
    int n = 0;
    for (int c = 0x20; c <= 0x7E; c++) {
        if (have[c]) continue;
        if (n < cap - 1) out[n] = (char)c;
        n++;
    }
    out[n < cap ? n : cap - 1] = 0;
    return n;
}
