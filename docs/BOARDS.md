# 开发板筛选与支持范围（0.2.0）

先核对芯片、Flash 类型与容量，再核对 USB 口。不能凭品牌名、蓝牙名称、信号强弱或 USB VID 判断兼容性。

| 板型配置 | 固件 | USB 剪贴板桥 | USB 键盘 / PowerShell 初始化 | LED / 按钮 | 验证状态 |
| --- | --- | --- | --- | --- | --- |
| AtomS3U、ESP32-S3、8MB | atoms3u-8m | 实现 | 实现 | 官方 GPIO35 RGB、GPIO41 按钮 | 编译 / 模拟，实机待验 |
| Generic ESP32-S3 SPI NOR 4MB | generic-s3-4m | 实现 | 实现 | 不启用 | 编译 / 模拟，实机待验 |
| Generic ESP32-S3 SPI NOR 8MB | generic-s3-8m | 实现 | 实现 | 不启用 | 编译 / 模拟，实机待验 |
| Generic ESP32-S3 SPI NOR 16MB | generic-s3-16m | 实现 | 实现 | 不启用 | 编译 / 模拟，实机待验 |
| ESP32-C3 / C6 / 经典 ESP32 | 未移植 | 后续可评估 BLE↔串口桥 | 不支持本实现的原生 HID | 未配置 | 路线图 |
| nRF52840 | 未移植 | 需专用固件 | 需专用固件 | 未配置 | 路线图 |
| Pico W / Pico 2 W | 未移植 | 需专用固件 | 需专用固件 | 未配置 | 路线图 |
| ESP32-S2 / 普通 Pico 等无 BLE 板 | 无 | 需要额外无线硬件 | 不构成完整 BLE 方案 | 未配置 | 不支持 |

Generic S3 要求支持 DIO、80MHz 的 SPI NOR，使用 ESP32-S3 原生 USB D− GPIO19 / D+ GPIO20 所在的数据口；仅 USB-UART 接口不能提供这里的 HID。固件禁用 PSRAM，不代表支持所有带不同内存接线的模块。OPI / Octal Flash、加密 Flash、安全启动或安全下载均不接受。按板厂说明手动进入下载模式；通用板不猜测 RESET/BOOT 接法，也不操作未知 GPIO。

固件目录由 `boards/profiles.json` 与对应分区 CSV 生成。首次安装和跨分区升级要求明确清空；同布局正常升级按清单分段写入，保留 0x9000–0xE000 的 NVS。错误芯片、容量、校验或分区不通过即停止。烧录工具不是硬件品牌识别器，最终板型和原生 USB 口仍须用户核对。

设备广播只作发现提示。设备编号由出厂 MAC 派生，连接读取完整 128 位编号并完成独立密钥认证；完整编号重复时停止连接。新板通过物理 USB 绑定，不能靠“附近第一块板”认领。没有已知 LED 的板只能通过 USB 完整编号核对。

硬件依据：[Espressif USB 能力表](https://docs.espressif.com/projects/esp-iot-solution/en/latest/usb/usb_overview/usb_overview.html)、[AtomS3U 官方引脚表](https://docs.m5stack.com/en/core/AtomS3U)、[Nordic nRF52840](https://www.nordicsemi.com/Products/nRF52840)、[Raspberry Pi Pico 系列](https://www.raspberrypi.com/documentation/microcontrollers/pico-series.html)。具备 BLE / USB 硬件只说明存在移植基础，不表示 AtomClip 已实现对应后端。
