package org.sonicroute.android;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioDeviceCallback;
import android.media.AudioDeviceInfo;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.lang.reflect.Constructor;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * Enumerates phone speaker and playable Bluetooth output sinks, and plays PC per-app PCM streams
 * routed to a specific phone output device via AudioTrack.setPreferredDevice.
 */
public final class AudioOutputBridge {
    private static final int SAMPLE_RATE = 48000;
    private static final int BYTES_PER_FRAME = 4; // 16-bit stereo

    static AudioManager getAudioManager(Context context) {
        try {
            if (context != null) {
                Object service = context.getSystemService(Context.AUDIO_SERVICE);
                if (service instanceof AudioManager) return (AudioManager) service;
            }
        } catch (Throwable ignored) { }
        try {
            Constructor<AudioManager> ctor = AudioManager.class.getDeclaredConstructor(Context.class);
            ctor.setAccessible(true);
            return ctor.newInstance(context);
        } catch (Throwable ignored) { }
        try {
            Constructor<AudioManager> ctor = AudioManager.class.getDeclaredConstructor();
            ctor.setAccessible(true);
            return ctor.newInstance();
        } catch (Throwable ignored) {
            return null;
        }
    }

    private static boolean isPrimaryBluetoothSinkType(int type) {
        return type == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP
            || type == AudioDeviceInfo.TYPE_BLE_HEADSET
            || type == AudioDeviceInfo.TYPE_BLE_SPEAKER
            || type == AudioDeviceInfo.TYPE_BLE_BROADCAST;
    }

    private static boolean isFallbackBluetoothSinkType(int type) {
        return type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO;
    }

    private static String safeAddress(AudioDeviceInfo device) {
        try {
            String address = device.getAddress();
            return address == null ? "" : address.trim();
        } catch (Throwable ignored) {
            return "";
        }
    }

    private static String safeProductName(AudioDeviceInfo device, String address) {
        String name = "";
        try {
            CharSequence product = device.getProductName();
            if (product != null) name = product.toString().trim();
        } catch (Throwable ignored) { }
        if (name.isEmpty() || name.equalsIgnoreCase(Build.MODEL)) {
            name = !address.isEmpty() && !"00:00:00:00:00:00".equals(address)
                ? "蓝牙 " + address : "蓝牙设备";
        }
        return name.length() > 128 ? name.substring(0, 128) : name;
    }

