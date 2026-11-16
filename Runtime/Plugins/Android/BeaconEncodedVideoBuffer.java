package com.macacagames.beacon;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Android-independent, keyframe-aware compressed history. No codec buffers are retained. */
final class BeaconEncodedVideoBuffer {
    static final int KEY_FRAME = 1;

    static final class Sample {
        final byte[] data;
        final long timeUs;
        final int flags;
        private int references = 1;

        Sample(byte[] data, long timeUs, int flags) {
            this.data = data;
            this.timeUs = timeUs;
            this.flags = flags;
        }

        boolean isKeyFrame() { return (flags & KEY_FRAME) != 0; }
    }

    static final class Clip {
        final long requestedStartUs;
        final long endUs;
        List<Sample> samples;
        long startUs;

        Clip(long startUs, long endUs) {
            this.requestedStartUs = startUs;
            this.endUs = endUs;
        }
    }

    private final long historyUs;
    private final long maximumBytes;
    private final ArrayDeque<Sample> history = new ArrayDeque<>();
    private long retainedBytes;
    private long latestUs = -1;
    private long firstKeyUs = -1;
    private Clip active;

    BeaconEncodedVideoBuffer(long historyUs, long maximumBytes) {
        if (historyUs < 0 || maximumBytes <= 0) throw new IllegalArgumentException("Invalid video cache limits.");
        this.historyUs = historyUs;
        this.maximumBytes = maximumBytes;
    }

    synchronized void checkCapacity(int bytes, long timeUs) {
        if (timeUs < 0 || timeUs <= latestUs)
            throw new IllegalStateException("Encoder timestamps are not strictly increasing.");
        prune(timeUs);
        if (bytes <= 0 || bytes > maximumBytes - retainedBytes)
            throw new IllegalStateException("Compressed video cache limit exceeded; the complete incident cannot be retained.");
    }

    synchronized void append(byte[] data, long timeUs, int flags) {
        checkCapacity(data.length, timeUs);
        latestUs = timeUs;
        if (firstKeyUs < 0 && (flags & KEY_FRAME) != 0) firstKeyUs = timeUs;
        // Samples preceding the first keyframe can never form a playable clip.
        if (history.isEmpty() && (flags & KEY_FRAME) == 0) return;
        history.addLast(new Sample(data, timeUs, flags));
        retainedBytes += data.length;
        prune(timeUs);
    }

    synchronized Clip begin(long startUs, long endUs) {
        if (active != null) throw new IllegalStateException("An incident video is already pending.");
        if (endUs < startUs) throw new IllegalArgumentException("Invalid incident window.");
        active = new Clip(Math.max(0, startUs), Math.max(0, endUs));
        return active;
    }

    /** Pins a shared snapshot once the encoder has passed the incident end. */
    synchronized boolean snapshot(Clip clip) {
        if (clip != active) throw new IllegalStateException("Incident was cancelled.");
        if (clip.samples != null) return true;
        if (latestUs < clip.endUs) return false;
        Sample first = null;
        for (Sample sample : history) {
            if (sample.timeUs > clip.endUs) break;
            if (sample.isKeyFrame() && sample.timeUs <= clip.requestedStartUs) first = sample;
        }
        // Only startup may have less history than requested. Never silently
        // substitute a later keyframe for history lost through cache pressure.
        if (first == null && !history.isEmpty() && clip.requestedStartUs <= firstKeyUs
                && history.peekFirst().timeUs == firstKeyUs && firstKeyUs <= clip.endUs)
            first = history.peekFirst();
        if (first == null) throw new IllegalStateException("No keyframe covers the requested incident window.");
        List<Sample> selected = new ArrayList<>();
        for (Sample sample : history) {
            if (sample.timeUs >= first.timeUs && (sample.timeUs < clip.endUs || sample == first)) {
                sample.references++;
                selected.add(sample);
            }
        }
        clip.startUs = first.timeUs;
        clip.samples = Collections.unmodifiableList(selected);
        prune(latestUs);
        return true;
    }

    synchronized void release(Clip clip) {
        if (clip != active) return;
        if (clip.samples != null) {
            for (Sample sample : clip.samples) releaseSample(sample);
            clip.samples = null;
        }
        active = null;
        prune(latestUs);
    }

    synchronized long retainedBytes() { return retainedBytes; }

    synchronized void clearHistory() {
        while (!history.isEmpty()) releaseSample(history.removeFirst());
    }

    private void prune(long nowUs) {
        long cutoff = nowUs - historyUs;
        if (active != null && active.samples == null) cutoff = Math.min(cutoff, active.requestedStartUs);
        Sample keep = null;
        for (Sample sample : history) {
            if (sample.timeUs > cutoff) break;
            if (sample.isKeyFrame()) keep = sample;
        }
        if (keep != null)
            while (history.peekFirst() != keep) releaseSample(history.removeFirst());
    }

    private void releaseSample(Sample sample) {
        if (--sample.references == 0) retainedBytes -= sample.data.length;
    }
}
