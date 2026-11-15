// Tab5 键盘模块驱动，说明见 tab5_kbd.hpp
//
// 省电：键盘接着、没人按键时任务一直等 INT（低有效），不轮询；另有 2 秒一次的兜底（INT 没来也能读到；
// 发现拔掉了）。每一轮顺带核对键盘是不是拔下又插上过。读不通时 50 毫秒后再试。没接键盘时 3 秒探测一次。
#include "tab5_kbd.hpp"
#include "kbd_keymap.hpp"

#include <atomic>
#include <cstring>
#include <driver/gpio.h>
#include <driver/i2c_master.h>
#include <esp_log.h>
#include <freertos/FreeRTOS.h>
#include <freertos/idf_additions.h>      // xTaskCreatePinnedToCoreWithCaps
#include <freertos/queue.h>
#include <freertos/semphr.h>
#include <freertos/task.h>

namespace kbd {

static const char* TAG = "kbd";

constexpr uint8_t    REG_INT_CFG = 0x00, REG_INT_STA = 0x01, REG_EVENT_NUM = 0x02, REG_BRIGHTNESS = 0x03;
constexpr uint8_t    REG_MODE = 0x10, REG_RGB_MODE = 0x11, REG_KEY_EVENT = 0x20, REG_RGB = 0x60, REG_FW = 0xFE;
constexpr uint8_t    KB_ADDR = 0x6D, EVENT_EMPTY = 0xFF;
constexpr gpio_num_t KB_INT_GPIO = GPIO_NUM_50;
constexpr uint8_t    LED_CAPS = 0x02;
constexpr uint8_t    INT_CFG_NORMAL = 0x01;      // 只开普通模式的中断（键盘上电后的默认值是 0x07，三种模式都开）
constexpr uint8_t    INT_CFG_MASK = 0x07;        // 中断配置寄存器里有定义的位（高 5 位保留）
constexpr int        MAX_ERRS = 3;               // 连着这么多次读不通算拔掉了
constexpr uint32_t   IDLE_MS = 2000, PROBE_MS = 3000, RETRY_MS = 50;     // 接着时的兜底间隔 / 没接时的探测间隔 / 读不通后多久再试

static i2c_master_bus_handle_t s_bus = nullptr;
static i2c_master_dev_handle_t s_dev = nullptr;
static QueueHandle_t           s_out = nullptr;      // 给 listen 的一方的报文
static QueueHandle_t           s_dbg = nullptr;      // 调试台塞进来的按键事件（和键盘的事件字节同一个格式）
static SemaphoreHandle_t       s_sem = nullptr;      // 中断 / 调试台 / set_leds → 叫醒任务
static std::atomic<bool>       s_listen{false}, s_log{false};
static std::atomic<uint8_t>    s_leds{0};
static kbdmap::Tracker         s_trk;                // 只有任务读写
static bool                    s_here = false;       // 只有任务写
static bool                    s_cfg_check = false;  // 只有任务读写：这块键盘的中断配置读得回来，可以靠它发现键盘重新上过电
static volatile bool           s_irq  = false;       // 中断来过（任务读了就清）
static uint8_t                 s_fw = 0, s_leds_shown = 0;
static portMUX_TYPE            s_mux = portMUX_INITIALIZER_UNLOCKED;
static Info                    s_info = {};          // info() 用的副本
static TaskHandle_t            s_task = nullptr;

static bool rd(uint8_t reg, uint8_t* data, size_t len)
{
    return i2c_master_transmit_receive(s_dev, &reg, 1, data, len, 100) == ESP_OK;
}

static bool wr(uint8_t reg, uint8_t val)
{
    uint8_t buf[2] = {reg, val};
    return i2c_master_transmit(s_dev, buf, 2, 100) == ESP_OK;
}

// 左边那颗灯：Caps 亮成暗白色，否则灭；右边那颗一直灭
static void show_leds(uint8_t leds)
{
    uint8_t v = (leds & LED_CAPS) ? 40 : 0;
    uint8_t buf[8] = {REG_RGB, v, v, v, 0, 0, 0, 0};       // 寄存器 + 两颗灯的 B G R（中间隔一个字节）
    if (i2c_master_transmit(s_dev, buf, sizeof buf, 100) == ESP_OK) s_leds_shown = leds;      // 没写成的话下一轮再写
}

// 更新 info() 用的副本。rep 是空 = 按键状态没变
static void sync_info(const uint8_t* rep)
{
    portENTER_CRITICAL(&s_mux);
    s_info.present = s_here;
    s_info.fw      = s_fw;
    if (rep) memcpy(s_info.now.b, rep, 8);
    portEXIT_CRITICAL(&s_mux);
}

// 按键状态变了：出一份报文
static void publish(const uint8_t rep[8])
{
    Report r;
    memcpy(r.b, rep, 8);
    sync_info(rep);
    if (s_log.load())
        ESP_LOGW(TAG, "报文 %02x %02x %02x %02x %02x %02x %02x %02x", rep[0], rep[1], rep[2], rep[3], rep[4], rep[5], rep[6], rep[7]);
    if (!s_listen.load()) return;
    // 每份报文都是完整的按键状态。队列满了（对方一直没来取）就丢最旧的一份，最新的状态一定要送到
    if (xQueueSend(s_out, &r, 0) != pdTRUE) {
        Report old;
        xQueueReceive(s_out, &old, 0);
        xQueueSend(s_out, &r, 0);
    }
}

static void on_event(uint8_t ev)
{
    uint8_t rep[2][8];      // 一个事件出 0~2 份报文（两份的情况见 kbd_keymap.hpp 开头）
    int     n = s_trk.on_key((ev >> 4) & 0x07, ev & 0x0F, (ev & 0x80) != 0, rep);
    for (int i = 0; i < n; i++) publish(rep[i]);
}

static void set_here(bool here)
{
    s_here = here;
    if (!here) s_fw = 0;
    uint8_t rep[8];
    // 拔掉了：按着的键都算松开，出一份全松开的报文（本来就没有键按着的话按键状态没变，不出）。接上时按键状态也没变。
    if (!here && s_trk.release_all(rep)) publish(rep);
    else sync_info(nullptr);
}

static bool attach()
{
    if (i2c_master_probe(s_bus, KB_ADDR, 100) != ESP_OK) return false;
    if (!s_dev) {
        i2c_device_config_t dc = {};
        dc.dev_addr_length = I2C_ADDR_BIT_LEN_7;
        dc.device_address  = KB_ADDR;
        dc.scl_speed_hz    = 100000;
        if (i2c_master_bus_add_device(s_bus, &dc, &s_dev) != ESP_OK) return false;
    }
    // 普通模式，只开普通模式的中断，清掉队列和中断标志
    if (!wr(REG_MODE, 0) || !wr(REG_INT_CFG, INT_CFG_NORMAL)) return false;
    // 写进去的中断配置读得回来，才拿它判断「键盘重新上过电」（见任务循环）。读回来不一样就不做这项检测：
    // 不然每次配置完都会被当成重新上过电，没完没了地重新配置，键盘反而用不了
    uint8_t cfg = 0;
    if (!rd(REG_INT_CFG, &cfg, 1)) return false;
    s_cfg_check = (cfg & INT_CFG_MASK) == INT_CFG_NORMAL;
    if (!s_cfg_check) ESP_LOGW(TAG, "中断配置读回来是 0x%02x（写进去的是 0x%02x）：不做重新上电检测", cfg, INT_CFG_NORMAL);
    wr(REG_EVENT_NUM, 0);
    wr(REG_INT_STA, 0);
    // 两颗灯默认跟着键盘自己的状态亮（接上就亮，晃眼）：改成自定义模式，由 show_leds 管
    wr(REG_RGB_MODE, 1);
    wr(REG_BRIGHTNESS, 20);
    show_leds(s_leds.load());
    rd(REG_FW, &s_fw, 1);
    ESP_LOGW(TAG, "键盘接上了（固件版本 %u）", s_fw);
    return true;
}

// 低电平触发：事件没读走之前引脚一直是低的，所以进了中断先把它关掉，任务读完再打开
static void IRAM_ATTR kbd_isr(void*)
{
    gpio_intr_disable(KB_INT_GPIO);
    s_irq = true;
    BaseType_t hp = pdFALSE;
    xSemaphoreGiveFromISR(s_sem, &hp);
    if (hp) portYIELD_FROM_ISR();
}

// 把键盘队列里的事件读完。返回读到了几个事件（中断标志置着也算 1 个）；-1 = 读不通。
static int drain_events()
{
    uint8_t sta = 0;
    if (!rd(REG_INT_STA, &sta, 1)) return -1;
    // 先清中断标志（放开 INT）再读队列。反过来的话，队列读空之后、清标志之前新来的事件会连标志一起被清掉，
    // INT 不再拉低，这个按键要等到 2 秒一次的兜底才读得到。
    // 清了标志之后读队列出错的话，剩下的事件 INT 也不会再提醒：任务里读不通后很快再读一次（RETRY_MS）。
    if (sta && !wr(REG_INT_STA, 0)) return -1;
    int n = sta ? 1 : 0;
    for (int i = 0; i < 64; i++) {      // 队列最长 32 个；读的时候又来新的也不会没完没了
        uint8_t ev = EVENT_EMPTY;
        if (!rd(REG_KEY_EVENT, &ev, 1)) return -1;
        if (ev == EVENT_EMPTY) break;
        on_event(ev);
        n++;
    }
    return n;
}

static void task(void*)
{
    // Ext.Port1 单独一条 I2C 总线。P4 只有两个高速 I2C 控制器：0 号是板上的系统总线（BSP 的 bsp_i2c_init），1 号给键盘。
    // BSP 的外接 / Grove I2C（bsp_ext_i2c_init、bsp_grove_i2c_init）也写死用 1 号，现在没有人调；
    // 以后要和键盘同时用的话没有第三个控制器可以分，得让其中一方换别的办法（LP I2C 或者软件 I2C）。
    i2c_master_bus_config_t bc = {};
    bc.i2c_port          = I2C_NUM_1;
    bc.sda_io_num        = GPIO_NUM_0;
    bc.scl_io_num        = GPIO_NUM_1;
    bc.clk_source        = I2C_CLK_SRC_DEFAULT;
    bc.glitch_ignore_cnt = 7;
    bc.flags.enable_internal_pullup = true;
    if (i2c_new_master_bus(&bc, &s_bus) != ESP_OK) {
        ESP_LOGE(TAG, "I2C 总线初始化失败");
        vTaskDelete(nullptr);
        return;
    }

    // INT 脚：低电平触发的中断，键盘接上之后才打开（没接时引脚悬空）。
    // 顺序有讲究：开机时这个脚可能已经是低的（开机前按过键；软件复位时键盘不断电，队列里的事件还在）。低电平触发的中断
    // 在处理函数加上之前就打开的话，进了中断没有人去关它，会一直重进。所以先按「不触发」配置引脚、把处理函数加上
    // （gpio_isr_handler_add 会顺带把这个脚的中断打开，这时触发方式还是「不触发」，不会进），关掉，最后才改成低电平触发。
    gpio_config_t ic = {};
    ic.pin_bit_mask = 1ULL << KB_INT_GPIO;
    ic.mode         = GPIO_MODE_INPUT;
    ic.pull_up_en   = GPIO_PULLUP_ENABLE;
    ic.intr_type    = GPIO_INTR_DISABLE;
    gpio_config(&ic);
    esp_err_t e = gpio_install_isr_service(0);      // 别的模块先装过的话返回 ESP_ERR_INVALID_STATE，照样能用
    if (e != ESP_OK && e != ESP_ERR_INVALID_STATE) ESP_LOGE(TAG, "装 GPIO 中断服务失败：%s", esp_err_to_name(e));
    gpio_isr_handler_add(KB_INT_GPIO, kbd_isr, nullptr);
    gpio_intr_disable(KB_INT_GPIO);
    gpio_set_intr_type(KB_INT_GPIO, GPIO_INTR_LOW_LEVEL);

    int errs = 0;
    for (;;) {
        const bool irq = s_irq;     // 这一轮是被键盘的中断叫醒的
        s_irq = false;
        uint32_t wait_ms = PROBE_MS;
        bool     retry   = false;       // 这一轮读不通，等 RETRY_MS 再读
        if (!s_here && attach()) {
            errs = 0;
            set_here(true);
        }
        if (s_here) {
            wait_ms = IDLE_MS;
            bool    gone = false;
            uint8_t cfg  = INT_CFG_NORMAL;
            int     n    = drain_events();
            // 每一轮都读回中断配置（1 个字节）。键盘重新上过电（拔下又很快插上）的话寄存器回到默认值，
            // 模式、灯都不是我们配的了，拔之前按着的键也等不到松开 → 当成拔掉，下一轮重新配置
            if (n >= 0 && s_cfg_check && !rd(REG_INT_CFG, &cfg, 1)) n = -1;
            if (n < 0) {
                // 读不通：很快再试，连着几次都不通算拔掉了
                wait_ms = RETRY_MS;
                retry   = true;
                if (++errs >= MAX_ERRS) {
                    ESP_LOGW(TAG, "键盘拔掉了");
                    gone = true;
                }
            } else {
                errs = 0;
                if ((cfg & INT_CFG_MASK) != INT_CFG_NORMAL) {
                    ESP_LOGW(TAG, "键盘重新上过电（中断配置变回 0x%02x），重新配置", cfg);
                    gone    = true;
                    wait_ms = RETRY_MS;
                } else if (n == 0 && irq) {
                    // 被中断叫醒却什么都没读到：INT 线被别的原因拉低了。歇一下再开中断，免得空转
                    vTaskDelay(pdMS_TO_TICKS(50));
                }
            }
            if (gone) {
                errs = 0;
                gpio_intr_disable(KB_INT_GPIO);
                set_here(false);
            }
        }
        // 调试台塞进来的事件：没接键盘也处理
        uint8_t ev;
        while (xQueueReceive(s_dbg, &ev, 0) == pdTRUE) on_event(ev);
        if (s_here) {
            uint8_t leds = s_leds.load();
            if (leds != s_leds_shown) show_leds(leds);
            // 等着重试的这一小段不开中断：INT 多半还低着，开了会马上被叫醒，几次重试就挤在一起了
            if (retry) gpio_intr_disable(KB_INT_GPIO);
            else gpio_intr_enable(KB_INT_GPIO);
        }
        xSemaphoreTake(s_sem, pdMS_TO_TICKS(wait_ms));
    }
}

void start()
{
    if (s_sem) return;
    s_out = xQueueCreate(32, sizeof(Report));
    s_dbg = xQueueCreate(16, sizeof(uint8_t));
    s_sem = xSemaphoreCreateBinary();
    // 栈放 PSRAM：常驻任务，只做 I2C，把内部内存留给 WiFi-SDIO
    if (xTaskCreatePinnedToCoreWithCaps(task, "kbd", 6144, nullptr, 3, &s_task, 0, MALLOC_CAP_SPIRAM) != pdPASS) {
        ESP_LOGE(TAG, "任务起不来，键盘用不了");
        s_task = nullptr;
    }
}

Info info()
{
    portENTER_CRITICAL(&s_mux);
    Info i = s_info;
    portEXIT_CRITICAL(&s_mux);
    i.stack_left = s_task ? (uint32_t)uxTaskGetStackHighWaterMark(s_task) : 0;
    return i;
}

void listen(bool on)
{
    if (!s_out) return;
    // 开始听之前、不听之后都把队列清掉：留在里面的报文是给上一个听的人的，不能让下一个听的人取到
    if (on) xQueueReset(s_out);
    s_listen.store(on);
    if (!on) {
        xQueueReset(s_out);
        set_leds(0);
    }
}

bool pop(Report& r)
{
    return s_out && xQueueReceive(s_out, &r, 0) == pdTRUE;
}

void set_leds(uint8_t leds)
{
    if (!s_sem || s_leds.exchange(leds) == leds) return;
    xSemaphoreGive(s_sem);
}

void dbg_key(uint8_t row, uint8_t col, bool pressed)
{
    if (!s_dbg) return;
    uint8_t ev = (uint8_t)((pressed ? 0x80 : 0) | ((row & 0x07) << 4) | (col & 0x0F));
    xQueueSend(s_dbg, &ev, 0);
    xSemaphoreGive(s_sem);
}

void dbg_log(bool on)
{
    s_log.store(on);
}

}  // namespace kbd
