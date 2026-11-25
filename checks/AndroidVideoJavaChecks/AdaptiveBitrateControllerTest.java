// SPDX-License-Identifier: GPL-3.0-only
import java.lang.reflect.Method;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;
import org.sonicroute.android.SonicRouteServer;
import org.sonicroute.android.core.ServerOptions;
import org.sonicroute.android.io.PacketWriter;
import org.sonicroute.android.video.AdaptiveBitrateController;
import org.sonicroute.android.video.AdaptiveBitrateController.TransportMode;

/**
 * Pure-JVM unit tests for {@link AdaptiveBitrateController}, {@link ServerOptions} ABR flags,
 * {@link SonicRouteServer#createAdaptiveBitrateController(ServerOptions)}, and {@link PacketWriter} metrics API.
 */
public final class AdaptiveBitrateControllerTest {
    private static final long MS = 1_000_000L;

    private static int passedAssertions;

    private AdaptiveBitrateControllerTest() {
    }

    public static void main(String[] args) throws Exception {
        testServerOptionsAndFactory();
        testPacketWriterMetricsApi();
        testCongestionDownshiftAndCooldown();
        testEmergencyDownshiftThresholds();
        testSustainedEmergencyDownshifts();
        testRecoveryAndBaselineCapWithPeriodicKeyframes();
        testWifiRecoveryTimingAndStepSize();
        testThroughputGuidedDownshift();
        testSmallDownshiftsReachMinimumBitrate();
        testFpsRangeCompatibility();
        testHysteresisAndDeadbandNoOscillation();
        testUsbVsWifiDifferentiationAndFloors();
        testDriverRejectionFallbackToFixedBitrate();
        testDisabledAbrMode();
        System.out.println("PASS AdaptiveBitrateControllerTest: " + passedAssertions + " assertions verified.");
    }

    private static void testServerOptionsAndFactory() {
        ServerOptions defaults = ServerOptions.parse("3.3.4", "video_bit_rate=8000000");
        assertTrue(!defaults.isWifiTransport(), "default video_transport should be usb");
        assertTrue(defaults.isVideoAbr(), "default video_abr should be true");

        AdaptiveBitrateController defaultCtrl = SonicRouteServer.createAdaptiveBitrateController(defaults);
        assertEquals(TransportMode.USB, defaultCtrl.getTransportMode(), "factory maps default transport to USB");
        assertTrue(defaultCtrl.isEnabled(), "factory preserves default ABR enabled");

        ServerOptions wifiDisabled = ServerOptions.parse(
                "3.3.4",
                "video_bit_rate=6000000",
                "video_transport=wifi",
                "video_abr=false");
        assertTrue(wifiDisabled.isWifiTransport(), "video_transport=wifi should enable wifi mode");
        assertTrue(!wifiDisabled.isVideoAbr(), "video_abr=false should disable ABR");

        AdaptiveBitrateController wifiCtrl = SonicRouteServer.createAdaptiveBitrateController(wifiDisabled);
        assertEquals(TransportMode.WIFI, wifiCtrl.getTransportMode(), "factory maps wifi transport");
        assertTrue(!wifiCtrl.isEnabled(), "factory maps disabled ABR flag");

        boolean rejectedInvalid = false;
        try {
            ServerOptions.parse("video_transport=bluetooth");
        } catch (IllegalArgumentException expected) {
            rejectedInvalid = true;
        }
        assertTrue(rejectedInvalid, "unsupported video_transport must throw IllegalArgumentException");
    }

    private static void testPacketWriterMetricsApi() throws NoSuchMethodException {
        Method writePacketRaw = PacketWriter.class.getMethod(
                "writePacket", ByteBuffer.class, long.class, boolean.class, boolean.class);
        Method lastDuration = PacketWriter.class.getMethod("getLastWriteDurationNs");
        Method lastBytes = PacketWriter.class.getMethod("getLastWriteBytes");
        Method totalBytes = PacketWriter.class.getMethod("getTotalBytesWritten");

        assertEquals(void.class, writePacketRaw.getReturnType(), "existing writePacket signature preserved");
        assertEquals(long.class, lastDuration.getReturnType(), "getLastWriteDurationNs returns long");
        assertEquals(int.class, lastBytes.getReturnType(), "getLastWriteBytes returns int");
        assertEquals(long.class, totalBytes.getReturnType(), "getTotalBytesWritten returns long");
    }

