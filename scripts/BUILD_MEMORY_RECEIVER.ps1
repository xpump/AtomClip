param([string]$OutputFile,[switch]$Bidirectional)
$ErrorActionPreference='Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PrivateDirectory = if ($env:ATOMCLIP_PRIVATE_DIR) { $env:ATOMCLIP_PRIVATE_DIR } else { Join-Path $RepoRoot '.local\private' }
if (!$OutputFile) { throw 'Specify a private build output file' }
$protocol=[IO.File]::ReadAllText((Join-Path $RepoRoot 'src\windows\UsbProtocol.cs'))
$protocol=[regex]::Replace($protocol,'(?m)^#if !USB\r?\n','')
$start=$protocol.IndexOf('        public static byte[] CreateTypeJob(')
$end=$protocol.IndexOf('    public sealed class PacketParser',$start)
if ($start -lt 0 -or $end -le $start) { throw 'Typing protocol source boundaries changed' }
$protocol=$protocol.Remove($start,$end-$start).Insert($start,"    }`n`n")
$sources=@($protocol,[IO.File]::ReadAllText((Join-Path $RepoRoot 'src\windows\MemoryReceiver.cs')),[IO.File]::ReadAllText((Join-Path $RepoRoot 'src\windows\ClipboardSender.cs')))
$usings=New-Object 'System.Collections.Generic.HashSet[string]'
$bodies=foreach($source in $sources) {
    foreach($m in [regex]::Matches($source,'(?m)^using [A-Za-z_][\w.]*;\r?$')){[void]$usings.Add($m.Value.Trim())}
    $body=[regex]::Replace($source,'(?m)^using [A-Za-z_][\w.]*;\r?\n','')
    (($body -split '\r?\n') | Where-Object {$_.Trim().Length -gt 0 -and !$_.TrimStart().StartsWith('//')} | ForEach-Object { $_.TrimStart() }) -join "`n"
}
$code=(($usings|Sort-Object)-join "`n")+"`n"+($bodies -join "`n")
# Retain every line and quoted literal; omit optional punctuation spacing.
$code=[regex]::Replace($code,'("(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*'')|[ \t]*([{}();,])[ \t]*',{param($m) if($m.Groups[1].Success){$m.Value}else{$m.Groups[2].Value}})
$pairing=Get-Content -LiteralPath (Join-Path $PrivateDirectory 'pairing.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$keyBytes=[Convert]::FromBase64String($pairing.Key)
try {
    if($keyBytes.Length -ne 32){throw 'Invalid pairing key'}
    $prefix=@'
& {
$ErrorActionPreference='Stop'
try {
Add-Type -ReferencedAssemblies @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll') -ErrorAction Stop -TypeDefinition @'
'@
    $suffix=@'
} catch { Write-Host ('Receiver startup failed: '+$_.Exception.GetType().Name) }
}
'@
    $modeArgument=if($Bidirectional){', $true'}else{''}
    $text=$prefix+"`n"+$code+"`n'@`n"+"[AtomClipUsb.MemoryReceiver]::Run('"+$pairing.Key+"'"+$modeArgument+")`n"+$suffix
    $text=$text -replace '\r?\n',"`r`n"
    $text=$text.TrimEnd([char[]]"`r`n")+"`r`n`r`n"
    $count=[Text.Encoding]::UTF8.GetByteCount($text)
    if($count -gt 16384){throw ('Fixed bootstrap exceeds text limit: '+$count)}
    New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutputFile))) -Force | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputFile),$text,[Text.UTF8Encoding]::new($false))
    Write-Host ('Fixed memory receiver initialization bytes: '+$count)
} finally { [Array]::Clear($keyBytes,0,$keyBytes.Length) }