    private static String normalizeToken(String raw) {
        StringBuilder sb = new StringBuilder(raw.length());
        for (int i = 0; i < raw.length() && sb.length() < 96; i++) {
            char c = raw.charAt(i);
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '_' || c == '-' || c == '.' || c == ':' || c >= 0x4E00 && c <= 0x9FFF) {
                sb.append(c);
            } else {
                sb.append('_');
            }
        }
        String result = sb.toString();
        return result.isEmpty() ? "default" : result;
    }

    static String bluetoothSinkKey(AudioDeviceInfo device) {
        String address = safeAddress(device);
        String name = safeProductName(device, address);
        String token = (!address.isEmpty() && !"00:00:00:00:00:00".equals(address)) ? address : name;
        return "bt:" + normalizeToken(token);
    }

    static String bluetoothNameAliasKey(AudioDeviceInfo device) {
        String address = safeAddress(device);
        String name = safeProductName(device, address);
        return "bt:" + normalizeToken(name);
    }

    static JSONArray collectOutputs(AudioManager manager) {
        JSONArray array = new JSONArray();
        AudioDeviceInfo[] devices = new AudioDeviceInfo[0];
        if (manager != null) {
            try {
                AudioDeviceInfo[] list = manager.getDevices(AudioManager.GET_DEVICES_OUTPUTS);
                if (list != null) devices = list;
            } catch (Throwable ignored) { }
        }

        AudioDeviceInfo speaker = null;
        Map<String, AudioDeviceInfo> bluetoothSinks = new LinkedHashMap<>();
        Set<String> seenBluetoothNames = new HashSet<>();

        for (AudioDeviceInfo d : devices) {
            if (d == null || !d.isSink()) continue;
            int type = d.getType();
            if (type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER && speaker == null) {
                speaker = d;
            } else if (isPrimaryBluetoothSinkType(type)) {
                String key = bluetoothSinkKey(d);
                String nameKey = safeProductName(d, safeAddress(d)).toLowerCase(Locale.ROOT);
                if (!bluetoothSinks.containsKey(key) && seenBluetoothNames.add(nameKey)) {
                    bluetoothSinks.put(key, d);
                }
            }
        }
        // Only fall back to SCO when no primary A2DP/BLE sink exists and the product name was not already added.
        if (bluetoothSinks.isEmpty()) {
            for (AudioDeviceInfo d : devices) {
                if (d == null || !d.isSink()) continue;
                if (isFallbackBluetoothSinkType(d.getType())) {
                    String key = bluetoothSinkKey(d);
                    String nameKey = safeProductName(d, safeAddress(d)).toLowerCase(Locale.ROOT);
                    if (!bluetoothSinks.containsKey(key) && seenBluetoothNames.add(nameKey)) {
                        bluetoothSinks.put(key, d);
                    }
                }
            }
        }

        try {
            JSONObject speakerJson = new JSONObject();
            speakerJson.put("key", "speaker");
            speakerJson.put("kind", "speaker");
            speakerJson.put("id", speaker == null ? 0 : speaker.getId());
            speakerJson.put("type", AudioDeviceInfo.TYPE_BUILTIN_SPEAKER);
            speakerJson.put("name", "手机扬声器");
            speakerJson.put("address", "");
            array.put(speakerJson);

            for (Map.Entry<String, AudioDeviceInfo> entry : bluetoothSinks.entrySet()) {
                AudioDeviceInfo bt = entry.getValue();
                String address = safeAddress(bt);
                String name = safeProductName(bt, address);
                JSONObject btJson = new JSONObject();
                btJson.put("key", entry.getKey());
                btJson.put("kind", "bluetooth");
                btJson.put("id", bt.getId());
                btJson.put("type", bt.getType());
                btJson.put("name", name);
                btJson.put("address", address);
                array.put(btJson);
            }
        } catch (Throwable ignored) { }

        return array;
    }

    static AudioDeviceInfo findTargetDevice(AudioManager manager, String targetKey) {
        if (manager == null || targetKey == null || targetKey.isEmpty()) return null;
        AudioDeviceInfo[] devices;
        try {
            devices = manager.getDevices(AudioManager.GET_DEVICES_OUTPUTS);
            if (devices == null) return null;
        } catch (Throwable ignored) {
            return null;
        }
        if ("speaker".equalsIgnoreCase(targetKey)) {
            for (AudioDeviceInfo d : devices) {
                if (d != null && d.isSink() && d.getType() == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER) return d;
            }
            return null;
        }
        if (targetKey.startsWith("bt:")) {
            List<AudioDeviceInfo> primaryBt = new ArrayList<>();
            AudioDeviceInfo fallback = null;
            for (AudioDeviceInfo d : devices) {
                if (d == null || !d.isSink()) continue;
                int type = d.getType();
                if (isPrimaryBluetoothSinkType(type)) {
                    primaryBt.add(d);
                    if (bluetoothSinkKey(d).equalsIgnoreCase(targetKey)
                        || bluetoothNameAliasKey(d).equalsIgnoreCase(targetKey)) {
                        return d;
                    }
                } else if (isFallbackBluetoothSinkType(type) && fallback == null
                    && (bluetoothSinkKey(d).equalsIgnoreCase(targetKey)
                        || bluetoothNameAliasKey(d).equalsIgnoreCase(targetKey))) {
                    fallback = d;
                }
            }
            if (primaryBt.size() == 1) return primaryBt.get(0);
            if (fallback != null && primaryBt.isEmpty()) return fallback;
        }
        return null;
    }

    private static synchronized void emitJson(JSONObject json) {
        System.out.println(json.toString());
        System.out.flush();
    }

    private static void emitRouted(AudioTrack track, String targetKey, boolean preferredApplied) {
        try {
            AudioDeviceInfo routed = track.getRoutedDevice();
            JSONObject info = new JSONObject();
            info.put("target", targetKey);
            info.put("preferredApplied", preferredApplied);
            if (routed != null) {
                String address = safeAddress(routed);
                info.put("id", routed.getId());
                info.put("type", routed.getType());
                info.put("name", routed.getType() == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER
                    ? "手机扬声器" : safeProductName(routed, address));
                info.put("key", routed.getType() == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER
                    ? "speaker" : bluetoothSinkKey(routed));
            } else {
                info.put("id", 0);
                info.put("type", 0);
                info.put("name", "");
                info.put("key", "");
            }
            emitJson(new JSONObject().put("routed", info));
        } catch (Throwable ignored) { }
    }

    private static boolean applyPreferred(AudioTrack track, AudioManager manager, String targetKey) {
        AudioDeviceInfo target = findTargetDevice(manager, targetKey);
        if (target == null) return false;
        try {
            return track.setPreferredDevice(target);
        } catch (Throwable ignored) {
            return false;
        }
    }

    public static void main(String[] args) {
        try {
            Looper.prepareMainLooper();
            Class.forName("com.genymobile.scrcpy.Workarounds").getMethod("apply").invoke(null);
            Context context = (Context) Class.forName("com.genymobile.scrcpy.FakeContext").getMethod("get").invoke(null);
            try {
                Class<?> initializer = Class.forName("android.media.MediaFrameworkPlatformInitializer");
                if (initializer.getMethod("getMediaServiceManager").invoke(null) == null) {
                    Class<?> serviceManager = Class.forName("android.media.MediaServiceManager");
                    initializer.getMethod("setMediaServiceManager", serviceManager)
                        .invoke(null, serviceManager.getConstructor().newInstance());
                }
            } catch (ClassNotFoundException ignored) { }

            final AudioManager audioManager = getAudioManager(context);
            if (args.length == 0 || "list".equals(args[0])) {
                emitJson(new JSONObject().put("outputs", collectOutputs(audioManager)));
                System.exit(0);
                return;
            }

            if (!"play".equals(args[0]) || args.length < 3) {
                throw new IllegalArgumentException("Usage: AudioOutputBridge [list | play <socket> <targetKey>]");
            }

            final String socketName = args[1];
            if (!socketName.matches("[A-Za-z0-9_.-]{4,64}")) {
                throw new IllegalArgumentException("Invalid socket name");
            }
            final String[] targetHolder = new String[] { args[2] };
            final float[] volumeHolder = new float[] { 1.0f };
            final boolean[] mutedHolder = new boolean[] { false };

            HandlerThread callbackThread = new HandlerThread("sonicroute-out-looper");
            callbackThread.setDaemon(true);
            callbackThread.start();
            final Handler callbackHandler = new Handler(callbackThread.getLooper());

            try (LocalServerSocket serverSocket = new LocalServerSocket(socketName)) {
                JSONArray initialOutputs = collectOutputs(audioManager);
                final String[] lastOutputsHolder = new String[] { initialOutputs.toString() };
                JSONObject ready = new JSONObject();
                ready.put("listening", true);
                ready.put("outputs", initialOutputs);
                emitJson(ready);

                final LocalSocket client = serverSocket.accept();
                try { serverSocket.close(); } catch (Throwable ignored) { }

                int minBuffer = AudioTrack.getMinBufferSize(
                    SAMPLE_RATE, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT);
                int bufferBytes = Math.max(minBuffer > 0 ? minBuffer * 2 : 15360, BYTES_PER_FRAME * 960 * 4);

                final AudioTrack track = new AudioTrack.Builder()
                    .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                        .build())
                    .setAudioFormat(new AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(SAMPLE_RATE)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                        .build())
                    .setBufferSizeInBytes(bufferBytes)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .build();
                try { track.setVolume(1.0f); } catch (Throwable ignored) { }

                boolean initialPreferred = applyPreferred(track, audioManager, targetHolder[0]);
                if (targetHolder[0].startsWith("bt:") && !initialPreferred) {
                    try { track.release(); } catch (Throwable ignored) { }
                    try { client.close(); } catch (Throwable ignored) { }
                    emitJson(new JSONObject().put("error", "目标蓝牙设备未连接或不可播放。"));
                    System.exit(1);
                    return;
                }

                final Runnable checkOutputsAction = new Runnable() {
                    @Override public void run() {
                        try {
                            JSONArray currentOutputs = collectOutputs(audioManager);
                            String outputsText = currentOutputs.toString();
                            boolean changed;
                            synchronized (lastOutputsHolder) {
                                changed = !outputsText.equals(lastOutputsHolder[0]);
                                if (changed) lastOutputsHolder[0] = outputsText;
                            }
                            if (changed) {
                                emitJson(new JSONObject().put("outputs", currentOutputs));
                            }
                            String currentTarget;
                            synchronized (targetHolder) { currentTarget = targetHolder[0]; }
                            if (currentTarget.startsWith("bt:") && findTargetDevice(audioManager, currentTarget) == null) {
                                emitJson(new JSONObject().put("outputs", currentOutputs));
                                emitJson(new JSONObject().put("error", "蓝牙音频设备已断开连接。"));
                                try { client.close(); } catch (Throwable ignored) { }
                                return;
                            }
                            if (changed) {
                                boolean applied = applyPreferred(track, audioManager, currentTarget);
                                emitRouted(track, currentTarget, applied);
                            }
                        } catch (Throwable ignored) { }
                    }
                };

                final AudioDeviceCallback deviceCallback = new AudioDeviceCallback() {
                    @Override public void onAudioDevicesAdded(AudioDeviceInfo[] added) {
                        checkOutputsAction.run();
                    }
                    @Override public void onAudioDevicesRemoved(AudioDeviceInfo[] removed) {
                        checkOutputsAction.run();
                    }
                };

                boolean outputCallbackRegistered = false;
                if (audioManager != null) {
                    try {
                        audioManager.registerAudioDeviceCallback(deviceCallback, callbackHandler);
                        outputCallbackRegistered = true;
                    }
                    catch (Throwable ignored) { }
                }

                final Runnable periodicWatch = new Runnable() {
                    @Override public void run() {
                        checkOutputsAction.run();
                        callbackHandler.postDelayed(this, 5000);
                    }
                };
                // Normal playback uses device/routing notifications. Keep a recovery
                // scan only for systems which cannot register device callbacks.
                if (!outputCallbackRegistered) callbackHandler.postDelayed(periodicWatch, 5000);

                track.addOnRoutingChangedListener(router -> {
                    checkOutputsAction.run();
                    String currentTarget;
                    synchronized (targetHolder) { currentTarget = targetHolder[0]; }
                    emitRouted(track, currentTarget, true);
                }, callbackHandler);

                Thread controlThread = new Thread(() -> {
                    try {
                        BufferedReader reader = new BufferedReader(new InputStreamReader(System.in, "UTF-8"));
                        String line;
                        while ((line = reader.readLine()) != null) {
                            line = line.trim();
                            if ("quit".equals(line)) {
                                try { client.close(); } catch (Throwable ignored) { }
                                break;
                            } else if (line.startsWith("target ")) {
                                String nextTarget = line.substring(7).trim();
                                if (!nextTarget.isEmpty()) {
                                    synchronized (targetHolder) { targetHolder[0] = nextTarget; }
                                    boolean applied = applyPreferred(track, audioManager, nextTarget);
                                    if (nextTarget.startsWith("bt:") && !applied) {
                                        emitJson(new JSONObject().put("outputs", collectOutputs(audioManager)));
                                        emitJson(new JSONObject().put("error", "目标蓝牙设备未连接或不可播放。"));
                                        try { client.close(); } catch (Throwable ignored) { }
                                        break;
                                    }
                                    emitRouted(track, nextTarget, applied);
                                }
                            } else if (line.startsWith("volume ")) {
                                int pct = Integer.parseInt(line.substring(7).trim());
                                float gain = Math.max(0f, Math.min(1f, pct / 100f));
                                synchronized (volumeHolder) {
                                    volumeHolder[0] = gain;
                                    track.setVolume(mutedHolder[0] ? 0f : gain);
                                }
                            } else if (line.startsWith("mute ")) {
                                boolean mute = "1".equals(line.substring(5).trim()) || "true".equalsIgnoreCase(line.substring(5).trim());
                                synchronized (volumeHolder) {
                                    mutedHolder[0] = mute;
                                    track.setVolume(mute ? 0f : volumeHolder[0]);
                                }
                            }
                        }
                    } catch (Throwable ignored) { }
                }, "sonicroute-out-ctrl");
                controlThread.setDaemon(true);
                controlThread.start();

                try {
                    InputStream input = client.getInputStream();
                    byte[] pcm = new byte[BYTES_PER_FRAME * 960]; // 20ms frame chunks (3840 bytes)
                    int pending = 0;
                    boolean playing = false;
                    while (true) {
                        int read = input.read(pcm, pending, pcm.length - pending);
                        if (read < 0) break;
                        int total = pending + read;
                        int aligned = total - (total % BYTES_PER_FRAME);
                        if (aligned > 0) {
                            track.write(pcm, 0, aligned, AudioTrack.WRITE_BLOCKING);
                            if (!playing) {
                                track.play();
                                playing = true;
                                String currentTarget;
                                synchronized (targetHolder) { currentTarget = targetHolder[0]; }
                                emitRouted(track, currentTarget, initialPreferred);
                            }
                        }
                        pending = total - aligned;
                        if (pending > 0) {
                            System.arraycopy(pcm, aligned, pcm, 0, pending);
                        }
                    }
                } finally {
                    callbackHandler.removeCallbacksAndMessages(null);
                    if (audioManager != null) {
                        try { audioManager.unregisterAudioDeviceCallback(deviceCallback); } catch (Throwable ignored) { }
                    }
                    try { callbackThread.quitSafely(); } catch (Throwable ignored) { }
                    try { track.stop(); } catch (Throwable ignored) { }
                    try { track.release(); } catch (Throwable ignored) { }
                    try { client.close(); } catch (Throwable ignored) { }
                }
            }
            System.exit(0);
        } catch (Throwable error) {
            Throwable cause = error;
            while (cause.getCause() != null) cause = cause.getCause();
            error.printStackTrace(System.err);
            try { emitJson(new JSONObject().put("error", cause.toString())); } catch (Throwable ignored) { }
            System.exit(1);
        }
    }
}