    private static void testCongestionDownshiftAndCooldown() {
        AdaptiveBitrateController usb = new AdaptiveBitrateController(TransportMode.USB, true);
        List<Integer> applied = new ArrayList<Integer>();
        usb.resetSession(8_000_000, 100_000, 50_000_000, 60.0f, bps -> {
            applied.add(bps);
            return true;
        });

        // Initial IDR frame should not falsely downshift on packet #1.
        usb.onPacketSent(45_000, 18 * MS, 20_000L, true, false, 0L);
        assertEquals(0, applied.size(), "initial keyframe alone must not downshift");

        // Sustained moderate write blocking + high frame age over 60fps frames.
        long nowNs = 16 * MS;
        for (int i = 0; i < 6; ++i) {
            usb.onPacketSent(18_000, 15 * MS, 75_000L, false, false, nowNs);
            nowNs += 16 * MS;
        }
        assertEquals(1, applied.size(), "USB should downshift once after trend forms");
        assertEquals(7_200_000, applied.get(0).intValue(), "USB normal downshift should apply 0.90x");
        assertEquals(7_200_000, usb.getCurrentBitRateBps(), "current bitrate reflects applied value");
        assertEquals(1, usb.getDownshiftCount(), "downshift counter increments");
        assertEquals(1, usb.getAdjustmentCount(), "adjustment counter increments");

        // Continue high pressure inside 600ms cooldown -> must not downshift again yet.
        for (int i = 0; i < 20; ++i) {
            usb.onPacketSent(18_000, 15 * MS, 75_000L, false, false, nowNs);
            nowNs += 16 * MS;
        }
        assertEquals(1, applied.size(), "USB must respect 600ms normal downshift cooldown");

        // Advance past 600ms cooldown and trigger second downshift.
        nowNs += 300 * MS;
        usb.onPacketSent(18_000, 15 * MS, 75_000L, false, false, nowNs);
        assertEquals(2, applied.size(), "USB should downshift again once 600ms cooldown elapses");
        assertEquals(6_480_000, applied.get(1).intValue(), "second USB downshift steps down by 0.90x");
    }

