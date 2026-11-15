// 无线显示的声音，见 wd_audio.hpp。
#include "wd_audio.hpp"
#include "audio_mic.hpp"

#include <atomic>
#include <cmath>
#include <cstring>

#include <bsp/m5stack_tab5.h>
#include <esp_codec_dev.h>
#include <esp_heap_caps.h>
#include <esp_log.h>
#include <esp_timer.h>
#include <freertos/FreeRTOS.h>
#include <freertos/idf_additions.h>   // xTaskCreatePinnedToCoreWithCaps
#include <freertos/semphr.h>
#include <freertos/task.h>

namespace wdaudio {

static const char* TAG = "wdaudio";

constexpr size_t BYTES_PER_MS = RATE / 1000 * FRAME_BYTES;
constexpr size_t STEP         = 10 * BYTES_PER_MS;          // 播放任务一次往 codec 写 10 ms
constexpr size_t RING_BYTES   = RING_MS * BYTES_PER_MS;
constexpr int    CODEC_SETTLE_MS = 30;                      // codec 打开之后先放这么久的静音，再开功放
constexpr int    PA_START_MS     = 150;                     // 功放使能之后这么久才开始放声音（实测使能后约 130 ms 才出声，之前放的听不到）
constexpr int    PA_GIVE_UP      = 5;                       // 功放的使能脚连着这么多次没拨成，就不再让声音等它
constexpr int    QUIET_CAP_MS    = 1000000;                 // quiet_ms 数到这里就不再往上数（一条连接里可以一直没有声音）
static_assert(PREBUFFER_MAX_MS + HEADROOM_MS + 2 * 20 <= RING_MS, "缓冲要装得下「起播缓冲 + 余量」再加两个最长的报文");

static StaticSemaphore_t s_lock_buf;
static SemaphoreHandle_t s_lock;                // 保护下面这一组
static uint8_t*          s_ring;                // RING_BYTES + STEP，放 PSRAM；头一次 push 时申请，之后一直留着。后面那 STEP 字节是播放任务一次取出来的那一小段
static size_t            s_rd, s_len;           // 读位置、里面有多少字节（都是 FRAME_BYTES 的整数倍）
static int64_t           s_last_push_us;        // 最近一次收进来的时刻
static TaskHandle_t      s_task;
static Stats             s_st;                  // 只用 open / breaks / break_ms / break_max_ms / long_gaps / underruns；几个「合计」按字节记在下面，stats() 里才换成毫秒
static uint64_t          s_pushed, s_played, s_trimmed, s_dropped;   // 字节。报文不一定是整毫秒，每次都换算会一点点少算

static std::atomic<int>  s_volume{60};
static std::atomic<int>  s_prebuffer_ms{PREBUFFER_MS};
static std::atomic<bool> s_vol_dirty{false};
static std::atomic<bool> s_stop{false};
static std::atomic<bool> s_session{false};      // start() 和 stop() 之间：codec 和功放一直开着
static std::atomic<bool> s_pa_stuck{false};     // 上一轮收尾时功放没关掉
static std::atomic<bool> s_headphone{false};    // 上一次看的时候耳机插着（只在 codec 开着时看）

void init()
{
    if (!s_lock) s_lock = xSemaphoreCreateMutexStatic(&s_lock_buf);
}

static void apply_volume(esp_codec_dev_handle_t dev)
{
    const int v = s_volume.load();
    esp_codec_dev_set_out_mute(dev, v <= 0);
    if (v > 0) esp_codec_dev_set_out_vol(dev, v);
}

// 从缓冲里取最多 STEP 字节到 out，返回取了多少。playing：进来时是不是正放着；出去时是接下来的状态。
// started：这一次是不是从「攒着」变成了「放」（不够 STEP 的尾巴会在同一次里开始又放空，光看 playing 看不出来）
static size_t take_step(uint8_t* out, bool& playing, bool& started)
{
    const size_t pre    = (size_t)s_prebuffer_ms.load() * BYTES_PER_MS;
    const size_t high   = pre + HEADROOM_MS * BYTES_PER_MS;
    size_t       got    = 0;
    started = false;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    // 攒够了开始放。没攒够、但已经 TAIL_FLUSH_MS 没有新的到：后面不会马上有了（一段声音的结尾，或者一声很短的提示音），
    // 把手里的放掉——不然这一截会留到下一段声音开头才出来
    if (!playing && (s_len >= pre || (s_len && esp_timer_get_time() - s_last_push_us > (int64_t)TAIL_FLUSH_MS * 1000))) playing = started = true;
    if (playing) {
        if (s_len > high) {                     // 攒得太多：扔掉最旧的
            const size_t cut = s_len - pre;
            s_rd = (s_rd + cut) % RING_BYTES;
            s_len -= cut;
            s_trimmed += cut;
        }
        got = s_len < STEP ? s_len : STEP;
        const size_t first = got < RING_BYTES - s_rd ? got : RING_BYTES - s_rd;
        memcpy(out, s_ring + s_rd, first);
        memcpy(out + first, s_ring, got - first);
        s_rd = (s_rd + got) % RING_BYTES;
        s_len -= got;
        s_played += got;
        if (got < STEP) playing = false;        // 放空了：重新攒
    }
    xSemaphoreGive(s_lock);
    return got;
}

// 左右两路混成一路、两个声道写一样的。扬声器只接了 codec 的左声道输出（实测：只放右声道时扬声器不响），
// 不混的话电脑那边偏右的声音就听不到了。插着耳机时不混，耳机里照样是立体声。
static void downmix(uint8_t* pcm, size_t bytes)
{
    for (size_t i = 0; i + FRAME_BYTES <= bytes; i += FRAME_BYTES) {
        int16_t l, r;
        memcpy(&l, pcm + i, 2);
        memcpy(&r, pcm + i + 2, 2);
        const int16_t m = (int16_t)(((int32_t)l + r) / 2);
        memcpy(pcm + i, &m, 2);
        memcpy(pcm + i + 2, &m, 2);
    }
}

static void flush()
{
    xSemaphoreTake(s_lock, portMAX_DELAY);
    s_rd = s_len = 0;
    xSemaphoreGive(s_lock);
}

static size_t buffered()
{
    xSemaphoreTake(s_lock, portMAX_DELAY);
    const size_t n = s_len;
    xSemaphoreGive(s_lock);
    return n;
}

static bool all_zero(const uint8_t* p, size_t n)
{
    for (size_t i = 0; i < n; i++)
        if (p[i]) return false;
    return true;
}

static void set_open(bool open)
{
    xSemaphoreTake(s_lock, portMAX_DELAY);
    s_st.open = open;
    xSemaphoreGive(s_lock);
}

// 记一次断音：放空之后隔了 ms 毫秒又接上了
static void note_break(uint32_t ms)
{
    xSemaphoreTake(s_lock, portMAX_DELAY);
    s_st.breaks++;
    s_st.break_ms += ms;
    if (ms > s_st.break_max_ms) s_st.break_max_ms = ms;
    xSemaphoreGive(s_lock);
}

static void note_long_gap()
{
    xSemaphoreTake(s_lock, portMAX_DELAY);
    s_st.long_gaps++;
    xSemaphoreGive(s_lock);
}

static void note_underruns(uint32_t n)
{
    xSemaphoreTake(s_lock, portMAX_DELAY);
    s_st.underruns += n;
    xSemaphoreGive(s_lock);
}

static void play_task(void*)
{
    uint8_t* const step = s_ring + RING_BYTES;
    for (;;) {
        ulTaskNotifyTake(pdTRUE, portMAX_DELAY);            // codec 关着：等有声音来
        if (s_stop.exchange(false)) flush();                // 不直接回去等：stop() 之后紧跟着的 start() 的通知可能已经被上面一起收走了
        if (!s_session.load() && !buffered()) continue;     // 上一轮放着的时候攒下的通知（那些声音已经放掉了）：不为它白开一次 codec
        // 上一轮收尾时功放没关掉（I2C 没通）：开 codec 之前再关一次，不然 codec 上电的那一下会放出来
        if (s_pa_stuck.load()) s_pa_stuck.store(bsp_speaker_pa_enable(false) != ESP_OK);

        esp_codec_dev_handle_t dev = bsp_audio_codec_speaker_init();
        esp_codec_dev_sample_info_t fs = {};
        fs.bits_per_sample = 16;
        fs.channel         = 2;
        fs.sample_rate     = RATE;
        if (!dev || esp_codec_dev_open(dev, &fs) != ESP_OK) {
            ESP_LOGE(TAG, "扬声器开不了，这一段声音不放");
            if (dev) esp_codec_dev_close(dev);
            flush();
            vTaskDelay(pdMS_TO_TICKS(1000));                // 一直有声音来的话别一秒几十次地重试
            continue;
        }
        apply_volume(dev);
        s_vol_dirty.store(false);
        set_open(true);

        bool     playing  = false;
        bool     broke    = false;                          // 放着放着放空了（放空之前那一小段不是纯静音），还没接上
        bool     silent   = true;                           // 最近取出来的那一小段是纯静音
        bool     mono     = true;                           // 没插耳机：左右混成一路再放（见 downmix）
        bool     failed   = false;
        bool     pa_on    = false;                          // 功放的使能脚拉高了
        int      pa_fails = 0;                              // 拨功放的使能脚连着没拨成几次了
        uint32_t pa_next  = 0;                              // 没拨成：到第几轮再拨
        int      settle_ms = 0, pa_ms = 0;                  // codec 打开之后放了多久的静音、功放使能之后又放了多久
        int      quiet_ms = 0;
        bool     heard    = false;                          // 上一轮写出去的那一小段里有听得到的声音
        uint32_t ur       = 0;                              // 上一轮写完时 I2S 的欠载计数
        // 一条连接里（start() 之后）一直开着；没有连接时（调试命令放的声音）停了 IDLE_CLOSE_MS 就关
        for (uint32_t n = 0; s_session.load() || quiet_ms < IDLE_CLOSE_MS; n++) {
            if (n % 50 == 0) {                              // 半秒看一次耳机插没插（读 IO 扩展器，一次 I2C）
                mono = !bsp_headphone_detect();
                s_headphone.store(!mono);
            }
            if (s_stop.exchange(false)) {
                flush();
                if (!s_session.load()) break;               // stop()，或者没有连接时的 drop()：关掉
                playing = broke = false;                    // 连接里的 drop()（调试命令放完了）：只扔掉缓冲里的，接着开着
            }
            if (s_vol_dirty.exchange(false)) apply_volume(dev);
            // 功放：codec 放过 CODEC_SETTLE_MS 的静音之后拨。没拨成（I2C 正被别的器件占着）隔一阵再拨，头几次隔 100 ms，之后隔 0.5 秒
            if (!pa_on && settle_ms >= CODEC_SETTLE_MS && n >= pa_next) {
                pa_on = bsp_speaker_pa_enable(true) == ESP_OK;
                if (!pa_on) {
                    if (++pa_fails == PA_GIVE_UP) ESP_LOGE(TAG, "功放开不了（I2C 没通）：扬声器没有声音，耳机照常；接着隔 0.5 秒试一次");
                    pa_next = n + (pa_fails < PA_GIVE_UP ? 10 : 50);
                }
            }
            const bool   was = playing;
            bool         started = false;
            size_t       got     = 0;
            if (pa_ms < PA_START_MS) {
                // 还出不了声：codec 先放一小段静音（时钟、DAC、输出级这时候才稳下来），再开功放，功放自己还要静音一阵。
                // 这段时间只写静音，到了的声音留在缓冲里（攒多了的开头会被 take_step 扔掉，所以连接一建立就 start()，赶在声音来之前）
                // 功放试了 PA_GIVE_UP 次都没拨成就不等它了，照常往下放：插着耳机的话不靠功放也有声音
                if (settle_ms < CODEC_SETTLE_MS) settle_ms += 10;
                else if (pa_on || pa_fails >= PA_GIVE_UP) pa_ms += 10;
            } else {
                got = take_step(step, playing, started);
            }
            if (started && broke) {                         // 接上了
                const uint32_t gap = (uint32_t)quiet_ms + 10;   // 放空的那一小段没放满，也算进去
                if (gap <= BREAK_MAX_MS) note_break(gap);
                else note_long_gap();                       // 隔得久的多半是声音自己停了又来了一段，不算断音，单独记个数
                broke = false;
            }
            if (got) silent = all_zero(step, got);
            if ((was || started) && !playing) broke = !silent;   // 放空了。放空之前是纯静音的听不出来（电脑那边静音满 0.5 秒就不发了，每一段声音都是这么收尾的），不算
            if (mono) downmix(step, got);
            memset(step + got, 0, STEP - got);              // 不够的补静音：codec 一直有数据写，这个循环也靠写的阻塞来走时间
            if (esp_codec_dev_write(dev, step, STEP) != ESP_CODEC_DEV_OK) {
                ESP_LOGE(TAG, "往扬声器写不进去，这一段声音不放");
                failed = true;                              // 写一直立刻失败的话这个循环就不走时间了：不能留在里面空转
                break;
            }
            // 写的间隔太长、I2S 自己补了静音放出去（听得到「咔」，但缓冲没放空，不在断音里）。
            // 只数听得到的：这一小段或者上一小段里有声音。一条连接里没有声音时放的本来就是静音，补进去的静音听不出来，不算
            const uint32_t now      = bsp_audio_tx_underruns();
            const bool     hear_now = got && !silent;
            if (now != ur && (heard || hear_now)) note_underruns(now - ur);
            ur    = now;
            heard = hear_now;
            quiet_ms = got ? 0 : quiet_ms < QUIET_CAP_MS ? quiet_ms + 10 : quiet_ms;
        }
        // 收尾：先放两小段静音，把还排在 I2S 里的声音放完（正放着声音时断开，别在波形中间关功放）→ 关功放（立刻没声）
        // → 再放一小段静音 → 关 codec：codec 掉电、时钟停下来的那一下到不了扬声器
        memset(step, 0, STEP);
        for (int i = 0; !failed && i < 2; i++) failed = esp_codec_dev_write(dev, step, STEP) != ESP_CODEC_DEV_OK;
        for (int i = 0; pa_on && i < 5; i++) {
            if (i) vTaskDelay(pdMS_TO_TICKS(10));           // 总线正被别的器件占着的话，等一下再试
            pa_on = bsp_speaker_pa_enable(false) != ESP_OK;
        }
        if (pa_on) {
            ESP_LOGE(TAG, "功放关不掉（I2C 没通），关 codec 时可能响一下；下次开之前再关");
            s_pa_stuck.store(true);
        }
        if (!failed) esp_codec_dev_write(dev, step, STEP);
        esp_codec_dev_close(dev);
        set_open(false);
        if (failed) {
            flush();
            vTaskDelay(pdMS_TO_TICKS(1000));
        }
    }
}

// 缓冲和播放任务：头一次用到时才建，之后一直留着。拿着 s_lock 调；返回 false = 建不起来
static bool ensure_task()
{
    if (!s_ring) s_ring = (uint8_t*)heap_caps_malloc(RING_BYTES + STEP, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (s_ring && !s_task    // 栈走 PSRAM，不占内部 RAM
        && xTaskCreatePinnedToCoreWithCaps(play_task, "wdaudio", 4096, nullptr, 6, &s_task, tskNO_AFFINITY, MALLOC_CAP_SPIRAM) != pdPASS)
        s_task = nullptr;
    return s_ring && s_task;
}

void start()
{
    if (!s_lock) return;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    const bool   ready = ensure_task();
    TaskHandle_t t     = s_task;
    xSemaphoreGive(s_lock);
    if (!ready) return;                                     // 建不起来：这条连接没有声音，画面照常
    s_session.store(true);
    xTaskNotifyGive(t);
}

void push(const uint8_t* pcm, size_t bytes)
{
    if (!s_lock || !bytes) return;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    const bool   ready = ensure_task();
    const size_t room  = ready ? RING_BYTES - s_len : 0;
    const size_t take  = bytes <= room ? bytes : 0;         // 放不下就整段不收（播放任务卡住了才会这样）：只收半段会把左右声道错开
    if (take) {
        const size_t wr    = (s_rd + s_len) % RING_BYTES;
        const size_t first = take < RING_BYTES - wr ? take : RING_BYTES - wr;
        memcpy(s_ring + wr, pcm, first);
        memcpy(s_ring, pcm + first, take - first);
        s_len += take;
        s_pushed += take;
        s_last_push_us = esp_timer_get_time();
    } else {
        s_dropped += bytes;
    }
    TaskHandle_t t = s_task;
    xSemaphoreGive(s_lock);
    if (take && t) xTaskNotifyGive(t);
}

// 缓冲里没放完的扔掉。end_session = 连同连接的会话一起结束（codec 和功放关掉）；不是的话，有连接时接着开着，没有连接时关掉
static void drop(bool end_session)
{
    if (!s_lock) return;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    TaskHandle_t t = s_task;
    xSemaphoreGive(s_lock);
    if (!t) return;
    if (end_session) s_session.store(false);
    s_stop.store(true);
    xTaskNotifyGive(t);
}

void stop()
{
    drop(true);
}

void set_volume(int pct)
{
    s_volume.store(pct < 0 ? 0 : pct > 100 ? 100 : pct);
    s_vol_dirty.store(true);
}

int volume() { return s_volume.load(); }

void set_prebuffer(int ms)
{
    s_prebuffer_ms.store(ms < PREBUFFER_MIN_MS ? PREBUFFER_MIN_MS : ms > PREBUFFER_MAX_MS ? PREBUFFER_MAX_MS : ms);
}

int prebuffer() { return s_prebuffer_ms.load(); }

Stats stats()
{
    Stats st = {};
    if (!s_lock) return st;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    st = s_st;
    st.depth_ms   = s_len / BYTES_PER_MS;
    st.pushed_ms  = (uint32_t)(s_pushed / BYTES_PER_MS);
    st.played_ms  = (uint32_t)(s_played / BYTES_PER_MS);
    st.trimmed_ms = (uint32_t)(s_trimmed / BYTES_PER_MS);
    st.dropped_ms = (uint32_t)(s_dropped / BYTES_PER_MS);
    xSemaphoreGive(s_lock);
    st.headphone = s_headphone.load();
    return st;
}

void reset_stats()
{
    if (!s_lock) return;
    xSemaphoreTake(s_lock, portMAX_DELAY);
    const bool open = s_st.open;
    s_st = {};
    s_st.open = open;
    s_pushed = s_played = s_trimmed = s_dropped = 0;
    xSemaphoreGive(s_lock);
}

// ── 自检 ─────────────────────────────────────────────────────────────────
// 一段采样里 hz 这个频率的幅度（满幅正弦 = 1.0），Goertzel
static double tone_level(const int16_t* x, int n, int stride, int hz)
{
    const double w = 2.0 * M_PI * hz / RATE, c = 2.0 * cos(w);
    double s1 = 0, s2 = 0;
    for (int i = 0; i < n; i++) {
        const double s0 = x[i * stride] / 32768.0 + c * s1 - s2;
        s2 = s1;
        s1 = s0;
    }
    const double p = s1 * s1 + s2 * s2 - c * s1 * s2;
    return 2.0 * sqrt(p > 0 ? p : 0) / n;
}

// 占住麦克风并打开（48 kHz、16 位、2 声道）。返回空 = 被占着 / 开不了（已经放手）
static esp_codec_dev_handle_t mic_open()
{
    if (!audiomic::acquire(500)) {
        ESP_LOGW(TAG, "麦克风正被占着，不做");
        return nullptr;
    }
    esp_codec_dev_handle_t mic = bsp_audio_codec_microphone_init();
    esp_codec_dev_sample_info_t fs = {};
    fs.bits_per_sample = 16;
    fs.channel         = 2;
    fs.sample_rate     = RATE;
    if (!mic || esp_codec_dev_open(mic, &fs) != ESP_OK) {
        ESP_LOGE(TAG, "麦克风开不了");
        if (mic) esp_codec_dev_close(mic);
        audiomic::release();
        return nullptr;
    }
    esp_codec_dev_set_in_gain(mic, 30.0f);
    return mic;
}

static void mic_close(esp_codec_dev_handle_t mic)
{
    esp_codec_dev_close(mic);
    audiomic::release();
}

constexpr int    MIC_N     = RATE / 100;                    // 麦克风一次读 10 ms
constexpr size_t MIC_BYTES = MIC_N * 2 * sizeof(int16_t);   // 这 10 ms 的两个声道

// n 块「10 ms 双声道」的缓冲（PSRAM），调试命令用的时候才申请，用完 heap_caps_free
static int16_t* dbg_bufs(int n)
{
    int16_t* p = (int16_t*)heap_caps_malloc(n * MIC_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (!p) ESP_LOGE(TAG, "分不出缓冲");
    return p;
}

bool selftest(int hz, int channels)
{
    if (hz < 100 || hz > 8000 || !(channels & 3)) return false;
    int16_t* const mic_buf = dbg_bufs(2);
    if (!mic_buf) return false;
    int16_t* const tone = mic_buf + MIC_N * 2;
    esp_codec_dev_handle_t mic = mic_open();
    if (!mic) {
        heap_caps_free(mic_buf);
        return false;
    }

    // 每一轮读 10 ms 麦克风（读是阻塞的，循环靠它走时间）。0.5 秒不出声量底噪，然后放 1.5 秒：头 0.5 秒不算（缓冲、codec 起来），后 1 秒量
    double   before = 0, during = 0;
    int      nb = 0, nd = 0;
    uint32_t phase = 0;
    for (int i = 0; i < 200; i++) {
        if (i >= 50) {
            for (int k = 0; k < MIC_N; k++, phase++) {
                const int16_t v = (int16_t)(16000 * sin(2.0 * M_PI * hz * (phase % RATE) / RATE));
                tone[k * 2]     = (channels & 1) ? v : 0;
                tone[k * 2 + 1] = (channels & 2) ? v : 0;
            }
            push((const uint8_t*)tone, MIC_BYTES);
        }
        if (esp_codec_dev_read(mic, mic_buf, MIC_BYTES) != ESP_OK) continue;
        const double lv = tone_level(mic_buf, MIC_N, 2, hz);
        if (i >= 10 && i < 50) { before += lv; nb++; }
        if (i >= 100)          { during += lv; nd++; }
    }
    mic_close(mic);
    heap_caps_free(mic_buf);
    drop(false);            // 没放完的不放了。有连接时不动它的会话（codec 接着开着）

    if (!nb || !nd) {
        ESP_LOGE(TAG, "自检：麦克风没读到数据");
        return false;
    }
    const double b = 20.0 * log10(before / nb + 1e-9), d = 20.0 * log10(during / nd + 1e-9);
    const bool   ok = d - b >= 20.0;
    const Stats  st = stats();
    ESP_LOGW(TAG, "自检：%d Hz（%s声道），麦克风在这个频率上：放之前 %.1f dBFS，放的时候 %.1f dBFS，高了 %.1f dB → %s；音量 %d；累计 收到 %u ms 放出 %u ms 断音 %u 次",
             hz, channels == 3 ? "左右" : channels == 1 ? "左" : "右", b, d, d - b, ok ? "扬声器在响" : "没录到（扬声器没响，或者音量太小）",
             s_volume.load(), (unsigned)st.pushed_ms, (unsigned)st.played_ms, (unsigned)st.breaks);
    return ok;
}

bool listen(int hz, int ms, double& dbfs)
{
    if (hz < 100 || hz > 8000 || ms < 100) return false;
    int16_t* const mic_buf = dbg_bufs(1);
    if (!mic_buf) return false;
    esp_codec_dev_handle_t mic = mic_open();
    if (!mic) {
        heap_caps_free(mic_buf);
        return false;
    }
    double sum = 0;
    int    n   = 0;
    for (int i = 0; i < ms / 10 + 10; i++) {
        if (esp_codec_dev_read(mic, mic_buf, MIC_BYTES) != ESP_OK) continue;
        if (i < 10) continue;                       // 头 0.1 秒不算（麦克风刚开）
        sum += tone_level(mic_buf, MIC_N, 2, hz);
        n++;
    }
    mic_close(mic);
    heap_caps_free(mic_buf);
    if (!n) return false;
    dbfs = 20.0 * log10(sum / n + 1e-9);
    return true;
}

bool poprec(int ms, const PopEvent* ev, int n_ev)
{
    if (ms < 100 || ms > 8000) return false;
    const int blocks = ms / 10;
    int16_t* const rec = dbg_bufs(blocks + 1);              // 最后一块拿来放要推的声音
    if (!rec) return false;
    int16_t* const out = rec + (size_t)blocks * MIC_N * 2;
    esp_codec_dev_handle_t mic = mic_open();
    if (!mic) {
        heap_caps_free(rec);
        return false;
    }
    const uint32_t ur0 = bsp_audio_tx_underruns();
    int      tone_left = 0;                                 // 还要推多少个 10 ms 的正弦波
    uint32_t phase     = 0;
    int      got       = 0;
    for (int i = 0; i < blocks; i++) {
        for (int k = 0; k < n_ev; k++) {
            if (ev[k].at_ms / 10 != i) continue;
            switch (ev[k].kind) {
                case PopEvent::SILENCE:
                    memset(out, 0, MIC_BYTES);
                    for (int j = 0; j < 10; j++) push((const uint8_t*)out, MIC_BYTES);
                    break;
                case PopEvent::TONE:   tone_left = 30; break;
                case PopEvent::STOP:   drop(false); break;
                case PopEvent::PA_ON:  bsp_speaker_pa_enable(true); break;
                case PopEvent::PA_OFF: bsp_speaker_pa_enable(false); break;
            }
        }
        if (tone_left) {
            tone_left--;
            for (int k = 0; k < MIC_N; k++, phase++) out[k * 2] = out[k * 2 + 1] = (int16_t)(3277 * sin(2.0 * M_PI * 1000 * (phase % RATE) / RATE));
            push((const uint8_t*)out, MIC_BYTES);
        }
        if (esp_codec_dev_read(mic, rec + (size_t)i * MIC_N * 2, MIC_BYTES) != ESP_OK) break;
        got++;
    }
    mic_close(mic);
    ESP_LOGW(TAG, "pop：I2S 发送欠载计数 开录时 %u，录完 %u", (unsigned)ur0, (unsigned)bsp_audio_tx_underruns());

    // 每 5 ms 一格的峰峰值（不受直流影响）；一行 100 ms
    constexpr int WIN = RATE / 200;
    for (int ch = 0; ch < 2; ch++) {
        for (int line = 0; line * 10 < got; line++) {
            char buf[160];
            int  n = snprintf(buf, sizeof(buf), "%s %4d:", ch ? "aec" : "mic", line * 100);
            for (int w = 0; w < 20 && (line * 20 + w) * WIN < got * MIC_N; w++) {
                const int16_t* x = rec + (size_t)(line * 20 + w) * WIN * 2 + ch;
                int lo = 32767, hi = -32768;
                for (int k = 0; k < WIN; k++) {
                    const int v = x[k * 2];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                n += snprintf(buf + n, sizeof(buf) - n, " %5d", hi - lo);
            }
            ESP_LOGW(TAG, "pop %s", buf);
        }
    }
    heap_caps_free(rec);
    return got == blocks;
}

}  // namespace wdaudio
