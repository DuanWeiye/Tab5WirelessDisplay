// 无线显示的声音：电脑发来的 PCM（48 kHz、双声道、16 位小端，协议 AUDIO 报文）经一小段缓冲，从 ES8388 放出来（扬声器 / 耳机口）。
//
// 定的口径（2026-11-13）：低延迟优先，缓冲几十毫秒，偶尔断一下音可以接受，不做带时间戳的音画同步；音量在 Tab5 上调、不跟电脑的系统音量。
// 所以这里只有一条规矩：攒到「起播缓冲」那么多开始放（没攒够、但已经 TAIL_FLUSH_MS 没有新的到，也放——一段声音的结尾不留在缓冲里）；
// 放空了（链路停了一下，或者电脑那边没声音了）就重新攒；
// 攒得太多（链路停完之后一口气补来一大段）就把最旧的扔掉、回到起播缓冲——延迟不会越积越多。
// 电脑和 Tab5 的采样时钟差一点点（几十 ppm），缓冲会很慢地涨或者落，同样落在这两条上：十几分钟断一下 / 跳一下。
//
// 扬声器只接了 codec 的左声道输出（实测），所以没插耳机时左右两路混成一路放；插着耳机（IO 扩展器上有检测脚）就原样放立体声。
//
// 扬声器功放平时是关着的（BSP 开机不使能）。一条连接从 start() 到 stop() 之间 codec 和功放一直开着、没有声音时放静音：
// 打开的顺序是 codec 先开、放一小段静音稳下来 → 开功放 → 功放自己静音约 130 ms 之后才出声；关的顺序反过来。
// 这样 codec 上电 / 掉电、I2S 时钟起停的瞬变都落在功放关着的时候，不会从扬声器放出来（爆音）。
// 连接一建立就 start()：等第一段声音到了才开的话，那一段的开头（约 0.15 秒）出不了声。
// 没有连接时（调试命令放的声音）来了声音才开，停了 IDLE_CLOSE_MS 之后关——I2S 发送通道不能一直空跑（见 bsp_audio_init 里的说明）。
#pragma once

#include <cstddef>
#include <cstdint>