    private static void testEmergencyDownshiftThresholds() {
        // USB emergency: single packet write > 120ms (or frameAge > 120ms), cooldown 250ms.
        AdaptiveBitrateController usb = new AdaptiveBitrateController(TransportMode.USB, true);
        List<Integer> usbApplied = new ArrayList<Integer>();
        usb.resetSession(8_000_000, 100_000, 50_000_000, 30.0f, bps -> {
            usbApplied.add(bps);
            return true;
        });

        usb.onPacketSent(120_000, 120 * MS, 120_000L, false, false, 0L);
        assertEquals(0, usbApplied.size(), "USB emergency thresholds are strict");
        usb.onPacketSent(120_000, 125 * MS, 30_000L, false, false, 130 * MS);
        assertEquals(1, usbApplied.size(), "USB >120ms write stall triggers immediate emergency downshift");
        assertEquals(6_400_000, usbApplied.get(0).intValue(), "USB emergency factor is 0.80x with sufficient throughput");

        // Within 250ms emergency cooldown, should not fire again.
        usb.onPacketSent(120_000, 140 * MS, 130_000L, false, false, 379 * MS);
        assertEquals(1, usbApplied.size(), "USB emergency respects 250ms cooldown");

        // After 250ms cooldown, frameAge > 120ms triggers immediate emergency downshift even before 600ms.
        usb.onPacketSent(40_000, 5 * MS, 130_000L, false, false, 380 * MS);
        assertEquals(2, usbApplied.size(), "USB frameAge >120ms triggers emergency downshift at 250ms");
        assertEquals(5_120_000, usbApplied.get(1).intValue(), "USB frame-age emergency applies the 0.80x factor");

        // Wi-Fi emergency: > 90ms triggers immediately.
        AdaptiveBitrateController wifi = new AdaptiveBitrateController(TransportMode.WIFI, true);
        List<Integer> wifiApplied = new ArrayList<Integer>();
        wifi.resetSession(8_000_000, 100_000, 50_000_000, 30.0f, bps -> {
            wifiApplied.add(bps);
            return true;
        });

        wifi.onPacketSent(100_000, 90 * MS, 90_000L, false, false, 0L);
        assertEquals(0, wifiApplied.size(), "Wi-Fi emergency thresholds are strict");
        wifi.onPacketSent(100_000, 95 * MS, 20_000L, false, false, 100 * MS);
        assertEquals(1, wifiApplied.size(), "Wi-Fi >90ms write stall triggers immediate emergency downshift");
        assertEquals(6_000_000, wifiApplied.get(0).intValue(), "Wi-Fi emergency factor is 0.75x with sufficient throughput");
        wifi.onPacketSent(100_000, 95 * MS, 20_000L, false, false, 299 * MS);
        assertEquals(1, wifiApplied.size(), "Wi-Fi emergency respects 200ms cooldown");
        wifi.onPacketSent(40_000, 5 * MS, 95_000L, false, false, 300 * MS);
        assertEquals(2, wifiApplied.size(), "Wi-Fi frameAge >90ms triggers emergency downshift at 200ms");
        assertEquals(4_500_000, wifiApplied.get(1).intValue(), "Wi-Fi frame-age emergency applies the 0.75x factor");
    }

    private static void testRecoveryAndBaselineCapWithPeriodicKeyframes() {
        AdaptiveBitrateController usb = new AdaptiveBitrateController(TransportMode.USB, true);
        List<Integer> applied = new ArrayList<Integer>();
        usb.resetSession(8_000_000, 100_000, 50_000_000, 60.0f, bps -> {
            applied.add(bps);
            return true;
        });

        // Apply two normal drops so recovery can exercise both the +12% step and the baseline cap.
        long nowNs = 0L;
        for (int i = 0; i < 6; ++i) {
            usb.onPacketSent(20_000, 14 * MS, 75_000L, false, false, nowNs);
            nowNs += 16 * MS;
        }
        nowNs += 600 * MS;
        usb.onPacketSent(20_000, 14 * MS, 75_000L, false, false, nowNs);
        nowNs += 16 * MS;
        assertEquals(6_480_000, usb.getCurrentBitRateBps(), "precondition: two USB drops reach 6.48 Mbps");
        int drops = applied.size();

        // Now stream healthy packets (1ms write, 10ms frame age) for 15 seconds, with a normal 25ms IDR every 2s.
        for (int step = 0; step < 900; ++step) {
            boolean isIdr = (step > 0 && step % 120 == 0);
            long writeNs = isIdr ? 25 * MS : 1 * MS;
            int bytes = isIdr ? 90_000 : 8_000;
            usb.onPacketSent(bytes, writeNs, 10_000L, isIdr, false, nowNs);
            nowNs += 16 * MS;
            if (step == 149) {
                assertEquals(drops, applied.size(), "USB must wait >=2500ms low pressure before upshift");
            }
        }

        assertEquals(2, usb.getUpshiftCount(), "USB recovers over two >=2.5s stability windows");
        assertEquals(7_257_600, applied.get(drops).intValue(), "USB recovery step is +12% before reaching baseline");
        assertEquals(8_000_000, usb.getCurrentBitRateBps(), "recovered bitrate caps cleanly at user baseline");
        for (int bps : applied) {
            assertTrue(bps <= 8_000_000, "applied bitrate never exceeds user baseline: " + bps);
        }
    }

    private static void testSustainedEmergencyDownshifts() {
        assertEmergencyDownshiftsReachMinimum(TransportMode.USB, 250 * MS,
                new int[] {5_600_000, 3_920_000, 2_744_000, 2_000_000});
        assertEmergencyDownshiftsReachMinimum(TransportMode.WIFI, 200 * MS,
                new int[] {4_800_000, 2_880_000, 1_728_000, 1_200_000});
    }

