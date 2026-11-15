// 滚动位移复用（UI 侧）——治滚动掉帧的框架级方案
//
// 背景（2026-07-29 实测定量）：滚动时"整屏重绘 + 整屏 PPA 旋转"把 PSRAM 带宽打满，
// 帧率锁死 ~15fps 且与内容复杂度无关。本模块把滚动帧改造成：
//   SCROLL 事件累计滚动量 → REFR_START(渲染前)把"整容器失效"收窄为
//   「新露条带 + 容器外顶/底带 + 滚动条带」，同时令 esp_lvgl_port 用 GDMA 把
//   前台帧缓冲错位平移进后台帧缓冲(与渲染并行) → REFR_READY 翻页扫出。
//   每帧带宽从 ~5.4MB 降到 ~2MB，实测滚动帧时间大幅下降。
//
// 适用条件（不满足自动回退整屏重绘，正确性永远优先）：
//   容器整宽(x 占满屏幕)、纯纵向滚动、滚动区内无"不随内容移动的浮层"。
//   盖在容器上面的东西（悬浮按钮、下拉列表、对话框遮罩）由本模块自己认出来：小的随位移帧一起重画，
//   大的（整屏遮罩）那几帧退回整屏重绘，页面不用管。
//   由页面显式 ui_scroll_accel(cont) 开启（opt-in 白名单）。
#include "ui.h"
#include "esp_lvgl_port.h"
#include "src/display/lv_display_private.h"   // 直接手术失效区 inv_areas/inv_p
#include "src/misc/lv_area_private.h"          // lv_area_intersect
#include "src/core/lv_obj_draw_private.h"      // lv_obj_get_ext_draw_size

#include <cstring>
#include <esp_log.h>
#include <esp_timer.h>

static const char* TAG = "scrlacc";

#define MAX_ACCEL  4    // 同时登记的容器上限（每页一个，页面销毁自动注销）
#define STRIP_OVLP 2    // 露出条带多重绘的余量行数
#define SB_BAND_W  28   // 容器右缘"滚动条带"宽度（滚动条重绘自愈区）

struct Ent {
    lv_obj_t* cont;
    int32_t   last_y;
};
static Ent  s_ents[MAX_ACCEL];
static int  s_n      = 0;
static bool s_hooked = false;

// 本刷新周期待处理的滚动（SCROLL 事件累计，REFR_START 消费）
static lv_obj_t* s_pend_cont = nullptr;
static int       s_pend_dy   = 0;
static bool      s_pend_bad  = false;
static bool      s_armed     = false;

// 这个刷新周期里被 LVGL 去重丢掉的小失效区（inv_area_cb 记，REFR_START 消费）。
// dy_at = 记下它时已经累计的滚动量（还没滚过是 0）：REFR_START 时据此算出它之后又跟着内容挪了多少（见那里的说明）。
#define MAX_LOST 8
struct Lost {
    lv_area_t a;
    int       dy_at;
};
static Lost s_lost[MAX_LOST];
static int  s_lost_n    = 0;
static bool s_lost_over = false;     // 记不全（太多，或者记的时候参照的那个容器没了）：这个周期不平移
static bool s_restyling = false;     // 正在给容器加 / 去「滚动中」状态（见 restyle_cb）

static void pend_clear()
{
    s_pend_cont = nullptr; s_pend_dy = 0; s_pend_bad = false;
    s_lost_n = 0; s_lost_over = false;
}
static bool      s_dbg_disabled = false;   // 调试：强制关闭加速（量整屏重绘基线用，'disp accel off'）

// 帧节奏观测（探针开时低频打印）
static int64_t  s_last_shift_us = 0;
static uint32_t s_shift_cnt     = 0;

