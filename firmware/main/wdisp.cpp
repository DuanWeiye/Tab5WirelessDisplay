// 无线显示接收端实现。说明见 wdisp.hpp，协议见 windisplay/PROTOCOL.md。
#include "wdisp.hpp"

#include <atomic>
#include <cerrno>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/semphr.h>
#include <freertos/task.h>
#include <freertos/idf_additions.h>   // xTaskCreatePinnedToCoreWithCaps
#include <esp_cache.h>
#include <esp_heap_caps.h>
#include <esp_lcd_touch.h>
#include <esp_log.h>
#include <esp_lvgl_port.h>
#include <esp_pm.h>
#include <esp_random.h>
#include <esp_timer.h>
#include <lvgl.h>
#include <lwip/inet.h>
#include <lwip/sockets.h>

#include "bsp/m5stack_tab5.h"
#include "net/wifi.hpp"
#include "settings_store.hpp"
#include "utils/hwjpeg.hpp"
#include "utils/tab5_kbd.hpp"
#include "wd_audio.hpp"
#include "wd_osd.hpp"
#include "wdisp_crypto.hpp"
#include "wdisp_pair.hpp"

extern "C" esp_lcd_touch_handle_t bsp_display_get_touch_handle(void);   // 定义在板级包里，没有头文件声明