    private static void assertEmergencyDownshiftsReachMinimum(TransportMode mode, long cooldownNs, int[] expectedSteps) {
        AdaptiveBitrateController controller = new AdaptiveBitrateController(mode, true);
        controller.resetSession(8_000_000, 100_000, 50_000_000, 60.0f, bps -> true);

        // Very low measured throughput hits the single-step floor until the session minimum takes over.
        long nowNs = 0L;
        for (int expected : expectedSteps) {
            controller.onPacketSent(10_000, 130 * MS, 130_000L, false, false, nowNs);
            assertEquals(expected, controller.getCurrentBitRateBps(), mode + " steps down at its emergency cooldown");
            nowNs += cooldownNs;
        }
        controller.onPacketSent(10_000, 130 * MS, 130_000L, false, false, nowNs);
        assertEquals(expectedSteps.length, controller.getDownshiftCount(), mode + " stops at its floor under emergency pressure");
    }

    private static void testWifiRecoveryTimingAndStepSize() {
        AdaptiveBitrateController wifi = new AdaptiveBitrateController(TransportMode.WIFI, true);
        List<Integer> applied = new ArrayList<Integer>();
        wifi.resetSession(10_000_000, 100_000, 50_000_000, 60.0f, bps -> {
            applied.add(bps);
            return true;
        });

        long nowNs = 0L;
        for (int i = 0; i < 6; ++i) {
            wifi.onPacketSent(25_000, 12 * MS, 60_000L, false, false, nowNs);
            nowNs += 16 * MS;
        }
        assertEquals(8_500_000, wifi.getCurrentBitRateBps(), "Wi-Fi drops 10Mbps -> 8.5Mbps");
        int countAfterDrop = applied.size();

        // Feed low pressure for ~3200ms (< 3500ms required for Wi-Fi) -> must NOT upshift yet.
        for (int i = 0; i < 200; ++i) {
            wifi.onPacketSent(10_000, 1 * MS, 10_000L, false, false, nowNs);
            nowNs += 16 * MS;
        }
        assertEquals(countAfterDrop, applied.size(), "Wi-Fi must wait >=3500ms low pressure before upshift");

        // Feed another 600ms of low pressure (crosses 3500ms) -> single +8% upshift.
        for (int i = 0; i < 40; ++i) {
            wifi.onPacketSent(10_000, 1 * MS, 10_000L, false, false, nowNs);
            nowNs += 16 * MS;
        }
        assertEquals(countAfterDrop + 1, applied.size(), "Wi-Fi should upshift once after >=3500ms");
        assertEquals(9_180_000, wifi.getCurrentBitRateBps(), "Wi-Fi recovery step is +8% (8.5M * 1.08 = 9.18M)");
    }

    private static void testThroughputGuidedDownshift() {
        AdaptiveBitrateController wifi = new AdaptiveBitrateController(TransportMode.WIFI, true);
        wifi.resetSession(10_000_000, 100_000, 50_000_000, 30.0f, bps -> true);

        // 40 ms writes form a normal congestion trend. Throughput safety target is 7.2M * 0.85.
        for (int i = 0; i < 3; ++i) {
            wifi.onPacketSent(36_000, 40 * MS, 45_000L, false, false, i * 45 * MS);
        }
        assertEquals(6_120_000, wifi.getCurrentBitRateBps(), "normal Wi-Fi congestion can use throughput within the step limit");

        // Lower throughput produces a 1.7 Mbps target, below the permitted single-step floor.
        wifi.resetSession(10_000_000, 100_000, 50_000_000, 30.0f, bps -> true);
        for (int i = 0; i < 3; ++i) {
            wifi.onPacketSent(10_000, 40 * MS, 45_000L, false, false, i * 45 * MS);
        }
        assertEquals(6_000_000, wifi.getCurrentBitRateBps(), "normal Wi-Fi throughput-guided drop clamps to 0.60x");
        wifi.onPacketSent(10_000, 40 * MS, 45_000L, false, false, 540 * MS);
        assertEquals(3_600_000, wifi.getCurrentBitRateBps(), "next Wi-Fi drop is bounded relative to the current bitrate");
    }

