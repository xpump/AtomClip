param(
 [string]$BleAssembly=(Join-Path $PSScriptRoot '..\dist\Windows\BLE\AtomClipBLE.exe'),
 [string]$TestDirectory=(Join-Path $PSScriptRoot '..\.local\tests'),
 [switch]$ClipboardTests
)
$ErrorActionPreference='Stop';$repo=Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path $TestDirectory -Force | Out-Null
Copy-Item -LiteralPath $BleAssembly -Destination (Join-Path $TestDirectory 'AtomClipBLE.exe') -Force
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach($name in @('EditorReceiverTests','ConnectionModeTests','EditorRenderingTests','FlashTests','FrameworkTests','HardwareTests','ConsolePasteTests')){
 $target=if($name -eq 'ConsolePasteTests'){'winexe'}else{'exe'}
 & $compiler /nologo /utf8output ('/target:'+$target) /platform:x64 ('/out:'+(Join-Path $TestDirectory ($name+'.exe'))) ('/reference:'+(Join-Path $TestDirectory 'AtomClipBLE.exe')) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $repo ('tests\'+$name+'.cs'))
 if($LASTEXITCODE -ne 0){throw ($name+' compilation failed')}
}
& (Join-Path $TestDirectory 'EditorReceiverTests.exe');if($LASTEXITCODE -ne 0){throw 'Editor/UI regression failed'}
& (Join-Path $TestDirectory 'ConnectionModeTests.exe');if($LASTEXITCODE -ne 0){throw 'Connection mode/pacing regression failed'}
& (Join-Path $TestDirectory 'EditorRenderingTests.exe');if($LASTEXITCODE -ne 0){throw 'Editor rendering/state regression failed'}
& (Join-Path $TestDirectory 'FlashTests.exe');if($LASTEXITCODE -ne 0){throw 'Flash resource/plan regression failed'}
if($ClipboardTests){
 $usbAssembly=Join-Path (Split-Path -Parent (Split-Path -Parent ([IO.Path]::GetFullPath($BleAssembly)))) 'USB\AtomClipUSB.exe'
 Copy-Item -LiteralPath $usbAssembly -Destination (Join-Path $TestDirectory 'AtomClipUSB.exe') -Force
 & $compiler /nologo /utf8output /target:exe /platform:x64 ('/out:'+(Join-Path $TestDirectory 'UsbClipboardTests.exe')) ('/reference:'+(Join-Path $TestDirectory 'AtomClipUSB.exe')) /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll (Join-Path $repo 'tests\UsbClipboardTests.cs')
 if($LASTEXITCODE -ne 0){throw 'USB clipboard test compilation failed'}
 & (Join-Path $TestDirectory 'UsbClipboardTests.exe');if($LASTEXITCODE -ne 0){throw 'USB clipboard direction regression failed'}
 $bootstrap=Join-Path $TestDirectory 'receiver.txt'
 & (Join-Path $PSScriptRoot 'BUILD_MEMORY_RECEIVER.ps1') -OutputFile $bootstrap
 & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -File (Join-Path $repo 'tests\VerifyMemoryReceiver.ps1') -BootstrapFile $bootstrap -PeerAssembly ([IO.Path]::GetFullPath($BleAssembly))
 if($LASTEXITCODE -ne 0){throw 'Actual clipboard receiver regression failed'}
}
Write-Host 'No physical BLE, USB or flash operation was performed. Cross-language tests: see docs/DEVELOPMENT.md.'
