/*
 * SPDX-FileCopyrightText: 2015-2023 Espressif Systems (Shanghai) CO LTD
 * SPDX-License-Identifier: Apache-2.0
 */

#ifndef __MEMPOOL_H__
#define __MEMPOOL_H__

#include <string.h>
#include <stdio.h>
#include <sys/queue.h>
#include "os_wrapper.h"


#define MEMPOOL_OK                       0
#define MEMPOOL_FAIL                     -1


#define LOG                              printf

#define MEMPOOL_NAME_STR_SIZE            32

#define MEMPOOL_ALIGNMENT_BYTES          64
#define MEMPOOL_ALIGNMENT_MASK           (MEMPOOL_ALIGNMENT_BYTES-1)
#define IS_MEMPOOL_ALIGNED(VAL)          (!((VAL)& MEMPOOL_ALIGNMENT_MASK))
#define MEMPOOL_ALIGNED(VAL)             ((VAL) + MEMPOOL_ALIGNMENT_BYTES - \
                                             ((VAL)& MEMPOOL_ALIGNMENT_MASK))

#define MEMSET_REQUIRED                  1
#define MEMSET_NOT_REQUIRED              0


#ifdef H_USE_MEMPOOL
struct mempool_entry {
	SLIST_ENTRY(mempool_entry) entries;
};

typedef SLIST_HEAD(slisthead, mempool_entry) mempool_t;

struct mempool {
	mempool_t head;
	void * spinlock;
	uint32_t block_size;
	/* ---- 以下为本工程新增（说明见组件根目录 LOCAL_CHANGES.md）---- */
	uint32_t heap_caps;      /* 块从哪种内存来：0 = 内部 DMA 内存（原行为）；否则按这组 heap caps 分配（如 PSRAM） */
	uint16_t max_blocks;     /* 向堆要来的块数上限（在用的 + 空闲链表里的）；0 = 不限（原行为） */
	uint16_t max_cached;     /* 空闲链表最多留几块，多出来的还给堆；0 = 全留着（原行为：只增不减） */
	uint16_t num_blocks;     /* 现在向堆要来了几块 */
	uint16_t num_cached;     /* 现在空闲链表里有几块 */
	uint32_t num_alloc_fail; /* 累计分配失败次数（到了上限，或堆里没内存） */
};
#endif

struct mempool * mempool_create(uint32_t block_size);
/* 带上限的内存池（本工程新增）。heap_caps / max_blocks / max_cached 的含义见 struct mempool。
 * 分配失败时 mempool_alloc 返回 NULL，调用方必须处理（丢包 / 返回错误），不能断言。 */
struct mempool * mempool_create_ex(uint32_t block_size, uint32_t heap_caps,
		uint16_t max_blocks, uint16_t max_cached);
void mempool_destroy(struct mempool* mp);
void * mempool_alloc(struct mempool* mp, int nbytes, int need_memset);

/* 内存池用量（本工程新增，诊断用） */
struct mempool_stats {
	uint16_t num_blocks;      /* 现在向堆要来了几块（在用的 + 空闲的） */
	uint16_t max_blocks;      /* 上限，0 = 不限 */
	uint16_t num_cached;      /* 其中空闲几块 */
	uint32_t num_alloc_fail;  /* 累计分配失败次数 */
};
void mempool_get_stats(struct mempool* mp, struct mempool_stats *out);
void mempool_free(struct mempool* mp, void *mem);
#endif
