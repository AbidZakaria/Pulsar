# Builds release\Pulsar.apk (signed with a local debug-style key). Needs JDK 17 + Android SDK (build-tools 34, android-34);
# defaults to the copies in %LOCALAPPDATA%\PulsarBuild, override with $env:PULSAR_BUILD.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot; $b = if ($env:PULSAR_BUILD) { $env:PULSAR_BUILD } else { "$env:LOCALAPPDATA\PulsarBuild" }
$jdk = (Get-ChildItem "$b\jdk" | Select-Object -First 1).FullName; $sdk = "$b\sdk"; $bt = "$sdk\build-tools\34.0.0"; $plat = "$sdk\platforms\android-34\android.jar"
$env:JAVA_HOME = $jdk; $w = "$root\android\build"
Remove-Item $w -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force "$w\cls", "$w\gen", "$w\assets", "$root\release" | Out-Null
Copy-Item "$root\player.html" "$w\assets\player.html"
& "$bt\aapt2.exe" compile --dir "$root\android\res" -o "$w\res.zip"
& "$bt\aapt2.exe" link -o "$w\base.apk" -I $plat --manifest "$root\android\AndroidManifest.xml" -A "$w\assets" --java "$w\gen" "$w\res.zip" --min-sdk-version 24 --target-sdk-version 34
$ErrorActionPreference = 'Continue'
& "$jdk\bin\javac.exe" -source 8 -target 8 -nowarn -cp $plat -d "$w\cls" (Get-ChildItem "$root\android\src", "$w\gen" -Recurse -Filter *.java).FullName 2>$null
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }
$ErrorActionPreference = "Stop"
& "$bt\d8.bat" --lib $plat --min-api 24 --output $w (Get-ChildItem "$w\cls" -Recurse -Filter *.class).FullName
Push-Location $w; & "$jdk\bin\jar.exe" uf base.apk classes.dex; Pop-Location
& "$bt\zipalign.exe" -f 4 "$w\base.apk" "$w\aligned.apk"
$ks = "$b\pulsar.keystore"
if (-not (Test-Path $ks)) { & "$jdk\bin\keytool.exe" -genkeypair -keystore $ks -storepass pulsar123 -keypass pulsar123 -alias pulsar -keyalg RSA -keysize 2048 -validity 36500 -dname "CN=Pulsar" }
& "$bt\apksigner.bat" sign --ks $ks --ks-pass pass:pulsar123 --key-pass pass:pulsar123 --out "$root\release\Pulsar.apk" "$w\aligned.apk"
"built release\Pulsar.apk"
