using System;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

class Program
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);

    public static byte[] Res(string name)
    {
        using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (st == null) return null;
            var b = new byte[st.Length];
            int off = 0; while (off < b.Length) off += st.Read(b, off, b.Length - off);
            return b;
        }
    }

    [STAThread]
    static void Main()
    {
        // everything is embedded: managed DLLs load from resources, the native loader is unpacked once
        AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
        {
            var b = Res(new AssemblyName(e.Name).Name + ".dll");
            return b == null ? null : Assembly.Load(b);
        };
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfflinePlayer", "bin");
        Directory.CreateDirectory(bin);
        var native = Path.Combine(bin, "WebView2Loader.dll");
        var nb = Res("WebView2Loader.dll");
        if (!File.Exists(native) || new FileInfo(native).Length != nb.Length) File.WriteAllBytes(native, nb);
        LoadLibrary(native);
        Application.EnableVisualStyles();
        Application.Run(new MainForm());
    }
}

class MainForm : Form
{
    WebView2 wv = new WebView2();
    string appDir = AppDomain.CurrentDomain.BaseDirectory;
    string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfflinePlayer");
    string cfgFile, music;

    public MainForm()
    {
        Text = "Pulsar";
        BackColor = Color.FromArgb(11, 11, 13);
        ClientSize = new Size(520, 900);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        wv.Dock = DockStyle.Fill;
        wv.DefaultBackgroundColor = Color.FromArgb(11, 11, 13);
        Controls.Add(wv);
        cfgFile = Path.Combine(dataDir, "music-folder.txt");
        Directory.CreateDirectory(dataDir);
        if (File.Exists(cfgFile)) music = File.ReadAllText(cfgFile).Trim();
        Load += async delegate { await Init(); };
    }

    bool PickFolder()
    {
        using (var d = new FolderBrowserDialog())
        {
            d.Description = "Choose your music folder";
            if (music != null && Directory.Exists(music)) d.SelectedPath = music;
            if (d.ShowDialog(this) != DialogResult.OK) return false;
            music = d.SelectedPath;
            File.WriteAllText(cfgFile, music);
            Map();
            return true;
        }
    }

    void Map()
    {
        // one origin for page + music: a folder holding player.html plus a junction "music" -> your music folder
        var site = Path.Combine(dataDir, "site");
        Directory.CreateDirectory(site);
        File.WriteAllBytes(Path.Combine(site, "player.html"), Program.Res("player.html"));
        var c = wv.CoreWebView2;
        try { c.ClearVirtualHostNameToFolderMapping("app.local"); } catch { }
        c.SetVirtualHostNameToFolderMapping("app.local", site, CoreWebView2HostResourceAccessKind.Allow);
    }

