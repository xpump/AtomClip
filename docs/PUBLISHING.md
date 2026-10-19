# 0.2.0 打包与发布

仓库：[xpump/AtomClip](https://github.com/xpump/AtomClip)。Release 标签 v0.2.0，Windows 0.2.0.0，Android 显示 0.2.0、versionCode 61。

公开源码通过 Package-Source.ps1 打包，排除 .local、配置、签名、用户 .acbind、日志和构建输出。使用包通过 Package-Release.ps1 -KitKind public 打包，使用原公开签名与旧正文配对；私人套件、签名私钥、口令和配对配置不得上传。

BUILD_ANDROID.ps1 / BUILD_WINDOWS.ps1 接收 -FirmwareDirectory，目录含 bundle.json 及四板型分段文件。命令见 [DEVELOPMENT.md](DEVELOPMENT.md)。

发布前核对版本、APK 原签名、板型及分段摘要、内嵌配套资源、内网 EXE ≤64KiB、PowerShell 模板 ≤16KiB，并运行适用回归。使用包附第三方许可及 esptool 5.3.1 对应源码。通过 Check-Publish.ps1 和源码归档扫描确认没有配对配置、签名材料和用户绑定文件。

Release 包含公开使用包、两个 EXE、APK、项目源码、esptool 对应源码及仅覆盖这些附件的 SHA256SUMS。核对源码提交、标签、附件和远端摘要后完成发布，保留历史 v0.1.0 Release。

实机验证范围见 [RELEASE_NOTES.md](RELEASE_NOTES.md) 与 [VUC_SPEED_020.md](VUC_SPEED_020.md)。
