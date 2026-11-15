// kbd_keymap.hpp 的电脑端测试：照着键帽上印的字、照着平时打字的按法，核对出来的 HID 报文在美式布局下是不是那个字。
// 跑法：firmware/tools/kbdtest/run.sh
#include "../../main/utils/kbd_keymap.hpp"

#include <cstdio>
#include <random>
#include <string>
#include <vector>

using kbdmap::Tracker;

static int g_fail = 0, g_total = 0;

static void check(const std::string& name, bool ok, const std::string& detail = "")
{
    g_total++;
    if (!ok) g_fail++;
    printf("%s %s%s%s\n", ok ? "✓" : "✗", name.c_str(), detail.empty() ? "" : "：", detail.c_str());
}

// 美式布局：HID 键码 + 有没有 Shift → 打出来的字（不可打印的键给一个名字）
static std::string us_layout(uint8_t code, bool shift)
{
    if (code >= 0x04 && code <= 0x1D) return std::string(1, (char)((shift ? 'A' : 'a') + code - 0x04));
    if (code >= 0x1E && code <= 0x27) return std::string(1, (shift ? "!@#$%^&*()" : "1234567890")[code - 0x1E]);
    switch (code) {
    case 0x28: return "Enter";
    case 0x29: return "Esc";
    case 0x2A: return "Backspace";
    case 0x2B: return "Tab";
    case 0x2C: return "Space";
    case 0x2D: return shift ? "_" : "-";
    case 0x2E: return shift ? "+" : "=";
    case 0x2F: return shift ? "{" : "[";
    case 0x30: return shift ? "}" : "]";
    case 0x31: return shift ? "|" : "\\";
    case 0x33: return shift ? ":" : ";";
    case 0x34: return shift ? "\"" : "'";
    case 0x35: return shift ? "~" : "`";
    case 0x36: return shift ? "<" : ",";
    case 0x37: return shift ? ">" : ".";
    case 0x38: return shift ? "?" : "/";
    case 0x4C: return "Del";
    case 0x4F: return "Right";
    case 0x50: return "Left";
    case 0x51: return "Down";
    case 0x52: return "Up";
    }
    return "?" + std::to_string(code);
}

struct Rep {
    uint8_t b[8];
    std::string hex() const
    {
        char s[32];
        snprintf(s, sizeof s, "%02x %02x %02x %02x %02x %02x %02x %02x", b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7]);
        return s;
    }
    bool is(std::initializer_list<uint8_t> v) const
    {
        uint8_t want[8] = {};
        int     i = 0;
        for (uint8_t x : v) want[i++] = x;
        return memcmp(b, want, 8) == 0;
    }
};

// 美式布局：按着 Shift 打这个字，出来的是什么（<Esc> 这类名字原样返回）
static std::string us_shifted(const std::string& ch)
{
    static const std::string lo = "`1234567890-=[]\\;',./", hi = "~!@#$%^&*()_+{}|:\"<>?";
    if (ch.size() != 1) return ch;
    if (ch[0] >= 'a' && ch[0] <= 'z') return std::string(1, (char)(ch[0] - 'a' + 'A'));
    size_t i = lo.find(ch[0]);
    return i == std::string::npos ? ch : std::string(1, hi[i]);
}

// 主机那一侧：收到一串报文之后「打出来的字」。新出现在报文里的键码算一次按下，按这份报文里的 Shift 换成字
// （协议规定的注入顺序：先松掉不在报文里的键，再改修饰键，最后按新出现的键）。
struct Host {
    Rep         last = {};
    std::string typed;
    void feed(const Rep& r)
    {
        for (int i = 2; i < 8; i++) {
            if (!r.b[i]) continue;
            bool was = false;
            for (int j = 2; j < 8; j++) was |= last.b[j] == r.b[i];
            if (was) continue;
            std::string c = us_layout(r.b[i], (r.b[0] & 0x22) != 0);
            typed += c.size() == 1 ? c : "<" + c + ">";
        }
        last = r;
    }
};

struct Kbd {
    Tracker t;
    Host    host;
    std::vector<Rep> reps;
    void key(int row, int col, bool down)
    {
        uint8_t out[2][8];
        int     n = t.on_key((uint8_t)row, (uint8_t)col, down, out);
        for (int i = 0; i < n; i++) {
            Rep r;
            memcpy(r.b, out[i], 8);
            reps.push_back(r);
            host.feed(r);
        }
    }
    void tap(int row, int col) { key(row, col, true); key(row, col, false); }
    Rep now() const { Rep r; t.report(r.b); return r; }
};

