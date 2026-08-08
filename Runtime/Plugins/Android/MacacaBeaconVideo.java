package com.macacagames.beacon;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.content.Context;
import android.content.pm.PackageInfo;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.os.Build;
import android.view.Surface;
import android.util.Log;

import java.io.IOException;
import java.io.File;
import java.io.FileInputStream;
import java.nio.ByteBuffer;
import java.util.HashMap;
import java.util.Map;

public final class MacacaBeaconVideo {
    private static final boolean nativeVideoLoaded;
    static {
        boolean loaded = false;
        try {
            System.loadLibrary("MacacaBeaconAndroidVideo");
            loaded = true;
        } catch (Throwable throwable) {
            Log.w("MacacaBeacon", "Android GPU video plugin is unavailable", throwable);
        }
        nativeVideoLoaded = loaded;
    }

    private static native int nativeRegisterSurface(long id, Surface surface);
    private static native void nativeUnregisterSurface(long id);
    private static native void nativeWaitForIdle(long id);
    private static final Map<Long, Session> sessions = new HashMap<>();
    private static final Map<Long, EncodeJob> jobs = new HashMap<>();
    private static final Map<Long, SurfaceSession> surfaceSessions = new HashMap<>();
    private static String lastCreateFailure = "";
    private static long nextId = 1;
    private static long nextJobId = 1;
    private static long nextSurfaceId = 1;

    private MacacaBeaconVideo() { }

    public static synchronized int isAvailable() {
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP ? 1 : 0;
    }

    public static int getVersionCode() {
        try {
            Class<?> activityThread = Class.forName("android.app.ActivityThread");
            Context application = (Context)activityThread.getMethod("currentApplication").invoke(null);
            if (application == null) return -1;
            PackageInfo packageInfo = application.getPackageManager().getPackageInfo(application.getPackageName(), 0);
            if (Build.VERSION.SDK_INT >= 28)
                return (int)packageInfo.getLongVersionCode();
            return packageInfo.versionCode;
        } catch (Exception exception) {
            Log.w("MacacaBeacon", "Could not read Android version code", exception);
            return -1;
        }
    }

    public static synchronized long create(String path, int width, int height, int fps, int bitrate) {
        try {
            Session session = new Session(path, width, height, fps, bitrate);
            long id = nextId++;
            sessions.put(id, session);
            return id;
        } catch (Exception exception) {
            lastCreateFailure = exception.getClass().getSimpleName() + ": " + exception.getMessage();
            Log.e("MacacaBeacon", "Could not create Android H.264 encoder", exception);
            return 0;
        }
    }

    public static synchronized String lastCreateError() {
        return lastCreateFailure;
    }

    public static synchronized int addJpeg(long id, byte[] jpeg, int length, double presentationSeconds) {
        Session session = sessions.get(id);
        return session == null ? 0 : session.addJpeg(jpeg, length, presentationSeconds);
    }

    public static synchronized int addRgba(long id, byte[] rgba, int length, int width, int height, double presentationSeconds) {
        Session session = sessions.get(id);
        return session == null ? 0 : session.addRgba(rgba, length, width, height, presentationSeconds);
    }

    public static synchronized int finish(long id) {
        Session session = sessions.get(id);
        return session == null ? 0 : session.finish();
    }

    public static synchronized String lastError(long id) {
        Session session = sessions.get(id);
        return session == null ? "Android encoder session was not found." : session.error;
    }

    public static synchronized void destroy(long id) {
        Session session = sessions.remove(id);
        if (session != null) session.close();
    }

    public static synchronized long createSurfaceSession(String path, int width, int height, int fps, int bitrate) {
        try {
            SurfaceSession session = new SurfaceSession(path, width, height, fps, bitrate);
            long id = nextSurfaceId++;
            if (!nativeVideoLoaded || nativeRegisterSurface(id, session.inputSurface) == 0) {
                session.close();
                throw new IOException("Android GPU video plugin could not register the MediaCodec input Surface.");
            }
            surfaceSessions.put(id, session);
            return id;
        } catch (Exception exception) {
            lastCreateFailure = exception.getClass().getSimpleName() + ": " + exception.getMessage();
            Log.e("MacacaBeacon", "Could not create Android Surface H.264 encoder", exception);
            return 0;
        }
    }

