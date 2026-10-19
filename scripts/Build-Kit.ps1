param(
 [string]$PrivateDirectory=(Join-Path $PSScriptRoot '..\.local\private'),
 [string]$ToolsDirectory=(Join-Path $PSScriptRoot '..\.tools'),
 [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\dist'),
 [string]$ArduinoConfig='',
 [ValidateSet('migration','independent')][string]$AtomPairingMode='independent'
)
$ErrorActionPreference='Stop'
$previousPrivate=$env:ATOMCLIP_PRIVATE_DIR;$previousTools=$env:ATOMCLIP_TOOLS_DIR
try{
 $env:ATOMCLIP_PRIVATE_DIR=[IO.Path]::GetFullPath($PrivateDirectory)
 $env:ATOMCLIP_TOOLS_DIR=[IO.Path]::GetFullPath($ToolsDirectory)
 & (Join-Path $PSScriptRoot 'New-Pairing.ps1') -PrivateDirectory $env:ATOMCLIP_PRIVATE_DIR
 $firmwareDirectory=Join-Path $OutputDirectory 'firmware'
 $work=Join-Path $env:TEMP ('AtomClip-kit-'+[Guid]::NewGuid().ToString('N'))
 $firmwareDirs=@()
 foreach($profile in @('atoms3u-8m','generic-s3-4m','generic-s3-8m','generic-s3-16m')){
  $dir=Join-Path $firmwareDirectory $profile;$firmwareDirs+=,$dir
  $mode=if($profile -eq 'atoms3u-8m'){$AtomPairingMode}else{'independent'}
  & (Join-Path $PSScriptRoot 'BUILD_FIRMWARE.ps1') -ArduinoCli (Join-Path $ToolsDirectory 'arduino_cli\arduino-cli.exe') -ConfigFile $ArduinoConfig -StageDirectory (Join-Path $work ($profile+'-stage')) -BuildDirectory (Join-Path $work ($profile+'-build')) -OutputDirectory $dir -Profile $profile -PairingMode $mode
 }
 $bundle=Join-Path $firmwareDirectory 'bundle'
 & (Join-Path $PSScriptRoot 'New-FirmwareBundle.ps1') -Directories $firmwareDirs -OutputDirectory $bundle
 & (Join-Path $PSScriptRoot 'BUILD_WINDOWS.ps1') -OutputDirectory (Join-Path $OutputDirectory 'Windows') -FirmwareDirectory $bundle
 & (Join-Path $PSScriptRoot 'BUILD_ANDROID.ps1') -ToolsDirectory (Join-Path $ToolsDirectory 'android_tools') -BuildDirectory (Join-Path $work 'android') -OutputFile (Join-Path $OutputDirectory 'Android\AtomClipBLE.apk') -FirmwareDirectory $bundle
 Write-Host 'Kit built. Bind each board physically over USB, then share only password-encrypted binding exports. Signing keys and legacy pairing stay private.'

}finally{$env:ATOMCLIP_PRIVATE_DIR=$previousPrivate;$env:ATOMCLIP_TOOLS_DIR=$previousTools}
