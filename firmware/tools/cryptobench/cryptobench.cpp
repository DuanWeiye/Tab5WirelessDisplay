// 诊断工具，平时不编进固件：量软件加密在 Tab5（ESP32-P4，360 MHz）上有多快，给「无线显示」的加密方案选型用。
// 本平台的硬件加密（AES / SHA / MPI / ECC）不可靠，sdkconfig 里都关着，所以量的全是 mbedtls 的软件实现。
// 要求（2026-11-12）：加密要在不影响传输速度的前提下做。链路上实测的最大吞吐是 26.5 Mbps ≈ 3.3 MB/s
// （整屏噪点，一帧 430 KB）；滚动这类常见的重画面是 10.7 Mbps，一帧 47 KB、每个条带约 6 KB。
//
// 量的是：对一段数据做「加密 + 认证」每秒多少 MB、一次多少微秒，数据分别放在 PSRAM（帧数据缓冲就在那里）和内部 RAM；
// 以及 X25519 密钥交换一次要多久（只在连接 / 配对时做）。任务栈在 PSRAM 上（调试台任务和无线显示任务都是这样）。
// 量的时候拿着满速锁（没人拿锁时 CPU 会降到 90 MHz；无线显示连着的时候它自己也拿着），开头结尾各打一次当时的 CPU 频率。
//
// 怎么用（量完全部撤掉）：
//   1. 把本目录两个文件拷到 main/utils/，main/CMakeLists.txt 的 SRCS 里加 "utils/cryptobench.cpp"；
//   2. sdkconfig 里 CONFIG_MBEDTLS_CHACHA20_C=y、CONFIG_MBEDTLS_POLY1305_C=y、CONFIG_MBEDTLS_CHACHAPOLY_C=y（mbedtls 会重编）；
//   3. main/debug_console.cpp：#include "utils/cryptobench.hpp"，命令表里加 {"cbench", "cbench 量软件加密的速度", [](const char*) { cryptobench::run_all(); }}；
//   4. 烧录，串口发 cbench（约 45 秒）。
//
// 2026-11-12 量到的（360 MHz，原始输出在同目录 result_20261112.txt）：
//   ChaCha20-Poly1305  6.8 MB/s（ChaCha20 8.9 + Poly1305 29.6），数据在 PSRAM 还是内部 RAM 没有差别；一个 48 KB 的帧 7 ms。
//   AES-128-GCM        0.8 MB/s —— 不能用；AES-128-CTR 4.4 MB/s，HMAC-SHA256 4.6 MB/s，两样加起来比 ChaCha20-Poly1305 慢一半多。
//   X25519             生成密钥对、算共享密钥各约 58 ms。
//   ChaCha20 换成「状态字放寄存器」的写法只快到 11.9 MB/s（+33%）：这颗 CPU 没有循环移位指令，一个字节要二十几条指令，
//   再怎么写也就这样；要再快得用它的向量扩展写汇编。所以结论是用 mbedtls 自带的，耗时靠「边收边解密」藏进等网络的时间里。
#include "cryptobench.hpp"
#include <sdkconfig.h>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <esp_heap_caps.h>
#include <esp_log.h>
#include <esp_pm.h>
#include <esp_private/esp_clk.h>
#include <esp_random.h>
#include <esp_timer.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <mbedtls/aes.h>
#include <mbedtls/ecdh.h>
#include <mbedtls/ecp.h>
#include <mbedtls/gcm.h>
#include <mbedtls/md.h>
#if CONFIG_MBEDTLS_CHACHAPOLY_C
#include <mbedtls/chacha20.h>
#include <mbedtls/chachapoly.h>
#include <mbedtls/poly1305.h>
#endif

namespace {

const char* TAG = "cbench";
const uint8_t KEY[32] = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32};
const uint8_t NONCE[16] = {9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 0, 0, 0, 0};
const uint8_t AAD[8] = {0x10, 0, 0, 0, 0, 0x18, 0, 0};       // 一个报文头那么长

// 上下文放内部 RAM（静态区），不放栈上：栈在 PSRAM
#if CONFIG_MBEDTLS_CHACHAPOLY_C
mbedtls_chachapoly_context s_cp;
#endif
mbedtls_gcm_context s_gcm;
mbedtls_aes_context s_aes;