namespace wdisp {

static const char* TAG = "wdisp";

// ── 协议常量 ─────────────────────────────────────────────────────────────
constexpr int      FB_W = 720, FB_H = 1280;        // 帧缓冲（面板原生竖屏）
constexpr int      VIEW_W = 1280, VIEW_H = 720;    // 人看到的横屏画面
constexpr int      STRIP_ALIGN = 16;               // 条带的起始行和高都是它的倍数
constexpr int      UNITS = FB_H / STRIP_ALIGN;     // 帧缓冲按 16 行一格分成 80 格
constexpr uint32_t MAX_STRIP_BYTES = 256 * 1024;
constexpr uint32_t MAX_FRAME_BYTES = 1024 * 1024;
constexpr uint16_t PROTO_VER = 2;
constexpr int64_t  STALL_US       = 5 * 1000 * 1000;    // 一个报文发到一半，这么久没有新数据算对端失联
constexpr int64_t  FIRST_FRAME_US = 5 * 1000 * 1000;    // 握手走完之后这么久还没发完第一帧就断开
constexpr int64_t  HS_STEP_US     = 5 * 1000 * 1000;    // 握手的每一步：发送端的下一个报文要在这么久之内到齐
constexpr int64_t  PASSWORD_BOX_US = 120 * 1000 * 1000; // 密码框从弹出 / 标红起这么久没输完就关掉、断开
// 我们发了 BYE 之后等发送端关连接（见 drain_peer）：它这么久既没有数据来、也没关，就不等了；不管数据来不来，总共最多等 BYE_DRAIN_US。
// 「这么久」要比 TCP 的重传超时长：我们发出去的报文（BYE 自己，或者排在它前面的 ACK）在空中丢了的时候，BYE 要等 lwIP 重传才到发送端
// （实测 1.1~1.5 秒），这期间发送端在等确认，不发数据也不关连接。定成 0.5 秒的那一版，正好在这种时候等不到它就先关了。
// 上限按协议里最坏的在途数据量定：4 帧 × 单帧上限 1 MB，实测链路每秒 2.5 MB 上下，读完要 1.6 秒，留了将近一倍的余量。
constexpr int64_t  BYE_IDLE_US    = 2 * 1000 * 1000;
constexpr int64_t  BYE_DRAIN_US   = 3 * 1000 * 1000;

enum : uint8_t {
    T_HELLO = 0x01, T_AUTH = 0x02, T_AUTH_OK = 0x03, T_AUTH_UNKNOWN = 0x04, T_READY = 0x05,
    T_PAIR = 0x06, T_PAIR_REPLY = 0x07, T_PAIR_OK = 0x08,
    T_STRIP = 0x10, T_PRESENT = 0x11, T_ACK = 0x12,
    T_TOUCH = 0x20, T_KEY = 0x21, T_HELD = 0x23, T_INPUT_MODE = 0x24, T_TPAD_GAIN = 0x25, T_PICTURE = 0x26, T_PICTURE_MODE = 0x27, T_DEV_BYE = 0x2F, T_KBD_LEDS = 0x30, T_BYE = 0x3F,
    T_AUDIO = 0x40,
};
constexpr uint32_t CAP_DEV_AUDIO     = 1u << 0;     // HELLO.caps：我们能收 AUDIO（协议「声音」），电脑看到这一位才发
constexpr uint32_t CAP_DEV_YUV422    = 1u << 1;     // HELLO.caps：我们能走 YUV422 帧缓冲（协议「画面的色彩」），电脑看到这一位才可以在 AUTH 里说用它
constexpr uint32_t CAP_PC_INPUT_MODE = 1u << 0;     // AUTH.caps：电脑认得 INPUT_MODE（协议「触摸方式」）
constexpr uint32_t CAP_PC_TPAD_GAIN  = 1u << 1;     // AUTH.caps：电脑认得 TPAD_GAIN（同上）
constexpr uint32_t CAP_PC_PICTURE    = 1u << 2;     // AUTH.caps：电脑认得 PICTURE（协议「画质」：老发送端收的那一种）
constexpr uint32_t CAP_PC_PIC_MODE   = 1u << 4;     // AUTH.caps：电脑认得 PICTURE_MODE（协议「画质」：四档）
constexpr uint32_t CAP_PC_YUV422     = 1u << 3;     // AUTH.caps：这条连接的条带都是 4:2:2、有限范围，要我们走 YUV422 帧缓冲（协议「画面的色彩」）
constexpr uint32_t FRAME_US_MIN = 5000, FRAME_US_MAX = 50000;     // PICTURE_MODE 里帧周期能取的范围（微秒）：之外的电脑收到就断开（协议「画质」）

struct __attribute__((packed)) Hdr {
    uint8_t  type, flags;
    uint16_t arg;
    uint32_t len;
};
// 握手报文的载荷（PROTOCOL.md「握手」那张表）
struct __attribute__((packed)) HelloBody {
    uint16_t fb_w, fb_h, view_w, view_h;
    uint32_t max_strip, max_frame, caps;
    uint8_t  id[wdc::ID_LEN];
    uint8_t  name[wdpair::NAME_LEN];
};
struct __attribute__((packed)) AuthBody {
    uint32_t caps;
    uint8_t  id[wdc::ID_LEN];
    uint8_t  key_id[wdc::KEY_ID_LEN];
    uint8_t  pub[wdc::X25519_LEN];
    uint8_t  name[wdpair::NAME_LEN];
};
struct __attribute__((packed)) PairBody {
    uint8_t id[wdc::ID_LEN];
    uint8_t name[wdpair::NAME_LEN];
    uint8_t pA[wdc::POINT_LEN];
};
constexpr size_t HELLO_LEN = sizeof(HelloBody), AUTH_LEN = sizeof(AuthBody), PAIR_LEN = sizeof(PairBody);
constexpr size_t AUTH_OK_LEN = wdc::X25519_LEN, PAIR_OK_LEN = wdc::CONFIRM_LEN, PAIR_REPLY_LEN = wdc::POINT_LEN + wdc::CONFIRM_LEN;
static_assert(sizeof(Hdr) == wdc::HDR_LEN && HELLO_LEN == 68 && AUTH_LEN == 92 && PAIR_LEN == 113 && PAIR_REPLY_LEN == 97,
              "握手报文的长度和协议里写的不一样");

struct __attribute__((packed)) PresentBody { uint32_t seq, t_ms; };
struct __attribute__((packed)) AckBody { uint32_t seq, t_ms, recv_us, decode_us, present_us; };
struct __attribute__((packed)) TouchPt { uint8_t id; uint16_t x, y; };

// 自动发现的包（PROTOCOL.md「自动发现」）：询问和应答是同一种
struct __attribute__((packed)) DiscMsg {
    char     magic[4];      // "T5WD"
    uint8_t  ver, kind;     // kind：1 = 询问，2 = 应答
    uint16_t port;
    uint8_t  id[wdc::ID_LEN];
    uint8_t  nonce[8];
};
static_assert(sizeof(DiscMsg) == 32, "自动发现的包是 32 字节");
constexpr uint8_t DISC_QUERY = 1, DISC_REPLY = 2;
constexpr int     DISC_PER_SEC = 10;        // 一秒最多应答这么多个
constexpr int     DISC_BURST   = 16;        // 醒一次最多看这么多个包（多的下一轮再看，别让一阵乱包拖住接受连接）

// ── 一帧的压缩数据 ───────────────────────────────────────────────────────
// 留三帧：正在收的（cur）、它前面的一帧（prev）、再前面的一帧（prev2）。帧缓冲是三块轮流用的（见「翻页」），
// 一帧解完之后，下一帧要解进去的那一块里还是往前数第三帧的画面：中间隔着的 prev2、prev 两帧，
// 它们的条带要按先后再解一遍才追得上。这件事拖到非做不可的时候才做：新一帧的条带如果盖住了
// 老的某个条带，那个条带就不用补了（整屏都在变的画面因此不用每帧解三遍）。见「解码（排活和补解）」。
struct Strip {
    uint16_t row, h;     // 帧缓冲里的起始行、行数
    uint32_t off, len;   // 压缩数据在 Frame::buf 里的位置
    bool     pending;    // 只对 prev、prev2 有意义：还没补到解码写的那块帧缓冲（s_back）里
};
struct Frame {
    uint8_t* buf   = nullptr;    // MAX_FRAME_BYTES 字节，条带的压缩数据一个接一个放（解码器的输入没有对齐要求）
    uint32_t bytes = 0;          // 这一帧已有的压缩数据字节数
    int      n     = 0;
    Strip    strips[UNITS];
    uint8_t  covered[UNITS];     // 每一格有没有被这一帧的条带盖住
    void reset() { bytes = 0; n = 0; memset(covered, 0, sizeof covered); }
};

static Frame  s_frames[3];
static uint8_t* s_audio_buf;         // 一个 AUDIO 报文的载荷（wdaudio::MAX_CHUNK，PSRAM）；和三帧的数据缓冲一起申请 / 释放，只有 wdisp 任务用
static Frame* s_cur   = &s_frames[0];
static Frame* s_prev  = &s_frames[1];
static Frame* s_prev2 = &s_frames[2];

// ── 状态 ─────────────────────────────────────────────────────────────────
static std::atomic<bool> s_want{false};   // 界面要我们监听
static std::atomic<bool> s_kick{false};   // 有人要求断开当前连接
static std::atomic<bool> s_dbg_exit{false};   // 调试命令要求按"用户做了退出手势"处理
static std::atomic<int32_t> s_dbg_touch{-1};  // 调试命令 wd touch：当成有一根手指按在横屏画面的 (x << 16 | y)；-1 = 没有
static TaskHandle_t      s_task    = nullptr;
static bool              s_showing = false;                    // 屏幕在我们手里（只有本任务读写）
static bool              s_yuv = false;                        // 这条连接的画面走 YUV422 帧缓冲（见「帧缓冲的格式」）。握手走完时定，连接期间不变
// 三块帧缓冲各自的角色（见「翻页」）。只有本任务写；解码任务在有活的时候读 s_back（有解码活排着、在做的时候不换它）
static uint16_t*         s_front   = nullptr;                  // 正在扫出的那一块
static uint16_t*         s_back    = nullptr;                  // 后台帧缓冲，解码写这一块
static uint16_t*         s_spare   = nullptr;                  // 剩下的那一块：有登记了没生效的翻页时是等着上屏的那一帧，否则空着
static esp_pm_lock_handle_t s_cpu_lock = nullptr;
static Ui                s_ui      = {};                       // 见 open()。只在拿着 LVGL 锁时读写

static portMUX_TYPE s_stats_mux = portMUX_INITIALIZER_UNLOCKED;
static Stats        s_stats     = {};
static Gaps         s_gaps      = {};                          // s_stats_mux 保护
static bool         s_peer_known = false;                      // 握手走完了：s_peer_id 是正连着的那台电脑（s_stats_mux 保护）
static uint8_t      s_peer_id[wdc::ID_LEN];

// 两个方向的加密状态。放在静态区（内部 RAM），不放任务栈上（栈在 PSRAM）。
// s_tx 只有无线显示任务用。s_rx 带着报文计数、要按报文的先后用：握手期间无线显示任务用；握手之后大条带交给解密任务，
// 它手里有活的时候无线显示任务不碰，要自己解密（小报文）、清密钥之前先等它做完（见「后台任务」）
static wdc::Cipher s_tx, s_rx;
static bool        s_tx_on = false;         // AUTH_OK 已经发出去了：之后发的报文都加密

// 密码框：任务发出一次询问（PairPrompt），界面经 pair_answer() 交回人输的密码
static PairPrompt        s_prompt = {};                        // 正开着的那一次询问（seq 是 0 = 没有）。只在拿着 LVGL 锁时读写
static portMUX_TYPE      s_pw_mux = portMUX_INITIALIZER_UNLOCKED;
static uint32_t          s_ask_seq = 0;                        // 最近一次询问的编号（下面三个都由 s_pw_mux 保护）
static bool              s_pw_ready = false;                   // s_pw 里是对 s_ask_seq 那一次询问的回答
static char              s_pw[PASSWORD_MAX + 1];

static void set_state(State st)
{
    portENTER_CRITICAL(&s_stats_mux);
    s_stats.state = st;
    portEXIT_CRITICAL(&s_stats_mux);
}

static inline int64_t now_us() { return esp_timer_get_time(); }

// ── 帧缓冲的格式 ─────────────────────────────────────────────────────────
// 本机界面（LVGL）的帧缓冲是 RGB565。硬件 JPEG 解码器解成 RGB565 是直接截位的，平滑的灰色、渐变上看得出色阶断层；
// 解成 YUV422（JPEG 里的数据原样写出来）、由 DSI 桥转成 RGB888 送给面板就没有这个问题。两种都是每像素 2 字节，
// 所以帧缓冲不用换，接管屏幕时把桥切到 YUV422、归还时切回来（bsp_display_set_fb_yuv422）。
// 切的过程中面板上有一帧左右是黑的，切完帧缓冲里的东西就按新格式解读了，所以两头都先把一帧黑换上屏幕再切：
// YUV 的黑（字节 80 10 80 10）按 RGB565 读是 (2, 4, 0)，也近乎全黑，哪种读法都看不出来；
// 反过来不行——RGB565 的黑（全 0）按 YUV 读是一片绿。
// 一条连接走哪种由发送端在握手里说（AUTH 的能力位，见 handshake_steps）：说了的，条带都是 4:2:2、画面压在有限范围里
// （桥是按有限范围换算的，16 以下都是黑、235 以上都是白），走 YUV422；没说的（加这个能力之前的发送端）照旧走 RGB565。
static std::atomic<bool> s_fb_yuv{false};       // 桥现在是不是 YUV422 输入。只有本任务写；调试命令（wd px / wd check）在别的任务里读
static bool s_fb_unsure = false;                // 上一次切换停在半路（桥改了、DSI 主机没跟上）：下一次哪怕目标和 s_fb_yuv 一样也要真的切一遍。只有本任务读写
// 接管屏幕时连着这么多次切不到 YUV422，这次开机就不再在 HELLO 里说「能走 YUV422」：发送端自己退回 RGB565，
// 画面有色阶断层但能用，好过每条连接都以「接管不了屏幕」结束、发送端重连、再失败
constexpr int YUV_FAILS_MAX = 3;
static int  s_yuv_fails = 0;                    // 连着几次切不到 YUV422（切成一次就清零）。只有本任务读写
static bool s_yuv_offered = false;              // 这条连接的 HELLO 里说了「能走 YUV422」。只有本任务读写

// 登记把 s_back 换上屏幕（之前不能有没生效的翻页，所以 s_spare 这时空着）：登记上了解码就改写空着的那一块。false = 没登记上
static bool fb_register()
{
    if (!lvgl_port_direct_flip_begin(s_back)) return false;
    uint16_t* t = s_back;  s_back = s_spare;  s_spare = t;
    return true;
}

// 登记的翻页生效了（或者不等了）：等着上屏的那一块成了正在扫出的，原来扫出的那一块空出来。返回生效的时刻（没等到帧边界是 0）
static int64_t fb_flipped()
{
    const int64_t t = lvgl_port_direct_flip_end();
    uint16_t* old = s_front;  s_front = s_spare;  s_spare = old;
    return t;
}

// 把桥切到 YUV422 / 切回 RGB565。切之前屏幕换成全黑（后台缓冲填 YUV 的黑、翻上去），切完屏幕上还是这一帧黑。
// 调用时 LVGL 已经停了、屏幕在我们手里，不能有没走完的翻页、没做完的解码活。返回 false = 没切成：
// 多数时候桥还是原来的格式（s_fb_yuv 不变）；只有「桥改了、DSI 主机没跟上」那一种，桥已经是新格式，s_fb_yuv 照桥的记，
// 另外记下 s_fb_unsure：之后不管要切到哪一种（包括再切一次同一种）都会真的去切，而不是以为本来就是那种格式
static bool fb_set_yuv(bool yuv)
{
    if (s_fb_yuv.load() == yuv && !s_fb_unsure) return true;
    uint32_t* p = (uint32_t*)s_back;
    for (size_t i = 0; i < (size_t)FB_W * FB_H / 2; i++) p[i] = 0x10801080u;
    esp_cache_msync(s_back, (size_t)FB_W * FB_H * 2, ESP_CACHE_MSYNC_FLAG_DIR_C2M);    // 面板走 DMA 读，不经缓存
    if (!fb_register()) {
        ESP_LOGW(TAG, "切帧缓冲格式：黑帧的翻页没登记上");
    } else {
        if (!lvgl_port_direct_flip_wait(100)) ESP_LOGW(TAG, "切帧缓冲格式：黑帧等帧边界超时");
        fb_flipped();
    }
    const esp_err_t e = bsp_display_set_fb_yuv422(yuv);
    if (e != ESP_OK) ESP_LOGE(TAG, "帧缓冲格式切不到 %s：%s", yuv ? "YUV422" : "RGB565", esp_err_to_name(e));
    if (e == ESP_OK || e == ESP_ERR_INVALID_RESPONSE) s_fb_yuv.store(yuv);
    if (e == ESP_OK) s_fb_unsure = false;
    else if (e == ESP_ERR_INVALID_RESPONSE) s_fb_unsure = true;     // 别的错误码桥没动过，原来确定的还确定、不确定的还不确定
    return e == ESP_OK;
}

// ── 接管 / 归还屏幕 ──────────────────────────────────────────────────────
static bool enter_show()
{
    bool ok = false;
    lvgl_port_lock(0);
    if (s_want.load()) {     // 拿锁期间界面可能已经不要监听了（暂停）
        lv_indev_reset(nullptr, nullptr);       // 这一刻手指正按着界面上的东西的话，作废这次按压
        settings::save();                       // 按着的要是滑块（音量），它等不到松手那一下存盘了：在这儿存。没变的项 NVS 不会真的写
        lvgl_port_stop();
        uint16_t* fbs[3] = {};
        ok = lvgl_port_direct_begin(fbs);
        if (!ok) ESP_LOGE(TAG, "接管不了屏幕：面板没有三块帧缓冲（只有 ST712x 面板配了三块），或者界面正在滚动位移帧当中");
        s_front = fbs[0];  s_back = fbs[1];  s_spare = fbs[2];
        // 条带是 16 行的整数倍 = 23040 字节的整数倍，帧缓冲本身对齐了，每个条带的输出位置和长度就都对齐
        if (ok && (((uintptr_t)s_front | (uintptr_t)s_back | (uintptr_t)s_spare) & (hwjpeg::out_align() - 1))) {
            ESP_LOGE(TAG, "帧缓冲没有按 %u 字节对齐（%p / %p / %p），硬件解码器不能直接写", (unsigned)hwjpeg::out_align(), s_front, s_back, s_spare);
            ok = false;
        }
        // 桥的格式和这条连接要的不一样就现在切（屏幕黑一下）：平时是走 YUV422 的连接在这儿切过去；走 RGB565 的连接不用切，
        // 除非上一条连接归还屏幕时没切回来、或者停在了半路。切完 s_back、s_spare 里是以前留下的东西（按另一种格式写的），
        // 头两帧会把它们整屏盖掉才上屏（第一帧整屏解进 s_back，第二帧解进另一块时把第一帧整屏补上）。
        // 切不成就不接管：桥回到界面用的 RGB565（没动过的话这一步什么都不做），屏幕上已经是那一帧黑了，让 LVGL 整屏重画
        const bool blanked = ok && (s_fb_yuv.load() != s_yuv || s_fb_unsure);
        if (blanked && !fb_set_yuv(s_yuv)) ok = false;
        if (blanked && s_yuv) {
            s_yuv_fails = ok ? 0 : s_yuv_fails + 1;
            if (s_yuv_fails == YUV_FAILS_MAX) ESP_LOGE(TAG, "连着 %d 次切不到 YUV422：重启之前不再走 YUV422，之后的连接都走 RGB565", YUV_FAILS_MAX);
        }
        if (!ok) {
            if (blanked) fb_set_yuv(false);
            lvgl_port_direct_end();             // 黑帧换到了第三块上的话换回 LVGL 的两块之一
            lvgl_port_resume();
            if (blanked) lv_obj_invalidate(lv_screen_active());
        }
        if (ok && s_ui.screen_take) s_ui.screen_take();     // 息屏着的话点亮（等连接时到时间会息屏，照常监听）
    }
    lvgl_port_unlock();
    s_showing = ok;
    if (ok) set_state(State::Showing);
    return ok;
}

// 等手指都抬起来（最多 1.5 秒）。退出手势划到一半就把屏幕还给 LVGL 的话，剩下的半截会被界面当成自己的手势。
// 连着 3 次（40 ms）读不到触点才算抬起，原因见 LIFT_US。
static void wait_touch_release()
{
    esp_lcd_touch_handle_t tp = bsp_display_get_touch_handle();
    if (!tp) return;
    for (int i = 0, empty = 0; i < 75; i++) {
        esp_lcd_touch_point_data_t raw[1];
        uint8_t  cnt = 0;
        esp_lcd_touch_read_data(tp);
        esp_lcd_touch_get_data(tp, raw, &cnt, 1);
        empty = cnt ? 0 : empty + 1;
        if (empty >= 3) return;
        vTaskDelay(pdMS_TO_TICKS(20));
    }
}

// user_exit = 是用户做了退出手势才结束的：还屏幕的同时告诉界面（见 Ui::screen_back）。
static void leave_show(bool user_exit)
{
    if (!s_showing) return;
    wait_touch_release();
    // 桥在 YUV422 上的话切回来（屏幕黑一下，切完还是黑的），下面 LVGL 整屏重画。没切成再试一次；还不成的话界面的颜色是错的，
    // 下一条连接接管屏幕时会再切（enter_show）
    if (!fb_set_yuv(false) && !fb_set_yuv(false)) ESP_LOGE(TAG, "归还屏幕：帧缓冲格式切不回 RGB565，界面的颜色会不对");
    lvgl_port_lock(0);
    // 正在扫出的是第三块的话换回 LVGL 用的两块之一（画面不变，要拷一块、等一个帧边界，约 30 ms）
    if (!lvgl_port_direct_end()) ESP_LOGE(TAG, "归还屏幕：换不回界面的帧缓冲，界面要到下一次滚动翻页才看得见");
    lvgl_port_resume();
    lv_obj_invalidate(lv_screen_active());      // 帧缓冲里是发送端的画面，整屏重画
    lv_display_trigger_activity(nullptr);       // 刚才的触摸 LVGL 没看到，别一回来就因为"没人操作"息屏
    if (s_ui.screen_back) s_ui.screen_back(user_exit);
    lvgl_port_unlock();
    s_showing = false;
}

// ── 触摸 ─────────────────────────────────────────────────────────────────
// 触摸芯片不给触点编号，这里按"离上一次哪个点最近"认人，保证一根手指从按下到抬起编号不变。
// 从左边缘（EDGE_ZONE 以内）起手的触点不发给发送端：它是 Tab5 自己的手势，向右划过 EXIT_SWIPE 就退出显示。
constexpr int     MAX_PTS = 5;
constexpr int     EDGE_ZONE = 32, EXIT_SWIPE = 250;      // 边缘区和界面自己的侧滑返回一样宽
constexpr int64_t EXIT_SWIPE_US = 800 * 1000, TOUCH_PERIOD_US = 16 * 1000;
constexpr int     MATCH_DIST = 200;      // 两次读数之间同一根手指最多挪这么远（像素）
// 一个触点从第一次读不到起、连续这么久都读不到（而且至少读了两次）才算抬起。这里是定时去读触摸芯片
//（LVGL 停着，它那条按中断读的路也停了），芯片两次上报之间读到的是"没有新数据"，驱动报成 0 个点
//（GT911 是数据就绪位没置上），并不是手指抬起了。一次读不到就算抬起的话，按着不动的手指会被拆成一连串点击。
// 芯片的上报间隔最长 20 ms，这里取它的两倍。
// 从第一次读不到起算、不从上一次读到起算：解码、补解、等帧边界加起来可以有 40 ms 以上不来读触摸，
// 按"距上一次读到多久"算的话，这样一段空档之后只要读空一次就成了抬起，拖动会被打断。
constexpr int64_t LIFT_US = 40 * 1000;

struct Track {
    bool     used;
    bool     edge;       // 从左边缘起手的
    uint16_t x, y, x0;
    int64_t  t0;         // 按下的时刻
    int64_t  gone;       // 连续读不到它的那几次里，第一次是什么时刻（misses 不是 0 时有意义）
    uint8_t  misses;     // 已经连续几次读不到它（读到就清零）
};
static Track    s_tracks[MAX_PTS];
static TouchPt  s_sent[MAX_PTS];     // 上一次发给发送端的快照
static int      s_sent_n = 0;
static int64_t  s_touch_next = 0;
// 「还按着」（见 send_held）
constexpr int64_t HELD_PERIOD_US = 200 * 1000;
static bool     s_keys_down  = false;   // 上一份发出去的 KEY 里有键按着（修饰键也算）
static int64_t  s_input_sent = 0;       // 上一个输入报文（TOUCH / KEY / HELD）是什么时候发的
// 触摸方式（见 send_mode）
static bool     s_mode_peer  = false;   // 握手走完了，而且这台电脑认得 INPUT_MODE
static int      s_mode_sent  = -1;      // 这条连接上告诉过电脑的触摸方式；-1 = 还没说过
static bool     s_gain_peer  = false;   // 握手走完了，而且这台电脑认得 TPAD_GAIN
static int      s_gain_sent  = -1;      // 这条连接上告诉过电脑的触摸板加速（0.1 倍）；-1 = 还没说过
static bool     s_pic_peer   = false;   // 握手走完了，而且这台电脑认得 PICTURE
static bool     s_picmode_peer = false; // 握手走完了，而且这台电脑认得 PICTURE_MODE（认得的就不发 PICTURE 了）
static int      s_pic_sent   = -1;      // 这条连接上告诉过电脑的画质（第几档）；-1 = 还没说过

// 一条连接开始时：输入这边的状态都从头来
static void input_reset()
{
    memset(s_tracks, 0, sizeof s_tracks);
    s_sent_n = 0;
    s_touch_next = 0;
    s_keys_down  = false;
    s_input_sent = 0;
    s_mode_peer  = false;
    s_mode_sent  = -1;
    s_gain_peer  = false;
    s_gain_sent  = -1;
    s_pic_peer   = false;
    s_picmode_peer = false;
    s_pic_sent   = -1;
    s_dbg_touch.store(-1);
}

static bool send_all(int fd, const void* data, size_t n)
{
    const uint8_t* p = (const uint8_t*)data;
    while (n) {
        int r = send(fd, p, n, 0);
        if (r <= 0) return false;
        p += r;
        n -= (size_t)r;
    }
    return true;
}

// 发一个报文。AUTH_OK 之后发的都加密：头 ‖ 密文 ‖ 认证码。载荷最长的是 PAIR_REPLY
static bool send_msg(int fd, uint8_t type, uint16_t arg, const void* payload = nullptr, size_t n = 0)
{
    uint8_t buf[sizeof(Hdr) + PAIR_REPLY_LEN + wdc::TAG_LEN];
    if (n > PAIR_REPLY_LEN) return false;
    Hdr h = {type, 0, arg, (uint32_t)n};
    memcpy(buf, &h, sizeof h);
    size_t total = sizeof h + n;
    if (s_tx_on) {
        if (!s_tx.seal(buf, (const uint8_t*)payload, n, buf + sizeof h)) return false;
        total += wdc::TAG_LEN;
    } else if (n) {
        memcpy(buf + sizeof h, payload, n);
    }
    return send_all(fd, buf, total);
}

enum class Touch { Ok, SendFailed, ExitGesture };

static Touch poll_touch(int fd)
{
    int64_t now = now_us();
    if (!s_showing || now < s_touch_next) return Touch::Ok;
    s_touch_next = now + TOUCH_PERIOD_US;
    esp_lcd_touch_handle_t tp = bsp_display_get_touch_handle();
    if (!tp) return Touch::Ok;

    esp_lcd_touch_point_data_t raw[MAX_PTS];
    uint8_t  cnt = 0;
    esp_lcd_touch_read_data(tp);
    esp_lcd_touch_get_data(tp, raw, &cnt, MAX_PTS);
    if (int32_t fake = s_dbg_touch.load(); fake >= 0 && cnt < MAX_PTS) {
        // 调试命令 wd touch 要的那根手指：换回面板原生方向，和读到的触点一起走下面同一条路
        raw[cnt].x = (uint16_t)(fake & 0xFFFF);
        raw[cnt].y = (uint16_t)(VIEW_W - 1 - (fake >> 16));
        cnt++;
    }

    // 触摸坐标是面板原生方向（和帧缓冲一样），换成横屏画面的坐标
    bool claimed[MAX_PTS] = {};
    for (int i = 0; i < cnt; i++) {
        int x = VIEW_W - 1 - (int)raw[i].y, y = (int)raw[i].x;
        x = x < 0 ? 0 : (x >= VIEW_W ? VIEW_W - 1 : x);
        y = y < 0 ? 0 : (y >= VIEW_H ? VIEW_H - 1 : y);
        int best = -1, best_d = MATCH_DIST * MATCH_DIST;
        for (int k = 0; k < MAX_PTS; k++) {
            if (!s_tracks[k].used || claimed[k]) continue;
            int dx = x - s_tracks[k].x, dy = y - s_tracks[k].y, d = dx * dx + dy * dy;
            if (d < best_d) { best_d = d; best = k; }
        }
        if (best < 0) {     // 新按下的手指：找一个空编号
            for (int k = 0; k < MAX_PTS && best < 0; k++)
                if (!s_tracks[k].used && !claimed[k]) best = k;
            if (best < 0) continue;
            s_tracks[best] = Track{true, x <= EDGE_ZONE, (uint16_t)x, (uint16_t)y, (uint16_t)x, now, 0, 0};
        }
        claimed[best] = true;
        s_tracks[best].x      = (uint16_t)x;
        s_tracks[best].y      = (uint16_t)y;
        s_tracks[best].misses = 0;
        if (s_tracks[best].edge && x - s_tracks[best].x0 >= EXIT_SWIPE && now - s_tracks[best].t0 <= EXIT_SWIPE_US)
            return Touch::ExitGesture;
    }
    for (int k = 0; k < MAX_PTS; k++) {      // 这次没读到的触点：见 LIFT_US
        Track& t = s_tracks[k];
        if (!t.used || claimed[k]) continue;
        if (t.misses == 0) t.gone = now;
        if (t.misses < UINT8_MAX) t.misses++;
        if (t.misses >= 2 && now - t.gone >= LIFT_US) t.used = false;
    }

    TouchPt pts[MAX_PTS];
    int n = 0;
    for (int k = 0; k < MAX_PTS; k++)
        if (s_tracks[k].used && !s_tracks[k].edge) pts[n++] = TouchPt{(uint8_t)k, s_tracks[k].x, s_tracks[k].y};
    if (n == s_sent_n && memcmp(pts, s_sent, n * sizeof(TouchPt)) == 0) return Touch::Ok;
    memcpy(s_sent, pts, n * sizeof(TouchPt));
    s_sent_n = n;
    s_input_sent = now;
    return send_msg(fd, T_TOUCH, (uint16_t)n, pts, n * sizeof(TouchPt)) ? Touch::Ok : Touch::SendFailed;
}

// ── 键盘 ─────────────────────────────────────────────────────────────────
// 键盘模块的按键报文（8 字节，见 utils/tab5_kbd.hpp）：**接管屏幕之后才发**（协议「键盘」），和触摸一样。
// 这里自己把这一关，不能只靠「接管之后才 kbd::listen(true)」：read_full 在握手期间也会调到这里，那时发出去的是明文、
// 对端还没认证——上一条连接收尾时晚到的一份报文要是留在队列里，就会发给下一个连上来的人。
// 一份一发、不合并：每份是完整的按键状态，合并会把「按下又松开」吞掉。返回 false = 发不出去了。
static bool send_keys(int fd)
{
    if (!s_showing) return true;
    kbd::Report r;
    while (kbd::pop(r)) {
        if (!send_msg(fd, T_KEY, 0, &r, sizeof r)) return false;
        s_keys_down = false;
        for (uint8_t b : r.b) s_keys_down |= b != 0;
        s_input_sent = now_us();
    }
    return true;
}

// ── 触摸方式 ─────────────────────────────────────────────────────────────
// 触摸板模式的开关在设置页上（settings 的 wd_touchpad）。触点照旧原样发，换成鼠标动作是电脑那边的事，这里只告诉它现在是哪种：
// 握手走完说一次（加密之后；不等接管屏幕——第一份 TOUCH 之前电脑就得知道），之后变了再说。连上之后、第一帧到齐之前那一页还在屏幕上，
// 人这时拨了当场就说；接管屏幕之后界面停着，人换方式是左边缘右滑退出显示（停在那一页、暂停）、拨、点「开始显示」，新连接握手走完时说新的。
// 显示期间变只有调试命令 wd tpad 做得到。电脑没在 AUTH 里说认得这个报文就不发：老的发送端收到不认识的报文会断开。
// 触摸板模式下光标的加速（settings 的 wd_tpad_gain，同一页上的「触摸板加速」）是一样的做法，另一个报文（TPAD_GAIN）、另一个能力位；
// 触摸板模式关着也照样告诉电脑，它用不用是它的事。
// 画质（主页面上那排四档的按钮，settings 的 wd_pic）也是这个做法：报文 PICTURE_MODE，arg 是第几档，载荷是屏幕的帧周期（微秒，
// 限帧率的两档按它的 2 倍、1.5 倍限）。压画面、限帧率都是电脑的事，这里只报数。
// 不认得 PICTURE_MODE 的老发送端发 PICTURE：按档位折成一组「质量 / 画面在动时每帧多少 KB」（settings 里的表），它不限帧率，大致对得上。
// 返回 false = 发不出去了。
static bool send_mode(int fd)
{
    const int mode = settings::get().wd_touchpad ? 1 : 0;
    if (s_mode_peer && mode != s_mode_sent) {
        if (!send_msg(fd, T_INPUT_MODE, (uint16_t)mode)) return false;
        s_mode_sent = mode;
        ESP_LOGW(TAG, "告诉电脑触摸方式：%s", mode ? "触摸板" : "触摸");
    }
    const int gain = settings::get().wd_tpad_gain;
    if (s_gain_peer && gain != s_gain_sent) {
        if (!send_msg(fd, T_TPAD_GAIN, (uint16_t)gain)) return false;
        s_gain_sent = gain;
        ESP_LOGW(TAG, "告诉电脑触摸板加速：最多 %d.%d 倍", gain / 10, gain % 10);
    }
    const int pic = settings::get().wd_pic;
    if (s_picmode_peer && pic != s_pic_sent) {
        // 帧周期是屏幕驱动给的（量到的，没量到时是按时序算的标称值），两种面板都在范围里。不在范围里的数不能发：电脑收到就断开、托盘接着重连，
        // 一直连不上。那样是屏幕驱动出了错（比如面板没起来，返回 0），这条连接上就不报档位了，电脑按它自己的默认值来
        const uint32_t frame_us = bsp_display_get_frame_period_us();
        s_pic_sent = pic;
        if (frame_us < FRAME_US_MIN || frame_us > FRAME_US_MAX) {
            ESP_LOGE(TAG, "屏幕的帧周期 %u 微秒不在 %u~%u 之间，画质的档位不报给电脑", (unsigned)frame_us, (unsigned)FRAME_US_MIN, (unsigned)FRAME_US_MAX);
            return true;
        }
        if (!send_msg(fd, T_PICTURE_MODE, (uint16_t)pic, &frame_us, sizeof frame_us)) return false;
        ESP_LOGW(TAG, "告诉电脑画质：第 %d 档「%s」，屏幕的帧周期 %u 微秒", pic, settings::WD_PIC_NAMES[pic], (unsigned)frame_us);
    } else if (!s_picmode_peer && s_pic_peer && pic != s_pic_sent) {
        const int kb = settings::WD_PIC_OLD_KB[pic];
        if (!send_msg(fd, T_PICTURE, (uint16_t)(settings::WD_PIC_OLD_QUALITY | (kb << 8)))) return false;
        s_pic_sent = pic;
        ESP_LOGW(TAG, "告诉电脑画质（它不认得档位，折成）：质量 %d，画面在动时每帧 %d KB%s", settings::WD_PIC_OLD_QUALITY, kb, kb ? "" : "（不降）");
    }
    return true;
}

// ── 「还按着」────────────────────────────────────────────────────────────
// 有键（修饰键也算）或者触点按着的时候，离上一个输入报文满 HELD_PERIOD_US 就给发送端发一个 HELD（协议「按着的时候」）。
// 发送端照着我们上一次报的状态按时间做事（按键连发、触点按着不动时重报）。我们突然没了（WiFi 断了、死机、重启）不会说一声，
// 它要过 5~11 秒才发现连接断了；有了这个，它几百毫秒没收到就知道那份状态不作数了，不会照着它一直连发下去。
// 说的是「发给了发送端的状态」：接管屏幕之前按下、之后一直没动的键没报过，不算；从左边缘起手的触点没发过，也不算。
// 返回 false = 发不出去了。
static bool send_held(int fd)
{
    if (!s_showing || !(s_keys_down || s_sent_n > 0)) return true;
    int64_t now = now_us();
    if (now - s_input_sent < HELD_PERIOD_US) return true;
    s_input_sent = now;
    return send_msg(fd, T_HELD, 0);
}

// ── 后台任务 ─────────────────────────────────────────────────────────────
// 无线显示任务（task）只管收数据，另外两样活交给两个后台任务，三件事并行：
//   解密任务：大一点的条带，报文头、载荷（来一块交一块，原地解在帧数据缓冲里）、认证码都交给它，无线显示任务不等结果、接着收后面的报文；
//             它核对完，无线显示任务再按到达的先后处理这些条带（apply_strips；等数据的每次醒来都看一眼，见 read_full）。
//             认证码没对上的条带，内容不会被用到。
//   解码任务：处理过的条带交给它用硬件解码器解进后台帧缓冲（一个条带要等一两毫秒）。
// 都串在一个任务里的话，一帧的用时 = 收 + 解密 + 等解码（整屏滚动一帧 47 KB：7 + 7 + 11 ms），而链路送完这一帧只要 9 ms；
// 实测开加密后整屏滚动从 27 帧/秒掉到 21。分开之后三件事各占各的时间（两个核平时各有七成空闲）。
// 规矩：两个任务都按先后做排着的活；排的一方用 join() 等它做完。s_rx（报文计数跟着它走）要按报文的先后用：
// 解密任务手里有活的时候无线显示任务不碰它，要自己解密（小报文）之前先 join。
// 解码任务干活期间无线显示任务不动 s_back 和已经交出去的条带（收下一个条带写的是同一块缓冲里靠后的位置）。

// 交给了解密任务、无线显示任务还没处理的条带，按到达的先后排。只有 verdict 是两个任务都碰的
struct Pending {
    Strip                s;                     // 起始行、数据在帧数据缓冲里的位置和长度（高要等查过 JPEG 文件头才有）
    uint8_t              hdr[wdc::HDR_LEN];     // 报文头（算进认证码）
    uint8_t              tag[wdc::TAG_LEN];
    std::atomic<uint8_t> verdict;               // 0 = 还没核对完，1 = 认证码对上了，2 = 没对上
};
static Pending s_pend[UNITS];                   // 一帧最多 UNITS 个条带
static int     s_pend_n = 0, s_pend_done = 0;   // 排进来了几个、处理了几个（都处理完就一起归零）。只有无线显示任务用

struct Job {
    enum Kind : uint8_t { Sync, Begin, Decrypt, Finish, Decode } kind;      // Sync = 做到这一个时放一次 done（join 在等）
    union {
        Pending* pend;                                      // Begin：s_rx.open_begin(pend->hdr)；Finish：核对 pend->tag，结果写进 pend->verdict
        struct { uint8_t* p; size_t n; } data;              // Decrypt：s_rx.open_update(p, n)
        struct { const Frame* f; Strip s; } strip;          // Decode：decode_strip(f, s)
    };
};
struct Worker {
    QueueHandle_t     q    = nullptr;
    SemaphoreHandle_t done = nullptr;
    SemaphoreHandle_t tick = nullptr;      // 每做完一个 Decode 活放一次（见 osd_draw）
    TaskHandle_t      task = nullptr;
    void post(const Job& j) { xQueueSend(q, &j, portMAX_DELAY); }       // 队列满了就等：排着的活每个只要一两毫秒
    void join()
    {
        Job j;
        j.kind = Job::Sync;
        post(j);
        xSemaphoreTake(done, portMAX_DELAY);
    }
};
static Worker                s_rxw, s_decw;         // 解密任务、解码任务
static std::atomic<bool>     s_dec_failed{false};   // 上一次 dec_join 之后有条带解码失败（之后排着的不再解）
static std::atomic<uint32_t> s_dec_us{0};           // 上一次 dec_join 之后解码用时合计
static std::atomic<uint32_t> s_dec_done{0};         // 解码任务做完了几个 Decode 活（失败、跳过的也算）；和 s_dec_posted 对着看
static uint32_t              s_dec_posted = 0;      // 排出去了几个 Decode 活，也就是最后排的那个的编号（只有无线显示任务用）
constexpr uint32_t           RX_OFFLOAD_MIN = 2048; // 不到这么多字节的条带就在无线显示任务里解密：零碎的活交出去不划算

static bool decode_strip(const Frame* f, const Strip& s)
{
    uint8_t* out      = (uint8_t*)(s_back + (size_t)s.row * FB_W);
    size_t   out_size = (size_t)s.h * FB_W * 2;
    uint32_t got      = 0;
    esp_err_t e = s_yuv ? hwjpeg::decode_yuv422(f->buf + s.off, s.len, out, out_size, &got)
                        : hwjpeg::decode_rgb565(f->buf + s.off, s.len, out, out_size, &got);
    if (e != ESP_OK || got != out_size) {
        ESP_LOGW(TAG, "条带（起始行 %u，高 %u，%u 字节）解码失败：%s，输出 %u / %u 字节",
                 s.row, s.h, (unsigned)s.len, esp_err_to_name(e), (unsigned)got, (unsigned)out_size);
        return false;
    }
    return true;
}

static void worker_task(void* arg)
{
    Worker* w = (Worker*)arg;
    for (;;) {
        Job j;
        xQueueReceive(w->q, &j, portMAX_DELAY);
        switch (j.kind) {
        case Job::Sync:
            xSemaphoreGive(w->done);
            break;
        // 解密这三步里哪一步出了错，这个报文的认证码都对不上（open_finish 返回 false）
        case Job::Begin:
            s_rx.open_begin(j.pend->hdr);
            break;
        case Job::Decrypt:
            s_rx.open_update(j.data.p, j.data.n);
            break;
        case Job::Finish:
            j.pend->verdict.store(s_rx.open_finish(j.pend->tag) ? 1 : 2);
            break;
        case Job::Decode:
            if (!s_dec_failed.load()) {
                const int64_t t0 = now_us();
                if (!decode_strip(j.strip.f, j.strip.s)) s_dec_failed.store(true);
                s_dec_us.fetch_add((uint32_t)(now_us() - t0));
            }
            s_dec_done.fetch_add(1);
            xSemaphoreGive(w->tick);
            break;
        }
    }
}

static bool worker_start(Worker& w, const char* name, uint32_t stack, UBaseType_t prio)
{
    if (!w.q) w.q = xQueueCreate(32, sizeof(Job));
    if (!w.done) w.done = xSemaphoreCreateBinary();
    if (!w.tick) w.tick = xSemaphoreCreateBinary();
    if (w.q && w.done && w.tick && !w.task
        && xTaskCreatePinnedToCoreWithCaps(worker_task, name, stack, &w, prio, &w.task, tskNO_AFFINITY, MALLOC_CAP_SPIRAM) != pdPASS)
        w.task = nullptr;
    return w.task != nullptr;
}

// ── 一条连接 ─────────────────────────────────────────────────────────────
// 一帧从解完到回 ACK 一路带着的东西
struct FrameInfo {
    int64_t  t_hdr = 0;             // 这一帧的 PRESENT 报文头到达的时刻
    uint32_t seq = 0, t_ms = 0;     // PRESENT 里带来的，ACK 原样带回
    uint32_t recv_us = 0, decode_us = 0, bytes = 0, catchup = 0;
};
// 翻页走到哪一步了（见「翻页」一节）。同一时刻最多有两帧在这里面：ready + pending（一帧放着、等前一帧的翻页生效），
// 或者 pending + due（前一帧刚生效、这一帧刚登记，前一帧的 ACK 马上就回）。due 一立起来当场就报掉，不会留到下一次登记之后
struct Flip {
    bool      ready   = false;      // 有一帧解完了（在 s_back 里），要等前一帧的翻页生效才能登记：这期间三块帧缓冲都不能写
    bool      pending = false;      // 有一帧登记了翻页，帧边界还没到
    bool      due     = false;      // 有一帧的翻页生效了，它的 ACK 和统计还没做（flip_report）
    int64_t   t_reg   = 0;          // pending 的那一帧是什么时候登记的
    int64_t   t_done  = 0;          // due 的那一帧的翻页是什么时候生效的
    FrameInfo next, reg, done;      // 依次是 ready / pending / due 说的那一帧
};
// 收画面收到哪一步了、是怎么结束的（给日志用）、结束之后要不要等发送端关连接
struct Conn {
    int         fd;
    bool        first       = true;     // 第一帧还没上屏：条带先收着，到它的 PRESENT 时一起解（见 serve）
    const char* why         = "连接断开";
    char        detail[160] = "";       // 最长的一条原因（条带尺寸不对）约 130 字节
    bool        said_bye    = false;    // 是我们这边要结束的、已经给发送端发了 BYE
    bool        user_exit   = false;    // 用户做了退出手势
    Flip        flip;
    int64_t     win_t0      = 0;        // 帧率 / 码率的统计窗口
    uint32_t    win_frames  = 0, win_bytes = 0;
    int64_t     t_shown     = 0;        // 上一帧换上屏幕的时刻（0 = 还没有上过屏）
};

// 发送端不守协议：记下原因。总是返回 false，好写成 return violation(...)
__attribute__((format(printf, 2, 3))) static bool violation(Conn& c, const char* fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(c.detail, sizeof c.detail, fmt, ap);
    va_end(ap);
    c.why = "发送端不守协议";
    return false;
}

static bool apply_strips(Conn& c, bool wait);

// ── 翻页 ─────────────────────────────────────────────────────────────────
// 帧缓冲有三块：s_front 正在扫出；s_back 是解码写的那一块；s_spare 是剩下的那一块。一帧解完（PRESENT）之后：
//   · 前一帧的翻页已经生效了：马上登记这一帧的翻页（fb_register）。登记只是登记，到下一个帧边界才生效，这里不停下来等：
//     s_back 换成空着的那一块，下一帧的条带接着往里解。帧边界一过（flip_arrived）这一帧上屏、原来扫出的那一块空出来，给发送端回 ACK。
//   · 前一帧的翻页还没生效（c.flip.pending）：同一时刻只能有一次登记了没生效的翻页，这一帧先放着（c.flip.ready）。
//     这期间三块都不能写（一块在扫出，一块等着上屏，一块是放着的这一帧），后面到的条带先不处理（apply_strips 里挡着），
//     数据照收、照解密（交给解密任务的大条带和声音是这样；在本任务里解密的小条带要排在前面的条带后面处理，轮到它时先等帧边界，再读它的载荷）。
//     前一帧的翻页一生效：登记这一帧 → 处理攒下的条带（排去解码）→ 回前一帧的 ACK。
// 一帧解得比一个帧周期（15.3 ms）快的时候，稳定下来是每个帧边界上屏一帧，解码从上一个帧边界前后开工，多出来的时间放在「放着等」上；
// 偶尔一帧解得慢了，只是它自己晚一拍上屏，后面的帧照常接着解，不会连带着也错过。
// 走过的两版：只有两块帧缓冲、登记之后不等帧边界接着收数据——登记到帧边界之间没有地方可写，解码只能从帧边界开工，
// 整屏 4:2:2 解一帧 13.8 ms，离一个帧周期只差 1.5 ms，帧稍大一点就隔一帧错过一次刷新（游戏画面锁在刷新率的 2/3）；
// 更早是登记之后停下来等帧边界、翻页之后才开始读下一帧，读 + 解密 + 解码比一个帧周期长一点，整屏在动的画面被锁在每两个帧周期一帧。
constexpr int64_t  FLIP_TIMEOUT_US = 100 * 1000;    // 登记之后这么久帧边界还没来就不等了（平时最多一个帧周期）
constexpr uint32_t FLIP_POLL_MS    = 2;             // 没有数据可读时等帧边界，一次最多等这么久就回去看一眼有没有数据到
                                                    // （帧比接收窗口大的时候，剩下的那一截要我们读了才发得过来，别让它干等到帧边界）

// 登记了的翻页生效了没有，最多等 wait_ms 毫秒。true = 没有在等的翻页，或者它生效了（等过了 FLIP_TIMEOUT_US 也算，那一帧可能撕裂一下）：
// 可以再登记下一次翻页了；刚生效的话还欠发送端一个 ACK（c.flip.due，调用方接着要 flip_report，不能留到下一次翻页生效）。false = 帧边界还没到
static bool flip_arrived(Conn& c, uint32_t wait_ms)
{
    Flip& f = c.flip;
    if (!f.pending) return true;
    const int64_t left_us = f.t_reg + FLIP_TIMEOUT_US - now_us();
    const uint32_t cap_ms = left_us > 0 ? (uint32_t)((left_us + 999) / 1000) : 0;
    const bool ok = lvgl_port_direct_flip_wait(wait_ms < cap_ms ? wait_ms : cap_ms);
    if (!ok && now_us() - f.t_reg < FLIP_TIMEOUT_US) return false;
    const int64_t t = fb_flipped();
    if (!ok) ESP_LOGW(TAG, "第 %u 帧等帧边界超时", (unsigned)f.reg.seq);
    f.t_done  = t ? t : now_us();
    f.done    = f.reg;
    f.pending = false;
    f.due     = true;
    return true;
}

// 刚换上屏幕的那一帧：给发送端回 ACK，记统计。返回 false = ACK 发不出去（连接断了）
static bool flip_report(Conn& c)
{
    const FrameInfo& f      = c.flip.done;
    const int64_t    t_done = c.flip.t_done;
    c.flip.due = false;
    AckBody ack = {f.seq, f.t_ms, f.recv_us, f.decode_us, (uint32_t)(t_done - f.t_hdr)};
    if (!send_msg(c.fd, T_ACK, 0, &ack, sizeof ack)) return false;
    wdosd::frame_shown(ack.recv_us + ack.present_us);       // 这一帧第一个字节到达 → 上屏

    c.win_frames++;
    c.win_bytes += f.bytes;
    portENTER_CRITICAL(&s_stats_mux);
    if (c.t_shown) {                    // 离上一帧上屏隔了多久
        // 画面不变时两帧可以隔很久：封顶在 uint32 微秒能表示的范围里（约 71 分钟），不让它回绕成一个小数
        const int64_t  gap64 = t_done - c.t_shown;
        const uint32_t gap   = gap64 > (int64_t)UINT32_MAX ? UINT32_MAX : (uint32_t)gap64;
        s_gaps.n++;
        if (gap > s_gaps.max_us) s_gaps.max_us = gap;
        for (int i = 0; i < Gaps::NTH && gap >= Gaps::TH_MS[i] * 1000u; i++) s_gaps.ge[i]++;
        if (gap >= Gaps::TH_MS[Gaps::BIG_TH] * 1000u)
            s_gaps.recent[s_gaps.big_total++ % Gaps::RECENT] = {f.seq, (gap + 500) / 1000};
    }
    c.t_shown = t_done;
    s_stats.frames++;
    s_stats.bytes     += f.bytes;
    s_stats.catchup   += f.catchup;
    s_stats.recv_us    = ack.recv_us;
    s_stats.decode_us  = ack.decode_us;
    s_stats.present_us = ack.present_us;
    if (t_done - c.win_t0 >= 1000000) {
        s_stats.fps_x10 = (uint32_t)((uint64_t)c.win_frames * 10000000 / (uint64_t)(t_done - c.win_t0));
        s_stats.kbps    = (uint32_t)((uint64_t)c.win_bytes * 8000 / (uint64_t)(t_done - c.win_t0));
        c.win_t0 = t_done;
        c.win_frames = c.win_bytes = 0;
    }
    portEXIT_CRITICAL(&s_stats_mux);
    return true;
}

// ── 调试信息 ─────────────────────────────────────────────────────────────
// 调试信息（wd_osd）叠在后台缓冲最先解码的那几行上，在无线显示任务里混，和解码任务解后面的条带同时进行：
// 整屏在动时一帧 15.3 ms 里解码占 11 ms，混一次要 1 ms，等整帧解完再混就有一部分帧赶不上帧边界（实测 54 → 50 帧/秒）。
// 能混的条件是「排出去的、会写浮层那几行的解码活都做完了」：排活的只有本任务，所以混的过程中不会有新的活去写那几行；
// 解码任务这时在解的是别的行，互不相干。混完之后又有活写了那几行（补解、不按行的先后发的条带），s_osd_stale 会再立起来，到 PRESENT 时再混一次。
static bool     s_osd_stale = false;    // 上次混完之后，又排了会写浮层那几行的解码活（立着时 s_back 一定是正在解的那一帧的：活只在没有「解完放着的一帧」时排，PRESENT 处这一帧解完之前放下）
static uint32_t s_osd_job   = 0;        // 其中最后一个的编号

// wait = 一直等到那些活做完（PRESENT 处用，那之后这一帧就算解完了）；不 wait 的话还没做完就什么都不做，下次再看。
static void osd_draw(bool wait)
{
    while (s_osd_stale && (int32_t)(s_dec_done.load() - s_osd_job) < 0) {
        if (!wait) return;
        xSemaphoreTake(s_decw.tick, pdMS_TO_TICKS(5));      // 做完一个放一次；放在我们看计数之前的那一次会让这里空转一圈，不碍事
    }
    s_osd_stale = false;
    wdosd::draw(s_back, now_us());
}

// ── 收数据 ───────────────────────────────────────────────────────────────
enum class Io { Ok, PeerGone, Stop, ExitGesture, Late, Bad };   // Bad = 核对完的条带里有不合规定的，c 里已经记下原因
enum class Rx { Plain, Inline, Offload };       // 读到的数据：不用解密 / 在本任务里解密 / 交给解密任务解（见「后台任务」）

// 读满 n 字节。套接字的接收超时是 20 ms，所以等数据期间也会定期醒来读触摸、取按键、看退出标志，
// 并把解密任务核对完了的条带处理掉（不等它）：解码能早点开始；认证码没对上的，发送端之后不再发东西也照样马上发现。
// 有登记了还没生效的翻页时不卡在 recv 里：有数据就读，没有就等帧边界（见「翻页」），帧边界一过马上回 ACK、登记放着的那一帧、处理攒下的条带。
// mid = 已经在一个报文中间：这时 STALL_US 没有新数据算对端失联；报文之间可以一直等（画面不变就没有数据）。
// deadline = 不是 0 的话，到这个时刻还没读满就返回 Late（握手的每一步、第一帧上屏之前用）。
// rx = 读的是不是加密报文的载荷：是的话来一块就原地解一块（认证码对上之前，解出来的内容不能用）。
static Io read_full(Conn& c, void* dst, size_t n, bool mid, int64_t deadline, Rx rx = Rx::Plain)
{
    const int fd   = c.fd;
    uint8_t*  p    = (uint8_t*)dst;
    int64_t   last = now_us();
    while (n) {
        if (!s_want.load() || s_kick.load()) return Io::Stop;
        if (s_showing && s_dbg_exit.exchange(false)) return Io::ExitGesture;
        if (deadline && now_us() > deadline) return Io::Late;
        if (!apply_strips(c, false)) return Io::Bad;
        if (s_osd_stale) osd_draw(false);       // 调试信息：盖着它的条带解完了就先混上，不等这一帧其余的条带
        int r = recv(fd, p, n, c.flip.pending ? MSG_DONTWAIT : 0);
        if (r > 0) {
            if (rx == Rx::Offload) {
                Job j;
                j.kind = Job::Decrypt;
                j.data = {p, (size_t)r};
                s_rxw.post(j);
            } else if (rx == Rx::Inline && !s_rx.open_update(p, (size_t)r)) {
                return Io::PeerGone;        // 只有没设密钥才会走到，握手之后不会
            }
            p += r;
            n -= (size_t)r;
            mid  = true;
            last = now_us();
        } else if (r == 0) {
            return Io::PeerGone;
        } else if (errno != EAGAIN && errno != EWOULDBLOCK) {
            return Io::PeerGone;
        } else if (mid && now_us() - last > STALL_US) {
            return Io::PeerGone;
        } else if (c.flip.pending && flip_arrived(c, FLIP_POLL_MS) && !apply_strips(c, false)) {
            return Io::Bad;
        }
        // 没有数据来的时候调试信息也要更新（帧率该掉到 0）：原地改正显示的那一块（解码器只写后台那一块，不相干）。
        // 有登记了还没生效的翻页时不动——正显示的那一块马上要换下去了
        if (r < 0 && s_showing && !c.first && !c.flip.pending) wdosd::idle(s_front, now_us());
        if (!send_mode(fd)) return Io::PeerGone;     // 排在触点前面：换了方式之后的第一份 TOUCH 要按新的算
        switch (poll_touch(fd)) {
        case Touch::SendFailed:  return Io::PeerGone;
        case Touch::ExitGesture: return Io::ExitGesture;
        case Touch::Ok:          break;
        }
        if (!send_keys(fd) || !send_held(fd)) return Io::PeerGone;
    }
    return Io::Ok;
}

// ── 解码（排活和补解）────────────────────────────────────────────────────
// 排一个条带去解码（见「后台任务」）
static void dec_post(const Frame* f, const Strip& s)
{
    s_dec_posted++;
    if (wdosd::touched(s_back, s.row, s.h)) {       // 调试信息那一块里被这个条带盖住的行，解完之后要重新混（见「调试信息」）
        s_osd_stale = true;
        s_osd_job   = s_dec_posted;
    }
    Job j;
    j.kind  = Job::Decode;
    j.strip = {f, s};
    s_decw.post(j);
}

// 等排着的都解完，这一段的解码用时加到 decode_us 上。返回 false = 其中有解码失败的
static bool dec_join(uint32_t& decode_us)
{
    s_decw.join();
    decode_us += s_dec_us.exchange(0);
    return !s_dec_failed.exchange(false);
}

// 等排着的都解完（PRESENT 处用），用时加到 decode_us 上。和 dec_join 不同的是等的时候盯着前一帧的翻页：它一生效就马上回 ACK。
// 这一帧是在前一帧等帧边界的时候解的，光等解码的话，前一帧的 ACK 要晚到这一帧解完才回（晚十来毫秒），发送端的下一帧也跟着晚。
// 返回 false = 连接到此为止（解码失败，或者 ACK 发不出去），c 里已经记下原因
static bool dec_join_present(Conn& c, uint32_t& decode_us)
{
    Job j;
    j.kind = Job::Sync;
    s_decw.post(j);
    bool sent = true;
    for (;;) {
        if (!c.flip.pending) {
            xSemaphoreTake(s_decw.done, portMAX_DELAY);
            break;
        }
        if (xSemaphoreTake(s_decw.done, 0) == pdTRUE) break;
        // 翻页还没生效的时候解完得晚一两毫秒才知道也不碍事：这一帧反正要等它生效才能登记
        if (flip_arrived(c, FLIP_POLL_MS) && !flip_report(c)) sent = false;     // 发不出去也要等解码任务停手才能走
    }
    decode_us += s_dec_us.exchange(0);
    if (s_dec_failed.exchange(false)) {
        c.why = "解码失败";
        return false;
    }
    return sent;
}

// 往 s_back 的 [row, row+h) 写某一帧的条带之前，把更老的帧里和它重叠、还没补的条带了结掉：被它完全盖住的不用补，
// 只重叠一部分的现在就排去补。解码是按排的先后做的，同一块地方必须老的先解、新的后解，所以补的要排在它前面；
// 老的帧有两帧，补 prev 的条带之前同样要先了结 prev2 里和它重叠的（不然 prev2 的条带后解，会把 prev 的内容盖掉）。
// newer = 要写的条带是哪一帧的：1 = prev 的（只管 prev2），2 = cur 的（先管 prev2，再管 prev）
static void settle_under(int newer, uint16_t row, uint16_t h, uint32_t& catchup)
{
    Frame* const hist[2] = {s_prev2, s_prev};
    for (int k = 0; k < newer; k++) {
        Frame* f = hist[k];
        for (int i = 0; i < f->n; i++) {
            Strip& p = f->strips[i];
            if (!p.pending || p.row >= row + h || row >= p.row + p.h) continue;
            p.pending = false;
            if (p.row >= row && p.row + p.h <= row + h) continue;
            if (k > 0) settle_under(k, p.row, p.h, catchup);
            dec_post(f, p);
            catchup++;
        }
    }
}

// 一帧的条带都排上之后：前两帧剩下还没补的条带全部排去补上，s_back 里才是完整的一帧。
// 走到这里还没补的 prev2 的条带，和已经排上的条带（这一帧的、prev 的）都不重叠，所以排在最后也不会盖掉新内容
static void settle_all(uint32_t& catchup)
{
    for (int i = 0; i < s_prev->n; i++) {
        Strip& p = s_prev->strips[i];
        if (!p.pending) continue;
        p.pending = false;
        settle_under(1, p.row, p.h, catchup);
        dec_post(s_prev, p);
        catchup++;
    }
    for (int i = 0; i < s_prev2->n; i++) {
        Strip& p = s_prev2->strips[i];
        if (!p.pending) continue;
        p.pending = false;
        dec_post(s_prev2, p);
        catchup++;
    }
}

// ── 服务一条连接 ─────────────────────────────────────────────────────────
static void disc_drain();       // 在下面「任务」一节

// 告诉发送端这条连接到此为止（BYE 是明文还是加密，看 AUTH_OK 发出去没有）。返回发出去没有。
// 发之前先把连接期间攒下的自动发现询问读掉（什么时候读、为什么，见 disc_drain）
static bool say_bye(int fd)
{
    disc_drain();
    return send_msg(fd, T_DEV_BYE, 0);
}

// 读数据没读成时的结束原因。late = 等过了时限的话该怎么说。
// 是 Tab5 这边要结束的（暂停、调试命令、退出手势），先告诉发送端
static void read_ended(Conn& c, Io io, const char* late)
{
    if (io == Io::PeerGone || io == Io::Bad) return;
    if (io == Io::Late) {
        violation(c, "%s", late);
        return;
    }
    c.user_exit = io == Io::ExitGesture;
    c.why       = c.user_exit ? "退出手势" : "本机断开";
    c.said_bye  = say_bye(c.fd);
}

// ── 握手（PROTOCOL.md「连上之后走哪条路」Tab5 那张表）────────────────────
// 握手里发送端来的一个明文报文。raw 是它在线上的字节：算摘要要用
struct HsMsg {
    uint8_t raw[sizeof(Hdr) + PAIR_LEN];        // 最长的是 PAIR
    size_t  size = 0;                           // 头 + 载荷
    uint8_t        type() const { return raw[0]; }
    const uint8_t* body() const { return raw + sizeof(Hdr); }
};

// 收握手的下一个报文：只收 a、b 两种（b = 0 是只收 a），flags 和 arg 是 0，长度是那种报文的定长，整个报文在 HS_STEP_US 之内到齐。
// names 是这一步该来的报文的名字（给日志用）。返回 false = 握手到此为止，c 里已经记下原因
static bool hs_recv(Conn& c, HsMsg& m, uint8_t a, uint8_t b, const char* names)
{
    const int64_t deadline = now_us() + HS_STEP_US;
    char late[72];
    snprintf(late, sizeof late, "握手：%d 秒内没有等到 %s", (int)(HS_STEP_US / 1000000), names);
    Io io = read_full(c, m.raw, sizeof(Hdr), false, deadline);
    if (io != Io::Ok) {
        read_ended(c, io, late);
        return false;
    }
    Hdr h;
    memcpy(&h, m.raw, sizeof h);
    // 先定下类型是这一步该来的，再按类型看长度：下面要照 h.len 往 m.raw 里读
    const bool   wanted = h.type == a || (b != 0 && h.type == b);
    const size_t len    = h.type == T_AUTH ? AUTH_LEN : h.type == T_PAIR ? PAIR_LEN : PAIR_OK_LEN;
    if (!wanted || h.flags || h.arg || h.len != len)
        return violation(c, "握手：这一步该来 %s，来的是 0x%02x（flags=%u arg=%u，载荷 %u 字节）", names, h.type, h.flags, h.arg, (unsigned)h.len);
    io = read_full(c, m.raw + sizeof(Hdr), len, true, deadline);
    if (io != Io::Ok) {
        read_ended(c, io, late);
        return false;
    }
    m.size = sizeof(Hdr) + len;
    return true;
}

// 把界面上的密码框同步成 s_prompt 现在的样子（seq 是 0 = 不该开着）。在 LVGL 线程里跑。
// 不分别排「弹出」「关掉」两种回调：lv_async_call 不保证先排的先跑（新排的插在队头），一关一弹挨得近时先后会颠倒。
// 照最新的状态来就不怕颠倒；同一次询问因此可能通知不止一次，界面按 seq 认出来
static void ui_sync_cb(void*)
{
    if (s_prompt.seq) {
        if (s_ui.pair_prompt) s_ui.pair_prompt(s_prompt);
    } else if (s_ui.pair_close) {
        s_ui.pair_close();
    }
}

// 让界面弹出密码框（或者把开着的换成这一次的内容）
static void post_prompt(const PairPrompt& ask)
{
    portENTER_CRITICAL(&s_pw_mux);
    s_ask_seq  = ask.seq;
    s_pw_ready = false;
    portEXIT_CRITICAL(&s_pw_mux);
    lvgl_port_lock(0);
    s_prompt = ask;
    lv_async_call(ui_sync_cb, nullptr);
    lvgl_port_unlock();
}

// 让界面关掉密码框；这之后才交来的密码不作数
static void post_close()
{
    portENTER_CRITICAL(&s_pw_mux);
    s_ask_seq  = 0;
    s_pw_ready = false;
    memset(s_pw, 0, sizeof s_pw);
    portEXIT_CRITICAL(&s_pw_mux);
    lvgl_port_lock(0);
    s_prompt = {};
    lv_async_call(ui_sync_cb, nullptr);
    lvgl_port_unlock();
}

enum class Box { Answer, PeerGone, PeerSpoke, Timeout, Stop };

// 密码框开着，等人输完（密码拷进 out）。这期间电脑什么都不该发；连接断了、到时间了、本机要结束了也返回
static Box wait_password(int fd, char out[PASSWORD_MAX + 1])
{
    const int64_t t0 = now_us();
    for (;;) {
        if (!s_want.load() || s_kick.load()) return Box::Stop;
        bool got = false;
        portENTER_CRITICAL(&s_pw_mux);
        if (s_pw_ready) {
            memcpy(out, s_pw, sizeof s_pw);
            memset(s_pw, 0, sizeof s_pw);
            s_pw_ready = false;
            s_ask_seq  = 0;         // 这一次询问有了回答，再交来的不作数
            got        = true;
        }
        portEXIT_CRITICAL(&s_pw_mux);
        if (got) return Box::Answer;
        if (now_us() - t0 > PASSWORD_BOX_US) return Box::Timeout;
        uint8_t peek;
        int     r = recv(fd, &peek, 1, MSG_PEEK);       // 只看不取；接收超时 20 ms（见 setup_client）
        if (r > 0) return Box::PeerSpoke;
        if (r == 0 || (errno != EAGAIN && errno != EWOULDBLOCK)) return Box::PeerGone;
    }
}

// 走「配对」。m 进来是发送端的 PAIR，出去是配对成功之后紧跟的那个 AUTH（已经核对过它用的是配对时的编号和刚配出来的密钥）；
// key 里是新配出来的密钥。返回 false = 握手到此为止，c 里已经记下原因。密码框不管怎么结束都在这里面关掉
static bool pair(Conn& c, const uint8_t* hello, size_t hello_len, const uint8_t self[wdc::ID_LEN], HsMsg& m, uint8_t key[wdc::KEY_LEN])
{
    static uint32_t seq = 0;        // 询问的编号，只有本任务用
    uint8_t         pc_id[wdc::ID_LEN];
    wdc::PairOut    out;
    bool            wrong = false, paired = false;
    for (;;) {
        const PairBody* p = (const PairBody*)m.body();
        if (!wdc::point_ok(p->pA, sizeof p->pA)) {
            violation(c, "PAIR：pA 不是曲线上的点");
            break;
        }
        memcpy(pc_id, p->id, sizeof pc_id);

        PairPrompt ask = {};
        ask.seq = ++seq ? seq : ++seq;      // 0 留作「没有在问」
        wdpair::clean_name(p->name, ask.pc_name);
        ask.known = wdpair::find(p->id, nullptr);
        ask.wrong = wrong;
        post_prompt(ask);
        ESP_LOGW(TAG, "配对：等人输密码（电脑「%s」%s%s）", ask.pc_name, ask.known ? "，以前配对过" : "", wrong ? "，上一次输的不对" : "");

        char password[PASSWORD_MAX + 1];
        Box  box = wait_password(c.fd, password);
        if (box == Box::PeerSpoke) violation(c, "密码框开着的时候电脑发来了东西（这期间它只能等 PAIR_REPLY）");
        if (box == Box::Timeout) c.why = "密码框到时间了";      // 不算人拒绝：不发 BYE，电脑过一会儿会再连上来
        if (box == Box::Stop) read_ended(c, Io::Stop, "");
        if (box != Box::Answer) break;

        // 这一步要算两三秒（PBKDF2 一万轮 + 三次点乘），一直占着一个核：降到最低的优先级算，不和界面抢
        uint8_t aad[wdc::HASH_LEN];
        wdc::sha256_cat(hello, hello_len, m.raw, m.size, nullptr, 0, aad);
        const UBaseType_t prio = uxTaskPriorityGet(nullptr);
        const int64_t     t0   = now_us();
        vTaskPrioritySet(nullptr, 1);
        wdc::Pair r = wdc::spake2_b(password, p->pA, p->id, self, aad, out);
        vTaskPrioritySet(nullptr, prio);
        wdc::wipe(password, sizeof password);
        if (r == wdc::Pair::BadPoint) violation(c, "PAIR：算出来的 K 是无穷远点");
        if (r == wdc::Pair::Failed) c.why = "本机出错：配对算不出来";
        if (r != wdc::Pair::Ok) break;
        ESP_LOGW(TAG, "配对：算了 %u ms", (unsigned)((now_us() - t0) / 1000));
        // 算的这两三秒里人点了取消（或者本机要断开）：PAIR_REPLY 不发了，直接告别
        if (!s_want.load() || s_kick.load()) {
            read_ended(c, Io::Stop, "");
            break;
        }

        uint8_t reply[PAIR_REPLY_LEN];
        memcpy(reply, out.pB, wdc::POINT_LEN);
        memcpy(reply + wdc::POINT_LEN, out.cB, wdc::CONFIRM_LEN);
        if (!send_msg(c.fd, T_PAIR_REPLY, 0, reply, sizeof reply)) break;
        if (!hs_recv(c, m, T_PAIR, T_PAIR_OK, "PAIR_OK 或 PAIR")) break;
        if (m.type() == T_PAIR_OK) {
            if (wdc::same(m.body(), out.cA, wdc::CONFIRM_LEN)) {
                memcpy(key, out.key, wdc::KEY_LEN);
                paired = true;
            } else {
                violation(c, "PAIR_OK 里的 cA 不对");
            }
            break;
        }
        wrong = true;       // 又来了一个 PAIR：刚才输的密码和电脑的不一样
    }
    wdc::wipe(&out, sizeof out);
    post_close();
    if (!paired) return false;

    if (!hs_recv(c, m, T_AUTH, 0, "AUTH")) return false;
    const AuthBody* a = (const AuthBody*)m.body();
    uint8_t         kid[wdc::KEY_ID_LEN];
    wdc::key_id(key, kid);
    if (memcmp(a->id, pc_id, sizeof pc_id) != 0 || !wdc::same(a->key_id, kid, sizeof kid))
        return violation(c, "配对之后的 AUTH 用的不是配对时的电脑编号、或者不是刚配出来的那把密钥");
    return true;
}

// 握手的各步。key = 这条连接用的配对密钥，放在调用方那里：从哪里返回它都负责清掉（见 handshake）
static bool handshake_steps(Conn& c, uint8_t key[wdc::KEY_LEN])
{
    const int     fd = c.fd;
    const int64_t t0 = now_us();
    if (!wdc::rand_ready()) {
        c.why = "本机出错：随机数生成器播不了种";
        return false;
    }
    wdc::rand_stir();

    uint8_t hello[sizeof(Hdr) + HELLO_LEN];
    uint8_t self[wdc::ID_LEN];
    {
        Hdr       h = {T_HELLO, 0, PROTO_VER, HELLO_LEN};
        HelloBody b = {};
        char      name[wdpair::NAME_LEN + 1];
        b.fb_w = FB_W;  b.fb_h = FB_H;  b.view_w = VIEW_W;  b.view_h = VIEW_H;
        b.max_strip = MAX_STRIP_BYTES;
        b.max_frame = MAX_FRAME_BYTES;
        s_yuv_offered = s_yuv_fails < YUV_FAILS_MAX;
        b.caps      = CAP_DEV_AUDIO | (s_yuv_offered ? CAP_DEV_YUV422 : 0);
        wdpair::self_id(self);
        wdpair::self_name(name);
        memcpy(b.id, self, sizeof self);
        memcpy(b.name, name, strnlen(name, sizeof b.name));     // 不足的已经是 0
        memcpy(hello, &h, sizeof h);
        memcpy(hello + sizeof h, &b, sizeof b);
    }
    if (!send_all(fd, hello, sizeof hello)) return false;

    HsMsg          m;
    const uint32_t since = wdpair::removals();      // 握手期间人在 Tab5 上移除过配对的话，走完不存（见 wdpair::save）
    bool           fresh = false;                   // 配对密钥是这条连接上刚配出来的
    if (!hs_recv(c, m, T_AUTH, T_PAIR, "AUTH 或 PAIR")) return false;
    if (m.type() == T_AUTH) {
        // 没有这台电脑的配对，或者它拿来的不是我们存的这一把：让它重新配对
        const AuthBody* a = (const AuthBody*)m.body();
        uint8_t         kid[wdc::KEY_ID_LEN];
        bool            known = wdpair::find(a->id, key);
        if (known) {
            wdc::key_id(key, kid);
            known = wdc::same(kid, a->key_id, sizeof kid);
        }
        if (!known) {
            if (!send_msg(fd, T_AUTH_UNKNOWN, 0)) return false;
            if (!hs_recv(c, m, T_PAIR, 0, "PAIR")) return false;
        }
    }
    if (m.type() == T_PAIR) {
        if (!pair(c, hello, sizeof hello, self, m, key)) return false;
        fresh = true;
    }

    // 每次连接：m 是 AUTH。取一次性密钥，算出两个方向的会话密钥，AUTH_OK 和 READY（加密）一次发出，中间不插别的
    const AuthBody* a = (const AuthBody*)m.body();
    uint8_t         ok_ready[sizeof(Hdr) + AUTH_OK_LEN + sizeof(Hdr) + wdc::TAG_LEN];
    uint8_t*        ready = ok_ready + sizeof(Hdr) + AUTH_OK_LEN;
    uint8_t         priv[wdc::X25519_LEN], dh[wdc::X25519_LEN], th[wdc::HASH_LEN], k_in[wdc::KEY_LEN], k_out[wdc::KEY_LEN];
    const Hdr       okh = {T_AUTH_OK, 0, 0, AUTH_OK_LEN}, rh = {T_READY, 0, 0, 0};
    memcpy(ok_ready, &okh, sizeof okh);
    memcpy(ready, &rh, sizeof rh);
    bool mine = wdc::x25519_keypair(priv, ok_ready + sizeof(Hdr));
    bool peer = mine && wdc::x25519_shared(priv, a->pub, dh);
    bool keys = false;
    if (peer) {
        wdc::sha256_cat(hello, sizeof hello, m.raw, m.size, ok_ready, sizeof(Hdr) + AUTH_OK_LEN, th);
        keys = wdc::session_keys(key, dh, th, k_in, k_out);
    }
    if (keys) {
        s_rx.set_key(k_in);
        s_tx.set_key(k_out);
        keys = s_tx.seal(ready, nullptr, 0, ready + sizeof(Hdr));
    }
    wdc::wipe(priv, sizeof priv);
    wdc::wipe(dh, sizeof dh);
    wdc::wipe(k_in, sizeof k_in);
    wdc::wipe(k_out, sizeof k_out);
    if (!keys) {
        // 共享值算不出来多半是对方给的公钥不合格（低阶点：共享值全 0），本机算的时候出错也是这个结果，分不出来
        if (mine && !peer) return violation(c, "AUTH：拿它的一次性公钥算不出共享值（公钥不合格、共享值全 0，也可能是本机出错）");
        c.why = "本机出错：会话密钥算不出来";
        return false;
    }
    s_tx_on = true;         // AUTH_OK 是最后一个明文报文
    bool sent = send_all(fd, ok_ready, sizeof ok_ready);

    // 发送端的 READY（加密）：认证码对上了，才算它真有这把配对密钥
    Hdr     h = {};
    uint8_t tag[wdc::TAG_LEN];
    bool    ok = false;
    if (sent) {
        const int64_t deadline = now_us() + HS_STEP_US;
        Io            io       = read_full(c, &h, sizeof h, false, deadline);
        if (io == Io::Ok && h.len) {
            violation(c, "发送端加密后的第一个报文 0x%02x 带了 %u 字节的载荷，不是 READY", h.type, (unsigned)h.len);
        } else {
            if (io == Io::Ok) io = read_full(c, tag, sizeof tag, true, deadline);
            if (io != Io::Ok) read_ended(c, io, "握手：5 秒内没有等到 READY");
            else if (!s_rx.open_begin((const uint8_t*)&h) || !s_rx.open_finish(tag)) violation(c, "发送端的 READY 认证码对不上");
            else if (h.type != T_READY || h.flags || h.arg)
                violation(c, "发送端加密后的第一个报文是 0x%02x（flags=%u arg=%u），不是 READY", h.type, h.flags, h.arg);
            else ok = true;
        }
    }
    if (ok) {
        // 握手走完了才存：新配的密钥、电脑这次报的名字。握手期间人在 Tab5 上移除过配对的话就不能存（刚移除的不能被存回去）：
        // save() 拿 since 核对，新配对和照旧密钥认证过来的都管。先登记「正连着的是这台电脑」再存——forget() 是先移除、再看登记：
        // 它的移除排在 save() 前面，save() 发现了、不存，这条连接在下面结束；排在后面，它看得到登记、把这条连接结束掉
        char name[wdpair::NAME_LEN + 1];
        wdpair::clean_name(a->name, name);
        portENTER_CRITICAL(&s_stats_mux);
        memcpy(s_peer_id, a->id, sizeof s_peer_id);
        s_peer_known = true;
        strlcpy(s_stats.name, name, sizeof s_stats.name);
        portEXIT_CRITICAL(&s_stats_mux);
        if (wdpair::save(a->id, key, a->name, fresh, since)) {
            s_mode_peer = (a->caps & CAP_PC_INPUT_MODE) != 0;       // 不认识的位不管（协议「握手」）
            s_gain_peer = (a->caps & CAP_PC_TPAD_GAIN) != 0;
            s_pic_peer  = (a->caps & CAP_PC_PICTURE) != 0;
            s_picmode_peer = (a->caps & CAP_PC_PIC_MODE) != 0;
            s_yuv       = s_yuv_offered && (a->caps & CAP_PC_YUV422) != 0;      // HELLO 里没说能走的，对方置了也不算（协议「画面的色彩」）
            ESP_LOGW(TAG, "握手走完：电脑「%s」，%s，用了 %u ms；画面走 %s%s", name, fresh ? "刚配对的" : "配对过的", (unsigned)((now_us() - t0) / 1000),
                     s_yuv ? "YUV422" : s_yuv_offered ? "RGB565（它没说用 YUV422）" : "RGB565（这次开机切不到 YUV422，没让它用）",
                     !s_mode_peer ? "；它不认得触摸方式的报文（旧版发送端），触摸板模式对它不起作用"
                     : !s_gain_peer ? "；它不认得触摸板加速的报文（旧版发送端），加速多少按它自己的来"
                     : !s_pic_peer && !s_picmode_peer ? "；它没说认得画质的报文，画质按它自己的来"
                     : !s_picmode_peer ? "；它不认得画质档位的报文（旧版发送端），折成质量和预算告诉它" : "");
        } else {
            c.why      = "握手期间 Tab5 上移除过配对";
            c.said_bye = say_bye(fd);
            ok         = false;
        }
    }
    return ok;
}

// 发 HELLO，走完握手：两个方向的加密状态设好，配对存好。返回 false = 握手没走完，c 里已经记下原因
static bool handshake(Conn& c)
{
    uint8_t    key[wdc::KEY_LEN];
    const bool ok = handshake_steps(c, key);
    wdc::wipe(key, sizeof key);         // 握手怎么结束的都清
    return ok;
}

// 一个认证码对上了的条带：查 JPEG 文件头和位置，记进这一帧，排去解码（第一帧还没上屏的话先收着，到 PRESENT 时一起解）。
// 返回 false = 发送端不守协议，c 里已经记下原因
static bool apply_strip(Conn& c, Strip s)
{
    hwjpeg::Info inf = {};
    if (!hwjpeg::info(s_cur->buf + s.off, s.len, inf)) return violation(c, "条带（起始行 %u）不是能解的 JPEG", s.row);
    if (inf.w != FB_W || inf.h % STRIP_ALIGN || s.row + inf.h > FB_H)
        return violation(c, "条带（起始行 %u）是 %d×%d，要求宽 %d、高是 %d 的倍数、不超出 %d 行", s.row, inf.w, inf.h, FB_W, STRIP_ALIGN, FB_H);
    if (s_yuv && inf.hv != 0x21) return violation(c, "条带（起始行 %u）的色度采样不是 4:2:2（握手里说了走 YUV422 的连接只能发 4:2:2）", s.row);
    s.h = (uint16_t)inf.h;
    for (int u = s.row / STRIP_ALIGN; u < (s.row + s.h) / STRIP_ALIGN; u++) {
        if (s_cur->covered[u]) return violation(c, "条带（起始行 %u，高 %u）和同一帧里别的条带重叠", s.row, s.h);
        s_cur->covered[u] = 1;
    }
    uint32_t catchup = 0;
    if (!c.first) {
        settle_under(2, s.row, s.h, catchup);
        dec_post(s_cur, s);
    }
    s_cur->strips[s_cur->n++] = s;
    portENTER_CRITICAL(&s_stats_mux);
    s_stats.strips++;
    s_stats.catchup += catchup;
    portEXIT_CRITICAL(&s_stats_mux);
    return true;
}

// 翻页往前走一步，再把交给解密任务的条带里已经核对完的按到达的先后处理掉。wait = 先等解密任务把手里的活做完（于是全部都会处理到）。
// 有解完放着的一帧（c.flip.ready）时条带不能处理（处理 = 排去解码，这时没有能写的帧缓冲）：前一帧的翻页生效了就登记它，然后照常处理；
// 还没生效的话，wait 就等帧边界，不 wait 这次就不处理。只有登记了没生效的翻页、没有放着的一帧时，条带照常处理（解进空着的那一块）。
// 翻页刚生效的那一帧在这里回 ACK——排在处理条带之后：先让解码开工，再去发报文。
// 这样排的前提是攒下的条带（连同要补解的）排得进解码队列、不用等：发送端默认每 160 行一条，一帧 8 条，加上补解前两帧的最多 24 个活，
// 队列放得下。条带切得很细（16 行一条，一帧 80 条）时会卡在排队上等解码腾位置，ACK 晚几十毫秒——只是慢，不会错。
// 返回 false = 连接到此为止，c 里已经记下原因（ACK 发不出去的话就是默认的「连接断开」）
static bool apply_strips(Conn& c, bool wait)
{
    Flip& f = c.flip;
    bool arrived = flip_arrived(c, 0);
    if (f.ready) {
        if (!arrived && wait) {
            while (!flip_arrived(c, (uint32_t)(FLIP_TIMEOUT_US / 1000))) {}     // 最多等到 FLIP_TIMEOUT_US，到时一定返回 true
            arrived = true;
        }
        if (arrived) {
            if (!fb_register()) {
                c.why = "翻页失败";
                return false;
            }
            f.reg     = f.next;
            f.t_reg   = now_us();
            f.pending = true;
            f.ready   = false;
        }
    }
    if (!f.ready) {
        if (wait && s_pend_done < s_pend_n) s_rxw.join();
        while (s_pend_done < s_pend_n) {
            const Pending& p = s_pend[s_pend_done];
            const uint8_t  v = p.verdict.load();
            if (v == 0) break;
            s_pend_done++;
            if (v != 1) return violation(c, "条带（起始行 %u，载荷 %u 字节）的认证码对不上", p.s.row, (unsigned)p.s.len);
            if (!apply_strip(c, p.s)) return false;
        }
        if (s_pend_done == s_pend_n) s_pend_done = s_pend_n = 0;
    }
    return !f.due || flip_report(c);
}

// 握手走完之后收画面，直到一方结束。返回 true = 是我们这边要结束的、已经给发送端发了 BYE（之后连接怎么关见 drain_peer）。
static bool serve(int fd)
{
    Conn c;
    c.fd = fd;
    for (Frame& f : s_frames) f.reset();
    input_reset();
    s_osd_stale = false;
    s_yuv = false;                  // 握手走完时按发送端说的定（handshake_steps）
    // 第一帧整帧到齐（收到它的 PRESENT）才接管屏幕，这之前屏幕一直是 Tab5 自己的界面、照常能用。
    // 来一个条带就接管的话，发送端发到一半停下，屏幕会冻在 Tab5 的界面上，点哪里都没反应。
    // 第一帧还限时：握手走完 FIRST_FRAME_US 之内必须发完，不然一条只连不发的连接能一直占着唯一的位置。
    int64_t  t_ready    = 0;            // 握手走完的时刻
    int64_t  t_first    = 0;            // 这一帧第一个条带的报文头到达的时刻
    uint32_t decode_us  = 0;
    char     late[64];
    snprintf(late, sizeof late, "握手走完 %d 秒内没有发完第一帧", (int)(FIRST_FRAME_US / 1000000));

#define VIOLATION(...) do { violation(c, __VA_ARGS__); goto done; } while (0)

    if (!handshake(c)) goto done;
    t_ready = c.win_t0 = now_us();
    wdosd::reset(s_yuv);
    wdaudio::reset_stats();             // 声音的统计按连接算（和画面的一样）
    wdaudio::start();                   // 扬声器现在就开始预备（开 codec、开功放要 0.2 秒上下），到下面的 stop() 为止

    for (;;) {
        const int64_t deadline = c.first ? t_ready + FIRST_FRAME_US : 0;
        Hdr h;
        Io  io = read_full(c, &h, sizeof h, false, deadline);
        if (io != Io::Ok) { read_ended(c, io, late); goto done; }
        const int64_t t_hdr = now_us();

        // 报文头一到先检查（类型、flags、长度），不合规定的不去读后面的
        uint8_t        small[sizeof(PresentBody)];
        uint8_t*       body = small;
        if (h.flags) VIOLATION("报文 0x%02x 的 flags 是 %u", h.type, h.flags);
        switch (h.type) {
        case T_STRIP:
            if (h.arg % STRIP_ALIGN) VIOLATION("条带起始行 %u 不是 %d 的倍数", h.arg, STRIP_ALIGN);
            if (h.len == 0 || h.len > MAX_STRIP_BYTES) VIOLATION("条带 %u 字节，应在 1~%u 之间", (unsigned)h.len, (unsigned)MAX_STRIP_BYTES);
            if (s_cur->bytes + h.len > MAX_FRAME_BYTES) VIOLATION("一帧超过上限 %u 字节", (unsigned)MAX_FRAME_BYTES);
            if (s_cur->n + (s_pend_n - s_pend_done) >= UNITS) VIOLATION("一帧的条带超过 %d 个", UNITS);
            if (s_cur->bytes == 0) t_first = t_hdr;         // 这一帧的第一个条带
            body = s_cur->buf + s_cur->bytes;
            break;
        case T_PRESENT:
            if (h.arg || h.len != sizeof(PresentBody)) VIOLATION("PRESENT 的 arg / 长度是 %u / %u", h.arg, (unsigned)h.len);
            break;
        case T_AUDIO:
            if (h.arg || h.len == 0 || h.len > wdaudio::MAX_CHUNK || h.len % wdaudio::FRAME_BYTES)
                VIOLATION("AUDIO 的 arg / 长度是 %u / %u（长度应是 %d 的倍数、不超过 %u）", h.arg, (unsigned)h.len, wdaudio::FRAME_BYTES, (unsigned)wdaudio::MAX_CHUNK);
            body = s_audio_buf;
            break;
        case T_KBD_LEDS:
        case T_BYE:
            if (h.len) VIOLATION("报文 0x%02x 带了 %u 字节载荷", h.type, (unsigned)h.len);
            break;
        default:
            VIOLATION("不认识的报文类型 0x%02x", h.type);
        }

        // 大的条带：密文直接收进帧数据缓冲，解密和核对交给解密任务，这里不等结果、接着收后面的报文（见「后台任务」）
        if (h.type == T_STRIP && h.len >= RX_OFFLOAD_MIN) {
            Pending& p = s_pend[s_pend_n++];
            p.s = Strip{h.arg, 0, s_cur->bytes, h.len, false};
            memcpy(p.hdr, &h, sizeof h);
            p.verdict.store(0);
            Job j;
            j.kind = Job::Begin;
            j.pend = &p;
            s_rxw.post(j);
            io = read_full(c, body, h.len, true, deadline, Rx::Offload);
            if (io == Io::Ok) io = read_full(c, p.tag, sizeof p.tag, true, deadline);
            if (io != Io::Ok) { read_ended(c, io, late); goto done; }
            j.kind = Job::Finish;
            s_rxw.post(j);
            s_cur->bytes += h.len;
            continue;
        }

        // 别的报文在这里解密、核对。s_rx 要按报文的先后用：先等解密任务把手里的条带解完。
        // 条带、PRESENT、BYE 还要排在前面那些条带后面才作数，所以先把它们都处理完（有解完放着的一帧就等帧边界，登记了它才处理得了）；
        // 声音和指示灯和画面没有先后，不为它们等翻页（声音夹在条带之间来，每次都等的话「等帧边界时接着收」就白做了）
        if (h.type == T_AUDIO || h.type == T_KBD_LEDS) {
            if (s_pend_done < s_pend_n) s_rxw.join();
        } else if (!apply_strips(c, true)) {
            goto done;
        }
        uint8_t tag[wdc::TAG_LEN];
        if (!s_rx.open_begin((const uint8_t*)&h)) { c.why = "本机出错：解密"; goto done; }
        if (h.len) io = read_full(c, body, h.len, true, deadline, Rx::Inline);
        if (io == Io::Ok) io = read_full(c, tag, sizeof tag, true, deadline);
        if (io != Io::Ok) { read_ended(c, io, late); goto done; }
        if (!s_rx.open_finish(tag)) VIOLATION("报文 0x%02x（载荷 %u 字节）的认证码对不上", h.type, (unsigned)h.len);

        if (h.type == T_STRIP) {
            const Strip s = {h.arg, 0, s_cur->bytes, h.len, false};
            s_cur->bytes += h.len;
            if (!apply_strip(c, s)) goto done;
        } else if (h.type == T_PRESENT) {
            PresentBody pb;
            memcpy(&pb, small, sizeof pb);
            if (s_cur->n == 0) VIOLATION("PRESENT 之前这一帧没有条带");
            uint32_t catchup = 0;
            if (c.first) {
                for (int u = 0; u < UNITS; u++)
                    if (!s_cur->covered[u]) VIOLATION("第一帧没有覆盖整屏（第 %d 行起没有条带）", u * STRIP_ALIGN);
                if (!enter_show()) { c.why = "接管不了屏幕"; goto done; }
                kbd::listen(true);      // 屏幕上是发送端的画面了，从这时起按键归它
                for (int i = 0; i < s_cur->n; i++) dec_post(s_cur, s_cur->strips[i]);
            } else {
                settle_all(catchup);
            }
            osd_draw(true);         // 调试信息（开着的话）混到这一帧上：只等盖着它的条带，其余的条带这时接着解
            // 这一帧的条带（和要补的）都解完了才能翻页。等的时候前一帧的翻页生效了就回它的 ACK
            if (!dec_join_present(c, decode_us)) goto done;
            // 这一帧解完了，放着等登记（见「翻页」）。走到这里时没有别的帧放着（前面的 apply_strips(c, true)）
            c.flip.next.t_hdr     = t_hdr;
            c.flip.next.seq       = pb.seq;
            c.flip.next.t_ms      = pb.t_ms;
            c.flip.next.recv_us   = (uint32_t)(t_hdr - t_first);
            c.flip.next.decode_us = decode_us;
            c.flip.next.bytes     = s_cur->bytes;
            c.flip.next.catchup   = catchup;
            c.flip.ready          = true;

            // 这一帧和它前面的一帧留着，用来追平另外两块缓冲；下一帧的数据从现在起收进最老的那一个（它的条带上面都补完了）。
            // 下一帧要解进去的那一块比这一帧落后两帧，所以留着的两帧的条带都算没补
            Frame* t = s_prev2;  s_prev2 = s_prev;  s_prev = s_cur;  s_cur = t;
            for (int i = 0; i < s_prev->n; i++) s_prev->strips[i].pending = true;
            for (int i = 0; i < s_prev2->n; i++) s_prev2->strips[i].pending = true;
            s_cur->reset();
            c.first   = false;
            decode_us = 0;
            if (!apply_strips(c, false)) goto done;     // 前一帧的翻页已经生效了的话现在就登记这一帧，不用等下一个报文来
            if (c.flip.ready) {                         // 没登记上：这一帧解得比屏幕刷新快，要放着等前一帧的翻页
                portENTER_CRITICAL(&s_stats_mux);
                s_stats.held++;
                portEXIT_CRITICAL(&s_stats_mux);
            }
        } else if (h.type == T_AUDIO) {
            wdaudio::push(s_audio_buf, h.len);        // 只拷进播放缓冲，不等；接管屏幕之前到的也放（声音不用等第一帧）
        } else if (h.type == T_KBD_LEDS) {
            kbd::set_leds((uint8_t)h.arg);      // 主机的键盘指示灯状态，亮在键盘模块的灯上
        } else {                                // T_BYE
            c.why = "发送端结束";
            goto done;
        }
    }
#undef VIOLATION

done:
    // 不是我们发 BYE 结束的（发送端的 BYE、连接断了、对方违规……）：连接期间攒下的自动发现询问现在就读掉，赶在下面的收尾之前。
    // 我们发 BYE 结束的，发之前已经读过（say_bye），那之后到的是新的，留着
    if (!c.said_bye) disc_drain();
    {
        // 登记了的翻页先走完（不回 ACK 了）；解完放着、还没登记的那一帧不上屏了（它在 s_back 里，没人再用）；
        // 两个后台任务手里的活做完（或者跳过）了，才能把屏幕还回去、清密钥、动三帧的数据
        while (!flip_arrived(c, (uint32_t)(FLIP_TIMEOUT_US / 1000))) {}
        c.flip.ready = false;
        uint32_t unused = 0;
        s_rxw.join();
        dec_join(unused);
        s_pend_n = s_pend_done = 0;
    }
    if (c.user_exit) s_want.store(false);   // 不再监听，见 Ui::screen_back 的说明
    kbd::listen(false);                     // 按键不再给这条连接，指示灯灭掉（没接管过屏幕的连接也可能点过灯）
    wdaudio::stop();                        // 没放完的声音扔掉
    leave_show(c.user_exit);
    s_tx_on = false;                        // 这条连接的会话密钥到此为止
    s_tx.clear();
    s_rx.clear();
    portENTER_CRITICAL(&s_stats_mux);
    s_peer_known = false;
    portEXIT_CRITICAL(&s_stats_mux);
    Stats st = stats();
    ESP_LOGW(TAG, "连接结束（%s%s%s）：%u 帧，%u 个条带（另补解 %u 个），%u KB；任务栈最低余量 %u 字节（解密任务 %u，解码任务 %u）；解完之后等过前一帧翻页的 %u 帧", c.why, c.detail[0] ? "：" : "",
             c.detail, (unsigned)st.frames, (unsigned)st.strips, (unsigned)st.catchup, (unsigned)(st.bytes >> 10),
             (unsigned)uxTaskGetStackHighWaterMark(nullptr), (unsigned)uxTaskGetStackHighWaterMark(s_rxw.task),
             (unsigned)uxTaskGetStackHighWaterMark(s_decw.task), (unsigned)st.held);
    return c.said_bye;
}

// 我们发了 BYE 之后的收尾：不马上关，把发送端还在路上的数据读掉，等它关连接（它读到 BYE 就会关）之后再关。
// 发送端关连接之前要先把已经交给系统的数据发完（画面大的时候有一两 MB），所以只要数据还在来就接着读；等多久见 BYE_IDLE_US。
// 发完 BYE 立刻 close 的话，接收缓冲里有没读的数据、或者关了之后又有数据到，lwIP 回的是 RST；Windows 收到 RST 会把还没读的数据
// （就是那个 BYE）丢掉，发送端于是把「Tab5 结束的」当成掉线、自动重连——而协议规定收到 BYE 不得自动重连。
// 调用时界面已经回到 Tab5 自己的页面，等的这一会儿用户看不到。
//
// 这里不能先 shutdown(fd, SHUT_WR)（「只关发送方向」）再读：发送端读到 BYE 就关连接，它的 FIN 常常比我们的 shutdown 先到，
// 这时 lwIP 里这条连接进 LAST_ACK；接着 recv 读到头，lwIP 的套接字层自动关接收方向、却还留着控制块的指针；对端最后那个 ACK 一到，
// 协议栈释放控制块而不通知套接字层，随后的 close() 再释放一次——断言失败重启（那块内存要是已经被别人分走，就成了悄悄释放别人的内存）。
// 这是个竞态：那个 ACK 比 recv 读到头早到就没事，所以不是每次都出。
// 告诉对端「结束了」靠的是 BYE 报文本身，用不着 FIN。
static void drain_peer(int fd)
{
    const int64_t t0   = now_us();
    int64_t       last = t0;
    uint32_t      got  = 0;
    char          gone[32];
    const char*   how;
    for (;;) {
        // 读掉的数据扔进帧数据缓冲：这条连接的画面已经不要了，缓冲在监听期间一直在
        int     r   = recv(fd, s_cur->buf, 32 * 1024, 0);       // 接收超时 20 ms，见 setup_client
        int64_t now = now_us();
        if (r == 0) { how = "发送端关了连接"; break; }
        if (r > 0) {
            got += (uint32_t)r;
            last = now;
        } else if (errno != EAGAIN && errno != EWOULDBLOCK) {
            snprintf(gone, sizeof gone, "连接断了（errno %d）", errno);
            how = gone;
            break;
        } else if (now - last > BYE_IDLE_US) {
            how = "发送端没有关连接，不等了";
            break;
        }
        if (now - t0 > BYE_DRAIN_US) { how = "到了总时限发送端还没关连接，不等了"; break; }
    }
    ESP_LOGW(TAG, "BYE 之后：%s；又读掉 %u KB，用了 %u ms", how, (unsigned)(got >> 10), (unsigned)((now_us() - t0) / 1000));
}

// ── 任务 ─────────────────────────────────────────────────────────────────
static int listen_socket()
{
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return -1;
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    sockaddr_in addr = {};
    addr.sin_family      = AF_INET;
    addr.sin_port        = htons(PORT);
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(fd, (sockaddr*)&addr, sizeof addr) != 0 || listen(fd, 1) != 0) {
        ESP_LOGE(TAG, "监听端口 %u 失败：errno %d", PORT, errno);
        ::close(fd);
        return -1;
    }
    return fd;
}

