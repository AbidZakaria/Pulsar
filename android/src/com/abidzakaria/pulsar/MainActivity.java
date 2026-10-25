package com.abidzakaria.pulsar;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Hosts player.html (bundled in assets) in a WebView and serves songs from the phone's Music folder. */
public class MainActivity extends Activity {
    WebView wv;
    final Map<String, File> songs = new HashMap<String, File>();
    boolean loaded;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        wv = new WebView(this);
        setContentView(wv);
        WebSettings s = wv.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        wv.setWebChromeClient(new WebChromeClient());
        wv.addJavascriptInterface(new Object() {
            @JavascriptInterface
            public void postMessage(String m) {
                if ("pick".equals(m)) runOnUiThread(new Runnable() { public void run() { ensureAccess(); } });
            }
        }, "PulsarAndroid");
        wv.setWebViewClient(new WebViewClient() {
            @Override
            public WebResourceResponse shouldInterceptRequest(WebView v, WebResourceRequest r) {
                String host = r.getUrl().getHost();
                try {
                    if ("app.local".equals(host)) {
                        return resp("text/html", getAssets().open("player.html"), 200);
                    }
                    if ("music.local".equals(host)) {
                        String name = r.getUrl().getLastPathSegment();
                        File f = name == null ? null : songs.get(name);
                        if (f != null && f.exists()) return resp("application/octet-stream", new FileInputStream(f), 200);
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

    String perm() {
        return Build.VERSION.SDK_INT >= 33 ? Manifest.permission.READ_MEDIA_AUDIO : Manifest.permission.READ_EXTERNAL_STORAGE;
    }

    void ensureAccess() {
        if (Build.VERSION.SDK_INT >= 23 && checkSelfPermission(perm()) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] { perm() }, 1);
            return;
        }
        sendList();
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] p, int[] res) {
        if (res.length > 0 && res[0] == PackageManager.PERMISSION_GRANTED) sendList();
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

    void sendList() {
        if (!loaded) return;
        songs.clear();
        List<String> names = new ArrayList<String>();
        File music = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_MUSIC);
        scan(music, 0, names);
        Collections.sort(names);
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < names.size(); i++) {
            if (i > 0) sb.append(',');
            sb.append('"').append(names.get(i).replace("\\", "\\\\").replace("\"", "\\\"")).append('"');
        }
        sb.append(']');
        wv.evaluateJavascript("window.nativeInit&&window.nativeInit(" + sb + ",false)", null);
    }

    @Override
    public void onBackPressed() {
        moveTaskToBack(true);
    }
}