// 把 fn 连着跑到累计约 3 MB（至少 4 次），打印每秒多少 KB、一次多少微秒
template <typename F>
void bench(const char* name, const char* where, size_t len, F&& fn)
{
    int reps = (int)((3u * 1024 * 1024) / len);
    if (reps < 4) reps = 4;
    int64_t t0 = esp_timer_get_time();
    for (int i = 0; i < reps; i++) fn();
    int64_t us = esp_timer_get_time() - t0;
    if (us <= 0) us = 1;
    ESP_LOGW(TAG, "%-24s %-5s %3u KB：%6u KB/s，一次 %6u µs", name, where, (unsigned)(len >> 10),
             (unsigned)((uint64_t)len * reps * 1000000 / us / 1024), (unsigned)(us / reps));
    vTaskDelay(pdMS_TO_TICKS(30));      // 让一让别的任务，也别惹看门狗
}

#if CONFIG_MBEDTLS_CHACHAPOLY_C
// 对照：同一个算法（RFC 8439 的 ChaCha20）换一种写法——16 个状态字放在局部变量（寄存器）里算，整块按 32 位字异或。
// mbedtls 的写法是每一步都读写内存里的数组、逐字节异或。只用来看「换个写法」能快多少，run_all 里先和 mbedtls 的输出逐字节比对。
inline uint32_t rotl(uint32_t v, int n) { return (v << n) | (v >> (32 - n)); }
#define CB_QR(a, b, c, d)                \
    a += b; d ^= a; d = rotl(d, 16);     \
    c += d; b ^= c; b = rotl(b, 12);     \
    a += b; d ^= a; d = rotl(d, 8);      \
    c += d; b ^= c; b = rotl(b, 7);

void chacha20_reg(const uint8_t key[32], const uint8_t nonce[12], uint32_t counter, size_t len, const uint8_t* in, uint8_t* out)
{
    uint32_t k[8], nn[3];
    memcpy(k, key, 32);         // 本机是小端，和算法规定的字节序一致
    memcpy(nn, nonce, 12);
    const bool aligned = (((uintptr_t)in | (uintptr_t)out) & 3) == 0;
    while (len > 0) {
        uint32_t x0 = 0x61707865, x1 = 0x3320646e, x2 = 0x79622d32, x3 = 0x6b206574;
        uint32_t x4 = k[0], x5 = k[1], x6 = k[2], x7 = k[3], x8 = k[4], x9 = k[5], x10 = k[6], x11 = k[7];
        uint32_t x12 = counter, x13 = nn[0], x14 = nn[1], x15 = nn[2];
        for (int i = 0; i < 10; i++) {
            CB_QR(x0, x4, x8, x12) CB_QR(x1, x5, x9, x13) CB_QR(x2, x6, x10, x14) CB_QR(x3, x7, x11, x15)
            CB_QR(x0, x5, x10, x15) CB_QR(x1, x6, x11, x12) CB_QR(x2, x7, x8, x13) CB_QR(x3, x4, x9, x14)
        }
        const uint32_t ks[16] = {x0 + 0x61707865, x1 + 0x3320646e, x2 + 0x79622d32, x3 + 0x6b206574,
                                 x4 + k[0], x5 + k[1], x6 + k[2], x7 + k[3], x8 + k[4], x9 + k[5], x10 + k[6], x11 + k[7],
                                 x12 + counter, x13 + nn[0], x14 + nn[1], x15 + nn[2]};
        const size_t n = len < 64 ? len : 64;
        if (n == 64 && aligned) {
            const uint32_t* wi = (const uint32_t*)__builtin_assume_aligned(in, 4);
            uint32_t*       wo = (uint32_t*)__builtin_assume_aligned(out, 4);
            for (int i = 0; i < 16; i++) wo[i] = wi[i] ^ ks[i];
        } else {
            const uint8_t* kb = (const uint8_t*)ks;
            for (size_t i = 0; i < n; i++) out[i] = in[i] ^ kb[i];
        }
        counter++;
        in += n;
        out += n;
        len -= n;
    }
}
#endif

int rng(void*, unsigned char* buf, size_t len)
{
    esp_fill_random(buf, len);
    return 0;
}