// 放弃原因统计（定位"加速为何没生效"，每秒最多打一次）。从开机算起累计；开帧探针时会清零（见 ui_scroll_accel_dbg_window）
enum { ST_OK, ST_NOPEND, ST_BAD, ST_TOOBIG, ST_NOMATCH, ST_DIRTY, ST_NOROOM, ST_ARMFAIL, ST_COVERED, ST_N };
static uint32_t s_stat[ST_N];
static uint32_t s_patched;   // 位移帧里"膨胀重绘"处理掉的容器内小脏区累计数
static uint32_t s_lost_cnt;  // 其中 LVGL 自己的失效表里没有、靠 inv_area_cb 记下来的
static uint32_t s_late;      // 排版里才发生、这一帧已经整块画过的滚动（不带到下一帧去平移，见 refr_ready_cb）累计次数
static void stat_bump(int r)
{
    s_stat[r]++;
    if (r == ST_NOPEND) return;                 // 无滚动的空闲帧不打
    static int64_t last = 0;
    int64_t now = esp_timer_get_time();
    if (now - last < 1000000) return;
    last = now;
    ESP_LOGI(TAG, "stat ok=%u bad=%u big=%u nomatch=%u dirty=%u room=%u armfail=%u cover=%u patch=%u lost=%u late=%u",
             (unsigned)s_stat[ST_OK], (unsigned)s_stat[ST_BAD], (unsigned)s_stat[ST_TOOBIG],
             (unsigned)s_stat[ST_NOMATCH], (unsigned)s_stat[ST_DIRTY],
             (unsigned)s_stat[ST_NOROOM], (unsigned)s_stat[ST_ARMFAIL], (unsigned)s_stat[ST_COVERED], (unsigned)s_patched,
             (unsigned)s_lost_cnt, (unsigned)s_late);
}

static Ent* find_ent(lv_obj_t* o)
{
    for (int i = 0; i < s_n; i++)
        if (s_ents[i].cont == o) return &s_ents[i];
    return nullptr;
}

static void scroll_cb(lv_event_t* e)
{
    lv_obj_t* obj = (lv_obj_t*)lv_event_get_target(e);
    Ent* en = find_ent(obj);
    if (!en) return;
    int32_t y  = lv_obj_get_scroll_y(obj);
    int     dy = (int)(y - en->last_y);
    en->last_y = y;
    if (lv_obj_get_scroll_x(obj) != 0) s_pend_bad = true;   // 横向滚动不支持
    if (dy == 0) return;
    if (s_pend_cont && s_pend_cont != obj) { s_pend_bad = true; return; }  // 同周期多容器：放弃
    s_pend_cont = obj;
    s_pend_dy  += dy;
}

static void cont_delete_cb(lv_event_t* e)
{
    lv_obj_t* obj = (lv_obj_t*)lv_event_get_target(e);
    for (int i = 0; i < s_n; i++)
        if (s_ents[i].cont == obj) { s_ents[i] = s_ents[--s_n]; break; }
    // 丢掉的小失效区是按它的滚动量记的：它没了，这个周期剩下的记录对不上别的容器，不平移
    if (s_pend_cont == obj) { s_pend_cont = nullptr; s_pend_dy = 0; s_pend_bad = false; s_lost_over = true; }
}

// SCROLL_BEGIN / SCROLL_END，挂在 LVGL 自己的处理之前（PREPROCESS）。
// LVGL 在这两个事件里给容器加 / 去 LV_STATE_SCROLLED；只要有样式跟这个状态走（默认主题里是滚动条的不透明度），它就把容器的样式
// 整个刷一遍：容器整块失效一次，再把所有子孙挨个标一遍失效。内容没变，变的只有滚动条（那一带每帧本来就重画），
// 这一串不能当成「容器里的小变化」记下来：几十条一下就把表占满，每次滚动的头一帧都得退回整屏重绘。
// 所以这里抢先把状态换掉、换的这一下不记；轮到 LVGL 自己处理时状态已经是那样，它什么也不做。
// （不用「前后各挂一个回调夹住」的写法：后一个回调排在页面先挂的回调之后，页面在滚动开始时做的真变化会被夹进去。）
static void restyle_cb(lv_event_t* e)
{
    lv_obj_t* cont = (lv_obj_t*)lv_event_get_current_target(e);
    const bool prev = s_restyling;      // 换状态引出的事件里万一又给它发了一次滚动开始 / 结束：里层不提前把标志清掉
    s_restyling = true;
    if (lv_event_get_code(e) == LV_EVENT_SCROLL_BEGIN) lv_obj_add_state(cont, LV_STATE_SCROLLED);
    else                                               lv_obj_remove_state(cont, LV_STATE_SCROLLED);
    s_restyling = prev;
}

