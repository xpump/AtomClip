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

## 构建 0.2.0 整合客户端（配套 0.2.0 固件）

工具目录保持上表结构。运行 `Build-Kit.ps1` 生成四种板型分段目录，并构建嵌入完整固件目录的 Windows / Android 客户端。新板运行时经物理 USB 配置独立密钥，无须每板重新编译。旧正文配对和 Android 签名仍保存在私有目录，不能公开。

```powershell
./scripts/Build-Kit.ps1 -ToolsDirectory ./.tools -ArduinoConfig /path/to/arduino-cli.yaml
# 迁移现有 AtomS3U：复用原私有目录及签名，仅 Atom 配置使用旧正文配对
./scripts/Build-Kit.ps1 -PrivateDirectory /path/to/existing-private -ToolsDirectory ./.tools -ArduinoConfig /path/to/config.yaml -AtomPairingMode migration
```

按组件构建时，`BUILD_FIRMWARE.ps1 -Profile` 接受 `atoms3u-8m`、`generic-s3-4m`、`generic-s3-8m`、`generic-s3-16m`，`-PairingMode` 为 `independent` 或 `migration`。显式指定独立 StageDirectory / BuildDirectory / OutputDirectory，避免并行构建覆盖配对头。

```powershell
$env:ATOMCLIP_PRIVATE_DIR = '/path/to/private'
./scripts/BUILD_FIRMWARE.ps1 -ArduinoCli /path/to/arduino-cli.exe -ConfigFile /path/to/config.yaml -Profile atoms3u-8m -PairingMode migration -OutputDirectory ./dist/firmware/atoms3u-8m
# 四种 profile 均构建后聚合
./scripts/New-FirmwareBundle.ps1 -Directories ./dist/firmware/atoms3u-8m,./dist/firmware/generic-s3-4m,./dist/firmware/generic-s3-8m,./dist/firmware/generic-s3-16m -OutputDirectory ./dist/firmware/bundle
./scripts/BUILD_WINDOWS.ps1 -FirmwareDirectory ./dist/firmware/bundle -EsptoolFile /path/to/esptool.exe -MetadataDirectory /path/to/winmd
./scripts/BUILD_ANDROID.ps1 -ToolsDirectory /path/to/android_tools -FirmwareDirectory ./dist/firmware/bundle
```

Android APK 与 Windows EXE 均嵌入 `bundle.json` 与四板型分段文件，不嵌入用于升级的完整合并镜像。构建检查迁移固件的配对哈希，防止私人 / 公开配套交换；每段另验证 SHA256、长度、偏移和芯片。保留合并镜像仅供开发排查，用户普通升级不能写整个 Flash。中央配置与分区见 `boards/`，能力和范围见 [BOARDS.md](BOARDS.md)。

Windows 内网构建限制 64KiB；兼容旧内网 EXE 的条件见 [MIGRATION.md](MIGRATION.md)。PowerShell 双向模板正文配对替换保持 44 字符，脚本仍须不超过 16384 UTF-8 字节。

## 测试

```powershell
# 不需要真实开发板，不读取用户配对密钥
./scripts/Test-Protocol.ps1
./scripts/Test-V2.ps1

# Android 编译 + 编辑及 ROM 下载协议模拟；只运行核心测试可用 -CoreOnly（无需 Android SDK）
./scripts/Test-Android.ps1 -ToolsDirectory /path/to/android_tools

# 需先构建 BLE EXE；不会发送 BLE、打开串口或烧录
./scripts/Test.ps1

# 临时使用并恢复本机剪贴板，验证实际系统接收代码
./scripts/Test.ps1 -ClipboardTests
```

`Test-Protocol.ps1` 使用公开、确定性的测试密钥。它验证实际 C# Framework、原 .NET 8 协议和 Android Java 的互通，以及固件中的 PowerShell 文字解码流程；不访问硬件。GitHub Actions 的 Windows 作业运行该检查，尚未在远程仓库实际执行。

`Test-Android.ps1` 运行真实 Java 编辑历史与 ROM 实现，覆盖 SLIP 分片、转义、压缩写入顺序、芯片/容量/加密拒绝、超时、拔线、中止及 MD5 失败。不会打开 USB；Android 系统权限、布局、输入法和 OTG 供电需手机验证。

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
- Android `MainActivity.java` / `EditorHistory.java`：紧凑界面、同页全屏、各模式草稿和事务撤销；`BleTransport.java` 负责 GATT 队列。
- Android `UsbFlashSession.java` / `UsbCdcTransport.java` / `EspRom.java`：旋转保留的烧录会话、系统 USB 授权、原生 CDC 传输、ROM 协议与分段镜像与分区兼容校验。

`BUILD_MEMORY_RECEIVER.ps1` 生成可读初始化文本，移除字符串外的可选标点空格、保留逐行代码，强制 Windows CRLF，并以空白行结束。正文仍受 16 KiB 限制；单向 16332、双向 16339 UTF-8 字节（双向仅 45 字节余量）。修改接收器后必须检查大小和真实控制台粘贴，不应移除换行来压缩体积。

USB 构建仅嵌入 32 字节配对资源，图标使用原生 16/32/48 像素帧并由窗口/托盘复用；不嵌入 PNG、大尺寸 ICO、外网教程或键盘任务代码。构建和测试均限制 USB EXE 不超过 64 KiB。

## 整理与发布

```powershell
./scripts/Check-Publish.ps1 -PrivateDirectory /path/to/private
./scripts/Package-Source.ps1
```

源码 ZIP 包含 dotfiles、文档、测试和许可证，不包含 `.local/`、`.tools/`、`dist/`、`bin/`、`obj/`。可将该 ZIP 解压到空目录后作为 GitHub 仓库初始内容。首次上传与标签发布见 [发布说明](PUBLISHING.md)，没有自动创建远程仓库或推送。

配套二进制与签名文件另存本机；不要将其加入公开源码仓库。0.2.1 支持通过物理 USB 生成、改绑和恢复每板独立配置；嵌入的旧正文配对仅用于兼容及迁移。私人 / 公开套件保持各自原签名和旧正文配对，不能互换。用户绑定配置 `.acbind` 不进入任何发行包。

## 版本约定

Windows 外网与内网 AssemblyVersion / FileVersion 为 0.2.1.0；Android versionName 为 0.2.1、versionCode 为 59；固件 Info 保持 0.2.0。两套 APK 分别沿用各自 0.1.1 的签名。内部版本码必须递增以支持覆盖安装，显示版本和真实验收状态分别记录，当前交付为本地审查包。安全绑定需要 0.2.0 固件；已正常绑定的 V2 板无需因 0.2.1 客户端更新重烧。
