param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Destination,[Parameter(Mandatory)][byte[]]$LegacyKey)
$ErrorActionPreference='Stop'
$manifest=Get-Content -LiteralPath (Join-Path $Directory 'bundle.json') -Raw|ConvertFrom-Json
if($manifest.schema -ne 2 -or $manifest.builds.Count -ne 4){throw 'A complete schema2 firmware bundle is required'}
$sha=[Security.Cryptography.SHA256]::Create();try{$pairHash=[BitConverter]::ToString($sha.ComputeHash($LegacyKey)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
New-Item -ItemType Directory -Path $Destination -Force|Out-Null
$ids=@{}
foreach($b in $manifest.builds){
 if($b.schema -ne 2 -or $b.chip -ne 'esp32s3' -or $b.profile -notin @(1,260,264,272) -or $ids.ContainsKey($b.profile) -or $b.segments.Count -ne 4){throw 'Invalid firmware manifest'};$ids[$b.profile]=$true
 if($b.pairingMode -eq 'migration' -and $b.pairingHash -ne $pairHash){throw 'Migration firmware does not match the client legacy pairing'}
 foreach($s in $b.segments){if($s.file -notmatch '^[a-z0-9-]+\.bin$'){throw 'Invalid firmware path'};$file=Join-Path $Directory $s.file;if((Get-Item -LiteralPath $file).Length -ne $s.length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $s.sha256){throw 'Firmware segment integrity failure'};Copy-Item -LiteralPath $file -Destination (Join-Path $Destination $s.file) -Force}
}
Copy-Item -LiteralPath (Join-Path $Directory 'bundle.json') -Destination (Join-Path $Destination 'bundle.json') -Force
