# Builds release\Pulsar.exe: ONE self-contained file (DLLs + player.html + icon embedded)
$root = $PSScriptRoot; $out = "$root\release"; $src = 'C:\Program Files\Lively Wallpaper\Plugins\WebView2'; $lib = "$root\native\lib"
New-Item -ItemType Directory -Force $out, $lib | Out-Null
foreach ($f in 'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll') { if (Test-Path "$src\$f") { Copy-Item "$src\$f" $lib -Force } }
if (Test-Path "$src\runtimes\win-x64\native\WebView2Loader.dll") { Copy-Item "$src\runtimes\win-x64\native\WebView2Loader.dll" $lib -Force }
if (-not (Test-Path "$root\native\pulsar.ico")) { & "$root\native\make-icon.ps1" }
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /win32icon:"$root\native\pulsar.ico" /out:"$out\Pulsar.exe" `
  /r:"$lib\Microsoft.Web.WebView2.Core.dll" /r:"$lib\Microsoft.Web.WebView2.WinForms.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  /resource:"$lib\Microsoft.Web.WebView2.Core.dll",Microsoft.Web.WebView2.Core.dll /resource:"$lib\Microsoft.Web.WebView2.WinForms.dll",Microsoft.Web.WebView2.WinForms.dll `
  /resource:"$lib\WebView2Loader.dll",WebView2Loader.dll /resource:"$root\player.html",player.html "$root\native\Program.cs"

# ---- installer: PulsarSetup.exe carries Pulsar.exe inside ----
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /win32icon:"$root\native\pulsar.ico" /out:"$out\PulsarSetup.exe" `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
  /resource:"$out\Pulsar.exe",Pulsar.exe "$root\native\Setup.cs"
