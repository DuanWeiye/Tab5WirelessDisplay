// 无线显示的调试信息浮层（画面顶上靠右的一条）。
//
// 画面是硬件解码器直接写进帧缓冲的，而且是增量的：没变的条带发送端不重发，Tab5 也不重解；三块帧缓冲轮流上屏。
// 所以浮层不能画上去就不管，也不能每帧整块重画（整屏在动时每帧只有十五毫秒，多花一两毫秒就掉帧）。做法是给每块帧缓冲各记一份账：
//   s_save[k]   浮层那一块下面的干净像素（没有浮层时画面本来的样子）；
//   s_fresh[k]  上次画完之后，解码器又写过这一块里的哪些行（touched()，排解码活的时候记），一行一个标记：写过的行不一定连着
//               （条带切得细、只有两头在变时中间那几行没人写）。解码器写的是整行，写过的行上浮层没了、画面是新的。
//   draw()      登记翻页之前：解码器写过的行重新存干净像素、重新混浮层；没写过的行画面没变，浮层还在，数字没变就不用动。
//               数字变了（一秒一次）或者这块缓冲上还没画过，才整块重混。整屏在动时每帧存一遍、混一遍；只有别处在变时什么都不用做。
//               混一遍要 1 ms 上下（见下面位置的说明），所以不等整帧解完：盖着浮层的那几个条带解完就混，后面的条带同时接着解（wdisp.cpp「调试信息」）。
//   idle()      画面不动时没有帧来，数字照样要更新（帧率该掉到 0）：拿存的干净像素重新混一遍，直接写正在显示的那一块。
//               那一块正被面板扫出，写的这一下浮层里可能撕一道，只是调试信息，不管它。
// 字用界面的 20 号字库直接取字形（位图不压缩，取字形是纯函数），不经过 LVGL 的渲染：显示期间 LVGL 停着，这里也不在它的线程上。
// 帧缓冲是 CPU 经缓存写的，而解码器、面板走 DMA 不经缓存：每次写完都回写（esp_cache_msync），
// 不然缓存里的脏行不知道什么时候才落下去，会盖掉解码器后来写的内容。
// 帧缓冲有两种格式（一条连接用哪种在握手时定，见 wdisp.cpp「帧缓冲的格式」），都是每像素 2 字节，存干净像素、擦掉、记账两种格式是同一套，
// 只有混色分两种写法（blend_rgb565 / blend_yuv422）。

#include "wd_osd.hpp"

#include <cstdio>
#include <cstring>

#include "esp_cache.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "lvgl.h"

#include "settings_store.hpp"
#include "ui/theme.h"

