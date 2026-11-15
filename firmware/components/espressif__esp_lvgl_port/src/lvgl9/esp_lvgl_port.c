/*
 * SPDX-FileCopyrightText: 2024-2025 Espressif Systems (Shanghai) CO LTD
 *
 * SPDX-License-Identifier: Apache-2.0
 */

#include <string.h>
#include "esp_system.h"
#include "esp_log.h"
#include "esp_err.h"
#include "esp_check.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/portmacro.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "freertos/event_groups.h"
#include "esp_lvgl_port.h"
#include "esp_lvgl_port_priv.h"
#include "lvgl.h"

static const char *TAG = "LVGL";

#define ESP_LVGL_PORT_TASK_MUX_DELAY_MS 10000

/*******************************************************************************
 * Types definitions
 *******************************************************************************/

typedef struct lvgl_port_ctx_s {
    TaskHandle_t lvgl_task;
    SemaphoreHandle_t lvgl_mux;
    SemaphoreHandle_t timer_mux;
    EventGroupHandle_t lvgl_events;
    SemaphoreHandle_t task_init_mux;
    esp_timer_handle_t tick_timer;
    bool running;
    volatile bool stopped;      /* 【本地修改】lvgl_port_stop() 之后为真：LVGL 的定时器停着 */
    int task_max_sleep_ms;
    int timer_period_ms;
} lvgl_port_ctx_t;

/*******************************************************************************
 * Local variables
 *******************************************************************************/
static lvgl_port_ctx_t lvgl_port_ctx;

/*******************************************************************************
 * Function definitions
 *******************************************************************************/
static void lvgl_port_task(void *arg);
static esp_err_t lvgl_port_tick_init(void);
static void lvgl_port_task_deinit(void);

/*******************************************************************************
 * Public API functions
 *******************************************************************************/

esp_err_t lvgl_port_init(const lvgl_port_cfg_t *cfg)
{
    esp_err_t ret = ESP_OK;
    ESP_GOTO_ON_FALSE(cfg, ESP_ERR_INVALID_ARG, err, TAG, "invalid argument");
    ESP_GOTO_ON_FALSE(cfg->task_affinity < (configNUM_CORES), ESP_ERR_INVALID_ARG, err, TAG,
                      "Bad core number for task! Maximum core number is %d", (configNUM_CORES - 1));

    memset(&lvgl_port_ctx, 0, sizeof(lvgl_port_ctx));

    /* Tick init */
    lvgl_port_ctx.timer_period_ms = cfg->timer_period_ms;
    /* Create task */
    lvgl_port_ctx.task_max_sleep_ms = cfg->task_max_sleep_ms;
    if (lvgl_port_ctx.task_max_sleep_ms == 0) {
        lvgl_port_ctx.task_max_sleep_ms = 500;
    }
    /* Timer semaphore */
    lvgl_port_ctx.timer_mux = xSemaphoreCreateMutex();
    ESP_GOTO_ON_FALSE(lvgl_port_ctx.timer_mux, ESP_ERR_NO_MEM, err, TAG, "Create timer mutex fail!");
    /* LVGL semaphore */
    lvgl_port_ctx.lvgl_mux = xSemaphoreCreateRecursiveMutex();
    ESP_GOTO_ON_FALSE(lvgl_port_ctx.lvgl_mux, ESP_ERR_NO_MEM, err, TAG, "Create LVGL mutex fail!");
    /* Task init semaphore */
    lvgl_port_ctx.task_init_mux = xSemaphoreCreateMutex();
    ESP_GOTO_ON_FALSE(lvgl_port_ctx.task_init_mux, ESP_ERR_NO_MEM, err, TAG, "Create LVGL task sem fail!");
    /* Task queue */
    lvgl_port_ctx.lvgl_events = xEventGroupCreate();
    ESP_GOTO_ON_FALSE(lvgl_port_ctx.lvgl_events, ESP_ERR_NO_MEM, err, TAG, "Create LVGL Event Group fail!");

    BaseType_t res;
    if (cfg->task_affinity < 0) {
        res = xTaskCreate(lvgl_port_task, "taskLVGL", cfg->task_stack, xTaskGetCurrentTaskHandle(), cfg->task_priority,
                          &lvgl_port_ctx.lvgl_task);
    } else {
        res = xTaskCreatePinnedToCore(lvgl_port_task, "taskLVGL", cfg->task_stack, xTaskGetCurrentTaskHandle(),
                                      cfg->task_priority, &lvgl_port_ctx.lvgl_task, cfg->task_affinity);
    }
    ESP_GOTO_ON_FALSE(res == pdPASS, ESP_FAIL, err, TAG, "Create LVGL task fail!");

    // Wait until taskLVGL starts
    if (ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(5000)) == 0) {
        ret = ESP_ERR_TIMEOUT;
    }

