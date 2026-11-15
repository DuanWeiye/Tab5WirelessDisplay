// USB-JTAG 调试命令通道（整行命令，回车执行）
//
// 单字符命令的清单在启动时打印一行；多字符命令（wd / open / disp / help）输入 help 查看。
// 仅用于开发调试：没法点屏幕时远程驱动页面与开关、配合日志定位问题。
#pragma once

namespace dbgcon {
void start();
}
