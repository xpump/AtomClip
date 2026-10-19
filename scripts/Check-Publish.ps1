param([string]$PrivateDirectory='')
$ErrorActionPreference='Stop';$repo=Split-Path -Parent $PSScriptRoot
$paths=@('assets','boards','src','scripts','tests','docs','licenses','.github')
$files=@(Get-ChildItem -LiteralPath $repo -File)
foreach($path in $paths){$directory=Join-Path $repo $path;if(Test-Path -LiteralPath $directory){$files+=Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'}}}
$secret='';if($PrivateDirectory){$pairing=Join-Path $PrivateDirectory 'pairing.json';if(Test-Path -LiteralPath $pairing){$secret=(Get-Content -LiteralPath $pairing -Raw -Encoding UTF8 | ConvertFrom-Json).Key}}
foreach($file in $files){
 if($file.Extension -in @('.exe','.apk','.bin','.p12','.keystore','.jks','.log','.acbind') -or $file.Name -match '^(pairing\.json|PairingKey\.h|.*signing-password.*)$'){throw ('Private/generated file in publication set: '+$file.Name)}
 if($secret -and $file.Extension -in @('.cs','.java','.h','.ino','.ps1','.md','.json','.yml','.xml')){
  if([IO.File]::ReadAllText($file.FullName).Contains($secret)){throw ('Pairing material detected in '+$file.Name)}
 }
}
Write-Host ('PASS: '+$files.Count+' public source/document files; no generated binaries, signing files or provided pairing value. dist/.local/.tools/bin/obj are excluded.')
