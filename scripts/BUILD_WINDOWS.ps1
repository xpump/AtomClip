param(
 [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\Windows'),
 [string]$MetadataDirectory = '',
 [string]$FirmwareFile = (Join-Path $PSScriptRoot '..\dist\firmware\AtomClip-S3U.bin'),
 [string]$EsptoolFile = '',
 [switch]$BleOnly
)
$ErrorActionPreference='Stop'
$RepoRoot=Split-Path -Parent $PSScriptRoot
$PrivateDirectory=if($env:ATOMCLIP_PRIVATE_DIR){$env:ATOMCLIP_PRIVATE_DIR}else{Join-Path $RepoRoot '.local\private'}
$ToolsDirectory=if($env:ATOMCLIP_TOOLS_DIR){$env:ATOMCLIP_TOOLS_DIR}else{Join-Path $RepoRoot '.tools'}
if(!$MetadataDirectory){$MetadataDirectory=Join-Path $ToolsDirectory 'windows_sdk_contracts\ref\netstandard2.0'}
if(!$EsptoolFile){$EsptoolFile=Join-Path $ToolsDirectory 'esptool.exe'}
foreach($required in @($FirmwareFile,$EsptoolFile,(Join-Path $MetadataDirectory 'Windows.winmd'))){if(!(Test-Path -LiteralPath $required)){throw "Missing build dependency: $required"}}
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$pairing=Get-Content -LiteralPath (Join-Path $PrivateDirectory 'pairing.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$keyBytes=[Convert]::FromBase64String($pairing.Key)
if($keyBytes.Length -ne 32){throw 'Invalid pairing key'}
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('AtomClip-build-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
function Pack-FlashResource([string]$File,[string]$Name){
 $packed=Join-Path $temporary ($Name+'.gz')
 $input=[IO.File]::OpenRead($File);$output=[IO.File]::Create($packed)
 $gzip=[IO.Compression.GZipStream]::new($output,[IO.Compression.CompressionLevel]::Optimal)
 try{$input.CopyTo($gzip)}finally{$gzip.Dispose();$output.Dispose();$input.Dispose()}
 $digest=Join-Path $temporary ($Name+'.sha256')
 [IO.File]::WriteAllText($digest,(Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash,[Text.Encoding]::ASCII)
 @("/resource:$packed,AtomClip.Flash.$Name","/resource:$digest,AtomClip.Flash.$Name.sha256")
}
try{
 $temporaryKey=Join-Path $temporary 'pairing.bin';[IO.File]::WriteAllBytes($temporaryKey,$keyBytes)
 $bootstrap=Join-Path $temporary 'receiver.txt'
 $duplexBootstrap=Join-Path $temporary 'receiver-duplex.txt'
 $licenseText=Join-Path $temporary 'licenses.txt'
 $licenseFiles=@((Join-Path $RepoRoot 'LICENSE'))+@(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'licenses') -File | Where-Object Extension -ne '.zip' | ForEach-Object FullName)
 $licenseContent=foreach($licenseFile in $licenseFiles){[IO.File]::ReadAllText($licenseFile)}
 [IO.File]::WriteAllText($licenseText,($licenseContent -join "`r`n`r`n"),[Text.UTF8Encoding]::new($false))
 & (Join-Path $PSScriptRoot 'BUILD_MEMORY_RECEIVER.ps1') -OutputFile $bootstrap
 & (Join-Path $PSScriptRoot 'BUILD_MEMORY_RECEIVER.ps1') -OutputFile $duplexBootstrap -Bidirectional
 $flashResources=@(Pack-FlashResource $FirmwareFile 'AtomClip-S3U.bin')+@(Pack-FlashResource $EsptoolFile 'esptool.exe')
 $modes=if($BleOnly){@('BLE')}else{@('USB','BLE')}
 foreach($mode in $modes){
  $dir=Join-Path $OutputDirectory $mode;New-Item -ItemType Directory -Path $dir -Force | Out-Null
  $out=Join-Path $dir ('AtomClip'+$mode+'.exe')
  $files=@('Program.cs','Theme.cs','UsbProtocol.cs','UsbLink.cs','FileLog.cs') | ForEach-Object {Join-Path $RepoRoot ('src\windows\'+$_)}
  $refs=@('/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll')
  $brandIcon=Join-Path $RepoRoot 'assets\branding\AtomClip.ico'
  $brandMark=Join-Path $RepoRoot 'assets\branding\AtomClip-256.png'
  $resources=@("/resource:$brandMark,AtomClip.Mark","/resource:$temporaryKey,AtomClip.PairingKey","/resource:$brandIcon,AtomClip.Brand","/win32icon:$brandIcon")
  if($mode -eq 'USB'){
   $compactIcon=Join-Path $temporary 'AtomClip-compact.ico'
   $iconBytes=[IO.File]::ReadAllBytes($brandIcon)
   $entries=@();$count=[BitConverter]::ToUInt16($iconBytes,4)
   for($i=0;$i -lt $count;$i++){
    $entry=6+16*$i;$size=[int]$iconBytes[$entry]
    if($size -in @(16,32,48)){$entries+=,$entry}
   }
   if($entries.Count -ne 3){throw 'Expected 16/32/48 pixel icon entries'}
   $iconStream=[IO.MemoryStream]::new();$iconWriter=[IO.BinaryWriter]::new($iconStream)
   try{
    $iconWriter.Write([uint16]0);$iconWriter.Write([uint16]1);$iconWriter.Write([uint16]$entries.Count)
    $offset=6+16*$entries.Count
    foreach($entry in $entries){
     $iconWriter.Write($iconBytes,$entry,12);$iconWriter.Write([uint32]$offset)
     $offset += [BitConverter]::ToUInt32($iconBytes,$entry+8)
    }
    foreach($entry in $entries){$iconWriter.Write($iconBytes,[int][BitConverter]::ToUInt32($iconBytes,$entry+12),[int][BitConverter]::ToUInt32($iconBytes,$entry+8))}
    $iconWriter.Flush();[IO.File]::WriteAllBytes($compactIcon,$iconStream.ToArray())
   }finally{$iconWriter.Dispose();$iconStream.Dispose()}
   $resources=@("/resource:$temporaryKey,AtomClip.PairingKey","/win32icon:$compactIcon")
  }
  if($mode -eq 'BLE'){
   $files+=@('BleLink.cs','ReceiverSetup.cs','ConnectionModes.cs','KeyboardInput.cs','FirmwareInstaller.cs') | ForEach-Object {Join-Path $RepoRoot ('src\windows\'+$_)}
   $resources+=@("/resource:$bootstrap,AtomClip.ReceiverBootstrap","/resource:$duplexBootstrap,AtomClip.DuplexBootstrap","/resource:$licenseText,AtomClip.Licenses")+$flashResources
   $refs+=@("/reference:$framework\System.Runtime.WindowsRuntime.dll","/reference:$framework\System.Runtime.dll","/reference:$framework\System.Runtime.InteropServices.WindowsRuntime.dll")
   $refs+=Get-ChildItem -LiteralPath $MetadataDirectory -Filter '*.winmd' | ForEach-Object {'/reference:'+ $_.FullName}
  }
  & "$framework\csc.exe" /nologo /utf8output /target:winexe /platform:x64 /optimize+ /checked- ('/define:'+$mode) "/out:$out" $resources $refs $files
  if($LASTEXITCODE -ne 0){throw "$mode compilation failed"}
  if($mode -eq 'USB' -and (Get-Item -LiteralPath $out).Length -gt 65536){throw 'USB build exceeds 64 KiB budget'}
  Get-Item -LiteralPath $out | Select-Object Name,Length
 }
}finally{
 [Array]::Clear($keyBytes,0,$keyBytes.Length)
 if((Split-Path -Parent $temporary) -eq ([IO.Path]::GetTempPath().TrimEnd('\')) -and (Split-Path -Leaf $temporary) -match '^AtomClip-build-[a-f0-9]{32}$'){
  Remove-Item -LiteralPath $temporary -Recurse -Force
 }
}
