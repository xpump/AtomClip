param(
 [string]$PrivateDirectory=(Join-Path $PSScriptRoot '..\.local\private'),
 [string]$ToolsDirectory=(Join-Path $PSScriptRoot '..\.tools'),
 [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\dist'),
 [string]$ArduinoConfig=''
)
$ErrorActionPreference='Stop'
$previousPrivate=$env:ATOMCLIP_PRIVATE_DIR;$previousTools=$env:ATOMCLIP_TOOLS_DIR
try{
 $env:ATOMCLIP_PRIVATE_DIR=[IO.Path]::GetFullPath($PrivateDirectory)
 $env:ATOMCLIP_TOOLS_DIR=[IO.Path]::GetFullPath($ToolsDirectory)
 & (Join-Path $PSScriptRoot 'New-Pairing.ps1') -PrivateDirectory $env:ATOMCLIP_PRIVATE_DIR
 $firmwareDirectory=Join-Path $OutputDirectory 'firmware'
 $work=Join-Path $env:TEMP ('AtomClip-kit-'+[Guid]::NewGuid().ToString('N'))
 & (Join-Path $PSScriptRoot 'BUILD_FIRMWARE.ps1') -ArduinoCli (Join-Path $ToolsDirectory 'arduino_cli\arduino-cli.exe') -ConfigFile $ArduinoConfig -StageDirectory (Join-Path $work 'stage') -BuildDirectory (Join-Path $work 'build') -OutputDirectory $firmwareDirectory
 & (Join-Path $PSScriptRoot 'BUILD_WINDOWS.ps1') -OutputDirectory (Join-Path $OutputDirectory 'Windows') -FirmwareFile (Join-Path $firmwareDirectory 'AtomClip-S3U.bin')
 & (Join-Path $PSScriptRoot 'BUILD_ANDROID.ps1') -ToolsDirectory (Join-Path $ToolsDirectory 'android_tools') -BuildDirectory (Join-Path $work 'android') -OutputFile (Join-Path $OutputDirectory 'Android\AtomClipBLE.apk')
 Write-Host 'Paired kit built. These private binaries share one key; do not publish them as a universal public download.'
}finally{$env:ATOMCLIP_PRIVATE_DIR=$previousPrivate;$env:ATOMCLIP_TOOLS_DIR=$previousTools}