// 键盘上各键的位置
constexpr int SYM[2] = {3, 0}, AA[2] = {3, 1}, CTRL[2] = {4, 0}, ALT[2] = {4, 1};

int main()
{
    // ── 每个键单独按一下，打出来的就是键帽上印的字 ──
    const char* legend[5][14] = {
        {"<Esc>", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "+", "<Del>"},
        {"`", "!", "@", "#", "$", "%", "^", "&", "*", "(", ")", "[", "]", "\\"},
        {"<Tab>", "q", "w", "e", "r", "t", "y", "u", "i", "o", "p", ";", "'", "<Backspace>"},
        {"", "", "a", "s", "d", "f", "g", "h", "j", "k", "l", "<Up>", "_", "<Enter>"},
        {"", "", "z", "x", "c", "v", "b", "n", "m", ".", "<Left>", "<Down>", "<Right>", "<Space>"},
    };
    {
        std::string bad;
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 14; c++) {
                Kbd k;
                k.tap(r, c);
                if (k.host.typed != legend[r][c]) bad += " (" + std::to_string(r) + "," + std::to_string(c) + ")=" + k.host.typed;
                if (!k.now().is({})) bad += " (" + std::to_string(r) + "," + std::to_string(c) + ") 松开后报文不是全 0";
            }
        check("每个键单独按一下 = 键帽上印的字（70 个键）", bad.empty(), bad);
    }

    // ── 按住 Sym 再按：键帽上第二个符号 ──
    {
        struct { int r, c; const char* want; } sym[] = {
            {1, 0, "~"}, {1, 1, "?"}, {1, 8, "/"}, {1, 9, "<"}, {1, 10, ">"}, {1, 11, "{"}, {1, 12, "}"}, {1, 13, "|"},
            {2, 11, ":"}, {2, 12, "\""}, {3, 12, "="}, {4, 9, ","},
            {2, 1, "q"}, {0, 1, "1"}, {1, 2, "@"},      // 没有第二个符号的键不受影响
        };
        std::string bad;
        for (auto& s : sym) {
            Kbd k;
            k.key(SYM[0], SYM[1], true);
            k.tap(s.r, s.c);
            k.key(SYM[0], SYM[1], false);
            if (k.host.typed != s.want) bad += std::string(" ") + s.want + "→" + k.host.typed;
        }
        check("按住 Sym 再按 = 键帽上的第二个符号", bad.empty(), bad);
        Kbd k;
        k.key(SYM[0], SYM[1], true);
        k.key(SYM[0], SYM[1], false);
        check("单按 Sym 不出报文", k.reps.empty());
    }

    // ── 打一句话：Aa 当 Shift ──
    {
        Kbd k;
        auto letter = [&](char ch) {
            const char* rows[3] = {"qwertyuiop", "asdfghjkl", "zxcvbnm"};
            for (int r = 0; r < 3; r++)
                for (int c = 0; rows[r][c]; c++)
                    if (rows[r][c] == ch) { k.tap(r + 2, c + (r == 0 ? 1 : 2)); return; }
        };
        k.key(AA[0], AA[1], true);  letter('h');  k.key(AA[0], AA[1], false);      // H
        for (char ch : std::string("ello")) letter(ch);
        k.tap(4, 9 /* . */);  k.tap(4, 13 /* 空格 */);
        k.tap(1, 1 /* ! */);  k.tap(1, 1);
        k.key(SYM[0], SYM[1], true);  k.tap(1, 1 /* ? */);  k.key(SYM[0], SYM[1], false);
        k.tap(3, 13 /* Enter */);
        check("打「Hello. !!?」再回车", k.host.typed == "Hello.<Space>!!?<Enter>", k.host.typed);
        check("打完之后报文全 0", k.now().is({}), k.now().hex());
    }

    // ── 组合键 ──
    {
        Kbd k;
        k.key(CTRL[0], CTRL[1], true);
        check("按下 Ctrl：只有修饰键", k.now().is({0x01}), k.now().hex());
        k.key(4, 4 /* c */, true);
        check("Ctrl+C", k.now().is({0x01, 0, 0x06}), k.now().hex());
        k.key(4, 4, false);
        k.key(CTRL[0], CTRL[1], false);
        check("都松开", k.now().is({}), k.now().hex());

        k.key(CTRL[0], CTRL[1], true);  k.key(ALT[0], ALT[1], true);  k.key(0, 13 /* Del */, true);
        check("Ctrl+Alt+Del", k.now().is({0x05, 0, 0x4C}), k.now().hex());
        k.key(0, 13, false);  k.key(ALT[0], ALT[1], false);  k.key(CTRL[0], CTRL[1], false);

        k.key(ALT[0], ALT[1], true);  k.tap(2, 0 /* Tab */);  k.tap(2, 0);
        check("Alt 按住、Tab 按两下：Alt 一直在", k.now().is({0x04}), k.now().hex());
        k.key(ALT[0], ALT[1], false);

        k.key(AA[0], AA[1], true);  k.key(4, 10 /* ← */, true);
        check("Shift+←（选中文字）", k.now().is({0x02, 0, 0x50}), k.now().hex());
        k.key(4, 10, false);  k.key(AA[0], AA[1], false);
        check("组合键做完报文全 0", k.now().is({}), k.now().hex());
    }

    // ── 多键同按（打字快的时候上一个键还没松、下一个已经按下）──
    {
        Kbd k;
        k.key(3, 2 /* a */, true);  k.key(3, 3 /* s */, true);  k.key(3, 2, false);  k.key(3, 4 /* d */, true);
        k.key(3, 3, false);  k.key(3, 4, false);
        check("a、s、d 叠着按", k.host.typed == "asd" && k.now().is({}), k.host.typed);

        Kbd w;      // 游戏里常见：w 按住不放，再按 a、空格
        w.key(2, 2 /* w */, true);  w.key(3, 2 /* a */, true);  w.key(4, 13 /* 空格 */, true);
        check("w + a + 空格同时按着：三个键码按先后排", w.now().is({0, 0, 0x1A, 0x04, 0x2C}), w.now().hex());
        w.key(3, 2, false);
        check("松开中间那个，其余的还在", w.now().is({0, 0, 0x1A, 0x2C}), w.now().hex());
    }

    // ── 带强制 Shift 的键和别的键叠着按 ──
    {
        Kbd k;
        k.key(3, 2 /* a */, true);  k.key(1, 1 /* ! */, true);
        check("按住 a 再按 !：Shift + 1", k.now().is({0x02, 0, 0x04, 0x1E}) && k.host.typed == "a!", k.now().hex() + " / " + k.host.typed);
        k.key(1, 1, false);
        check("松开 !：Shift 撤掉，a 还在", k.now().is({0, 0, 0x04}), k.now().hex());
        k.key(3, 2, false);

        Kbd m;
        m.key(1, 1 /* ! */, true);  m.key(3, 2 /* a */, true);
        check("按住 ! 再按 a：打出的是小写 a", m.host.typed == "!a", m.host.typed);

        Kbd d;      // 1 和 ! 是同一个键码
        d.key(0, 1 /* 1 */, true);  d.key(1, 1 /* ! */, true);
        check("1 和 ! 同时按着：键码只出现一次", d.now().is({0x02, 0, 0x1E}), d.now().hex());
        d.key(0, 1, false);
        check("松开 1，! 还按着：键码还在", d.now().is({0x02, 0, 0x1E}), d.now().hex());
        d.key(1, 1, false);
        check("都松开", d.now().is({}), d.now().hex());
        check("整个过程主机打出 1!", d.host.typed == "1!", d.host.typed);
    }

    // ── 两个键是同一个键码、叠着按（打字快的时候 0 还没松就按了 )）：后按的那个字不能丢 ──
    {
        Kbd k;
        k.key(0, 10 /* 0 */, true);  k.key(1, 10 /* ) */, true);
        check("按住 0 再按 )：先出一份没有这个键码的报文，再出 Shift + 0",
              k.reps.size() == 3 && k.reps[0].is({0, 0, 0x27}) && k.reps[1].is({}) && k.reps[2].is({0x02, 0, 0x27}),
              std::to_string(k.reps.size()) + " 份，最后一份 " + k.now().hex());
        k.key(0, 10, false);  k.key(1, 10, false);
        check("主机打出 0)", k.host.typed == "0)" && k.now().is({}), k.host.typed);

        struct { int r1, c1, r2, c2; bool sym1, sym2; const char* want; } pair[] = {
            {1, 10, 0, 10, false, false, ")0"},      // 反过来：按住 ) 再按 0
            {0, 11, 3, 12, false, false, "-_"},
            {3, 12, 0, 11, false, false, "_-"},
            {1, 8, 1, 1, true, true, "/?"},          // Sym + * 是 /，Sym + ! 是 ?
            {3, 12, 0, 12, true, false, "=+"},       // Sym + _ 是 =
            {4, 9, 1, 9, true, true, ",<"},          // Sym + . 是逗号，Sym + ( 是 <
        };
        std::string bad;
        for (auto& p : pair) {
            Kbd q;
            auto press = [&](int r, int c, bool sym) {
                if (sym) q.key(SYM[0], SYM[1], true);
                q.key(r, c, true);
                if (sym) q.key(SYM[0], SYM[1], false);
            };
            press(p.r1, p.c1, p.sym1);  press(p.r2, p.c2, p.sym2);
            q.key(p.r1, p.c1, false);  q.key(p.r2, p.c2, false);
            if (q.host.typed != p.want || !q.now().is({})) bad += std::string(" ") + p.want + "→" + q.host.typed;
        }
        check("同一个键码的两个键叠着按，两个字都打出来（)0、-_、_-、/?、=+、,<）", bad.empty(), bad);

        Kbd t;      // 中间还隔着别的键
        t.key(0, 1 /* 1 */, true);  t.key(3, 2 /* a */, true);  t.key(1, 1 /* ! */, true);
        check("按住 1 和 a 再按 !：打出 1a!", t.host.typed == "1a!" && t.now().is({0x02, 0, 0x1E, 0x04}), t.host.typed + " / " + t.now().hex());

        Kbd f;      // 已经按着 6 个键（其中有 0），再按 )：放不下、不记，也不能让主机以为 0 又按了一次
        for (int c = 2; c <= 6; c++) f.key(3, c, true);
        f.key(0, 10 /* 0 */, true);
        size_t n_rep = f.reps.size();
        f.key(1, 10 /* ) */, true);  f.key(1, 10, false);
        check("按满 6 个键（含 0）之后再按 )：不出报文", f.reps.size() == n_rep && f.host.typed == "asdfg0", f.host.typed);
    }

    // ── 键码在按下那一刻定下来 ──
    {
        Kbd k;
        k.key(SYM[0], SYM[1], true);  k.key(4, 9 /* . → , */, true);  k.key(SYM[0], SYM[1], false);
        check("按住 Sym 按下 . 之后先松 Sym：还是逗号", k.now().is({0, 0, 0x36}), k.now().hex());
        k.key(4, 9, false);
        check("再松开这个键：全 0", k.now().is({}), k.now().hex());
        check("整个过程主机只打出一个逗号", k.host.typed == ",", k.host.typed);
    }

    // ── 超过 6 个键、事件丢失、拔掉键盘 ──
    {
        Kbd k;
        for (int c = 2; c <= 8; c++) k.key(3, c, true);      // a s d f g h j：7 个
        check("同时按 7 个键：报文里是前 6 个", k.now().is({0, 0, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B}), k.now().hex());
        k.key(3, 8 /* j，第 7 个 */, false);
        check("第 7 个键松开：报文不变", k.now().is({0, 0, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B}), k.now().hex());
        for (int c = 2; c <= 7; c++) k.key(3, c, false);
        check("都松开：全 0", k.now().is({}), k.now().hex());

        Kbd l;
        l.key(3, 2, true);      // a 按下，松开事件丢了
        l.key(3, 2, true);      // 再按一次
        check("同一个键连着两次按下（中间的松开丢了）：键码只有一个，主机打出两个 a",
              l.now().is({0, 0, 0x04}) && l.host.typed == "aa", l.now().hex() + " / " + l.host.typed);
        l.key(3, 2, false);
        check("松开后全 0", l.now().is({}), l.now().hex());
        size_t n_rep = l.reps.size();
        l.key(2, 5, false);
        check("没按过的键来了松开事件：不出报文", l.reps.size() == n_rep, std::to_string(l.reps.size()));
        l.key(7, 3, true);  l.key(2, 14, true);
        check("行列超出范围的事件不理会", l.reps.size() == n_rep, std::to_string(l.reps.size()));

        Kbd y;      // 按住 Sym 按下 .（逗号），松开事件丢了，之后不按 Sym 再按 .
        y.key(SYM[0], SYM[1], true);  y.key(4, 9, true);  y.key(SYM[0], SYM[1], false);
        y.key(4, 9, true);
        check("Sym + . 的松开丢了、再单按 .：逗号松掉、句点按下", y.now().is({0, 0, 0x37}) && y.host.typed == ",.", y.now().hex() + " / " + y.host.typed);

        Kbd u;
        u.key(CTRL[0], CTRL[1], true);  u.key(3, 2, true);
        Rep r;
        bool ch = u.t.release_all(r.b);
        check("按着 Ctrl+A 时拔掉键盘：出一份全 0 的报文", ch && r.is({}), r.hex());
        check("已经全松开时再 release_all：不出报文", !u.t.release_all(r.b));
        u.key(3, 2, false);  u.key(CTRL[0], CTRL[1], false);
        check("拔掉之后晚到的松开事件：不出报文", u.reps.size() == 2, std::to_string(u.reps.size()));
    }

    // ── 乱序连打：随机按 / 松全部 70 个键（最多同时按着 6 个普通键，Sym / Aa / Ctrl / Alt 随时按随时松），
    //    每按下一个普通键，主机都应该正好打出那一刻键帽上对应的字（Aa 按着算 Shift），不多不少 ──
    {
        const char* sym_legend[5][14] = {};      // 按住 Sym 时换了字的键
        sym_legend[1][0] = "~";  sym_legend[1][1] = "?";  sym_legend[1][8] = "/";  sym_legend[1][9] = "<";  sym_legend[1][10] = ">";
        sym_legend[1][11] = "{";  sym_legend[1][12] = "}";  sym_legend[1][13] = "|";
        sym_legend[2][11] = ":";  sym_legend[2][12] = "\"";  sym_legend[3][12] = "=";  sym_legend[4][9] = ",";

        std::mt19937 rng(20261112);
        Kbd          k;
        bool         down[5][14] = {};
        std::vector<std::pair<int, int>> held;      // 按着的普通键
        int          n_press = 0, n_overlap = 0;
        std::string  want, first_bad;
        for (int step = 0; step < 40000 && first_bad.empty(); step++) {
            unsigned x = rng() % 100;
            int      r, c;
            if (x < 15) {                                           // Sym / Aa / Ctrl / Alt（第 3、4 行的头两个键）：按着就松，松着就按
                r = 3 + (int)(rng() % 2);
                c = (int)(rng() % 2);
                down[r][c] = !down[r][c];
                k.key(r, c, down[r][c]);
            } else if (!held.empty() && (x < 60 || held.size() == 6)) {     // 松开一个按着的普通键
                size_t i = rng() % held.size();
                r = held[i].first;
                c = held[i].second;
                held.erase(held.begin() + (long)i);
                down[r][c] = false;
                k.key(r, c, false);
            } else {                                                // 按下一个没按着的普通键
                do {
                    r = (int)(rng() % 5);
                    c = (int)(rng() % 14);
                } while (down[r][c] || ((r == 3 || r == 4) && c <= 1));
                std::string ch = down[SYM[0]][SYM[1]] && sym_legend[r][c] ? sym_legend[r][c] : legend[r][c];
                if (down[AA[0]][AA[1]]) ch = us_shifted(ch);
                want += ch;
                n_press++;
                if (!held.empty()) n_overlap++;
                held.push_back({r, c});
                down[r][c] = true;
                k.key(r, c, true);
            }
            if (k.host.typed != want)
                first_bad = "第 " + std::to_string(step) + " 步（" + std::to_string(r) + "," + std::to_string(c) + "）之后：应该是 …"
                            + want.substr(want.size() > 12 ? want.size() - 12 : 0) + "，实际 …"
                            + k.host.typed.substr(k.host.typed.size() > 12 ? k.host.typed.size() - 12 : 0);
        }
        check("乱序连打 4 万个事件（" + std::to_string(n_press) + " 次按下，其中 " + std::to_string(n_overlap) + " 次是别的键还没松就按的）：每次按下正好打出键帽上的字",
              first_bad.empty(), first_bad);
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 14; c++)
                if (down[r][c]) k.key(r, c, false);
        check("连打完全部松开：报文全 0", k.now().is({}), k.now().hex());
    }

    printf("\n共 %d 项，%d 项不过\n", g_total, g_fail);
    return g_fail ? 1 : 0;
}
