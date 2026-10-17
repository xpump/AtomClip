<p align="center"><img src="assets/branding/AtomClip-256.png" width="96" alt="AtomClip"></p>

# AtomClip

通过 **USB + BLE**，在 Windows 内网电脑与外网 Windows / Android 设备之间传递纯文本。

**版本 0.1.0** · Windows x64 · Android 8.0+ · M5Stack AtomS3U

开发板插在内网电脑，外网设备通过 BLE 连接。不需要 Wi-Fi、云服务或共同服务器；支持中文、英文、换行，剪贴板模式还支持 emoji。

![Windows 主界面](docs/images/windows.png)

## 选择适合的方式

| 内网条件 | 使用方式 | 传输能力 |
| --- | --- | --- |
| 允许运行内网程序 | 有内网程序 | 双向剪贴板同步；外网可编辑后发送，也可开启复制自动发送 |
| 允许系统 PowerShell、Add-Type 和 USB 串口 | 无内网程序 → PowerShell | 四步设置后默认双向；内网窗口按 **B** 切换回传，**P** 暂停，**Q** 退出 |
| 仅允许 USB 键盘 | 无内网程序 → 仅 USB 键盘 | GBK / VUC 中英文混排、纯英文；单向输入到当前光标位置 |

**仅 USB 键盘不能读取内网剪贴板。** GBK 依赖系统和目标窗口的数字码支持；VUC 依赖已有的微软拼音全拼。两种方式均不支持任意 Unicode，遇到不支持的字符明确拒绝，不静默跳过。

## 功能

- Windows 内网程序保持精简：本机 0.1.0 构建 **57,344 字节（56 KiB）**，不捆绑运行时、固件或烧录工具。
- Windows 外网端内置固件、离线烧录工具与设置向导；新开发板可直接初始化。
- PowerShell 默认双向，窗口内按 B 切换回传，无需为方向重做初始化。
- USB 输入方式直接显示为按钮，保留 GBK 高速、VUC 微软拼音及纯英文；**试输可选**，默认显示正文。
- 浅色分组界面、可滚动布局、分层帮助；文本框在发送完成和模式切换时保留草稿。
- AES-GCM、认证回执、重试、去重与暂停；单条最多 **16,384 UTF-8 字节**，超长拒绝，不截断。
- 不保存剪贴板历史；日志只记录状态与字节数，不记录正文或配对信息。

![USB 中英文输入](docs/images/windows-gbk.png)

## 开始使用

此仓库提供源码和独立配对构建方法。先按[开发与构建](docs/DEVELOPMENT.md)生成自己的一套程序，再按以下步骤连接。

1. 新板在外网 Windows 打开 `AtomClipBLE.exe` → **开发板设置**，按向导烧录。仅适用于无屏幕的 **AtomS3U / ESP32-S3 / 8 MB**。
2. 将开发板插入内网电脑；外网 Windows 或 Android 打开客户端并连接 BLE。一次只能连接一个外网客户端。
3. 根据内网允许的条件选择模式，按[使用说明](docs/QUICKSTART.md)完成设置或直接键盘输入。

PowerShell 启动等待默认 10 秒，可设 3–60 秒，慢电脑建议 20–30 秒。界面显示估算范围和已用时间，完成以开发板回执为准。USB 直接输入开始前保留 3 秒准备时间。

## 环境要求

| 组件 | 要求 |
| --- | --- |
| 开发板 | M5Stack AtomS3U，无屏幕，ESP32-S3，8 MB Flash，无 PSRAM |
| Windows 外网端 | Windows 10 2004 / build 19041+ 或 Windows 11；x64、.NET Framework 4.8、BLE 适配器 |
| Windows 内网程序 | Windows 10 1903 / build 18362+；x64、.NET Framework 4.8 |
| PowerShell 接收器 | 系统 PowerShell 5.1 / STA、.NET Framework 4.8，允许 Add-Type 与 USB 数据通信 |
| Android | Android 8.0+、BLE；8–11 需定位权限及开关，12+ 需附近设备权限 |

Android 需要保持应用在前台；离开后断开，返回后重连，不补发离线内容。没有跨应用后台剪贴板同步，也不提供手机 USB 烧录。详细范围见 [Android 说明](docs/ANDROID.md)。

仅传输纯文本，不支持图片、文件或富文本。系统接收模式的动态编译组件可能创建临时文件；软件不更改执行策略。GBK / VUC 的目标窗口兼容性和速度需要在实际设备上验证。

## 源码与发布

```text
assets/       图标与可编辑 SVG
src/windows/  Windows 主程序、精简接收端、PowerShell 接收器
src/android/  原生 Android 客户端
src/firmware/ AtomS3U USB / BLE / HID 固件
scripts/      配对生成、构建、测试与打包
tests/        协议、界面、剪贴板及按键规划测试
docs/         使用、构建、发布与验证说明
licenses/     第三方许可
.github/      GitHub Actions 协议互通检查
```

每套构建使用独立的 32 字节配对密钥，Windows、Android 和固件必须匹配。**公开源码包不包含密钥、安卓签名、SDK 或个人配套 EXE / APK / 固件。** 当前没有运行时重新配对功能，不要把自己的配套使用包作为通用公开下载。

- [使用说明与故障排查](docs/QUICKSTART.md)
- [开发与构建](docs/DEVELOPMENT.md)
- [上传 GitHub 与打包](docs/PUBLISHING.md)
- [Android 功能范围](docs/ANDROID.md)
- [通信协议](docs/PROTOCOL.md)
- [验证记录](docs/VALIDATION.md)
- [版本记录](CHANGELOG.md)
- [配对与隐私](SECURITY.md)

## 验证状态

Windows / Android 编译、APK 签名、桌面协议互通及 Windows 界面回归已有记录；GitHub Actions 已配置。**实际内网目标程序的 HID 兼容性、漏字率以及 Android 真机流程仍待验收**，编译和模拟链路通过不等同于实机通过。

0.1.0 是内部开发版的整理发布，协议保持兼容。此前已烧录开发版 0.5.4 / 0.5.5 固件且配对相同的用户无需重烧，原内网程序仍可使用；不同配对材料之间不能混用。

自有代码采用 [MIT](LICENSE) 许可；第三方组件遵循各自许可，见[第三方说明](licenses/THIRD_PARTY.md)。
