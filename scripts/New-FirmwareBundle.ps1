param([Parameter(Mandatory)][string[]]$Directories,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$ids=@{};$builds=foreach($dir in $Directories){
 $m=Get-Content -LiteralPath (Join-Path $dir 'manifest.json') -Raw|ConvertFrom-Json
 if($m.schema -ne 2 -or $ids.ContainsKey([string]$m.profile)){throw 'Invalid or duplicate firmware manifest'};$ids[[string]$m.profile]=$true
 foreach($s in $m.segments){if($s.file -notmatch '^[a-z0-9-]+\.bin$'){throw 'Invalid segment path'};$file=Join-Path $dir $s.file;if((Get-Item -LiteralPath $file).Length -ne $s.length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $s.sha256){throw 'Invalid segment hash'};Copy-Item -LiteralPath $file -Destination (Join-Path $OutputDirectory $s.file) -Force};$m
}
if($ids.Count -ne 4 -or !$ids.ContainsKey('1') -or !$ids.ContainsKey('260') -or !$ids.ContainsKey('264') -or !$ids.ContainsKey('272')){throw 'Bundle must include exactly four supported board profiles'}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'bundle.json'),(@{schema=2;builds=@($builds)}|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
