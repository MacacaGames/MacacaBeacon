using System;
using System.IO;

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

        public VideoCaptureFrame(byte[] jpegData, double capturedAt)
        {
            JpegData = jpegData;
            DataFilePath = null;
            Format = VideoCaptureFrameFormat.Jpeg;
            Width = 0;
            Height = 0;
            ByteCount = jpegData == null ? 0 : jpegData.Length;
            CapturedAt = capturedAt;
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
        }

        public bool HasData => JpegData != null && JpegData.Length > 0 ||
                               !string.IsNullOrEmpty(DataFilePath) && File.Exists(DataFilePath);

        public byte[] ReadData()
        {
            if (JpegData != null)
                return JpegData;
            return string.IsNullOrEmpty(DataFilePath) ? null : File.ReadAllBytes(DataFilePath);
        }

        public void DeleteDataFile()
        {
            if (string.IsNullOrEmpty(DataFilePath) || !File.Exists(DataFilePath))
                return;
            try { File.Delete(DataFilePath); }
            catch { }
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

        public VideoCaptureResult(string filePath, string extension, string mimeType, double durationSeconds, int frameCount, string encoderName)
        {
            FilePath = filePath;
            Extension = extension;
            MimeType = mimeType;
            DurationSeconds = durationSeconds;
            FrameCount = frameCount;
            EncoderName = encoderName;
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