static void setup_client(int fd)
{
    int     one = 1, idle = 5, intvl = 2, cnt = 3;
    timeval rcv = {0, 20 * 1000}, snd = {2, 0};
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &rcv, sizeof rcv);     // 见 read_full
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &snd, sizeof snd);
    // 画面不变时双方都不发数据，靠保活探测发现对端没了（约 11 秒）
    setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &one, sizeof one);
    setsockopt(fd, IPPROTO_TCP, TCP_KEEPIDLE, &idle, sizeof idle);
    setsockopt(fd, IPPROTO_TCP, TCP_KEEPINTVL, &intvl, sizeof intvl);
    setsockopt(fd, IPPROTO_TCP, TCP_KEEPCNT, &cnt, sizeof cnt);
}

// ── 自动发现（PROTOCOL.md「自动发现」）───────────────────────────────────
static int disc_socket()
{
    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) return -1;
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &one, sizeof one);     // 询问是广播来的（lwIP 开了 IP_SOF_BROADCAST_RECV 的话，没有它收不到）
    sockaddr_in addr = {};
    addr.sin_family      = AF_INET;
    addr.sin_port        = htons(PORT);
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(fd, (sockaddr*)&addr, sizeof addr) != 0) {
        ::close(fd);
        return -1;
    }
    return fd;
}

