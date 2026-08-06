using System;
using System.IO;

namespace MacacaGames.RuntimeBugReporter
{
    public readonly struct VideoCaptureFrame
    {
        public readonly byte[] JpegData;
        public readonly double CapturedAt;

        public VideoCaptureFrame(byte[] jpegData, double capturedAt)
        {
            JpegData = jpegData;
            CapturedAt = capturedAt;
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
