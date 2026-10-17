# 开发与构建

## 依赖

构建在 Windows x64 上执行，建议使用 PowerShell 7；应用本身仍面向 .NET Framework 4.8 和系统 PowerShell 5.1。

| 用途 | 依赖 |
| --- | --- |
| Windows | 系统 Framework64/v4.0.30319/csc.exe；Microsoft.Windows.SDK.Contracts 10.0.19041.1 的 `ref/netstandard2.0/*.winmd` |
| 固件 | Arduino CLI；ESP32 Arduino **3.3.12**；NimBLE-Arduino **2.5.1** |
| 内置烧录工具 | Windows x64 **esptool 5.3.1** 独立 EXE，以及该版本许可和对应源代码归档 |
| Android | JDK 17；Android SDK platform 35、build-tools 35（包含 aapt2、d8、zipalign、apksigner） |
| 跨语言测试 | .NET SDK 8、JDK 17、系统 PowerShell 5.1 |

从各组件官方发行渠道准备工具。本项目不包含完整 SDK 或运行时，仓库体积不随开发环境增长。

脚本默认查找以下目录，也可通过参数传入其他目录：

```text
.tools/
├─ arduino_cli/arduino-cli.exe
├─ windows_sdk_contracts/ref/netstandard2.0/*.winmd
├─ esptool.exe
└─ android_tools/
   ├─ jdk/<jdk-17-directory>/bin/java.exe
   ├─ build-tools/<version>/aapt2.exe
   └─ platform/<platform-directory>/android.jar
```

Arduino CLI 的 data、downloads、user 等目录由其配置文件管理；ESP32 / NimBLE 应安装在该配置对应的位置。若 ESP32 工具链因中文路径链接失败，可用 `subst` 将开发目录映射为 ASCII 盘符，再传入 StageDirectory / BuildDirectory 和 Arduino 配置。

## 构建一套独立配对程序

本机升级只构建外网时，`BUILD_WINDOWS.ps1 -BleOnly` 可跳过内网 EXE；沿用同一套密钥的原 USB EXE，核对 SHA256。新配对材料必须重新构建匹配的内网程序，不能混用其他套的 USB EXE。

中文按键规划回归（不接硬件）：使用 ESP32 工具链的 `xtensa-esp32s3-elf-g++ -std=c++17 -fsyntax-only tests/PinyinPlannerTests.cpp tests/GbkPlannerTests.cpp`。编译期断言直接使用生产头文件；实际输入法、Alt 数字码语义和速度必须另做内网试输。GBK 映射在 Windows / Android 严格编码完成，固件不嵌入中文表。

在仓库根目录运行以下代码。**这些是开发者在外网电脑运行的构建命令，不需要在内网输入。**

```powershell
./scripts/Build-Kit.ps1 -ToolsDirectory ./.tools -ArduinoConfig /path/to/arduino-cli.yaml
```

脚本会生成并复用 `.local/private/pairing.json`，用同一份 32 字节密钥编译 Windows、Android 和固件。首次 Android 构建创建本地签名文件；后续保留它才能覆盖安装同一应用。不要混用不同套的产物。

默认产物：`dist/Windows/USB/AtomClipUSB.exe`、`dist/Windows/BLE/AtomClipBLE.exe`、`dist/Android/AtomClipBLE.apk`、`dist/firmware/AtomClip-S3U.bin`。Windows BLE EXE 内置对应固件、esptool、接收初始化文本和许可，不依赖这些文件在旁边存在。

按组件构建：

```powershell
$env:ATOMCLIP_PRIVATE_DIR = '/path/to/private'
./scripts/BUILD_FIRMWARE.ps1 -ArduinoCli /path/to/arduino-cli.exe -ConfigFile /path/to/config.yaml
./scripts/BUILD_WINDOWS.ps1 -FirmwareFile ./dist/firmware/AtomClip-S3U.bin -EsptoolFile /path/to/esptool.exe -MetadataDirectory /path/to/winmd
./scripts/BUILD_ANDROID.ps1 -ToolsDirectory /path/to/android_tools
```

固件使用 ESP32-S3、8 MB Flash、无 PSRAM、USB-OTG CDC、`default_8MB` 分区。分区是 Flash 内存放程序和数据的区域划分；程序空间上限由分区表决定，不能按全部 8 MB 计算。GPIO 是芯片的可编程输入输出引脚，使用 3.3 V 逻辑；本项目不要求外接 GPIO 硬件。