    private static void testSmallDownshiftsReachMinimumBitrate() {
        // Frame-age pressure with fast writes keeps throughput guidance out of the downshift.
        assertSmallDownshiftsReachMinimum(TransportMode.USB, 800_000, 600 * MS, 75_000L,
                new int[] {720_000, 648_000, 583_200, 524_880, 500_000});
        assertSmallDownshiftsReachMinimum(TransportMode.WIFI, 600_000, 450 * MS, 60_000L,
                new int[] {510_000, 433_500, 400_000});
    }

    private static void assertSmallDownshiftsReachMinimum(
            TransportMode mode, int baseline, long cooldownNs, long frameAgeUs, int[] expectedSteps) {
        AdaptiveBitrateController controller = new AdaptiveBitrateController(mode, true);
        controller.resetSession(baseline, 100_000, 50_000_000, 60.0f, bps -> true);

        controller.onPacketSent(2_000, MS, frameAgeUs, false, false, 0L);
        controller.onPacketSent(2_000, MS, frameAgeUs, false, false, 16 * MS);
        long nowNs = 32 * MS;
        for (int expected : expectedSteps) {
            controller.onPacketSent(2_000, MS, frameAgeUs, false, false, nowNs);
            assertEquals(expected, controller.getCurrentBitRateBps(), mode + " applies sub-100kbps downshift");
            nowNs += cooldownNs;
        }
        controller.onPacketSent(2_000, MS, frameAgeUs, false, false, nowNs);
        assertEquals(expectedSteps.length, controller.getDownshiftCount(), mode + " stops adjusting at its minimum bitrate");
    }

    private static void testFpsRangeCompatibility() {
        // Verify 120fps healthy stream (1.5ms write) and 15fps healthy stream (6ms write) stay stable.
        AdaptiveBitrateController usb120 = new AdaptiveBitrateController(TransportMode.USB, true);
        AtomicInteger calls120 = new AtomicInteger();
        usb120.resetSession(16_000_000, 100_000, 50_000_000, 120.0f, bps -> {
            calls120.incrementAndGet();
            return true;
        });
        long nowNs = 0L;
        for (int i = 0; i < 120; ++i) {
            usb120.onPacketSent(16_000, 1_500_000L, 12_000L, i == 0, false, nowNs);
            nowNs += 8_333_333L;
        }
        assertEquals(0, calls120.get(), "healthy 120fps stream must not falsely downshift");

        // Sustained 6ms writes at 120fps (72% frame budget) should trigger downshift.
        for (int i = 0; i < 10; ++i) {
            usb120.onPacketSent(16_000, 6_500_000L, 40_000L, false, false, nowNs);
            nowNs += 8_333_333L;
        }
        assertTrue(usb120.getDownshiftCount() >= 1, "sustained 120fps write saturation triggers downshift");

        // 15fps stream with 8ms normal write (12% frame budget) must stay stable.
        AdaptiveBitrateController usb15 = new AdaptiveBitrateController(TransportMode.USB, true);
        AtomicInteger calls15 = new AtomicInteger();
        usb15.resetSession(4_000_000, 100_000, 50_000_000, 15.0f, bps -> {
            calls15.incrementAndGet();
            return true;
        });
        nowNs = 0L;
        for (int i = 0; i < 30; ++i) {
            usb15.onPacketSent(30_000, 8 * MS, 20_000L, i == 0, false, nowNs);
            nowNs += 66_666_667L;
        }
        assertEquals(0, calls15.get(), "healthy 15fps stream must not falsely downshift");

        // Verify resetSession restores dynamicSupported after a previous session rejection.
        usb15.resetSession(4_000_000, 100_000, 50_000_000, 30.0f, bps -> false);
        usb15.onPacketSent(30_000, 130 * MS, 130_000L, false, false, 0L);
        assertTrue(!usb15.isDynamicSupported(), "rejected session marks dynamicSupported false");
        usb15.resetSession(4_000_000, 100_000, 50_000_000, 30.0f, bps -> true);
        assertTrue(usb15.isDynamicSupported(), "new session reset restores dynamicSupported");
        assertEquals(0, usb15.getAdjustmentCount(), "new session reset clears adjustment count");
    }

