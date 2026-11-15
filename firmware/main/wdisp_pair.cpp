// 见 wdisp_pair.hpp。
#include "wdisp_pair.hpp"

#include <cstdio>
#include <cstring>

#include <esp_log.h>
#include <esp_mac.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include <nvs.h>

#include "wdisp_crypto.hpp"

namespace wdpair {

static const char* TAG = "wdpair";
static const char* NS  = "wdpair";

// 存进 NVS 的一条配对（键 p0~p3）。布局变了就改 VERSION，旧的读出来当作没有
constexpr uint8_t VERSION = 1;
struct __attribute__((packed)) Stored {
    uint8_t  version;
    uint8_t  id[ID_LEN];
    uint8_t  key[KEY_LEN];
    char     name[NAME_LEN + 1];
    uint32_t seq;               // 越大越是最近连过的
};

static SemaphoreHandle_t s_mux    = nullptr;
static bool              s_loaded = false;
static bool              s_have_id = false;
static uint8_t           s_id[ID_LEN];
static bool              s_used[MAX_PEERS];
static Stored            s_peers[MAX_PEERS];
static uint32_t          s_removals = 0;    // 见 removals()

static void slot_key(int i, char out[4])
{
    out[0] = 'p';
    out[1] = (char)('0' + i);       // MAX_PEERS 是一位数
    out[2] = 0;
}

// 拿锁并保证已经从 NVS 读进来了
static void lock()
{
    static portMUX_TYPE once = portMUX_INITIALIZER_UNLOCKED;
    if (!s_mux) {
        SemaphoreHandle_t m = xSemaphoreCreateMutex();
        portENTER_CRITICAL(&once);
        if (!s_mux) {
            s_mux = m;
            m = nullptr;
        }
        portEXIT_CRITICAL(&once);
        if (m) vSemaphoreDelete(m);
    }
    xSemaphoreTake(s_mux, portMAX_DELAY);
    if (s_loaded) return;
    s_loaded = true;
    nvs_handle_t h;
    if (nvs_open(NS, NVS_READONLY, &h) != ESP_OK) return;       // 还没存过东西
    size_t n = sizeof s_id;
    s_have_id = nvs_get_blob(h, "id", s_id, &n) == ESP_OK && n == sizeof s_id;
    for (int i = 0; i < MAX_PEERS; i++) {
        char k[4];
        slot_key(i, k);
        n = sizeof(Stored);
        s_used[i] = nvs_get_blob(h, k, &s_peers[i], &n) == ESP_OK && n == sizeof(Stored) && s_peers[i].version == VERSION;
        if (s_used[i]) s_peers[i].name[NAME_LEN] = 0;
    }
    nvs_close(h);
}

static void unlock()
{
    xSemaphoreGive(s_mux);
}

// 把第 i 条写进 NVS。写不进去只记日志：内存里的照常用，重启之后这一条回到写之前的样子
static void store_slot(int i)
{
    nvs_handle_t h;
    esp_err_t    err = nvs_open(NS, NVS_READWRITE, &h);
    if (err == ESP_OK) {
        char k[4];
        slot_key(i, k);
        err = nvs_set_blob(h, k, &s_peers[i], sizeof(Stored));
        if (err == ESP_OK) err = nvs_commit(h);
        nvs_close(h);
    }
    if (err != ESP_OK) ESP_LOGE(TAG, "配对存不进 NVS（%s）", esp_err_to_name(err));
}

// 移除第 i 条：先从 NVS 里删，删成了才清内存里的。删不掉就两边都留着、返回 false——
// 只清内存的话，重启之后这台被移除的电脑又能免密连上来，而人以为它已经移除了
static bool drop_slot(int i)
{
    nvs_handle_t h;
    esp_err_t    err = nvs_open(NS, NVS_READWRITE, &h);
    if (err == ESP_OK) {
        char k[4];
        slot_key(i, k);
        err = nvs_erase_key(h, k);
        if (err == ESP_ERR_NVS_NOT_FOUND) err = ESP_OK;         // 当初就没写进去的（见 store_slot）
        if (err == ESP_OK) err = nvs_commit(h);
        nvs_close(h);
    }
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "配对从 NVS 里删不掉（%s）", esp_err_to_name(err));
        return false;
    }
    wdc::wipe(&s_peers[i], sizeof s_peers[i]);
    s_used[i] = false;
    s_removals++;
    return true;
}

static int find_slot(const uint8_t pc_id[ID_LEN])
{
    for (int i = 0; i < MAX_PEERS; i++)
        if (s_used[i] && wdc::same(s_peers[i].id, pc_id, ID_LEN)) return i;
    return -1;
}

void self_id(uint8_t out[ID_LEN])
{
    lock();
    if (!s_have_id && wdc::rand_bytes(s_id, sizeof s_id)) {
        nvs_handle_t h;
        esp_err_t    err = nvs_open(NS, NVS_READWRITE, &h);
        if (err == ESP_OK) {
            err = nvs_set_blob(h, "id", s_id, sizeof s_id);
            if (err == ESP_OK) err = nvs_commit(h);
            nvs_close(h);
        }
        // 存不进去的话这次开机先用着，下次开机会换一个编号（配对过的电脑要重新配对）
        if (err != ESP_OK) ESP_LOGE(TAG, "本机编号存不进 NVS（%s）", esp_err_to_name(err));
        s_have_id = true;
    }
    memcpy(out, s_id, ID_LEN);
    unlock();
}