// a 里有没有落在容器 ca 里、要单独重画的一块（放进 hit）。不算的：碰不到容器；整个落在滚动条带里（那一带每帧本来就重画）；
// 盖满整个容器（那是整容器失效，不是小块）
static bool part_in_cont(const lv_area_t* a, const lv_area_t* ca, lv_area_t* hit)
{
    return lv_area_intersect(hit, a, ca) && hit->x1 < ca->x2 - SB_BAND_W + 1 && !lv_area_is_in(ca, hit, 0);
}

// LV_EVENT_INVALIDATE_AREA：lv_inv_area 往失效表里记之前发（参数是已经夹进屏幕的区域；失效被关掉、区域整个在屏幕外时不发）。
// LVGL 记之前先去重：新区域落在表里已有的某条之内就丢掉。整个容器一进失效表（滚动一发生就进；滚动条淡入淡出的过渡每一步也进），
// 这个刷新周期里之后容器里的小变化（缩略图刚解码完、按压态过渡的下一步、定时刷新的标签）全被那一条吞掉；到 REFR_START 把整容器那条
// 收窄成新露条带，这些小块就没人重画，停在平移过来的旧内容上。所以被丢掉的自己记一份。
// 没被丢掉的不用记：它们在失效表里，refr_start_cb 扫得到。被表里另一条小失效区盖住的、而且这个周期还没滚过的也不用记：
// 那一条到时候按整个滚动量扩开，照样盖得住（行去掉按压态时 LVGL 把它的子孙挨个标一遍，都属于这种；记了白占表）。
// 分不出来的：容器整块因为别的原因失效（容器自己换样式、有个孩子正好盖满整个容器）又赶上同一个周期里有滚动——区域和滚动
// 自己那条一模一样，而滚动条过渡每一步也发这样一条，不能见到就放弃平移。
static void inv_area_cb(lv_event_t* e)
{
    if (s_lost_over || s_restyling) return;
    lv_display_t* disp = (lv_display_t*)lv_event_get_target(e);
    if (disp->rendering_in_progress) return;        // 渲染里发的这个事件是在问「这块取整后多高」，不是失效
    const lv_area_t* a = (const lv_area_t*)lv_event_get_param(e);

    // 和哪个登记过的容器都不相干的（顶栏上的、别的页面的）不占表
    lv_area_t ca, hit;
    bool      mine = false;
    for (int i = 0; i < s_n && !mine; i++) {
        lv_obj_get_coords(s_ents[i].cont, &ca);
        mine = part_in_cont(a, &ca, &hit);
    }
    if (!mine) return;

    // 表里哪几条盖住了它（和 lv_inv_area 接下来做的去重是同一个判断）：盖满整个容器的（到时候会被收窄的那种），还是别的小失效区
    bool by_whole = false, by_small = false;
    for (uint32_t i = 0; i < disp->inv_p; i++) {
        if (!lv_area_is_in(a, &disp->inv_areas[i], 0)) continue;
        if (lv_area_is_in(&ca, &disp->inv_areas[i], 0)) by_whole = true;
        else                                           by_small = true;
    }
    if (!by_whole && !by_small) return;             // 没被丢
    if (by_small && s_pend_dy == 0) return;

    for (int i = 0; i < s_lost_n; i++)
        if (s_lost[i].dy_at == s_pend_dy && lv_area_is_in(a, &s_lost[i].a, 0)) return;   // 已经记过
    if (s_lost_n >= MAX_LOST) { s_lost_over = true; return; }
    s_lost[s_lost_n++] = {*a, s_pend_dy};
}

// ── 盖在容器上面的东西 ────────────────────────────────────────────────
// 平移挪的是整块帧缓冲：盖在容器上面的东西（悬浮按钮、下拉列表、对话框的遮罩和软键盘）不跟着内容动，
// 它们的像素却会被一起挪走：原处露出底下的内容，偏 dy 的地方多出一份残影，而这一帧本来只重画新露的条带。
// 办法和容器里的小脏区一样：把每个盖着的东西所在的那一块沿滚动方向扩 dy（原处 + 残影处）一起重画。
// 盖着的东西太大（整屏遮罩）、太多时这样画不划算，退回整屏重绘（放弃原因记 cover）。

