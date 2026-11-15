// 持久网络工作任务实现
#include "worker.hpp"

#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/queue.h>
#include <freertos/idf_additions.h>  // xTaskCreatePinnedToCoreWithCaps
#include <esp_heap_caps.h>           // MALLOC_CAP_SPIRAM

namespace netw {

struct Job {
    void (*fn)(void*);
    void*  arg;
};

static QueueHandle_t s_q       = nullptr;
static bool          s_started = false;

static void worker(void*)
{
    Job j;
    for (;;) {
        if (xQueueReceive(s_q, &j, portMAX_DELAY) == pdTRUE && j.fn) {
            j.fn(j.arg);
        }
    }
}

void start()
{
    if (s_started) return;
    s_started = true;
    s_q = xQueueCreate(8, sizeof(Job));
    // 一个常驻任务，作业排队执行。现在的作业都是调试命令：收包自测（netrx）、C6 OTA（要跑几分钟，期间其它作业排队等它）。
    // 栈 20KB（够 OTA 里的 HTTP 客户端用）。钉 CPU0（LVGL 在 CPU1）。
    // 栈放 PSRAM 腾内部 RAM：worker 只跑联网作业，不写 flash、不在 cache 关闭期运行，
    // 符合 PSRAM 栈使用条件。TCB 仍在内部 RAM。
    xTaskCreatePinnedToCoreWithCaps(worker, "netw", 20480, nullptr, 3, nullptr, 0, MALLOC_CAP_SPIRAM);
}

bool submit(void (*fn)(void*), void* arg)
{
    if (!s_q) return false;
    Job j{fn, arg};
    return xQueueSend(s_q, &j, 0) == pdTRUE;
}

}  // namespace netw
