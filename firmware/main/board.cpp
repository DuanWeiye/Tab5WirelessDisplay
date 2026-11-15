#include "board.hpp"

#include <cmath>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/idf_additions.h>
#include <esp_heap_caps.h>
#include <esp_log.h>
#include <esp_timer.h>
#include <esp_pm.h>
#include <nvs_flash.h>
#include <bsp/m5stack_tab5.h>
#include <bsp/display.h>
#include <lvgl.h>
#include <ina226.hpp>
#include "settings_store.hpp"

static const char* TAG = "board";

static INA226 s_ina226;

namespace board {

// 充电上限维持状态：是否因达到设定上限「并经沉降复核确认」而停充（=显示"充满"）。
// governor 仅在正式 CAPPED 时置位，readPower/UI 读。
static volatile bool s_charge_capped = false;
// 沉降复核相位：governor 到 limit 后短暂停充测静置电压期间为 true。期间电流≈0，但对外
// 仍按"充电中"显示（不显示充满、也不误判放电），避免逼近过程图标闪（见 charge_governor_task）。
static volatile bool s_settling = false;

// DFS 满速锁：持锁=360MHz，释放=空闲降到 90MHz（见 pmInit/cpuFastHold）
static esp_pm_lock_handle_t s_cpu_lock  = nullptr;
static bool                 s_fast_held = false;

// 2S 锂电放电曲线（移植自 m5power：充电 IC ~8.1V 截止，按 8100mV=100% 折算）
static int voltToPercent(float v)
{
    const float mv = v * 1000.0f;
    static const float V[] = {6800, 7000, 7200, 7400, 7600, 7800, 8000, 8100};
    static const int   P[] = {0, 10, 20, 35, 55, 75, 92, 100};
    const int N = 8;
    if (mv <= V[0]) return 0;
    if (mv >= V[N - 1]) return 100;
    for (int i = 1; i < N; i++) {
        if (mv <= V[i])
            return P[i - 1] + (int)((mv - V[i - 1]) * (P[i] - P[i - 1]) / (V[i] - V[i - 1]));
    }
    return 100;
}

void init(uint8_t brightness_pct)
{
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ESP_ERROR_CHECK(nvs_flash_init());
    }

    ESP_LOGI(TAG, "i2c + io expander init");
    bsp_i2c_init();
    i2c_master_bus_handle_t bus = bsp_i2c_get_handle();
    bsp_io_expander_pi4ioe_init(bus);

    bsp_set_charge_qc_en(true);
    vTaskDelay(pdMS_TO_TICKS(50));
    bsp_set_charge_en(true);

    ESP_LOGI(TAG, "ina226 init");
    s_ina226.begin(bus, 0x41);
    s_ina226.configure(INA226_AVERAGES_16, INA226_BUS_CONV_TIME_1100US,
                       INA226_SHUNT_CONV_TIME_1100US, INA226_MODE_SHUNT_BUS_CONT);
    s_ina226.calibrate(0.005, 8.192);

    ESP_LOGI(TAG, "display init");
    bsp_reset_tp();
    bsp_display_cfg_t cfg = {
        .lvgl_port_cfg = ESP_LVGL_PORT_INIT_CONFIG(),
        .buffer_size   = BSP_LCD_H_RES * BSP_LCD_V_RES,
        .double_buffer = true,
        .flags         = {
            .buff_dma    = true,
            .buff_spiram = true,
            // 注意：这份 M5 魔改 esp_lvgl_port 里 sw_rotate=true 的 90° 路径
            // 实际走 PPA 硬件旋转（rotate_copy_pixel）；false 反而没有旋转
            // 回退（面板不支持 swap_xy），屏幕方向会错。必须保持 true。
            .sw_rotate   = true,
        },
    };
    // LVGL 渲染任务钉到 CPU1（CPU0 留给系统与联网任务），提优先级
    cfg.lvgl_port_cfg.task_affinity = 1;
    cfg.lvgl_port_cfg.task_priority = 5;
    cfg.lvgl_port_cfg.task_stack    = 8192;
    lv_display_t* disp = bsp_display_start_with_config(&cfg);
    lv_display_set_rotation(disp, LV_DISPLAY_ROTATION_90);  // 竖屏面板 → 横屏 UI
    bsp_display_brightness_set(brightness_pct);

    pmInit();   // 配置 DFS（max=360/min=90）并持开机满速锁
    ESP_LOGI(TAG, "board init done, panel=%s", bsp_display_get_panel_ic());
}

