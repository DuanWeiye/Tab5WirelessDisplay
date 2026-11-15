// Tab5 键盘模块（Tab5 Keyboard，A164）：矩阵按键事件 → USB HID Boot 键盘报文（8 字节：修饰键、保留、6 个键码）。
//
// 纯逻辑，不依赖 ESP-IDF，电脑上能直接编译测试（firmware/tools/kbdtest/）。
// 两张键码表取自官方库 m5stack/M5Unit-KEYBOARD（MIT），src/unit/unit_Tab5Keyboard.cpp 的 KEY_MATRIX_HID_BASE / KEY_MATRIX_HID_SYM。
//
// 键盘用「普通模式」：每个键的按下 / 松开各报一次（行、列）。这里记着哪些键按着，每次变化算出一份完整的报文。
//   - Ctrl / Alt 是 HID 修饰键；Aa 当 Shift 用（按住才有效）；Sym 不是 HID 的键，按住时其它键查 SYM 表（换一层符号）。
//   - 键帽上印的是上档符号的键（! @ # …）在表里带着「强制 Shift」。一份报文只有一个修饰键字节，所以强制 Shift 只看
//     最后按下的那个键：按住 a 再按 !，主机看到 Shift + 1，打出 !；反过来按住 ! 再按 a，Shift 撤掉，打出 a。
//   - 一个键换成哪个键码在按下那一刻就定了：按住 Sym 按下一个键、先松 Sym，这个键松开之前键码不变。
//   - 两个键可以是同一个键码（0 和 )、1 和 !、- 和 _）。主机只认「报文里新出现的键码」，所以新按的键的键码
//     已经在报文里时，一次按下出两份报文：先出一份没有这个键码的，再出新的，主机才知道又按了一次。
//   - 键码按美式（US）键盘布局给：键帽上印的字 = 这份报文在 US 布局的主机上打出来的字。
#pragma once

#include <cstdint>
#include <cstring>

