param(
    [string]$ArduinoCli = 'arduino-cli',
    [string]$ConfigFile = '',
    [string]$StageDirectory = (Join-Path $env:TEMP 'AtomClip-firmware'),
    [string]$BuildDirectory = (Join-Path $env:TEMP 'AtomClip-build'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\firmware'),
    [string]$Profile = 'atoms3u-8m',
    [ValidateSet('migration','independent')][string]$PairingMode = 'migration'
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PrivateDirectory = if ($env:ATOMCLIP_PRIVATE_DIR) { $env:ATOMCLIP_PRIVATE_DIR } else { Join-Path $RepoRoot '.local\private' }
$stage = Join-Path $StageDirectory 'AtomClip'
New-Item -ItemType Directory -Path $stage,$BuildDirectory,$OutputDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $RepoRoot 'src\firmware\AtomClip\*') -Destination $stage -Force
$catalog=Get-Content -LiteralPath (Join-Path $RepoRoot 'boards\profiles.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$board=@($catalog.profiles | Where-Object key -eq $Profile)
if($board.Count -ne 1 -or $board[0].chip -ne 'esp32s3' -or !$board[0].nativeUsb -or $board[0].backend -ne 'esp32s3-rom'){throw 'Unsupported board profile/backend'}
$board=$board[0]
$legacy=if($PairingMode -eq 'migration'){1}else{0}
$profileHeader="#pragma once`n#define AC_LEGACY_DATA_PAIRING $legacy`nnamespace board { constexpr unsigned id=$($board.id); constexpr unsigned capabilities=$($board.capabilities); constexpr unsigned flashBytes=$($board.flashBytes); constexpr int buttonPin=$($board.buttonPin); constexpr int rgbLedPin=$($board.rgbLedPin); }`n"
[IO.File]::WriteAllText((Join-Path $stage 'BoardProfile.h'),$profileHeader,[Text.Encoding]::ASCII)
$key = if($legacy){[Convert]::FromBase64String((Get-Content (Join-Path $PrivateDirectory 'pairing.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Key)}else{[byte[]]::new(32)}
if ($key.Length -ne 32) { throw 'Invalid existing pairing file' }
$header = '#pragma once' + "`n" + 'static const unsigned char PAIRING_KEY[32]={' + (($key | ForEach-Object { '0x' + $_.ToString('x2') }) -join ',') + '};' + "`n"
[IO.File]::WriteAllText((Join-Path $stage 'PairingKey.h'),$header,[Text.Encoding]::ASCII)
[Array]::Clear($key,0,$key.Length)
$cliArgs = @()
if ($ConfigFile) { $cliArgs += @('--config-file',$ConfigFile) }
if($board.flashFrequencyMHz -ne 80 -or $board.flashMode -ne 'dio'){throw 'This Arduino toolchain supports the declared DIO/80MHz profile only'}
Copy-Item -LiteralPath (Join-Path $RepoRoot ('boards\partitions\'+$board.partitionFile)) -Destination (Join-Path $stage 'partitions.csv') -Force
$flashM=[int]($board.flashBytes/1MB)
$fqbn="esp32:esp32:esp32s3:USBMode=default,CDCOnBoot=cdc,FlashSize=$($flashM)M,FlashMode=dio,PartitionScheme=$($board.partitionScheme),PSRAM=disabled"
$cliArgs += @('compile','--fqbn',$fqbn,'--jobs','4','--build-path',$BuildDirectory,'--output-dir',$OutputDirectory,$stage)
& $ArduinoCli $cliArgs
if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed' }
Copy-Item (Join-Path $OutputDirectory 'AtomClip.ino.merged.bin') (Join-Path $OutputDirectory 'AtomClip-S3U.bin') -Force

& (Join-Path $PSScriptRoot 'New-FirmwareManifest.ps1') -Directory $OutputDirectory -Profile $Profile -PairingMode $PairingMode