void bench_buffer(uint8_t* buf, uint8_t* other, const char* where, size_t len)
{
    uint8_t tag[16], mac[32];
    bench("memcpy（参照）", where, len, [&] { memcpy(other, buf, len); });
#if CONFIG_MBEDTLS_CHACHAPOLY_C
    bench("ChaCha20-Poly1305", where, len, [&] { mbedtls_chachapoly_encrypt_and_tag(&s_cp, len, NONCE, AAD, sizeof(AAD), buf, buf, tag); });
    bench("  其中 ChaCha20", where, len, [&] { mbedtls_chacha20_crypt(KEY, NONCE, 1, len, buf, buf); });
    bench("  ChaCha20 寄存器写法", where, len, [&] { chacha20_reg(KEY, NONCE, 1, len, buf, buf); });
    bench("  其中 Poly1305", where, len, [&] { mbedtls_poly1305_mac(KEY, buf, len, tag); });
#endif
    bench("AES-128-GCM", where, len, [&] {
        mbedtls_gcm_crypt_and_tag(&s_gcm, MBEDTLS_GCM_ENCRYPT, len, NONCE, 12, AAD, sizeof(AAD), buf, buf, sizeof(tag), tag);
    });
    bench("AES-128-CTR", where, len, [&] {
        size_t off = 0;
        uint8_t ctr[16], stream[16];
        memcpy(ctr, NONCE, 16);
        mbedtls_aes_crypt_ctr(&s_aes, len, &off, ctr, stream, buf, buf);
    });
    bench("HMAC-SHA256", where, len, [&] { mbedtls_md_hmac(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256), KEY, 32, buf, len, mac); });
}

void bench_x25519()
{
    mbedtls_ecp_group grp;
    mbedtls_mpi d, z;
    mbedtls_ecp_point q;
    mbedtls_ecp_group_init(&grp);
    mbedtls_mpi_init(&d);
    mbedtls_mpi_init(&z);
    mbedtls_ecp_point_init(&q);
    int rc = mbedtls_ecp_group_load(&grp, MBEDTLS_ECP_DP_CURVE25519);
    for (int i = 0; i < 3 && rc == 0; i++) {
        int64_t t0 = esp_timer_get_time();
        rc = mbedtls_ecdh_gen_public(&grp, &d, &q, rng, nullptr);
        int64_t t1 = esp_timer_get_time();
        if (rc == 0) rc = mbedtls_ecdh_compute_shared(&grp, &z, &q, &d, rng, nullptr);
        int64_t t2 = esp_timer_get_time();
        ESP_LOGW(TAG, "X25519：生成密钥对 %u ms，算共享密钥 %u ms（rc=%d）", (unsigned)((t1 - t0) / 1000), (unsigned)((t2 - t1) / 1000), rc);
        vTaskDelay(pdMS_TO_TICKS(30));
    }
    mbedtls_ecp_point_free(&q);
    mbedtls_mpi_free(&z);
    mbedtls_mpi_free(&d);
    mbedtls_ecp_group_free(&grp);
}

}  // namespace