// 套接字上现成的包逐个看（一次最多 DISC_BURST 个），是询问的单播应答给它的来源；一秒最多应答 DISC_PER_SEC 个。只在等连接时调。
// 应答是发回给来源的，来源地址伪造成广播地址的话，应答就成了替别人往全网段广播。来源是广播 / 组播地址的包
// lwIP 在 IP 层就丢了（ip4_input「packet source is not valid」，RFC 1122 3.2.1.3），到不了这里；
// 只有全 0 的来源它放行（DHCP 要用），而发给 0.0.0.0 的应答会按以太网广播发出去（真机抓到过）：这一种在下面自己挡
static void disc_answer(int ufd)
{
    static int64_t sec    = -1;     // 限速：正在数的是哪一秒、这一秒已经应答了几个（只有本任务用）
    static int     in_sec = 0;
    for (int i = 0; i < DISC_BURST; i++) {
        uint8_t     buf[sizeof(DiscMsg) + 1];       // 多一个字节：比 32 字节长的包看得出来
        sockaddr_in from = {};
        socklen_t   flen = sizeof from;
        int         r    = recvfrom(ufd, buf, sizeof buf, MSG_DONTWAIT, (sockaddr*)&from, &flen);
        if (r < 0) return;
        DiscMsg m;
        if (r != (int)sizeof m) continue;
        memcpy(&m, buf, sizeof m);
        if (memcmp(m.magic, "T5WD", 4) != 0 || m.ver != PROTO_VER || m.kind != DISC_QUERY) continue;
        if (from.sin_addr.s_addr == htonl(INADDR_ANY)) continue;
        const int64_t s = now_us() / 1000000;
        if (s != sec) {
            sec    = s;
            in_sec = 0;
        }
        if (in_sec >= DISC_PER_SEC) continue;
        if (!wdc::rand_ready()) continue;           // 握不了手（见 handshake 开头），就别说自己在
        in_sec++;
        m.kind = DISC_REPLY;
        m.port = PORT;
        wdpair::self_id(m.id);
        sendto(ufd, &m, sizeof m, 0, (sockaddr*)&from, flen);
    }
}