    public static synchronized Surface getSurface(long id) {
        SurfaceSession session = surfaceSessions.get(id);
        return session == null ? null : session.inputSurface;
    }

    public static synchronized int finishSurfaceSession(long id) {
        SurfaceSession session = surfaceSessions.get(id);
        if (nativeVideoLoaded) {
            try { nativeWaitForIdle(id); } catch (Throwable ignored) { }
        }
        return session == null ? 0 : session.finish();
    }

    public static synchronized String surfaceSessionError(long id) {
        SurfaceSession session = surfaceSessions.get(id);
        return session == null ? "Android Surface encoder session was not found." : session.error;
    }

    public static synchronized void destroySurfaceSession(long id) {
        SurfaceSession session = surfaceSessions.remove(id);
        if (nativeVideoLoaded) {
            try { nativeUnregisterSurface(id); } catch (Throwable ignored) { }
        }
        if (session != null) session.close();
    }

    public static synchronized long beginEncodeRawFiles(
            String outputPath,
            String[] framePaths,
            double[] presentationSeconds,
            int width,
            int height,
            int fps,
            int bitrate,
            double durationSeconds) {
        if (outputPath == null || framePaths == null || presentationSeconds == null ||
                framePaths.length == 0 || framePaths.length != presentationSeconds.length)
            return 0;
        long id = nextJobId++;
        EncodeJob job = new EncodeJob(
                outputPath,
                framePaths,
                presentationSeconds,
                width,
                height,
                fps,
                bitrate,
                durationSeconds);
        jobs.put(id, job);
        job.start();
        return id;
    }

    public static synchronized int isEncodeJobDone(long id) {
        EncodeJob job = jobs.get(id);
        return job != null && job.done ? 1 : 0;
    }

    public static synchronized int didEncodeJobSucceed(long id) {
        EncodeJob job = jobs.get(id);
        return job != null && job.done && job.succeeded ? 1 : 0;
    }

    public static synchronized String encodeJobError(long id) {
        EncodeJob job = jobs.get(id);
        return job == null ? "Android encode job was not found." : job.error;
    }

    public static synchronized void destroyEncodeJob(long id) {
        jobs.remove(id);
    }

    private static final class EncodeJob implements Runnable {
        private final String outputPath;
        private final String[] framePaths;
        private final double[] presentationSeconds;
        private final int width;
        private final int height;
        private final int fps;
        private final int bitrate;
        private final double durationSeconds;
        private volatile boolean done;
        private volatile boolean succeeded;
        private volatile String error;

        EncodeJob(
                String outputPath,
                String[] framePaths,
                double[] presentationSeconds,
                int width,
                int height,
                int fps,
                int bitrate,
                double durationSeconds) {
            this.outputPath = outputPath;
            this.framePaths = framePaths;
            this.presentationSeconds = presentationSeconds;
            this.width = width;
            this.height = height;
            this.fps = fps;
            this.bitrate = bitrate;
            this.durationSeconds = durationSeconds;
        }

        void start() {
            Thread thread = new Thread(this, "MacacaBeaconVideoEncoder");
            thread.setPriority(Thread.NORM_PRIORITY - 1);
            thread.start();
        }