void cryptobench::run_all()
{
    const size_t BIG = 256 * 1024, MID = 48 * 1024, SMALL = 16 * 1024;      // 内部 RAM 很紧，只拿两块 16 KB
    esp_pm_lock_handle_t fast = nullptr;
    if (esp_pm_lock_create(ESP_PM_CPU_FREQ_MAX, 0, "cbench", &fast) == ESP_OK) esp_pm_lock_acquire(fast);
    vTaskDelay(pdMS_TO_TICKS(20));      // 等频率切上去
    ESP_LOGW(TAG, "CPU 现在 %d MHz（%s满速锁）", esp_clk_cpu_freq() / 1000000, fast ? "拿着" : "没拿到");
    auto* ps  = (uint8_t*)heap_caps_malloc(BIG, MALLOC_CAP_SPIRAM);
    auto* ps2 = (uint8_t*)heap_caps_malloc(BIG, MALLOC_CAP_SPIRAM);
    auto* in  = (uint8_t*)heap_caps_malloc(SMALL, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    auto* in2 = (uint8_t*)heap_caps_malloc(SMALL, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
    if (!ps || !ps2) {
        ESP_LOGE(TAG, "PSRAM 里分不出两块 256 KB");
    } else {
        esp_fill_random(ps, BIG);
#if CONFIG_MBEDTLS_CHACHAPOLY_C
        mbedtls_chachapoly_init(&s_cp);
        mbedtls_chachapoly_setkey(&s_cp, KEY);
        // 先核对一遍加了能解、改一个字节解不开，免得量了半天量的是出错返回
        uint8_t tag[16];
        memcpy(ps2, ps, 4096);
        int e = mbedtls_chachapoly_encrypt_and_tag(&s_cp, 4096, NONCE, AAD, sizeof(AAD), ps, ps, tag);
        int d = mbedtls_chachapoly_auth_decrypt(&s_cp, 4096, NONCE, AAD, sizeof(AAD), tag, ps, ps);
        bool same = memcmp(ps, ps2, 4096) == 0;
        mbedtls_chachapoly_encrypt_and_tag(&s_cp, 4096, NONCE, AAD, sizeof(AAD), ps, ps, tag);
        ps[100] ^= 1;
        int bad = mbedtls_chachapoly_auth_decrypt(&s_cp, 4096, NONCE, AAD, sizeof(AAD), tag, ps, ps2);
        ESP_LOGW(TAG, "ChaCha20-Poly1305 自检：加密 rc=%d，解密 rc=%d，解出来%s原文，改一个字节后解密 rc=%d（应该不是 0）", e, d, same ? "是" : "不是", bad);
        // 寄存器写法的输出要和 mbedtls 的一模一样：长度故意不是 64 的倍数，再试一次起点没对齐到 4 字节的
        const size_t N = 4096 + 37;
        esp_fill_random(ps, N + 1);
        mbedtls_chacha20_crypt(KEY, NONCE, 7, N, ps, ps2);
        chacha20_reg(KEY, NONCE, 7, N, ps, ps + 8192);
        bool eq1 = memcmp(ps2, ps + 8192, N) == 0;
        mbedtls_chacha20_crypt(KEY, NONCE, 7, N, ps + 1, ps2);
        chacha20_reg(KEY, NONCE, 7, N, ps + 1, ps + 8192 + 1);
        bool eq2 = memcmp(ps2, ps + 8192 + 1, N) == 0;
        ESP_LOGW(TAG, "ChaCha20 寄存器写法和 mbedtls 的输出%s（对齐）、%s（没对齐）", eq1 ? "一样" : "不一样", eq2 ? "一样" : "不一样");
        esp_fill_random(ps, BIG);
#else
        ESP_LOGW(TAG, "这个固件没编进 ChaCha20-Poly1305（见本文件开头的第 2 步），只量 AES 和 HMAC");
#endif
        mbedtls_gcm_init(&s_gcm);
        mbedtls_gcm_setkey(&s_gcm, MBEDTLS_CIPHER_ID_AES, KEY, 128);
        mbedtls_aes_init(&s_aes);
        mbedtls_aes_setkey_enc(&s_aes, KEY, 128);

        ESP_LOGW(TAG, "== 数据在 PSRAM ==");
        for (size_t len : {(size_t)6 * 1024, MID, BIG}) bench_buffer(ps, ps2, "PSRAM", len);
        if (in && in2) {
            ESP_LOGW(TAG, "== 数据在内部 RAM ==");
            esp_fill_random(in, SMALL);
            for (size_t len : {(size_t)6 * 1024, SMALL}) bench_buffer(in, in2, "内部", len);
        } else {
            ESP_LOGW(TAG, "内部 RAM 分不出两块 16 KB，这一组不量");
        }
        bench_x25519();
        mbedtls_gcm_free(&s_gcm);
        mbedtls_aes_free(&s_aes);
#if CONFIG_MBEDTLS_CHACHAPOLY_C
        mbedtls_chachapoly_free(&s_cp);
#endif
    }
    heap_caps_free(in2);
    heap_caps_free(in);
    heap_caps_free(ps2);
    heap_caps_free(ps);
    ESP_LOGW(TAG, "量完了，CPU 现在 %d MHz", esp_clk_cpu_freq() / 1000000);
    if (fast) {
        esp_pm_lock_release(fast);
        esp_pm_lock_delete(fast);
    }
}