    private static void testHysteresisAndDeadbandNoOscillation() {
        AdaptiveBitrateController usb = new AdaptiveBitrateController(TransportMode.USB, true);
        AtomicInteger calls = new AtomicInteger();
        usb.resetSession(8_000_000, 100_000, 50_000_000, 60.0f, bps -> {
            calls.incrementAndGet();
            return true;
        });

        // Minor fluctuation inside hysteresis zone (writeRatio ~0.30..0.42, frameAge 35..48ms).
        long nowNs = 0L;
        for (int i = 0; i < 300; ++i) {
            long writeNs = (i % 2 == 0) ? (5 * MS) : (7 * MS);
            long ageUs = (i % 2 == 0) ? 35_000L : 48_000L;
            usb.onPacketSent(10_000, writeNs, ageUs, false, false, nowNs);
            nowNs += 16 * MS;
        }
        assertEquals(0, calls.get(), "hysteresis zone fluctuations must not trigger adjustments");

        // Config packets with arbitrary timing must also be ignored.
        usb.onPacketSent(48, 200 * MS, 500_000L, false, true, nowNs);
        assertEquals(0, calls.get(), "config packets must not trigger bitrate changes");

        // Explicit deadband check: < max(100_000, current * 5%) suppressed except at boundaries.
        assertTrue(!AdaptiveBitrateController.shouldApplyBitrateDelta(8_000_000, 7_700_000, 2_000_000, 8_000_000),
                "300kbps delta at 8Mbps (<5% = 400kbps) must be suppressed by deadband");
        assertTrue(AdaptiveBitrateController.shouldApplyBitrateDelta(8_000_000, 7_600_000, 2_000_000, 8_000_000),
                "400kbps delta at 8Mbps (>=5%) must pass deadband");
        assertTrue(AdaptiveBitrateController.shouldApplyBitrateDelta(7_900_000, 8_000_000, 2_000_000, 8_000_000),
                "small delta reaching max boundary must pass deadband");
        assertTrue(AdaptiveBitrateController.shouldApplyBitrateDelta(2_050_000, 2_000_000, 2_000_000, 8_000_000),
                "small delta reaching min boundary must pass deadband");
    }

    private static void testUsbVsWifiDifferentiationAndFloors() {
        AdaptiveBitrateController usb = new AdaptiveBitrateController(TransportMode.USB, true);
        AdaptiveBitrateController wifi = new AdaptiveBitrateController(TransportMode.WIFI, true);

        List<Integer> usbSteps = new ArrayList<Integer>();
        List<Integer> wifiSteps = new ArrayList<Integer>();
        usb.resetSession(8_000_000, 100_000, 50_000_000, 60.0f, bps -> {
            usbSteps.add(bps);
            return true;
        });
        wifi.resetSession(8_000_000, 100_000, 50_000_000, 60.0f, bps -> {
            wifiSteps.add(bps);
            return true;
        });

        assertEquals(2_000_000, usb.getMinBitRateBps(), "USB floor at 8Mbps is 25% = 2Mbps");
        assertEquals(1_200_000, wifi.getMinBitRateBps(), "Wi-Fi floor at 8Mbps is 15% = 1.2Mbps");

        // Feed identical congestion (high frame age + moderate write duration, keeping throughput high so pure factor shows).
        long nowNs = 0L;
        for (int i = 0; i < 6; ++i) {
            usb.onPacketSent(25_000, 10 * MS, 70_000L, false, false, nowNs);
            wifi.onPacketSent(25_000, 10 * MS, 70_000L, false, false, nowNs);
            nowNs += 16 * MS;
        }
        assertEquals(7_200_000, usbSteps.get(0).intValue(), "USB first downshift = 0.90x");
        assertEquals(6_800_000, wifiSteps.get(0).intValue(), "Wi-Fi first downshift = 0.85x");

        // Check absolute floor clamp for smaller baselines: 1.5Mbps USB -> 500kbps, 2Mbps Wi-Fi -> 400kbps.
        usb.resetSession(1_500_000, 100_000, 50_000_000, 30.0f, bps -> true);
        wifi.resetSession(2_000_000, 100_000, 50_000_000, 30.0f, bps -> true);
        assertEquals(500_000, usb.getMinBitRateBps(), "USB minimum floor is 500kbps");
        assertEquals(400_000, wifi.getMinBitRateBps(), "Wi-Fi minimum floor is 400kbps");

        // Baseline below absolute floor must never raise bitrate above user baseline.
        usb.resetSession(300_000, 100_000, 50_000_000, 30.0f, bps -> true);
        assertEquals(300_000, usb.getMinBitRateBps(), "floor clamps to baseline when user selects <500kbps");
        assertEquals(300_000, usb.getMaxBitRateBps(), "ceiling equals user baseline");
    }

