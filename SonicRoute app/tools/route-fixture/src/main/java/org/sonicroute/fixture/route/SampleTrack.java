package org.sonicroute.fixture.route;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;

public final class SampleTrack extends Service {
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Runnable end = this::stopSelf;
    private AudioTrack track;

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationManager notices = getSystemService(NotificationManager.class);
        notices.createNotificationChannel(new NotificationChannel("route_sample", "Route sample", NotificationManager.IMPORTANCE_LOW));
        startForeground(1, new Notification.Builder(this, "route_sample").setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle("Silent route verification sample").build());
        if (track != null) return START_NOT_STICKY;
        try {
            short[] silence = new short[8000];
            track = new AudioTrack.Builder().setAudioAttributes(new AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build())
                .setAudioFormat(new AudioFormat.Builder().setSampleRate(8000).setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(silence.length * 2).setTransferMode(AudioTrack.MODE_STATIC).build();
            if (track.write(silence, 0, silence.length) != silence.length ||
                track.setLoopPoints(0, silence.length, -1) != AudioTrack.SUCCESS) throw new IllegalStateException();
            track.play();
            main.postDelayed(end, 90000);
        } catch (RuntimeException unavailable) { stopSelf(); }
        return START_NOT_STICKY;
    }
    @Override public void onDestroy() {
        main.removeCallbacks(end);
        if (track != null) { track.release(); track = null; }
        stopForeground(STOP_FOREGROUND_REMOVE);
        super.onDestroy();
    }
    @Override public IBinder onBind(Intent ignored) { return null; }
}
