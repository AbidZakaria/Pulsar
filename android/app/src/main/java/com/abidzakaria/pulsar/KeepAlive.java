package com.abidzakaria.pulsar;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.IBinder;

/** Foreground service: keeps Pulsar running (and says so in a notification) while music plays or a download is going. */
public class KeepAlive extends Service {
    static final String CH = "pulsar";

    @Override
    public IBinder onBind(Intent i) { return null; }

    @Override
    public int onStartCommand(Intent in, int flags, int id) {
        if (in != null && "cancel".equals(in.getAction())) {
            MainActivity a = MainActivity.current;
            if (a != null) a.cancel = true;
            return START_NOT_STICKY;
        }
        String text = in == null ? "Pulsar" : in.getStringExtra("text");
        int pct = in == null ? -1 : in.getIntExtra("pct", -1);
        boolean dl = in != null && in.getBooleanExtra("dl", false);
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CH, "Pulsar", NotificationManager.IMPORTANCE_LOW));
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification.Builder b = new Notification.Builder(this, CH)
                .setSmallIcon(android.R.drawable.stat_sys_download_done)
                .setContentTitle("Pulsar")
                .setContentText(text)
                .setContentIntent(open)
                .setOnlyAlertOnce(true)
                .setOngoing(true);
        if (dl) {
            b.setSmallIcon(android.R.drawable.stat_sys_download).setProgress(100, Math.max(0, pct), pct <= 0);
            PendingIntent c = PendingIntent.getService(this, 1, new Intent(this, KeepAlive.class).setAction("cancel"), PendingIntent.FLAG_IMMUTABLE);
            b.addAction(new Notification.Action.Builder(null, "Cancel", c).build());
        } else {
            b.setSmallIcon(android.R.drawable.ic_media_play);
        }
        startForeground(1, b.build(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK | ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        return START_NOT_STICKY;
    }
}
