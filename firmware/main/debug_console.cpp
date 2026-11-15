// USB-JTAG 调试命令通道
#include "debug_console.hpp"
#include "esp_lvgl_port.h"   // '^' 渲染微基准
#include "net/wifi.hpp"
#include "net/worker.hpp"
#include "net/nettest.hpp"
#include <esp_hosted_bufstat.h>
#include "app.hpp"
#include "settings_store.hpp"
#include "wd_audio.hpp"
#include "wdisp.hpp"
#include "wdisp_crypto.hpp"
#include "wdisp_pair.hpp"
#include "utils/tab5_kbd.hpp"
#include "utils/kbd_keymap.hpp"   // 键盘的行数、列数
#include <cstring>
#include <cstdio>    // sscanf（disp 命令解析）
#include <cstdlib>   // atoi
#include "ui/ui.h"
#include "ui/ui_pairbox.hpp"
#include <lvgl.h>
#include "src/core/lv_obj_private.h"   // disp mem：对象/spec_attr 所在内存区普查

#include "board.hpp"

#include <driver/usb_serial_jtag.h>
#include <esp_app_desc.h>
#include <esp_log.h>
#include <esp_timer.h>
#include <esp_heap_caps.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/idf_additions.h>
#include <bsp/m5stack_tab5.h>
#include <esp_hosted_api.h>
#include <esp_wifi.h>    // 'w' 诊断：radio MAC / netif MAC / DHCP 状态比对
#include <esp_netif.h>

