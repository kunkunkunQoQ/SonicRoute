// SPDX-License-Identifier: MIT
package org.sonicroute.android.audio;

import android.media.MediaCodec;
import java.io.IOException;
import java.nio.ByteBuffer;

/**
 * Common interface for PCM audio capture sources feeding the SonicRoute audio pipeline.
 */
public interface AudioCaptureSource {
    void checkSupported() throws IOException;

    void start() throws IOException;

    void stop();

    int read(ByteBuffer outDirectBuffer, MediaCodec.BufferInfo outBufferInfo) throws IOException;
}