    async System.Threading.Tasks.Task Init()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "wv"),
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
        await wv.EnsureCoreWebView2Async(env);
        var c = wv.CoreWebView2;
        c.Settings.AreDevToolsEnabled = false;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.AreDefaultContextMenusEnabled = false;

        // music is served by us from the chosen folder (CORS-enabled) on an unmapped host
        c.AddWebResourceRequestedFilter("https://music.local/*", CoreWebView2WebResourceContext.All);
        c.WebResourceRequested += delegate (object s, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string name = Path.GetFileName(Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath));
            string path = music == null ? null : Path.Combine(music, name);
            if (path != null && File.Exists(path))
                e.Response = c.Environment.CreateWebResourceResponse(File.OpenRead(path), 200, "OK",
                    "Access-Control-Allow-Origin: *\r\nContent-Type: application/octet-stream");
            else
                e.Response = c.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Access-Control-Allow-Origin: *");
        };
        c.AddWebResourceRequestedFilter("https://preview.local/*", CoreWebView2WebResourceContext.All);
        c.WebResourceRequested += delegate (object s, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!e.Request.Uri.StartsWith("https://preview.local/")) return;
            string name = Path.GetFileName(Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath));
            string path = Path.Combine(PreviewDir, name);
            if (name.Length > 0 && File.Exists(path))
                e.Response = c.Environment.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(path)), 200, "OK",
                    "Access-Control-Allow-Origin: *\r\nContent-Type: audio/mpeg");
            else
                e.Response = c.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Access-Control-Allow-Origin: *");
        };
        c.WebMessageReceived += delegate (object s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (e.TryGetWebMessageAsString() != null && e.TryGetWebMessageAsString().StartsWith("pv:")) { QueuePreview(e.TryGetWebMessageAsString().Substring(3)); return; }
            var msg = e.TryGetWebMessageAsString();
            if (msg == "pick") { if (PickFolder()) SendList(false); }
            else if (msg == "dlcancel") CancelDownload();
            else if (msg != null && msg.StartsWith("dl:")) StartDownload(msg.Substring(3));
            else if (msg != null && msg.StartsWith("ex:")) StartExplore(msg.Substring(3));
        };
        c.NavigationCompleted += delegate { SendList(false); };

        if (music == null || !Directory.Exists(music))
        {
            music = null;
            if (!PickFolder()) { Close(); return; }
        }
        else Map();
        c.Navigate("https://app.local/player.html");
        if (Environment.GetEnvironmentVariable("OP_DEBUG") != null)
        {
            c.NavigationCompleted += delegate { c.ExecuteScriptAsync("fetch('/player.html').then(r=>{window.__a=r.status}).catch(e=>{window.__a='ERR '+e});fetch('https://music.local/'+encodeURIComponent(files[0].name)).then(r=>{window.__b=r.status+' '+files[0].name}).catch(e=>{window.__b='ERR '+e+' '+files[0].name})"); };
            var t = new Timer(); t.Interval = 8000;
            t.Tick += async delegate {
                t.Stop();
                var r = await c.ExecuteScriptAsync("(window.__selftest?window.__selftest():'no selftest')");
                File.WriteAllText(Path.Combine(dataDir, "debug.txt"), r);
            };
            t.Start();
        }
    }

    void SendList(bool keep)
    {
        if (music == null || wv.CoreWebView2 == null) return;
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var f in Directory.GetFiles(music))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".mp3" && ext != ".m4a" && ext != ".opus" && ext != ".ogg" && ext != ".flac" && ext != ".wav") continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(Path.GetFileName(f).Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        sb.Append(']');
        wv.CoreWebView2.ExecuteScriptAsync("window.nativeInit&&window.nativeInit(" + sb + "," + (keep ? "true" : "false") + ")");
    }

    // ---------------- downloads via yt-dlp ----------------
    Process dl;
    static bool updated;

    static string Js(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ") + "\"";
    }

    void Ev(string type, string text, int pct)
    {
        try
        {
            BeginInvoke(new Action(delegate
            {
                if (wv.CoreWebView2 != null)
                    wv.CoreWebView2.ExecuteScriptAsync("window.dlEvent&&window.dlEvent({t:" + Js(type) + ",m:" + Js(text) + ",p:" + pct + "})");
            }));
        }
        catch { }
    }

    static string FindTool(string name)
    {
        var exe = name + ".exe";
        var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfflinePlayer", "tools", exe);
        if (File.Exists(own)) return own;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { if (d.Trim().Length > 0) { var p = Path.Combine(d.Trim(), exe); if (File.Exists(p)) return p; } } catch { }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var link = Path.Combine(local, "Microsoft", "WinGet", "Links", exe);
        if (File.Exists(link)) return link;
        var pk = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(pk))
        {
            try { var f = Directory.GetFiles(pk, exe, SearchOption.AllDirectories); if (f.Length > 0) return f[0]; } catch { }
        }
        return null;
    }

    static void RunWait(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args);
        psi.CreateNoWindow = true; psi.UseShellExecute = false;
        using (var p = Process.Start(psi)) { p.WaitForExit(600000); }
    }

    // ---------------- explore: similar songs via YouTube's own mix for each seed song ----------------
    bool exploring;

    void ExJs(string script)
    {
        try { BeginInvoke(new Action(delegate { if (wv.CoreWebView2 != null) wv.CoreWebView2.ExecuteScriptAsync(script); })); }
        catch { }
    }

    void StartExplore(string seeds)
    {
        if (exploring) return;
        var list = new System.Collections.Generic.List<string>();
        foreach (var s in seeds.Split('|'))
        {
            var t = s.Replace("\"", "").Replace("\\", "").Trim();
            if (t.Length > 0 && list.Count < 3) list.Add(t);
        }
        if (list.Count == 0) { ExJs("window.exEvent&&window.exEvent({t:'err',m:'Listen to a few songs first so Pulsar knows what you like'})"); return; }
        exploring = true;
        try   // drop old preview clips
        {
            foreach (var f in Directory.GetFiles(PreviewDir))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) File.Delete(f);
        }
        catch { }
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { RunExplore(list); });
    }

    // ---- previews: only a short clip of each suggestion is fetched, one at a time; clicked ones jump the queue ----
    string PreviewDir { get { var d = Path.Combine(dataDir, "preview"); Directory.CreateDirectory(d); return d; } }
    readonly System.Collections.Generic.List<string> pvQueue = new System.Collections.Generic.List<string>();
    bool pvRunning;

    void QueuePreview(string arg)
    {   // arg: "<id>" (prefetch) or "!<id>" (user wants it now)
        bool now = arg.StartsWith("!");
        var id = arg.TrimStart('!');
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{6,20}$")) return;
        if (File.Exists(Path.Combine(PreviewDir, id + ".mp3"))) { ExJs("window.pvEvent&&window.pvEvent({id:\"" + id + "\",t:'ready'})"); return; }
        lock (pvQueue)
        {
            pvQueue.Remove(id);
            if (now) pvQueue.Insert(0, id); else pvQueue.Add(id);
            if (pvRunning) return;
            pvRunning = true;
        }
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { PreviewWorker(); });
    }

    void PreviewWorker()
    {
        try
        {
            var ytdlp = FindTool("yt-dlp"); var ff = FindTool("ffmpeg");
            while (true)
            {
                string id;
                lock (pvQueue) { if (pvQueue.Count == 0) { pvRunning = false; return; } id = pvQueue[0]; pvQueue.RemoveAt(0); }
                if (ytdlp == null || ff == null) { ExJs("window.pvEvent&&window.pvEvent({id:\"" + id + "\",t:'err'})"); continue; }
                ExJs("window.pvEvent&&window.pvEvent({id:\"" + id + "\",t:'load'})");
                try
                {
                    var psi = new ProcessStartInfo(ytdlp, "-x --audio-format mp3 --audio-quality 5 --download-sections \"*0:25-0:55\" --no-playlist --no-warnings --ffmpeg-location \"" +
                        Path.GetDirectoryName(ff) + "\" -P \"" + PreviewDir + "\" -o \"" + id + ".%(ext)s\" \"https://www.youtube.com/watch?v=" + id + "\"");
                    psi.CreateNoWindow = true; psi.UseShellExecute = false;
                    using (var p = Process.Start(psi)) { if (!p.WaitForExit(90000)) { try { p.Kill(); } catch { } } }
                }
                catch { }
                ExJs("window.pvEvent&&window.pvEvent({id:\"" + id + "\",t:'" + (File.Exists(Path.Combine(PreviewDir, id + ".mp3")) ? "ready" : "err") + "'})");
            }
        }
        catch { lock (pvQueue) { pvRunning = false; } }
    }

    // runs yt-dlp, returns the JSON lines it printed (one object per line)
    static System.Collections.Generic.List<string> YtLines(string ytdlp, string args)
    {
        var res = new System.Collections.Generic.List<string>();
        var psi = new ProcessStartInfo(ytdlp, args);
        psi.CreateNoWindow = true; psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        using (var p = Process.Start(psi))
        {
            p.ErrorDataReceived += delegate { };
            p.BeginErrorReadLine();
            var task = System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                string l; while ((l = p.StandardOutput.ReadLine()) != null) if (l.StartsWith("{")) res.Add(l);
            });
            if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } }
            task.Wait(2000);
        }
        return res;
    }

    static string JsonField(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    void RunExplore(System.Collections.Generic.List<string> seeds)
    {
        try
        {
            var ytdlp = FindTool("yt-dlp");
            if (ytdlp == null)
            {
                ExJs("window.exEvent&&window.exEvent({t:'err',m:'Download any song once first - that installs the downloader Explore needs'})");
                return;
            }
            var found = new System.Collections.Generic.List<string>();
            var ids = new System.Collections.Generic.HashSet<string>();
            int n = 0;
            foreach (var seed in seeds)
            {
                n++;
                ExJs("window.exEvent&&window.exEvent({t:'prog',m:" + Js("Finding songs like " + seed + " (" + n + "/" + seeds.Count + ")...") + "})");
                var hit = YtLines(ytdlp, "--flat-playlist --dump-json --playlist-end 1 --no-warnings \"ytsearch1:" + seed + " audio\"");
                if (hit.Count == 0) continue;
                var id = JsonField(hit[0], "id");
                if (id.Length == 0) continue;
                ids.Add(id);
                var mix = YtLines(ytdlp, "--flat-playlist --dump-json --playlist-end 14 --no-warnings \"https://www.youtube.com/watch?v=" + id + "&list=RD" + id + "\"");
                foreach (var l in mix)
                {
                    var mid = JsonField(l, "id");
                    if (mid.Length == 0 || !ids.Add(mid)) continue;
                    found.Add("{\"id\":\"" + mid + "\",\"title\":\"" + JsonField(l, "title") + "\",\"by\":\"" +
                        (JsonField(l, "channel").Length > 0 ? JsonField(l, "channel") : JsonField(l, "uploader")) + "\",\"seed\":" + Js(seed) + "}");
                }
            }
            ExJs("window.exResult&&window.exResult([" + string.Join(",", found.ToArray()) + "])");
        }
        catch (Exception ex) { ExJs("window.exEvent&&window.exEvent({t:'err',m:" + Js("Explore failed: " + ex.Message) + "})"); }
        finally { exploring = false; }
    }

    void CancelDownload()
    {
        try
        {
            if (dl != null && !dl.HasExited)
            {
                var k = new ProcessStartInfo("taskkill", "/PID " + dl.Id + " /T /F");
                k.CreateNoWindow = true; k.UseShellExecute = false;
                Process.Start(k);
                Ev("err", "Download cancelled", 0);
            }
        }
        catch { }
    }

    void StartDownload(string url)
    {
        if (dl != null && !dl.HasExited) { Ev("err", "A download is already running", 0); return; }
        Uri u;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http") ||
            !(u.Host.EndsWith("youtube.com") || u.Host == "youtu.be" || u.Host.EndsWith(".youtu.be")))
        { Ev("err", "Paste a YouTube or YouTube Music link", 0); return; }
        if (music == null || !Directory.Exists(music)) { Ev("err", "Choose a music folder first", 0); return; }
        var target = music; var link = u.AbsoluteUri;
        Ev("info", "Starting...", 0);
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { RunDownload(link, target); });
    }

    void RunDownload(string url, string target)
    {
        try
        {
            var ytdlp = FindTool("yt-dlp");
            if (ytdlp == null)
            {
                Ev("info", "Installing the downloader (one time, a few MB)...", 0);
                RunWait("winget", "install --id yt-dlp.yt-dlp -e --accept-source-agreements --accept-package-agreements --silent");
                ytdlp = FindTool("yt-dlp");
                if (ytdlp == null) { Ev("err", "Could not install yt-dlp. Run: winget install yt-dlp.yt-dlp", 0); return; }
            }
            if (!updated && ytdlp.IndexOf("OfflinePlayer", StringComparison.OrdinalIgnoreCase) >= 0)
            {   // the copy we installed keeps itself current (YouTube changes often)
                updated = true;
                try { Ev("info", "Checking for downloader updates...", 0); RunWait(ytdlp, "-U"); } catch { }
            }
            var ff = FindTool("ffmpeg");
            if (ff == null)
            {
                Ev("info", "Installing ffmpeg (one time)...", 0);
                RunWait("winget", "install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements --silent");
                ff = FindTool("ffmpeg");
                if (ff == null) { Ev("err", "Could not install ffmpeg. Run: winget install Gyan.FFmpeg", 0); return; }
            }
            var archive = Path.Combine(dataDir, "downloaded.txt");
            var args = "-x --audio-format mp3 --audio-quality 0 --embed-thumbnail --embed-metadata --yes-playlist --ignore-errors --no-overwrites --newline" +
                " --ffmpeg-location \"" + Path.GetDirectoryName(ff) + "\" --download-archive \"" + archive + "\" -P \"" + target.TrimEnd('\\') + "\"" +
                " -o \"%(artist,uploader)s - %(title)s.%(ext)s\" \"" + url + "\"";
            var psi = new ProcessStartInfo(ytdlp, args);
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            int item = 1, total = 0, done = 0; double pct = 0; string name = "";
            var pctRe = new Regex(@"(\d+(?:\.\d+)?)%");
            var itemRe = new Regex(@"Downloading item (\d+) of (\d+)");
            dl = Process.Start(psi);
            DataReceivedEventHandler h = delegate (object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                var line = e.Data;
                var m = itemRe.Match(line);
                if (m.Success) { item = int.Parse(m.Groups[1].Value); total = int.Parse(m.Groups[2].Value); pct = 0; }
                else if (line.StartsWith("[download]") && pctRe.IsMatch(line)) pct = double.Parse(pctRe.Match(line).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                else if (line.StartsWith("[ExtractAudio] Destination:")) { done++; name = Path.GetFileNameWithoutExtension(line.Substring(line.IndexOf(':') + 1).Trim()); }
                else return;
                int overall = total > 0 ? (int)(((item - 1) + pct / 100.0) / total * 100) : (int)pct;
                Ev("prog", (total > 0 ? "Track " + item + " of " + total : "Downloading") + (name.Length > 0 ? " - last: " + name : ""), Math.Min(99, overall));
            };
            dl.OutputDataReceived += h; dl.ErrorDataReceived += h;
            dl.BeginOutputReadLine(); dl.BeginErrorReadLine();
            dl.WaitForExit();
            int code = dl.ExitCode;
            try { BeginInvoke(new Action(delegate { SendList(true); })); } catch { }
            if (code == 0 || done > 0) Ev("done", "Done - " + done + " new track" + (done == 1 ? "" : "s") + " added" + (code != 0 ? " (some items failed or were skipped)" : ""), 100);
            else Ev("done", "Nothing new to download (already have it, or unavailable)", 100);
        }
        catch (Exception ex) { Ev("err", "Download failed: " + ex.Message, 0); }
    }
}
