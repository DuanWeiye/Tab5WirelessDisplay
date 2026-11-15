// 无线显示接收端：把 Tab5 当成别的电脑的无线副屏。协议见仓库根目录 windisplay/PROTOCOL.md。
//
// 一个常驻任务监听 TCP 端口，同一时刻只服务一个发送端。连上先握手（协议「认证和加密」）：配对过的电脑互相认证，
// 没配对过的先配对——在屏幕上弹密码框让人输电脑上设的配对密码；握手之后的报文逐个加密。
// 发送端的画面按条带（JPEG）送来，用硬件解码器直接解进面板的后台帧缓冲，收到 PRESENT 时在帧边界翻页。显示期间 LVGL 停着，
// 触摸由本模块自己读、发回发送端；连接结束后把屏幕还给 LVGL。等连接的时候应答局域网里的自动发现询问（UDP，同一个端口号）。
#pragma once

#include <cstddef>
#include <cstdint>

namespace wdisp {

constexpr uint16_t PORT = 7250;
constexpr size_t   PASSWORD_MAX = 64;       // 配对密码最多这么多个字符（协议「配对」）

enum class State : uint8_t {
    Off,        // 没在监听
    Listening,  // 等发送端连上来
    Connected,  // 发送端连上了，在握手（可能正等人输配对密码），或者第一帧还没到齐（屏幕上还是 Tab5 自己的界面、或者息着屏，照常能用）
    Showing,    // 正在显示发送端的画面（LVGL 停着）
};

struct Stats {
    State    state;
    char     peer[16];      // 发送端地址（没有连接时是空串）
    char     name[33];      // 发送端报的名字（清洗过，只当显示用）；握手走完才有
    uint32_t frames;        // 这次连接已经上屏的帧数
    uint32_t strips;        // 这次连接收到的条带数
    uint32_t catchup;       // 这次连接里为追平轮流用的几块帧缓冲多解的条带数
    uint32_t held;          // 这次连接里解完之后前一帧的翻页还没生效、放着等过的帧数（解得比屏幕刷新快才会有）
    uint64_t bytes;         // 这次连接收到的画面数据字节数
    uint32_t fps_x10;       // 最近一秒的帧率 ×10
    uint32_t kbps;          // 最近一秒的画面码率
    uint32_t recv_us;       // 最近一帧：第一个条带到 PRESENT 的接收用时
    uint32_t decode_us;     // 最近一帧：解码用时合计
    uint32_t present_us;    // 最近一帧：收到 PRESENT 到换上屏幕
};

// 这次连接里相邻两帧上屏的间隔：一帧换上屏幕的时刻减上一帧的，Tab5 自己量，从第二帧起算。
// 画面有没有停顿以它为准（发送端只知道确认什么时候到，确认迟到不等于画面停了）——前提是发送端的画面一直在变：
// 画面不变时发送端不发帧，那一段也记成一个大间隔（日常投着不动的桌面时就是这样），所以只在测试场景 / 一直在动的画面下看它。
struct Gaps {
    static constexpr int      NTH = 7;
    static constexpr uint16_t TH_MS[NTH] = {50, 80, 100, 150, 200, 300, 500};
    static constexpr int      BIG_TH = 1;       // 不短于 TH_MS[BIG_TH] 的算「大间隔」，逐个记下来
    static constexpr int      RECENT = 64;
    struct Big { uint32_t seq, ms; };           // 哪一帧上屏时离上一帧隔了多久
    uint32_t n;                 // 量了多少个间隔
    uint32_t ge[NTH];           // 不短于 TH_MS[i] 的次数
    uint32_t max_us;            // 最长的一个
    uint32_t big_total;         // 大间隔一共出现过几次；比 RECENT 多的话 recent 里只有最后 RECENT 个
    Big      recent[RECENT];    // 大间隔，按先后（环形：第 i 个在 recent[i % RECENT]）
};

// 配对时要人输密码的一次询问。
struct PairPrompt {
    uint32_t seq;           // 这一次询问的编号：回答时带上，过了期的回答不作数
    char     pc_name[33];   // 电脑报的名字（清洗过，只当显示用）
    bool     known;         // 这台电脑以前配对过（标题写「重新配对」：平时不该再问，让人多看一眼）
    bool     wrong;         // 上一次输的密码不对：密码框标红、清空，等人重输
};

// 界面那一头（现在是应用层 main/app.cpp）。前两个函数在 LVGL 线程里被调；后两个在 wdisp 自己的任务上、拿着 LVGL 锁被调
// （那时 LVGL 正要停 / 刚恢复，等不了它的线程），里面可以动 LVGL 的东西，别做慢事。
struct Ui {
    // 弹出密码框（已经开着的话：换成这一次的内容）。同一次询问（seq 相同）可能调不止一次，后面几次不用理。
    // 人输完调 pair_answer()；点取消 = 暂停（界面调 close()，这条连接以 BYE 结束、不再监听）。
    void (*pair_prompt)(const PairPrompt&);
    // 密码框可以关了：配上了、连接断了、电脑不守协议、从弹出 / 标红起 2 分钟没输完。没开着也会调。
    void (*pair_close)();
    // 电脑的画面这就要上屏了（第一帧到齐、屏幕已经接管下来）：息屏着的话把屏幕点亮。
    void (*screen_take)();
    // 屏幕还给界面了（连接结束）。和 screen_take 成对。
    // user_exit = 是用户在显示期间做了退出手势（左边缘右滑）才结束的：wdisp 已经自己停止监听了——不然会自动重连的发送端
    // 马上又把屏幕占回去，用户等于退不出来。界面在这里当场记下（现在的做法：记成「暂停」，回主页面、露出「开始显示」）；
    // 要再接受连接得重新调 open()。和「屏幕还回来」放在同一次调用里说，是为了界面不用去猜两件事谁先到。
    void (*screen_back)(bool user_exit);
};

// 开始监听（可重复调：正等着人输配对密码的话，会把那一次询问重新交给 pair_prompt）。在 LVGL 线程里、或者拿着 LVGL 锁调。
void  open(const Ui& ui);
void  close();    // 断开当前连接并停止监听。只置标志，立刻返回
void  kick();     // 断开当前连接、回到等待
Stats stats();
Gaps  gaps();     // 这次连接（没有连接时是上一次）的上屏间隔统计

// 人在密码框里输完了。seq 是那一次 PairPrompt 的编号；密码要 1~PASSWORD_MAX 个字符。哪个线程都能调。
void  pair_answer(uint32_t seq, const char* password);

// 移除一台电脑的配对 / 全部配对。正连着的是它（们）的话那条连接立刻结束（发 BYE）。哪个线程都能调。
bool  forget(const uint8_t pc_id[16]);      // 没有这一台、或者从闪存里删不掉（那它还留着），返回 false
bool  forget_all();                         // 有删不掉的返回 false

// 调试：显示期间调，效果和用户做了退出手势一样（手势本身只能用手指试，其后的处理靠它在自动测试里走到）。
void  dbg_user_exit();

// 调试：显示期间调，当成有一根手指按在横屏画面的 (x, y) 上，x < 0 = 抬起。再调一次 = 挪到新的位置，
// 一次最多挪 200 像素（MATCH_DIST）：再远就认成换了一根手指（旧的那根过一会儿抬起，发送端看到的是点了一下），要挪得远就分几步。
// 和真的触点走同一条路（认人、边缘手势、发给发送端）；没抬起的到下一条连接开始时清掉。触摸只能用手指试的那些事靠它在自动测试里走到。
void  dbg_touch(int x, int y);

// 调试：正在显示 windisplay/tools/wd_send_test.py 的测试卡时，读前台帧缓冲里四角色块的像素，
// 和期望值对比，核对画面方向和红蓝顺序。结果打到日志。
void  dbg_check_card();
// 调试：读前台帧缓冲里横屏画面 (x, y) 那一个像素，打到日志（正在显示发送端的画面时才有）。
// 两个命令都按帧缓冲现在的格式读（RGB565 / YUV422），打出来的 (r,g,b) 是面板上显示的颜色。
void  dbg_pixel(int x, int y);

}  // namespace wdisp
