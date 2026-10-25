package com.abidzakaria.pulsar;

import org.schabi.newpipe.extractor.InfoItem;
import org.schabi.newpipe.extractor.MediaFormat;
import org.schabi.newpipe.extractor.NewPipe;
import org.schabi.newpipe.extractor.ServiceList;
import org.schabi.newpipe.extractor.downloader.Downloader;
import org.schabi.newpipe.extractor.downloader.Request;
import org.schabi.newpipe.extractor.downloader.Response;
import org.schabi.newpipe.extractor.exceptions.ReCaptchaException;
import org.schabi.newpipe.extractor.search.SearchExtractor;
import org.schabi.newpipe.extractor.stream.AudioStream;
import org.schabi.newpipe.extractor.stream.StreamInfo;
import org.schabi.newpipe.extractor.stream.StreamInfoItem;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** YouTube lookups and audio fetching (no Android classes, so it can be tested on a plain JVM). */
public class Core {
    static final String UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";
    static boolean ready;

    static class Net extends Downloader {
        @Override
        public Response execute(Request req) throws IOException, ReCaptchaException {
            HttpURLConnection c = (HttpURLConnection) new URL(req.url()).openConnection();
            c.setRequestMethod(req.httpMethod());
            c.setConnectTimeout(20000);
            c.setReadTimeout(30000);
            c.setRequestProperty("User-Agent", UA);
            for (Map.Entry<String, List<String>> h : req.headers().entrySet())
                for (int i = 0; i < h.getValue().size(); i++) {
                    if (i == 0) c.setRequestProperty(h.getKey(), h.getValue().get(i));
                    else c.addRequestProperty(h.getKey(), h.getValue().get(i));
                }
            byte[] data = req.dataToSend();
            if (data != null) {
                c.setDoOutput(true);
                OutputStream o = c.getOutputStream();
                o.write(data);
                o.close();
            }
            int code = c.getResponseCode();
            if (code == 429) throw new ReCaptchaException("rate limited", req.url());
            InputStream in = code >= 400 ? c.getErrorStream() : c.getInputStream();
            String body = "";
            if (in != null) {
                ByteArrayOutputStream bo = new ByteArrayOutputStream();
                byte[] b = new byte[16384];
                int n;
                while ((n = in.read(b)) > 0) bo.write(b, 0, n);
                in.close();
                body = new String(bo.toByteArray(), StandardCharsets.UTF_8);
            }
            Map<String, List<String>> hdr = new HashMap<String, List<String>>();
            for (Map.Entry<String, List<String>> e : c.getHeaderFields().entrySet())
                if (e.getKey() != null) hdr.put(e.getKey(), e.getValue());
            return new Response(code, c.getResponseMessage(), hdr, body, c.getURL().toString());
        }
    }

    static synchronized void init() {
        if (!ready) { NewPipe.init(new Net()); ready = true; }
    }

    static String idOf(String url) {
        Matcher m = Pattern.compile("(?:v=|youtu\\.be/|/shorts/)([A-Za-z0-9_-]{11})").matcher(url);
        return m.find() ? m.group(1) : null;
    }

    static String watchUrl(String id) { return "https://www.youtube.com/watch?v=" + id; }

    /** Songs similar to {@code seed}: search for it, then take YouTube's related list of the best match. Each row is {id, title, by}. */
    static List<String[]> similar(String seed, int max) throws Exception {
        init();
        SearchExtractor se = ServiceList.YouTube.getSearchExtractor(seed + " audio");
        se.fetchPage();
        String first = null;
        for (InfoItem it : se.getInitialPage().getItems())
            if (it instanceof StreamInfoItem) { first = it.getUrl(); break; }
        List<String[]> out = new ArrayList<String[]>();
        if (first == null) return out;
        StreamInfo si = StreamInfo.getInfo(first);
        for (InfoItem it : si.getRelatedItems()) {
            if (!(it instanceof StreamInfoItem) || out.size() >= max) continue;
            String id = idOf(it.getUrl());
            if (id != null) out.add(new String[] { id, it.getName(), ((StreamInfoItem) it).getUploaderName() });
        }
        return out;
    }

    static class Audio {
        String url, title, by, ext, mime;
    }

    /** Resolves the audio stream used for both preview and full download (same pick every time, so a preview is a true prefix of the full file). */
    static Audio audio(String id) throws Exception {
        init();
        StreamInfo si = StreamInfo.getInfo(watchUrl(id));
        AudioStream best = null;
        for (AudioStream a : si.getAudioStreams()) {
            if (a.getFormat() != MediaFormat.M4A || a.getContent() == null) continue;
            if (best == null || better(a, best)) best = a;
        }
        if (best == null) throw new IOException("No playable audio stream found");
        Audio r = new Audio();
        r.url = best.getContent();
        r.title = si.getName();
        r.by = si.getUploaderName();
        r.ext = "m4a";
        r.mime = "audio/mp4";
        return r;
    }

    // prefer the stream closest to 128 kbps
    static boolean better(AudioStream a, AudioStream b) {
        int x = Math.abs(a.getAverageBitrate() - 128), y = Math.abs(b.getAverageBitrate() - 128);
        return x < y || (x == y && a.getAverageBitrate() > b.getAverageBitrate());
    }

    interface Progress { boolean on(long done, long total); } // return false to cancel

    /** Copies bytes [from, from+limit) (limit<0: to the end) of the stream into out, in ranged requests. Returns the total size of the file. */
    static long fetch(String url, long from, long limit, OutputStream out, Progress p) throws IOException {
        final int CH = 2 * 1024 * 1024;
        long pos = from, total = -1, end = limit < 0 ? Long.MAX_VALUE : from + limit;
        byte[] buf = new byte[32768];
        while (pos < end && (total < 0 || pos < total)) {
            long to = Math.min(pos + CH, end) - 1;
            HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(20000);
            c.setReadTimeout(30000);
            c.setRequestProperty("User-Agent", UA);
            c.setRequestProperty("Range", "bytes=" + pos + "-" + to);
            int code = c.getResponseCode();
            if (code != 206 && code != 200) throw new IOException("HTTP " + code);
            String cr = c.getHeaderField("Content-Range");
            if (cr != null && cr.contains("/")) total = Long.parseLong(cr.substring(cr.indexOf('/') + 1).trim());
            else if (code == 200) total = c.getContentLengthLong();
            InputStream in = c.getInputStream();
            int n;
            long got = 0;
            while ((n = in.read(buf)) > 0) {
                out.write(buf, 0, n);
                got += n;
                if (p != null && !p.on(pos + got, total)) { in.close(); throw new IOException("cancelled"); }
            }
            in.close();
            if (got == 0) break;
            pos += got;
        }
        return total;
    }

    static String safeName(String s) {
        return s.replaceAll("[\\\\/:*?\"<>|\\p{Cntrl}]", "").trim();
    }
}
