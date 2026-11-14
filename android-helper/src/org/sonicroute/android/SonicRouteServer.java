// SPDX-License-Identifier: LicenseRef-SonicRoute-Source-Available-1.0
package org.sonicroute.android;

import android.os.Build;
import android.os.Looper;
import android.view.KeyEvent;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import org.sonicroute.android.audio.OpusAudioRunner;
import org.sonicroute.android.audio.PlaybackAudioCapture;
import org.sonicroute.android.control.InputController;
import org.sonicroute.android.core.CleanupProcess;
import org.sonicroute.android.core.ServerOptions;
import org.sonicroute.android.io.PacketWriter;
import org.sonicroute.android.io.StreamSession;
import org.sonicroute.android.platform.AndroidDisplayPlatform;
import org.sonicroute.android.platform.AndroidSystemPlatform;
import org.sonicroute.android.util.ServerLog;
import org.sonicroute.android.video.H264VideoRunner;
import org.sonicroute.android.video.OwnedAppMonitor;
import org.sonicroute.android.video.PhysicalScreenSource;
import org.sonicroute.android.video.VideoCaptureSource;
import org.sonicroute.android.video.VirtualDisplaySource;

/**
 * Self-authored SonicRoute Android streaming and control server entry point replacing third-party server binaries.
 */
public final class SonicRouteServer {
    private SonicRouteServer() {
    }

    public static void main(String... args) {
        boolean preparedHere = false;
        try {
            if (Looper.getMainLooper() == null) {
                Looper.prepareMainLooper();
                preparedHere = true;
            }
        } catch (Throwable ignored) {
            // Main looper already prepared in instrumentation/test hosts.
        }

        try {
            ServerOptions options = ServerOptions.parse(args);
            ServerLog.setLevel(options.getLogLevel());
            ServerLog.i("Device: [" + Build.MANUFACTURER + "] " + Build.MODEL + " (Android " + Build.VERSION.RELEASE + ")");
            ServerLog.silenceStandardStreams();

            if (!options.isVideo() && !options.isAudio() && !options.isControl()) {
                throw new IllegalArgumentException("At least one of video, audio, or control must be enabled");
            }

            AndroidWorkarounds.apply();
            if (options.isVideo() || options.isAudio()) {
                AndroidWorkarounds.ensureMediaFrameworkInitialized();
            }

            if (preparedHere && Looper.myLooper() == Looper.getMainLooper()) {
                Thread serverThread = new Thread(() -> runServerAndExit(options), "SonicRoute-Server");
                serverThread.setDaemon(false);
                serverThread.start();
                Looper.loop();
                return;
            }
            runServerAndExit(options);
        } catch (Throwable error) {
            ServerLog.e("SonicRouteServer terminated with error", error);
            System.exit(1);
        }
    }

    private static void runServerAndExit(ServerOptions options) {
        int status = 0;
        try {
            runServer(options);
        } catch (Throwable error) {
            ServerLog.e("SonicRouteServer terminated with error", error);
            status = 1;
        } finally {
            // Force termination of non-daemon Binder / MediaCodec threads once cleanup completes.
            System.exit(status);
        }
    }

