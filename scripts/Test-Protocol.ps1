param([string]$Dotnet='dotnet',[string]$Java='java',[string]$Javac='javac')
$ErrorActionPreference='Stop';$repo=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path $repo '.local\protocol-tests';New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$protocolAssembly=Join-Path $testRoot 'AtomClipProtocol.dll'
& $compiler /nologo /target:library /platform:x64 ('/out:'+$protocolAssembly) /reference:System.dll /reference:System.Core.dll (Join-Path $repo 'src\windows\UsbProtocol.cs') (Join-Path $repo 'src\windows\FileLog.cs')
if($LASTEXITCODE -ne 0){throw 'Protocol assembly compilation failed'}
$frameworkTests=Join-Path $testRoot 'FrameworkTests.exe'
& $compiler /nologo /target:exe /platform:x64 ('/out:'+$frameworkTests) ('/reference:'+$protocolAssembly) /reference:System.dll /reference:System.Core.dll (Join-Path $repo 'tests\FrameworkTests.cs')
if($LASTEXITCODE -ne 0){throw 'Framework test compilation failed'}
& $Javac -encoding UTF-8 --release 8 -d $testRoot (Join-Path $repo 'src\android\Protocol.java') (Join-Path $repo 'tests\AndroidProtocolTests.java')
if($LASTEXITCODE -ne 0){throw 'Java test compilation failed'}
& $Dotnet build (Join-Path $repo 'tests\InteropTests.csproj') -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Interop test compilation failed'}
$javaPath=(Get-Command $Java -ErrorAction Stop).Source
& $Dotnet (Join-Path $repo '.local\dotnet\bin\Release\net8.0-windows\InteropTests.dll') (Join-Path $testRoot 'vectors') $frameworkTests $javaPath $testRoot (Join-Path $repo 'src\firmware\AtomClip\Typing.h')
if($LASTEXITCODE -ne 0){throw 'Cross-platform protocol regression failed'}
