param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Profile,[ValidateSet('migration','independent')][string]$PairingMode='independent')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$catalog=Get-Content -LiteralPath (Join-Path $repo 'boards/profiles.json') -Raw | ConvertFrom-Json
$p=@($catalog.profiles|Where-Object key -eq $Profile);if($p.Count -ne 1){throw 'Unknown profile'};$p=$p[0]
$merged=[IO.File]::ReadAllBytes((Join-Path $Directory 'AtomClip.ino.merged.bin'))
if($merged.Length -ne $p.flashBytes){throw 'Merged image capacity mismatch'}
$parts=@(@{name='bootloader';offset=0;file='AtomClip.ino.bootloader.bin'},@{name='partitions';offset=32768;file='AtomClip.ino.partitions.bin'},@{name='boot-selection';offset=57344;file=''},@{name='app';offset=65536;file='AtomClip.ino.bin'})
$segments=foreach($part in $parts){
 $bytes=if($part.file){[IO.File]::ReadAllBytes((Join-Path $Directory $part.file))}else{$merged[57344..65535] -as [byte[]]}
 if($part.name -eq 'partitions' -and $bytes.Length -ne 3072){throw 'Partition table must be 3072 bytes'}
 $file="$Profile-$($part.name).bin";$path=Join-Path $Directory $file;[IO.File]::WriteAllBytes($path,$bytes)
 @{offset=$part.offset;length=$bytes.Length;file=$file;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$pairingHash=''
if($PairingMode -eq 'migration'){
 $private=if($env:ATOMCLIP_PRIVATE_DIR){$env:ATOMCLIP_PRIVATE_DIR}else{Join-Path $repo '.local/private'}
 $key=[Convert]::FromBase64String((Get-Content -LiteralPath (Join-Path $private 'pairing.json') -Raw|ConvertFrom-Json).Key)
 $sha=[Security.Cryptography.SHA256]::Create();try{$pairingHash=[BitConverter]::ToString($sha.ComputeHash($key)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();[Array]::Clear($key,0,$key.Length)}
}
$manifest=@{schema=2;profile=$p.id;key=$p.key;chip='esp32s3';flashBytes=$p.flashBytes;pairingMode=$PairingMode;pairingHash=$pairingHash;version='0.2.0';segments=@($segments)}
[IO.File]::WriteAllText((Join-Path $Directory 'manifest.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