// 电源管理：DFS 动态调频。仅调 CPU 频率；light sleep 必须关
// （红线：light sleep 会停 SDIO 通信 → esp-hosted WiFi 掉线 → sdio_rx_get_buffer）。
void pmInit()
{
    esp_pm_config_t pm = {};
    pm.max_freq_mhz       = 360;   // 有操作/渲染时满速
    pm.min_freq_mhz       = 90;    // 空闲降频（P4 合法档位：360/180/90）
    pm.light_sleep_enable = false;
    esp_err_t e = esp_pm_configure(&pm);
    ESP_LOGI(TAG, "pm configure max=360 min=90 ls=0 -> %s", esp_err_to_name(e));
    if (esp_pm_lock_create(ESP_PM_CPU_FREQ_MAX, 0, "cpu_fast", &s_cpu_lock) == ESP_OK) {
        esp_pm_lock_acquire(s_cpu_lock);   // 开机持满速：启动渲染不被降频拖慢
        s_fast_held = true;
    }
}

// 满速锁开关（幂等）：on=锁 360MHz，off=释放后空闲降到 90MHz。
void cpuFastHold(bool on)
{
    if (!s_cpu_lock || on == s_fast_held) return;
    if (on) esp_pm_lock_acquire(s_cpu_lock);
    else    esp_pm_lock_release(s_cpu_lock);
    s_fast_held = on;
}

// 屏幕亮/灭全局状态（scrmgr 写，各后台任务读；volatile 单写多读足够）
static volatile bool s_screen_on = true;
void notifyScreen(bool on) { s_screen_on = on; }
bool screenOn()            { return s_screen_on; }

PowerData readPower()
{
    PowerData d;
    // 三个寄存器有一个读失败（I2C 总线正被别的设备占着、等锁超时等），这次采样就整个作废：
    // 不折算电量、不更新下面"是否装着电池"的判定，由调用方丢弃。
    d.valid = s_ina226.readBusVoltage(d.voltage) && s_ina226.readShuntCurrent(d.current) &&
              s_ina226.readBusPower(d.watts);
    if (!d.valid) return d;
    d.percent  = voltToPercent(d.voltage);
    // 流入电池(>50mA)=充电中；沉降复核期（governor 到上限后短暂停充测静置电压、电流≈0）
    // 也按"充电中"显示，避免逼近过程闪成放电/充满（见 charge_governor_task）。
    d.charging = (d.current < -0.05f) || s_settling;

    // 无电池判定（滞回）：实测拔电池后，充电 IC 空载做"检测脉冲"，电池端电压在
    // 8.39V↔4.2V 间周期跳变（高压段约 10s），电流恒约 -0.6mA。单次采样无法区分
    // "无电池 8.4V 空载"与"满电 8.4V 电池"，但只有无电池才会周期性掉到 <5V
    // （2S 电池即便深放也不会 <5V）。故记住"最近一次见到低压的时刻"，窗口内见过低压
    // → 判为无电池；窗口（15s）需 > 跳变高压段时长，留足裕量。
    // 读失败的采样在上面已经作废，走到这里的电压都是真量到的，见到一次 <5V 就算数。
    // ⚠️ 多线程调用（UI 任务 + governor），static 状态需临界区保护。
    static const int64_t ABSENT_WIN_US = 15LL * 1000 * 1000;  // 滞回窗口 15s（可上机微调）
    static portMUX_TYPE  mux           = portMUX_INITIALIZER_UNLOCKED;
    static int64_t       s_last_low_us = -2 * ABSENT_WIN_US;   // 初始假设有电池（不溢出）
    const int64_t now = esp_timer_get_time();
    taskENTER_CRITICAL(&mux);
    if (d.voltage < 5.0f) s_last_low_us = now;
    bool present = (now - s_last_low_us) > ABSENT_WIN_US;
    taskEXIT_CRITICAL(&mux);
    d.present = present;

    // 充满：装着电池、电量满、且电流极小（<10mA）。满充后系统会缓慢放电，
    // 用此判据把"缓慢放电"显示/上报为充满，避免误判为放电。
    // 充满：达到设定上限被停充（s_charge_capped），或"不限制"时充到 100% 且电流极小。
    // 满充后系统会缓慢放电，用此判据把"已到上限/已满"显示为充满而非放电。
    d.full = d.present && (s_charge_capped || (d.percent >= 100 && fabsf(d.current) < 0.010f));
    return d;
}

