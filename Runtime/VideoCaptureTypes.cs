using System;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace MacacaGames.RuntimeBugReporter
{
    public enum VideoCaptureFrameFormat
    {
        Jpeg,
        Rgba32
    }

    public readonly struct VideoCaptureFrame
    {
        public readonly byte[] JpegData;
        public readonly string DataFilePath;
        public readonly VideoCaptureFrameFormat Format;
        public readonly int Width;
        public readonly int Height;
        public readonly int ByteCount;
        public readonly double CapturedAt;
        internal readonly NativeVideoFrame NativeData;
        internal readonly int NativeGeneration;
        internal readonly bool RowsAreBottomUp;

        public VideoCaptureFrame(byte[] jpegData, double capturedAt)
        {
            JpegData = jpegData;
            DataFilePath = null;
            Format = VideoCaptureFrameFormat.Jpeg;
            Width = 0;
            Height = 0;
            ByteCount = jpegData == null ? 0 : jpegData.Length;
            CapturedAt = capturedAt;
            NativeData = null;
            NativeGeneration = 0;
            RowsAreBottomUp = false;
        }

        public VideoCaptureFrame(byte[] data, VideoCaptureFrameFormat format, int width, int height, double capturedAt)
        {
            JpegData = data;
            DataFilePath = null;
            Format = format;
            Width = width;
            Height = height;
            ByteCount = data == null ? 0 : data.Length;
            CapturedAt = capturedAt;
            NativeData = null;
            NativeGeneration = 0;
            RowsAreBottomUp = false;
        }

        public VideoCaptureFrame(string dataFilePath, VideoCaptureFrameFormat format, int width, int height, int byteCount, double capturedAt)
        {
            JpegData = null;
            DataFilePath = dataFilePath;
            Format = format;
            Width = width;
            Height = height;
            ByteCount = byteCount;
            CapturedAt = capturedAt;
            NativeData = null;
            NativeGeneration = 0;
            RowsAreBottomUp = false;
        }

        internal VideoCaptureFrame(NativeVideoFrame nativeData, double capturedAt, bool rowsAreBottomUp)
        {
            JpegData = null;
            DataFilePath = null;
            Format = VideoCaptureFrameFormat.Rgba32;
            Width = nativeData?.Width ?? 0;
            Height = nativeData?.Height ?? 0;
            ByteCount = nativeData?.ByteCount ?? 0;
            CapturedAt = capturedAt;
            NativeData = nativeData;
            NativeGeneration = nativeData?.Generation ?? 0;
            RowsAreBottomUp = rowsAreBottomUp;
            nativeData?.Retain(NativeGeneration);
        }

        public bool HasData => JpegData != null && JpegData.Length > 0 ||
                               !string.IsNullOrEmpty(DataFilePath) && File.Exists(DataFilePath) ||
                               NativeData != null && NativeData.IsReadable(NativeGeneration);

        public byte[] ReadData()
        {
            if (JpegData != null)
                return JpegData;
            if (!string.IsNullOrEmpty(DataFilePath))
                return File.ReadAllBytes(DataFilePath);
            return NativeData?.CopyToManaged(NativeGeneration, RowsAreBottomUp);
        }

        internal unsafe bool TryGetNativeRgbaPointer(out IntPtr pointer)
        {
            pointer = IntPtr.Zero;
            if (Format != VideoCaptureFrameFormat.Rgba32 ||
                NativeData == null ||
                !NativeData.IsReadable(NativeGeneration))
                return false;
            pointer = (IntPtr)NativeData.GetUnsafeReadOnlyPointer(NativeGeneration);
            return pointer != IntPtr.Zero;
        }

        internal void RetainNative() => NativeData?.Retain(NativeGeneration);

        internal void ReleaseNative() => NativeData?.Release(NativeGeneration);

        public void DeleteDataFile()
        {
            if (string.IsNullOrEmpty(DataFilePath) || !File.Exists(DataFilePath))
                return;
            try { File.Delete(DataFilePath); }
            catch { }
        }
    }

    internal sealed class NativeVideoFrame : IDisposable
    {
        internal NativeArray<byte> Data;
        private int referenceCount;
        private bool requestInFlight;
        private bool disposed;

        internal int Width { get; }
        internal int Height { get; }
        internal int ByteCount => Data.IsCreated ? Data.Length : 0;
        internal int Generation { get; private set; }
        internal bool IsAvailable => !disposed && !requestInFlight && referenceCount == 0;

        internal NativeVideoFrame(int width, int height)
        {
            Width = width;
            Height = height;
            Data = new NativeArray<byte>(checked(width * height * 4), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        internal bool TryBeginRequest()
        {
            if (!IsAvailable)
                return false;
            requestInFlight = true;
            Generation++;
            if (Generation == 0)
                Generation = 1;
            return true;
        }

        internal void CompleteRequest(bool succeeded)
        {
            requestInFlight = false;
            if (!succeeded)
                referenceCount = 0;
        }

        internal bool IsReadable(int generation)
        {
            return !disposed && !requestInFlight && Data.IsCreated && generation == Generation;
        }

        internal void Retain(int generation)
        {
            if (!IsReadable(generation))
                throw new InvalidOperationException("Cannot retain a stale native video frame.");
            referenceCount++;
        }

        internal void Release(int generation)
        {
            if (disposed || generation != Generation || referenceCount <= 0)
                return;
            referenceCount--;
        }

        internal unsafe void* GetUnsafeReadOnlyPointer(int generation)
        {
            return IsReadable(generation) ? NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(Data) : null;
        }

        internal byte[] CopyToManaged(int generation, bool flipRows)
        {
            if (!IsReadable(generation))
                return null;
            var result = Data.ToArray();
            if (flipRows)
                FlipRows(result, Width, Height);
            return result;
        }

        private static void FlipRows(byte[] bytes, int width, int height)
        {
            var rowLength = width * 4;
            var row = new byte[rowLength];
            for (var y = 0; y < height / 2; y++)
            {
                var opposite = height - 1 - y;
                Buffer.BlockCopy(bytes, y * rowLength, row, 0, rowLength);
                Buffer.BlockCopy(bytes, opposite * rowLength, bytes, y * rowLength, rowLength);
                Buffer.BlockCopy(row, 0, bytes, opposite * rowLength, rowLength);
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            referenceCount = 0;
            requestInFlight = false;
            if (Data.IsCreated)
                Data.Dispose();
        }
    }

    internal sealed class NativeVideoFrameRing : IDisposable
    {
        private const int InFlightReserve = 3;
        private readonly NativeVideoFrame[] frames;
        private int nextIndex;

        internal int Width { get; }
        internal int Height { get; }
        internal int Capacity => frames.Length;
        internal int EffectiveFramesPerSecond { get; }
        internal long AllocatedBytes => (long)frames.Length * Width * Height * 4L;

        private NativeVideoFrameRing(int width, int height, int capacity, int effectiveFramesPerSecond)
        {
            Width = width;
            Height = height;
            EffectiveFramesPerSecond = effectiveFramesPerSecond;
            frames = new NativeVideoFrame[capacity];
            try
            {
                for (var index = 0; index < frames.Length; index++)
                    frames[index] = new NativeVideoFrame(width, height);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal static bool TryCreate(
            int width,
            int height,
            int requestedFramesPerSecond,
            int secondsBefore,
            int secondsAfter,
            int maximumMegabytes,
            out NativeVideoFrameRing ring,
            out string diagnostic)
        {
            ring = null;
            diagnostic = null;
            try
            {
                var frameBytes = checked((long)width * height * 4L);
                var maximumBytes = Math.Max(32L, maximumMegabytes) * 1024L * 1024L;
                var capacityByMemory = (int)Math.Min(int.MaxValue, maximumBytes / frameBytes);
                var durationSeconds = Math.Max(1, secondsBefore + Math.Max(0, secondsAfter));
                if (capacityByMemory <= InFlightReserve)
                {
                    diagnostic = "RAM frame cache is too small for one second of capture plus in-flight slots.";
                    return false;
                }

                var effectiveFps = Math.Max(1, Math.Min(
                    Math.Max(1, requestedFramesPerSecond),
                    (capacityByMemory - InFlightReserve) / durationSeconds));
                var requestedCapacity = checked(durationSeconds * effectiveFps + InFlightReserve);
                var capacity = Math.Min(capacityByMemory, requestedCapacity);
                ring = new NativeVideoFrameRing(width, height, capacity, effectiveFps);
                diagnostic = "RAM frame cache allocated " + capacity + " slots (" +
                             (ring.AllocatedBytes / (1024d * 1024d)).ToString("0.0") + " MiB), requested " +
                             requestedFramesPerSecond + " FPS, effective " + effectiveFps + " FPS.";
                return true;
            }
            catch (Exception exception)
            {
                ring?.Dispose();
                ring = null;
                diagnostic = "RAM frame cache allocation failed: " + exception.Message;
                return false;
            }
        }

        internal NativeVideoFrame TryAcquire()
        {
            for (var attempt = 0; attempt < frames.Length; attempt++)
            {
                var index = (nextIndex + attempt) % frames.Length;
                if (!frames[index].TryBeginRequest())
                    continue;
                nextIndex = (index + 1) % frames.Length;
                return frames[index];
            }
            return null;
        }

        public void Dispose()
        {
            if (frames == null)
                return;
            foreach (var frame in frames)
                frame?.Dispose();
        }
    }

    internal sealed class VideoCaptureResult
    {
        public readonly string FilePath;
        public readonly string Extension;
        public readonly string MimeType;
        public readonly double DurationSeconds;
        public readonly int FrameCount;
        public readonly string EncoderName;
        public readonly int Width;
        public readonly int Height;

        public VideoCaptureResult(
            string filePath,
            string extension,
            string mimeType,
            double durationSeconds,
            int frameCount,
            string encoderName,
            int width = 0,
            int height = 0)
        {
            FilePath = filePath;
            Extension = extension;
            MimeType = mimeType;
            DurationSeconds = durationSeconds;
            FrameCount = frameCount;
            EncoderName = encoderName;
            Width = width;
            Height = height;
        }

        public void DeleteFile()
        {
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
                return;
            try
            {
                File.Delete(FilePath);
            }
            catch
            {
                // Best effort cleanup. A staged failed report must never be removed here.
            }
        }
    }
}