// 这个对象往屏幕上画不画东西。只认一种「不画」：没有孩子的普通对象，背景、背景图、边框、描边、阴影都没有
// （页面边缘的侧滑返回条就是这样：透明、只接触摸）。别的一律算画。
// 认不出来的：这样的对象上挂了自己画东西的回调（LV_EVENT_DRAW_*）——工程里没有这种写法，有的话要在这里加判断。
static bool draws_nothing(lv_obj_t* o)
{
    if (lv_obj_get_child_count(o) != 0 || lv_obj_get_class(o) != &lv_obj_class) return false;
    return lv_obj_get_style_bg_opa(o, LV_PART_MAIN) == LV_OPA_TRANSP &&
           lv_obj_get_style_bg_image_src(o, LV_PART_MAIN) == nullptr &&
           lv_obj_get_style_border_width(o, LV_PART_MAIN) == 0 &&
           lv_obj_get_style_outline_width(o, LV_PART_MAIN) == 0 &&
           lv_obj_get_style_shadow_width(o, LV_PART_MAIN) == 0;
}

#define MAX_OVER 4      // 一帧里最多照看几个盖着的东西，再多就退回整屏重绘

// o 是画在容器之后的对象：它画出来的东西落在容器里（滚动条带以左）的话，把那一块记进 out。
// 返回 false = 记不下了（太多）
static bool note_overlay(lv_obj_t* o, const lv_area_t* ca, lv_area_t* out, int* n)
{
    if (lv_obj_has_flag(o, LV_OBJ_FLAG_HIDDEN) || lv_obj_get_style_opa(o, LV_PART_MAIN) < LV_OPA_MIN || draws_nothing(o)) return true;
    lv_area_t oa, hit;
    lv_obj_get_coords(o, &oa);
    const int32_t ext = lv_obj_get_ext_draw_size(o);        // 阴影、描边画在框外面
    lv_area_increase(&oa, ext, ext);
    if (!lv_area_intersect(&hit, &oa, ca)) return true;
    if (hit.x1 >= ca->x2 - SB_BAND_W + 1) return true;      // 整个落在滚动条带里：那一带每帧本来就重画
    if (*n >= MAX_OVER) return false;
    out[(*n)++] = hit;
    return true;
}

// 找出不跟着内容动、又画在容器里的东西：容器自己带 FLOATING 的孩子（LVGL 里悬浮按钮的标准写法，滚动时不动）；
// 从容器一路往上到屏幕，每一层里排在后面的兄弟（按子对象的顺序，不是创建的先后：下拉列表打开时会被挪到最后）；再加顶层、系统层上的。
// 返回个数；太多返回 -1。
// 范围按对象自己的框加外扩量算，认不出来的：孩子画到父对象外面（OVERFLOW_VISIBLE）、带缩放 / 旋转的对象、祖先在孩子之后画的东西
// （border_post、自定义的后画回调）。工程里都没有；要用的话在这里补。
static int find_overlays(lv_obj_t* cont, const lv_area_t* ca, lv_area_t* out)
{
    int n = 0;
    const uint32_t kids = lv_obj_get_child_count(cont);
    for (uint32_t i = 0; i < kids; i++) {
        lv_obj_t* kid = lv_obj_get_child(cont, (int32_t)i);
        if (lv_obj_has_flag(kid, LV_OBJ_FLAG_FLOATING) && !note_overlay(kid, ca, out, &n)) return -1;
    }
    for (lv_obj_t* o = cont; lv_obj_get_parent(o); o = lv_obj_get_parent(o)) {
        lv_obj_t*      parent = lv_obj_get_parent(o);
        const uint32_t cnt    = lv_obj_get_child_count(parent);
        for (uint32_t i = (uint32_t)lv_obj_get_index(o) + 1; i < cnt; i++)
            if (!note_overlay(lv_obj_get_child(parent, (int32_t)i), ca, out, &n)) return -1;
    }
    lv_obj_t* const layers[] = {lv_layer_top(), lv_layer_sys()};
    for (lv_obj_t* layer : layers) {
        const uint32_t cnt = lv_obj_get_child_count(layer);
        for (uint32_t i = 0; i < cnt; i++)
            if (!note_overlay(lv_obj_get_child(layer, (int32_t)i), ca, out, &n)) return -1;
    }
    return n;
}

