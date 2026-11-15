// 见 wdisp_crypto.hpp。
#include "wdisp_crypto.hpp"

#include <cstdio>
#include <cstring>
#include <initializer_list>

#include <bootloader_random.h>
#include <esp_log.h>
#include <esp_mac.h>
#include <esp_random.h>
#include <esp_timer.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include <freertos/task.h>
extern "C" {
#include <mbedtls/constant_time.h>      // 这个头文件自己没有 extern "C"（mbedtls 3.6.3），不包起来链接不上
}
#include <mbedtls/ctr_drbg.h>
#include <mbedtls/ecdh.h>
#include <mbedtls/ecp.h>
#include <mbedtls/hkdf.h>
#include <mbedtls/md.h>
#include <mbedtls/pkcs5.h>
#include <mbedtls/platform_util.h>
#include <mbedtls/sha256.h>

namespace wdc {

static const char* TAG = "wdc";

// PROTOCOL.md「配对」里的常量
static const char    PBKDF2_SALT[]      = "Tab5 WinDisplay v2 password";
constexpr int        PBKDF2_ROUNDS      = 10000;
constexpr size_t     W0_LEN             = 40;
static const char    INFO_CONFIRM[]     = "ConfirmationKeys";
static const char    INFO_PAIRING_KEY[] = "Tab5 WinDisplay v2 pairing key";
static const char    INFO_SESSION[]     = "Tab5 WinDisplay v2 session";
static const char    INFO_KEY_ID[]      = "Tab5 WinDisplay v2 key id";
// RFC 9382 给 P-256 定的两个点，非压缩格式
static const uint8_t SPAKE_M[POINT_LEN] = {
    0x04, 0x88, 0x6e, 0x2f, 0x97, 0xac, 0xe4, 0x6e, 0x55, 0xba, 0x9d, 0xd7, 0x24, 0x25, 0x79, 0xf2,
    0x99, 0x3b, 0x64, 0xe1, 0x6e, 0xf3, 0xdc, 0xab, 0x95, 0xaf, 0xd4, 0x97, 0x33, 0x3d, 0x8f, 0xa1,
    0x2f, 0x5f, 0xf3, 0x55, 0x16, 0x3e, 0x43, 0xce, 0x22, 0x4e, 0x0b, 0x0e, 0x65, 0xff, 0x02, 0xac,
    0x8e, 0x5c, 0x7b, 0xe0, 0x94, 0x19, 0xc7, 0x85, 0xe0, 0xca, 0x54, 0x7d, 0x55, 0xa1, 0x2e, 0x2d,
    0x20};
static const uint8_t SPAKE_N[POINT_LEN] = {
    0x04, 0xd8, 0xbb, 0xd6, 0xc6, 0x39, 0xc6, 0x29, 0x37, 0xb0, 0x4d, 0x99, 0x7f, 0x38, 0xc3, 0x77,
    0x07, 0x19, 0xc6, 0x29, 0xd7, 0x01, 0x4d, 0x49, 0xa2, 0x4b, 0x4f, 0x98, 0xba, 0xa1, 0x29, 0x2b,
    0x49, 0x07, 0xd6, 0x0a, 0xa6, 0xbf, 0xad, 0xe4, 0x50, 0x08, 0xa6, 0x36, 0x33, 0x7f, 0x51, 0x68,
    0xc6, 0x4d, 0x9b, 0xd3, 0x60, 0x34, 0x80, 0x8c, 0xd5, 0x64, 0x49, 0x0b, 0x1e, 0x65, 0x6e, 0xdb,
    0xe7};

// ── 随机数 ───────────────────────────────────────────────────────────────
static mbedtls_ctr_drbg_context s_drbg;
static SemaphoreHandle_t        s_rand_mux = nullptr;       // DRBG 不是线程安全的：无线显示任务和调试台（自检）都会用
static bool                     s_seeded   = false;

// CTR-DRBG 要熵的时候（播种、每隔一万次取数之后重新播种）调：打开主熵源的这一小段时间里，硬件随机数是真随机数
static int entropy_cb(void*, unsigned char* out, size_t len)
{
    bootloader_random_enable();
    vTaskDelay(1);                      // 让噪声先搅进去一会儿
    esp_fill_random(out, len);
    bootloader_random_disable();
    return 0;
}

static int rng(void*, unsigned char* out, size_t len)
{
    return rand_bytes(out, len) ? 0 : MBEDTLS_ERR_CTR_DRBG_REQUEST_TOO_BIG;
}

bool rand_ready()
{
    static portMUX_TYPE once = portMUX_INITIALIZER_UNLOCKED;
    if (!s_rand_mux) {
        SemaphoreHandle_t m = xSemaphoreCreateMutex();
        portENTER_CRITICAL(&once);
        if (!s_rand_mux) {
            s_rand_mux = m;
            m = nullptr;
        }
        portEXIT_CRITICAL(&once);
        if (m) vSemaphoreDelete(m);
        if (!s_rand_mux) return false;
    }
    xSemaphoreTake(s_rand_mux, portMAX_DELAY);
    if (!s_seeded) {
        struct { int64_t t; uint8_t mac[6]; } pers = {};        // 个性化串：这台机器、这一刻
        pers.t = esp_timer_get_time();
        esp_efuse_mac_get_default(pers.mac);
        mbedtls_ctr_drbg_init(&s_drbg);
        int rc = mbedtls_ctr_drbg_seed(&s_drbg, entropy_cb, nullptr, (const unsigned char*)&pers, sizeof pers);
        if (rc == 0) {
            s_seeded = true;
        } else {
            ESP_LOGE(TAG, "随机数生成器播种失败（-0x%04x）", (unsigned)-rc);
            mbedtls_ctr_drbg_free(&s_drbg);
        }
    }
    bool ok = s_seeded;
    xSemaphoreGive(s_rand_mux);
    return ok;
}

void rand_stir()
{
    if (!rand_ready()) return;
    struct { int64_t t; uint32_t r[4]; } add;
    add.t = esp_timer_get_time();
    esp_fill_random(add.r, sizeof add.r);
    xSemaphoreTake(s_rand_mux, portMAX_DELAY);
    mbedtls_ctr_drbg_update(&s_drbg, (const unsigned char*)&add, sizeof add);
    xSemaphoreGive(s_rand_mux);
}

bool rand_bytes(void* out, size_t n)
{
    if (!rand_ready()) return false;
    xSemaphoreTake(s_rand_mux, portMAX_DELAY);
    int      rc = 0;
    uint8_t* p  = (uint8_t*)out;
    while (n && rc == 0) {      // 一次最多取 MBEDTLS_CTR_DRBG_MAX_REQUEST 字节
        size_t take = n < MBEDTLS_CTR_DRBG_MAX_REQUEST ? n : MBEDTLS_CTR_DRBG_MAX_REQUEST;
        rc = mbedtls_ctr_drbg_random(&s_drbg, p, take);
        p += take;
        n -= take;
    }
    xSemaphoreGive(s_rand_mux);
    return rc == 0;
}

// ── 小工具 ───────────────────────────────────────────────────────────────
void sha256_cat(const uint8_t* a, size_t an, const uint8_t* b, size_t bn, const uint8_t* c, size_t cn, uint8_t out[HASH_LEN])
{
    mbedtls_sha256_context ctx;
    mbedtls_sha256_init(&ctx);
    mbedtls_sha256_starts(&ctx, 0);
    if (an) mbedtls_sha256_update(&ctx, a, an);
    if (bn) mbedtls_sha256_update(&ctx, b, bn);
    if (cn) mbedtls_sha256_update(&ctx, c, cn);
    mbedtls_sha256_finish(&ctx, out);
    mbedtls_sha256_free(&ctx);
}

bool same(const void* a, const void* b, size_t n)
{
    return mbedtls_ct_memcmp(a, b, n) == 0;
}

void wipe(void* p, size_t n)
{
    mbedtls_platform_zeroize(p, n);
}

static const mbedtls_md_info_t* sha256_md()
{
    return mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
}

// ── P-256 ────────────────────────────────────────────────────────────────
namespace {

// 一次运算里用到的群、整数和点，出作用域时一起释放（整数里有密码推出来的 w 和随机数，释放时 mbedtls 会清零）
struct P256 {
    mbedtls_ecp_group grp;
    mbedtls_mpi       w, y, one, neg1;
    mbedtls_ecp_point pa, m, n, wm, wn, t, k, yg, pb;
    P256()
    {
        mbedtls_ecp_group_init(&grp);
        for (mbedtls_mpi* x : {&w, &y, &one, &neg1}) mbedtls_mpi_init(x);
        for (mbedtls_ecp_point* p : {&pa, &m, &n, &wm, &wn, &t, &k, &yg, &pb}) mbedtls_ecp_point_init(p);
    }
    ~P256()
    {
        for (mbedtls_ecp_point* p : {&pa, &m, &n, &wm, &wn, &t, &k, &yg, &pb}) mbedtls_ecp_point_free(p);
        for (mbedtls_mpi* x : {&w, &y, &one, &neg1}) mbedtls_mpi_free(x);
        mbedtls_ecp_group_free(&grp);
    }
};

// 读一个对方给的点：只收 65 字节、04 开头的非压缩格式，坐标在范围内、在曲线上（check_pubkey 也挡住无穷远点）
bool read_point(mbedtls_ecp_group& grp, mbedtls_ecp_point& out, const uint8_t* p, size_t n)
{
    if (n != POINT_LEN || p[0] != 0x04) return false;
    return mbedtls_ecp_point_read_binary(&grp, &out, p, n) == 0 && mbedtls_ecp_check_pubkey(&grp, &out) == 0;
}

// 密码 → w：w0 = PBKDF2-HMAC-SHA256(密码, 定好的盐, 10000 次, 40 字节)，w = (w0 按大端当成整数) mod n
bool password_to_w(mbedtls_ecp_group& grp, const char* password, mbedtls_mpi& w)
{
    uint8_t w0[W0_LEN];
    bool    ok = mbedtls_pkcs5_pbkdf2_hmac_ext(MBEDTLS_MD_SHA256, (const unsigned char*)password, strlen(password),
                                               (const unsigned char*)PBKDF2_SALT, sizeof PBKDF2_SALT - 1, PBKDF2_ROUNDS, W0_LEN, w0) == 0
                 && mbedtls_mpi_read_binary(&w, w0, W0_LEN) == 0 && mbedtls_mpi_mod_mpi(&w, &w, &grp.N) == 0;
    wipe(w0, sizeof w0);
    return ok;
}

bool write_point(mbedtls_ecp_group& grp, const mbedtls_ecp_point& p, uint8_t out[POINT_LEN])
{
    size_t n = 0;
    return mbedtls_ecp_point_write_binary(&grp, &p, MBEDTLS_ECP_PF_UNCOMPRESSED, &n, out, POINT_LEN) == 0 && n == POINT_LEN;
}

}  // namespace

bool point_ok(const uint8_t* p, size_t n)
{
    mbedtls_ecp_group grp;
    mbedtls_ecp_point pt;
    mbedtls_ecp_group_init(&grp);
    mbedtls_ecp_point_init(&pt);
    bool ok = mbedtls_ecp_group_load(&grp, MBEDTLS_ECP_DP_SECP256R1) == 0 && read_point(grp, pt, p, n);
    mbedtls_ecp_point_free(&pt);
    mbedtls_ecp_group_free(&grp);
    return ok;
}

// ── 配对：SPAKE2（RFC 9382），Tab5 是 B ──────────────────────────────────
// pB = y·G + w·N，K = y·(pA − w·M)。带秘密标量（w、y）的乘法都走 mbedtls_ecp_mul 并给随机数源（防旁路）；
// 点的加减用 mbedtls_ecp_muladd，它不防旁路，所以只让它拿 1 和 −1 当系数（这两个它直接拷贝 / 取负，不做乘法）。
Pair spake2_b(const char* password, const uint8_t pA[POINT_LEN], const uint8_t id_pc[ID_LEN], const uint8_t id_tab5[ID_LEN],
              const uint8_t aad[HASH_LEN], PairOut& out, const uint8_t* fixed_y)
{
    if (!fixed_y && !rand_ready()) return Pair::Failed;
    P256    e;
    uint8_t wb[32], kb[POINT_LEN];
    // TT = 六项各带 8 字节小端的长度：电脑编号、Tab5 编号、pA、pB、K、w
    uint8_t tt[6 * 8 + ID_LEN + ID_LEN + POINT_LEN + POINT_LEN + POINT_LEN + 32];
    uint8_t digest[HASH_LEN], kc[32], info[sizeof INFO_CONFIRM - 1 + HASH_LEN];
    Pair    res = Pair::Failed;

    do {
        if (mbedtls_ecp_group_load(&e.grp, MBEDTLS_ECP_DP_SECP256R1) != 0) break;
        if (!password_to_w(e.grp, password, e.w)) break;
        if (mbedtls_mpi_lset(&e.one, 1) != 0 || mbedtls_mpi_lset(&e.neg1, -1) != 0) break;
        if (!read_point(e.grp, e.pa, pA, POINT_LEN)) { res = Pair::BadPoint; break; }
        if (!read_point(e.grp, e.m, SPAKE_M, POINT_LEN) || !read_point(e.grp, e.n, SPAKE_N, POINT_LEN)) break;

        if (fixed_y) {
            if (mbedtls_mpi_read_binary(&e.y, fixed_y, 32) != 0) break;
        } else if (mbedtls_ecp_gen_privkey(&e.grp, &e.y, rng, nullptr) != 0) {      // 1 ≤ y < n
            break;
        }
        // K = y·(pA − w·M)
        if (mbedtls_ecp_mul(&e.grp, &e.wm, &e.w, &e.m, rng, nullptr) != 0) break;
        if (mbedtls_ecp_muladd(&e.grp, &e.t, &e.one, &e.pa, &e.neg1, &e.wm) != 0) break;
        if (mbedtls_ecp_is_zero(&e.t)) { res = Pair::BadPoint; break; }
        if (mbedtls_ecp_mul(&e.grp, &e.k, &e.y, &e.t, rng, nullptr) != 0) break;
        if (mbedtls_ecp_is_zero(&e.k)) { res = Pair::BadPoint; break; }
        // pB = y·G + w·N
        if (mbedtls_ecp_mul(&e.grp, &e.yg, &e.y, &e.grp.G, rng, nullptr) != 0) break;
        if (mbedtls_ecp_mul(&e.grp, &e.wn, &e.w, &e.n, rng, nullptr) != 0) break;
        if (mbedtls_ecp_muladd(&e.grp, &e.pb, &e.one, &e.yg, &e.one, &e.wn) != 0 || mbedtls_ecp_is_zero(&e.pb)) break;
        if (!write_point(e.grp, e.pb, out.pB) || !write_point(e.grp, e.k, kb)) break;
        if (mbedtls_mpi_write_binary(&e.w, wb, sizeof wb) != 0) break;

        size_t pos = 0;
        auto put = [&](const uint8_t* v, size_t n) {
            memset(tt + pos, 0, 8);
            tt[pos]     = (uint8_t)n;           // 各项都不到 256 字节
            memcpy(tt + pos + 8, v, n);
            pos += 8 + n;
        };
        put(id_pc, ID_LEN);
        put(id_tab5, ID_LEN);
        put(pA, POINT_LEN);
        put(out.pB, POINT_LEN);
        put(kb, POINT_LEN);
        put(wb, sizeof wb);

        // Ke ‖ Ka = SHA-256(TT)，各 16 字节；KcA ‖ KcB = HKDF(Ka, info = "ConfirmationKeys" ‖ AAD)，各 16 字节
        sha256_cat(tt, sizeof tt, nullptr, 0, nullptr, 0, digest);
        memcpy(info, INFO_CONFIRM, sizeof INFO_CONFIRM - 1);
        memcpy(info + sizeof INFO_CONFIRM - 1, aad, HASH_LEN);
        if (mbedtls_hkdf(sha256_md(), nullptr, 0, digest + 16, 16, info, sizeof info, kc, sizeof kc) != 0) break;
        if (mbedtls_md_hmac(sha256_md(), kc, 16, tt, sizeof tt, out.cA) != 0) break;
        if (mbedtls_md_hmac(sha256_md(), kc + 16, 16, tt, sizeof tt, out.cB) != 0) break;
        if (mbedtls_hkdf(sha256_md(), nullptr, 0, digest, 16, (const unsigned char*)INFO_PAIRING_KEY, sizeof INFO_PAIRING_KEY - 1,
                         out.key, KEY_LEN) != 0) break;
        res = Pair::Ok;
    } while (false);

    wipe(wb, sizeof wb);
    wipe(kb, sizeof kb);
    wipe(tt, sizeof tt);
    wipe(digest, sizeof digest);
    wipe(kc, sizeof kc);
    if (res != Pair::Ok) wipe(&out, sizeof out);
    return res;
}

void key_id(const uint8_t key[KEY_LEN], uint8_t out[KEY_ID_LEN])
{
    uint8_t mac[HASH_LEN];
    mbedtls_md_hmac(sha256_md(), key, KEY_LEN, (const unsigned char*)INFO_KEY_ID, sizeof INFO_KEY_ID - 1, mac);
    memcpy(out, mac, KEY_ID_LEN);
}

// ── 每次连接：一次性 X25519 + 配对密钥 → 会话密钥 ────────────────────────
namespace {

struct X25519 {
    mbedtls_ecp_group grp;
    mbedtls_mpi       d, z;
    mbedtls_ecp_point q;
    X25519()
    {
        mbedtls_ecp_group_init(&grp);
        mbedtls_mpi_init(&d);
        mbedtls_mpi_init(&z);
        mbedtls_ecp_point_init(&q);
    }
    ~X25519()
    {
        mbedtls_ecp_point_free(&q);
        mbedtls_mpi_free(&z);
        mbedtls_mpi_free(&d);
        mbedtls_ecp_group_free(&grp);
    }
    // 32 字节的私钥 → 标量（RFC 7748 的规整：低 3 位清 0，最高位清 0，次高位置 1）
    bool load(const uint8_t priv[X25519_LEN])
    {
        return mbedtls_ecp_group_load(&grp, MBEDTLS_ECP_DP_CURVE25519) == 0 && mbedtls_mpi_read_binary_le(&d, priv, X25519_LEN) == 0
               && mbedtls_mpi_set_bit(&d, 0, 0) == 0 && mbedtls_mpi_set_bit(&d, 1, 0) == 0 && mbedtls_mpi_set_bit(&d, 2, 0) == 0
               && mbedtls_mpi_set_bit(&d, 255, 0) == 0 && mbedtls_mpi_set_bit(&d, 254, 1) == 0;
    }
};

}  // namespace

bool x25519_public(const uint8_t priv[X25519_LEN], uint8_t pub[X25519_LEN])
{
    if (!rand_ready()) return false;
    X25519 x;
    size_t n = 0;
    return x.load(priv) && mbedtls_ecp_mul(&x.grp, &x.q, &x.d, &x.grp.G, rng, nullptr) == 0
           && mbedtls_ecp_point_write_binary(&x.grp, &x.q, MBEDTLS_ECP_PF_UNCOMPRESSED, &n, pub, X25519_LEN) == 0 && n == X25519_LEN;
}

bool x25519_keypair(uint8_t priv[X25519_LEN], uint8_t pub[X25519_LEN])
{
    return rand_bytes(priv, X25519_LEN) && x25519_public(priv, pub);
}

bool x25519_shared(const uint8_t priv[X25519_LEN], const uint8_t peer_pub[X25519_LEN], uint8_t dh[X25519_LEN])
{
    if (!rand_ready()) return false;
    X25519 x;
    // 低阶点（共享值会是全 0 的那几个）在 mbedtls 里查公钥时就拒了；下面再查一遍结果，协议要求的是「全 0 就断开」
    bool ok = x.load(priv) && mbedtls_ecp_point_read_binary(&x.grp, &x.q, peer_pub, X25519_LEN) == 0
              && mbedtls_ecdh_compute_shared(&x.grp, &x.z, &x.q, &x.d, rng, nullptr) == 0
              && mbedtls_mpi_write_binary_le(&x.z, dh, X25519_LEN) == 0;
    if (ok) {
        uint8_t acc = 0;
        for (size_t i = 0; i < X25519_LEN; i++) acc |= dh[i];
        ok = acc != 0;
    }
    if (!ok) wipe(dh, X25519_LEN);
    return ok;
}

bool session_keys(const uint8_t pair_key[KEY_LEN], const uint8_t dh[X25519_LEN], const uint8_t th[HASH_LEN],
                  uint8_t k_pc_to_tab5[KEY_LEN], uint8_t k_tab5_to_pc[KEY_LEN])
{
    uint8_t info[sizeof INFO_SESSION - 1 + HASH_LEN], okm[2 * KEY_LEN];
    memcpy(info, INFO_SESSION, sizeof INFO_SESSION - 1);
    memcpy(info + sizeof INFO_SESSION - 1, th, HASH_LEN);
    bool ok = mbedtls_hkdf(sha256_md(), pair_key, KEY_LEN, dh, X25519_LEN, info, sizeof info, okm, sizeof okm) == 0;
    if (ok) {
        memcpy(k_pc_to_tab5, okm, KEY_LEN);
        memcpy(k_tab5_to_pc, okm + KEY_LEN, KEY_LEN);
    }
    wipe(okm, sizeof okm);
    return ok;
}

// ── 逐报文加密：ChaCha20-Poly1305 ────────────────────────────────────────
Cipher::Cipher() { mbedtls_chachapoly_init(&ctx_); }
Cipher::~Cipher() { mbedtls_chachapoly_free(&ctx_); }

void Cipher::set_key(const uint8_t key[KEY_LEN], uint64_t count)
{
    mbedtls_chachapoly_setkey(&ctx_, key);
    count_ = count;
    keyed_ = true;
}

void Cipher::clear()
{
    mbedtls_chachapoly_free(&ctx_);     // 清零
    mbedtls_chachapoly_init(&ctx_);
    count_ = 0;
    keyed_ = false;
}

// nonce = 4 字节 0 ‖ 这个方向的报文计数（u64 小端）。取一次计数加 1
void Cipher::nonce(uint8_t out[12])
{
    memset(out, 0, 4);
    for (int i = 0; i < 8; i++) out[4 + i] = (uint8_t)(count_ >> (8 * i));
    count_++;
}

bool Cipher::seal(const uint8_t hdr[HDR_LEN], const uint8_t* payload, size_t n, uint8_t* out)
{
    if (!keyed_) return false;
    uint8_t nc[12];
    nonce(nc);
    return mbedtls_chachapoly_encrypt_and_tag(&ctx_, n, nc, hdr, HDR_LEN, payload, out, out + n) == 0;
}

bool Cipher::open_begin(const uint8_t hdr[HDR_LEN])
{
    if (!keyed_) return false;
    uint8_t nc[12];
    nonce(nc);
    return mbedtls_chachapoly_starts(&ctx_, nc, MBEDTLS_CHACHAPOLY_DECRYPT) == 0 && mbedtls_chachapoly_update_aad(&ctx_, hdr, HDR_LEN) == 0;
}

bool Cipher::open_update(uint8_t* data, size_t n)
{
    return n == 0 || mbedtls_chachapoly_update(&ctx_, n, data, data) == 0;
}

bool Cipher::open_finish(const uint8_t tag[TAG_LEN])
{
    uint8_t mac[TAG_LEN];
    bool    ok = mbedtls_chachapoly_finish(&ctx_, mac) == 0 && same(mac, tag, TAG_LEN);
    wipe(mac, sizeof mac);
    return ok;
}

// ── 自检 ─────────────────────────────────────────────────────────────────
namespace {
#include "wdisp_vectors.inc"

int s_bad;

void item(bool ok, const char* what)
{
    vTaskDelay(1);      // 整套要连着算十几秒：每项之间让一下，空闲任务才喂得上看门狗
    if (!ok) s_bad++;
    ESP_LOGW(TAG, "自检：[%s] %s", ok ? "对" : "不对", what);
}

Cipher s_test_cipher;       // 放静态区，见 Cipher 的说明

}  // namespace

bool selftest()
{
    s_bad = 0;
    int64_t t0 = esp_timer_get_time();
    if (!rand_ready()) {
        ESP_LOGE(TAG, "自检：随机数生成器没播上种");
        return false;
    }
    {   // 随机数：两次取出来的不一样、不是全 0（只能查出「根本没在工作」）
        uint8_t a[32], b[32], z[32] = {};
        bool    ok = rand_bytes(a, sizeof a) && rand_bytes(b, sizeof b);
        item(ok && memcmp(a, b, sizeof a) != 0 && memcmp(a, z, sizeof a) != 0 && memcmp(b, z, sizeof b) != 0, "随机数：连取两次不一样，也不是全 0");
    }

    // 配对：固定的 y，逐字节对 pB、cA、cB、配对密钥、密钥标识
    PairOut po;
    Pair    r = spake2_b(V_PAIRING.password, V_PAIRING.pA, V_PAIRING.id_pc, V_PAIRING.id_tab5, V_PAIRING.aad, po, V_PAIRING.y);
    item(r == Pair::Ok && !memcmp(po.pB, V_PAIRING.pB, POINT_LEN), "配对：pB");
    item(r == Pair::Ok && !memcmp(po.cB, V_PAIRING.cB, CONFIRM_LEN) && !memcmp(po.cA, V_PAIRING.cA, CONFIRM_LEN), "配对：cA、cB");
    item(r == Pair::Ok && !memcmp(po.key, V_PAIRING.key, KEY_LEN), "配对：配对密钥");
    uint8_t kid[KEY_ID_LEN];
    key_id(V_PAIRING.key, kid);
    item(!memcmp(kid, V_PAIRING.key_id, KEY_ID_LEN), "配对密钥的标识");
    for (const auto& v : V_PASSWORDS) {     // 密码 → w
        P256    e;
        uint8_t w[32];
        char    what[64];
        bool    ok = mbedtls_ecp_group_load(&e.grp, MBEDTLS_ECP_DP_SECP256R1) == 0 && password_to_w(e.grp, v.password, e.w)
                     && mbedtls_mpi_write_binary(&e.w, w, sizeof w) == 0 && !memcmp(w, v.w, sizeof w);
        snprintf(what, sizeof what, "密码 → w（%u 个字符的密码）", (unsigned)strlen(v.password));
        item(ok, what);
    }
    // 换一个密码，pB 和 cB 就得不一样；随机的 y 两次配出来的 pB 不一样
    {
        PairOut a, b, c;
        bool    ok = spake2_b(V_PASSWORDS[1].password, V_PAIRING.pA, V_PAIRING.id_pc, V_PAIRING.id_tab5, V_PAIRING.aad, a, V_PAIRING.y) == Pair::Ok;
        item(ok && memcmp(a.cB, V_PAIRING.cB, CONFIRM_LEN) != 0 && memcmp(a.pB, V_PAIRING.pB, POINT_LEN) != 0, "配对：换一个密码，pB 和 cB 都变了");
        ok = spake2_b(V_PAIRING.password, V_PAIRING.pA, V_PAIRING.id_pc, V_PAIRING.id_tab5, V_PAIRING.aad, b) == Pair::Ok
             && spake2_b(V_PAIRING.password, V_PAIRING.pA, V_PAIRING.id_pc, V_PAIRING.id_tab5, V_PAIRING.aad, c) == Pair::Ok;
        item(ok && memcmp(b.pB, c.pB, POINT_LEN) != 0 && memcmp(b.key, c.key, KEY_LEN) != 0, "配对：y 随机取，两次的 pB 和配对密钥不一样");
    }
    // 该拒收的点
    for (const auto& p : V_BAD_POINTS) {
        char what[96];
        snprintf(what, sizeof what, "拒收的点：%s", p.what);
        bool rejected = !point_ok(p.p, p.n);
        if (p.n == POINT_LEN) rejected = rejected && spake2_b(V_PAIRING.password, p.p, V_PAIRING.id_pc, V_PAIRING.id_tab5, V_PAIRING.aad, po) == Pair::BadPoint;
        item(rejected, what);
    }
    item(point_ok(V_KINF_PA, POINT_LEN)
         && spake2_b(V_PASSWORDS[0].password, V_KINF_PA, V_PAIRING.id_pc, V_PAIRING.id_tab5, V_PAIRING.aad, po) == Pair::BadPoint,
         "拒收：pA 在曲线上，但 K 是无穷远点");

    // 每次连接
    uint8_t pub[X25519_LEN], dh[X25519_LEN], th[HASH_LEN], k1[KEY_LEN], k2[KEY_LEN];
    item(x25519_public(V_SESSION.priv_tab5, pub) && !memcmp(pub, V_SESSION.pub_tab5, X25519_LEN), "X25519：私钥 → 公钥");
    item(x25519_shared(V_SESSION.priv_tab5, V_SESSION.pub_pc, dh) && !memcmp(dh, V_SESSION.dh, X25519_LEN), "X25519：共享值");
    sha256_cat(V_SESSION_HELLO, sizeof V_SESSION_HELLO, V_SESSION_AUTH, sizeof V_SESSION_AUTH, V_SESSION_AUTH_OK, sizeof V_SESSION_AUTH_OK, th);
    item(!memcmp(th, V_SESSION.th, HASH_LEN), "握手三个报文的摘要");
    item(session_keys(V_SESSION.key, V_SESSION.dh, V_SESSION.th, k1, k2) && !memcmp(k1, V_SESSION.k_p2t, KEY_LEN)
         && !memcmp(k2, V_SESSION.k_t2p, KEY_LEN), "会话密钥（两个方向）");
    for (const auto& x : V_BAD_X25519) {
        char what[96];
        snprintf(what, sizeof what, "拒收的 X25519 公钥：%s", x.what);
        item(!x25519_shared(V_SESSION.priv_tab5, x.pub, dh), what);
    }
    {
        uint8_t p1[X25519_LEN], q1[X25519_LEN], p2[X25519_LEN], q2[X25519_LEN], s1[X25519_LEN], s2[X25519_LEN];
        bool    ok = x25519_keypair(p1, q1) && x25519_keypair(p2, q2) && x25519_shared(p1, q2, s1) && x25519_shared(p2, q1, s2);
        item(ok && !memcmp(s1, s2, X25519_LEN) && memcmp(q1, q2, X25519_LEN) != 0, "X25519：随机取的两对密钥，两边算出同一个共享值");
    }

    // 逐报文加解密：加密出来和线上的字节一样；解出来和明文一样；改一个比特、计数不对都过不了
    static uint8_t buf[512];
    for (size_t i = 0; i < sizeof V_RECORDS / sizeof V_RECORDS[0]; i++) {
        const auto& v = V_RECORDS[i];
        size_t      n = v.wire_len - HDR_LEN - TAG_LEN;
        char        what[128];
        s_test_cipher.set_key(v.key, v.count);
        bool ok = n + TAG_LEN <= sizeof buf && s_test_cipher.seal(v.wire, V_RECORD_PLAIN[i], n, buf) && !memcmp(buf, v.wire + HDR_LEN, n + TAG_LEN);
        snprintf(what, sizeof what, "加密：%s", v.what);
        item(ok, what);

        s_test_cipher.set_key(v.key, v.count);
        memcpy(buf, v.wire + HDR_LEN, n);
        size_t half = n / 2;        // 分两块解，像边收边解密那样
        ok = s_test_cipher.open_begin(v.wire) && s_test_cipher.open_update(buf, half) && s_test_cipher.open_update(buf + half, n - half)
             && s_test_cipher.open_finish(v.wire + HDR_LEN + n) && !memcmp(buf, V_RECORD_PLAIN[i], n);
        snprintf(what, sizeof what, "解密：%s", v.what);
        item(ok, what);

        uint8_t hdr[HDR_LEN], tag[TAG_LEN];
        memcpy(hdr, v.wire, HDR_LEN);
        memcpy(tag, v.wire + HDR_LEN + n, TAG_LEN);
        bool caught = true;
        for (int how = 0; how < 3; how++) {     // 改报文头、改认证码、计数差一
            uint8_t h2[HDR_LEN], t2[TAG_LEN];
            memcpy(h2, hdr, HDR_LEN);
            memcpy(t2, tag, TAG_LEN);
            if (how == 0) h2[2] ^= 1;
            if (how == 1) t2[TAG_LEN - 1] ^= 0x80;
            s_test_cipher.set_key(v.key, how == 2 ? v.count + 1 : v.count);
            memcpy(buf, v.wire + HDR_LEN, n);
            if (s_test_cipher.open_begin(h2) && s_test_cipher.open_update(buf, n) && s_test_cipher.open_finish(t2)) caught = false;
        }
        if (n) {                                // 改密文
            s_test_cipher.set_key(v.key, v.count);
            memcpy(buf, v.wire + HDR_LEN, n);
            buf[n - 1] ^= 1;
            if (s_test_cipher.open_begin(hdr) && s_test_cipher.open_update(buf, n) && s_test_cipher.open_finish(tag)) caught = false;
        }
        snprintf(what, sizeof what, "认证不过：%s（改头 / 改认证码 / 计数差一 / 改密文）", v.what);
        item(caught, what);
    }
    s_test_cipher.clear();

    ESP_LOGW(TAG, "自检：%s，用时 %u 毫秒", s_bad ? "有不对的" : "全部都对", (unsigned)((esp_timer_get_time() - t0) / 1000));
    return s_bad == 0;
}

}  // namespace wdc