        @Override public void run() {
            Session session = null;
            try {
                session = new Session(outputPath, width, height, fps, bitrate);
                byte[] lastFrame = null;
                for (int index = 0; index < framePaths.length; index++) {
                    byte[] frame = readFile(framePaths[index]);
                    if (session.addRgba(frame, frame.length, width, height, presentationSeconds[index]) == 0)
                        throw new IOException(session.error == null ? "Android rejected a raw video frame." : session.error);
                    lastFrame = frame;
                }

                double lastTime = presentationSeconds[presentationSeconds.length - 1];
                if (lastFrame != null && durationSeconds > lastTime + (0.5d / Math.max(1, fps)) &&
                        session.addRgba(lastFrame, lastFrame.length, width, height, durationSeconds) == 0)
                    throw new IOException(session.error == null ? "Android could not extend the final video frame." : session.error);

                if (session.finish() == 0)
                    throw new IOException(session.error == null ? "Android could not finalize the MP4 file." : session.error);
                succeeded = true;
            } catch (Throwable throwable) {
                error = throwable.getClass().getSimpleName() + ": " + throwable.getMessage();
                Log.e("MacacaBeacon", "Background H.264 encoding failed", throwable);
            } finally {
                if (session != null) session.close();
                done = true;
            }
        }

        private static byte[] readFile(String path) throws IOException {
            File file = new File(path);
            long length = file.length();
            if (length <= 0 || length > Integer.MAX_VALUE)
                throw new IOException("Invalid raw frame file: " + path);
            byte[] bytes = new byte[(int)length];
            FileInputStream stream = new FileInputStream(file);
            try {
                int offset = 0;
                while (offset < bytes.length) {
                    int count = stream.read(bytes, offset, bytes.length - offset);
                    if (count < 0) throw new IOException("Unexpected end of raw frame file: " + path);
                    offset += count;
                }
            } finally {
                stream.close();
            }
            return bytes;
        }
    }

    private static final class SurfaceSession implements Runnable {
        private final MediaCodec codec;
        private final MediaMuxer muxer;
        private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
        private final Surface inputSurface;
        private final int width;
        private final int height;
        private final int fps;
        private int track = -1;
        private boolean muxerStarted;
        private boolean finished;
        private volatile String error;
        private Thread drainThread;

        SurfaceSession(String path, int requestedWidth, int requestedHeight, int requestedFps, int bitrate) throws Exception {
            width = align16(Math.max(2, requestedWidth));
            height = align16(Math.max(2, requestedHeight));
            fps = Math.max(1, requestedFps);
            MediaCodecInfo codecInfo = findSurfaceCodec();
            if (codecInfo == null) throw new IOException("No Android H.264 surface encoder is available.");

            MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
            format.setInteger(MediaFormat.KEY_BIT_RATE, Math.max(128000, bitrate));
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
            codec = MediaCodec.createByCodecName(codecInfo.getName());
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            inputSurface = codec.createInputSurface();
            codec.start();
            muxer = new MediaMuxer(path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);
            error = null;
            drainThread = new Thread(this, "MacacaBeaconSurfaceDrain");
            drainThread.setPriority(Thread.NORM_PRIORITY - 1);
            drainThread.start();
            Log.i("MacacaBeacon", "Using Android Surface H.264 encoder " + codecInfo.getName() + " size=" + width + "x" + height);
        }

        @Override public void run() {
            try {
                while (!finished) drain(false);
                drain(true);
            } catch (Throwable throwable) {
                error = throwable.getClass().getSimpleName() + ": " + throwable.getMessage();
                Log.e("MacacaBeacon", "Android Surface drain failed", throwable);
            }
        }

        int finish() {
            if (finished) return error == null ? 1 : 0;
            try {
                codec.signalEndOfInputStream();
                finished = true;
                if (drainThread != null) drainThread.join(5000);
                if (error == null && muxerStarted) {
                    muxer.stop();
                    muxerStarted = false;
                }
                return error == null ? 1 : 0;
            } catch (Exception exception) {
                error = exception.getMessage() == null ? "Android Surface finalization failed." : exception.getMessage();
                return 0;
            }
        }

