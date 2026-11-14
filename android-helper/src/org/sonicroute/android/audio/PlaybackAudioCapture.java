// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.audio;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioTimestamp;
import android.media.MediaCodec;
import android.media.MediaRecorder;
import android.os.Build;
import java.io.IOException;
import java.lang.reflect.Method;
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
 * Supports UID/package isolation, {@code audio_dup}, {@code audio_mix_mode=uid|pr}, and strict
 * no-global leak sentinel ({@code 0x7FFFFFFF}).
 */
public final class PlaybackAudioCapture implements AudioCaptureSource {
    public static final int SAMPLE_RATE = 48_000;
    public static final int CHANNELS = 2;
    public static final int BYTES_PER_SAMPLE = 2;
    public static final int CHANNEL_MASK = AudioFormat.CHANNEL_IN_STEREO;
    public static final int ENCODING = AudioFormat.ENCODING_PCM_16BIT;
    public static final int MAX_READ_SIZE = 4096;
    private static final long ONE_SAMPLE_US = (1_000_000L + SAMPLE_RATE - 1) / SAMPLE_RATE;

    // Sentinel UID guaranteeing empty capture instead of leaking unrelated phone audio.
    private static final int NO_MATCH_SENTINEL_UID = 0x7FFFFFFF;

    private final AudioSourceMode sourceMode;
    private final boolean keepPlayingOnDevice;
    private final boolean prMixMode;
    private boolean strictNoGlobal;
    private volatile int[] targetUids = new int[0];

    private Object registeredPolicy;
    private AudioRecord recorder;
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
            if (uid >= 0) {
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
        if (sourceMode.isPlayback() && Build.VERSION.SDK_INT < 29) {
            throw new IOException("Audio playback capture requires Android 10+");
        }
    }

    @Override
    public void start() throws IOException {
        stop();
        lastTimestampNs = -1;
        previousPtsUs = 0;
        nextPtsUs = 0;

        try {
            recorder = sourceMode.isPlayback() ? createPlaybackPolicyRecorder() : createDirectRecorder();
            if (recorder == null) {
                throw new IOException("Failed to create AudioRecord instance");
            }
            recorder.startRecording();
        } catch (IOException error) {
            stop();
            throw error;
        } catch (Throwable error) {
            stop();
            throw new IOException("Unable to start audio capture: " + error.getMessage(), error);
        }
    }

    @Override
    public synchronized void stop() {
        if (recorder != null) {
            try {
                recorder.stop();
            } catch (Throwable ignored) {
            }
            try {
                recorder.release();
            } catch (Throwable ignored) {
            }
            recorder = null;
        }
        unregisterPolicy();
    }

    @Override
    public int read(ByteBuffer outDirectBuffer, MediaCodec.BufferInfo outBufferInfo) throws IOException {
        AudioRecord rec = recorder;
        if (rec == null) {
            return -1;
        }
        int maxBytes = Math.min(outDirectBuffer.remaining(), MAX_READ_SIZE);
        if (maxBytes <= 0) {
            return 0;
        }
        int readBytes = rec.read(outDirectBuffer, maxBytes);
        if (readBytes <= 0) {
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
        Class<?> ruleClass = Class.forName("android.media.audiopolicy.AudioMixingRule");
        Class<?> ruleBuilderClass = Class.forName("android.media.audiopolicy.AudioMixingRule$Builder");
        Object ruleBuilder = ruleBuilderClass.getConstructor().newInstance();

        int mixRolePlayers = ruleClass.getField("MIX_ROLE_PLAYERS").getInt(null);
        ruleBuilderClass.getMethod("setTargetMixRole", int.class).invoke(ruleBuilder, mixRolePlayers);

        int ruleMatchUsage = ruleClass.getField("RULE_MATCH_ATTRIBUTE_USAGE").getInt(null);
        int ruleMatchUid = readOptionalIntField(ruleClass, "RULE_MATCH_UID", 4);
        Method addMixRule = ruleBuilderClass.getMethod("addMixRule", int.class, Object.class);

        int[] effectiveUids = targetUids;
        if (effectiveUids.length == 0 && strictNoGlobal) {
            effectiveUids = new int[] { NO_MATCH_SENTINEL_UID };
            ServerLog.w("Strict audio isolation active with no resolved UID: capturing silence via sentinel UID");
        }

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

        int routeFlag = keepPlayingOnDevice
                ? mixClass.getField("ROUTE_FLAG_LOOP_BACK_RENDER").getInt(null)
                : mixClass.getField("ROUTE_FLAG_LOOP_BACK").getInt(null);
        mixBuilderClass.getMethod("setRouteFlags", int.class).invoke(mixBuilder, routeFlag);
        Object audioMix = mixBuilderClass.getMethod("build").invoke(mixBuilder);

        Class<?> policyClass = Class.forName("android.media.audiopolicy.AudioPolicy");
        Class<?> policyBuilderClass = Class.forName("android.media.audiopolicy.AudioPolicy$Builder");
        Object policyBuilder = policyBuilderClass.getConstructor(android.content.Context.class).newInstance(ShellContext.get());
        policyBuilderClass.getMethod("addMix", mixClass).invoke(policyBuilder, audioMix);
        Object policy = policyBuilderClass.getMethod("build").invoke(policyBuilder);

        Method registerStatic = AudioManager.class.getDeclaredMethod("registerAudioPolicyStatic", policyClass);
        registerStatic.setAccessible(true);
        int status = (int) registerStatic.invoke(null, policy);
        if (status != 0) {
            throw new IOException("registerAudioPolicyStatic failed with status " + status);
        }
        registeredPolicy = policy;
        return (AudioRecord) policyClass.getMethod("createAudioRecordSink", mixClass).invoke(policy, audioMix);
    }

    private AudioRecord createDirectRecorder() {
        int sourceId = toAndroidAudioSource(sourceMode);
        return AndroidWorkarounds.createAudioRecord(
                sourceId, SAMPLE_RATE, CHANNEL_MASK, CHANNELS, CHANNEL_MASK, ENCODING);
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

    private void unregisterPolicy() {
        if (registeredPolicy == null) {
            return;
        }
        try {
            Class<?> policyClass = Class.forName("android.media.audiopolicy.AudioPolicy");
            Method unregister = AudioManager.class.getDeclaredMethod("unregisterAudioPolicyAsyncStatic", policyClass);
            unregister.setAccessible(true);
            unregister.invoke(null, registeredPolicy);
        } catch (Throwable error) {
            ServerLog.w("unregisterAudioPolicyAsyncStatic failed", error);
        } finally {
            registeredPolicy = null;
        }
    }
}