namespace wdaudio {

constexpr int    RATE        = 48000;
constexpr int    FRAME_BYTES = 4;                               // 一个采样帧：左、右各 16 位
constexpr size_t MAX_CHUNK   = RATE / 50 * FRAME_BYTES;         // 一个 AUDIO 报文最多 20 ms（3840 字节）
constexpr int    PREBUFFER_MS     = 50;                         // 起播缓冲的默认值：攒到这么多开始放
constexpr int    PREBUFFER_MIN_MS = 20, PREBUFFER_MAX_MS = 300; // 起播缓冲能设的范围
constexpr int    HEADROOM_MS      = 80;                         // 攒得比起播缓冲多出这么多就扔掉最旧的，回到起播缓冲
constexpr int    RING_MS          = 600;                        // 缓冲的容量（起播缓冲最大值 + HEADROOM_MS 之上还留着一截）
constexpr int    IDLE_CLOSE_MS    = 2000;                       // 不在连接里时：这么久没有声音就把 codec 关掉
constexpr int    TAIL_FLUSH_MS    = 250;                        // 没攒够起播缓冲、但这么久没有新的到，就把手里的放掉。要比链路一次停顿长：停顿当中补来的一点点不提前放（不然一次断音被切成两次）
constexpr int    BREAK_MAX_MS     = 500;                        // 放空之后这么久之内又接上的才算断音（链路一次停 30~200 ms）；更久的是声音自己停了

// 开机调一次（建锁），在任何别的调用之前。
void init();

// 一条连接开始（握手走完）：把 codec 和功放打开预备着，到 stop() 为止。立刻返回；缓冲 / 播放任务建不起来就什么也不做（这条连接没有声音）。
void start();

// 收到一段声音：bytes 是 FRAME_BYTES 的整数倍、不超过 MAX_CHUNK（调用方查过）。只拷贝进缓冲、立刻返回；哪个任务调都行。
// 头一次调时才申请缓冲、建播放任务；申请不到就把声音丢掉（画面照常）。
void push(const uint8_t* pcm, size_t bytes);

// 一条连接结束：缓冲里没放完的扔掉，功放和 codec 关掉。立刻返回。
void stop();

// 音量 0~100（0 = 静音）。立刻返回，正在放的话下一小段（10 ms）起生效。只管放出来多响，存盘是设置那边的事。
void set_volume(int pct);
int  volume();

// 起播缓冲（毫秒，夹在 PREBUFFER_MIN_MS~PREBUFFER_MAX_MS 之间）。下一次起播起生效。不存盘。
void set_prebuffer(int ms);
int  prebuffer();

struct Stats {
    bool     open;          // codec 开着（一条连接里一直开着，不等于正有声音）
    bool     headphone;     // 上一次放的时候耳机插着（插着放立体声，没插左右混成一路从扬声器放）
    uint32_t depth_ms;      // 缓冲里现在有多少
    uint32_t pushed_ms;     // 收到的声音合计
    uint32_t played_ms;     // 放出去的声音合计（不算补的静音）
    uint32_t breaks;        // 断音：正放着的时候放空了、BREAK_MAX_MS 之内又接上的次数。放空之前那一小段是纯静音的（听不出来）、隔得更久的（声音自己停了）不算
    uint32_t break_ms;      // 这些断音各自没声音的时间合计
    uint32_t break_max_ms;  // 其中最长的一次
    uint32_t long_gaps;     // 放空之前不是纯静音、隔了 BREAK_MAX_MS 以上才接上的次数（没有连接时还要赶在 codec 关掉之前）：链路停得久，或者声音自己停了又来了一段，分不出来，单独记
    uint32_t trimmed_ms;    // 攒得太多、扔掉的合计
    uint32_t dropped_ms;    // 缓冲申请不到 / 满了，没收进来的合计
    uint32_t underruns;     // 播放任务往 I2S 写晚了、I2S 自己补静音放出去的次数（一次约 2.7 ms），只数前后有声音的（听得到的）。缓冲没放空，所以不在断音里
};
Stats stats();              // 从开机或者上一次 reset_stats() 起累计
void  reset_stats();

// 调试：扬声器放一个 hz 赫兹的正弦波（走和 push 一样的路），同时用麦克风录，比较放之前 / 放的时候这个频率上的声压，结果打到日志。
// channels：1 = 只放左声道，2 = 只放右声道，3 = 两个都放（没插耳机时左右是混成一路放的，所以三种都该响，单放一边的小 6 dB）。约 2 秒，阻塞；麦克风正被别的页面占着就放弃。
// 人不在跟前也能知道扬声器真的响了（返回 true = 录到的那个频率比放之前高了 20 dB 以上）。
bool selftest(int hz, int channels);

// 调试：只录不放。麦克风录 ms 毫秒，hz 这个频率上的平均幅度（dBFS，满幅正弦 = 0）放进 dbfs。
// 电脑发来的声音正放着的时候用它看扬声器是不是真的在响那个频率。阻塞；麦克风被占着 / 开不了返回 false。
bool listen(int hz, int ms, double& dbfs);

// 调试：量爆音。麦克风录 ms 毫秒（48 kHz、2 路——第 0 路是左麦克风，第 1 路是功放输出的回采，功放关着时没有信号），
// 录的当中按 ev[] 里的时刻做事，录完把两路每 5 ms 一格的峰峰值打到日志（一行 100 ms）。从麦克风打开的第一个采样录起，
// 所以「麦克风一开扬声器就响一下」也在里面。阻塞；麦克风被占着 / 开不了 / 分不出缓冲返回 false。
struct PopEvent {
    enum Kind : uint8_t { SILENCE, TONE, STOP, PA_ON, PA_OFF };
    int  at_ms;     // 从开始录算起
    Kind kind;      // SILENCE = 推 100 ms 全 0 的声音（扬声器会为它打开）；TONE = 推 300 ms、1 kHz、−20 dBFS；STOP = 缓冲里没放完的扔掉（没有连接时扬声器随即关掉）；
                    // PA_ON / PA_OFF = 直接拨功放使能脚，不经过播放任务：PA_ON 之后要自己 PA_OFF，不然功放开到下一次放完声音为止
};
bool poprec(int ms, const PopEvent* ev, int n_ev);

}  // namespace wdaudio