static int s_disc_fd = -1;      // 自动发现的 UDP 套接字，监听期间开着（只有本任务用）

// 连接期间攒下的询问都过时了（电脑发出询问后只收 300 毫秒的应答）：读掉，不应答。不读不行：UDP 的接收队列只有
// CONFIG_LWIP_UDP_RECVMBOX_SIZE（6）个，被旧询问占满之后新到的会被 lwIP 丢掉。
// 什么时候读：知道这条连接结束的那一刻——我们发 BYE 之前（say_bye），别的结束方式一进收尾（serve 的 done:）。
// 这之后到的询问都算数，留在套接字里，关掉这条连接、回到监听的循环时应答。
// 不能等收尾做完再读：收尾要等后台任务、把屏幕还回去（几十毫秒，慢的时候更久），而电脑收到我们的 BYE 就关连接、马上再问
// （自动连接就是这样）；它自己发 BYE 结束的，等我们关连接也只等 300 毫秒，到时间就自己关、马上再问。
// 这些新询问在收尾期间就到了，和旧的一起读掉的话，电脑要过 3 秒问下一轮才连得上
static void disc_drain()
{
    if (s_disc_fd < 0) return;
    uint8_t buf[sizeof(DiscMsg) + 1];
    for (int i = 0; i < 64 && recvfrom(s_disc_fd, buf, sizeof buf, MSG_DONTWAIT, nullptr, nullptr) >= 0; i++) {}
}

