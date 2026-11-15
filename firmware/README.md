# Tab5 无线显示固件

ESP-IDF 5.4.2 + LVGL 9，深色主题，横屏 1280×720（面板原生竖屏 720×1280，显示管线里转 90°）。这个固件只做无线显示（整体说明见仓库根目录的 `README.md`，协议见 `../windisplay/PROTOCOL.md`）。
联网只走 WiFi（板载 ESP32-C6，经 SDIO，esp-hosted）。

## 界面和分层

- 一共四页（主页面、设置页一屏放完、不用滚；WiFi 页扫出的网络多了才滚）：**主页面**（中间是连接状态：图标加一行大字「等待连接 / 已连接 / 已暂停 / 正在联网 / 未联网」，暂停时多一个「开始显示」；
  下面两行是常动的：亮度、音量；画质四档、已配对电脑、性能信息）、**设置页**（顶栏的齿轮进：WiFi、触摸板模式、触摸板加速、息屏时间、充电上限、信息）、
  **已配对的电脑**页（主页面进）、**WiFi** 页（设置页进；主页面没联网时点状态也到这里）。配对密码框挂在顶层，界面在哪一页都弹。
- **服务和界面分开**：`wdisp`（无线显示接收端）开机就监听，不属于哪一页；`app`（应用层）管它和界面之间的事——运行 / 暂停、配对密码框、
  电脑的画面要上屏时点亮屏幕、画面结束把界面还回来；界面和调试命令改设置都走 `app::set_*`。页面只读状态来画自己。
- **息屏**（`ui/screen_manager.cpp`）：界面在屏幕上的时候（等连接、握手、等人输配对密码、暂停着）到时间关背光，照常监听；电脑的画面上屏时自动点亮；
  要人输配对密码时先亮屏；双击亮屏。显示期间不息屏。没有锁屏。

## 构建 / 烧录

```bash
./build.sh        # 仅编译（会自动 source ~/esp/esp-idf-v5.4.2；dependencies/lvgl 没有的话先从上游取 v9.2.2）
./build.sh -f     # 编译 + 烧录（只烧到 Tab5 的 USB-JTAG 口）
```

烧录和测试脚本用的串口、Tab5 的局域网地址由 `tools/tab5_env.sh` 定：环境变量 `TAB5_PORT` / `TAB5_IP`，或者写在 `tools/local.env` 里（不入库）。
串口没给时在 `/dev/serial/by-id` 里找 Espressif 的 USB-JTAG 口，正好一个才用，免得烧到别的设备上。

也可以进 `build/` 用 esptool 直接烧：`python -m esptool --chip esp32p4 -p <by-id 口> -b 460800 write_flash "@flash_args"`。

## 结构

```
main/
  app_main.cpp        入口；内部 RAM 连续 DMA 块的低频监控
  app.*               应用层：运行 / 暂停、配对密码框、屏幕的交接、设置的统一入口（set_*）
  board.*             板级：I2C / IO 扩展器 / INA226 / 显示 / 亮度 / 充电控制 / CPU 满速锁
  settings_store.*    NVS 设置（读谁都行，改只经 app::set_*）
  debug_console.*     USB-JTAG 调试台（整行命令，回车执行；help 列出多字符命令）
  wdisp.*             无线显示接收端：监听、握手、收帧、硬件解码、翻页、输入回传
  wdisp_crypto.* wdisp_pair.*   配对、认证、逐报文加密；配对过的电脑（NVS）
  wd_audio.*          电脑的声音（PCM → 扬声器）
  wd_osd.*            显示期间叠在画面上的调试信息（帧率、延迟）
  audio_mic.*         麦克风（声音自检的调试命令用）
  net/
    wifi.*            WiFi STA 管理（esp_wifi_remote → C6）
    worker.*          常驻的联网工作任务（收包自测、C6 OTA 排到这里跑，见踩坑 7）
    nettest.*         收包自测（调试台 netrx）
  utils/
    hwjpeg.*          硬件 JPEG 解码引擎（全工程一个）
    tab5_kbd.* kbd_keymap.hpp   键盘模块 A164 驱动和键码表
  ui/
    theme.*           配色 / 字体 / 卡片 / 设置行 / 分段选择 / 顶栏 / 确认框
    ui_root.cpp       初始化、返回和亮屏恢复的导航
    ui_main.cpp       主页面
    ui_settings.cpp   设置页
    ui_wifi.cpp ui_pairs.cpp     WiFi 页、已配对的电脑页
    ui_pairbox.*      配对密码框（顶层）
    ui_keyboard.cpp   软键盘
    screen_manager.cpp   息屏 / 双击亮屏 / 来画面时点亮 / 按需升降 CPU 频率 / 帧探针 / 滚动基准页
    ui_transition.cpp    页面切换的淡入淡出
    scroll_accel.cpp     滚动位移复用（滚动不掉帧的框架，用法见下面「滚动加速」）
    fonts/               字库（tools/gen_font.py 生成，20 / 28 号）
components/
  esp_hosted/         **工程内改过的副本**，改之前读 components/esp_hosted/LOCAL_CHANGES.md
  m5stack_tab5/ espressif__esp_lvgl_port/ esp_lcd_st7121/ power_monitor_ina226/   BSP 等（来自官方 UserDemo，显示管线改过）
c6_slave/             板载 C6 上跑的 esp-hosted 从机工程（官方 1.4.0 的 slave 源码，没改；出厂固件就是它。免拆机更新的办法见 main/debug_console.cpp 的「协处理器固件维护」）
tools/
  dispbench/          显示 / 网络 / 无线显示的真机测试脚本（日志写到它下面的 logs/，不入库；ser.sh 抓串口并发命令，wd_regress.sh 无线显示回归）
  fbshot/             经 USB-JTAG 截帧缓冲
  kbdtest/ ackholdtest/ wdvec/   主机侧单测和测试向量
  cryptobench/ heapwatch/   诊断工具，平时不编进固件
  gen_font.py         字库生成
```