err:
    if (ret != ESP_OK) {
        lvgl_port_deinit();
    }

    return ret;
}

/* 【本地修改】停 / 恢复 LVGL 的全部定时器（屏幕刷新、输入设备读取、动画、lv_async 都靠它们）。
 * 原实现只在有 tick 定时器时才动手；本工程的 tick 改成回调以后没有这个定时器，两个函数成了空操作。
 * 现在不管有没有 tick 定时器都生效，无线显示接管屏幕时用。
 * 要拿着 LVGL 锁调：这样 stop 返回时 lv_timer_handler 一定不在半路上，之后也不会再渲染。 */
esp_err_t lvgl_port_resume(void)
{
    esp_err_t ret = ESP_OK;

    lv_timer_enable(true);
    lvgl_port_ctx.stopped = false;
    if (lvgl_port_ctx.tick_timer != NULL) {
        ret = esp_timer_start_periodic(lvgl_port_ctx.tick_timer, lvgl_port_ctx.timer_period_ms * 1000);
    }
    /* LVGL 任务停着的时候睡得很长，叫醒它马上跑一轮 */
    lvgl_port_task_wake(LVGL_PORT_EVENT_USER, NULL);

    return ret;
}

esp_err_t lvgl_port_stop(void)
{
    esp_err_t ret = ESP_OK;

    lv_timer_enable(false);
    lvgl_port_ctx.stopped = true;
    if (lvgl_port_ctx.tick_timer != NULL) {
        ret = esp_timer_stop(lvgl_port_ctx.tick_timer);
    }

    return ret;
}

esp_err_t lvgl_port_deinit(void)
{
    /* Stop and delete timer */
    if (lvgl_port_ctx.tick_timer != NULL) {
        esp_timer_stop(lvgl_port_ctx.tick_timer);
        esp_timer_delete(lvgl_port_ctx.tick_timer);
        lvgl_port_ctx.tick_timer = NULL;
    }

    /* Stop running task */
    if (lvgl_port_ctx.running) {
        lvgl_port_ctx.running = false;
    }

    /* Wait for stop task */
    if (xSemaphoreTake(lvgl_port_ctx.task_init_mux, pdMS_TO_TICKS(ESP_LVGL_PORT_TASK_MUX_DELAY_MS)) != pdTRUE) {
        ESP_LOGE(TAG, "Failed to stop LVGL task");
        return ESP_ERR_TIMEOUT;
    }
    ESP_LOGI(TAG, "Stopped LVGL task");

    lvgl_port_task_deinit();

    return ESP_OK;
}

bool lvgl_port_lock(uint32_t timeout_ms)
{
    assert(lvgl_port_ctx.lvgl_mux && "lvgl_port_init must be called first");

    const TickType_t timeout_ticks = (timeout_ms == 0) ? portMAX_DELAY : pdMS_TO_TICKS(timeout_ms);
    return xSemaphoreTakeRecursive(lvgl_port_ctx.lvgl_mux, timeout_ticks) == pdTRUE;
}

void lvgl_port_unlock(void)
{
    assert(lvgl_port_ctx.lvgl_mux && "lvgl_port_init must be called first");
    xSemaphoreGiveRecursive(lvgl_port_ctx.lvgl_mux);
}

esp_err_t lvgl_port_task_wake(lvgl_port_event_type_t event, void *param)
{
    EventBits_t bits = 0;
    if (!lvgl_port_ctx.lvgl_events) {
        return ESP_ERR_INVALID_STATE;
    }

    /* Get unprocessed bits */
    if (xPortInIsrContext() == pdTRUE) {
        bits = xEventGroupGetBitsFromISR(lvgl_port_ctx.lvgl_events);
    } else {
        bits = xEventGroupGetBits(lvgl_port_ctx.lvgl_events);
    }

    /* Set event */
    bits |= event;

    /* Save */
    if (xPortInIsrContext() == pdTRUE) {
        BaseType_t xHigherPriorityTaskWoken = pdFALSE;
        xEventGroupSetBitsFromISR(lvgl_port_ctx.lvgl_events, bits, &xHigherPriorityTaskWoken);
        if (xHigherPriorityTaskWoken) {
            portYIELD_FROM_ISR();
        }
    } else {
        xEventGroupSetBits(lvgl_port_ctx.lvgl_events, bits);
    }

    return ESP_OK;
}

IRAM_ATTR bool lvgl_port_task_notify(uint32_t value)
{
    BaseType_t need_yield = pdFALSE;

    // Notify LVGL task
    if (xPortInIsrContext() == pdTRUE) {
        xTaskNotifyFromISR(lvgl_port_ctx.lvgl_task, value, eNoAction, &need_yield);
    } else {
        xTaskNotify(lvgl_port_ctx.lvgl_task, value, eNoAction);
    }

    return (need_yield == pdTRUE);
}

/*******************************************************************************
 * Private functions
 *******************************************************************************/

