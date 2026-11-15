// WiFi STA 管理（esp_wifi_remote → 板载 ESP32-C6）
//
// 多网络记忆版：NVS blob 存最多 MAX_SAVED 条凭据。
// 自动连接策略 = 先连"上次成功"的网络；连不上再扫描，在已保存
// 网络里挑当前信号最强的连。这样平时秒连常用网络，换地方也能自动切。
#include "wifi.hpp"
#include "../board.hpp"   // board::screenOn()：RSSI 轮询仅亮屏做

#include <cstring>
#include <cstdio>

#include <esp_log.h>
#include <esp_wifi.h>
#include <esp_event.h>
#include <esp_netif.h>
#include <esp_hosted_bufstat.h>   // esp_hosted_link_failed()
#include <nvs.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <freertos/semphr.h>

namespace wifi {

static const char* TAG = "wifi";
static const char* NVS_NS = "wifi";

// ── 状态共享 ──────────────────────────────────────────────────
static Status            s_status;
static SemaphoreHandle_t s_mtx     = nullptr;   // 只保护 s_status
static bool              s_started = false;

// ── 省电 ──────────────────────────────────────────────────────
// 连上后让从机 C6 进 modem-sleep（DTIM 间隙关射频）；有人 ps_hold 期间不省电。
// 改计数和下发设置要在同一把锁里：不然 hold 和"重新连上"撞在一起时，后下发的可能是旧状态。
static SemaphoreHandle_t s_ps_mtx   = nullptr;
static int               s_ps_holds = 0;

static void ps_apply_locked()
{
    esp_wifi_set_ps(s_ps_holds > 0 ? WIFI_PS_NONE : WIFI_PS_MIN_MODEM);
}

static void ps_change(int delta)
{
    if (!s_ps_mtx) return;      // init() 之前没有 WiFi，也就没有可设的
    xSemaphoreTake(s_ps_mtx, portMAX_DELAY);
    const bool was = s_ps_holds > 0;
    s_ps_holds += delta;
    if (s_ps_holds < 0) s_ps_holds = 0;
    if (was != (s_ps_holds > 0) || delta == 0) ps_apply_locked();
    xSemaphoreGive(s_ps_mtx);
}

void ps_hold()    { ps_change(+1); }
void ps_release() { ps_change(-1); }

// ── 记忆列表（多网络）────────────────────────────────────────
struct Cred {
    char ssid[33] = "";
    char pass[65] = "";
};
static SemaphoreHandle_t s_nets_mtx = nullptr;  // 保护 s_nets/s_count/s_active/s_lastok
static Cred s_nets[MAX_SAVED];
static int  s_count       = 0;     // 已保存条数
static int  s_active      = -1;    // 当前正连/已连的索引（-1=无）
static char s_lastok[33]  = "";    // 上次成功连接的 SSID（开机优先直连它）
static bool s_enabled     = true;
static volatile bool s_lastok_dirty = false;    // 连上后置位，让后台任务落盘（事件回调里不写 NVS）

// NVS blob：结构变更时改魔数，老 blob 会被忽略并触发迁移
static const uint32_t NETS_MAGIC = 0x57464932;  // 'WFI2'
struct NetsBlob {
    uint32_t magic;
    int32_t  count;
    char     lastok[33];
    Cred     nets[MAX_SAVED];
};

// 自动连接编排状态机
enum class Auto {
    Idle,      // 未编排：若断开且开着，从头发起一轮（先试 preferred）
    TryLast,   // 已试"上次成功/首选"，等结果；失败则转扫描
    Scanning,  // 自动扫描进行中；完成后挑最强已保存网络连
    Cooldown,  // 一整轮失败，冷却若干周期再重来（避免空连狂刷）
};
static Auto s_auto      = Auto::Idle;
static int  s_cooldown  = 0;

// 扫描结果缓存
static const int MAX_AP = 16;
static ApInfo        s_aps[MAX_AP];
static int           s_ap_count = 0;
static volatile bool s_scanning = false;

template <typename F>
static void with_status(F&& fn)
{
    xSemaphoreTake(s_mtx, portMAX_DELAY);
    fn(s_status);
    xSemaphoreGive(s_mtx);
}

static void nets_lock()   { if (s_nets_mtx) xSemaphoreTake(s_nets_mtx, portMAX_DELAY); }
static void nets_unlock() { if (s_nets_mtx) xSemaphoreGive(s_nets_mtx); }

Status status()
{
    Status copy;
    if (s_mtx) {
        xSemaphoreTake(s_mtx, portMAX_DELAY);
        copy = s_status;
        xSemaphoreGive(s_mtx);
    }
    // 无线模块故障盖过别的状态。在这里现查（只读一个标志），界面和各模块不用等后台任务下一轮。
    // 故障后本模块的各个操作照常可以调：esp_wifi_* 会立刻返回失败，不会卡住。
    if (esp_hosted_link_failed()) {
        copy.state = State::Fault;
        copy.ip[0] = '\0';
        copy.rssi  = 0;
    }
    return copy;
}

bool enabled()   { return s_enabled; }
bool scan_busy() { return s_scanning; }

// ── NVS：保存/读取记忆列表 ───────────────────────────────────
static void nets_save()
{
    NetsBlob blob = {};
    nets_lock();
    blob.magic = NETS_MAGIC;
    blob.count = s_count;
    memcpy(blob.nets, s_nets, sizeof(blob.nets));
    snprintf(blob.lastok, sizeof(blob.lastok), "%s", s_lastok);
    bool en = s_enabled;
    nets_unlock();

    nvs_handle_t h;
    if (nvs_open(NVS_NS, NVS_READWRITE, &h) != ESP_OK) return;
    nvs_set_blob(h, "nets", &blob, sizeof(blob));
    nvs_set_u8(h, "en", en ? 1 : 0);
    nvs_commit(h);
    nvs_close(h);
}

static void nets_load()
{
    nvs_handle_t h;
    if (nvs_open(NVS_NS, NVS_READONLY, &h) != ESP_OK) return;

    uint8_t en = 1;
    nvs_get_u8(h, "en", &en);
    s_enabled = en != 0;

    bool migrated = false;
    NetsBlob blob = {};
    size_t sz = sizeof(blob);
    esp_err_t r = nvs_get_blob(h, "nets", &blob, &sz);
    if (r == ESP_OK && sz == sizeof(blob) && blob.magic == NETS_MAGIC) {
        nets_lock();
        s_count = blob.count;
        if (s_count < 0) s_count = 0;
        if (s_count > MAX_SAVED) s_count = MAX_SAVED;
        memcpy(s_nets, blob.nets, sizeof(s_nets));
        snprintf(s_lastok, sizeof(s_lastok), "%s", blob.lastok);
        nets_unlock();
    } else {
        // 迁移旧的单网络键（ssid/pass）→ 记忆列表第 0 条
        char ssid[33] = {};
        char pass[65] = {};
        size_t a = sizeof(ssid), b = sizeof(pass);
        if (nvs_get_str(h, "ssid", ssid, &a) == ESP_OK && ssid[0]) {
            nvs_get_str(h, "pass", pass, &b);
            nets_lock();
            snprintf(s_nets[0].ssid, sizeof(s_nets[0].ssid), "%s", ssid);
            snprintf(s_nets[0].pass, sizeof(s_nets[0].pass), "%s", pass);
            s_count = 1;
            snprintf(s_lastok, sizeof(s_lastok), "%s", ssid);
            nets_unlock();
            migrated = true;
        }
    }
    nvs_close(h);

    // 迁移过就立刻落盘成新 blob，下次直接走 blob 分支
    if (migrated) nets_save();
}

// ── 记忆列表增删查（调用方持 s_nets_mtx）────────────────────
static int find_net_locked(const char* ssid)
{
    for (int i = 0; i < s_count; i++)
        if (strcmp(s_nets[i].ssid, ssid) == 0) return i;
    return -1;
}

// 新增或更新（已存在则更新密码）；满了淘汰一条非当前项。返回索引。
static int net_upsert_locked(const char* ssid, const char* pass)
{
    int idx = find_net_locked(ssid);
    if (idx >= 0) {
        snprintf(s_nets[idx].pass, sizeof(s_nets[idx].pass), "%s", pass ? pass : "");
        return idx;
    }
    if (s_count < MAX_SAVED) {
        idx = s_count++;
    } else {
        // 存满：淘汰索引 0（最早的一条）；若它正是当前项则淘汰索引 1
        idx = (s_active == 0) ? 1 : 0;
    }
    s_nets[idx] = Cred{};   // 清空旧残留（Cred 非平凡类型不能 memset）
    snprintf(s_nets[idx].ssid, sizeof(s_nets[idx].ssid), "%s", ssid);
    snprintf(s_nets[idx].pass, sizeof(s_nets[idx].pass), "%s", pass ? pass : "");
    return idx;
}

static void remove_index_locked(int idx)
{
    if (idx < 0 || idx >= s_count) return;
    for (int i = idx; i < s_count - 1; i++) s_nets[i] = s_nets[i + 1];
    s_count--;
    s_nets[s_count] = Cred{};   // 清空末尾空槽（Cred 非平凡类型不能 memset）
    if (s_active == idx)      s_active = -1;
    else if (s_active > idx)  s_active--;
}

// ── 发起连接：取第 idx 条凭据快照，set_config + connect ──────
static void start_connect(int idx)
{
    char ssid[33] = {};   // 零初始化：NUL 之后保持全 0，整段 memcpy 进定长字段才干净
    char pass[65] = {};
    nets_lock();
    if (idx < 0 || idx >= s_count) { nets_unlock(); return; }
    s_active = idx;
    snprintf(ssid, sizeof(ssid), "%s", s_nets[idx].ssid);
    snprintf(pass, sizeof(pass), "%s", s_nets[idx].pass);
    nets_unlock();

    wifi_config_t cfg = {};
    memcpy(cfg.sta.ssid, ssid, sizeof(cfg.sta.ssid));         // 字段 32 字节，源已零填充
    memcpy(cfg.sta.password, pass, sizeof(cfg.sta.password)); // 字段 64 字节
    cfg.sta.threshold.authmode = pass[0] ? WIFI_AUTH_WPA_PSK : WIFI_AUTH_OPEN;

    esp_err_t e1 = esp_wifi_set_config(WIFI_IF_STA, &cfg);
    with_status([&](Status& st) {
        st.state = State::Connecting;
        snprintf(st.ssid, sizeof(st.ssid), "%s", ssid);
        st.ip[0] = '\0'; st.rssi = 0;
    });
    esp_err_t e2 = esp_wifi_connect();
    ESP_LOGI(TAG, "connect %s: set_config=%s connect=%s", ssid, esp_err_to_name(e1), esp_err_to_name(e2));
}

// 首选连接：上次成功 > 当前活动 > 第一条
static void connect_preferred()
{
    int idx = -1;
    nets_lock();
    if (s_lastok[0]) idx = find_net_locked(s_lastok);
    if (idx < 0 && s_active >= 0 && s_active < s_count) idx = s_active;
    if (idx < 0 && s_count > 0) idx = 0;
    nets_unlock();
    if (idx >= 0) {
        start_connect(idx);
        s_auto = Auto::TryLast;
    }
}

// 扫描结果里挑"已保存且信号最强"的一条，返回其在 s_nets 的索引（-1=没有）
static int pick_strongest_known()
{
    int best = -1, best_rssi = -999;
    nets_lock();
    for (int i = 0; i < s_ap_count; i++) {
        int idx = find_net_locked(s_aps[i].ssid);
        if (idx >= 0 && s_aps[i].rssi > best_rssi) {
            best_rssi = s_aps[i].rssi;
            best = idx;
        }
    }
    nets_unlock();
    return best;
}

// ── 事件处理 ──────────────────────────────────────────────────
static void on_wifi_event(void*, esp_event_base_t, int32_t id, void* data)
{
    switch (id) {
        case WIFI_EVENT_STA_START:
            connect_preferred();   // 开机/重启 STA 后立刻连首选网络
            break;
        case WIFI_EVENT_STA_CONNECTED: {
            // 关联成功（L2 通了），接下来等 DHCP。诊断"关联上但拿不到 IP"必须看到这条。
            auto* ev = (wifi_event_sta_connected_t*)data;
            ESP_LOGI(TAG, "assoc ok: ssid=%.32s ch=%d authmode=%d", (char*)ev->ssid, ev->channel, ev->authmode);
            break;
        }
        case WIFI_EVENT_STA_DISCONNECTED: {
            // reason 是定位连不上的第一手证据（15=四次握手超时/密码错、201=找不到 AP、2=AUTH_EXPIRE…）
            auto* ev = (wifi_event_sta_disconnected_t*)data;
            ESP_LOGW(TAG, "disconnected: ssid=%.32s reason=%d rssi=%d", (char*)ev->ssid, ev->reason, ev->rssi);
            with_status([](Status& st) {
                if (st.state == State::Connected || st.state == State::Connecting)
                    st.state = State::Idle;
                st.ip[0] = '\0'; st.rssi = 0;
            });
            // 后续编排（转扫描/换网）由后台任务的 auto_step 推进
            break;
        }
        case WIFI_EVENT_SCAN_DONE: {
            uint16_t n = MAX_AP;
            // ⚠️ 必须 static：事件回调跑在默认事件循环任务（栈仅 ~2.3KB），
            // wifi_ap_record_t×16 ≈ 1.3KB 放栈上会溢出。SCAN_DONE 串行，static 安全。
            static wifi_ap_record_t recs[MAX_AP];
            if (esp_wifi_scan_get_ap_records(&n, recs) == ESP_OK) {
                int cnt = 0;
                for (int i = 0; i < n && cnt < MAX_AP; i++) {
                    if (!recs[i].ssid[0]) continue;  // 隐藏 SSID 跳过
                    snprintf(s_aps[cnt].ssid, sizeof(s_aps[cnt].ssid), "%s", (char*)recs[i].ssid);
                    s_aps[cnt].rssi   = recs[i].rssi;
                    s_aps[cnt].secure = recs[i].authmode != WIFI_AUTH_OPEN;
                    cnt++;
                }
                s_ap_count = cnt;
            }
            s_scanning = false;
            break;
        }
        default:
            break;
    }
}

static void on_ip_event(void*, esp_event_base_t, int32_t id, void* data)
{
    if (id == IP_EVENT_STA_GOT_IP) {
        auto* ev = (ip_event_got_ip_t*)data;
        char ip[16];
        snprintf(ip, sizeof(ip), IPSTR, IP2STR(&ev->ip_info.ip));
        ESP_LOGI(TAG, "got ip %s", ip);
        with_status([&](Status& st) {
            st.state = State::Connected;
            snprintf(st.ip, sizeof(st.ip), "%s", ip);
        });
        // 记录"上次成功"= 当前活动项的 SSID；NVS 落盘交给后台任务（事件回调不写 flash）
        nets_lock();
        if (s_active >= 0 && s_active < s_count) {
            snprintf(s_lastok, sizeof(s_lastok), "%s", s_nets[s_active].ssid);
            s_lastok_dirty = true;
        }
        nets_unlock();
        s_auto = Auto::Idle;   // 连上了，复位编排

        ps_change(0);    // 按当前要求下发省电设置（见 ps_hold）
    }
}

// ── 自动连接编排：未连接时一步步推进 ────────────────────────
static void auto_step()
{
    switch (s_auto) {
        case Auto::Idle:
            connect_preferred();             // → 置 TryLast（或仍 Idle 表示无网络）
            break;
        case Auto::TryLast:
            // 走到这且仍未连上 → 首选失败，转扫描挑最强
            if (s_scanning) {
                s_auto = Auto::Scanning;     // 复用正在进行的扫描
            } else if (scan_start()) {
                s_auto = Auto::Scanning;
            }
            // 扫描起不来则保持 TryLast，下轮再试
            break;
        case Auto::Scanning:
            if (!s_scanning) {
                int idx = pick_strongest_known();
                if (idx >= 0) start_connect(idx);
                s_auto = Auto::Cooldown;     // 不论成败，本轮结束，冷却后重来
                s_cooldown = 6;              // ~30s（循环步进 5s）
            }
            break;
        case Auto::Cooldown:
            if (--s_cooldown <= 0) s_auto = Auto::Idle;
            break;
    }
}

// ── 后台任务：初始化 + 自动连接编排 + RSSI 刷新 ─────────────
static void task(void*)
{
    nets_load();
    // 开着的：留在 Starting，到下面初始化完了才转 Idle（界面这几秒写「正在联网」，不是「未联网」）；关着的直接报关着
    if (!s_enabled) with_status([](Status& st) { st.state = State::Disabled; });

    esp_netif_init();
    esp_event_loop_create_default();
    esp_netif_t* netif = esp_netif_create_default_wifi_sta();

    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    esp_err_t err = esp_wifi_init(&cfg);
    if (err != ESP_OK) {
        // 起不来就是故障（到下次开机为止 WiFi 用不了），不管链路那边判没判定：不然界面一直停在「正在联网」。本任务到此结束
        ESP_LOGE(TAG, "esp_wifi_init failed: %s（到 C6 的链路没起来，或 C6 固件异常）", esp_err_to_name(err));
        with_status([](Status& st) { st.state = State::Fault; });
        vTaskDelete(nullptr);
        return;
    }
    esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, on_wifi_event, nullptr);
    esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_ip_event, nullptr);

    esp_wifi_set_mode(WIFI_MODE_STA);
    with_status([](Status& st) { st.ready = true; if (st.state == State::Starting) st.state = State::Idle; });
    if (s_enabled) esp_wifi_start();   // STA_START 事件里 connect_preferred

    int connecting_ticks = 0;   // Connecting 停留计数：关联成功但 DHCP 一直不来时自愈重连
    bool fault_handled = false; // 无线模块故障后的收尾做过没有

    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(5000));

        if (s_lastok_dirty) { s_lastok_dirty = false; nets_save(); }

        // 无线模块故障：把网络接口放下来（清掉 IP，套接字立刻报错而不是等超时），
        // 之后不再编排连接、不再发任何请求。
        if (esp_hosted_link_failed()) {
            if (!fault_handled) {
                fault_handled = true;
                s_scanning = false;   // 故障那一刻正在扫描的话，扫描结束事件不会来了
                ESP_LOGE(TAG, "无线模块故障，WiFi 停用到下次开机");
                if (netif) esp_netif_action_disconnected(netif, nullptr, 0, nullptr);
            }
            continue;
        }

        // netif MAC 全零自愈（2026-07-30 真机实锤的根因）：esp-hosted 冷启动竞态下，
        // 默认 wifi netif glue 在 STA_START 时向 C6 要 radio MAC 可能失败 → netif MAC
        // 保持全零 → DHCP DISCOVER 的 chaddr/L2 源地址全零，OFFER 永远回不来，表现为
        // "关联成功但拿不到 IP"。这里周期性核对，一旦发现全零就用 radio MAC 补上并
        // 重连（断开→重关联会重启 DHCP，用上正确 MAC）。
        if (netif && s_enabled) {
            uint8_t nm[6] = {};
            if (esp_netif_get_mac(netif, nm) == ESP_OK &&
                (nm[0] | nm[1] | nm[2] | nm[3] | nm[4] | nm[5]) == 0) {
                uint8_t wm[6] = {};
                if (esp_wifi_get_mac(WIFI_IF_STA, wm) == ESP_OK &&
                    (wm[0] | wm[1] | wm[2] | wm[3] | wm[4] | wm[5]) != 0) {
                    esp_netif_set_mac(netif, wm);
                    ESP_LOGW(TAG, "netif MAC 全零 → 补成 radio MAC %02x:%02x:%02x:%02x:%02x:%02x，重连走 DHCP",
                             wm[0], wm[1], wm[2], wm[3], wm[4], wm[5]);
                    esp_wifi_disconnect();   // DISCONNECTED → Idle → auto_step 用新 MAC 重连
                }
            }
        }

        Status st = status();
        if (st.state != State::Connecting) connecting_ticks = 0;
        if (st.state == State::Connected) {
            // RSSI 只为 WiFi 页显示：息屏没人看，跳过这趟 SDIO 往返
            if (board::screenOn()) {
                wifi_ap_record_t ap;
                if (esp_wifi_sta_get_ap_info(&ap) == ESP_OK)
                    with_status([&](Status& s) { s.rssi = ap.rssi; });
            }
            continue;
        }
        if (st.state == State::Connecting) {
            // 无限期停在 Connecting = 关联成功但 DHCP 拿不到 IP（断开事件也不会来）。
            // 25s 还没结果就主动断开重来，让编排状态机继续推进，不再死等。
            if (++connecting_ticks >= 5) {
                ESP_LOGW(TAG, "connecting stuck %ds (assoc ok but no IP?) -> disconnect and retry",
                         connecting_ticks * 5);
                connecting_ticks = 0;
                esp_wifi_disconnect();   // 触发 DISCONNECTED → Idle → auto_step 重新编排
            }
            continue;   // 等连接结果
        }

        // 此时 Idle / Disabled
        if (!s_enabled) { s_auto = Auto::Idle; continue; }
        nets_lock();
        bool has = s_count > 0;
        nets_unlock();
        if (!has) { s_auto = Auto::Idle; continue; }

        auto_step();
    }
}

