using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class MacOsH264Mp4Encoder : IVideoEncoderBackend
    {
        public string Name => "Apple AVAssetWriter H.264";
        public string Extension => ".mp4";
        public string MimeType => "video/mp4";

        public bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
                try
                {
                    return NativeIsAvailable() != 0;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
#else
                return false;
#endif
            }
        }

        public bool TryEncode(string outputPath, IReadOnlyList<VideoCaptureFrame> frames, int width, int height, int framesPerSecond, int bitrateKbps, double durationSeconds, out string error)
        {
            error = null;
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
            IntPtr session = IntPtr.Zero;
            try
            {
                session = NativeCreate(outputPath, width, height, framesPerSecond, Math.Max(128, bitrateKbps) * 1000);
                if (session == IntPtr.Zero)
                {
                    error = "AVAssetWriter could not create an encoding session.";
                    return false;
                }

                var sourceStart = frames[0].CapturedAt;
                var lastPresentationTime = 0d;
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    var frameData = frame.ReadData();
                    if (frameData == null || frameData.Length == 0)
                        continue;
                    var presentationTime = Math.Max(0d, frame.CapturedAt - sourceStart);
                    var added = frame.Format == VideoCaptureFrameFormat.Rgba32
                        ? NativeAddRgba(session, frameData, frameData.Length, frame.Width, frame.Height, presentationTime)
                        : NativeAddJpeg(session, frameData, frameData.Length, presentationTime);
                    if (added == 0)
                    {
                        error = LastError(session, "AVAssetWriter rejected a captured frame.");
                        return false;
                    }
                    lastPresentationTime = presentationTime;
                }

                // AVAssetWriter derives sample duration from the following timestamp. Holding the
                // last frame to the requested incident end keeps sparse capture from shortening time.
                if (durationSeconds > lastPresentationTime + (0.5d / Math.Max(1, framesPerSecond)))
                {
                    var finalFrame = frames[frames.Count - 1];
                    var lastFrame = finalFrame.ReadData();
                    var added = finalFrame.Format == VideoCaptureFrameFormat.Rgba32
                        ? NativeAddRgba(session, lastFrame, lastFrame == null ? 0 : lastFrame.Length, finalFrame.Width, finalFrame.Height, durationSeconds)
                        : NativeAddJpeg(session, lastFrame, lastFrame == null ? 0 : lastFrame.Length, durationSeconds);
                    if (lastFrame != null && lastFrame.Length > 0 && added == 0)
                    {
                        error = LastError(session, "AVAssetWriter could not extend the final frame duration.");
                        return false;
                    }
                }

                if (NativeFinish(session) == 0)
                {
                    error = LastError(session, "AVAssetWriter could not finalize the MP4 file.");
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Apple MP4 encoder failed: " + exception.Message;
                return false;
            }
            finally
            {
                if (session != IntPtr.Zero)
                    NativeDestroy(session);
            }
#else
            error = "The Apple H.264 backend is only available in macOS and iOS builds.";
            return false;
#endif
        }

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
        private static string LastError(IntPtr session, string fallback)
        {
            var pointer = NativeLastError(session);
            return pointer == IntPtr.Zero ? fallback : Marshal.PtrToStringAnsi(pointer) ?? fallback;
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_IsAvailable")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_IsAvailable")]
#endif
        private static extern int NativeIsAvailable();

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_Create")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Create")]
#endif
        private static extern IntPtr NativeCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_AddJpeg")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_AddJpeg")]
#endif
        private static extern int NativeAddJpeg(IntPtr session, byte[] jpegBytes, int byteCount, double presentationSeconds);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_AddRgba")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_AddRgba")]
#endif
        private static extern int NativeAddRgba(IntPtr session, byte[] rgbaBytes, int byteCount, int width, int height, double presentationSeconds);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_Finish")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Finish")]
#endif
        private static extern int NativeFinish(IntPtr session);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_LastError")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_LastError")]
#endif
        private static extern IntPtr NativeLastError(IntPtr session);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal", EntryPoint = "MacacaBeaconVideo_Destroy")]
#else
        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Destroy")]
#endif
        private static extern void NativeDestroy(IntPtr session);
#endif
    }
}
