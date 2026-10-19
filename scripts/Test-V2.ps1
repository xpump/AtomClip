param([string]$Java='java',[string]$Javac='javac')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$testDir=Join-Path $env:TEMP ('AtomClip-v2-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$files=@('src\windows\UsbProtocol.cs','src\windows\V2Protocol.cs','src\windows\BoardPresence.cs','src\windows\DeviceBindings.cs','tests\V2ProtocolTests.cs') | ForEach-Object {Join-Path $repo $_}
& $compiler /nologo /utf8output /target:exe /platform:x64 ('/out:'+(Join-Path $testDir 'V2Tests.exe')) /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll $files
if($LASTEXITCODE -ne 0){throw 'V2 C# compilation failed'}
$javaFiles=@('src\android\V2Protocol.java','src\android\BoardPresence.java','src\android\V2Registry.java','src\android\EspRom.java','src\android\FirmwareBundle.java','src\android\UsbBoardClient.java','tests\AndroidV2Tests.java','tests\UsbBindingTests.java') | ForEach-Object {Join-Path $repo $_}
& $Javac -encoding UTF-8 --release 8 -d $testDir $javaFiles
if($LASTEXITCODE -ne 0){throw 'V2 Java compilation failed'}
$vectors=Join-Path $repo 'tests\vectors\v2.properties'
& (Join-Path $testDir 'V2Tests.exe') $vectors $testDir
if($LASTEXITCODE -ne 0){throw 'V2 C# golden/security regression failed'}
& $Java -cp $testDir dev.atomclip.mobile.AndroidV2Tests $vectors $testDir
if($LASTEXITCODE -ne 0){throw 'V2 Java golden/security regression failed'}
& $Java -cp $testDir dev.atomclip.mobile.UsbBindingTests $vectors
if($LASTEXITCODE -ne 0){throw 'V2 USB provisioning/pending recovery regression failed'}
& (Join-Path $testDir 'V2Tests.exe') $vectors $testDir check-java
if($LASTEXITCODE -ne 0){throw 'V2 cross-platform configuration/session regression failed'}
Write-Host 'V2 core tests passed. BLE/USB/firmware integration and physical-board tests are separate.'
