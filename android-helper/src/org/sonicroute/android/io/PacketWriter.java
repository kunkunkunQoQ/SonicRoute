// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.io;

import android.media.MediaCodec;
import android.system.ErrnoException;
import android.system.Os;
import java.io.FileDescriptor;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;

/**
 * Framed elementary stream writer compatible with {@code ScrcpyOpusReceiver} and
 * {@code ScrcpyVideoProtocol} (4B codec id, optional video dimensions, 12B packet header).
 */
public final class PacketWriter {
    public static final int CODEC_ID_H264 = 0x68323634; // "h264"
    public static final int CODEC_ID_OPUS = 0x6f707573; // "opus"
    public static final int CODEC_ID_RAW = 0x72617720;  // "raw "

    private static final long PACKET_FLAG_CONFIG = 1L << 63;
    private static final long PACKET_FLAG_KEY_FRAME = 1L << 62;
    private static final byte[] OPUS_CSD_MAGIC = "AOPUSHDR".getBytes(StandardCharsets.US_ASCII);
    private static final byte[] OPUS_HEAD_MAGIC = "OpusHead".getBytes(StandardCharsets.US_ASCII);

    private final FileDescriptor fd;
    private final int codecId;
    private final boolean sendCodecMeta;
    private final boolean sendFrameMeta;
    private final ByteBuffer headerBuffer = ByteBuffer.allocate(12);

    public PacketWriter(FileDescriptor fd, int codecId, boolean sendCodecMeta, boolean sendFrameMeta) {
        this.fd = fd;
        this.codecId = codecId;
        this.sendCodecMeta = sendCodecMeta;
        this.sendFrameMeta = sendFrameMeta;
    }

    public static int codecIdForName(String name) {
        if ("h264".equalsIgnoreCase(name)) {
            return CODEC_ID_H264;
        }
        if ("opus".equalsIgnoreCase(name)) {
            return CODEC_ID_OPUS;
        }
        if ("raw".equalsIgnoreCase(name)) {
            return CODEC_ID_RAW;
        }
        throw new IllegalArgumentException("Unsupported codec name: " + name);
    }

    public void writeVideoHeader(int width, int height) throws IOException {
        if (!sendCodecMeta) {
            return;
        }
        ByteBuffer buf = ByteBuffer.allocate(12);
        buf.putInt(codecId);
        buf.putInt(width);
        buf.putInt(height);
        buf.flip();
        writeFully(fd, buf);
    }

    public void writeAudioHeader() throws IOException {
        if (!sendCodecMeta) {
            return;
        }
        ByteBuffer buf = ByteBuffer.allocate(4);
        buf.putInt(codecId);
        buf.flip();
        writeFully(fd, buf);
    }

    public void writeAudioDisabledMarker() throws IOException {
        ByteBuffer buf = ByteBuffer.allocate(4);
        buf.putInt(0);
        buf.flip();
        writeFully(fd, buf);
    }

    public void writePacket(ByteBuffer payload, long ptsUs, boolean isConfig, boolean isKeyFrame) throws IOException {
        if (isConfig && codecId == CODEC_ID_OPUS) {
            ByteBuffer stripped = extractOpusHead(payload);
            writeRawPacket(stripped, ptsUs, true, isKeyFrame);
            return;
        }
        writeRawPacket(payload, ptsUs, isConfig, isKeyFrame);
    }

    public void writePacket(ByteBuffer payload, MediaCodec.BufferInfo info) throws IOException {
        boolean isConfig = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
        boolean isKeyFrame = (info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;
        writePacket(payload, info.presentationTimeUs, isConfig, isKeyFrame);
    }

    private void writeRawPacket(ByteBuffer payload, long ptsUs, boolean isConfig, boolean isKeyFrame) throws IOException {
        if (sendFrameMeta) {
            headerBuffer.clear();
            long ptsAndFlags;
            if (isConfig) {
                ptsAndFlags = PACKET_FLAG_CONFIG;
            } else {
                ptsAndFlags = ptsUs & (PACKET_FLAG_KEY_FRAME - 1);
                if (isKeyFrame) {
                    ptsAndFlags |= PACKET_FLAG_KEY_FRAME;
                }
            }
            headerBuffer.putLong(ptsAndFlags);
            headerBuffer.putInt(payload.remaining());
            headerBuffer.flip();
            writeFully(fd, headerBuffer);
        }
        writeFully(fd, payload);
    }

    /**
     * Strips Android's {@code AOPUSHDR} MediaCodec CSD wrapper (8-byte magic + native-order
     * 64-bit payload length + trailing delay/preroll tags) down to the raw RFC 7845 {@code OpusHead}
     * slice, or passes through an un-wrapped {@code OpusHead} buffer unchanged.
     */
    public static ByteBuffer extractOpusHead(ByteBuffer src) throws IOException {
        ByteBuffer view = src.duplicate();
        int remaining = view.remaining();
        if (remaining >= 16 && startsWith(view, OPUS_CSD_MAGIC)) {
            view.position(view.position() + 8);
            view.order(ByteOrder.nativeOrder());
            long headerLenLong = view.getLong();
            if (headerLenLong < 19 || headerLenLong > remaining - 16) {
                throw new IOException("Invalid AOPUSHDR length: " + headerLenLong + " (remaining=" + remaining + ")");
            }
            int headerLen = (int) headerLenLong;
            ByteBuffer slice = view.slice();
            slice.limit(headerLen);
            return slice;
        }
        if (remaining >= 19 && startsWith(view, OPUS_HEAD_MAGIC)) {
            return view;
        }
        return src;
    }

    private static boolean startsWith(ByteBuffer buf, byte[] prefix) {
        if (buf.remaining() < prefix.length) {
            return false;
        }
        int base = buf.position();
        for (int i = 0; i < prefix.length; ++i) {
            if (buf.get(base + i) != prefix[i]) {
                return false;
            }
        }
        return true;
    }

    private static void writeFully(FileDescriptor target, ByteBuffer buf) throws IOException {
        while (buf.hasRemaining()) {
            try {
                int written = Os.write(target, buf);
                if (written <= 0) {
                    throw new IOException("Zero-byte write on stream descriptor");
                }
            } catch (ErrnoException error) {
                throw new IOException("Stream write failed: errno=" + error.errno, error);
            }
        }
    }
}