// 把 a 往上扩 up 行、往下扩 down 行，再夹进容器的上下边
static lv_area_t grown(lv_area_t a, int up, int down, const lv_area_t* ca)
{
    a.y1 -= up;
    a.y2 += down;
    if (a.y1 < ca->y1) a.y1 = ca->y1;
    if (a.y2 > ca->y2) a.y2 = ca->y2;
    return a;
}

// REFR_START（渲染前）：判定并实施失效区收窄手术
static void refr_start_cb(lv_event_t* e)
{
    lv_obj_t* cont = s_pend_cont;
    int  dy  = s_pend_dy;
    bool bad = s_pend_bad;
    const int  lost_n    = s_lost_n;        // s_lost[] 里的内容本函数里接着用：下面只把个数清零，本函数不会再触发失效
    const bool lost_over = s_lost_over;
    pend_clear();

    if (!cont || dy == 0) { stat_bump(ST_NOPEND); return; }
    lv_display_t* disp = (lv_display_t*)lv_event_get_target(e);
    // 容器不在当前屏幕上（后台页面被程序滚了一下；滚完同一个周期里换了屏幕）：它的坐标和当前屏幕上那条整块失效可能正好一样，不能拿来收窄
    if (bad || s_dbg_disabled || scrmgr_is_off() || lv_obj_get_screen(cont) != lv_display_get_screen_active(disp)) {
        stat_bump(ST_BAD);
        return;
    }
    int hor = lv_display_get_horizontal_resolution(disp);
    int ver = lv_display_get_vertical_resolution(disp);

    lv_area_t ca;
    lv_obj_get_coords(cont, &ca);
    int ady = dy > 0 ? dy : -dy;
    // 失效区记的是滚动之前的位置时，沿内容挪动的方向扩 dy 行就盖住了前后两处（dy > 0 = 内容上移，新位置在上方）
    const int up_dy = dy > 0 ? dy : 0, down_dy = dy < 0 ? -dy : 0;
    if (ady + STRIP_OVLP >= ca.y2 - ca.y1 + 1) { stat_bump(ST_TOOBIG); return; }  // 一帧滚过大半屏

    // 在失效区里找"恰好等于容器区域"的那条（滚动的整体失效）。
    // 其它与容器相交的脏区（按压态高亮/过渡动画/周期 tick 标签等）不再一票否决：
    // 小脏区收集起来，随位移帧一起"膨胀重绘"——脏区记录的可能是对象移动前的坐标，
    // 平移后对象在新位置（偏 dy），把脏区沿滚动方向扩 dy 覆盖新旧两处即可画对。
    // 之前遇到这类脏区就整帧放弃 → 整屏重绘直写扫出中的前台 FB → 按在可点行上
    // 滑动必撕裂（2026-07-30 实测反馈的根因）。过大/过多仍放弃保正确性。
    int found = -1;
    int dirty_idx[6];
    int dirty_n = 0;
    int dirty_h = 0;                     // 收集脏区的总高（含膨胀量），限流用
    for (uint32_t i = 0; i < disp->inv_p && i < LV_INV_BUF_SIZE; i++) {
        const lv_area_t* a = &disp->inv_areas[i];
        if (a->x1 == ca.x1 && a->y1 == ca.y1 && a->x2 == ca.x2 && a->y2 == ca.y2) {
            if (found < 0) { found = (int)i; continue; }
            stat_bump(ST_NOMATCH);                           // 出现两条同样的？异常，放弃（和「一条都没有」记在一起）
            return;
        }
        lv_area_t tmp;
        if (lv_area_intersect(&tmp, a, &ca) && a->x1 < ca.x2 - SB_BAND_W + 1) {
            if (dirty_n >= (int)(sizeof(dirty_idx) / sizeof(dirty_idx[0]))) {
                stat_bump(ST_DIRTY);                         // 脏区太多：放弃
                return;
            }
            dirty_h += (int)(a->y2 - a->y1 + 1) + ady;
            if (dirty_h > (int)(ca.y2 - ca.y1 + 1) / 2) {
                stat_bump(ST_DIRTY);                         // 脏区太大（重绘过半屏）：放弃
                return;
            }
            dirty_idx[dirty_n++] = (int)i;
        }
    }
    if (found < 0) { stat_bump(ST_NOMATCH); return; }

    // 被 LVGL 去重丢掉的小失效区（失效表里没有，见 inv_area_cb），占新槽位。记下时它在 hit，要重画的地方有三处，取并集：
    //   原处（偏 0）        —— 不跟着内容动的东西（盖在容器上的、悬浮的）变了样、被藏起来或挪走，原处要重画；
    //   偏 rem = dy - dy_at —— 跟着内容动的对象：记下之后内容又挪了 rem，它现在在那里；
    //   偏 dy               —— 不跟着动的东西上一帧画在原处，整块平移之后那些旧像素落在偏 dy 的地方（它这时可能已经藏起来了，find_overlays 看不到）。
    // 一个周期里只朝一个方向滚时 rem 在 0 和 dy 之间，并集就是「沿滚动方向扩 dy」，和表内小脏区一样；中途反过向才会更大。
    // 已经被表里某条小脏区（扩开之后）盖住的不重复加。和上面的小脏区算同一份额度。
    lv_area_t add[MAX_LOST];
    int       add_n = 0;
    if (lost_over) { stat_bump(ST_DIRTY); return; }
    for (int k = 0; k < lost_n; k++) {
        lv_area_t hit;
        if (!part_in_cont(&s_lost[k].a, &ca, &hit)) continue;
        const int       rem  = dy - s_lost[k].dy_at;
        const int       up   = LV_MAX3(0, rem, dy);
        const int       down = LV_MAX3(0, -rem, -dy);
        const lv_area_t g    = grown(hit, up, down, &ca);
        bool            dup  = false;
        for (int j = 0; j < dirty_n && !dup; j++) {
            const lv_area_t d = grown(disp->inv_areas[dirty_idx[j]], up_dy, down_dy, &ca);
            dup = lv_area_is_in(&g, &d, 0);
        }
        if (dup) continue;
        dirty_h += (int)(hit.y2 - hit.y1 + 1) + up + down;
        if (dirty_h > (int)(ca.y2 - ca.y1 + 1) / 2) { stat_bump(ST_DIRTY); return; }
        add[add_n++] = g;
    }

    // 盖在容器上面的东西：和小脏区算在同一份「重绘不过半屏」的额度里
    lv_area_t over[MAX_OVER];
    // 屏幕切换的动画期间旧屏幕可能画在新屏幕上面（prev_scr）：不去细分，那几帧不平移
    const int over_n = disp->prev_scr ? -1 : find_overlays(cont, &ca, over);
    for (int k = 0; k < over_n; k++) dirty_h += (int)(over[k].y2 - over[k].y1 + 1) + ady;
    if (over_n < 0 || dirty_h > (int)(ca.y2 - ca.y1 + 1) / 2) { stat_bump(ST_COVERED); return; }

    int extra = (ca.y1 > 0 ? 1 : 0) + (ca.y2 < ver - 1 ? 1 : 0) +
                (ca.x1 > 0 ? 1 : 0) + (ca.x2 < hor - 1 ? 1 : 0) + 1 + over_n + add_n;
    if (disp->inv_p + extra > LV_INV_BUF_SIZE) { stat_bump(ST_NOROOM); return; }

    if (!lvgl_port_scroll_shift_arm(disp, dy)) { stat_bump(ST_ARMFAIL); return; }  // GDMA 平移
    stat_bump(ST_OK);

    // 收集到的容器内小脏区：沿滚动方向膨胀 dy（覆盖对象移动前后两个位置）并夹进容器。
    // 它们本来就在失效表里，原位改写即可，不占新槽位。
    for (int k = 0; k < dirty_n; k++) {
        disp->inv_areas[dirty_idx[k]]       = grown(disp->inv_areas[dirty_idx[k]], up_dy, down_dy, &ca);
        disp->inv_area_joined[dirty_idx[k]] = 0;
    }
    s_patched  += (uint32_t)(dirty_n + add_n);
    s_lost_cnt += (uint32_t)add_n;

    // ── 手术：整容器失效 → 新露条带；追加"被平移殃及"的容器外带 + 滚动条带 ──
    // 线性平移作用于整个 FB，容器外的顶/底/左/右区域全部重绘自愈（都是窄带，代价小）。
    // 条带按净滚动量算。没管的一种：一个周期里先滚出去又滚回来（方向反过），某个对象正好在整个滚出容器的那一刻变了样
    // （看不见的对象 LVGL 不发失效），滚回来的那几行是平移过来的旧内容。要同一个周期里反向滚动 + 那一刻有变化，没遇到过。
    lv_area_t strip = ca;
    if (dy > 0) strip.y1 = ca.y2 - (ady + STRIP_OVLP) + 1;   // 内容上移：底部露新
    else        strip.y2 = ca.y1 + (ady + STRIP_OVLP) - 1;   // 内容下移：顶部露新
    disp->inv_areas[found]       = strip;
    disp->inv_area_joined[found] = 0;

    uint32_t p = disp->inv_p;
    if (ca.y1 > 0) {          // 容器上方（顶栏）
        lv_area_set(&disp->inv_areas[p], 0, 0, hor - 1, ca.y1 - 1);
        disp->inv_area_joined[p] = 0; p++;
    }
    if (ca.y2 < ver - 1) {    // 容器下方（底栏）
        lv_area_set(&disp->inv_areas[p], 0, ca.y2 + 1, hor - 1, ver - 1);
        disp->inv_area_joined[p] = 0; p++;
    }
    if (ca.x1 > 0) {          // 容器左侧边距
        lv_area_set(&disp->inv_areas[p], 0, ca.y1, ca.x1 - 1, ca.y2);
        disp->inv_area_joined[p] = 0; p++;
    }
    if (ca.x2 < hor - 1) {    // 容器右侧边距
        lv_area_set(&disp->inv_areas[p], ca.x2 + 1, ca.y1, hor - 1, ca.y2);
        disp->inv_area_joined[p] = 0; p++;
    }
    lv_area_set(&disp->inv_areas[p], ca.x2 - SB_BAND_W + 1, ca.y1, ca.x2, ca.y2);  // 滚动条带
    disp->inv_area_joined[p] = 0; p++;
    for (int k = 0; k < over_n; k++) {      // 盖着的东西：原处 + 平移后残影落的地方（内容上移时残影在上方）
        disp->inv_areas[p]       = grown(over[k], up_dy, down_dy, &ca);
        disp->inv_area_joined[p] = 0; p++;
    }
    for (int k = 0; k < add_n; k++) {       // 被 LVGL 去重丢掉的小失效区
        disp->inv_areas[p]       = add[k];
        disp->inv_area_joined[p] = 0; p++;
    }
    disp->inv_p = p;

    s_armed = true;
}

