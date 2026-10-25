# Builds release\Pulsar.apk with Gradle. Needs JDK 17, the Android SDK (platform 34, build-tools 34) and Gradle 8.10;
# defaults to the copies in %LOCALAPPDATA%\PulsarBuild (override the folder with $env:PULSAR_BUILD).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot; $b = if ($env:PULSAR_BUILD) { $env:PULSAR_BUILD } else { "$env:LOCALAPPDATA\PulsarBuild" }
$env:JAVA_HOME = (Get-ChildItem "$b\jdk" | Select-Object -First 1).FullName
$env:PULSAR_KEYSTORE = "$b\pulsar.keystore"
$env:ANDROID_HOME = "$b\sdk"
$a = "$root\android"
"sdk.dir=$(("$b\sdk") -replace '\\','/')" | Set-Content "$a\local.properties" -Encoding ascii
New-Item -ItemType Directory -Force "$a\app\src\main\assets", "$root\release" | Out-Null
Copy-Item "$root\player.html" "$a\app\src\main\assets\player.html" -Force
Push-Location $a
try {
    & "$b\gradle-8.10.2\bin\gradle.bat" --no-daemon -q assembleRelease
    if ($LASTEXITCODE -ne 0) { throw 'gradle failed' }
} finally { Pop-Location }
Copy-Item "$a\app\build\outputs\apk\release\app-release.apk" "$root\release\Pulsar.apk" -Force
"built release\Pulsar.apk"
