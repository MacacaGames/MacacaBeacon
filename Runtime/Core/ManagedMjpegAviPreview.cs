using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class ManagedMjpegAviPreview : IDisposable
    {
        internal readonly struct Frame
        {
            public readonly int Offset;
            public readonly int Length;

            public Frame(int offset, int length)
            {
                Offset = offset;
                Length = length;
            }
        }

        private const int MaximumFrameCount = 4096;
        private readonly byte[] aviData;
        private readonly Frame[] frames;
        private readonly Texture2D texture;
        private int displayedFrame = -1;

        public Texture Texture => texture;
        public double Duration { get; }
        public double Time { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsPrepared => displayedFrame >= 0;
        public string Error { get; private set; }

        private ManagedMjpegAviPreview(byte[] data, Frame[] parsedFrames, double duration)
        {
            aviData = data;
            frames = parsedFrames;
            Duration = Math.Max(1d / parsedFrames.Length, duration);
            texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        }

        public static bool TryCreate(
            string path,
            double duration,
            long maximumBytes,
            out ManagedMjpegAviPreview preview,
            out string error)
        {
            preview = null;
            error = null;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length <= 0)
                {
                    error = "Managed MJPEG preview file was missing or empty.";
                    return false;
                }
                if (file.Length > Math.Max(1L, maximumBytes) || file.Length > int.MaxValue)
                {
                    error = "Managed MJPEG preview file exceeded the configured attachment limit.";
                    return false;
                }

                var data = File.ReadAllBytes(path);
                if (!TryParseFrames(data, out var frames, out error))
                    return false;

                preview = new ManagedMjpegAviPreview(data, frames, duration);
                if (!preview.DecodeFrame(0, out error))
                {
                    preview.Dispose();
                    preview = null;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Managed MJPEG preview could not be opened: " + exception.Message;
                return false;
            }
        }

        internal static bool TryParseFrames(byte[] data, out Frame[] frames, out string error)
        {
            frames = null;
            error = null;
            if (data == null || data.Length < 12 ||
                !HasFourCc(data, 0, "RIFF") || !HasFourCc(data, 8, "AVI "))
            {
                error = "Managed MJPEG preview expected a RIFF AVI file.";
                return false;
            }

            var riffSize = ReadUInt32(data, 4);
            var riffEndValue = 8L + riffSize;
            if (riffSize < 4 || riffEndValue > data.Length)
            {
                error = "Managed MJPEG preview found invalid RIFF bounds.";
                return false;
            }

            var result = new List<Frame>();
            var position = 12;
            var riffEnd = (int)riffEndValue;
            while (position + 8 <= riffEnd)
            {
                var chunkSize = ReadUInt32(data, position + 4);
                var chunkData = position + 8;
                var chunkEndValue = (long)chunkData + chunkSize;
                if (chunkEndValue > riffEnd)
                {
                    error = "Managed MJPEG preview found a chunk outside the RIFF bounds.";
                    return false;
                }

                if (HasFourCc(data, position, "LIST") && chunkSize >= 4 &&
                    HasFourCc(data, chunkData, "movi") &&
                    !TryParseMovieFrames(data, chunkData + 4, (int)chunkEndValue, result, out error))
                    return false;

                position = AlignToWord(chunkEndValue);
            }

            if (result.Count == 0)
            {
                error = "Managed MJPEG preview found no JPEG video frames.";
                return false;
            }
            frames = result.ToArray();
            return true;
        }

        private static bool TryParseMovieFrames(
            byte[] data,
            int position,
            int movieEnd,
            List<Frame> frames,
            out string error)
        {
            error = null;
            while (position + 8 <= movieEnd)
            {
                var chunkSize = ReadUInt32(data, position + 4);
                var chunkData = position + 8;
                var chunkEndValue = (long)chunkData + chunkSize;
                if (chunkSize > int.MaxValue || chunkEndValue > movieEnd)
                {
                    error = "Managed MJPEG preview found an invalid video-frame chunk.";
                    return false;
                }

                if (HasFourCc(data, position, "00dc") && chunkSize > 0)
                {
                    if (frames.Count >= MaximumFrameCount)
                    {
                        error = "Managed MJPEG preview exceeded the supported frame count.";
                        return false;
                    }
                    frames.Add(new Frame(chunkData, (int)chunkSize));
                }
                position = AlignToWord(chunkEndValue);
            }
            return true;
        }

        public void Update(double deltaTime)
        {
            if (!IsPlaying)
                return;
            Time = Math.Min(Duration, Time + Math.Max(0d, deltaTime));
            if (!DecodeFrame(FrameIndexForTime(Time, Duration, frames.Length), out var error))
            {
                Error = error;
                IsPlaying = false;
            }
            if (Time >= Duration)
                IsPlaying = false;
        }

        public void Play()
        {
            if (Time >= Duration)
                Seek(0d);
            IsPlaying = true;
        }

        public void Pause()
        {
            IsPlaying = false;
        }

        public bool Seek(double time)
        {
            Time = Math.Max(0d, Math.Min(Duration, time));
            var decoded = DecodeFrame(FrameIndexForTime(Time, Duration, frames.Length), out var error);
            Error = decoded ? null : error;
            return decoded;
        }

        internal static int FrameIndexForTime(double time, double duration, int frameCount)
        {
            if (frameCount <= 1 || duration <= 0d)
                return 0;
            var progress = Math.Max(0d, Math.Min(1d, time / duration));
            return Math.Min(frameCount - 1, (int)Math.Floor(progress * frameCount));
        }

        private bool DecodeFrame(int index, out string error)
        {
            error = null;
            if (index == displayedFrame)
                return true;
            var frame = frames[Math.Max(0, Math.Min(frames.Length - 1, index))];
            var jpeg = new byte[frame.Length];
            Buffer.BlockCopy(aviData, frame.Offset, jpeg, 0, frame.Length);
            if (!ImageConversion.LoadImage(texture, jpeg, false))
            {
                error = "Managed MJPEG preview could not decode JPEG frame " + index + ".";
                return false;
            }
            Error = null;
            displayedFrame = index;
            return true;
        }

        public void Dispose()
        {
            IsPlaying = false;
            if (texture != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(texture);
                else
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static bool HasFourCc(byte[] data, int offset, string value)
        {
            return offset >= 0 && offset + 4 <= data.Length &&
                   data[offset] == value[0] && data[offset + 1] == value[1] &&
                   data[offset + 2] == value[2] && data[offset + 3] == value[3];
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] |
                          data[offset + 1] << 8 |
                          data[offset + 2] << 16 |
                          data[offset + 3] << 24);
        }

        private static int AlignToWord(long value)
        {
            return checked((int)(value + (value & 1L)));
        }
    }
}