// REFR_READY：等 PPA 清空 → 翻页扫出
static void refr_ready_cb(lv_event_t* e)
{
    // 这一帧开始（REFR_START）之后才报上来的滚动不留到下一帧：LVGL 的排版在 REFR_START 之后做，排版里发生的滚动
    // （内容变短了，它自己把滚动位置拉回来：lv_obj_readjust_scroll，不带动画）这时才发 SCROLL 事件，而它的整容器失效赶上了这一帧、
    // 已经整块画对。留着的话，下一帧只要又有一条整容器失效，就会把已经画对的画面再平移一次，错开的那一截没人补画、一直留在屏幕上。
    if (s_pend_cont && s_pend_dy) s_late++;
    pend_clear();
    if (!s_armed) return;
    s_armed = false;
    lvgl_port_scroll_shift_finish((lv_display_t*)lv_event_get_target(e));

    // 帧节奏观测：每 16 个位移帧打一次间隔（配合 '#' 探针远程验证）
    int64_t now = esp_timer_get_time();
    if (s_last_shift_us && (++s_shift_cnt & 15) == 0) {
        // 分段耗时累加器每次都取走（读取即清零），没人看时也不让它一直累加
        uint32_t ph[6];
        lvgl_port_dbg_shift_get(ph);
        if (scrmgr_profiler_on() && ph[0]) {
            // 帧探针开着：打分段耗时（最近 ~16 个位移帧的平均）：
            // 等围栏（含等上一帧在途的旋转）/ DMA2D 拷贝 / flush 等拷贝 / 等 PPA / arm→finish 总计 / 总计去掉等围栏那一段
            float    n    = (float)ph[0];
            uint32_t work = ph[5] >= ph[1] ? ph[5] - ph[1] : 0;
            ESP_LOGI(TAG, "shift-frame dt=%.1fms | avg(n=%u) fence=%.1f copy=%.1f cpwait=%.1f ppawait=%.1f total=%.1f work=%.1f ms",
                     (now - s_last_shift_us) / 1000.0, (unsigned)ph[0],
                     ph[1] / n / 1000.0f, ph[2] / n / 1000.0f, ph[3] / n / 1000.0f, ph[4] / n / 1000.0f,
                     ph[5] / n / 1000.0f, work / n / 1000.0f);
        } else {
            ESP_LOGI(TAG, "shift-frame dt=%.1fms", (now - s_last_shift_us) / 1000.0);
        }
    }
    s_last_shift_us = now;
}

