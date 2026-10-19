param([string]$ToolsDirectory=(Join-Path $PSScriptRoot '..\.tools\android_tools'),[switch]$CoreOnly,[string]$Java='java',[string]$Javac='javac')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$classes=Join-Path $env:TEMP ('AtomClip-android-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $classes | Out-Null
if($CoreOnly){
 $sources=@('EditorHistory.java','EspRom.java','FirmwareBundle.java') | ForEach-Object {Join-Path $repo ('src\android\'+$_)}
 & $Javac -encoding UTF-8 --release 8 -d $classes $sources (Join-Path $repo 'tests\AndroidFeatureTests.java')
}else{
 $jdk=(Get-ChildItem (Join-Path $ToolsDirectory 'jdk') -Directory | Select-Object -First 1).FullName
 $androidJar=(Get-ChildItem (Join-Path $ToolsDirectory 'platform') -Filter android.jar -Recurse | Select-Object -First 1).FullName
 $Java=Join-Path $jdk 'bin\java.exe'
 & "$jdk\bin\javac.exe" -encoding UTF-8 --release 8 -classpath $androidJar -d $classes (Get-ChildItem (Join-Path $repo 'src\android') -Filter '*.java' | ForEach-Object FullName) (Join-Path $repo 'tests\AndroidFeatureTests.java')
}
if($LASTEXITCODE -ne 0){throw 'Android compilation failed'}
& $Java -cp $classes dev.atomclip.mobile.AndroidFeatureTests
if($LASTEXITCODE -ne 0){throw 'Android feature tests failed'}
Write-Host 'Java protocol/undo tests passed. Android UI, USB permissions, OTG electrical compatibility and real flashing still require a phone.'
