param(
    [string]$ArduinoCli = 'arduino-cli',
    [string]$ConfigFile = '',
    [string]$StageDirectory = (Join-Path $env:TEMP 'AtomClip-firmware'),
    [string]$BuildDirectory = (Join-Path $env:TEMP 'AtomClip-build'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\firmware')
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PrivateDirectory = if ($env:ATOMCLIP_PRIVATE_DIR) { $env:ATOMCLIP_PRIVATE_DIR } else { Join-Path $RepoRoot '.local\private' }
$stage = Join-Path $StageDirectory 'AtomClip'
New-Item -ItemType Directory -Path $stage,$BuildDirectory,$OutputDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $RepoRoot 'src\firmware\AtomClip\*') -Destination $stage -Force
$key = [Convert]::FromBase64String((Get-Content (Join-Path $PrivateDirectory 'pairing.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Key)
if ($key.Length -ne 32) { throw 'Invalid existing pairing file' }
$header = '#pragma once' + "`n" + 'static const unsigned char PAIRING_KEY[32]={' + (($key | ForEach-Object { '0x' + $_.ToString('x2') }) -join ',') + '};' + "`n"
[IO.File]::WriteAllText((Join-Path $stage 'PairingKey.h'),$header,[Text.Encoding]::ASCII)
[Array]::Clear($key,0,$key.Length)
$cliArgs = @()
if ($ConfigFile) { $cliArgs += @('--config-file',$ConfigFile) }
$cliArgs += @('compile','--fqbn','esp32:esp32:esp32s3:USBMode=default,CDCOnBoot=cdc,FlashSize=8M,PartitionScheme=default_8MB,PSRAM=disabled','--jobs','4','--build-path',$BuildDirectory,'--output-dir',$OutputDirectory,$stage)
& $ArduinoCli $cliArgs
if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed' }
Copy-Item (Join-Path $OutputDirectory 'AtomClip.ino.merged.bin') (Join-Path $OutputDirectory 'AtomClip-S3U.bin') -Force