## 踩坑备忘

1. **LVGL 9 必须 `CONFIG_LV_USE_CLIB_MALLOC=y` + `CONFIG_SPIRAM_USE_MALLOC=y`**：用内置 64KB 池时绘制层分配失败 → lv_draw 死循环 → 看门狗。
2. **别用整屏 FADE 切屏**：720p 的 ARGB 合成层一帧约 3.7MB。页面切换用 `ui_transition.cpp` 的淡出 → 建页 → 淡入。
3. **`sw_rotate` 必须保持 true**（`board.cpp`）：这份改过的 esp_lvgl_port 里它走的其实是 PPA 硬件旋转；改成 false 会触发面板不支持的 swap_xy。
4. **改配置时 `sdkconfig` 和 `sdkconfig.defaults` 两个都改**，不要删掉 `sdkconfig` 重新生成（会丢手工配置）。`sdkconfig.defaults` 里整数项后面不能加行内注释。
5. 开发用的 Tab5 是新批次：面板 ST7121、触摸 ST7123；只有 ST712x 面板配了三块帧缓冲，才能跑无线显示。
6. **esp-hosted（SDIO → C6）**：4-bit 模式下 D1 引脚由 `CONFIG_ESP_HOSTED_SDIO_PRIV_PIN_D1_4BIT_BUS` 决定（只设 `PIN_D1` 无效，默认值和复位脚冲突，开机就重启循环）；
   从机目标要显式 `CONFIG_SLAVE_IDF_TARGET_ESP32C6=y`。
7. **联网作业必须跑在常驻任务里（`net/worker.cpp`），不能「用完自删」**：用过 pthread-TLS 的任务（WiFi 的 mbedtls / lwip 会用）自删时会误判 abort。
8. **抓串口日志用 `tools/dispbench/ser.sh`（`stty` + `cat` 重定向到文件）**，不要用 pyserial 裸开 USB-JTAG 口：默认拉 RTS = P4 的 EN，芯片被摁在复位。
9. **挂在页面上的弹窗（modal）必须 `LV_OBJ_FLAG_IGNORE_LAYOUT`**：页面根是 flex 纵向布局，遮罩参与布局会被排到内容下面、飞出屏幕（例：`ui_wifi.cpp`）。
   挂在顶层（`lv_layer_top()`）的弹窗（确认框、配对密码框）息屏时会被整个清掉——留着的话盖在黑屏页上，双击落在它身上，屏幕叫不醒；
   亮屏后还需要它的，由拥有者重新弹（配对密码框见 `app.cpp` 的 `on_wake`）。
10. **带 LV_SYMBOL 图标的控件用 Montserrat 字体**：中文字库里没有图标字符，会显示成方块。
11. 本平台的硬件加密（AES / SHA / MPI / ECC）不可靠，`sdkconfig` 里都关着，一律用软件实现。
12. 性能相关的结论（滚动位移复用、LP RAM 不进堆、内部 DMA 内存的红线等）写在相应代码的注释、`sdkconfig.defaults` 的说明和 `components/*/LOCAL_CHANGES.md` 里，改显示管线或内存布局前先读它们。

## 滚动加速（`ui/scroll_accel.cpp`）

用手指滚动的页面不掉帧靠的是「滚动位移复用」：双帧缓冲 + DMA2D 错位拷贝，只重绘新露出来的条带，在帧边界翻页。

- **新增「用户手指滚动的容器」时默认接入**：容器建好后调 `ui_scroll_accel(cont)`（声明在 `ui/ui.h`），逐容器接入，别处不用改。
- **三个适用条件**（不满足会自动退回整屏重绘，不会画错，只是没加速）：容器横向占满整宽（或只留窄边距）；纯纵向滚动；
  滚动区里没有不随内容移动的浮层（悬浮按钮、固定输入栏）。有浮层时把它挪到滚动容器外面（做成兄弟节点）再接入。
  挪出去之后还盖在容器上的（排在后面的兄弟、`lv_layer_top()` 上的确认框、下拉列表、对话框遮罩）和容器里带 `LV_OBJ_FLAG_FLOATING` 的孩子由框架处理：
  小的随位移帧一起重画，大的那几帧退回整屏重绘（统计里记 `cover`）。框架认不出来的情况列在 `find_overlays` 的注释里。
- **不要另起炉灶**给某个页面单写滚动优化（分条渲染、私有双缓冲、直接写帧缓冲都试过、都有坑）；不够用就改 `scroll_accel.cpp`，让所有页面受益。
- **改完真机实测**：调试台 `prof on`（开帧探针）→ `%`（自动滚动）→ `prof off`，看关探针时打的 `scrlacc: window frames=… ok=…`：
  `frames` 是位移帧总数，`ok` 是其中走了加速的，两者应相等。再用眼睛看方向和撕裂——有些毛病统计完全正常、只有肉眼看得出来。
- 改 `scroll_accel.cpp` 本身时另外跑滚动基准页的 F、G 变体（调试台 `bench f` / `bench g`，说明在 `screen_manager.cpp` 的 `churn_tick` 上面）：
  `prof on` → 等几秒 → `prof off`，`frames` 应等于 `ok`、`lost` 不为 0；F 滚动中用 `tools/fbshot/fbshot.sh` 截前台帧缓冲，所有色块应是同一个颜色、悬浮块上下不带残影。