namespace wdosd {

static const char* TAG = "wdosd";

constexpr int FB_W = 720, FB_H = 1280;      // 帧缓冲（竖屏排列）；横屏画面 (x, y) 在帧缓冲第 (1279 − x) 行、第 y 列
// 浮层的位置和大小。帧缓冲里它是 [ROW0, ROW0 + W) 行、[Y0, Y0 + H) 列，也就是横屏画面顶上、从右边数第 ROW0 个像素起往左的一条
// （第 ROW0 + i 行是横屏的第 1279 − ROW0 − i 列）。
//   · 每帧的开销主要是读帧缓冲：W 行、每行 H 个像素的一小段，各行隔着一整行（1440 字节），一行就是一次缓存不命中（帧缓冲在 PSRAM 里），
//     252 行存一遍、混一遍约 1 ms（量的时候浮层是 252 行）。所以 W 只留到够放最长的一行字（"99.9 fps / 999 ms" 宽 155，两边各留 PAD）；贴着上边（Y0 = 0），每行那 64 字节多数落在一个 128 字节的缓存行里。
//   · 靠右：帧缓冲第 0 行是横屏最右边一列，发送端按行的先后发条带，靠右的最先解完，后面的条带还在解的时候就能混（不占每帧的时间）。
//   · 不贴着右边，空出 ROW0 = 160 个像素：那里是最大化窗口的关闭 / 最大化 / 最小化按钮，别盖住。
constexpr int ROW0 = 160, Y0 = 0;
constexpr int W = 176, H = 32;
constexpr int PAD = 10;                     // 字离浮层左边多远
static_assert(Y0 % 2 == 0 && H % 2 == 0, "YUV422 下一对像素（同一行相邻两列）共用色度：浮层在每一行上要从偶数列起、占偶数个像素");
// 字的颜色：青色（红 0、绿蓝拉满）。YUV422 帧缓冲里是有限范围的 BT.601（DSI 桥按这个换算回 RGB）：Y = 170，Cb = 166，Cr = 16
constexpr int INK_Y = 170, INK_U = 166, INK_V = 16;
constexpr int64_t PERIOD_US = 1000 * 1000;  // 数字多久算一次
constexpr int64_t IDLE_US   = 250 * 1000;   // idle() 多久才真的看一眼

static uint8_t*  s_mask = nullptr;                  // 字的不透明度 0~255，和帧缓冲同一个排列 [i * H + y]（i 见 ROW0）
static bool      s_ink[W];                          // 第 i 行上有没有字（没有的行只压暗，不用逐个像素混）
constexpr int    NFB = 3;                           // 无线显示轮流用的帧缓冲有几块（wdisp.cpp「翻页」）
static uint16_t* s_save[NFB]  = {};                 // 各块帧缓冲被浮层盖住的干净像素，帧缓冲排列 [i * H + y]
static uint16_t* s_owner[NFB] = {};                 // s_save[k] 是哪一块帧缓冲的
static bool      s_drawn[NFB] = {};                 // 那一块帧缓冲上画着浮层（s_save[k] 有效）
static uint32_t  s_ver[NFB]   = {};                 // 画的是第几版文字
static bool      s_yuv = false;                     // 这条连接的帧缓冲是 YUV422（否则 RGB565），见 reset()
static bool      s_fresh[NFB][W];                    // 画完之后解码器写过浮层里的第 i 行（见 ROW0）：帧缓冲里那一行现在是没有浮层的新画面
static char      s_text[48] = "";
static uint32_t  s_text_ver = 0;                    // s_text 每变一次加 1
static int64_t   s_win_t0 = 0, s_idle_at = 0;
static uint32_t  s_win_frames = 0;
static uint64_t  s_win_latency = 0;

static bool alloc()
{
    if (s_mask) return true;
    uint8_t*  m = (uint8_t*)heap_caps_malloc(W * H, MALLOC_CAP_SPIRAM);
    uint16_t* save[NFB];
    bool      ok = m != nullptr;
    for (int k = 0; k < NFB; k++) {
        save[k] = (uint16_t*)heap_caps_malloc(W * H * 2, MALLOC_CAP_SPIRAM);
        ok = ok && save[k];
    }
    if (!ok) {
        heap_caps_free(m);
        for (int k = 0; k < NFB; k++) heap_caps_free(save[k]);
        ESP_LOGE(TAG, "内存不够，调试信息不显示");
        return false;
    }
    memset(m, 0, W * H);
    s_mask = m;
    for (int k = 0; k < NFB; k++) s_save[k] = save[k];
    return true;
}

// 取 UTF-8 串里从 i 起的一个字，i 挪到下一个字。串是这个文件里自己拼的，不会有半截的字
static uint32_t utf8_next(const char* s, uint32_t& i)
{
    const uint8_t c = (uint8_t)s[i++];
    if (c < 0x80) return c;
    const int more = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : 1;
    uint32_t  ch   = c & (0x3F >> more);
    for (int k = 0; k < more && s[i]; k++) ch = (ch << 6) | ((uint8_t)s[i++] & 0x3F);
    return ch;
}

// 把一行字排进 s_mask（竖直居中，从左边 PAD 起；排不下的截掉）。
// 这里不在 LVGL 的线程上、也没拿它的锁：靠的是这个字库的位图不压缩（取字形只读字库的常量数据）。换成压缩的字库就不能这样取了
static void render(const char* text)
{
    memset(s_mask, 0, W * H);
    memset(s_ink, 0, sizeof s_ink);
    const lv_font_t* font = &font_cn_20;
    static uint8_t glyph[40 * 40];          // 一个字形的位图（20 号字最大也就二十几见方）
    lv_draw_buf_t  db;
    memset(&db, 0, sizeof db);
    db.data = glyph;
    db.data_size = sizeof glyph;
    const int top = (H - font->line_height) / 2 + font->line_height - font->base_line;      // 基线在浮层里的第几行
    int      pen = PAD;
    uint32_t i = 0;
    while (text[i]) {
        const uint32_t ch = utf8_next(text, i);
        lv_font_glyph_dsc_t g;
        if (!lv_font_get_glyph_dsc(font, &g, ch, 0)) continue;
        if (g.box_w && g.box_h && (int)g.box_w * g.box_h <= (int)sizeof glyph && lv_font_get_glyph_bitmap(&g, &db)) {
            const int gx = pen + g.ofs_x, gy = top - g.box_h - g.ofs_y;
            for (int y = 0; y < g.box_h; y++) {
                if (gy + y < 0 || gy + y >= H) continue;
                for (int x = 0; x < g.box_w; x++) {
                    const uint8_t a = glyph[y * g.box_w + x];       // 位图一行就是 box_w 字节（步长不对齐）
                    if (!a || gx + x < 0 || gx + x >= W) continue;
                    const int i = W - 1 - (gx + x);                 // 横屏的第 x 列是帧缓冲里浮层的第 W − 1 − x 行
                    s_mask[i * H + gy + y] = a;
                    s_ink[i] = true;
                }
            }
        }
        pen += g.adv_w;
        if (pen >= W) break;
    }
}

// 到点了就按这一秒的统计重新排字。返回字变了没有
static bool update_text(int64_t now)
{
    if (s_win_t0 == 0) s_win_t0 = now;
    char text[sizeof s_text];
    if (s_text[0] == '\0') {
        strlcpy(text, "-- fps", sizeof text);         // 才打开的第一秒还没有数：从现在起重新数（关着的时候 frame_shown 也在累加）
        s_win_t0 = now;
        s_win_frames = 0;
        s_win_latency = 0;
    } else if (now - s_win_t0 < PERIOD_US) {
        return false;
    } else {
        const uint32_t fps10 = (uint32_t)((uint64_t)s_win_frames * 10000000 / (uint64_t)(now - s_win_t0));
        if (s_win_frames) {
            // 延迟最多显示到 999：浮层的宽度是按三位数留的，链路卡住一秒以上时四位数排不下
            const uint64_t ms = (s_win_latency / s_win_frames + 500) / 1000;
            snprintf(text, sizeof text, "%u.%u fps / %u ms", (unsigned)(fps10 / 10), (unsigned)(fps10 % 10), (unsigned)(ms < 999 ? ms : 999));
        } else {
            strlcpy(text, "0 fps", sizeof text);
        }
        s_win_t0 = now;
        s_win_frames = 0;
        s_win_latency = 0;
    }
    if (strcmp(text, s_text) == 0) return false;
    strlcpy(s_text, text, sizeof s_text);
    s_text_ver++;
    render(s_text);
    return true;
}

static int slot_of(const uint16_t* fb, bool take)
{
    for (int k = 0; k < NFB; k++) if (s_owner[k] == fb) return k;
    if (take)
        for (int k = 0; k < NFB; k++) if (!s_owner[k]) { s_owner[k] = (uint16_t*)fb; s_drawn[k] = false; return k; }
    return -1;
}

// 浮层里第 [lo, hi) 行在帧缓冲里的那一段回写到内存
static void flush(uint16_t* fb, int lo, int hi)
{
    uint16_t* p = fb + (size_t)(ROW0 + lo) * FB_W + Y0;
    esp_cache_msync(p, ((size_t)(hi - lo - 1) * FB_W + H) * 2, ESP_CACHE_MSYNC_FLAG_DIR_C2M | ESP_CACHE_MSYNC_FLAG_UNALIGNED);
}

// RGB565 的一行：底色压暗一半，字是青色
static void blend_rgb565(uint16_t* dst, const uint16_t* src, const uint8_t* m)
{
    if (!m) {
        for (int y = 0; y < H; y++) dst[y] = (src[y] >> 1) & 0x7BEF;
        return;
    }
    for (int y = 0; y < H; y++) {
        uint32_t px = (src[y] >> 1) & 0x7BEF;
        const uint32_t a = m[y] + (m[y] >> 7);      // 0~256
        if (a) {
            const uint32_t r = px >> 11, g = (px >> 5) & 0x3F, b = px & 0x1F;
            px = ((r - ((r * a) >> 8)) << 11) | ((g + (((63 - g) * a) >> 8)) << 5) | (b + (((31 - b) * a) >> 8));
        }
        dst[y] = (uint16_t)px;
    }
}

// YUV422 的一行：每两个像素 4 个字节 U Y0 V Y1，有限范围（黑是 Y = 16，没有颜色是 U = V = 128）。
// 压暗一半 = 亮度往 16 靠一半、色度往 128 靠一半。字：亮度逐像素混，色度按这一对像素的平均不透明度混
static void blend_yuv422(uint8_t* dst, const uint8_t* src, const uint8_t* m)
{
    for (int y = 0; y < H; y += 2, dst += 4, src += 4) {
        int u = (src[0] + 128) >> 1, y0 = (src[1] + 16) >> 1, v = (src[2] + 128) >> 1, y1 = (src[3] + 16) >> 1;
        if (m && (m[y] | m[y + 1])) {
            const int a0 = m[y] + (m[y] >> 7), a1 = m[y + 1] + (m[y + 1] >> 7), ac = (a0 + a1) >> 1;     // 0~256
            y0 += ((INK_Y - y0) * a0) >> 8;
            y1 += ((INK_Y - y1) * a1) >> 8;
            u  += ((INK_U - u) * ac) >> 8;
            v  += ((INK_V - v) * ac) >> 8;
        }
        dst[0] = (uint8_t)u;  dst[1] = (uint8_t)y0;  dst[2] = (uint8_t)v;  dst[3] = (uint8_t)y1;
    }
}

// 第 [lo, hi) 行：把干净像素（s_save[k]；fresh = 这几行解码器刚写过，先从 fb 重新存一份）混上浮层写进 fb。
// 整块（176 行）存一遍、混一遍约 0.9 ms：从帧缓冲读 0.4、混 0.3、回写 0.2（2026-11-15 量的，两种格式差不多）
static void blend(uint16_t* fb, int k, int lo, int hi, bool fresh)
{
    for (int i = lo; i < hi; i++) {
        uint16_t* dst = fb + (size_t)(ROW0 + i) * FB_W + Y0;
        uint16_t* src = s_save[k] + i * H;
        if (fresh) memcpy(src, dst, H * 2);
        const uint8_t* m = s_ink[i] ? s_mask + i * H : nullptr;     // 没有字的行只压暗
        if (s_yuv) blend_yuv422((uint8_t*)dst, (const uint8_t*)src, m);
        else blend_rgb565(dst, src, m);
    }
    flush(fb, lo, hi);
}

// 把 fb 上的浮层擦掉：解码器没写过的行从存的干净像素还原（写过的行本来就干净了）
static void erase(uint16_t* fb, int k)
{
    for (int i = 0; i < W; i++) {
        if (s_fresh[k][i]) continue;
        memcpy(fb + (size_t)(ROW0 + i) * FB_W + Y0, s_save[k] + i * H, H * 2);
    }
    flush(fb, 0, W);
    s_drawn[k] = false;
}

void reset(bool yuv)
{
    s_yuv = yuv;
    for (int k = 0; k < NFB; k++) {
        s_owner[k] = nullptr;
        s_drawn[k] = false;
    }
    s_text[0] = '\0';
    s_win_t0 = s_idle_at = 0;
    s_win_frames = 0;
    s_win_latency = 0;
}

void frame_shown(uint32_t latency_us)
{
    s_win_frames++;
    s_win_latency += latency_us;
}

bool touched(const uint16_t* fb, int row, int h)
{
    const int lo = row - ROW0 < 0 ? 0 : row - ROW0, hi = row + h - ROW0 > W ? W : row + h - ROW0;
    if (lo >= hi) return false;
    const int k = slot_of(fb, false);
    if (k >= 0 && s_drawn[k]) memset(s_fresh[k] + lo, 1, hi - lo);
    return true;
}

void draw(uint16_t* fb, int64_t now_us)
{
    const bool on = settings::get().wd_osd != 0;
    int k = slot_of(fb, false);
    if (!on) {
        if (k >= 0 && s_drawn[k]) erase(fb, k);         // 关掉之后这块缓冲头一回再用：把留着的浮层擦掉
        s_text[0] = '\0';                               // 再打开时从「-- fps」起、重新数
        return;
    }
    if (!alloc()) return;
    update_text(now_us);
    if (k < 0 && (k = slot_of(fb, true)) < 0) return;
    if (!s_drawn[k]) memset(s_fresh[k], 1, W);          // 头一回画：整块都是没有浮层的画面，都要存
    const bool all = s_ver[k] != s_text_ver;            // 数字变了：没被解码器写过的行也要重混，它们的干净像素还是存着的那份
    // 一段一段地走（写没写过相同的连着的行算一段）。解码器写过的行现在是新画面：重新存一份干净的，混上浮层
    for (int lo = 0; lo < W;) {
        const bool fresh = s_fresh[k][lo];
        int hi = lo + 1;
        while (hi < W && s_fresh[k][hi] == fresh) hi++;
        if (fresh || all) blend(fb, k, lo, hi, fresh);
        lo = hi;
    }
    memset(s_fresh[k], 0, W);
    s_drawn[k] = true;
    s_ver[k] = s_text_ver;
}

void idle(uint16_t* front, int64_t now_us)
{
    if (now_us - s_idle_at < IDLE_US) return;
    s_idle_at = now_us;
    // 正显示的这一块解码器不会去写，draw() 管的那套账在它身上照样成立：数字变了就整块重混，开关关了就擦掉，才打开就画上
    draw(front, now_us);
}

}  // namespace wdosd