        private void drain(boolean endOfStream) {
            int idleCount = 0;
            while (idleCount < (endOfStream ? 100 : 2)) {
                int result = codec.dequeueOutputBuffer(bufferInfo, 10000);
                if (result == MediaCodec.INFO_TRY_AGAIN_LATER) {
                    idleCount++;
                } else if (result == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    if (muxerStarted) throw new IllegalStateException("Android Surface encoder changed output format twice.");
                    track = muxer.addTrack(codec.getOutputFormat());
                    muxer.start();
                    muxerStarted = true;
                } else if (result >= 0) {
                    ByteBuffer output = codec.getOutputBuffer(result);
                    if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0 && bufferInfo.size > 0 && muxerStarted && output != null) {
                        output.position(bufferInfo.offset);
                        output.limit(bufferInfo.offset + bufferInfo.size);
                        muxer.writeSampleData(track, output, bufferInfo);
                    }
                    boolean eos = (bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                    codec.releaseOutputBuffer(result, false);
                    if (eos) break;
                }
            }
        }

        void close() {
            try { if (!finished) finish(); } catch (Exception ignored) { }
            try { inputSurface.release(); } catch (Exception ignored) { }
            try { if (muxerStarted) muxer.stop(); } catch (Exception ignored) { }
            try { muxer.release(); } catch (Exception ignored) { }
            try { codec.stop(); } catch (Exception ignored) { }
            try { codec.release(); } catch (Exception ignored) { }
        }

        private static MediaCodecInfo findSurfaceCodec() {
            MediaCodecList list = new MediaCodecList(MediaCodecList.ALL_CODECS);
            for (MediaCodecInfo info : list.getCodecInfos()) {
                if (!info.isEncoder()) continue;
                for (String type : info.getSupportedTypes()) {
                    if (!"video/avc".equalsIgnoreCase(type)) continue;
                    for (MediaCodecInfo.CodecCapabilities capabilities : new MediaCodecInfo.CodecCapabilities[] { info.getCapabilitiesForType(type) })
                        for (int format : capabilities.colorFormats)
                            if (format == MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface) return info;
                }
            }
            return null;
        }

        private static int align16(int value) {
            return (value + 15) & ~15;
        }
    }

    private static final class Session {
        private final MediaCodec codec;
        private final MediaMuxer muxer;
        private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
        private final int width;
        private final int height;
        private final int colorFormat;
        private int track = -1;
        private boolean muxerStarted;
        private boolean finished;
        private String error = "Android encoder has not started.";

        Session(String path, int requestedWidth, int requestedHeight, int fps, int bitrate) throws Exception {
            // A number of Android H.264 encoders reject dimensions that are
            // even but not aligned to a macroblock boundary (for example
            // 960x540). Padding the encoded frame keeps those devices on the
            // MP4 path; the extra pixels are only a small border.
            width = align16(Math.max(2, requestedWidth));
            height = align16(Math.max(2, requestedHeight));
            MediaCodecInfo codecInfo = findCodec();
            if (codecInfo == null) throw new IOException("No Android H.264 encoder is available.");
            colorFormat = findColorFormat(codecInfo.getCapabilitiesForType("video/avc"));
            if (colorFormat == 0) throw new IOException("The Android H.264 encoder has no supported YUV420 input format.");

            MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT, colorFormat);
            format.setInteger(MediaFormat.KEY_FRAME_RATE, Math.max(1, fps));
            format.setInteger(MediaFormat.KEY_BIT_RATE, Math.max(128000, bitrate));
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
            codec = MediaCodec.createByCodecName(codecInfo.getName());
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            codec.start();
            muxer = new MediaMuxer(path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);
            error = null;
            Log.i("MacacaBeacon", "Using Android H.264 encoder " + codecInfo.getName() + " colorFormat=" + colorFormat + " size=" + width + "x" + height);
        }

