using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class WindowsOpenH264Mp4Encoder : IVideoEncoderBackend
    {
        private static readonly bool isWine = DetectWine();

        public string Name => "OpenH264 deferred software MP4";
        public string Extension => ".mp4";
        public string MimeType => "video/mp4";

        internal static bool IsWine => isWine;

        private static bool DetectWine()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                var ntdll = GetModuleHandle("ntdll.dll");
                return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

        public bool IsAvailable
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                try { return NativeIsAvailable() != 0; }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
                catch (BadImageFormatException) { return false; }
#else
                return false;
#endif
            }
        }

        public unsafe bool TryEncode(
            string outputPath,
            IReadOnlyList<VideoCaptureFrame> frames,
            int width,
            int height,
            int framesPerSecond,
            int bitrateKbps,
            double durationSeconds,
            out string error)
        {
            error = null;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (frames == null || frames.Count == 0)
            {
                error = "OpenH264 received no captured frames.";
                return false;
            }

            IntPtr session = IntPtr.Zero;
            try
            {
                session = NativeCreate(
                    outputPath,
                    width,
                    height,
                    Math.Max(1, framesPerSecond),
                    Math.Max(128, bitrateKbps) * 1000);
                if (session == IntPtr.Zero)
                {
                    error = "OpenH264 could not create an encoding session.";
                    return false;
                }

                var sourceStart = frames[0].CapturedAt;
                var submitted = 0;
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    if (frame.Format != VideoCaptureFrameFormat.Rgba32 ||
                        frame.Width != width ||
                        frame.Height != height)
                    {
                        error = "OpenH264 requires equally-sized RGBA32 frames.";
                        return false;
                    }

                    var presentationSeconds = Math.Max(0d, frame.CapturedAt - sourceStart);
                    if (frame.TryGetNativeRgbaPointer(out var nativePointer))
                    {
                        if (NativeAddRgba(
                                session,
                                nativePointer,
                                frame.ByteCount,
                                frame.RowsAreBottomUp ? 1 : 0,
                                presentationSeconds) == 0)
                        {
                            error = LastError(session, "OpenH264 rejected a native RGBA frame.");
                            return false;
                        }
                    }
                    else
                    {
                        var bytes = frame.ReadData();
                        if (bytes == null || bytes.Length != checked(width * height * 4))
                            continue;
                        fixed (byte* pointer = bytes)
                        {
                            if (NativeAddRgba(
                                    session,
                                    (IntPtr)pointer,
                                    bytes.Length,
                                    0,
                                    presentationSeconds) == 0)
                            {
                                error = LastError(session, "OpenH264 rejected an RGBA frame.");
                                return false;
                            }
                        }
                    }
                    submitted++;
                }

                if (submitted == 0)
                {
                    error = "OpenH264 received no readable RGBA frames.";
                    return false;
                }
                if (NativeFinish(session, Math.Max(durationSeconds, 1d / Math.Max(1, framesPerSecond))) == 0)
                {
                    error = LastError(session, "OpenH264 could not finalize the MP4 file.");
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "OpenH264 MP4 encoding failed: " + exception.Message;
                return false;
            }
            finally
            {
                if (session != IntPtr.Zero)
                    NativeDestroy(session);
            }
#else
            error = "The OpenH264 deferred encoder is only available in Windows builds.";
            return false;
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private static string LastError(IntPtr session, string fallback)
        {
            var pointer = NativeLastError(session);
            return pointer == IntPtr.Zero ? fallback : Marshal.PtrToStringAnsi(pointer) ?? fallback;
        }

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_SoftwareIsAvailable", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeIsAvailable();

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_SoftwareCreate", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeCreate(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string outputPath,
            int width,
            int height,
            int framesPerSecond,
            int bitrate);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_SoftwareAddRgba", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeAddRgba(
            IntPtr session,
            IntPtr rgba,
            int byteCount,
            int rowsAreBottomUp,
            double presentationSeconds);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_SoftwareFinish", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeFinish(IntPtr session, double durationSeconds);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_SoftwareLastError", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NativeLastError(IntPtr session);

        [DllImport("MacacaBeaconVideoWindows", EntryPoint = "MacacaBeaconWindowsVideo_SoftwareDestroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void NativeDestroy(IntPtr session);
#endif
    }
}
