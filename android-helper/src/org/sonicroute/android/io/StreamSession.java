// SPDX-License-Identifier: MIT
package org.sonicroute.android.io;

import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import android.os.Build;
import java.io.Closeable;
import java.io.FileDescriptor;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

/**
 * Manages the ordered local abstract sockets (video, audio, control) shared with the host.
 */
public final class StreamSession implements Closeable {
    private static final int DEVICE_NAME_FIELD_BYTES = 64;

    private final LocalSocket videoSocket;
    private final LocalSocket audioSocket;
    private final LocalSocket controlSocket;

    private StreamSession(LocalSocket videoSocket, LocalSocket audioSocket, LocalSocket controlSocket) {
        this.videoSocket = videoSocket;
        this.audioSocket = audioSocket;
        this.controlSocket = controlSocket;
    }

    public static StreamSession open(
            String socketName,
            boolean tunnelForward,
            boolean video,
            boolean audio,
            boolean control,
            boolean sendDummyByte) throws IOException {
        LocalSocket videoSocket = null;
        LocalSocket audioSocket = null;
        LocalSocket controlSocket = null;
        try {
            if (tunnelForward) {
                try (LocalServerSocket serverSocket = new LocalServerSocket(socketName)) {
                    if (video) {
                        videoSocket = acceptAndMaybeWriteDummy(serverSocket, true, sendDummyByte);
                    }
                    if (audio) {
                        audioSocket = acceptAndMaybeWriteDummy(serverSocket, !video, sendDummyByte);
                    }
                    if (control) {
                        controlSocket = acceptAndMaybeWriteDummy(serverSocket, !video && !audio, sendDummyByte);
                    }
                }
            } else {
                if (video) {
                    videoSocket = connectClient(socketName, true, sendDummyByte);
                }
                if (audio) {
                    audioSocket = connectClient(socketName, !video, sendDummyByte);
                }
                if (control) {
                    controlSocket = connectClient(socketName, !video && !audio, sendDummyByte);
                }
            }

            return new StreamSession(videoSocket, audioSocket, controlSocket);
        } catch (IOException | RuntimeException error) {
            closeQuietly(videoSocket);
            closeQuietly(audioSocket);
            closeQuietly(controlSocket);
            throw error;
        }
    }

    private static LocalSocket acceptAndMaybeWriteDummy(
            LocalServerSocket serverSocket, boolean isFirstSocket, boolean sendDummyByte) throws IOException {
        LocalSocket socket = serverSocket.accept();
        if (isFirstSocket && sendDummyByte) {
            // Write dummy byte before accepting subsequent channels so single-threaded clients do not deadlock.
            socket.getOutputStream().write(0);
            socket.getOutputStream().flush();
        }
        return socket;
    }

    private static LocalSocket connectClient(
            String socketName, boolean isFirstSocket, boolean sendDummyByte) throws IOException {
        LocalSocket socket = new LocalSocket();
        socket.connect(new LocalSocketAddress(socketName));
        if (isFirstSocket && sendDummyByte) {
            socket.getOutputStream().write(0);
            socket.getOutputStream().flush();
        }
        return socket;
    }

    public LocalSocket getFirstSocket() {
        if (videoSocket != null) {
            return videoSocket;
        }
        if (audioSocket != null) {
            return audioSocket;
        }
        return controlSocket;
    }

    public FileDescriptor getVideoFd() throws IOException {
        return videoSocket != null ? videoSocket.getFileDescriptor() : null;
    }

    public FileDescriptor getAudioFd() throws IOException {
        return audioSocket != null ? audioSocket.getFileDescriptor() : null;
    }

    public LocalSocket getControlSocket() {
        return controlSocket;
    }

    public void sendDeviceMeta() throws IOException {
        LocalSocket first = getFirstSocket();
        if (first == null) {
            return;
        }
        byte[] packet = buildDeviceMetaPacket(Build.MODEL);
        OutputStream out = first.getOutputStream();
        out.write(packet);
        out.flush();
    }

    public static byte[] buildDeviceMetaPacket(String deviceName) {
        byte[] out = new byte[DEVICE_NAME_FIELD_BYTES];
        if (deviceName == null || deviceName.isEmpty()) {
            return out;
        }
        byte[] raw = deviceName.getBytes(StandardCharsets.UTF_8);
        int len = truncateUtf8(raw, DEVICE_NAME_FIELD_BYTES - 1);
        System.arraycopy(raw, 0, out, 0, len);
        return out;
    }

    public static int truncateUtf8(byte[] utf8, int maxBytes) {
        if (utf8.length <= maxBytes) {
            return utf8.length;
        }
        int len = Math.max(0, maxBytes);
        while (len > 0 && (utf8[len] & 0xC0) == 0x80) {
            len--;
        }
        return len;
    }

    @Override
    public void close() {
        closeQuietly(videoSocket);
        closeQuietly(audioSocket);
        closeQuietly(controlSocket);
    }

    public void shutdownStreams() {
        shutdownQuietly(videoSocket);
        shutdownQuietly(audioSocket);
        shutdownQuietly(controlSocket);
    }

    private static void shutdownQuietly(LocalSocket socket) {
        if (socket == null) {
            return;
        }
        try {
            socket.shutdownInput();
        } catch (IOException ignored) {
        }
        try {
            socket.shutdownOutput();
        } catch (IOException ignored) {
        }
    }

    private static void closeQuietly(Closeable closeable) {
        if (closeable == null) {
            return;
        }
        try {
            closeable.close();
        } catch (IOException ignored) {
        }
    }
}
