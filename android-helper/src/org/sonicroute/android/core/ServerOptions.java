// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.core;

import org.sonicroute.android.model.Geometry.NewDisplaySpec;
import org.sonicroute.android.model.Geometry.Size;
import org.sonicroute.android.util.ServerLog;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;

/**
 * Parses CLI {@code key=value} arguments for {@code SonicRouteServer}.
 * Accepts an optional non-key-value version token as {@code args[0]} so both legacy
 * scrcpy launchers and direct SonicRoute callers work seamlessly.
 */
public final class ServerOptions {
    public static final int DISPLAY_ID_NONE = -1;
    public static final int IME_POLICY_UNSET = -1;
    public static final int IME_POLICY_LOCAL = 0;
    public static final int IME_POLICY_FALLBACK = 1;
    public static final int IME_POLICY_HIDE = 2;

    public enum AudioSourceMode {
        PLAYBACK,
        OUTPUT,
        MIC,
        MIC_UNPROCESSED,
        MIC_CAMCORDER,
        MIC_VOICE_RECOGNITION,
        MIC_VOICE_COMMUNICATION,
        VOICE_CALL,
        VOICE_CALL_UPLINK,
        VOICE_CALL_DOWNLINK;

        public boolean isPlayback() {
            return this == PLAYBACK || this == OUTPUT;
        }

        public static AudioSourceMode parse(String value) {
            switch (value) {
                case "playback":
                    return PLAYBACK;
                case "output":
                    return OUTPUT;
                case "mic":
                    return MIC;
                case "mic-unprocessed":
                    return MIC_UNPROCESSED;
                case "mic-camcorder":
                    return MIC_CAMCORDER;
                case "mic-voice-recognition":
                    return MIC_VOICE_RECOGNITION;
                case "mic-voice-communication":
                    return MIC_VOICE_COMMUNICATION;
                case "voice-call":
                    return VOICE_CALL;
                case "voice-call-uplink":
                    return VOICE_CALL_UPLINK;
                case "voice-call-downlink":
                    return VOICE_CALL_DOWNLINK;
                default:
                    throw new IllegalArgumentException("Unsupported audio_source: " + value);
            }
        }
    }

    private ServerLog.Level logLevel = ServerLog.Level.DEBUG;
    private int scid = -1;
    private boolean hasScid;
    private String socketPrefix = "scrcpy";
    private boolean video = true;
    private boolean audio = true;
    private boolean control = true;

    private String videoCodec = "h264";
    private int videoBitRate = 8_000_000;
    private int maxSize;
    private float maxFps;
    private String videoEncoder;
    private List<MediaCodecParams.Entry> videoCodecOptions = Collections.emptyList();
    private boolean downsizeOnError = true;

    private int displayId = 0;
    private NewDisplaySpec newDisplay;
    private boolean vdDestroyContent = true;
    private boolean vdSystemDecorations = true;
    private int displayImePolicy = IME_POLICY_UNSET;
    private String startApp;

    private String audioCodec = "opus";
    private AudioSourceMode audioSource = AudioSourceMode.OUTPUT;
    private boolean audioDup;
    private boolean audioMixModePr;
    private String audioTargetPackages;
    private int audioBitRate = 128_000;
    private String audioEncoder;
    private List<MediaCodecParams.Entry> audioCodecOptions = Collections.emptyList();

    private boolean tunnelForward;
    private boolean sendDeviceMeta = true;
    private boolean sendFrameMeta = true;
    private boolean sendDummyByte = true;
    private boolean sendCodecMeta = true;

    private boolean clipboardAutosync = true;
    private boolean powerOn = true;
    private boolean powerOffScreenOnClose;
    private boolean stayAwake;
    private boolean showTouches;
    private int screenOffTimeout = -1;
    private boolean cleanup = true;

    private ServerOptions() {
    }

    public static ServerOptions parse(String... args) {
        ServerOptions opts = new ServerOptions();
        if (args == null || args.length == 0) {
            return opts;
        }

        int startIndex = 0;
        if (args[0] != null && args[0].indexOf('=') == -1) {
            // Optional compatibility version token (e.g. "3.3.4" or "1.0").
            startIndex = 1;
        }

        for (int i = startIndex; i < args.length; ++i) {
            String arg = args[i];
            if (arg == null || arg.isEmpty()) {
                continue;
            }
            int eq = arg.indexOf('=');
            if (eq == -1) {
                if ("new_display".equals(arg)) {
                    applyOption(opts, "new_display", "");
                    continue;
                }
                throw new IllegalArgumentException("Expected key=value argument, got: \"" + arg + "\"");
            }
            if (eq == 0) {
                throw new IllegalArgumentException("Empty option key in argument: \"" + arg + "\"");
            }
            String key = arg.substring(0, eq);
            String value = arg.substring(eq + 1);
            applyOption(opts, key, value);
        }

        if (opts.newDisplay != null) {
            opts.displayId = DISPLAY_ID_NONE;
        }
        return opts;
    }

