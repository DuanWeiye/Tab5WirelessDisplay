# esp_lcd 本工程副本：相对 IDF v5.4.2 的改动

本目录是 ESP-IDF v5.4.2 的 `components/esp_lcd` 原样搬进来的（不含 `test_apps`）。工程 `components/` 下的同名组件会盖过 IDF 自带的那一份。

看具体改了什么：和 ESP-IDF v5.4.2 的 `components/esp_lcd` 逐文件对比（改动只在下面列的两个文件里）。

## 为什么要副本

无线显示要在运行中把帧缓冲的色彩格式从 RGB565（本机界面）切到 YUV422（投屏，没有色阶断层）再切回来。
IDF 只能在建面板时定格式；运行中切要动取数的 DMA 通道，它是 `esp_lcd_dpi_panel_t` 里的私有成员，组件外面够不着。

## 改了什么（都在 `dsi/esp_lcd_panel_dpi.c` 和 `dsi/include/esp_lcd_mipi_dsi.h`，标着「本工程加的 / 改的」）

- **新接口 `esp_lcd_dpi_panel_switch_color_format()`**：面板在显示的时候切换帧缓冲的色彩格式（每像素位数相同的格式之间）。
  顺序：欠载填充色设黑 → 请帧结束中断别重启 DMA（DMA 送完一整帧自己停下）→ 按那一刻往后算，等到下一帧有效行的中段、
  核对桥的 FIFO 是空的 → 桥改 `pixel_type` / `yuv_cfg` → 主机经影子寄存器在下一帧帧头换色彩编码 → DMA 从帧缓冲开头重送。
  为什么是这个顺序写在函数前面的注释里。
- 帧结束中断 `dma_trans_done_cb`：每次记下时刻和帧周期（`frame_end_us` / `frame_us`，切换时用来算「现在在一帧里的什么位置」）。
  量出来的帧周期离标称值（建面板时按时序和实际分频算的 `nominal_frame_us`）超过 3% 的不收：中断晚到、漏了一次时量出来的不是帧周期。
  切换函数等 DMA 停住之后才取这两个数，而且只在停住之后的头 3 帧里找改格式的时刻（隔得越久推出来的位置越偏），过了就不改、返回超时；
  `hold_dma` 立着的那一次不重启 DMA（`dma_held`），由切换函数改完格式之后重启。
- `esp_lcd_dpi_panel_t` 加了标志 `switching_format`：切换时有意让桥断流，那之后头一两帧报的欠载不打印
  （`dma_trans_done_cb` 里见到第一个没有欠载的帧就恢复照常报）。建面板时另外记下场消隐和一帧的行数。
- `on_refresh_done` 回调的事件数据（原来是空结构、传 NULL）加了 `next_fb`：帧结束中断这一次实际挂上去的那块帧缓冲。
  翻页（`draw_bitmap` 传帧缓冲内部的指针，只改 `cur_fb_index`）在下一个帧结束中断里生效，但中断在开头就读了 `cur_fb_index`、
  到末尾才回调；登记翻页的任务在另一个核上正好落在这中间的话，这一次挂的还是旧的那一块。移植层的翻页围栏
  （`esp_lvgl_port_disp.c` 的 `lvgl_port_flip_fence_refresh_cb`）拿 `next_fb` 和登记的那一块对上了才算生效。
  切色彩格式停住 DMA 的那一次报 NULL；切换函数重启 DMA 的那一次不走回调，那时挂上去的那一块要到再下一个帧结束中断才报。
- **新接口 `esp_lcd_dpi_panel_get_frame_period_us()`**：屏幕的帧周期（上面量的那个；还没量到时给标称值）。无线显示把它报给电脑，限帧率的两档画质按它算。
- `CMakeLists.txt`：私有依赖加了 `esp_timer`（帧结束中断里取时刻）。

别的文件没动。

## 注意

- **显示起来之后不要调 `esp_lcd_panel_io_rx_param()` 读面板**：IDF 的读实现会把 DSI 主机切到命令模式、读完不切回来，
  屏幕全黑，`esp_lcd_dpi_panel_switch_color_format()` 也会因为主机不接影子请求而超时。（这是 IDF 原有的行为，不是副本改出来的。）
- 升级 IDF 时：把新版的 `components/esp_lcd` 搬进来，再把上面这些改动挪过去；切换的顺序依赖桥和主机的硬件行为，换了芯片版本要重新上机验。
