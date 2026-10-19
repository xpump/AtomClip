param(
 [string]$BuildDirectory=(Join-Path $PSScriptRoot '..\dist'),
 [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\..\AtomClip_0.2.0'),
 [Parameter(Mandatory=$true)][string]$EsptoolSourceZip,
 [ValidateSet('private','public')][string]$KitKind='public',
 [string]$ZipFile=(Join-Path $PSScriptRoot '..\..\AtomClip-v0.2.0-public.zip')
)
$ErrorActionPreference='Stop';$repo=Split-Path -Parent $PSScriptRoot
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new empty release directory; existing release is not removed.'}
New-Item -ItemType Directory -Path $OutputDirectory,(Join-Path $OutputDirectory 'Windows\USB'),(Join-Path $OutputDirectory 'Windows\BLE'),(Join-Path $OutputDirectory 'Android') -Force | Out-Null
foreach($relative in @('Windows\USB\AtomClipUSB.exe','Windows\BLE\AtomClipBLE.exe','Android\AtomClipBLE.apk')){Copy-Item -LiteralPath (Join-Path $BuildDirectory $relative) -Destination (Join-Path $OutputDirectory $relative)}
$bundle=Join-Path $BuildDirectory 'firmware\bundle';$bundleOutput=Join-Path $OutputDirectory 'firmware\bundle'
$catalog=Get-Content -LiteralPath (Join-Path $bundle 'bundle.json') -Raw | ConvertFrom-Json
if($catalog.schema -ne 2 -or $catalog.builds.Count -ne 4){throw 'Expected four-profile schema2 firmware bundle'}
New-Item -ItemType Directory -Path $bundleOutput -Force | Out-Null
foreach($build in $catalog.builds){foreach($segment in $build.segments){
 if($segment.file -notmatch '^[a-z0-9-]+\.bin$'){throw 'Invalid firmware segment path'}
 $file=Join-Path $bundle $segment.file
 if((Get-Item -LiteralPath $file).Length -ne $segment.length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $segment.sha256){throw 'Firmware segment integrity failure'}
 Copy-Item -LiteralPath $file -Destination (Join-Path $bundleOutput $segment.file)
}}
Copy-Item -LiteralPath (Join-Path $bundle 'bundle.json') -Destination $bundleOutput
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $OutputDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination $OutputDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $OutputDirectory
Copy-Item -LiteralPath $EsptoolSourceZip -Destination (Join-Path $OutputDirectory 'licenses\esptool-source-v5.3.1.zip')
$readme=@'
# AtomClip 0.2.0 — __KIT_LABEL__

__KIT_NOTICE__

构建及桌面回归已完成；实机验证范围见 docs/RELEASE_NOTES.md。

| 文件 | 用途 |
| --- | --- |
| Windows/BLE/AtomClipBLE.exe | 外网 Windows 主程序，内置固件、离线烧录工具和设置向导 |
| Windows/USB/AtomClipUSB.exe | 内网精简程序；仅用于允许运行 EXE 的电脑，大小不超过 64 KiB |
| Android/AtomClipBLE.apk | 安卓外网端，原签名，可覆盖安装开发版；显示版本 0.2.0，内部版本码 61 |
| firmware/bundle/ | 与客户端嵌入内容一致的四板型分段清单与固件；不要当作完整 Flash 镜像写入 |

已正常绑定的 0.2.0 开发板无需重烧。添加入口为“我的开发板 → 添加开发板 → 读取开发板 → 确认添加”，再次接入已绑定板会核对原配置。扫描附近按完整编号合并，旧固件兼容连接已移除。

首次用新板：外网 Windows 主程序 → 开发板设置 → 按向导进入下载模式 → 烧录。手机与 Windows 外网端一次只能连接一个。

PowerShell 默认双向，内网接收窗口按 B 切换回传。USB 键盘保留 GBK / VUC 中英文混排和纯英文；输入方式按钮始终可见，试输可选。VUC 默认兼容速度为 4ms / 80ms，可点击“自定义速度”保存新参数或恢复默认。详细操作见 docs/QUICKSTART.md。

安全设备绑定需要 0.2.0 固件。首次安装 / 分区不兼容时先备份加密绑定配置并明确清空安装，成功后经 USB 绑定。兼容分区的后续升级保留 NVS。原内网 EXE 仅在保留旧正文配对时兼容；通用内网程序可从新版板 USB 初始化正文配对。

首轮包括 AtomS3U 8MB、Generic ESP32-S3 SPI NOR 4/8/16MB，均要求原生 USB、DIO 80MHz、不开 PSRAM；通用板不假设 LED / 按钮。其他芯片尚未移植。手机 USB 烧录、实际 BLE/HID 与目标输入法兼容性均待实机验证。

公开源码使用独立 source ZIP。EXE / APK / 迁移固件含本套旧正文配对，不能在私人 / 公开配套间混用；APK 签名文件、配对配置、用户 .acbind 不包含在本包内。发布地址：https://github.com/xpump/AtomClip/releases/tag/v0.2.0 。

使用说明：docs/QUICKSTART.md；Android 范围：docs/ANDROID.md；验证边界：docs/VALIDATION.md。未完成真实内网 HID / Android 真机验收。

'@
$kitLabel=if($KitKind -eq 'private'){'私人配套'}else{'公开配套'}
$kitNotice=if($KitKind -eq 'private'){'本包沿用私人签名与旧正文配对，仅供自用，不得上传。'}else{'本包沿用公开配套的原签名与旧正文配对；新板应通过物理 USB 建立独立绑定。'}
$readme=$readme.Replace('__KIT_LABEL__',$kitLabel).Replace('__KIT_NOTICE__',$kitNotice)
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'README.md'),$readme,[Text.UTF8Encoding]::new($false))
$release=[IO.Path]::GetFullPath($OutputDirectory)
$files=Get-ChildItem -LiteralPath $release -File -Recurse | Sort-Object FullName
$manifest=foreach($file in $files){(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$file.FullName.Substring($release.Length+1).Replace('\','/')}
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'),$manifest,[Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path -LiteralPath $ZipFile){throw 'Release ZIP already exists; choose a new output name.'}
[IO.Compression.ZipFile]::CreateFromDirectory($release,[IO.Path]::GetFullPath($ZipFile),[IO.Compression.CompressionLevel]::Optimal,$false)
$archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ZipFile))
try{
 foreach($entry in $archive.Entries){$local=Join-Path $release $entry.FullName;$stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create();try{$actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()};if($actual -ne (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash){throw 'Release ZIP mismatch'}}
 Write-Host ('PASS: '+$archive.Entries.Count+' release ZIP entries verified.')
}finally{$archive.Dispose()}
Get-Item -LiteralPath $ZipFile | Select-Object Name,Length
