using System;
using System.Collections.Generic;
using System.IO;

namespace MacacaGames.RuntimeBugReporter
{
    public interface IVideoEncoderBackend
    {
        string Name { get; }
        string Extension { get; }
        string MimeType { get; }
        bool IsAvailable { get; }
        bool TryEncode(string outputPath, IReadOnlyList<VideoCaptureFrame> frames, int width, int height, int framesPerSecond, int bitrateKbps, double durationSeconds, out string error);
    }

    internal static class VideoEncoderBackend
    {
        public static VideoCaptureResult Encode(
            string outputStem,
            IReadOnlyList<VideoCaptureFrame> frames,
            int width,
            int height,
            int framesPerSecond,
            int bitrateKbps,
            double durationSeconds,
            bool preferMp4,
            bool allowLegacyAviFallback,
            out string error)
        {
            error = null;
            if (frames == null || frames.Count == 0)
            {
                error = "No captured video frames were available.";
                return null;
            }

            if (preferMp4)
            {
                var custom = BugReporter.VideoEncoderOverride;
                if (custom != null && custom.IsAvailable)
                {
                    var customOutputPath = outputStem + custom.Extension;
                    if (custom.TryEncode(customOutputPath, frames, width, height, framesPerSecond, bitrateKbps, durationSeconds, out error))
                        return new VideoCaptureResult(customOutputPath, custom.Extension, custom.MimeType, durationSeconds, frames.Count, custom.Name);
                    TryDelete(customOutputPath);
                }

                var mp4 = CreatePlatformMp4Encoder();
                if (mp4.IsAvailable)
                {
                    var outputPath = outputStem + mp4.Extension;
                    if (mp4.TryEncode(outputPath, frames, width, height, framesPerSecond, bitrateKbps, durationSeconds, out error))
                        return new VideoCaptureResult(outputPath, mp4.Extension, mp4.MimeType, durationSeconds, frames.Count, mp4.Name);
                    TryDelete(outputPath);
                }
                else
                {
                    error = "No H.264 MP4 encoder backend is available on this platform.";
                }
            }

            if (!allowLegacyAviFallback)
                return null;

            try
            {
                var jpegFrames = new List<byte[]>(frames.Count);
                for (var index = 0; index < frames.Count; index++)
                    jpegFrames.Add(frames[index].JpegData);
                var bytes = MjpegAviEncoder.Encode(jpegFrames, width, height, framesPerSecond, (float)durationSeconds);
                if (bytes == null || bytes.Length == 0)
                {
                    error = "The legacy AVI encoder returned no data.";
                    return null;
                }

                var outputPath = outputStem + ".avi";
                File.WriteAllBytes(outputPath, bytes);
                return new VideoCaptureResult(outputPath, ".avi", "video/x-msvideo", durationSeconds, frames.Count, "Managed MJPEG AVI fallback");
            }
            catch (Exception exception)
            {
                error = "Legacy AVI fallback failed: " + exception.Message;
                return null;
            }
        }

        private static IVideoEncoderBackend CreatePlatformMp4Encoder()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return new WindowsMediaFoundationMp4Encoder();
#else
            return new MacOsH264Mp4Encoder();
#endif
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // The caller will report the encoder error; cleanup is best effort.
            }
        }
    }
}
