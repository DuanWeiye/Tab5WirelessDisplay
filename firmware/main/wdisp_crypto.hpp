// 无线显示协议 v2 的密码学部分（windisplay/PROTOCOL.md「认证和加密」）：配对（SPAKE2，Tab5 是 B）、每次连接的会话密钥、逐报文加密。
// 算式改了先改 PROTOCOL.md；同样的算式还有两份：windisplay/tools/wdcrypto.py（Python）和 windisplay/sender 的 WinDisplay.Core（C#），
// 三处对着同一份测试向量 windisplay/testvectors/v2.json（固件这边见 selftest()）。
// 本平台的硬件加密（AES / SHA / MPI / ECC）不可靠，sdkconfig 里都关着：这里用的全是 mbedtls 的软件实现。
#pragma once

#include <cstddef>
#include <cstdint>

#include <mbedtls/chachapoly.h>

namespace wdc {

constexpr size_t ID_LEN = 16, KEY_LEN = 32, KEY_ID_LEN = 8, POINT_LEN = 65, CONFIRM_LEN = 32, X25519_LEN = 32, TAG_LEN = 16, HASH_LEN = 32;
constexpr size_t HDR_LEN = 8;       // 报文头，算进认证码

// ── 随机数 ───────────────────────────────────────────────────────────────
// 一次性密钥和 SPAKE2 的随机数都从这里取：握手的新鲜全靠它们，不能弱。
// ESP32-P4 自己没有射频（WiFi 在 C6 上），程序跑起来之后硬件随机数只剩次要的熵源，乐鑫的文档说这时只能当伪随机数用。
// 所以这里用 mbedtls 的 CTR-DRBG，播种时临时打开芯片内部的主熵源（SAR ADC 的噪声，引导程序用的也是它）取真随机数。
bool rand_ready();                      // 播过种了（第一次调时播；播不成返回 false，这时不能握手）
void rand_stir();                       // 每条连接开头调一次：把当时的时刻和硬件随机数再搅进去
bool rand_bytes(void* out, size_t n);

// ── 小工具 ───────────────────────────────────────────────────────────────
// SHA-256(a ‖ b ‖ c)，不用的段给长度 0
void sha256_cat(const uint8_t* a, size_t an, const uint8_t* b, size_t bn, const uint8_t* c, size_t cn, uint8_t out[HASH_LEN]);
bool same(const void* a, const void* b, size_t n);      // 比较用的时间不随内容变化
void wipe(void* p, size_t n);

// ── 配对 ─────────────────────────────────────────────────────────────────
// 收到的点是 65 字节、04 开头、在曲线上
bool point_ok(const uint8_t* p, size_t n);

enum class Pair { Ok, BadPoint, Failed };       // BadPoint = 对方给的 pA 不合格，或者 K 是无穷远点；Failed = 本机算不出来（内存不够之类）
struct PairOut {
    uint8_t pB[POINT_LEN];
    uint8_t cB[CONFIRM_LEN];        // 发给电脑的校验值
    uint8_t cA[CONFIRM_LEN];        // 电脑的 PAIR_OK 里应该是它
    uint8_t key[KEY_LEN];           // 配对密钥（cA 对上了才能用）
};
// aad = SHA-256(HELLO 报文 ‖ 这一次的 PAIR 报文)。fixed_y 只给自检对测试向量用，平时不给（随机取）。
Pair spake2_b(const char* password, const uint8_t pA[POINT_LEN], const uint8_t id_pc[ID_LEN], const uint8_t id_tab5[ID_LEN],
              const uint8_t aad[HASH_LEN], PairOut& out, const uint8_t* fixed_y = nullptr);

void key_id(const uint8_t key[KEY_LEN], uint8_t out[KEY_ID_LEN]);

// ── 每次连接 ─────────────────────────────────────────────────────────────
bool x25519_keypair(uint8_t priv[X25519_LEN], uint8_t pub[X25519_LEN]);
bool x25519_public(const uint8_t priv[X25519_LEN], uint8_t pub[X25519_LEN]);
// 对方的公钥不合格、或者共享值全 0，返回 false
bool x25519_shared(const uint8_t priv[X25519_LEN], const uint8_t peer_pub[X25519_LEN], uint8_t dh[X25519_LEN]);
// th = SHA-256(HELLO ‖ AUTH ‖ AUTH_OK 三个报文)
bool session_keys(const uint8_t pair_key[KEY_LEN], const uint8_t dh[X25519_LEN], const uint8_t th[HASH_LEN],
                  uint8_t k_pc_to_tab5[KEY_LEN], uint8_t k_tab5_to_pc[KEY_LEN]);

// ── 逐报文加密 ───────────────────────────────────────────────────────────
// 一个方向的加密状态：密钥 + 报文计数。每加密 / 解密一个报文往前走一格。对象要放在内部 RAM 里（静态区），别放任务栈上（栈在 PSRAM，慢）。
class Cipher {
public:
    Cipher();
    ~Cipher();
    void set_key(const uint8_t key[KEY_LEN], uint64_t count = 0);
    void clear();
    // out 要有 n + TAG_LEN 字节：密文 ‖ 认证码。payload 可以和 out 是同一块
    bool seal(const uint8_t hdr[HDR_LEN], const uint8_t* payload, size_t n, uint8_t* out);
    // 边收边解密：begin（报文头到了）→ 来一块 update 一块（原地解）→ finish（认证码到了）。finish 返回 true 之前解出来的内容不能用
    bool open_begin(const uint8_t hdr[HDR_LEN]);
    bool open_update(uint8_t* data, size_t n);
    bool open_finish(const uint8_t tag[TAG_LEN]);

private:
    void nonce(uint8_t out[12]);
    mbedtls_chachapoly_context ctx_;
    uint64_t                   count_ = 0;
    bool                       keyed_ = false;
};

// 对着测试向量核对上面每一样（调试命令 `wd selftest`），逐项打日志，全对返回 true。约 18 秒。
bool selftest();

}  // namespace wdc