    private static void runServer(ServerOptions options) throws IOException {
        CleanupProcess cleanup = null;
        StreamSession session = null;
        VideoCaptureSource videoSource = null;
        H264VideoRunner videoRunner = null;
        OpusAudioRunner audioRunner = null;
        InputController controller = null;

        try {
            cleanup = CleanupProcess.start(options);
            session = StreamSession.open(
                    options.getSocketName(),
                    options.isTunnelForward(),
                    options.isVideo(),
                    options.isAudio(),
                    options.isControl(),
                    options.isSendDummyByte());

            if (options.isSendDeviceMeta()) {
                session.sendDeviceMeta();
            }

            if (options.isVideo()
                    && options.isPowerOn()
                    && options.getNewDisplay() == null
                    && !AndroidDisplayPlatform.isScreenOn(options.getDisplayId())) {
                AndroidSystemPlatform.pressReleaseKeycode(
                        KeyEvent.KEYCODE_POWER,
                        options.getDisplayId(),
                        AndroidSystemPlatform.INJECT_MODE_ASYNC);
            }

            CountDownLatch shutdownLatch = new CountDownLatch(1);
            AtomicInteger activeWorkers = new AtomicInteger(0);
            AtomicBoolean quitPosted = new AtomicBoolean(false);

            final CleanupProcess cleanupRef = cleanup;
            final StreamSession sessionRef = session;
            final H264VideoRunner[] videoRef = new H264VideoRunner[1];
            final OpusAudioRunner[] audioRef = new OpusAudioRunner[1];
            final InputController[] controlRef = new InputController[1];

            Runnable stopAndQuit = () -> {
                if (!quitPosted.compareAndSet(false, true)) {
                    return;
                }
                if (controlRef[0] != null) {
                    controlRef[0].stop();
                }
                if (videoRef[0] != null) {
                    videoRef[0].stop();
                }
                if (audioRef[0] != null) {
                    audioRef[0].stop();
                }
                sessionRef.shutdownStreams();
                shutdownLatch.countDown();
            };

            Runnable onCriticalFinish = () -> {
                activeWorkers.decrementAndGet();
                stopAndQuit.run();
            };

            Runnable onAudioFinish = () -> {
                if (activeWorkers.decrementAndGet() <= 0) {
                    stopAndQuit.run();
                }
            };

            if (options.isControl()) {
                controller = new InputController(session.getControlSocket(), options, cleanupRef, onCriticalFinish);
                controlRef[0] = controller;
                activeWorkers.incrementAndGet();
            }

            if (options.isAudio()) {
                PlaybackAudioCapture audioCapture = buildAudioCapture(options);
                PacketWriter audioWriter = new PacketWriter(
                        session.getAudioFd(),
                        PacketWriter.codecIdForName(options.getAudioCodec()),
                        options.isSendCodecMeta(),
                        options.isSendFrameMeta());
                audioRunner = new OpusAudioRunner(
                        audioCapture,
                        audioWriter,
                        options.getAudioCodec(),
                        options.getAudioBitRate(),
                        options.getAudioEncoder(),
                        options.getAudioCodecOptions(),
                        onAudioFinish);
                audioRef[0] = audioRunner;
                activeWorkers.incrementAndGet();
            }

            if (options.isVideo()) {
                videoSource = options.getNewDisplay() != null
                        ? new VirtualDisplaySource(options)
                        : new PhysicalScreenSource(options.getDisplayId(), options.getMaxSize());
                if (controller != null) {
                    controller.setCaptureSource(videoSource);
                }
                PacketWriter videoWriter = new PacketWriter(
                        session.getVideoFd(),
                        PacketWriter.codecIdForName(options.getVideoCodec()),
                        options.isSendCodecMeta(),
                        options.isSendFrameMeta());
                videoRunner = new H264VideoRunner(
                        videoSource,
                        videoWriter,
                        options.getVideoBitRate(),
                        options.getMaxFps(),
                        options.getVideoEncoder(),
                        options.getVideoCodecOptions(),
                        options.isDownsizeOnError(),
                        onCriticalFinish);
                videoRef[0] = videoRunner;
                activeWorkers.incrementAndGet();
            }

            if (controller != null) {
                controller.start();
            }
            if (audioRunner != null) {
                audioRunner.start();
            }
            if (videoRunner != null) {
                videoRunner.start();
            }

            try {
                shutdownLatch.await();
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            }
        } finally {
            if (controller != null) {
                controller.stop();
            }
            if (videoRunner != null) {
                videoRunner.stop();
            }
            if (audioRunner != null) {
                audioRunner.stop();
            }
            if (session != null) {
                session.shutdownStreams();
            }
            joinQuietly(controller, videoRunner, audioRunner);
            if (session != null) {
                session.close();
            }
            if (videoSource != null) {
                videoSource.release();
            }
            if (cleanup != null) {
                cleanup.close();
            }
        }
    }

    private static PlaybackAudioCapture buildAudioCapture(ServerOptions options) {
        PlaybackAudioCapture capture = new PlaybackAudioCapture(
                options.getAudioSource(),
                options.isAudioDup(),
                options.isAudioMixModePr());

        List<String> targets = new ArrayList<>();
        for (String spec : ServerOptions.splitCsv(options.getAudioTargetPackages())) {
            String clean = OwnedAppMonitor.stripLaunchPrefix(spec);
            if (clean != null && !clean.isEmpty()) {
                targets.add(clean);
            }
        }

        boolean explicitIsolation = !targets.isEmpty() || options.getAudioTargetPackages() != null;
        if (targets.isEmpty() && options.getNewDisplay() != null && options.getStartApp() != null) {
            String startPkg = OwnedAppMonitor.stripLaunchPrefix(options.getStartApp());
            if (startPkg != null && !startPkg.isEmpty()) {
                targets.add(startPkg);
                explicitIsolation = true;
            }
        }

        if (!targets.isEmpty()) {
            capture.setTargetSpecs(targets);
        }
        if (explicitIsolation) {
            capture.setStrictNoGlobal(true);
        }
        return capture;
    }

    private static void joinQuietly(InputController controller, H264VideoRunner videoRunner, OpusAudioRunner audioRunner) {
        boolean interrupted = false;
        if (controller != null) {
            try {
                controller.join();
            } catch (InterruptedException ignored) {
                interrupted = true;
            }
        }
        if (videoRunner != null) {
            try {
                videoRunner.join();
            } catch (InterruptedException ignored) {
                interrupted = true;
            }
        }
        if (audioRunner != null) {
            try {
                audioRunner.join();
            } catch (InterruptedException ignored) {
                interrupted = true;
            }
        }
        if (interrupted) {
            Thread.currentThread().interrupt();
        }
    }
}
