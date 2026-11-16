package com.macacagames.beacon;

public final class BeaconEncodedVideoBufferTest {
    private static int passed;
    private static final long SECOND = 1000000L;

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    private static void fails(Runnable action, String message) {
        try { action.run(); } catch (IllegalStateException expected) { return; }
        throw new AssertionError(message);
    }

    private static void add(BeaconEncodedVideoBuffer ring, long timeUs, boolean key, int bytes) {
        ring.append(new byte[bytes], timeUs, key ? 1 : 0);
    }

    private static void test(String name, Runnable action) {
        action.run();
        passed++;
        System.out.println("PASS " + name);
    }

    public static void main(String[] args) {
        test("five-minute game retains only incident history", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(8 * SECOND, 1000);
            for (int i = 0; i <= 300; i++) add(ring, i * SECOND, true, 10);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(292 * SECOND, 301 * SECOND);
            check(!ring.snapshot(clip), "Must wait for after-window");
            add(ring, 301 * SECOND, true, 10);
            check(ring.snapshot(clip), "Incident should be ready");
            check(clip.startUs == 292 * SECOND, "Must not start at game launch");
            check(clip.samples.size() == 9, "End boundary must not add a zero-duration sample");
            check(ring.retainedBytes() <= 100, "Cache must not grow with session length");
        });
        test("start uses preceding keyframe", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(2 * SECOND, 100);
            for (int i = 0; i <= 10; i++) add(ring, i * SECOND / 2, i % 2 == 0, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(3250000, 5 * SECOND);
            check(ring.snapshot(clip), "Ready");
            check(clip.startUs == 3 * SECOND, "Include preceding GOP");
            check(clip.samples.get(0).isKeyFrame(), "First sample must decode independently");
        });
        test("startup uses available history", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(8 * SECOND, 100);
            add(ring, 10000, true, 1);
            add(ring, SECOND, false, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(-7 * SECOND, SECOND);
            check(ring.snapshot(clip) && clip.startUs == 10000, "Startup cannot synthesize history");
        });
        test("zero after requires no later frame when already drained", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 100);
            add(ring, 0, true, 1);
            add(ring, SECOND, true, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, SECOND);
            check(ring.snapshot(clip), "Zero-after snapshot should be ready immediately");
        });
        test("10 before plus 5 after protects early samples", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(10 * SECOND, 100);
            for (int i = 0; i <= 30; i++) add(ring, i * SECOND, true, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(20 * SECOND, 35 * SECOND);
            for (int i = 31; i <= 35; i++) add(ring, i * SECOND, true, 1);
            check(ring.snapshot(clip) && clip.startUs == 20 * SECOND, "After-window must not evict before-window");
        });
        test("low FPS preserves real sample timing", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(8 * SECOND, 100);
            add(ring, 0, true, 1);
            add(ring, 3 * SECOND, false, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, 4 * SECOND);
            add(ring, 6 * SECOND, true, 1);
            check(ring.snapshot(clip), "Crossing the boundary finalizes despite low FPS");
            check(clip.samples.size() == 2 && clip.samples.get(1).timeUs == 3 * SECOND, "No fabricated timestamps");
        });
        test("consecutive incidents share ongoing history", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(8 * SECOND, 100);
            for (int i = 0; i <= 10; i++) add(ring, i * SECOND, true, 1);
            BeaconEncodedVideoBuffer.Clip first = ring.begin(2 * SECOND, 10 * SECOND);
            check(ring.snapshot(first), "First snapshot");
            ring.release(first);
            add(ring, 11 * SECOND, true, 1);
            BeaconEncodedVideoBuffer.Clip second = ring.begin(3 * SECOND, 11 * SECOND);
            check(ring.snapshot(second) && second.startUs == 3 * SECOND, "No restart after export");
        });
        test("cancel collection releases frozen history", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 100);
            add(ring, 0, true, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, 10 * SECOND);
            for (int i = 1; i <= 5; i++) add(ring, i * SECOND, true, 1);
            ring.release(clip);
            check(ring.retainedBytes() == 2, "Cancellation prunes the frozen window");
            fails(() -> ring.snapshot(clip), "Cancelled request cannot publish later");
        });
        test("snapshot pins count once and survive history eviction", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 100);
            add(ring, 0, true, 10);
            add(ring, SECOND, true, 10);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, SECOND);
            check(ring.snapshot(clip), "Ready");
            check(ring.retainedBytes() == 20, "Shared snapshot must not double-count bytes");
            add(ring, 2 * SECOND, true, 10);
            add(ring, 3 * SECOND, true, 10);
            check(ring.retainedBytes() == 30, "Evicted samples remain charged while exporting");
            ring.release(clip);
            check(ring.retainedBytes() == 20, "Exporter release returns pinned budget");
            ring.release(clip);
            check(ring.retainedBytes() == 20, "Release is idempotent");
        });
        test("cache pressure fails instead of truncating protected incident", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 25);
            add(ring, 0, true, 10);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, 5 * SECOND);
            add(ring, SECOND, true, 10);
            fails(() -> add(ring, 2 * SECOND, true, 10), "Must reject overflow");
            check(ring.retainedBytes() <= 25, "Hard retained-byte bound");
            ring.release(clip);
            add(ring, 2 * SECOND, true, 10);
            check(ring.retainedBytes() == 20, "Cancelled incident frees budget");
        });
        test("pinned exporter participates in capacity limit", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(0, 25);
            add(ring, 0, true, 10);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, 0);
            check(ring.snapshot(clip), "Ready");
            add(ring, SECOND, true, 10);
            // With zero history, the new keyframe evicts its predecessor only
            // after append. Budget still includes the exporting first GOP.
            fails(() -> add(ring, 2 * SECOND, true, 20), "Exporter pins must be included");
            ring.release(clip);
        });
        test("busy request cannot replace original", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 100);
            add(ring, 0, true, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, 0);
            fails(() -> ring.begin(0, 0), "Duplicate request rejected");
            check(ring.snapshot(clip), "Original remains intact");
        });
        test("missing keyframe fails and never emits corrupt video", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 100);
            add(ring, 0, false, 1);
            add(ring, SECOND, false, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, SECOND);
            fails(() -> ring.snapshot(clip), "Cannot start from predictive frame");
            check(ring.retainedBytes() == 0, "Undecodable leading data is discarded");
        });
        test("out-of-order timestamps fail rather than accelerate playback", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 100);
            add(ring, SECOND, true, 1);
            fails(() -> add(ring, 0, false, 1), "No invented timeline");
            fails(() -> add(ring, SECOND, false, 1), "Duplicate timestamp rejected");
        });
        test("cleanup retains exporter pins until worker releases", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 100);
            add(ring, 0, true, 10);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(0, 0);
            check(ring.snapshot(clip), "Ready");
            ring.clearHistory();
            check(ring.retainedBytes() == 10, "Worker still owns the bytes");
            ring.release(clip);
            check(ring.retainedBytes() == 0, "All ownership released");
        });
        test("missing requested history is reported instead of using a later keyframe", () -> {
            BeaconEncodedVideoBuffer ring = new BeaconEncodedVideoBuffer(SECOND, 100);
            for (int i = 0; i <= 10; i++) add(ring, i * SECOND, true, 1);
            BeaconEncodedVideoBuffer.Clip clip = ring.begin(2 * SECOND, 10 * SECOND);
            fails(() -> ring.snapshot(clip), "Cannot silently omit the requested event history");
        });
        System.out.println(passed + " tests passed");
    }
}