static void task(void*)
{
    for (;;) {
        while (!s_want.load()) ulTaskNotifyTake(pdTRUE, portMAX_DELAY);

        for (Frame& f : s_frames)
            if (!f.buf) f.buf = (uint8_t*)heap_caps_malloc(MAX_FRAME_BYTES, MALLOC_CAP_SPIRAM);
        if (!s_audio_buf) s_audio_buf = (uint8_t*)heap_caps_malloc(wdaudio::MAX_CHUNK, MALLOC_CAP_SPIRAM);
        const bool bufs = s_frames[0].buf && s_frames[1].buf && s_frames[2].buf && s_audio_buf;
        int lfd = bufs ? listen_socket() : -1;
        if (lfd < 0) {
            if (!bufs) ESP_LOGE(TAG, "分不出三块 %u KB 的帧数据缓冲和声音的缓冲", (unsigned)(MAX_FRAME_BYTES >> 10));
            vTaskDelay(pdMS_TO_TICKS(1000));     // 过一会儿再试（s_want 没了就回到上面等）
        } else {
            // 自动发现的询问和 TCP 用同一个端口号。开不出来的话电脑还可以手工填地址来连，照常监听
            const int ufd = s_disc_fd = disc_socket();
            if (ufd < 0) ESP_LOGE(TAG, "自动发现用的 UDP 端口 %u 开不了：errno %d", PORT, errno);
            set_state(State::Listening);
            ESP_LOGW(TAG, "开始监听端口 %u", PORT);
            while (s_want.load()) {
                fd_set  rd;
                timeval tv = {0, 200 * 1000};
                FD_ZERO(&rd);
                FD_SET(lfd, &rd);
                if (ufd >= 0) FD_SET(ufd, &rd);
                if (select((lfd > ufd ? lfd : ufd) + 1, &rd, nullptr, nullptr, &tv) <= 0) continue;
                if (ufd >= 0 && FD_ISSET(ufd, &rd)) disc_answer(ufd);
                if (!FD_ISSET(lfd, &rd)) continue;
                sockaddr_in peer = {};
                socklen_t   plen = sizeof peer;
                int fd = accept(lfd, (sockaddr*)&peer, &plen);
                if (fd < 0) continue;
                setup_client(fd);
                s_kick.store(false);
                s_dbg_exit.store(false);
                portENTER_CRITICAL(&s_stats_mux);
                s_stats = Stats{};
                s_gaps  = Gaps{};
                s_stats.state = State::Connected;
                portEXIT_CRITICAL(&s_stats_mux);
                char ip[16];
                inet_ntoa_r(peer.sin_addr, ip, sizeof ip);
                portENTER_CRITICAL(&s_stats_mux);
                strlcpy(s_stats.peer, ip, sizeof s_stats.peer);
                portEXIT_CRITICAL(&s_stats_mux);
                ESP_LOGW(TAG, "%s 连上了", ip);

                // 连接期间 WiFi 不省电、CPU 不降频：省电模式下收包要等信标，延迟和吞吐都差一截
                wifi::ps_hold();
                if (s_cpu_lock) esp_pm_lock_acquire(s_cpu_lock);
                const bool said_bye = serve(fd);
                // 屏幕已经还回去了，这条连接对外就算结束了：状态现在就改，不等下面的收尾（最长 BYE_DRAIN_US）。
                // 不再监听的话（退出手势、暂停）监听口也现在就关，免得收尾期间新来的连接先握上手、随后又被重置。
                // 收尾期间又要监听了的话（点了「开始显示」），要等收尾结束才回到外层重新监听，这期间连上来的会被拒绝。发送端守规矩（收到 BYE 马上关）时
                // 收尾只有几毫秒；拖到几秒的只有它迟迟不关、或者 BYE 在空中丢了等重传的时候。
                const bool stop = !s_want.load();
                portENTER_CRITICAL(&s_stats_mux);
                s_stats.state   = stop ? State::Off : State::Listening;
                s_stats.peer[0] = 0;
                s_stats.name[0] = 0;
                portEXIT_CRITICAL(&s_stats_mux);
                if (stop) {
                    ::close(lfd);
                    lfd = -1;
                }
                if (said_bye) drain_peer(fd);
                if (s_cpu_lock) esp_pm_lock_release(s_cpu_lock);
                wifi::ps_release();
                ::close(fd);
                if (lfd < 0) break;     // 收尾期间又要监听了的话，回到外层重新监听
            }
            if (lfd >= 0) ::close(lfd);
            if (ufd >= 0) ::close(ufd);
            s_disc_fd = -1;
            ESP_LOGW(TAG, "停止监听");
        }
        if (!s_want.load()) {
            for (Frame& f : s_frames) {
                heap_caps_free(f.buf);
                f.buf = nullptr;
            }
            heap_caps_free(s_audio_buf);
            s_audio_buf = nullptr;
            set_state(State::Off);
        }
    }
}

