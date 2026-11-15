#include "utils/hwjpeg.hpp"

#include <esp_heap_caps.h>
#include <esp_log.h>
#include <esp_private/esp_cache_private.h>
#include "driver/jpeg_decode.h"

namespace hwjpeg {

static const char* TAG = "hwjpeg";

static jpeg_decoder_handle_t s_engine = nullptr;

bool preinit()
{
    if (s_engine) return true;
    jpeg_decode_engine_cfg_t cfg = {};
    cfg.intr_priority = 0;
    cfg.timeout_ms    = 1000;
    esp_err_t e = jpeg_new_decoder_engine(&cfg, &s_engine);
    if (e != ESP_OK) {
        ESP_LOGE(TAG, "建硬件 JPEG 引擎失败：%s", esp_err_to_name(e));
        s_engine = nullptr;
    }
    return s_engine != nullptr;
}

size_t out_align()
{
    static size_t align = 0;      // 值是固定的，几个任务同时第一次调也只是各自写进同一个数
    if (!align) {
        size_t a = 0;
        align = (esp_cache_get_alignment(MALLOC_CAP_SPIRAM, &a) == ESP_OK && a) ? a : 128;
    }
    return align;
}

// 2D-DMA 描述符里图片的宽和高各只有 14 位，再大驱动会把截断后的尺寸交给硬件、自己算的输出字节数还会 32 位溢出。
// 这个数不能再放宽：驱动里按 MCU 取整后的 宽 × 高 × 16 是 32 位算的，4:4:4 时最大 16376 × 16376 × 16 ≈ 4.29e9，
// 离 2^32 只差一点。
constexpr int MAX_DIM = 16383;

// 把文件头（从 SOI 到 SOS）严格走一遍，取尺寸。只放行驱动能解对、而且驱动自己再走一遍时不会越界的结构。
//
// 为什么不直接用驱动的 jpeg_decoder_get_info / 让 jpeg_decoder_process 自己报错：IDF 5.4.2 的驱动解析文件头时
// 不查边界——段长度、量化表号、霍夫曼表号、码字个数都直接拿数据里的值去移指针、当数组下标和循环次数。
// 一张坏图（存储卡上损坏的文件，或者网络上来的数据）就能让它读写到缓冲区外面，写坏的是堆。
// 所以这里是把关的地方：过不了这一关的数据不交给驱动。
//
// 放行的结构：SOI，然后是一段一段紧挨着的 APPn / COM / DQT / DHT / SOF0 / DRI，最后是 SOS 和压缩数据。
//   - 每段的长度都在缓冲区以内；标记之间不许有填充字节（驱动两个字节两个字节地认标记，有填充会认错位）
//   - DQT：表号不重复，而且全文件的表号正好是 0、1、…（驱动按"第几张"而不是按表号把表写给硬件，
//     只定义了 1 号表、或者同一个表号定义两次，硬件拿到的是空表）；每个分量用到的表都定义过
//   - DHT：类别和表号都是 0 / 1，码字个数 DC 不超过 16、AC 不超过 256（驱动里表的大小）；每个 DHT 段至少有一张表。
//     一个 DHT 段都没有时驱动用标准表；有的话 SOS 里各分量用到的表都得定义过（没定义的表硬件拿到的是全 0）
//   - SOF0：8 位精度，3 个分量（灰度图硬件解不成 RGB565），编号互不相同；亮度采样 4:4:4 / 4:2:2 / 4:2:0
//     （硬件只支持这三种）；宽 × 高是 8 的倍数（驱动的规矩）；按 MCU 取整后的宽高不超过 MAX_DIM
//   - SOS：分量数、各分量的编号和顺序都和 SOF0 一致（不一致时硬件解到一半报错——不越界，但那时屏幕已经接管了），
//     基线的固定参数（Ss = 0，Se = 63，Ah = Al = 0）
//   - 渐进式等别的 SOFn、其它标记：一律不放行（硬件解不了）
static bool parse_header(const uint8_t* p, size_t len, Info& out)
{
    if (!p || len < 4 || p[0] != 0xFF || p[1] != 0xD8) return false;
    size_t  pos = 2;
    bool    sof = false;
    int     dqt_n = 0;
    uint8_t dqt_mask = 0, dht_mask = 0;      // dht_mask 的位：类别 * 2 + 表号
    uint8_t tq[3] = {}, cid[3] = {};         // SOF0 里各分量的量化表号、编号
    for (;;) {
        if (pos + 4 > len || p[pos] != 0xFF) return false;
        const uint8_t marker  = p[pos + 1];
        const size_t  seg_len = (size_t)p[pos + 2] << 8 | p[pos + 3];     // 含长度字段自己的 2 字节
        if (seg_len < 2 || pos + 2 + seg_len > len) return false;
        const uint8_t* d = p + pos + 4;      // 段的内容
        const size_t   n = seg_len - 2;

        if ((marker >= 0xE0 && marker <= 0xEF) || marker == 0xFE) {
            // APPn / COM：内容不看
        } else if (marker == 0xDB) {         // DQT：若干张 [精度|表号] + 64 个值
            for (size_t i = 0; i < n;) {
                const int    prec = d[i] >> 4, id = d[i] & 0x0F;
                const size_t need = 1 + (prec ? 128 : 64);
                if (prec > 1 || id > 3 || (dqt_mask & (1 << id)) || i + need > n) return false;
                dqt_mask |= (uint8_t)(1 << id);
                dqt_n++;
                i += need;
            }
        } else if (marker == 0xC4) {         // DHT：若干张 [类别|表号] + 16 个码长计数 + 码字
            if (n == 0) return false;
            for (size_t i = 0; i < n;) {
                if (i + 17 > n) return false;
                const int cls = d[i] >> 4, id = d[i] & 0x0F;
                size_t    codes = 0;
                for (int k = 1; k <= 16; k++) codes += d[i + k];
                if (cls > 1 || id > 1 || codes > (cls ? 256u : 16u) || i + 17 + codes > n) return false;
                dht_mask |= (uint8_t)(1 << (cls * 2 + id));
                i += 17 + codes;
            }
        } else if (marker == 0xC0) {         // SOF0：精度、高、宽、分量数，每个分量 [编号][采样][量化表号]
            if (sof || n != 6 + 3 * 3 || d[0] != 8 || d[5] != 3) return false;
            out.h = d[1] << 8 | d[2];
            out.w = d[3] << 8 | d[4];
            if (out.w == 0 || out.h == 0 || ((uint32_t)out.w * (uint32_t)out.h) % 8) return false;
            const uint8_t hv = d[6 + 1];
            if ((hv != 0x11 && hv != 0x21 && hv != 0x22) || d[6 + 3 + 1] != 0x11 || d[6 + 6 + 1] != 0x11) return false;
            out.hv = hv;
            const int mcu_w = (hv >> 4) * 8, mcu_h = (hv & 0x0F) * 8;
            out.out_w = (out.w + mcu_w - 1) / mcu_w * mcu_w;
            out.out_h = (out.h + mcu_h - 1) / mcu_h * mcu_h;
            if (out.out_w > MAX_DIM || out.out_h > MAX_DIM) return false;
            for (int c = 0; c < 3; c++) {
                cid[c] = d[6 + 3 * c];
                tq[c]  = d[6 + 3 * c + 2];
            }
            if (cid[0] == cid[1] || cid[0] == cid[2] || cid[1] == cid[2]) return false;
            sof = true;
        } else if (marker == 0xDD) {         // DRI
            if (n != 2) return false;
        } else if (marker == 0xDA) {         // SOS：分量数、每个分量 [编号][DC 表号|AC 表号]、Ss、Se、Ah|Al，后面得有压缩数据
            if (!sof || n != 4 + 2 * 3 || d[0] != 3) return false;
            if (dqt_n == 0 || dqt_mask != (1 << dqt_n) - 1) return false;
            for (int c = 0; c < 3; c++) {
                if (d[1 + 2 * c] != cid[c]) return false;
                if (tq[c] > 3 || !(dqt_mask & (1 << tq[c]))) return false;
                const int td = d[1 + 2 * c + 1] >> 4, ta = d[1 + 2 * c + 1] & 0x0F;
                if (td > 1 || ta > 1) return false;
                if (dht_mask && (!(dht_mask & (1 << td)) || !(dht_mask & (1 << (2 + ta))))) return false;
            }
            if (d[7] != 0 || d[8] != 63 || d[9] != 0) return false;
            return pos + 2 + seg_len < len;
        } else {
            return false;
        }
        pos += 2 + seg_len;
    }
}

bool info(const uint8_t* jpg, size_t len, Info& out)
{
    return parse_header(jpg, len, out);
}

// yuv = 输出 YUV422（不转换），否则 RGB565
static esp_err_t decode(const uint8_t* jpg, size_t len, bool yuv, uint8_t* out, size_t out_size, uint32_t* got)
{
    if (got) *got = 0;
    Info inf = {};
    if (!parse_header(jpg, len, inf)) return ESP_ERR_INVALID_ARG;       // 见 parse_header：没校验过的数据不交给驱动
    if (yuv && inf.hv != 0x21) return ESP_ERR_NOT_SUPPORTED;
    // 输出放不放得下自己先算（驱动也查，但它的算式在大尺寸上会 32 位溢出）
    const size_t align = out_align();
    if (!out || ((uintptr_t)out & (align - 1)) || (out_size & (align - 1)) || out_size < inf.out_bytes()) return ESP_ERR_INVALID_ARG;
    if (!s_engine) return ESP_ERR_INVALID_STATE;
    jpeg_decode_cfg_t cfg = {};
    cfg.output_format = yuv ? JPEG_DECODE_OUT_FORMAT_YUV422 : JPEG_DECODE_OUT_FORMAT_RGB565;
    cfg.rgb_order     = JPEG_DEC_RGB_ELEMENT_ORDER_BGR;   // 这个顺序解出来的 RGB565 和 LVGL / 帧缓冲一致（YUV 输出不看这一项）
    cfg.conv_std      = JPEG_YUV_RGB_CONV_STD_BT601;
    uint32_t n = 0;
    esp_err_t e = jpeg_decoder_process(s_engine, &cfg, jpg, (uint32_t)len, out, (uint32_t)out_size, &n);
    if (got) *got = n;
    return e;
}

esp_err_t decode_rgb565(const uint8_t* jpg, size_t len, uint8_t* out, size_t out_size, uint32_t* got)
{
    return decode(jpg, len, false, out, out_size, got);
}

esp_err_t decode_yuv422(const uint8_t* jpg, size_t len, uint8_t* out, size_t out_size, uint32_t* got)
{
    return decode(jpg, len, true, out, out_size, got);
}

}  // namespace hwjpeg
