// 诊断工具，平时不编进固件：查 tlsf_free「block already marked as free」（同一块内存释放两次）时用来找「第一次是谁释放的」。
// 2026-11-12 用它查出 lwIP 半关连接重复释放 TCP 控制块。
//
// 做法：靠 IDF 的堆钩子（CONFIG_HEAP_USE_HOOKS）盯住「大小正好等于某个结构」的内存块（这里是 struct tcp_pcb，换目标就改
// 分配钩子里的 sizeof 和 record() 里读的字段）：记下每一次分配、以及第一次释放它的是哪个任务、调用栈上有哪些返回地址。
// 记录放在 RTC_NOINIT 里，断言失败重启之后还在，用调试台命令 hw 打出来；地址用 riscv32-esp-elf-addr2line -pfiaC -e build/*.elf 解。
//
// 怎么用（查完全部撤掉）：
//   1. 把本目录两个文件拷到 main/utils/，main/CMakeLists.txt 的 SRCS 里加 "utils/heapwatch.cpp"；
//   2. sdkconfig 里 CONFIG_HEAP_USE_HOOKS=y（会整个重编）；
//   3. main/debug_console.cpp：#include "utils/heapwatch.hpp"，加一个命令 {"hw", "...", [](const char*) { heapwatch::dump(); }}；
//   4. 复现 → 重启后发 hw → 找和断言时那个指针相同的最后一条 F。
#include "heapwatch.hpp"
#include <sdkconfig.h>
#if CONFIG_HEAP_USE_HOOKS
#include <cstddef>
#include <cstring>
#include <esp_attr.h>
#include <esp_heap_caps.h>
#include <esp_log.h>
#include <esp_timer.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/semphr.h>
#include <lwip/tcp.h>
#include <lwip/priv/tcp_priv.h>
#include <lwip/tcpip.h>

extern "C" {
extern char _instruction_reserved_start, _instruction_reserved_end, _iram_text_start, _iram_text_end;
}

namespace {
const char* TAG = "heapwatch";
constexpr uint32_t MAGIC = 0x48575432;
constexpr int NEV = 64, NPC = 14, NWATCH = 24;

struct Ev {
    uint32_t ptr, t_ms;
    char     type;        // 'A' 分配 / 'F' 释放
    uint8_t  state;       // 释放时块里 tcp_pcb.state 那个位置的值（块已经还给堆了，只当参考）
    uint16_t lport, rport, flags;
    char     task[8];
    uint32_t pc[NPC];
};
struct Store {
    uint32_t magic;
    uint32_t n;           // 累计写了多少条（下标 = n % NEV）
    Ev       ev[NEV];
};
RTC_NOINIT_ATTR Store s_store;     // P4 的链接脚本没有 .noinit，放 LP RAM 的 rtc_noinit
Store        s_prev;                  // 上一次开机留下的
bool         s_inited = false;
uint32_t     s_watch[NWATCH];
uint32_t     s_watch_seq = 0;
portMUX_TYPE s_mux = portMUX_INITIALIZER_UNLOCKED;

inline bool is_code(uint32_t a)
{
    return !(a & 1) && ((a >= (uint32_t)&_instruction_reserved_start && a < (uint32_t)&_instruction_reserved_end) ||
                        (a >= (uint32_t)&_iram_text_start && a < (uint32_t)&_iram_text_end));
}

void init_locked()
{
    if (s_store.magic == MAGIC) s_prev = s_store;
    else memset(&s_prev, 0, sizeof s_prev);
    memset(&s_store, 0, sizeof s_store);
    s_store.magic = MAGIC;
    s_inited = true;
}

// 在调用者的栈上从 sp 往上找像返回地址的字（RISC-V 没有帧指针，只能这样）
IRAM_ATTR void record(char type, void* ptr)
{
    Ev e = {};
    e.ptr  = (uint32_t)ptr;
    e.t_ms = (uint32_t)(esp_timer_get_time() / 1000);
    e.type = type;
    strlcpy(e.task, pcTaskGetName(nullptr), sizeof e.task);
    if (type == 'F') {
        const uint8_t* p = (const uint8_t*)ptr;     // 块已经不归 lwIP 了，按字节读，不当成对象用
        e.state = p[offsetof(struct tcp_pcb, state)];
        memcpy(&e.lport, p + offsetof(struct tcp_pcb, local_port), 2);
        memcpy(&e.rport, p + offsetof(struct tcp_pcb, remote_port), 2);
        memcpy(&e.flags, p + offsetof(struct tcp_pcb, flags), 2);
    }
    uint32_t  tp;
    __asm__ volatile("mv %0, tp" : "=r"(tp));
    uint32_t* sp  = (uint32_t*)__builtin_frame_address(0);
    uint32_t* end = (uint32_t*)tp;
    if (end < sp || end - sp > 2048) end = sp + 256;
    int n = 0;
    for (uint32_t* w = sp; w < end && n < NPC; w++)
        if (is_code(*w)) e.pc[n++] = *w;

    portENTER_CRITICAL(&s_mux);
    if (!s_inited) init_locked();
    s_store.ev[s_store.n % NEV] = e;
    s_store.n++;
    portEXIT_CRITICAL(&s_mux);
}

void print_store(const char* title, const Store& s)
{
    uint32_t n = s.n, first = n > NEV ? n - NEV : 0;
    ESP_LOGW(TAG, "== %s：共 %u 条%s", title, (unsigned)n, n > NEV ? "（只留最后 64 条）" : "");
    for (uint32_t i = first; i < n; i++) {
        const Ev& e = s.ev[i % NEV];
        char buf[200];
        int  o = 0;
        for (int k = 0; k < NPC && e.pc[k]; k++) o += snprintf(buf + o, sizeof buf - o, " %08x", (unsigned)e.pc[k]);
        if (e.type == 'F')
            ESP_LOGW(TAG, "%c %08x t=%u.%03u %-7s state=%u lport=%u rport=%u flags=%04x pc:%s", e.type, (unsigned)e.ptr,
                     (unsigned)(e.t_ms / 1000), (unsigned)(e.t_ms % 1000), e.task, e.state, e.lport, e.rport, e.flags, buf);
        else
            ESP_LOGW(TAG, "%c %08x t=%u.%03u %-7s pc:%s", e.type, (unsigned)e.ptr, (unsigned)(e.t_ms / 1000),
                     (unsigned)(e.t_ms % 1000), e.task, buf);
        vTaskDelay(pdMS_TO_TICKS(5));
    }
}

struct Census {
    SemaphoreHandle_t done;
    int               active[11], tw, bound, listen;
    char              tw_list[160];
};
void census_cb(void* arg)
{
    Census* c = (Census*)arg;
    // 计数自己清零：上一次 dump 等超时之后才跑到的回调和这一次的回调用的是同一个 Census，不清的话会累加
    SemaphoreHandle_t done = c->done;
    memset(c, 0, sizeof *c);
    c->done = done;
    for (tcp_pcb* p = tcp_active_pcbs; p; p = p->next)
        if (p->state < 11) c->active[p->state]++;
    int o = 0;
    for (tcp_pcb* p = tcp_tw_pcbs; p; p = p->next) {
        c->tw++;
        if (o < (int)sizeof c->tw_list - 24)
            o += snprintf(c->tw_list + o, sizeof c->tw_list - o, " %p:%u>%u", p, p->local_port, p->remote_port);
    }
    for (tcp_pcb* p = tcp_bound_pcbs; p; p = p->next) c->bound++;
    for (tcp_pcb_listen* p = tcp_listen_pcbs.listen_pcbs; p; p = p->next) c->listen++;
    xSemaphoreGive(c->done);
}
}  // namespace