// ── 公开接口 ──────────────────────────────────────────────────
void init()
{
    if (s_started) return;
    s_started = true;
    s_mtx      = xSemaphoreCreateMutex();
    s_nets_mtx = xSemaphoreCreateMutex();
    s_ps_mtx   = xSemaphoreCreateMutex();
    xTaskCreatePinnedToCore(task, "wifi", 6144, nullptr, 4, nullptr, 0);
}

void set_enabled(bool en)
{
    if (en == s_enabled) return;
    s_enabled = en;
    nets_save();
    if (en) {
        s_auto = Auto::Idle;
        // 还没起完就被打开的：接着等它起完（那时任务自己 esp_wifi_start）；起不来的（Fault）不动
        with_status([](Status& st) { if (st.state != State::Fault) st.state = st.ready ? State::Idle : State::Starting; });
        esp_wifi_start();
    } else {
        esp_wifi_disconnect();
        esp_wifi_stop();
        with_status([](Status& st) {
            if (st.state != State::Fault) st.state = State::Disabled;
            st.ip[0] = '\0'; st.rssi = 0;
        });
    }
}

void connect(const char* ssid, const char* pass)
{
    if (!ssid || !ssid[0]) return;
    int idx;
    nets_lock();
    idx = net_upsert_locked(ssid, pass);
    nets_unlock();
    nets_save();
    esp_wifi_disconnect();
    start_connect(idx);
    s_auto = Auto::TryLast;   // 用户选定这条；失败则自动转扫描挑最强
}