// ── 对外接口 ─────────────────────────────────────────────────────────────
void open(const Ui& ui)
{
    s_ui = ui;
    if (s_prompt.seq) lv_async_call(ui_sync_cb, nullptr);       // 正等着人输密码：把这一次询问重新交给界面（息屏后亮屏回来，密码框要再弹出来）
    s_want.store(true);
    if (!s_task) {
        if (!s_cpu_lock) esp_pm_lock_create(ESP_PM_CPU_FREQ_MAX, 0, "wdisp", &s_cpu_lock);
        // 三个任务的栈余量看「连接结束」那行日志（握手 + 配对 + 收画面，无线显示任务实测用到 3.6 KB）
        // 解码任务的优先级比另外两个高一级：整屏在动时一帧的时间几乎全花在硬件解码上（4:2:2 一帧 13.8 ms，一个帧周期 15.3 ms），
        // 它自己不怎么占 CPU（等硬件解完一个条带、马上交下一个），但轮到它时不能等——另外两个任务都在忙的时候
        // （解密、混调试信息浮层）同优先级要等到下一个时间片，实测每帧的解码用时因此多 0.4 ms（14.2 对 13.8）
        if (!worker_start(s_rxw, "wdisp_rx", 4096, 5) || !worker_start(s_decw, "wdisp_dec", 6144, 6)
            || xTaskCreatePinnedToCoreWithCaps(task, "wdisp", 16384, nullptr, 5, &s_task, tskNO_AFFINITY, MALLOC_CAP_SPIRAM) != pdPASS) {
            ESP_LOGE(TAG, "建任务失败");
            s_task = nullptr;
            s_want.store(false);
            return;
        }
    }
    xTaskNotifyGive(s_task);
}

