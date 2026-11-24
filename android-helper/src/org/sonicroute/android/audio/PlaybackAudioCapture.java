// SPDX-License-Identifier: GPL-3.0-only
package org.sonicroute.android.audio;

import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioRecordingConfiguration;
import android.media.AudioTimestamp;
import android.media.MediaCodec;
import android.media.MediaRecorder;
import android.os.Build;
import java.io.IOException;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.sonicroute.android.AndroidWorkarounds;
import org.sonicroute.android.ShellContext;
import org.sonicroute.android.core.ServerOptions.AudioSourceMode;
import org.sonicroute.android.platform.AndroidSystemPlatform;
import org.sonicroute.android.util.ServerLog;

/**
 * Self-authored {@code AudioPlaybackCapture} (via hidden {@code AudioPolicy}) plus direct mic fallback.
 * Supports UID/package isolation, {@code audio_dup}, and {@code audio_mix_mode=uid|pr}.
 */
public final class PlaybackAudioCapture implements AudioCaptureSource {
    public static final int SAMPLE_RATE = 48_000;
    public static final int CHANNELS = 2;
    public static final int BYTES_PER_SAMPLE = 2;
    public static final int CHANNEL_MASK = AudioFormat.CHANNEL_IN_STEREO;
    public static final int ENCODING = AudioFormat.ENCODING_PCM_16BIT;
    public static final int MAX_READ_SIZE = 4096;
    private static final long ONE_SAMPLE_US = (1_000_000L + SAMPLE_RATE - 1) / SAMPLE_RATE;

    private final AudioSourceMode sourceMode;
    private final boolean keepPlayingOnDevice;
    private final boolean prMixMode;
    private boolean strictNoGlobal;
    private volatile int[] targetUids = new int[0];

    private Runnable unregisterPolicy;
    private volatile AudioRecord recorder;
    private final AudioTimestamp timestamp = new AudioTimestamp();
    private long lastTimestampNs = -1;
    private long previousPtsUs;
    private long nextPtsUs;

    public PlaybackAudioCapture(AudioSourceMode sourceMode, boolean keepPlayingOnDevice, boolean prMixMode) {
        this.sourceMode = sourceMode;
        this.keepPlayingOnDevice = keepPlayingOnDevice;
        this.prMixMode = prMixMode;
    }

    public void setStrictNoGlobal(boolean strict) {
        this.strictNoGlobal = strict;
    }

    public void setTargetSpecs(List<String> specs) {
        if (specs == null || specs.isEmpty()) {
            this.targetUids = new int[0];
            return;
        }
        List<Integer> resolved = new ArrayList<>(specs.size());
        for (String spec : specs) {
            if (spec == null || spec.isEmpty()) {
                continue;
            }
            int uid = resolveUid(spec);
            if (uid > 0 && uid != Integer.MAX_VALUE) {
                resolved.add(uid);
            } else {
                ServerLog.w("Target audio package not found or no UID: " + spec);
            }
        }
        int[] uids = new int[resolved.size()];
        for (int i = 0; i < resolved.size(); ++i) {
            uids[i] = resolved.get(i);
        }
        this.targetUids = uids;
    }

    private static int resolveUid(String spec) {
        try {
            return Integer.parseInt(spec);
        } catch (NumberFormatException ignored) {
            return AndroidSystemPlatform.resolvePackageUid(spec);
        }
    }

    public static AudioFormat createPcmFormat() {
        return new AudioFormat.Builder()
                .setEncoding(ENCODING)
                .setSampleRate(SAMPLE_RATE)
                .setChannelMask(CHANNEL_MASK)
                .build();
    }

    @Override
    public void checkSupported() throws IOException {
        if (sourceMode.isPlayback() && Build.VERSION.SDK_INT < 30) {
            throw new IOException("Audio playback capture requires Android 11+");
        }
    }

    @Override
    public synchronized void start() throws IOException {
        stop();
        lastTimestampNs = -1;
        previousPtsUs = 0;
        nextPtsUs = 0;

        String stage = sourceMode.isPlayback() ? "AudioPolicy setup" : "AudioRecord creation";
        boolean started = false;
        try {
            recorder = sourceMode.isPlayback() ? createPlaybackPolicyRecorder() : createDirectRecorder();
            if (recorder == null || recorder.getState() != AudioRecord.STATE_INITIALIZED) {
                throw new IOException("AudioRecord was not initialized");
            }
            stage = "AudioRecord.startRecording";
            recorder.startRecording();
            if (recorder.getRecordingState() != AudioRecord.RECORDSTATE_RECORDING) {
                throw new IOException("AudioRecord did not enter the recording state");
            }
            if (sourceMode.isPlayback() && isCaptureSilenced(recorder)) {
                throw new IOException("Android silenced the recording client; check ROM capture restrictions and device lock state");
            }
            started = true;
        } catch (Exception | LinkageError error) {
            if (error instanceof IOException) {
                throw (IOException) error;
            }
            throw captureFailure(stage, error);
        } finally {
            if (!started) {
                stop();
            }
        }
    }

