// 诊断工具，平时不编进固件。说明见 cryptobench.cpp 开头。
#pragma once

namespace cryptobench {
// 把几种软件加密算法各跑一遍，结果打到日志里（约 45 秒）。在调试台任务上跑。
void run_all();
}
