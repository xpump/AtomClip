param(
 [string]$BuildDirectory=(Join-Path $PSScriptRoot '..\dist'),
 [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\..\AtomClip_0.1.0'),
 [Parameter(Mandatory=$true)][string]$EsptoolSourceZip,
 [string]$ZipFile=(Join-Path $PSScriptRoot '..\..\AtomClip-v0.1.0-private.zip')
)
$ErrorActionPreference='Stop';$repo=Split-Path -Parent $PSScriptRoot
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new empty release directory; existing release is not removed.'}
New-Item -ItemType Directory -Path $OutputDirectory,(Join-Path $OutputDirectory 'Windows\USB'),(Join-Path $OutputDirectory 'Windows\BLE'),(Join-Path $OutputDirectory 'Android') -Force | Out-Null
foreach($relative in @('Windows\USB\AtomClipUSB.exe','Windows\BLE\AtomClipBLE.exe','Android\AtomClipBLE.apk')){Copy-Item -LiteralPath (Join-Path $BuildDirectory $relative) -Destination (Join-Path $OutputDirectory $relative)}
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $OutputDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination $OutputDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $OutputDirectory
Copy-Item -LiteralPath $EsptoolSourceZip -Destination (Join-Path $OutputDirectory 'licenses\esptool-source-v5.3.1.zip')
$readme=@'
# AtomClip 0.1.0 — 本机配套程序

此目录是使用同一份本机配对材料构建的使用包，请保留自用。

| 文件 | 用途 |
| --- | --- |
| Windows/BLE/AtomClipBLE.exe | 外网 Windows 主程序，内置固件、离线烧录工具和设置向导 |
| Windows/USB/AtomClipUSB.exe | 内网精简程序；仅用于允许运行 EXE 的电脑，大小不超过 64 KiB |
| Android/AtomClipBLE.apk | 安卓外网端，原签名，可覆盖安装开发版；显示版本 0.1.0，内部版本码 56 |

首次用新板：外网 Windows 主程序 → 开发板设置 → 按向导进入下载模式 → 烧录。手机与 Windows 外网端一次只能连接一个。

PowerShell 默认双向，内网接收窗口按 B 切换回传。USB 键盘保留 GBK / VUC 中英文混排和纯英文；输入方式按钮始终可见，试输可选。详细操作见 docs/QUICKSTART.md。

版本从内部开发号统一整理为 0.1.0。固件与开发版 0.5.4 / 0.5.5 相同，已刷者无需重烧；原内网程序仍兼容。新包内网 EXE 统一版本标识和帮助，不增加传输依赖。

公开 GitHub 请使用 AtomClip-v0.1.0-source.zip，解压后上传其内容。此使用包的 EXE / APK / 固件含本机配对材料，不随公开源码发布。APK 签名文件和配对配置不包含在本包内。

使用说明：docs/QUICKSTART.md；Android 范围：docs/ANDROID.md；验证边界：docs/VALIDATION.md。未完成真实内网 HID / Android 真机验收。

'@
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
