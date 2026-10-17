# 第三方组件

AtomClip 自有源码采用根目录的 MIT 许可。第三方工具、固件库及其派生产物保留各自许可，整个发行包不能统一视为 MIT。

| 组件 | 版本 | 许可与对应源码 |
| --- | --- | --- |
| esptool 独立 Windows 工具 | 5.3.1 | GPL-2.0-or-later；[对应源码](https://github.com/espressif/esptool/tree/v5.3.1)，完整源代码归档随配套发行包保留；许可见 esptool-LICENSE |
| Arduino ESP32 核心及 USB 库 | 3.3.12 | 相关文件 LGPL-2.1-or-later；[对应源码](https://github.com/espressif/arduino-esp32/tree/3.3.12)，见 LGPL-2.1.txt；本项目未修改上游 USB 库 |
| NimBLE-Arduino | 2.5.1 | Apache-2.0 及其依赖通知；[对应源码](https://github.com/h2zero/NimBLE-Arduino/tree/2.5.1)，见 NimBLE-Arduino-LICENSE |
| ESP-IDF、mbedTLS 等固件底层依赖 | 由上述 Arduino 包固定 | 各组件许可及源码随 [ESP-IDF](https://github.com/espressif/esp-idf) 和对应 Arduino 包提供 |

外网 EXE 以压缩资源形式携带独立 esptool，烧录时临时解包并作为单独进程运行；配套发布应同时保留其许可证和对应源码归档。Windows 菜单 **更多 → 关于 / 开源许可** 可查看内置许可证。

Windows 客户端使用系统 .NET Framework 4.8、WinForms、Windows BLE API 和 bcrypt.dll，不分发 .NET 运行库。Android 使用系统 API 和 Java 加密/压缩接口，无 AndroidX 或其他第三方应用库。

构建工具 JDK、Android SDK、Arduino CLI 和 ESP32 工具链不进入源码包。依赖应从各自官方渠道准备并遵守其发行许可。