固件合并镜像从地址 `0x0` 写入。当前固件支持桥接、直接键盘输入、可调启动等待、VUC 与 GBK。与开发版 0.5.4 / 0.5.5 的配套固件相同；能力检查决定旧固件可以使用的方式。

## 测试

```powershell
# 不需要真实开发板，不读取用户配对密钥
./scripts/Test-Protocol.ps1

# 需先构建 BLE EXE；不会发送 BLE、打开串口或烧录
./scripts/Test.ps1

# 临时使用并恢复本机剪贴板，验证实际系统接收代码
./scripts/Test.ps1 -ClipboardTests
```

`Test-Protocol.ps1` 使用公开、确定性的测试密钥。它验证实际 C# Framework、原 .NET 8 协议和 Android Java 的互通，以及固件中的 PowerShell 文字解码流程；不访问硬件。GitHub Actions 的 Windows 作业运行该检查，尚未在远程仓库实际执行。

`FlashTests` 注入模拟命令结果验证识别与写入顺序、型号/容量/端口/错误/哈希检查，同时解包真实资源、核对 SHA256，运行内置 esptool 的 `--help`。它不会烧板。

真实控制台粘贴工具 `tests/ConsolePasteTests.cs` 以 x64 WinExe 编译。传入生成的初始化文本和结果日志路径。它替换等长虚拟密钥、临时使用并恢复剪贴板，通过真实经典控制台粘贴启动，取消端口选择后退出。测试自动替换为独立互斥锁，允许本机旧 AtomClip 保持运行；初始化文本本身不得公开。

`HardwareTests` 是独立、手动执行的硬件工具。部分模式会发送测试文字或执行键盘动作，必须先准备专用测试窗口；不会由默认测试或 CI 自动运行。

## 代码职责

- `Program.cs`：连接控制、文本收发、主界面和托盘生命周期。
- `ConnectionModes.cs`：有/无内网程序层级、PowerShell / USB 键盘子界面、独立草稿及布局。
- `ReceiverSetup.cs`：四步接收设置界面，可从端口选择页返回重新打开接收窗口。
- `FirmwareInstaller.cs`：离线资源校验、USB 设备描述、烧录状态机和进度日志。
- `MemoryReceiver.cs`：内网系统桥接循环，由 PowerShell Add-Type 编译；客户端统一使用双向初始化资源，B 切换内网回传；保留旧单向资源用于协议回归。
- `UsbProtocol.cs` / Android `Protocol.java`：加密、认证回执、帧解析和键盘任务。
- Android `MainActivity.java` / `BleTransport.java`：前台界面、权限、连接和 GATT 队列。

`BUILD_MEMORY_RECEIVER.ps1` 生成可读初始化文本，移除字符串外的可选标点空格、保留逐行代码，强制 Windows CRLF，并以空白行结束。正文仍受 16 KiB 限制；单向 16332、双向 16339 UTF-8 字节（双向仅 45 字节余量）。修改接收器后必须检查大小和真实控制台粘贴，不应移除换行来压缩体积。

USB 构建仅嵌入 32 字节配对资源，图标使用原生 16/32/48 像素帧并由窗口/托盘复用；不嵌入 PNG、大尺寸 ICO、外网教程或键盘任务代码。构建和测试均限制 USB EXE 不超过 64 KiB。

## 整理与发布

```powershell
./scripts/Check-Publish.ps1 -PrivateDirectory /path/to/private
./scripts/Package-Source.ps1
```

源码 ZIP 包含 dotfiles、文档、测试和许可证，不包含 `.local/`、`.tools/`、`dist/`、`bin/`、`obj/`。可将该 ZIP 解压到空目录后作为 GitHub 仓库初始内容。首次上传与标签发布见 [发布说明](PUBLISHING.md)，没有自动创建远程仓库或推送。

配套二进制与签名文件另存本机；不要将其加入公开仓库。当前版本没有独立的运行时重新配对功能，单个预编译套件的所有副本共享其嵌入密钥。公开分发应以源码和独立构建说明为入口，避免公开自己的配对套件。

## 版本约定

Windows 外网与内网 AssemblyVersion / FileVersion、标题、日志及关于页统一为 0.1.0；Android versionName 为 0.1.0、versionCode 为 56。重新整理显示版本时内部版本码仍必须递增，以支持覆盖安装原签名 APK。固件不含同名应用版本字段，当前镜像与最后开发版保持相同，使用相同配对材料时无需重烧。