static void lvgl_port_task(void *arg)
{
    TaskHandle_t task_to_notify = (TaskHandle_t)arg;
    EventBits_t events          = 0;
    uint32_t task_delay_ms      = 0;
    lv_indev_t *indev           = NULL;

    /* Take the task semaphore */
    if (xSemaphoreTake(lvgl_port_ctx.task_init_mux, 0) != pdTRUE) {
        ESP_LOGE(TAG, "Failed to take LVGL task sem");
        lvgl_port_task_deinit();
        vTaskDelete(NULL);
    }

    /* LVGL init */
    lv_init();
    /* LVGL is initialized, notify lvgl_port_init() function about it */
    xTaskNotifyGive(task_to_notify);
    /* Tick init */
    lvgl_port_tick_init();

    ESP_LOGI(TAG, "Starting LVGL task");
    lvgl_port_ctx.running = true;
    while (lvgl_port_ctx.running) {
        /* Wait for queue or timeout (sleep task) */
        TickType_t wait = (pdMS_TO_TICKS(task_delay_ms) >= 1 ? pdMS_TO_TICKS(task_delay_ms) : 1);
        events          = xEventGroupWaitBits(lvgl_port_ctx.lvgl_events, 0xFF, pdTRUE, pdFALSE, wait);

        if (lv_display_get_default() && lvgl_port_lock(0)) {
            /* Call read input devices
             * 【本地修改】停着的时候不读：屏幕这时不归 LVGL，触摸也不该落到它的界面上 */
            if ((events & LVGL_PORT_EVENT_TOUCH) && !lvgl_port_ctx.stopped) {
                xSemaphoreTake(lvgl_port_ctx.timer_mux, portMAX_DELAY);
                indev = lv_indev_get_next(NULL);
                while (indev != NULL) {
                    lv_indev_read(indev);
                    indev = lv_indev_get_next(indev);
                }
                xSemaphoreGive(lvgl_port_ctx.timer_mux);
            }

            /* Handle LVGL */
            task_delay_ms = lv_timer_handler();
            lvgl_port_unlock();
        } else {
            task_delay_ms = 1; /*Keep trying*/
        }

        if (task_delay_ms == LV_NO_TIMER_READY) {
            task_delay_ms = lvgl_port_ctx.task_max_sleep_ms;
        }
        /* 【本地修改】定时器停着时 lv_timer_handler 直接返回 1，照它睡的话这个任务每 2 ms 空转一圈。
         * 改成睡到上限，lvgl_port_resume() 会发事件把它叫醒。 */
        if (lvgl_port_ctx.stopped) {
            task_delay_ms = lvgl_port_ctx.task_max_sleep_ms;
        }

        /* Minimal dealy for the task. When there is too much events, it takes time for other tasks and interrupts. */
        vTaskDelay(1);
    }

    /* Give semaphore back */
    xSemaphoreGive(lvgl_port_ctx.task_init_mux);

    /* Close task */
    vTaskDelete(NULL);
}

static void lvgl_port_task_deinit(void)
{
    if (lvgl_port_ctx.timer_mux) {
        vSemaphoreDelete(lvgl_port_ctx.timer_mux);
    }
    if (lvgl_port_ctx.lvgl_mux) {
        vSemaphoreDelete(lvgl_port_ctx.lvgl_mux);
    }
    if (lvgl_port_ctx.task_init_mux) {
        vSemaphoreDelete(lvgl_port_ctx.task_init_mux);
    }
    if (lvgl_port_ctx.lvgl_events) {
        vEventGroupDelete(lvgl_port_ctx.lvgl_events);
    }
    memset(&lvgl_port_ctx, 0, sizeof(lvgl_port_ctx));
#if LV_ENABLE_GC || !LV_MEM_CUSTOM
    /* Deinitialize LVGL */
    lv_deinit();
#endif
}

/* 【本地修改·功耗】tick 改为按需回调（lv_tick_set_cb），彻底删除 5ms 周期 esp_timer：
 * 原实现 200Hz 常年唤醒 CPU（息屏也不停），是 tickless idle 下最大的软件唤醒源。
 * LVGL9 原生支持 tick 回调：需要时间时才读 esp_timer 计数器，零周期唤醒、精度反而更高。
 * lvgl_port_ctx.tick_timer 保持 NULL，deinit 对 NULL 安全。
 * lvgl_port_stop/resume 不靠这个定时器，本工程改成了停 / 恢复 LVGL 任务的刷新（无线显示在用）。 */
static uint32_t lvgl_port_tick_get_cb(void)
{
    return (uint32_t)(esp_timer_get_time() / 1000ULL);   // 32 位回绕由 LVGL 差值运算自处理
}

static esp_err_t lvgl_port_tick_init(void)
{
    lv_tick_set_cb(lvgl_port_tick_get_cb);
    return ESP_OK;
}
