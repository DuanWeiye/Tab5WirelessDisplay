// Tab5 无线显示固件 — 入口
#include <esp_log.h>
#include <esp_app_desc.h>
#include <esp_heap_caps.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/idf_additions.h>
#include <bsp/m5stack_tab5.h>
#include <esp_hosted_api.h>

#include "app.hpp"
#include "board.hpp"
#include "audio_mic.hpp"
#include "wd_audio.hpp"
#include "settings_store.hpp"
#include "net/wifi.hpp"
#include "net/worker.hpp"
#include "debug_console.hpp"
#include "utils/hwjpeg.hpp"
#include "utils/tab5_kbd.hpp"
#include "ui/ui.h"

static const char* TAG = "main";

// 内部 RAM 连续 DMA 块低频监控（WiFi-SDIO 红线预警）。WiFi-SDIO 单次要 ~2KB 连续 DMA-capable 内部 RAM，
// 看的是「最大连续块」而非总剩余。低于阈值就打警告，便于现场提前发现内存趋势、验证优化效果（零行为副作用）。
static void mem_watch_task(void*)
{
    const size_t WARN = 4096;        // 连续 DMA 块低于 4KB 预警（留一倍余量给 2KB 需求）
    size_t last_min = (size_t)-1;
    for (;;) {
        size_t largest = heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL | MALLOC_CAP_DMA);
        if (largest < WARN && largest < last_min) {     // 只在刷新更低记录时打，避免刷屏
            last_min = largest;
            ESP_LOGW("memwatch", "low contiguous DMA block: largest=%u free_int=%u",
                     (unsigned)largest, (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));
        }
        if (largest >= WARN) last_min = (size_t)-1;      // 回到安全区后复位，下次再低会重报
        vTaskDelay(pdMS_TO_TICKS(3000));
    }
}

extern "C" void app_main(void)
{
    ESP_LOGI(TAG, "Tab5 firmware %s", esp_app_get_description()->version);

    // WiFi 协处理器（C6）的传输层。官方组件原来用构造函数在调度器启动前初始化，本工程去掉了
    // 那个构造函数（原因见 components/esp_hosted/host/esp_hosted_host_init.c），改在这里显式初始化。
    // 排在最前面：和原来（构造函数）一样早于其它初始化，并且趁内部 DMA 内存还是整块的时候
    // 把它的定长收发缓冲拿到手。这一步只建队列 / 任务、初始化 SDMMC 主控，不碰 C6 本身：
    // C6 的电源由 board::init 经 IO 扩展芯片打开，复位和 SDIO 卡初始化要到 wifi::init 里的
    // esp_wifi_init 才做。
    ESP_ERROR_CHECK(esp_hosted_init());

    audiomic::init();  // 麦克风互斥（声音自检命令串行使用单例 codec，杜绝双重 open）
    wdaudio::init();   // 无线显示的声音：只建锁，缓冲和播放任务到头一次有声音来才建
    settings::load();  // nvs_flash_init 在 board::init 里，但 load 失败时用默认值，顺序无害
    board::init(settings::get().brightness);
    settings::load();  // NVS 就绪后再读一次真实值
    board::setBrightness(settings::get().brightness);
    wdaudio::set_volume(settings::get().wd_volume);
    // JPEG 硬解引擎必须趁内部 DMA 未碎片化时建（见 hwjpeg::preinit 的说明）
    if (!hwjpeg::preinit()) ESP_LOGE(TAG, "JPEG 硬解引擎建不出来：无线显示用不了");

    bsp_display_lock(0);
    ui_init();
    bsp_display_unlock();

    // 网络开机自启
    netw::start();    // 常驻网络工作任务（联网的调试作业排队在它上面跑；不自删避免 TLS 删除回调崩溃，见 worker.hpp）
    wifi::init();     // WiFi：esp-hosted → C6，自动重连保存的网络（后台任务）

    // 开始等电脑连。无线显示的服务从这里起常驻，和界面在哪一页、息没息屏无关（见 app.hpp）。排在 wifi::init 之后：要先有网络栈
    bsp_display_lock(0);
    app::start();
    bsp_display_unlock();

    kbd::start();     // 键盘模块（Tab5 Keyboard，可以随时插拔）：按键回传给无线显示的发送端
    dbgcon::start();  // USB-JTAG 调试命令（命令表见 debug_console.cpp，输入 help 查看）
    board::chargeGovernorStart();  // 充电上限维持（按设置到上限停充，电池旁路吃 C 口）

    xTaskCreatePinnedToCoreWithCaps(mem_watch_task, "memwatch", 3072, nullptr, 1, nullptr, tskNO_AFFINITY, MALLOC_CAP_SPIRAM);  // 栈走 PSRAM，腾内部 DMA RAM

    // app_main 跑在主任务上，栈只有 CONFIG_ESP_MAIN_TASK_STACK_SIZE 那么大，上面每一步初始化都在吃它。
    // 打印最低余量：往 app_main 里加东西以后，看这个数还剩多少。
    ESP_LOGI(TAG, "ui ready（主任务栈最低余量 %u 字节）", (unsigned)uxTaskGetStackHighWaterMark(nullptr));
}
