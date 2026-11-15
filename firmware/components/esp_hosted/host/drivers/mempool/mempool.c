// Copyright 2015-2022 Espressif Systems (Shanghai) PTE LTD
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

#include "mempool.h"
#include "esp_hosted_config.h"
#include "stats.h"
#include "esp_log.h"
#include "esp_heap_caps.h"
#define MEMPOOL_DEBUG 1


static char * MEM_TAG = "mpool";
#if H_MEM_STATS
#include "esp_log.h"


#endif

/* 内部 DMA 内存的块要直接交给 SDIO 做 DMA：对齐必须是内部内存数据缓存行（ESP32-P4 上是 L1 缓存行）的
 * 整数倍。MEM_ALLOC / esp_dma_capable_malloc 是运行时按这个条件分配的，这里自己分配，编译期核对。 */
#if defined(CONFIG_CACHE_L1_CACHE_LINE_SIZE)
_Static_assert((MEMPOOL_ALIGNMENT_BYTES % CONFIG_CACHE_L1_CACHE_LINE_SIZE) == 0,
		"内存池的对齐必须是内部内存数据缓存行的整数倍");
#endif

struct mempool * mempool_create(uint32_t block_size)
{
	/* 原接口：内部 DMA 内存、不设上限、空闲块全留着 */
	return mempool_create_ex(block_size, 0, 0, 0);
}

struct mempool * mempool_create_ex(uint32_t block_size, uint32_t heap_caps,
		uint16_t max_blocks, uint16_t max_cached)
{
#ifdef H_USE_MEMPOOL
	struct mempool * new = (struct mempool *)g_h.funcs->_h_malloc(MEMPOOL_ALIGNED(sizeof(struct mempool)));

	if (!new) {
		ESP_LOGE(MEM_TAG, "Prob to create mempool size(%u)", MEMPOOL_ALIGNED(sizeof(struct mempool)));
		return NULL;
	}

	if (!IS_MEMPOOL_ALIGNED((long)new)) {

		ESP_LOGV(MEM_TAG, "Nonaligned");
		g_h.funcs->_h_free(new);
		new = (struct mempool *)g_h.funcs->_h_malloc(MEMPOOL_ALIGNED(sizeof(struct mempool)));
	}

	if (!new) {
		ESP_LOGE(MEM_TAG, "failed to create mempool size(%u)", MEMPOOL_ALIGNED(sizeof(struct mempool)));
		return NULL;
	}

	new->spinlock = g_h.funcs->_h_create_lock_mempool();

	new->block_size = MEMPOOL_ALIGNED(block_size);
	SLIST_INIT(&(new->head));

	new->heap_caps = heap_caps;
	new->max_blocks = max_blocks;
	new->max_cached = max_cached;
	new->num_blocks = 0;
	new->num_cached = 0;
	new->num_alloc_fail = 0;


	ESP_LOGV(MEM_TAG, "Create mempool %p with block_size:%lu", new, (unsigned long int)block_size);
	return new;
#else
	return NULL;
#endif
}

void mempool_destroy(struct mempool* mp)
{
#ifdef H_USE_MEMPOOL
	void * node1 = NULL;

	if (!mp)
		return;


	ESP_LOGV(MEM_TAG, "Destroy mempool %p", mp);

	while ((node1 = SLIST_FIRST(&(mp->head))) != NULL) {
		SLIST_REMOVE_HEAD(&(mp->head), entries);
		g_h.funcs->_h_free(node1);
	}
	SLIST_INIT(&(mp->head));

	g_h.funcs->_h_free(mp);
#endif
}

