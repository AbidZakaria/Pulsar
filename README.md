# Pulsar

A small offline music player for Windows. Plays a folder of audio files with:

- shuffle / in-order playback with a proper back/forward history
- retention tracking per song (rolling window) and an explore-vs-exploit shuffle
- automatic per-song volume that learns your preferred level
- 20 beat-reactive background styles (incl. Conway's Game of Life) that learn which you like
- built-in YouTube / YT Music downloader (needs yt-dlp + ffmpeg, installed via winget on first use)

`player.html` is the whole UI. `native/Program.cs` is a tiny WebView2 host that is compiled into a single `Pulsar.exe`.

## Build
Needs Windows 10/11 (WebView2 runtime). `build.ps1` compiles with the .NET Framework compiler built into Windows and embeds everything
into `release\Pulsar.exe`. It expects `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll` and `WebView2Loader.dll`
in `native\lib` (get them from the `Microsoft.Web.WebView2` NuGet package; they are not committed).
