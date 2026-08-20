using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class WebGlWebCodecsMp4Encoder
    {
        public const string Name = "WebCodecs H.264 + MP4 muxer";
        public const string Extension = ".mp4";
        public const string MimeType = "video/mp4";

        public static bool IsAvailable
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                try { return NativeIsAvailable() != 0; }
                catch (Exception exception)
                {
                    Debug.LogWarning("[Macaca Beacon] WebCodecs is unavailable: " + exception.Message);
                }
#endif
                return false;
            }
        }

        public static IEnumerator TryEncodeAsync(
            string outputPath,
            IReadOnlyList<VideoCaptureFrame> frames,
            int width,
            int height,
            int framesPerSecond,
            int bitrateKbps,
            double durationSeconds,
            Action<VideoCaptureResult, string> completed)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (frames == null || frames.Count == 0)
            {
                completed?.Invoke(null, "No captured video frames were available.");
                yield break;
            }

            if (!IsAvailable)
            {
                completed?.Invoke(null, "This browser does not provide WebCodecs H.264 encoding.");
                yield break;
            }

            var handle = NativeBegin(outputPath, width, height, Math.Max(1, framesPerSecond), Math.Max(128, bitrateKbps) * 1000);
            if (handle == 0)
            {
                completed?.Invoke(null, LastError() ?? "WebCodecs could not create an encoding session.");
                yield break;
            }

            var sourceStart = frames[0].CapturedAt;
            try
            {
                Debug.Log("[Macaca Beacon] WebCodecs encode starting with " + frames.Count + " frames at " + width + "x" + height + ".");
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    var data = frame.ReadData();
                    if (data == null || data.Length == 0)
                        continue;

                    var timestamp = Math.Max(0d, frame.CapturedAt - sourceStart);
                    var accepted = frame.Format == VideoCaptureFrameFormat.Rgba32
                        ? NativeAddRgba(handle, data, data.Length, frame.Width > 0 ? frame.Width : width, frame.Height > 0 ? frame.Height : height, timestamp)
                        : NativeAddJpeg(handle, data, data.Length, timestamp);
                    if (accepted == 0)
                    {
                        completed?.Invoke(null, LastError() ?? "WebCodecs rejected a captured frame.");
                        yield break;
                    }
                }

                NativeFinish(handle, Math.Max(durationSeconds, 1d / Math.Max(1, framesPerSecond)));
                Debug.Log("[Macaca Beacon] WebCodecs frames submitted; waiting for MP4 finalization.");
                var waitStarted = Time.realtimeSinceStartup;
                while (NativeIsDone(handle) == 0)
                {
                    if (Time.realtimeSinceStartup - waitStarted > 30f)
                    {
                        completed?.Invoke(null, "WebCodecs timed out while finalizing the MP4.");
                        yield break;
                    }
                    yield return null;
                }

                var error = LastError() ?? string.Empty;
                if (!string.IsNullOrEmpty(error) || !File.Exists(outputPath))
                {
                    completed?.Invoke(null, string.IsNullOrEmpty(error) ? "WebCodecs did not produce an MP4 file." : error);
                    yield break;
                }

                completed?.Invoke(new VideoCaptureResult(outputPath, Extension, MimeType, durationSeconds, frames.Count, Name, width, height), null);
            }
            finally
            {
                NativeDestroy(handle);
            }
#else
            completed?.Invoke(null, "WebCodecs is only available in a Unity WebGL player build.");
            yield break;
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconWebCodecs_IsAvailable")]
        private static extern int NativeIsAvailable();

        [DllImport("__Internal", EntryPoint = "MacacaBeaconWebCodecs_Begin")]
        private static extern int NativeBegin([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);

        [DllImport("__Internal", EntryPoint = "MacacaBeaconWebCodecs_AddJpeg")]
        private static extern int NativeAddJpeg(int handle, byte[] bytes, int byteCount, double presentationSeconds);

        [DllImport("__Internal", EntryPoint = "MacacaBeaconWebCodecs_AddRgba")]
        private static extern int NativeAddRgba(int handle, byte[] bytes, int byteCount, int width, int height, double presentationSeconds);

        [DllImport("__Internal", EntryPoint = "MacacaBeaconWebCodecs_Finish")]
        private static extern void NativeFinish(int handle, double durationSeconds);

        [DllImport("__Internal", EntryPoint = "MacacaBeaconWebCodecs_IsDone")]
        private static extern int NativeIsDone(int handle);

        [DllImport("__Internal", EntryPoint = "MacacaBeaconWebCodecs_LastError")]
        private static extern IntPtr NativeLastErrorPointer();

        [DllImport("__Internal", EntryPoint = "MacacaBeaconWebCodecs_Destroy")]
        private static extern void NativeDestroy(int handle);

        private static string LastError()
        {
            var pointer = NativeLastErrorPointer();
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
        }
#endif
    }
}
