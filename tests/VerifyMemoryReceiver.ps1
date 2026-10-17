param([Parameter(Mandatory=$true)][string]$BootstrapFile,
[Parameter(Mandatory=$true)][string]$PeerAssembly)
$ErrorActionPreference='Stop'
try {
    $tokens=$null; $parseErrors=$null
    $text=[IO.File]::ReadAllText($BootstrapFile)
    $ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$parseErrors)
    if($parseErrors.Count){throw 'Initialization PowerShell parse failed'}
    $cmd=$ast.Find({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Add-Type'},$true)
    $code=$cmd.CommandElements[$cmd.CommandElements.Count-1].Value
    $test=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MemoryReceiverTests.cs'))
    $usings=[regex]::Matches(($code+[Environment]::NewLine+$test),'(?m)^using [A-Za-z_][\w.]*;\r?$')|ForEach-Object{$_.Value.Trim()}|Sort-Object -Unique
    $body=[regex]::Replace(($code+[Environment]::NewLine+$test),'(?m)^using [A-Za-z_][\w.]*;\r?\n','')
    $joined=($usings -join [Environment]::NewLine)+[Environment]::NewLine+$body
    Add-Type -TypeDefinition $joined -ReferencedAssemblies @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll') -ErrorAction Stop
    [MemoryReceiverTests]::Run($PeerAssembly)
} catch {
    Write-Host ('FAIL: '+$_.Exception.GetType().Name+'; '+$_.FullyQualifiedErrorId)
    exit 1
}
