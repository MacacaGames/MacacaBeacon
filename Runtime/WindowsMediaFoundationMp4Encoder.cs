using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class WindowsMediaFoundationMp4Encoder : IVideoEncoderBackend
    {
        public string Name => "Windows Media Foundation H.264";
        public string Extension => ".mp4";
        public string MimeType => "video/mp4";

        public bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                try
                {
                    return NativeIsAvailable() != 0;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
                catch (BadImageFormatException) { return false; }
#else
                return false;
#endif
            }
        }

        public bool TryEncode(string outputPath, IReadOnlyList<VideoCaptureFrame> frames, int width, int height, int framesPerSecond, int bitrateKbps, double durationSeconds, out string error)
        {
            error = null;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            IntPtr session = IntPtr.Zero;
            try
            {
                session = NativeCreate(outputPath, width, height, framesPerSecond, Math.Max(128, bitrateKbps) * 1000);
                if (session == IntPtr.Zero)
                {
                    error = "Media Foundation could not allocate an encoding session.";
                    return false;
                }

                var sourceStart = frames[0].CapturedAt;
                var lastPresentationTime = 0d;
                byte[] lastFrame = null;
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    if (frame.JpegData == null || frame.JpegData.Length == 0)
                        continue;

                    var presentationTime = Math.Max(0d, frame.CapturedAt - sourceStart);
                    if (NativeAddJpeg(session, frame.JpegData, frame.JpegData.Length, presentationTime) == 0)
                    {
                        error = LastError(session, "Media Foundation rejected a captured frame.");
                        return false;
                    }
                    lastPresentationTime = presentationTime;
                    lastFrame = frame.JpegData;
                }

                if (lastFrame == null)
                {
                    error = "No captured JPEG frame was available for the Windows MP4 encoder.";
                    return false;
                }

                // Add a final hold frame at the incident boundary. This preserves the requested
                // before/after duration even when Unity cannot capture at the configured FPS.
                if (lastFrame != null && durationSeconds > lastPresentationTime + (0.5d / Math.Max(1, framesPerSecond)))
                {
                    if (NativeAddJpeg(session, lastFrame, lastFrame.Length, durationSeconds) == 0)
                    {
                        error = LastError(session, "Media Foundation could not extend the final frame duration.");
                        return false;
                    }
                }

                if (NativeFinish(session) == 0)
                {
                    error = LastError(session, "Media Foundation could not finalize the MP4 file.");
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Windows MP4 encoder failed: " + exception.Message;
                return false;
            }
            finally
            {
                if (session != IntPtr.Zero)
                    NativeDestroy(session);
            }
#else
            error = "The Media Foundation H.264 backend is only available in the Windows Editor and Windows Player.";
            return false;
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private static string LastError(IntPtr session, string fallback)
        {
            var pointer = NativeLastError(session);
            return pointer == IntPtr.Zero ? fallback : Marshal.PtrToStringAnsi(pointer) ?? fallback;
        }

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_IsAvailable", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeIsAvailable();

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_Create", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath, int width, int height, int framesPerSecond, int bitrate);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_AddJpeg", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeAddJpeg(IntPtr session, byte[] jpegBytes, int byteCount, double presentationSeconds);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_Finish", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeFinish(IntPtr session);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_LastError", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeLastError(IntPtr session);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_Destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void NativeDestroy(IntPtr session);
#endif
    }
}
