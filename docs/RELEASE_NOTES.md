# AtomClip 0.2.0

本次更新以 GitHub 已发布的 **v0.1.0** 为比较基准，包含此后累积的功能和修复。Windows / Android 均为 **0.2.0**，Android versionCode 从 56 升至 **61**，沿用原公开签名。

支持 Windows x64、Android 8.0+ 和具备原生 USB 的 ESP32-S3 开发板。

## 相比已发布的 v0.1.0

| 项目 | v0.1.0 | v0.2.0 |
| --- | --- | --- |
| 开发板识别与绑定 | 使用套件内固定配对，无独立开发板管理 | 新增“我的开发板”，经物理 USB 单独绑定，按完整 128 位编号选择目标；已绑定记录与附近扫描合并显示，简化添加流程 |
| 绑定配置迁移 | 无绑定配置导入、导出界面 | 新增加密 `.acbind` 导入导出、待确认绑定恢复；本地配置由 Windows DPAPI / Android Keystore 保护 |
| Android USB 烧录 | 不支持手机 USB 烧录 | 新增原生 USB OTG 烧录与 USB 绑定，APK 内置配套固件；真实手机 OTG 流程仍待验证 |
| 板型与固件升级 | 仅 AtomS3U / ESP32-S3 8MB | 扩展为 AtomS3U 8MB 和 Generic ESP32-S3 SPI NOR 4 / 8 / 16MB；两端使用分段烧录，检查芯片、容量、OPI、分区及摘要，写入后校验；兼容分区升级保留 NVS 绑定区 |
| Android 编辑 | 固定高度编辑区，已有基本的分模式草稿 | 新增同一 Activity 内全屏编辑、撤销和可撤销的一键清空；完善草稿、选区与撤销历史保持，发送后保留文字和编辑界面；调整双行选项自然高度、按钮间距及字体缩放布局 |
| VUC 输入速度 | 默认按键 1ms / 确认等待 35ms，可手动选择兼容速度 | 默认使用兼容速度 **4ms / 80ms**；新增自定义速度、保存及恢复默认，试输与正文共用参数，预计时间及超时随参数调整 |
| Windows BLE 连接 | 部分蓝牙环境按 UUID 查询服务失败后无法连接 | 改为完整服务枚举后选择目标，修复已复现的 `0x80070016`；连接阶段和具体错误持续显示 |
| 内网程序 | 56KiB，使用固定正文配对 | **60KiB**，仍不超过 64KiB，可从物理 USB 初始化正文配对 |

新版增加独立控制密钥、双向 HMAC 认证、HKDF 和方向独立的 AES-GCM 会话及严格序号。正文配对可按迁移流程保留；旧 BLE 固件兼容连接已移除。

双向纯文本剪贴板、PowerShell 默认双向及内网按 B 切换、GBK / VUC / 纯英文和可选试输均为 0.1.0 已有功能。USB 键盘模式仍只支持单向输入，不能读取内网剪贴板。

[查看 v0.1.0 → v0.2.0 源码差异](https://github.com/xpump/AtomClip/compare/v0.1.0...v0.2.0)

## 下载

推荐下载 [**AtomClip-v0.2.0-clients-public.zip**](https://github.com/xpump/AtomClip/releases/download/v0.2.0/AtomClip-v0.2.0-clients-public.zip)，包含 Windows 外网 / 内网程序、Android APK、固件、说明及第三方许可。[打开 0.2.0 Release](https://github.com/xpump/AtomClip/releases/tag/v0.2.0)。

- `AtomClipBLE.exe`：Windows 外网主程序，内置烧录工具。
- `AtomClipUSB.exe`：Windows 内网精简程序。
- `AtomClipBLE.apk`：Android 外网应用，沿用公开签名。
- `AtomClip-v0.2.0-source.zip`：项目源码与构建说明。
- `esptool-source-v5.3.1.zip`：内置 esptool 的对应源码，完整使用包中也已包含。
- `SHA256SUMS.txt`：上述下载文件校验值。

## 从 0.1.0 升级

0.1.0 固件须先升级到本包 0.2.0 固件，再经物理 USB 添加绑定。首次安装或分区不兼容时须明确清空安装，详见 [迁移说明](MIGRATION.md)。新版不再提供旧 BLE 兼容连接。

已有 0.2.0 V2 固件且完成绑定的板无需重刷。在旧 Windows 程序“更多 → 退出”后打开新版，本机绑定继续沿用。Android 可覆盖安装原公开版本；私人签名 APK 不能直接覆盖为公开版。

## 验证范围

构建、签名、跨语言协议、速度参数、编辑状态、设备合并及分段烧录模拟检查通过。Windows 一台实际目标板已完成完整编号核对、双向认证和加密状态往返。

Android 新界面、软键盘 / 字体 / 旋转、Keystore、OTG / USB 权限、真实烧录、BLE 多板场景及 HID / 目标输入法仍待实机验证。VUC 默认复用用户已确认有效的兼容节奏，自定义速度尚未宣称完成实机验收。详见 [验证记录](VUC_SPEED_020.md)。