    private static void applyOption(ServerOptions opts, String key, String value) {
        switch (key) {
            case "scid":
                if ("-1".equals(value)) {
                    opts.scid = -1;
                    opts.hasScid = false;
                } else {
                    opts.scid = Integer.parseUnsignedInt(value, 16);
                    opts.hasScid = true;
                }
                break;
            case "socket_prefix":
                if (!value.isEmpty() && value.matches("[A-Za-z0-9_.-]+")) {
                    opts.socketPrefix = value;
                } else {
                    throw new IllegalArgumentException("Invalid socket_prefix: " + value);
                }
                break;
            case "log_level":
                opts.logLevel = ServerLog.Level.parse(value);
                break;
            case "video":
                opts.video = Boolean.parseBoolean(value);
                break;
            case "audio":
                opts.audio = Boolean.parseBoolean(value);
                break;
            case "control":
                opts.control = Boolean.parseBoolean(value);
                break;
            case "video_codec":
                if (!"h264".equalsIgnoreCase(value)) {
                    throw new IllegalArgumentException("Only h264 video_codec is supported, got: " + value);
                }
                opts.videoCodec = "h264";
                break;
            case "audio_codec":
                String lowerAudioCodec = value.toLowerCase(Locale.ENGLISH);
                if (!"opus".equals(lowerAudioCodec) && !"raw".equals(lowerAudioCodec)) {
                    throw new IllegalArgumentException("Unsupported audio_codec: " + value);
                }
                opts.audioCodec = lowerAudioCodec;
                break;
            case "video_source":
                if (!"display".equalsIgnoreCase(value)) {
                    throw new IllegalArgumentException("Only display video_source is supported, got: " + value);
                }
                break;
            case "audio_source":
                opts.audioSource = AudioSourceMode.parse(value);
                break;
            case "audio_dup":
                opts.audioDup = Boolean.parseBoolean(value);
                break;
            case "audio_mix_mode":
                opts.audioMixModePr = "pr".equalsIgnoreCase(value);
                break;
            case "audio_target_packages":
                opts.audioTargetPackages = value;
                break;
            case "max_size":
                opts.maxSize = Integer.parseInt(value) & ~7;
                break;
            case "video_bit_rate":
                opts.videoBitRate = Integer.parseInt(value);
                break;
            case "audio_bit_rate":
                opts.audioBitRate = Integer.parseInt(value);
                break;
            case "max_fps":
                opts.maxFps = Float.parseFloat(value);
                break;
            case "video_encoder":
                opts.videoEncoder = value.isEmpty() ? null : value;
                break;
            case "audio_encoder":
                opts.audioEncoder = value.isEmpty() ? null : value;
                break;
            case "video_codec_options":
                opts.videoCodecOptions = MediaCodecParams.parse(value);
                break;
            case "audio_codec_options":
                opts.audioCodecOptions = MediaCodecParams.parse(value);
                break;
            case "downsize_on_error":
                opts.downsizeOnError = Boolean.parseBoolean(value);
                break;
            case "display_id":
                opts.displayId = Integer.parseInt(value);
                break;
            case "new_display":
                opts.newDisplay = parseNewDisplay(value);
                break;
            case "vd_destroy_content":
                opts.vdDestroyContent = Boolean.parseBoolean(value);
                break;
            case "vd_system_decorations":
                opts.vdSystemDecorations = Boolean.parseBoolean(value);
                break;
            case "display_ime_policy":
                opts.displayImePolicy = parseDisplayImePolicy(value);
                break;
            case "start_app":
                opts.startApp = value.isEmpty() ? null : value;
                break;
            case "tunnel_forward":
                opts.tunnelForward = Boolean.parseBoolean(value);
                break;
            case "send_device_meta":
                opts.sendDeviceMeta = Boolean.parseBoolean(value);
                break;
            case "send_frame_meta":
                opts.sendFrameMeta = Boolean.parseBoolean(value);
                break;
            case "send_dummy_byte":
                opts.sendDummyByte = Boolean.parseBoolean(value);
                break;
            case "send_codec_meta":
                opts.sendCodecMeta = Boolean.parseBoolean(value);
                break;
            case "raw_stream":
                if (Boolean.parseBoolean(value)) {
                    opts.sendDeviceMeta = false;
                    opts.sendFrameMeta = false;
                    opts.sendDummyByte = false;
                    opts.sendCodecMeta = false;
                }
                break;
            case "clipboard_autosync":
                opts.clipboardAutosync = Boolean.parseBoolean(value);
                break;
            case "power_on":
                opts.powerOn = Boolean.parseBoolean(value);
                break;
            case "power_off_on_close":
                opts.powerOffScreenOnClose = Boolean.parseBoolean(value);
                break;
            case "stay_awake":
                opts.stayAwake = Boolean.parseBoolean(value);
                break;
            case "show_touches":
                opts.showTouches = Boolean.parseBoolean(value);
                break;
            case "screen_off_timeout":
                opts.screenOffTimeout = Integer.parseInt(value);
                if (opts.screenOffTimeout < -1) {
                    throw new IllegalArgumentException("Invalid screen_off_timeout: " + opts.screenOffTimeout);
                }
                break;
            case "cleanup":
                opts.cleanup = Boolean.parseBoolean(value);
                break;
            default:
                ServerLog.w("Ignoring unknown server option: " + key);
                break;
        }
    }