// 充电使能：**每周期都重写硬件**，仅状态变化时打日志。
// 曾用"仅变化才写 I2C"去抖，但那是单点故障：一次写失败（总线被触摸/RTC 抢占超时，
// 本机已知现象）后缓存与硬件永久失配，且永不重试 → 限充从此形同虚设、充到 100%。
// 2 字节 I2C 每 3~15s 重写一次代价可忽略，还能自愈任何外因改动（扩展器复位/误写）。
static volatile int s_charge_want = -1;   // 期望状态（chargeDbgDump 用）
static void set_charge(bool en)
{
    if (s_charge_want != (int)en) {
        ESP_LOGW(TAG, "chggov: set_charge(%d)", (int)en);
        s_charge_want = (int)en;
    }
    bsp_set_charge_en(en);
}

// 充电上限维持任务（沉降确认法）：按 settings.charge_limit 控制充电开关。
// 充电时端电压被流入电流抬高（V_term = OCV + I·R_internal），voltToPercent 会把这个
// 虚高电压当成高电量、提前在"显示 limit%（真实 OCV 更低）"就停充；停充后电压回落、
// 显示掉到真实值（曾见设 95% 停充后回落到 86%，即实际只充到 ~86%）。故不在充电虚高
// 电量就定论，而是：
//   到达 limit → 先停充进入"沉降复核"→ 等电压回落 SETTLE 秒 → 用静置电量复核：
//     仍 ≥limit → 正式 cap（真达标）；<limit → 之前是虚高、没充够 → 恢复充电，过
//     MIN_CHARGE 秒再复核。如此自适应逼近"静置电量≈设定值"，不依赖未知内阻。
// 显示：沉降复核期间（停充测电压、电流≈0）对外仍按"充电中"显示，只有正式 cap 才显示"充满"——
//   故图标不会在"充满↔充电"间闪、也不会误显示放电；复核失败回充时电量百分比仍会小幅来回
//   （停充回落→显示降→回充又升），属正常逼近过程。
// power path 已验证：关充电后系统由 C 口直供、电池零放电（见 md/TAB5_BATTERY_DETECT）。
enum ChgState { CHG_CHARGING, CHG_SETTLING, CHG_CAPPED };
static volatile ChgState s_chg_st = CHG_CHARGING;   // 文件级：chargeDbgDump 要读
static const char* chg_st_name(ChgState s)
{
    return s == CHG_CHARGING ? "CHARGING" : s == CHG_SETTLING ? "SETTLING" : "CAPPED";
}

