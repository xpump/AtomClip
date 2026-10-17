param([string]$PrivateDirectory=(Join-Path $PSScriptRoot '..\.local\private'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $PrivateDirectory -Force | Out-Null
$file=Join-Path $PrivateDirectory 'pairing.json'
if(Test-Path -LiteralPath $file){Write-Host 'Existing pairing retained.';return}
$bytes=New-Object byte[] 32
$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try{
 $rng.GetBytes($bytes)
 $json=@{Key=[Convert]::ToBase64String($bytes)} | ConvertTo-Json
 [IO.File]::WriteAllText([IO.Path]::GetFullPath($file),$json,[Text.UTF8Encoding]::new($false))
 Write-Host 'Created a unique pairing key. Value not printed.'
}finally{$rng.Dispose();[Array]::Clear($bytes,0,$bytes.Length)}
