package com.abidzakaria.pulsar;

import android.Manifest;
import android.app.Activity;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Intent;
import android.os.Handler;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.provider.MediaStore;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.LinkedList;
import java.util.List;
import java.util.Map;

/** Hosts player.html (bundled in assets) in a WebView, serves songs from the Music folder, and does Explore / previews / downloads. */
public class MainActivity extends Activity {
    static final int PREVIEW_BYTES = 560 * 1024; // roughly 35 s of AAC

    WebView wv;
    final Map<String, File> songs = new HashMap<String, File>();
    boolean loaded;
    volatile boolean exploring, cancel, downloading;
    final LinkedList<String> pvQueue = new LinkedList<String>();
    boolean pvRunning;
    boolean playing;
    String dlText = "";
    int dlPct = -1;
    static volatile MainActivity current;
    final Handler ui = new Handler();
    final Runnable stopSoon = new Runnable() { public void run() { bgUpdate(); } };

    /** Keeps the process alive (foreground service + notification) while music plays or a download runs. */
    void bgUpdate() {
        Intent i = new Intent(this, KeepAlive.class);
        if (!downloading && !playing) { stopService(i); return; }
        String text = downloading ? dlText : "Playing";
        i.putExtra("text", text).putExtra("pct", downloading ? dlPct : -1).putExtra("dl", downloading);
        try { startForegroundService(i); } catch (Exception e) { }
    }

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        current = this;
        wv = new WebView(this);
        setContentView(wv);
        WebSettings s = wv.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        wv.setWebChromeClient(new WebChromeClient());
        wv.addJavascriptInterface(new Object() {
            @JavascriptInterface
            public void postMessage(final String m) {
                if ("pick".equals(m)) runOnUiThread(new Runnable() { public void run() { ensureAccess(); } });
                else if ("dlcancel".equals(m)) cancel = true;
                else if (m.startsWith("ex:")) explore(m.substring(3));
                else if (m.startsWith("pv:")) queuePreview(m.substring(3));
                else if (m.startsWith("dl:")) download(m.substring(3));
                else if (m.startsWith("bg:")) runOnUiThread(new Runnable() { public void run() {
                    playing = m.endsWith("1");
                    ui.removeCallbacks(stopSoon);
                    if (playing) bgUpdate(); else ui.postDelayed(stopSoon, 4000);   // track changes pause for a moment
                } });
            }
        }, "PulsarAndroid");
        wv.setWebViewClient(new WebViewClient() {
            @Override
            public WebResourceResponse shouldInterceptRequest(WebView v, WebResourceRequest r) {
                String host = r.getUrl().getHost();
                try {
                    if ("app.local".equals(host)) return resp("text/html", getAssets().open("player.html"), 200);
                    String name = r.getUrl().getLastPathSegment();
                    if ("music.local".equals(host)) {
                        File f = name == null ? null : songs.get(name);
                        if (f != null && f.exists()) return resp("application/octet-stream", new FileInputStream(f), 200);
                        return resp("text/plain", null, 404);
                    }
                    if ("preview.local".equals(host)) {
                        File f = name == null ? null : new File(previewDir(), name.replaceAll("[^A-Za-z0-9_.-]", ""));
                        if (f != null && f.exists()) return resp("audio/mp4", new FileInputStream(f), 200);
                        return resp("text/plain", null, 404);
                    }
                } catch (Exception e) { }
                return null;
            }

            @Override
            public void onPageFinished(WebView v, String url) {
                loaded = true;
                ensureAccess();
            }
        });
        wv.loadUrl("https://app.local/player.html");
    }

    WebResourceResponse resp(String type, InputStream in, int code) {
        WebResourceResponse r = new WebResourceResponse(type, null, code, code == 200 ? "OK" : "Not Found", null, in);
        Map<String, String> h = new HashMap<String, String>();
        h.put("Access-Control-Allow-Origin", "*");
        r.setResponseHeaders(h);
        return r;
    }

    File previewDir() {
        File d = new File(getCacheDir(), "preview");
        d.mkdirs();
        return d;
    }

    // ---------------- music access and library ----------------
    String perm() {
        return Build.VERSION.SDK_INT >= 33 ? Manifest.permission.READ_MEDIA_AUDIO : Manifest.permission.READ_EXTERNAL_STORAGE;
    }

    void ensureAccess() {
        if (checkSelfPermission(perm()) != PackageManager.PERMISSION_GRANTED) {
            // the notification permission (Android 13+) only decides whether the status notification is visible
            requestPermissions(Build.VERSION.SDK_INT >= 33 ? new String[] { perm(), Manifest.permission.POST_NOTIFICATIONS } : new String[] { perm() }, 1);
            return;
        }
        sendList(false);
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] p, int[] res) {
        // even if refused the app still opens (on the Explore page); songs just won't be listed
        sendList(false);
    }

    void scan(File dir, int depth, List<String> out) {
        File[] fs = dir.listFiles();
        if (fs == null) return;
        for (File f : fs) {
            if (f.isDirectory()) { if (depth < 3) scan(f, depth + 1, out); continue; }
            String n = f.getName(), l = n.toLowerCase();
            if (!(l.endsWith(".mp3") || l.endsWith(".m4a") || l.endsWith(".opus") || l.endsWith(".ogg") || l.endsWith(".flac") || l.endsWith(".wav"))) continue;
            if (songs.containsKey(n)) continue;
            songs.put(n, f);
            out.add(n);
        }
    }

    void sendList(final boolean keep) {
        if (!loaded) return;
        songs.clear();
        List<String> names = new ArrayList<String>();
        if (checkSelfPermission(perm()) == PackageManager.PERMISSION_GRANTED)
            scan(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC), 0, names);
        Collections.sort(names);
        JSONArray a = new JSONArray();
        for (String n : names) a.put(n);
        js("window.nativeInit&&window.nativeInit(" + a + "," + keep + ")");
    }

    void js(final String script) {
        runOnUiThread(new Runnable() { public void run() { wv.evaluateJavascript(script, null); } });
    }

    void ev(String fn, String type, String msg, int pct) {
        js("window." + fn + "&&window." + fn + "({t:" + JSONObject.quote(type) + ",m:" + JSONObject.quote(msg) + ",p:" + pct + "})");
    }

    static String why(Exception e) {
        String m = e.getMessage();
        return m == null || m.length() > 120 ? e.getClass().getSimpleName() : m;
    }

    // ---------------- explore ----------------
    void explore(final String seedArg) {
        if (exploring) return;
        final List<String> seeds = new ArrayList<String>();
        for (String s : seedArg.split("\\|")) {
            s = s.trim();
            if (s.length() > 0 && seeds.size() < 3) seeds.add(s);
        }
        if (seeds.isEmpty()) { ev("exEvent", "err", "Tell Pulsar what you like first", 0); return; }
        exploring = true;
        new Thread(new Runnable() {
            public void run() {
                try {
                    JSONArray out = new JSONArray();
                    List<String> seen = new ArrayList<String>();
                    int n = 0;
                    for (String seed : seeds) {
                        n++;
                        ev("exEvent", "prog", "Finding songs like " + seed + " (" + n + "/" + seeds.size() + ")...", 0);
                        try {
                            for (String[] r : Core.similar(seed, 14)) {
                                if (seen.contains(r[0])) continue;
                                seen.add(r[0]);
                                out.put(new JSONObject().put("id", r[0]).put("title", r[1]).put("by", r[2]).put("seed", seed));
                            }
                        } catch (Exception e) {
                            if (n == seeds.size() && out.length() == 0) throw e;
                        }
                    }
                    js("window.exResult&&window.exResult(" + out + ")");
                } catch (Exception e) {
                    ev("exEvent", "err", "Explore failed: " + why(e), 0);
                } finally {
                    exploring = false;
                }
            }
        }).start();
    }

    // ---------------- previews: only the first ~35 s of each suggestion, one at a time ----------------
    void queuePreview(String arg) {
        boolean now = arg.startsWith("!");
        String id = arg.replace("!", "");
        if (!id.matches("[A-Za-z0-9_-]{11}")) return;
        if (new File(previewDir(), id + ".m4a").exists()) { js("window.pvEvent&&window.pvEvent({id:'" + id + "',t:'ready'})"); return; }
        synchronized (pvQueue) {
            pvQueue.remove(id);
            if (now) pvQueue.addFirst(id); else pvQueue.addLast(id);
            if (pvRunning) return;
            pvRunning = true;
        }
        new Thread(new Runnable() { public void run() { previewWorker(); } }).start();
    }

    void previewWorker() {
        while (true) {
            String id;
            synchronized (pvQueue) {
                if (pvQueue.isEmpty()) { pvRunning = false; return; }
                id = pvQueue.removeFirst();
            }
            js("window.pvEvent&&window.pvEvent({id:'" + id + "',t:'load'})");
            File f = new File(previewDir(), id + ".m4a"), tmp = new File(previewDir(), id + ".part");
            boolean ok = false;
            try {
                Core.Audio au = Core.audio(id);
                FileOutputStream o = new FileOutputStream(tmp);
                try { Core.fetch(au.url, 0, PREVIEW_BYTES, o, null); } finally { o.close(); }
                ok = tmp.length() > 0 && tmp.renameTo(f);
            } catch (Exception e) { }
            js("window.pvEvent&&window.pvEvent({id:'" + id + "',t:'" + (ok ? "ready" : "err") + "'})");
        }
    }

    // ---------------- download: continues from the preview file when there is one ----------------
    void download(final String link) {
        if (downloading) { ev("dlEvent", "err", "A download is already running", 0); return; }
        final String id = Core.idOf(link);
        if (id == null) { ev("dlEvent", "err", "Paste a YouTube or YouTube Music song link", 0); return; }
        downloading = true;
        cancel = false;
        dlText = "Starting download...";
        dlPct = 0;
        bgUpdate();
        new Thread(new Runnable() {
            public void run() {
                Uri uri = null;
                ContentResolver cr = getContentResolver();
                try {
                    ev("dlEvent", "info", "Starting...", 0);
                    Core.Audio au = Core.audio(id);
                    String name = Core.safeName(au.by.replace(" - Topic", "") + " - " + au.title) + "." + au.ext;
                    for (String have : songs.keySet())
                        if (have.equalsIgnoreCase(name)) { ev("dlEvent", "done", "Already in your library", 100); return; }

                    ContentValues v = new ContentValues();
                    v.put(MediaStore.Audio.Media.DISPLAY_NAME, name);
                    v.put(MediaStore.Audio.Media.MIME_TYPE, au.mime);
                    v.put(MediaStore.Audio.Media.RELATIVE_PATH, "Music/Pulsar");
                    v.put(MediaStore.Audio.Media.IS_PENDING, 1);
                    uri = cr.insert(MediaStore.Audio.Media.EXTERNAL_CONTENT_URI, v);
                    OutputStream out = cr.openOutputStream(uri);
                    long have = 0;
                    File pv = new File(previewDir(), id + ".m4a");
                    try {
                        if (pv.exists()) { // the preview is the start of the same file: reuse it and fetch only the rest
                            InputStream in = new FileInputStream(pv);
                            byte[] buf = new byte[32768];
                            int n;
                            while ((n = in.read(buf)) > 0) { out.write(buf, 0, n); have += n; }
                            in.close();
                        }
                        final String shown = name.replaceFirst("\\.[^.]+$", "");
                        Core.fetch(au.url, have, -1, out, new Core.Progress() {
                            public boolean on(long done, long total) {
                                if (cancel) return false;
                                int pct = total > 0 ? (int) Math.min(99, done * 100 / total) : 0;
                                if (pct != dlPct) {
                                    dlPct = pct;
                                    dlText = "Downloading - " + shown;
                                    ev("dlEvent", "prog", dlText, pct);
                                    bgUpdate();
                                }
                                return true;
                            }
                        });
                    } finally {
                        out.close();
                    }
                    v.clear();
                    v.put(MediaStore.Audio.Media.IS_PENDING, 0);
                    cr.update(uri, v, null, null);
                    uri = null;
                    runOnUiThread(new Runnable() { public void run() { sendList(true); } });
                    ev("dlEvent", "done", "Done - added " + name.replaceFirst("\\.[^.]+$", ""), 100);
                } catch (Exception e) {
                    if (uri != null) try { cr.delete(uri, null, null); } catch (Exception x) { }
                    ev("dlEvent", "err", cancel ? "Download cancelled" : "Download failed: " + why(e), 0);
                } finally {
                    downloading = false;
                    runOnUiThread(new Runnable() { public void run() { bgUpdate(); } });
                }
            }
        }).start();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        if (!downloading && !playing) stopService(new Intent(this, KeepAlive.class));
    }

    @Override
    public void onBackPressed() {
        moveTaskToBack(true);
    }
}