    private static boolean isCaptureSilenced(AudioRecord record) {
        try {
            AudioRecordingConfiguration config = record.getActiveRecordingConfiguration();
            return config != null && config.isClientSilenced();
        } catch (RuntimeException | LinkageError error) {
            ServerLog.d("Could not query initial AudioRecord silencing status: " + error);
            return false;
        }
    }

    @Override
    public synchronized void stop() {
        if (recorder != null) {
            AudioRecord record = recorder;
            recorder = null;
            AndroidWorkarounds.releaseAudioRecord(record);
        }
        if (unregisterPolicy != null) {
            Runnable unregister = unregisterPolicy;
            unregisterPolicy = null;
            unregister.run();
        }
    }

    @Override
    public int read(ByteBuffer outDirectBuffer, MediaCodec.BufferInfo outBufferInfo) throws IOException {
        AudioRecord rec = recorder;
        if (rec == null) {
            return -1;
        }
        int maxBytes = Math.min(outDirectBuffer.remaining(), MAX_READ_SIZE) & ~3;
        if (maxBytes <= 0) {
            return 0;
        }
        int readBytes = rec.read(outDirectBuffer, maxBytes);
        if (readBytes <= 0) {
            if (readBytes < 0 && recorder == rec) {
                throw new IOException("AudioRecord.read failed with status " + readBytes);
            }
            return readBytes;
        }

        long ptsUs;
        int ret = rec.getTimestamp(timestamp, AudioTimestamp.TIMEBASE_MONOTONIC);
        if (ret == AudioRecord.SUCCESS && timestamp.nanoTime != lastTimestampNs) {
            ptsUs = timestamp.nanoTime / 1000L;
            lastTimestampNs = timestamp.nanoTime;
        } else {
            if (nextPtsUs == 0) {
                nextPtsUs = System.nanoTime() / 1000L;
            }
            ptsUs = nextPtsUs;
        }

        long durationUs = readBytes * 1_000_000L / ((long) CHANNELS * BYTES_PER_SAMPLE * SAMPLE_RATE);
        nextPtsUs = ptsUs + durationUs;

        if (previousPtsUs != 0 && ptsUs < previousPtsUs + ONE_SAMPLE_US) {
            ptsUs = previousPtsUs + ONE_SAMPLE_US;
        }
        previousPtsUs = ptsUs;
        outBufferInfo.set(0, readBytes, ptsUs, 0);
        return readBytes;
    }

