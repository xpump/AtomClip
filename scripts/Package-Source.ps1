param([string]$OutputFile=(Join-Path $PSScriptRoot '..\..\AtomClip-v0.1.0-source.zip'))
$ErrorActionPreference='Stop';$repo=Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Check-Publish.ps1')
$stage=Join-Path $env:TEMP ('AtomClip-source-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach($name in @('assets','src','scripts','tests','docs','licenses','.github')){
 $from=Join-Path $repo $name;if(!(Test-Path -LiteralPath $from)){continue}
 foreach($file in (Get-ChildItem -LiteralPath $from -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'})){
  $relative=$file.FullName.Substring($repo.Length+1);$target=Join-Path $stage $relative
  New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null;Copy-Item -LiteralPath $file.FullName -Destination $target
 }
}
Get-ChildItem -LiteralPath $repo -File | Copy-Item -Destination $stage
Add-Type -AssemblyName System.IO.Compression.FileSystem
if(Test-Path -LiteralPath $OutputFile){Remove-Item -LiteralPath $OutputFile -Force}
[IO.Compression.ZipFile]::CreateFromDirectory($stage,[IO.Path]::GetFullPath($OutputFile),[IO.Compression.CompressionLevel]::Optimal,$false)
$archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($OutputFile))
try{
 foreach($entry in $archive.Entries){$local=Join-Path $stage $entry.FullName;$stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create();try{$actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()};if($actual -ne (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash){throw 'Source ZIP content mismatch'}}
 Write-Host ('PASS: '+$archive.Entries.Count+' source ZIP entries verified, including dotfiles.')
}finally{$archive.Dispose()}
Get-Item -LiteralPath $OutputFile | Select-Object Name,Length
