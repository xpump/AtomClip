param(
    [string]$ToolsDirectory = (Join-Path $PSScriptRoot '..\.tools\android_tools'),
    [string]$BuildDirectory = (Join-Path $env:TEMP 'AtomClip-android'),
    [string]$OutputFile = (Join-Path $PSScriptRoot '..\dist\Android\AtomClipBLE.apk'),
    [string]$FirmwareDirectory = (Join-Path $PSScriptRoot '..\dist\firmware\bundle')
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$BuildDirectory = Join-Path $BuildDirectory ([Guid]::NewGuid().ToString('N'))
$PrivateDirectory = if ($env:ATOMCLIP_PRIVATE_DIR) { $env:ATOMCLIP_PRIVATE_DIR } else { Join-Path $RepoRoot '.local\private' }
$jdk = (Get-ChildItem (Join-Path $ToolsDirectory 'jdk') -Directory | Select-Object -First 1).FullName
$tools = (Get-ChildItem (Join-Path $ToolsDirectory 'build-tools') -Directory | Select-Object -First 1).FullName
$platform = (Get-ChildItem (Join-Path $ToolsDirectory 'platform') -Directory | Select-Object -First 1).FullName
$androidJar = Join-Path $platform 'android.jar'
New-Item -ItemType Directory -Path "$BuildDirectory\classes","$BuildDirectory\dex","$BuildDirectory\assets",([IO.Path]::GetDirectoryName($OutputFile)) -Force | Out-Null
$key = [Convert]::FromBase64String((Get-Content (Join-Path $PrivateDirectory 'pairing.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Key)
if ($key.Length -ne 32) { throw 'Invalid pairing file' }
[IO.File]::WriteAllBytes((Join-Path $BuildDirectory 'assets\pairing.bin'),$key)
& (Join-Path $PSScriptRoot 'Copy-FirmwareBundle.ps1') -Directory $FirmwareDirectory -Destination (Join-Path $BuildDirectory 'assets\firmware') -LegacyKey $key
[Array]::Clear($key,0,$key.Length)
Copy-Item -LiteralPath (Join-Path $RepoRoot 'boards\profiles.json') -Destination (Join-Path $BuildDirectory 'assets\boards.json') -Force
$licenseFiles=@((Join-Path $RepoRoot 'LICENSE'))+@(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'licenses') -File | Where-Object Extension -ne '.zip' | ForEach-Object FullName)
$licenseText=foreach($licenseFile in $licenseFiles){[IO.File]::ReadAllText($licenseFile)}
[IO.File]::WriteAllText((Join-Path $BuildDirectory 'assets\licenses.txt'),($licenseText -join "`n`n"),[Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'BUILD_MEMORY_RECEIVER.ps1') -OutputFile (Join-Path $BuildDirectory 'assets\receiver.txt')
& (Join-Path $PSScriptRoot 'BUILD_MEMORY_RECEIVER.ps1') -OutputFile (Join-Path $BuildDirectory 'assets\receiver-duplex.txt') -Bidirectional
$javaFiles = Get-ChildItem (Join-Path $RepoRoot 'src\android') -Filter '*.java' | ForEach-Object FullName
& "$jdk\bin\javac.exe" -encoding UTF-8 --release 8 -classpath $androidJar -d "$BuildDirectory\classes" $javaFiles
if ($LASTEXITCODE -ne 0) { throw 'Java compilation failed' }
& "$jdk\bin\jar.exe" cf "$BuildDirectory\classes.jar" -C "$BuildDirectory\classes" .
& "$jdk\bin\java.exe" -cp "$tools\lib\d8.jar" com.android.tools.r8.D8 --lib $androidJar --min-api 26 --output "$BuildDirectory\dex" "$BuildDirectory\classes.jar"
if ($LASTEXITCODE -ne 0) { throw 'DEX compilation failed' }
Copy-Item -LiteralPath (Join-Path $RepoRoot 'src\android\res') -Destination (Join-Path $BuildDirectory 'res') -Recurse
Copy-Item -LiteralPath (Join-Path $RepoRoot 'src\android\AndroidManifest.xml') -Destination (Join-Path $BuildDirectory 'AndroidManifest.xml')
& "$tools\aapt2.exe" compile --dir "$BuildDirectory\res" -o "$BuildDirectory\resources.zip"
if ($LASTEXITCODE -ne 0) { throw 'Android resource compilation failed' }
& "$tools\aapt2.exe" link --manifest "$BuildDirectory\AndroidManifest.xml" -I $androidJar -A "$BuildDirectory\assets" --min-sdk-version 26 --target-sdk-version 35 -o "$BuildDirectory\unsigned.apk" "$BuildDirectory\resources.zip"
if ($LASTEXITCODE -ne 0) { throw 'APK packaging failed' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::Open("$BuildDirectory\unsigned.apk",[IO.Compression.ZipArchiveMode]::Update)
try { Get-ChildItem "$BuildDirectory\dex" -Filter '*.dex' | ForEach-Object { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$_.FullName,$_.Name) | Out-Null } } finally { $zip.Dispose() }
& "$tools\zipalign.exe" -p -f 4 "$BuildDirectory\unsigned.apk" "$BuildDirectory\aligned.apk"
if ($LASTEXITCODE -ne 0) { throw 'APK alignment failed' }
$keystore = Join-Path $PrivateDirectory 'android-signing.p12'
$passwordPath = Join-Path $PrivateDirectory 'android-signing-password.txt'
if (-not (Test-Path -LiteralPath $keystore)) {
    $password = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($passwordPath,$password)
    & "$jdk\bin\keytool.exe" -genkeypair -keystore $keystore -storetype PKCS12 -storepass $password -keypass $password -alias atomclip -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=AtomClip Local Build'
    if ($LASTEXITCODE -ne 0) { throw 'APK signing key creation failed' }
}
& "$jdk\bin\java.exe" -jar "$tools\lib\apksigner.jar" sign --ks $keystore --ks-pass "file:$passwordPath" --ks-key-alias atomclip --out $OutputFile "$BuildDirectory\aligned.apk"
if ($LASTEXITCODE -ne 0) { throw 'APK signing failed' }
& "$jdk\bin\java.exe" -jar "$tools\lib\apksigner.jar" verify --verbose $OutputFile
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed' }
Get-Item -LiteralPath $OutputFile | Select-Object Name,Length