extern "C" IRAM_ATTR void esp_heap_trace_alloc_hook(void* ptr, size_t size, uint32_t caps)
{
    if (size != sizeof(struct tcp_pcb)) return;
    if (xPortInIsrContext() || xTaskGetSchedulerState() == taskSCHEDULER_NOT_STARTED) return;
    portENTER_CRITICAL(&s_mux);
    int slot = -1;
    for (int i = 0; i < NWATCH; i++) {
        if (s_watch[i] == (uint32_t)ptr) {
            slot = i;
            break;
        }
        if (slot < 0 && !s_watch[i]) slot = i;
    }
    if (slot < 0) slot = s_watch_seq++ % NWATCH;      // 满了就顶掉一个
    s_watch[slot] = (uint32_t)ptr;
    portEXIT_CRITICAL(&s_mux);
    record('A', ptr);
}

extern "C" IRAM_ATTR void esp_heap_trace_free_hook(void* ptr)
{
    bool hit = false;
    portENTER_CRITICAL(&s_mux);
    for (int i = 0; i < NWATCH; i++)
        if (s_watch[i] == (uint32_t)ptr) {
            s_watch[i] = 0;
            hit = true;
        }
    portEXIT_CRITICAL(&s_mux);
    if (!hit) return;
    if (xPortInIsrContext() || xTaskGetSchedulerState() == taskSCHEDULER_NOT_STARTED) return;
    record('F', ptr);
}

namespace heapwatch {
void dump()
{
    portENTER_CRITICAL(&s_mux);
    if (!s_inited) init_locked();
    portEXIT_CRITICAL(&s_mux);
    static Store snap;
    portENTER_CRITICAL(&s_mux);
    snap = s_store;
    portEXIT_CRITICAL(&s_mux);
    if (s_prev.magic == MAGIC && s_prev.n) print_store("上一次开机", s_prev);
    else ESP_LOGW(TAG, "== 上一次开机：没有记录");
    print_store("这一次开机", snap);

    // 信号量只建一次、不删：等超时之后回调才跑到的话，它还会来给这个信号量，删掉就成了释放后使用。
    // 已知的限制：上一次 dump 等超时（tcpip 线程卡住 2 秒以上）之后紧接着再 dump，上一次的回调可能在这一次的回调还在数的时候
    // 把这一次的等待提前叫醒，打印出来的数就只数了一半。调试工具，碰上了再 dump 一次即可。
    static Census            c;
    static SemaphoreHandle_t sem = xSemaphoreCreateBinary();
    xSemaphoreTake(sem, 0);     // 上一次超时之后才给的那一下，清掉
    memset(&c, 0, sizeof c);
    c.done = sem;
    if (tcpip_callback(census_cb, &c) == ERR_OK && xSemaphoreTake(sem, pdMS_TO_TICKS(2000)) == pdTRUE) {
        ESP_LOGW(TAG, "TCP 控制块：活动 SYN_SENT %d SYN_RCVD %d ESTABLISHED %d FIN_WAIT_1 %d FIN_WAIT_2 %d CLOSE_WAIT %d CLOSING %d LAST_ACK %d；"
                      "TIME_WAIT %d；只绑定 %d；监听 %d",
                 c.active[2], c.active[3], c.active[4], c.active[5], c.active[6], c.active[7], c.active[8], c.active[9],
                 c.tw, c.bound, c.listen);
        if (c.tw) ESP_LOGW(TAG, "TIME_WAIT：%s", c.tw_list);
    }
}
}  // namespace heapwatch
#else
namespace heapwatch {
void dump() {}
}
#endif
