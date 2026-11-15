/*
* SPDX-FileCopyrightText: 2024 Espressif Systems (Shanghai) CO LTD
*
* SPDX-License-Identifier: Apache-2.0
*/

#include "os_header.h"
#include "os_wrapper.h"
#include "esp_log.h"
#include "esp_hosted_api.h"
#include "esp_private/startup_internal.h"

/* 本工程改动：去掉了原来的构造函数 esp_hosted_host_init()（以及配对的析构函数）。
 *
 * 原版靠构造函数在调度器启动之前调 esp_hosted_init()。那个时候内部堆只有最前面的一小块可用
 * （本工程 66KB，其余要等主任务跑起来才并入堆），esp-hosted 一个组件就用掉 27KB（三个 5KB 的任务栈、
 * 队列等），到创建 IDLE 任务时余量不到 17KB；再多占一点，IDLE 的栈就落到 TCM 区，开机断言、循环重启
 * （2026-11-11 实际踩过，见组件根目录 LOCAL_CHANGES.md）。
 *
 * 现在由应用在 app_main 开头显式调用 esp_hosted_init()（main/app_main.cpp）。
 * 没调就用 WiFi 的话，esp_wifi_init() 会在 esp_hosted_reconfigure() 里报
 * "Transport not initialized, call esp_hosted_init() first" 并中止，不会悄悄出错。 */