    private AudioRecord createPlaybackPolicyRecorder() throws Exception {
        int[] effectiveUids = targetUids;
        if (effectiveUids.length == 0 && strictNoGlobal) {
            throw new IOException("No valid target UID for isolated audio capture");
        }
        Class<?> ruleClass = Class.forName("android.media.audiopolicy.AudioMixingRule");
        Class<?> ruleBuilderClass = Class.forName("android.media.audiopolicy.AudioMixingRule$Builder");
        Object ruleBuilder = ruleBuilderClass.getConstructor().newInstance();

        // Android 11/12 infer the player role from UID and usage rules.
        Method setRole = findPolicyMethod(ruleBuilderClass, "setTargetMixRole", int.class);
        if (setRole != null) {
            setRole.invoke(ruleBuilder, ruleClass.getField("MIX_ROLE_PLAYERS").getInt(null));
        }

        int ruleMatchUsage = ruleClass.getField("RULE_MATCH_ATTRIBUTE_USAGE").getInt(null);
        int ruleMatchUid = readOptionalIntField(ruleClass, "RULE_MATCH_UID", 4);
        Method addMixRule = ruleBuilderClass.getMethod("addMixRule", int.class, Object.class);

        if (effectiveUids.length > 0) {
            for (int uid : effectiveUids) {
                addMixRule.invoke(ruleBuilder, ruleMatchUid, Integer.valueOf(uid));
            }
            if (prMixMode) {
                for (int usage = 0; usage <= 16; ++usage) {
                    addUsageRule(addMixRule, ruleBuilder, ruleMatchUsage, usage);
                }
                ServerLog.w("Audio capture PR-compat mix rules active for UIDs " + Arrays.toString(effectiveUids));
            } else {
                ServerLog.i("Audio capture UID-isolated for UIDs " + Arrays.toString(effectiveUids)
                        + " (dup=" + keepPlayingOnDevice + ")");
            }
        } else {
            addUsageRule(addMixRule, ruleBuilder, ruleMatchUsage, AudioAttributes.USAGE_MEDIA);
            addUsageRule(addMixRule, ruleBuilder, ruleMatchUsage, AudioAttributes.USAGE_GAME);
            addUsageRule(addMixRule, ruleBuilder, ruleMatchUsage, AudioAttributes.USAGE_UNKNOWN);
            ServerLog.i("Audio capture global playback mode (dup=" + keepPlayingOnDevice + ")");
        }

        // Must be configured before build() so calls/voice streams stay eligible when permitted.
        try {
            ruleBuilderClass.getMethod("voiceCommunicationCaptureAllowed", boolean.class).invoke(ruleBuilder, true);
        } catch (NoSuchMethodException ignored) {
        }

        Object mixingRule = ruleBuilderClass.getMethod("build").invoke(ruleBuilder);

        Class<?> mixClass = Class.forName("android.media.audiopolicy.AudioMix");
        Class<?> mixBuilderClass = Class.forName("android.media.audiopolicy.AudioMix$Builder");
        Object mixBuilder = mixBuilderClass.getConstructor(ruleClass).newInstance(mixingRule);
        mixBuilderClass.getMethod("setFormat", AudioFormat.class).invoke(mixBuilder, createPcmFormat());

        int routeFlag = mixClass.getField("ROUTE_FLAG_LOOP_BACK").getInt(null);
        if (keepPlayingOnDevice) {
            routeFlag |= mixClass.getField("ROUTE_FLAG_RENDER").getInt(null);
        }
        mixBuilderClass.getMethod("setRouteFlags", int.class).invoke(mixBuilder, routeFlag);
        Object audioMix = mixBuilderClass.getMethod("build").invoke(mixBuilder);

        Class<?> policyClass = Class.forName("android.media.audiopolicy.AudioPolicy");
        Class<?> policyBuilderClass = Class.forName("android.media.audiopolicy.AudioPolicy$Builder");
        Object policyBuilder = policyBuilderClass.getConstructor(Context.class).newInstance(ShellContext.get());
        policyBuilderClass.getMethod("addMix", mixClass).invoke(policyBuilder, audioMix);
        Object policy = policyBuilderClass.getMethod("build").invoke(policyBuilder);

        registerPolicy(policyClass, policy);
        return createPolicySink(policyClass, policy, mixClass, audioMix);
    }

    private void registerPolicy(Class<?> policyClass, Object policy) throws IOException {
        try {
            Method register = findPolicyMethod(AudioManager.class, "registerAudioPolicyStatic", policyClass);
            if (register == null) {
                register = AudioManager.class.getMethod("registerAudioPolicy", policyClass);
            }
            Method unregister = findPolicyMethod(AudioManager.class, "unregisterAudioPolicyAsyncStatic", policyClass);
            if (unregister == null) {
                unregister = findPolicyMethod(AudioManager.class, "unregisterAudioPolicyAsync", policyClass);
            }
            if (unregister == null) {
                unregister = AudioManager.class.getMethod("unregisterAudioPolicy", policyClass);
            }
            final Method cleanup = unregister;
            final Object cleanupReceiver = policyMethodReceiver(cleanup);
            Object registerReceiver = policyMethodReceiver(register);
            // Set cleanup first in case the service registers the policy before invocation fails.
            unregisterPolicy = () -> {
                try {
                    cleanup.invoke(cleanupReceiver, policy);
                } catch (ReflectiveOperationException | RuntimeException | LinkageError error) {
                    ServerLog.w("AudioPolicy unregistration failed", captureFailure(cleanup.getName(), error));
                }
            };
            int status = (int) register.invoke(registerReceiver, policy);
            if (status != 0) {
                unregisterPolicy = null; // Registration was rejected.
                if (ShellContext.get().checkSelfPermission("android.permission.MODIFY_AUDIO_ROUTING")
                        != PackageManager.PERMISSION_GRANTED) {
                    throw new IOException("AudioPolicy registration rejected by system (status " + status
                            + "); this ROM does not grant MODIFY_AUDIO_ROUTING to the shell");
                }
                throw new IOException("AudioPolicy registration failed with status " + status);
            }
        } catch (ReflectiveOperationException | RuntimeException | LinkageError error) {
            throw captureFailure("AudioPolicy registration", error);
        }
    }

    private static Object policyMethodReceiver(Method method) {
        if (Modifier.isStatic(method.getModifiers())) {
            return null;
        }
        Object manager = ShellContext.get().getSystemService(Context.AUDIO_SERVICE);
        if (manager == null) {
            throw new IllegalStateException("AudioManager service unavailable");
        }
        return manager;
    }

