using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

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
            int jpegQuality,
            bool preferMp4,
            bool allowLegacyAviFallback,
            out string error,
            bool allowCustomEncoder = true)
        {
            error = null;
            if (frames == null || frames.Count == 0)
            {
                error = "No captured video frames were available.";
                return null;
            }

            if (preferMp4)
            {
                var custom = allowCustomEncoder ? BugReporter.VideoEncoderOverride : null;
                if (custom != null && custom.IsAvailable)
                {
                    var customOutputPath = outputStem + custom.Extension;
                    if (custom.TryEncode(customOutputPath, frames, width, height, framesPerSecond, bitrateKbps, durationSeconds, out error))
                        return new VideoCaptureResult(customOutputPath, custom.Extension, custom.MimeType, durationSeconds, frames.Count, custom.Name, width, height);
                    TryDelete(customOutputPath);
                }

                var mp4 = CreatePlatformMp4Encoder();
                if (mp4.IsAvailable)
                {
                    var outputPath = outputStem + mp4.Extension;
                    if (mp4.TryEncode(outputPath, frames, width, height, framesPerSecond, bitrateKbps, durationSeconds, out error))
                        return new VideoCaptureResult(outputPath, mp4.Extension, mp4.MimeType, durationSeconds, frames.Count, mp4.Name, width, height);
                    TryDelete(outputPath);
                }
                else
                {
                    var windows = mp4 as WindowsMediaFoundationMp4Encoder;
                    var availabilityError = windows?.AvailabilityError;
                    error = string.IsNullOrEmpty(availabilityError)
                        ? "No H.264 MP4 encoder backend is available on this platform."
                        : "Windows H.264 MP4 encoder is unavailable: " + availabilityError;
                }
            }

            if (!allowLegacyAviFallback)
                return null;

            try
            {
                var jpegFrames = new List<byte[]>(frames.Count);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    if (!frame.HasData)
                    {
                        error = "Legacy AVI fallback frame " + index + " had no data.";
                        return null;
                    }

                    var data = frame.ReadData();
                    if (frame.Format == VideoCaptureFrameFormat.Rgba32)
                    {
                        var expectedByteCount = (long)frame.Width * frame.Height * 4L;
                        if (frame.Width <= 0 || frame.Height <= 0 || data == null || data.LongLength != expectedByteCount)
                        {
                            error = "Legacy AVI fallback frame " + index + " had invalid RGBA32 dimensions or data length.";
                            return null;
                        }
                        data = ImageConversion.EncodeArrayToJPG(
                            data,
                            GraphicsFormat.R8G8B8A8_UNorm,
                            (uint)frame.Width,
                            (uint)frame.Height,
                            0,
                            Math.Max(1, Math.Min(100, jpegQuality)));
                    }
                    if (data == null || data.Length == 0)
                    {
                        error = "Legacy AVI fallback frame " + index + " could not be converted to JPEG.";
                        return null;
                    }
                    jpegFrames.Add(data);
                }
                var bytes = MjpegAviEncoder.Encode(jpegFrames, width, height, framesPerSecond, (float)durationSeconds);
                if (bytes == null || bytes.Length == 0)
                {
                    error = "The legacy AVI encoder returned no data.";
                    return null;
                }

                var outputPath = outputStem + ".avi";
                File.WriteAllBytes(outputPath, bytes);
                return new VideoCaptureResult(outputPath, ".avi", "video/x-msvideo", durationSeconds, frames.Count, "Managed MJPEG AVI fallback", width, height);
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
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidMediaCodecMp4Encoder();
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
