// 持久网络工作任务（作业队列）
//
// ⚠️ 为什么需要它：P4 的 .flash.text 映射在 0x4800_0000，而 esp_ptr_executable 认的
//    IROM 范围是 0x4000_0000–0x4400_0000，不含 0x48xxxxxx。于是凡是用过 pthread 线程
//    局部存储的任务（WiFi 的 mbedtls/lwip 路径会用）一旦被删除，FreeRTOS 的 TLSP 删除
//    回调校验会把合法的 pthread 回调误判为"非可执行"而 abort（整机重启）。
//    对策：联网作业一律丢到这个**永不删除**的常驻任务里跑，绝不"用完自删"。
#pragma once

namespace netw {

// 启动常驻工作任务（幂等）。app_main 调用。
void start();

// 投递一个在常驻网络任务里执行的作业。返回 false = 队列满/未启动。
// fn 在工作任务栈上运行（20KB）；arg 生命周期由调用方/作业自理。
bool submit(void (*fn)(void*), void* arg);

}  // namespace netw