    private static AudioRecord createPolicySink(Class<?> policyClass, Object policy, Class<?> mixClass,
            Object audioMix) throws IOException {
        Throwable factoryError;
        try {
            AudioRecord record = (AudioRecord) policyClass.getMethod("createAudioRecordSink", mixClass)
                    .invoke(policy, audioMix);
            if (record == null) {
                // AOSP returns null if policyReadyToUse() fails.
                throw new IOException("AudioPolicy could not provide a recording sink");
            }
            if (record.getState() == AudioRecord.STATE_INITIALIZED) {
                return record;
            }
            AndroidWorkarounds.releaseAudioRecord(record);
            factoryError = new IllegalStateException("AudioPolicy returned an uninitialized recording sink");
        } catch (InvocationTargetException error) {
            factoryError = error.getCause();
            if (!(factoryError instanceof NullPointerException) && !(factoryError instanceof LinkageError)) {
                throw captureFailure("AudioPolicy.createAudioRecordSink", factoryError);
            }
        } catch (ReflectiveOperationException | RuntimeException | LinkageError error) {
            throw captureFailure("AudioPolicy.createAudioRecordSink", error);
        }

        try {
            String address = (String) mixClass.getMethod("getRegistration").invoke(audioMix);
            if (address == null || address.isEmpty()) {
                throw new IOException("AudioPolicy has no registered loopback address");
            }
            int minSize = AudioRecord.getMinBufferSize(SAMPLE_RATE, CHANNEL_MASK, ENCODING);
            if (minSize <= 0) {
                throw new IOException("Android rejected the existing stereo PCM format: " + minSize);
            }
            ServerLog.w("AudioPolicy sink initialization failed; retrying its registered mix: " + factoryError);
            return AndroidWorkarounds.createAudioRecord(MediaRecorder.AudioSource.REMOTE_SUBMIX,
                    SAMPLE_RATE, CHANNELS, CHANNEL_MASK, ENCODING, minSize, address);
        } catch (Exception | LinkageError error) {
            IOException failure = captureFailure("AudioRecord setup for registered AudioPolicy mix", error);
            failure.addSuppressed(factoryError);
            throw failure;
        }
    }

    private AudioRecord createDirectRecorder() throws Exception {
        int sourceId = toAndroidAudioSource(sourceMode);
        int minSize = AudioRecord.getMinBufferSize(SAMPLE_RATE, CHANNEL_MASK, ENCODING);
        return AndroidWorkarounds.createAudioRecord(
                sourceId, SAMPLE_RATE, CHANNELS, CHANNEL_MASK, ENCODING, Math.max(minSize, 4096) * 8, null);
    }

    private static int toAndroidAudioSource(AudioSourceMode mode) {
        switch (mode) {
            case MIC:
                return MediaRecorder.AudioSource.MIC;
            case MIC_UNPROCESSED:
                return Build.VERSION.SDK_INT >= 24
                        ? MediaRecorder.AudioSource.UNPROCESSED
                        : MediaRecorder.AudioSource.VOICE_RECOGNITION;
            case MIC_CAMCORDER:
                return MediaRecorder.AudioSource.CAMCORDER;
            case MIC_VOICE_RECOGNITION:
                return MediaRecorder.AudioSource.VOICE_RECOGNITION;
            case MIC_VOICE_COMMUNICATION:
                return MediaRecorder.AudioSource.VOICE_COMMUNICATION;
            case VOICE_CALL:
                return MediaRecorder.AudioSource.VOICE_CALL;
            case VOICE_CALL_UPLINK:
                return MediaRecorder.AudioSource.VOICE_UPLINK;
            case VOICE_CALL_DOWNLINK:
                return MediaRecorder.AudioSource.VOICE_DOWNLINK;
            default:
                return MediaRecorder.AudioSource.REMOTE_SUBMIX;
        }
    }

    private static void addUsageRule(Method addMixRule, Object builder, int ruleMatchUsage, int usage)
            throws ReflectiveOperationException {
        AudioAttributes attrs = new AudioAttributes.Builder().setUsage(usage).build();
        addMixRule.invoke(builder, ruleMatchUsage, attrs);
    }

    private static int readOptionalIntField(Class<?> cls, String name, int fallback) {
        try {
            return cls.getField(name).getInt(null);
        } catch (ReflectiveOperationException ignored) {
            return fallback;
        }
    }

    private static Method findPolicyMethod(Class<?> owner, String name, Class<?> parameter) {
        try {
            Method method = owner.getDeclaredMethod(name, parameter);
            method.setAccessible(true);
            return method;
        } catch (NoSuchMethodException ignored) {
            return null;
        }
    }

    private static IOException captureFailure(String stage, Throwable error) {
        Throwable cause = error instanceof InvocationTargetException ? error.getCause() : error;
        return new IOException(stage + " failed: " + cause, cause);
    }
}