void close()
{
    s_want.store(false);
}

void kick()
{
    s_kick.store(true);
}

void pair_answer(uint32_t seq, const char* password)
{
    const size_t n = password ? strnlen(password, PASSWORD_MAX + 1) : 0;
    if (n == 0 || n > PASSWORD_MAX) return;
    portENTER_CRITICAL(&s_pw_mux);
    if (seq != 0 && seq == s_ask_seq && !s_pw_ready) {      // 只收对「正开着的这一次询问」的第一个回答
        memset(s_pw, 0, sizeof s_pw);
        memcpy(s_pw, password, n);
        s_pw_ready = true;
    }
    portEXIT_CRITICAL(&s_pw_mux);
}

// 先移除、再看正连着的是不是它：和 handshake() 末尾「先登记、再存」配着用，见那里的注释
bool forget(const uint8_t pc_id[16])
{
    const bool gone = wdpair::remove(pc_id);
    portENTER_CRITICAL(&s_stats_mux);
    const bool on = s_peer_known && memcmp(s_peer_id, pc_id, sizeof s_peer_id) == 0;
    portEXIT_CRITICAL(&s_stats_mux);
    if (gone && on) kick();     // 没移除掉的话它的配对还在，断开了它也马上连回来
    return gone;
}

bool forget_all()
{
    const bool all = wdpair::remove_all();
    portENTER_CRITICAL(&s_stats_mux);
    const bool on = s_peer_known;
    portEXIT_CRITICAL(&s_stats_mux);
    if (on) kick();
    return all;
}

Stats stats()
{
    portENTER_CRITICAL(&s_stats_mux);
    Stats st = s_stats;
    portEXIT_CRITICAL(&s_stats_mux);
    return st;
}

Gaps gaps()
{
    portENTER_CRITICAL(&s_stats_mux);
    Gaps g = s_gaps;
    portEXIT_CRITICAL(&s_stats_mux);
    return g;
}

void dbg_user_exit()
{
    if (stats().state != State::Showing) {
        ESP_LOGW(TAG, "exit：现在没有在显示发送端的画面");
        return;
    }
    s_dbg_exit.store(true);
}

void dbg_touch(int x, int y)
{
    if (x < 0) {
        s_dbg_touch.store(-1);
    } else if (stats().state != State::Showing) {
        ESP_LOGW(TAG, "touch：现在没有在显示发送端的画面");
    } else {
        s_dbg_touch.store((int32_t)(x < VIEW_W ? x : VIEW_W - 1) << 16 | (y < 0 ? 0 : y < VIEW_H ? y : VIEW_H - 1));
    }
}

// 读前台帧缓冲里横屏画面 (x, y) 那一个像素，换算成面板上显示的颜色（8 位 RGB）；raw 是帧缓冲里的原始值。
// 横屏画面 (x, y) 在帧缓冲第 (1279 − x) 行、第 y 列。
//   RGB565：raw 是那 16 位；各分量低位补 0（红蓝 0~248、绿 0~252）。
//   YUV422：这一行里相邻两列（偶数列和它右边那一列）是一组 4 个字节 U Y0 V Y1，raw 是这 4 个字节（按内存里的先后从高到低排）；
//           按 DSI 桥的算法换成 RGB：BT.601、有限范围（Y 16~235 → 0~255）。
static void fb_rgb(int x, int y, bool yuv, uint32_t& raw, int& r, int& g, int& b)
{
    const size_t row = (size_t)(VIEW_W - 1 - x);
    if (!yuv) {
        const uint16_t v = s_front[row * FB_W + y];
        raw = v;
        r = (v >> 11) << 3;  g = ((v >> 5) & 0x3F) << 2;  b = (v & 0x1F) << 3;
        return;
    }
    const uint8_t* p = (const uint8_t*)s_front + (row * FB_W + (y & ~1)) * 2;
    raw = (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3];
    const int c = 298 * (p[y & 1 ? 3 : 1] - 16), d = p[0] - 128, e = p[2] - 128;
    auto clip = [](int v) { return v < 0 ? 0 : v > 255 ? 255 : v; };
    r = clip((c + 409 * e + 128) >> 8);
    g = clip((c - 100 * d - 208 * e + 128) >> 8);
    b = clip((c + 516 * d + 128) >> 8);
}

void dbg_check_card()
{
    if (stats().state != State::Showing || !s_front) {
        ESP_LOGW(TAG, "check：现在没有在显示发送端的画面");
        return;
    }
    // 测试卡四角各有一个 60×60 的色块，取色块中心
    static const struct { const char* name; int x, y; uint8_t r, g, b; } pts[] = {
        {"左上 红", 30, 30, 255, 0, 0},
        {"右上 绿", VIEW_W - 30, 30, 0, 255, 0},
        {"左下 蓝", 30, VIEW_H - 30, 0, 0, 255},
        {"右下 白", VIEW_W - 30, VIEW_H - 30, 255, 255, 255},
    };
    const bool yuv = s_fb_yuv.load();
    int bad = 0;
    for (const auto& p : pts) {
        uint32_t raw;
        int r, g, b;
        fb_rgb(p.x, p.y, yuv, raw, r, g, b);
        // RGB565 读出来的分量低位是 0，期望值也照样截掉再比
        const int er = yuv ? p.r : p.r & 0xF8, eg = yuv ? p.g : p.g & 0xFC, eb = yuv ? p.b : p.b & 0xF8;
        bool ok = abs(r - er) <= 24 && abs(g - eg) <= 24 && abs(b - eb) <= 24;
        if (!ok) bad++;
        ESP_LOGW(TAG, "check：%s  期望 (%d,%d,%d)  读到 0x%0*x = (%d,%d,%d)  %s", p.name, p.r, p.g, p.b, yuv ? 8 : 4, (unsigned)raw, r, g, b, ok ? "对" : "不对");
    }
    ESP_LOGW(TAG, "check：%s（帧缓冲 %s）", bad ? "有色块对不上（方向或红蓝顺序有问题，或者现在显示的不是测试卡）" : "四角都对，方向和红蓝顺序正确",
             yuv ? "YUV422" : "RGB565");
}

void dbg_pixel(int x, int y)
{
    if (stats().state != State::Showing || !s_front) {
        ESP_LOGW(TAG, "px：现在没有在显示发送端的画面");
        return;
    }
    if (x < 0 || x >= VIEW_W || y < 0 || y >= VIEW_H) {
        ESP_LOGW(TAG, "px：坐标要在 %d×%d 里面", VIEW_W, VIEW_H);
        return;
    }
    const bool yuv = s_fb_yuv.load();
    uint32_t raw;
    int r, g, b;
    fb_rgb(x, y, yuv, raw, r, g, b);
    ESP_LOGW(TAG, "px：(%d, %d) = 0x%0*x = (%d,%d,%d)", x, y, yuv ? 8 : 4, (unsigned)raw, r, g, b);
}

}  // namespace wdisp