    private static NewDisplaySpec parseNewDisplay(String value) {
        if (value.isEmpty()) {
            return new NewDisplaySpec(null, 0);
        }
        String[] parts = value.split("/", -1);
        Size size = null;
        if (!parts[0].isEmpty()) {
            String[] dims = parts[0].split("x", -1);
            if (dims.length != 2) {
                throw new IllegalArgumentException("Invalid new_display size: " + value);
            }
            int w = Integer.parseInt(dims[0]);
            int h = Integer.parseInt(dims[1]);
            if (w <= 0 || h <= 0) {
                throw new IllegalArgumentException("Non-positive new_display dimensions: " + value);
            }
            size = new Size(w, h);
        }
        int dpi = 0;
        if (parts.length >= 2 && !parts[1].isEmpty()) {
            dpi = Integer.parseInt(parts[1]);
            if (dpi <= 0) {
                throw new IllegalArgumentException("Non-positive new_display dpi: " + value);
            }
        }
        return new NewDisplaySpec(size, dpi);
    }

    private static int parseDisplayImePolicy(String value) {
        switch (value.toLowerCase(Locale.ENGLISH)) {
            case "local":
            case "0":
                return IME_POLICY_LOCAL;
            case "fallback":
            case "1":
                return IME_POLICY_FALLBACK;
            case "hide":
            case "2":
                return IME_POLICY_HIDE;
            default:
                throw new IllegalArgumentException("Unsupported display_ime_policy: " + value);
        }
    }

    public static List<String> splitCsv(String csv) {
        if (csv == null || csv.trim().isEmpty()) {
            return Collections.emptyList();
        }
        List<String> out = new ArrayList<>();
        for (String item : csv.split(",")) {
            String trimmed = item.trim();
            if (!trimmed.isEmpty()) {
                out.add(trimmed);
            }
        }
        return out;
    }

    public ServerLog.Level getLogLevel() {
        return logLevel;
    }

    public int getScid() {
        return scid;
    }

    public String getSocketName() {
        if (!hasScid) {
            return socketPrefix;
        }
        return socketPrefix + String.format(Locale.ENGLISH, "_%08x", scid);
    }

    public boolean isVideo() {
        return video;
    }

    public boolean isAudio() {
        return audio;
    }

    public boolean isControl() {
        return control;
    }

    public String getVideoCodec() {
        return videoCodec;
    }

    public int getVideoBitRate() {
        return videoBitRate;
    }

    public int getMaxSize() {
        return maxSize;
    }

    public float getMaxFps() {
        return maxFps;
    }

    public String getVideoEncoder() {
        return videoEncoder;
    }

    public List<MediaCodecParams.Entry> getVideoCodecOptions() {
        return videoCodecOptions;
    }

    public boolean isDownsizeOnError() {
        return downsizeOnError;
    }

    public int getDisplayId() {
        return displayId;
    }

    public NewDisplaySpec getNewDisplay() {
        return newDisplay;
    }

    public boolean isVdDestroyContent() {
        return vdDestroyContent;
    }

    public boolean isVdSystemDecorations() {
        return vdSystemDecorations;
    }

    public int getDisplayImePolicy() {
        return displayImePolicy;
    }

    public String getStartApp() {
        return startApp;
    }

    public String getAudioCodec() {
        return audioCodec;
    }

    public AudioSourceMode getAudioSource() {
        return audioSource;
    }

    public boolean isAudioDup() {
        return audioDup;
    }

    public boolean isAudioMixModePr() {
        return audioMixModePr;
    }

    public String getAudioTargetPackages() {
        return audioTargetPackages;
    }

    public int getAudioBitRate() {
        return audioBitRate;
    }

    public String getAudioEncoder() {
        return audioEncoder;
    }

    public List<MediaCodecParams.Entry> getAudioCodecOptions() {
        return audioCodecOptions;
    }

    public boolean isTunnelForward() {
        return tunnelForward;
    }

    public boolean isSendDeviceMeta() {
        return sendDeviceMeta;
    }

    public boolean isSendFrameMeta() {
        return sendFrameMeta;
    }

    public boolean isSendDummyByte() {
        return sendDummyByte;
    }

    public boolean isSendCodecMeta() {
        return sendCodecMeta;
    }

    public boolean isClipboardAutosync() {
        return clipboardAutosync;
    }

    public boolean isPowerOn() {
        return powerOn;
    }

    public boolean isPowerOffScreenOnClose() {
        return powerOffScreenOnClose;
    }

    public boolean isStayAwake() {
        return stayAwake;
    }

    public boolean isShowTouches() {
        return showTouches;
    }

    public int getScreenOffTimeout() {
        return screenOffTimeout;
    }

    public boolean isCleanup() {
        return cleanup;
    }
}