// 调试：量一段时间里的加速命中情况。begin = true 把统计清零；false 把清零以来的合计完整打一行。
// 平时那一行 stat 是累计（从开机或上次清零算起），而且每秒最多打一次、只在下一个位移帧才打，拿它判「刚才这次滚动有没有全程走加速」
// 会把之前的滚动算进来、把这次的后半段漏掉。帧探针（调试台 '#' / prof on|off）开的时候清零、关的时候打合计，量到的就正好是探针开着的这一段。
// frames = 这一段里的位移帧总数（下面各项之和），ok = 其中走了加速的；frames 等于 ok 就是全部走了加速。
void ui_scroll_accel_dbg_window(bool begin)
{
    if (begin) {
        memset(s_stat, 0, sizeof s_stat);
        s_patched   = 0;
        s_lost_cnt  = 0;
        s_late      = 0;
        return;
    }
    uint32_t frames = 0;
    for (int r = 0; r < ST_N; r++) {
        if (r != ST_NOPEND) frames += s_stat[r];
    }
    ESP_LOGW(TAG, "window frames=%u ok=%u bad=%u big=%u nomatch=%u dirty=%u room=%u armfail=%u cover=%u patch=%u lost=%u late=%u",
             (unsigned)frames, (unsigned)s_stat[ST_OK], (unsigned)s_stat[ST_BAD], (unsigned)s_stat[ST_TOOBIG],
             (unsigned)s_stat[ST_NOMATCH], (unsigned)s_stat[ST_DIRTY],
             (unsigned)s_stat[ST_NOROOM], (unsigned)s_stat[ST_ARMFAIL], (unsigned)s_stat[ST_COVERED], (unsigned)s_patched,
             (unsigned)s_lost_cnt, (unsigned)s_late);
}