void * mempool_alloc(struct mempool* mp, int nbytes, int need_memset)
{
	void *buf = NULL;

#ifdef H_USE_MEMPOOL
	if (!mp || mp->block_size < nbytes)
		return NULL;


	g_h.funcs->_h_lock_mempool(mp->spinlock);

	if (!SLIST_EMPTY(&(mp->head))) {
		buf = SLIST_FIRST(&(mp->head));
		SLIST_REMOVE_HEAD(&(mp->head), entries);
		mp->num_cached--;

		g_h.funcs->_h_unlock_mempool(mp->spinlock);

#if H_MEM_STATS
		h_stats_g.mp_stats.num_reuse++;
		ESP_LOGV(MEM_TAG, "%p: num_reuse: %lu", mp, (unsigned long int)(h_stats_g.mp_stats.num_reuse));
#endif
	} else {
		/* 空闲链表空了，要向堆要新块。先在锁里把名额占上：到上限就直接失败，
		 * 不让池子无限制地长（原实现没有上限，突发流量能把内部 DMA 内存吃光）。 */
		if (mp->max_blocks && mp->num_blocks >= mp->max_blocks) {
			mp->num_alloc_fail++;
			g_h.funcs->_h_unlock_mempool(mp->spinlock);
			return NULL;
		}
		mp->num_blocks++;

		g_h.funcs->_h_unlock_mempool(mp->spinlock);

		/* 内部 DMA 内存的块和 MEM_ALLOC（esp_dma_capable_malloc）分配出来的一样：MALLOC_CAP_DMA、
		 * 64 字节对齐、长度是 64 的整数倍（缓存行在文件开头编译期核对过）。不直接用 MEM_ALLOC，是因为它
		 * 每失败一次就打一行错误日志；带上限的池子在内存吃紧时拿不到块是预期内的事（调用方丢包并限流
		 * 告警），逐次打日志会拖慢正在收发包的任务。 */
		buf = heap_caps_aligned_alloc(MEMPOOL_ALIGNMENT_BYTES, MEMPOOL_ALIGNED(mp->block_size),
				mp->heap_caps ? mp->heap_caps : MALLOC_CAP_DMA);

		if (!buf) {
			/* 堆里没内存：把名额退回去，交给调用方处理 */
			g_h.funcs->_h_lock_mempool(mp->spinlock);
			mp->num_blocks--;
			mp->num_alloc_fail++;
			g_h.funcs->_h_unlock_mempool(mp->spinlock);
		}
#if H_MEM_STATS
		h_stats_g.mp_stats.num_fresh_alloc++;
		ESP_LOGV(MEM_TAG, "%p: num_alloc: %lu", mp, (unsigned long int)(h_stats_g.mp_stats.num_fresh_alloc));
#endif
	}
#else
	buf = g_h.funcs->_h_malloc_align(MEMPOOL_ALIGNED(nbytes), MEMPOOL_ALIGNMENT_BYTES);
#endif
	ESP_LOGV(MEM_TAG, "alloc %u bytes at %p", nbytes, buf);

	if (buf && need_memset)
		g_h.funcs->_h_memset(buf, 0, nbytes);

	return buf;

}

void mempool_free(struct mempool* mp, void *mem)
{
	if (!mem)
		return;
#ifdef H_USE_MEMPOOL
	if (!mp)
		return;

	g_h.funcs->_h_lock_mempool(mp->spinlock);

	if (mp->max_cached && mp->num_cached >= mp->max_cached) {
		/* 备用块已经够数：这一块还给堆。原实现只增不减，一次突发撑大以后那些内存就再也回不来了。 */
		mp->num_blocks--;
		g_h.funcs->_h_unlock_mempool(mp->spinlock);
		g_h.funcs->_h_free(mem);
		return;
	}

	SLIST_INSERT_HEAD(&(mp->head), (struct mempool_entry *)mem, entries);
	mp->num_cached++;

	g_h.funcs->_h_unlock_mempool(mp->spinlock);

#if H_MEM_STATS
	h_stats_g.mp_stats.num_free++;
	ESP_LOGV(MEM_TAG, "%p: num_ret: %lu", mp, (unsigned long int)(h_stats_g.mp_stats.num_free));
#endif

#else
	ESP_LOGV(MEM_TAG, "free at %p", mem);
	g_h.funcs->_h_free_align(mem);
#endif
}

void mempool_get_stats(struct mempool* mp, struct mempool_stats *out)
{
	if (!out)
		return;
	memset(out, 0, sizeof(*out));
#ifdef H_USE_MEMPOOL
	if (!mp)
		return;
	g_h.funcs->_h_lock_mempool(mp->spinlock);
	out->num_blocks = mp->num_blocks;
	out->max_blocks = mp->max_blocks;
	out->num_cached = mp->num_cached;
	out->num_alloc_fail = mp->num_alloc_fail;
	g_h.funcs->_h_unlock_mempool(mp->spinlock);
#endif
}
