using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class AndroidMediaCodecMp4Encoder : IVideoEncoderBackend
    {
        public string Name => "Android MediaCodec H.264";
        public string Extension => ".mp4";
        public string MimeType => "video/mp4";

        public bool IsAvailable
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try
                {
                    using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                        return bridge.CallStatic<int>("isAvailable") != 0;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[Macaca Beacon] Android MP4 encoder is unavailable: " + exception.Message);
                }
#endif
                return false;
            }
        }

        public bool TryEncode(string outputPath, IReadOnlyList<VideoCaptureFrame> frames, int width, int height, int framesPerSecond, int bitrateKbps, double durationSeconds, out string error)
        {
            error = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaClass bridge = null;
            long session = 0;
            try
            {
                bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo");
                session = bridge.CallStatic<long>("create", outputPath, width, height, framesPerSecond, Math.Max(128, bitrateKbps) * 1000);
                if (session == 0)
                {
                    error = bridge.CallStatic<string>("lastCreateError");
                    if (string.IsNullOrEmpty(error))
                        error = "Android MediaCodec could not create an encoding session.";
                    Debug.LogWarning("[Macaca Beacon] Android MP4 encoder creation failed: " + error);
                    return false;
                }

                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    var frameData = frame.ReadData();
                    if (frameData == null || frameData.Length == 0)
                        continue;
                    var added = frame.Format == VideoCaptureFrameFormat.Rgba32
                        ? bridge.CallStatic<int>("addRgba", session, frameData, frameData.Length, frame.Width, frame.Height, frame.CapturedAt - frames[0].CapturedAt)
                        : bridge.CallStatic<int>("addJpeg", session, frameData, frameData.Length, frame.CapturedAt - frames[0].CapturedAt);
                    if (added == 0)
                    {
                        error = bridge.CallStatic<string>("lastError", session);
                        Debug.LogWarning("[Macaca Beacon] Android MP4 frame encoding failed: " + error);
                        return false;
                    }
                }

                if (bridge.CallStatic<int>("finish", session) == 0)
                {
                    error = bridge.CallStatic<string>("lastError", session);
                    Debug.LogWarning("[Macaca Beacon] Android MP4 finalization failed: " + error);
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Android MP4 encoder failed: " + exception.Message;
                return false;
            }
            finally
            {
                if (bridge != null && session != 0)
                {
                    try { bridge.CallStatic("destroy", session); }
                    catch (Exception exception) { Debug.LogWarning("[Macaca Beacon] Android encoder cleanup failed: " + exception.Message); }
                }
                bridge?.Dispose();
            }
#else
            error = "The Android H.264 backend is only available in Android player builds.";
            return false;
#endif
        }

        public IEnumerator TryEncodeRawFilesAsync(
            string outputPath,
            IReadOnlyList<VideoCaptureFrame> frames,
            int width,
            int height,
            int framesPerSecond,
            int bitrateKbps,
            double durationSeconds,
            Action<VideoCaptureResult, string> completed)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaClass bridge = null;
            long job = 0;
            try
            {
                if (frames == null || frames.Count == 0)
                {
                    completed?.Invoke(null, "No captured video frames were available.");
                    yield break;
                }

                var paths = new string[frames.Count];
                var timestamps = new double[frames.Count];
                var sourceStart = frames[0].CapturedAt;
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    if (frame.Format != VideoCaptureFrameFormat.Rgba32 ||
                        string.IsNullOrEmpty(frame.DataFilePath) ||
                        !File.Exists(frame.DataFilePath))
                    {
                        completed?.Invoke(null, "Android background encoding requires raw RGBA frame files.");
                        yield break;
                    }
                    paths[index] = frame.DataFilePath;
                    timestamps[index] = Math.Max(0d, frame.CapturedAt - sourceStart);
                }

                bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo");
                if (bridge.CallStatic<int>("isAvailable") == 0)
                {
                    completed?.Invoke(null, "Android MediaCodec is unavailable.");
                    yield break;
                }

                job = bridge.CallStatic<long>(
                    "beginEncodeRawFiles",
                    outputPath,
                    paths,
                    timestamps,
                    width,
                    height,
                    Math.Max(1, framesPerSecond),
                    Math.Max(128, bitrateKbps) * 1000,
                    durationSeconds);
                if (job == 0)
                {
                    completed?.Invoke(null, "Android could not start its background H.264 job.");
                    yield break;
                }

                while (bridge.CallStatic<int>("isEncodeJobDone", job) == 0)
                    yield return null;

                if (bridge.CallStatic<int>("didEncodeJobSucceed", job) == 0 || !File.Exists(outputPath))
                {
                    completed?.Invoke(null, bridge.CallStatic<string>("encodeJobError", job) ?? "Android background H.264 encoding failed.");
                    yield break;
                }

                completed?.Invoke(
                    new VideoCaptureResult(outputPath, Extension, MimeType, durationSeconds, frames.Count, Name + " background job"),
                    null);
            }
            finally
            {
                if (bridge != null && job != 0)
                {
                    try { bridge.CallStatic("destroyEncodeJob", job); }
                    catch (Exception exception) { Debug.LogWarning("[Macaca Beacon] Android encode job cleanup failed: " + exception.Message); }
                }
                bridge?.Dispose();
            }
#else
            completed?.Invoke(null, "Android background encoding is only available in Android player builds.");
            yield break;
#endif
        }
    }
}
