using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// Pulsar installer / uninstaller. One exe: it carries Pulsar.exe inside, installs per-user (no admin needed),
// makes shortcuts, registers an uninstall entry and (optionally) fetches ffmpeg + yt-dlp from their official sites.
// Switches: /S silent, /D=path install dir, /nodl skip downloader tools, /nodesktop, /nostart, /uninstall, /wipe (with /uninstall: also delete settings+stats)
static class Setup
{
    public static bool Silent, NoDl, NoDesktop, NoStart, Wipe;
    public static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Pulsar");
    public static string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfflinePlayer");
    public static string Tools { get { return Path.Combine(Data, "tools"); } }
    const string UKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pulsar";

    [STAThread]
    static void Main(string[] a)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
        bool un = false, hasDir = false;
        foreach (var s in a)
        {
            var l = s.ToLowerInvariant();
            if (l == "/s") Silent = true;
            else if (l == "/uninstall") un = true;
            else if (l == "/nodl") NoDl = true;
            else if (l == "/nodesktop") NoDesktop = true;
            else if (l == "/nostart") NoStart = true;
            else if (l == "/wipe") Wipe = true;
            else if (l.StartsWith("/d=")) { Dir = s.Substring(3).Trim('"'); hasDir = true; }
        }
        Application.EnableVisualStyles();
        if (un)
        {
            if (!hasDir) Dir = Path.GetDirectoryName(Application.ExecutablePath);
            Uninstall();
            return;
        }
        Application.Run(new SetupForm());
    }

    // ---------- helpers ----------
    public static bool HasWebView2()
    {
        string[] keys = { @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" };
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var k in keys)
                using (var r = root.OpenSubKey(k))
                    if (r != null) { var v = r.GetValue("pv") as string; if (!string.IsNullOrEmpty(v) && v != "0.0.0.0") return true; }
        return false;
    }

    public static byte[] Res(string name)
    {
        using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            var b = new byte[st.Length]; int off = 0;
            while (off < b.Length) off += st.Read(b, off, b.Length - off);
            return b;
        }
    }

    public static void KillPulsar()
    {
        foreach (var p in Process.GetProcessesByName("Pulsar"))
        {
            try { if (p.MainModule.FileName.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); } } catch { }
        }
    }

    static object Com(object o, string name, BindingFlags f, params object[] args) { return o.GetType().InvokeMember(name, f, null, o, args); }

    public static void MakeShortcut(string lnk, string target, string icon)
    {
        var sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        var l = Com(sh, "CreateShortcut", BindingFlags.InvokeMethod, lnk);
        Com(l, "TargetPath", BindingFlags.SetProperty, target);
        Com(l, "WorkingDirectory", BindingFlags.SetProperty, Path.GetDirectoryName(target));
        Com(l, "IconLocation", BindingFlags.SetProperty, icon);
        Com(l, "Description", BindingFlags.SetProperty, "Pulsar - offline music player");
        Com(l, "Save", BindingFlags.InvokeMethod);
    }

    static string ShortcutTarget(string lnk)
    {
        try
        {
            var sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            var l = Com(sh, "CreateShortcut", BindingFlags.InvokeMethod, lnk);
            return (string)Com(l, "TargetPath", BindingFlags.GetProperty);
        }
        catch { return null; }
    }

    public static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Pulsar.lnk"); } }
    public static string StartLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Pulsar.lnk"); } }

    // ---------- uninstall ----------
    static void Uninstall()
    {
        if (!Silent && MessageBox.Show("Remove Pulsar from this computer?\n\nYour music files are never touched.", "Pulsar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        bool wipe = Wipe;
        if (!Silent && !Wipe)
            wipe = MessageBox.Show("Also delete Pulsar's saved settings, listening stats and downloader tools?\n\n(Choose No to keep them in case you reinstall.)", "Pulsar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        KillPulsar();
        foreach (var l in new[] { DesktopLnk, StartLnk })
        {
            var t = ShortcutTarget(l);
            if (t != null && t.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) try { File.Delete(l); } catch { }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(UKey, false); } catch { }
        if (wipe) try { Directory.Delete(Data, true); } catch { }
        // this exe may be running from the folder being removed, so let a helper delete it after we exit
        var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rd /s /q \"" + Dir + "\"");
        psi.CreateNoWindow = true; psi.UseShellExecute = false;
        Process.Start(psi);
        if (!Silent) MessageBox.Show("Pulsar has been removed.", "Pulsar", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------- install ----------
    public static async Task Install(bool desktop, bool tools, Action<int, string> progress)
    {
        if (!HasWebView2())
        {
            progress(3, "Installing Microsoft WebView2 (needed to display the app)...");
            var wv = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            await Fetch("https://go.microsoft.com/fwlink/p/?LinkId=2124703", wv, 3, 12, progress, "Downloading WebView2");
            await Task.Run(() => { var p = Process.Start(wv, "/silent /install"); p.WaitForExit(600000); });
        }
        progress(15, "Installing Pulsar...");
        KillPulsar();
        Directory.CreateDirectory(Dir);
        var exe = Path.Combine(Dir, "Pulsar.exe");
        File.WriteAllBytes(exe, Res("Pulsar.exe"));
        var un = Path.Combine(Dir, "Uninstall.exe");
        if (!string.Equals(Application.ExecutablePath, un, StringComparison.OrdinalIgnoreCase)) File.Copy(Application.ExecutablePath, un, true);

        progress(20, "Creating shortcuts...");
        Directory.CreateDirectory(Path.GetDirectoryName(StartLnk));
        MakeShortcut(StartLnk, exe, exe);
        if (desktop) MakeShortcut(DesktopLnk, exe, exe);

        using (var k = Registry.CurrentUser.CreateSubKey(UKey))
        {
            k.SetValue("DisplayName", "Pulsar");
            k.SetValue("DisplayVersion", "1.0");
            k.SetValue("Publisher", "Pulsar");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("DisplayIcon", exe);
            k.SetValue("UninstallString", "\"" + un + "\" /uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", 3000, RegistryValueKind.DWord);
        }

        if (tools)
        {
            Directory.CreateDirectory(Tools);
            await Fetch("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", Path.Combine(Tools, "yt-dlp.exe"), 25, 45, progress, "Downloading yt-dlp");
            var zip = Path.Combine(Path.GetTempPath(), "pulsar-ffmpeg.zip");
            await Fetch("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", zip, 45, 92, progress, "Downloading ffmpeg");
            progress(93, "Unpacking ffmpeg...");
            await Task.Run(() =>
            {
                using (var z = ZipFile.OpenRead(zip))
                    foreach (var e in z.Entries)
                    {
                        var n = e.FullName.Replace('\\', '/');
                        if (n.EndsWith("/bin/ffmpeg.exe") || n.EndsWith("/bin/ffprobe.exe")) e.ExtractToFile(Path.Combine(Tools, Path.GetFileName(n)), true);
                    }
                try { File.Delete(zip); } catch { }
            });
            if (!File.Exists(Path.Combine(Tools, "ffmpeg.exe"))) throw new Exception("ffmpeg could not be unpacked");
        }
        progress(100, "All done!");
    }

    static async Task Fetch(string url, string dest, int from, int to, Action<int, string> progress, string label)
    {
        using (var wc = new WebClient())
        {
            wc.Headers["User-Agent"] = "PulsarSetup";
            wc.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs e)
            {
                progress(from + (int)((to - from) * (e.ProgressPercentage / 100.0)), label + "... " + (e.BytesReceived / 1048576) + " MB" + (e.TotalBytesToReceive > 0 ? " of " + (e.TotalBytesToReceive / 1048576) + " MB" : ""));
            };
            await wc.DownloadFileTaskAsync(new Uri(url), dest);
        }
    }
}

class SetupForm : Form
{
    Label status = new Label();
    ProgressBar pb = new ProgressBar();
    Button go = new Button();
    CheckBox cbDesk = new CheckBox(), cbTools = new CheckBox(), cbRun = new CheckBox();
    bool done;

    public SetupForm()
    {
        var gray = Color.FromArgb(165, 165, 178);
        Text = "Pulsar Setup"; ClientSize = new Size(500, 410);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(16, 16, 20); ForeColor = Color.White; Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label { Text = "Pulsar", Font = new Font("Segoe UI", 26f, FontStyle.Bold), Left = 28, Top = 20, AutoSize = true });
        Controls.Add(new Label { Text = "Offline music player with beat-reactive visuals.\nThis sets everything up for you - no technical steps.", Left = 32, Top = 80, Width = 440, Height = 44, ForeColor = gray });
        cbDesk.Text = "Create a desktop shortcut"; cbDesk.Checked = true; cbDesk.Left = 34; cbDesk.Top = 140; cbDesk.AutoSize = true;
        cbTools.Text = "Install the music downloader (YouTube / YT Music)"; cbTools.Checked = true; cbTools.Left = 34; cbTools.Top = 172; cbTools.AutoSize = true;
        Controls.Add(new Label { Text = "Downloads about 130 MB (ffmpeg + yt-dlp) from their official sites.\nUntick it to save data - playing your own music works without it.", Left = 56, Top = 198, Width = 420, Height = 40, ForeColor = gray, Font = new Font("Segoe UI", 8.5f) });
        cbRun.Text = "Launch Pulsar when finished"; cbRun.Checked = true; cbRun.Left = 34; cbRun.Top = 252; cbRun.AutoSize = true;
        pb.Left = 34; pb.Top = 304; pb.Width = 432; pb.Height = 10;
        status.Left = 34; status.Top = 320; status.Width = 432; status.Height = 40; status.ForeColor = gray; status.Text = "Installs for your user only - no administrator rights needed.";
        go.Text = "Install"; go.Left = 336; go.Top = 362; go.Width = 130; go.Height = 36; go.FlatStyle = FlatStyle.Flat;
        go.BackColor = Color.FromArgb(255, 77, 109); go.ForeColor = Color.White; go.FlatAppearance.BorderSize = 0; go.Cursor = Cursors.Hand;
        Controls.AddRange(new Control[] { cbDesk, cbTools, cbRun, pb, status, go });
        go.Click += async delegate { await Run(); };
        Shown += async delegate
        {
            if (Setup.Silent) { Opacity = 0; ShowInTaskbar = false; cbDesk.Checked = !Setup.NoDesktop; cbTools.Checked = !Setup.NoDl; cbRun.Checked = !Setup.NoStart; await Run(); }
        };
    }

    async Task Run()
    {
        if (done)
        {
            if (cbRun.Checked) try { Process.Start(Path.Combine(Setup.Dir, "Pulsar.exe")); } catch { }
            Close(); return;
        }
        go.Enabled = false; cbDesk.Enabled = cbTools.Enabled = cbRun.Enabled = false;
        try
        {
            await Setup.Install(cbDesk.Checked, cbTools.Checked, delegate (int p, string t) { pb.Value = Math.Max(0, Math.Min(100, p)); status.Text = t; });
            done = true; go.Text = "Finish"; go.Enabled = true; cbRun.Enabled = true;
            status.Text = "Pulsar is installed. Find it in the Start menu" + (cbDesk.Checked ? " or on your desktop" : "") + ".";
            if (Setup.Silent) { if (cbRun.Checked) await Run(); else Close(); }
        }
        catch (Exception ex)
        {
            status.ForeColor = Color.FromArgb(255, 130, 130);
            status.Text = "Something went wrong: " + ex.Message + "\nCheck your internet connection and try again.";
            go.Text = "Retry"; go.Enabled = true; cbDesk.Enabled = cbTools.Enabled = true;
            if (Setup.Silent) { Environment.ExitCode = 1; Close(); }
        }
    }
}