void ui_scroll_accel_dbg_enable(bool on)
{
    s_dbg_disabled = !on;
    ESP_LOGW(TAG, "scroll accel %s (debug)", on ? "ON" : "OFF");
}

void ui_scroll_accel(lv_obj_t* cont)
{
    if (!cont || s_n >= MAX_ACCEL || find_ent(cont)) return;
    if (!s_hooked) {
        lv_display_t* d = lv_display_get_default();
        if (!d) return;
        // 注册在 scrmgr 的升频回调之后：升频回调先看到"收窄前"的大失效区 → 滚动照常满速
        lv_display_add_event_cb(d, refr_start_cb, LV_EVENT_REFR_START, nullptr);
        lv_display_add_event_cb(d, refr_ready_cb, LV_EVENT_REFR_READY, nullptr);
        lv_display_add_event_cb(d, inv_area_cb, LV_EVENT_INVALIDATE_AREA, nullptr);
        s_hooked = true;
    }
    s_ents[s_n].cont   = cont;
    s_ents[s_n].last_y = lv_obj_get_scroll_y(cont);
    s_n++;
    lv_obj_add_event_cb(cont, scroll_cb, LV_EVENT_SCROLL, nullptr);
    lv_obj_add_event_cb(cont, restyle_cb, (lv_event_code_t)(LV_EVENT_SCROLL_BEGIN | LV_EVENT_PREPROCESS), nullptr);
    lv_obj_add_event_cb(cont, restyle_cb, (lv_event_code_t)(LV_EVENT_SCROLL_END | LV_EVENT_PREPROCESS), nullptr);
    lv_obj_add_event_cb(cont, cont_delete_cb, LV_EVENT_DELETE, nullptr);
}
