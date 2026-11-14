package org.sonicroute.android;

import android.content.Context;
import android.graphics.Bitmap;
import android.media.AudioDeviceCallback;
import android.media.AudioDeviceInfo;
import android.media.AudioManager;
import android.media.MediaMetadata;
import android.media.session.MediaController;
import android.media.session.MediaSessionManager;
import android.media.session.MediaSession;
import android.media.session.PlaybackState;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import org.json.JSONObject;
import org.json.JSONArray;
import android.util.Base64;
import java.io.ByteArrayOutputStream;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.util.ArrayList;
import java.util.List;

/** Visible-panel media subscription. Device callbacks, with a bounded-lifetime fallback if unavailable. */
public final class MediaSessionBridge {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final MediaSessionManager manager;
    private final AudioManager audioManager;
    private final List<MediaController> controllers = new ArrayList<>();
    private MediaController selected;
    private String last;
    private String lastOutputs = "";
    private String sentOutputs = "";
    private JSONArray outputSnapshot;
    private String sentArtwork, sentArtworkPackage;
    private boolean artworkDirty = true;
    private MediaSession.Token artworkToken;
    private String artwork = "";
    private final MediaController.Callback callback = new MediaController.Callback() {
        @Override public void onMetadataChanged(MediaMetadata metadata) { artworkDirty = true; emit(); }
        @Override public void onPlaybackStateChanged(PlaybackState state) { emit(); }
        @Override public void onSessionDestroyed() { update(manager.getActiveSessions(null)); }
    };
    private final MediaSessionManager.OnActiveSessionsChangedListener listener = this::update;
    private final AudioDeviceCallback deviceCallback = new AudioDeviceCallback() {
        @Override public void onAudioDevicesAdded(AudioDeviceInfo[] addedDevices) { refreshOutputs(); emit(); }
        @Override public void onAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) { refreshOutputs(); emit(); }
    };
    private final Runnable outputWatch = new Runnable() {
        @Override public void run() {
            try {
                if (refreshOutputs()) emit();
            } catch (Throwable ignored) { }
            // Only scheduled when callback registration fails, and removed on close.
            handler.postDelayed(this, 5000);
        }
    };

    private MediaSessionBridge(Context context) throws Exception {
        // app_process skips the zygote's media-module initialization on newer Android.
        // Initialize this helper process's registry only, and leave an existing one intact.
        AndroidWorkarounds.ensureMediaFrameworkInitialized();
        // ShellContext attributes calls to the shell UID, rather than system_server's package.
        manager = (MediaSessionManager) MediaSessionManager.class.getConstructor(Context.class).newInstance(context);
        audioManager = AudioOutputBridge.getAudioManager(context);
    }
    private void update(List<MediaController> active) {
        for (MediaController c : controllers) c.unregisterCallback(callback);
        controllers.clear();
        if (active != null) controllers.addAll(active);
        for (MediaController c : controllers) c.registerCallback(callback, handler);
        artworkDirty = true;
        emit();
    }
    private static String text(MediaMetadata metadata, String key) {
        String value = metadata == null ? null : metadata.getString(key);
        return value == null ? "" : value.substring(0, Math.min(value.length(), 256));
    }
    private boolean refreshOutputs() {
        JSONArray outputs = AudioOutputBridge.collectOutputs(audioManager);
        String text = outputs.toString();
        boolean changed = !text.equals(lastOutputs);
        outputSnapshot = outputs; lastOutputs = text;
        return changed;
    }
    private void emit() {
        try {
            selected = controllers.isEmpty() ? null : controllers.get(0);
            JSONArray playingPackages = new JSONArray();
            java.util.Set<String> playing = new java.util.LinkedHashSet<>();
            for (MediaController c : controllers) {
                PlaybackState state = c.getPlaybackState();
                if (state != null && state.getState() == PlaybackState.STATE_PLAYING) {
                    if (playing.isEmpty()) selected = c;
                    playing.add(c.getPackageName());
                }
            }
            for (String name : playing) playingPackages.put(name);
            JSONObject json = new JSONObject();
            json.put("playingPackages", playingPackages);
            if (outputSnapshot == null) refreshOutputs();
            String packageName = selected == null ? "" : selected.getPackageName();
            json.put("package", packageName);
            MediaMetadata metadata = selected == null ? null : selected.getMetadata();
            String title = text(metadata, MediaMetadata.METADATA_KEY_TITLE);
            if (title.isEmpty()) title = text(metadata, MediaMetadata.METADATA_KEY_DISPLAY_TITLE);
            json.put("title", title);
            json.put("artist", text(metadata, MediaMetadata.METADATA_KEY_ARTIST));
            MediaSession.Token current = selected == null ? null : selected.getSessionToken();
            if (artworkDirty || (current == null ? artworkToken != null : !current.equals(artworkToken))) {
                try { artwork = encodeArtwork(metadata); } catch (RuntimeException ignored) { artwork = ""; }
                artworkToken = current; artworkDirty = false;
            }
            PlaybackState state = selected == null ? null : selected.getPlaybackState();
            json.put("playing", state != null && state.getState() == PlaybackState.STATE_PLAYING);
            json.put("actions", state == null ? 0 : state.getActions());
            long duration = metadata == null ? 0 : Math.max(0, metadata.getLong(MediaMetadata.METADATA_KEY_DURATION));
            long position = state == null ? 0 : Math.max(0, state.getPosition());
            float speed = state == null ? 1f : state.getPlaybackSpeed();
            if (Float.isNaN(speed) || Float.isInfinite(speed) || speed <= 0 || speed > 8) speed = 1f;
            if (state != null && state.getState() == PlaybackState.STATE_PLAYING && state.getLastPositionUpdateTime() > 0)
                position += (long) (Math.max(0, SystemClock.elapsedRealtime() - state.getLastPositionUpdateTime()) * speed);
            json.put("position", duration > 0 ? Math.min(position, duration) : position);
            json.put("duration", duration);
            json.put("speed", speed);
            String stateLine = json.toString();
            boolean artworkChanged = !artwork.equals(sentArtwork) || !packageName.equals(sentArtworkPackage);
            boolean outputsChanged = !lastOutputs.equals(sentOutputs);
            if (!stateLine.equals(last) || artworkChanged || outputsChanged) {
                // Position/action events carry only metadata. Send PNG again on changes, including
                // an explicit empty string when the artwork is removed, and when the app changes.
                if (artworkChanged) json.put("artwork", artwork);
                if (outputsChanged) json.put("outputs", outputSnapshot);
                System.out.println(artworkChanged || outputsChanged ? json.toString() : stateLine); System.out.flush();
                last = stateLine; sentArtwork = artwork; sentArtworkPackage = packageName; sentOutputs = lastOutputs;
            }
        } catch (Throwable error) { fail(error); }
    }
    private static String encodeArtwork(MediaMetadata metadata) {
        if (metadata == null) return "";
        Bitmap bitmap = metadata.getBitmap(MediaMetadata.METADATA_KEY_ART);
        if (bitmap == null) bitmap = metadata.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART);
        if (bitmap == null) bitmap = metadata.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON);
        if (bitmap == null || bitmap.getWidth() <= 0 || bitmap.getHeight() <= 0) return "";
        float scale = Math.min(1f, 96f / Math.max(bitmap.getWidth(), bitmap.getHeight()));
        Bitmap thumbnail = Bitmap.createScaledBitmap(bitmap, Math.max(1, Math.round(bitmap.getWidth() * scale)),
            Math.max(1, Math.round(bitmap.getHeight() * scale)), true);
        try {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            if (!thumbnail.compress(Bitmap.CompressFormat.PNG, 100, bytes) || bytes.size() > 128 * 1024) return "";
            return Base64.encodeToString(bytes.toByteArray(), Base64.NO_WRAP);
        } finally { if (thumbnail != bitmap) thumbnail.recycle(); }
    }
    private void command(String command) {
        if (command.startsWith("ping ")) {
            String id = command.substring(5);
            if (id.matches("[0-9a-f]{32}")) {
                try {
                    // Reuse the existing visible Wi-Fi latency request as a recovery
                    // check for ROMs which occasionally miss a device callback.
                    if (refreshOutputs()) emit();
                    System.out.println(new JSONObject().put("pong", id).toString());
                    System.out.flush();
                }
                catch (Exception error) { fail(error); }
            }
            return;
        }
        if (selected == null) return;
        MediaController.TransportControls controls = selected.getTransportControls();
        if (command.startsWith("seek ")) {
            String[] fields = command.split(" ");
            if (fields.length == 3 && fields[1].equals(selected.getPackageName())) {
                PlaybackState state = selected.getPlaybackState();
                MediaMetadata metadata = selected.getMetadata();
                long duration = metadata == null ? 0 : metadata.getLong(MediaMetadata.METADATA_KEY_DURATION);
                if (state != null && (state.getActions() & PlaybackState.ACTION_SEEK_TO) != 0 && duration > 0)
                    controls.seekTo(Math.max(0, Math.min(Long.parseLong(fields[2]), duration)));
            }
            return;
        }
        switch (command) {
            case "previous": controls.skipToPrevious(); break;
            case "next": controls.skipToNext(); break;
            case "toggle":
                PlaybackState state = selected.getPlaybackState();
                if (state != null && state.getState() == PlaybackState.STATE_PLAYING) controls.pause();
                else controls.play();
                break;
        }
    }
    private void close() {
        try {
            handler.removeCallbacksAndMessages(null);
            if (audioManager != null) {
                try { audioManager.unregisterAudioDeviceCallback(deviceCallback); } catch (Throwable ignored) { }
            }
            manager.removeOnActiveSessionsChangedListener(listener);
            for (MediaController c : controllers) c.unregisterCallback(callback);
            controllers.clear();
        } finally { System.exit(0); } // Android's main Looper is deliberately not quittable.
    }
    private static void fail(Throwable error) {
        Throwable cause = error;
        while (cause.getCause() != null) cause = cause.getCause();
        error.printStackTrace(System.err);
        try { System.out.println(new JSONObject().put("error", cause.toString()).toString()); System.out.flush(); }
        catch (Exception ignored) { }
    }
    public static void main(String[] args) {
        try {
            Looper.prepareMainLooper();
            AndroidWorkarounds.apply();
            Context context = ShellContext.get();
            MediaSessionBridge bridge = new MediaSessionBridge(context);
            if (bridge.audioManager != null) {
                try { bridge.audioManager.registerAudioDeviceCallback(bridge.deviceCallback, bridge.handler); }
                catch (Throwable ignored) { bridge.handler.postDelayed(bridge.outputWatch, 5000); }
            }
            bridge.manager.addOnActiveSessionsChangedListener(bridge.listener, null, bridge.handler);
            bridge.update(bridge.manager.getActiveSessions(null));
            Thread input = new Thread(() -> {
                try {
                    BufferedReader reader = new BufferedReader(new InputStreamReader(System.in, "UTF-8"));
                    String command;
                    while ((command = reader.readLine()) != null) {
                        final String value = command;
                        if ("quit".equals(value)) break;
                        if ("previous".equals(value) || "toggle".equals(value) || "next".equals(value) || value.startsWith("seek ") || value.startsWith("ping "))
                            bridge.handler.post(() -> { try { bridge.command(value); } catch (Throwable e) { fail(e); } });
                    }
                } catch (Exception ignored) { }
                bridge.handler.post(bridge::close);
            }, "sonicroute-media-input");
            input.setDaemon(true); input.start();
            Looper.loop();
            System.exit(0);
        } catch (Throwable error) { fail(error); System.exit(1); }
    }
}
