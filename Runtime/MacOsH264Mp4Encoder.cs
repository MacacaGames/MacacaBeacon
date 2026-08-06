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
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
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
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
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
                    if (frame.JpegData == null || frame.JpegData.Length == 0)
                        continue;
                    var presentationTime = Math.Max(0d, frame.CapturedAt - sourceStart);
                    if (NativeAddJpeg(session, frame.JpegData, frame.JpegData.Length, presentationTime) == 0)
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
                    var lastFrame = frames[frames.Count - 1].JpegData;
                    if (lastFrame != null && lastFrame.Length > 0 && NativeAddJpeg(session, lastFrame, lastFrame.Length, durationSeconds) == 0)
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
                error = "macOS MP4 encoder failed: " + exception.Message;
                return false;
            }
            finally
            {
                if (session != IntPtr.Zero)
                    NativeDestroy(session);
            }
#else
            error = "The Apple H.264 backend is only available in the macOS Editor and macOS Player.";
            return false;
#endif
        }

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        private static string LastError(IntPtr session, string fallback)
        {
            var pointer = NativeLastError(session);
            return pointer == IntPtr.Zero ? fallback : Marshal.PtrToStringAnsi(pointer) ?? fallback;
        }

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_IsAvailable")]
        private static extern int NativeIsAvailable();

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Create")]
        private static extern IntPtr NativeCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_AddJpeg")]
        private static extern int NativeAddJpeg(IntPtr session, byte[] jpegBytes, int byteCount, double presentationSeconds);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Finish")]
        private static extern int NativeFinish(IntPtr session);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_LastError")]
        private static extern IntPtr NativeLastError(IntPtr session);

        [DllImport("MacacaBeaconVideo", EntryPoint = "MacacaBeaconVideo_Destroy")]
        private static extern void NativeDestroy(IntPtr session);
#endif
    }
}