void self_name(char out[NAME_LEN + 1])
{
    uint8_t mac[6] = {};
    esp_efuse_mac_get_default(mac);
    snprintf(out, NAME_LEN + 1, "Tab5-%02X%02X", mac[4], mac[5]);
}

bool find(const uint8_t pc_id[ID_LEN], uint8_t key[KEY_LEN])
{
    lock();
    int i = find_slot(pc_id);
    if (i >= 0 && key) memcpy(key, s_peers[i].key, KEY_LEN);
    unlock();
    return i >= 0;
}

uint32_t removals()
{
    lock();
    const uint32_t n = s_removals;
    unlock();
    return n;
}

bool save(const uint8_t pc_id[ID_LEN], const uint8_t key[KEY_LEN], const uint8_t raw_name[NAME_LEN], bool fresh, uint32_t since)
{
    char name[NAME_LEN + 1];
    clean_name(raw_name, name);
    lock();
    uint32_t newest = 0;
    for (int i = 0; i < MAX_PEERS; i++)
        if (s_used[i] && s_peers[i].seq > newest) newest = s_peers[i].seq;
    int  i    = find_slot(pc_id);
    bool same = i >= 0 && wdc::same(s_peers[i].key, key, KEY_LEN);
    if (since != s_removals || (!fresh && !same)) {
        unlock();
        return false;
    }
    if (same && strcmp(s_peers[i].name, name) == 0 && s_peers[i].seq == newest) {
        unlock();       // 什么都没变、它本来就是最近连过的那一台：不用写闪存（同一台电脑反复重连是最常见的情况）
        return true;
    }
    if (i < 0) {
        for (int k = 0; k < MAX_PEERS && i < 0; k++)
            if (!s_used[k]) i = k;
        if (i < 0) {                    // 满了：挤掉最久没连过的
            i = 0;
            for (int k = 1; k < MAX_PEERS; k++)
                if (s_peers[k].seq < s_peers[i].seq) i = k;
            ESP_LOGW(TAG, "配对满 %d 台了，挤掉最久没连过的「%s」", MAX_PEERS, s_peers[i].name);
        }
    }
    Stored& p = s_peers[i];
    memset(&p, 0, sizeof p);
    p.version = VERSION;
    memcpy(p.id, pc_id, ID_LEN);
    memcpy(p.key, key, KEY_LEN);
    strlcpy(p.name, name, sizeof p.name);
    p.seq     = newest + 1;
    s_used[i] = true;
    store_slot(i);
    unlock();
    return true;
}

int list(Entry out[MAX_PEERS])
{
    lock();
    int n = 0;
    for (int i = 0; i < MAX_PEERS; i++) {
        if (!s_used[i]) continue;
        int at = n++;                   // 按 seq 从大到小插进去
        while (at > 0) {
            int j = find_slot(out[at - 1].id);
            if (s_peers[j].seq > s_peers[i].seq) break;
            out[at] = out[at - 1];
            at--;
        }
        memcpy(out[at].id, s_peers[i].id, ID_LEN);
        strlcpy(out[at].name, s_peers[i].name, sizeof out[at].name);
    }
    unlock();
    return n;
}

bool remove(const uint8_t pc_id[ID_LEN])
{
    lock();
    int  i  = find_slot(pc_id);
    bool ok = i >= 0 && drop_slot(i);
    unlock();
    return ok;
}

bool remove_all()
{
    lock();
    bool ok = true;
    s_removals++;           // 一台都没有也算一次：正在配对的那一台，配出来的不能在这之后存进来
    for (int i = 0; i < MAX_PEERS; i++)
        if (s_used[i] && !drop_slot(i)) ok = false;
    unlock();
    return ok;
}

// 要换成 ? 的字符：控制字符，和看不见又能搅乱显示的那几段（零宽、换行 / 分段、方向控制），以及「替换字符」本身
static bool hidden(uint32_t c)
{
    return c < 0x20 || (c >= 0x7F && c <= 0x9F) || (c >= 0x200B && c <= 0x200F) || (c >= 0x2028 && c <= 0x202E)
           || (c >= 0x2060 && c <= 0x206F) || c == 0xFEFF || c == 0xFFFD;
}

void clean_name(const uint8_t raw[NAME_LEN], char out[NAME_LEN + 1])
{
    size_t len = 0, o = 0;
    while (len < NAME_LEN && raw[len]) len++;
    for (size_t i = 0; i < len;) {
        uint8_t  b = raw[i];
        size_t   need = b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 0;
        uint32_t c = need == 1 ? b : need == 2 ? (b & 0x1F) : need == 3 ? (b & 0x0F) : (b & 0x07);
        bool     ok = need != 0 && i + need <= len;
        for (size_t k = 1; ok && k < need; k++) {
            ok = (raw[i + k] & 0xC0) == 0x80;
            c  = (c << 6) | (raw[i + k] & 0x3F);
        }
        // 用了比必要更多的字节、代理区、超出 Unicode 范围的，都不是合法的 UTF-8
        static const uint32_t MIN[5] = {0, 0, 0x80, 0x800, 0x10000};
        if (ok && (c < MIN[need] || (c >= 0xD800 && c <= 0xDFFF) || c > 0x10FFFF)) ok = false;
        if (!ok) {              // 不合法：这一个字节换成 ?，从下一个字节接着看
            out[o++] = '?';
            i++;
        } else if (hidden(c)) {
            out[o++] = '?';
            i += need;
        } else {
            memcpy(out + o, raw + i, need);
            o += need;
            i += need;
        }
    }
    out[o] = 0;
}

}  // namespace wdpair