void connect_saved(const char* ssid)
{
    if (!ssid || !ssid[0]) return;
    int idx;
    nets_lock();
    idx = find_net_locked(ssid);
    nets_unlock();
    if (idx < 0) return;
    esp_wifi_disconnect();
    start_connect(idx);
    s_auto = Auto::TryLast;
}

void forget(const char* ssid)
{
    if (!ssid || !ssid[0]) return;
    bool was_active = false;
    nets_lock();
    int idx = find_net_locked(ssid);
    if (idx >= 0) {
        was_active = (idx == s_active);
        if (strcmp(s_lastok, ssid) == 0) s_lastok[0] = '\0';
        remove_index_locked(idx);
    }
    nets_unlock();
    nets_save();
    if (was_active) {
        esp_wifi_disconnect();
        with_status([](Status& st) {
            if (st.state != State::Fault && st.state != State::Starting) st.state = s_enabled ? State::Idle : State::Disabled;
            st.ssid[0] = '\0'; st.ip[0] = '\0'; st.rssi = 0;
        });
        s_auto = Auto::Idle;   // 删的是当前网络，重新编排（会去连别的已保存网络）
    }
}

void forget()
{
    Status st = status();
    if (st.ssid[0]) forget(st.ssid);
}

int saved_list(SavedNet* out, int max)
{
    nets_lock();
    int n = s_count < max ? s_count : max;
    for (int i = 0; i < n; i++) {
        snprintf(out[i].ssid, sizeof(out[i].ssid), "%.32s", s_nets[i].ssid);
        out[i].current = (i == s_active);
    }
    nets_unlock();
    return n;
}

bool scan_start()
{
    if (s_scanning || !s_enabled) return false;
    s_scanning = true;
    s_ap_count = 0;
    if (esp_wifi_scan_start(nullptr, false) != ESP_OK) {
        s_scanning = false;
        return false;
    }
    return true;
}

int scan_results(ApInfo* out, int max)
{
    int n = s_ap_count < max ? s_ap_count : max;
    // 按 RSSI 降序（结果少，冒泡即可）
    ApInfo tmp[MAX_AP];
    memcpy(tmp, s_aps, sizeof(tmp));
    for (int i = 0; i < n; i++)
        for (int j = i + 1; j < s_ap_count; j++)
            if (tmp[j].rssi > tmp[i].rssi) { ApInfo t = tmp[i]; tmp[i] = tmp[j]; tmp[j] = t; }
    memcpy(out, tmp, n * sizeof(ApInfo));
    return n;
}

}  // namespace wifi
