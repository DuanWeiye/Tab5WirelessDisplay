#!/usr/bin/env python3
# 给 dependencies/lvgl（v9.2.2，gitignore、纯净 checkout）打临时渲染探针。
# 用法：python3 tools/dispbench/lvgl_probe_patch.py        打补丁（幂等）
#       (cd dependencies/lvgl && git checkout -- src)      还原
# 探针内容：
#   lv_draw_sw.c       按绘制任务类型累计耗时/次数（lv_dbg_task_us / lv_dbg_task_n）
#   lv_draw_sw_fill.c  圆角填充分段计时 + 蒙版缓冲地址（lv_dbg_fill_stat），
#                      lv_dbg_fill_mask_psram=1 时蒙版缓冲强制从 PSRAM 分配（对照实验）
import pathlib, sys
root = pathlib.Path(__file__).resolve().parents[2] / "dependencies/lvgl/src/draw/sw"

def patch(path, pairs):
    s = path.read_text()
    if "[DBG-PROBE]" in s:
        print("已打过补丁，跳过:", path.name); return
    for old, new in pairs:
        if s.count(old) != 1:
            sys.exit("锚点不唯一/不存在: %s: %r" % (path.name, old[:60]))
        s = s.replace(old, new)
    path.write_text(s)
    print("已打补丁:", path.name)

# ── lv_draw_sw.c：按任务类型计时 ─────────────────────────────
patch(root / "lv_draw_sw.c", [
    ('#include "../../core/lv_global.h"\n',
     '#include "../../core/lv_global.h"\n'
     '/* [DBG-PROBE] 临时探针：按绘制任务类型累计耗时 */\n'
     '#include "esp_timer.h"\n'
     'volatile uint32_t lv_dbg_task_us[16];\n'
     'volatile uint32_t lv_dbg_task_n[16];\n'),
    ('    lv_draw_task_t * t = u->task_act;\n    switch(t->type) {\n        case LV_DRAW_TASK_TYPE_FILL:\n            lv_draw_sw_fill((lv_draw_unit_t *)u, t->draw_dsc, &t->area);',
     '    lv_draw_task_t * t = u->task_act;\n'
     '    int64_t dbg_t0 = esp_timer_get_time();   /* [DBG-PROBE] */\n'
     '    switch(t->type) {\n        case LV_DRAW_TASK_TYPE_FILL:\n            lv_draw_sw_fill((lv_draw_unit_t *)u, t->draw_dsc, &t->area);'),
    ('        default:\n            break;\n    }\n\n#if LV_USE_PARALLEL_DRAW_DEBUG',
     '        default:\n            break;\n    }\n'
     '    {   /* [DBG-PROBE] */\n'
     '        uint32_t ty = (uint32_t)t->type < 16 ? (uint32_t)t->type : 15;\n'
     '        __atomic_fetch_add(&lv_dbg_task_us[ty], (uint32_t)(esp_timer_get_time() - dbg_t0), __ATOMIC_RELAXED);\n'
     '        __atomic_fetch_add(&lv_dbg_task_n[ty], 1, __ATOMIC_RELAXED);\n'
     '    }\n\n#if LV_USE_PARALLEL_DRAW_DEBUG'),
])

