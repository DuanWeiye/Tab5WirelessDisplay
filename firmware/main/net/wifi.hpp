// WiFi（ESP32-C6 经 SDIO，esp-hosted + esp_wifi_remote）
//
// Tab5 的 ESP32-P4 本身无 WiFi，无线由板载 ESP32-C6 提供：
//   P4 ←SDIO→ C6（出厂已刷 esp-hosted slave 固件），
//   esp_wifi_remote 把标准 esp_wifi API 转发给 C6，应用层照常用。
// SDIO 引脚等配置在 sdkconfig.defaults（照抄官方 UserDemo）。
#pragma once

#include <cstdint>

namespace wifi {

enum class State {
    Disabled,    // 用户关闭
    Idle,        // 开着但未连接（无保存凭据 / 连接失败待重试）
    Connecting,  // 连接中
    Connected,   // 已连接（拿到 IP）
    Fault,       // 无线模块故障：到 C6 的链路出错、自动重启几次都没恢复（或者开机时 WiFi 起不来），到下次开机为止 WiFi 不可用
    Starting,    // 开机后无线模块还在起（几秒钟）：起完了自己转成上面某一种，不用人管。排在最后，前面几种的数值不变
};

struct Status {
    State state    = State::Starting;
    bool  ready    = false;  // esp-hosted/C6 初始化完成（界面看 state 就够了：没完成时是 Starting）
    char  ssid[33] = "";     // 当前目标/已连 SSID
    char  ip[16]   = "";
    int   rssi     = 0;      // 已连接时有效
};

struct ApInfo {
    char ssid[33] = "";
    int  rssi     = 0;
    bool secure   = false;
};

// 最多记忆的 WiFi 数（个人设备足够；存满再存新的淘汰最旧的非当前项）
static constexpr int MAX_SAVED = 8;

// 已保存网络（供 UI 枚举）
struct SavedNet {
    char ssid[33] = "";
    bool current  = false;  // 是否当前活动项（再结合 status().state 判断已连/连接中）
};

// 开机即调（app_main）：后台任务初始化 esp-hosted/netif/STA，
// 若 NVS 有保存凭据且开关为开则自动连接。幂等。
void init();

Status status();

// WiFi 总开关（持久化 NVS）
void set_enabled(bool en);
bool enabled();

// 连接并存入记忆列表（已存在则更新密码）；pass 可为空 = 开放网络。
// 成功连上后该网络成为"上次成功"，下次开机优先直连它。
void connect(const char* ssid, const char* pass);

// 连接已保存的某条（用存好的密码，无需再输）
void connect_saved(const char* ssid);

// 忘记当前连接的网络（从列表删除并断开）
void forget();

// 忘记指定网络（从列表删除；若正连着则断开）
void forget(const char* ssid);

// 枚举已保存网络到 out（最多 max 条），返回实际条数
int saved_list(SavedNet* out, int max);

// 要求 WiFi 暂时不省电（省电时包按信标间隔成批到达，吞吐低、延迟大）。可以嵌套，hold 和 release 成对调；
// 没有人 hold 时是省电的。期间 WiFi 断开重连也照这个状态设置。
void ps_hold();
void ps_release();

// 异步扫描：start 后轮询 scan_done，完成后取 results
bool scan_start();
bool scan_busy();
int  scan_results(ApInfo* out, int max);  // 返回条数（按 RSSI 降序）

}  // namespace wifi