namespace dbgcon {

static const char* TAG = "dbgcon";

// ── 协处理器（板载 ESP32-C6）固件维护 ────────────────────────────────────
// P4 自身无射频，WiFi 在 C6 上，经 SDIO 由 esp-hosted 驱动。C6 跑的 slave 固件源码在 firmware/c6_slave/
// （本机的 esp-hosted 主机侧副本就是对着它测的）。官方恢复流程需要 Downloader 配件接板上预留下载口；
// esp_hosted 支持 host 经 SDIO 给 slave 做 OTA，免拆机——出厂固件的分区表与官方 slave 完全一致
// （ota_0/ota_1 各 1536K），所以这条路走得通。
//
// ⚠️ rpc_ota 内部的 esp_http_client 没配任何 TLS 字段 → 只能明文 HTTP，不能走
//    HTTPS 对外入口。用 Linux 开发机上临时起的 HTTP 服务即可（也免得把固件放进对外路径）。
// ⚠️ 下面是示例地址：刷之前改成你那台开发机的地址（和 Tab5 在同一个局域网里），重新编译烧录。
static const char* C6_OTA_URL = "http://192.168.1.10:8000/network_adapter.bin";

// OTA 作业：必须跑在常驻网络任务里（见 worker.hpp 军规：联网作业绝不"用完自删"，
// 否则 pthread TLSP 删除回调会误判非可执行而 abort）。下载 ~1MB + RPC 分块转发，
// 耗时数分钟。slave 侧只有完整走完 ota_end + set_boot_partition 才切启动分区，
// 所以中途失败（断网/URL 错）是安全的：otadata 不变，C6 照常从原固件启动。
static void c6_ota_job(void*)
{
    ESP_LOGW(TAG, "C6 OTA: 开始，url=%s（耗时数分钟，勿断电）", C6_OTA_URL);
    esp_err_t err = esp_hosted_slave_ota(C6_OTA_URL);
    if (err == ESP_OK) {
        ESP_LOGW(TAG, "C6 OTA: 成功，C6 将自行重启；WiFi 会短暂断开重连。用 '6' 复核版本");
    } else {
        ESP_LOGE(TAG, "C6 OTA: 失败 err=%s（未切启动分区，C6 仍跑原固件）", esp_err_to_name(err));
    }
}

static bool s_prof_req = false;   // 帧探针要开还是要关（'#' 切换、prof on|off 指定；实际的开关在 LVGL 线程里由 prof_apply 照它办）

// 军规：任何非 LVGL 任务碰 lv_*（包括 lv_async_call——它内部 lv_timer_create 改 LVGL
// timer 链表）都必须持显示锁，否则与 LVGL task 并发改链表 = 链表损坏/野指针。
static void ui_async(void (*fn)(void*))
{
    bsp_display_lock(0);
    lv_async_call(fn, nullptr);
    bsp_display_unlock();
}

// ── 多字符命令（同样回车结束）─────────────────────────────────────────────
// 为什么要有这一层：单字符命令曾把 94 个可打印键位**全部用尽**，再加功能就只能去
// 抢别人的大小写别名 —— 那是在错误的层打补丁。改成整行读取之后，命令可以起个
// 读得懂的名字、还能带参数。新命令一律加在这张表里，不再往单字符里塞。
// 单字符命令一条不用改，照常工作（只是同样要按回车），两套共存。
struct WordCmd {
    const char* name;
    const char* usage;
    void (*fn)(const char* arg);   // arg = 命令名之后的参数，已去首尾空白；无参时为 ""
};

// 外接 5V（Grove 口 / 扩展排针）：开机默认不上电。这里可以查状态，也可以临时开关（不落盘，重启后回到不上电）。
static void cmd_ext5v(const char* arg)
{
    if (strcmp(arg, "on") == 0 || strcmp(arg, "off") == 0) {
        bsp_set_ext_5v_en(strcmp(arg, "on") == 0);
    } else if (arg[0]) {
        ESP_LOGW(TAG, "用法：ext5v [on|off]；不带参数 = 只看状态");
        return;
    }
    ESP_LOGW(TAG, "外接 5V（EXT5V_EN 回读）=%d", bsp_get_ext_5v_en());
}

// WiFi-SDIO（esp-hosted）收发缓冲的用量和丢包计数。
//   hosted ackhold <微秒>   TCP 纯 ACK 最多暂存多久（0 = 不合并；不落盘，重启后回到默认值），新旧对比用
static void cmd_hosted(const char* arg)
{
    int us = 0;
    if (sscanf(arg, "ackhold %d", &us) == 1 && us >= 0) {
        esp_hosted_set_ack_hold_us((uint32_t)us);
    } else if (arg[0]) {
        ESP_LOGW(TAG, "用法：hosted [ackhold <微秒>]；不带参数 = 只看统计");
        return;
    }
    esp_hosted_log_buf_stats();
}

// 收包自测：量 Tab5 实际收到多少数据。发送端是 tools/dispbench/nettx.py。
//   netrx tcp <对端IP> <端口> [秒数=10] [每秒最多读多少KB=不限]
//                                          连过去只收不发，打印每秒吞吐和内部内存余量；
//                                          带限速 = 模拟应用读得慢，看接收窗口填满时占多少内部内存
//   netrx udp <本机端口> [秒数=10]         在本机端口上收，按序号统计丢包
static void cmd_netrx(const char* arg)
{
    char mode[8] = "", ip[20] = "";
    int  a = 0, b = 10, lim = 0;
    if (sscanf(arg, "%7s", mode) == 1 && strcmp(mode, "tcp") == 0 &&
        sscanf(arg, "%*s %19s %d %d %d", ip, &a, &b, &lim) >= 2 && a > 0 && a < 65536 && b > 0 && lim >= 0) {
        nettest::start_tcp_rx(ip, (uint16_t)a, b, lim);
    } else if (strcmp(mode, "udp") == 0 && sscanf(arg, "%*s %d %d", &a, &b) >= 1 &&
               a > 0 && a < 65536 && b > 0) {
        nettest::start_udp_rx((uint16_t)a, b);
    } else {
        ESP_LOGW(TAG, "用法：netrx tcp <对端IP> <端口> [秒数] [每秒最多读多少KB] | netrx udp <本机端口> [秒数]");
    }
}

// WiFi 省电开关（临时，不落盘；重新连上 WiFi 后回到省电）。不带参数只看状态。
// 省电时包按信标间隔成批到达，吞吐低、延迟大；量吞吐时要关掉对照。
static void cmd_wifips(const char* arg)
{
    if (strcmp(arg, "on") == 0)       esp_wifi_set_ps(WIFI_PS_MIN_MODEM);
    else if (strcmp(arg, "off") == 0) esp_wifi_set_ps(WIFI_PS_NONE);
    else if (arg[0]) { ESP_LOGW(TAG, "用法：wifips [on|off]；不带参数 = 只看状态"); return; }
    wifi_ps_type_t ps = WIFI_PS_NONE;
    esp_err_t e = esp_wifi_get_ps(&ps);
    ESP_LOGW(TAG, "WiFi 省电=%s（%s）", ps == WIFI_PS_NONE ? "关" : ps == WIFI_PS_MIN_MODEM ? "开(min modem)" : "开(max modem)",
             esp_err_to_name(e));
}

// 帧探针开 / 关，在 LVGL 线程里执行。照 s_prof_req 现在的值办，不是排队时的值：连着来几条时（LVGL 的异步调用后排的先执行）
// 不管先执行哪条，最后都落在最后一次要的状态上。
// 开：没开就开，而且总是从这一刻起重新统计滚动加速的命中；关：开着才关，并打印这一段的合计（scrlacc: window …）。
static void prof_apply(void*)
{
    if (s_prof_req) {
        if (!scrmgr_profiler_on()) scrmgr_profiler(true);
        ui_scroll_accel_dbg_window(true);
    } else if (scrmgr_profiler_on()) {
        scrmgr_profiler(false);
        ui_scroll_accel_dbg_window(false);
    }
}

// 帧探针：和 '#' 是同一个开关，但指定开还是关、不是切换，重复发也没事。脚本用它——切换式的命令在状态不明时发出去会正好弄反。
static void cmd_prof(const char* arg)
{
    if (strcmp(arg, "on") == 0)       s_prof_req = true;
    else if (strcmp(arg, "off") == 0) s_prof_req = false;
    else { ESP_LOGW(TAG, "用法：prof on|off（现在要的是 %s）", s_prof_req ? "on" : "off"); return; }
    ui_async(prof_apply);
}

// tap <x> <y>：点一下屏幕上的 (x, y)（横屏坐标）。找这一点上最上面的可点对象（先顶层、再当前页，和手指点到谁是同一套找法），
// 给它发 CLICKED。只发这一个事件、不经输入设备：按下的样子、长按、拖动都不会有，回调里问「是哪个输入设备点的」会拿到空。
// 和手指不一样的地方：置灰的对象也会收到；开关、复选框不会跟着切换（那是输入设备在松手时做的）；
// 按钮矩阵、软键盘点不中（它们靠按下时记住是哪个键）。所以只适合点普通按钮、可点的行。脚本复现「点了某个按钮之后」的问题用。
// 要和真手指一样（控件自己跟着变）用 press。
static void cmd_tap(const char* arg)
{
    int x = 0, y = 0;
    if (sscanf(arg, "%d %d", &x, &y) != 2 || x < 0 || x > 1279 || y < 0 || y > 719) {
        ESP_LOGW(TAG, "用法：tap <x> <y>（横屏坐标，x 0~1279、y 0~719）");
        return;
    }
    bsp_display_lock(0);
    lv_async_call([](void* p) {
        lv_point_t pt = {(int32_t)((uintptr_t)p >> 16), (int32_t)((uintptr_t)p & 0xffff)};
        const char* where = "系统层";
        lv_obj_t*   o     = lv_indev_search_obj(lv_layer_sys(), &pt);
        if (!o) { o = lv_indev_search_obj(lv_layer_top(), &pt); where = "顶层"; }
        if (!o) { o = lv_indev_search_obj(lv_screen_active(), &pt); where = "当前页"; }
        // 点到的是哪一层的对象也说一声：顶层 = 有弹框（确认框、配对密码框）盖着
        if (o) ESP_LOGW(TAG, "CMD: tap (%d, %d)：点到%s的对象", (int)pt.x, (int)pt.y, where);
        else   ESP_LOGW(TAG, "CMD: tap (%d, %d)：这一点上没有可点的对象", (int)pt.x, (int)pt.y);
        if (o) lv_obj_send_event(o, LV_EVENT_CLICKED, nullptr);
    }, (void*)(uintptr_t)(((uint32_t)(x & 0xffff) << 16) | (uint32_t)(y & 0xffff)));
    bsp_display_unlock();
}

// 显示管线实验（2026-11-11 刷新性能探索）。全部投递到 LVGL 任务执行。
//   disp bench            扩展微基准（内存区速度/刷新率/CPU 旋转/DMA2D/PPA 小块）
//   disp awake on|off     常亮开关（on 时顺带唤醒；远程测试不被息屏打断）
//   disp accel on|off     滚动位移复用开关（off = 量整屏重绘基线）
//   disp pipe 0|1|2|3     flush 输出管线：0=PPA 直写(生产) 1=丢弃 2=CPU 旋转单核 3=双核
//   disp dbuf <rows>      绘制缓冲换成 rows 行小条带（0=恢复整屏缓冲）
//   disp mem              内存区普查：各内部堆余量、不同大小 malloc 落在哪个区、任务栈/LVGL 对象所在区
//   disp lvprof           打印并清零 LVGL 渲染探针（按绘制任务类型耗时、圆角填充分段；需先打 tools/dispbench 探针补丁）
//   disp maskram 0|1      圆角蒙版缓冲 0=照常 malloc 1=强制 PSRAM（对照实验，同上需探针补丁）
//   disp hog on|off       占满/释放 TCM 剩余空间：复现「开机久了 TCM 被占用 → 0.5~16KB 的 malloc 落进 LP RAM」的状态
//   disp lpclaim on|off   占满/释放 LP RAM 剩余空间：运行时模拟「LP RAM 不进堆」
//                         （= CONFIG_ESP_SYSTEM_ALLOW_RTC_FAST_MEM_AS_HEAP=n 对此后新分配的效果）
//   disp dmahog on|off    占满/释放内部 DMA 内存（含预留池）的全部剩余空间：复现「内部内存吃紧」，
//                         用来验证 WiFi-SDIO 收发在拿不到缓冲时是丢包而不是断言重启（配合 netstress.sh / hosted）
//   disp extmem <n|default>  运行时改 malloc「优先内部 RAM」的大小门限（= CONFIG_SPIRAM_MALLOC_ALWAYSINTERNAL，默认 16384；持续到重启）

// LVGL 临时探针（tools/dispbench/lvgl_probe_patch.py 打进 dependencies/lvgl）。弱引用：没打补丁时也能编译。
extern "C" {
extern volatile uint32_t lv_dbg_task_us[16] __attribute__((weak));
extern volatile uint32_t lv_dbg_task_n[16] __attribute__((weak));
extern volatile uint32_t lv_dbg_fill_stat[8] __attribute__((weak));
extern volatile uint32_t lv_dbg_fill_mask_psram __attribute__((weak));
}

// 地址属于哪个内存区（ESP32-P4）：L2MEM=片内主 RAM，LPRAM=低功耗域 RAM（访问极慢），TCM，PSRAM
static const char* mem_region(const void* p)
{
    uintptr_t a = (uintptr_t)p;
    if (a >= 0x4ff00000u && a < 0x4ffc0000u) return "L2MEM";
    if (a >= 0x50108000u && a < 0x50110000u) return "LPRAM";
    if (a >= 0x30100000u && a < 0x30102000u) return "TCM";
    if (a >= 0x48000000u && a < 0x4c000000u) return "PSRAM";
    return "?";
}

static int mem_region_idx(const void* p)
{
    const char* r = mem_region(p);
    return r[0] == 'L' ? (r[1] == '2' ? 0 : 1) : r[0] == 'T' ? 2 : r[0] == 'P' ? 3 : 4;
}

// 递归统计 LVGL 对象（及其 spec_attr）落在各内存区的个数
static void lv_obj_region_census(lv_obj_t* o, uint32_t cnt[5], uint32_t spec[5])
{
    cnt[mem_region_idx(o)]++;
    if (o->spec_attr) spec[mem_region_idx(o->spec_attr)]++;
    uint32_t n = lv_obj_get_child_count(o);
    for (uint32_t i = 0; i < n; i++) lv_obj_region_census(lv_obj_get_child(o, i), cnt, spec);
}

static void disp_mem_dump()
{
    printf("==== disp mem ====\n");
    heap_caps_print_heap_info(MALLOC_CAP_INTERNAL);
    printf("free: INTERNAL=%u INTERNAL|DMA=%u(largest %u) RTCRAM=%u(largest %u) TCM=%u SPIRAM=%u\n",
           (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),
           (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA),
           (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA),
           (unsigned)heap_caps_get_free_size(MALLOC_CAP_RTCRAM),
           (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_RTCRAM),
           (unsigned)heap_caps_get_free_size(MALLOC_CAP_TCM),
           (unsigned)heap_caps_get_free_size(MALLOC_CAP_SPIRAM));
    // 不同大小的普通 malloc 现在会落到哪个区
    static const size_t sizes[] = {32, 128, 512, 1280, 4096, 8192, 16384, 20000};
    for (size_t sz : sizes) {
        void* q = malloc(sz);
        printf("malloc(%5u) -> %p %s\n", (unsigned)sz, q, q ? mem_region(q) : "FAIL");
        free(q);
    }
    // 任务栈所在区
    UBaseType_t n = uxTaskGetNumberOfTasks() + 4;
    TaskStatus_t* st = (TaskStatus_t*)heap_caps_malloc(n * sizeof(TaskStatus_t), MALLOC_CAP_SPIRAM);
    if (st) {
        n = uxTaskGetSystemState(st, n, nullptr);
        uint32_t cnt[5] = {0};
        for (UBaseType_t i = 0; i < n; i++) {
            int r = mem_region_idx(st[i].pxStackBase);
            cnt[r]++;
            if (r != 0) printf("task %-16s stack %p %s\n", st[i].pcTaskName, st[i].pxStackBase, mem_region(st[i].pxStackBase));
        }
        printf("task stacks: L2MEM=%u LPRAM=%u TCM=%u PSRAM=%u other=%u\n",
               (unsigned)cnt[0], (unsigned)cnt[1], (unsigned)cnt[2], (unsigned)cnt[3], (unsigned)cnt[4]);
        free(st);
    }
    // 当前屏 LVGL 对象所在区
    if (bsp_display_lock(1000)) {
        uint32_t cnt[5] = {0}, spec[5] = {0};
        lv_obj_region_census(lv_screen_active(), cnt, spec);
        lv_obj_region_census(lv_layer_top(), cnt, spec);
        bsp_display_unlock();
        printf("lv objs:  L2MEM=%u LPRAM=%u TCM=%u PSRAM=%u other=%u\n",
               (unsigned)cnt[0], (unsigned)cnt[1], (unsigned)cnt[2], (unsigned)cnt[3], (unsigned)cnt[4]);
        printf("lv spec:  L2MEM=%u LPRAM=%u TCM=%u PSRAM=%u other=%u\n",
               (unsigned)spec[0], (unsigned)spec[1], (unsigned)spec[2], (unsigned)spec[3], (unsigned)spec[4]);
    }
    printf("==== end ====\n");
}

// 占住某个内存区（caps 指定：TCM、LP RAM 或内部 DMA 内存）的全部剩余空间，块首字串成单链表；off 时全部释放。
// 占 TCM = 复现开机久了的状态；占 LP RAM = 模拟它不进堆（此后 ≤门限 的 malloc 溢出到 PSRAM，
// 只要内部 RAM 的分配溢出到预留 DMA 池）。
static void disp_hog(const char* name, uint32_t caps, void** head, bool on)
{
    size_t total = 0;
    int    n = 0;
    if (on) {
        for (size_t sz = 16384; sz >= 16;) {
            void* q = heap_caps_malloc(sz, caps);
            if (!q) { sz /= 2; continue; }
            *(void**)q = *head;
            *head = q;
            total += sz;
            n++;
        }
    } else {
        while (*head) {
            void* nx = *(void**)*head;
            free(*head);
            *head = nx;
            n++;
        }
    }
    void* probe = malloc(1280);
    ESP_LOGW("dbgcon", "%s %s: %d 块 %u 字节；该区剩 %u；malloc(1280) 现在落在 %s",
             name, on ? "on" : "off", n, (unsigned)total,
             (unsigned)heap_caps_get_free_size(caps), probe ? mem_region(probe) : "FAIL");
    free(probe);
}
static void* s_hog_tcm = nullptr;
static void* s_hog_lp  = nullptr;
static void* s_hog_dma = nullptr;

static void disp_lvprof_dump()
{
    if (!&lv_dbg_task_us || !&lv_dbg_task_n || !&lv_dbg_fill_stat) { ESP_LOGW("dbgcon", "LVGL 探针补丁未打"); return; }
    static const char* const names[] = {"none", "fill", "border", "shadow", "label", "image", "layer", "line",
                                        "arc", "tri", "maskrect", "maskbmp", "vector"};
    printf("==== lvprof ====\n");
    for (int i = 0; i < 13; i++) {
        // 绘制线程在用原子加法累加：读出并清零也用原子交换，免得夹在中间的计数丢掉
        uint32_t us  = __atomic_exchange_n(&lv_dbg_task_us[i], 0u, __ATOMIC_RELAXED);
        uint32_t cnt = __atomic_exchange_n(&lv_dbg_task_n[i], 0u, __ATOMIC_RELAXED);
        if (cnt) printf("task %-8s n=%u us=%u (avg %u)\n", names[i], (unsigned)cnt, (unsigned)us, (unsigned)(us / cnt));
    }
    uint32_t f[8];
    for (int i = 0; i < 8; i++) f[i] = __atomic_exchange_n(&lv_dbg_fill_stat[i], 0u, __ATOMIC_RELAXED);
    if (f[0]) {
        printf("roundfill n=%u total=%uus prep=%uus maskblend=%uus center=%uus iters=%u (per-iter %uus) mask_buf=%p %s stack=%p %s\n",
               (unsigned)f[0], (unsigned)f[1], (unsigned)f[2], (unsigned)f[3], (unsigned)f[4], (unsigned)f[7],
               (unsigned)(f[7] ? (f[2] + f[3]) / f[7] : 0),
               (void*)(uintptr_t)f[5], mem_region((void*)(uintptr_t)f[5]),
               (void*)(uintptr_t)f[6], mem_region((void*)(uintptr_t)f[6]));
    }
    printf("==== end ====\n");
}
static void ui_async_int(void (*fn)(void*), int v)
{
    bsp_display_lock(0);
    lv_async_call(fn, (void*)(intptr_t)v);
    bsp_display_unlock();
}

// 滚动基准页：点名开某一个变体（'(' 是接着上一次往下轮，脚本里要哪个不好数）。息屏着不开（ui_dbg_benchpage 自己拒绝）
static void cmd_bench(const char* arg)
{
    const int v = arg[0] && !arg[1] ? (arg[0] | 0x20) - 'a' : -1;
    if (v < 0 || v > 6) {
        ESP_LOGW(TAG, "用法：bench <a~g>（A纯色 / B圆角 / C中文 / D位图 / E瓦片平移 / F边滚边变 / G同F带滚动条样式）");
        return;
    }
    ui_async_int([](void* p) { ui_dbg_benchpage((int)(intptr_t)p); }, v);
}

// press <x> <y> [毫秒]：一根手指按在屏幕的 (x, y) 上（横屏坐标），过一阵（默认 80 毫秒）抬起。
// 经一个调试用的指针输入设备喂给 LVGL，之后和真手指走的是同一条路：按下的样子、松手时拨钮 / 分段按钮的切换、
// 按钮矩阵和软键盘认哪个键、置灰的点不动，都照常。tap 只给对象发一个 CLICKED，这些都走不到——
// 要验「手指点了之后控件自己怎么变」用这一条。
// 电脑的画面占着屏幕时 LVGL 停着，排进去的要等画面结束才轮到：那时已经不是发命令时的页面了，所以排队超过 1 秒的丢掉、不点。
// 上一下还没抬起就又来一条的也不点（接着按新坐标的话 LVGL 看到的是同一根手指拖过去）。
static lv_indev_t* s_press_indev;       // 头一次用到时才建
static struct {
    lv_point_t raw;         // 喂给 LVGL 的坐标：LVGL 会按屏幕的旋转再换算一遍，所以这里存的是换算之前的
    enum { Up, Pending, Down } st;      // Pending = 要按下、还没报给 LVGL
    uint32_t   hold_ms;     // 按多久
    uint32_t   until;       // 按到什么时候（lv_tick）。从头一次报「按下」算起：这样再短的一下 LVGL 也至少看到一次按下
} s_press;                  // 只在 LVGL 线程里读写

static void press_read_cb(lv_indev_t*, lv_indev_data_t* data)
{
    if (s_press.st == s_press.Pending) {
        s_press.st    = s_press.Down;
        s_press.until = lv_tick_get() + s_press.hold_ms;
    } else if (s_press.st == s_press.Down && (int32_t)(lv_tick_get() - s_press.until) >= 0) {
        s_press.st = s_press.Up;
    }
    data->point = s_press.raw;
    data->state = s_press.st == s_press.Down ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED;
}

struct PressArg { int16_t x, y; uint16_t ms; int64_t t_cmd; };     // 每条命令自己一份，跟着异步回调走（回调里释放）

static void cmd_press(const char* arg)
{
    int x = 0, y = 0, ms = 80;
    const int n = sscanf(arg, "%d %d %d", &x, &y, &ms);
    if (n < 2 || x < 0 || x > 1279 || y < 0 || y > 719 || ms < 20 || ms > 5000) {
        ESP_LOGW(TAG, "用法：press <x> <y> [毫秒]（横屏坐标，x 0~1279、y 0~719；按住 20~5000 毫秒，默认 80）");
        return;
    }
    PressArg* a = (PressArg*)malloc(sizeof(PressArg));
    if (!a) return;
    *a = {(int16_t)x, (int16_t)y, (uint16_t)ms, esp_timer_get_time()};
    bsp_display_lock(0);
    lv_async_call([](void* p) {
        const PressArg a = *(PressArg*)p;
        free(p);
        if (esp_timer_get_time() - a.t_cmd > 1000000) {
            ESP_LOGW(TAG, "CMD: press (%d, %d)：排了 1 秒以上才轮到（LVGL 停着：电脑的画面占着屏幕），不点了", a.x, a.y);
            return;
        }
        if (s_press.st != s_press.Up) {
            ESP_LOGW(TAG, "CMD: press (%d, %d)：上一下还按着，这一条不点", a.x, a.y);
            return;
        }
        if (!s_press_indev) {
            s_press_indev = lv_indev_create();
            lv_indev_set_type(s_press_indev, LV_INDEV_TYPE_POINTER);
            lv_indev_set_read_cb(s_press_indev, press_read_cb);
            scrmgr_watch_indev(s_press_indev);      // 和触摸屏一样：按下就升到满速
        }
        // 把横屏坐标换回 LVGL 换算之前的坐标（lv_indev.c 的 indev_pointer_proc 按屏幕的旋转做的那一步，反过来算）
        lv_display_t* d = lv_display_get_default();
        const lv_display_rotation_t rot = lv_display_get_rotation(d);
        const bool    swapped = rot == LV_DISPLAY_ROTATION_90 || rot == LV_DISPLAY_ROTATION_270;
        const int32_t hor = swapped ? lv_display_get_vertical_resolution(d) : lv_display_get_horizontal_resolution(d);     // 没转之前的宽、高
        const int32_t ver = swapped ? lv_display_get_horizontal_resolution(d) : lv_display_get_vertical_resolution(d);
        const int32_t px = a.x, py = a.y;
        switch (rot) {
            case LV_DISPLAY_ROTATION_90:  s_press.raw = {py, ver - 1 - px}; break;
            case LV_DISPLAY_ROTATION_180: s_press.raw = {hor - 1 - px, ver - 1 - py}; break;
            case LV_DISPLAY_ROTATION_270: s_press.raw = {hor - 1 - py, px}; break;
            default:                      s_press.raw = {px, py}; break;
        }
        s_press.hold_ms = a.ms;
        s_press.st      = s_press.Pending;
        ESP_LOGW(TAG, "CMD: press (%d, %d) %d 毫秒", (int)px, (int)py, (int)a.ms);
    }, a);
    bsp_display_unlock();
}

static void cmd_disp(const char* arg)
{
    char sub[12] = {0};
    int  val = 0;
    char word[8] = {0};
    int  n = sscanf(arg, "%11s %7s", sub, word);
    if (n >= 2) val = (strcasecmp(word, "on") == 0) ? 1 : (strcasecmp(word, "off") == 0) ? 0 : atoi(word);

    if (strcasecmp(sub, "bench") == 0) {
        ui_async([](void*) {
            lvgl_port_dbg_render_bench2(lv_display_get_default());
            lv_obj_invalidate(lv_screen_active());
        });
    } else if (strcasecmp(sub, "awake") == 0 && n >= 2) {
        ui_async_int([](void* p) {
            static bool held = false;       // 这条命令自己要没要过常亮（重复 on / off 不重复计数）
            bool on = (intptr_t)p != 0;
            if (on != held) {
                held = on;
                scrmgr_inhibit(on);
            }
            if (on && scrmgr_is_off()) scrmgr_wake();
        }, val);
    } else if (strcasecmp(sub, "accel") == 0 && n >= 2) {
        ui_async_int([](void* p) { ui_scroll_accel_dbg_enable((intptr_t)p != 0); }, val);
    } else if (strcasecmp(sub, "pipe") == 0 && n >= 2) {
        ui_async_int([](void* p) {
            lvgl_port_dbg_set_pipe((int)(intptr_t)p);
            lv_obj_invalidate(lv_screen_active());
        }, val);
    } else if (strcasecmp(sub, "dbuf") == 0 && n >= 2) {
        ui_async_int([](void* p) {
            esp_err_t e = lvgl_port_dbg_set_drawbuf_rows(lv_display_get_default(), (int)(intptr_t)p);
            if (e != ESP_OK) ESP_LOGW("dbgcon", "disp dbuf 失败: %s", esp_err_to_name(e));
            lv_obj_invalidate(lv_screen_active());
        }, val);
    } else if (strcasecmp(sub, "mem") == 0) {
        disp_mem_dump();
    } else if (strcasecmp(sub, "lvprof") == 0) {
        disp_lvprof_dump();
    } else if (strcasecmp(sub, "hog") == 0 && n >= 2) {
        disp_hog("hog(TCM)", MALLOC_CAP_TCM, &s_hog_tcm, val != 0);
    } else if (strcasecmp(sub, "lpclaim") == 0 && n >= 2) {
        disp_hog("lpclaim(LP RAM)", MALLOC_CAP_RTCRAM, &s_hog_lp, val != 0);
    } else if (strcasecmp(sub, "dmahog") == 0 && n >= 2) {
        disp_hog("dmahog(内部 DMA)", MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA, &s_hog_dma, val != 0);
    } else if (strcasecmp(sub, "extmem") == 0 && n >= 2) {
        // 只接受正整数；default/off 恢复编译期默认值。0 会让所有 malloc 改走 PSRAM，
        // 负数会变成超大门限把内部 RAM 挤爆，都不接受。设置持续到重启。
        long lim = -1;
        if (strcasecmp(word, "default") == 0 || strcasecmp(word, "off") == 0) {
            lim = CONFIG_SPIRAM_MALLOC_ALWAYSINTERNAL;
        } else {
            char* end = nullptr;
            long  v   = strtol(word, &end, 10);
            if (end && end != word && *end == '\0' && v > 0) lim = v;
        }
        if (lim > 0) {
            heap_caps_malloc_extmem_enable((size_t)lim);
            ESP_LOGW("dbgcon", "malloc 优先内部 RAM 的门限 = %ld 字节", lim);
        } else {
            ESP_LOGW("dbgcon", "用法：disp extmem <正整数字节数|default>");
        }
    } else if (strcasecmp(sub, "maskram") == 0 && n >= 2) {
        if (&lv_dbg_fill_mask_psram) {
            lv_dbg_fill_mask_psram = val ? 1 : 0;
            ESP_LOGW("dbgcon", "圆角蒙版缓冲: %s", val ? "强制 PSRAM" : "照常 malloc");
        } else {
            ESP_LOGW("dbgcon", "LVGL 探针补丁未打");
        }
    } else {
        ESP_LOGW(TAG, "用法：disp bench | disp awake on|off | disp accel on|off | disp pipe 0..3 | disp dbuf <rows> | disp mem | disp lvprof | disp maskram 0|1 | disp hog on|off | disp dmahog on|off | disp lpclaim on|off | disp extmem <n|default>");
    }
}

// 远程打开页面：没法点屏幕时，用它验证每个页面都能正常建立和销毁。
struct PageCmd { const char* name; void (*open)(void); };
static const PageCmd PAGES[] = {
    {"wdisp",    ui_open_main},     // 主页面
    {"settings", ui_open_settings},
    {"wifi",     ui_open_wifi},
    {"pairs",    ui_open_pairs},
};

static void cmd_open(const char* arg)
{
    // 不带参数：报记录的"当前页面"（各页开页时记的；息屏着、或者画面刚结束页面还没建回来时，是接下来要回的那一页，
    // 不一定已经在前台——要确认在前台，主页面、设置页用 wd page）、屏幕亮没亮
    if (!arg[0]) {
        bsp_display_lock(0);
        lv_async_call([](void*) {
            const char* name = "？";
            for (const auto& pg : PAGES) if (pg.open == ui_nav_current()) name = pg.name;
            ESP_LOGW(TAG, "open：记录的当前页是 %s，屏幕%s", name, scrmgr_is_off() ? "息着" : "亮着");
        }, nullptr);
        bsp_display_unlock();
        return;
    }
    for (const auto& pg : PAGES) {
        if (strcasecmp(arg, pg.name) != 0) continue;
        bsp_display_lock(0);   // 非 LVGL 任务调 lv_async_call 必须持显示锁（见 ui_async）
        lv_async_call([](void* p) {
            const PageCmd* page = (const PageCmd*)p;
            // 息屏时不换页：背光是关的、刷新率也降了，换过去只会变成"屏黑但页面在跑"。
            // 判断放在 LVGL 任务里做：息屏状态只在这个任务里改，在这里看才不会和自动息屏抢先后。
            if (scrmgr_is_off()) {
                ESP_LOGW(TAG, "息屏中，先发 o 唤醒再 open");
                return;
            }
            ESP_LOGW(TAG, "CMD: open %s", page->name);
            page->open();
        }, (void*)&pg);
        bsp_display_unlock();
        return;
    }
    ESP_LOGW(TAG, "用法：open [wdisp|settings|wifi|pairs]（wdisp = 主页面；不带参数 = 报记录的当前页、屏幕亮没亮）");
}

// 无线显示接收端：状态 / 断开当前连接 / 核对测试卡 / 模拟退出手势 / 暂停 / 点「开始显示」/ 主页面现在的样子 / 触摸板模式和加速 / 密码学自检 / 配对密码框 / 配对过的电脑
// 改设置的几条（pic / osd / tpad / vol）走 app 的 set_*，和界面上拨是同一条路
static void cmd_wd(const char* arg)
{
    // 配对密码框：和手指走同一条路（填进输入框、点按钮），所以放到 LVGL 任务里做
    if (strncasecmp(arg, "pw ", 3) == 0 || strcasecmp(arg, "pwcancel") == 0 || strcasecmp(arg, "box") == 0
        || strcasecmp(arg, "page") == 0 || strcasecmp(arg, "start") == 0 || strcasecmp(arg, "pause") == 0 || strncasecmp(arg, "slide ", 6) == 0) {
        char* end = nullptr;
        const long pct = strncasecmp(arg, "slide ", 6) == 0 ? strtol(arg + 6, &end, 10) : 0;
        if (end && (end == arg + 6 || *end || pct < 0 || pct > 100)) {
            ESP_LOGW(TAG, "用法：wd slide <0~100>");
            return;
        }
        bsp_display_lock(0);
        if (end) {
            lv_async_call([](void* p) { ui_main_dbg_volume((int)(intptr_t)p); }, (void*)(intptr_t)pct);
        } else if (strcasecmp(arg, "box") == 0) {
            lv_async_call([](void*) { ui_pairbox_dbg_box(); }, nullptr);
        } else if (strcasecmp(arg, "page") == 0) {
            lv_async_call([](void*) { ui_main_dbg_page(); ui_settings_dbg_page(); }, nullptr);
        } else if (strcasecmp(arg, "start") == 0) {
            lv_async_call([](void*) { ui_main_dbg_start(); }, nullptr);
        } else if (strcasecmp(arg, "pause") == 0) {
            lv_async_call([](void*) {
                app::pause();
                ESP_LOGW(TAG, "wd：暂停了（不再接受连接；点「开始显示」/ wd start 接着等）");
            }, nullptr);
        } else {
            char* pw = arg[2] == ' ' ? lv_strdup(arg + 3) : nullptr;      // 空 = 点取消
            lv_async_call([](void* p) {
                ui_pairbox_dbg_password((const char*)p);
                if (p) lv_free(p);
            }, pw);
        }
        bsp_display_unlock();
        return;
    }
    if (strcasecmp(arg, "pairs") == 0 || strncasecmp(arg, "forget ", 7) == 0) {
        wdpair::Entry list[wdpair::MAX_PEERS];
        const int     n = wdpair::list(list);
        if (strcasecmp(arg, "pairs") == 0) {
            ESP_LOGW(TAG, "wd：配对过 %d 台电脑（最近连过的在前）", n);
            for (int i = 0; i < n; i++)
                ESP_LOGW(TAG, "wd：  %d  「%s」 编号 %02x%02x%02x%02x…", i + 1, list[i].name, list[i].id[0], list[i].id[1], list[i].id[2], list[i].id[3]);
        } else if (strcasecmp(arg + 7, "all") == 0) {
            if (wdisp::forget_all()) ESP_LOGW(TAG, "wd：%d 台电脑的配对都移除了", n);
            else ESP_LOGE(TAG, "wd：有的配对从闪存里删不掉，还留着（wd pairs 看剩下的）");
        } else {
            const int i = atoi(arg + 7);
            if (i < 1 || i > n) {
                ESP_LOGW(TAG, "wd：没有第 %d 台（wd pairs 看序号）", i);
            } else if (wdisp::forget(list[i - 1].id)) {
                ESP_LOGW(TAG, "wd：「%s」的配对移除了", list[i - 1].name);
            } else {
                ESP_LOGE(TAG, "wd：「%s」的配对没移除掉", list[i - 1].name);
            }
        }
        return;
    }
    if (strcasecmp(arg, "kick") == 0) {
        wdisp::kick();
        ESP_LOGW(TAG, "wd：已要求断开当前连接");
        return;
    }
    if (strcasecmp(arg, "check") == 0) {
        wdisp::dbg_check_card();
        return;
    }
    if (strncasecmp(arg, "px ", 3) == 0) {
        int x = -1, y = -1;
        if (sscanf(arg + 3, "%d %d", &x, &y) == 2) wdisp::dbg_pixel(x, y);
        else ESP_LOGW(TAG, "wd px <x> <y>：读前台画面上这一点的像素");
        return;
    }
    if (strcasecmp(arg, "exit") == 0) {
        wdisp::dbg_user_exit();
        return;
    }
    if (strncasecmp(arg, "touch ", 6) == 0) {    // 当成有一根手指按在横屏画面的这一点上 / 抬起
        int x = -1, y = -1;
        if (strcasecmp(arg + 6, "up") == 0) wdisp::dbg_touch(-1, -1);
        else if (sscanf(arg + 6, "%d %d", &x, &y) == 2 && x >= 0 && y >= 0) wdisp::dbg_touch(x, y);
        else ESP_LOGW(TAG, "用法：wd touch <x 0~1279> <y 0~719> | wd touch up");
        return;
    }
    if (strcasecmp(arg, "pic") == 0 || strncasecmp(arg, "pic ", 4) == 0) {
        // 画质：和主页面上那排画质按钮是同一个值（落盘）。给第几档就改，不给只报现在的
        if (arg[3]) {
            char* end   = nullptr;
            long  level = strtol(arg + 4, &end, 10);
            if (end == arg + 4 || *end || level < 0 || level >= settings::WD_PIC_N) {
                ESP_LOGW(TAG, "用法：wd pic [0~%d]（最佳画质 / 高画质 / 均衡 / 最佳速度）", settings::WD_PIC_N - 1);
                return;
            }
            app::set_picture((int)level);
        }
        ESP_LOGW(TAG, "wd：画质第 %u 档「%s」", (unsigned)settings::get().wd_pic, settings::WD_PIC_NAMES[settings::get().wd_pic]);
        return;
    }
    if (strcasecmp(arg, "osd") == 0 || strcasecmp(arg, "osd on") == 0 || strcasecmp(arg, "osd off") == 0) {
        // 调试信息浮层：和主页面上的「性能信息」开关是同一个值（落盘）。不带 on / off 只报现在是开是关
        if (arg[3]) {
            app::set_osd(strcasecmp(arg, "osd on") == 0);
        }
        ESP_LOGW(TAG, "wd：调试信息%s", settings::get().wd_osd ? "开" : "关");
        return;
    }
    if (strncasecmp(arg, "tpad accel", 10) == 0 && (arg[10] == '\0' || arg[10] == ' ')) {
        // 触摸板加速：和设置页上的「触摸板加速」是同一个值（落盘）。给档位（0~3 = 关 / 低 / 中 / 高）就改，不给只报现在是哪一档
        if (arg[10]) {
            char* end = nullptr;
            long level = strtol(arg + 11, &end, 10);
            if (end == arg + 11 || *end || level < 0 || level >= settings::WD_TPAD_GAIN_N) {
                ESP_LOGW(TAG, "用法：wd tpad accel [0~%d]（关 / 低 / 中 / 高）", settings::WD_TPAD_GAIN_N - 1);
                return;
            }
            app::set_touchpad_gain(settings::WD_TPAD_GAINS[level]);
        }
        const unsigned gain = settings::get().wd_tpad_gain;
        const char* name = "？";
        for (int i = 0; i < settings::WD_TPAD_GAIN_N; i++) if (settings::WD_TPAD_GAINS[i] == gain) name = settings::WD_TPAD_GAIN_NAMES[i];
        ESP_LOGW(TAG, "wd：触摸板加速 %s（最多 %u.%u 倍）", name, gain / 10, gain % 10);
        return;
    }
    if (strcasecmp(arg, "tpad") == 0 || strcasecmp(arg, "tpad on") == 0 || strcasecmp(arg, "tpad off") == 0) {
        // 触摸板模式：和设置页上的「触摸板模式」开关是同一个值（落盘）。不带 on / off 只报现在是开是关
        if (arg[4]) {
            app::set_touchpad(strcasecmp(arg, "tpad on") == 0);
        }
        ESP_LOGW(TAG, "wd：触摸板模式%s", settings::get().wd_touchpad ? "开" : "关");
        return;
    }
    if (strcasecmp(arg, "vol") == 0 || strncasecmp(arg, "vol ", 4) == 0) {
        // 音量：和主页面上的「音量」滑块是同一个值（落盘）。不带数只报现在是多少
        if (arg[3]) {
            char* end = nullptr;
            long v = strtol(arg + 4, &end, 10);
            if (end == arg + 4 || *end || v < 0 || v > 100) {
                ESP_LOGW(TAG, "用法：wd vol [0~100]");
                return;
            }
            app::set_volume((int)v);
        }
        ESP_LOGW(TAG, "wd：音量 %u", (unsigned)settings::get().wd_volume);
        return;
    }
    if (strcasecmp(arg, "abuf") == 0 || strncasecmp(arg, "abuf ", 5) == 0) {
        // 声音的起播缓冲（毫秒）：攒到这么多才开始放。不落盘，重启回到默认值；下一次起播起生效
        if (arg[4]) {
            char* end = nullptr;
            long v = strtol(arg + 5, &end, 10);
            if (end == arg + 5 || *end || v < wdaudio::PREBUFFER_MIN_MS || v > wdaudio::PREBUFFER_MAX_MS) {
                ESP_LOGW(TAG, "用法：wd abuf [%d~%d]", wdaudio::PREBUFFER_MIN_MS, wdaudio::PREBUFFER_MAX_MS);
                return;
            }
            wdaudio::set_prebuffer((int)v);
        }
        ESP_LOGW(TAG, "wd：声音的起播缓冲 %d ms（默认 %d）", wdaudio::prebuffer(), wdaudio::PREBUFFER_MS);
        return;
    }
    if (strcasecmp(arg, "audiotest") == 0 || strncasecmp(arg, "audiotest ", 10) == 0) {
        // 扬声器自检：放一个正弦波、用麦克风录回来（约 2 秒）。wd audiotest [赫兹] [l|r]，不给声道 = 左右都放
        int  hz = 1000;
        char ch = 0;
        const int n = arg[9] ? sscanf(arg + 10, "%d %c", &hz, &ch) : 0;
        const int channels = (ch == 'l' || ch == 'L') ? 1 : (ch == 'r' || ch == 'R') ? 2 : 3;
        if ((arg[9] && n < 1) || hz < 100 || hz > 8000 || (n == 2 && channels == 3)) {
            ESP_LOGW(TAG, "用法：wd audiotest [赫兹 100~8000] [l|r]");
            return;
        }
        wdaudio::selftest(hz, channels);        // 结果它自己打
        return;
    }
    if (strncasecmp(arg, "listen ", 7) == 0) {
        // 只录不放：麦克风录一段，报某个频率上有多响。电脑发来的声音正放着的时候看扬声器是不是真的在响。wd listen <赫兹> [毫秒]
        int hz = 0, ms = 1000;
        double db = 0;
        if (sscanf(arg + 7, "%d %d", &hz, &ms) < 1 || hz < 100 || hz > 8000 || ms < 100 || ms > 10000) {
            ESP_LOGW(TAG, "用法：wd listen <赫兹 100~8000> [毫秒 100~10000]");
            return;
        }
        if (wdaudio::listen(hz, ms, db)) ESP_LOGW(TAG, "wd listen：%d Hz 上 %.1f dBFS（录了 %d ms）", hz, db, ms);
        else ESP_LOGW(TAG, "wd listen：录不了（麦克风被占着或者开不了）");
        return;
    }
    if (strncasecmp(arg, "pop ", 4) == 0) {
        // 量爆音：麦克风录一段，当中按时刻做事，录完打两路（左麦克风 / 功放输出的回采）每 5 ms 的峰峰值。事件的时刻要落在整 10 ms 的录音块里
        // wd pop <毫秒> [sil@毫秒] [tone@毫秒] [stop@毫秒] [pa1@毫秒] [pa0@毫秒]
        wdaudio::PopEvent ev[8];
        int   n_ev = 0, ms = 0, used = 0;
        bool  ok = sscanf(arg + 4, "%d%n", &ms, &used) == 1 && ms >= 100 && ms <= 8000;
        for (const char* p = arg + 4 + used; ok && *p;) {
            char name[8];
            int  at = 0, len = 0;
            if (sscanf(p, " %7[a-z0-9]@%d%n", name, &at, &len) != 2 || n_ev >= 8 || at < 0 || at >= ms / 10 * 10) { ok = false; break; }
            p += len;
            wdaudio::PopEvent::Kind kind;
            if (!strcmp(name, "sil")) kind = wdaudio::PopEvent::SILENCE;
            else if (!strcmp(name, "tone")) kind = wdaudio::PopEvent::TONE;
            else if (!strcmp(name, "stop")) kind = wdaudio::PopEvent::STOP;
            else if (!strcmp(name, "pa1")) kind = wdaudio::PopEvent::PA_ON;
            else if (!strcmp(name, "pa0")) kind = wdaudio::PopEvent::PA_OFF;
            else { ok = false; break; }
            ev[n_ev++] = {at, kind};
        }
        if (!ok) {
            ESP_LOGW(TAG, "用法：wd pop <毫秒 100~8000> [sil@毫秒] [tone@毫秒] [stop@毫秒] [pa1@毫秒] [pa0@毫秒]");
            return;
        }
        ESP_LOGW(TAG, "wd pop：%s", wdaudio::poprec(ms, ev, n_ev) ? "录完" : "录不了（麦克风被占着、开不了，或者分不出缓冲）");
        return;
    }
    if (strcasecmp(arg, "selftest") == 0) {     // 密码学部分对着测试向量自检（约 18 秒）
        wdc::selftest();
        ESP_LOGW(TAG, "wd：自检完，调试台任务栈最低余量 %u 字节", (unsigned)uxTaskGetStackHighWaterMark(nullptr));
        return;
    }
    if (strcasecmp(arg, "gaps") == 0) {         // 这次连接里相邻两帧上屏的间隔（Tab5 自己量的）
        static wdisp::Gaps g;                   // 五百多字节，留在静态区（取的时候栈上还会过一份临时的，栈 8 KB 够用）；只有调试台任务用
        g = wdisp::gaps();
        ESP_LOGW(TAG, "wd gaps：量了 %u 个上屏间隔；不短于 50 / 80 / 100 / 150 / 200 / 300 / 500 ms 的各 %u / %u / %u / %u / %u / %u / %u 次；最长 %u.%u ms",
                 (unsigned)g.n, (unsigned)g.ge[0], (unsigned)g.ge[1], (unsigned)g.ge[2], (unsigned)g.ge[3], (unsigned)g.ge[4], (unsigned)g.ge[5],
                 (unsigned)g.ge[6], (unsigned)(g.max_us / 1000), (unsigned)(g.max_us % 1000 / 100));
        // 不短于 80 ms 的逐个列出来（帧序号:毫秒），发送端的逐帧记录按帧序号对得上
        const uint32_t kept = g.big_total < wdisp::Gaps::RECENT ? g.big_total : wdisp::Gaps::RECENT;
        if (g.big_total > kept) ESP_LOGW(TAG, "wd gaps：不短于 80 ms 的一共 %u 次，只留着最后 %u 次", (unsigned)g.big_total, (unsigned)kept);
        for (uint32_t i = 0; i < kept; ) {
            char   line[10 * 22 + 1];           // 一行 10 个，每个最长「 4294967295:4294967295」22 个字符
            size_t len = 0;
            for (int k = 0; k < 10 && i < kept; k++, i++) {
                const wdisp::Gaps::Big& b = g.recent[(g.big_total - kept + i) % wdisp::Gaps::RECENT];
                const int w = snprintf(line + len, sizeof line - len, " %u:%u", (unsigned)b.seq, (unsigned)b.ms);
                if (w < 0 || (size_t)w >= sizeof line - len) {          // 放不下（不该发生）：这一行到此为止，i 没加，下一行接着打
                    line[len] = '\0';
                    break;
                }
                len += (size_t)w;
            }
            if (len == 0) break;                // 一个都没放下：不会发生，防止死循环
            ESP_LOGW(TAG, "wd gaps：大间隔%s", line);
        }
        return;
    }
    static const char* const NAMES[] = {"没在监听", "等待连接", "已连接（第一帧还没到齐）", "显示中"};
    wdisp::Stats st = wdisp::stats();
    ESP_LOGW(TAG, "wd：%s %s「%s」；触摸板模式%s（加速最多 %u.%u 倍）；%u 帧 %u 个条带（另补解 %u 个）%u KB；最近一秒 %u.%u 帧/秒 %u kbps；"
                  "最近一帧 接收 %u.%u ms 解码 %u.%u ms 上屏 %u.%u ms",
             NAMES[(int)st.state], st.peer, st.name, settings::get().wd_touchpad ? "开" : "关",
             (unsigned)(settings::get().wd_tpad_gain / 10), (unsigned)(settings::get().wd_tpad_gain % 10), (unsigned)st.frames, (unsigned)st.strips, (unsigned)st.catchup,
             (unsigned)(st.bytes >> 10), (unsigned)(st.fps_x10 / 10), (unsigned)(st.fps_x10 % 10), (unsigned)st.kbps,
             (unsigned)(st.recv_us / 1000), (unsigned)(st.recv_us % 1000 / 100),
             (unsigned)(st.decode_us / 1000), (unsigned)(st.decode_us % 1000 / 100),
             (unsigned)(st.present_us / 1000), (unsigned)(st.present_us % 1000 / 100));
    ESP_LOGW(TAG, "wd：翻页围栏没放行的帧边界 %u 次（开机以来，界面滚动的也算；登记还没被那次中断采用，之后的帧边界才放行）", (unsigned)lvgl_port_dbg_flip_collisions());
    // 声音：这次连接（没有连接时是上一次）的；断音 = 正放着的时候放空了、0.5 秒之内又接上（放空之前是纯静音的不算），
    // 隔了 0.5 秒以上才接上的另记（链路停得久，或者声音自己停了又来）；
    // I2S 欠载 = 播放任务写晚了、I2S 自己补了约 2.7 ms 的静音（缓冲没放空，不在断音里）
    const wdaudio::Stats au = wdaudio::stats();
    ESP_LOGW(TAG, "wd：声音 音量 %u，起播缓冲 %d ms，%s%s；收到 %u ms 放出 %u ms，断音 %u 次（合计 %u ms，最长 %u ms；另有 %u 次隔了 0.5 秒以上才接上），攒多扔掉 %u ms，没收进来 %u ms，缓冲里 %u ms，I2S 欠载 %u 次",
             (unsigned)settings::get().wd_volume, wdaudio::prebuffer(), au.open ? "codec 开着" : "codec 关着", au.headphone ? "（耳机）" : "", (unsigned)au.pushed_ms, (unsigned)au.played_ms,
             (unsigned)au.breaks, (unsigned)au.break_ms, (unsigned)au.break_max_ms, (unsigned)au.long_gaps, (unsigned)au.trimmed_ms, (unsigned)au.dropped_ms, (unsigned)au.depth_ms, (unsigned)au.underruns);
}

// 键盘模块（Tab5 Keyboard）：状态 / 当成键盘上报了一个按键事件（没接键盘也能验证后面整条路）/ 每份报文打一行日志
static void cmd_kbd(const char* arg)
{
    int  row = -1, col = -1;
    char word[8] = "";
    if (sscanf(arg, "key %d %d %7s", &row, &col, word) == 3 && row >= 0 && row < kbdmap::ROWS && col >= 0 &&
        col < kbdmap::COLS && (strcmp(word, "down") == 0 || strcmp(word, "up") == 0)) {
        kbd::dbg_key((uint8_t)row, (uint8_t)col, word[0] == 'd');
        return;
    }
    if (strcmp(arg, "log on") == 0 || strcmp(arg, "log off") == 0) {
        kbd::dbg_log(strcmp(arg, "log on") == 0);
    } else if (arg[0]) {
        ESP_LOGW(TAG, "用法：kbd [key <行 0~%d> <列 0~%d> <down|up> | log on|off]；不带参数 = 只看状态", kbdmap::ROWS - 1, kbdmap::COLS - 1);
        return;
    }
    kbd::Info i = kbd::info();
    ESP_LOGW(TAG, "键盘：%s（固件版本 %u）；现在的报文 %02x %02x %02x %02x %02x %02x %02x %02x；任务栈最低余量 %u 字节",
             i.present ? "接着" : "没接", i.fw, i.now.b[0], i.now.b[1], i.now.b[2], i.now.b[3], i.now.b[4], i.now.b[5],
             i.now.b[6], i.now.b[7], (unsigned)i.stack_left);
}

// 息屏时间：和设置页上的「息屏时间」是同一个值（落盘）。不带参数只报现在是多少秒。秒数可以随便给
// （界面上只有那几档；测「到时间息屏」时给个几秒的，不用干等一分钟），0 = 不息屏
static void cmd_scroff(const char* arg)
{
    if (arg[0]) {
        char* end = nullptr;
        const long s = strtol(arg, &end, 10);
        if (end == arg || *end || s < 0 || s > 86400) {
            ESP_LOGW(TAG, "用法：scroff [秒]（0 = 不息屏）");
            return;
        }
        app::set_screenoff((uint32_t)s);
    }
    ESP_LOGW(TAG, "scroff：息屏时间 %lu 秒", (unsigned long)settings::get().screenoff_s);
}

// 设备上跑的是哪一版固件：提交号（编译时工作区有没提交的改动就带 -dirty）、编译时间；IDF 版本和面板型号也在这里看
static void cmd_ver(const char* /*arg*/)
{
    const esp_app_desc_t* d = esp_app_get_description();
    ESP_LOGW(TAG, "固件 %s，编译于 %s %s；ESP-IDF %s，面板 %s", d->version, d->date, d->time, d->idf_ver, board::panelName());
}

static void cmd_help(const char* arg);   // 定义在表之后（它要遍历这张表）

static const WordCmd WORD_CMDS[] = {
    {"open", "open [页面]    打开页面：wdisp（主页面） settings wifi pairs；不带参数报记录的当前页、屏幕亮没亮", cmd_open},
    {"wd",   "wd [gaps|kick|check|px <x> <y>|exit|pause|start|page|touch <x> <y>|touch up|tpad [on|off]|tpad accel [0~3]|vol [0~100]|slide <0~100>|abuf [毫秒]|audiotest [赫兹] [l|r]|listen <赫兹> [毫秒]|pop <毫秒> [sil|tone|stop|pa1|pa0@毫秒]…|selftest|pw <密码>|pwcancel|box|pairs|forget <序号>|forget all] 无线显示：状态 / 这次连接相邻两帧上屏的间隔 / "
             "断开当前连接 / 核对测试卡四角的像素 / 读画面上一点的像素 / 模拟退出手势 / 暂停（不再接受连接）/ 点「开始显示」/ 主页面（和设置页）现在的样子 / 模拟一根手指按在横屏画面的 (x, y) / 抬起 / 触摸板模式开关（落盘）/ 触摸板加速的档位（落盘）/ 音量（落盘）/ 把页面上的音量滑块拖到某处再松手 / 声音的起播缓冲（不落盘）/ 扬声器自检（放正弦波、麦克风录回来）/ 麦克风量某个频率有多响 / 量爆音（麦克风录一段、当中按时刻做事）/ 密码学自检 / 往配对密码框里输密码并确定 / 点密码框的取消 / "
             "密码框现在的样子 / 配对过的电脑 / 移除一台 / 全部移除", cmd_wd},
    {"kbd",  "kbd [key <行> <列> <down|up> | log on|off]  键盘模块：状态 / 注入一个按键事件 / 每份报文打日志", cmd_kbd},
    {"ext5v", "ext5v [on|off] 外接 5V（Grove 口）临时开关（不落盘）/ 查状态", cmd_ext5v},
    {"hosted", "hosted [ackhold <微秒>]  WiFi-SDIO 收发缓冲用量和丢包计数；ackhold = TCP 纯 ACK 最多暂存多久（0 = 不合并）", cmd_hosted},
    {"netrx", "netrx tcp <IP> <端口> [秒] [限速KB/秒] | netrx udp <端口> [秒]  收包自测（发送端 tools/dispbench/nettx.py）", cmd_netrx},
    {"wifips", "wifips [on|off] WiFi 省电临时开关 / 查状态", cmd_wifips},
    {"disp", "disp bench|awake|accel|pipe|dbuf|mem|lvprof|maskram|hog|dmahog|lpclaim|extmem  显示管线实验（见 cmd_disp 注释）", cmd_disp},
    {"ver",  "ver            固件版本（提交号、编译时间）、IDF 版本、面板型号", cmd_ver},
    {"bench", "bench <a~g>   开滚动基准页的某一个变体（'(' 是挨个轮）；配合 prof on|off、'%' 量滚动加速", cmd_bench},
    {"prof", "prof on|off    帧探针开 / 关（同 '#'，但不是切换）；开时重新统计滚动加速的命中，关时打印这一段的合计", cmd_prof},
    {"scroff", "scroff [秒]   息屏时间（落盘；0 = 不息屏）/ 查现在的值", cmd_scroff},
    {"tap",  "tap <x> <y>    点一下屏幕上的 (x, y)：给这一点上最上面的可点对象发 CLICKED", cmd_tap},
    {"press", "press <x> <y> [毫秒]  一根手指按在 (x, y) 上、过一阵抬起：经输入设备走，和真手指同一条路（拨钮、分段按钮、软键盘都认）", cmd_press},
    {"help", "help           列出多字符命令",             cmd_help},
};

static void cmd_help(const char* /*arg*/)
{
    ESP_LOGW(TAG, "多字符命令（回车结束，大小写不敏感）：");
    for (const auto& c : WORD_CMDS) ESP_LOGW(TAG, "  %s", c.usage);
    ESP_LOGW(TAG, "单字符命令仍照旧（也要回车），见启动时打印的那一行");
}

// 返回 false = 没这条命令（已提示用户）
static bool dispatch_word(char* line)
{
    const char* arg = "";
    char* sp = strpbrk(line, " \t");
    if (sp) {
        *sp++ = '\0';
        while (*sp == ' ' || *sp == '\t') sp++;
        arg = sp;
    }
    for (const auto& c : WORD_CMDS)
        if (strcasecmp(line, c.name) == 0) { c.fn(arg); return true; }
    ESP_LOGW(TAG, "未知命令 \"%s\" —— 输入 help 看多字符命令，单字符命令见启动那行", line);
    return false;
}

static void task(void*)
{
    usb_serial_jtag_driver_config_t cfg = {
        .tx_buffer_size = 256,
        .rx_buffer_size = 128,
    };
    if (usb_serial_jtag_driver_install(&cfg) != ESP_OK) {
        ESP_LOGW(TAG, "usb_serial_jtag driver install failed");
        vTaskDelete(nullptr);
        return;
    }
    ESP_LOGI(TAG, "debug console ready（⚠️命令按回车执行）：w=WiFi状态 ?=WiFi扫描 +=WiFi开关 h=主页面 o=息屏/唤醒 m=堆诊断 6=查C6固件版本 F=刷C6固件(需'!'确认) #=帧探针 %%=自动滚动 (=滚动基准页 ^=渲染微基准 _=WiFi页 `=限充现场 }=运行时统计");
    ESP_LOGI(TAG, "多字符命令输入 help 查看（如 wd / open wdisp）—— 新命令一律加在那边");

    bool c6_ota_armed = false;   // 'F' 已输入，等待 '!' 二次确认
    char line[96];               // 行缓冲：命令按回车才执行。最长的一条是 wd pw 加 64 个字符的密码
    int  len = 0;
    bool too_long = false;
    for (;;) {
        uint8_t ch = 0;
        int n = usb_serial_jtag_read_bytes(&ch, 1, pdMS_TO_TICKS(500));
        if (n != 1) continue;

        // \r 和 \n 都当行尾：不同终端发的不一样，发 \r\n 的那第二个字符会落成
        // 空行被下面忽略掉，不会误触发命令。
        if (ch != '\n' && ch != '\r') {
            if (len < (int)sizeof(line) - 1) line[len++] = (char)ch;
            else                             too_long = true;   // 超长：整行作废，别截断后当成别的命令执行
            continue;
        }
        line[len] = '\0';
        len = 0;
        if (too_long) {
            too_long = false;
            ESP_LOGW(TAG, "命令过长（上限 %d 字符），整行已丢弃", (int)sizeof(line) - 1);
            continue;
        }

        // 去首尾空白（手敲难免多打空格；也顺手吃掉 \r\n 里落单的那个）
        char* s = line;
        while (*s == ' ' || *s == '\t') s++;
        char* e = s + strlen(s);
        while (e > s && (e[-1] == ' ' || e[-1] == '\t')) *--e = '\0';
        if (!*s) continue;                     // 空行

        // 刷 C6 是全场唯一不可逆的操作（刷坏了只能靠 Downloader 配件救砖），
        // 故要求二次确认，绝不让一次误输入把协处理器刷掉。
        if (c6_ota_armed) {
            c6_ota_armed = false;
            if (strcmp(s, "!") == 0) {
                if (!netw::submit(c6_ota_job, nullptr))
                    ESP_LOGE(TAG, "C6 OTA: 作业队列满/未启动，未执行");
            } else {
                ESP_LOGW(TAG, "C6 OTA: 已取消");
            }
            continue;
        }

        if (s[1]) { dispatch_word(s); continue; }   // 两个字符以上 → 多字符命令表

        uint8_t c = (uint8_t)s[0];                  // 单字符 → 沿用下面这套原有命令
        switch (c) {
            case 'w': case 'W': {
                // 纯状态查询。⚠️ 别在这里顺手 scan_start()：连接中扫描会干扰关联/DHCP，
                // 之前"查状态即扫描"的副作用把诊断现场都搅浑了。扫描用 '?'。
                wifi::Status ws = wifi::status();
                ESP_LOGW(TAG, "CMD: wifi state=%d ready=%d ssid=%s ip=%s",
                         (int)ws.state, ws.ready, ws.ssid[0] ? ws.ssid : "-",
                         ws.ip[0] ? ws.ip : "-");
                // "关联上但拿不到 IP"专项：radio MAC(esp_wifi=C6) vs netif MAC 必须一致，
                // 不一致则 DHCP OFFER 被射频按 MAC 过滤丢掉；顺带打 DHCP 客户端状态。
                uint8_t wm[6] = {}, nm[6] = {};
                esp_err_t we = esp_wifi_get_mac(WIFI_IF_STA, wm);
                esp_netif_t* nif = esp_netif_get_handle_from_ifkey("WIFI_STA_DEF");
                esp_netif_dhcp_status_t dh = ESP_NETIF_DHCP_INIT;
                esp_netif_ip_info_t ipi = {};
                if (nif) {
                    esp_netif_get_mac(nif, nm);
                    esp_netif_dhcpc_get_status(nif, &dh);
                    esp_netif_get_ip_info(nif, &ipi);
                }
                ESP_LOGW(TAG, "CMD: wifi mac=%02x:%02x:%02x:%02x:%02x:%02x(%s) netif=%02x:%02x:%02x:%02x:%02x:%02x dhcpc=%d ip=" IPSTR " gw=" IPSTR,
                         wm[0], wm[1], wm[2], wm[3], wm[4], wm[5], esp_err_to_name(we),
                         nm[0], nm[1], nm[2], nm[3], nm[4], nm[5], (int)dh,
                         IP2STR(&ipi.ip), IP2STR(&ipi.gw));
                // 上一次扫描（'?' 触发）留下的结果：只读缓存，不发起扫描。
                // static：调试台任务栈不宽，16 条记录不放栈上（命令串行执行，static 安全）。
                static wifi::ApInfo aps[16];
                int an = wifi::scan_results(aps, 16);
                ESP_LOGW(TAG, "CMD: wifi 扫描结果 %d 条%s", an, wifi::scan_busy() ? "（扫描中）" : "");
                for (int i = 0; i < an; i++)
                    ESP_LOGW(TAG, "CMD:   %-32s rssi=%d %s", aps[i].ssid, aps[i].rssi,
                             aps[i].secure ? "加密" : "开放");
                break;
            }
            case '?':       // WiFi 扫描（原来是 'w' 的副作用，拆出来单独触发）
                ESP_LOGW(TAG, "CMD: wifi scan_start=%d", wifi::scan_start());
                break;
            case '+':       // WiFi 总开关切换
                wifi::set_enabled(!wifi::enabled());
                ESP_LOGW(TAG, "CMD: wifi set_enabled(%d)", wifi::enabled());
                break;
            case '_':       // 打开 WiFi 二级页
                ESP_LOGW(TAG, "CMD: open wifi page");
                ui_async([](void*){ ui_open_wifi(); });
                break;
            case 'h': case 'H':
                ESP_LOGW(TAG, "CMD: 回主页面");
                ui_async([](void*){ ui_open_main(); });
                break;
            case 'o': case 'O':     // 息屏/唤醒切换（远程测试息屏路径；黑屏页有双击回调，命令等效）
                ESP_LOGW(TAG, "CMD: screen %s", scrmgr_is_off() ? "wake" : "off");
                if (scrmgr_is_off()) {
                    ui_async([](void*){ scrmgr_wake(); });
                } else {
                    // scrmgr_request_off 内部是 lv_async_call → 非 LVGL 任务调用必须持显示锁（军规）
                    bsp_display_lock(0);
                    scrmgr_request_off();
                    bsp_display_unlock();
                }
                break;
            case 'm': case 'M':
                ESP_LOGW(TAG, "CMD: heap  free_int=%u dma_largest=%u free_psram=%u 堆结构检查=%s",
                         (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),
                         (unsigned)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA),
                         (unsigned)heap_caps_get_free_size(MALLOC_CAP_SPIRAM),
                         heap_caps_check_integrity_all(true) ? "通过" : "有损坏");
                break;
            case '6': {   // 查协处理器（C6）固件版本：刷机前后对比的铁证
                esp_hosted_coprocessor_fwver_t v = {};
                esp_err_t err = esp_hosted_get_coprocessor_fwversion(&v);
                if (err == ESP_OK)
                    ESP_LOGW(TAG, "C6 slave fw = %u.%u.%u",
                             (unsigned)v.major1, (unsigned)v.minor1, (unsigned)v.patch1);
                else
                    ESP_LOGE(TAG, "C6 版本查询失败: %s", esp_err_to_name(err));
                break;
            }
            case '#':     // 帧探针开关（切换）：逐帧打印 脏区/渲染耗时/帧间隔/PPA旋转耗时；开时清零滚动加速的命中统计，关时打印这一段的合计
                s_prof_req = !s_prof_req;
                ui_async(prof_apply);
                break;
            case '%':     // 自动滚动当前页第一个可滚动容器（配合 '#' 远程量滚动性能；WiFi 页有，主页面、设置页一屏放完、不滚）
                ui_async([](void*){ ui_dbg_autoscroll(); });
                break;
            case '(':     // 合成滚动基准页（循环 A~G 变体，配合 '#' '%' 用）
                ui_async([](void*){ ui_dbg_benchpage(-1); });
                break;
            case '^':     // 渲染管线微基准：带宽/旋转方案对比（跑完整屏重绘恢复画面）
                ui_async([](void*){
                    lvgl_port_dbg_render_bench(lv_display_get_default());
                    lv_obj_invalidate(lv_screen_active());
                });
                break;
            case '`':     // 限充全链路现场：设置值/状态机/软件期望/CHG_EN 硬件回读/实时电参
                board::chargeDbgDump();
                break;
            case '}': {   // FreeRTOS 运行时统计（待机功耗诊断：看谁在耗 CPU）
                char* buf = (char*)heap_caps_malloc(6144, MALLOC_CAP_SPIRAM);
                if (buf) {
                    vTaskGetRunTimeStats(buf);
                    printf("==== run-time stats (task, ticks, %%) ====\n%s====\n", buf);
                    free(buf);
                }
                break;
            }
            case 'f': case 'F':   // 给 C6 刷 firmware/c6_slave 编出的 slave 固件（不可逆，需 '!' 确认）
                c6_ota_armed = true;
                ESP_LOGW(TAG, "⚠ 即将给 C6 刷 slave 固件（%s），此操作不可逆", C6_OTA_URL);
                ESP_LOGW(TAG, "  按 '!' 确认执行，按任意其它键取消");
                break;
            default:
                break;
        }
    }
}

void start()
{
    // 栈走 PSRAM：腾内部 DMA RAM 给 esp-hosted SDIO
    xTaskCreatePinnedToCoreWithCaps(task, "dbgcon", 8192, nullptr, 3, nullptr, 0, MALLOC_CAP_SPIRAM);
}

}  // namespace dbgcon