        int addJpeg(byte[] jpeg, int length, double seconds) {
            if (finished || jpeg == null || length <= 0) return fail("Invalid JPEG frame.");
            Bitmap source = BitmapFactory.decodeByteArray(jpeg, 0, Math.min(length, jpeg.length));
            if (source == null) return fail("Android could not decode a captured JPEG frame.");
            Bitmap bitmap = source;
            try {
                if (source.getWidth() != width || source.getHeight() != height) {
                    bitmap = Bitmap.createScaledBitmap(source, width, height, true);
                    source.recycle();
                }
                int[] pixels = new int[width * height];
                bitmap.getPixels(pixels, 0, width, 0, 0, width, height);
                byte[] yuv = toYuv420(pixels, width, height, colorFormat == MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Planar);
                int inputIndex = codec.dequeueInputBuffer(100000);
                if (inputIndex < 0) return fail("Android H.264 encoder input buffer was unavailable.");
                ByteBuffer input = codec.getInputBuffer(inputIndex);
                if (input == null || input.capacity() < yuv.length) return fail("Android H.264 encoder input buffer is too small.");
                input.clear();
                input.put(yuv);
                codec.queueInputBuffer(inputIndex, 0, yuv.length, Math.max(0L, (long)(seconds * 1000000d)), 0);
                drain(false);
                return error == null ? 1 : 0;
            } catch (Exception exception) {
                return fail(exception.getMessage() == null ? "Android H.264 frame encoding failed." : exception.getMessage());
            } finally {
                if (!bitmap.equals(source) && !source.isRecycled()) source.recycle();
                if (!bitmap.isRecycled()) bitmap.recycle();
            }
        }

        int addRgba(byte[] rgba, int length, int sourceWidth, int sourceHeight, double seconds) {
            if (finished || rgba == null || sourceWidth <= 0 || sourceHeight <= 0 ||
                    length < sourceWidth * sourceHeight * 4)
                return fail("Invalid RGBA frame.");
            try {
                byte[] yuv = toYuv420Rgba(
                        rgba,
                        sourceWidth,
                        sourceHeight,
                        width,
                        height,
                        colorFormat == MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Planar);
                int inputIndex = codec.dequeueInputBuffer(100000);
                if (inputIndex < 0) return fail("Android H.264 encoder input buffer was unavailable.");
                ByteBuffer input = codec.getInputBuffer(inputIndex);
                if (input == null || input.capacity() < yuv.length) return fail("Android H.264 encoder input buffer is too small.");
                input.clear();
                input.put(yuv);
                codec.queueInputBuffer(inputIndex, 0, yuv.length, Math.max(0L, (long)(seconds * 1000000d)), 0);
                drain(false);
                return error == null ? 1 : 0;
            } catch (Exception exception) {
                return fail(exception.getMessage() == null ? "Android H.264 RGBA encoding failed." : exception.getMessage());
            }
        }

