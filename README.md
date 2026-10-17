# Pulsar

**[Download PulsarSetup.exe](https://github.com/AbidZakaria/Pulsar/raw/main/release/PulsarSetup.exe)** - double-click and follow the window. It installs Pulsar (Start menu + optional desktop shortcut) and, if you tick the box, the music downloader (ffmpeg + yt-dlp, about 130 MB of downloads). No admin rights needed; uninstall from *Settings -> Apps*.
Windows 10/11 only. SmartScreen may warn because the files are unsigned: click *More info* -> *Run anyway*.

Prefer no installer? [Pulsar.exe](https://github.com/AbidZakaria/Pulsar/raw/main/release/Pulsar.exe) is the same app as a single portable file (the downloader then installs its tools on first use through winget).

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



## License
[MIT](LICENSE) - (c) 2026 Abid Zakaria. You're free to use, modify and share Pulsar, as long as you keep the copyright notice and license text (i.e. credit the original author).

Third-party pieces: the app uses Microsoft WebView2 (Microsoft's own license). The optional downloader uses yt-dlp (Unlicense) and ffmpeg (GPL/LGPL), which the installer downloads from their official sites rather than bundling them.
