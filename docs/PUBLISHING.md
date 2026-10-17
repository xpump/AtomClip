# 上传 GitHub 与打包

## 上传哪一个包

| 内容 | 用途 | 是否公开上传 |
| --- | --- | --- |
| `AtomClip-v0.1.0-source.zip` | 源码、README、图标、文档、测试和构建脚本 | 可以 |
| `AtomClip/` 源码中的受检文件 | Git 仓库内容 | 可以；不要加入被忽略的目录 |
| `AtomClip-v0.1.0-private.zip` / `AtomClip_0.1.0/` | 自用配套 Windows / Android 程序 | 含本机配对材料，保留自用 |
| `AtomClip_开发资料/`、`.local/`、`.tools/`、`dist/` | 密钥、签名、SDK 和构建结果 | 不上传 |

源码 ZIP 解压后，README、src、scripts 等应位于仓库根目录。不要只把整个 ZIP 作为仓库唯一文件，GitHub 无法直接展示压缩包内的说明和代码。

## 首次推送

登录自己的 GitHub，创建空仓库，建议命名 `AtomClip`，公开与私有由自己选择。把源码包解压到新的文件夹，在该文件夹中执行以下命令。示例账号为本次连接的 `xpump`，仓库名不同时请修改 URL。

```powershell
git init -b main
git add .
git status --short
git diff --cached --stat
git commit -m "Release AtomClip 0.1.0"
git remote add origin https://github.com/xpump/AtomClip.git
git push -u origin main
```

按自己的账号配置 Git 提交作者，认证通过系统凭据管理器或 SSH 完成，不把令牌写进文件。已有远程历史时，先克隆仓库，把源码复制进去再提交，不要强推覆盖。

本次整理只生成本地发布材料，没有自动创建远程仓库或推送。

## 标记版本

首次推送成功后，可创建标签：

```powershell
git tag -a v0.1.0 -m "AtomClip 0.1.0"
git push origin v0.1.0
```

可在该标签上建立 GitHub Release，使用 [RELEASE_NOTES.md](RELEASE_NOTES.md) 作为发布说明。GitHub 会提供标签对应的源码归档；另行附加本地源码 ZIP 时，可同时附带它的 SHA256。不要附加本机配套使用包、配对文件或签名文件。

## 重新生成源码包

在源码根目录运行：

```powershell
./scripts/Check-Publish.ps1 -PrivateDirectory /path/to/private
./scripts/Package-Source.ps1
```

检查器核对公开文件集合，可使用本机配对配置检查实际密钥。打包排除 `.local/`、`.tools/`、`dist/`、`bin/`、`obj/`，保留根目录 `.gitignore` 和工作流。

`.gitignore` 不会删除已提交的秘密。若曾提交配对或签名材料，应视为泄露并按 [SECURITY.md](../SECURITY.md) 处理，不能只删除最新文件。

## 检查工作流

`.github/workflows/protocol.yml` 在 push、pull request 或手动触发时运行源码检查及 C# / Java 协议互通测试；不下载个人配对文件，不自动公开配套二进制。首次推送后在 Actions 查看结果，本地通过不能替代远程状态。