        int finish() {
            if (finished) return error == null ? 1 : 0;
            try {
                int inputIndex = codec.dequeueInputBuffer(100000);
                if (inputIndex >= 0) codec.queueInputBuffer(inputIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                drain(true);
                if (error == null) {
                    muxer.stop();
                    muxerStarted = false;
                    finished = true;
                    return 1;
                }
            } catch (Exception exception) {
                fail(exception.getMessage() == null ? "Android H.264 finalization failed." : exception.getMessage());
            }
            return 0;
        }

        private void drain(boolean endOfStream) {
            int idleCount = 0;
            while (idleCount < (endOfStream ? 100 : 3)) {
                int result = codec.dequeueOutputBuffer(bufferInfo, 10000);
                if (result == MediaCodec.INFO_TRY_AGAIN_LATER) {
                    idleCount++;
                    if (!endOfStream) break;
                } else if (result == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    if (muxerStarted) throw new IllegalStateException("Android encoder changed output format twice.");
                    track = muxer.addTrack(codec.getOutputFormat());
                    muxer.start();
                    muxerStarted = true;
                } else if (result >= 0) {
                    ByteBuffer output = codec.getOutputBuffer(result);
                    if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0 && bufferInfo.size > 0 && muxerStarted && output != null) {
                        output.position(bufferInfo.offset);
                        output.limit(bufferInfo.offset + bufferInfo.size);
                        muxer.writeSampleData(track, output, bufferInfo);
                    }
                    boolean eos = (bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                    codec.releaseOutputBuffer(result, false);
                    if (eos) break;
                }
            }
        }

        void close() {
            try { if (muxerStarted) muxer.stop(); } catch (Exception ignored) { }
            try { muxer.release(); } catch (Exception ignored) { }
            try { codec.stop(); } catch (Exception ignored) { }
            try { codec.release(); } catch (Exception ignored) { }
        }

        private int fail(String message) {
            error = message;
            return 0;
        }

        private static MediaCodecInfo findCodec() {
            android.media.MediaCodecList list = new android.media.MediaCodecList(android.media.MediaCodecList.ALL_CODECS);
            for (MediaCodecInfo info : list.getCodecInfos()) {
                if (info.isEncoder()) {
                    for (String type : info.getSupportedTypes())
                        if ("video/avc".equalsIgnoreCase(type)) return info;
                }
            }
            return null;
        }

        private static int findColorFormat(MediaCodecInfo.CodecCapabilities capabilities) {
            for (int format : capabilities.colorFormats)
                if (format == MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Planar || format == MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420SemiPlanar)
                    return format;
            return 0;
        }

        private static int align16(int value) {
            return (value + 15) & ~15;
        }

        private static byte[] toYuv420(int[] pixels, int width, int height, boolean planar) {
            byte[] output = new byte[width * height * 3 / 2];
            int yIndex = 0;
            int uIndex = width * height;
            int vIndex = planar ? uIndex + (width * height / 4) : uIndex + 1;
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int color = pixels[y * width + x];
                    int r = (color >> 16) & 255;
                    int g = (color >> 8) & 255;
                    int b = color & 255;
                    output[yIndex++] = (byte)Math.max(0, Math.min(255, ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16));
                    if ((y & 1) == 0 && (x & 1) == 0) {
                        int u = Math.max(0, Math.min(255, ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128));
                        int v = Math.max(0, Math.min(255, ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128));
                        if (planar) {
                            output[uIndex++] = (byte)u;
                            output[vIndex++] = (byte)v;
                        } else {
                            output[uIndex++] = (byte)u;
                            output[uIndex++] = (byte)v;
                        }
                    }
                }
            }
            return output;
        }

        private static byte[] toYuv420Rgba(
                byte[] rgba,
                int sourceWidth,
                int sourceHeight,
                int outputWidth,
                int outputHeight,
                boolean planar) {
            byte[] output = new byte[outputWidth * outputHeight * 3 / 2];
            int yIndex = 0;
            int uIndex = outputWidth * outputHeight;
            int vIndex = planar ? uIndex + (outputWidth * outputHeight / 4) : uIndex + 1;
            for (int y = 0; y < outputHeight; y++) {
                int sourceY = Math.min(sourceHeight - 1, y * sourceHeight / outputHeight);
                for (int x = 0; x < outputWidth; x++) {
                    int sourceX = Math.min(sourceWidth - 1, x * sourceWidth / outputWidth);
                    int source = (sourceY * sourceWidth + sourceX) * 4;
                    int r = rgba[source] & 255;
                    int g = rgba[source + 1] & 255;
                    int b = rgba[source + 2] & 255;
                    output[yIndex++] = (byte)Math.max(0, Math.min(255, ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16));
                    if ((y & 1) == 0 && (x & 1) == 0) {
                        int u = Math.max(0, Math.min(255, ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128));
                        int v = Math.max(0, Math.min(255, ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128));
                        if (planar) {
                            output[uIndex++] = (byte)u;
                            output[vIndex++] = (byte)v;
                        } else {
                            output[uIndex++] = (byte)u;
                            output[uIndex++] = (byte)v;
                        }
                    }
                }
            }
            return output;
        }
    }
}