static void charge_governor_task(void*)
{
    const int     HYST          = 10;                  // 滞回带(%)：cap 后掉到 上限-HYST 才重充
    const int64_t SETTLE_US     = 10LL * 1000 * 1000;  // 停充后等电压回落再复核
    const int64_t MIN_CHARGE_US = 60LL * 1000 * 1000;  // 复核未达标后至少再充这么久才停（防高频充停）
    int64_t settle_t0 = 0;
    int64_t charge_t0 = 0;   // 上次"复核失败回充"的时刻；0=开机即视为已充够，首次到上限立即可停
    {
        const PowerData p0 = readPower();
        ESP_LOGW(TAG, "chggov: start limit=%u valid=%d present=%d pct=%d v=%.2f CHG_EN=%d",
                 settings::get().charge_limit, p0.valid, p0.present, p0.percent, p0.voltage,
                 bsp_get_charge_en());
    }
    for (;;) {
        const int       limit = settings::get().charge_limit;
        const PowerData p     = readPower();
        const int64_t   now   = esp_timer_get_time();
        const ChgState  st    = s_chg_st;

        if (!p.valid) {
            // 读数无效（I2C 读失败）：状态机这一拍不动，不拿没量到的值做判断——否则会被当成
            // 0V、电量 0%，让 CAPPED/SETTLING 误判"掉到滞回下限"而假重充，限充被击穿。
            // 循环末尾的落地重写照做（它只写状态机的期望，不依赖读数）。
        } else if (limit >= 100) {
            // 不限制：保持充电、不 cap（落地写在循环末尾统一做）
            s_charge_capped = false;
            s_settling      = false;
            s_chg_st        = CHG_CHARGING;
        } else if (!p.present) {
            // 无电池：状态冻结、不动充电开关。没电池时开关本就无意义（系统 C 口直供）；
            // 冻结还能保住 CAPPED：电池拔下再插回，不会因为中间这段"无电池"就重新开充。
        } else if (st == CHG_CHARGING) {
            // 充电中：显示到达上限（可能虚高）且距上次回充已过最小间隔 → 停充进入沉降复核。
            // charge_t0==0 单独放行：语义是"开机首次到上限立即可停"，若并进减法条件，
            // 开机头 60s 内 now 本身 <60s 会把停充卡住——board::init 又是无条件开充的，
            // 每次重启都会给已到上限的电池白充 60s（本机重启频繁，积少成多）。
            if (p.percent >= limit && (charge_t0 == 0 || now - charge_t0 >= MIN_CHARGE_US)) {
                ESP_LOGW(TAG, "chggov: 到达上限 pct=%d(v=%.2f) >= limit=%d → 停充沉降复核",
                         p.percent, p.voltage, limit);
                s_settling = true;       // 沉降期对外仍按"充电中"显示（不显示充满），到 CAPPED 才显满
                s_chg_st   = CHG_SETTLING;
                settle_t0 = now;
            }
        } else if (st == CHG_SETTLING) {
            // 已停充，等电压回落到接近静置后用静置电量复核
            if (now - settle_t0 >= SETTLE_US) {
                s_settling = false;
                if (p.percent >= limit) {
                    ESP_LOGW(TAG, "chggov: 静置复核达标 pct=%d(v=%.2f) >= %d → CAPPED",
                             p.percent, p.voltage, limit);
                    s_charge_capped = true;   // 静置仍达标 → 正式 cap（此刻才显示"充满"）
                    s_chg_st        = CHG_CAPPED;
                } else {
                    ESP_LOGW(TAG, "chggov: 静置回落 pct=%d(v=%.2f) < %d → 恢复充电（60s 保护期）",
                             p.percent, p.voltage, limit);
                    s_chg_st  = CHG_CHARGING;
                    charge_t0 = now;
                }
            }
        } else {  // CHG_CAPPED
            // 已停充维持：掉到滞回下限才重充
            if (p.percent <= limit - HYST) {
                ESP_LOGW(TAG, "chggov: 回落 pct=%d(v=%.2f) <= %d → 重充", p.percent, p.voltage, limit - HYST);
                s_charge_capped = false;
                s_chg_st  = CHG_CHARGING;
                charge_t0 = now;
            }
        }
        // 唯一落地点：每周期把期望状态重写进硬件（自愈，见 set_charge 注释）。
        // 无电池冻结期也照写：写的是"当前状态机的期望"，不会破坏任何语义。
        set_charge(s_chg_st == CHG_CHARGING);
        // 电池动力学是分钟级：息屏 15s 采样无损（SETTLE 10s/MIN_CHARGE 60s 语义不受影响），
        // 少 4/5 的 I2C 唤醒；亮屏保持 3s（顶栏的电量要跟手）。
        vTaskDelay(pdMS_TO_TICKS(screenOn() ? 3000 : 15000));
    }
}

// 调试台 '`'：限充全链路现场（设置值/状态机/软件期望/硬件回读/实时电参）。
// CHG_EN 读回与 want 不一致 = 有外因改写或 I2C 写丢失（governor 下周期会自愈重写）。
void chargeDbgDump()
{
    const PowerData p = readPower();
    ESP_LOGW(TAG, "chg: limit=%u st=%s capped=%d settling=%d want=%d CHG_EN读回=%d | "
                  "valid=%d v=%.3fV i=%.4fA pct=%d present=%d charging=%d full=%d",
             settings::get().charge_limit, chg_st_name(s_chg_st),
             (int)s_charge_capped, (int)s_settling, (int)s_charge_want, bsp_get_charge_en(),
             (int)p.valid, p.voltage, p.current, p.percent, (int)p.present, (int)p.charging, (int)p.full);
}

void chargeGovernorStart()
{
    // 栈走 PSRAM：腾内部 DMA RAM 给 esp-hosted SDIO
    xTaskCreatePinnedToCoreWithCaps(charge_governor_task, "chggov", 4096, nullptr, 2, nullptr, tskNO_AFFINITY, MALLOC_CAP_SPIRAM);
}

void setBrightness(uint8_t pct)
{
    bsp_display_brightness_set(pct);
}

const char* panelName()
{
    return bsp_display_get_panel_ic();
}

}  // namespace board