    private static void testDriverRejectionFallbackToFixedBitrate() {
        AdaptiveBitrateController wifi = new AdaptiveBitrateController(TransportMode.WIFI, true);
        AtomicInteger attempts = new AtomicInteger();
        AtomicInteger attemptedBitrate = new AtomicInteger();
        wifi.resetSession(8_000_000, 100_000, 50_000_000, 60.0f, bps -> {
            attempts.incrementAndGet();
            attemptedBitrate.set(bps);
            return false; // Simulate encoder driver rejecting runtime bitrate change.
        });

        long nowNs = 0L;
        for (int i = 0; i < 40; ++i) {
            wifi.onPacketSent(20_000, 100 * MS, 100_000L, false, false, nowNs);
            nowNs += 500 * MS;
        }

        assertEquals(1, attempts.get(), "applier must stop being called immediately after returning false");
        assertEquals(4_800_000, attemptedBitrate.get(), "encoder is offered the bounded Wi-Fi emergency bitrate");
        assertTrue(!wifi.isDynamicSupported(), "dynamicSupported must become false after rejection");
        assertEquals(8_000_000, wifi.getCurrentBitRateBps(), "bitrate must fall back to fixed baseline");
        assertEquals(0, wifi.getAdjustmentCount(), "rejected call must not count as successful adjustment");
    }

    private static void testDisabledAbrMode() {
        AdaptiveBitrateController disabled = new AdaptiveBitrateController(TransportMode.WIFI, false);
        AtomicInteger calls = new AtomicInteger();
        disabled.resetSession(12_000_000, 100_000, 50_000_000, 60.0f, bps -> {
            calls.incrementAndGet();
            return true;
        });

        long nowNs = 0L;
        for (int i = 0; i < 50; ++i) {
            disabled.onPacketSent(30_000, 150 * MS, 200_000L, false, false, nowNs);
            nowNs += 500 * MS;
        }

        assertTrue(!disabled.isEnabled(), "disabled ABR reports isEnabled == false");
        assertEquals(0, calls.get(), "disabled ABR must never invoke applier");
        assertEquals(12_000_000, disabled.getCurrentBitRateBps(), "disabled ABR preserves user baseline");
        assertEquals(0, disabled.getAdjustmentCount(), "disabled ABR records zero adjustments");
    }

    private static void assertTrue(boolean condition, String message) {
        if (!condition) {
            throw new AssertionError("Assertion failed: " + message);
        }
        passedAssertions++;
    }

    private static void assertEquals(int expected, int actual, String message) {
        if (expected != actual) {
            throw new AssertionError(message + " (expected=" + expected + ", actual=" + actual + ")");
        }
        passedAssertions++;
    }

    private static void assertEquals(Object expected, Object actual, String message) {
        if (expected == null ? actual != null : !expected.equals(actual)) {
            throw new AssertionError(message + " (expected=" + expected + ", actual=" + actual + ")");
        }
        passedAssertions++;
    }
}
