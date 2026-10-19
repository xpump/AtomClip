> 当前交付为 2026-10-19 整合的 **0.2.0 公开配套**（Android code 61），速度更新、验证及产物见 [VUC_SPEED_020.md](VUC_SPEED_020.md)。下面保留历史工作版记录，非当前版本号。

# 0.2.1 产物摘要（本地审查包）

2026-10-19。Windows / Android 客户端更新；配套固件保持 0.2.0。未发布 GitHub，两套配套不能混用。

| 配套 | 文件 | 字节数 | SHA256 |
| --- | --- | ---: | --- |
| 私人 | Windows/USB/AtomClipUSB.exe | 61,440 | `e995d1c9678eef2b9be157940b3b1228d426d1edc76ddf4d518c96ed994a8162` |
| 私人 | Windows/BLE/AtomClipBLE.exe | 16,247,296 | `5308b1e04e649d9b1ec0cd8c6c42610a6796dce236a2f1ab280064af04b38a19` |
| 私人 | Android/AtomClipBLE.apk | 1,907,196 | `67f5cf030f8b36d465adc4375f0f658f696c8fd565ce73f8a4b8d6710cec7642` |
| 公开配套 | Windows/USB/AtomClipUSB.exe | 61,440 | `1bd53d6f037ce092a7d7f47b08ced0dae85abf72e04a94b22f9a407ea5d2ed29` |
| 公开配套 | Windows/BLE/AtomClipBLE.exe | 16,247,296 | `9282c3f31042b7756462954b4e760f20f45c424ce614e299cc7b1a64f2230cba` |
| 公开配套 | Android/AtomClipBLE.apk | 1,907,196 | `cf53ee5f2fd0412d325f9325030a3f7af008e13f60d3631899d2462343fe367f` |

Windows Assembly / FileVersion 0.2.1.0；Android 包名 dev.atomclip.mobile，versionName 0.2.1，versionCode 59。两套 APK v2 / v3 签名验证通过，证书分别与对应 0.1.1 和 0.2.0 相同。内网 EXE 61,440 字节（60KiB），不超过 64KiB。

固件未改动，所有段与上轮对应配套逐字节一致；已正常绑定的 V2 板无需重烧。

| 固件应用 BIN | 字节数 |
| --- | ---: |
| 独立 AtomS3U 8MB | 666,144 |
| 独立 Generic ESP32-S3 4 / 8 / 16MB（各自） | 666,016 |
| AtomS3U 私人 / 公开迁移（各自） | 666,176 |

表中为实际 BIN 文件长度，不是编译器的 sketch 大小或分区上限。每个使用包包含本套 AtomS3U 迁移固件与三种 Generic 独立固件；四个分段地址为 0、0x8000、0xe000、0x10000。段长度及 SHA256 由本套 bundle.json 记录，客户端和独立目录已逐字节核对。

ZIP 内的 SHA256SUMS.txt 覆盖包内文件；交付目录顶层 SHA256SUMS.txt 覆盖三个 ZIP。本机使用包附第三方许可证及 esptool-source-v5.3.1.zip。源代码包不含个人签名、配对配置、用户 .acbind 或生成二进制。
