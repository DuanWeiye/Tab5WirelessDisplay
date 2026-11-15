#include "settings_store.hpp"

#include <nvs.h>
#include <esp_log.h>

static const char* TAG = "settings";
static const char* NS  = "tab5cfg";

namespace settings {

static Values s_values;

const Values& get()
{
    return s_values;
}

Values& mut()
{
    return s_values;
}

void load()
{
    nvs_handle_t h;
    if (nvs_open(NS, NVS_READONLY, &h) != ESP_OK) return;  // 首次启动用默认值
    uint8_t b;
    uint32_t s;
    if (nvs_get_u8(h, "bright", &b) == ESP_OK) s_values.brightness = b;
    if (nvs_get_u32(h, "scroff", &s) == ESP_OK) s_values.screenoff_s = s;
    if (nvs_get_u8(h, "chglim", &b) == ESP_OK) s_values.charge_limit = b;
    if (nvs_get_u8(h, "wdtpad", &b) == ESP_OK) s_values.wd_touchpad = b ? 1 : 0;
    if (nvs_get_u8(h, "wdtpgain", &b) == ESP_OK) {
        for (uint8_t g : WD_TPAD_GAINS) if (g == b) s_values.wd_tpad_gain = b;      // 不是表里的值就留着默认的
    }
    if (nvs_get_u8(h, "wdvol", &b) == ESP_OK) s_values.wd_volume = b > 100 ? 100 : b;
    if (nvs_get_u8(h, "wdpic", &b) == ESP_OK && b < WD_PIC_N) s_values.wd_pic = b;
    if (nvs_get_u8(h, "wdosd", &b) == ESP_OK) s_values.wd_osd = b ? 1 : 0;
    nvs_close(h);
    ESP_LOGI(TAG, "loaded: brightness=%u screenoff=%lus charge_limit=%u",
             s_values.brightness, (unsigned long)s_values.screenoff_s, s_values.charge_limit);
}

void save()
{
    nvs_handle_t h;
    if (nvs_open(NS, NVS_READWRITE, &h) != ESP_OK) return;
    nvs_set_u8(h, "bright", s_values.brightness);
    nvs_set_u32(h, "scroff", s_values.screenoff_s);
    nvs_set_u8(h, "chglim", s_values.charge_limit);
    nvs_set_u8(h, "wdtpad", s_values.wd_touchpad);
    nvs_set_u8(h, "wdtpgain", s_values.wd_tpad_gain);
    nvs_set_u8(h, "wdvol", s_values.wd_volume);
    nvs_set_u8(h, "wdpic", s_values.wd_pic);
    nvs_set_u8(h, "wdosd", s_values.wd_osd);
    nvs_commit(h);
    nvs_close(h);
}

}  // namespace settings
