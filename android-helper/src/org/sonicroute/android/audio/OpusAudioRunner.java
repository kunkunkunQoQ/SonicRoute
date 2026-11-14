// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.audio;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Build;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.List;
import org.sonicroute.android.core.MediaCodecParams;
import org.sonicroute.android.io.PacketWriter;
import org.sonicroute.android.util.ServerLog;

/**
 * Blocking PCM capture + 48 kHz Opus (or raw PCM) encoder worker.
 * Uses pure blocking {@code AudioRecord.read} and {@code MediaCodec.dequeue*} calls without polling.
 */
public final class OpusAudioRunner {
    private static final String MIME_OPUS = MediaFormat.MIMETYPE_AUDIO_OPUS;
    private static final int READ_BUFFER_BYTES = PlaybackAudioCapture.MAX_READ_SIZE * 2;

    private final AudioCaptureSource capture;
    private final PacketWriter writer;
    private final String codecName;
    private final int bitRate;
    private final String explicitEncoder;
    private final List<MediaCodecParams.Entry> codecOptions;
    private final Runnable onFinish;

    private volatile boolean stopped;
    private Thread mainThread;

    public OpusAudioRunner(
            AudioCaptureSource capture,
            PacketWriter writer,
            String codecName,
            int bitRate,
            String explicitEncoder,
            List<MediaCodecParams.Entry> codecOptions,
            Runnable onFinish) {
        this.capture = capture;
        this.writer = writer;
        this.codecName = codecName;
        this.bitRate = bitRate;
        this.explicitEncoder = explicitEncoder;
        this.codecOptions = codecOptions;
        this.onFinish = onFinish;
    }

    public void start() {
        mainThread = new Thread(this::runSafe, "SonicRoute-Audio");
        mainThread.start();
    }

    public void stop() {
        stopped = true;
        capture.stop();
        if (mainThread != null) {
            mainThread.interrupt();
        }
    }

    public void join() throws InterruptedException {
        if (mainThread != null) {
            mainThread.join();
        }
    }

    private void runSafe() {
        try {
            capture.checkSupported();
            capture.start();
        } catch (IOException error) {
            ServerLog.w("Audio capture unavailable, sending disabled marker: " + error.getMessage());
            try {
                writer.writeAudioDisabledMarker();
            } catch (IOException ignored) {
            }
            finish();
            return;
        }

        try {
            writer.writeAudioHeader();
            if ("raw".equalsIgnoreCase(codecName)) {
                streamRawPcm();
            } else {
                streamOpus();
            }
        } catch (IOException error) {
            if (!stopped) {
                ServerLog.d("Audio stream ended: " + error.getMessage());
            }
        } catch (Throwable error) {
            ServerLog.e("Audio encoder failed", error);
        } finally {
            capture.stop();
            finish();
        }
    }

    private void streamRawPcm() throws IOException {
        ByteBuffer buffer = ByteBuffer.allocateDirect(PlaybackAudioCapture.MAX_READ_SIZE);
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        while (!stopped && !Thread.currentThread().isInterrupted()) {
            buffer.clear();
            int read = capture.read(buffer, info);
            if (read < 0) {
                break;
            }
            if (read == 0) {
                continue;
            }
            buffer.limit(read);
            buffer.position(0);
            writer.writePacket(buffer, info);
        }
    }

    private void streamOpus() throws IOException {
        MediaCodec codec = MediaCodecParams.createEncoder(MIME_OPUS, explicitEncoder);
        boolean started = false;
        Thread inputThread = null;
        try {
            MediaFormat format = MediaFormat.createAudioFormat(
                    MIME_OPUS, PlaybackAudioCapture.SAMPLE_RATE, PlaybackAudioCapture.CHANNELS);
            format.setInteger(MediaFormat.KEY_BIT_RATE, bitRate);
            format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, READ_BUFFER_BYTES);
            MediaCodecParams.apply(format, codecOptions);

            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            codec.start();
            started = true;

            final boolean c2OpusPtsFix = isC2AndroidOpusEncoder(codec);
            inputThread = new Thread(() -> feedInputLoop(codec), "SonicRoute-AudioIn");
            inputThread.start();
            drainOutputLoop(codec, c2OpusPtsFix);
        } finally {
            stopped = true;
            capture.stop();
            if (started) {
                try {
                    codec.stop();
                } catch (Throwable ignored) {
                }
            }
            if (inputThread != null) {
                inputThread.interrupt();
                try {
                    inputThread.join(500);
                } catch (InterruptedException ignored) {
                    Thread.currentThread().interrupt();
                }
            }
            try {
                codec.release();
            } catch (Throwable ignored) {
            }
        }
    }

    private void feedInputLoop(MediaCodec codec) {
        MediaCodec.BufferInfo readInfo = new MediaCodec.BufferInfo();
        while (!stopped && !Thread.currentThread().isInterrupted()) {
            int inputIndex;
            try {
                inputIndex = codec.dequeueInputBuffer(-1);
            } catch (IllegalStateException error) {
                break;
            }
            if (inputIndex < 0) {
                continue;
            }
            ByteBuffer dst;
            try {
                dst = codec.getInputBuffer(inputIndex);
            } catch (IllegalStateException error) {
                break;
            }
            if (dst == null) {
                break;
            }
            int read = -1;
            try {
                read = capture.read(dst, readInfo);
            } catch (IOException error) {
                if (!stopped) {
                    ServerLog.w("Audio capture read failed: " + error.getMessage());
                }
            }
            if (read <= 0 || stopped) {
                try {
                    codec.queueInputBuffer(inputIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                } catch (IllegalStateException ignored) {
                }
                break;
            }
            try {
                codec.queueInputBuffer(inputIndex, 0, read, readInfo.presentationTimeUs, 0);
            } catch (IllegalStateException error) {
                break;
            }
        }
    }

    private void drainOutputLoop(MediaCodec codec, boolean c2OpusPtsFix) throws IOException {
        MediaCodec.BufferInfo outInfo = new MediaCodec.BufferInfo();
        long previousInputPtsUs = 0;
        boolean eos = false;
        while (!eos && !stopped) {
            int outputIndex;
            try {
                outputIndex = codec.dequeueOutputBuffer(outInfo, -1);
            } catch (IllegalStateException error) {
                break;
            }
            if (outputIndex < 0) {
                continue;
            }
            try {
                eos = (outInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                if (outInfo.size > 0) {
                    ByteBuffer buf = codec.getOutputBuffer(outputIndex);
                    if (buf != null) {
                        buf.position(outInfo.offset);
                        buf.limit(outInfo.offset + outInfo.size);
                        boolean isConfig = (outInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                        if (c2OpusPtsFix && !isConfig) {
                            long durationUs = previousInputPtsUs > 0 ? Math.max(0L, outInfo.presentationTimeUs - previousInputPtsUs) : 0L;
                            previousInputPtsUs = outInfo.presentationTimeUs;
                            outInfo.presentationTimeUs = (System.nanoTime() / 1000L) - durationUs;
                        }
                        writer.writePacket(buf, outInfo);
                    }
                }
            } finally {
                try {
                    codec.releaseOutputBuffer(outputIndex, false);
                } catch (IllegalStateException ignored) {
                }
            }
        }
    }

    private static boolean isC2AndroidOpusEncoder(MediaCodec codec) {
        if (Build.VERSION.SDK_INT < 29) {
            return false;
        }
        try {
            return "c2.android.opus.encoder".equals(codec.getCanonicalName());
        } catch (Throwable ignored) {
            return false;
        }
    }

    private void finish() {
        if (onFinish != null) {
            onFinish.run();
        }
    }
}