namespace kbdmap {

constexpr int     ROWS = 5, COLS = 14, KEYS = ROWS * COLS;
constexpr uint8_t IDX_SYM = 3 * COLS + 0, IDX_AA = 3 * COLS + 1, IDX_CTRL = 4 * COLS + 0, IDX_ALT = 4 * COLS + 1;
constexpr uint8_t MOD_LCTRL = 0x01, MOD_LSHIFT = 0x02, MOD_LALT = 0x04;
constexpr int     MAX_HELD = 6;       // Boot 报文放得下 6 个键码

struct Map {
    uint8_t code;     // HID 键码（Keyboard 页 0x07）；0 = 这个位置不是普通键
    uint8_t mod;      // 这个键自带的修饰键（0x02 = 强制 Shift）
};

inline constexpr Map BASE[KEYS] = {
    // 第 0 行：Esc 1 2 3 4 5 6 7 8 9 0 - + Del
    {0x29, 0}, {0x1E, 0}, {0x1F, 0}, {0x20, 0}, {0x21, 0}, {0x22, 0}, {0x23, 0},
    {0x24, 0}, {0x25, 0}, {0x26, 0}, {0x27, 0}, {0x2D, 0}, {0x2E, 2}, {0x4C, 0},
    // 第 1 行：` ! @ # $ % ^ & * ( ) [ ] 反斜杠
    {0x35, 0}, {0x1E, 2}, {0x1F, 2}, {0x20, 2}, {0x21, 2}, {0x22, 2}, {0x23, 2},
    {0x24, 2}, {0x25, 2}, {0x26, 2}, {0x27, 2}, {0x2F, 0}, {0x30, 0}, {0x31, 0},
    // 第 2 行：Tab q w e r t y u i o p ; ' Backspace
    {0x2B, 0}, {0x14, 0}, {0x1A, 0}, {0x08, 0}, {0x15, 0}, {0x17, 0}, {0x1C, 0},
    {0x18, 0}, {0x0C, 0}, {0x12, 0}, {0x13, 0}, {0x33, 0}, {0x34, 0}, {0x2A, 0},
    // 第 3 行：Sym Aa a s d f g h j k l ↑ _ Enter
    {0x00, 0}, {0x00, 0}, {0x04, 0}, {0x16, 0}, {0x07, 0}, {0x09, 0}, {0x0A, 0},
    {0x0B, 0}, {0x0D, 0}, {0x0E, 0}, {0x0F, 0}, {0x52, 0}, {0x2D, 2}, {0x28, 0},
    // 第 4 行：Ctrl Alt z x c v b n m . ← ↓ → 空格
    {0x00, 0}, {0x00, 0}, {0x1D, 0}, {0x1B, 0}, {0x06, 0}, {0x19, 0}, {0x05, 0},
    {0x11, 0}, {0x10, 0}, {0x37, 0}, {0x50, 0}, {0x51, 0}, {0x4F, 0}, {0x2C, 0},
};

// 按住 Sym 时的表：只有键帽上另印了符号的键和 BASE 不同
inline constexpr Map SYM[KEYS] = {
    // 第 0 行：和 BASE 一样
    {0x29, 0}, {0x1E, 0}, {0x1F, 0}, {0x20, 0}, {0x21, 0}, {0x22, 0}, {0x23, 0},
    {0x24, 0}, {0x25, 0}, {0x26, 0}, {0x27, 0}, {0x2D, 0}, {0x2E, 2}, {0x4C, 0},
    // 第 1 行：` → ~，! → ?，* → /，( → <，) → >，[ → {，] → }，反斜杠 → |
    {0x35, 2}, {0x38, 2}, {0x1F, 2}, {0x20, 2}, {0x21, 2}, {0x22, 2}, {0x23, 2},
    {0x24, 2}, {0x38, 0}, {0x36, 2}, {0x37, 2}, {0x2F, 2}, {0x30, 2}, {0x31, 2},
    // 第 2 行：; → :，' → "
    {0x2B, 0}, {0x14, 0}, {0x1A, 0}, {0x08, 0}, {0x15, 0}, {0x17, 0}, {0x1C, 0},
    {0x18, 0}, {0x0C, 0}, {0x12, 0}, {0x13, 0}, {0x33, 2}, {0x34, 2}, {0x2A, 0},
    // 第 3 行：_ → =
    {0x00, 0}, {0x00, 0}, {0x04, 0}, {0x16, 0}, {0x07, 0}, {0x09, 0}, {0x0A, 0},
    {0x0B, 0}, {0x0D, 0}, {0x0E, 0}, {0x0F, 0}, {0x52, 0}, {0x2E, 0}, {0x28, 0},
    // 第 4 行：. → ,
    {0x00, 0}, {0x00, 0}, {0x1D, 0}, {0x1B, 0}, {0x06, 0}, {0x19, 0}, {0x05, 0},
    {0x11, 0}, {0x10, 0}, {0x36, 0}, {0x50, 0}, {0x51, 0}, {0x4F, 0}, {0x2C, 0},
};

class Tracker {
public:
    // 一个按键事件（行、列、按下 / 松开）。返回这次出了几份报文（0 = 报文没变），按先后放在 out[0]、out[1] 里，要按这个顺序都发出去。
    // 出两份的情况：新按的键的键码已经在报文里（见文件开头）。
    int on_key(uint8_t row, uint8_t col, bool pressed, uint8_t out[2][8])
    {
        if (row >= ROWS || col >= COLS) return 0;
        const uint8_t idx = (uint8_t)(row * COLS + col);
        int n = 0;
        if (idx == IDX_SYM) sym_ = pressed;
        else if (idx == IDX_AA) aa_ = pressed;
        else if (idx == IDX_CTRL) ctrl_ = pressed;
        else if (idx == IDX_ALT) alt_ = pressed;
        else if (pressed) {
            const Map m = sym_ ? SYM[idx] : BASE[idx];
            if (!m.code) return 0;
            release(idx);                       // 松开事件丢过的话，这个键还记着：按新的算
            if (n_ < MAX_HELD) {                // 第 7 个键放不下，不记（它松开时也就没有动作）
                // 这个键码已经在报文里（另一个按着的键是同一个键码，或者上面刚去掉的就是它自己）：先出一份没有它的
                hide_ = m.code;
                n += changed(out[n]);
                hide_ = 0;
                held_[n_++] = Held{idx, m.code, m.mod};
            }
        } else {
            release(idx);
        }
        n += changed(out[n]);
        return n;
    }

    // 全部松开（键盘拔掉了）。返回 true = 报文变了。
    bool release_all(uint8_t out[8])
    {
        n_   = 0;
        sym_ = aa_ = ctrl_ = alt_ = false;
        return changed(out);
    }

    // 现在的报文
    void report(uint8_t out[8]) const
    {
        memset(out, 0, 8);
        out[0] = (uint8_t)((ctrl_ ? MOD_LCTRL : 0) | (aa_ ? MOD_LSHIFT : 0) | (alt_ ? MOD_LALT : 0) | (n_ ? held_[n_ - 1].mod : 0));
        int k = 2;
        for (int i = 0; i < n_; i++) {
            bool skip = held_[i].code == hide_;     // 两个键可以是同一个键码（1 和 !），报文里只放一次
            for (int j = 2; j < k; j++) skip |= out[j] == held_[i].code;
            if (!skip) out[k++] = held_[i].code;
        }
    }

private:
    struct Held { uint8_t idx, code, mod; };

    void release(uint8_t idx)
    {
        for (int i = 0; i < n_; i++) {
            if (held_[i].idx != idx) continue;
            memmove(&held_[i], &held_[i + 1], (size_t)(n_ - i - 1) * sizeof(Held));
            n_--;
            return;
        }
    }

    bool changed(uint8_t out[8])
    {
        report(out);
        if (memcmp(out, last_, 8) == 0) return false;
        memcpy(last_, out, 8);
        return true;
    }

    Held    held_[MAX_HELD] = {};      // 按着的普通键，按按下的先后排
    int     n_ = 0;
    bool    sym_ = false, aa_ = false, ctrl_ = false, alt_ = false;
    uint8_t hide_ = 0;                 // 不是 0 = report() 里不放这个键码（只在 on_key 出中间那份报文时用）
    uint8_t last_[8] = {};
};

}  // namespace kbdmap
