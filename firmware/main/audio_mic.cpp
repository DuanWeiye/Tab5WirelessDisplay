// 共享麦克风互斥实现（见 audio_mic.hpp 说明）。
#include "audio_mic.hpp"
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include <esp_log.h>

namespace audiomic {

static const char* TAG = "audiomic";
static StaticSemaphore_t s_mtx_buf;          // 静态分配，避免开机外的动态分配/懒初始化竞态
static SemaphoreHandle_t s_mtx = nullptr;

void init() {
    if (!s_mtx) s_mtx = xSemaphoreCreateMutexStatic(&s_mtx_buf);
}

bool acquire(uint32_t timeout_ms) {
    if (!s_mtx) { ESP_LOGE(TAG, "acquire before init"); return false; }
    if (xSemaphoreTake(s_mtx, pdMS_TO_TICKS(timeout_ms)) == pdTRUE) return true;
    ESP_LOGW(TAG, "mic busy (acquire timeout %ums)", (unsigned)timeout_ms);
    return false;
}

void release() {
    if (s_mtx) xSemaphoreGive(s_mtx);
}

}  // namespace audiomic