# ── lv_draw_sw_fill.c：圆角填充分段计时 + 蒙版缓冲地址 ────────
patch(root / "lv_draw_sw_fill.c", [
    ('#include "../lv_draw_mask.h"\n',
     '#include "../lv_draw_mask.h"\n'
     '/* [DBG-PROBE] 临时探针：圆角填充分段计时 + 蒙版缓冲地址 */\n'
     '#include "esp_timer.h"\n'
     '#include "esp_heap_caps.h"\n'
     'volatile uint32_t lv_dbg_fill_mask_psram = 0;   /* 1 = 蒙版缓冲强制从 PSRAM 分配 */\n'
     '/* 0 圆角填充次数 1 总us 2 蒙版准备us 3 蒙版行混合us 4 中段us 5 最近 mask_buf 地址 6 最近栈地址 7 行迭代数 */\n'
     'volatile uint32_t lv_dbg_fill_stat[8];\n'),
    ('    void * mask_list[2] = {NULL, NULL};\n    if(rout > 0) {\n        mask_buf = lv_malloc(clipped_w);\n',
     '    void * mask_list[2] = {NULL, NULL};\n'
     '    bool dbg_mask_ext = false;   /* [DBG-PROBE] 本次蒙版缓冲是否来自 heap_caps（谁分配谁释放） */\n'
     '    if(rout > 0) {\n'
     '        if(lv_dbg_fill_mask_psram) mask_buf = heap_caps_malloc(clipped_w, MALLOC_CAP_SPIRAM);\n'
     '        if(mask_buf) dbg_mask_ext = true;\n'
     '        else mask_buf = lv_malloc(clipped_w);\n'),
    ('    int32_t h;\n\n    lv_area_t blend_area;\n',
     '    int32_t h;\n'
     '    int64_t dbg_t0 = esp_timer_get_time();   /* [DBG-PROBE] */\n'
     '    uint32_t dbg_prep = 0, dbg_blend = 0, dbg_iter = 0;\n\n'
     '    lv_area_t blend_area;\n'),
    ('        bool preblend = false;\n\n        /* Initialize the mask to opa instead of 0xFF and blend with LV_OPA_COVER.\n',
     '        bool preblend = false;\n'
     '        int64_t dbg_ta = esp_timer_get_time();   /* [DBG-PROBE] */\n\n'
     '        /* Initialize the mask to opa instead of 0xFF and blend with LV_OPA_COVER.\n'),
    ('        if(blend_dsc.mask_res == LV_DRAW_SW_MASK_RES_FULL_COVER) blend_dsc.mask_res = LV_DRAW_SW_MASK_RES_CHANGED;\n\n        bool hor_grad_processed = false;\n',
     '        if(blend_dsc.mask_res == LV_DRAW_SW_MASK_RES_FULL_COVER) blend_dsc.mask_res = LV_DRAW_SW_MASK_RES_CHANGED;\n'
     '        int64_t dbg_tb = esp_timer_get_time();   /* [DBG-PROBE] */\n\n'
     '        bool hor_grad_processed = false;\n'),
    ('            lv_draw_sw_blend(draw_unit, &blend_dsc);\n        }\n    }\n\n    /* Draw the center of the rectangle.*/\n',
     '            lv_draw_sw_blend(draw_unit, &blend_dsc);\n        }\n'
     '        dbg_prep += (uint32_t)(dbg_tb - dbg_ta);   /* [DBG-PROBE] */\n'
     '        dbg_blend += (uint32_t)(esp_timer_get_time() - dbg_tb);\n'
     '        dbg_iter++;\n'
     '    }\n'
     '    int64_t dbg_tc = esp_timer_get_time();   /* [DBG-PROBE] */\n\n'
     '    /* Draw the center of the rectangle.*/\n'),
    ('    if(mask_buf) {\n        lv_free(mask_buf);\n',
     '    if(mask_buf) {   /* [DBG-PROBE] */\n'
     '        int64_t dbg_td = esp_timer_get_time();\n'
     '        __atomic_fetch_add(&lv_dbg_fill_stat[0], 1, __ATOMIC_RELAXED);\n'
     '        __atomic_fetch_add(&lv_dbg_fill_stat[1], (uint32_t)(dbg_td - dbg_t0), __ATOMIC_RELAXED);\n'
     '        __atomic_fetch_add(&lv_dbg_fill_stat[2], dbg_prep, __ATOMIC_RELAXED);\n'
     '        __atomic_fetch_add(&lv_dbg_fill_stat[3], dbg_blend, __ATOMIC_RELAXED);\n'
     '        __atomic_fetch_add(&lv_dbg_fill_stat[4], (uint32_t)(dbg_td - dbg_tc), __ATOMIC_RELAXED);\n'
     '        lv_dbg_fill_stat[5] = (uint32_t)(uintptr_t)mask_buf;\n'
     '        lv_dbg_fill_stat[6] = (uint32_t)(uintptr_t)&dbg_td;\n'
     '        __atomic_fetch_add(&lv_dbg_fill_stat[7], dbg_iter, __ATOMIC_RELAXED);\n'
     '    }\n'
     '    if(mask_buf) {\n'
     '        if(dbg_mask_ext) heap_caps_free(mask_buf);\n'
     '        else lv_free(mask_buf);\n'),
])
