// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android.video;

import android.media.MediaCodec;
import android.view.Surface;
import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;
import org.sonicroute.android.model.Geometry.Size;

/**
 * Abstract display capture source producing rendered frames onto a {@link MediaCodec} input
 * {@link Surface}, paired with an event-driven {@link ResetCoordinator}.
 */
public abstract class VideoCaptureSource {
    public static final class ResetCoordinator {
        private final AtomicBoolean pending = new AtomicBoolean();
        private MediaCodec activeCodec;

        public boolean consumeReset() {
            return pending.getAndSet(false);
        }

        public synchronized void setActiveCodec(MediaCodec codec) {
            this.activeCodec = codec;
            if (codec != null && pending.get()) {
                try {
                    codec.signalEndOfInputStream();
                } catch (IllegalStateException ignored) {
                }
            }
        }

        public synchronized void requestReset() {
            pending.set(true);
            if (activeCodec != null) {
                try {
                    activeCodec.signalEndOfInputStream();
                } catch (IllegalStateException ignored) {
                }
            }
        }
    }

    private final ResetCoordinator reset = new ResetCoordinator();

    public ResetCoordinator getResetCoordinator() {
        return reset;
    }

    public abstract void init() throws IOException;

    public abstract void prepare() throws IOException;

    public abstract void start(Surface surface) throws IOException;

    public abstract void stop();

    public abstract void release();

    public abstract Size getVideoSize();

    public abstract Size getDisplaySize();

    public abstract int getDisplayId();

    public abstract boolean setMaxSize(int newMaxSize);

    public boolean isClosed() {
        return false;
    }

    public void requestReset() {
        reset.requestReset();
    }
}
